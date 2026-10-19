"""Independently verify MMR-25 projected-world exports and write evidence/fwg/projection-export.json.

For each export (worldbridge --project), this script recomputes in Python, from the source world.json, every
terrain sample's z and sea flag, and every settlement, hamlet, port, castle and key-location position, and compares
them with the C++ output. It also checks that one projection identity (the SHA-256 of projection.json) is on every
output, that road and river pieces never step more than W/2, and that a repeated export is byte-identical.
Usage:
  python tools/summarize_projection.py --export NAME=SOURCE_DIR=EXPORT_DIR [--export ...] --repeat EXPORT_DIR=EXPORT_DIR2 --out FILE
"""
import argparse
import datetime
import hashlib
import json
import math
import os
import struct

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def rel(path):
    path = os.path.abspath(path)
    return os.path.relpath(path, REPO).replace('\\', '/') if path.startswith(REPO) else path


def load(path):
    with open(path, encoding='utf-8') as f:
        return json.load(f)


def verify(name, source_dir, export_dir, policy):
    world = load(os.path.join(source_dir, 'world.json'))
    projection = load(os.path.join(export_dir, 'projection.json'))
    terrain_meta = load(os.path.join(export_dir, 'terrain.json'))
    objects = load(os.path.join(export_dir, 'objects.json'))
    manifest = load(os.path.join(export_dir, 'manifest.json'))
    identity = sha(os.path.join(export_dir, 'projection.json'))
    n = world['config']['size']
    radius, sea_level = world['topology']['radius_m'], world['effective_config']['sea_level']
    W = policy['logicalWidth']; H = W * policy['heightRatio']; V = policy['verticalScale']; cm = policy['centralMeridianDeg']
    south = 1 + (n - 2) * (n - 1)

    def sample(c, r):
        u = c / (n - 1) - cm / 360.0
        return (W * (u - math.floor(u))) % W, H * r / (n - 1)

    def node_xy(node):
        assert node not in (0, south), 'pole node'
        return sample((node - 1) % (n - 1), 1 + (node - 1) // (n - 1))

    checks = {}
    checks['identityIsProjectionHash'] = manifest['projectionSha256'] == identity == terrain_meta['projectionSha256'] == objects['projectionSha256']
    checks['manifestHashesMatch'] = all(sha(os.path.join(export_dir, f)) == h for f, h in manifest['files'].items())
    checks['policyHashRecorded'] = projection['policySha256'] == manifest['policySha256']
    checks['sourceFrameFromEffectiveConfig'] = projection['source']['radiusM'] == radius and projection['source']['seaLevelM'] == sea_level \
        and projection['source']['gridSide'] == n and projection['source']['layoutSeed'] == world['seeds']['layout']
    with open(os.path.join(export_dir, 'terrain.bin'), 'rb') as f:
        blob = f.read()
    side, = struct.unpack_from('<I', blob, 8)
    checks['terrainHeaderCarriesIdentity'] = blob[:8] == b'MNBLPT01' and side == n and blob[12:76].decode('ascii') == identity
    zs = struct.unpack_from('<%dd' % (n * n), blob, 76)
    below = blob[76 + 8 * n * n:76 + 9 * n * n]
    water = blob[76 + 9 * n * n:]
    heights = [v for row in world['layers']['height'] for v in row]
    water_type = [int(v) for row in world['layers']['water_type'] for v in row]
    checks['terrainSampleCount'] = len(heights) == n * n == len(below) == len(water) and len(blob) == 76 + 10 * n * n
    worst = max(abs(z - (h - sea_level) * V) for z, h in zip(zs, heights))
    checks['terrainZRecomputed'] = worst <= 1e-12
    checks['belowDatumRecomputed'] = all((b == 1) == (h - sea_level < 0) for b, h in zip(below, heights))
    checks['waterTypeCarriedUnchanged'] = list(water) == water_type
    checks['seaContinuityRecorded'] = set(projection.get('seaContinuity', {})) == {'seam', 'strips', 'poles'}
    checks['seamColumnDuplicatesColumn0'] = all(zs[r * n + n - 1] == zs[r * n] for r in range(n))
    checks['poleRowsAreOneValue'] = len(set(zs[:n])) == 1 and len(set(zs[-n:])) == 1

    def close(a, b):
        return abs(a[0] - b[0]) <= 1e-9 and abs(a[1] - b[1]) <= 1e-9

    def node_of(c, r):
        return 0 if r == 0 else south if r == n - 1 else 1 + (r - 1) * (n - 1) + c % (n - 1)

    positions = {}
    mismatch = []
    for key, records, uid in (('settlements', world['settlements']['sites'], 'uid'), ('hamlets', world['humans']['hamlets'], 'uid'),
                              ('castles', world['humans']['fortresses'], 'uid')):
        exported = {o['uid']: o for o in objects[key]}
        for rec in records:
            o = exported.get(rec[uid])
            want = node_xy(rec['node'])
            if o is None or rec['node'] != node_of(rec['x'], rec['z']) or not close(o['position'], want) \
                    or not close(o['physical'], (want[0] + W * policy['stripRatio'], want[1] + W * policy['poleMarginRatio'])):
                mismatch.append((key, rec[uid]))
            if key == 'hamlets' and 'sea_node' in rec:
                if o is None or 'port' not in o or not close(o['port']['position'], node_xy(rec['sea_node'])):
                    mismatch.append(('port', rec[uid]))
        positions[key] = len(records)
    keys = {o['id']: o for o in objects['keyLocations']}
    for rec in world['key_locations']['sites']:
        o = keys.get(rec['id'])
        if o is None or not close(o['position'], node_xy(rec['node'])):
            mismatch.append(('keyLocations', rec['id']))
    positions['keyLocations'] = len(world['key_locations']['sites'])
    positions['ports'] = sum(1 for h in world['humans']['hamlets'] if 'sea_node' in h)
    checks['everyObjectPositionRecomputed'] = not mismatch and positions['ports'] == objects['counts']['ports']

    def path_ok(drawn, nodes):
        pts = [p for piece in drawn['pieces'] for p in piece]
        steps = all(abs(b[0] - a[0]) <= W / 2 for piece in drawn['pieces'] for a, b in zip(piece, piece[1:]))
        first, last = drawn['pieces'][0][0], drawn['pieces'][-1][-1]
        ends = close(first, node_xy(nodes[0])) or (nodes[0] in (0, south))
        ends = ends and (close(last, node_xy(nodes[-1])) or nodes[-1] in (0, south) or (abs(last[0] - W) < 1e-9 and close((0, last[1]), node_xy(nodes[-1]))))
        return steps and ends and len(drawn['links']) == len(drawn['pieces']) - 1 and len(pts) >= 2

    checks['roadsDrawnWithoutMapWideSteps'] = len(objects['roads']) == len(world['roads']['routes']) and all(
        path_ok(d, r['nodes']) for d, r in zip(objects['roads'], world['roads']['routes']))
    checks['riversDrawnWithoutMapWideSteps'] = len(objects['rivers']) == len(world['water']['river_segments']) and all(
        path_ok(d, s) for d, s in zip(objects['rivers'], world['water']['river_segments']))
    layout = projection['periodicLayout']
    checks['periodicLayoutMatchesR10'] = layout['strip'] == W / 2 and layout['canonicalPhysicalX'] == [W / 2, 1.5 * W] and layout['physicalWidth'] == 2 * W \
        and projection['seam']['wrapIntervalWorldUnits'] == W
    checks['trueScaleOnEquatorAndMeridians'] = abs(projection['horizontal']['equatorWorldUnitsPerMetre'] - W / (2 * math.pi * radius)) < 1e-15 \
        and abs(projection['horizontal']['meridianWorldUnitsPerMetre'] - projection['horizontal']['equatorWorldUnitsPerMetre']) < 1e-15
    return {
        'name': name, 'source': rel(source_dir), 'sourceWorldSha256': sha(os.path.join(source_dir, 'world.json')),
        'export': rel(export_dir), 'projectionSha256': identity, 'gridSide': n, 'radiusM': radius, 'seaLevelM': sea_level,
        'counts': objects['counts'], 'recomputedPositions': positions, 'terrain': {'samples': n * n, 'zRange': terrain_meta['zRange'],
        'belowDatumSamples': terrain_meta['belowDatumSamples'], 'waterTypeSamples': terrain_meta['waterTypeSamples'],
        'belowDatumNotOcean': terrain_meta['belowDatumNotOcean'], 'oceanNotBelowDatum': terrain_meta['oceanNotBelowDatum'], 'worstZError': worst},
        'files': manifest['files'], 'checks': checks,
        'mismatches': mismatch[:20],
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--export', action='append', required=True)
    ap.add_argument('--repeat', action='append', default=[])
    ap.add_argument('--policy', default=os.path.join(REPO, 'worldgen/policies/projection.json'))
    ap.add_argument('--ctest-log', default=os.path.join(REPO, 'evidence/fwg/projection-ctest.log'))
    ap.add_argument('--unit-log')
    ap.add_argument('--out', required=True)
    a = ap.parse_args()
    policy = load(a.policy)
    exports = [verify(*spec.split('=', 2), policy) for spec in a.export]
    repeats = []
    for spec in a.repeat:
        x, y = spec.split('=', 1)
        mx, my = load(os.path.join(x, 'manifest.json')), load(os.path.join(y, 'manifest.json'))
        repeats.append({'first': rel(x), 'second': rel(y), 'identical': mx == my})
    calibration = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(a.policy)), policy['calibration']['file']))
    summary = {
        'schema': 'mnbl.projection-export', 'version': 1, 'ticket': 'MMR-25',
        'generatedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'policy': {'file': rel(a.policy), 'sha256': sha(a.policy)},
        'calibration': {'file': rel(calibration), 'sha256': sha(calibration), 'pinnedByPolicy': sha(calibration) == policy['calibration']['sha256']},
        'tools': {'worldbridge': sha(os.path.join(REPO, 'build/worldbridge/Release/worldbridge.exe'))},
        'ctestLog': {'file': rel(a.ctest_log), 'sha256': sha(a.ctest_log)} if os.path.exists(a.ctest_log) else None,
        'unitLog': {'file': rel(a.unit_log), 'sha256': sha(a.unit_log)} if a.unit_log and os.path.exists(a.unit_log) else None,
        'exports': exports, 'repeats': repeats,
        'passed': all(all(e['checks'].values()) for e in exports) and all(r['identical'] for r in repeats) and sha(calibration) == policy['calibration']['sha256'],
        'reproduce': [
            './tools/Build.ps1',
            'ctest -C Release --output-on-failure (in build/worldbridge)',
            'build/worldbridge/Release/worldbridge.exe --project <split world> --contract worldgen/contracts/source-compatibility.json --policy worldgen/policies/projection.json --out <new dir>',
            'python tools/summarize_projection.py --export NAME=<split world>=<export dir> --repeat <export dir>=<second export dir> --out evidence/fwg/projection-export.json'],
    }
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(summary, f, indent=1)
    failing = [(e['name'], k) for e in exports for k, v in e['checks'].items() if not v]
    print('passed=%s failing=%s' % (summary['passed'], failing))


if __name__ == '__main__':
    main()
