"""MMR-279: summarize the population pass of generated worlds into evidence/fwg/population.json.

Reads each world's compiled campaign.json v2 (a bundle's compiled/campaign/campaign.json or a travel stage's campaign.json) and
recomputes every population criterion from the records themselves, not from the census block: the kingdom count against
round(9 F / 133) and the merge steps, houses per kingdom and fiefs per house, ownership by a house of the kingdom, castellans with
their castle's house, an adult of each sex and vanilla lord cultures per house, the War Sails-scaled targets (houses, adult lords,
companies, outlaw clans within 10 percent; wanderers equal to W), company and outlaw clan sizes, the outlaw war guard, starting
wars, the bucket accounting (HeroFactory's notable caps from rosters.json applied in uid order) and clean unique clan names.

Usage: python tools/summarize_population.py OUT LABEL=CAMPAIGN_JSON [LABEL=CAMPAIGN_JSON ...] [--world LABEL=WORLD_JSON ...]
With --world, the living FWGC++ people of that world's world.json are matched against the hero records (every one placed once).
"""
import collections
import json
import math
import os
import re
import struct
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
AGE = re.compile(r'\(\s*age\s+\d+\s*\)', re.IGNORECASE)
ROMAN = {'II', 'III', 'IV', 'V', 'VI', 'VII', 'VIII', 'IX', 'X', 'XI', 'XII', 'XIII', 'XIV', 'XV', 'XVI', 'XVII', 'XVIII', 'XIX', 'XX'}


def numeral(name):
    return any(any(ch.isdigit() for ch in w) or w in ROMAN for w in re.split(r'[\s,()-]+', name) if w)


def f32(x):
    return struct.unpack('f', struct.pack('f', x))[0]


def wanderer_target(per_town, towns):
    desired = f32(f32(per_town) * f32(towns))
    w = 0
    while w < desired:
        w += 1
    return w


def within(actual, target, tolerance=0.10):
    return abs(actual - target) <= tolerance * target + 1e-9


def summarize(label, path, policy, caps, world_json=None):
    c = json.load(open(path, encoding='utf-8'))
    towns = {t['id']: t for t in c['towns']}
    castles = {k['id']: k for k in c['castles']}
    villages = {v['id']: v for v in c['villages']}
    heroes = {h['id']: h for h in c['heroes']}
    houses = {x['id']: x for x in c['houses']}
    kingdoms = {k['id']: k for k in c['kingdoms']}
    F = len(towns) + len(castles)
    base = policy['base']['fiefs']
    t = policy['targets']
    K = max(t['kingdoms']['min'], math.floor(t['kingdoms']['perBase'] * F / base + 0.5))
    H = max(math.floor(t['houses']['perBase'] * F / base + 0.5), min(t['houses']['minPerKingdom'] * K, F))
    M = math.floor(t['companies']['perBase'] * F / base + 0.5)
    O = math.floor(t['outlawClans']['perBase'] * F / base + 0.5)
    W = wanderer_target(t['wanderers']['perTown'], len(towns))
    adults_target = t['adultLordsPerHouse'] * H
    problems = []

    # Kingdoms and the merge.
    steps = collections.Counter(s['step'] for k in kingdoms.values() for s in k['mergeSteps'])
    water = sum(1 for k in kingdoms.values() for s in k['mergeSteps'] if s.get('crossesWater'))
    houses_of = collections.defaultdict(list)
    for x in houses.values():
        houses_of[x['kingdom']].append(x['id'])
    kingdom_rows = []
    max_fiefs = t['kingdoms']['maxFiefs']
    for k in sorted(kingdoms.values(), key=lambda k: k['id']):
        n = len(k['towns']) + len(k['castles'])
        kingdom_rows.append({'id': k['id'], 'presentation': k['presentation'], 'fiefs': n, 'houses': len(houses_of[k['id']]), 'memberRealms': len(k['memberRealms']),
                             'steps': dict(collections.Counter(s['step'] for s in k['mergeSteps']))})
        if n > max_fiefs and len(k['memberRealms']) > 1:
            problems.append('kingdom %s holds %d fiefs above maxFiefs' % (k['id'], n))
        if len(houses_of[k['id']]) < min(t['kingdoms']['minHouses'], n):
            problems.append('kingdom %s has %d houses' % (k['id'], len(houses_of[k['id']])))
        if heroes[k['ruler']]['clan'] != k['rulingHouse'] or houses[k['rulingHouse']]['leader'] != k['ruler']:
            problems.append('kingdom %s ruler does not lead its ruling house' % k['id'])
    if abs(len(kingdoms) - K) > 1:
        problems.append('kingdoms %d, target %d' % (len(kingdoms), K))

    # Houses: fiefs, ownership, members, sexes, cultures, castellans.
    lord_cultures = set(policy['houses']['lordCultures'])
    owner = {}
    for f in list(towns.values()) + list(castles.values()):
        owner[f['id']] = f['owner']
        x = houses.get(f['owner'])
        if not x or x['kingdom'] != f['kingdom'] or f['id'] not in x['fiefs']:
            problems.append('fief %s is not owned by a house of its kingdom' % f['id'])
    sizes, adults_all, minors_all = [], 0, 0
    for x in houses.values():
        if not 1 <= len(x['fiefs']) <= t['houses']['maxFiefs'] or x['home'] not in x['fiefs']:
            problems.append('house %s fiefs %d, home in fiefs %s' % (x['id'], len(x['fiefs']), x['home'] in x['fiefs']))
        members = [heroes[m] for m in x['members']]
        adults = [h for h in members if h['age'] >= 18]
        sizes.append(len(members))
        adults_all += len(adults)
        minors_all += len(members) - len(adults)
        if not 3 <= len(members) <= 10:
            problems.append('house %s has %d members' % (x['id'], len(members)))
        if not any(h['female'] for h in adults) or not any(not h['female'] for h in adults) or len(adults) < 2:
            problems.append('house %s lacks an adult of each sex' % x['id'])
        for h in members:
            if h['presentation'] not in lord_cultures or h['occupation'] != 'Lord' or h.get('clan') != x['id'] or h['bucket'] != 'house':
                problems.append('house member %s: culture %s, occupation %s, clan %s' % (h['id'], h['presentation'], h['occupation'], h.get('clan')))
            for kin in ('spouse', 'father', 'mother'):
                if kin in h and h[kin] not in heroes:
                    problems.append('hero %s %s missing' % (h['id'], kin))
        for h in members:
            if h['age'] < 18 and not (h.get('father') and h.get('mother')):
                problems.append('minor %s lacks a parent' % h['id'])
    castellans_ok = 0
    castle_of_uid = {k['uid']: k['id'] for k in castles.values()}
    people = {}
    if world_json:
        w = json.load(open(world_json, encoding='utf-8'))
        people = {p['uid']: p for p in w['heroes']['people']}
    for h in heroes.values():
        if h['role'] != 'castellan' or h.get('synthesized'):
            continue
        p = people.get(h['uid'])
        castle = castle_of_uid.get((p or {}).get('presence', {}).get('uid')) if p else None
        if castle and h.get('clan') != owner[castle]:
            problems.append('castellan %s is not in the house owning %s' % (h['id'], castle))
        elif castle:
            castellans_ok += 1

    # Companies, outlaw clans, wanderers.
    companies, outlaws = c['mercenaryCompanies'], c['outlawClans']
    for k in companies:
        if not t['companies']['members'][0] <= len(k['members']) <= t['companies']['members'][1]:
            problems.append('company %s has %d members' % (k['id'], len(k['members'])))
        line = policy['troopTemplates']['cultures'][k['presentation']]['mercenary']
        if k['home'] not in towns or k['partyTemplate'] != line['partyTemplate'] or k['characterTemplates'] != line['characterTemplates']:
            problems.append('company %s home or templates' % k['id'])
        for m in k['members']:
            if heroes[m]['bucket'] != 'company' or heroes[m].get('clan') != k['id']:
                problems.append('company member %s' % m)
    kingdoms_of = collections.Counter(k['presentation'] for k in kingdoms.values())
    outlaw_rows = []
    for k in outlaws:
        if not t['outlawClans']['members'][0] <= len(k['members']) <= t['outlawClans']['members'][1]:
            problems.append('outlaw clan %s has %d members' % (k['id'], len(k['members'])))
        lines = policy['troopTemplates']['cultures'][k['presentation']]['outlaws']
        if k['home'] not in towns or not any(l['kind'] == k['kind'] and l['partyTemplate'] == k['partyTemplate'] and l['characterTemplates'] == k['characterTemplates'] for l in lines):
            problems.append('outlaw clan %s home or templates' % k['id'])
        expected = sorted(x['id'] for x in kingdoms.values() if x['presentation'] == k['presentation'])
        if sorted(k['wars']) != expected or len(expected) > policy['outlaws']['maxWarKingdoms']:
            problems.append('outlaw clan %s wars %s, guard %d' % (k['id'], k['wars'], policy['outlaws']['maxWarKingdoms']))
        outlaw_rows.append({'id': k['id'], 'kind': k['kind'], 'presentation': k['presentation'], 'wars': len(k['wars']), 'source': k['source'].split(':')[0], 'members': len(k['members'])})
    wanderers = c['wanderers']
    for wd in wanderers:
        h = heroes[wd['hero']]
        if h['bucket'] != 'wanderer' or h['occupation'] != 'Wanderer' or h['female'] != wd['female']:
            problems.append('wanderer %s' % wd['hero'])
    if len(wanderers) != W:
        problems.append('wanderers %d, W %d' % (len(wanderers), W))

    # Wars.
    wars = collections.Counter()
    for war in c['wars']:
        wars[war['a']] += 1
        wars[war['b']] += 1
    if len(kingdoms) > 1 and (min(wars[k] for k in kingdoms) < 1 or max(wars.values()) > policy['wars']['maxPerKingdom']):
        problems.append('wars per kingdom %s' % dict(wars))

    # Buckets: every living source hero once; notables within HeroFactory's caps (uid order).
    placed = collections.Counter()
    for x in houses.values():
        for m in x['members']:
            placed[m] += 1
    for k in companies + outlaws:
        for m in k['members']:
            placed[m] += 1
    for wd in wanderers:
        placed[wd['hero']] += 1
    count = collections.Counter()
    notables, bounded = 0, 0
    for h in sorted(heroes.values(), key=lambda h: h['uid']):
        if h['bucket'] != 'notable':
            continue
        cap = caps.get(h.get('settlementKind'), {}).get(h['occupation'], 0)
        key = (h.get('settlement'), h['occupation'])
        if count[key] < cap:
            count[key] += 1
            notables += 1
            placed[h['id']] += 1
        else:
            bounded += 1
    buckets = collections.Counter()
    unassigned = []
    for h in heroes.values():
        if not h['alive']:
            continue
        if placed[h['id']] != 1:
            unassigned.append(h['uid'])
        elif not h.get('synthesized'):
            buckets[h['bucket']] += 1
    living_source = None
    if people:
        living = {u for u, p in people.items() if p['status'] == 'living'}
        records = {h['uid'] for h in heroes.values() if not h.get('synthesized')}
        living_source = len(living)
        missing = sorted(living - records)
        if missing:
            problems.append('%d living FWGC++ heroes have no hero record, first %s' % (len(missing), missing[0]))
    if unassigned:
        problems.append('%d heroes unassigned, first %s' % (len(unassigned), unassigned[0]))
    census = c['census']
    for key, value in (('notables', notables), ('houses', len(houses)), ('mercenaryCompanies', len(companies)), ('outlawClans', len(outlaws)),
                       ('wanderers', W), ('adultLords', adults_all), ('minors', minors_all), ('unassigned', len(unassigned))):
        if census.get(key) != value:
            problems.append('census %s %s, recomputed %s' % (key, census.get(key), value))

    # Names: unique among clans, no Age tag, no numeral.
    clan_names = [x['name'] for x in houses.values()] + [k['name'] for k in companies + outlaws]
    bad_names = [n for n in clan_names if AGE.search(n) or numeral(n)]
    if bad_names or len(set(clan_names)) != len(clan_names):
        problems.append('clan names: %d tagged or numbered, %d duplicates' % (len(bad_names), len(clan_names) - len(set(clan_names))))

    targets = {'kingdoms': K, 'houses': H, 'adultLords': round(adults_target), 'mercenaryCompanies': M, 'outlawClans': O, 'wanderers': W}
    actual = {'kingdoms': len(kingdoms), 'houses': len(houses), 'adultLords': adults_all, 'mercenaryCompanies': len(companies), 'outlawClans': len(outlaws), 'wanderers': len(wanderers)}
    for key in ('houses', 'adultLords', 'mercenaryCompanies', 'outlawClans'):
        if not within(actual[key], targets[key]):
            problems.append('%s %d outside 10%% of %d' % (key, actual[key], targets[key]))
    return {
        'world': label, 'campaign': os.path.relpath(path, REPO).replace('\\', '/') if path.startswith(REPO) else path.replace('\\', '/'),
        'fiefs': F, 'towns': len(towns), 'castles': len(castles), 'villages': len(villages), 'realms': sum(len(k['memberRealms']) for k in kingdoms.values()),
        'targets': targets, 'actual': actual,
        'kingdoms': {'mergeSteps': dict(steps), 'crossesWater': water, 'rows': kingdom_rows, 'maxFiefs': max(r['fiefs'] for r in kingdom_rows),
                     'minHouses': min(r['houses'] for r in kingdom_rows)},
        'houses': {'members': {'min': min(sizes), 'max': max(sizes), 'mean': round(sum(sizes) / len(sizes), 3)}, 'adultsPerHouse': round(adults_all / len(houses), 3),
                   'minors': minors_all, 'origins': dict(collections.Counter(x['origin'] for x in houses.values())),
                   'fiefs': dict(collections.Counter(len(x['fiefs']) for x in houses.values())), 'castellansWithTheirCastle': castellans_ok},
        'companies': {'members': sorted(len(k['members']) for k in companies), 'sources': dict(collections.Counter(k['source'].split(':')[0] for k in companies)),
                      'presentations': dict(collections.Counter(k['presentation'] for k in companies))},
        'outlawClans': outlaw_rows,
        'wars': {'pairs': len(c['wars']), 'perKingdom': dict(collections.Counter(wars[k] for k in kingdoms)),
                 'partners': dict(collections.Counter(w['partner'] for w in c['wars']))},
        'wanderers': {'count': len(wanderers), 'W': W, 'female': sum(1 for x in wanderers if x['female']),
                      'presentations': dict(collections.Counter(x['presentation'] for x in wanderers))},
        'buckets': dict(buckets), 'livingSourceHeroes': living_source, 'notablesInstantiated': notables, 'notablesBounded': bounded,
        'unassigned': len(unassigned), 'engineChildren': census['engineChildren'], 'synthesized': census['synthesized'],
        'overflow': len(census['overflow']), 'censusNotes': census['notes'],
        'names': {'clans': len(clan_names), 'unique': len(set(clan_names)) == len(clan_names), 'taggedOrNumbered': len(bad_names),
                  'sample': sorted(clan_names)[:12]},
        'problems': problems,
    }


def main():
    args = sys.argv[1:]
    if len(args) < 2:
        print(__doc__)
        return 2
    out = args[0]
    worlds, campaigns = {}, []
    i = 1
    while i < len(args):
        if args[i] == '--world':
            label, path = args[i + 1].split('=', 1)
            worlds[label] = path
            i += 2
        else:
            campaigns.append(args[i].split('=', 1))
            i += 1
    policy = json.load(open(os.path.join(REPO, 'worldgen/policies/population.json'), encoding='utf-8'))
    caps = json.load(open(os.path.join(REPO, 'worldgen/policies/rosters.json'), encoding='utf-8'))['notables']['caps']
    rows = [summarize(label, path, policy, caps, worlds.get(label)) for label, path in campaigns]
    doc = collections.OrderedDict([
        ('schema', 'mnbl.population-evidence'), ('version', 1),
        ('ticket', 'MMR-279'),
        ('method', 'worldbridge --generate (size 129, phase 16, the module policy list) then --bundle per seed; each campaign.json v2 summarized by '
                   'tools/summarize_population.py, which recomputes every criterion from the records (not from the census block)'),
        ('policySha256', __import__('hashlib').sha256(open(os.path.join(REPO, 'worldgen/policies/population.json'), 'rb').read()).hexdigest()),
        ('worlds', rows),
        ('passed', all(not r['problems'] for r in rows)),
    ])
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(doc, f, indent=1)
        f.write('\n')
    for r in rows:
        print(r['world'], 'targets', r['targets'], 'actual', r['actual'], 'problems', len(r['problems']))
        for p in r['problems'][:20]:
            print('   ', p)
    return 0 if doc['passed'] else 1


if __name__ == '__main__':
    sys.exit(main())
