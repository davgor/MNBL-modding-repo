"""The files worldgen/calibration/naval-transitions/naval-transitions.json pins by SHA-256 still hold the pinned bytes.

The calibration is a world input (every bundle's policies/), so its pins cannot follow an edited file: an edit to a pinned file must go
elsewhere (docs/formats/navmesh-bin-loading.md took navmesh-bin.md's later answers). The tracked sources are checked everywhere; the
in-game run record is git-ignored and checked only where it exists; the installed game file is checked by tools/summarize_travel.py.
"""
import hashlib
import json
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
CALIBRATION = REPO / 'worldgen/calibration/naval-transitions/naval-transitions.json'


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


class CalibrationPins(unittest.TestCase):
    def setUp(self):
        self.source = json.loads(CALIBRATION.read_text(encoding='utf-8-sig'))['source']

    def test_tracked_sources_match_their_pins(self):
        for key in ('faceGroupTable', 'evidence'):
            path = REPO / self.source[key]
            with self.subTest(key=key, path=self.source[key]):
                self.assertTrue(path.is_file(), 'pinned file missing')
                self.assertEqual(sha(path), self.source[key + 'Sha256'],
                                 'the file drifted from its calibration pin; put new text in another file, the calibration is a world input')

    def test_run_record_matches_its_pin_where_present(self):
        path = REPO / self.source['run']
        if not path.is_file():
            self.skipTest('the in-game run record is git-ignored and absent here')
        self.assertEqual(sha(path), self.source['runSha256'])


if __name__ == '__main__':
    unittest.main()
