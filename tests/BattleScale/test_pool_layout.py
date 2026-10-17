import copy
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools/battle-scale"))
from pool_layout import ExpandedLayout, integer


class LayoutTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.original = json.loads((ROOT / "evidence/engine-extension/native-pool-layout.json").read_text())

    def test_all_slots_and_metadata_disjoint_at_supported_boundaries(self):
        for capacity in (2040, 2041, 2048, 4096, 32766):
            with self.subTest(capacity=capacity):
                layout = ExpandedLayout(self.original, capacity)
                previous_end = 0
                for pool in layout.pools:
                    start, stride, table, counter, high = map(integer, (pool["missionOffset"], pool["elementStride"], pool["indexTableOffset"], pool["counterOffset"], pool["highWaterOffset"]))
                    self.assertGreaterEqual(start, previous_end)
                    for slot in range(capacity):
                        self.assertLessEqual(start + (slot + 1) * stride, table)
                        self.assertLessEqual(table + (slot + 1) * 2, counter)
                    self.assertEqual(start + capacity * stride, table)
                    self.assertEqual(table + capacity * 2, counter)
                    self.assertEqual(counter + 4, high)
                    previous_end = high + 4
                self.assertLess(previous_end, layout.size)

    def test_old_slot_fields_preserve_offsets_inside_each_pool(self):
        layout = ExpandedLayout(self.original, 4096)
        for old, new in zip(self.original["pools"], layout.pools):
            old_base, new_base, stride = integer(old["missionOffset"]), integer(new["missionOffset"]), integer(old["elementStride"])
            for index in (0, 1, 2039):
                for field in (0, 1, stride - 1):
                    self.assertEqual(layout.mission_offset(old_base + index * stride + field), new_base + index * stride + field)

    def test_pool_pointer_and_mission_pointer_translations_agree(self):
        layout = ExpandedLayout(self.original, 4096)
        for old, new in zip(self.original["pools"], layout.pools):
            old_base, new_base, stride = integer(old["missionOffset"]), integer(new["missionOffset"]), integer(old["elementStride"])
            for field in ("indexTableOffset", "counterOffset", "highWaterOffset"):
                relative = integer(old[field]) - old_base
                self.assertEqual(new_base + layout.pool_offset(stride, relative), integer(new[field]))

    def test_tail_shift_and_pinned_expected_allocation(self):
        layout = ExpandedLayout(self.original, 4096)
        self.assertEqual(layout.size, 35495824)
        self.assertEqual(layout.size - layout.original_size, 17126480)
        for field in (0x1037938, 0x10438F0, 0x1184B3F):
            self.assertEqual(layout.mission_offset(field), field + 17126480)
        self.assertEqual(layout.mission_offset(0x2D50), 0x2D50)

    def test_original_capacity_identity(self):
        layout = ExpandedLayout(self.original, 2040)
        self.assertEqual(layout.size, layout.original_size)
        for offset in (0, 0x2D50, 0x62CA50, 0xC58750, 0x1037938, layout.size):
            self.assertEqual(layout.mission_offset(offset), offset)

    def test_job_pages_cover_every_agent_without_overlapping_flags(self):
        layout = ExpandedLayout(self.original, 4080, expand_jobs=True)
        self.assertEqual(layout.size, 35366000)
        self.assertEqual(layout.size - ExpandedLayout(self.original, 4080).size, 0xD80)
        previous_end = 0
        for table in layout.job_tables:
            base, pointers, flags, end = map(integer, (table['base'], table['pointerBase'], table['flagBase'], table['endExclusive']))
            self.assertGreaterEqual(base, previous_end)
            self.assertEqual(base % 8, 0)
            self.assertEqual(pointers, base + 8)
            self.assertEqual(flags, pointers + 255 * 8)
            self.assertEqual(end, base + 0x900)
            self.assertEqual(end - (flags + 255), 1)
            for agent in range(4080):
                page, record = divmod(agent, 16)
                self.assertLess(pointers + page * 8 + 7, flags)
                self.assertLess(flags + page, end)
                self.assertLess(record, 16)
            previous_end = end
        for capacity in (2040, 2041, 4096, 32766):
            with self.assertRaises(ValueError):
                ExpandedLayout(self.original, capacity, expand_jobs=True)

    def test_cleanup_unrolling_visits_every_expanded_slot_once(self):
        for batch in (3, 10):
            visited = [iteration * batch + lane for iteration in range(4080 // batch) for lane in range(batch)]
            self.assertEqual(visited, list(range(4080)))

    def test_unsigned_byte_comparison_has_same_stop_as_255_dword_bound(self):
        # CMP DL/BL,0xff is valid only because these four loops start at zero,
        # increment by one and stop at 255. Immediate ff with opcode83 means -1.
        for count in range(256):
            self.assertEqual((count & 0xFF) < 255, count < 255)
        self.assertTrue(255 < 0xFFFFFFFF)
        self.assertFalse((255 & 0xFF) < 255)

    def test_reject_overflow_and_invalid_profile(self):
        for capacity in (2039, 32767, 65536, -1, 4096.0, True):
            with self.assertRaises(ValueError):
                ExpandedLayout(self.original, capacity)
        broken = copy.deepcopy(self.original)
        broken["pools"][1]["missionOffset"] = "0x2d50"
        with self.assertRaises(ValueError):
            ExpandedLayout(broken, 4096)
        with self.assertRaises(ValueError):
            ExpandedLayout(self.original, 4096).pool_offset(0xC60, 0x700000)


if __name__ == "__main__":
    unittest.main()
