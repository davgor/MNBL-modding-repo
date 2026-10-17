"""MMR-42: write worldgen/policies/mission-scenes.json, the versioned catalogue of installed mission scenes generated worlds use.

Read from the installed game (hash-pinned):
  * battle scenes: every module's ModuleData/sp_battle_scenes.xml in the order GameSceneDataManager loads them (SandBox, NavalDLC), with
    the table hash GeneratedBattleTerrain computes in game (MMR-60: 3aa0154c...), so the catalogue provably is the table the scene model reads;
  * conversation scenes (conversation_scenes.xml), the scenes SandBoxMissions.OpenConversationMission takes by terrain;
  * every scene a settlement prototype's locations name (worldgen/templates/settlements/prototypes.json ids, per location and wall level,
    from the merged campaign catalog's Settlements document the prototypes come from);
  * each scene's scene.xscene, inventoried: module, SHA-256, the scene levels (<scene><levels>), the contract tags and scripts below, the
    spawn paths (spawn_path_*), terrain layers and how many have an enabled winter variant. Nothing is inferred from a scene's name.
The mission contracts (required levels, tags, scripts and paths) cite the installed code that reads them (IL, 1.4.8 / War Sails 1.2.8).
Usage: python tools/make_mission_scenes.py [--game GAME_ROOT]
"""
import argparse
import collections
import hashlib
import json
import os
import re
import xml.etree.ElementTree as ET

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CATALOG = 'evidence/runtime/in-game/campaign_catalog-10212.json'
PROTOTYPES = 'worldgen/templates/settlements/prototypes.json'
OUT = 'worldgen/policies/mission-scenes.json'
BATTLE_MODULES = ['SandBox', 'NavalDLC']            # Campaign.InitializeScenes: each active module's sp_battle_scenes.xml (MMR-60 IL)
CONVERSATION_MODULES = ['SandBox', 'NavalDLC']
ALLOWED_MODULES = ['Native', 'SandBoxCore', 'SandBox', 'NavalDLC']
SCENE_MODULES = ['Native', 'SandBoxCore', 'SandBox', 'NavalDLC', 'StoryMode', 'CustomBattle', 'Multiplayer']
# TaleWorlds.Core.TerrainType (installed enum values).
TERRAIN = collections.OrderedDict([('Plain', 1), ('Desert', 2), ('Snow', 3), ('Forest', 4), ('Steppe', 5), ('Fording', 6), ('Mountain', 7), ('Lake', 8),
                                   ('Water', 10), ('River', 11), ('Canyon', 13), ('RuralArea', 14), ('Swamp', 15), ('Dune', 16), ('Bridge', 17),
                                   ('CoastalSea', 18), ('OpenSea', 19), ('Beach', 20), ('Cliff', 21), ('NonNavigableRiver', 22), ('LandRestriction', 23),
                                   ('SeaRestriction', 24), ('UnderBridge', 25)])
CONTRACT_TAGS = ['spawnpoint_player', 'player_spawn_frame', 'sp_naval_raid_player_spawn', 'unsafe_archer_point', 'sp_guard', 'npc_common',
                 'strategycameraattacker', 'strategycameradefender', 'attacker_wait_pos']
TAG_PREFIXES = ['attacker_', 'defender_', 'landing_', 'jumping_']
CONTRACT_SCRIPTS = ['CommonAreaMarker', 'StealthAreaMarker', 'HideoutSpawnPointGroup', 'TacticalPosition', 'TacticalRegion', 'PatrolArea',
                    # siege readiness, inventoried (not required: prototypes are reused unmodified)
                    'CastleGate', 'DeploymentPoint', 'SiegeLadderSpawner', 'SiegeTowerSpawner', 'BatteringRamSpawner', 'WallSegment', 'StrategicArea']
# The installed navigation model's valid terrain (evidence/baseline/scene-contract.json navigation, MMR-13; the game compares its live model):
# where a land party and where a ship may stand.
LAND_TERRAINS = ['Plain', 'Desert', 'Snow', 'Forest', 'Steppe', 'Fording', 'Swamp', 'Dune', 'Bridge', 'Beach']
NAVAL_TERRAINS = ['Lake', 'Water', 'River', 'CoastalSea', 'OpenSea', 'LandRestriction', 'SeaRestriction', 'UnderBridge']
# DefaultSceneModel._conversationTerrains (IL, .cctor) and GetTerrainByCount: a Snow first sample counts as Plain; only these terrains are counted.
CONVERSATION_TERRAINS = ['Plain', 'Desert', 'Swamp', 'Steppe', 'OpenSea', 'CoastalSea', 'Lake', 'River', 'Water']

CONTRACTS = collections.OrderedDict([
    ('fieldBattle', collections.OrderedDict([
        ('scenes', 'sp_battle_scenes with is_naval false'), ('paths', 'spawn_path_'), ('minimumPaths', 1),
        ('evidence', 'MenuHelper.EncounterAttackConsequence -> DefaultSceneModel.GetBattleSceneForMapPatch(patch, false); the patch places the armies on '
                     'the scene\'s spawn paths (TaleWorlds.MountAndBlade.MBSceneUtilities.GetAllSpawnPaths: paths named spawn_path_*)')])),
    ('navalBattle', collections.OrderedDict([
        ('scenes', 'sp_battle_scenes with is_naval true'), ('paths', 'spawn_path_'), ('minimumPaths', 1),
        ('evidence', 'DefaultSceneModel.GetBattleSceneForMapPatch(patch, true): several naval scenes are filtered by Terrain == '
                     'SandBox.MapScene.GetTerrainTypeAtPosition(MainParty.Position) (the GetEnvironmentTerrainTypesCount out value), then random; '
                     'ships spawn on the spawn paths')])),
    ('settlementCenter', collections.OrderedDict([
        ('location', 'center'), ('levels', ['level_1', 'level_2', 'level_3']),
        ('evidence', 'town and castle visits open center.GetSceneName(wall level) with DefaultLocationModel.GetUpgradeLevelTag(wall level) = level_N')])),
    ('siege', collections.OrderedDict([
        ('location', 'center'), ('levels', ['level_1', 'level_2', 'level_3', 'siege']),
        ('evidence', 'SandBox.SandBoxMissions.OpenSiegeMissionWithDeployment / OpenSiegeMissionNoDeployment: SceneLevels = '
                     'LocationModel.GetUpgradeLevelTag(wall level) + " siege" on the center scene of that wall level')])),
    ('siegeLordsHall', collections.OrderedDict([
        ('location', 'lordshall'), ('levels', ['siege']),
        ('evidence', 'SandBox.SandBoxMissions.OpenSiegeLordsHallFightMission: SceneLevels "siege" on the lord\'s hall scene')])),
    ('villageCenter', collections.OrderedDict([
        ('location', 'village_center'), ('levels', []), ('optionalLevels', ['land_raid']),
        ('evidence', 'village visits open the village_center location scene; a land raid battle (PlayerEncounter.StartVillageBattleMission) opens '
                     'GetScene("village_center", 1) with level "land_raid" when the scene has it (most installed village scenes do not; inventoried, not required)')])),
    ('navalRaid', collections.OrderedDict([
        ('location', 'village_center'), ('levels', ['naval_raid']), ('tagPrefixes', ['landing_', 'jumping_']), ('inLevel', 'naval_raid'),
        ('evidence', 'NavalDLC.Missions.NavalMissions.OpenNavalRaidMission: the village\'s LocationComplex.GetScene("village_center") with SceneLevels '
                     '"naval_raid"; NavalRaidMissionController.OnBehaviorInitialize collects the ships\' landing points (tags landing(_\\d+)*) and '
                     'the troops\' jumping points (jumping(_\\d+)*); "player_spawn_frame" (the defending player) and the "attacker_"/"defender_" + '
                     'formation spawn entities are used when present (NavalRaidMissionDeploymentPlanningLogic falls back otherwise)')])),
    ('hideout', collections.OrderedDict([
        ('location', 'hideout_center'), ('levels', ['level_1', 'level_2']), ('tags', ['spawnpoint_player']), ('scripts', ['CommonAreaMarker']),
        ('evidence', 'SandBox.SandBoxMissions.OpenHideoutBattleMission: SceneLevels level_1/level_2; HideoutMissionController.InitializeMission and '
                     'SpawnTroops take the CommonAreaMarker areas; SandBoxHelpers.SpawnPlayer uses "spawnpoint_player"')])),
    ('port', collections.OrderedDict([
        ('location', 'port'), ('levels', []),
        ('evidence', 'a port town\'s port location (War Sails shipyard) opens its port scene; only its installation is checked (no level or entity '
                     'requirement was traced)')])),
    ('conversation', collections.OrderedDict([
        ('scenes', 'conversation_scenes.xml'),
        ('evidence', 'SandBox.SandBoxMissions.OpenConversationMission without a special scene -> DefaultSceneModel.GetConversationSceneForMapPosition('
                     'MainParty.Position): the most frequent terrain of GetEnvironmentTerrainTypesCount (57 samples), then a random conversation scene '
                     'of that terrain, else a random one')])),
])

# How generated geography picks battle scene terrain (MMR-42 substitution rules; docs/mission-scenes.md). A biome or a face terrain names a
# scene class: an installed land scene terrain, an optional preference among its scenes (forest density in priority order: the first value any
# scene has; or a TerrainTypes child: the installed scenes with Mountain or Canyon ground are Plain ones, so rock prefers those), and the
# terrains to fall back to, in order, when a coast or river cell finds no index of the class's terrain. Every class names a terrain with unique
# land indices installed (Plain, Steppe, Desert, Swamp); no Forest, Snow or Mountain battle scene exists.
CLASSES = collections.OrderedDict([
    ('open', collections.OrderedDict([('terrain', 'Plain'), ('prefer', None), ('fallback', ['Steppe'])])),
    ('woodland', collections.OrderedDict([('terrain', 'Plain'), ('prefer', collections.OrderedDict([('forestDensity', ['High', 'Low'])])), ('fallback', [])])),
    ('steppe', collections.OrderedDict([('terrain', 'Steppe'), ('prefer', None), ('fallback', ['Plain'])])),
    ('rock', collections.OrderedDict([('terrain', 'Plain'), ('prefer', collections.OrderedDict([('terrainTypes', ['Mountain', 'Canyon'])])), ('fallback', ['Steppe'])])),
    ('cold', collections.OrderedDict([('terrain', 'Steppe'), ('prefer', None), ('fallback', ['Plain'])])),
    ('desert', collections.OrderedDict([('terrain', 'Desert'), ('prefer', None), ('fallback', ['Steppe', 'Plain'])])),
    ('wetland', collections.OrderedDict([('terrain', 'Swamp'), ('prefer', None), ('fallback', ['Plain'])])),
])
# Relief (the compiled masks' landform plane, worldgen/policies/terrain-materials.json landforms): within the class's scenes, prefer those whose
# ground (TerrainTypes) shows the relief; a class with no such scene keeps its own preference.
RELIEF = collections.OrderedDict([('Mountain / ridge', ['Mountain', 'Canyon']), ('Valley', ['River', 'Water'])])
BIOME_CLASS = collections.OrderedDict([('grassland', 'open'), ('forest', 'woodland'), ('boreal_forest', 'woodland'), ('rainforest', 'woodland'),
                                       ('tundra', 'cold'), ('cold_tundra', 'cold'), ('snow', 'cold'), ('land_ice', 'cold'), ('exposed_rock', 'rock'),
                                       ('desert', 'desert'), ('marsh', 'wetland'), ('ocean', 'open'), ('lake', 'open')])
FACE_CLASS = collections.OrderedDict([('Plain', 'open'), ('RuralArea', 'open'), ('Beach', 'open'), ('Fording', 'open'), ('Bridge', 'open'), ('Forest', 'woodland'),
                                      ('Steppe', 'steppe'), ('Snow', 'cold'), ('Mountain', 'rock'), ('Cliff', 'rock'), ('Canyon', 'rock'), ('Desert', 'desert'),
                                      ('Dune', 'desert'), ('Swamp', 'wetland')])


def sha_bytes(data):
    return hashlib.sha256(data).hexdigest()


def sha(path):
    with open(path, 'rb') as f:
        return sha_bytes(f.read())


def rel(game, path):
    return os.path.relpath(path, game).replace('\\', '/')


def scene_dirs(game):
    found = {}
    for module in SCENE_MODULES:
        root = os.path.join(game, 'Modules', module, 'SceneObj')
        if not os.path.isdir(root):
            continue
        for name in sorted(os.listdir(root)):
            xs = os.path.join(root, name, 'scene.xscene')
            if os.path.isfile(xs) and name not in found:
                found[name] = (module, xs)
    return found


def inventory(module, path):
    data = open(path, 'rb').read()
    root = ET.fromstring(data)
    levels = collections.OrderedDict()
    block = root.find('levels')
    for l in (block if block is not None else []):
        if l.get('mask') is not None:
            levels[l.get('name')] = int(l.get('mask'))
    tags, scripts = collections.Counter(), collections.Counter()
    by_level = collections.defaultdict(set)   # contract tag / prefix / script -> the scene levels it is visible in ('*': every level)
    depth = [0]
    entities = [0]

    def walk(entity, inherited, d):
        entities[0] += 1
        depth[0] = max(depth[0], d)
        own = entity.find('levels')
        mine = [l.get('name') for l in own] if own is not None and len(own) else inherited
        names = [t.get('name') for t in entity.findall('tags/tag')]
        found = [n for n in names if n in CONTRACT_TAGS] + [p for p in TAG_PREFIXES for n in names if n.startswith(p)]
        found += [sc.get('name') for sc in entity.findall('scripts/script') if sc.get('name') in CONTRACT_SCRIPTS]
        for item in found:
            by_level[item].update(mine if mine else ['*'])
        tags.update(names)
        scripts.update(sc.get('name') for sc in entity.findall('scripts/script'))
        children = entity.find('children')
        for child in (children.findall('game_entity') if children is not None else []):
            walk(child, mine, d + 1)

    top = root.find('entities')
    roots = top.findall('game_entity') if top is not None else root.findall('game_entity')
    for e in roots:
        walk(e, None, 1)
    paths = [p.get('name') for p in root.iter('path') if p.get('name')]
    layers = list(root.iter('layer'))
    row = collections.OrderedDict()
    row['module'] = module
    row['sha256'] = sha_bytes(data)
    row['bytes'] = len(data)
    row['levels'] = levels
    row['tags'] = collections.OrderedDict((t, tags[t]) for t in CONTRACT_TAGS if tags[t])
    row['tagPrefixes'] = collections.OrderedDict((p, sum(v for k, v in tags.items() if k.startswith(p))) for p in TAG_PREFIXES if any(k.startswith(p) for k in tags))
    row['scripts'] = collections.OrderedDict((sc, scripts[sc]) for sc in CONTRACT_SCRIPTS if scripts[sc])
    row['byLevel'] = collections.OrderedDict((k, sorted(v)) for k, v in sorted(by_level.items()))
    row['spawnPaths'] = sum(1 for p in paths if p.startswith('spawn_path_'))
    row['paths'] = len(paths)
    row['spawnPoints'] = sum(v for k, v in tags.items() if k.startswith('sp_') or k.startswith('spawnpoint'))
    row['entities'] = entities[0]
    row['rootEntities'] = len(roots)
    row['maxDepth'] = depth[0]
    row['distinctTags'] = len(tags)
    row['layers'] = len(layers)
    row['winterLayers'] = sum(1 for l in layers for w in l.findall('winter') if w.get('is_enabled') == 'true')
    return row


def supports(row):
    """The contracts a scene meets on its own (levels, tags, prefixes in their level, scripts, spawn paths); location-specific checks are the
    module's (MissionScenes.LocationProblems)."""
    result = []
    for name, c in CONTRACTS.items():
        if name == 'conversation':
            continue
        ok = all(l in row['levels'] for l in c.get('levels', []))
        ok = ok and all(t in row['tags'] for t in c.get('tags', []))
        level = c.get('inLevel')
        ok = ok and all(p in row['tagPrefixes'] and (level is None or '*' in row['byLevel'].get(p, []) or level in row['byLevel'].get(p, [])) for p in c.get('tagPrefixes', []))
        ok = ok and all(sc in row['scripts'] for sc in c.get('scripts', []))
        ok = ok and row['spawnPaths'] >= c.get('minimumPaths', 0)
        if ok:
            result.append(name)
    return result


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--game', default=r'D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord')
    a = ap.parse_args()
    game = a.game
    dirs = scene_dirs(game)
    sources = []
    # Battle scenes, in the scene model's load order.
    battle = []
    for module in BATTLE_MODULES:
        path = os.path.join(game, 'Modules', module, 'ModuleData', 'sp_battle_scenes.xml')
        sources.append(collections.OrderedDict([('path', rel(game, path)), ('sha256', sha(path))]))
        for s in ET.parse(path).getroot():
            indices = [int(x) for x in (s.get('map_indices') or '').replace(' ', '').split(',') if x]
            battle.append(collections.OrderedDict([
                ('id', s.get('id')), ('module', module), ('naval', (s.get('is_naval') or 'false').lower() == 'true'), ('terrain', s.get('terrain')),
                ('terrainTypes', [t.get('name') for t in s.iter('TerrainType')]), ('forestDensity', s.get('forest_density')), ('mapIndices', indices)]))
    unknown = sorted({b['terrain'] for b in battle} - set(TERRAIN))
    if unknown:
        raise SystemExit('battle scene terrain not in TerrainType: %s' % unknown)
    # GeneratedBattleTerrain.SceneTableSha: SceneID (ordinal), IsNaval, Terrain (enum value), sorted MapIndices (duplicates kept).
    table = ''.join('%s|%s|%s|%s\n' % (b['id'], 'True' if b['naval'] else 'False', b['terrain'], ','.join(str(i) for i in sorted(b['mapIndices'])))
                    for b in sorted(battle, key=lambda b: (b['id'].encode('utf-8'), b['naval'], TERRAIN[b['terrain']])))
    table_sha = sha_bytes(table.encode('utf-8'))
    conversations = []
    for module in CONVERSATION_MODULES:
        path = os.path.join(game, 'Modules', module, 'ModuleData', 'conversation_scenes.xml')
        sources.append(collections.OrderedDict([('path', rel(game, path)), ('sha256', sha(path))]))
        for s in ET.parse(path).getroot():
            conversations.append(collections.OrderedDict([('id', s.get('id')), ('module', module), ('terrain', s.get('terrain')), ('forestDensity', s.get('forest_density'))]))
    # Settlement prototypes: every location's scene per wall level, from the catalog the prototypes were made from.
    receipt = json.load(open(os.path.join(REPO, CATALOG), encoding='utf-8-sig'))
    doc = [d for d in receipt['outputs']['documents'] if d['id'] == 'Settlements'][0]
    if sha(doc['path']) != doc['sha256']:
        raise SystemExit('catalog Settlements differs from its receipt')
    protos = json.load(open(os.path.join(REPO, PROTOTYPES), encoding='utf-8'))
    if protos['provenance']['settlementsSha256'] != doc['sha256']:
        raise SystemExit('prototypes.json was made from another Settlements document')
    kind_of = {}
    for culture, kinds in protos['cultures'].items():
        for kind, items in kinds.items():
            for p in items:
                kind_of[p['id']] = (culture, kind)
    root = ET.parse(doc['path']).getroot()
    prototypes = collections.OrderedDict()
    for s in root:
        if s.get('id') not in kind_of:
            continue
        culture, kind = kind_of[s.get('id')]
        locations = collections.OrderedDict()
        for loc in (s.find('Locations') if s.find('Locations') is not None else []):
            locations[loc.get('id')] = [loc.get('scene_name') or None] + [loc.get('scene_name_%d' % i) or None for i in (1, 2, 3)]
        prototypes[s.get('id')] = collections.OrderedDict([('kind', kind), ('culture', culture), ('locations', locations)])
    missing_protos = sorted(set(kind_of) - set(prototypes))
    if missing_protos:
        raise SystemExit('prototypes missing from the catalog: %s' % missing_protos[:10])
    prototypes = collections.OrderedDict(sorted(prototypes.items()))
    # Scene inventory: battle, conversation and every location scene.
    wanted = sorted({b['id'] for b in battle} | {c['id'] for c in conversations} |
                    {v for p in prototypes.values() for levels in p['locations'].values() for v in levels if v})
    scenes = collections.OrderedDict()
    absent = []
    for name in wanted:
        if name not in dirs:
            absent.append(name)
            continue
        module, path = dirs[name]
        scenes[name] = inventory(module, path)
    # Where each scene is used (its locations in the prototypes, or the battle table), and the contracts it meets there.
    location_contracts = {'center': ['settlementCenter', 'siege'], 'lordshall': ['siegeLordsHall'], 'village_center': ['villageCenter', 'navalRaid'],
                          'hideout_center': ['hideout'], 'port': ['port']}
    used_at = collections.defaultdict(set)
    for proto in prototypes.values():
        for loc, names in proto['locations'].items():
            for n in names:
                if n:
                    used_at[n].add(loc)
    for b in battle:
        used_at[b['id']].add('navalBattle' if b['naval'] else 'fieldBattle')
    for c in conversations:
        used_at[c['id']].add('conversation')
    for name, inv in scenes.items():
        inv['usedAt'] = sorted(used_at[name])
        relevant = set(k for u in used_at[name] for k in location_contracts.get(u, [u] if u in CONTRACTS else []))
        inv['supports'] = [k for k in supports(inv) if k in relevant]
    for b in battle:
        inv = scenes.get(b['id'])
        b['spawnPaths'] = inv['spawnPaths'] if inv else None
        b['capacity'] = collections.OrderedDict([('spawnPaths', inv['spawnPaths'] if inv else None), ('entities', inv['entities'] if inv else None)])
        # The scene's place in the selection: the land classes whose terrain it is, or the sea terrain it serves.
        b['classes'] = [n for n, c in CLASSES.items() if not b['naval'] and (c['terrain'] == b['terrain'] or b['terrain'] in c['fallback'])]
    # The inventory hash also covers what the class preferences read (forest density, TerrainTypes); the game computes it from its loaded rows.
    inventory_table = ''.join('%s|%s|%s|%s|%s|%s\n' % (b['id'], 'True' if b['naval'] else 'False', b['terrain'], b['forestDensity'] or '', ','.join(sorted(b['terrainTypes'])),
                                                     ','.join(str(i) for i in sorted(b['mapIndices'])))
                              for b in sorted(battle, key=lambda b: (b['id'].encode('utf-8'), b['naval'], TERRAIN[b['terrain']])))
    inventory_sha = sha_bytes(inventory_table.encode('utf-8'))
    policy = collections.OrderedDict()
    policy['schema'] = 'mnbl.mission-scenes'
    policy['version'] = 1
    policy['generator'] = 'tools/make_mission_scenes.py'
    policy['rule'] = ('generated worlds reuse installed mission scenes (product contract); a scene is usable when it is installed in an allowed module '
                      'and meets its mission contract; nothing is inferred from scene names')
    policy['allowedModules'] = ALLOWED_MODULES
    policy['sources'] = sources + [collections.OrderedDict([('path', CATALOG + ' Settlements'), ('sha256', doc['sha256'])]),
                                   collections.OrderedDict([('path', PROTOTYPES), ('sha256', sha(os.path.join(REPO, PROTOTYPES)))])]
    policy['battleTable'] = collections.OrderedDict([('sha256', table_sha), ('inventorySha256', inventory_sha), ('scenes', len(battle)),
                                                     ('rule', 'GeneratedBattleTerrain.SceneTableSha over GameSceneDataManager.SingleplayerBattleScenes; the game '
                                                              'refuses to use a catalogue whose table hash differs from the installed one')])
    policy['terrainTypes'] = TERRAIN
    policy['contracts'] = CONTRACTS
    policy['classes'] = CLASSES
    policy['biomeClass'] = BIOME_CLASS
    policy['faceClass'] = FACE_CLASS
    policy['relief'] = RELIEF
    policy['landTerrains'] = LAND_TERRAINS
    policy['navalTerrains'] = NAVAL_TERRAINS
    policy['conversationTerrains'] = CONVERSATION_TERRAINS
    policy['blockSize'] = 16
    policy['battleScenes'] = battle
    policy['conversationScenes'] = conversations
    policy['scenes'] = scenes
    policy['absentScenes'] = absent
    policy['prototypes'] = prototypes
    out = os.path.join(REPO, OUT)
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(policy, f, indent=1, ensure_ascii=False)
        f.write('\n')
    print('wrote', OUT, 'battle', len(battle), 'table', table_sha[:12], 'conversation', len(conversations), 'scenes', len(scenes), 'absent', len(absent),
          'prototypes', len(prototypes), 'bytes', os.path.getsize(out))


if __name__ == '__main__':
    main()
