#!/usr/bin/env python3
"""Per-mod release tooling for CI, driven by config/mods.json.

  check [--base REF]       registry covers every Modules/<id>/ and src/<dir>/; with --base, every release mod whose
                           owned files changed since REF has a higher SubModule.xml Version
  plan                     release mods whose <id>/<version> tag does not exist yet (JSON list; GitHub output 'mods')
  info ID                  one mod's id, version, tag and build script (JSON)
  references --out DIR     download the pinned compile references (config/ci-references.json) laid out like a game install
  package ID --stage DIR --out DIR
                           zip a staged module folder as <id>/..., with a .sha256 file, release notes and a manifest
                           (<id>-<version>.manifest.json: every file's size, SHA-256 and provenance, the dependencies, the
                           build fingerprints and licenses); the mod's "package" block in config/mods.json lists the only
                           files a package may hold, and no file may carry an official game or DLC name
"""
import argparse
import fnmatch
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import urllib.request
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path, PurePosixPath

REPO = Path(__file__).resolve().parents[2]
REGISTRY = 'config/mods.json'
REFERENCES = 'config/ci-references.json'
VERSION = re.compile(r'^v(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?$')
NUGET = 'https://api.nuget.org/v3-flatcontainer'
# Dependencies a player installs separately; anything else is treated as part of the base game.
KNOWN_DEPENDENCIES = {
    'Bannerlord.Harmony': 'Harmony (Steam Workshop item 2859188632)',
    'NavalDLC': 'War Sails DLC',
}


# File names that belong to the game, its DLC or a separately installed dependency: a package never carries them.
PROPRIETARY = re.compile(r'^(TaleWorlds\..*|NavalDLC.*|SandBox.*|StoryMode.*|CustomBattle.*|Bannerlord(\..*)?\.(exe|dll)|0Harmony\.dll|steam_api.*|Steamworks.*|'
                         r'MCMv\d.*|Bannerlord\.(ButterLib|UIExtenderEx|MBOptionScreen).*|Newtonsoft\.Json.*|.*\.(tpac|brf|xscene|xsb|xsoundevent))$', re.I)


class CheckError(Exception):
    pass


def git(root, *args, check=True, binary=False):
    result = subprocess.run(['git', *args], cwd=root, capture_output=True,
                            **({} if binary else {'text': True, 'encoding': 'utf-8'}))
    if check and result.returncode:
        raise CheckError(f'git {" ".join(args)} failed: {result.stderr.strip()}')
    return result


def load_registry(root):
    registry = json.loads((root / REGISTRY).read_text(encoding='utf-8'))
    if registry.get('schema') != 'mnbl.mod-registry' or registry.get('version') != 1:
        raise CheckError(f'{REGISTRY}: expected schema mnbl.mod-registry version 1')
    return registry['mods']


def find_mod(root, mod_id):
    for mod in load_registry(root):
        if mod['id'] == mod_id:
            return mod
    raise CheckError(f'{mod_id} is not in {REGISTRY}')


def parse_submodule(data, label):
    try:
        module = ET.fromstring(data)
    except ET.ParseError as error:
        raise CheckError(f'{label}: {error}')
    def value(tag):
        node = module.find(tag)
        return node.get('value') if node is not None else None
    depends = [(node.get('Id'), node.get('DependentVersion'), node.get('Optional') == 'true')
               for node in module.findall('DependedModules/DependedModule')]
    return {'id': value('Id'), 'name': value('Name'), 'version': value('Version'), 'depends': depends}


def read_submodule(root, mod_id):
    path = root / 'Modules' / mod_id / 'SubModule.xml'
    if not path.is_file():
        raise CheckError(f'Modules/{mod_id}/SubModule.xml is missing')
    return parse_submodule(path.read_bytes(), f'Modules/{mod_id}/SubModule.xml')


def version_key(version, label):
    match = VERSION.match(version or '')
    if not match:
        raise CheckError(f'{label}: Version {version!r} is not vMAJOR.MINOR.PATCH')
    return tuple(int(part or 0) for part in match.groups())


def tag_for(mod_id, version):
    return f'{mod_id}/{version}'


def listed_files(root):
    """Tracked plus untracked-but-not-ignored files, so a new mod folder is caught before it is committed."""
    output = git(root, 'ls-files', '--cached', '--others', '--exclude-standard', '-z').stdout
    return [path for path in output.split('\0') if path]


def owned_patterns(root, mod):
    """The mod's 'owns' entries plus every file its project embeds (those ship inside the DLL)."""
    patterns = list(mod.get('owns', []))
    project = mod.get('project')
    if project:
        tree = ET.parse(root / project)
        namespace = {'m': 'http://schemas.microsoft.com/developer/msbuild/2003'}
        for item in tree.getroot().findall('m:ItemGroup/m:EmbeddedResource', namespace):
            target = PurePosixPath(project).parent.joinpath(item.get('Include').replace('\\', '/'))
            parts = []
            for part in target.parts:
                if part == '..':
                    parts.pop()
                elif part != '.':
                    parts.append(part)
            patterns.append('/'.join(parts))
    return patterns


def is_owned(path, patterns):
    return any(path == pattern or (pattern.endswith('/') and path.startswith(pattern)) for pattern in patterns)


def registry_errors(root):
    errors = []
    mods = load_registry(root)
    ids = [mod.get('id') for mod in mods]
    for duplicate in sorted({mod_id for mod_id in ids if ids.count(mod_id) > 1}):
        errors.append(f'{REGISTRY}: {duplicate} is listed more than once')
    files = listed_files(root)
    for top in ('Modules', 'src'):
        folders = sorted({path.split('/')[1] for path in files if path.startswith(top + '/') and path.count('/') >= 2})
        for folder in folders:
            prefix = f'{top}/{folder}/'
            if top == 'Modules' and folder not in ids:
                errors.append(f'{prefix} is not a registered mod; add {folder} to {REGISTRY}')
            elif not any(prefix.startswith(pattern) or pattern.startswith(prefix)
                         for mod in mods for pattern in mod.get('owns', [])):
                errors.append(f'{prefix} is not owned by any mod; add it to a mod\'s "owns" in {REGISTRY}')
    for mod in mods:
        mod_id = mod.get('id')
        try:
            submodule = read_submodule(root, mod_id)
            if submodule['id'] != mod_id:
                errors.append(f'Modules/{mod_id}/SubModule.xml: Id is {submodule["id"]!r}, expected {mod_id!r}')
            version_key(submodule['version'], f'Modules/{mod_id}/SubModule.xml')
        except CheckError as error:
            errors.append(str(error))
        if not mod.get('owns'):
            errors.append(f'{mod_id}: "owns" is empty')
        for pattern in mod.get('owns', []):
            if not (root / pattern).exists():
                errors.append(f'{mod_id}: owned path {pattern} does not exist')
        if mod.get('release'):
            if not mod.get('build') or not (root / mod['build']).is_file():
                errors.append(f'{mod_id}: release mods need an existing "build" script')
            block = mod.get('package')
            if block is not None and (not block.get('files') or not block.get('provenance')):
                errors.append(f'{mod_id}: a "package" block needs "files" and "provenance"')
        elif not mod.get('reason'):
            errors.append(f'{mod_id}: mods with "release": false need a "reason"')
    errors += reference_errors(root)
    return errors


def reference_errors(root):
    references = json.loads((root / REFERENCES).read_text(encoding='utf-8'))
    tools_lock = json.loads((root / 'tools.lock.json').read_text(encoding='utf-8'))
    api_lock = json.loads((root / 'config/runtime-api-lock.json').read_text(encoding='utf-8'))
    wanted = references.get('gameVersion')
    errors = []
    if tools_lock['game']['version'] != wanted or api_lock['gameVersion'] != wanted:
        errors.append(f'{REFERENCES} gameVersion {wanted} differs from tools.lock.json ({tools_lock["game"]["version"]}) '
                      f'or config/runtime-api-lock.json ({api_lock["gameVersion"]}); re-pin the CI references for the same game build')
    return errors


def version_errors(root, base):
    if git(root, 'rev-parse', '--verify', '--quiet', f'{base}^{{commit}}', check=False).returncode:
        raise CheckError(f'base {base} is not a commit in this clone (fetch full history)')
    changed = [path for path in git(root, 'diff', '--name-only', '-z', f'{base}...HEAD').stdout.split('\0') if path]
    errors = []
    for mod in load_registry(root):
        mod_id = mod['id']
        head = read_submodule(root, mod_id)
        before = git(root, 'show', f'{base}:Modules/{mod_id}/SubModule.xml', check=False, binary=True)
        if before.returncode:
            continue  # a new mod: any first version is fine
        old = parse_submodule(before.stdout, f'{base}:Modules/{mod_id}/SubModule.xml')
        label = f'Modules/{mod_id}/SubModule.xml'
        new_key, old_key = version_key(head['version'], label), version_key(old['version'], f'{base}:{label}')
        if new_key < old_key:
            errors.append(f'{mod_id}: Version went down from {old["version"]} to {head["version"]}')
            continue
        if not mod.get('release'):
            continue
        patterns = owned_patterns(root, mod)
        touched = [path for path in changed if is_owned(path, patterns)]
        if touched and new_key == old_key:
            shown = ', '.join(touched[:5]) + (f' and {len(touched) - 5} more' if len(touched) > 5 else '')
            errors.append(f'{mod_id}: {shown} changed but Version is still {head["version"]}; bump it in {label}')
    return errors


def release_plan(root):
    planned = []
    for mod in load_registry(root):
        if not mod.get('release'):
            continue
        version = read_submodule(root, mod['id'])['version']
        if git(root, 'rev-parse', '--verify', '--quiet', f'refs/tags/{tag_for(mod["id"], version)}', check=False).returncode:
            planned.append(mod['id'])
    return planned


def github_output(values):
    path = os.environ.get('GITHUB_OUTPUT')
    if path:
        with open(path, 'a', encoding='utf-8') as output:
            for key, value in values.items():
                output.write(f'{key}={value}\n')


def lay_out_references(root, out, cache):
    references = json.loads((root / REFERENCES).read_text(encoding='utf-8'))
    out, cache = Path(out).resolve(), Path(cache).resolve()
    if out == root or out in root.parents or out == cache or out in cache.parents:
        raise CheckError(f'refusing to replace {out}')
    if out.exists():
        shutil.rmtree(out)
    cache.mkdir(parents=True, exist_ok=True)
    for package in references['packages']:
        name = f'{package["id"].lower()}.{package["version"].lower()}.nupkg'
        archive = cache / name
        if not archive.is_file() or hashlib.sha512(archive.read_bytes()).hexdigest() != package['sha512']:
            url = f'{NUGET}/{package["id"].lower()}/{package["version"].lower()}/{name}'
            with urllib.request.urlopen(url, timeout=120) as response:
                archive.write_bytes(response.read())
        actual = hashlib.sha512(archive.read_bytes()).hexdigest()
        if actual != package['sha512']:
            raise CheckError(f'{name}: SHA-512 {actual} differs from {REFERENCES}')
        target = out / package['to']
        copied = 0
        with zipfile.ZipFile(archive) as nupkg:
            for entry in nupkg.infolist():
                if entry.is_dir() or not entry.filename.startswith(package['from']):
                    continue
                relative = PurePosixPath(entry.filename[len(package['from']):])
                if relative.is_absolute() or '..' in relative.parts:
                    raise CheckError(f'{name}: unsafe entry {entry.filename}')
                destination = target.joinpath(*relative.parts)
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_bytes(nupkg.read(entry))
                copied += 1
        if not copied:
            raise CheckError(f'{name}: nothing under {package["from"]}')
        print(f'{package["id"]} {package["version"]}: {copied} files -> {package["to"]}')


def dependency_lines(depends):
    lines = []
    for mod_id, version, optional in depends:
        label = KNOWN_DEPENDENCIES.get(mod_id, f'{mod_id} (base game)')
        lines.append(f'- {label}' + (f' {version}' if version else '') + (' (optional)' if optional else ''))
    return lines or ['- Nothing beyond the base game']


def package(root, mod_id, stage, out):
    mod = find_mod(root, mod_id)
    if not mod.get('release'):
        raise CheckError(f'{mod_id} has "release": false')
    source = read_submodule(root, mod_id)
    stage, out = Path(stage).resolve(), Path(out).resolve()
    if not (stage / 'SubModule.xml').is_file():
        raise CheckError(f'{stage} has no SubModule.xml; run the mod\'s build script first')
    staged = parse_submodule((stage / 'SubModule.xml').read_bytes(), f'{stage}/SubModule.xml')
    if (staged['id'], staged['version']) != (mod_id, source['version']):
        raise CheckError(f'staged SubModule.xml is {staged["id"]} {staged["version"]}, expected {mod_id} {source["version"]}')
    version = source['version']
    files = sorted(path for path in stage.rglob('*') if path.is_file())
    rows = package_rows(mod, stage, files)   # refuses before anything is written
    out.mkdir(parents=True, exist_ok=True)
    archive = out / f'{mod_id}-{version}.zip'
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as bundle:
        for path in files:
            entry = zipfile.ZipInfo(f'{mod_id}/{path.relative_to(stage).as_posix()}', date_time=(1980, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            bundle.writestr(entry, path.read_bytes())
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    checksum = out / f'{archive.name}.sha256'
    checksum.write_text(f'{digest}  {archive.name}\n', encoding='utf-8')
    commit = os.environ.get('GITHUB_SHA') or git(root, 'rev-parse', 'HEAD').stdout.strip()
    game = json.loads((root / REFERENCES).read_text(encoding='utf-8'))['gameVersion']
    notes = out / 'notes.md'
    notes.write_text('\n'.join([
        f'**{source["name"]}** `{version}`, built from `{commit[:12]}` against Bannerlord `{game}`.',
        '',
        '### Install',
        f'1. Download `{archive.name}`.',
        '2. Right-click the zip, choose **Properties**, tick **Unblock**, then **OK**. Without this, Windows blocks the DLLs and the game will not load the mod.',
        f'3. Extract it into `<Bannerlord install>\\Modules\\`, so that `Modules\\{mod_id}\\SubModule.xml` exists.',
        f'4. Enable **{source["name"]}** in the launcher\'s Mods tab.',
        '',
        '### Requires',
        *dependency_lines(source['depends']),
        '',
        f'SHA-256 of the zip: `{digest}`',
        '',
    ]), encoding='utf-8')
    manifest = out / f'{mod_id}-{version}.manifest.json'
    manifest.write_text(json.dumps(package_manifest(root, mod, source, rows, archive.name, digest, commit, game), indent=1) + '\n', encoding='utf-8')
    result = {'tag': tag_for(mod_id, version), 'title': f'{source["name"]} {version}', 'zip': str(archive),
              'sha256': str(checksum), 'notes': str(notes), 'manifest': str(manifest), 'files': len(files)}
    github_output({key: value for key, value in result.items() if key != 'files'})
    return result


def file_sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def package_rows(mod, stage, files):
    """Every staged file's size, SHA-256 and provenance. Every package refuses a file named like an official game, DLC or dependency
    file; a mod with a "package" block also refuses a file the block does not list or give a provenance, and a package that lacks a
    listed file (every literal entry present, every pattern matched at least once)."""
    block = mod.get('package')
    allowed, provenance = (block or {}).get('files') or [], (block or {}).get('provenance') or {}
    rows, problems = [], []
    for path in files:
        rel = path.relative_to(stage).as_posix()
        origin = next((text for pattern, text in provenance.items() if fnmatch.fnmatchcase(rel, pattern)), None)
        rows.append({'path': rel, 'bytes': path.stat().st_size, 'sha256': file_sha(path), 'provenance': origin})
        if PROPRIETARY.match(PurePosixPath(rel).name):
            problems.append(f'{rel} is named like an official game, DLC or dependency file')
        if block is not None and not any(fnmatch.fnmatchcase(rel, pattern) for pattern in allowed):
            problems.append(f'{rel} is not a file the package may hold (config/mods.json package.files)')
        if block is not None and origin is None:
            problems.append(f'{rel} has no provenance (config/mods.json package.provenance)')
    staged = [r['path'] for r in rows]
    for pattern in allowed:
        if not any(fnmatch.fnmatchcase(rel, pattern) for rel in staged):
            problems.append(f'the package lacks {pattern} (config/mods.json package.files)')
    if problems:
        raise CheckError('package refused: ' + '; '.join(problems))
    return rows


def package_manifest(root, mod, source, rows, archive_name, digest, commit, game):
    """What a package holds and where each file comes from, the dependencies, the supported build and the licenses."""
    block = mod.get('package')
    locks = {}
    for name in ('tools.lock.json', 'config/runtime-api-lock.json', REFERENCES):
        if (root / name).is_file():
            locks[name] = file_sha(root / name)
    tools = json.loads((root / 'tools.lock.json').read_text(encoding='utf-8')) if (root / 'tools.lock.json').is_file() else {}
    dirty = git(root, 'status', '--porcelain', '--', *mod.get('owns', []), check=False).stdout.strip() != ''
    return {'schema': 'mnbl.package-manifest', 'version': 1, 'mod': mod['id'], 'name': source['name'], 'modVersion': source['version'],
            'archive': {'file': archive_name, 'sha256': digest, 'layout': f'{mod["id"]}/... (extract into <Bannerlord install>/Modules/)'},
            'source': {'commit': commit, 'ownedFilesUncommitted': dirty},
            'supportedBuild': {'game': game, 'gameLock': (tools.get('game') or {}).get('version'), 'navalDlc': (tools.get('game') or {}).get('navalVersion'),
                               'producer': {k: (tools.get('producer') or {}).get(k) for k in ('sourceCommit', 'sha256', 'schemaVersion')}, 'locks': locks},
            'dependencies': [{'id': mod_id, 'version': version, 'optional': optional, 'installedSeparately': KNOWN_DEPENDENCIES.get(mod_id, f'{mod_id} (base game)')}
                             for mod_id, version, optional in source['depends']],
            'files': rows, 'totalBytes': sum(r['bytes'] for r in rows), 'runtimeRequirements': (block or {}).get('runtimeRequirements') or [],
            'licenses': (block or {}).get('licenses') or {}, 'contentNotes': (block or {}).get('contentNotes') or [], 'notRedistributed': (block or {}).get('notRedistributed') or []}


def main(argv=None):
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8')
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--root', default=str(REPO), help=argparse.SUPPRESS)
    commands = parser.add_subparsers(dest='command', required=True)
    check = commands.add_parser('check')
    check.add_argument('--base')
    commands.add_parser('plan')
    info = commands.add_parser('info')
    info.add_argument('id')
    references = commands.add_parser('references')
    references.add_argument('--out', required=True)
    references.add_argument('--cache', default=str(REPO / 'build/ci/nuget'))
    pack = commands.add_parser('package')
    pack.add_argument('id')
    pack.add_argument('--stage', required=True)
    pack.add_argument('--out', required=True)
    args = parser.parse_args(argv)
    root = Path(args.root).resolve()
    try:
        if args.command == 'check':
            errors = registry_errors(root) + (version_errors(root, args.base) if args.base else [])
            for error in errors:
                print(f'ERROR: {error}')
            if errors:
                return 1
            print(f'PASS: {len(load_registry(root))} registered mods' + (f'; versions checked against {args.base}' if args.base else ''))
        elif args.command == 'plan':
            planned = release_plan(root)
            github_output({'mods': json.dumps(planned, separators=(',', ':'))})
            print(json.dumps(planned))
        elif args.command == 'info':
            mod = find_mod(root, args.id)
            version = read_submodule(root, args.id)['version']
            print(json.dumps({'id': mod['id'], 'version': version, 'tag': tag_for(mod['id'], version),
                              'release': bool(mod.get('release')), 'build': mod.get('build')}))
        elif args.command == 'references':
            lay_out_references(root, args.out, args.cache)
        elif args.command == 'package':
            print(json.dumps(package(root, args.id, args.stage, args.out), indent=2))
    except CheckError as error:
        print(f'ERROR: {error}')
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
