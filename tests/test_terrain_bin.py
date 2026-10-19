"""Tests for tools/scenebin/terrain_bin.py (Bannerlord terrain.bin reader/writer).

Run:  python tests/test_terrain_bin.py            (unittest, verbose)
Installed-file tests use the game at MNBL_GAME_DIR (default: the Steam path
below) and are skipped when it is absent.  They only read official files.
"""
import array
import glob
import math
import os
import random
import struct
import sys
import tempfile
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools", "scenebin"))
import terrain_bin as tb  # noqa: E402

GAME = os.environ.get("MNBL_GAME_DIR", r"D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord")
MODULES = os.path.join(GAME, "Modules")
HAVE_GAME = os.path.isdir(MODULES)
try:
    import PIL  # noqa: F401
    HAVE_PIL = True
except Exception:
    HAVE_PIL = False


def installed_terrain_files():
    return sorted(glob.glob(os.path.join(MODULES, "*", "SceneObj", "*", "terrain.bin")))


def analytic(x, y):
    return 30.0 + 18.0 * math.sin(x / 70.0) * math.cos(y / 55.0) + 0.002 * x * y / 10.0


def analytic_grad(x, y):
    dx = 18.0 / 70.0 * math.cos(x / 70.0) * math.cos(y / 55.0) + 0.0002 * y
    dy = -18.0 / 55.0 * math.sin(x / 70.0) * math.sin(y / 55.0) + 0.0002 * x
    return dx, dy


class Lz4Tests(unittest.TestCase):
    def check(self, data):
        for comp in (tb.lz4_compress(data), tb.lz4_store(data)):
            self.assertEqual(tb.lz4_decompress(comp, len(data)), data)
            if len(data) >= 13:
                # end-of-block rules: decoding must end on a literal-only sequence of >= 5 bytes
                self.assertGreaterEqual(len(comp), 6)

    def test_edge_sizes_and_patterns(self):
        rnd = random.Random(7)
        for n in list(range(0, 40)) + [255, 256, 270, 1000, 65536 + 17]:
            self.check(bytes(rnd.getrandbits(8) for _ in range(n)))
            self.check(b"\x00" * n)
            self.check((b"abc" * (n // 3 + 1))[:n])

    def test_long_matches_and_far_offsets(self):
        rnd = random.Random(3)
        block = bytes(rnd.getrandbits(8) for _ in range(70000))
        self.check(block + block[:5000] + b"\xff" * 100000 + block[-3000:])

    def test_decompress_rejects_corruption(self):
        with self.assertRaises(tb.TerrainFormatError):
            tb.lz4_decompress(b"\x1f\x41\x05\x00", None)  # offset beyond output
        with self.assertRaises(tb.TerrainFormatError):
            tb.lz4_decompress(tb.lz4_compress(b"x" * 100), 99)


class PngTests(unittest.TestCase):
    def test_encode_decode_paths(self):
        rnd = random.Random(1)
        cases = [(65, 65, 16, 0), (17, 9, 8, 0), (33, 33, 8, 6)]
        for w, h, bd, ct in cases:
            ch = {0: 1, 6: 4}[ct]
            if bd == 16:
                vals = [rnd.randrange(65536) for _ in range(w * h)]
                raw = struct.pack(">%dH" % len(vals), *vals)
            else:
                raw = bytes(rnd.getrandbits(8) for _ in range(w * h * ch))
                vals = list(raw)
            for ft in (0, 2):
                png = tb.png_encode(w, h, bd, ct, raw, filter_type=ft)
                self.assertEqual(tb.png_header(png)[:4], (w, h, bd, ct))
                paths = [False] + ([True] if HAVE_PIL else [])
                for use_pil in paths:
                    W, H, C, samples = tb.png_decode(png, use_pillow=use_pil)
                    self.assertEqual((W, H), (w, h))
                    self.assertEqual(list(samples), vals, "filter %d pillow %s" % (ft, use_pil))


def make_grid(nx, ny, cells, node_size):
    W, H = nx * cells + 1, ny * cells + 1
    sp = node_size / cells
    return [[analytic(c * sp, r * sp) for c in range(W)] for r in range(H)], sp


class BuilderTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.nx, cls.ny, cls.cells, cls.node_size = 3, 2, 32, 80.0
        cls.heights, cls.sp = make_grid(cls.nx, cls.ny, cls.cells, cls.node_size)
        H, W = len(cls.heights), len(cls.heights[0])
        # layer 1 covers x < 100 m, layer 2 a diagonal band
        cls.w1 = [[255 if c * cls.sp < 100.0 else 0 for c in range(W)] for r in range(H)]
        cls.w2 = [[200 if abs(c - r) < 6 else 0 for c in range(W)] for r in range(H)]
        cls.data, cls.info = tb.build_terrain(cls.heights, cls.nx, cls.ny, cls.node_size,
                                              layer_weights={1: cls.w1, 2: cls.w2})
        cls.model = tb.parse(cls.data)

    def test_container_roundtrip_and_layout(self):
        self.assertEqual(self.data[:8], b"ZGR6RTRN")
        self.assertEqual(self.model.serialize(), self.data)
        self.assertEqual([s.tag for s in self.model.sections], list(tb.SECTION_TAGS))
        self.assertEqual(self.model.node_count, self.nx * self.ny)
        self.assertEqual(self.model.grid_shape(), (self.nx, self.ny))
        self.assertEqual(self.info["heightmap_level"], 5)
        for p in self.model.midx_images():
            self.assertEqual(tb.png_header(p)[:4], (self.nx * 256, self.ny * 256, 8, 0))

    def test_heights_within_quantisation(self):
        lo, hi = self.info["min_height"], self.info["max_height"]
        grid = tb.decode_heights(self.model, lo, hi)
        self.assertEqual((len(grid), len(grid[0])), (len(self.heights), len(self.heights[0])))
        q = (hi - lo) / 65535.0
        err = max(abs(a - b) for ra, rb in zip(grid, self.heights) for a, b in zip(ra, rb))
        self.assertLessEqual(err, q / 2 + 1e-9)

    def test_shared_edges_identical(self):
        recs = self.model.nodes()
        maps = [tb._node_heights(r.height_png) for r in recs]
        for i, (w, h, a) in enumerate(maps):
            x, y = i % self.nx, i // self.nx
            if x + 1 < self.nx:
                b = maps[i + 1][2]
                self.assertTrue(all(a[r * w + w - 1] == b[r * w] for r in range(w)))
            if y + 1 < self.ny:
                b = maps[i + self.nx][2]
                self.assertEqual(a[(w - 1) * w:], b[:w])

    def test_normals_match_analytic(self):
        recs = self.model.nodes()
        R = self.cells + 1
        W, H = len(self.heights[0]), len(self.heights)
        worst = 0.0
        worst_interior = 0.0
        for i, rec in enumerate(recs):
            ix, iy = i % self.nx, i // self.nx
            n = tb.decode_normals(rec.normal_png)
            for r in range(R):
                for c in range(R):
                    gc, gr = ix * self.cells + c, iy * self.cells + r
                    gx, gy = analytic_grad(gc * self.sp, gr * self.sp)
                    L = math.sqrt(gx * gx + gy * gy + 1)
                    e = max(abs(n[r * R + c][0] + gx / L), abs(n[r * R + c][1] + gy / L))
                    worst = max(worst, e)
                    if 0 < gc < W - 1 and 0 < gr < H - 1:
                        worst_interior = max(worst_interior, e)
        # interior: 8-bit bucket half-width (0.0039) + Sobel discretisation;
        # outer border: one-sided differences (first order) but correctly scaled
        self.assertLess(worst_interior, 0.01)
        self.assertLess(worst, 0.04)
        # flat terrain encodes as (127, 127, 63, 0) like official files
        flat, _ = tb.build_terrain([[5.0] * 9] * 9, 2, 2, 10.0)
        px = tb.png_decode(tb.parse(flat).nodes()[0].normal_png)[3]
        self.assertEqual(set(zip(px[0::4], px[1::4], px[2::4], px[3::4])), {(127, 127, 63, 0)})

    def test_weights_and_physics(self):
        recs = self.model.nodes()
        R = self.cells + 1
        W = len(self.heights[0])
        for i, rec in enumerate(recs):
            ix, iy = i % self.nx, i // self.nx
            self.assertEqual(rec.weight_flags.count(1), sum(1 for w in rec.weights if w is not None))
            self.assertIsNotNone(rec.weights[0])
            self.assertEqual(sum(1 for w in rec.weights if w is not None), bin(self.info["nodes"][i]["layer_mask"]).count("1"))
            self.assertEqual([s for s, _m in rec.physics], [0, 1, 2, 3])
            phys = rec.physics[0][1]
            self.assertEqual(len(phys), R * R)
            w1 = tb.png_decode(rec.weights[1])[3] if rec.weights[1] else bytes(R * R)
            for r in range(R):
                for c in range(R):
                    gr, gc = iy * self.cells + r, ix * self.cells + c
                    if self.w1[gr][gc]:
                        self.assertEqual(w1[r * R + c], 255)
                    # PHYM is x-major: index = column * R + row
                    p = phys[c * R + r]
                    if self.w2[gr][gc] >= 128:
                        self.assertEqual(p, 2)
                    elif self.w1[gr][gc]:
                        self.assertEqual(p, 1)
                    else:
                        self.assertEqual(p, 0)
        self.assertEqual(W, self.nx * self.cells + 1)

    def test_physics_layer_selection_and_explicit_seasons(self):
        data, _ = tb.build_terrain(self.heights, self.nx, self.ny, self.node_size,
                                   layer_weights={1: self.w1, 2: self.w2}, physics_layers=[1])
        vals = set()
        for rec in tb.parse(data).nodes():
            vals.update(rec.physics[0][1])
        self.assertEqual(vals, {0, 1})
        H, W = len(self.heights), len(self.heights[0])
        seasons = [[[s] * W for _ in range(H)] for s in range(4)]
        data, _ = tb.build_terrain(self.heights, self.nx, self.ny, self.node_size, physics=seasons)
        rec = tb.parse(data).nodes()[0]
        self.assertEqual([set(m) for _s, m in rec.physics], [{0}, {1}, {2}, {3}])

    def test_empty_normals_weights_layout(self):
        data, info = tb.build_terrain(self.heights, self.nx, self.ny, self.node_size,
                                      write_normals=False, write_weights=False)
        m = tb.parse(data)
        self.assertEqual(m.serialize(), data)
        self.assertIsNone(m.section("NRML").blob)
        self.assertIsNone(m.section("WGHT").blob)
        self.assertEqual(m.section("NRML").offset, m.section("PHYM").offset)
        recs = m.nodes()
        self.assertTrue(all(r.normal_png is None for r in recs))

    def test_rejects_bad_grids(self):
        with self.assertRaises(ValueError):
            tb.build_terrain([[0.0] * 10] * 10, 1, 1, 10.0)        # 9 cells: not a power of two
        with self.assertRaises(ValueError):
            tb.build_terrain([[0.0] * 9] * 17, 1, 1, 10.0)         # non-square nodes
        with self.assertRaises(ValueError):
            tb.build_terrain([[float("nan")] * 5] * 5, 1, 1, 10.0)

    def test_nodes_xml(self):
        xml = tb.nodes_xml(self.info)
        self.assertEqual(xml.count("<node "), self.nx * self.ny)
        self.assertIn('name="heightmap_level" value="5"', xml)


@unittest.skipUnless(HAVE_GAME, "Bannerlord install not found (set MNBL_GAME_DIR)")
class InstalledFileTests(unittest.TestCase):
    def test_every_installed_file_roundtrips_byte_identical(self):
        files = installed_terrain_files()
        self.assertGreater(len(files), 0)
        failed = []
        for f in files:
            with open(f, "rb") as fh:
                d = fh.read()
            if tb.parse(d).serialize() != d:
                failed.append(f)
        print("\n  installed terrain.bin: %d files, %d identical, %d failed" % (len(files), len(files) - len(failed), len(failed)))
        self.assertEqual(failed, [])

    def _scene(self, rel):
        d = os.path.join(MODULES, rel)
        if not os.path.exists(os.path.join(d, "terrain.bin")):
            self.skipTest(rel + " missing")
        with open(os.path.join(d, "terrain.bin"), "rb") as fh:
            m = tb.parse(fh.read())
        return m, tb.read_scene_terrain(os.path.join(d, "scene.xscene"))

    def test_battle_terrain_001_decode(self):
        m, st = self._scene(os.path.join("SandBoxCore", "SceneObj", "battle_terrain_001"))
        self.assertEqual(m.version, 6)
        self.assertEqual(m.grid_shape(), (st["node_dimension_x"], st["node_dimension_y"]))
        grid = tb.decode_heights(m, st["min_height"], st["max_height"])
        self.assertEqual((len(grid[0]), len(grid)), (5 * 64 + 1, 8 * 64 + 1))
        flat = [v for row in grid for v in row]
        self.assertGreaterEqual(min(flat), st["min_height"])
        self.assertLessEqual(max(flat), st["max_height"])
        # per-node extremes agree with the xscene node statistics to < 1 m
        sc = (st["max_height"] - st["min_height"]) / 65535.0
        xn = {(int(n["idx"]), int(n["idy"])): n for n in st["nodes"]}
        for (x, y), rec in zip(m.node_grid_positions(), m.nodes()):
            w, h, a = tb._node_heights(rec.height_png)
            nd = xn[(x, y)]
            self.assertLess(abs(st["min_height"] + min(a) * sc - float(nd["min_height"])), 1.0)
            self.assertLess(abs(st["min_height"] + max(a) * sc - float(nd["max_height"])), 1.0)
        # blob sizes and directory agree; reserved/stamp fields are not validated by anything we know
        for s in m.sections:
            self.assertEqual(len(s.blob.data()), s.usize)

    def test_main_map_extremes_equal_scene_range(self):
        m, st = self._scene(os.path.join("SandBox", "SceneObj", "Main_map"))
        W, H, raw = tb.decode_raw_heights(m)
        self.assertEqual((W, H), (16 * 256 + 1, 16 * 256 + 1))
        self.assertEqual(max(raw), 65535)  # max code == max_height exactly (divisor 65535)
        sc = (st["max_height"] - st["min_height"]) / 65535.0
        self.assertLess(abs(st["min_height"] + min(raw) * sc - st["min_height"]), 0.0015)

    def test_zgr4_and_zgr5_nodes_parse(self):
        seen = set()
        for f in installed_terrain_files():
            with open(f, "rb") as fh:
                head = fh.read(4)
            v = {b"ZGR4": 4, b"ZGR5": 5}.get(head)
            if v is None or v in seen:
                continue
            with open(f, "rb") as fh:
                m = tb.parse(fh.read())
            st = tb.read_scene_terrain(os.path.join(os.path.dirname(f), "scene.xscene"))
            self.assertEqual(m.grid_shape(), (st["node_dimension_x"], st["node_dimension_y"]))
            recs = m.nodes()
            self.assertEqual(len(recs), st["node_dimension_x"] * st["node_dimension_y"])
            seen.add(v)
        self.assertEqual(seen, {4, 5})

    def test_lz4_compressor_on_official_section(self):
        m, st = self._scene(os.path.join("SandBoxCore", "SceneObj", "battle_terrain_001"))
        for s in m.sections:
            raw = s.blob.data()
            self.assertEqual(tb.lz4_decompress(tb.lz4_compress(raw), len(raw)), raw)


if __name__ == "__main__":
    unittest.main(verbosity=2)
