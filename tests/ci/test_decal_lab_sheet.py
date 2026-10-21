"""MMR-247 (D): tools/decal_lab_sheet.py on a synthetic decal lab result, without the game: a lab decal whose window differs between the shot
with the lab drawn and the shot with it hidden counts as drawn, one whose window does not counts as not drawn, whichever vertical sense the
viewport points use (read from the grid: rows further north sit higher at bearing 0); the labelled copies and the sheet are written.
"""
import json
import sys
import tempfile
import unittest
from pathlib import Path

from PIL import Image, ImageDraw

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / 'tools'))
import decal_lab_sheet as L  # noqa: E402


def lab_run(folder, y_up):
    """A result JSON and its four land shots: decal a/full (south) drawn as a bright square on the map shot, decal b/full (north) not drawn."""
    pid = 4242
    layout = [{'id': 'a/full', 'column': 'a', 'row': 'full', 'offset': [0, -5]}, {'id': 'b/full', 'column': 'b', 'row': 'full', 'offset': [0, 5]}]
    # pixel targets: a at (50, 70), b at (150, 30) on a 200 x 100 shot (north higher on the screen)
    def vp(px, py):
        return [px / 200, (1 - py / 100) if y_up else py / 100, 0.5]
    viewport = {'a/full': vp(50, 70), 'b/full': vp(150, 30)}
    shots = []
    for view in ('close', 'map', 'far', 'hidden'):
        im = Image.new('RGB', (200, 100), (90, 80, 60))
        if view != 'hidden':
            ImageDraw.Draw(im).rectangle((45, 65, 55, 75), fill=(30, 60, 200))
        label = 'lab-land-' + view
        im.save(folder / ('map2_acceptance-%d-shot-%s.bmp' % (pid, label)))
        shots.append({'step': 'w1.decalLab', 'label': label, 'path': None, 'viewport': viewport})
    doc = {'processId': pid, 'outputs': {'shots': shots, 'steps': {'w1.decalLab': {'status': 'done', 'layout': layout, 'columns': ['a', 'b'], 'rows': ['full'],
                                                                                  'spots': {'land': [10, 20]}}}}}
    path = folder / ('map2_acceptance-%d.json' % pid)
    path.write_text(json.dumps(doc), encoding='utf-8')
    return path


class DecalLabSheetTests(unittest.TestCase):
    def test_drawn_and_not_drawn(self):
        for y_up in (False, True):
            with tempfile.TemporaryDirectory() as tmp:
                folder = Path(tmp)
                result = lab_run(folder, y_up)
                out = folder / 'lab'
                self.assertEqual(L.main([str(result), '--out', str(out)]), 0)
                report = json.loads((out / 'decal-lab.json').read_text(encoding='utf-8'))
                decals = report['spots']['land']['decals']
                self.assertTrue(decals['a/full']['drawn'], (y_up, decals))
                self.assertFalse(decals['b/full']['drawn'], (y_up, decals))
                self.assertTrue((out / 'lab-land-map.png').exists() and (out / 'lab-land-sheet.png').exists())

    def test_mesh_grid(self):
        """MMR-291: the overlay mesh grid's shots (lab-land-mesh-<view>) are read as spot land-mesh, with the land spot's place."""
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            result = lab_run(folder, False)
            doc = json.loads(result.read_text(encoding='utf-8'))
            lab = doc['outputs']['steps']['w1.decalLab']
            lab['layout'] += [{'id': 'mesh:vein/cell', 'column': 'mesh:vein', 'row': 'full', 'offset': [0, -5], 'mesh': 'veins'},
                              {'id': 'mesh:river/cell', 'column': 'mesh:river', 'row': 'full', 'offset': [0, 5], 'mesh': 'rivers'}]
            lab['columns'] += ['mesh:vein', 'mesh:river']
            lab['meshRows'] = ['cell']   # MMR-291-W: the mesh grid's own rows (the passes) and the policy's passes
            lab['overlayMeshes'] = {'policy': {'passes': {'veins': 'blend', 'magicWater': 'depth', 'alphaTest': 0.3}}}
            viewport = {'mesh:vein/cell': [0.25, 0.7, 0.5], 'mesh:river/cell': [0.75, 0.3, 0.5]}
            for view in ('close', 'map', 'far', 'hidden'):
                im = Image.new('RGB', (200, 100), (90, 80, 60))
                if view != 'hidden':
                    ImageDraw.Draw(im).rectangle((145, 25, 155, 35), fill=(20, 90, 140))   # the river piece draws, the vein does not
                label = 'lab-land-mesh-' + view
                im.save(folder / ('map2_acceptance-%d-shot-%s.bmp' % (doc['processId'], label)))
                doc['outputs']['shots'].append({'step': 'w1.decalLab', 'label': label, 'path': None, 'viewport': viewport})
            result.write_text(json.dumps(doc), encoding='utf-8')
            out = folder / 'lab'
            self.assertEqual(L.main([str(result), '--out', str(out)]), 0)
            report = json.loads((out / 'decal-lab.json').read_text(encoding='utf-8'))
            mesh = report['spots']['land-mesh']
            self.assertEqual(mesh['at'], [10, 20])
            self.assertTrue(mesh['decals']['mesh:river/cell']['drawn'], mesh)
            self.assertFalse(mesh['decals']['mesh:vein/cell']['drawn'], mesh)
            self.assertTrue(report['spots']['land']['decals']['a/full']['drawn'])
            self.assertEqual(report['meshPasses']['magicWater'], 'depth')
            self.assertEqual(report['meshRows'], ['cell'])


if __name__ == '__main__':
    unittest.main()
