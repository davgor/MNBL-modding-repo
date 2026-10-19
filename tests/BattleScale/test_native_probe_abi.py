"""Compare the probe's private ABI constants with independently audited native evidence."""
import json
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]


class NativeProbeAbiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = (ROOT / 'src/MNBL.BattleScale/NativePoolProbeMission.cs').read_text()
        cls.contract = json.loads((ROOT / 'evidence/engine-extension/native-pool-exhaustion-contract.json').read_text())
        cls.layout = json.loads((ROOT / 'evidence/engine-extension/native-pool-patch-4080.json').read_text())['layout']

    def test_loaded_code_pin_is_complete_reviewed_failure_path(self):
        expected = re.search(r'FullAllocatorPathHex = "([0-9a-f]+)"', self.source).group(1)
        rows = self.contract['rawProofs']['allocatorFullFailureAndFirstMutation']['candidate']
        failure = [r for r in rows if int(r['address'], 16) < 0x1805d8059]
        self.assertEqual(expected, ''.join(r['bytes'] for r in failure))
        self.assertEqual(failure[-1]['mnemonic'], 'ret')
        self.assertFalse(any(r['mnemonic'] == 'call' for r in failure))

    def test_all_five_tables_and_metadata_are_contiguous_in_candidate_layout(self):
        initializer = re.search(r'poolIndexTables = \{([^}]+)\}', self.source).group(1)
        offsets = [int(x.strip(), 16) for x in initializer.split(',')]
        self.assertEqual(len(offsets), 5)
        for offset, pool in zip(offsets, self.layout['pools']):
            self.assertEqual(offset, int(pool['indexTableOffset'], 16))
            self.assertEqual(offset + 4080 * 2, int(pool['counterOffset'], 16))
            self.assertEqual(offset + 4080 * 2 + 4, int(pool['highWaterOffset'], 16))

    def test_primary_this_pointer_and_stride_match_layout(self):
        primary = self.layout['pools'][0]
        self.assertEqual(int(primary['missionOffset'], 16), 0x2d50)
        self.assertEqual(int(primary['elementStride'], 16), 0xc60)
        self.assertEqual(primary['capacity'], 4080)
        self.assertEqual(self.contract['allocator']['entry'], '0x1805d8020')


if __name__ == '__main__':
    unittest.main()
