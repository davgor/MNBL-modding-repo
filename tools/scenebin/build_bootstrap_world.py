#!/usr/bin/env python3
"""Offline MMR18 scene preparation; never installs, launches, or edits a module.

Uses the accepted worldbridge native terrain encoder, then authors a new campaign
scene and navigation topology. placement.json is the shared object/scene contract:
consume its IDs and coordinates rather than independently choosing dataset XY.
All output must be NEW under this repository's build/. No third-party packages.
Native boot, generated distance-cache registration and weather initialization are
root integration work: offline validation does not establish playable ports.
"""
from __future__ import annotations

import argparse
from collections import Counter, deque
import copy
import hashlib
import json
import math
from pathlib import Path
import re
import shutil
import struct
import subprocess
import sys
import xml.etree.ElementTree as ET

sys.dont_write_bytecode = True
import build_slot
import navmesh_bin
import terrain_bin

REPO = Path(__file__).resolve().parents[2]
FILES = ('scene.xscene', 'terrain.bin', 'navmesh.bin', 'flora.bin', 'atmosphere.xml')
SCHEMA = 'mnbl.bootstrap-placement.v1'
POLICY = 'generated-cell-coast-v1'


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def write_json(path, value):
    Path(path).write_text(json.dumps(value, indent=2, allow_nan=False) + '\n', encoding='utf-8')


def identifier(value):
    require(isinstance(value, str) and re.fullmatch(r'mnbl_[a-z0-9_]{1,80}', value),
            'Expected owned lowercase mnbl_ identifier: ' + str(value))
    return value


def output_path(path):
    path = Path(path).absolute()
    for p in [path, *path.parents]:
        require(not p.is_symlink() and not (hasattr(p, 'is_junction') and p.is_junction()),
                'Redirected output path: ' + str(p))
    resolved = path.resolve()
    require(resolved.is_relative_to((REPO / 'build').resolve()), 'Output must be under repo build/')
    require(not resolved.exists(), 'Refusing existing output: ' + str(resolved))
    return resolved


def read_field(path):
    data = Path(path).read_bytes()
    require(len(data) >= 12 and data[:8] == b'MNBLHF01', 'Invalid heightfield header')
    side, = struct.unpack_from('<I', data, 8)
    require(side in (129, 257) and len(data) == 12 + side * side * 8, 'Invalid field dimensions/length')
    values = struct.unpack_from('<%dd' % (side * side), data, 12)
    require(all(math.isfinite(v) for v in values), 'Nonfinite height')
    return [list(values[r * side:(r + 1) * side]) for r in range(side)]


def neighbors(c, r, size):
    for x, y in ((c - 1, r), (c + 1, r), (c, r - 1), (c, r + 1)):
        if 0 <= x < size and 0 <= y < size:
            yield x, y


def components(groups, allowed):
    size = len(groups)
    labels, sizes = {}, []
    for r in range(size):
        for c in range(size):
            if groups[r][c] not in allowed or (c, r) in labels:
                continue
            label = len(sizes)
            pending = deque([(c, r)])
            labels[c, r] = label
            count = 0
            while pending:
                x, y = pending.popleft()
                count += 1
                for n in neighbors(x, y, size):
                    if n not in labels and groups[n[1]][n[0]] in allowed:
                        labels[n] = label
                        pending.append(n)
            sizes.append(count)
    return labels, sizes


def navigation(grid, extent):
    """Generated full-resolution triangles; sea heights clamped to sea level.

    Region labels are installed TerrainType values, not native region numbers.
    The campaign's PartyNavigationModel must supply SetNavMeshRegionMap at Read.
    Cell-center sign defines a deliberately coarse diagnostic coastline. This is
    not certified generated-world routing or shore geometry (MMR17).
    """
    size = len(grid) - 1
    cell = extent / size
    groups, centers, slopes = [], {}, {}
    for r in range(size):
        row = []
        for c in range(size):
            heights = (grid[r][c], grid[r][c + 1], grid[r + 1][c], grid[r + 1][c + 1])
            center = (heights[0] + heights[3]) / 2  # main-diagonal center
            slope = (max(heights) - min(heights)) / cell
            centers[c, r], slopes[c, r] = center, slope
            row.append(18 if center < 0 else 7 if slope > 1 else 1)
        groups.append(row)
    return groups, centers, slopes, cell


def build_mesh(grid, cell, groups):
    mesh = navmesh_bin.build_grid_navmesh(
        [[max(0., h) for h in row] for row in grid], cell, groups,
        triangulate='fixed', z_offset=0.2)
    require(not navmesh_bin.validate(mesh)['errors'], 'Generated navigation invalid')
    return mesh


OBSTRUCTION_GROUP = 5  # Steppe: land-valid and otherwise unused, so the engine can toggle exactly this strip
LAKE_GROUP = 8         # Lake: naval-valid, invalid for land; enclosed by land so no sea route reaches it


def navigation_tests(plan, groups, centers, cell, land, sea):
    """Seed-derived MMR17 route fixtures stamped into the face groups after placement."""
    size = len(groups)
    digest = hashlib.sha256((plan['worldId'] + ':navigation-tests').encode()).digest()
    town = plan['settlements'][0]
    t = tuple(town['landCell'])
    protected = {tuple(s['landCell']) for s in plan['settlements']}
    plain = lambda c, r: 0 <= c < size and 0 <= r < size and groups[r][c] == 1
    # Obstruction: a solid wall (at least two cells across, so no diagonal corner gap) across the
    # line from town_1 to a seed-ordered plain target 10-18 cells away. Accepted only when every
    # shortest 4-connected land route uses the wall and a land detour at least 4 cells longer exists,
    # so closing the wall must reroute rather than disconnect.
    half = 3 + digest[1] % 2
    def wall_for(target):
        length = math.dist(t, target)
        ux, uy = (target[0] - t[0]) / length, (target[1] - t[1]) / length
        mx, my = (t[0] + target[0]) / 2, (t[1] + target[1]) / 2
        reach = half + 2
        return [(c, r) for c in range(math.floor(mx - reach), math.ceil(mx + reach) + 1)
                for r in range(math.floor(my - reach), math.ceil(my + reach) + 1)
                if abs((c - mx) * ux + (r - my) * uy) <= 1.0 and abs((r - my) * ux - (c - mx) * uy) <= half]
    def detour(start, goal, closed):
        seen, frontier, steps = {start}, [start], 0
        while frontier:
            if goal in seen:
                return steps
            steps += 1
            frontier = [n for c in frontier for n in neighbors(*c, size) if n not in seen and n not in closed and plain(*n)
                        and not seen.add(n)]
        return None
    town_land = town['landComponent']
    targets = sorted([cr for cr, comp in land.items() if comp == town_land and plain(*cr) and 10 <= math.dist(cr, t) <= 18],
                     key=lambda cr: hashlib.sha256(digest + b':obstruction:' + bytes(str(cr), 'ascii')).digest())
    chosen, rejected = None, Counter()
    for target in targets:
        # Only plain band cells become wall; slope and sea band cells are already land-invalid.
        wall = [c for c in wall_for(target) if plain(*c)]
        if len(wall) < 2 * half or min(math.dist(c, p) for c in wall for p in protected | {target}) < 1.5:
            rejected['short or near settlement'] += 1; continue
        direct = detour(t, target, set())
        around = detour(t, target, set(wall))
        if direct is not None and around is not None and direct + 4 <= around <= 3 * direct:
            chosen = (target, wall, direct, around)
            break
        rejected['unreachable' if direct is None else 'no detour' if around is None else 'detour too short' if around < direct + 4
                 else 'detour too long'] += 1
    require(chosen, 'No seed-ordered obstruction site with a land detour on this terrain: %d targets, %s' % (len(targets), dict(rejected)))
    target, strip, direct, around = chosen
    # Lake: 3x3 plain block on town_2's landmass, ringed by plain land, seed-ordered choice. The towns
    # share a landmass, so a lake site is accepted only if it leaves the wall's verified direct and
    # detour lengths unchanged. Chosen before stamping so wall cells still count as plain here.
    lc = plan['settlements'][1]['landComponent']
    blocked = set(strip) | protected
    def lake_ok(c, r):
        cells = [(c + i, r + j) for i in range(-2, 3) for j in range(-2, 3)]
        return all(0 <= x < size and 0 <= y < size and groups[y][x] == 1 and land.get((x, y)) == lc and (x, y) not in blocked for x, y in cells)
    candidates = sorted([cr for cr, comp in land.items() if comp == lc and lake_ok(*cr)],
                        key=lambda cr: hashlib.sha256(digest + bytes(str(cr), 'ascii')).digest())
    lake = lake_cells = None
    for candidate in candidates:
        cells = {(candidate[0] + i, candidate[1] + j) for i in (-1, 0, 1) for j in (-1, 0, 1)}
        if detour(t, target, cells) == direct and detour(t, target, cells | set(strip)) == around:
            lake, lake_cells = candidate, sorted(cells)
            break
    require(lake, 'No enclosed lake site on the town_2 landmass that leaves the obstruction detour unchanged')
    for c, r in strip:
        groups[r][c] = OBSTRUCTION_GROUP
    for c, r in lake_cells:
        groups[r][c] = LAKE_GROUP
    def point(cr, z_land=True):
        return [(cr[0] + .5) * cell, (cr[1] + .5) * cell, centers[cr] if z_land else 0.]
    # Off-landmass coast: the coastal plain cell nearest town_1 on a different land component of at
    # least 9 cells that touches the ports' sea, on a landmass separated from town_1's by water alone
    # (all non-water cells, slopes included, form different components), not by a ridge or a one-cell islet.
    port_sea = plan['settlements'][0]['seaComponent']
    component_cells = Counter(land.values())
    landmass, _ = components(groups, {1, 7, OBSTRUCTION_GROUP})
    island = None
    for (c, r), comp in sorted(land.items(), key=lambda kv: (math.dist(kv[0], t), kv[0][1], kv[0][0])):
        if comp == town_land or component_cells[comp] < 9 or groups[r][c] != 1 or landmass.get((c, r)) == landmass[t]:
            continue
        water = [n for n in neighbors(c, r, size) if sea.get(n) == port_sea]
        if water:
            island = ((c, r), water[0]); break
    open_sea = max((cr for cr, comp in sea.items() if comp == port_sea),
                   key=lambda cr: (min(math.dist(cr, l) for l in [tuple(s['landCell']) for s in plan['settlements']]), -cr[1], -cr[0]))
    return dict(
        obstruction=dict(group=OBSTRUCTION_GROUP, terrain='Steppe', cells=[list(c) for c in strip], fromSettlement=town['id'],
                         to=point(target), directCells=direct, detourCells=around, halfLength=half,
                         note='Land-valid; disabling face group 5 at runtime closes the wall; the builder verified a plain-land detour'),
        lake=dict(group=LAKE_GROUP, terrain='Lake', center=point(lake, False), cells=[list(c) for c in lake_cells],
                  westShore=point((lake[0] - 2, lake[1])), eastShore=point((lake[0] + 2, lake[1]))),
        offLandmassCoast=None if island is None else dict(land=point(island[0]), sea=point(island[1], False),
                                                          landComponent=land[island[0]], landComponentCells=component_cells[land[island[0]]]),
        openSea=point(open_sea, False),
        seed='sha256(worldId + ":navigation-tests")')


def placements(grid, extent, world_id, scene_name, ids):
    groups, centers, slopes, cell = navigation(grid, extent)
    land, land_sizes = components(groups, {1})
    sea, sea_sizes = components(groups, {18})
    size = len(groups)
    candidates = []
    for (c, r), lc in land.items():
        if not (3 <= c < size - 3 and 3 <= r < size - 3 and centers[c, r] > .1):
            continue
        for neighbor in neighbors(c, r, size):
            if neighbor in sea and centers[neighbor] < -.1:
                candidates.append(((c, r), neighbor, lc, sea[neighbor]))
    require(candidates, 'No eligible generated coast; no stock-coordinate fallback')
    # Prefer the largest common land/sea components; deterministic row/column tie breaks.
    pair_counts = Counter((v[2], v[3]) for v in candidates)
    pairs = sorted(pair_counts, key=lambda p: (-min(land_sizes[p[0]], sea_sizes[p[1]]), -pair_counts[p], p))
    selected = None
    for pair in pairs:
        eligible = [v for v in candidates if (v[2], v[3]) == pair]
        first = min(eligible, key=lambda v: (slopes[v[0]], v[0][1], v[0][0]))
        second = max(eligible, key=lambda v: ((v[0][0]-first[0][0])**2+(v[0][1]-first[0][1])**2, -slopes[v[0]], v[0]))
        if math.dist(first[0], second[0]) * cell >= extent * .2:
            selected = [first, second]
            break
    require(selected is not None, 'No two separated ports on common generated land/sea components')
    def point(cr, water=False):
        return [(cr[0] + .5) * cell, (cr[1] + .5) * cell, 0. if water else centers[cr]]
    used = {v[0] for v in selected}
    towns, villages = [], []
    for i, (town, port, lc, sc) in enumerate(selected):
        options = [cr for cr, component in land.items() if component == lc and cr not in used
                   and 3 <= cr[0] < size - 3 and 3 <= cr[1] < size - 3
                   and 3 <= math.dist(cr, town) <= size * .12 and centers[cr] > .5
                   and all(math.dist(cr, other) >= 3 for other in used)]
        require(options, 'No eligible bound village near generated port')
        village = min(options, key=lambda cr: (slopes[cr], abs(math.dist(cr, town)-5), cr[1], cr[0]))
        used.add(village)
        towns.append(dict(id=ids[i], kind='town', position=point(town), gatePosition=point(town),
                          boundTo=None, portPosition=point(port, True), landCell=list(town),
                          seaCell=list(port), landComponent=lc, seaComponent=sc))
        villages.append(dict(id=ids[i+2], kind='village', position=point(village), gatePosition=point(village),
                             boundTo=ids[i], portPosition=None, landCell=list(village), landComponent=lc))
    plan = dict(schema=SCHEMA, worldId=world_id, sceneName=scene_name, extent=extent, seaLevel=0.,
                placementPolicy=POLICY, coordinateSystem='world units: +X columns, +Y rows, Z quantized source; origin 0,0',
                settlements=towns+villages, playerStart=list(towns[0]['gatePosition']))
    plan['navigationTests'] = navigation_tests(plan, groups, centers, cell, land, sea)
    mesh = build_mesh(grid, cell, groups)
    plan['nav'] = dict(gridSide=size+1, spacing=cell, faces=len(mesh.faces), terrainGroups=dict(Counter(f.group for f in mesh.faces)),
                       landComponents=len(land_sizes), seaComponents=len(sea_sizes),
                       coastPolicy='main-diagonal source center <0 sea18; slope>1 mountain7; otherwise plain1; then MMR17 stamps steppe5 strip and lake8',
                       geometry='source grid, main diagonal, vertexZ=max(seaLevel,quantizedHeight)+0.2',
                       nativeRouteValidated=False)
    return plan, mesh


def entity(parent, name, position=(0, 0, 0), tags=(), mesh=None, levels=()):
    e = ET.SubElement(parent, 'game_entity', name=name, mobility='1')
    if tags:
        ts = ET.SubElement(e, 'tags')
        for tag in tags:
            ET.SubElement(ts, 'tag', name=tag)
    ET.SubElement(e, 'transform', position=', '.join(format(v, '.9g') for v in position), rotation_euler='0, 0, 0')
    if mesh:
        ET.SubElement(ET.SubElement(e, 'components'), 'meta_mesh_component', name=mesh)
    if levels:
        ls = ET.SubElement(e, 'levels')
        for level in levels:
            ET.SubElement(ls, 'level', name=level)
    return e


def script(e, name, variables=None):
    scripts = e.find('scripts')
    if scripts is None:
        scripts = ET.SubElement(e, 'scripts')
    s = ET.SubElement(scripts, 'script', name=name)
    if variables:
        vs = ET.SubElement(s, 'variables')
        for key, value in variables.items():
            ET.SubElement(vs, 'variable', name=key, value=str(value))
    return s


def scene_entities(root, official, plan, grid):
    es = root.find('entities')
    require(es is not None and not list(es), 'Encoder scene must have empty entities')
    extent = plan['extent']
    entity(es, 'border_min', (0, 0, 0))
    entity(es, 'border_max', (extent, extent, max(max(row) for row in grid) + 100))
    # Only script variable/resource definitions copied; every entity identity and
    # coordinate authored here. No official settlement hierarchy/world markers.
    for name in ('Town Scene Manager', 'MapColorGradeManager', 'CampaignMapSiegePrefabEntityCache'):
        matches = official.findall("./entities/game_entity/scripts/script[@name='%s']" % name)
        require(len(matches) == 1, 'Missing/ambiguous installed helper ' + name)
        e = entity(es, plan['worldId'] + '_helper_' + re.sub('[^a-z0-9]', '_', name.lower()))
        ET.SubElement(e, 'scripts').append(copy.deepcopy(matches[0]))
    for item in plan['settlements']:
        sid = item['id']
        e = entity(es, sid, item['position'], tags=(item['kind'],))
        script(e, 'Town Entity Manager', {'Override Factor Color':'false', 'Factor Color':'1, 1, 1, 1'})
        ch = ET.SubElement(e, 'children')
        def marker(suffix, tag, position=(0, 0, 0)):
            return entity(ch, sid+'_'+suffix, position, tags=(tag,))
        # Reusable icon meshes are referenced, never extracted/copied.
        icon = 'mi_emp_keep_1' if item['kind']=='town' else 'village_empire_1'
        entity(ch, sid+'_icon', mesh=icon)
        collider = marker('selection', 'bo_town' if item['kind']=='town' else 'bo_village', (0,0,1))
        physics = ET.SubElement(collider, 'physics', shape='bo_sphere_collider')
        ET.SubElement(ET.SubElement(physics, 'body_flags'), 'body_flag', name='only_collide_with_raycast')
        marker('circle', 'map_settlement_circle')
        # SettlementVisual.GetBannerPositionForParty calls GetChild(0) on a placeholder for each
        # lord inside a fortification; a childless placeholder faults in TaleWorlds.Native.dll
        # (PID31916/35676). Installed placeholders carry one editor-only VolumeBox child.
        for n in range(4 if item['kind'] == 'town' else 1):
            banner = marker('banner_' + str(n), 'map_banner_placeholder', (n - 1.5, 0, 2))
            volume = ET.SubElement(ET.SubElement(banner, 'children'), 'game_entity', name=sid+'_banner_'+str(n)+'_volume', mobility='1')
            masks = ET.SubElement(volume, 'visibility_masks')
            ET.SubElement(masks, 'visibility_mask', name='visible_only_when_editing', value='true')
            ET.SubElement(volume, 'transform', position='0.5, 0.5, -0.5', rotation_euler='0, 0, 0')
            ET.SubElement(ET.SubElement(volume, 'components'), 'meta_mesh_component', name='volume_box')
            script(volume, 'VolumeBox', {'NavMeshPrefabName': ''})
        if item['kind'] != 'town':
            continue
        marker('gate', 'main_map_city_gate')
        offset = tuple(p-q for p,q in zip(item['portPosition'], item['position']))
        marker('port', 'main_map_city_port', offset)  # must be a direct child
        script(e, 'BlockadePositionScript', {'MaximumNumberOfShips':'10', 'NumberOfArcs':'4',
               'DistanceBetweenShips':'1.555', 'DistanceRandomizationOnArcs':'0.100',
               'DistanceRandomizationBetweenArcs':'0.100', 'Angle':'1.901',
               'MissionShipId':'dromon_ship_nested', 'ShipScaleFactor':'0.045',
               'IsVisualizationEnabled':'false', 'IsRandomizationEnabled':'false', 'IsShipVisualizationEnabled':'false'})
        for suffix, sign in [('start',-1),('end',1)]:
            marker('blockade_'+suffix, 'Blockade_Arc_'+suffix.title(), (offset[0]+sign,offset[1],offset[2]))
        for camp in (1,2):
            marker('camp_'+str(camp), 'map_camp_area_'+str(camp), (-3, camp*2, 0))
        for n in range(4):
            marker('attacker_'+str(n), 'map_siege_engine_'+str(n), (-3,n-1.5,0))
            marker('defender_'+str(n), 'map_defensive_engine_'+str(n), (2,n-1.5,0))
        marker('ram', 'map_siege_ram', (-2,0,0))
        for n in range(2):
            marker('tower_'+str(n), 'map_siege_tower', (-2,n*2-1,0))
    # A new plane using installed mesh/material/physics definitions. No stock
    # water_flowmap script, authored flowmap or ocean-gradient geography copied.
    water = entity(es, plan['worldId']+'_water', (extent/2,extent/2,0), tags=('snapped_water_body',))
    water.find('transform').set('scale', '%s, %s, 1' % (extent*2, extent*2))
    ET.SubElement(water, 'physics', shape='bo_editor_plane')
    component = ET.SubElement(ET.SubElement(water, 'components'), 'meta_mesh_component', name='editor_plane_high')
    ET.SubElement(component, 'mesh', name='editor_plane_high', material='naval_worldmap_water', argument='0, 0, 0, 0')


def validate_scene(folder, plan, official):
    root = ET.parse(folder/'scene.xscene').getroot()
    require(root.get('name') == plan['sceneName'], 'Scene identity mismatch')
    entities = list(root.iter('game_entity'))
    names = [e.get('name') for e in entities]
    require(len(names)==len(set(names)), 'Duplicate entity names')
    old_ids = {e.get('name') for e in official.find('entities')
               if e.get('name','').startswith(('town_','village_','castle_','hideout_','retirement_'))}
    require(not old_ids.intersection(names), 'Official world entity retained')
    require(all(n in ('border_min','border_max') or n.startswith(plan['worldId']+'_') or
                n in {s['id'] for s in plan['settlements']} or
                any(n.startswith(s['id']+'_') for s in plan['settlements']) for n in names), 'Unexpected entity identity')
    scripts = [s.get('name') for s in root.findall('.//scripts/script')]
    require('SettlementPositionScript' not in scripts and not root.findall('.//game_entity[@prefab="MapSnowAndRainDefiner"]'), 'Stock cache/climate loader retained')
    require(not root.findall('Paths') and not root.findall('camera_data'), 'Authored world geography retained')
    for settlement in plan['settlements']:
        e = root.find("./entities/game_entity[@name='%s']" % settlement['id'])
        require(e is not None, 'Missing generated settlement marker')
        banners = [b for b in e.findall('./children/game_entity') if b.find('tags/tag[@name="map_banner_placeholder"]') is not None]
        require(len(banners) == (4 if settlement['kind']=='town' else 1), 'Unexpected banner placeholder count')
        require(all(len(b.findall('./children/game_entity'))==1 and b.find('./children/game_entity/components/meta_mesh_component[@name="volume_box"]') is not None
                    for b in banners), 'Banner placeholder needs exactly one volume_box child')
        if settlement['kind']=='town':
            require(len(e.findall('./children/game_entity/tags/tag[@name="main_map_city_port"]'))==1, 'Port must be a direct child')
        else:
            require(settlement['boundTo'] in {s['id'] for s in plan['settlements'] if s['kind']=='town'}, 'Village binding dangling')
    _,_,payload=navmesh_bin.read_navmesh_file(str(folder/'navmesh.bin'))
    mesh=navmesh_bin.decode_payload(payload)
    check=navmesh_bin.validate(mesh)
    require(not check['errors'], 'Written navmesh invalid')
    require(len(mesh.faces)==plan['nav']['faces'], 'Navmesh face count mismatch')
    groups_written = {f.group for f in mesh.faces}
    require(groups_written <= {1, 7, 18, OBSTRUCTION_GROUP, LAKE_GROUP} and {1, 18, OBSTRUCTION_GROUP, LAKE_GROUP} <= groups_written, 'Generated land/sea/test groups missing')
    tests = plan['navigationTests']
    size = plan['nav']['gridSide'] - 1
    face_group = {}  # two triangles per cell, row-major: cell (c, r) -> faces 2*(r*size+c), +1
    for index, face in enumerate(mesh.faces):
        face_group.setdefault(index // 2, set()).add(face.group)
    require(all(face_group[r * size + c] == {OBSTRUCTION_GROUP} for c, r in tests['obstruction']['cells']), 'Obstruction cells not written as steppe faces')
    require(all(face_group[r * size + c] == {LAKE_GROUP} for c, r in tests['lake']['cells']), 'Lake cells not written as lake faces')
    return dict(offlineValid=True, nativeValidated=False, entityCount=len(entities), settlementCount=4,
                portCount=2, officialSettlementEntities=0, navFaces=len(mesh.faces), navValidation=check,
                retainedOfficialTopology=False, retainedOfficialDistanceCache=False)


def build(args):
    world_id, scene_name = identifier(args.world_id), identifier(args.scene_name)
    ids = args.settlement_ids or [world_id+s for s in ('_town_1','_town_2','_village_1','_village_2')]
    ids = [identifier(i) for i in ids]
    require(len(ids)==4 and len(set(ids))==4, 'Need four distinct IDs: two towns, then two villages')
    require(math.isfinite(args.extent) and args.extent>0 and math.isfinite(args.scale) and args.scale>0, 'Extent/scale must be positive')
    source = Path(args.heightfield).resolve(); read_field(source)
    template, encoder = Path(args.surface_template).resolve(), Path(args.encoder).resolve()
    require(encoder.is_file(), 'Missing encoder')
    official_dir = Path(args.game_root).resolve()/'Modules/NavalDLC/SceneObj/Main_map'
    official = ET.parse(official_dir/'scene.xscene').getroot()
    referenced_meshes = {'mi_emp_keep_1', 'village_empire_1', 'editor_plane_high', 'volume_box'}
    require(referenced_meshes <= {e.get('name') for e in official.findall('.//meta_mesh_component')},
            'Installed map no longer references required reusable icon/water meshes')
    out = output_path(args.out); out.mkdir(parents=True)
    local_template = out/'encoder-template'; local_template.mkdir()
    for name in FILES:
        shutil.copyfile(template/name, local_template/name)
    xml=ET.parse(local_template/'scene.xscene'); xml.getroot().set('name',scene_name)
    xml.write(local_template/'scene.xscene',encoding='utf-8',xml_declaration=True)
    folder=out/scene_name
    command=[str(encoder),'--surface-heightfield',str(source),'--surface-template',str(local_template),
             '--surface-output',str(folder),'--surface-extent',str(args.extent),'--surface-scale',str(args.scale)]
    result=subprocess.run(command,capture_output=True,text=True,check=False)
    (out/'encoder.stdout.log').write_text(result.stdout,encoding='utf-8');(out/'encoder.stderr.log').write_text(result.stderr,encoding='utf-8')
    require(result.returncode==0,'Native encoder failed; see isolated logs')
    grid=read_field(folder/'quantized-heightfield.bin')
    info=json.loads((folder/'surface.json').read_text(encoding='utf-8'))
    decoded=terrain_bin.decode_heights(terrain_bin.parse((folder/'terrain.bin').read_bytes()),info['minHeight'],info['maxHeight'])
    require(len(grid)==len(decoded) and max(abs(a-b) for ra,rb in zip(grid,decoded) for a,b in zip(ra,rb))<1e-9,'Independent terrain decode differs from quantized field')
    plan,mesh=placements(grid,args.extent,world_id,scene_name,ids)
    plan['sourceSha256']=sha(source); plan['terrainSha256']=sha(folder/'terrain.bin')
    plan['quantizedHeightfieldSha256']=sha(folder/'quantized-heightfield.bin')
    root=ET.parse(folder/'scene.xscene').getroot()
    levels=root.find('levels');root.remove(levels);root.insert(1,copy.deepcopy(official.find('levels')))
    scene_entities(root,official,plan,grid)
    ET.indent(root,space='\t');ET.ElementTree(root).write(folder/'scene.xscene',encoding='utf-8',xml_declaration=True)
    navmesh_bin.write_scene_navmesh(mesh,str(folder/'navmesh.bin'))
    shutil.copyfile(official_dir/'atmosphere.xml',folder/'atmosphere.xml')
    # Preserve the encoder-stage receipt outside the finished scene: its original
    # 33x33 nav statistics no longer describe our generated campaign navmesh.
    (folder/'surface.json').replace(out/'encoder-surface.json')
    plan['sceneFiles']={name:sha(folder/name) for name in FILES}
    plan['runtimeRequirements']=[
        'Register this sceneName to the owned module after installation, before Scene.Read.',
        'Load generated settlement IDs/positions before settlement visual startup; assign town PortPosition from descriptor.',
        'Register fresh world-owned Default/Naval/All navigation caches before consumers; no SettlementPositionScript or official cache fallback.',
        'Initialize generated snow/rain/wind data before weather caches; MapSnowAndRainDefiner intentionally absent.',
        'Replace or explicitly disable native stock battle-index geography; current DiagnosticMapScene.GetBattleSceneIndexMap requires root integration.',
        'Validate port entry/exit, water rendering, native navigation groups and placement in runtime; offline topology is not route acceptance.'
    ]
    plan['scope']='Instrumented four-settlement bootstrap placement derived from generated terrain; not full generated-content or routing acceptance.'
    write_json(out/'placement.json',plan)
    validation=validate_scene(folder,plan,official)
    report=dict(schema='mnbl.bootstrap-scene-build.v1',worldId=world_id,sceneName=scene_name,
                status='offline_valid_runtime_pending',validation=validation,placementSha256=sha(out/'placement.json'),
                command=command,encoderExitCode=result.returncode,
                encoderStageReceipt=dict(path='encoder-surface.json',sha256=sha(out/'encoder-surface.json'),
                                         scope='Before campaign entities, navigation and atmosphere replacement'),
                inputs=[dict(path=str(p),sha256=sha(p)) for p in [Path(__file__),REPO/'tools/scenebin/navmesh_bin.py',
                        REPO/'tools/scenebin/terrain_bin.py',encoder,source,template/'scene.xscene',official_dir/'scene.xscene',official_dir/'atmosphere.xml']],
                files=plan['sceneFiles'],retainedReusableContent=['terrain material/environment definitions','passive campaign atmosphere presets',
                    'scene level names','siege projectile prefab definitions','map color grade settings','referenced icon meshes/water material'],
                prohibitedContentRetained=[],runtimeRequirements=plan['runtimeRequirements'])
    write_json(out/'build-receipt.json',report)  # completion receipt last
    return report


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--world-id',required=True);parser.add_argument('--scene-name',required=True)
    parser.add_argument('--heightfield',required=True);parser.add_argument('--extent',type=float,required=True)
    parser.add_argument('--scale',type=float,default=.01)
    parser.add_argument('--surface-template',default='build/slots/mnbl_world_b')
    parser.add_argument('--encoder',default='build/worldbridge/Release/worldbridge.exe')
    parser.add_argument('--game-root',default=build_slot.GAME)
    parser.add_argument('--settlement-ids',nargs=4,metavar=('TOWN1','TOWN2','VILLAGE1','VILLAGE2'))
    parser.add_argument('--out',required=True)
    args=parser.parse_args()
    print(json.dumps(build(args),indent=2))


if __name__=='__main__':
    main()
