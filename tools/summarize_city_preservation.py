"""MMR-251 city preservation over an offline seed sweep (tools/Run-SeedSweep.ps1): per seed the generation's attempts (seed, state, FWGC++
city count, reason codes), the accepted attempt and whether it is attempt 0, every town repair per town uid (travel-graph.json repairs whose
subject is a town: snap, town_snap, carve, footprint, access, join, coast_port as a forced port, move), the forced ports (ports.json
"forced": true), the preservation counts of the accepted world (FWGC++ sites, settlements.founding.placed_cities, compiled towns, id-map
towns, travel-graph towns, one uid set) and whether its bundle reaches the campaign offline: built settlements (Test-Settlements), the
runtime navigation build, validator, flags and settlement placement (Test-NavigationSweep) and no refusal from the activation harnesses, the
way tools/summarize_seed_yield.py reads them. Writes evidence/fwg/city-preservation.json.
Usage: python tools/summarize_city_preservation.py <sweep dir> <worldbridge.exe>
"""
import collections
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from summarize_seed_yield import REPO, SELF_TEST, harness_failures, rows, sha  # noqa: E402

OUT = os.path.join(REPO, 'evidence', 'fwg', 'city-preservation.json')
TOWN_CODES = {'quality.unplaceable_town', 'quality.unreachable_town'}


def load(path):
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def town_repairs(graph_path, ports_path):
    graph = load(graph_path)
    towns = {s['uid']: s for s in graph['settlements'] if s['kind'] == 'town'}
    repairs = collections.defaultdict(list)
    for x in graph['repairs']:
        if x['subject'] in towns:
            repairs[x['subject']].append(x['kind'])
    forced = sorted(p['townUid'] for p in load(ports_path)['ports'] if p.get('forced'))
    methods = collections.Counter(s['method'] for s in towns.values())
    return graph, {uid: kinds for uid, kinds in sorted(repairs.items())}, forced, dict(sorted(methods.items()))


def preservation(world_dir, travel_dir, bundle_dir):
    world = load(os.path.join(world_dir, 'world.json'))
    sites = sorted(s['uid'] for s in world['settlements']['sites'])
    placed = world['settlements']['founding'].get('placed_cities')
    campaign = load(os.path.join(travel_dir, 'campaign.json'))
    graph = load(os.path.join(travel_dir, 'travel-graph.json'))
    compiled = sorted(t['uid'] for t in campaign['towns'])
    travel = sorted(s['uid'] for s in graph['settlements'] if s['kind'] == 'town')
    ids = sorted(e['uid'] for e in load(os.path.join(bundle_dir, 'compiled', 'campaign', 'id-map.json'))['entries'] if e['kind'] == 'town') if bundle_dir else None
    return {'sites': len(sites), 'placedCities': placed, 'compiledTowns': len(compiled), 'idMapTowns': None if ids is None else len(ids), 'travelTowns': len(travel),
            'sameUidSet': sites == compiled == travel and ids in (None, sites), 'everyTownPlaced': all('cell' in s for s in graph['settlements'] if s['kind'] == 'town')}


def main():
    sweep, bridge = sys.argv[1], sys.argv[2]
    checks = {r['seed']: r for r in rows(os.path.join(sweep, 'settlement-checks.jsonl'))}
    navigation = {r['bundle']: r for r in rows(os.path.join(sweep, 'navigation', 'navigation-sweep.jsonl'))}
    harnesses = collections.defaultdict(list)
    for r in rows(os.path.join(sweep, 'harnesses', 'harnesses.jsonl')):
        harnesses[r['bundle']].append(r)
    bundle_root = os.path.join(sweep, 'bundles')
    bundles = {}
    for name in sorted(os.listdir(bundle_root)) if os.path.isdir(bundle_root) else []:
        graph = os.path.join(bundle_root, name, 'compiled', 'travel', 'travel-graph.json')
        if os.path.exists(os.path.join(bundle_root, name, 'DATA_READY')):
            bundles[sha(graph)] = name
    seeds = []
    for row in rows(os.path.join(sweep, 'sweep.jsonl')):
        attempts = []
        for a in row['attempts']:
            item = {'index': a['index'], 'seed': a['seed'], 'state': a['state'], 'cities': a['cities'], 'codes': a['codes']}
            if a.get('graph'):
                _, repairs, forced, methods = town_repairs(a['graph'], os.path.join(os.path.dirname(a['graph']), 'ports.json'))
                item.update({'townMethods': methods, 'townRepairs': repairs, 'forcedPorts': forced})
            attempts.append(item)
        entry = {'seed': row['seed'], 'status': row['status'], 'acceptedAttempt': row['chosen'], 'attempts': attempts}
        if row['status'] == 'accepted':
            chosen = next(a for a in row['attempts'] if a['index'] == row['chosen'])
            travel_dir = os.path.dirname(chosen['graph'])
            bundle = bundles.get(sha(chosen['graph']))
            entry['preservation'] = preservation(os.path.join(chosen['dir'], 'world'), travel_dir, os.path.join(bundle_root, bundle) if bundle else None)
            entry['bundle'] = bundle
            c = checks.get(row['seed'])
            fails = [f for f in (c or {}).get('fails') or [] if SELF_TEST not in f]
            nav = navigation.get(bundle) if bundle else None
            refusals, fixture = [], set()
            runs = harnesses.get(bundle[:12], []) if bundle else []
            for h in runs:
                r_, f_ = harness_failures(os.path.join(sweep, 'harnesses', '%s-%s.log' % (bundle[:12], h['harness'])))
                refusals += [{'harness': h['harness'], 'line': x} for x in r_]
                fixture |= {'%s: %s' % (h['harness'], x[:120]) for x in f_}
            built = c is not None and not fails
            navigable = nav is not None and nav.get('activatable') is True and not nav.get('flagsFailed') and nav.get('placementActivatable') is not False
            entry['reach'] = {
                'settlementsBuilt': built, 'settlementFailures': [f[:240] for f in fails],
                'navigationActivatable': navigable, 'navigationFailures': None if nav is None else (nav.get('failures') or []) + (nav.get('flagsFailed') or []),
                'placementProblems': None if nav is None else nav.get('placementProblems'),
                'harnessesRun': sorted(h['harness'] for h in runs), 'harnessRefusals': refusals, 'fixtureLines': sorted(fixture),
                'reachedCampaign': built and navigable and bool(runs) and not refusals,
            }
        seeds.append(entry)
    accepted = [s for s in seeds if s['status'] == 'accepted']
    attempt0 = [s for s in accepted if s['acceptedAttempt'] == 0]
    reached = [s for s in accepted if s['reach']['reachedCampaign']]
    all_attempts = [a for s in seeds for a in s['attempts']]
    town_rejections = [a for a in all_attempts if TOWN_CODES & set(a['codes'])]
    chosen_attempts = [next(a for a in s['attempts'] if a['index'] == s['acceptedAttempt']) for s in accepted]
    kinds = collections.Counter(k for a in chosen_attempts for ks in a['townRepairs'].values() for k in ks)
    n = len(seeds)
    doc = {
        'schema': 'mnbl.city-preservation', 'version': 1, 'ticket': 'MMR-251',
        'profile': 'worldgen/profiles/one-shot.json: recipe 3, size 129, phase 16; the module\'s policy list; maxAttempts 3 (derived seeds after attempt 0)',
        'tool': {'worldbridge': sha(bridge), 'producer': load(os.path.join(REPO, 'tools.lock.json'))['producer']['sha256'],
                 'playabilitySha256': sha(os.path.join(REPO, 'worldgen', 'policies', 'playability.json'))},
        'summary': {
            'seeds': n, 'accepted': len(accepted), 'acceptRate': round(len(accepted) / n, 3) if n else None,
            'attempt0': len(attempt0), 'attempt0Rate': round(len(attempt0) / n, 3) if n else None,
            'attempts': len(all_attempts), 'attemptsRejectedForTownCodes': len(town_rejections),
            'rejectionCodes': dict(sorted(collections.Counter(c for a in all_attempts if a['state'] == 'rejected' for c in a['codes']).items())),
            'preservationHolds': all(s['preservation']['sameUidSet'] and s['preservation']['everyTownPlaced'] and
                                     s['preservation']['sites'] == s['preservation']['placedCities'] == s['preservation']['compiledTowns'] == s['preservation']['idMapTowns'] == s['preservation']['travelTowns']
                                     for s in accepted),
            'towns': sum(s['preservation']['travelTowns'] for s in accepted),
            'townCounts': {str(s['seed']): s['preservation']['travelTowns'] for s in accepted},
            'repairedTowns': sum(len(a['townRepairs']) for a in chosen_attempts),
            'townRepairsByKind': dict(sorted(kinds.items())),
            'forcedPorts': sum(len(a['forcedPorts']) for a in chosen_attempts),
            'reachedCampaign': len(reached), 'reachRate': round(len(reached) / n, 3) if n else None,
        },
        'seeds': seeds,
        'note': ('acceptRate: requests accepted within 3 quality attempts; attempt0Rate: accepted on the requested seed itself. Town repairs '
                 '(docs/travel-graph.md "Town repairs") keep every FWGC++ city as a town, so no attempt is rejected for unplaceable_town or '
                 'unreachable_town; the remaining rejections are the non-town repair and exclusion bounds. The preservation counts are read '
                 'from the accepted attempt\'s split and travel outputs (the bundle holds the same bytes). reachedCampaign is the offline '
                 'BuildWorld pass: settlements built, runtime navigation with placement and flags activatable, and no refusal from the '
                 'activation harnesses (NavalContent, SpawnPolicy, GraphValidation, Factions, Characters, CampaignRegistry, WaterClimate); '
                 'fixtureLines are world A fixture counts those harnesses carry, not refusals. Nothing here ran in the game.'),
    }
    with open(OUT, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(doc, f, indent=1)
        f.write('\n')
    s = doc['summary']
    print('city preservation: %d seeds; accepted %d (attempt 0: %d); town-code rejections %d; forced ports %d; preservation %s; reached the campaign %d' %
          (n, s['accepted'], s['attempt0'], s['attemptsRejectedForTownCodes'], s['forcedPorts'], s['preservationHolds'], s['reachedCampaign']))


if __name__ == '__main__':
    main()
