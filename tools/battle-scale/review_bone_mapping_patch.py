"""Reproduce the isolated machine/geometry checks and pin their exact inputs."""
from pathlib import Path
import hashlib
import json
import runpy
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / 'build/battle-scale/python-libs'), str(Path(__file__).parent)]
from patch_native_copy import safe_review_output
from audit_bone_mapping_patch import build


def review():
    patch_path = ROOT / 'evidence/engine-extension/native-bone-mapping-patch.json'
    lifecycle_path = ROOT / 'evidence/engine-extension/native-renderer-buffer-lifecycle-review.json'
    patch = json.loads(patch_path.read_text())
    assert patch == build(), 'Patch is stale against its source/generator/evidence'
    lifecycle = json.loads(lifecycle_path.read_text())
    assert lifecycle['targetSha256'] == patch['sourceSha256']
    assert lifecycle['layout']['bufferStride'] == '0x128'
    assert lifecycle['layout']['pageCount'] == 32
    emulator = ROOT / 'tests/BattleScale/test_renderer_guard_emulation.py'
    geometry = ROOT / 'tests/BattleScale/test_bone_mapping_geometry.py'
    definitions = runpy.run_path(str(emulator))
    for rva, label in (('0x886d5', 'INDEX'), ('0x89925', 'MATRIX')):
        entry = next(e for e in patch['edits'] if e['rva'] == rva)
        assert bytes.fromhex(entry['originalBytes']) == definitions[label+'_OLD']
        assert bytes.fromhex(entry['replacementBytes']) == definitions[label+'_NEW']
    runs = []
    for test in (emulator, geometry):
        completed = subprocess.run([sys.executable, str(test)], cwd=ROOT, text=True,
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=60)
        assert completed.returncode == 0, completed.stdout
        runs.append(dict(file=str(test), exitCode=completed.returncode, output=completed.stdout))
    sources = [patch_path, lifecycle_path, emulator, geometry, Path(__file__),
               ROOT / 'tools/battle-scale/audit_bone_mapping_patch.py',
               ROOT / 'tools/battle-scale/requirements-native-audit.txt']
    return dict(schemaVersion=1, status='accepted-bounded-machine-change-for-restricted-runtime-test',
                gameExecuted=False, nativeDllWritten=False, sourceSha256=patch['sourceSha256'],
                sources={str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in sources},
                editCount=13, machineTestCount=6, geometryTestCount=4, runs=runs,
                findings=[], acceptance=[
                    'Every valid first/last page pair preserves all general registers and the original loop/skip destination in actual x64 emulation.',
                    'Both guards trap with UD2 before page-table setup when the last page is >=32; abnormal termination cannot count as a test pass.',
                    'Original zero mapping reservation bypass and first>last skip remain intact; original unwind saves and stack geometry are unchanged.',
                    'All nine division/remainder/rounding shifts and both allocation/upload byte sizes agree on16384 DWORDs per mapping page.',
                    'Shared32-pointer headers and constructors/destructors retain their original geometry; matrix payload page size is unchanged.',
                    'Type12 GPU upload consumes the descriptor page size and total byte count; no hardcoded2048 remains on that upload branch.'
                ], limitations=[
                    'This is approval of a bounded machine-code change for an isolated test, not a successful runtime result or production readiness.',
                    'Original asynchronous producer/upload/two-frame reuse ordering is preserved, but this review does not newly prove whole-engine backend synchronization.',
                    'Every-byte relative-branch/address scans cannot exclude arbitrary computed indirect destinations or third-party code.',
                    'The finite capacities remain524288 mapping DWORDs and262144 matrix records; malformed render objects and allocator failure retain existing behavior.',
                    'Parent integration must independently preserve all existing source hashes, isolated provider profile and required native guards.'
                ])


if __name__ == '__main__':
    result = review()
    output = ROOT / 'evidence/engine-extension/native-bone-mapping-independent-review.json'
    safe_review_output(output, list(result['sources']))
    output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps({'status': result['status'], 'tests': result['machineTestCount']+result['geometryTestCount']}))
