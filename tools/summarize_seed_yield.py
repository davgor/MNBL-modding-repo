"""Seed yield at the one-shot profile, offline: one quality attempt per seed through worldbridge --generate (the module's policy list,
size 129, phase 16), then each accepted seed's bundle through the settlement harness tools/Test-Settlements.ps1 compiles (SettlementFactory
and VillageBinding, as activation builds the settlements), the navigation sweep (tools/Test-NavigationSweep.ps1: the runtime navigation
build, plan and validator over the stand-in engine, and the playability validator's host-check and transition flags from that report)
and the per-bundle activation harnesses (tools/Test-NavalContent.ps1, Test-SpawnPolicy,
Test-GraphValidation, Test-Factions, Test-Characters, Test-CampaignRegistry, Test-WaterClimate). Reads an earlier sweep and the current one
(each a directory holding sweep.jsonl and, for the current one, settlement-checks.jsonl, navigation/navigation-sweep.jsonl and
harnesses/harnesses.jsonl) and writes evidence/world/seed-yield.json.
The harness's "each is refused" line is its negative self-test, written against worlds A and B (it mutates a world and expects a refusal);
on other worlds a mutation can miss, so that line alone does not count as an activation failure. Likewise the activation harnesses carry
world A's fixture counts (ruler, slot and copy counts) and Test-CompiledWorld reads world A's layout report; on other worlds only an
exception thrown in the module's own code (the factories refuse by throwing: the first stack frame outside System is MNBL.ProceduralWorld)
outside a harness's negative self-test (its Refused helper) counts as an activation failure. An exception thrown in a harness's own code (a fixture lookup) and the other failed lines are listed as
fixture lines.
Usage: python tools/summarize_seed_yield.py <earlier sweep dir> <current sweep dir> <worldbridge.exe> [<reproduction sweep dir>...]
worldbridge.exe is the binary the evidence names; each reproduction sweep (generation and bundles only, made with that binary) must
publish the same bundle ids as the current sweep for its seeds, and reproduced records whether it did.
"""
import hashlib
import json
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(REPO, 'evidence', 'world', 'seed-yield.json')
SELF_TEST = 'each is refused:'


def rows(path):
    out = []
    if not os.path.exists(path):
        return out
    with open(path, encoding='utf-8-sig') as f:
        for line in f:
            line = line.strip()
            if line:
                out.append(json.loads(line))
    return out


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def harness_failures(log):
    """(refusals, fixture lines) of one harness log: an unexpected exception whose first stack frame outside System is the module's."""
    if not os.path.exists(log):
        return [], []
    with open(log, encoding='utf-8-sig', errors='replace') as f:
        lines = [l.rstrip('\n') for l in f]
    refusals, fixture = [], []
    for i, line in enumerate(lines):
        if not line.startswith('FAIL'):
            continue
        if 'unexpected exception' in line:
            frames = [l.strip()[3:] for l in lines[i + 1:i + 40] if l.strip().startswith('at ')]
            own = next((fr for fr in frames if not fr.startswith('System.')), '')
            # A harness's negative self-test (its Refused helper) mutates the world and expects one refusal; another is no activation failure.
            self_test = any('.Refused(' in fr for fr in frames)
            (refusals if own.startswith('MNBL.ProceduralWorld.') and not self_test else fixture).append(line[6:246])
        else:
            fixture.append(line[6:126])
    return refusals, fixture


def prefix_of(bundle):
    with open(os.path.join(bundle, 'compiled', 'campaign', 'campaign.json'), encoding='utf-8-sig') as f:
        town = json.load(f)['towns'][0]['id']
    return town[:town.index('_', 5) + 1]


def main():
    earlier, current, bridge, reproductions = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4:]
    before = {r['seed']: r for r in rows(os.path.join(earlier, 'sweep.jsonl'))}
    after = {r['seed']: r for r in rows(os.path.join(current, 'sweep.jsonl'))}
    checks = {r['seed']: r for r in rows(os.path.join(current, 'settlement-checks.jsonl'))}
    # Bundles to seeds through the world prefix the settlement harness records.
    seed_of_prefix = {}
    for seed in checks:
        path = os.path.join(current, 'settlements-%d.json' % seed)
        if os.path.exists(path):
            with open(path, encoding='utf-8-sig') as f: seed_of_prefix[json.load(f)['worldPrefix']] = seed
    bundles = {}
    root = os.path.join(current, 'bundles')
    for name in sorted(os.listdir(root)) if os.path.isdir(root) else []:
        if os.path.exists(os.path.join(root, name, 'DATA_READY')):
            seed = seed_of_prefix.get(prefix_of(os.path.join(root, name)))
            if seed is not None: bundles[seed] = name
    navigation = {r['bundle']: r for r in rows(os.path.join(current, 'navigation', 'navigation-sweep.jsonl'))}
    harnesses = {}
    for r in rows(os.path.join(current, 'harnesses', 'harnesses.jsonl')):
        harnesses.setdefault(r['bundle'], []).append(r)
    seeds = []
    for seed in sorted(after):
        a, b, c = after[seed], before.get(seed) or {}, checks.get(seed)
        fails = [f for f in (c or {}).get('fails') or [] if SELF_TEST not in f]
        bundle = bundles.get(seed)
        nav = navigation.get(bundle) if bundle else None
        runs = harnesses.get(bundle[:12], []) if bundle else []
        refusals, fixture = [], set()
        for h in runs:
            r_, f_ = harness_failures(os.path.join(current, 'harnesses', '%s-%s.log' % (bundle[:12], h['harness'])))
            if h['harness'] == 'CompiledWorld': f_, r_ = f_ + r_, []   # world A's layout report: no verdict on another world
            refusals += [{'harness': h['harness'], 'line': x} for x in r_]
            fixture |= {'%s: %s' % (h['harness'], x[:120]) for x in f_}
        fixture = sorted(fixture)
        seeds.append({
            'seed': seed,
            'before': {'status': b.get('status'), 'codes': sorted({x['code'] for x in b.get('reasons') or []})},
            'after': {'status': a.get('status'), 'codes': sorted({x['code'] for x in a.get('reasons') or []})},
            'bundle': bundle,
            'settlements': None if c is None else {'built': not fails, 'failures': [f[:240] for f in fails],
                                                   'selfTestOnly': bool(c.get('fails')) and not fails},
            # A failed host-check or transition flag refuses the world at runtime.playability as a failed route refuses it at navigation.
            'navigation': None if nav is None else {'activatable': nav.get('activatable') is True and not nav.get('flagsFailed'), 'error': nav.get('error'),
                                                    'diagnostics': (nav.get('buildDiagnostics') or []) + (nav.get('planDiagnostics') or []),
                                                    'failures': nav.get('failures') or [], 'flagsFailed': nav.get('flagsFailed')},
            'harnesses': None if not runs else {'run': sorted(h['harness'] for h in runs), 'refusals': refusals, 'fixtureLines': fixture},
        })
    reproduced = None
    if reproductions:
        names = set()
        for r in reproductions:
            root_r = os.path.join(r, 'bundles')
            names |= {n for n in os.listdir(root_r) if os.path.exists(os.path.join(root_r, n, 'DATA_READY'))} if os.path.isdir(root_r) else set()
        reproduced = {'sweeps': [os.path.relpath(r, REPO).replace(os.sep, '/') for r in reproductions], 'identical': names == set(bundles.values())}
    accepted_before = sum(1 for s in seeds if s['before']['status'] == 'accepted')
    accepted_after = [s for s in seeds if s['after']['status'] == 'accepted']
    built = [s for s in accepted_after if s['settlements'] and s['settlements']['built']]
    navigable = [s for s in accepted_after if s['navigation'] and s['navigation']['activatable']]
    activatable = [s for s in built if s in navigable and s['harnesses'] and not s['harnesses']['refusals']]
    doc = {
        'schema': 'mnbl.seed-yield', 'version': 1,
        'profile': 'worldgen/profiles/one-shot.json: recipe 3, size 129, phase 16; one quality attempt per seed (a request gets 3, with derived seeds)',
        'tool': {'worldbridge': sha(bridge), 'producer': json.load(open(os.path.join(REPO, 'tools.lock.json'), encoding='utf-8'))['producer']['sha256'],
                 'playabilitySha256': sha(os.path.join(REPO, 'worldgen', 'policies', 'playability.json'))},
        'reproduced': reproduced,
        'seeds': seeds,
        'summary': {'seeds': len(seeds), 'acceptedBefore': accepted_before, 'acceptedAfter': len(accepted_after),
                    'settlementsBuilt': len(built), 'navigationActivatable': len(navigable),
                    'offlineActivatable': len(activatable), 'rejectedAfter': {code: sum(1 for s in seeds if code in s['after']['codes'])
                                                                      for code in sorted({c for s in seeds for c in s['after']['codes']})}},
        'note': ('Before: the bridge that rejected a world for one unplaceable or unreachable town. After: the town repairs, the harbour-role '
                 'and trade-distance village repairs (docs/travel-graph.md) and the village binding\'s evening out (docs/settlements.md). '
                 'settlements.built is the activation\'s settlement build on the bundle (no game); navigation.activatable is the runtime '
                 'navigation build and validator over the stand-in engine, with no failed host-check or transition flag (flagsFailed: null '
                 'when the sweep did not resolve them); harnesses.refusals are exceptions from the activation\'s managed stages. '
                 'offlineActivatable counts the seeds that pass all three. Not run here: the runtime settlement placement '
                 '(SettlementPositionResolver). In game on 2026-10-19 seeds 56 and 67 were refused there (settlement.port_far); the in-game '
                 'activation of seeds other than 42 and 73 is otherwise not shown here.'),
    }
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(doc, f, indent=1)
        f.write('\n')
    print('seed yield: %d seeds; accepted %d -> %d; settlements built on %d; navigation activatable on %d; offline activatable %d' %
          (len(seeds), accepted_before, len(accepted_after), len(built), len(navigable), len(activatable)))


if __name__ == '__main__':
    main()
