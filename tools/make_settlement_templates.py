"""MMR-33: write worldgen/templates/settlements/prototypes.json, the installed settlement prototypes generated settlements reuse.

A prototype supplies only assets: the component structure, location complex template, location scene names, common areas and
background/wait meshes. It never supplies identity, geography, ownership, economy or lore: SettlementFactory replaces the id, name,
text, culture, owner, positions, prosperity, hearth, village type and bound settlement. Prototypes are grouped per culture and kind:
town_port (has a port location), town_inland, castle, village_coastal (has a port position: the installed harbour villages, every
fishing village among them), village_inland, and hideout per bandit culture. Read from the merged campaign catalog (hash-checked
against its receipt).
Each prototype also names its map prefab: the old_prefab_name of the entity the vanilla campaign map (SandBox Main_map scene.xscene,
hash-pinned) places under the prototype's id (null when that entity is not a prefab instance), which a generated settlement without a
scene entity of its own can be given (MMR-33 fixtures; MMR-40 places the world's entities).
Usage: python tools/make_settlement_templates.py [--game GAME_ROOT]
"""
import argparse
import collections
import hashlib
import json
import os
import re
import xml.etree.ElementTree as ET

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CATALOG = 'evidence/runtime/in-game/campaign_catalog-10212.json'


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--game', default=r'D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord')
    a = ap.parse_args()
    main_map = os.path.join(a.game, 'Modules', 'SandBox', 'SceneObj', 'Main_map', 'scene.xscene')
    text = open(main_map, encoding='utf-8', errors='replace').read()
    prefabs = {m.group(1): m.group(2) for m in re.finditer(r'<game_entity name="([^"]+)" old_prefab_name="([^"]*)"', text)}
    receipt = json.load(open(os.path.join(REPO, CATALOG), encoding='utf-8-sig'))
    doc = [d for d in receipt['outputs']['documents'] if d['id'] == 'Settlements'][0]
    if sha(doc['path']) != doc['sha256']:
        raise SystemExit('catalog Settlements differs from its receipt')
    root = ET.parse(doc['path']).getroot()
    groups = collections.defaultdict(list)
    for s in root:
        comps = s.find('Components')
        if comps is None or not len(comps):
            continue
        c = comps[0]
        culture = (s.get('culture') or '').split('.', 1)[-1]
        locations = s.find('Locations')
        port = locations is not None and locations.find("Location[@id='port']") is not None
        if c.tag == 'Town':
            kind = 'castle' if c.get('is_castle') == 'true' else ('town_port' if port else 'town_inland')
        elif c.tag == 'Village':
            kind = 'village_coastal' if s.get('port_posX') is not None else 'village_inland'
        elif c.tag == 'Hideout':
            kind = 'hideout'
        else:
            continue
        groups[(culture, kind)].append(collections.OrderedDict([
            ('id', s.get('id')),
            ('complexTemplate', locations.get('complex_template') if locations is not None else None),
            ('locations', [l.get('id') for l in locations] if locations is not None else []),
            ('scenes', sorted({v for l in (locations if locations is not None else []) for k, v in l.attrib.items() if k.startswith('scene_name') and v})),
            ('backgroundMesh', c.get('background_mesh')), ('waitMesh', c.get('wait_mesh')),
            ('mapPrefab', prefabs.get(s.get('id')) if prefabs.get(s.get('id')) not in (None, '', '__empty_object') else None),
        ]))
    cultures = collections.OrderedDict()
    for (culture, kind), items in sorted(groups.items()):
        cultures.setdefault(culture, collections.OrderedDict())[kind] = sorted(items, key=lambda x: x['id'])
    out = collections.OrderedDict([
        ('schema', 'mnbl.settlement-prototypes'), ('version', 1),
        ('rule', 'assets only: component structure, location complex template, scenes, common areas and meshes; SettlementFactory replaces '
                 'id, name, text, culture, owner, positions and economy, and drops the prototype\'s lore text'),
        ('provenance', collections.OrderedDict([('catalogReceipt', CATALOG), ('settlementsSha256', doc['sha256']),
                                                ('mainMapScene', 'Modules/SandBox/SceneObj/Main_map/scene.xscene'), ('mainMapSceneSha256', sha(main_map))])),
        ('cultures', cultures),
    ])
    path = os.path.join(REPO, 'worldgen/templates/settlements/prototypes.json')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(out, f, indent=1)
        f.write('\n')
    print('wrote', path, {c: {k: len(v) for k, v in kinds.items()} for c, kinds in cultures.items()})


if __name__ == '__main__':
    main()
