"""Checks worldgen/producer.lock.json against the accepted F02/R01 provenance and the live inputs.

Offline; launches nothing. Exit 0 only when every pin agrees.
"""
import hashlib
import json
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def main():
    lock = json.loads((REPO / 'worldgen/producer.lock.json').read_text(encoding='utf-8'))
    tools = json.loads((REPO / 'tools.lock.json').read_text(encoding='utf-8'))
    producer, toolchain, runtime = lock['producer'], lock['toolchain'], lock['runtime']
    failures, checks = [], 0

    def check(condition, label):
        nonlocal checks
        checks += 1
        if not condition:
            failures.append(label)

    check(lock['schema'] == 'mnbl.producer-lock' and lock['version'] == 1, 'lock schema')
    pinned = tools['producer']
    check(producer['sourceCommit'] == pinned['sourceCommit'], 'source commit matches tools.lock.json')
    check(producer['sha256'] == pinned['sha256'], 'binary sha256 matches tools.lock.json')
    check(producer['capabilitiesSha256'] == pinned['capabilitiesSha256'], 'capabilities sha256 matches tools.lock.json')
    check(producer['generatorSchema'] == pinned['schema'] and producer['schemaVersion'] == pinned['schemaVersion'], 'generator schema')
    check(producer['splitVersion'] == pinned['splitVersion'] and producer['sourceDirty'] is False and pinned['sourceDirty'] is False, 'split version / clean source')
    for key in ('version', 'toolset', 'sdk', 'configuration', 'architecture', 'floatingPoint'):
        mine = {'version': 'compilerVersion', 'sdk': 'windowsSdk'}.get(key, key)
        check(toolchain[mine] == pinned_compiler(tools, key), 'toolchain ' + key)
    check(runtime['ucrtbaseSha256'] == tools['compiler']['ucrtSha256'], 'UCRT sha256 matches tools.lock.json')
    check(sha(REPO / producer['binary']) == producer['sha256'], 'built producer bytes match lock')
    check(sha(REPO / 'evidence/fwg/capabilities.json') == producer['capabilitiesSha256'], 'accepted capabilities evidence hash')
    check(sha(REPO / 'evidence/fwg/help.txt') == producer['helpSha256'], 'accepted help evidence hash')
    for name in ('buildLog', 'ctestLog', 'verification'):
        entry = lock['acceptedEvidence'][name]
        check(sha(REPO / entry['path']) == entry['sha256'], 'accepted evidence ' + name)
    cache = (REPO / 'build/producer/CMakeCache.txt').read_text(encoding='utf-8', errors='replace')
    version = '.'.join(line.split('=', 1)[1] for line in cache.splitlines()
                       if line.split(':', 1)[0] in ('CMAKE_CACHE_MAJOR_VERSION', 'CMAKE_CACHE_MINOR_VERSION', 'CMAKE_CACHE_PATCH_VERSION'))
    check(toolchain['cmake'].startswith(version), 'producer CMake cache version ' + version)
    source = Path(producer['localSource'])
    head = subprocess.run(['git', '-C', str(source), 'rev-parse', 'HEAD'], capture_output=True, text=True).stdout.strip()
    dirty = subprocess.run(['git', '-C', str(source), 'status', '--porcelain'], capture_output=True, text=True).stdout.strip()
    check(head == producer['sourceCommit'] and dirty == '', 'local FWGC++ checkout is the pinned clean commit')
    check(sha(source / 'dependencies.json') == lock['contentProvenance']['dependencyManifestSha256'], 'dependency manifest hash')
    for failure in failures:
        print('FAIL:', failure)
    print(('PASS' if not failures else 'FAIL') + ': %d/%d producer lock checks' % (checks - len(failures), checks))
    return 1 if failures else 0


def pinned_compiler(tools, key):
    return tools['compiler'][key]


if __name__ == '__main__':
    sys.exit(main())
