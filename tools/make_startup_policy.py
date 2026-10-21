"""MMR-34: write worldgen/policies/startup.json, the versioned policy for a generated campaign's startup (docs/campaign-behavior-compatibility.md).

From the installed campaign (hash-checked catalog) it derives what the startup must supply for the installed behaviours to work on a
generated map, never a Calradia coordinate or id:
  * hideouts: the installed density per town, the installed bandit culture of the hideouts nearest each town culture (weights), and the
    installed distances (to the nearest settlement and between hideouts) relative to the installed town spacing;
  * bandit clans: the installed land bandit clans the installed behaviours look up by id (the MMR-34 behaviour scan), re-created as world
    aliases with a generated hideout as their home; the two pirate clans are MMR-35's (naval);
  * the retirement retreat: the installed retirement_retreat record (RetirementCampaignBehavior finds it by id and dereferences it every
    hour), re-created as a world alias at a generated land point;
  * starts: one per creation culture (the installed main cultures character creation offers), each re-emitted so no installed start point
    survives; what a player start needs (a port town with food, notables to recruit from, and a sea destination in the same open basin);
  * spawn anchors: the positions the installed behaviours spawn parties at, which must be passable land;
  * quality: the codes a refused startup reports (GeneratedCampaignInitializer.StartupException), and which may pass on another seed.
Usage: python tools/make_startup_policy.py
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
NAVAL_CLANS = ['northern_pirates', 'southern_pirates']   # MMR-35: pirate clans and spawn intents
# Map epic 2 (MMR-248, RT-28): the bandit cultures each natural land biome favours (terrain-materials.json biome cores; authored).
BIOME_WEIGHTS = {
    'desert': {'desert_bandits': 1.0},
    'grassland': {'steppe_bandits': 0.5, 'forest_bandits': 0.3, 'mountain_bandits': 0.2},
    'forest': {'forest_bandits': 1.0}, 'rainforest': {'forest_bandits': 1.0}, 'boreal_forest': {'forest_bandits': 1.0},
    'marsh': {'forest_bandits': 1.0},
    'exposed_rock': {'mountain_bandits': 1.0},
    'cold_tundra': {'mountain_bandits': 0.5, 'forest_bandits': 0.5},
    'tundra': {'mountain_bandits': 1.0}, 'snow': {'mountain_bandits': 1.0}, 'land_ice': {'mountain_bandits': 1.0},
}


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def od(*pairs):
    return collections.OrderedDict(pairs)


def main():
    receipt = json.load(open(os.path.join(REPO, CATALOG), encoding='utf-8-sig'))
    docs = {}
    for d in receipt['outputs']['documents']:
        if d['id'] in ('Settlements', 'Factions', 'SPCultures'):
            if sha(d['path']) != d['sha256']:
                raise SystemExit('catalog %s differs from its receipt' % d['id'])
            docs[d['id']] = d
    root = ET.parse(docs['Settlements']['path']).getroot()
    pos, kind, culture = {}, {}, {}
    retreat = None
    for s in root:
        comps = s.find('Components')
        if comps is None or not len(comps):
            continue
        c = comps[0]
        i = s.get('id')
        pos[i] = (float(s.get('posX')), float(s.get('posY')))
        culture[i] = (s.get('culture') or '').split('.', 1)[-1]
        if c.tag == 'Town':
            kind[i] = 'castle' if c.get('is_castle') == 'true' else 'town'
        elif c.tag == 'Village':
            kind[i] = 'village'
        elif c.tag == 'Hideout':
            kind[i] = 'hideout'
        elif c.tag == 'CustomSettlementComponent' and c.get('component_name') == 'RetirementSettlementComponent':
            retreat = s
    towns = sorted(i for i in kind if kind[i] == 'town')
    hideouts = sorted(i for i in kind if kind[i] == 'hideout')
    settlements = sorted(i for i in kind if kind[i] in ('town', 'castle', 'village'))
    dist = lambda a, b: math.hypot(pos[a][0] - pos[b][0], pos[a][1] - pos[b][1])
    spacing = statistics.mean(min(dist(t, u) for u in towns if u != t) for t in towns)
    weights = collections.OrderedDict()
    for h in hideouts:
        town = min(towns, key=lambda t: (dist(h, t), t))
        weights.setdefault(culture[town], collections.Counter())[culture[h]] += 1
    to_settlement = sorted(min(dist(h, s) for s in settlements) / spacing for h in hideouts)
    between = sorted(min(dist(h, g) for g in hideouts if g != h) / spacing for h in hideouts)
    factions = ET.parse(docs['Factions']['path']).getroot()
    bandits = sorted(f.get('id') for f in factions if f.get('is_bandit') == 'true' and f.get('id') not in NAVAL_CLANS)
    cultures = {c.get('id'): c for c in ET.parse(docs['SPCultures']['path']).getroot()}
    # The engine's bandit-faction rule (Clan.IsBanditFaction: a bandit culture that can have settlements and no naval capability): the
    # bandit cultures BanditSpawnCampaignBehavior keys its hideouts by.
    clan_culture = {f.get('id'): (f.get('culture') or '').split('.', 1)[-1] for f in factions}
    required = sorted({clan_culture[b] for b in bandits if cultures[clan_culture[b]].get('is_bandit') == 'true' and cultures[clan_culture[b]].get('can_have_settlement') == 'true'})
    if not set(required) <= {culture[h] for h in hideouts}:
        raise SystemExit('a required bandit culture has no installed hideout to take a prototype from: %s' % sorted(set(required) - {culture[h] for h in hideouts}))
    creation = sorted(c for c, e in cultures.items() if e.get('is_main_culture') == 'true' and e.get('start_point_position_x') is not None)
    installed_starts = od(*[(c, [float(cultures[c].get('start_point_position_x')), float(cultures[c].get('start_point_position_y'))]) for c in creation])
    if retreat is None:
        raise SystemExit('the installed catalog has no retirement retreat')
    q = lambda xs, p: xs[min(len(xs) - 1, int(p * (len(xs) - 1) + 0.5))]
    economy = json.load(open(os.path.join(REPO, 'worldgen/policies/economy.json'), encoding='utf-8'))

    policy = od(
        ('schema', 'mnbl.startup'), ('version', 1),
        ('notes', 'MMR-34 generated campaign startup (docs/campaign-behavior-compatibility.md). Owner: GeneratedCampaignInitializer '
                  '(src/MNBL.ProceduralWorld/Campaign/GeneratedCampaignInitializer.cs). Every value comes from the installed campaign; no Calradia '
                  'coordinate or settlement id is carried into a generated world.'),
        ('streams', od(('rule', 'value(stream, version, world prefix, entity, draw) = first 8 bytes of sha256("mnbl.startup-stream/1\\n" + stream + "\\n" + '
                                'version + "\\n" + prefix + "\\n" + entity + "\\n" + draw), big-endian; construction only (the campaign draws its own '
                                'mutable gameplay randomness from MBRandom)'),
                       ('hideouts', 1), ('starts', 1))),
        ('passable', od(('rule', 'a spawn anchor or a placed position must lie on a travel-graph cell of these classes (cells.bin after repairs) in '
                                 'a land component (4-neighbour, columns wrap) of an MMR-28 land group that holds a town (a component takes the group '
                                 'of the settlements on it; the compiler\'s groups join components across its required bridges)'),
                        ('classes', ['land']))),
        ('hideouts', od(('rule', 'count = round(perTown x towns), shared out over the land groups with towns in proportion to their towns '
                                 '(largest remainder, at least one per land group that can hold one); a hideout takes a passable cell at least '
                                 'settlementDistance x spacing from every settlement and hideoutSpacing x spacing from every other hideout, '
                                 'candidates in hideouts-stream order; its bandit culture is drawn from cultureWeights of its nearest town\'s '
                                 'culture (all installed hideouts pooled for a culture without hideouts). Every requiredCultures culture then gets at '
                                 'least one hideout: a missing one takes the hideout whose nearest town weighs it most among the hideouts whose culture '
                                 'has more than one (BanditSpawnCampaignBehavior.CacheHideouts keys hideouts by culture, and at new game '
                                 'GetInfestedHideoutCount reads that map with each bandit clan\'s culture unchecked: a bandit culture without a hideout '
                                 'throws KeyNotFoundException, run 37036); a world with fewer hideouts than required cultures is refused. '
                                 'requiredCultures are the cultures of the installed land bandit clans that the engine counts as bandit factions '
                                 '(Clan.IsBanditFaction: a bandit culture with can_have_settlement and no naval capability)'),
                        ('perTown', round(len(hideouts) / float(len(towns)), 4)),
                        ('requiredCultures', required),
                        ('settlementDistance', round(q(to_settlement, 0.1), 4)), ('hideoutSpacing', round(between[0], 4)),
                        ('cultureWeights', od(*[(k, od(*sorted(v.items()))) for k, v in sorted(weights.items())])),
                        ('biome', od(('rule', 'map epic 2 (MMR-248 keys, MMR-287 RT-28): the bandit culture weights of a hideout are '
                                              '(1 - share) x its nearest town culture\'s cultureWeights + share x biomeWeights of the natural biome '
                                              'of its cell (both normalized to 1), and a hideout whose cell is within coastCells cells of sea adds '
                                              'coastWeight to coastCulture before normalizing; requiredCultures still each get a hideout. Every '
                                              'value is a starting value MMR-287 checks with its hideout census on worlds A and B'),
                                     ('share', 0.6),
                                     ('biomeWeights', od(*[(k, od(*sorted(v.items()))) for k, v in sorted(BIOME_WEIGHTS.items())])),
                                     ('coastCells', 1), ('coastCulture', 'sea_raiders'), ('coastWeight', 1.0))),
                        ('installed', od(('hideouts', len(hideouts)), ('towns', len(towns)), ('spacing', round(spacing, 2)),
                                         ('toSettlement', od(('minimum', round(to_settlement[0], 4)), ('p10', round(q(to_settlement, 0.1), 4)),
                                                             ('median', round(q(to_settlement, 0.5), 4)))),
                                         ('betweenHideouts', od(('minimum', round(between[0], 4)), ('median', round(q(between, 0.5), 4)))))))),
        ('banditClans', od(('rule', 'the installed land bandit clans, re-emitted with their installed ids as world aliases (installed behaviours and '
                                    'models find them by these ids: DefaultAlleyModel, HideoutCampaignBehavior, BanditInteractionsCampaignBehavior, '
                                    'DefaultBanditDensityModel, DesertersCampaignBehavior and others, evidence/fwg/behavior-scan.json); each one\'s '
                                    'initial home is the first generated hideout of its culture, else the first generated hideout. The pirate clans '
                                    '(%s) are MMR-35\'s' % ', '.join(NAVAL_CLANS)),
                           ('clans', bandits))),
        ('retreat', od(('rule', 'the installed retirement retreat record (CustomSettlementComponent RetirementSettlementComponent, its location '
                                'complex and scene), re-emitted with its installed ids as a world alias at the passable cell of the main land group '
                                'farthest from every settlement (a hidden spot, as the installed one); its culture is the presentation culture of '
                                'the nearest town'),
                       ('settlement', retreat.get('id')), ('component', retreat.find('Components')[0].get('id')))),
        ('starts', od(('rule', 'every creation culture (creationCultures: the installed main cultures with a start point, the cultures '
                               'character creation can offer whether or not the world presents through them) gets a start, re-emitted as its '
                               'start point so none of installedStartPoints survives: one of its towns that is a port town (its own port is the '
                               'operational port and its shipyard the ship source; the player embarks only at a town port, MMR-17), whose '
                               'trade-area food supply is at least foodMinimum, which has notables to recruit from, and whose port lies in an open '
                               'sea basin with another port town (the sea destination, the nearest such port); among qualifying towns, the starts '
                               'stream picks; a culture without a qualifying town of its own takes the qualifying town nearest its towns, and a '
                               'culture without towns the qualifying town the starts stream draws for it (fallback, reported); a world without any '
                               'qualifying town is refused. CultureObject.StartingPoint is the start town\'s gate (CharacterCreationManager.'
                               'ApplyFinalEffects places the main party there when it is navigable)'),
                      ('creationCultures', creation), ('installedStartPoints', installed_starts),
                      ('foodMinimum', economy['food']['installed']['p10']),
                      ('foodMinimumSource', 'the installed towns\' 10th-percentile trade-area food supply (economy.json food.installed.p10)'),
                      ('notablesMinimum', 1))),
        ('spawns', od(('rule', 'the positions installed behaviours spawn parties at: the player at the start gate; caravans at town gates '
                               '(CaravansCampaignBehavior); villagers at village centres (VillagerCampaignBehavior); lords at their clan\'s home '
                               'settlement gate (HeroSpawnCampaignBehavior); bandits at and around hideouts (BanditSpawnCampaignBehavior); every one '
                               'must be passable and land-connected to a town'),
                      ('kinds', ['player', 'caravan', 'villager', 'lord', 'bandit']))),
        ('quality', od(('rule', 'a world that cannot supply the startup is refused (GeneratedCampaignInitializer.StartupException) with these '
                                'codes; a refusal is retryable only when every code is (another seed may pass); MMR-44 records them with the '
                                'generation job and MMR-45 wires the initializer into generation'),
                       ('codes', od(
                           ('quality.startup_policy', od(('retryable', False), ('meaning', 'the startup policy is malformed or disagrees with the installed catalog'))),
                           ('quality.startup_catalog', od(('retryable', False), ('meaning', 'an installed record the startup re-creates (retreat, bandit clan, creation culture) is missing'))),
                           ('quality.startup_plan', od(('retryable', False), ('meaning', 'a runtime hero intent has no template or generated home (an MMR-32 defect)'))),
                           ('quality.startup_map', od(('retryable', True), ('meaning', 'the travel cells disagree with the land groups, or no land group holds two towns'))),
                           ('quality.startup_hideouts', od(('retryable', True), ('meaning', 'no hideout could be placed, or fewer hideouts than required bandit cultures'))),
                           ('quality.startup_retreat', od(('retryable', True), ('meaning', 'no passable cell for the retirement retreat'))),
                           ('quality.startup_no_start', od(('retryable', True), ('meaning', 'no town qualifies as a player start'))),
                           ('quality.startup_unplaceable_spawn', od(('retryable', True), ('meaning', 'a spawn anchor is not passable land connected to a town'))))))),
        ('provenance', od(('catalogReceipt', CATALOG), ('settlementsSha256', docs['Settlements']['sha256']), ('factionsSha256', docs['Factions']['sha256']),
                          ('culturesSha256', docs['SPCultures']['sha256']),
                          ('economySha256', sha(os.path.join(REPO, 'worldgen/policies/economy.json'))))),
    )
    out = os.path.join(REPO, 'worldgen/policies/startup.json')
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(policy, f, indent=1)
        f.write('\n')
    print('wrote', out, 'hideouts per town', policy['hideouts']['perTown'], 'distance', policy['hideouts']['settlementDistance'],
          'spacing', policy['hideouts']['hideoutSpacing'], 'bandit clans', bandits, 'required', required, 'creation', creation)


if __name__ == '__main__':
    main()
