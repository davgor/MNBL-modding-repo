"""MMR-248: write worldgen/policies/population.json, the map epic 2 population policy (docs/research/map-epic-2-findings.md
sections 16 and 17; consumed by MMR-279 population.cpp, MMR-280 FactionFactory/HeroFactory and MMR-281 the wanderer tick).

The rules (merge order, house sizes, seat and member priority, spill order, wars, rotation) are authored here. Every count is per one
fief base, the War Sails campaign's 133 fiefs (RT-03: one base everywhere), and every installed value is read from the merged campaign
catalog (evidence/runtime/in-game/campaign_catalog-10212.json, hash-checked):
  * Settlements.xml + Factions.xml: towns, castles, fiefs per kingdom (the largest is the kingdom-merge maximum), noble clans;
  * Heroes.xml + NPCCharacters.xml: alive XML lords of noble clans, their ages and personality traits (the lord-trait distribution,
    RT-05), and the wanderer templates;
  * Factions.xml + partyTemplates.xml + NPCCharacters.xml: the minor clans' party templates, leader templates, flags and tiers, which
    the per-culture troop template table (RT-03) names and checks.
Usage: python tools/make_population_policy.py
"""
import collections
import hashlib
import json
import math
import os
import xml.etree.ElementTree as ET

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CATALOG = 'evidence/runtime/in-game/campaign_catalog-10212.json'
CULTURES = ['aserai', 'battania', 'empire', 'khuzait', 'nord', 'sturgia', 'vlandia']
PERSONALITY = ['Mercy', 'Valor', 'Honor', 'Generosity', 'Calculating']
# The installed minor clan whose party template and 4 leader templates each presentation culture's mercenary companies use (plan
# MMR-248: vanilla mercenary lines for aserai, empire, sturgia and vlandia; battania -> wolfskins (outlaw_battania); khuzait ->
# karakhuzaits; nord -> skolderbrotva (mercenary_sturgia)). Sturgia has no mercenary clan: its companies take the mercenary_sturgia
# party template with the leader templates of lakepike, the installed clan whose leaders are of culture sturgia (default taken).
MERCENARY = collections.OrderedDict([
    ('aserai', ('ghilman', None)), ('battania', ('wolfskins', None)), ('empire', ('legion_of_the_betrayed', None)),
    ('khuzait', ('karakhuzaits', None)), ('nord', ('skolderbrotva', None)),
    ('sturgia', ('lakepike', 'kingdom_hero_party_mercenary_sturgia_template')), ('vlandia', ('company_of_the_boar', None)),
])
# The 10 installed outlaw lines by presentation culture (the culture of their leader templates). Nord has none installed: its outlaw
# clans take lakepike's line (sturgia, mafia), the nearest northern one (default taken).
OUTLAW = collections.OrderedDict([
    ('aserai', ['beni_zilal', 'jawwal']), ('battania', ['wolfskins']), ('empire', ['hidden_hand', 'embers_of_flame', 'eleftheroi']),
    ('khuzait', ['karakhuzaits']), ('nord', ['lakepike']), ('sturgia', ['lakepike', 'forest_people']), ('vlandia', ['brotherhood_of_woods']),
])
OUTLAW_FALLBACK = {'nord': 'no installed nord outlaw clan; the sturgia mafia line lakepike stands in'}
BACKSTORY_CENSUS = 'evidence/map-epic-2/wanderer-backstories.json'
# MMR-287: when a culture has no backstory-safe template of a wanderer's sex, the pool takes one of these cultures in order (neighbours
# and kin in Calradia first; default taken). Nord has no safe template at all.
WANDERER_FALLBACK = {
    'aserai': ['empire', 'khuzait', 'vlandia', 'battania', 'sturgia', 'nord'],
    'battania': ['sturgia', 'vlandia', 'nord', 'empire', 'khuzait', 'aserai'],
    'empire': ['vlandia', 'aserai', 'khuzait', 'battania', 'sturgia', 'nord'],
    'khuzait': ['aserai', 'sturgia', 'empire', 'battania', 'vlandia', 'nord'],
    'nord': ['sturgia', 'battania', 'vlandia', 'empire', 'khuzait', 'aserai'],
    'sturgia': ['nord', 'battania', 'khuzait', 'vlandia', 'empire', 'aserai'],
    'vlandia': ['battania', 'empire', 'sturgia', 'nord', 'aserai', 'khuzait'],
}


def epithet(name):
    """A wanderer template's epithet: its name pattern without the {=id} prefix and {FIRSTNAME} ("{=x}{FIRSTNAME} the Scholar" -> "the Scholar")."""
    text = name.split('}', 1)[1] if name.startswith('{=') else name
    return ' '.join(text.replace('{FIRSTNAME}', ' ').split())


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def od(*pairs):
    return collections.OrderedDict(pairs)


def ref(value):
    return value.split('.', 1)[1] if value and '.' in value else value


def main():
    receipt = json.load(open(os.path.join(REPO, CATALOG), encoding='utf-8-sig'))
    caps = json.load(open(os.path.join(REPO, 'worldgen/policies/rosters.json'), encoding='utf-8-sig'))['notables']['caps']
    docs = {d['id']: d for d in receipt['outputs']['documents']}
    roots = {}
    for name in ('Settlements', 'Factions', 'Kingdoms', 'Heroes', 'NPCCharacters', 'partyTemplates'):
        if sha(docs[name]['path']) != docs[name]['sha256']:
            raise SystemExit('catalog %s differs from its receipt' % name)
        roots[name] = ET.parse(docs[name]['path']).getroot()
    factions = {e.get('id'): e for e in roots['Factions']}
    characters = {e.get('id'): e for e in roots['NPCCharacters']}
    templates = {e.get('id') for e in roots['partyTemplates']}
    kingdoms = [k.get('id') for k in roots['Kingdoms']]

    towns = castles = 0
    fiefs = collections.Counter()
    for s in roots['Settlements']:
        comps = s.find('Components')
        town = comps.find('Town') if comps is not None else None
        if town is None:
            continue
        if town.get('is_castle') == 'true':
            castles += 1
        else:
            towns += 1
        fiefs[ref(factions[ref(s.get('owner'))].get('super_faction'))] += 1
    largest, largest_fiefs = fiefs.most_common(1)[0]
    nobles = [e for e in factions.values() if e.get('is_noble') == 'true']

    lords = []
    for h in roots['Heroes']:
        c = characters.get(h.get('id'))
        clan = factions.get(ref(h.get('faction')))
        if c is None or c.get('occupation') != 'Lord' or h.get('alive') == 'false' or clan is None or clan.get('is_noble') != 'true':
            continue
        traits = {t.get('id'): int(t.get('value')) for t in c.iter('Trait')}
        lords.append((ref(c.get('culture')), tuple(traits.get(p, 0) for p in PERSONALITY), int(c.get('age') or 0)))
    adults = sum(1 for l in lords if l[2] >= 18)
    joint = collections.Counter(l[1] for l in lords)
    by_culture = collections.defaultdict(collections.Counter)
    for culture, values, _ in lords:
        by_culture[culture][values] += 1
    sharing = sum(n for c in by_culture.values() for n in c.values() if n > 1)

    minor = collections.OrderedDict((k, e) for k, e in factions.items() if e.get('is_minor_faction') == 'true' and k != 'player_faction')

    def line(clan_id, party=None):
        e = minor[clan_id]
        kind = 'mercenary' if e.get('is_clan_type_mercenary') == 'true' else 'mafia' if e.get('is_mafia') == 'true' else 'nomad' if e.get('is_nomad') == 'true' else 'sect' if e.get('is_sect') == 'true' else None
        leaders = [ref(t.get('id')) for t in e.iter('template')]
        party = party or ref(e.get('default_party_template'))
        if party not in templates:
            raise SystemExit('party template %s is not installed' % party)
        cultures = {ref(characters[t].get('culture')) for t in leaders if t in characters}
        if len(leaders) != 4 or any(t not in characters for t in leaders) or len(cultures) != 1:
            raise SystemExit('minor clan %s has no 4 installed leader templates of one culture' % clan_id)
        return od(('sourceClan', clan_id), ('kind', kind), ('partyTemplate', party), ('characterTemplates', leaders), ('tier', int(e.get('tier'))),
                  ('templateCulture', cultures.pop()))

    troops = collections.OrderedDict()
    for culture in CULTURES:
        clan, party = MERCENARY[culture]
        merc = line(clan, party)
        merc['kind'] = 'mercenary'
        outlaws = [line(c) for c in OUTLAW[culture]]
        for o in outlaws:
            if o['kind'] not in ('mafia', 'nomad', 'sect'):
                raise SystemExit('outlaw line %s is not mafia, nomad or sect' % o['sourceClan'])
        entry = od(('mercenary', merc), ('outlaws', outlaws))
        if culture in OUTLAW_FALLBACK:
            entry['outlawFallback'] = OUTLAW_FALLBACK[culture]
        troops[culture] = entry
    outlaw_tiers = collections.Counter(int(e.get('tier')) for e in minor.values() if e.get('is_outlaw') == 'true')
    merc_tiers = collections.Counter(int(e.get('tier')) for e in minor.values() if e.get('is_clan_type_mercenary') == 'true')

    wanderer_templates = [od(('id', e.get('id')), ('culture', ref(e.get('culture'))), ('female', e.get('is_female') == 'true'), ('epithet', epithet(e.get('name'))))
                          for e in roots['NPCCharacters'] if e.get('occupation') == 'Wanderer' and e.get('is_template') == 'true']
    # MMR-287 (RT-32): the templates whose backstory names a Calradian proper noun, from the census tool's tracked output.
    census = json.load(open(os.path.join(REPO, BACKSTORY_CENSUS), encoding='utf-8'))
    if census['catalogSha256'] != sha(os.path.join(REPO, CATALOG)) or sorted(r['id'] for r in census['rows']) != sorted(t['id'] for t in wanderer_templates):
        raise SystemExit('the backstory census was made from another catalog; re-run tools/census_wanderer_backstories.py')
    unsafe = [t['id'] for t in wanderer_templates if t['id'] in set(census['unsafe'])]

    base = od(('fiefs', towns + castles), ('towns', towns), ('castles', castles), ('kingdoms', len(kingdoms)), ('nobleClans', len(nobles)),
              ('aliveLords', len(lords)), ('adultLords', adults), ('minorLords', len(lords) - adults),
              ('mercenaryClans', sum(1 for e in minor.values() if e.get('is_clan_type_mercenary') == 'true')),
              ('outlawClans', sum(1 for e in minor.values() if e.get('is_outlaw') == 'true')),
              ('fiefsPerKingdom', od(*sorted(fiefs.items()))), ('source', CATALOG))
    if base['fiefs'] != 133:
        raise SystemExit('the installed War Sails campaign has %d fiefs, not 133' % base['fiefs'])

    policy = od(
        ('schema', 'mnbl.population'), ('version', 1),
        ('notes', 'Map epic 2 population policy (MMR-248; findings sections 16 and 17). Owners: WorldBridge population.cpp (MMR-279, the plan '
                  'in campaign.json v2), FactionFactory and HeroFactory (MMR-280, the emitted clans and heroes) and the wanderer daily tick '
                  '(MMR-281). Generated by tools/make_population_policy.py from the merged campaign catalog. Every target scales with the '
                  'world\'s fiefs F over base.fiefs (133, the installed War Sails campaign; one base everywhere, RT-03): kingdoms '
                  'round(perBase x F / 133), at least min; houses perBase x F / 133 and at least minPerKingdom x kingdoms where the world '
                  'allows; adult lords about adultLordsPerHouse per house; companies and outlaw clans perBase x F / 133; wanderers W the '
                  'smallest integer not below the float32 product perTown x towns (War Sails\' own rule, RT-04). Every count is a starting '
                  'value MMR-279 checks against its targets.'),
        ('streams', od(('rule', 'value(stream, version, world prefix, entity, draw) = first 8 bytes of sha256("mnbl.population-stream/1\\n" + stream '
                                '+ "\\n" + version + "\\n" + prefix + "\\n" + entity + "\\n" + draw), big-endian'),
                       ('houses', 1), ('kin', 1), ('traits', 1), ('wanderers', 1), ('companies', 1), ('outlaws', 1), ('wars', 1))),
        ('base', base),
        ('targets', od(
            ('kingdoms', od(('perBase', 9), ('min', 1), ('minHouses', 3), ('maxFiefs', largest_fiefs), ('maxFiefsSource', largest))),
            ('houses', od(('perBase', len(nobles)), ('minPerKingdom', 3), ('maxFiefs', 4))),
            ('adultLordsPerHouse', round(adults / float(len(nobles)), 2)),
            ('minorShare', round((len(lords) - adults) / float(len(lords)), 3)),
            ('companies', od(('perBase', 12), ('members', [6, 10]))),
            ('outlawClans', od(('perBase', 10), ('members', [4, 6]))),
            ('wanderers', od(('perTown', 0.6), ('rounding', 'ceilFloat32'))),
            ('engineChildren', 'ceil(adults / 2) per house, added by InitialChildGenerationCampaignBehavior (kept on, owner decision 15)'))),
        ('kingdomMerge', od(
            ('order', ['civilizationLand', 'presentationProximity', 'splitAboveMax']),
            ('crossWater', True),
            ('ruler', 'highestRankedRealm'),
            ('vassals', 'other realm sovereigns or synthesized regents head vassal houses that keep their own fiefs'),
            ('record', ['memberRealms', 'mergeSteps']))),
        ('wars', od(('perKingdom', 1), ('maxPerKingdom', 2), ('partners', ['landBorder', 'nearestBySea']), ('relation', -1))),
        ('houses', od(
            ('seatPriority', ['kingdomRuler', 'realmRuler', 'castellan', 'pretender', 'magistrate']),
            ('memberPriority', ['sovereign', 'pretender', 'castellan', 'founder', 'magistrate', 'commander']),
            ('kin', ['spouse', 'sibling', 'adultChild']),
            ('deadParents', True),
            ('minors', od(('enabled', True), ('requireBothParents', True))),
            ('size', od(('draw', 'chaotic'), ('min', 3), ('max', 8), ('cap', 10), ('minAdultLords', 2), ('eachSex', True))),
            ('age', od(('adultMin', 18), ('adultMax', 70), ('source', 'npcs.people appearance age, clamped'))),
            ('lordCultures', CULTURES),
            ('castellanRule', 'each castellan heads, or belongs to, the house owning its castle'))),
        ('companies', od(
            ('captains', ['remnantChampionHired', 'livingWarlord', 'domainHolder', 'synthesized']),
            ('members', ['ruinWarden', 'warlord', 'domainHolder', 'synthesizedSoldier']),
            ('tier', 4), ('flags', ['is_minor_faction', 'is_clan_type_mercenary']), ('home', 'town'))),
        ('outlaws', od(
            ('leaders', ['banditBandLeader', 'deserterBandLeader', 'ruinCampChief', 'synthesized']),
            ('kinds', ['mafia', 'nomad', 'sect']),
            ('tier', outlaw_tiers.most_common(1)[0][0]), ('installedTiers', od(*[(str(k), v) for k, v in sorted(outlaw_tiers.items())])),
            ('flags', ['is_minor_faction', 'is_outlaw']),
            ('maxWarKingdoms', 2), ('home', 'townOfCulture'))),
        ('troopTemplates', od(
            ('rule', 'companies and outlaw clans of a presentation culture take default_party_template and the 4 minor_faction_character_templates '
                     'of their line here, never a lord party template; an outlaw clan takes the first line of its culture whose kind it is, else '
                     'the first line. Tier is the source clan\'s installed tier'),
            ('mercenaryTiers', od(*[(str(k), v) for k, v in sorted(merc_tiers.items())])),
            ('cultures', troops))),
        ('lordTraits', od(
            ('rule', 'each generated lord draws one personality vector from joint, weighted by count, with the traits stream seeded by its hero '
                     'id; template <Traits> are never copied (RT-05). sameCultureSharedShare is the installed share of alive XML lords whose '
                     'vector another lord of their culture also has, the bound MMR-280 checks'),
            ('traits', PERSONALITY),
            ('lords', len(lords)),
            ('sameCultureSharedShare', round(sharing / float(len(lords)), 4)),
            ('marginals', od(*[(p, od(*[(str(v), n) for v, n in sorted(collections.Counter(l[1][i] for l in lords).items())])) for i, p in enumerate(PERSONALITY)])),
            ('joint', [od(('values', list(v)), ('count', n)) for v, n in sorted(joint.items(), key=lambda x: (-x[1], x[0]))]))),
        ('wanderers', od(
            ('rule', 'W wanderers are kept alive (hired included, as vanilla counts them); SwapCompanions is kept; TryKillCompanion never runs; '
                     'each day with rotation.dailyChance one unmet, unhired wanderer retires without a death notice and a fresh FWGC++-style '
                     'wanderer replaces it; below W the tick tops up. A new wanderer is made in four steps (CreateSpecialHero from a template '
                     'of the pool, AdjustEquipments, ChangeState(Active), EnterSettlementAction), keeps its FWGC++ sex and an epithet; the pool '
                     'is the installed templates minus backstoryUnsafe, distinct within a culture where possible, with a recorded cross-culture '
                     'fallback. A runtime wanderer takes its name, epithet and sex from campaign.json wandererReserve: per presentation culture '
                     'of the towns, reserve FWGC++ residents of its civilizations (sex as the residents\', names no other hero carries) with an '
                     'archetypal FWGC++ epithet (one two or more of the world\'s people carry; else the template\'s) (MMR-281)'),
            ('sources', ['champion', 'magister', 'exile', 'domainHolder']),
            ('rotation', od(('dailyChance', 0.1), ('retire', 'unmetUnhired'))),
            ('topUp', True), ('keepEpithet', True), ('sex', 'source'),
            ('reserve', 24),
            ('backstoryUnsafe', unsafe),
            ('backstoryUnsafeOwner', 'MMR-287: the templates whose conversation lines name a Calradian proper noun (settlement, realm, clan, hero, '
                                     'Calradia, the emperor or a reviewed lore noun); the pool every generated wanderer draws from (HeroFactory.WandererPool, '
                                     'at new game and at top-up) is the other templates'),
            ('backstoryCensus', BACKSTORY_CENSUS),
            ('fallback', od(*[(c, WANDERER_FALLBACK[c]) for c in CULTURES])),
            ('templates', wanderer_templates))),
        ('spill', od(('order', ['house', 'wanderer', 'company']), ('houseCap', 10), ('regalToCompany', False))),
        # HeroFactory's per-settlement notable caps (rosters.json notables.caps), which the population census applies (MMR-279, RT-11).
        ('census', od(('notableCaps', od(('source', 'worldgen/policies/rosters.json notables.caps (HeroFactory)'),
                                          ('town', od(*sorted(caps['town'].items()))), ('village', od(*sorted(caps['village'].items()))))),
                      ('assertUnassigned', 0))),
    )
    out = os.path.join(REPO, 'worldgen/policies/population.json')
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(policy, f, indent=1)
        f.write('\n')
    print('wrote', out, 'fiefs', base['fiefs'], 'largest kingdom', largest, largest_fiefs, 'lords', len(lords), 'adults', adults,
          'shared', policy['lordTraits']['sameCultureSharedShare'], 'wanderer templates', len(wanderer_templates))


if __name__ == '__main__':
    main()
