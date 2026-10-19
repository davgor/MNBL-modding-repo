"""Local boot fixture: FWGC terrain with installed campaign scaffolding.

Not a production world: retains official settlement XY, nav topology and caches.
Only build/ gets written; never package the extracted official scene assets.
"""
import copy
import hashlib
import json
from pathlib import Path
import shutil
from types import SimpleNamespace
import xml.etree.ElementTree as ET
import build_slot
import terrain_bin
import navmesh_bin


def main():
    out = Path('build/campaign-fixture')
    slot = 'mnbl_campaign_42'
    args = SimpleNamespace(nodes=4, level=5, node_size=260.0, vertical_scale=0.01,
        surface='fwgc:build/heightfield-source42/heightfield.bin', nav_cell=8.125,
        mountain_slope=1.0, out=str(out), slot=slot)
    report = build_slot.build(args)
    dest = out / slot
    game = Path(build_slot.GAME)
    official = game / 'Modules/NavalDLC/SceneObj/Main_map'
    root = ET.parse(official / 'scene.xscene').getroot()
    root.set('name', slot)
    generated = ET.parse(dest / 'scene.xscene').getroot()
    old = root.find('terrain')
    terrain = copy.deepcopy(generated.find('terrain'))
    # Eliminate stock horizon geometry, which is unrelated to the generated world.
    outer = terrain.find('outer_mesh')
    if outer is not None:
        for variable in outer:
            if variable.get('name') == 'outer_mesh_name': variable.set('value', '')
    root.remove(old)
    root.append(terrain)
    grid = terrain_bin.decode_heights(terrain_bin.parse((dest / 'terrain.bin').read_bytes()),
        report['minHeight'], report['maxHeight'])
    def height(x, y):
        return build_slot.interpolate(grid, min(1039.99,max(0,x)), min(1039.99,max(0,y)), 8.125)[1]

    # Retain settlement child tags, shared prefab caches, weather and naval support.
    # Drop stock landscape decoration and river geometry, not campaign entities.
    entities = root.find('entities')
    kept, removed, relocated = [], [], []
    support = {'SettlementPositionScript','Town Scene Manager','CampaignMapSiegePrefabEntityCache',
               'MapColorGradeManager','PirateSpawnPoint','SceneLeveler','MapAtmosphereProbe'}
    for entity in list(entities):
        name = entity.get('name', '')
        scripts = {s.get('name') for s in entity.findall('./scripts/script')}
        settlement = name.startswith(('town_', 'castle_', 'village_', 'hideout_', 'retirement_'))
        keep = settlement or name.startswith('border_') or bool(scripts & support) or entity.get('prefab') == 'MapSnowAndRainDefiner'
        if not keep:
            entities.remove(entity)
            removed.append(name or entity.get('prefab'))
            continue
        kept.append(name or entity.get('prefab'))
        if settlement:
            transform = entity.find('transform')
            if transform is not None:
                xyz = [float(x.strip()) for x in transform.get('position').split(',')]
                xyz[2] = height(xyz[0],xyz[1])
                transform.set('position', ', '.join('%.3f' % v for v in xyz))
                relocated.append(name)
    # Existing IDs/navigation regions remain bootstrap dependencies, conformed to
    # our terrain heights. This is explicitly not generated-world navigation.
    _, _, payload = navmesh_bin.read_navmesh_file(str(official / 'navmesh.bin'))
    nav = navmesh_bin.decode_payload(payload)
    nav.vertices = [(x,y,height(x,y)+.2) for x,y,z in nav.vertices]
    navmesh_bin.write_scene_navmesh(nav, str(dest / 'navmesh.bin'))
    ET.indent(root, space='\t')
    ET.ElementTree(root).write(dest / 'scene.xscene', encoding='utf-8', xml_declaration=True)
    # Campaign atmosphere includes seasonal presets; no stock TileSets or terrain.
    shutil.copyfile(official / 'atmosphere.xml', dest / 'atmosphere.xml')
    info = dict(schema='mnbl.campaign-bootstrap-fixture', version=1, slot=slot,
        productionReady=False, source=report['surface'], extent=[1040,1040],
        retainedOfficialContent=['settlement identities and XY', 'navmesh XY/topology/region IDs',
            'campaign bootstrap helper entities', 'global weather/battle grids and distance caches'],
        navFaces=len(nav.faces), entitiesKept=len(kept), entitiesRemoved=len(removed),
        settlementsGrounded=len(relocated), files={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in dest.iterdir() if p.is_file()})
    (out / 'manifest.json').write_text(json.dumps(info,indent=2))
    print(json.dumps(info,indent=2))


if __name__ == '__main__': main()
