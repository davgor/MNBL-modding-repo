#!/usr/bin/env python3
"""MMR-63 periodic-seam fixture: one logical world whose east and west edges are the same longitude.

Offline only; never installs, launches or edits a module. Output must be NEW under build/.

Layout (all coordinates in world units, +X columns, +Y rows):
  * The FWGC++ source heightfield is periodic in x (its first and last columns are the same seam
    column) and its first/last rows are pole aliases (one node replicated across the row).
  * Logical world: x in [0, W) with W = 128 source cells. Physical terrain: a left strip copying logical
    [W/2, W), the canonical world at physical [S, S + W) with S = W/2, and a right strip copying
    logical [0, W/2). Every shortest cross-seam route (|dx| > W/2) therefore has its target image
    inside the physical terrain, so native path queries on that image need no stitching.
  * Source rows sit in the middle; rows above/below are the pole rows' ocean, so north and south are
    never connected and no polar geography is cropped.
  * Settlements exist only at canonical positions. Strips carry visual-only copies of settlement icons
    (no settlement tags or scripts), so no campaign identity is duplicated.
"""
from __future__ import annotations

import argparse
from collections import Counter
import copy
import json
import math
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import xml.etree.ElementTree as ET

sys.dont_write_bytecode = True
import build_bootstrap_world as bbw
import build_slot
import navmesh_bin
import terrain_bin

SCHEMA = 'mnbl.periodic-placement.v1'
LOGICAL = 128   # source cells per seam period
STRIP = 64      # strip cells on each side (W/2)
SIDE = LOGICAL + 2 * STRIP  # 256 cells -> 257 vertices


def read_source(path):
    data = Path(path).read_bytes()
    bbw.require(data[:8] == b'MNBLHF01', 'Invalid heightfield header')
    side, = struct.unpack_from('<I', data, 8)
    bbw.require(side == LOGICAL + 1, 'Periodic fixture expects a %d-vertex source' % (LOGICAL + 1))
    values = struct.unpack_from('<%dd' % (side * side), data, 12)
    grid = [list(values[r * side:(r + 1) * side]) for r in range(side)]
    seam = max(abs(row[0] - row[-1]) for row in grid)
    bbw.require(seam == 0.0, 'Source is not periodic in x (first/last column differ by %g)' % seam)
    poles = {name: (min(grid[r]), max(grid[r])) for name, r in (('north', 0), ('south', side - 1))}
    bbw.require(all(lo == hi for lo, hi in poles.values()), 'Source pole rows are not single-node aliases')
    bbw.require(all(hi < 0 for _, hi in poles.values()), 'Pole rows are land; padding with ocean would crop them')
    return grid, poles


def extend(grid):
    """257x257: periodic columns around the canonical world, pole-ocean rows above and below."""
    out = []
    for i in range(SIDE + 1):
        r = i - STRIP
        source = grid[0] if r < 0 else grid[LOGICAL] if r > LOGICAL else grid[r]
        out.append([source[(j - STRIP) % LOGICAL] for j in range(SIDE + 1)])
    return out


def write_field(path, grid):
    side = len(grid)
    with open(path, 'wb') as f:
        f.write(b'MNBLHF01' + struct.pack('<I', side))
        for row in grid:
            f.write(struct.pack('<%dd' % side, *row))


def seam_placements(grid, cell, groups, centers, slopes, world_id, ids):
    """Two port towns on the seam-crossing continent, one on each side of the seam, plus a village each."""
    size = len(groups)
    land, land_sizes = bbw.components(groups, {1})
    sea, sea_sizes = bbw.components(groups, {18})
    lo, hi = STRIP, STRIP + LOGICAL  # canonical columns
    def coastal(columns):
        out = []
        for (c, r), lc in land.items():
            if c not in columns or not (3 <= r < size - 3) or centers[c, r] <= .1:
                continue
            for n in bbw.neighbors(c, r, size):
                if n in sea and centers[n] < -.1 and lo <= n[0] < hi:
                    out.append(((c, r), n, lc, sea[n]))
        return out
    east = coastal(range(hi - 16, hi))   # within 100 m west of the east edge
    west = coastal(range(lo, lo + 16))   # within 100 m east of the west edge
    bbw.require(east and west, 'No coastal land near both sides of the seam')
    # Same physical land component through a strip means the continent crosses the seam; prefer a pair about
    # 12 cells (75 m at 6.25 m) apart through the right strip (east town -> image of west town).
    best = None
    for e in east:
        for w in west:
            image = (w[0][0] + LOGICAL, w[0][1])  # west town's image in the right strip
            if image not in land or land[image] != e[2]:
                continue
            d = math.dist(e[0], image)
            if d < 8:
                continue  # crossing must take observable travel (and stay outside one encounter radius)
            key = (abs(d - 12), slopes[e[0]] + slopes[w[0]], e[0][1], e[0][0], w[0][1], w[0][0])
            if best is None or key < best[0]:
                best = (key, e, w)
    bbw.require(best is not None, 'No seam-crossing continent with coastal land on both sides')
    _, e, w = best
    def point(cr, water=False):
        return [(cr[0] + .5) * cell, (cr[1] + .5) * cell, 0. if water else centers[cr]]
    towns, villages, used = [], [], {e[0], w[0]}
    for i, (town, port, lc, sc) in enumerate((e, w)):
        side_cols = range(hi - 24, hi) if i == 0 else range(lo, lo + 24)
        options = [cr for cr, comp in land.items() if comp == land[town] and cr not in used and cr[0] in side_cols
                   and 3 <= cr[1] < size - 3 and 3 <= math.dist(cr, town) <= 12 and centers[cr] > .5
                   and all(math.dist(cr, other) >= 3 for other in used)]
        bbw.require(options, 'No village site near seam town')
        village = min(options, key=lambda cr: (slopes[cr], abs(math.dist(cr, town) - 5), cr[1], cr[0]))
        used.add(village)
        towns.append(dict(id=ids[i], kind='town', position=point(town), gatePosition=point(town), boundTo=None,
                          portPosition=point(port, True), landCell=list(town), seaCell=list(port), landComponent=lc, seaComponent=sc))
        villages.append(dict(id=ids[i + 2], kind='village', position=point(village), gatePosition=point(village),
                             boundTo=ids[i], portPosition=None, landCell=list(village), landComponent=land[village]))
    return towns + villages, land, sea, land_sizes, sea_sizes


def display_copies(root, plan):
    """Visual-only settlement icon copies in the strips (no campaign identity: no tags, scripts or children)."""
    es = root.find('entities')
    width, origin, extent = plan['periodic']['width'], plan['periodic']['originX'], plan['extent']
    copies = []
    for s in plan['settlements']:
        icon = 'mi_emp_keep_1' if s['kind'] == 'town' else 'village_empire_1'
        for direction, shift in (('east', width), ('west', -width)):
            x = s['position'][0] + shift
            if 0 < x < extent:
                name = '%s_display_%s_%s' % (plan['worldId'], s['id'][len(plan['worldId']) + 1:], direction)
                bbw.entity(es, name, (x, s['position'][1], s['position'][2]), mesh=icon)
                copies.append(dict(entity=name, of=s['id'], position=[x, s['position'][1]], shift=shift))
    return copies


def build(args):
    world_id, scene_name = bbw.identifier(args.world_id), bbw.identifier(args.scene_name)
    ids = [world_id + s for s in ('_town_1', '_town_2', '_village_1', '_village_2')]
    source = Path(args.heightfield).resolve()
    grid0, poles = read_source(source)
    cell = args.cell
    extent = SIDE * cell
    out = bbw.output_path(args.out); out.mkdir(parents=True)
    extended = out / 'periodic-heightfield.bin'
    write_field(extended, extend(grid0))
    template, encoder = Path(args.surface_template).resolve(), Path(args.encoder).resolve()
    bbw.require(encoder.is_file(), 'Missing encoder')
    official_dir = Path(args.game_root).resolve() / 'Modules/NavalDLC/SceneObj/Main_map'
    official = ET.parse(official_dir / 'scene.xscene').getroot()
    local_template = out / 'encoder-template'; local_template.mkdir()
    for name in bbw.FILES:
        shutil.copyfile(template / name, local_template / name)
    xml = ET.parse(local_template / 'scene.xscene'); xml.getroot().set('name', scene_name)
    xml.write(local_template / 'scene.xscene', encoding='utf-8', xml_declaration=True)
    folder = out / scene_name
    command = [str(encoder), '--surface-heightfield', str(extended), '--surface-template', str(local_template),
               '--surface-output', str(folder), '--surface-extent', str(extent), '--surface-scale', str(args.scale)]
    result = subprocess.run(command, capture_output=True, text=True, check=False)
    (out / 'encoder.stdout.log').write_text(result.stdout, encoding='utf-8'); (out / 'encoder.stderr.log').write_text(result.stderr, encoding='utf-8')
    bbw.require(result.returncode == 0, 'Native encoder failed; see isolated logs')
    grid = bbw.read_field(folder / 'quantized-heightfield.bin')
    info = json.loads((folder / 'surface.json').read_text(encoding='utf-8'))
    decoded = terrain_bin.decode_heights(terrain_bin.parse((folder / 'terrain.bin').read_bytes()), info['minHeight'], info['maxHeight'])
    bbw.require(len(grid) == len(decoded) and max(abs(a - b) for ra, rb in zip(grid, decoded) for a, b in zip(ra, rb)) < 1e-9, 'Independent terrain decode differs')
    # The quantized field must keep the period: strip vertices equal their canonical originals.
    seam_error = max(abs(grid[i][j] - grid[i][STRIP + (j - STRIP) % LOGICAL]) for i in range(SIDE + 1) for j in range(SIDE + 1))
    bbw.require(seam_error < 1e-9, 'Quantized terrain lost the period (max strip difference %g)' % seam_error)
    groups, centers, slopes, cell_size = bbw.navigation(grid, extent)
    settlements, land, sea, land_sizes, sea_sizes = seam_placements(grid, cell_size, groups, centers, slopes, world_id, ids)
    width, origin = LOGICAL * cell_size, STRIP * cell_size
    plan = dict(schema=bbw.SCHEMA, periodicSchema=SCHEMA, worldId=world_id, sceneName=scene_name, extent=extent, seaLevel=0.,
                placementPolicy='periodic-seam-v1',
                coordinateSystem='world units: +X columns, +Y rows; canonical logical x = physical x - originX in [0, width)',
                settlements=settlements, playerStart=list(settlements[0]['gatePosition']))
    mesh = bbw.build_mesh(grid, cell_size, groups)
    rows = [r for r in range(STRIP, STRIP + LOGICAL + 1)]
    plan['periodic'] = dict(
        axis='x', width=width, originX=origin, stripWidth=origin, sourceCells=LOGICAL, cell=cell_size,
        canonicalRange=[origin, origin + width],
        imageRule='every shortest cross-seam route has its target image within [0, extent): strips are width/2',
        locatorGridPeriod=160.0, widthIsLocatorMultiple=abs(width / 160.0 - round(width / 160.0)) < 1e-9,
        sourceRows=[STRIP, STRIP + LOGICAL], poleRows=dict(north=STRIP, south=STRIP + LOGICAL),
        poles=dict(north=dict(height=poles['north'][0], alias='single source node replicated across the row'),
                   south=dict(height=poles['south'][0], alias='single source node replicated across the row')),
        padding='rows outside the source repeat the pole rows (ocean); north and south are not connected',
        polarDistortion=dict(rule='east-west map distance overstates sphere distance by 1/cos(latitude); latitude = 90 - 180 * (row - northPoleRow) / sourceCells',
                             at60deg=2.0, at80deg=round(1 / math.cos(math.radians(80)), 3), atPoleRow='unbounded (a single node)'))
    # Seam routes for the runtime probe (canonical endpoints; the image is the probe's business).
    e, w = settlements[0], settlements[1]
    plan['seamTests'] = dict(
        landTowns=[e['id'], w['id']], seaPorts=[e['portPosition'], w['portPosition']],
        eastTownLogicalX=e['position'][0] - origin, westTownLogicalX=w['position'][0] - origin,
        planarDx=abs(e['position'][0] - w['position'][0]), periodicDx=width - abs(e['position'][0] - w['position'][0]))
    plan['nav'] = dict(gridSide=SIDE + 1, spacing=cell_size, faces=len(mesh.faces), terrainGroups=dict(Counter(f.group for f in mesh.faces)),
                       landComponents=len(land_sizes), seaComponents=len(sea_sizes), nativeRouteValidated=False)
    plan['sourceSha256'] = bbw.sha(source); plan['extendedHeightfieldSha256'] = bbw.sha(extended)
    plan['terrainSha256'] = bbw.sha(folder / 'terrain.bin')
    root = ET.parse(folder / 'scene.xscene').getroot()
    levels = root.find('levels'); root.remove(levels); root.insert(1, copy.deepcopy(official.find('levels')))
    bbw.scene_entities(root, official, plan, grid)
    plan['displayCopies'] = display_copies(root, plan)
    ET.indent(root, space='\t'); ET.ElementTree(root).write(folder / 'scene.xscene', encoding='utf-8', xml_declaration=True)
    navmesh_bin.write_scene_navmesh(mesh, str(folder / 'navmesh.bin'))
    shutil.copyfile(official_dir / 'atmosphere.xml', folder / 'atmosphere.xml')
    (folder / 'surface.json').replace(out / 'encoder-surface.json')
    plan['sceneFiles'] = {name: bbw.sha(folder / name) for name in bbw.FILES}
    plan['scope'] = 'MMR-63 periodic-seam diagnostic fixture from FWGC++ seed 42; not product generation.'
    bbw.write_json(out / 'placement.json', plan)
    _, _, payload = navmesh_bin.read_navmesh_file(str(folder / 'navmesh.bin'))
    check = navmesh_bin.validate(navmesh_bin.decode_payload(payload))
    bbw.require(not check['errors'], 'Written navmesh invalid')
    report = dict(schema='mnbl.periodic-scene-build.v1', worldId=world_id, sceneName=scene_name, status='offline_valid_runtime_pending',
                  periodic=plan['periodic'], seamTests=plan['seamTests'], settlements=[s['id'] for s in settlements],
                  displayCopies=len(plan['displayCopies']), navFaces=len(mesh.faces), placementSha256=bbw.sha(out / 'placement.json'),
                  command=command, files=plan['sceneFiles'],
                  inputs=[dict(path=str(p), sha256=bbw.sha(p)) for p in [Path(__file__), Path(bbw.__file__), source, encoder]])
    bbw.write_json(out / 'build-receipt.json', report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--world-id', required=True); parser.add_argument('--scene-name', required=True)
    parser.add_argument('--heightfield', required=True)
    parser.add_argument('--cell', type=float, default=6.25)
    parser.add_argument('--scale', type=float, default=.01)
    parser.add_argument('--surface-template', default='build/slots/mnbl_world_b')
    parser.add_argument('--encoder', default='build/worldbridge/Release/worldbridge.exe')
    parser.add_argument('--game-root', default=build_slot.GAME)
    parser.add_argument('--out', required=True)
    args = parser.parse_args()
    print(json.dumps(build(args), indent=2))


if __name__ == '__main__':
    main()
