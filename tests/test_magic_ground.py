"""Targeted checks for the MMR-259 / MMR-288 magic-ground tools: tools/art/tpac_textures.py (pack reader),
tools/art/magic_ground.py (vein-stamp derivation and gates) and tools/art/check_magic_packs.py (published-pack header check),
and the committed texture sources.

Run:  python tests/test_magic_ground.py      (the installed-game cases skip without the game; the source cases skip on LFS pointers)
"""
import io
import json
import os
import struct
import sys
import tempfile
import unittest

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, 'tools', 'art'))
import check_magic_packs as cmp  # noqa: E402
import magic_ground as mg  # noqa: E402
import tpac_textures as tp  # noqa: E402

MODULES = os.path.join(os.environ.get('MNBL_GAME_DIR', r'D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord'), 'Modules')
SOURCES = os.path.join(ROOT, 'Modules', 'MNBL.ProceduralWorld', 'AssetSources', 'magic_ground')


def lenstr(s):
    b = s.encode()
    return struct.pack('<I', len(b)) + b


def synthetic_pack(path, blocks, stored_raw=True):
    """One DXT1 8x8 texture record laid out like the installed packs: a raw low-resolution chunk first, the full chain second."""
    w = h = 8
    full = len(blocks)
    rec = (struct.pack('<I', 0) + lenstr('foo_d') + struct.pack('<IIII', 200, 0, 3, 0) + bytes(16)
           + lenstr('$BASE/Modules/X/AssetSources/t/foo_d.png') + struct.pack('<Q', 0x1234) + bytes(2)
           + struct.pack('<I', 1) + lenstr('for_terrain') + struct.pack('<IB', 2, 2) + struct.pack('<III', w, h, 1)
           + struct.pack('<BH', 1, 1) + lenstr('DXT1') + struct.pack('<I', 1) + lenstr('has_alpha') + lenstr('none') + bytes(9))
    head_len = len(rec) + 4 + 2 * 24 + 16
    data_off = 0x24 + head_len
    table = struct.pack('<I', 2) + struct.pack('<QQQ', data_off + full, 8, 8) + struct.pack('<QQQ', data_off, full, full) + bytes(16)
    hdr = b'TPAC' + struct.pack('<I', 2) + bytes(16) + struct.pack('<III', 1, head_len, 0)
    body = rec + table
    assert len(body) == head_len
    with open(path, 'wb') as f:
        f.write(hdr + body + blocks + bytes(8))


class PackReaderTests(unittest.TestCase):
    def test_lz4_block_with_overlapping_match(self):
        # token: 3 literals, match length 4 + 5 = 9 at offset 3 -> 'abc' repeated
        self.assertEqual(tp.lz4_block(bytes([0x35]) + b'abc' + bytes([3, 0]), 12), b'abcabcabcabc')
        with self.assertRaises(ValueError):
            tp.lz4_block(bytes([0x35]) + b'abc' + bytes([3, 0]), 11)

    def test_raw_chunk_and_full_chain_found_after_a_streaming_chunk(self):
        # two DXT1 blocks per row, 2 x 2 blocks: colour0 white, colour1 black, all indices 0 -> white
        block = struct.pack('<HHI', 0xFFFF, 0x0000, 0)
        with tempfile.TemporaryDirectory() as t:
            p = os.path.join(t, 'x.tpac')
            synthetic_pack(p, block * 4)
            recs = tp.records(p)
            self.assertEqual(len(recs), 1)
            r = recs[0]
            self.assertEqual((r['name'], r['format'], r['width'], r['height'], r['mips']), ('foo_d', 'DXT1', 8, 8, 1))
            self.assertEqual(set(r['flags']), {'for_terrain', 'has_alpha'})
            self.assertEqual(r['chunk'][1], 32)  # the full chain, not the 8-byte streaming chunk listed first
            self.assertEqual(r['chunk'][1], r['chunk'][2])  # stored raw
            im = tp.decode(r)
            self.assertEqual(im.size, (8, 8))
            self.assertEqual(im.convert('RGB').getpixel((3, 5)), (255, 255, 255))

    @unittest.skipUnless(os.path.isdir(MODULES), 'game not installed')
    def test_installed_large_pack_and_raw_chunk(self):
        core = [r for r in tp.records(os.path.join(MODULES, 'Native', 'AssetPackages', 'core.tpac')) if r['name'] == 'snow_a_d']
        self.assertEqual(len(core), 1)
        self.assertEqual((core[0]['format'], core[0]['width']), ('DXT1', 2048))
        im = tp.decode(core[0])
        self.assertGreater(im.convert('L').getextrema()[1], 200)  # snow decodes white, not garbage
        raw = [r for r in tp.records(os.path.join(MODULES, 'Native', 'EmAssetPackages', 'terrain_textures', 'grass', 'ground_drygrass_a',
                                                  'ground_drygrass_a.tpac')) if r['name'] == 'ground_drygrass_a_h']
        self.assertEqual(raw[0]['chunk'][1], raw[0]['chunk'][2])
        self.assertEqual(tp.decode(raw[0]).size, (2048, 2048))


class GateTests(unittest.TestCase):
    def test_ciede2000_reference_pairs(self):
        # Sharma, Wu and Dalal (2005) test data, pairs 1, 7 and 17
        self.assertAlmostEqual(mg.de2000((50, 2.6772, -79.7751), (50, 0, -82.7485)), 2.0425, places=3)
        self.assertAlmostEqual(mg.de2000((50, 0, 0), (50, -1, 2)), 2.3669, places=3)
        self.assertAlmostEqual(mg.de2000((50, 2.5, 0), (73, 25, -18)), 27.1492, places=3)

    def test_margin_window_and_minimum_accent(self):
        w = mg.window()
        im = mg.I(w)
        self.assertEqual(im.getpixel((mg.MARGIN, mg.CELL // 2)), 0.0)
        self.assertEqual(im.getpixel((mg.CELL - mg.MARGIN - 1, mg.CELL // 2)), 0.0)
        self.assertEqual(im.getpixel((mg.CELL // 2, mg.CELL // 2)), 1.0)
        dots = mg.to_l(mg.dots(2.0, 0.0004, 7)).point(lambda v: 255 if v >= 128 else 0)
        box = dots.getbbox()
        self.assertIsNotNone(box)
        # every dot is at least 4 px across: the smallest connected run along a row through a dot
        widths = []
        for y in range(dots.size[1]):
            run = 0
            for x in range(dots.size[0]):
                if dots.getpixel((x, y)):
                    run += 1
                elif run:
                    widths.append(run)
                    run = 0
        self.assertGreaterEqual(max(widths), 4)

    def test_atlas_contract(self):
        self.assertEqual([g['name'] for g in mg.GROUPS], ['arid', 'verdant', 'rock', 'cold'])
        self.assertEqual(mg.COLUMNS, ['trunk', 'fork', 'tip', 'burst'])  # MMR-288 contract C1
        self.assertEqual((mg.ATLAS, mg.CELL, mg.GRID), (2048, 512, 4))
        pol = mg.policy()
        self.assertEqual(sorted(mg.RECIPES), sorted(pol['mutation']['schools']))  # the 8 visible schools of the shared table
        self.assertTrue(set(pol['mutation']['hiddenLooks'].values()) <= set(mg.RECIPES))
        covered = {b for g in mg.GROUPS for b in g['biomes']}
        land = {b['core'] for b in pol['biomes'] if not b['water'] and b['core'] not in ('tundra', 'snow')}
        self.assertEqual(covered, land)  # every generated land core has a row


class SourceTests(unittest.TestCase):
    def setUp(self):
        probe = os.path.join(SOURCES, 'mnbl_magic_weave_d.png')
        if not os.path.isfile(probe) or os.path.getsize(probe) < 4096:
            self.skipTest('texture sources not checked out (git lfs pull)')
        with open(os.path.join(ROOT, 'evidence', 'art', 'magic-veins.json'), encoding='utf-8') as f:
            self.evidence = json.load(f)
        with open(os.path.join(ROOT, 'evidence', 'art', 'magic-textures.json'), encoding='utf-8') as f:
            self.slim = json.load(f)

    def test_sources_match_the_gated_evidence(self):
        self.assertTrue(self.evidence['passed'])
        self.assertEqual(self.evidence['failures'], [])
        self.assertEqual(self.slim['files'], {k: v['files'] for k, v in self.evidence['schools'].items()})
        for school in mg.SCHOOLS:
            for suffix, f in self.evidence['schools'][school]['files'].items():
                path = os.path.join(SOURCES, f['file'])
                self.assertEqual(mg.sha(path), f['sha256'], f['file'])

    def test_every_cell_is_transparent_in_its_margin(self):
        for school in mg.SCHOOLS:
            d = Image.open(os.path.join(SOURCES, 'mnbl_magic_%s_d.png' % school))
            self.assertEqual((d.mode, d.size), ('RGBA', (2048, 2048)))
            a = d.getchannel('A')
            for r in range(4):
                for c in range(4):
                    x, y = c * 512, r * 512
                    for box in ((x, y, x + 512, y + 16), (x, y + 496, x + 512, y + 512), (x, y, x + 16, y + 512), (x + 496, y, x + 512, y + 512)):
                        self.assertEqual(a.crop(box).getextrema()[1], 0, '%s cell %d,%d' % (school, c, r))
                    self.assertGreater(a.crop((x + 128, y + 128, x + 384, y + 384)).getextrema()[1], 200)

    def test_trunks_repeat_over_the_chain_period_and_feather_at_both_ends(self):
        # MMR-288: a trunk's tail block (u 0.8..1) is its head block (u 0..0.2) moved by 0.75 of the box, texel for texel
        for school in mg.SCHOOLS:
            for name in ('d', 'n', 's'):
                im = Image.open(os.path.join(SOURCES, 'mnbl_magic_%s_%s.png' % (school, name))).convert('RGB')
                for r in range(4):
                    y = r * 512
                    head = im.crop((mg.MARGIN, y, mg.MARGIN + mg.OVERLAP, y + 512))
                    tail = im.crop((mg.MARGIN + mg.PERIOD, y, mg.MARGIN + mg.PERIOD + mg.OVERLAP, y + 512))
                    self.assertEqual(head.tobytes(), tail.tobytes(), '%s_%s row %d' % (school, name, r))
            a = Image.open(os.path.join(SOURCES, 'mnbl_magic_%s_d.png' % school)).getchannel('A')
            mid = max(a.crop((256, 236, 257, 276)).tobytes())
            self.assertGreater(mid, 240)  # the vein is opaque mid-stamp
            self.assertLess(max(a.crop((mg.MARGIN + 2, 0, mg.MARGIN + 4, 512)).tobytes()), 40)  # and fades in at u = 0


class VeinGeometryTests(unittest.TestCase):
    STYLE = mg.vein_style('fire')

    def test_feathers_are_complementary_across_a_joint(self):
        _, f = mg.feather('trunk')
        for x in range(mg.MARGIN + mg.PERIOD, mg.CELL - mg.MARGIN):
            self.assertAlmostEqual(f[x] + f[x - mg.PERIOD], 1.0, places=6)
        _, ft = mg.feather('burst')
        self.assertEqual(set(ft), {1.0})

    def test_trunk_line_is_periodic_and_on_axis_at_both_ends(self):
        line = mg.TrunkLine(self.STYLE, 1234)
        for x in range(0, 200, 7):
            self.assertAlmostEqual(line.offset(x), line.offset(x + mg.PERIOD), places=9)
            self.assertGreaterEqual(line.width(x), mg.CORE_W)
            self.assertLessEqual(abs(line.offset(x)), mg.MEANDER_CAP)
        self.assertAlmostEqual(line.offset(mg.ux(0.0)), 0.0, places=9)
        self.assertAlmostEqual(line.offset(mg.ux(1.0)), 0.0, places=9)

    def test_fork_and_burst_skeletons_mirror_about_v_half_and_start_on_the_trunk(self):
        trunk = mg.vein_skeleton('trunk', self.STYLE, 99)['mains'][0]
        fork = mg.vein_skeleton('fork', self.STYLE, 99)['mains']
        head = [p for p in fork[0] if p[0] <= mg.ux(0.2)]
        self.assertEqual(head, [p for p in trunk if p[0] <= mg.ux(0.2)])
        a, b = fork[1], fork[2]
        self.assertEqual(len(a), len(b))
        for (x1, y1, w1), (x2, y2, w2) in zip(a, b):
            self.assertAlmostEqual(x1, x2, places=6)
            self.assertAlmostEqual(y1 - mg.CELL / 2, mg.CELL / 2 - y2, places=6)
        ex = [p for p in a if p[0] >= mg.ux(1.0)][0]
        self.assertAlmostEqual((ex[1] - mg.MARGIN) / mg.INNER, 0.5 + mg.CHILD_V, delta=0.01)
        rays = mg.vein_skeleton('burst', self.STYLE, 99)['mains']
        self.assertEqual(len(rays), 8)
        ys = sorted(round(r[-1][1] - mg.CELL / 2, 3) for r in rays)
        self.assertEqual(ys, sorted(-y for y in ys))

    def test_skeleton_is_deterministic(self):
        for kind in mg.COLUMNS:
            self.assertEqual(mg.vein_skeleton(kind, self.STYLE, 5), mg.vein_skeleton(kind, self.STYLE, 5))


class PublishedPackCheckTests(unittest.TestCase):
    @unittest.skipUnless(os.path.isdir(MODULES), 'game not installed')
    def test_checker_reads_vanilla_decal_material_and_texture(self):
        packs = os.path.join(MODULES, 'Native', 'EmAssetPackages', 'mainmap_decals')
        res = cmp.check(packs, 'Native', 'mainmap_decal_', schools=['undervillage'], atlas=1024)
        rows = {r['name']: r for r in res['rows']}
        self.assertTrue(rows['mainmap_decal_undervillage_d']['found'])
        self.assertEqual(rows['mainmap_decal_undervillage_d']['problems'], [])  # BC7, has_alpha, dont_resize_in_atlas, Native sources
        self.assertFalse(rows['mainmap_decal_undervillage_n']['found'])  # vanilla shares a normal map from elsewhere
        flags = rows['mainmap_decal_undervillage']['flags']
        self.assertEqual(flags, ['gbuffer_alpha_blend', 'use_albedo', 'render_on_terrain', 'use_normalmap'])
        self.assertFalse(res['passed'])

    def test_nothing_published_is_reported(self):
        with tempfile.TemporaryDirectory() as t:
            res = cmp.check(t, 'MNBL.ProceduralWorld', 'mnbl_magic_')
            self.assertEqual(res['packs'], [])
            self.assertFalse(res['passed'])


if __name__ == '__main__':
    unittest.main()
