"""Targeted checks for MMR-290's magic water art: the committed atlas sources (tools/art/magic_water.py) against their evidence and
the runtime policy (worldgen/policies/roads.json magicWater), and the published-pack header check's water spec.

Run:  python tests/test_magic_water.py      (the source cases skip on LFS pointers)
"""
import hashlib
import json
import os
import sys
import tempfile
import unittest

from PIL import Image, ImageChops

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, 'tools', 'art'))
import check_magic_packs as cmp  # noqa: E402
import magic_water as mw  # noqa: E402

SOURCES = os.path.join(ROOT, 'Modules', 'MNBL.ProceduralWorld', 'AssetSources', 'magic_water')
EVIDENCE = os.path.join(ROOT, 'evidence', 'art', 'magic-water.json')


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def real(path):
    """False for a Git LFS pointer (the sources were not pulled)."""
    with open(path, 'rb') as f:
        return f.read(8) == b'\x89PNG\r\n\x1a\n'


class MagicWater(unittest.TestCase):
    def setUp(self):
        with open(EVIDENCE, encoding='utf-8') as f:
            self.ev = json.load(f)
        with open(os.path.join(ROOT, 'worldgen', 'policies', 'roads.json'), encoding='utf-8') as f:
            self.policy = json.load(f)['magicWater']

    def test_policy_matches_the_atlas(self):
        atlas = self.policy['atlas']
        self.assertEqual(self.policy['material'], 'mnbl_magicwater')
        self.assertEqual([atlas['columns'], atlas['rows']], [len(mw.COLUMNS), len(mw.SCHOOLS)])
        self.assertEqual(sorted(atlas['schoolRows'], key=atlas['schoolRows'].get), mw.SCHOOLS)
        self.assertEqual(atlas['widthScale'], mw.WIDTH_SCALE)
        self.assertEqual(self.ev['atlas']['rows'], mw.SCHOOLS)
        self.assertTrue(self.ev['passed'])
        self.assertEqual(self.ev['failures'], [])

    def test_sources_match_the_evidence(self):
        for suffix, rec in self.ev['files'].items():
            path = os.path.join(SOURCES, rec['file'])
            if not real(path):
                self.skipTest('LFS pointer: ' + path)
            self.assertEqual(sha(path), rec['sha256'], path)
            with Image.open(path) as im:
                self.assertEqual(list(im.size), [mw.ATLAS, mw.ATLAS])
                self.assertEqual(im.mode, rec['mode'])

    def test_margins_clear_and_mirrored(self):
        path = os.path.join(SOURCES, 'mnbl_magicwater_d.png')
        if not real(path):
            self.skipTest('LFS pointer')
        with Image.open(path) as im:
            a = im.getchannel('A')
        for r in range(mw.ROWS):
            for c in range(mw.COLS):
                cell = a.crop((c * mw.CELL_W, r * mw.CELL_H, (c + 1) * mw.CELL_W, (r + 1) * mw.CELL_H))
                inner = cell.crop((mw.MARGIN, mw.MARGIN, mw.CELL_W - mw.MARGIN, mw.CELL_H - mw.MARGIN))
                self.assertGreater(inner.getextrema()[1], 200, (r, c))
                blank = Image.new('L', cell.size)
                blank.paste(inner, (mw.MARGIN, mw.MARGIN))
                self.assertEqual(ImageChops.difference(cell, blank).getextrema()[1], 0, ('margin', r, c))
                self.assertLessEqual(ImageChops.difference(cell, cell.transpose(Image.Transpose.FLIP_TOP_BOTTOM)).getextrema()[1], 2, ('symmetry', r, c))

    def test_header_check_reports_an_unpublished_water_material(self):
        with tempfile.TemporaryDirectory() as d:
            res = cmp.check_water(d, 'MNBL.ProceduralWorld')
        self.assertFalse(res['passed'])
        self.assertEqual(res['packs'], [])


if __name__ == '__main__':
    unittest.main()
