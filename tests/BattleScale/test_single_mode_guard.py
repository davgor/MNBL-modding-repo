"""Validate the actual guard's branches without executing any native code."""
import importlib.util
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools/battle-scale"))
import audit_single_mode_guard as guard
from patch_native_copy import PatchError


class NativeSingleModeGuardTests(unittest.TestCase):
    def execute_guard_model(self, value):
        # Interpret the decoded bytes rather than just the desired mode policy.
        # INT29 is recorded as termination, never executed on the test host.
        md = guard.capstone.Cs(guard.capstone.CS_ARCH_X86, guard.capstone.CS_MODE_64)
        md.detail = True
        decoded = {i.address: i for i in md.disasm(guard.REPLACEMENT + b"\xc3", guard.SETTER)}
        ip = guard.SETTER
        zf = None
        visited = []
        for _ in range(4):
            ins = decoded[ip]
            visited.append(ip)
            if ins.mnemonic == "test":
                self.assertEqual(ins.op_str, "ecx, ecx")
                zf = (value & 0xffffffff) == 0
            elif ins.mnemonic == "je":
                self.assertIsNotNone(zf)
                if zf:
                    ip = ins.operands[0].imm
                    continue
            elif ins.mnemonic == "int":
                self.assertEqual(ins.operands[0].imm, 0x29)
                return "fail-fast", visited
            elif ins.mnemonic == "ret":
                return "return-single", visited
            else:
                self.fail("Unexpected guard instruction: " + ins.mnemonic)
            ip += ins.size
        self.fail("Guard did not terminate")

    def test_single_reaches_existing_return(self):
        outcome, path = self.execute_guard_model(0)
        self.assertEqual(outcome, "return-single")
        self.assertEqual(path[-1], guard.SETTER + 6)
        self.assertNotIn(guard.SETTER + 4, path)

    def test_all_supported_and_unknown_nonzero_modes_fail_fast(self):
        for value in (1, 2, 3, 4, 5, 6, 255, 256, 65536, -1, 0x7fffffff, -0x80000000):
            with self.subTest(mode=value):
                outcome, path = self.execute_guard_model(value)
                self.assertEqual(outcome, "fail-fast")
                self.assertNotIn(guard.SETTER + 6, path)

    def test_guard_has_no_stack_or_nonvolatile_register_writes(self):
        md = guard.capstone.Cs(guard.capstone.CS_ARCH_X86, guard.capstone.CS_MODE_64)
        md.detail = True
        self.assertEqual(len(guard.ORIGINAL), len(guard.REPLACEMENT))
        for ins in md.disasm(guard.REPLACEMENT, guard.SETTER):
            written = {ins.reg_name(r) for r in ins.regs_access()[1]}
            self.assertLessEqual(written, {"rflags"})

    def test_saved_audit_preserves_zero_initializer_and_records_launch_hold(self):
        report = json.loads((ROOT/"evidence/engine-extension/native-single-mode-guard.json").read_text())
        self.assertEqual(report["patch"]["replacement"], guard.REPLACEMENT.hex())
        self.assertEqual(report["patch"]["original"], guard.ORIGINAL.hex())
        self.assertEqual(report["loaderInitialValue"], 0)
        self.assertEqual(report["directWriterAudit"]["literalAddressFirstFileOffsets"], {"rva32": -1, "va64": -1})
        self.assertEqual({w["address"] for w in report["directWriterAudit"]["writers"]}, {"0x18056eaa5", "0x180710c10"})
        self.assertFalse(report["gameExecuted"])
        self.assertEqual(report["status"], "static-verified-runtime-unverified")

    def test_audit_cannot_overwrite_its_retained_rea_input(self):
        bundle = ROOT/"evidence/engine-extension/native-closure-ghidra-evidence.json"
        before = bundle.read_bytes()
        with self.assertRaisesRegex(PatchError, "refusing to overwrite review input"):
            guard.audit(bundle)
        self.assertEqual(bundle.read_bytes(), before)


if __name__ == "__main__":
    unittest.main()
