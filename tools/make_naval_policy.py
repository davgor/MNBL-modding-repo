"""MMR-35: write worldgen/policies/naval-content.json, the versioned policy for a generated world's ports, fleets and naval faction records
(docs/naval-content.md).

From the installed campaign (the hash-checked catalog) and the installed War Sails campaign map scene (NavalDLC Main_map, hash-checked against
evidence/baseline/scene-contract.json) it derives what NavalContentFactory needs, never a Calradia coordinate or id of a world object:
  * shipyards: the installed port towns' shipyard level shares, and the hull tier each level unlocks (CanTownCreateShipFromHull);
  * hulls: every creation culture's available ship hulls with their type and production weight, and the reference chain each one needs,
    checked against the catalog and the installed files: mission ship, its nested prefab (NavalDLC AssetPackages/nested_prefabs_packed.xml)
    and the deck navmesh its MissionShip script names (NavMeshPrefabs/<NavMeshPrefabName>.bin), ship slots, and every upgrade piece's target
    slots and siege engine (a verified live default). Crew, captain and reinforcement spawn points live in the prefab's nested links and are
    resolved only when a mission builds the ship (MMR-42/MMR-51);
  * blockades: the installed BlockadePositionScript parameters (median) and the arc ends relative to the port, in the frame of the
    centre-to-port direction (the only coastline orientation the installed scene carries);
  * landings: the installed village drop-off points (main_map_village_dropoff) relative to the village centre and port;
  * pirates: the installed pirate clans and their templates; the PirateSpawnPoint entities clustered by position (the installed scene
    stacks an inner and an outer zone of one clan at most positions, and PiratesCampaignBehavior keeps both: FindIdenticalZone merges only
    equal position and radius): positions per port town, the outer and inner radii and the share of positions with both, the positions'
    distance from the nearest port town relative to the installed town spacing, and the clan weights by the nearest port town's culture;
  * fleets: who may hold ships on a generated world;
  * quality: the codes a refused naval build reports.
Usage: python tools/make_naval_policy.py
"""
import collections
import hashlib
import json
import math
import re
import os
import statistics
import xml.etree.ElementTree as ET

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CATALOG = 'evidence/runtime/in-game/campaign_catalog-10212.json'
SCENE_CONTRACT = 'evidence/baseline/scene-contract.json'
PIRATES = ['northern_pirates', 'southern_pirates']
TIERS = {'light': 1, 'medium': 2, 'heavy': 3}   # NavalDLC CanTownCreateShipFromHull: light >= 1, medium >= 2, heavy == 3


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def od(*pairs):
    return collections.OrderedDict(pairs)


def q(xs, p):
    xs = sorted(xs)
    return xs[min(len(xs) - 1, int(p * (len(xs) - 1) + 0.5))]


def floats(text):
    return [float(x) for x in text.split(',')]


def kids(e, name):
    c = e.find(name)
    return list(c) if c is not None else []


def rotate(x, y, a):
    return x * math.cos(a) - y * math.sin(a), x * math.sin(a) + y * math.cos(a)


def main():
    receipt = json.load(open(os.path.join(REPO, CATALOG), encoding='utf-8-sig'))
    docs = {}
    for d in receipt['outputs']['documents']:
        if sha(d['path']) != d['sha256']:
            raise SystemExit('catalog %s differs from its receipt' % d['id'])
        docs[d['id']] = d
    xml = {k: ET.parse(docs[k]['path']).getroot() for k in ('Settlements', 'Factions', 'SPCultures', 'partyTemplates', 'ShipHulls', 'ShipSlots',
                                                             'ShipUpgradePieces', 'MissionShips')}
    # The installed War Sails map scene, the one the scene contract hashed.
    contract = json.load(open(os.path.join(REPO, SCENE_CONTRACT), encoding='utf-8-sig'))
    naval_scene = [c for c in contract['scene_resolution']['collisions']['Main_map'] if 'NavalDLC' in c['path']][0]
    game = receipt['outputs'].get('gameRoot') or r'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord'
    scene_path = naval_scene['path'].replace('<game>', game)
    if sha(scene_path) != naval_scene['sha256']:
        raise SystemExit('the installed War Sails map scene differs from the scene contract')

    # Installed settlements.
    pos, port, culture, kind, shipyard, started = {}, {}, {}, {}, {}, []
    for s in xml['Settlements']:
        comp = s.find('Components')
        if comp is None or not len(comp):
            continue
        i = s.get('id'); c = comp[0]
        pos[i] = (float(s.get('posX')), float(s.get('posY')))
        culture[i] = (s.get('culture') or '').split('.', 1)[-1]
        if s.get('port_posX'):
            port[i] = (float(s.get('port_posX')), float(s.get('port_posY')))
        if c.tag == 'Town':
            kind[i] = 'castle' if c.get('is_castle') == 'true' else 'town'
            b = [x for x in s.iter('Building') if x.get('id') == 'building_shipyard']
            if i in port and kind[i] == 'town':
                # A port town without a shipyard in XML gets one at its start level (1) at game start: BuildingsCampaignBehavior.
                # BuildDevelopmentsAtGameStart adds every allowed building, and NavalBuildingTypes registers the shipyard with start level 1.
                shipyard[i] = int(b[0].get('level')) if b else 1
                if not b:
                    started.append(i)
        elif c.tag == 'Village':
            kind[i] = 'village'
    port_towns = sorted(i for i in port if kind.get(i) == 'town')
    towns = sorted(i for i in kind if kind[i] == 'town')
    spacing = statistics.mean(min(math.dist(pos[t], pos[u]) for u in towns if u != t) for t in towns)
    levels = collections.Counter(shipyard[t] for t in port_towns)

    # Hulls and their chains.
    hulls = {h.get('id'): h for h in xml['ShipHulls']}
    missions = {m.get('id') for m in xml['MissionShips']}
    slots = {s.get('id') for s in xml['ShipSlots']}
    pieces = {p.get('id'): p for p in xml['ShipUpgradePieces']}
    cultures = {c.get('id'): c for c in xml['SPCultures']}
    creation = sorted(c for c, e in cultures.items() if e.get('is_main_culture') == 'true' and e.get('start_point_position_x') is not None)
    problems = []

    # The installed nested ship prefabs and the deck navmeshes their MissionShip scripts name.
    packed_path = os.path.join(game, 'Modules', 'NavalDLC', 'AssetPackages', 'nested_prefabs_packed.xml')
    packed = open(packed_path, encoding='utf-8', errors='replace').read()
    folders = [(m.start(), m.group(1)) for m in re.finditer(r'<prefab_folder name="([^"]+)">', packed)]
    navmesh_of = {}
    for k, (i0, name) in enumerate(folders):
        body = packed[i0:folders[k + 1][0] if k + 1 < len(folders) else len(packed)]
        m = re.search(r'<script name="MissionShip">.*?<variable name="NavMeshPrefabName" type_name="string" value="([^"]+)"', body, re.S)
        navmesh_of[name] = m.group(1) if m else None
    navmesh_dir = os.path.join(game, 'Modules', 'NavalDLC', 'NavMeshPrefabs')
    defaults_run = json.load(open(os.path.join(REPO, json.load(open(os.path.join(REPO, 'worldgen/policies/rosters.json'), encoding='utf-8'))['provenance']['defaultReferences']), encoding='utf-8-sig'))
    live_defaults = set(defaults_run['outputs']['worlds'][0]['registration']['verifiedLiveDefaultReferences'])
    mission_prefab = {m.get('id'): m.get('prefab') for m in xml['MissionShips']}

    def hull_chain(hid):
        h = hulls.get(hid)
        if h is None:
            problems.append('hull %s is not installed' % hid); return None
        mission = h.get('mission_ship')
        hslots = [s.get('id') for s in h.iter('ShipSlot')]
        if mission not in missions:
            problems.append('hull %s mission ship %s is not installed' % (hid, mission))
        prefab = mission_prefab.get(mission)
        navmesh = navmesh_of.get(prefab)
        if prefab not in navmesh_of:
            problems.append('hull %s mission prefab %s is not an installed nested ship prefab' % (hid, prefab))
        elif not navmesh or not os.path.isfile(os.path.join(navmesh_dir, navmesh + '.bin')):
            problems.append('hull %s prefab %s names no installed deck navmesh (%s)' % (hid, prefab, navmesh))
        for s in hslots:
            if s not in slots:
                problems.append('hull %s slot %s is not installed' % (hid, s))
        if h.get('ship_type') not in TIERS:
            problems.append('hull %s has ship_type %s' % (hid, h.get('ship_type')))
        return od(('type', h.get('ship_type')), ('tier', TIERS.get(h.get('ship_type'))), ('productionWeight', float(h.get('production_build_weight') or 0)),
                  ('missionShip', mission), ('prefab', prefab), ('deckNavmesh', navmesh), ('slots', hslots),
                  ('crew', od(('skeletal', int(h.get('skeletal_crew_capacity'))), ('total', int(h.get('total_crew_capacity'))))))

    culture_hulls = od()
    for c in creation:
        e = cultures[c]
        ids = [x.get('id').split('.', 1)[-1] for x in e.find('available_ship_hulls')]
        culture_hulls[c] = od(*[(i, hull_chain(i)) for i in ids])
    siege_engines = set()
    for p in pieces.values():
        for t in kids(p, 'TargetSlots'):
            if t.get('id') and t.get('id') not in slots:
                problems.append('upgrade piece %s targets slot %s' % (p.get('id'), t.get('id')))
        if p.get('siege_engine'):
            siege_engines.add(p.get('siege_engine').split('.', 1)[-1])
            if p.get('siege_engine') not in live_defaults:
                problems.append('upgrade piece %s siege engine %s is not a verified live default' % (p.get('id'), p.get('siege_engine')))
    templates = {t.get('id'): t for t in xml['partyTemplates']}

    def template_hulls(tid):
        t = templates.get(tid)
        if t is None:
            problems.append('party template %s is not installed' % tid); return []
        stacks = [od(('hull', x.get('id').split('.', 1)[-1]), ('min', int(x.get('min_value'))), ('max', int(x.get('max_value')))) for x in kids(t, 'ship_hulls')]
        for s in stacks:
            hull_chain(s['hull'])
        return stacks

    naval_roles = od()
    for c in creation:
        e = cultures[c]
        roles = od(*[(k, e.get(k).split('.', 1)[-1]) for k in ('fishing_party_template', 'settlement_patrol_template_coastal', 'shipwright', 'shipyard_worker') if e.get(k)])
        roles['templateHulls'] = od(*[(roles[k], template_hulls(roles[k])) for k in ('fishing_party_template', 'settlement_patrol_template_coastal') if k in roles])
        naval_roles[c] = roles

    # The installed War Sails map scene: blockades, drop-offs and pirate spawn points.
    root = ET.parse(scene_path).getroot()
    entities = root.find('entities')
    blockades, dropoffs, spawns = [], [], []

    def walk(e, ox, oy, oa, osx, osy, top):
        # World position of each entity: the parent's position, rotation (z) and scale applied to the local position.
        t = e.find('transform')
        lx, ly, la, sx, sy = 0.0, 0.0, 0.0, 1.0, 1.0
        if t is not None:
            p = floats(t.get('position', '0,0,0')); lx, ly = p[0], p[1]
            if t.get('rotation_euler'):
                la = floats(t.get('rotation_euler'))[2]
            if t.get('scale'):
                sc = floats(t.get('scale')); sx, sy = sc[0], sc[1]
        rx, ry = rotate(lx * osx, ly * osy, oa)
        x, y, a = ox + rx, oy + ry, oa + la
        own_tags = {g.get('name') for g in kids(e, 'tags')}
        yield e, x, y, a, own_tags, top
        for c in kids(e, 'children'):
            yield from walk(c, x, y, a, osx * sx, osy * sy, top)

    for top in entities.findall('game_entity'):
        name = top.get('name')
        own_scripts = {s.get('name'): {v.get('name'): v.get('value') for v in s.iter('variable')} for s in kids(top, 'scripts')}
        if 'PirateSpawnPoint' in own_scripts:
            t = floats(top.find('transform').get('position'))
            v = own_scripts['PirateSpawnPoint']
            spawns.append(od(('clan', v.get('ClanStringId')), ('x', t[0]), ('y', t[1]), ('radius', float(v.get('Radius', '0')))))
            continue
        nodes = list(walk(top, 0.0, 0.0, 0.0, 1.0, 1.0, name))
        if name in port and kind.get(name) == 'town' and 'BlockadePositionScript' in own_scripts:
            starts = [(x, y) for e, x, y, a, tags, _ in nodes if 'Blockade_Arc_Start' in tags]
            ends = [(x, y) for e, x, y, a, tags, _ in nodes if 'Blockade_Arc_End' in tags]
            centre = (nodes[0][1], nodes[0][2])
            v = own_scripts['BlockadePositionScript']
            blockades.append(od(('town', name), ('centre', centre), ('start', starts[0] if starts else None), ('end', ends[0] if ends else None), ('script', v)))
        if kind.get(name) == 'village':
            for e, x, y, a, tags, _ in nodes:
                if 'main_map_village_dropoff' in tags:
                    dropoffs.append(od(('village', name), ('x', x), ('y', y), ('centre', (nodes[0][1], nodes[0][2]))))
    # Blockade arcs in the port frame: u along centre -> port, v to its left.
    frames = []
    for b in blockades:
        t = b['town']; cx, cy = pos[t]; px, py = port[t]
        ux, uy = px - cx, py - cy; n = math.hypot(ux, uy); ux, uy = ux / n, uy / n; vx, vy = -uy, ux
        if b['start'] and b['end']:
            rel = lambda p: ((p[0] - px) * ux + (p[1] - py) * uy, (p[0] - px) * vx + (p[1] - py) * vy)
            frames.append((rel(b['start']), rel(b['end']), math.dist(b['centre'], pos[t])))
    scripts = [b['script'] for b in blockades]
    median = lambda key: statistics.median(float(s[key]) for s in scripts if s.get(key) is not None)
    # Drop-offs relative to the village centre and its XML port.
    drop_centre = [math.dist((d['x'], d['y']), pos[d['village']]) for d in dropoffs]
    drop_port = [math.dist((d['x'], d['y']), port[d['village']]) for d in dropoffs if d['village'] in port]
    # Pirate spawn points clustered by position: each position holds one clan's zones (an outer and, at most positions, an inner radius).
    positions = collections.OrderedDict()   # first point of a cluster -> its zones (points within 1 unit stack at one position)
    for s in spawns:
        key = next((k for k in positions if math.dist(k, (s['x'], s['y'])) < 1.0), (s['x'], s['y']))
        positions.setdefault(key, []).append(s)
    for key, zs in positions.items():
        if len({z['clan'] for z in zs}) != 1:
            problems.append('pirate spawn position %s holds zones of several clans' % (key,))
    outer = [max(z['radius'] for z in zs) for zs in positions.values()]
    inner = [min(z['radius'] for z in zs) for zs in positions.values() if len(zs) > 1]
    nearest = []
    weights = collections.OrderedDict()
    for (x, y), zs in positions.items():
        t = min(port_towns, key=lambda u: (math.dist((x, y), pos[u]), u))
        nearest.append(math.dist((x, y), port[t]) / spacing)
        weights.setdefault(culture[t], collections.Counter())[zs[0]['clan']] += 1
    factions = {f.get('id'): f for f in xml['Factions']}
    pirate_records = od()
    for p in PIRATES:
        f = factions[p]
        tid = f.get('default_party_template').split('.', 1)[-1]
        pc = f.get('culture').split('.', 1)[-1]
        pirate_records[p] = od(('culture', pc), ('cultureCanHaveSettlement', cultures[pc].get('can_have_settlement') == 'true'), ('isBandit', f.get('is_bandit') == 'true'),
                               ('template', tid), ('templateHulls', template_hulls(tid)), ('installedSpawnPoints', sum(1 for s in spawns if s['clan'] == p)),
                               ('installedPositions', sum(1 for zs in positions.values() if zs[0]['clan'] == p)))
    if problems:
        raise SystemExit('installed naval references do not resolve: %s' % problems[:10])
    playability = json.load(open(os.path.join(REPO, 'worldgen/policies/playability.json'), encoding='utf-8'))

    policy = od(
        ('schema', 'mnbl.naval-content'), ('version', 1),
        ('notes', 'MMR-35 generated ports, fleets and naval faction records (docs/naval-content.md). Owner: NavalContentFactory '
                  '(src/MNBL.ProceduralWorld/Campaign/NavalContentFactory.cs). Every value comes from the installed campaign and the installed War '
                  'Sails campaign map scene; no Calradia coordinate or settlement id is carried into a generated world. Scene entities (port '
                  'markers, blockade arcs, village drop-offs, pirate spawn points) are intents for MMR-40, which places the map entities.'),
        ('streams', od(('rule', 'value(stream, version, world prefix, entity, draw) = first 8 bytes of sha256("mnbl.naval-stream/1\\n" + stream + "\\n" + '
                                'version + "\\n" + prefix + "\\n" + entity + "\\n" + draw), big-endian; construction only'),
                       ('pirates', 1))),
        ('water', od(('rule', 'water cells are the travel cells of class sea, lake or river (cells.bin), joined by shared edges with columns wrapping; '
                              'a water component takes the MMR-28 basin id of the port and coast transitions on it; a component is open when it '
                              'holds a sea cell and at least minOpenCells cells (the MMR-28 rule). A sea cell is open sea when every cell within '
                              'openSeaCells (Chebyshev) is sea'),
                     ('minOpenCells', playability['basins']['minOpenCells']), ('openSeaCells', playability['cells']['openSeaCells']),
                     ('playabilitySha256', sha(os.path.join(REPO, 'worldgen/policies/playability.json'))))),
        ('shipyards', od(('rule', 'every port town on an open basin gets building_shipyard (BuildingsCampaignBehavior adds it at game start for '
                                  'IsTown && HasPort anyway, at its start level 1, and CanTownCreateShipFromHull dereferences it); its level '
                                  'follows the installed port towns\' shares, assigned in order of the port\'s harbour quality (the MMR-28 chosen '
                                  'candidate), best first: the top share level 3, then 2, the rest 1. The %d installed port towns without a shipyard '
                                  'in XML count at level 1, the level the game gives them at start (%s)' % (len(started), ', '.join(sorted(started)))),
                         ('installed', od(*[(str(k), v) for k, v in sorted(levels.items())])),
                         ('shares', od(*[(str(k), round(levels[k] / float(sum(levels.values())), 4)) for k in (3, 2, 1)])),
                         ('hullTiers', od(*TIERS.items())))),
        ('hulls', od(('rule', 'a shipyard builds the hulls of its town\'s culture (CultureObject.AvailableShipHulls) whose tier is at most its '
                              'level (heavy needs level 3), weighted by production_build_weight (ShipProductionCampaignBehavior.CreateShip); '
                              'every hull resolves its mission ship and ship slots'),
                     ('cultures', culture_hulls), ('roles', naval_roles))),
        ('blockades', od(('rule', 'each port town on an open basin gets a blockade intent: the installed BlockadePositionScript parameters '
                                  '(median) and its two arc ends at the installed median offsets from the port, in the frame of the centre-to-port '
                                  'direction (along, left); SiegeEvent activates a blockade when the besieged settlement HasPort and a besieger '
                                  'owns ships, and BlockadeVisualHelper reads the arc from the settlement entity (MMR-40)'),
                         ('installed', len(blockades)),
                         ('parameters', od(('MaximumNumberOfShips', int(median('MaximumNumberOfShips'))), ('NumberOfArcs', int(median('NumberOfArcs'))),
                                           ('DistanceBetweenShips', round(median('DistanceBetweenShips'), 4)), ('Angle', round(median('Angle'), 4)),
                                           ('MissionShipId', collections.Counter(s.get('MissionShipId') for s in scripts).most_common(1)[0][0]),
                                           ('ShipScaleFactor', round(median('ShipScaleFactor'), 4)))),
                         ('arc', od(('start', [round(statistics.median(f[0][0] for f in frames), 4), round(statistics.median(f[0][1] for f in frames), 4)]),
                                    ('end', [round(statistics.median(f[1][0] for f in frames), 4), round(statistics.median(f[1][1] for f in frames), 4)]),
                                    ('measured', len(frames)))))),
        ('landings', od(('rule', 'a harbour village whose MMR-33 port (its MMR-28 harbour cell) is water of an open basin gets a drop-off intent at '
                                 'that port (NavalMapSceneWrapper.InitializeDropOffLocations sets the village port from its main_map_village_dropoff '
                                 'entity); its distance from the centre must stay within the installed maximum plus one cell. A harbour cell on a '
                                 'closed basin or on no water is a named diagnostic, and the village gets no landing'),
                        ('installed', len({d['village'] for d in dropoffs})), ('installedEntities', len(dropoffs)),
                        ('installedVillagePorts', sum(1 for i in port if kind.get(i) == 'village')),
                        ('fromCentre', od(('median', round(statistics.median(drop_centre), 3)), ('maximum', round(max(drop_centre), 3)))),
                        ('fromXmlPort', od(('median', round(statistics.median(drop_port), 3)), ('maximum', round(max(drop_port), 3)), ('measured', len(drop_port)))))),
        ('pirates', od(('rule', 'the installed pirate clans are re-emitted with their installed records and ids as world aliases (PiratesCampaignBehavior '
                                'finds them structurally: a bandit clan whose culture cannot have settlements and whose default template has ship '
                                'hulls; NavalDLCVoiceOverModel compares their ids), homed at the generated hideout nearest their first spawn point. '
                                'Spawn positions: perPortTown x the port towns on open basins, shared over the open basins in proportion to their '
                                'port towns; each on an open-sea cell at least minPortDistance x spacing from every port and spawnSpacing x radius '
                                'from every other position, candidates in pirates-stream order; its clan drawn from the installed weights of its '
                                'nearest port town\'s culture (pooled for a culture without weights). Each position gets a PirateSpawnPoint zone of '
                                'radius and, drawn with innerShare, a second zone of innerRadius (radii relative to the installed town spacing), as '
                                'the installed scene stacks them. With no spawn point a clan spawns nothing (NavalDLCBanditDensityModel caps it at '
                                'its spawn points)'),
                       ('clans', pirate_records),
                       ('perPortTown', round(len(positions) / float(len(port_towns)), 4)),
                       ('radius', round(statistics.median(outer) / spacing, 4)),
                       ('innerRadius', round(statistics.median(inner) / spacing, 4)),
                       ('innerShare', round(len(inner) / float(len(positions)), 4)),
                       ('minPortDistance', round(q(nearest, 0.1), 4)), ('spawnSpacing', 1.0),
                       ('cultureWeights', od(*[(k, od(*sorted(v.items()))) for k, v in sorted(weights.items())])),
                       ('installed', od(('spawnPoints', len(spawns)), ('positions', len(positions)), ('positionsWithInnerZone', len(inner)),
                                        ('portTowns', len(port_towns)), ('spacing', round(spacing, 2)),
                                        ('radius', statistics.median(outer)), ('innerRadius', statistics.median(inner)),
                                        ('portDistance', od(('minimum', round(min(nearest), 4)), ('p10', round(q(nearest, 0.1), 4)), ('median', round(q(nearest, 0.5), 4)))))))),
        ('fleets', od(('rule', 'a clan may hold ships only when its home settlement\'s land group holds a port town on an open basin (its parties can '
                               'reach an anchor). The installed ShipTradeCampaignBehavior buys ships for a clan with a fixed chance (0.5 a day, and '
                               'at the new game\'s follow-ups) from its map faction\'s fiefs, else 20% of the time from any non-hostile town, for a '
                               'party ShipDistributionModel.CanPartyTakeShip accepts, with no reachability check; it sells when the clan holds more '
                               'than ClanShipOwnershipModel.GetIdealShipNumberForClan. The generated ShipDistributionModel refuses ships to the '
                               'parties of a clan without a reachable port (and fleet transfers to them), and the generated ClanShipOwnershipModel '
                               'gives such a clan an ideal fleet of 0, so any ship it still holds is sold; both delegate every other clan to the '
                               'installed models. A kingdom is coastal when one of its towns is such a port town, else landlocked (named reason). '
                               'Initial ships come from the installed path: ShipProductionCampaignBehavior builds them at shipyards and '
                               'ShipTradeCampaignBehavior sells them to clan parties (ChangeShipOwnerAction), during the new game\'s follow-ups'),)),
        ('quality', od(('rule', 'a world whose naval content cannot be built is refused (NavalContentFactory.NavalException) with these codes; a '
                                'refusal is retryable only when every code is'),
                       ('codes', od(
                           ('quality.naval_policy', od(('retryable', False), ('meaning', 'the naval policy is malformed, or disagrees with the installed catalog it is given (culture hull lists, culture naval roles, pirate templates)'))),
                           ('quality.naval_catalog', od(('retryable', False), ('meaning', 'an installed hull, slot, mission ship, template or pirate clan the world needs is missing'))),
                           ('quality.naval_unreachable_island', od(('retryable', True), ('meaning', 'a land group with towns has no port or coast transition to an open basin'))),
                           ('quality.naval_port', od(('retryable', True), ('meaning', 'a port town\'s or harbour village\'s port is not water of an open basin, or a village port is beyond the installed drop-off envelope'))))))),
        ('provenance', od(('catalogReceipt', CATALOG), ('settlementsSha256', docs['Settlements']['sha256']), ('factionsSha256', docs['Factions']['sha256']),
                          ('culturesSha256', docs['SPCultures']['sha256']), ('shipHullsSha256', docs['ShipHulls']['sha256']),
                          ('partyTemplatesSha256', docs['partyTemplates']['sha256']), ('mapScene', naval_scene['path']), ('mapSceneSha256', naval_scene['sha256']),
                          ('nestedPrefabsSha256', sha(packed_path)), ('siegeEngines', sorted(siege_engines)))),
    )
    out = os.path.join(REPO, 'worldgen/policies/naval-content.json')
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(policy, f, indent=1)
        f.write('\n')
    print('wrote', out, 'shipyard levels', dict(levels), 'blockades', len(blockades), 'arcs', len(frames), 'dropoffs', len(dropoffs), 'spawns', len(spawns),
          'per port town', policy['pirates']['perPortTown'], 'weights', {k: dict(v) for k, v in weights.items()})


if __name__ == '__main__':
    main()
