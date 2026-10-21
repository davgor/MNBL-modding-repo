#!/usr/bin/env python3
"""The runtime surface template (Modules/MNBL.ProceduralWorld/SurfaceTemplate), generated from tracked source (MMR-57, MMR-255).

The template the surface encoder starts from has five files. Two are ours, written by this repository's own writers
(tools/scenebin/build_slot.py: terrain.bin, navmesh.bin; the encoder regenerates both). flora.bin is the empty flora file (a 12-byte
header, byte-identical to every official empty flora file), which the module writes itself rather than ship. Two come from the installed
official battle scene SandBoxCore/SceneObj/battle_terrain_001: scene.xscene is that scene's text with our values substituted, and
atmosphere.xml is a verbatim copy. A package may not carry those two, so the module derives them at run time from the player's own
installed game (World/SurfaceTemplateSource.cs), and this tool writes the recipe that needs: template-recipe.json, with the official
files' paths and SHA-256, our substituted values and the SHA-256 every derived file must have.

Map epic 2 (MMR-255): every substituted value is generated here from tracked source, never copied from a built slot:
  - the material layers are worldgen/policies/terrain-materials.json layerSet, slot by slot: its textures, scale (the tile size in map
    units, MMR-249), physics material and tint (albedo_factor_mode / colour), smooth_blend_amount 1, every layer flag off (MMR-249:
    use_transparency_of_ground_slope height-blends), no flora meshes, and the same layer in all four seasons (seasonal variants and
    masks never switch at run time);
  - the terrain carries use_dynamic_weather_effects="true" (substitutions.terrainAttributes; MMR-256 adds its snow texture there);
  - no road layer, no school layers (MMR-249 chose delivery A, decals), never terrain_render_version="1".
The official scene keeps its environment, outer mesh (cleared by the encoder) and atmosphere.

Usage:
  python tools/scenebin/make_surface_template.py                 # write template-recipe.json (proves the derivation first)
  python tools/scenebin/make_surface_template.py --check         # exit 1 when the tracked recipe is not what the policy generates
  python tools/scenebin/make_surface_template.py --materialize DIR   # write the five-file template the module derives, for offline tools
Options: --game <install root> (default the Steam install), --out <SurfaceTemplate folder>.
"""
import argparse
import hashlib
import json
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OFFICIAL = 'Modules/SandBoxCore/SceneObj/battle_terrain_001'
OURS = ('terrain.bin', 'navmesh.bin')
EMPTY_FLORA = b'FLR2' + (4).to_bytes(4, 'little') + (0).to_bytes(4, 'little')
FILES = ('scene.xscene', 'terrain.bin', 'navmesh.bin', 'flora.bin', 'atmosphere.xml')
TERRAIN_ATTRIBUTES = ('node_dimension_x', 'node_dimension_y', 'node_size', 'min_height', 'max_height', 'vista_tileset')
SCENE_NAME = 'mnbl_runtime_surface'
SEASONS = ('summer', 'fall', 'winter', 'spring')
FLAGS = ('use_parallax', 'use_displacement_mapping', 'use_randomized_normalmap', 'use_transparency_of_diffuse_alpha',
         'use_transparency_of_ground_slope', 'use_dxt5_normal', 'use_six_side_uv_mapping')
EXTENT = 1600.0
LEVEL = 7


def sha(data):
    return hashlib.sha256(data).hexdigest()


def layer_xml(layer):
    """One <layer> of the template: the policy row in every season, no flora meshes, no flags."""
    tint = layer['tint']
    colour = tint['colour']
    attrs = ('is_flora_layer="false" is_mesh_blend_layer="false" pitch_roll_yaw="0.000, 0.000, 0.000" scale="%.3f, %.3f" '
             'shear="0.000, 0.000" position_offset="0.000, 0.000" physics_material="%s" detail_level_adjustment="0" '
             'elevation_amount="0.000" parallax_amount="0.000" ground_slope_scale="1.000" bigdetailmap_mode="0" '
             'bigdetailmap_weight="0.000" albedo_factor_mode="%d" albedo_factor_color="%.3f, %.3f, %.3f" smooth_blend_amount="1.000"'
             % (layer['scale'], layer['scale'], layer['physics'], tint['mode'], colour[0], colour[1], colour[2]))
    textures = (('diffusemap', layer['diffuse']), ('areamap', 'none'), ('normalmap', layer['normal']), ('specularmap', layer['specular']),
                ('splattingmap', 'none'), ('displacementmap', layer['height']))
    seasons = []
    for season in SEASONS:
        lines = ['\t\t\t\t<%s is_enabled="true" name="%s" %s>' % (season, layer['name'], attrs), '\t\t\t\t\t<textures>']
        lines += ['\t\t\t\t\t\t<texture type="%s" name="%s"/>' % kv for kv in textures]
        lines += ['\t\t\t\t\t</textures>', '\t\t\t\t\t<layer_flags>']
        lines += ['\t\t\t\t\t\t<flag name="%s" value="false"/>' % f for f in FLAGS]
        lines += ['\t\t\t\t\t</layer_flags>', '\t\t\t\t\t<meshes>', '\t\t\t\t\t</meshes>', '\t\t\t\t</%s>' % season]
        seasons.append('\n'.join(lines))
    return '\t\t\t<layer name="%s">\n%s\n\t\t\t</layer>' % (layer['name'], '\n'.join(seasons))


def layers_block(policy):
    rows = policy['layerSet']
    if [r['slot'] for r in rows] != list(range(len(rows))) or not 1 <= len(rows) <= 15:
        raise SystemExit('layerSet must list 1 to 15 layers by slot from 0')
    return '<layers version="1">\n' + '\n'.join(layer_xml(r) for r in rows) + '\n\t\t</layers>'


def nodes_block():
    """The placeholder nodes block (the encoder regenerates every node): 16 nodes, the base layer, level 7."""
    lines = ['\t\t<nodes>']
    for idy in range(4):
        for idx in range(4):
            lines.append('\t\t\t<node idx="%d" idy="%d" occluders_enabled="true" layer_is_used_mask_summer="1" layer_is_used_mask_fall="1" '
                         'layer_is_used_mask_winter="1" layer_is_used_mask_spring="1" layer_is_used_mask="true" height_complexity="0.000" '
                         'min_height="0.000" max_height="1.000">' % (idx, idy))
            lines.append('\t\t\t\t<texture_levels>')
            lines.append('\t\t\t\t\t<variable name="heightmap_level" value="%d"/>' % LEVEL)
            lines.append('\t\t\t\t\t<variable name="min_heightmap_level" value="2"/>')
            lines.append('\t\t\t\t\t<variable name="normalmap_level" value="%d"/>' % LEVEL)
            lines.append('\t\t\t\t\t<variable name="weightmap_levels">')
            lines += ['\t\t\t\t\t\t<variable layer="%d" value="%d"/>' % (k, LEVEL if k == 0 else 0) for k in range(16)]
            lines.append('\t\t\t\t\t</variable>')
            lines.append('\t\t\t\t</texture_levels>')
            lines.append('\t\t\t</node>')
    lines.append('\t\t</nodes>')
    return '\n'.join(lines)


def substitutions(policy):
    return {
        'sceneName': SCENE_NAME,
        'floraBoundingRect': '<flora_bounding_rect min="0.000, 0.000" max="%.3f, %.3f"/>' % (EXTENT, EXTENT),
        'physicsWorldMin': '-50.000, -50.000',
        'physicsWorldMax': '%.3f, %.3f' % (EXTENT + 50, EXTENT + 50),
        'terrain': {'node_dimension_x': '4', 'node_dimension_y': '4', 'node_size': '%.3f' % (EXTENT / 4), 'min_height': '0.000', 'max_height': '1.000',
                    'vista_tileset': ''},
        # Set (or appended in this order) on <terrain>: dynamic weather on (MMR-249: the terrain snows only from the texture its
        # dynamic_flowmap_texture_name names, with this flag; MMR-256 adds that attribute here).
        'terrainAttributes': {'use_dynamic_weather_effects': 'true'},
        'layers': layers_block(policy),
        'nodes': nodes_block(),
    }


def derive(official_bytes, recipe):
    """The template's scene.xscene from the official scene's bytes: the steps World/SurfaceTemplateSource.cs takes, in its order."""
    s = recipe['substitutions']
    text = official_bytes.decode('utf-8').replace('\r\n', '\n')
    text, n = re.subn(r'<scene name="[^"]*"', '<scene name="%s"' % s['sceneName'], text, count=1)
    assert n == 1
    text, n = re.subn(r'<entities>.*?</entities>', '<entities>\n\t</entities>', text, count=1, flags=re.S)
    assert n == 1
    text, n = re.subn(r'<flora_bounding_rect[^>]*/>', lambda m: s['floraBoundingRect'], text, count=1)
    assert n == 1
    text, n = re.subn(r'(<property name="physics_world_min" value=")[^"]*"', lambda m: m.group(1) + s['physicsWorldMin'] + '"', text, count=1)
    assert n == 1
    text, n = re.subn(r'(<property name="physics_world_max_" value=")[^"]*"', lambda m: m.group(1) + s['physicsWorldMax'] + '"', text, count=1)
    assert n == 1

    def terrain(match):
        tag = match.group(0)
        for name in TERRAIN_ATTRIBUTES:
            tag, k = re.subn(r' %s="[^"]*"' % name, lambda m, name=name: ' %s="%s"' % (name, s['terrain'][name]), tag)
            if k != 1:
                raise ValueError('the official <terrain> lacks ' + name)
        for name, value in s['terrainAttributes'].items():
            tag, k = re.subn(r' %s="[^"]*"' % name, lambda m, name=name, value=value: ' %s="%s"' % (name, value), tag)
            if k == 0:
                tag = tag[:-1] + ' %s="%s">' % (name, value)
        return tag
    text, n = re.subn(r'<terrain [^>]*>', terrain, text, count=1)
    assert n == 1
    text, n = re.subn(r'<layers version="1">.*?</layers>', lambda m: s['layers'], text, count=1, flags=re.S)
    assert n == 1
    text, n = re.subn(r'\t*<nodes>.*?</nodes>', lambda m: s['nodes'], text, count=1, flags=re.S)
    assert n == 1
    return text.encode('utf-8')


def build(game, out):
    policy = json.load(open(os.path.join(REPO, 'worldgen', 'policies', 'terrain-materials.json'), encoding='utf-8-sig'))
    official_dir = os.path.join(game, OFFICIAL)
    official_scene = open(os.path.join(official_dir, 'scene.xscene'), 'rb').read()
    official_atmosphere = open(os.path.join(official_dir, 'atmosphere.xml'), 'rb').read()
    ours = {f: open(os.path.join(out, f), 'rb').read() for f in OURS}
    recipe = {'schema': 'mnbl.surface-template-recipe', 'version': 2, 'ticket': 'MMR-255',
              'note': 'scene.xscene and atmosphere.xml are derived at run time from the installed official scene below (never shipped), with these '
                      'substitutions, all generated by tools/scenebin/make_surface_template.py from worldgen/policies/terrain-materials.json layerSet; '
                      'flora.bin is the empty flora file the module writes; terrain.bin and navmesh.bin are this module\'s own (tools/scenebin/build_slot.py writers).',
              'official': {'scene': OFFICIAL, 'sceneXsceneSha256': sha(official_scene), 'atmosphereXmlSha256': sha(official_atmosphere)},
              'policySha256': sha(open(os.path.join(REPO, 'worldgen', 'policies', 'terrain-materials.json'), 'rb').read()),
              'substitutions': substitutions(policy), 'ours': list(OURS)}
    derived = derive(official_scene, recipe)
    recipe['expected'] = {'scene.xscene': sha(derived), 'terrain.bin': sha(ours['terrain.bin']), 'navmesh.bin': sha(ours['navmesh.bin']),
                          'flora.bin': sha(EMPTY_FLORA), 'atmosphere.xml': sha(official_atmosphere)}
    files = {'scene.xscene': derived, 'atmosphere.xml': official_atmosphere, 'flora.bin': EMPTY_FLORA, **ours}
    return recipe, files


def recipe_text(recipe):
    return json.dumps(recipe, indent=1) + '\n'


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--game', default=os.environ.get('MNBL_GAME_DIR', r'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord'))
    ap.add_argument('--out', default=os.path.join(REPO, 'Modules', 'MNBL.ProceduralWorld', 'SurfaceTemplate'))
    ap.add_argument('--check', action='store_true')
    ap.add_argument('--materialize')
    a = ap.parse_args()
    recipe, files = build(a.game, a.out)
    path = os.path.join(a.out, 'template-recipe.json')
    if a.check:
        current = open(path, encoding='utf-8').read() if os.path.exists(path) else ''
        if current != recipe_text(recipe):
            print('the tracked template recipe is not what terrain-materials.json generates; run tools/scenebin/make_surface_template.py')
            return 1
        print('surface template recipe current (%s)' % recipe['expected']['scene.xscene'][:12])
        return 0
    if a.materialize:
        if os.path.exists(a.materialize):
            raise SystemExit('the materialize folder must not exist')
        os.makedirs(a.materialize)
        for name in FILES:
            with open(os.path.join(a.materialize, name), 'wb') as handle:
                handle.write(files[name])
        with open(os.path.join(a.materialize, 'template-recipe.json'), 'w', encoding='utf-8', newline='\n') as handle:
            handle.write(recipe_text(recipe))
        print('surface template materialized in %s (scene.xscene %s)' % (a.materialize, recipe['expected']['scene.xscene'][:12]))
        return 0
    with open(path, 'w', encoding='utf-8', newline='\n') as handle:
        handle.write(recipe_text(recipe))
    print('surface template: wrote %s (scene.xscene %s, %d layers)' % (path, recipe['expected']['scene.xscene'][:12], len(recipe['substitutions']['layers'].split('<layer name=')) - 1))
    return 0


if __name__ == '__main__':
    sys.exit(main())
