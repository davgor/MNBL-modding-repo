"""Read-only installed-artifact identity capture/check; never loads target code."""
from __future__ import annotations

import argparse
import hashlib
import json
import struct
import uuid
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path


def managed_mvid(data: bytes) -> str | None:
    def u16(offset):
        return struct.unpack_from('<H', data, offset)[0]

    def u32(offset):
        return struct.unpack_from('<I', data, offset)[0]

    if data[:2] != b'MZ':
        raise ValueError('Not a PE file')
    pe = u32(0x3c)
    if data[pe:pe + 4] != b'PE\0\0':
        raise ValueError('Invalid PE signature')
    count, optional_size = u16(pe + 6), u16(pe + 20)
    optional = pe + 24
    magic = u16(optional)
    if magic not in (0x10b, 0x20b):
        raise ValueError('Unsupported PE optional header')
    directories = optional + (112 if magic == 0x20b else 96)
    directory_count = u32(directories - 4)
    if directory_count < 15:
        return None
    cli_rva = u32(directories + 14 * 8)
    if not cli_rva:
        return None
    sections = optional + optional_size

    def offset(rva):
        if rva < u32(optional + 60):
            return rva
        for i in range(count):
            sec = sections + i * 40
            virtual_size, virtual_start, raw_size, raw_start = struct.unpack_from('<IIII', data, sec + 8)
            if virtual_start <= rva < virtual_start + max(virtual_size, raw_size):
                delta = rva - virtual_start
                if delta >= raw_size or raw_start + delta >= len(data):
                    raise ValueError('RVA has no source bytes')
                return raw_start + delta
        raise ValueError('RVA outside sections')

    metadata = offset(u32(offset(cli_rva) + 8))
    if data[metadata:metadata + 4] != b'BSJB':
        raise ValueError('Invalid CLI metadata signature')
    cursor = (metadata + 16 + u32(metadata + 12) + 3) & ~3
    stream_count = u16(cursor + 2)
    cursor += 4
    streams = {}
    for _ in range(stream_count):
        relative, length = struct.unpack_from('<II', data, cursor)
        name_start = cursor + 8
        name_end = data.index(b'\0', name_start, min(name_start + 32, len(data)))
        streams[data[name_start:name_end].decode('ascii')] = (metadata + relative, length)
        cursor = (name_end + 4) & ~3
    tables, _ = streams.get('#~', streams.get('#-', (None, None)))
    if tables is None or '#GUID' not in streams:
        raise ValueError('Missing CLI tables/GUID heap')
    heap_flags = data[tables + 6]
    valid = struct.unpack_from('<Q', data, tables + 8)[0]
    if not valid & 1 or u32(tables + 24) != 1:
        raise ValueError('Expected exactly one Module row')
    rows_start = tables + 24 + valid.bit_count() * 4
    mvid_column = rows_start + 2 + (4 if heap_flags & 1 else 2)
    guid_index = u32(mvid_column) if heap_flags & 2 else u16(mvid_column)
    guid_start, guid_length = streams['#GUID']
    if guid_index < 1 or guid_index * 16 > guid_length:
        raise ValueError('Invalid Module MVID index')
    start = guid_start + (guid_index - 1) * 16
    return str(uuid.UUID(bytes_le=data[start:start + 16]))


def file_identity(path: Path, root: Path) -> dict:
    before = path.stat()
    data = path.read_bytes()
    after = path.stat()
    if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
        raise ValueError(f'File changed during capture: {path}')
    result = {'path': path.relative_to(root).as_posix(), 'bytes': len(data),
              'sha256': hashlib.sha256(data).hexdigest()}
    if path.suffix.lower() in ('.exe', '.dll'):
        result['mvid'] = managed_mvid(data)
    return result


def capture(root: Path) -> dict:
    root = root.resolve(strict=True)
    paths = set()
    modules, content = [], []
    mode_dirs = ['Win64_Shipping_Client', 'Win64_Shipping_wEditor', 'Win64_Shipping_Server']
    bins = [(root / 'bin' / mode) for mode in mode_dirs]
    for name in ['Native', 'SandBoxCore', 'SandBox', 'StoryMode', 'NavalDLC']:
        module = root / 'Modules' / name
        descriptor = module / 'SubModule.xml'
        if not descriptor.exists():
            modules.append({'name': name, 'status': 'missing'})
            continue
        paths.add(descriptor)
        xml = ET.parse(descriptor).getroot()
        modules.append({'name': name, 'status': 'present', 'id': xml.find('Id').get('value'),
                        'version': xml.find('Version').get('value'),
                        'required_base_version': xml.find('RequiredBaseVersion').get('value') if xml.find('RequiredBaseVersion') is not None else None})
        bins.extend(module / 'bin' / mode for mode in mode_dirs)
        for directory in ['EmAssetPackages', 'AssetPackages', 'SceneObj', 'SceneEditData', 'ModuleData', 'Prefabs', 'NavMeshPrefabs']:
            folder = module / directory
            files = sorted(p for p in folder.glob('*') if p.is_file()) if folder.exists() else []
            content.append({'path': folder.relative_to(root).as_posix(), 'exists': folder.is_dir(),
                            'top_level_files': len(files), 'top_level_bytes': sum(p.stat().st_size for p in files),
                            'sample_names': [p.name for p in files[:5]],
                            'interpretation': 'presence only; not asset resolution or runtime readiness'})
    for directory in bins:
        if directory.is_dir():
            paths.update(p for p in directory.iterdir() if p.is_file() and p.suffix.lower() in ('.exe', '.dll')
                         and (directory.parent.name == 'bin' and 'Modules' in directory.parts or p.name.startswith(('TaleWorlds.', 'Bannerlord.'))))
    return {'schema': 'mnbl.modding-mcp.identity.v1', 'captured_utc': datetime.now(timezone.utc).isoformat(),
            'game_root': str(root), 'execution': 'none; static file reads only', 'modules': modules,
            'binaries_and_module_descriptors': [file_identity(p, root) for p in sorted(paths)],
            'content_presence': content, 'runtime_verified': False,
            'limitations': ['Does not hash every game asset or inspect active loaded modules.',
                            'Module descriptors and nonempty content directories do not establish usable editor/runtime behavior.',
                            'Native PE files have null MVID; managed MVID is decoded from CLI metadata without loading assemblies.']}


def verify(root: Path, manifest: dict) -> dict:
    root = root.resolve(strict=True)
    failures = []
    records = manifest.get('binaries_and_module_descriptors', [])
    if manifest.get('schema') != 'mnbl.modding-mcp.identity.v1' or not records:
        raise ValueError('Unsupported or empty identity manifest')
    seen = set()
    for expected in records:
        relative = expected['path']
        path = (root / relative).resolve()
        if not path.is_relative_to(root) or path in seen:
            raise ValueError('Escaping or duplicate identity path')
        # Resolved Windows Paths compare case-insensitively and collapse aliases.
        seen.add(path)
        try:
            actual = file_identity(path, root)
            changed = [key for key in ('bytes', 'sha256', 'mvid') if expected.get(key) != actual.get(key)]
            if changed:
                failures.append({'path': relative, 'status': 'mismatch', 'fields': changed})
        except (OSError, ValueError, struct.error) as exc:
            failures.append({'path': relative, 'status': 'unreadable', 'error': str(exc)})
    return {'schema': 'mnbl.modding-mcp.identity-check.v1', 'checked_utc': datetime.now(timezone.utc).isoformat(),
            'checked_files': len(records), 'passed': not failures, 'failures': failures,
            'scope': 'manifest-listed identities only; not a complete installation or live-process check'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['capture', 'verify'])
    parser.add_argument('--game-root', type=Path, required=True)
    parser.add_argument('--manifest', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if args.action == 'verify' and not args.manifest:
        parser.error('verify requires --manifest')
    # Reports must not overwrite installed content, even when a caller chooses a bad output path.
    if args.output.resolve().is_relative_to(args.game_root.resolve()):
        parser.error('output must be outside the game installation')
    result = capture(args.game_root) if args.action == 'capture' else verify(args.game_root, json.loads(args.manifest.read_text(encoding='utf-8-sig')))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'output': str(args.output), 'files': len(result.get('binaries_and_module_descriptors', [])) or result.get('checked_files'), 'passed': result.get('passed')}))
    if result.get('passed') is False:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
