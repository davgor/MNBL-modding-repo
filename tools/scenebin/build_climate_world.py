"""Offline MMR-59 fixture builder: a generated bootstrap world with open sea and generated climate fields.

Builds exactly what build_bootstrap_world.py builds, with two additions; it never installs or launches anything.

* Navigation: sea cells at least OPEN_SEA_CELLS (Chebyshev) from any non-sea cell become face group 19
  (TerrainType.OpenSea); the coastal ring stays 18. NavalDLC's storm spawner keeps only weather nodes that lie on
  OpenSea faces (StormCampaignBehavior.InitializeStormNodes), so a world without them never spawns storms.
* Climate: one of two contrasting generated patterns with known samples, recorded in placement['climate'].
  With --wind-zones 2 the wind speed is 0.30 west of x = extent/2 and 0.20 east of it (same direction), so a
  runtime query tests the field layout. --from <existing build> writes only a new placement and climate files for
  the same world and installed scene (no encoder run):
    along  - wind 0.30 along the town_1 port -> town_2 port direction everywhere; rain 230/255 west of the middle,
             0 east; no snow
    across - wind 0.30 perpendicular to that direction; snow 230/255 in the north half (y >= extent/2), 0 south;
             no rain
  Files next to placement.json:
    climate-wind.f32           512 x 512 (x, y) float32 little-endian pairs
    climate-precipitation.u8   32 x 32 (snow, rain) byte pairs, 0..255
  Both use the layout of SandBox.MapScene's _windFlowMapData / _snowAndRainData: index = row * dim + col with
  col = floor(x / extent * dim), row = floor(y / extent * dim), origin 0, clamped (GetTextureDataIndexForPosition).

The two additions reuse the bootstrap builder by replacing its navigation, components and scene-validation hooks
for the duration of one build.
"""
from __future__ import annotations

import argparse
from array import array
from collections import Counter, deque
import json
import math
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import build_bootstrap_world as bbw
import navmesh_bin

COASTAL, OPEN_SEA = 18, 19
OPEN_SEA_CELLS = 4
WIND_DIM, WEATHER_DIM = 512, 32
WIND_SPEED, LEVEL = 0.30, 230
SCHEMA = 'mnbl.generated-climate.v1'

_navigation, _components, _validate, _require = bbw.navigation, bbw.components, bbw.validate_scene, bbw.require


def navigation(grid, extent):
    groups, centers, slopes, cell = _navigation(grid, extent)
    size = len(groups)
    distance = [[None] * size for _ in range(size)]
    queue = deque()
    for r in range(size):
        for c in range(size):
            if groups[r][c] != COASTAL:
                distance[r][c] = 0
                queue.append((c, r))
    while queue:
        c, r = queue.popleft()
        for dc in (-1, 0, 1):
            for dr in (-1, 0, 1):
                n, m = c + dc, r + dr
                if 0 <= n < size and 0 <= m < size and distance[m][n] is None:
                    distance[m][n] = distance[r][c] + 1
                    queue.append((n, m))
    for r in range(size):
        for c in range(size):
            if groups[r][c] == COASTAL and distance[r][c] is not None and distance[r][c] >= OPEN_SEA_CELLS:
                groups[r][c] = OPEN_SEA
    return groups, centers, slopes, cell


def components(groups, allowed):
    allowed = set(allowed)
    if COASTAL in allowed:
        allowed.add(OPEN_SEA)  # one sea: coastal and open sea are both naval
    return _components(groups, allowed)


def validate_scene(folder, plan, official):
    def require(condition, message):
        if message != 'Generated land/sea/test groups missing':  # replaced below: open sea (19) is expected here
            _require(condition, message)
    bbw.require = require
    try:
        result = _validate(folder, plan, official)
    finally:
        bbw.require = _require
    _, _, payload = navmesh_bin.read_navmesh_file(str(folder / 'navmesh.bin'))
    groups = Counter(f.group for f in navmesh_bin.decode_payload(payload).faces)
    _require(set(groups) <= {1, 7, COASTAL, OPEN_SEA, bbw.OBSTRUCTION_GROUP, bbw.LAKE_GROUP}
             and {1, COASTAL, OPEN_SEA, bbw.OBSTRUCTION_GROUP, bbw.LAKE_GROUP} <= set(groups),
             'Generated land/coastal/open-sea/test groups missing')
    result['openSeaFaces'] = groups[OPEN_SEA]
    return result


def climate(plan, pattern, out, zones=1):
    extent = plan['extent']
    ports = {s['id']: s['portPosition'] for s in plan['settlements'] if s['kind'] == 'town'}
    p1, p2 = ports[plan['worldId'] + '_town_1'], ports[plan['worldId'] + '_town_2']
    dx, dy = p2[0] - p1[0], p2[1] - p1[1]
    length = math.hypot(dx, dy)
    _require(length > 0, 'Ports coincide')
    along = (dx / length, dy / length)
    direction = along if pattern == 'along' else (-along[1], along[0])
    wind = (WIND_SPEED * direction[0], WIND_SPEED * direction[1])
    east = (2 / 3 * wind[0], 2 / 3 * wind[1]) if zones == 2 else wind  # 0.20 east of the middle
    field = array('f')
    for row in range(WIND_DIM):
        for col in range(WIND_DIM):
            v = wind if (col + .5) * extent / WIND_DIM < extent / 2 else east
            field.append(v[0]); field.append(v[1])
    _require(field.itemsize == 4 and sys.byteorder == 'little', 'float32 little-endian host required')
    (out / 'climate-wind.f32').write_bytes(field.tobytes())
    precipitation = bytearray(WEATHER_DIM * WEATHER_DIM * 2)
    for row in range(WEATHER_DIM):
        for col in range(WEATHER_DIM):
            x, y = (col + .5) * extent / WEATHER_DIM, (row + .5) * extent / WEATHER_DIM
            snow = LEVEL if pattern == 'across' and y >= extent / 2 else 0
            rain = LEVEL if pattern == 'along' and x < extent / 2 else 0
            precipitation[2 * (row * WEATHER_DIM + col)] = snow
            precipitation[2 * (row * WEATHER_DIM + col) + 1] = rain
    (out / 'climate-precipitation.u8').write_bytes(bytes(precipitation))
    return dict(
        schema=SCHEMA, pattern=pattern,
        wind=dict(file='climate-wind.f32', sha256=bbw.sha(out / 'climate-wind.f32'), dimension=WIND_DIM,
                  vector=list(wind), speed=WIND_SPEED, zones=zones, eastVector=list(east), reference=dict(fromPort=p1[:2], toPort=p2[:2]),
                  encoding='float32 little-endian (x, y) pairs; index row*512+col; col=floor(x/extent*512), row=floor(y/extent*512); origin 0; clamped'),
        precipitation=dict(file='climate-precipitation.u8', sha256=bbw.sha(out / 'climate-precipitation.u8'), dimension=WEATHER_DIM,
                           encoding='byte pairs (snow, rain) 0..255; index row*32+col; same rule as wind',
                           rain='%d west of x=extent/2, else 0' % LEVEL if pattern == 'along' else '0 everywhere',
                           snow='%d where y>=extent/2, else 0' % LEVEL if pattern == 'across' else '0 everywhere'),
        binding='SandBox.MapScene private fields _windFlowMapData, _snowAndRainData, _snowAndRainDataTextureWidth/Height, '
                'written in GeneratedMapScene.Load (MMR-37; formerly DiagnosticMapScene) before MapWeatherModel.InitializeCaches; no texture import',
        scope='MMR-59 diagnostic pattern with known samples; not climate generation')


def climate_only(args):
    source = Path(getattr(args, 'from')).resolve()
    plan = json.loads((source / 'placement.json').read_text(encoding='utf-8'))
    _require(plan['worldId'] == args.world_id and plan['sceneName'] == args.scene_name, 'The source build is a different world')
    out = bbw.output_path(args.out); out.mkdir(parents=True)
    plan['climate'] = climate(plan, args.climate, out, args.wind_zones)
    plan['climateDerivedFrom'] = dict(placement=str(source / 'placement.json'), sha256=bbw.sha(source / 'placement.json'))
    bbw.write_json(out / 'placement.json', plan)
    report = dict(schema='mnbl.climate-variant-build.v1', worldId=plan['worldId'], sceneName=plan['sceneName'], placementSha256=bbw.sha(out / 'placement.json'),
                  climate=dict(pattern=args.climate, zones=args.wind_zones, files={k: plan['climate'][k]['sha256'] for k in ('wind', 'precipitation')}),
                  derivedFrom=plan['climateDerivedFrom'], sceneFiles=plan['sceneFiles'],
                  inputs=[dict(path=str(Path(__file__).resolve()), sha256=bbw.sha(Path(__file__).resolve()))])
    bbw.write_json(out / 'build-receipt.json', report)
    return report


def build(args):
    if getattr(args, 'from'):
        return climate_only(args)
    bbw.navigation, bbw.components, bbw.validate_scene = navigation, components, validate_scene
    try:
        report = bbw.build(args)
    finally:
        bbw.navigation, bbw.components, bbw.validate_scene = _navigation, _components, _validate
    out = Path(args.out).resolve()
    plan = json.loads((out / 'placement.json').read_text(encoding='utf-8'))
    plan['climate'] = climate(plan, args.climate, out, args.wind_zones)
    plan['nav']['openSeaPolicy'] = 'sea cells >= %d cells (Chebyshev) from any non-sea cell are face group 19 (OpenSea); the coastal ring stays 18' % OPEN_SEA_CELLS
    plan['nav']['terrainGroups'] = report['validation'].get('terrainGroups', plan['nav']['terrainGroups'])
    bbw.write_json(out / 'placement.json', plan)
    report['placementSha256'] = bbw.sha(out / 'placement.json')
    report['climate'] = dict(pattern=args.climate, files={k: plan['climate'][k]['sha256'] for k in ('wind', 'precipitation')})
    report['openSeaFaces'] = report['validation']['openSeaFaces']
    report['inputs'].append(dict(path=str(Path(__file__).resolve()), sha256=bbw.sha(Path(__file__).resolve())))
    bbw.write_json(out / 'build-receipt.json', report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--world-id', required=True); parser.add_argument('--scene-name', required=True)
    parser.add_argument('--heightfield'); parser.add_argument('--extent', type=float)
    parser.add_argument('--climate', choices=('along', 'across'), required=True)
    parser.add_argument('--wind-zones', type=int, choices=(1, 2), default=1)
    parser.add_argument('--from', help='existing build of the same world: write only a new placement and climate files')
    parser.add_argument('--scale', type=float, default=.01)
    parser.add_argument('--surface-template', default='build/surface-template', help='python tools/scenebin/make_surface_template.py --materialize build/surface-template')
    parser.add_argument('--encoder', default='build/worldbridge/Release/worldbridge.exe')
    parser.add_argument('--game-root', default=bbw.build_slot.GAME)
    parser.add_argument('--settlement-ids', nargs=4, metavar=('TOWN1', 'TOWN2', 'VILLAGE1', 'VILLAGE2'))
    parser.add_argument('--out', required=True)
    args = parser.parse_args()
    report = build(args)
    print(json.dumps(dict(worldId=report['worldId'], openSeaFaces=report.get('openSeaFaces'), climate=report['climate'],
                          placementSha256=report['placementSha256']), indent=1))


if __name__ == '__main__':
    main()
