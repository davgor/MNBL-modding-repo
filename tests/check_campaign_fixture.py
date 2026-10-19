"""Offline integration checks on the local, non-production boot fixture."""
import hashlib
import json
from pathlib import Path
import struct
import sys
import xml.etree.ElementTree as ET
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools/scenebin'))
import terrain_bin
import navmesh_bin
import build_slot

fixture = Path('build/campaign-fixture/mnbl_campaign_42')
manifest = json.loads(Path('build/campaign-fixture/manifest.json').read_text())
game = Path(build_slot.GAME)
root = ET.parse(fixture / 'scene.xscene').getroot()
assert root.get('name') == manifest['slot']
assert not manifest['productionReady']
names = {e.get('name') for e in root.findall('.//game_entity')}
ids = {e.get('id') for e in ET.parse(game/'Modules/NavalDLC/ModuleData/settlements.xml').findall('.//Settlement')}
# retirement_retreat is also absent from the official scene and generated via prefab.
assert ids - names <= {'retirement_retreat'}, ids - names
assert {'border_min', 'border_max'} <= names
assert any(e.get('prefab') == 'MapSnowAndRainDefiner' for e in root.findall('.//game_entity'))
assert any(e.get('name') == 'CampaignMapSiegePrefabEntityCache' for e in root.findall('.//script'))
assert root.find('terrain').get('vista_tileset') == ''
for name, expected in manifest['files'].items():
    assert hashlib.sha256((fixture/name).read_bytes()).hexdigest() == expected
terrain = terrain_bin.parse((fixture/'terrain.bin').read_bytes())
grid = terrain_bin.decode_heights(terrain, xscene_path=str(fixture/'scene.xscene'))
source = Path(manifest['source']['source']).read_bytes()
assert hashlib.sha256(source).hexdigest() == manifest['source']['sha256']
side, = struct.unpack_from('<I',source,8)
assert side == len(grid) == len(grid[0]) == 129
samples = struct.unpack_from('<%dd' % (side*side), source,12)
error = max(abs(grid[r][c] - samples[r*side+c]*.01) for r in range(side) for c in range(side))
assert error < .001, error
_, _, data = navmesh_bin.read_navmesh_file(str(fixture/'navmesh.bin'))
nav = navmesh_bin.decode_payload(data)
assert len(nav.faces) == manifest['navFaces']
assert not navmesh_bin.validate(nav)['errors']
result = dict(schema='mnbl.fixture-offline-check', passed=True, sourceSamples=side*side,
              maxHeightError=error, settlementIds=len(ids), missingSceneEntities=sorted(ids-names),
              navFaces=len(nav.faces), scope='terrain and bootstrap references; no runtime or gameplay claim')
Path('evidence/runtime/campaign-fixture-check.json').write_text(json.dumps(result,indent=2))
print(json.dumps(result,indent=2))
