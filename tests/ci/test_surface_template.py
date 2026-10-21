"""MMR-255: the surface template is generated from tracked source and carries no flora on its weighted layers.

Runs without the game. tools/scenebin/make_surface_template.py generates every substituted value of
Modules/MNBL.ProceduralWorld/SurfaceTemplate/template-recipe.json from worldgen/policies/terrain-materials.json; the tracked recipe must be
exactly what it generates (the derived file hashes need the installed official scene: --check covers those). The generated material
layers are the policy's 15-slot layer set in slot order, with no flora meshes, no flora or mesh-blend layer, every layer flag off, the
same layer in all four seasons, no pale *_mainmap texture, and the terrain carries use_dynamic_weather_effects (never
terrain_render_version 1); there is no road and no school layer.
"""
import json
import re
import sys
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / 'tools' / 'scenebin'))
import make_surface_template as mst  # noqa: E402


def load(rel):
    return json.loads((REPO / rel).read_text(encoding='utf-8-sig'))


class SurfaceTemplate(unittest.TestCase):
    def setUp(self):
        self.policy = load('worldgen/policies/terrain-materials.json')
        self.recipe = load('Modules/MNBL.ProceduralWorld/SurfaceTemplate/template-recipe.json')
        self.layers = ET.fromstring(self.recipe['substitutions']['layers'])

    def test_recipe_is_generated_from_the_policy(self):
        self.assertEqual(self.recipe['version'], 2)
        self.assertEqual(self.recipe['substitutions'], mst.substitutions(self.policy))
        self.assertEqual(self.recipe['policySha256'], mst.sha((REPO / 'worldgen/policies/terrain-materials.json').read_bytes()))

    def test_layers_are_the_policy_set_without_flora(self):
        names = [l.get('name') for l in self.layers]
        self.assertEqual(names, [l['name'] for l in self.policy['layerSet']])
        self.assertEqual(len(names), 15)
        self.assertNotIn('road', ' '.join(names))
        schools = set(self.policy['mutation']['schools']) | set(self.policy['mutation']['hiddenLooks'])
        self.assertFalse(schools & set(names), 'delivery A: no school layers in the template')
        for layer, row in zip(self.layers, self.policy['layerSet']):
            seasons = list(layer)
            self.assertEqual([s.tag for s in seasons], ['summer', 'fall', 'winter', 'spring'])
            first = ET.tostring(seasons[0]).decode().replace('<summer', '<season').replace('</summer>', '</season>').rstrip()
            for s in seasons:
                self.assertEqual(s.get('is_flora_layer'), 'false')
                self.assertEqual(s.get('is_mesh_blend_layer'), 'false')
                self.assertEqual(s.get('is_enabled'), 'true')
                self.assertEqual(s.find('meshes').findall('mesh'), [], 'no flora meshes on a weighted layer')
                self.assertTrue(all(f.get('value') == 'false' for f in s.find('layer_flags')), 'every layer flag off (no slope transparency)')
                self.assertEqual(ET.tostring(s).decode().replace('<%s' % s.tag, '<season').replace('</%s>' % s.tag, '</season>').rstrip(), first, 'the same layer in every season')
                self.assertEqual(s.get('physics_material'), row['physics'])
                self.assertEqual(s.get('scale'), '%.3f, %.3f' % (row['scale'], row['scale']))
                self.assertEqual(s.get('albedo_factor_mode'), str(row['tint']['mode']))
                self.assertEqual(s.get('smooth_blend_amount'), '1.000')
                textures = {t.get('type'): t.get('name') for t in s.find('textures')}
                self.assertEqual(textures['diffusemap'], row['diffuse'])
                self.assertNotRegex(textures['diffusemap'], r'_main_?map', 'MMR-249: the pale *_mainmap textures are not plain layers')
            self.assertGreaterEqual(row['scale'], 15)
            self.assertLessEqual(row['scale'], 20)

    def test_terrain_attributes(self):
        self.assertEqual(self.recipe['substitutions']['terrainAttributes'], {'use_dynamic_weather_effects': 'true'})
        self.assertNotIn('terrain_render_version', json.dumps(self.recipe['substitutions']))
        self.assertEqual(self.recipe['substitutions']['terrain']['vista_tileset'], '')
        nodes = re.findall(r'<node idx', self.recipe['substitutions']['nodes'])
        self.assertEqual(len(nodes), 16)


if __name__ == '__main__':
    unittest.main()
