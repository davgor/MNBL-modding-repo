"""MMR-247: tools/summarize_map2_acceptance.py on synthetic runs, without the game: an empty run grades every step and criterion without a
crash (nothing passes that did not run), a consistent day-0 census of a small synthetic world passes the town, village and population rules,
and each rule fails on the defect it exists for (a town short of a village, a lost FWGC++ city, a house in the wrong kingdom); the
screenshot whiteness measure the winter check reads tells snow from grass.
"""
import json
import os
import struct
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / 'tools'))
import summarize_map2_acceptance as S  # noqa: E402

ALL = {S.PASS, S.FAIL, S.BLOCKED, S.NOT_RUN}
FIXTURES = Path(__file__).resolve().parent / 'fixtures'
# acc4's run folder (the integration worktree's, git-ignored): the tests that re-read it are skipped where it is absent.
ACC4 = Path(os.environ.get('MNBL_ACC4_RUN', r'C:\Users\davgo\m2\int\build\acceptance\acc4'))
ACC6 = Path(os.environ.get('MNBL_ACC6_RUN', r'C:\Users\davgo\m2\int\build\acceptance\acc6'))
FACE = {'Plain': 1, 'Desert': 2, 'Snow': 3, 'Forest': 4, 'Steppe': 5, 'Swamp': 15}


def travel(root, rows, spacing=10.0, settlements=(), column_offset=0, row_offset=0):
    """A kept bundle's travel cells: rows of strings, one character per cell ('.' sea, 'm' mountain, 'r' river, else a land type's first
    letter: P Plain, D Desert, S Snow, F Forest, T Steppe, W Swamp), written as cells.bin MNBLTC02 with its travel-graph.json grid."""
    letters = {'P': 'Plain', 'D': 'Desert', 'S': 'Snow', 'F': 'Forest', 'T': 'Steppe', 'W': 'Swamp'}
    cols, n = len(rows[0]), len(rows) * len(rows[0])
    classes, types = bytearray(n), bytearray(n)
    for r, line in enumerate(rows):
        for c, ch in enumerate(line):
            i = r * cols + c
            classes[i] = {'.': 2, 'm': 1, 'r': 4}.get(ch, 0)
            types[i] = FACE[letters[ch]] if ch in letters else 0
    (root / 'compiled/travel').mkdir(parents=True, exist_ok=True)
    (root / 'compiled/travel/cells.bin').write_bytes(b'MNBLTC02' + struct.pack('<II', cols, len(rows)) + bytes(classes) + bytes(n) + bytes(types) + bytes(n))
    write(root / 'compiled/travel/travel-graph.json', {'grid': {'cols': cols, 'rows': len(rows), 'spacing': spacing, 'columnOffset': column_offset, 'rowOffset': row_offset},
                                                       'settlements': list(settlements)})


def write(path, doc):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(doc), encoding='utf-8')


def bundle(root):
    """A world of 2 towns (3 villages each), 1 castle (2 villages), one kingdom of one house, one company, W = 1."""
    towns = [{'id': 't1', 'uid': 'city-1', 'name': 'Alda', 'presentation': 'empire', 'kingdom': 'k1', 'port': True, 'villages': ['v1', 'v2', 'v3']},
             {'id': 't2', 'uid': 'city-2', 'name': 'Borna', 'presentation': 'empire', 'kingdom': 'k1', 'port': False, 'villages': ['v4', 'v5', 'v6']}]
    castles = [{'id': 'c1', 'uid': 'fort-1', 'name': 'Cast', 'kingdom': 'k1', 'villages': ['v7', 'v8']}]
    heroes = [{'id': 'h1', 'name': 'Ana', 'female': True, 'alive': True, 'bucket': 'house', 'role': 'sovereign'},
              {'id': 'h2', 'name': 'Bo', 'female': False, 'alive': True, 'bucket': 'company', 'role': 'warlord'},
              {'id': 'h3', 'name': 'Cid', 'female': False, 'alive': True, 'bucket': 'wanderer', 'role': 'champion'},
              {'id': 'h4', 'name': 'Dara', 'female': True, 'alive': True, 'bucket': 'house', 'role': 'regent'}]
    campaign = {'towns': towns, 'castles': castles, 'kingdoms': [{'id': 'k1'}], 'wars': [],
                'houses': [{'id': 'l1', 'kingdom': 'k1', 'fiefs': ['t1', 't2', 'c1'], 'members': ['h1', 'h4']}],
                'mercenaryCompanies': [{'id': 'm1', 'members': ['h2']}], 'outlawClans': [], 'heroes': heroes,
                'wanderers': [{'hero': 'h3', 'female': False}], 'census': {'wanderers': 1, 'engineChildren': 2}, 'villageQuota': {'exceptions': []}}
    write(root / 'compiled/campaign/campaign.json', campaign)
    write(root / 'compiled/campaign/id-map.json', {'entries': [{'id': 't1', 'kind': 'town', 'uid': 'city-1'}, {'id': 't2', 'kind': 'town', 'uid': 'city-2'}]})
    write(root / 'source/world.json', {'settlements': {'sites': [{'uid': 'city-1'}, {'uid': 'city-2'}]}})
    write(root / 'policies/terrain-materials.json', {'biomes': [{'id': 0, 'core': 'ocean', 'water': True, 'terrainType': None, 'battleClass': 'open'},
                                                                {'id': 1, 'core': 'grassland', 'water': False, 'terrainType': 'Plain', 'battleClass': 'open'}]})
    write(root / 'compiled/terrain/projection.json', {'periodicLayout': {'physicalWidth': 100.0}})
    side = 3
    (root / 'compiled/terrain').mkdir(parents=True, exist_ok=True)
    (root / 'compiled/terrain/masks.bin').write_bytes(b'MNBLMK01' + struct.pack('<I', side) + bytes([1] * side * side) + bytes(3 * side * side))
    return campaign


def census(campaign):
    """The probe's day-0 census of the synthetic world, as consistent as the compiler planned it."""
    pos = {'t1': [10, 10], 't2': [50, 10], 'c1': [30, 40]}
    vpos = {'v1': [14, 16], 'v2': [5, 16], 'v3': [10, 4], 'v4': [56, 10], 'v5': [44, 16], 'v6': [50, 3], 'v7': [36, 40], 'v8': [24, 46]}
    fief = lambda f, kind: {'id': f['id'], 'name': f['name'], 'culture': 'empire', 'kingdom': 'k1', 'owner': 'l1', 'port': f.get('port', False), 'villages': list(f['villages']),
                            'position': pos[f['id']], 'gate': pos[f['id']], 'wall': 1}
    types = ['wheat_farm', 'europe_horse_ranch', 'cattle_farm', 'wheat_farm', 'olive_trees', 'fisherman', 'wheat_farm', 'vineyard']
    villages = [{'id': v, 'name': 'Vale ' + 'abcdefgh'[i], 'culture': 'empire', 'bound': None, 'type': types[i], 'position': vpos[v], 'port': False} for i, v in enumerate(sorted(vpos))]
    return {'towns': [fief(t, 'town') for t in campaign['towns']], 'castles': [fief(c, 'castle') for c in campaign['castles']], 'villages': villages, 'hideouts': [],
            'kingdoms': [{'id': 'k1', 'name': 'Realm of Alda', 'warsWith': [], 'clans': ['l1', 'm1']}],
            'clans': [{'id': 'l1', 'name': 'Clan Ana', 'kingdom': 'k1', 'children': 2, 'alive': 4, 'minor': False},
                      {'id': 'm1', 'name': 'Bo Company', 'kingdom': None, 'minor': True, 'mercenary': True, 'alive': 5}],
            'heroes': [{'id': h['id'], 'name': h['name'], 'female': h['female'], 'alive': True, 'minorFactionHero': h['bucket'] == 'company'} for h in campaign['heroes']],
            'wanderers': [{'id': 'h3', 'name': 'Cid the Bold', 'female': False}],
            'traits': {'lords': 2, 'Honor': {'0': 1, '1': 1}, 'Valor': {'0': 1, '1': 1}, 'Mercy': {'0': 1, '-1': 1}, 'Generosity': {'0': 1, '1': 1}, 'Calculating': {'0': 1, '1': 1}, 'distinctVectors': 6},
            'population': {'wanderers': 1, 'wanderersTarget': 1, 'problems': []}}


def run_folder(root, census_doc=None):
    folder = root / 'run'
    (folder / 'in-game').mkdir(parents=True, exist_ok=True)
    state = {'runId': 'acctest', 'phases': {}, 'launches': [], 'hashes': []}
    if census_doc is not None:
        steps = {'w1.identity': {'status': 'done', 'world': {'frame': [0, 0, 100, 60]}}, 'w1.census0': {'status': 'done', 'census': census_doc, 'backstories': []}}
        write(folder / 'in-game/map2_acceptance-1.json', {'processId': 1, 'status': 'ok', 'outputs': {'steps': steps, 'shots': [], 'completed': True}})
        state['launches'] = [{'phase': 'world1', 'pid': 1, 'result': 'in-game/map2_acceptance-1.json'}]
    write(folder / 'state.json', state)
    return folder


class Map2AcceptanceSummarizer(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def grade(self, folder, *extra, committed=False):
        """Grades the run folder; without `committed`, with no attestations (an --attest file that does not exist), so a test sees only what
        the run itself shows; `extra` are more arguments."""
        out = self.root / 'acceptance.json'
        sys.argv = ['summarize', '--run', str(folder), '--out', str(out)] + ([] if committed else ['--attest', str(self.root / 'no-attestations.json')]) + list(extra)
        self.assertEqual(S.main(), 0)
        return json.loads(out.read_text(encoding='utf-8'))

    def test_an_empty_run_grades_everything_and_passes_nothing_that_did_not_run(self):
        doc = self.grade(run_folder(self.root))
        ids = [r['id'] for r in doc['criteria']]
        self.assertEqual(ids, ['c%d' % i for i in range(1, 34)])
        self.assertEqual(doc['hangs'], [])
        self.assertTrue({'1', '2.generate', '2.cast', '3', '4', '5', '6', '7'} <= {r['id'] for r in doc['steps']})
        for r in doc['steps'] + doc['criteria']:
            with self.subTest(item=r['id']):
                self.assertIn(r['status'], ALL)
                if r['id'] not in ('c23',):   # c23 reads the tracked player docs, which exist without a run
                    self.assertNotEqual(r['status'], S.PASS)
        self.assertEqual(next(r for r in doc['steps'] if r['id'] == '2.generate')['status'], S.NOT_RUN)

    def test_a_full_synthetic_run_grades_every_rule(self):
        """Every phase's steps in the probe's shapes: every rule runs (no rule crashes on real shapes), and the ones fed consistent data pass."""
        campaign = bundle(self.root / 'probe')
        folder = self.root / 'run'
        (folder / 'in-game').mkdir(parents=True)
        bundle(folder / 'bundles/A'); bundle(folder / 'bundles/B')
        day0 = census(campaign)
        case = lambda: {'failures': [], 'mission': {'scene': 'battle_terrain_a'}}
        cache = {'status': 'generated', 'generateSeconds': 100.0}
        start = lambda seed: {'status': 'done', 'seed': seed, 'generateSeconds': 20, 'startSeconds': 300, 'preview': {'estimateSeconds': 110.0, 'settlements': 11, 'countsText': 'about 2 minutes'},
                              'cacheBuild': cache, 'world': {'frame': [0, 0, 100, 60], 'bundle': 'x'}}
        cont = {'status': 'done', 'mutationPersisted': True, 'generatorStarted': False, 'cacheAcquired': {'status': 'reused'}, 'loader': {'cacheForecast': {'reuse': True}, 'phases': []},
                'forecastWithoutCaches': {'reuse': False, 'kind': 'missing'}, 'seconds': 40}
        w1 = {'w1.start': start(42), 'w1.identity': {'status': 'done', 'world': {'frame': [0, 0, 100, 60]}, 'audit': {'patchSignatureFailures': [], 'flora': {'instances': 10}, 'magic': {'drawn': 5}}},
              'w1.census0': {'status': 'done', 'census': day0, 'backstories': [{'hero': 'h3', 'name': 'Cid', 'template': 'spc_wanderer_empire_0', 'lines': {'prebackstory': 'I grew up by the sea.'}}]},
              'w1.frames': {'status': 'done', 'near': {'meanMainMs': 6.5, 'frameCapSuspected': True}, 'far': {'meanMainMs': 7.5}},
              'w1.memoryDay': {'status': 'done', 'after': {'workingSetBytes': 2}},
              'w1.biomes': {'status': 'done', 'spots': {'grassland': {}}, 'faceSamples': {'grassland': [[1, 1, 'Plain'], [2, 2, 'Plain']]}},
              'w1.relief': {'status': 'done', 'padsOver25': 1, 'poles': {'north': {'held': True}, 'south': {'held': True}}},
              'w1.water': {'status': 'done', 'visibility': {'passed': True, 'failures': [], 'cells': {'dryCount': 0, 'impassable': 9}}, 'decalsOnWater': {}},
              'w1.dressing': {'status': 'done', 'drawn': {'roads': 3, 'bridges': 0, 'ruins': 1}, 'planned': {'roads': 3, 'bridges': 0, 'ruins': 1}, 'errors': [],
                              'nearestRoad': [['t1', 'town', 1.0], ['v1', 'village', 2.0]]},
              'w1.magic': {'status': 'done', 'drawn': 5, 'snapshot': {'retypedCells': 0}, 'installer': {'installed': {'missingMaterials': []}}},
              'w1.looks': {'status': 'done', 'wallLevels': {'town:1': 1, 'town:2': 1}},
              'w1.speed': {'status': 'done', 'Plain': {'lines': [['Base', 4.0]], 'final': 4.0, 'roadCell': False}, 'Snow': {'lines': [['Base', 4.0], ['Terrain', -0.15]]}, 'Swamp': {'absent': True},
                           'Forest': {'lines': [['Base', 4.0], ['Forest', -0.3]]}, 'Desert': {'lines': [['Base', 4.0], ['Desert', -0.1]]},
                           'Road': {'lines': [['Base', 4.0], ['Road', 0.2]], 'roadCell': True, 'face': 'Plain'}, 'cellTypes': {'Plain': 9, 'Snow': 2, 'Forest': 3, 'Desert': 1}},
              'w1.seam': {'status': 'done', 'arrived': True, 'chosen': {'west': 'forest', 'east': 'desert'}, 'seconds': 9},
              'w1.incidents': {'status': 'done', 'culture': {'incident_vedmaks_treasure': {'candidates': 0, 'literals': ['sturgia']},
                                                            'incident_kannic_splendors': {'candidates': 2, 'trueAt': 'v1', 'tries': []}},
                               'terrain': {'conditions': [{}] * 6, 'Desert': {'predicates': [['incident_x', True, True, True]]}, 'Forest': {'absent': True}, 'Steppe': {'predicates': []}}},
              'w1.conversation': {'status': 'done', 'conversationInProgress': True, 'mapConversationActive': True, 'afterIdle': None},
              'w1.battle.snow': {'status': 'done', 'case': case()}, 'w1.battle.forest': {'status': 'done', 'case': case()},
              'w1.battle.settlement': {'status': 'done', 'biome': 'grassland', 'case': case()},
              'w1.town': {'status': 'done', 'case': case(), 'caseFailures': []}, 'w1.castle': {'status': 'done', 'case': case(), 'caseFailures': []},
              'w1.village': {'status': 'done', 'case': case(), 'caseFailures': []}, 'w1.siege': {'status': 'done', 'case': case(), 'caseFailures': []},
              'w1.ruinsPage': {'status': 'done', 'pageFound': True, 'rows': 1, 'planRuins': 1},
              'w1.season': {'status': 'done', 'extremes': [{'extreme': 'summer'}, {'extreme': 'winter'}], 'days': [{'cold': 'Snowy', 'warm': 'Clear'}, {'cold': 'Clear', 'warm': 'Clear'}],
                            'winds': [[0.1, 1, 0], [0.5, 0, 1], [0.9, -1, 0]]},
              'w1.cast': {'status': 'done', 'processStarts': {'producer_apply': 1}, 'producer': json.loads((REPO / 'worldgen/producer.lock.json').read_text())['producer']['sha256'],
                          'after': {'retypedCells': 2, 'digest': 'd'}, 'retyped': [[1, 5, 5, 'Desert', 'Desert']]},
              'w1.battle.mutated': {'status': 'done', 'battleCell': [1, 5, 5, 'Desert', 'Desert'], 'case': case()},
              'w1.continue': cont,
              'w1.soak': {'status': 'done', 'advance': {'daysAdvanced': 30.2}, 'census': {'population': {'wanderers': 1, 'wanderersTarget': 1, 'wanderersRotated': 1},
                          'clans': [{'id': 'm1', 'mercenary': True, 'minor': True, 'alive': 5, 'underMercenaryService': True}]},
                          'eventCounts': {'warDeclared': 1}, 'events': [[3.0, 'warDeclared', ['k1', 'k2', 'Default']]], 'economyBefore': {}, 'economyAfter': {}, 'homeTerrain': {}}}
        w2 = {'w2.start': start(73), 'w2.identity': {'status': 'done', 'world': {'frame': [0, 0, 100, 60]}}, 'w2.census0': {'status': 'done', 'census': day0},
              'w2.water': {'status': 'done', 'visibility': {'passed': True, 'failures': []}},
              'w2.world1Before': {'status': 'done', 'sceneTree': 'h'}, 'w2.world1After': {'status': 'done', 'sceneInstalled': True, 'sceneTree': 'h', 'forecast': {'reuse': True}},
              'w2.continue1': {'status': 'done', 'cacheAcquired': {'status': 'reused'}, 'generatorStarted': False, 'seconds': 50}}
        base = {'base.frames': {'status': 'done', 'near': {'meanMainMs': 5.0}, 'far': {'meanMainMs': 6.0}}, 'base.memoryDay': {'status': 'done', 'after': {'workingSetBytes': 1}},
                'base.soak': {'status': 'done', 'economyBefore': {}, 'economyAfter': {}}}
        mods = {'mods.modules': {'status': 'done'}, 'mods.setup': {'status': 'done', 'outcome': 'generated'},
                'mods.load': {'status': 'done', 'outcome': 'loaded', 'cacheAcquired': {'status': 'reused'}, 'loader': {'phases': [{'phase': 'Loading'}]}}}
        launches = []
        for pid, (phase, steps) in enumerate([('baseline', base), ('world1', w1), ('world2', w2), ('mods', mods)], start=10):
            shots = [{'label': l, 'path': 'x'} for l in (['biome-grassland', 'mountain-0', 'coast-0', 'water-lake', 'road-at-town', 'ruin-marker', 'ruins-page', 'siege-map', 'look-empire-town',
                                                        'magic-ground', 'seam-west', 'seam-east', 'cast-before', 'cast-after'] if phase == 'world1' else ['water-lake'] if phase == 'world2' else [])]
            for s in shots:
                (folder / ('in-game/map2_acceptance-%d-shot-%s.bmp' % (pid, s['label']))).write_bytes(b'BM')
            write(folder / ('in-game/map2_acceptance-%d.json' % pid), {'processId': pid, 'status': 'ok', 'outputs': {'steps': steps, 'shots': shots, 'completed': True,
                  'build': {'moduleAssemblySha256': 'm', 'embeddedPolicies': {}}}})
            launches.append({'phase': phase, 'pid': pid, 'result': 'in-game/map2_acceptance-%d.json' % pid})
        hashes = [{'TaleWorlds.Native.dll': 'n', 'TaleWorlds.MountAndBlade.dll': 'e'}] * 3
        write(folder / 'state.json', {'runId': 'acctest', 'claim': {'text': 'no partner alive'}, 'lock': {'acquired': 'a', 'released': 'b'}, 'launches': launches, 'hashes': hashes,
                                      'phases': {'build': {'status': 'done', 'detail': {'module': 'm'}}, 'deploy': {'status': 'done', 'detail': {'moduleAssembly': 'm'}},
                                                 'preflight': {'status': 'done', 'detail': {'tests': {'Test-GameUpdates': {'exit': 0}, 'Test-WorldStore': {'exit': 0}}}},
                                                 'mods': {'status': 'done', 'detail': {'removed': 'removed'}},
                                                 'restore': {'status': 'done', 'detail': {'module': {'identical': True}, 'saves': {'preexistingChanged': []}, 'content': 'removed'}}}})
        doc = self.grade(folder)
        status = {r['id']: r for r in doc['steps'] + doc['criteria']}
        for item in ('1', '2.generate', '2.villages', '2.names', '2.continue', '3', '4', '5', 'c3', 'c4', 'c5', 'c13', 'c14', 'c16', 'c17', 'c18', 'c20', 'c22', 'c23', 'c24', 'c25'):
            with self.subTest(item=item):
                self.assertEqual(status[item]['status'], S.PASS, status[item]['detail'])
        self.assertEqual(status['c12']['status'], S.BLOCKED)   # the owner's review is attested, never inferred
        self.assertEqual(status['c27']['status'], S.FAIL)      # world 1's main thread 20 %+ slower than vanilla: a blocker until accepted
        self.assertEqual(status['6']['status'], S.NOT_RUN)

    def grader(self, campaign_census):
        folder = run_folder(self.root, campaign_census)
        bundle(folder / 'bundles/A')
        return S.Grader(S.Run(str(folder), {}))

    def test_a_consistent_census_passes_the_town_village_and_population_rules(self):
        campaign = bundle(self.root / 'probe')
        g = self.grader(census(campaign))
        self.assertEqual(g.towns_sites('world1', 'A').status, S.PASS, g.towns_sites('world1', 'A').detail)
        self.assertEqual(g.villages('world1', 'A').status, S.PASS, g.villages('world1', 'A').detail)
        match, children = g.census_match('world1', 'A')
        self.assertEqual(match.status, S.PASS, match.detail)
        self.assertEqual(children.status, S.PASS, children.detail)
        self.assertEqual(g.compiled_flags('world1', 'A').status, S.PASS, g.compiled_flags('world1', 'A').detail)
        self.assertEqual(g.names('world1').status, S.PASS, g.names('world1').detail)

    def test_each_rule_fails_on_its_defect(self):
        campaign = bundle(self.root / 'probe')
        short = census(campaign); short['towns'][0]['villages'].pop()
        self.assertEqual(self.grader(short).villages('world1', 'A').status, S.FAIL)
        lost = census(campaign); lost['towns'].pop()
        self.assertEqual(self.grader(lost).towns_sites('world1', 'A').status, S.FAIL)
        moved = census(campaign); moved['clans'][0]['kingdom'] = 'k9'
        self.assertEqual(self.grader(moved).census_match('world1', 'A')[0].status, S.FAIL)
        crowded = census(campaign); crowded['villages'][0]['position'] = [11, 11]
        self.assertEqual(self.grader(crowded).villages('world1', 'A').status, S.FAIL)
        aged = census(campaign); aged['kingdoms'][0]['name'] = 'Realm of Alda (Age 2)'
        self.assertEqual(self.grader(aged).names('world1').status, S.FAIL)
        regent = census(campaign); regent['heroes'][3]['name'] = 'Regent of Alda'
        self.assertEqual(self.grader(regent).names('world1').status, S.FAIL)
        unflagged = census(campaign); unflagged['heroes'][1]['minorFactionHero'] = False
        self.assertEqual(self.grader(unflagged).compiled_flags('world1', 'A').status, S.FAIL)

    def attempts(self, *attempts):
        """A world 1 run of one launch per attempt, each with the given steps."""
        folder = self.root / 'run'
        (folder / 'in-game').mkdir(parents=True, exist_ok=True)
        launches = []
        for pid, steps in enumerate(attempts, start=1):
            write(folder / ('in-game/map2_acceptance-%d.json' % pid), {'processId': pid, 'status': 'ok', 'outputs': {'steps': steps, 'shots': [], 'completed': True}})
            launches.append({'phase': 'world1', 'pid': pid, 'result': 'in-game/map2_acceptance-%d.json' % pid})
        write(folder / 'state.json', {'runId': 'acctest', 'phases': {}, 'launches': launches, 'hashes': []})
        return S.Grader(S.Run(str(folder), {}))

    def test_the_snow_route_needs_its_evidence(self):
        """Only winter reached, or both extremes without the cold and ice screenshots: the route is undecided, never namedTexture."""
        places = {'cold': [1, 1], 'warm': [2, 2], 'ice': [3, 3]}
        one = {'status': 'done', 'places': places, 'extremes': [{'extreme': 'winter'}], 'days': [], 'winds': []}
        season, route = self.attempts({'w1.season': one}).winter()
        self.assertEqual(season.status, S.FAIL)
        self.assertEqual(route.status, S.BLOCKED, route.detail)
        both = dict(one, extremes=[{'extreme': 'summer'}, {'extreme': 'winter'}])
        self.assertEqual(self.attempts({'w1.season': both}).winter()[1].status, S.BLOCKED)
        errored = {'status': 'error', 'error': 'boom'}
        self.assertEqual(self.attempts({'w1.season': errored}).winter()[1].status, S.BLOCKED)

    def test_snow_weather_reads_the_weather_event(self):
        """The daily record is the weather event (Snowy or Blizzard is snow), not the terrain effect (only Default and Wet in 1.4.8)."""
        days = [{'cold': 'Snowy', 'warm': 'Clear', 'ice': 'Blizzard', 'effect': {'cold': 'Wet', 'warm': 'Default', 'ice': 'Wet'}},
                {'cold': 'Clear', 'warm': 'LightRain', 'ice': 'Snowy', 'effect': {'cold': 'Default', 'warm': 'Wet', 'ice': 'Wet'}}]
        row = {'status': 'done', 'places': {'cold': [1, 1], 'warm': [2, 2], 'ice': [3, 3]}, 'extremes': [{'extreme': 'summer'}, {'extreme': 'winter'}], 'days': days,
               'winds': [[0.1, 1, 0], [0.5, 0, 1], [0.9, -1, 0]]}
        season = self.attempts({'w1.season': row}).winter()[0]
        self.assertEqual(season.status, S.BLOCKED)   # no screenshots: blocked, while the weather part passes (a passing part is not listed)
        self.assertNotIn('snow weather', ' '.join(season.detail))
        effects = dict(row, days=[{'cold': 'Wet', 'warm': 'Default', 'ice': 'Wet'}, {'cold': 'Default', 'warm': 'Wet', 'ice': 'Wet'}])
        season = self.attempts({'w1.season': effects}).winter()[0]
        self.assertEqual(season.status, S.FAIL)
        self.assertIn('snow weather', ' '.join(season.detail))

    def test_a_restart_keeps_the_measured_cache_build(self):
        measured = {'status': 'done', 'seed': 42, 'cacheBuild': {'status': 'generated', 'generateSeconds': 390.0}}
        reused = {'status': 'done', 'seed': 42, 'cacheBuild': {'status': 'reused', 'generateSeconds': None}}
        g = self.attempts({'w1.start': measured}, {'w1.start': reused})
        self.assertEqual(g.run.step('world1', 'w1.start')['cacheBuild']['generateSeconds'], 390.0)
        g = self.attempts({'w1.start': reused}, {'w1.start': measured})
        self.assertEqual(g.run.step('world1', 'w1.start')['cacheBuild']['generateSeconds'], 390.0)

    def test_whiteness_tells_snow_from_grass(self):
        try:
            from PIL import Image
        except ImportError:
            self.skipTest('Pillow is not installed')
        snow, grass, tonemapped = self.root / 'snow.bmp', self.root / 'grass.bmp', self.root / 'tonemapped.bmp'
        Image.new('RGB', (64, 48), (236, 240, 244)).save(snow)
        Image.new('RGB', (64, 48), (70, 120, 40)).save(grass)
        Image.new('RGB', (64, 48), (181, 175, 183)).save(tonemapped)   # acc4's median land ice pixel: the map's tonemapping keeps snow below 200
        self.assertGreater(S.image_stats(str(snow))['white'], 0.9)
        self.assertLess(S.image_stats(str(grass))['white'], 0.1)
        self.assertGreater(S.image_stats(str(tonemapped))['white'], 0.9)

    def test_whiteness_on_acc4s_land_ice_and_desert(self):
        """notrun-1: the recalibrated whiteness on acc4's daylight shots (the central half at 128 x 128, kept as lossless fixtures): correctly
        rendered land ice counts as white (0.738; the old >= 200 rule gave 0.076), desert sand does not (0.060)."""
        try:
            from PIL import Image
        except ImportError:
            self.skipTest('Pillow is not installed')
        stats = {}
        for biome in ('land_ice', 'desert'):
            img = Image.open(str(FIXTURES / ('acc4-biome-%s-128.png' % biome))).convert('RGB')
            data = img.tobytes()
            stats[biome] = S.pixel_stats([(data[i], data[i + 1], data[i + 2]) for i in range(0, len(data), 3)])
            original = ACC4 / 'in-game' / ('map2_acceptance-14208-shot-biome-%s.bmp' % biome)
            if original.exists():
                self.assertEqual(S.image_stats(str(original)), stats[biome])
        self.assertGreaterEqual(stats['land_ice']['white'], 0.5, stats)
        self.assertLess(stats['desert']['white'], 0.15, stats)
        self.assertAlmostEqual(stats['land_ice']['white'], 0.738, places=2)

    def test_the_biome_plane_reads_as_the_runtime_reads_it(self):
        b = S.Bundle(str(self.root / 'b'))
        bundle(self.root / 'b')
        b = S.Bundle(str(self.root / 'b'))
        self.assertEqual(b.biome_at(10, 10), 'grassland')
        self.assertEqual(b.biome_at(-50, 500), 'grassland')   # clamped to the plane


class Map2AcceptanceGraders(unittest.TestCase):
    """The acc4 triage's grader fixes (cluster P), one rule each, on synthetic rows in the probe's shapes (acc4's own data where it is present)."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def world1(self, steps, cells=None, settlements=(), state=None, shots=(), log=None, campaign=None, outputs=None):
        """A world 1 run of one launch (pid 7) with the given steps, a kept bundle A (the synthetic one, plus travel cells when given). A shot is
        (label, image) or (label, image, the shot record's extra keys)."""
        folder = self.root / 'run'
        (folder / 'in-game').mkdir(parents=True, exist_ok=True)
        steps = dict(steps)
        steps.setdefault('w1.identity', {'status': 'done', 'world': {'frame': [0, 0, 120, 60]}})
        c = bundle(folder / 'bundles/A')
        if campaign:
            c.update(campaign)
            write(folder / 'bundles/A/compiled/campaign/campaign.json', c)
        if cells:
            travel(folder / 'bundles/A', cells, settlements=settlements)
        for shot in shots:
            label, path = shot[0], shot[1]
            (folder / ('in-game/map2_acceptance-7-shot-%s.bmp' % label)).write_bytes(Path(path).read_bytes() if path else b'BM')
        out = {'steps': steps, 'shots': [dict({'label': s[0]}, **(s[2] if len(s) > 2 else {})) for s in shots], 'completed': True}
        out.update(outputs or {})
        write(folder / 'in-game/map2_acceptance-7.json', {'processId': 7, 'status': 'ok', 'outputs': out})
        if log is not None:
            (folder / 'in-game/map2_acceptance-7.log').write_text('\n'.join(log) + '\n', encoding='utf-8')
        doc = {'runId': 'acctest', 'phases': {'world1': {'status': 'incomplete', 'detail': None}}, 'launches': [{'phase': 'world1', 'pid': 7, 'result': 'in-game/map2_acceptance-7.json'}], 'hashes': []}
        doc.update(state or {})
        write(folder / 'state.json', doc)
        return S.Grader(S.Run(str(folder), {}))

    def test_the_permanent_white_route_in_force(self):
        """acc5 moved the policy to permanentWhite: with world 1's snow texture record naming none, a cold region that stays unwhitened and land
        ice white in both seasons confirm the route in force (acc5's own shares); a cold region that whitens fails it."""
        season = {'status': 'done', 'places': {'cold': [1, 1], 'warm': [2, 2], 'ice': [3, 3]}, 'extremes': [{'extreme': 'summer'}, {'extreme': 'winter'}], 'days': [], 'winds': []}
        identity = {'status': 'done', 'world': {'frame': [0, 0, 120, 60]}, 'support': {'climateVisuals': {'snowTexture': {'scene': 's', 'name': None, 'status': 'none: the scene names no snow texture'}}}}
        white = {'season-winter-cold': 0.05, 'season-summer-cold': 0.03, 'season-winter-warm': 0.01, 'season-summer-warm': 0.01, 'season-winter-ice': 0.87, 'season-summer-ice': 0.84}
        shots = [(label, None) for label in white]
        saved = S.image_stats
        try:
            S.image_stats = lambda path: {'white': next(v for k, v in white.items() if k in path)}
            g = self.world1({'w1.season': season, 'w1.identity': identity}, shots=shots)
            self.assertEqual(g.snow_route_in_force(), 'permanentWhite')
            self.assertEqual(g.winter()[1].status, S.PASS, g.winter()[1].detail)
            white['season-winter-cold'] = 0.6
            self.assertEqual(self.world1({'w1.season': season, 'w1.identity': identity}, shots=shots).winter()[1].status, S.FAIL)
            named = dict(identity, support={'climateVisuals': {'snowTexture': {'name': 'mnbl_snow_0.png', 'named': 'mnbl_snow_0.png', 'status': 'loaded'}}})
            self.assertEqual(self.world1({'w1.season': season, 'w1.identity': named}, shots=shots).snow_route_in_force(), 'namedTexture')
        finally:
            S.image_stats = saved

    def test_a_warm_region_darker_in_winter_is_not_snow(self):
        """MMR-247 F (acc6 c8/c9): acc6's warm region (desert) at the same hour, camera and settled scene scores 0.46 near-white in summer (pale
        sand in clear light) and 0.00 in winter (the winter grade and cloud shade): it did not whiten, so it passes; a warm region that gains
        white in winter (land ice's look in its winter shot) still fails. The shots are acc6's central halves at 128 x 128 (lossless fixtures)."""
        try:
            from PIL import Image
        except ImportError:
            self.skipTest('Pillow is not installed')
        def shot(name):
            """A fixture as a shot the grader reads: centred on a 256 x 256 canvas, so the grader's central half is the fixture itself."""
            img = Image.open(str(FIXTURES / name)).convert('RGB')
            canvas = Image.new('RGB', (256, 256))
            canvas.paste(img, (64, 64))
            path = self.root / ('shot-' + name)
            canvas.save(str(path))
            return str(path), img
        stats, warm = {}, {}
        for e in ('summer', 'winter'):
            warm[e], img = shot('acc6-season-%s-warm-128.png' % e)
            data = img.tobytes()
            stats[e] = S.pixel_stats([(data[i], data[i + 1], data[i + 2]) for i in range(0, len(data), 3)])
            self.assertEqual(S.image_stats(warm[e]), stats[e])
            original = ACC6 / 'in-game' / ('map2_acceptance-4996-shot-season-%s-warm.bmp' % e)
            if original.exists():
                self.assertEqual(S.image_stats(str(original)), stats[e])
        self.assertAlmostEqual(stats['summer']['white'], 0.457, places=2)
        self.assertLess(stats['winter']['white'], 0.01)
        ice, sand = shot('acc4-biome-land_ice-128.png')[0], shot('acc4-biome-desert-128.png')[0]
        season = {'status': 'done', 'places': {'cold': [1, 1], 'warm': [2, 2], 'ice': [3, 3]}, 'extremes': [{'extreme': 'summer'}, {'extreme': 'winter'}],
                  'days': [{'cold': 'Snowy', 'warm': 'Clear', 'ice': 'Snowy'}, {'cold': 'Clear', 'warm': 'Clear', 'ice': 'Snowy'}], 'winds': [[0.1, 1, 0], [0.5, 0, 1], [0.9, -1, 0]]}
        identity = {'status': 'done', 'world': {'frame': [0, 0, 120, 60]}, 'support': {'climateVisuals': {'snowTexture': {'scene': 's', 'name': None, 'status': 'none'}}}}
        at_eleven = {'hour': 11, 'settled': True}
        shots = [('season-summer-warm', warm['summer'], at_eleven), ('season-winter-warm', warm['winter'], at_eleven), ('season-summer-cold', sand, at_eleven),
                 ('season-winter-cold', sand, at_eleven), ('season-summer-ice', ice, at_eleven), ('season-winter-ice', ice, at_eleven)]
        winter, route = self.world1({'w1.season': season, 'w1.identity': identity}, shots=shots).winter()
        self.assertEqual(winter.status, S.PASS, winter.detail)
        self.assertEqual(route.status, S.PASS, route.detail)
        snowed = [s if s[0] != 'season-winter-warm' else (s[0], ice, at_eleven) for s in shots]
        winter = self.world1({'w1.season': season, 'w1.identity': identity}, shots=snowed).winter()[0]
        self.assertEqual(winter.status, S.FAIL)
        self.assertIn('warm region not whitened', ' '.join(winter.detail))
        # A warm region white all year (land ice's look in both shots) gains nothing in winter but stands at land ice's level: it fails too.
        white = [s if s[0] not in ('season-winter-warm', 'season-summer-warm') else (s[0], ice, at_eleven) for s in shots]
        winter = self.world1({'w1.season': season, 'w1.identity': identity}, shots=white).winter()[0]
        self.assertEqual(winter.status, S.FAIL, winter.detail)

    def test_a_timed_out_step_fails_with_its_budget(self):
        v = S.gate({'status': 'timeout', 'budgetSeconds': 240, 'recover': {'notIdleAfter': None}})
        self.assertEqual(v.status, S.FAIL)
        self.assertIn('240', v.detail[0])

    def test_a_hang_is_reported_where_it_stopped(self):
        """hang-4: the launch stopped as CPU flat; its progress log's last step without an end line is the hung step (end lines close the
        others), it fails, the phase detail and the top-level hangs name it, and every rule it left unrun says so."""
        log = ['2026-10-20T19:13:59.5170817Z +556237ms map2_acceptance:w1.incidents', '2026-10-20T19:13:59.6000000Z +556300ms map2_acceptance:w1.incidents:end done',
               '2026-10-20T19:14:01.1381125Z +557858ms map2_acceptance:w1.battle.snow', '2026-10-20T19:14:16.0000000Z +572000ms map2_acceptance:w1.battle.snow:end done',
               '2026-10-20T19:14:17.8870863Z +574607ms map2_acceptance:w1.battle.forest', '2026-10-20T19:14:17.8885968Z +574608ms acc_missions:field.forest',
               '2026-10-20T19:14:17.8900000Z +574609ms acc_missions:field.forest:findPoint']
        p = S.progress(str(self.write_log(log)))
        self.assertEqual((p['hungStep'], p['lastStage']), ('w1.battle.forest', 'acc_missions:field.forest:findPoint'))
        self.assertEqual(S.progress(str(self.write_log(log[:4])))['hungStep'], None)   # every started step ended: nothing hung
        wait = {'stopped': 'CPU flat for 5 minutes (not responding in 20 of 20 polls)', 'seconds': 986.9, 'kill': {'exited': True}}
        g = self.world1({'w1.battle.snow': {'status': 'done', 'case': {'failures': []}}}, log=log,
                        state={'launches': [{'phase': 'world1', 'pid': 7, 'result': 'in-game/map2_acceptance-7.json', 'wait': wait}]})
        self.assertEqual([(h['hungStep'], h['killed']) for h in g.hangs], [('w1.battle.forest', True)])
        # The decal lab's deliberate repro block is not one of the run's hangs.
        lab = self.world1({'w1.battle.snow': {'status': 'done', 'case': {'failures': []}}}, log=log,
                          state={'launches': [{'phase': 'decallab', 'pid': 7, 'result': 'in-game/map2_acceptance-7.json', 'wait': wait}],
                                 'hungSteps': [{'phase': 'decallab', 'pid': 7, 'step': 'w1.hangRepro.war'}]})
        self.assertEqual(lab.hangs, [])
        # Nor the hangrepro phase's (MMR-247 X: its control variant crashes or blocks on purpose).
        repro = self.world1({'w1.battle.snow': {'status': 'done', 'case': {'failures': []}}}, log=log,
                            state={'launches': [{'phase': 'hangrepro', 'pid': 7, 'result': 'in-game/map2_acceptance-7.json', 'wait': wait}],
                                   'hungSteps': [{'phase': 'hangrepro', 'pid': 7, 'step': 'w1.hangRepro.warControl'}]})
        self.assertEqual(repro.hangs, [])
        g = self.world1({'w1.battle.snow': {'status': 'done', 'case': {'failures': []}}}, log=log,
                        state={'launches': [{'phase': 'world1', 'pid': 7, 'result': 'in-game/map2_acceptance-7.json', 'wait': wait}]})
        _, v = g.need('world1', 'w1.battle.forest')
        self.assertEqual(v.status, S.FAIL)
        self.assertIn('hung', v.detail[0])
        out = self.root / 'acceptance.json'
        sys.argv = ['summarize', '--run', str(self.root / 'run'), '--out', str(out)]
        self.assertEqual(S.main(), 0)
        doc = json.loads(out.read_text(encoding='utf-8'))
        self.assertEqual(doc['phases']['world1']['detail']['hungStep'], 'w1.battle.forest / acc_missions:field.forest:findPoint')
        self.assertEqual(doc['hangs'][0]['pid'], 7)
        self.assertEqual(doc['hangs'][0]['captures'], [])
        # MMR-247: the probe's hang captures (collected as in-game/map2_acceptance-<pid>-hang-<k>.txt/.dmp) are listed with the hang.
        ingame = self.root / 'run' / 'in-game'
        (ingame / 'map2_acceptance-7-hang-1.txt').write_text('MNBL hang capture 1 pid 7\nMNBL section on the engine thread: inside the probe step for 75.0 s\n', encoding='utf-8')
        (ingame / 'map2_acceptance-7-hang-1.dmp').write_bytes(b'MDMP' + b'\0' * 60)
        (ingame / 'map2_acceptance-7-hang-2.txt').write_text('MNBL hang capture 2 pid 7\nMNBL section on the engine thread: outside\nresumed 2026: after 300.0 s\n', encoding='utf-8')
        caps = S.Run(str(self.root / 'run'), {}).hangs()[0]['captures']
        self.assertEqual([(c['path'], c['section'], c['resumed'], c['dumpBytes']) for c in caps],
                         [('in-game/map2_acceptance-7-hang-1.txt', 'inside the probe step for 75.0 s', False, 64), ('in-game/map2_acceptance-7-hang-2.txt', 'outside', True, None)])
        season = next(r for r in doc['steps'] if r['id'] == '2.winter')
        self.assertEqual(season['status'], S.NOT_RUN)
        self.assertTrue(season['detail'][0].startswith('not run: world 1 hung at w1.battle.forest'), season['detail'])
        # acc4 itself: the forest battle, from the launch's wait and the collected log alone.
        if (ACC4 / 'state.json').exists():
            h = S.Run(str(ACC4), {}).hangs()
            self.assertEqual([(x['phase'], x['pid'], x['hungStep'], x['lastStage']) for x in h], [('world1', 14208, 'w1.battle.forest', 'acc_missions:field.forest')])

    def write_log(self, lines):
        p = self.root / ('log-%d.log' % len(lines))
        p.write_text('\n'.join(lines) + '\n', encoding='utf-8')
        return p

    def test_faces_follow_the_travel_cells(self):
        """fails-1: the in-game face against the compiled cell's land type on land cells only (>= 99%); points on a cell edge are skipped;
        acc4's biome samples on water or mountain cells (an invalid face read as Plain) no longer count."""
        cells = ['PPDD..', 'FFSSmm']
        samples = [[5, 5, 'land', 'Plain', 'Plain', 1], [25, 5, 'land', 'Desert', 'Desert', 2], [5, 15, 'land', 'Forest', 'Forest', 3], [45, 5, 'sea', None, 'invalid', -1]]
        g = self.world1({'w1.biomes': {'status': 'done', 'spots': {}, 'cellSamples': samples}}, cells=cells)
        v = g.biomes()[1]
        self.assertEqual(v.status, S.PASS, v.detail)
        self.assertIn('3 of 3', v.detail[0])
        self.assertIn('unavailable', v.detail[1])   # no navigation masks in the synthetic bundle: the recount is information only
        wrong = samples[:3] + [[35, 5, 'land', 'Desert', 'Plain', 4]]
        self.assertEqual(self.world1({'w1.biomes': {'status': 'done', 'spots': {}, 'cellSamples': wrong}}, cells=cells).biomes()[1].status, S.FAIL)
        # acc4's shape: biome samples mapped onto the cells; a sea sample reading Plain and a point on the cell edge are skipped.
        old = {'grassland': [[5, 5, 'Plain'], [45, 5, 'Plain'], [20.0, 5, 'Desert'], [15, 15, 'Forest']]}
        v = self.world1({'w1.biomes': {'status': 'done', 'spots': {}, 'faceSamples': old}}, cells=cells).biomes()[1]
        self.assertEqual(v.status, S.PASS, v.detail)
        self.assertIn('2 of 2', v.detail[0])
        self.assertIn('1 off land, 1 on a cell edge', v.detail[0])
        if (ACC4 / 'state.json').exists():
            v = S.Grader(S.Run(str(ACC4), {})).biomes()[1]
            self.assertEqual(v.status, S.PASS, v.detail)
            self.assertIn('190 of 190', v.detail[0])
            self.assertIn('0 mismatches', v.detail[1])

    def speed(self, **rows):
        base = {'status': 'done', 'Plain': {'lines': [['Base', 4.0]], 'final': 4.0, 'roadCell': False}, 'Snow': {'lines': [['Base', 4.0], ['Terrain', -0.2]], 'roadCell': False},
                'Forest': {'lines': [['Base', 4.0], ['Forest', -0.4]]}, 'Desert': {'lines': [['Base', 4.0], ['Desert', -0.1]]}, 'Swamp': {'absent': True, 'reason': 'no free Swamp face'},
                'Road': {'lines': [['Base', 4.0], ['Road', 0.3]], 'roadCell': True, 'face': 'Steppe'}, 'cellTypes': {'Plain': 4, 'Snow': 2}}
        base.update(rows)
        return self.world1({'w1.speed': base}).speed()

    def test_speed_terms_by_name_and_swamp_na(self):
        """fails-2/3: each type's own term (Snow and Swamp read 'Terrain'), the Road term on a road cell, Swamp n/a on a world without a Swamp cell."""
        v = self.speed()
        self.assertEqual(v.status, S.PASS, v.detail)
        self.assertEqual(self.speed(cellTypes={'Plain': 4, 'Swamp': 3}).status, S.BLOCKED)   # Swamp cells exist but no free face: not n/a
        self.assertEqual(self.speed(Desert={'lines': [['Base', 4.0], ['Road', 0.3]], 'roadCell': True}).status, S.FAIL)   # only the road term: no Desert term
        self.assertEqual(self.speed(Road={'lines': [['Base', 4.0], ['Road', 0.3]], 'roadCell': False}).status, S.FAIL)    # a Road sample off the road
        self.assertEqual(self.speed(Bridge={'lines': [['Base', 4.0], ['Road', 0.3]], 'roadCell': True}).status, S.FAIL)   # no 'Bridge on a road'

    def test_villages_keep_their_cultures_installed_protected_types(self):
        """fails-6: Nord keeps its wheat farm (War Sails' Nord villages have no horse ranch); an empire world without its ranch still fails."""
        campaign = bundle(self.root / 'probe')
        nord = census(campaign)
        for t in nord['towns'] + nord['castles']:
            t['culture'] = 'nord'
        for i, v in enumerate(nord['villages']):
            v['culture'] = 'nord'; v['type'] = ['wheat_farm', 'fisherman', 'lumberjack', 'trapper'][i % 4]
        g = self.world1({'w1.census0': {'status': 'done', 'census': nord}})
        self.assertEqual(g.villages('world1', 'A').status, S.PASS, g.villages('world1', 'A').detail)
        empire = census(campaign)
        for v in empire['villages']:
            if 'ranch' in v['type']:
                v['type'] = 'olive_trees'
        self.assertEqual(self.world1({'w1.census0': {'status': 'done', 'census': empire}}).villages('world1', 'A').status, S.FAIL)

    def test_parity_runs_or_reports_not_run_and_na(self):
        """fails-7/9: a culture incident whose every try was not run is not-run (not a fail); no kingdom of the literal's culture is n/a; a
        kingdom of it with no settlement still blocks."""
        campaign = bundle(self.root / 'probe')
        day0 = census(campaign)
        day0['kingdoms'][0]['culture'] = 'empire'
        g = self.world1({'w1.census0': {'status': 'done', 'census': day0}})
        wanted = {'literals': ['empire'], 'candidates': 3, 'tries': [['v1', 'village', 'empire', 'not run: no closure singleton'], ['t1', 'town', 'empire', 'not run: no closure singleton']]}
        self.assertEqual(g.culture_incident(wanted).status, S.NOT_RUN)
        self.assertEqual(g.culture_incident(dict(wanted, tries=[['v1', 'village', 'empire', False]])).status, S.FAIL)
        self.assertEqual(g.culture_incident(dict(wanted, tries=[['v1', 'village', 'empire', True]], trueAt='v1')).status, S.PASS)
        deer = {'literals': ['khuzait'], 'candidates': 0, 'tries': [], 'kingdomCultures': ['empire', 'vlandia'], 'kingdomOfCulture': False}
        self.assertEqual(g.culture_incident(deer).status, S.PASS)
        self.assertIn('n/a', g.culture_incident(deer).detail[0])
        self.assertEqual(g.culture_incident(dict(deer, kingdomOfCulture=True, kingdomCultures=['khuzait'])).status, S.BLOCKED)
        # acc4's rows lack kingdomOfCulture: the day-0 census's kingdom cultures decide (an empire world has no khuzait kingdom).
        self.assertEqual(g.culture_incident({'literals': ['khuzait'], 'candidates': 0, 'tries': []}).status, S.PASS)
        self.assertEqual(g.culture_incident({'literals': ['empire'], 'candidates': 0, 'tries': []}).status, S.BLOCKED)

    def test_hideouts_draw_from_the_towns_of_their_land_group(self):
        """fails-8: a desert-bandit hideout of an aserai land group is right even when the nearest town worldwide is a khuzait one across the
        sea (khuzait weighs no desert bandits); the same hideout in the khuzait group is wrong."""
        cells = ['PPPPP.PPPPP.'] * 6
        settlements = [{'id': 'tA', 'point': {'x': 15, 'y': 30}, 'group': 0}, {'id': 'tB', 'point': {'x': 62, 'y': 30}, 'group': 1}]
        towns = [{'id': 'tA', 'culture': 'aserai', 'position': [15, 30]}, {'id': 'tB', 'culture': 'khuzait', 'position': [62, 30]}]
        def verdict(x):
            # o1 is not the only desert-bandit hideout, so it is never the coverage hideout of its culture.
            hide = [{'id': 'o1', 'culture': 'desert_bandits', 'position': [x, 30], 'parties': []}, {'id': 'o2', 'culture': 'desert_bandits', 'position': [25, 10], 'parties': []},
                    {'id': 'o3', 'culture': 'steppe_bandits', 'position': [65, 10], 'parties': []}]
            row = {'status': 'done', 'census': {'towns': towns, 'hideouts': hide}}
            return self.world1({'w1.census0': row}, cells=cells, settlements=settlements).hideouts(row)
        self.assertEqual(verdict(45).status, S.PASS, verdict(45).detail)   # group 0, 17 units from tB, 30 from tA
        v = verdict(75)
        self.assertEqual(v.status, S.FAIL, v.detail)
        self.assertIn('o1', v.detail[0])
        if (ACC4 / 'state.json').exists():
            g = S.Grader(S.Run(str(ACC4), {}))
            v = g.hideouts(g.w1['w1.census0'])
            self.assertEqual(v.status, S.PASS, v.detail)

    def test_a_lake_counts_visible_under_any_shown_plane(self):
        """fails-10: a sub-datum lake whose ray meets the sea plane is visible; a lake whose ray met nothing still fails; a probe row naming the
        plane is taken as it is."""
        def water(missed, lakes=4, hits=2):
            vis = {'passed': False, 'failures': ['bodies: %d of %d lake rays did not meet their plane above the terrain' % (lakes - hits, lakes)],
                   'bodies': {'lakes': lakes, 'lakeRayHits': hits, 'missed': missed}, 'cells': {'dryCount': 0, 'impassable': 9}}
            return self.world1({'w1.water': {'status': 'done', 'visibility': vis}}, shots=[('water-lake', None)]).water('world1', 'w1.water')
        sea = [117, 665.625, 831.25, -12.75, 0, -12.78]
        self.assertEqual(water([sea, [119, 1465.625, 831.25, -12.75, 0, -12.78]]).status, S.PASS)
        v = water([sea, [94, 534.375, 796.875, 0.2955, 1.2359, 0.24]])
        self.assertEqual(v.status, S.FAIL)
        self.assertIn('94', v.detail[0])
        self.assertEqual(water([sea + ['sea'], [94, 534.375, 796.875, 0.2955, 1.2359, 0.24, 'lake:93']]).status, S.PASS)
        self.assertEqual(water([sea], lakes=4, hits=2).status, S.FAIL)   # one miss beyond the record cap stays unexplained

    def test_decals_on_water_count_only_confirmed_or_analytic_hits(self):
        """fails-5: the analytic test's counts are the verdict; acc4's ray counts fail only on examples with the ground under the datum."""
        def g(water):
            return self.world1({'w1.water': dict({'status': 'done'}, **water)})
        self.assertEqual(g({'decalsOnWater': {'road': 2}, 'decalsOnWaterRule': 'analytic', 'decalsOnWaterExamples': [['road', 1, 2, -0.1, 'sea']]}).decals_on_water('world1', 'road').status, S.FAIL)
        self.assertEqual(g({'decalsOnWater': {'road': 0}, 'decalsOnWaterRule': 'analytic'}).decals_on_water('world1', 'road').status, S.PASS)
        self.assertEqual(g({'decalsOnWater': {'magic': 3}, 'decalsOnWaterExamples': [['magic', 1, 2, 2.4, 2.42]]}).decals_on_water('world1', 'magic').status, S.BLOCKED)
        self.assertEqual(g({'decalsOnWater': {'road': 3}, 'decalsOnWaterExamples': [['road', 1, 2, -0.03, 0]]}).decals_on_water('world1', 'road').status, S.FAIL)

    def season(self, shots, **row):
        base = {'status': 'done', 'places': {'cold': [1, 1], 'ice': [3, 3]}, 'extremes': [{'extreme': 'summer'}, {'extreme': 'winter'}], 'days': [], 'winds': []}
        base.update(row)
        return self.world1({'w1.season': base}, shots=shots)

    def test_season_shots_at_night_or_unsettled_decide_nothing(self):
        """notrun-2: the cold region's winter shot at hour 22 makes its route part blocked, not a fail."""
        try:
            from PIL import Image
        except ImportError:
            self.skipTest('Pillow is not installed')
        white, dark = self.root / 'white.bmp', self.root / 'dark.bmp'
        Image.new('RGB', (64, 48), (200, 200, 205)).save(white)
        Image.new('RGB', (64, 48), (20, 25, 30)).save(dark)
        shots = [('season-winter-cold', dark), ('season-summer-cold', dark), ('season-winter-ice', white), ('season-summer-ice', white)]
        g = self.season(shots)
        meta = {'season-winter-cold': {'hour': 22.5, 'settled': True}, 'season-summer-cold': {'hour': 12.0, 'settled': True}}
        for r in g.run.results['world1']:
            for s in r['outputs']['shots']:
                s.update(meta.get(s['label'], {'hour': 12.0, 'settled': True}))
        route = g.winter()[1]
        self.assertEqual(route.status, S.BLOCKED, route.detail)
        self.assertIn('outside daylight', ' '.join(route.detail))
        for r in g.run.results['world1']:
            for s in r['outputs']['shots']:
                s.update({'hour': 12.0, 'settled': True})
        self.assertEqual(g.winter()[1].status, S.FAIL)   # at midday and settled, a cold region that stays dark fails the named route

    def test_snow_share_follows_the_node_byte(self):
        """notrun-6: the Snowy share on the sampled days against the engine rule for the place's snow byte, not fixed bounds: an ice cap
        snowing every day and a cold node snowing on the winter days pass; a cold node that never snows fails."""
        seasons = [0.0, 0.25, 0.5, 0.75, 1.0, 0.9, 0.6, 0.2]
        byte = 155   # GeneratedClimateSystem.SnowByte(0.75): Snowy for three quarters of the year
        snowy = [S.snow_value(s, byte) > 0.55 for s in seasons]
        days = [{'day': i, 'season': s, 'cold': 'Snowy' if w else 'Clear', 'ice': 'Blizzard'} for i, (s, w) in enumerate(zip(seasons, snowy))]
        row = {'status': 'done', 'snowBytes': {'cold': byte, 'ice': 255}, 'snowyShareExpected': {'cold': 0.75, 'ice': 1.0}, 'days': days}
        g = self.world1({'w1.season': row})
        self.assertEqual(g.snow_weather(row).status, S.PASS, g.snow_weather(row).detail)
        self.assertTrue(0 < sum(snowy) < len(snowy))
        dry = dict(row, days=[dict(d, cold='Clear') for d in days])
        self.assertEqual(g.snow_weather(dry).status, S.FAIL)
        self.assertLess(S.snow_value(1.0, 0), 0.55)   # a node with no snow season never snows, even at mid-winter
        self.assertGreater(S.snow_value(0.0, 255), 0.55)   # an ice cap node snows at mid-summer

    def test_a_field_battle_is_graded_by_the_resolvers_input(self):
        """notrun-3: the Forest face's battle cell centre reads desert; a desert scene is right for it, a woodland-only scene is not."""
        scene = lambda sid: next(s for s in json.loads((REPO / 'worldgen/policies/mission-scenes.json').read_text())['battleScenes'] if s['id'] == sid)
        desert = next(s['id'] for s in json.loads((REPO / 'worldgen/policies/mission-scenes.json').read_text())['battleScenes'] if not s['naval'] and s['classes'] == ['desert'])
        woods = next(s['id'] for s in json.loads((REPO / 'worldgen/policies/mission-scenes.json').read_text())['battleScenes'] if not s['naval'] and 'desert' not in s['classes'])
        g = self.world1({})
        case = lambda sid: {'failures': [], 'mission': {'scene': sid}, 'fieldAt': {'class': 'desert', 'face': 'Forest', 'biome': 'desert'}}
        self.assertEqual(g.battle_class(case(desert), face='Forest').status, S.PASS, g.battle_class(case(desert), face='Forest').detail)
        self.assertEqual(g.battle_class(case(woods), face='Forest').status, S.FAIL)
        self.assertIn('desert', scene(desert)['classes'])

    def test_the_zone_battle_is_graded_by_the_live_type(self):
        """acc5 c13: the Nerdun fire cast retyped a cold tundra cell (natural face Steppe) to Desert; MMR-260 patches the cell's battle class to
        the live type's, so a desert scene with a Desert initializer terrain passes, a scene without desert fails, and the initializer-terrain failure an
        acc5 probe raised against the navmesh face group is dropped only when the record's terrain is the live type."""
        scenes = json.loads((REPO / 'worldgen/policies/mission-scenes.json').read_text())['battleScenes']
        desert = next(s['id'] for s in scenes if not s['naval'] and s['classes'] == ['desert'])
        other = next(s['id'] for s in scenes if not s['naval'] and 'desert' not in s['classes'])
        failure = 'Initializer record terrain differs from the party\'s generated face terrain.'

        def zone(sid, terrain, failures=(failure,)):
            case = {'failures': list(failures), 'mission': {'scene': sid, 'initializerRecord': {'TerrainType': terrain}},
                    'fieldAt': {'class': 'cold', 'face': 'Steppe', 'biome': 'cold_tundra'}}
            return self.world1({'w1.battle.mutated': {'status': 'done', 'battleCell': [6630, 1040.625, 921.875, 'Desert', 'Desert'], 'case': case}}).zone_battle()
        self.assertEqual(zone(desert, FACE['Desert']).status, S.PASS, zone(desert, FACE['Desert']).detail)
        self.assertEqual(zone(other, FACE['Desert']).status, S.FAIL)
        self.assertEqual(zone(desert, FACE['Steppe']).status, S.FAIL)   # the mission did not take the live type
        self.assertEqual(zone(desert, FACE['Desert'], failures=()).status, S.PASS)   # the fixed probe's record

    def soak(self, events, detail, census0=None):
        row = {'status': 'done', 'advance': {'daysAdvanced': 31}, 'events': events, 'eventCounts': {'x': 1},
               'census': {'population': {'wanderers': 1, 'wanderersTarget': 1, 'wanderersRotated': 2, 'houseDetail': detail},
                          'clans': [{'id': 'm1', 'mercenary': True, 'minor': True, 'alive': 5, 'underMercenaryService': False}]}}
        steps = {'w1.soak': row, 'w1.census0': {'status': 'done', 'census': census0 or {'clans': []}}}
        return self.world1(steps).soak()

    def test_the_soak_counts_hires_and_keeps_dissolved_kingdoms_houses(self):
        """notrun-5: LeaveAsMercenary is no hire; JoinAsMercenary is; a dissolved kingdom's house removed fails c20."""
        kept = {'l1': {'kingdom': None, 'eliminated': False, 'removed': False}}
        self.assertEqual(self.soak([[3, 'clanChangedKingdom', ['m1', 'k1', None, 'LeaveAsMercenary', True]]], kept).status, S.FAIL)
        hire = [[3, 'clanChangedKingdom', ['m1', None, 'k1', 'JoinAsMercenary', True]]]
        self.assertEqual(self.soak(hire, kept).status, S.PASS, self.soak(hire, kept).detail)
        gone = hire + [[9, 'kingdomDestroyed', ['k1']]]
        self.assertEqual(self.soak(gone, kept).status, S.PASS)   # house l1 of k1 still listed
        v = self.soak(gone, {'l1': {'kingdom': None, 'eliminated': True, 'removed': True}})
        self.assertEqual(v.status, S.FAIL)
        self.assertIn('l1', ' '.join(v.detail))

    def test_engine_children_follow_the_engine_rule(self):
        """fails-12: the engine-added children against the compiled estimate and the 1.4.8 rule ceil(adults / 2) less the existing children."""
        campaign = bundle(self.root / 'probe')
        day0 = census(campaign)
        day0['clans'][0].update(adults=4, children=3)   # 1 planned minor + 2 engine children: ceil(4 / 2) - 1 = 1 allowed
        day0['population'].update(engineChildren=2, engineChildrenByHouse={'l1': 2})
        g = self.world1({'w1.census0': {'status': 'done', 'census': day0}})
        v = g.census_match('world1', 'A')[1]
        self.assertEqual(v.status, S.FAIL, v.detail)
        self.assertIn("('l1', 2, 1)", v.detail[0])
        day0['clans'][0].update(children=2)   # no planned minor: 2 allowed
        self.assertEqual(self.world1({'w1.census0': {'status': 'done', 'census': day0}}).census_match('world1', 'A')[1].status, S.PASS)
        if (ACC4 / 'state.json').exists():
            v = S.Grader(S.Run(str(ACC4), {})).census_match('world1', 'A')[1]
            self.assertEqual(v.status, S.PASS, v.detail)
            self.assertIn('engine-added children 102, compiled engineChildren 102', v.detail[0])

    @staticmethod
    def overlay_record(layers=('veins', 'magicWater', 'rivers'), entities=3):
        """OverlayMeshRuntime.Record() as the probe stores it: every layer drawn as meshes, its materials resolved, entities live."""
        part = lambda names, mats: {'status': 'installed', 'materials': mats,
                                    'installed': {'byLayer': {l: {'entities': entities if l in names else 0, 'pieces': 9, 'triangles': 40, 'hidden': False} for l in ('veins', 'rivers', 'magicWater')},
                                                  'missingMaterials': [], 'errors': []}}
        return {'policy': {'enabled': True, 'veins': 'mesh', 'magicWater': 'mesh', 'rivers': 'mesh'}, 'ready': {l: True for l in layers}, 'errors': [],
                'magic': part(('veins', 'magicWater'), {'vein:fire': {'template': 'torch_flame_base', 'copy': True}, 'magicWater': {'published': 'mnbl_overlay_magicwater'}}),
                'rivers': part(('rivers',), {'river': {'material': 'naval_worldmap_water'}})}

    def test_magic_criteria_c29_to_c31(self):
        """c29 needs the zone and cast screenshots, the vein meshes, the look shown and the owner's review; c30 is blocked on a build without
        MMR-289 and fails a feature on a road; c31 never passes on records alone: the screenshot step's mesh record plus the toggle diff pass its
        look, else it is blocked on the owner's review (an attestation decides it)."""
        try:
            from PIL import Image
        except ImportError:
            self.skipTest('Pillow is not installed')
        lit, plain = self.root / 'lit.bmp', self.root / 'plain.bmp'
        Image.new('RGB', (64, 48), (90, 160, 220)).save(lit)
        Image.new('RGB', (64, 48), (30, 60, 90)).save(plain)
        features = {'available': True, 'live': 40, 'generated': 38, 'liveMutation': 0, 'onWater': 0, 'onRoad': 0, 'nearSettlement': 0, 'errors': []}
        toggle = lambda label: {'label': label, 'hiddenLabel': label + '-hidden', 'setVisibleFalse': True, 'setVisibleTrue': True}
        steps = {'w1.magic': {'status': 'done', 'drawn': 5, 'zones': [{}, {}, {}], 'features': features, 'overlayMeshes': self.overlay_record(), 'toggle': toggle('magic-veins')},
                 'w1.cast': {'status': 'done', 'reveal': {'available': True}, 'features': dict(features, liveMutation=6)},
                 'w1.continue': {'status': 'done', 'features': dict(features, live=46, liveMutation=6)},
                 'w1.magicWater': {'status': 'done', 'drawn': 120, 'errors': [], 'record': {'status': 'replaced by the overlay meshes (MMR-291)'}, 'toggle': toggle('magic-water'),
                                   'overlayMeshes': self.overlay_record()},
                 'w1.frames': {'status': 'done', 'near': {'meanMainMs': 5.0}, 'far': {'meanMainMs': 6.0}}, 'w1.memoryDay': {'status': 'done', 'after': {}}}
        shown = {'meshLayer': {'meshes': True, 'entities': 3, 'inView': 2, 'hidden': False}}
        hid = {'meshLayer': {'meshes': True, 'entities': 3, 'inView': 2, 'hidden': True}}
        shots = [(l, None) for l in ('magic-ground', 'magic-zone-1', 'magic-zone-2', 'cast-before', 'cast-mid', 'cast-full')] + \
                [('magic-veins', lit, shown), ('magic-veins-hidden', plain, hid), ('magic-water', lit, shown), ('magic-water-hidden', plain, hid)]
        g = self.world1(steps, shots=shots)
        c29 = g.veins_look()
        self.assertEqual(c29.status, S.BLOCKED, c29.detail)   # only the owner's review is missing
        self.assertEqual(len(c29.detail), 1, c29.detail)
        self.assertIn('owner review', c29.detail[0])
        self.assertEqual(g.features().status, S.NOT_RUN)   # c27's baseline did not run: the frame cost part is not-run, the rest passes
        steps['w1.continue']['features'] = dict(features, onRoad=1)
        self.assertEqual(self.world1(steps, shots=shots).features().status, S.FAIL)
        steps['w1.magic']['features'] = {'available': False, 'reason': 'MagicFeaturesRuntime is not in this build'}
        self.assertEqual(self.world1(steps, shots=shots).features().status, S.BLOCKED)
        self.assertEqual(g.magic_water().status, S.PASS, g.magic_water().detail)
        # Hiding the layer changes nothing: blocked on the owner's review (with the lab hint), not passed and not failed on records.
        same = [s for s in shots if s[0] != 'magic-water-hidden'] + [('magic-water-hidden', lit, hid)]
        v = self.world1(steps, shots=same).magic_water()
        self.assertEqual(v.status, S.BLOCKED, v.detail)
        self.assertIn('publish request P', ' '.join(v.detail))
        # Records and a toggle diff without the shot's mesh record (decals, or a probe before MMR-291): blocked too.
        bare = [s[:2] for s in shots]
        self.assertEqual(self.world1(steps, shots=bare).magic_water().status, S.BLOCKED)
        self.assertEqual(self.world1(steps, shots=bare).veins_look().status, S.BLOCKED)
        blocked = dict(steps, **{'w1.magicWater': {'status': 'blocked', 'reason': 'MagicWaterRuntime is not in this build (MMR-290 not merged)'}})
        self.assertEqual(self.world1(blocked, shots=shots).magic_water().status, S.BLOCKED)
        self.assertEqual(S.diff_share([(0, 0, 0)] * 4, [(0, 0, 9), (0, 0, 7), (0, 0, 0), (8, 0, 0)]), 0.5)

    def test_c33_grades_the_overlay_meshes_from_the_records_and_the_lab(self):
        """c33: each layer's record at its step (entities, materials resolved, no error, the toggle taken, both screenshots), the decal lab's
        mesh grid when it ran, c27's frame cost; the grade names the screenshot review the look still needs."""
        toggle = lambda label: {'label': label, 'hiddenLabel': label + '-hidden', 'setVisibleFalse': True, 'setVisibleTrue': True}
        steps = {'w1.magic': {'status': 'done', 'overlayMeshes': self.overlay_record(), 'toggle': toggle('magic-veins')},
                 'w1.water': {'status': 'done', 'overlayMeshes': self.overlay_record(), 'toggle': toggle('river-mesh')},
                 'w1.magicWater': {'status': 'done', 'record': {'overlayMeshes': self.overlay_record()}, 'toggle': toggle('magic-water')},
                 'w1.frames': {'status': 'done', 'near': {'meanMainMs': 5.0}, 'far': {'meanMainMs': 6.0}}, 'w1.memoryDay': {'status': 'done', 'after': {}}}
        shots = [(l, None) for l in ('magic-veins', 'magic-veins-hidden', 'river-mesh', 'river-mesh-hidden', 'magic-water', 'magic-water-hidden')]
        base = {'launches': [{'phase': 'world1', 'pid': 7, 'result': 'in-game/map2_acceptance-7.json'},
                             {'phase': 'baseline', 'pid': 8, 'result': 'in-game/map2_acceptance-8.json'}]}
        g = self.world1(steps, shots=shots, state=base)
        write(self.root / 'run/in-game/map2_acceptance-8.json', {'processId': 8, 'outputs': {'steps': {'base.frames': {'status': 'done', 'near': {'meanMainMs': 5.0}, 'far': {'meanMainMs': 6.0}},
                                                                                                         'base.memoryDay': {'status': 'done', 'after': {}}}}})
        g = S.Grader(S.Run(str(self.root / 'run'), {}))
        v = g.overlay_meshes()
        self.assertEqual(v.status, S.PASS, v.detail)
        self.assertIn('screenshot review', v.detail[-1])
        # A layer whose material did not resolve, or whose toggle did not take, fails.
        bad = json.loads(json.dumps(steps))
        bad['w1.water']['overlayMeshes']['rivers']['materials'] = {'river': {'missing': 'naval_worldmap_water, worldmap_water'}}
        bad['w1.magic']['toggle']['setVisibleTrue'] = False
        g = self.world1(bad, shots=shots, state=base)
        self.assertEqual(g.mesh_layer('world1', 'w1.water', 'rivers').status, S.FAIL)
        self.assertEqual(g.mesh_layer('world1', 'w1.magic', 'veins').status, S.FAIL)
        self.assertEqual(g.mesh_layer('world1', 'w1.magicWater', 'magicWater').status, S.PASS)
        # Decals by policy, and a probe before MMR-291.
        dec = json.loads(json.dumps(steps)); dec['w1.magic']['overlayMeshes']['policy']['veins'] = 'decal'
        self.assertEqual(self.world1(dec, shots=shots).mesh_layer('world1', 'w1.magic', 'veins').status, S.FAIL)
        old = json.loads(json.dumps(steps)); del old['w1.magic']['overlayMeshes']
        self.assertEqual(self.world1(old, shots=shots).mesh_layer('world1', 'w1.magic', 'veins').status, S.NOT_RUN)
        # The decal lab's mesh grid: a column drawn nowhere fails c33's lab part.
        lab = {'spots': {'land-mesh': {'decals': {'mesh:vein/full': {'drawn': True}, 'mesh:river/cell': {'drawn': True}, 'mesh:magicWater/full': {'drawn': False}}},
                         'sea-mesh': {'decals': {'mesh:magicWater/plane': {'drawn': True}}}}}
        write(self.root / 'run/decal-lab/decal-lab.json', lab)
        self.assertEqual(S.Grader(S.Run(str(self.root / 'run'), {})).lab_meshes().status, S.PASS)
        lab['spots']['sea-mesh']['decals']['mesh:magicWater/plane']['drawn'] = False
        write(self.root / 'run/decal-lab/decal-lab.json', lab)
        v = S.Grader(S.Run(str(self.root / 'run'), {})).lab_meshes()
        self.assertEqual(v.status, S.FAIL)
        self.assertIn('mesh:magicWater', v.detail[0])
        # MMR-291-W: with the policy's passes named, magic water's pass row must draw over the sea (blend not drawing there is the A).
        lab = {'meshPasses': {'veins': 'blend', 'magicWater': 'depth', 'alphaTest': 0.3},
               'spots': {'land-mesh': {'decals': {'mesh:vein/blend': {'drawn': True}, 'mesh:river/blend': {'drawn': True}, 'mesh:magicWater/depth': {'drawn': True}}},
                         'sea-mesh': {'decals': {'mesh:magicWater/blend': {'drawn': False, 'meanDiff': 1.7}, 'mesh:magicWater/depth': {'drawn': True, 'meanDiff': 14.2}}}}}
        write(self.root / 'run/decal-lab/decal-lab.json', lab)
        v = S.Grader(S.Run(str(self.root / 'run'), {})).lab_meshes()
        self.assertEqual(v.status, S.PASS, v.detail)
        self.assertIn("magic water's pass depth over the sea: drawn", v.detail[-1])
        lab['spots']['sea-mesh']['decals']['mesh:magicWater/depth']['drawn'] = False
        write(self.root / 'run/decal-lab/decal-lab.json', lab)
        v = S.Grader(S.Run(str(self.root / 'run'), {})).lab_meshes()
        self.assertEqual(v.status, S.FAIL)
        self.assertIn('pass depth draws nothing over the sea', v.detail[0])

    def test_c32_no_freeze_with_the_fresh_map_rule_off(self):
        """c32: no world 1 hang or crash, no fresh-map reload, the battles, castle and siege run (one after a mission on its map), and the hang
        repro's variants with the fix ran; a pre-fix control's crash is only listed; a crashed world 1 step fails it and is reported."""
        case = lambda after: {'status': 'done', 'case': {'failures': [], 'missionOnThisMapBefore': after}}
        steps = {n: case(n != 'w1.battle.snow') for n in S.AFTER_MISSION_CASES}
        repro = {'phases': {'world1': {'status': 'done'}, 'hangrepro': {'status': 'done', 'detail': {'hangRepro': {
            'w1.hangRepro.warControl': {'outcome': 'crashed', 'detail': 'c0000005 in TaleWorlds.Native.dll+0x55e625'},
            'w1.hangRepro.war': {'outcome': 'ran'}, 'w1.hangRepro.warNoDressingMoves': {'outcome': 'ran'}, 'w1.hangRepro.castle': {'outcome': 'ran'}}}},
            'decallab': {'status': 'done', 'detail': {'hangRepro': {'w1.hangRepro.war': {'outcome': 'ran'}, 'w1.hangRepro.castle': {'outcome': 'ran'}}}}}}
        v = self.world1(steps, state=repro, outputs={'freshMapAfterMission': False}).no_freeze()
        self.assertEqual(v.status, S.PASS, v.detail)
        # The rule on, or a reload used.
        self.assertEqual(self.world1(steps, state=repro, outputs={'freshMapAfterMission': True}).no_freeze().status, S.NOT_RUN)
        reloaded = dict(steps, **{'w1.castle': {'status': 'done', 'case': {'failures': [], 'missionOnThisMapBefore': True, 'freshMapBefore': {'loaded': True}}}})
        self.assertEqual(self.world1(reloaded, state=repro, outputs={'freshMapAfterMission': False}).no_freeze().status, S.FAIL)
        # A variant with the fix that crashed fails; the repro not run leaves the rest to decide.
        crashed = json.loads(json.dumps(repro)); crashed['phases']['hangrepro']['detail']['hangRepro']['w1.hangRepro.castle'] = {'outcome': 'crashed', 'detail': 'c0000005'}
        self.assertEqual(self.world1(steps, state=crashed, outputs={'freshMapAfterMission': False}).no_freeze().status, S.FAIL)
        self.assertEqual(self.world1(steps, state={'phases': {'world1': {'status': 'done'}}}, outputs={'freshMapAfterMission': False}).no_freeze().status, S.PASS)
        # No case after a mission: not shown.
        first = {n: case(False) for n in S.AFTER_MISSION_CASES}
        self.assertEqual(self.world1(first, state=repro, outputs={'freshMapAfterMission': False}).no_freeze().status, S.NOT_RUN)
        # A world 1 launch that exited by itself with w1.siege open: crashed there, with its fault event; c32 fails and main() reports it.
        log = ['2026-10-21T10:00:00.0000000Z +1ms map2_acceptance:w1.siege', '2026-10-21T10:00:01.0000000Z +2ms acc_missions:siege:settle']
        open_siege = dict(steps); del open_siege['w1.siege']
        launch = {'phase': 'world1', 'pid': 7, 'result': 'in-game/map2_acceptance-7.json', 'wait': {'stopped': None},
                  'progress': {'openStep': 'w1.siege', 'lastStage': 'acc_missions:siege:settle', 'lastStageUtc': '2026-10-21T10:00:01Z'},
                  'crash': {'module': 'TaleWorlds.Native.dll', 'exceptionCode': '0xc0000005', 'faultOffset': '0x000000000055e625'}}
        g = self.world1(open_siege, state=dict(repro, launches=[launch]), log=log, outputs={'freshMapAfterMission': False})
        self.assertEqual([(c['step'], c['fault']) for c in g.crashes], [('w1.siege', '0xc0000005 in TaleWorlds.Native.dll+0x000000000055e625')])
        _, sv = g.need('world1', 'w1.siege')
        self.assertEqual(sv.status, S.FAIL)
        self.assertIn('crashed', sv.detail[0])
        self.assertEqual(g.no_freeze().status, S.FAIL)
        out = self.root / 'acceptance.json'
        sys.argv = ['summarize', '--run', str(self.root / 'run'), '--out', str(out)]
        self.assertEqual(S.main(), 0)
        doc = json.loads(out.read_text(encoding='utf-8'))
        self.assertEqual(doc['crashes'][0]['step'], 'w1.siege')
        self.assertEqual(next(r for r in doc['criteria'] if r['id'] == 'c32')['status'], S.FAIL)
        # The hang repro's own launches are not the run's crashes.
        lab = dict(launch, phase='hangrepro')
        self.assertEqual(self.world1(open_siege, state=dict(repro, launches=[lab]), outputs={'freshMapAfterMission': False}).crashes, [])


class Map2AcceptanceFixBatch(unittest.TestCase):
    """MMR-247 F (the acc6 fix batch): c21 graded on the cache build a reused start's caches came from, the committed attestations and the
    balance pass (c28), and acc6 re-graded where its run folder is present."""
    KEY = '04295bc5ea15e99c6853d724998ac6dc5224c6bef341e4e0885b72f8c0128a9c'
    OWNER = '"Approved: the vein look, the magic ground textures and the magic water look pass my review (c12, c29, c31). Finish the sprint."'

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def start(self, status, seconds=None, key=KEY, estimate=305.1):
        return {'status': 'done', 'seed': 42, 'generateSeconds': 46, 'startSeconds': 29, 'preview': {'estimateSeconds': estimate, 'settlements': 258, 'countsText': 'about 6 minutes'},
                'cacheBuild': {'status': status, 'generateSeconds': seconds, 'record': {'keySha256': key}}}

    def make_run(self, name, phase, pid, steps, started, more=()):
        """A run folder build/acceptance/<name> of one launch (plus `more`: (phase, pid, steps, started) launches)."""
        folder = self.root / 'acceptance' / name
        (folder / 'in-game').mkdir(parents=True, exist_ok=True)
        launches = []
        for ph, p, st, at in [(phase, pid, steps, started)] + list(more):
            write(folder / ('in-game/map2_acceptance-%d.json' % p), {'processId': p, 'status': 'ok', 'outputs': {'steps': st, 'shots': [], 'completed': True}})
            launches.append({'phase': ph, 'pid': p, 'started': at, 'result': 'in-game/map2_acceptance-%d.json' % p})
        write(folder / 'state.json', {'runId': name, 'phases': {}, 'hashes': [], 'launches': launches})
        return folder

    def test_a_reused_start_is_graded_on_the_build_that_made_its_caches(self):
        """acc6: world 1's start reused the caches acc6lab built for the same world 26 minutes before (same key); the estimate 305 s is graded
        against acc6lab's 367 s (17%), not failed for a missing measurement. Another key, or no sibling run, still fails."""
        self.make_run('lab', 'decallab', 30672, {'w1.start': self.start('generated', 366.6)}, '2026-10-21T15:09:24Z')
        world2 = dict(self.start('generated', 778.9, key='b' * 64, estimate=707.7), seed=73)
        run = self.make_run('acc', 'world1', 4996, {'w1.start': self.start('reused')}, '2026-10-21T15:35:19Z', [('world2', 24340, {'w2.start': world2}, '2026-10-21T16:07:28Z')])
        g = S.Grader(S.Run(str(run), {}))
        v = g.estimates()
        self.assertEqual(v.status, S.PASS, v.detail)
        world1 = next(d for d in v.detail if d.startswith('world1'))
        self.assertIn('estimate 708 s vs measured 779 s (9%', ' '.join(v.detail))
        self.assertIn('estimate 305 s vs measured 367 s (17%', world1)
        self.assertIn('run lab', world1)
        self.assertEqual(g.generate().status, S.PASS, g.generate().detail)
        self.assertIn('built in 367 s by run lab', g.generate().detail[0])
        self.assertEqual(S.Grader(S.Run(str(run), {}, builds_from=[])).generate().status, S.FAIL)
        other = self.make_run('acc2', 'world1', 5000, {'w1.start': self.start('reused', key='f' * 64)}, '2026-10-21T16:00:00Z')
        self.assertIn('no run measured a build', ' '.join(S.Grader(S.Run(str(other), {})).estimates().detail))
        # A build of the run's own (an earlier launch) comes first; an off estimate fails on the carried build too.
        self.make_run('lab2', 'decallab', 1, {'w1.start': self.start('generated', 100.0)}, '2026-10-21T15:10:00Z')
        far = self.make_run('acc3', 'world1', 7, {'w1.start': self.start('reused', estimate=305.1)}, '2026-10-21T15:40:00Z')
        self.assertIn('vs measured 100 s', ' '.join(S.Grader(S.Run(str(far), {}, builds_from=[str(self.root / 'acceptance' / 'lab2')])).estimates().detail))

    def test_the_committed_attestations_and_a_runs_own(self):
        """The committed attestations carry the owner's approval (quoted exactly) for c12, c29, c31 and MMR-262's review pass for c15; they are
        read by default, a run's own attestations.json replaces a key, and --attest names the files instead."""
        doc = json.loads((REPO / 'evidence/map-epic-2/attestations.json').read_text(encoding='utf-8'))
        for key in ('c12', 'c29', 'c31'):
            self.assertEqual((doc[key]['status'], doc[key]['note']), (S.PASS, self.OWNER))
            self.assertIn('owner', doc[key]['by'])
        self.assertEqual(doc['c15']['status'], S.PASS)
        self.assertIn('MMR-262', doc['c15']['by'])
        folder = run_folder(self.root)
        out = self.root / 'acceptance.json'
        sys.argv = ['summarize', '--run', str(folder), '--out', str(out)]
        self.assertEqual(S.main(), 0)
        graded = json.loads(out.read_text(encoding='utf-8'))
        self.assertEqual(next(r for r in graded['criteria'] if r['id'] == 'c15')['status'], S.PASS)
        self.assertEqual(graded['attestations'], ['evidence/map-epic-2/attestations.json'])
        write(folder / 'attestations.json', {'c15': {'status': 'fail', 'by': 'test', 'note': 'replaced'}})
        self.assertEqual(S.main(), 0)
        graded = json.loads(out.read_text(encoding='utf-8'))
        self.assertEqual(next(r for r in graded['criteria'] if r['id'] == 'c15')['status'], S.FAIL)
        self.assertEqual(len(graded['attestations']), 2)
        sys.argv = ['summarize', '--run', str(folder), '--out', str(out), '--attest', str(self.root / 'none.json')]
        self.assertEqual(S.main(), 0)
        self.assertEqual(next(r for r in json.loads(out.read_text(encoding='utf-8'))['criteria'] if r['id'] == 'c15')['status'], S.BLOCKED)

    def soak(self, home, balance=None, attest=None):
        folder = self.root / 'soak'
        (folder / 'in-game').mkdir(parents=True, exist_ok=True)
        econ = {'meanTownProsperity': 1.0}
        write(folder / 'in-game/map2_acceptance-1.json', {'processId': 1, 'status': 'ok', 'outputs': {'steps': {'base.soak': {'status': 'done', 'economyBefore': econ, 'economyAfter': econ}}}})
        write(folder / 'in-game/map2_acceptance-2.json', {'processId': 2, 'status': 'ok', 'outputs': {'steps': {
            'w1.soak': {'status': 'done', 'economyBefore': econ, 'economyAfter': econ, 'homeTerrain': {c: {'settlements': 1, 'faces': {'Forest': 3}} for c in home}}}}})
        write(folder / 'state.json', {'runId': 'soak', 'phases': {}, 'hashes': [], 'launches': [{'phase': 'baseline', 'pid': 1, 'result': 'in-game/map2_acceptance-1.json'},
                                                                                           {'phase': 'world1', 'pid': 2, 'result': 'in-game/map2_acceptance-2.json'}]})
        return S.Grader(S.Run(str(folder), attest or {}, balance=balance)).economy()

    def test_the_balance_pass_is_read_from_its_review(self):
        """c28's balance part: the committed review (acc6) records a decision, observation and reason for the Snow, Swamp and road terms, the
        trade-area economy and each culture acc6's soak saw; it passes with them, names a culture it lacks, and is not recorded without the file."""
        acc6 = ['aserai', 'battania', 'empire', 'khuzait', 'nord', 'sturgia', 'vlandia']
        v = self.soak(acc6)
        self.assertEqual(v.status, S.PASS, v.detail)
        self.assertIn('no policy change', ' '.join(v.detail))
        self.assertIn('home.khuzait: none', ' '.join(v.detail))
        review = json.loads((REPO / 'evidence/map-epic-2/balance-pass.json').read_text(encoding='utf-8'))
        self.assertEqual(review['policyChanges'], [])
        self.assertTrue(all(i['decision'] == 'none' for i in review['items']))
        v = self.soak(acc6 + ['nordic_new'])
        self.assertEqual(v.status, S.BLOCKED)
        self.assertIn('home.nordic_new', v.detail[-1])
        self.assertEqual(self.soak(acc6, balance=str(self.root / 'missing.json')).status, S.BLOCKED)
        partial = dict(review, items=[dict(i, reason='') if i['id'] == 'speed.road' else i for i in review['items']])
        write(self.root / 'partial.json', partial)
        v = self.soak(acc6, balance=str(self.root / 'partial.json'))
        self.assertEqual(v.status, S.BLOCKED)
        self.assertIn("'speed.road'", v.detail[-1])
        self.assertEqual(self.soak(acc6, balance=str(self.root / 'missing.json'), attest={'balance': {'by': 'fix batch', 'note': 'none'}}).status, S.PASS)

    def test_acc6_regraded(self):
        """acc6 itself (the integration worktree's run folder, git-ignored; skipped where absent): c8, c9 and c21 pass with the fixes; c28 passes
        with the committed balance pass; c12, c15, c29, c31 pass on the committed attestations; c11 still fails (the siege case, a probe fix
        that needs a live run)."""
        if not (ACC6 / 'state.json').exists() or not (ACC6.parent / 'acc6lab' / 'state.json').exists():
            self.skipTest('acc6 and acc6lab are not present')
        try:
            import PIL  # noqa: F401
        except ImportError:
            self.skipTest('Pillow is not installed')
        out = self.root / 'acceptance.json'
        sys.argv = ['summarize', '--run', str(ACC6), '--out', str(out)]
        self.assertEqual(S.main(), 0)
        status = {r['id']: r for r in json.loads(out.read_text(encoding='utf-8'))['criteria']}
        for c in ('c8', 'c9', 'c12', 'c15', 'c21', 'c28', 'c29', 'c31'):
            with self.subTest(criterion=c):
                self.assertEqual(status[c]['status'], S.PASS, status[c]['detail'])
        self.assertIn('acc6lab', ' '.join(status['c21']['detail']))
        self.assertEqual(status['c11']['status'], S.FAIL)


if __name__ == '__main__':
    unittest.main()
