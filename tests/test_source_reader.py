"""Development-only integration checks for the C++ source reader."""
import json
from pathlib import Path
import subprocess
import struct
import tempfile

ROOT = Path(__file__).resolve().parents[1]
EXE = ROOT / 'build/worldbridge/Release/worldbridge.exe'

def fixture():
    return {
        'schema': 'fwg.world', 'schema_version': 2, 'completed_stage': 16,
        'config': {'size': 3, 'seed': 42}, 'effective_config': {},
        'topology': {'type': 'sphere', 'duplicate_seam': True},
        'seeds': {'layout': '18446744073709551615'},
        'layers': {'height': [[1, 1, 1], [2, 3, 2], [4, 4, 4]]},
        'settlements': {'version': 16}, 'humans': {'version': 10},
        'heroes': {'version': 6}, 'roads': {'version': 1},
        'split': {'version': 1, 'scene': 'scene.json'},
    }

def check(name, change, expected):
    with tempfile.TemporaryDirectory(prefix='source reader ', dir=ROOT / 'build') as directory:
        base = Path(directory)
        source = base / 'source'
        source.mkdir()
        world = fixture()
        change(world)
        (source / 'world.json').write_text(json.dumps(world), encoding='utf-8')
        (source / 'scene.json').write_text('{}', encoding='utf-8')
        result = subprocess.run([str(EXE), '--source', str(source), '--out', str(base / 'out')], capture_output=True, text=True)
        assert (result.returncode == 0) == expected, (name, result.stdout, result.stderr)
        receipt = base / 'out/source-validation.json'
        assert receipt.exists() == expected, name
        grid = base / 'out/heightfield.bin'
        assert grid.exists() == expected, name
        if expected:
            report = json.loads(receipt.read_text())
            assert report['layoutSeed'] == '18446744073709551615'
            assert report['campaignReady'] is False
            data = grid.read_bytes()
            assert data[:8] == b'MNBLHF01'
            assert struct.unpack_from('<I', data, 8) == (3,)
            assert struct.unpack_from('<9d', data, 12) == (1, 1, 1, 2, 3, 2, 4, 4, 4)
            assert len(data) == 84
        print('PASS:', name)

check('uint64 seed retained exactly', lambda w: None, True)
check('failure document rejected', lambda w: w.update(schema='fantasy-world-generator.failure'), False)
check('unknown schema rejected', lambda w: w.update(schema_version=99), False)
check('unsupported block rejected', lambda w: w['humans'].update(version=99), False)
check('ragged grid rejected', lambda w: w['layers']['height'][1].pop(), False)
check('empty grid rejected', lambda w: w['layers'].update(height=[]), False)
check('numeric object is not a matrix', lambda w: w['layers'].update(height={str(i): r for i, r in enumerate(w['layers']['height'])}), False)
check('numeric object is not a row', lambda w: w['layers']['height'].__setitem__(1, {'0': 2, '1': 3, '2': 2}), False)
check('string height rejected', lambda w: w['layers']['height'][1].__setitem__(1, '3'), False)
check('seam mismatch rejected', lambda w: w['layers']['height'][1].__setitem__(2, 9), False)
check('pole mismatch rejected', lambda w: w['layers']['height'][0].__setitem__(1, 9), False)
check('split traversal rejected', lambda w: w['split'].update(scene='../scene.json'), False)
check('missing split file rejected', lambda w: w['split'].update(scene='missing.json'), False)
check('unsupported split rejected', lambda w: w['split'].update(version=99), False)
print('PASS: 14 source reader integration cases')
