"""Writes the minimal FWGC++ split fixture used by source_reader_tests (python make_fixture.py).

Record shapes follow the pinned producer's real exports (build/research-source23/split-129): settlement
sites, an empty heroes block, humans cores/hamlets (plain and harbour-shaped, including role "landing"), fortresses, key
locations, the four *_plans.files indexes and self-identifying plan files. Plan filenames deliberately
differ from the UIDs (a "-2" disambiguation suffix) so a reader must follow the index. Layers are 5 x 5
with a repeated seam column and constant pole rows. The file is pretty-printed so tests can check exact
line and column locations.
"""
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, 'valid')
SIDE = 5
SYSTEMS = ['tectonics', 'surface_detail', 'erosion', 'water', 'tectonic_transition', 'relic_carving', 'climate', 'terrain_labels',
           'leylines', 'astrology', 'ecology', 'biome_variants', 'terrain_detail', 'settlements', 'roads', 'humans', 'colleges',
           'seasonal_food', 'society', 'wildlife', 'threat_assessments', 'religion', 'age_transition', 'city_plans', 'hamlet_plans',
           'castle_plans', 'world_scene', 'nomads', 'beast_movements', 'encounters', 'nomad_effects', 'heroes', 'story_web', 'npcs',
           'key_locations', 'key_location_plans']
LAYERS = ['height', 'land', 'biome', 'landform', 'slope', 'temperature', 'moisture', 'water_depth', 'water_type', 'river', 'surface',
          'civilization_region', 'culture_region']


def raster(name, k):
    """Row 0 and row SIDE-1 constant; column SIDE-1 repeats column 0."""
    rows = []
    for r in range(SIDE):
        if r in (0, SIDE - 1):
            rows.append([k * 10 + r] * SIDE)
            continue
        row = [k * 10 + r + c for c in range(SIDE - 1)]
        rows.append(row + [row[0]])
    if name in ('height', 'temperature', 'moisture', 'slope'):
        rows = [[v + 0.25 for v in row] for row in rows]
    return rows


def city(site_id, uid, name, x, z, freshwater):
    return {'id': site_id, 'uid': uid, 'name': name, 'kind': 'city', 'city_class': 'capital' if site_id == 0 else 'town',
            'civilization_id': 'elf', 'layer': 'surface', 'node': x * SIDE + z, 'x': x, 'z': z, 'population_estimate': 4200 + site_id,
            'freshwater_distance_m': freshwater, 'height_m': 120.5, 'diaspora': None, 'suitability_factors': {'water': 0.8}}


def hamlet(hid, uid, core, role, x, z, harbour):
    row = {'access_cost': 803.4, 'access_nodes': [1, 2], 'core_id': core, 'culture_id': 'elf-1a2b', 'delivered_food': 17.2,
           'delivered_materials': 0, 'height_m': 148.3, 'id': hid, 'irrigation_benefit': 0, 'kind': 'hamlet', 'node': x * SIDE + z,
           'population_profile': 'elf', 'reason': 'Safe ground connection to city.', 'role': role, 'uid': uid, 'worked_area_km2': 1.03,
           'x': x, 'z': z}
    if harbour:
        row.update({'fishing_nodes': [7], 'harbor_quality': 0.86, 'landing_distance_m': 1524.9, 'layer': 'surface',
                    'monthly_fish': [1.5] * 12, 'sea_node': 9, 'trade_terminal': role != 'landing', 'winter_fishing_fraction': 0})
    return row


def main():
    cities = [city(0, 'surface-city-0-12-elf', 'Forimar', 1, 2, 812.5), city(1, 'surface-city-0-17-elf', 'Ashen Ford', 3, 2, None)]
    hamlets = [hamlet('hamlet-0', 'hamlet-node-6', 0, 'farming', 1, 1, False),
               hamlet('coastal-0-0', 'hamlet-node-11', 0, 'harbor + fishing', 2, 1, True),
               hamlet('coastal-1-0', 'hamlet-node-16', 1, 'landing', 3, 1, True)]
    fortress = {'access_cost': 3958.6, 'access_nodes': [3], 'core_id': 0, 'core_uid': cities[0]['uid'], 'culture_id': 'elf-1a2b',
                'defence_score': 1.85, 'height_m': 94.0, 'id': 'fortress-0', 'kind': 'fortress', 'node': 8, 'population_profile': 'elf',
                'protected_route_node': 8, 'raised_age': 0, 'reason': "Its city's road", 'uid': 'fortress-node-8', 'watch': 'road',
                'x': 1, 'z': 3}
    keyloc = {'asset_id': 'marker.key_location.academy_ruin', 'family': 'arcane', 'id': 'keyloc-academy_ruin-13', 'kind': 'academy_ruin',
              'layer': 'surface', 'links': {'chain': None, 'anchor': None}, 'name': 'the blind halls', 'node': 13, 'state': 'ruined'}
    world = {
        'schema': 'fwg.world', 'schema_version': 2, 'generator_version': 1, 'completed_stage': 16,
        'split': {'version': 1, 'scene': 'scene.json'},
        'config': {'world_size': 'tiny', 'seed': 42, 'size': SIDE},
        'topology': {'type': 'sphere', 'radius_m': 31830.99, 'longitude_deg': [-180, 180], 'latitude_deg': [90, -90], 'duplicate_seam': True},
        'seeds': {'layout': '1555514970434891308', 'crust': 600007910, 'detail': 805189603},
        'water': {'version': 1, 'nodes': 25}, 'climate': {'version': 1}, 'terrain': {'version': 6},
        'settlements': {'version': 16, 'population_profile': 'mixed', 'sites': cities},
        'civilizations': {'version': 3, 'registry': {'elf': {}}},
        'roads': {'version': 1, 'routes': []},
        'heroes': {'version': 6, 'status': 'ok', 'realms': [], 'people': []},
        'npcs': {'version': 3, 'status': 'ok', 'people': [], 'life_spans': {'parent_races': [], 'peoples': []}},
        'nomads': {'version': 1, 'groups': []},
        'humans': {'version': 10, 'population_profile': 'mixed',
                   'cores': [{'culture_id': 'elf-1a2b', 'fortress_ids': ['fortress-0'], 'hamlet_ids': ['hamlet-0', 'coastal-0-0'], 'site_id': 0},
                             {'culture_id': 'elf-1a2b', 'fortress_ids': [], 'hamlet_ids': ['coastal-1-0'], 'site_id': 1}],
                   'hamlets': hamlets, 'fortresses': [fortress]},
        'future_block': {'version': 99, 'note': 'unknown optional block; a reader must skip it'},
        'city_plans': {'version': 8, 'files': [{'city_uid': c['uid'], 'path': 'plans/city/' + c['uid'] + '-2.json'} for c in cities]},
        'hamlet_plans': {'version': 3, 'files': [{'hamlet_id': h['id'], 'path': 'plans/hamlet/' + h['id'] + '-2.json'} for h in hamlets]},
        'castle_plans': {'version': 3, 'files': [{'fortress_id': 'fortress-0', 'path': 'plans/castle/fortress-0-2.json'}]},
        'key_locations': {'version': 2, 'sites': [keyloc]},
        'key_location_plans': {'version': 1, 'files': [{'location_id': keyloc['id'], 'path': 'plans/key_location/' + keyloc['id'] + '-2.json'}]},
        'systems': SYSTEMS,
        'layers': {name: raster(name, k) for k, name in enumerate(LAYERS)},
        'warnings': [],
    }
    os.makedirs(OUT, exist_ok=True)
    for kind in ('city', 'hamlet', 'castle', 'key_location'):
        os.makedirs(os.path.join(OUT, 'plans', kind), exist_ok=True)

    def dump(path, value):
        with open(os.path.join(OUT, path), 'w', encoding='utf-8', newline='\n') as f:
            json.dump(value, f, indent=1)
            f.write('\n')

    dump('world.json', world)
    dump('scene.json', {'version': 1, 'unit': 'metres', 'coordinates': 'globe-centred XYZ; Y north', 'radius_m': 31830.99,
                        'terrain_detail': {'version': 1}, 'buildings': [], 'streets': [], 'junctions': [], 'regional_roads': [],
                        'limits': 'Fixture scene with no geometry.'})
    for c in cities:
        dump('plans/city/' + c['uid'] + '-2.json', {'version': 8, 'city_uid': c['uid'], 'site_id': c['id'], 'name': c['name'],
                                                    'fortifications': None, 'plots': [], 'status': 'complete'})
    for h in hamlets:
        dump('plans/hamlet/' + h['id'] + '-2.json', {'version': 3, 'hamlet_id': h['id'], 'hamlet_uid': h['uid'], 'kind': 'hamlet',
                                                     'role': h['role'], 'core_city_uid': cities[h['core_id']]['uid'], 'core_id': h['core_id'],
                                                     'plots': [], 'status': 'partial'})
    dump('plans/castle/fortress-0-2.json', {'version': 3, 'kind': 'castle', 'fortress_id': 'fortress-0', 'fortress_uid': 'fortress-node-8',
                                            'core_id': 0, 'plots': [], 'status': 'complete'})
    dump('plans/key_location/' + keyloc['id'] + '-2.json', {'location_id': keyloc['id'], 'kind': 'academy_ruin', 'version': 1,
                                                            'status': 'complete', 'plots': []})
    print('wrote', OUT)


if __name__ == '__main__':
    main()
