#!/usr/bin/env python3
"""MMR-39: generated pirate spawn points for a non-periodic bootstrap fixture (the MMR-59 w42 world).

Offline only; never installs, launches or edits a module. Output must be NEW under build/.

Copies a fixture placement (and the climate files and build receipt next to it) into OUT and adds
placement['naval']['pirateSpawnPoints']: COUNT points at the centroids of the scene navmesh's OpenSea faces (group 19) at least
SHORE units from the centroid of any other face and BORDER units inside the terrain edge, chosen by farthest-point sampling from the open-sea face nearest the terrain
centre (ties by y then x); northern_pirates north of the terrain's middle row, southern_pirates south of it; radius RADIUS.
The periodic fixtures get theirs from build_periodic_world.py --pirates; production points are NavalContentFactory's (MMR-35).
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import shutil
import sys

sys.dont_write_bytecode = True
import build_bootstrap_world as bbw
import navmesh_bin

OPEN_SEA = 19


def centroids(mesh):
    out = []
    for f in mesh.faces:
        xs = [mesh.vertices[v][0] for v in f.vertices]; ys = [mesh.vertices[v][1] for v in f.vertices]
        out.append((sum(xs) / len(xs), sum(ys) / len(ys), f.group))
    return out


def pirate_points(mesh, extent, count, shore, radius, border):
    faces = centroids(mesh)
    sea = [(x, y) for x, y, g in faces if g == OPEN_SEA]
    other = [(x, y) for x, y, g in faces if g != OPEN_SEA]
    bucket = shore
    grid = {}
    for x, y in other:
        grid.setdefault((int(x // bucket), int(y // bucket)), []).append((x, y))
    def near_shore(x, y):
        bx, by = int(x // bucket), int(y // bucket)
        for i in range(bx - 1, bx + 2):
            for j in range(by - 1, by + 2):
                for ox, oy in grid.get((i, j), ()):
                    if math.hypot(ox - x, oy - y) < shore:
                        return True
        return False
    candidates = sorted({(round(x, 6), round(y, 6)) for x, y in sea if border <= x <= extent - border and border <= y <= extent - border and not near_shore(x, y)},
                        key=lambda p: (p[1], p[0]))
    bbw.require(len(candidates) >= count, 'Not enough open sea for %d pirate spawn points' % count)
    centre = (extent / 2, extent / 2)
    chosen = [min(candidates, key=lambda p: (math.dist(p, centre), p[1], p[0]))]
    while len(chosen) < count:
        chosen.append(max(candidates, key=lambda p: (min(math.dist(p, o) for o in chosen), -p[1], -p[0])))
    return [dict(uid='fixture-pirates:%d:%d' % (round(x * 1000), round(y * 1000)), clan='northern_pirates' if y < extent / 2 else 'southern_pirates',
                 x=x, y=y, radius=radius) for x, y in chosen], len(sea), len(candidates)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--placement', required=True)
    parser.add_argument('--count', type=int, default=8)
    parser.add_argument('--shore', type=float, default=30.0)
    parser.add_argument('--radius', type=float, default=30.0)
    parser.add_argument('--border', type=float, default=60.0)
    parser.add_argument('--out', required=True)
    args = parser.parse_args()
    source = Path(args.placement).resolve()
    plan = json.loads(source.read_text(encoding='utf-8-sig'))
    navmesh = source.parent / plan['sceneName'] / 'navmesh.bin'
    bbw.require(bbw.sha(navmesh) == plan['sceneFiles']['navmesh.bin'], 'The scene navmesh is not the placement\'s')
    _, _, payload = navmesh_bin.read_navmesh_file(str(navmesh))
    mesh = navmesh_bin.decode_payload(payload)
    points, sea, candidates = pirate_points(mesh, plan['extent'], args.count, args.shore, args.radius, args.border)
    out = bbw.output_path(args.out); out.mkdir(parents=True)
    plan['naval'] = dict(pirateSpawnPoints=points,
                         rule='fixture rule (tools/scenebin/add_fixture_pirates.py): OpenSea face centroids at least %g from any other face and %g inside the terrain edge, farthest-point sampling from the centre; clan by half; radius %g. Production points are NavalContentFactory\'s (MMR-35).' % (args.shore, args.border, args.radius),
                         openSeaFaces=sea, candidates=candidates, sourcePlacementSha256=bbw.sha(source))
    plan['scope'] = plan.get('scope', '') + ' MMR-39: generated pirate spawn points added.'
    for name in ('climate-wind.f32', 'climate-precipitation.u8', 'climate-colorgrade.u8', 'build-receipt.json'):
        if (source.parent / name).is_file():
            shutil.copyfile(source.parent / name, out / name)
    bbw.write_json(out / 'placement.json', plan)
    print(json.dumps(dict(out=str(out), points=points, openSeaFaces=sea, candidates=candidates), indent=2))


if __name__ == '__main__':
    main()
