"""Targeted checks for tools/scenebin/build_probe_slots.py (MMR-249 probe fixtures).

Run:  python tests/test_probe_slots.py
"""
import os
import struct
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools", "scenebin"))
import build_probe_slots as bps  # noqa: E402


class FloraTests(unittest.TestCase):
    def test_flr2_layout(self):
        data = bps.flora_bin([("map_spruce_mix", 2, 10.0, 20.0, 3.0, 0.0, 0.25), ("worldmap_tree_beech_01", 0, 1.0, 2.0, 0.5, 1.0, 0.3)])
        self.assertEqual(data[:4], b"FLR2")
        payload, count = struct.unpack_from("<II", data, 4)
        self.assertEqual((payload, count), (len(data) - 8, 2))
        n = struct.unpack_from("<I", data, 12)[0]
        self.assertEqual(data[16:16 + n], b"map_spruce_mix")
        o = 16 + n
        self.assertEqual(struct.unpack_from("<I", data, o)[0], 2)
        m = struct.unpack_from("<16f", data, o + 4)
        self.assertAlmostEqual(m[0], 0.25, places=6)
        self.assertEqual(m[12:16], (10.0, 20.0, 3.0, 1.0))
        self.assertEqual(struct.unpack_from("<4f", data, o + 68), (1.0, 1.0, 1.0, 1.0))
        self.assertEqual(bps.flora_bin([]), b"FLR2" + struct.pack("<II", 4, 0))  # the installed empty flora file


class FieldTests(unittest.TestCase):
    def test_bspline2x_shape_and_constants(self):
        grid = [[3.0] * 5 for _ in range(5)]
        up = bps.bspline2x(grid)
        self.assertEqual((len(up), len(up[0])), (9, 9))
        self.assertTrue(all(abs(v - 3.0) < 1e-12 for row in up for v in row))
        ramp = [[float(c) for c in range(5)] for _ in range(5)]
        self.assertAlmostEqual(bps.bspline2x(ramp)[4][3], 1.5)  # a linear field is reproduced between interior samples

    def test_l6_pattern(self):
        w = bps.l6_weights(257)
        self.assertEqual(w[1][8][24], bps.ramp(24))          # band A ramp
        self.assertEqual(w[2][20][30], 128)                  # band B green
        self.assertEqual(w[3][40][48], 255)                  # band C full at il >= 48
        self.assertEqual(w[5][1][2], 255)                    # 2-on/2-off stripes
        self.assertEqual(w[5][2][2], 0)

    def test_shape_lowers_and_despikes(self):
        grid = [[1.0] * 7 for _ in range(7)]
        grid[3][3] = 20.0
        shaped, spikes = bps.shape(grid)
        self.assertEqual(spikes, 1)
        self.assertLess(max(max(r) for r in shaped), 1.0)


if __name__ == "__main__":
    unittest.main(verbosity=2)
