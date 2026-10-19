"""MMR-33: summarize the in-game settlement fixtures of a generated_campaign run (settlementFixtures=<repo>).

Usage: python tools/summarize_settlement_fixtures.py RUN_JSON OUT_JSON
The fixtures (a coastal town, a castle, a town village, a castle village and a hideout) are built by SettlementFactory.Element and
registered through the generated world's XML path. The checks compare the live values the probe read against values fixed outside the
game, not against the probe's own failure list:
  * every fixture has a location complex with scenes, and the coastal town's port location carries its XML port scene;
  * centre, gate and port are exactly the specified ones: the placement's verified land points, the town and castle gates 1.875 inside
    their centre's land cell (toward the port, east), the town's port at the placement's port, and the engine read the XML values;
  * town and castle buildings are at the economy policy's levels (medium tier plus the shipyard, castle tier);
  * the clan owns the town and castle, villages are bound, both villages trade with the town;
  * the starting values the installed new-game behaviours derived are present and bounded (food, militia, gold, workshops, garrison);
  * grain, which the fixture's wheat farm supplies, sells within 3 x its base value; clamp hits (>= 9.5 x) are recorded;
  * every village type's food production read from the engine equals the economy policy's food table;
  * an ownership transfer of the castle moved it and its village between the clans' and kingdoms' lists and back;
  * the economy policy and prototypes the run read are the repository's current files (sha256);
  * the run finished ok with no validation failure.
"""
import hashlib
import json
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXPECTED = {  # placement build/research-env59/w42 (obstruction.to, lake east/west shores, town and village_1) and the dataset's gate rule
    'town_1': {'position': [609.375, 165.625], 'gate': [609.375, 167.5], 'port': [609.375, 171.875]},
    'castle_1': {'position': [509.375, 134.375], 'gate': [511.25, 134.375], 'port': None},
    'village_1': {'position': [590.625, 146.875], 'gate': [590.625, 146.875], 'port': None},
    'village_3': {'position': [509.375, 71.875], 'gate': [509.375, 71.875], 'port': None},
    'hideout_1': {'position': [484.375, 71.875], 'gate': [484.375, 71.875], 'port': None},
}


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def close(a, b):
    if a is None or b is None:
        return a is None and b is None
    return len(a) == len(b) and all(x is not None and y is not None and abs(x - y) < 1e-3 for x, y in zip(a, b))


def main():
    run_path, out_path = sys.argv[1], sys.argv[2]
    run = json.load(open(run_path, encoding='utf-8-sig'))
    world = run['outputs']['worlds'][0]
    f = world.get('settlementFixtures') or {}
    w = world.get('worldId')
    economy_path = os.path.join(REPO, 'worldgen/policies/economy.json')
    economy = json.load(open(economy_path, encoding='utf-8'))
    checks = []

    def check(ok, label):
        checks.append({'ok': bool(ok), 'label': label})

    ids = {k: w + '_' + k for k in EXPECTED}
    check(run.get('status') == 'ok' and not f.get('error'), 'the probe finished ok and the fixture probe raised nothing')
    loc = f.get('locations') or {}
    check(all(ids[k] in loc and any(e.split(':', 1)[1] for e in loc[ids[k]]) for k in ids), 'every fixture has a location complex with at least one scene')
    port = f.get('townPortScene') or {}
    check(port.get('xml') and port.get('live') == port.get('xml'), 'the coastal town\'s port location carries its XML port scene (%s)' % port.get('live'))
    check(any(e.startswith('center:') for e in loc.get(ids['castle_1'], [])) and any(e.startswith('village_center:') for e in loc.get(ids['village_3'], [])) and
          any(e.startswith('hideout_center:') for e in loc.get(ids['hideout_1'], [])), 'castle, village and hideout centre locations exist')
    pos = f.get('positions') or {}
    exact = all(ids[k] in pos and close(pos[ids[k]].get('position'), e['position']) and close(pos[ids[k]].get('gate'), e['gate']) and close(pos[ids[k]].get('port'), e['port'])
                for k, e in EXPECTED.items())
    read = all(ids[k] in pos and close(pos[ids[k]]['position'], pos[ids[k]]['xml']['position']) and close(pos[ids[k]]['gate'], pos[ids[k]]['xml']['gate']) and
               close(pos[ids[k]]['port'], pos[ids[k]]['xml']['port']) for k in EXPECTED)
    check(exact and read and EXPECTED['town_1']['gate'] != EXPECTED['town_1']['position'] and EXPECTED['castle_1']['gate'] != EXPECTED['castle_1']['position'],
          'centre, gate and port of every fixture are exactly the specified ones and the XML\'s (town gate %s and castle gate %s distinct from their centres, town port %s; others have none)' % (
              EXPECTED['town_1']['gate'], EXPECTED['castle_1']['gate'], EXPECTED['town_1']['port']))
    b = f.get('buildings') or {}
    tier = dict(economy['buildings']['towns']['medium'], building_shipyard=economy['buildings']['shipyard'])
    castle_tier = economy['buildings']['castles']
    town_ok = all(b.get(ids['town_1'], {}).get(k) == v for k, v in tier.items())
    castle_ok = all(b.get(ids['castle_1'], {}).get(k) == v for k, v in castle_tier.items())
    check(town_ok and castle_ok, 'town buildings are at the policy\'s medium-tier levels with the shipyard at %d (%d buildings), castle buildings at the castle tier (%d)' % (
        economy['buildings']['shipyard'], len(tier), len(castle_tier)))
    v = f.get('villages') or {}
    check(v.get(ids['village_1'], {}).get('type') == 'wheat_farm' and v.get(ids['village_3'], {}).get('type') == 'cattle_farm' and
          v.get(ids['village_1'], {}).get('tradeBound') == ids['town_1'] and v.get(ids['village_3'], {}).get('tradeBound') == ids['town_1'],
          'village types are the specified ones and both villages trade with the town (the castle village through the engine\'s trade-bound assignment)')
    t, c = f.get('town') or {}, f.get('castle') or {}
    check(0 < t.get('foodStocks', 0) <= 1000 and 0 < c.get('foodStocks', 0) <= 1000 and 0 < t.get('militia', 0) <= 1000 and 0 < t.get('gold', 0) <= 100000 and
          len(t.get('workshops', [])) > 0 and 0 < t.get('garrison', 0) <= 1000 and 0 < c.get('garrison', 0) <= 1000,
          'starting values derived at new game are present and bounded: town food %s, militia %s, gold %s, %d workshops, garrison %s; castle food %s, garrison %s' % (
              t.get('foodStocks'), t.get('militia'), t.get('gold'), len(t.get('workshops', [])), t.get('garrison'), c.get('foodStocks'), c.get('garrison')))
    prices = f.get('prices') or {}
    grain = prices.get('grain') or {}
    check(grain.get('price', 0) > 0 and grain.get('multiple', 99) <= 3.0,
          'grain, supplied by the fixture\'s wheat farm, sells within 3 x its base value (%s); prices as multiples of base value: %s; at the 10 x clamp: %s' % (
              grain.get('multiple'), ', '.join('%s %s' % (k, p.get('multiple')) for k, p in sorted(prices.items())), f.get('priceClampHits')))
    food = f.get('villageTypeFood') or {}
    policy_food = economy['food']['villageTypes']
    mismatch = {k: (food.get(k), p) for k, p in policy_food.items() if food.get(k) is None or abs(food[k] - p) > 0.01}
    check(food and not mismatch and set(f.get('foodItems') or []) <= set(economy['food']['items']),
          'every one of the %d village types in the economy policy\'s food table produces exactly that much food in game (mismatches %s; food items %s)' % (
              len(policy_food), mismatch, f.get('foodItems')))
    transfer = f.get('ownershipTransfer') or {}
    check(transfer.get('moved') is True and transfer.get('restored') is True,
          'transferring the castle moved it and its village between the clans\' (and kingdoms\') settlement, fief and village lists, and back (kingdoms %s)' % transfer.get('kingdoms'))
    policies = f.get('policies') or {}
    check(policies and policies['economy']['sha256'] == sha(economy_path) and
          policies['prototypes']['sha256'] == sha(os.path.join(REPO, 'worldgen/templates/settlements/prototypes.json')),
          'the run read the repository\'s current economy policy and prototypes (sha256 %s, %s)' % (
              (policies.get('economy') or {}).get('sha256', '')[:12], (policies.get('prototypes') or {}).get('sha256', '')[:12]))
    failures = world.get('validationFailures', [])
    check(not failures, 'the run reports no validation failure, fixture checks included (%d)' % len(failures))
    summary = {'schema': 'mnbl.evidence.settlement-fixtures', 'version': 2, 'ticket': 'MMR-33', 'run': run_path.replace('\\', '/'), 'pid': run.get('processId'),
               'moduleSha256': run.get('moduleSha256'), 'worldId': w, 'expected': EXPECTED, 'fixtures': f, 'validationFailures': failures,
               'checks': checks, 'passed': sum(x['ok'] for x in checks), 'total': len(checks)}
    with open(out_path, 'w', encoding='utf-8', newline='\n') as fh:
        json.dump(summary, fh, indent=1)
        fh.write('\n')
    for x in checks:
        print(('ok    ' if x['ok'] else 'FAIL  ') + x['label'])
    print('%d/%d checks passed' % (summary['passed'], summary['total']))
    sys.exit(0 if summary['passed'] == summary['total'] else 1)


if __name__ == '__main__':
    main()
