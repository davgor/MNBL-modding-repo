"""Map epic 2 (MMR-248): the re-pin chain holds, and the shared biome table agrees with every policy that restates one of its columns.

Runs without the game. tools/repin_map2.py --check must find every pin current; terrain-materials.json v2's face, battle-class and
colour-grade columns must equal playability.json cells.landFaceGroups, mission-scenes.json biomeClass and climate-flow.json
colorGrade.biomes; the mutation remap stays inside the land-valid set (no Dune); and the v2 schemas accept their new members.
"""
import json
import subprocess
import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / 'tools'))
from summarize_travel import validate  # noqa: E402


def load(rel):
    return json.loads((REPO / rel).read_text(encoding='utf-8-sig'))


class Map2Policies(unittest.TestCase):
    def setUp(self):
        self.materials = load('worldgen/policies/terrain-materials.json')
        self.rows = {b['core']: b for b in self.materials['biomes']}

    def test_repin_chain_is_current(self):
        run = subprocess.run([sys.executable, str(REPO / 'tools/repin_map2.py'), '--check'], capture_output=True, text=True)
        self.assertEqual(run.returncode, 0, run.stdout + run.stderr)

    def test_face_column_is_playability(self):
        faces = load('worldgen/policies/playability.json')['cells']['landFaceGroups']
        land = {c: r['terrainType'] for c, r in self.rows.items() if not r['water']}
        self.assertEqual(faces['biomes'], land)
        white = self.materials['permanentWhite']
        self.assertEqual((faces['permanentWhite']['snowMonths'], faces['permanentWhite']['snowThreshold'], faces['permanentWhite']['faceGroup']),
                         (white['snowMonths'], white['snowThreshold'], white['terrainType']))

    def test_battle_and_grade_columns(self):
        scenes = load('worldgen/policies/mission-scenes.json')
        self.assertEqual({c: r['battleClass'] for c, r in self.rows.items()}, {c: scenes['biomeClass'][c] for c in self.rows})
        for t in self.materials['mutation']['landValid']:
            self.assertIn(t, scenes['faceClass'], 'a mutated cell takes faceClass[type] (RT-26)')
        grades = load('worldgen/policies/climate-flow.json')['colorGrade']['biomes']
        by_id = {b['id']: b for b in self.materials['biomes']}
        for key, grade in grades.items():
            self.assertEqual(by_id[int(key.split(' ')[0])]['colourGrade'], grade, key)

    def test_mutation_never_dune(self):
        m = self.materials['mutation']
        self.assertNotIn('Dune', m['landValid'])
        for school, t in m['schools'].items():
            self.assertIn(t, m['landValid'], school)
        for look in m['hiddenLooks'].values():
            self.assertIn(look, m['schools'])
        self.assertEqual((m['generatedVariants'], m['battleClass']), ('visual', 'faceClass'))

    def test_one_fief_base(self):
        p = load('worldgen/policies/population.json')
        self.assertEqual(p['base']['fiefs'], 133)
        self.assertEqual(sorted(p['troopTemplates']['cultures']), sorted(p['houses']['lordCultures']))
        self.assertEqual(p['targets']['kingdoms']['maxFiefs'], max(p['base']['fiefsPerKingdom'].values()))

    def test_census_caps_are_herofactory_caps(self):
        # MMR-279: the population census applies HeroFactory's per-settlement notable caps (rosters.json), so it equals what is instantiated.
        caps = load('worldgen/policies/population.json')['census']['notableCaps']
        rosters = load('worldgen/policies/rosters.json')['notables']['caps']
        self.assertEqual({k: caps[k] for k in ('town', 'village')}, rosters)

    def test_schemas_accept_the_new_members(self):
        graph = load('worldgen/contracts/travel-graph.schema.json')
        cell = [3, 4]
        point = {'x': 1.5, 'y': 2.5}
        piece = {'path': [cell, cell], 'start': {'kind': 'gate', 'cell': cell, 'point': point, 'settlement': 'v'},
                 'end': {'kind': 'junction', 'cell': cell, 'point': point, 'road': 3}}
        feeder = {'index': 40, 'kind': 'feeder', 'from': 'v', 'to': 't', 'fromUid': 'uv', 'toUid': 'ut', 'cells': 2, 'pieces': [piece], 'decks': [], 'settlement': 'v'}
        self.assertEqual(validate(graph['properties']['roads'], [feeder], graph), [])
        self.assertNotEqual(validate(graph['properties']['roads'], [{k: v for k, v in feeder.items() if k != 'pieces'}], graph), [])
        bank = dict(piece, end={'kind': 'bank', 'cell': cell, 'point': point})
        self.assertNotEqual(validate(graph['properties']['roads'], [dict(feeder, pieces=[bank])], graph), [], 'a bank end names its water cell')
        campaign = load('worldgen/contracts/campaign.schema.json')
        self.assertEqual(campaign['properties']['version'], {'const': 2})
        for key in ('houses', 'mercenaryCompanies', 'outlawClans', 'wanderers', 'wars', 'villageQuota', 'census'):
            self.assertIn(key, campaign['required'])
        self.assertNotIn('clans', campaign['properties'], 'houses replace the version-1 ruling clans')
        terrain = load('worldgen/contracts/terrain-metadata.schema.json')
        for key in ('layers', 'permanentWhite', 'shaping'):
            self.assertIn(key, terrain['properties'])


if __name__ == '__main__':
    unittest.main()
