"""MMR-291: tools/art/check_magic_packs.py --overlay reads material records by their alpha blend mode, shader GUID and flags: on the installed
vanilla template of the overlay meshes (Native torch_flame_base: pbr_shading, modulate, forward, use_vertex_colors, self_illumination) and on
NavalDLC's naval_worldmap_water (the rivers' material: water_surface_worldmap's GUID f634b512..., no_alpha_blend). Skipped without the game.
"""
import os
import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / 'tools' / 'art'))
import check_magic_packs as C  # noqa: E402
import tpac_textures as T  # noqa: E402

NATIVE = os.path.join(C.GAME, 'Modules', 'Native', 'AssetPackages', 'materials.tpac')
NAVAL = os.path.join(C.GAME, 'Modules', 'NavalDLC', 'AssetPackages', 'materials.tpac')


@unittest.skipUnless(os.path.exists(NATIVE) and os.path.exists(NAVAL), 'the game is not installed here')
class OverlayPackTests(unittest.TestCase):
    def test_template_and_river_material(self):
        native, _, _ = T.header(NATIVE)
        torch = C.material_record(native, C.OVERLAY_TEMPLATE)
        self.assertIsNotNone(torch)
        self.assertEqual(torch['blend'], 'modulate')
        for flag in C.OVERLAY_FLAGS:
            self.assertIn(flag, torch['strings'])
        for flag in C.OVERLAY_FORBIDDEN:
            self.assertNotIn(flag, torch['strings'])
        naval, _, _ = T.header(NAVAL)
        water = C.material_record(naval, 'naval_worldmap_water')
        self.assertEqual(water['blend'], 'no_alpha_blend')
        self.assertEqual(water['shader'], 'f634b5125313ba4789822e88506889f3')   # water_surface_worldmap (MMR-290's route)
        self.assertIsNone(C.material_record(native, 'mnbl_overlay_vein_fire'))


if __name__ == '__main__':
    unittest.main()
