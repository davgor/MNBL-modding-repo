"""MMR-33: write worldgen/policies/economy.json, the versioned settlement economy policy.

The installed game's own settlements give the bounded value distributions, never a formula that depends on Calradia's counts:
  * town prosperity: the sorted installed values, split into terciles that the compiler's town classes (small, medium, capital) use;
  * castle prosperity and village hearth: the sorted installed values;
  * building levels: per prosperity tercile (towns) and for castles, the median installed level of each building;
  * village types: per culture and production class, the installed village types with their installed counts as weights;
  * binding: the installed village-to-bound distances relative to the installed town spacing, the installed villages per town and
    castle, and the engine's own trade-bound limit;
  * balance: the installed villages and hearth per fief, the band a generated world must stay in;
  * food: each village type's daily food production (installed IL, FOOD below) and the installed towns' smallest trade-area supply;
  * village ports: the installed harbour villages' port-to-centre distances.
A generated value is a quantile of the installed distribution at the settlement's percentile among the generated settlements (of its
class), so it stays inside the installed range whatever the world's size, and raw population only orders settlements.
Usage: python tools/make_economy_policy.py
"""
import collections
import hashlib
import json
import math
import os
import statistics
import xml.etree.ElementTree as ET

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CATALOG = 'evidence/runtime/in-game/campaign_catalog-10212.json'
PRODUCTION = collections.OrderedDict([
    ('farming', ['wheat_farm', 'cattle_farm', 'swine_farm', 'sheep_farm', 'silk_plant', 'flax_plant', 'vineyard', 'olive_trees', 'date_farm',
                 'europe_horse_ranch', 'sturgian_horse_ranch', 'desert_horse_ranch', 'vlandian_horse_ranch', 'battanian_horse_ranch', 'steppe_horse_ranch']),
    ('fishing', ['fisherman', 'whaler', 'walrus_hunter']),
    ('resource', ['salt_mine', 'iron_mine', 'silver_mine', 'clay_mine', 'lumberjack', 'trapper']),
    ('harbor', ['fisherman', 'whaler', 'walrus_hunter']),
])
FISHING = ['fisherman', 'whaler', 'walrus_hunter']
# Daily food production of each village type at full production: the amounts of its products whose item category is food
# (ItemObject.IsFood: grain, fish, meat, cheese, butter, olives, date_fruit, grape). Installed IL, v1.4.8.119303 /
# NavalDLC v1.2.8.119303: DefaultVillageTypes.InitializeAll gives every type a primary production of grain (wheat_farm 50, every
# other type 3); DefaultVillageTypes.AddProductions adds fish 28 (fisherman), butter 4 + cheese 4 (cattle_farm), butter 2 + cheese 2
# (sheep_farm, swine_farm), date_fruit 8, olives 12, grape 11 (vineyard); NavalVillageTypes initializes whaler and walrus_hunter with
# fish 5 and no grain. The in-game fixture probe reads every registered VillageType's Productions and ItemObject.IsFood, and the fixture
# summary checks this table against them (run 30552 found grape is food: vineyard 14, not 3).
FOOD_ITEMS = ['grain', 'fish', 'meat', 'cheese', 'butter', 'olives', 'date_fruit', 'grape']
FOOD = collections.OrderedDict([
    ('battanian_horse_ranch', 3), ('cattle_farm', 11), ('clay_mine', 3), ('date_farm', 11), ('desert_horse_ranch', 3), ('europe_horse_ranch', 3),
    ('fisherman', 31), ('flax_plant', 3), ('iron_mine', 3), ('lumberjack', 3), ('olive_trees', 15), ('salt_mine', 3), ('sheep_farm', 7),
    ('silk_plant', 3), ('silver_mine', 3), ('steppe_horse_ranch', 3), ('sturgian_horse_ranch', 3), ('swine_farm', 7), ('trapper', 3),
    ('vineyard', 14), ('vlandian_horse_ranch', 3), ('walrus_hunter', 5), ('whaler', 5), ('wheat_farm', 50),
])
BAND = [0.75, 1.5]           # a generated world's villages and hearth per fief, as multiples of the installed values
DISTANCE_TOLERANCE = 1.25    # the preferred village-to-bound limit, as a multiple of the installed maximum ratio
ENGINE_TRADE_RATIO = 3.0     # DefaultVillageTradeModel: 3 x Campaign.GetAverageDistanceBetweenClosestTwoTownsWithNavigationType


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def od(*pairs):
    return collections.OrderedDict(pairs)


def main():
    receipt = json.load(open(os.path.join(REPO, CATALOG), encoding='utf-8-sig'))
    doc = [d for d in receipt['outputs']['documents'] if d['id'] == 'Settlements'][0]
    if sha(doc['path']) != doc['sha256']:
        raise SystemExit('catalog Settlements differs from its receipt')
    root = ET.parse(doc['path']).getroot()
    towns, castles, villages = [], [], []
    pos = {}
    for s in root:
        comps = s.find('Components')
        if comps is None or not len(comps):
            continue
        c = comps[0]
        pos[s.get('id')] = (float(s.get('posX')), float(s.get('posY')))
        if c.tag == 'Town':
            (castles if c.get('is_castle') == 'true' else towns).append((s, c))
        elif c.tag == 'Village':
            villages.append((s, c))
    town_prosperity = sorted(int(c.get('prosperity')) for s, c in towns)
    n = len(town_prosperity)
    terciles = [town_prosperity[:n // 3], town_prosperity[n // 3:2 * n // 3], town_prosperity[2 * n // 3:]]
    castle_prosperity = sorted(int(c.get('prosperity')) for s, c in castles)
    hearths = sorted(int(c.get('hearth')) for s, c in villages)

    def levels(items):
        acc = collections.defaultdict(list)
        for s, c in items:
            for b in c.iter('Building'):
                acc[b.get('id')].append(int(b.get('level')))
        return od(*[(k, int(round(statistics.median(v)))) for k, v in sorted(acc.items())])
    by_prosperity = sorted(towns, key=lambda x: int(x[1].get('prosperity')))
    town_levels = od(('small', levels(by_prosperity[:n // 3])), ('medium', levels(by_prosperity[n // 3:2 * n // 3])), ('capital', levels(by_prosperity[2 * n // 3:])))
    # The shipyard is the port rule's (shipyard below), not a tier's. A larger town never has a lower building level than a smaller
    # one: each tier takes at least the level of the tier below.
    for tier in town_levels.values():
        tier.pop('building_shipyard', None)
    for lower, upper in (('small', 'medium'), ('medium', 'capital')):
        for k, v in town_levels[lower].items():
            town_levels[upper][k] = max(town_levels[upper].get(k, 0), v)
    port_towns = [t for t in towns if t[0].find("Locations/Location[@id='port']") is not None]
    shipyard = sorted(int(b.get('level')) for s, c in port_towns for b in c.iter('Building') if b.get('id') == 'building_shipyard')
    weights = collections.OrderedDict()
    for s, c in villages:
        culture = s.get('culture').split('.', 1)[1]
        vtype = c.get('village_type').split('.', 1)[1]
        for production, kinds in PRODUCTION.items():
            if vtype in kinds:
                weights.setdefault(culture, collections.OrderedDict()).setdefault(production, collections.Counter())[vtype] += 1
    pools = od(*[(culture, od(*[(p, od(*sorted(w.items()))) for p, w in sorted(prods.items())])) for culture, prods in sorted(weights.items())])

    # Binding and balance from the installed geography (the installed map is not periodic).
    ids = {'town': [s.get('id') for s, c in towns], 'castle': [s.get('id') for s, c in castles]}
    kind = {i: k for k, v in ids.items() for i in v}
    dist = lambda a, b: math.hypot(pos[a][0] - pos[b][0], pos[a][1] - pos[b][1])
    spacing = statistics.mean(min(dist(t, u) for u in ids['town'] if u != t) for t in ids['town'])
    bound = {s.get('id'): c.get('bound').split('.', 1)[1] for s, c in villages}
    vtype = {s.get('id'): c.get('village_type').split('.', 1)[1] for s, c in villages}
    ratio = max(dist(v, b) / spacing for v, b in bound.items())
    per = collections.Counter(bound.values())
    town_counts = [per[t] for t in ids['town']]
    castle_counts = [per[c] for c in ids['castle']]
    fiefs = len(towns) + len(castles)
    # A town's trade area: its own villages and the villages of the castles nearest to it (the engine trade-binds a castle's
    # villages to the nearest town of the castle's faction).
    trade = {v: (b if kind[b] == 'town' else min(ids['town'], key=lambda t: dist(b, t))) for v, b in bound.items()}
    supply = sorted(sum(FOOD[vtype[v]] for v in trade if trade[v] == t) for t in ids['town'])
    missing = sorted({vtype[v] for v in vtype} - set(FOOD))
    if missing:
        raise SystemExit('village types without a food entry: %s' % missing)
    village_ports = sorted(math.hypot(float(s.get('port_posX')) - float(s.get('posX')), float(s.get('port_posY')) - float(s.get('posY')))
                           for s, c in villages if s.get('port_posX') is not None)
    fishing_ports = sum(1 for s, c in villages if c.get('village_type').split('.', 1)[1] in FISHING and s.get('port_posX') is not None)
    fishing_all = sum(1 for s, c in villages if c.get('village_type').split('.', 1)[1] in FISHING)
    port_centre = max(math.hypot(float(s.get('port_posX')) - float(s.get('posX')), float(s.get('port_posY')) - float(s.get('posY')))
                      for s, c in towns if s.get('port_posX') is not None)
    # MMR-248 (map epic 2, RT-08 and RT-14): the coastal village mix, the per-culture horse-ranch and wheat floors, and the initial wall
    # levels per prosperity tercile.
    coastal = collections.OrderedDict()
    for s, c in sorted(villages, key=lambda x: x[0].get('culture')):
        if s.get('port_posX') is None:
            continue
        row = coastal.setdefault(s.get('culture').split('.', 1)[1], [0, 0])
        row[0] += 1
        row[1] += c.get('village_type').split('.', 1)[1] in FISHING
    coastal_all = sum(v[0] for v in coastal.values())
    coastal_fishing = sum(v[1] for v in coastal.values())
    floors = collections.OrderedDict()
    for s, c in sorted(villages, key=lambda x: x[0].get('culture')):
        row = floors.setdefault(s.get('culture').split('.', 1)[1], collections.Counter())
        vt = c.get('village_type').split('.', 1)[1]
        row['villages'] += 1
        row['horseRanch'] += vt.endswith('_horse_ranch')
        row['wheat'] += vt == 'wheat_farm'

    def walls(items, building, names):
        ranked = sorted(items, key=lambda x: int(x[1].get('prosperity')))
        k = len(ranked)
        out = od()
        for name, part in zip(names, (ranked[:k // 3], ranked[k // 3:2 * k // 3], ranked[2 * k // 3:])):
            levels_ = collections.Counter(int(b.get('level')) for s, c in part for b in c.iter('Building') if b.get('id') == building)
            if sum(levels_.values()) != len(part):
                raise SystemExit('a fortification has no %s level' % building)
            out[name] = od(*[(str(lv), n) for lv, n in sorted(levels_.items())])
        return out

    policy = od(
        ('schema', 'mnbl.economy'), ('version', 2),
        ('notes', 'MMR-33 settlement economy (docs/settlements.md). Owner: SettlementFactory (src/MNBL.ProceduralWorld/Campaign/SettlementFactory.cs). '
                  'Every value is a quantile of the installed settlements\' own distribution at the settlement\'s percentile among the generated ones, '
                  'so it stays inside the installed range whatever the world\'s size. Food stocks, militia, gold, workshops and garrisons follow at new '
                  'game from these values through the installed behaviours.'),
        ('streams', od(('rule', 'value(stream, version, world prefix, entity, draw) = first 8 bytes of sha256("mnbl.settlement-stream/1\\n" + stream + "\\n" + '
                                'version + "\\n" + prefix + "\\n" + entity + "\\n" + draw), big-endian'),
                       ('prototypes', 1), ('villageTypes', 1))),
        ('prosperity', od(
            ('towns', od(('rule', 'the town\'s percentile of log(population) among the generated towns of its class (small, medium, capital), mapped to the '
                                  'quantile of the matching installed prosperity tercile'),
                         ('small', terciles[0]), ('medium', terciles[1]), ('capital', terciles[2]))),
            ('castles', od(('rule', 'the castle\'s garrison town\'s percentile of log(population) among the generated towns, mapped to the installed castle '
                                    'prosperity quantile'), ('values', castle_prosperity))))),
        ('hearth', od(('rule', 'a village\'s share of its town\'s rural population (rural / villages of the town), its percentile among the generated villages, '
                               'mapped to the installed hearth quantile'), ('values', hearths))),
        ('villageTypes', od(('rule', 'the source production class (farming, fishing, resource, harbor) picks from the village culture\'s installed village '
                                     'types of that class, weighted by their installed counts, from the villageTypes stream; a culture without that class '
                                     'falls back to all its installed types. A fishing type needs a port (installed: %d of %d fishing villages have one): '
                                     'a village without a port draws from its class without the fishing types, else from its culture\'s farming types'
                                     % (fishing_ports, fishing_all)),
                            ('classes', PRODUCTION), ('fishingTypes', FISHING), ('pools', pools))),
        ('villageMix', od(('rule', 'map epic 2 (MMR-252, RT-08): a coastal (harbour-capable) village draws a fishing type with probability '
                                   'coastalFishingShare, the installed share of fishing types among the installed villages with a port, else a '
                                   'non-fishing type of its class; every culture keeps a world-level floor of horse-ranch villages and wheat farms: at '
                                   'least max(1 when its installed pool has the type, floor(share x the generated villages of the culture)) of each, which '
                                   'VillagePool enforces and FoodPass never breaks'),
                          ('coastalFishingShare', round(coastal_fishing / float(coastal_all), 4)),
                          ('coastal', od(*[(k, od(('villages', v[0]), ('fishing', v[1]), ('share', round(v[1] / float(v[0]), 4)))) for k, v in coastal.items()])),
                          ('floors', od(*[(k, od(('villages', v['villages']), ('horseRanch', v['horseRanch']), ('wheat', v['wheat']),
                                                 ('horseRanchShare', round(v['horseRanch'] / float(v['villages']), 4)), ('wheatShare', round(v['wheat'] / float(v['villages']), 4))))
                                          for k, v in floors.items()])))),
        ('walls', od(('rule', 'map epic 2 (MMR-258, RT-14): the initial wall level of a town or castle (the fortifications building) is drawn from the '
                              'installed level counts of its prosperity tercile (towns small, medium, capital; castles low, middle, high, by castle '
                              'prosperity), weighted by count, from the settlement stream; not every wall starts at level 1'),
                     ('towns', walls(towns, 'building_settlement_fortifications', ('small', 'medium', 'capital'))),
                     ('castles', walls(castles, 'building_castle_fortifications', ('low', 'middle', 'high'))))),
        ('buildings', od(('rule', 'towns take the median installed building levels of their prosperity tercile (small, medium, capital); castles the median '
                                  'castle levels; a port town adds building_shipyard at the median installed port-town level, an inland town never has one'),
                         ('towns', town_levels), ('castles', levels(castles)), ('shipyard', int(round(statistics.median(shipyard)))))),
        ('binding', od(('rule', 'map epic 2 (MMR-252): the travel compiler\'s village quota binds exactly townVillages villages to every town and '
                                'castleVillages to every castle (campaign.json v2 bound, boundKind, sourceTown and villageQuota; playability.json villages; '
                                'docs/travel-graph.md "Village quota"), and the binding passes it through. A village farther from its bound settlement than '
                                'distance.preferredRatio x the world\'s town spacing is remote and must stay within distance.engineTradeRatio x spacing. A '
                                'town or castle without exactly its count (none at all crashes SnareTheWealthyIssueQuest, incident_the_quiet_life and '
                                'incident_wages_of_war_and_diseases), a remote village beyond the engine limit, and a castle village with no town of its '
                                'kingdom within the engine limit (its trade bound) are problems'),
                       ('castleVillages', 2), ('townVillages', 3),
                       ('distance', od(('rule', 'spacing: the mean periodic distance from each town to its nearest town in the generated world; '
                                                'preferredRatio = installedMaxRatio x tolerance; engineTradeRatio: the engine trade-binds a castle\'s '
                                                'village to a town within 3 x the average distance between the closest two towns '
                                                '(DefaultVillageTradeModel.TradeBoundDistanceLimitAsDays, installed IL)'),
                                       ('installedSpacing', round(spacing, 2)), ('installedMaxRatio', round(ratio, 4)), ('tolerance', DISTANCE_TOLERANCE),
                                       ('preferredRatio', round(ratio * DISTANCE_TOLERANCE, 4)), ('engineTradeRatio', ENGINE_TRADE_RATIO))),
                       ('installed', od(('townVillages', [min(town_counts), max(town_counts)]), ('castleVillages', [min(castle_counts), max(castle_counts)]),
                                        ('castlesWithTwo', castle_counts.count(2)), ('castles', len(castle_counts)),
                                        ('note', 'the installed counts are only roughly 3 and 2 (castles per town %.2f); the exact 3/2 rule is the '
                                                 'owner\'s (map epic 2), and its trade-area cost is recorded by MMR-247' % (len(castles) / float(len(towns)))))))),
        ('balance', od(('rule', 'a generated world\'s villages per fief and hearth per fief stay within band x the installed values, and every town and '
                                'castle has exactly its binding count'),
                       ('band', BAND),
                       ('installed', od(('villagesPerFief', round(len(villages) / float(fiefs), 3)),
                                        ('hearthPerFief', round(sum(int(c.get('hearth')) for s, c in villages) / float(fiefs), 1)))))),
        ('food', od(('rule', 'a town\'s trade-area food supply (the daily food production of the villages bound to it and to the castles whose trade '
                             'bound it is) is at least townMinimum, the smallest installed one; a town below it has its trade-area village with the '
                             'least food (nearest the town first) redrawn to the most food-producing type its culture allows for its class, else '
                             'among its culture\'s farming types (a fishing type only with a port), until it reaches it. Village production scales with hearth in game; the supply compares the types\' full production amounts, '
                             'computed the same way for the installed towns (a castle\'s villages counted with its nearest town)'),
                    ('items', FOOD_ITEMS), ('villageTypes', FOOD),
                    ('townMinimum', supply[0]),
                    ('installed', od(('towns', len(supply)), ('minimum', supply[0]), ('p10', supply[len(supply) // 10]), ('median', supply[len(supply) // 2]),
                                     ('maximum', supply[-1]))),
                    ('provenance', 'DefaultVillageTypes.InitializeAll and AddProductions, NavalVillageTypes.InitializeAll (installed IL); checked in game '
                                   'by the settlement fixture probe'))),
        ('positions', od(('rule', 'staged (pendingEngineValidation): centre and gate are the MMR-28 settlement point; a port town\'s port is its MMR-28 port '
                                  'candidate sea point, within the installed port-gate maximum plus one cell from the staged gate (the MMR-28 engine '
                                  'envelope installedPortGateMax 10.97; the installed port-centre maximum is %.2f); a harbour village\'s port is the '
                                  'centre of its MMR-28 harbour cell (a navigable sea cell), within the installed village port-centre maximum plus one '
                                  'cell; MMR-40 places gates and ports on the engine navmesh and MMR-35 compiles the landing metadata' % port_centre),
                         ('villagePorts', od(('installedPortCentreMax', round(village_ports[-1], 2)), ('installedPortCentreMedian', round(village_ports[len(village_ports) // 2], 2)),
                                             ('allowance', 6.25), ('installedCoastalVillages', len(village_ports)))),
                         ('installedTownPortCentreMax', round(port_centre, 2)))),
        ('texts', od(('town', 'The {kind} of {name}, in the {realm}.'), ('castle', 'The castle of {name}, in the {realm}.'),
                     ('village', 'The village of {name}, near {bound}.'), ('hideout', 'An outlaw hideout in the wilds.'),
                     ('sourceNote', ' Its old records name it {source}.'))),
        ('hideouts', od(('rule', 'SettlementFactory builds a hideout from a placed specification (bandit culture, position) with a prototype of that bandit '
                                 'culture; placing hideouts across a world is MMR-34\'s (bandits and hideouts)'),
                        ('cultures', sorted({s.get('culture').split('.', 1)[1] for s in root if s.find('Components') is not None and len(s.find('Components')) and s.find('Components')[0].tag == 'Hideout'})))),
        ('provenance', od(('catalogReceipt', CATALOG), ('settlementsSha256', doc['sha256']),
                          ('installed', od(('towns', len(towns)), ('castles', len(castles)), ('villages', len(villages)))))),
    )
    out = os.path.join(REPO, 'worldgen/policies/economy.json')
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(policy, f, indent=1)
        f.write('\n')
    print('wrote', out, 'town terciles', [(t[0], t[-1]) for t in terciles], 'castles', (castle_prosperity[0], castle_prosperity[-1]), 'hearth', (hearths[0], hearths[-1]))


if __name__ == '__main__':
    main()
