"""Self-contained offline PE tests. No Bannerlord installation is required."""
import copy
import importlib.util
import json
import os
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[2] / "tools" / "battle-scale" / "patch_native_copy.py"
SPEC = importlib.util.spec_from_file_location("patch_native_copy", SCRIPT)
engine = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(engine)


def synthetic_pe():
    data = bytearray(0x800)
    data[:2] = b"MZ"
    struct.pack_into("<I", data, 0x3C, 0x80)
    data[0x80:0x84] = b"PE\0\0"
    struct.pack_into("<HH", data, 0x84, 0x8664, 2)
    struct.pack_into("<H", data, 0x94, 240)
    optional = 0x98
    struct.pack_into("<H", data, optional, 0x20B)
    struct.pack_into("<Q", data, optional + 24, 0x180000000)
    struct.pack_into("<I", data, optional + 56, 0x3000)
    struct.pack_into("<I", data, optional + 60, 0x200)
    struct.pack_into("<I", data, optional + 108, 16)
    for i, (name, rva, raw, flags) in enumerate(((b".text", 0x1000, 0x200, 0x60000020), (b".data", 0x2000, 0x600, 0xC0000040))):
        section = 0x188 + i * 40
        data[section:section + len(name)] = name
        struct.pack_into("<IIII", data, section + 8, 0x200 if i else 0x400, rva, 0x200 if i else 0x400, raw)
        struct.pack_into("<I", data, section + 36, flags)
    data[0x210:0x215] = bytes.fromhex("b8f8070000")
    data[0x240:0x245] = bytes.fromhex("b9f9070000")
    return bytes(data)


def manifest_for(data):
    return {
        "schemaVersion": 1, "sourceSha256": engine.digest(data), "sourceSize": len(data),
        "imageBase": "0x180000000", "targetCapacity": 4080,
        "status": "experimental-static-candidate", "blockers": [],
        "patches": [
            {"id": "capacity", "fileOffset": 0x211, "rva": "0x1011", "original": "f8070000", "replacement": "f00f0000", "reason": "Synthetic capacity immediate", "evidenceIds": ["synthetic-test-only"]},
            {"id": "sentinel", "fileOffset": 0x241, "rva": "0x1041", "original": "f9070000", "replacement": "f10f0000", "reason": "Synthetic sentinel immediate", "evidenceIds": ["synthetic-test-only"]},
        ],
    }


class PatchCopyTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.data = synthetic_pe()
        self.manifest = manifest_for(self.data)
        self.source = self.root / "original.dll"
        self.source.write_bytes(self.data)
        self.manifest_path = self.root / "manifest.json"
        self.save_manifest()

    def save_manifest(self):
        self.manifest_path.write_text(json.dumps(self.manifest), encoding="utf-8")

    def reject_original(self, needle=None):
        with self.assertRaisesRegex(engine.PatchError, needle or "."):
            engine.validate_original(self.data, self.manifest)

    def test_roundtrip_and_receipt(self):
        output = self.root / "patched.dll"
        report = engine.run("apply", self.source, self.manifest_path, output)
        expected = bytearray(self.data)
        expected[0x211:0x215] = bytes.fromhex("f00f0000")
        expected[0x241:0x245] = bytes.fromhex("f10f0000")
        self.assertEqual(output.read_bytes(), expected)
        self.assertEqual(self.source.read_bytes(), self.data)
        self.assertEqual(report["manifestSha256"], engine.digest(self.manifest_path.read_bytes()))
        self.assertEqual(report["patchedSha256"], engine.digest(expected))
        self.assertFalse(report["runtimeValidated"])
        self.assertFalse(report["targetExecuted"])
        self.assertIn("unvalidated", report["warning"])
        receipt = json.loads(Path(str(output) + ".receipt.json").read_text())
        self.assertEqual(receipt, report)
        restored = self.root / "restored.dll"
        reverse = engine.run("revert", output, self.manifest_path, restored)
        self.assertEqual(restored.read_bytes(), self.data)
        self.assertEqual(reverse["outputSha256"], self.manifest["sourceSha256"])

    def test_verify_has_no_output(self):
        before = set(self.root.iterdir())
        report = engine.run("verify", self.source, self.manifest_path)
        self.assertTrue(report["applyEligible"])
        self.assertEqual(before, set(self.root.iterdir()))
        with self.assertRaises(engine.PatchError):
            engine.run("verify", self.source, self.manifest_path, self.root / "forbidden.dll")

    def test_stale_hash(self):
        self.manifest["sourceSha256"] = "0" * 64
        self.reject_original("SHA-256")

    def test_wrong_size(self):
        self.manifest["sourceSize"] += 1
        self.reject_original("size mismatch")

    def test_wrong_original_bytes(self):
        self.manifest["patches"][0]["original"] = "f7070000"
        self.reject_original("original bytes")

    def test_overlapping_patches(self):
        self.manifest["patches"][1]["fileOffset"] = 0x212
        self.reject_original("overlapping")

    def test_rva_mismatch(self):
        self.manifest["patches"][0]["rva"] = "0x1012"
        self.reject_original("RVA mismatch")

    def test_imagebase_mismatch(self):
        self.manifest["imageBase"] = "0x140000000"
        self.reject_original("image base")

    def test_blocked_manifest_verify_but_no_apply(self):
        self.manifest["status"] = "blocked"
        self.manifest["blockers"] = ["Dependent offsets not fully audited"]
        self.save_manifest()
        report = engine.run("verify", self.source, self.manifest_path)
        self.assertFalse(report["applyEligible"])
        output = self.root / "blocked.dll"
        with self.assertRaisesRegex(engine.PatchError, "blocked"):
            engine.run("apply", self.source, self.manifest_path, output)
        self.assertFalse(output.exists())

    def test_candidate_with_blockers_refuses_apply(self):
        self.manifest["blockers"] = ["Incomplete"]
        self.save_manifest()
        with self.assertRaisesRegex(engine.PatchError, "blockers"):
            engine.run("apply", self.source, self.manifest_path, self.root / "blocked.dll")

    def test_empty_blocked_manifest_supported(self):
        self.manifest["status"] = "blocked"
        self.manifest["patches"] = []
        engine.validate_original(self.data, self.manifest)
        self.manifest["status"] = "experimental-static-candidate"
        self.reject_original("contain patches")

    def test_source_and_existing_output_protected(self):
        for output in (self.source, self.root / "keep.dll"):
            if output != self.source:
                output.write_bytes(b"keep")
            before = output.read_bytes()
            with self.assertRaises(engine.PatchError):
                engine.run("apply", self.source, self.manifest_path, output)
            self.assertEqual(before, output.read_bytes())

    def test_supplied_and_default_game_root_protected(self):
        installation = self.root / "game"
        installation.mkdir()
        for use_default in (False, True):
            with patch.object(engine, "DEFAULT_GAME_ROOT", installation if use_default else engine.DEFAULT_GAME_ROOT):
                with self.assertRaisesRegex(engine.PatchError, "game installation"):
                    engine.run("apply", self.source, self.manifest_path, installation / "copy.dll", game_root=None if use_default else installation)
        # A sibling sharing the prefix is not part of the protected installation.
        sibling = self.root / "game-copy"
        sibling.mkdir()
        engine.run("apply", self.source, self.manifest_path, sibling / "copy.dll", game_root=installation)

    def test_receipt_game_root_and_existing_receipt_protected(self):
        installation = self.root / "game"
        installation.mkdir()
        output = self.root / "copy.dll"
        with self.assertRaisesRegex(engine.PatchError, "game installation"):
            engine.run("apply", self.source, self.manifest_path, output, installation / "receipt.json", installation)
        self.assertFalse(output.exists())
        receipt = self.root / "keep.json"
        receipt.write_text("keep")
        with self.assertRaises(engine.PatchError):
            engine.run("apply", self.source, self.manifest_path, output, receipt)
        self.assertFalse(output.exists())
        self.assertEqual(receipt.read_text(), "keep")

    def test_hardlink_to_source_protected(self):
        alias = self.root / "alias.dll"
        alias.hardlink_to(self.source)
        with self.assertRaises(engine.PatchError):
            engine.run("apply", self.source, self.manifest_path, alias)
        self.assertEqual(alias.read_bytes(), self.data)

    @unittest.skipUnless(os.name == "nt", "Windows namespace paths")
    def test_windows_device_and_network_output_rejected(self):
        for output in ("\\\\?\\" + str(self.root / "copy.dll"), r"\\localhost\C$\copy.dll", r"\\.\C:\copy.dll"):
            with self.subTest(output=output), self.assertRaisesRegex(engine.PatchError, "device and network"):
                engine.safe_output(output, self.source, [])

    def test_symlink_destination_rejected(self):
        alias = self.root / "link"
        target = self.root / "real"
        target.mkdir()
        try:
            alias.symlink_to(target, target_is_directory=True)
        except OSError:
            self.skipTest("OS does not permit creating symlinks")
        with self.assertRaisesRegex(engine.PatchError, "symlink"):
            engine.run("apply", self.source, self.manifest_path, alias / "copy.dll")

    def test_symlink_and_junction_guard_without_os_privilege(self):
        output = self.root / "copy.dll"
        real_is_symlink = Path.is_symlink

        def is_symlink(path):
            return path == self.root or real_is_symlink(path)

        with patch.object(Path, "is_symlink", is_symlink), self.assertRaisesRegex(engine.PatchError, "symlink"):
            engine.safe_output(output, self.source, [])
        if hasattr(Path, "is_junction"):
            with patch.object(Path, "is_junction", lambda path: path == self.root), self.assertRaisesRegex(engine.PatchError, "junction"):
                engine.safe_output(output, self.source, [])

    def test_mutation_outside_patch_rejected(self):
        after = bytearray(engine.transform(self.data, self.manifest))
        after[0x300] ^= 0x80
        with self.assertRaisesRegex(engine.PatchError, "outside"):
            engine.verify_transform(self.data, after, self.manifest["patches"])
        with self.assertRaisesRegex(engine.PatchError, "SHA-256"):
            engine.transform(after, self.manifest, reverse=True)

    def test_missing_edit_and_length_change_rejected(self):
        with self.assertRaises(engine.PatchError):
            engine.verify_transform(self.data, self.data, self.manifest["patches"])
        with self.assertRaises(engine.PatchError):
            engine.verify_transform(self.data, self.data + b"\0", self.manifest["patches"])

    def test_revert_wrong_patch_bytes_rejected(self):
        after = bytearray(engine.transform(self.data, self.manifest))
        after[0x211] ^= 1
        with self.assertRaisesRegex(engine.PatchError, "patched bytes"):
            engine.transform(after, self.manifest, reverse=True)

    def test_no_header_or_data_section_patch(self):
        for offset, rva in ((0x10, "0x10"), (0x610, "0x2010"), (0x5FE, "0x13fe")):
            with self.subTest(offset=offset):
                self.manifest = manifest_for(self.data)
                self.manifest["patches"][0].update(fileOffset=offset, rva=rva, original="00000000", replacement="01000000")
                self.reject_original()

    def test_relocation_directory_cannot_be_patched(self):
        modified = bytearray(self.data)
        struct.pack_into("<II", modified, 0x98 + 112 + 5 * 8, 0x1010, 0x10)
        self.data = bytes(modified)
        self.manifest = manifest_for(self.data)
        self.reject_original("relocation directory")

    def test_relocation_section_rejected_even_when_executable(self):
        modified = bytearray(self.data)
        modified[0x188:0x190] = b".reloc\0\0"
        self.data = bytes(modified)
        self.manifest = manifest_for(self.data)
        self.reject_original("relocation section")

    def test_malformed_pe_rejected(self):
        cases = [(0, b"NO"), (0x80, b"FAIL"), (0x84, b"\x4c\x01"), (0x98, b"\x0b\x01"), (0x94, b"\x01\x00")]
        for offset, replacement in cases:
            modified = bytearray(self.data)
            modified[offset:offset + len(replacement)] = replacement
            with self.subTest(offset=offset), self.assertRaises(engine.PatchError):
                engine.validate_original(bytes(modified), manifest_for(modified))
        with self.assertRaises(engine.PatchError):
            engine.parse_pe(b"MZ")

    def test_overlapping_pe_sections_rejected(self):
        for field, value in ((20, 0x300), (12, 0x1100)):
            modified = bytearray(self.data)
            struct.pack_into("<I", modified, 0x188 + 40 + field, value)
            with self.subTest(field=field), self.assertRaisesRegex(engine.PatchError, "overlapping"):
                engine.validate_original(bytes(modified), manifest_for(modified))

    def test_invalid_manifest_values_rejected(self):
        cases = [("fileOffset", -1), ("fileOffset", True), ("rva", "xyz"), ("rva", "0x100000000"), ("original", "0"), ("original", "xx"), ("replacement", "00"), ("replacement", "f8070000"), ("reason", ""), ("evidenceIds", [])]
        for key, value in cases:
            candidate = copy.deepcopy(self.manifest)
            candidate["patches"][0][key] = value
            with self.subTest(key=key, value=value), self.assertRaises(engine.PatchError):
                engine.validate_manifest(candidate)
        for key, value in (("schemaVersion", True), ("targetCapacity", 2040), ("targetCapacity", 4096), ("sourceSha256", "A" * 64), ("status", "ready"), ("sourceSize", 0), ("blockers", "none")):
            candidate = copy.deepcopy(self.manifest)
            candidate[key] = value
            with self.subTest(key=key, value=value), self.assertRaises(engine.PatchError):
                engine.validate_manifest(candidate)

    def test_duplicate_json_keys_rejected(self):
        self.manifest_path.write_text('{"schemaVersion":1,"schemaVersion":1}')
        with self.assertRaisesRegex(engine.PatchError, "duplicate JSON"):
            engine.load_manifest(self.manifest_path)

    def test_failed_receipt_write_removes_only_own_output(self):
        output = self.root / "copy.dll"
        receipt = self.root / "receipt.json"
        real_open = Path.open

        def intercepted_open(path, *args, **kwargs):
            if path == receipt and args and args[0] == "x":
                raise PermissionError("synthetic receipt failure")
            return real_open(path, *args, **kwargs)

        with patch.object(Path, "open", intercepted_open), self.assertRaises(PermissionError):
            engine.run("apply", self.source, self.manifest_path, output, receipt)
        self.assertFalse(output.exists())
        self.assertEqual(self.source.read_bytes(), self.data)

    def test_receipt_collision_after_preflight_preserved(self):
        output = self.root / "copy.dll"
        receipt = self.root / "receipt.json"
        real_open = Path.open

        def intercepted_open(path, *args, **kwargs):
            if path == receipt and args and args[0] == "x":
                with real_open(path, "x", encoding="utf-8") as handle:
                    handle.write("created by another writer")
            return real_open(path, *args, **kwargs)

        with patch.object(Path, "open", intercepted_open), self.assertRaises(FileExistsError):
            engine.run("apply", self.source, self.manifest_path, output, receipt)
        self.assertFalse(output.exists())
        self.assertEqual(receipt.read_text(), "created by another writer")

    def test_partial_receipt_write_rolls_back_both_files(self):
        output = self.root / "copy.dll"
        receipt = self.root / "receipt.json"

        def failed_dump(report, handle, **kwargs):
            handle.write('{"unfinished":')
            raise OSError("synthetic interrupted receipt write")

        with patch.object(engine.json, "dump", failed_dump), self.assertRaises(OSError):
            engine.run("apply", self.source, self.manifest_path, output, receipt)
        self.assertFalse(output.exists())
        self.assertFalse(receipt.exists())
        self.assertEqual(self.source.read_bytes(), self.data)

    def test_output_flush_failure_removes_partial_file(self):
        output = self.root / "copy.dll"
        with patch.object(engine.os, "fsync", side_effect=OSError("synthetic disk flush failure")), self.assertRaises(OSError):
            engine.run("apply", self.source, self.manifest_path, output)
        self.assertFalse(output.exists())
        self.assertFalse(Path(str(output) + ".receipt.json").exists())

    def test_cleanup_failure_reports_paths_and_continues_cleanup(self):
        output = self.root / "copy.dll"
        receipt = self.root / "receipt.json"
        real_unlink = Path.unlink

        def failed_unlink(path, *args, **kwargs):
            if path == receipt:
                raise PermissionError("synthetic locked receipt")
            return real_unlink(path, *args, **kwargs)

        with patch.object(engine.json, "dump", side_effect=OSError("synthetic write failure")), patch.object(Path, "unlink", failed_unlink), self.assertRaisesRegex(engine.PatchError, "partial output cleanup failed") as failure:
            engine.run("apply", self.source, self.manifest_path, output, receipt)
        self.assertIn(str(receipt), str(failure.exception))
        self.assertIn("synthetic write failure", str(failure.exception))
        self.assertFalse(output.exists())
        self.assertTrue(receipt.exists())


if __name__ == "__main__":
    unittest.main()
