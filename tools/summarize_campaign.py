"""Independently verify MMR-27 campaign records and write evidence/fwg/campaign-semantics.json.

For each verified world, this script recomputes in Python, from the source world.json and the two policies, every
stable id (prefix from the source identity and the world.json hash, plus the per-record hash), every reference
(homes, hamlet presences, cultures, realms, civilizations), every ownership/capital/binding/membership join, every
occupation and settlement attachment, the regents, the ports, the not-instantiated legends, every synthesized name
and the producer limits, and compares them with campaign.json and id-map.json. With --game it also checks, against the
installed game's official modules, that every occupation the records use carries hero characters or templates
there; that check does not rely on the compiler's policy. Larger worlds are summarized from their records. A repeated
compile must be byte-identical.
Usage:
  python tools/summarize_campaign.py --verify NAME=SOURCE=COMPILED [...] --summarize NAME=COMPILED [...] --repeat A=B
                                     [--game BANNERLORD_DIR] --out FILE
"""
import argparse
import collections
import datetime
import hashlib
import json
import os
import re
import xml.etree.ElementTree as ET

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def rel(path):
    path = os.path.abspath(path)
    return os.path.relpath(path, REPO).replace('\\', '/') if path.startswith(REPO) else path


def load(path):
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def h(text):
    return hashlib.sha256(text.encode('utf-8')).hexdigest()


def xml_escape(s):
    out = []
    for ch in s:
        out.append({'&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&apos;'}.get(ch, ch if ord(ch) >= 0x20 else ''))
    return ''.join(out)


def localized(i, s):
    return '{=' + i + '}' + ''.join('(' if c == '{' else ')' if c == '}' else c for c in s if ord(c) >= 0x20)


def fill(pattern, values):
    return re.sub(r'\{([^{}]*)\}', lambda m: values.get(m.group(1), m.group(0)), pattern)


def identity_text(world):
    s = world['seeds']
    return ('{"schema":"mnbl.source-identity","version":1,"sourceSchema":' + json.dumps(world['schema']) + ',"schemaVersion":' + str(world['schema_version']) +
            ',"completedStage":' + str(world['completed_stage']) + ',"generatorVersion":' + json.dumps(str(world['generator_version'])) +
            ',"gridSide":' + str(world['config']['size']) + ',"layoutSeed":"' + s['layout'] + '","crustSeed":' + str(s['crust']) + ',"detailSeed":' + str(s['detail']) + '}')


def installed_occupations(game):
    """Occupation -> number of is_hero or is_template NPCCharacters in the game's official modules."""
    counts = collections.Counter()
    modules = os.path.join(game, 'Modules')
    for mod in sorted(os.listdir(modules)):
        sub = os.path.join(modules, mod, 'SubModule.xml')
        if not os.path.exists(sub):
            continue
        kind = ET.parse(sub).getroot().find('ModuleType')
        if kind is None or not kind.get('value', '').startswith('Official'):
            continue
        for dirpath, _, files in os.walk(os.path.join(modules, mod, 'ModuleData')):
            for f in files:
                if not f.endswith('.xml'):
                    continue
                try:
                    tree = ET.parse(os.path.join(dirpath, f))
                except ET.ParseError:
                    continue
                for e in tree.iter('NPCCharacter'):
                    if e.get('occupation') and (e.get('is_hero') == 'true' or e.get('is_template') == 'true'):
                        counts[e.get('occupation')] += 1
    return counts


def verify(source_dir, compiled_dir, content, cultures, installed):
    world_file = os.path.join(source_dir, 'world.json')
    world = load(world_file)
    rec = load(os.path.join(compiled_dir, 'campaign.json'))
    idmap = load(os.path.join(compiled_dir, 'id-map.json'))
    seed = world['seeds']['layout']
    identity, world_sha = identity_text(world), sha(world_file)
    prefix = 'mnbl_' + h('mnbl.world-prefix/2\n' + identity + '\n' + world_sha)[:8]
    code = {'town': 't', 'village': 'v', 'castle': 'c', 'kingdom': 'k', 'clan': 'l', 'hero': 'h'}

    def gid(kind, uid):
        return prefix + '_' + code[kind] + '_' + h('mnbl.stable-id/1\n' + kind + '\n' + uid)[:10]
    sites = world['settlements']['sites']
    hamlets = world['humans']['hamlets']
    forts = world['humans']['fortresses']
    cores = {c['site_id']: c for c in world['humans']['cores']}
    culture_list = world['humans']['cultures']
    culture_civ = {c['id']: c['civilization_id'] for c in culture_list}
    realms = world['heroes']['realms']
    people = world['heroes']['people']
    roles = {r['role']: r for r in content['heroRoles']}
    statuses = {s['status']: s['instantiate'] for s in content['heroStatuses']}
    occ = {o['occupation']: o for o in content['occupations']}
    hroles = {r['role']: r for r in content['hamletRoles']}
    present = {c['civilization']: c['culture'] for c in cultures['civilizations']}
    realm_of_city = {u: r for r in realms for u in r['city_uids']}
    realm_by_uid = {r['uid']: r for r in realms}
    by_site = {s['id']: s for s in sites}
    city_uids = {s['uid'] for s in sites}
    hamlet_uids = {x['uid'] for x in hamlets}
    instantiated = [p for p in people if statuses[p['status']]]
    expected_ids = set()
    for s in sites: expected_ids.add((gid('town', s['uid']), 'town', s['uid']))
    for x in hamlets: expected_ids.add((gid('village', x['uid']), 'village', x['uid']))
    for f in forts: expected_ids.add((gid('castle', f['uid']), 'castle', f['uid']))
    for r in realms:
        expected_ids.add((gid('kingdom', r['uid']), 'kingdom', r['uid']))
        expected_ids.add((gid('clan', 'clan:' + r['uid']), 'clan', 'clan:' + r['uid']))
        if not r.get('sovereign_uid'):
            expected_ids.add((gid('hero', 'regent:' + r['uid']), 'hero', 'regent:' + r['uid']))
    for p in instantiated: expected_ids.add((gid('hero', p['uid']), 'hero', p['uid']))
    actual_ids = {(e['id'], e['kind'], e['uid']) for e in idmap['entries']}
    checks = {
        'prefixFromSourceIdentityAndWorldHash': idmap['prefix'] == prefix and idmap['layoutSeed'] == seed and idmap['sourceIdentity'] == identity and idmap['worldSha256'] == world_sha == rec['worldSha256'],
        'idMapRecomputed': actual_ids == expected_ids,
        'idsUniqueAndBounded': len({e['id'] for e in idmap['entries']}) == len(idmap['entries']) and all(re.fullmatch(r'mnbl_[0-9a-f]{8}_[tvcklh]_[0-9a-f]{10}', e['id']) and len(e['id']) <= 26 for e in idmap['entries']),
        'idMapHashRecorded': rec['idMapSha256'] == sha(os.path.join(compiled_dir, 'id-map.json')),
        'countsPreserved': (len(rec['towns']), len(rec['villages']), len(rec['castles']), len(rec['kingdoms'])) == (len(sites), len(hamlets), len(forts), len(realms)),
    }

    def occupation(p):
        rule = roles[p['role']]
        return rule['withoutRealm'] if (not p.get('realm_uid') and 'withoutRealm' in rule) else rule['occupation']
    # Every reference the compiler consumes resolves in the source, independently of the compiled records.
    refs = len(culture_civ) == len(culture_list)
    refs = refs and all(r['culture_id'] in culture_civ and culture_civ[r['culture_id']] == r['civilization_id'] for r in realms)
    refs = refs and all(not cores[s['id']].get('culture_id') or culture_civ.get(cores[s['id']]['culture_id']) == s['civilization_id'] for s in sites)
    refs = refs and all(not x.get('culture_id') or x['culture_id'] in culture_civ for x in hamlets + forts)
    for p in instantiated:
        home = p.get('home') or {}
        refs = refs and (not home or (home.get('kind') == 'city' and home.get('uid') in city_uids))
        refs = refs and (not p.get('realm_uid') or p['realm_uid'] in realm_by_uid) and p['civilization_id'] in present
        pres = p.get('presence') or {}
        if occ[occupation(p)].get('settlement') == 'village' and pres.get('site_kind') == 'hamlet':
            refs = refs and pres.get('uid') in hamlet_uids
    checks['everyReferenceResolves'] = refs
    towns = {t['uid']: t for t in rec['towns']}
    kingdoms = {k['uid']: k for k in rec['kingdoms']}
    clans = {c['uid']: c for c in rec['clans']}
    heroes = {x['uid']: x for x in rec['heroes']}
    villages = {v['uid']: v for v in rec['villages']}
    castles = {c['uid']: c for c in rec['castles']}

    def town_culture(s):
        return cores[s['id']].get('culture_id') or realm_of_city[s['uid']]['culture_id']
    checks['townJoins'] = all(towns[s['uid']]['kingdom'] == gid('kingdom', realm_of_city[s['uid']]['uid']) and towns[s['uid']]['owner'] == gid('clan', 'clan:' + realm_of_city[s['uid']]['uid'])
                              and towns[s['uid']]['capital'] == (realm_of_city[s['uid']]['capital_uid'] == s['uid']) and towns[s['uid']]['presentation'] == present[s['civilization_id']]
                              and towns[s['uid']]['cultureUid'] == town_culture(s) for s in sites)
    checks['villageBinding'] = all(villages[x['uid']]['bound'] == gid('town', by_site[x['core_id']]['uid']) and villages[x['uid']]['harbour'] == hroles[x['role']]['harbour']
                                   and villages[x['uid']]['production'] == hroles[x['role']]['production'] for x in hamlets)

    def castle_owner(f):
        if f['core_id'] != -1 and f.get('core_uid'):
            return realm_of_city[f['core_uid']]['uid']
        match = [r for r in realms if r['culture_id'] == f['culture_id']]
        return match[0]['uid'] if len(match) == 1 else None
    checks['castleOwnership'] = all(castles[f['uid']]['owner'] == gid('clan', 'clan:' + castle_owner(f)) for f in forts)

    def ruler(r):
        return gid('hero', r['sovereign_uid']) if r.get('sovereign_uid') else gid('hero', 'regent:' + r['uid'])
    checks['rulersAndLeaders'] = all(kingdoms[r['uid']]['ruler'] == ruler(r) and clans['clan:' + r['uid']]['leader'] == ruler(r) and kingdoms[r['uid']]['capital'] == gid('town', r['capital_uid']) for r in realms)
    regents = [x for x in rec['heroes'] if x['role'] == 'regent']
    checks['regentsForNullSovereigns'] = len(regents) == sum(1 for r in realms if not r.get('sovereign_uid')) and all(x['nameProvenance'].startswith('synthesized') for x in regents)

    def settlement(p, o):
        pres, home = p.get('presence') or {}, p.get('home') or {}
        if occ[o].get('settlement') == 'village' and pres.get('site_kind') == 'hamlet':
            return gid('village', pres['uid']), 'village'
        if home.get('uid'):
            return gid('town', home['uid']), 'town'
        return gid('town', realm_by_uid[p['realm_uid']]['capital_uid']), 'town'
    occ_ok = True
    for p in instantiated:
        x = heroes[p['uid']]
        o = occupation(p)
        occ_ok = occ_ok and x['occupation'] == o and x['affiliation'] == occ[o]['affiliation'] and x['presentation'] == present[p['civilization_id']] and x['raceId'] == p.get('race_id', '')
        if occ[o]['affiliation'] == 'clan':
            occ_ok = occ_ok and x.get('clan') == gid('clan', 'clan:' + p['realm_uid'])
        if occ[o]['affiliation'] == 'settlement':
            occ_ok = occ_ok and (x.get('settlement'), x.get('settlementKind')) == settlement(p, o)
    checks['occupationsAndAffiliations'] = occ_ok
    used = {x['occupation'] for x in rec['heroes']}
    if installed is not None:
        checks['occupationsHaveInstalledTemplates'] = all(installed.get(o, 0) > 0 for o in used)
    checks['legendsNotInstantiated'] = set(rec['notInstantiated']) == {p['uid'] for p in people if not statuses[p['status']]}
    harbour_towns = {by_site[x['core_id']]['uid'] for x in hamlets if hroles[x['role']]['harbour']}
    checks['portsFromHarbourVillages'] = {p['town'] for p in rec['ports']} == {gid('town', u) for u in harbour_towns}
    # Names (MMR-286): the shared cleaner names every kind (tools/summarize_names.py is the census): no Age tag, no ordinal numeral, unique
    # per kind (towns, castles and villages together), a renamed source name kept as sourceName, regents with personal names.
    from summarize_names import AGE, numeral  # noqa: E402
    clans = rec.get('houses', rec.get('clans', [])) + rec.get('mercenaryCompanies', []) + rec.get('outlawClans', [])
    kinds = [rec['kingdoms'], clans, rec['towns'] + rec['villages'] + rec['castles'], rec['heroes']]
    checks['namesClean'] = all(len({x['name'] for x in k}) == len(k) and not any(AGE.search(x['name']) or numeral(x['name']) for x in k) for k in kinds) and \
        all(not x['name'].startswith('Regent of') for x in regents) and all(x.get('sourceName', x['name']) != x['name'] or 'sourceName' not in x for x in rec['towns'] + rec['kingdoms'])
    all_named = rec['towns'] + rec['villages'] + rec['castles'] + clans + rec['kingdoms'] + rec['heroes']
    checks['nameEscaping'] = all(x['nameXml'] == xml_escape(x['name']) and x['nameLocalized'] == localized(x['id'], x['name']) and x['nameLocalizedXml'] == xml_escape(localized(x['id'], x['name'])) for x in all_named)
    founding, config = world['settlements']['founding'], world['effective_config']
    land = collections.Counter(x['core_id'] for x in hamlets if 'sea_node' not in x)
    checks['producerLimits'] = (rec['producer']['sites'] == founding['placed_cities'] == len(sites) <= founding['target_cities'] and len(forts) <= config['fortress_count']
                                and max(land.values(), default=0) <= config['hamlets_per_core'] and not any(x.get('founded_by') for x in hamlets + forts))
    checks['minimaHold'] = all(r['status'] == 'deferred' or r['actual'] >= r['minimum'] for r in rec['rules'])
    return {'source': rel(source_dir), 'sourceWorldSha256': world_sha, 'compiled': rel(compiled_dir), 'layoutSeed': seed, 'prefix': prefix,
            'counts': rec['counts'], 'producer': rec['producer'], 'checks': checks,
            'occupations': dict(collections.Counter(x['occupation'] for x in rec['heroes'])),
            'settlementKinds': dict(collections.Counter(x.get('settlementKind', 'none') for x in rec['heroes'])),
            'installedTemplates': {o: installed.get(o, 0) for o in sorted(used)} if installed is not None else None,
            'presentations': dict(collections.Counter(k['presentation'] for k in rec['kingdoms']))}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--verify', action='append', default=[])
    ap.add_argument('--summarize', action='append', default=[])
    ap.add_argument('--repeat', action='append', default=[])
    ap.add_argument('--content', default=os.path.join(REPO, 'worldgen/policies/campaign-content.json'))
    ap.add_argument('--cultures', default=os.path.join(REPO, 'worldgen/policies/culture-presentations.json'))
    ap.add_argument('--game')
    ap.add_argument('--unit-log')
    ap.add_argument('--ctest-log')
    ap.add_argument('--out', required=True)
    a = ap.parse_args()
    content, cultures = load(a.content), load(a.cultures)
    installed = installed_occupations(a.game) if a.game else None
    verified = []
    for spec in a.verify:
        name, source, compiled = spec.split('=', 2)
        v = verify(source, compiled, content, cultures, installed)
        v['name'] = name
        verified.append(v)
    summarized = []
    for spec in a.summarize:
        name, compiled = spec.split('=', 1)
        rec = load(os.path.join(compiled, 'campaign.json'))
        process = load(compiled + '.process.json') if os.path.exists(compiled + '.process.json') else {}
        summarized.append({'name': name, 'compiled': rel(compiled), 'campaignSha256': sha(os.path.join(compiled, 'campaign.json')),
                           'idMapSha256': sha(os.path.join(compiled, 'id-map.json')), 'prefix': load(os.path.join(compiled, 'id-map.json'))['prefix'],
                           'counts': rec['counts'], 'producer': rec['producer'], 'wallSeconds': process.get('wallSeconds'),
                           'countsMatchProducer': (rec['counts']['towns'], rec['counts']['villages'], rec['counts']['castles'], rec['counts']['kingdoms']) ==
                                                  (rec['producer']['sites'], rec['producer']['hamlets'], rec['producer']['fortresses'], rec['producer']['realms'])})
    repeats = []
    for spec in a.repeat:
        x, y = spec.split('=', 1)
        repeats.append({'first': rel(x), 'second': rel(y), 'identical': all(sha(os.path.join(x, f)) == sha(os.path.join(y, f)) for f in ('id-map.json', 'campaign.json'))})
    prefixes = [v['prefix'] for v in verified] + [s['prefix'] for s in summarized]
    summary = {
        'schema': 'mnbl.campaign-semantics', 'version': 1, 'ticket': 'MMR-27',
        'generatedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'policies': {'content': {'file': rel(a.content), 'sha256': sha(a.content)}, 'cultures': {'file': rel(a.cultures), 'sha256': sha(a.cultures)}},
        'tools': {'worldbridge': sha(os.path.join(REPO, 'build/worldbridge/Release/worldbridge.exe'))},
        'game': a.game,
        'unitLog': {'file': rel(a.unit_log), 'sha256': sha(a.unit_log)} if a.unit_log else None,
        'ctestLog': {'file': rel(a.ctest_log), 'sha256': sha(a.ctest_log)} if a.ctest_log else None,
        'verified': verified, 'summarized': summarized, 'repeats': repeats,
        'distinctWorldPrefixes': len(set(prefixes)) == len(prefixes),
        'passed': all(all(v['checks'].values()) for v in verified) and all(s['countsMatchProducer'] for s in summarized) and all(r['identical'] for r in repeats)
                  and len(set(prefixes)) == len(prefixes),
        'reproduce': ['./tools/Build.ps1', 'ctest -C Release (build/worldbridge)',
                      'worldbridge --compile-campaign <split world> --contract worldgen/contracts/source-compatibility.json --content worldgen/policies/campaign-content.json --cultures worldgen/policies/culture-presentations.json --out <new dir>',
                      'python tools/summarize_campaign.py --verify NAME=<split>=<compiled> --summarize NAME=<compiled> --repeat <a>=<b> --game <Bannerlord dir> --out evidence/fwg/campaign-semantics.json'],
    }
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(summary, f, indent=1)
    print('passed=%s failing=%s' % (summary['passed'], [(v['name'], k) for v in verified for k, ok in v['checks'].items() if not ok]))


if __name__ == '__main__':
    main()
