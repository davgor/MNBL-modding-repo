import os
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools/battle-scale"))
from patch_native_copy import PatchError, safe_review_output


class ReviewOutputTests(unittest.TestCase):
    def test_only_json_and_no_input_overwrite(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source.dll"
            source.write_bytes(b"unchanged")
            protected = root / "inventory.json"
            protected.write_text("{}")
            for output in (source, root / "different.dll", protected):
                with self.assertRaises(PatchError):
                    safe_review_output(output, [source, protected])
            target = root / "review.json"
            self.assertEqual(safe_review_output(target, [source, protected]), target)
            target.write_text("{}")
            self.assertEqual(safe_review_output(target, [source, protected]), target)
            self.assertEqual(source.read_bytes(), b"unchanged")

    def test_installation_and_hardlink_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            installation = root / "game"
            installation.mkdir()
            with self.assertRaises(PatchError):
                safe_review_output(installation / "output.json", [], [installation])
            source = root / "source.dll"
            source.write_bytes(b"unchanged")
            alias = root / "alias.json"
            os.link(source, alias)
            with self.assertRaises(PatchError):
                safe_review_output(alias, [source])
            with self.assertRaises(PatchError):
                safe_review_output(alias, [])


if __name__ == "__main__":
    unittest.main()
