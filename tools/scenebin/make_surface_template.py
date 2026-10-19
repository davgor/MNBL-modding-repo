#!/usr/bin/env python3
"""MMR-57: the shippable form of the runtime surface template (Modules/MNBL.ProceduralWorld/SurfaceTemplate).

The template the surface encoder starts from has five files. Two are ours, written by this repository's own writers
(tools/scenebin/build_slot.py: terrain.bin, navmesh.bin). flora.bin is the empty flora file (a 12-byte header, byte-identical to every
official empty flora file), which the module writes itself rather than ship. Two come from the installed official battle scene
SandBoxCore/SceneObj/battle_terrain_001 (build_slot.py's note: local fixtures, not package content): scene.xscene is that scene's
text with our values substituted, atmosphere.xml is a verbatim copy. A package may not carry those two, so the module derives them at
run time from the player's own installed game (World/SurfaceTemplateSource.cs), and this tool writes what that needs:
  - our two binaries, copied into the module folder;
  - template-recipe.json: the official files' paths and SHA-256, our substituted values (scene name, terrain attributes, flora and
    physics bounds, the terrain nodes block), and the SHA-256 every derived file must have.
It proves the recipe first: deriving scene.xscene from the installed official scene with these values (the transformation
World/SurfaceTemplateSource.cs implements) must give the existing template byte for byte, or nothing is written.
Usage: python tools/scenebin/make_surface_template.py --template <existing SurfaceTemplate folder> [--game <install root>]
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OFFICIAL = 'Modules/SandBoxCore/SceneObj/battle_terrain_001'
OURS = ('terrain.bin', 'navmesh.bin')
EMPTY_FLORA = b'FLR2' + (4).to_bytes(4, 'little') + (0).to_bytes(4, 'little')
FILES = ('scene.xscene', 'terrain.bin', 'navmesh.bin', 'flora.bin', 'atmosphere.xml')
TERRAIN_ATTRIBUTES = ('node_dimension_x', 'node_dimension_y', 'node_size', 'min_height', 'max_height', 'vista_tileset')
SCENE_NAME = 'mnbl_runtime_surface'


def sha(data):
    return hashlib.sha256(data).hexdigest()


def one(pattern, text, flags=0):
    found = re.findall(pattern, text, flags)
    if len(found) < 1:
        raise ValueError('the template lacks ' + pattern)
    return re.search(pattern, text, flags)


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
            tag, k = re.subn(r'%s="[^"]*"' % name, lambda m, name=name: '%s="%s"' % (name, s['terrain'][name]), tag)
            if k != 1:
                raise ValueError('the official <terrain> lacks ' + name)
        return tag
    text, n = re.subn(r'<terrain [^>]*>', terrain, text, count=1)
    assert n == 1
    text, n = re.subn(r'\t*<nodes>.*?</nodes>', lambda m: s['nodes'], text, count=1, flags=re.S)
    assert n == 1
    return text.encode('utf-8')


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--template', required=True)
    ap.add_argument('--game', default=r'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord')
    ap.add_argument('--out', default=os.path.join(REPO, 'Modules', 'MNBL.ProceduralWorld', 'SurfaceTemplate'))
    a = ap.parse_args()
    template = {f: open(os.path.join(a.template, f), 'rb').read() for f in FILES}
    official_dir = os.path.join(a.game, OFFICIAL)
    official_scene = open(os.path.join(official_dir, 'scene.xscene'), 'rb').read()
    official_atmosphere = open(os.path.join(official_dir, 'atmosphere.xml'), 'rb').read()
    if official_atmosphere != template['atmosphere.xml']:
        raise SystemExit('the template\'s atmosphere.xml is not the installed official one')
    if template['flora.bin'] != EMPTY_FLORA:
        raise SystemExit('the template\'s flora.bin is not the empty flora file the module writes')
    text = template['scene.xscene'].decode('utf-8')
    terrain_tag = one(r'<terrain [^>]*>', text).group(0)
    substitutions = {
        'sceneName': one(r'<scene name="([^"]*)"', text).group(1),
        'floraBoundingRect': one(r'<flora_bounding_rect[^>]*/>', text).group(0),
        'physicsWorldMin': one(r'<property name="physics_world_min" value="([^"]*)"', text).group(1),
        'physicsWorldMax': one(r'<property name="physics_world_max_" value="([^"]*)"', text).group(1),
        'terrain': {n: one(r'%s="([^"]*)"' % n, terrain_tag).group(1) for n in TERRAIN_ATTRIBUTES},
        'nodes': one(r'\t*<nodes>.*?</nodes>', text, re.S).group(0),
    }
    if substitutions['sceneName'] != SCENE_NAME:
        raise SystemExit('the template is named %s, not %s' % (substitutions['sceneName'], SCENE_NAME))
    recipe = {'schema': 'mnbl.surface-template-recipe', 'version': 1, 'ticket': 'MMR-57',
              'note': 'scene.xscene and atmosphere.xml are derived at run time from the installed official scene below (never shipped); flora.bin is the empty flora file the module writes; terrain.bin and navmesh.bin are this module\'s own (tools/scenebin/build_slot.py writers).',
              'official': {'scene': OFFICIAL, 'sceneXsceneSha256': sha(official_scene), 'atmosphereXmlSha256': sha(official_atmosphere)},
              'substitutions': substitutions, 'ours': list(OURS), 'expected': {f: sha(template[f]) for f in FILES}}
    derived = derive(official_scene, recipe)
    if derived != template['scene.xscene']:
        raise SystemExit('deriving scene.xscene from the installed official scene does not reproduce the template (%s vs %s)' % (sha(derived), sha(template['scene.xscene'])))
    os.makedirs(a.out, exist_ok=True)
    stale = os.path.join(a.out, 'flora.bin')
    if os.path.exists(stale):
        os.remove(stale)
    for f in OURS:
        shutil.copyfile(os.path.join(a.template, f), os.path.join(a.out, f))
    with open(os.path.join(a.out, 'template-recipe.json'), 'w', encoding='utf-8', newline='\n') as handle:
        json.dump(recipe, handle, indent=1)
        handle.write('\n')
    print('surface template: scene.xscene derived byte for byte (%s); wrote %s and %s' % (sha(derived)[:12], ', '.join(OURS), 'template-recipe.json'))
    return 0


if __name__ == '__main__':
    sys.exit(main())
