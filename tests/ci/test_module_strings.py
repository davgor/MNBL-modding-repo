"""MMR-286: the module's localization file holds every "{=mnbl_...}" string of the sources, and the load, setup and preview messages
named by map epic 2 (MMR-282's warnings, MMR-283's and MMR-284's refusals, the load refusals and loader messages) are among them.

Runs without the game: tools/make_module_strings.py --check regenerates the file in memory and compares.
"""
import subprocess
import sys
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
FILE = REPO / 'Modules/MNBL.ProceduralWorld/ModuleData/Languages/std_mnbl_procedural_world.xml'
REQUIRED = [
    # MMR-282: setup and preview waits, the load's navigation rebuild warning and its reasons.
    'mnbl_ws_wait_start_preview', 'mnbl_ws_wait_navigation', 'mnbl_load_rebuild_title', 'mnbl_load_rebuild', 'mnbl_load_rebuild_measured',
    'mnbl_load_rebuild_continue', 'mnbl_load_rebuild_cancel', 'mnbl_load_rebuild_module', 'mnbl_load_rebuild_missing', 'mnbl_load_rebuild_game',
    # MMR-283: content refusals at setup and load.
    'mnbl_err_content_title', 'mnbl_load_content_title', 'mnbl_load_content_missing_mod', 'mnbl_load_content_changed_mod', 'mnbl_load_content_battle',
    # MMR-284: builds and patch signatures.
    'mnbl_err_game_version', 'mnbl_err_war_sails_version', 'mnbl_err_patch_signature', 'mnbl_load_build_saved_old', 'mnbl_load_build_running_old',
    'mnbl_load_patch_signature',
    # MMR-286: every load refusal's title and message, and the loader's own messages.
    'mnbl_load_missing_world_title', 'mnbl_load_missing_world', 'mnbl_load_damaged_world_title', 'mnbl_load_damaged_world', 'mnbl_load_damaged_save_title',
    'mnbl_load_damaged_save', 'mnbl_load_build_title', 'mnbl_load_build', 'mnbl_load_not_rebuildable_title', 'mnbl_load_not_rebuildable',
    'mnbl_load_not_generated_title', 'mnbl_load_not_generated', 'mnbl_load_busy_title', 'mnbl_load_busy', 'mnbl_load_unavailable_title',
    'mnbl_load_unavailable', 'mnbl_load_failed_title', 'mnbl_load_failed', 'mnbl_load_already', 'mnbl_load_restored', 'mnbl_load_ended_running',
    'mnbl_load_back_at_menu', 'mnbl_load_ok', 'mnbl_load_routed', 'mnbl_load_routed_unapplied', 'mnbl_load_periodic_frozen',
]


class ModuleStrings(unittest.TestCase):
    def test_file_is_current(self):
        run = subprocess.run([sys.executable, str(REPO / 'tools/make_module_strings.py'), '--check'], capture_output=True, text=True)
        self.assertEqual(run.returncode, 0, run.stdout + run.stderr)

    def test_required_ids(self):
        root = ET.parse(FILE).getroot()
        self.assertEqual([t.get('language') for t in root.iter('tag')], ['English'])
        ids = {s.get('id'): s.get('text') for s in root.iter('string')}
        missing = [i for i in REQUIRED if i not in ids]
        self.assertEqual(missing, [])
        self.assertTrue(all(t for t in ids.values()), 'every string has a text')


if __name__ == '__main__':
    unittest.main()
