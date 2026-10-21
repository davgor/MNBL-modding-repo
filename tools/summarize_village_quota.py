"""MMR-252: the village quota over offline generation sweeps (tools/Run-SeedSweep.ps1 -Stage generate) -> evidence/fwg/village-quota.json.

Per accepted world (the chosen attempt's travel/ outputs): towns, castles and villages, whether every town has exactly
villages.townVillages and every castle castleVillages bound villages (campaign.json v2 bound), the villageQuota counts, the synthesized
villages by tier, the islet exceptions, the villages closer than the clearance to a fortification (recomputed from travel-graph.json
points, periodic), settlements sharing a point, forced ports and whether each has its pinned synthesized harbour village, whether every
valid port's chosen harbour village is bound to its town, castles the quota excluded, and the second analysis' stability.
Usage: python tools/summarize_village_quota.py OUT.json SWEEP_DIR [SWEEP_DIR ...]
"""
import collections
import json
import math
import os
import sys


def load(path):
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def world_row(seed, attempt, travel):
    c = load(os.path.join(travel, 'campaign.json'))
    g = load(os.path.join(travel, 'travel-graph.json'))
    p = load(os.path.join(travel, 'ports.json'))
    q = c['villageQuota']
    bound = collections.Counter(v['bound'] for v in c['villages'])
    exact = all(bound[t['id']] == q['townVillages'] for t in c['towns']) and all(bound[k['id']] == q['castleVillages'] for k in c['castles'])
    zero = [x['id'] for x in c['towns'] + c['castles'] if bound[x['id']] == 0]
    villages = {v['id']: v for v in c['villages']}
    valid = [x for x in p['ports'] if x['valid']]
    forced = [x for x in valid if x.get('forced')]
    width = g['grid']['cols'] * g['grid']['spacing']
    gone = {e['id'] for e in g['exclusions']}
    points = {s['id']: s['point'] for s in g['settlements'] if 'point' in s and s['id'] not in gone}

    def dist(a, b):
        dx = abs(a['x'] - b['x']); dx = min(dx, width - dx); return math.hypot(dx, a['y'] - b['y'])
    forts = [points[x['id']] for x in c['towns'] + c['castles']]
    near = sorted(round(min(dist(points[v], f) for f in forts), 3) for v in villages if min(dist(points[v], f) for f in forts) < 4.0)
    ids = sorted(points)
    shared = sum(1 for i in range(len(ids)) for j in range(i + 1, len(ids)) if dist(points[ids[i]], points[ids[j]]) < 1e-9)
    return collections.OrderedDict([
        ('seed', seed), ('attempt', attempt), ('towns', len(c['towns'])), ('castles', len(c['castles'])), ('villages', len(c['villages'])),
        ('exact', exact), ('zeroVillageFortifications', len(zero)), ('version', c['version']),
        ('kept', q['kept']), ('reassigned', q['reassigned']), ('dropped', q['dropped']), ('synthesized', q['synthesized']),
        ('synthesizedByTier', q['synthesizedByTier']), ('protectedKept', q['protectedKept']), ('clearanceMoves', q['clearanceMoves']),
        ('movedNotables', len(q['movedNotables'])), ('exceptions', len(q['exceptions'])), ('villagesWithinClearance', near), ('sharedPoints', shared),
        ('validPorts', len(valid)), ('forcedPorts', len(forced)),
        ('forcedPortsWithHarbourVillage', sum(1 for x in forced if villages.get(x['chosen'].get('village'), {}).get('pinned') and villages[x['chosen']['village']]['origin'] == 'synthesized')),
        ('pinnedPortsBound', all(villages[x['chosen']['village']]['bound'] == x['town'] for x in valid if x['chosen'].get('village') in villages)),
        ('castlesExcludedByQuota', sum(1 for e in g['exclusions'] if e['kind'] == 'castle' and e['reason'].startswith('quota'))),
        ('passes', q['passes']), ('stable', q['stable'])])


def main():
    out, sweeps = sys.argv[1], sys.argv[2:]
    rows = []
    for sweep in sweeps:
        for line in open(os.path.join(sweep, 'sweep.jsonl'), encoding='utf-8-sig'):
            r = json.loads(line)
            if r['status'] != 'accepted':
                rows.append(collections.OrderedDict([('seed', r['seed']), ('status', r['status'])]))
                continue
            a = r['attempts'][r['chosen']]
            rows.append(world_row(r['seed'], a['index'], os.path.join(a['dir'], 'travel')))
    rows.sort(key=lambda x: x['seed'])
    worlds = [x for x in rows if 'exact' in x]
    tiers = collections.Counter()
    for w in worlds:
        tiers.update(w['synthesizedByTier'])
    summary = collections.OrderedDict([
        ('worlds', len(worlds)), ('exact', sum(w['exact'] for w in worlds)), ('zeroVillageFortifications', sum(w['zeroVillageFortifications'] for w in worlds)),
        ('synthesized', sum(w['synthesized'] for w in worlds)), ('synthesizedByTier', dict(sorted(tiers.items()))), ('dropped', sum(w['dropped'] for w in worlds)),
        ('exceptions', sum(w['exceptions'] for w in worlds)), ('villagesWithinClearance', sum(len(w['villagesWithinClearance']) for w in worlds)),
        ('sharedPoints', sum(w['sharedPoints'] for w in worlds)), ('forcedPorts', sum(w['forcedPorts'] for w in worlds)),
        ('forcedPortsWithHarbourVillage', sum(w['forcedPortsWithHarbourVillage'] for w in worlds)), ('protectedKept', sum(w['protectedKept'] for w in worlds)),
        ('castlesExcludedByQuota', sum(w['castlesExcludedByQuota'] for w in worlds)), ('stable', all(w['stable'] for w in worlds))])
    doc = collections.OrderedDict([
        ('schema', 'mnbl.evidence.village-quota'), ('version', 1), ('ticket', 'MMR-252'),
        ('recipe', 'tools/Run-SeedSweep.ps1 -Stage generate (size 129, phase 16, the module policy list, 3 attempts), then this script'),
        ('summary', summary), ('worlds', rows)])
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        f.write(json.dumps(doc, indent=1) + '\n')
    print('village quota: %d worlds, %d exact 3/2, %d forced ports with harbour villages of %d, %d islet exceptions, %d shared points, stable %s' % (
        summary['worlds'], summary['exact'], summary['forcedPortsWithHarbourVillage'], summary['forcedPorts'], summary['exceptions'], summary['sharedPoints'], summary['stable']))


if __name__ == '__main__':
    main()
