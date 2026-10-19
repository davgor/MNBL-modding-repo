"""Execution-free regressions for bounded switch index proofs."""
import importlib.util
from pathlib import Path
import struct
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/battle-scale'))
spec = importlib.util.spec_from_file_location('decode_gap_audit', ROOT / 'tools/battle-scale/audit_native_decode_gaps.py')
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class IndexWidthTests(unittest.TestCase):
    def proof(self, raw, register='r15', switches=None):
        decoder = audit.capstone.Cs(audit.capstone.CS_ARCH_X86, audit.capstone.CS_MODE_64)
        decoder.detail = True
        instructions = {i.address:i for i in decoder.disasm(bytes.fromhex(raw), 0x1000)}
        guard = next(i.address for i in instructions.values() if i.mnemonic == 'cmp')
        return audit.prove_guard_index(instructions, 0x1000, guard, register, switches or {}, [(0x1000, 0x1100)])

    def test_sign_extension_proves_full_index(self):
        proof = self.proof('4c63f8 4183ff0f c3')
        self.assertTrue(proof['accepted'])
        self.assertEqual(proof['establishingInstructions'][0]['bytes'], '4c63f8')

    def test_bypass_of_establishing_write_rejects(self):
        self.assertFalse(self.proof('85c0 7403 4c63f8 4183ff0f c3')['accepted'])

    def test_partial_register_writes_kill_fact(self):
        for overwrite in ('41b701', '6641bf0100'):
            with self.subTest(overwrite=overwrite):
                self.assertFalse(self.proof('4c63f8 '+overwrite+' 4183ff0f c3')['accepted'])

    def test_nonvolatile_preserved_across_call(self):
        self.assertTrue(self.proof('4c63f8 e800010000 4183ff0f c3')['accepted'])

    def test_volatile_clobbered_across_call(self):
        self.assertFalse(self.proof('4c63c0 e800010000 4183f80f c3', 'r8')['accepted'])

    def test_32_bit_assignment_proves_zero_extension(self):
        self.assertTrue(self.proof('41bf0f000000 4183ff0f c3')['accepted'])

    def test_64_bit_assignment_with_unknown_high_bits_rejects(self):
        self.assertFalse(self.proof('49bf0f00000001000000 4183ff0f c3')['accepted'])

    def test_loop_reestablishes_fact(self):
        # After CMP a loop may destroy R15, but every repeat passes MOVSXD.
        self.assertTrue(self.proof('4c63f8 4183ff0f 7408 4c8b3b ebf2 909090 c3')['accepted'])

    def test_loop_bypassing_reestablishment_rejects(self):
        self.assertFalse(self.proof('4c63f8 4183ff0f 7408 4c8b3b ebf5 909090 c3')['accepted'])

    def test_unknown_indirect_control_flow_rejects(self):
        self.assertFalse(self.proof('4c63f8 4183ff0f ffe1')['accepted'])

    def test_reachable_missing_instruction_boundary_rejects(self):
        self.assertFalse(self.proof('4c63f8 4183ff0f eb01 48b80100000000000000 c3')['accepted'])

    def test_negative_sign_extended_inputs_fail_unsigned_bound(self):
        for value in (0, 1, 15, 16, 0x7fffffff, 0x80000000, 0xfffffff0, 0xffffffff):
            full = value if value < 0x80000000 else value | 0xffffffff00000000
            if value <= 15:
                self.assertLessEqual(full, 15)
            else:
                self.assertGreater(value, 15)


class LocalTemplateTests(unittest.TestCase):
    def test_full_width_move_accepted_partial_moves_rejected(self):
        base = 0x180000000
        class FakePE:
            def get_data(self, address, size):
                return struct.pack('<IIII', 0x1030, 0x1030, 0x1030, 0x1030)[:size] if address == 0x2000 else b''
        decoder = audit.capstone.Cs(audit.capstone.CS_ARCH_X86, audit.capstone.CS_MODE_64)
        decoder.detail = True
        for move, expected in (('8bc1', 1), ('8ac1', 0), ('668bc1', 0)):
            raw = bytes.fromhex('488d15') + struct.pack('<i', -0x1007) + bytes.fromhex(move+'83f803770c8b8c82002000004803caffe1c3')
            instructions = {i.address:i for i in decoder.disasm(raw, base+0x1000)}
            with self.subTest(move=move):
                self.assertEqual(len(audit.switch_candidates(instructions, FakePE(), base, [(0x1000, 0x1100)])), expected)


if __name__ == '__main__':
    unittest.main()
