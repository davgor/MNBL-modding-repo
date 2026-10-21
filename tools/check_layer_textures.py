"""MMR-248: check that every texture the shared layer set names (worldgen/policies/terrain-materials.json layerSet) is installed and
flagged for terrain use.

The installed packs (Modules/*/AssetPackages and EmAssetPackages, *.tpac) list each texture record as its name followed by its source
path ($BASE/Modules/<module>/AssetSources/.../<name>.<ext>) and its flags; a terrain layer texture must carry for_terrain. Only the
record headers are read (the first 4 MiB of each pack, which holds every record of every installed pack; the scan reports a pack whose
records run past that). Usage: python tools/check_layer_textures.py [--game GAME_ROOT] [--out JSON]
Exit 1 when a name is missing or not flagged; 2 when the game is not installed.
"""
import argparse
import json
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RECORD = re.compile(rb'\$BASE/Modules/([A-Za-z0-9_]+)/AssetSources/([^\x00-\x1f]*?)/([a-z0-9_]+)\.(psd|tif|tga|png|dds|jpg|bmp)', re.I)
WINDOW = 4 << 20


def scan(modules):
    found, truncated = {}, []
    for root, _, files in os.walk(modules):
        for name in files:
            if not name.endswith('.tpac'):
                continue
            path = os.path.join(root, name)
            with open(path, 'rb') as f:
                head = f.read(WINDOW)
            records = list(RECORD.finditer(head))
            for i, m in enumerate(records):
                end = records[i + 1].start() if i + 1 < len(records) else min(len(head), m.end() + 400)
                rec = found.setdefault(m.group(3).decode(), {'pack': os.path.relpath(path, modules).replace('\\', '/'), 'forTerrain': False})
                rec['forTerrain'] = rec['forTerrain'] or b'for_terrain' in head[m.end():end]
            if records and len(head) == WINDOW and records[-1].end() > WINDOW - 200000:
                truncated.append(os.path.relpath(path, modules).replace('\\', '/'))
    return found, truncated


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--game', default=os.environ.get('MNBL_GAME_DIR', r'D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord'))
    ap.add_argument('--out')
    a = ap.parse_args()
    modules = os.path.join(a.game, 'Modules')
    if not os.path.isdir(modules):
        print('no game at', a.game)
        return 2
    policy = json.load(open(os.path.join(REPO, 'worldgen/policies/terrain-materials.json'), encoding='utf-8-sig'))
    found, truncated = scan(modules)
    rows, bad = [], []
    for layer in policy['layerSet']:
        for role in ('diffuse', 'normal', 'specular', 'height'):
            name = layer[role]
            if name == 'none':
                continue
            rec = found.get(name)
            ok = bool(rec and rec['forTerrain'])
            rows.append({'slot': layer['slot'], 'layer': layer['name'], 'role': role, 'texture': name, 'installed': rec is not None,
                         'forTerrain': bool(rec and rec['forTerrain']), 'pack': rec['pack'] if rec else None})
            if not ok:
                bad.append(name)
    doc = {'schema': 'mnbl.layer-textures', 'version': 1, 'ticket': 'MMR-248', 'textures': len(rows), 'missingOrNotTerrain': bad,
           'packsWithRecordsPastWindow': truncated, 'rows': rows}
    if a.out:
        with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
            json.dump(doc, f, indent=1)
            f.write('\n')
    print('%d layer textures checked, %d missing or not flagged for terrain%s' % (len(rows), len(bad), (': ' + ', '.join(bad)) if bad else ''))
    if truncated:
        print('packs whose records run past the scan window (names there are not seen):', ', '.join(truncated))
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
