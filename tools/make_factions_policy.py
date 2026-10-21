"""MMR-31: write worldgen/policies/factions.json, the versioned faction policy.

The rules (sovereigns, clans, tiers, diplomacy, zero-town realms, texts, RNG streams) are authored here. The asset pools
are read from installed files and hash-pinned:
  * banner_icons.xml (Native): the BannerColors palette (id -> ARGB); every installed module's banner_icons.xml (Native, NavalDLC):
    the icon and background ids, so each culture's icon pool and the background mesh are checked to exist in an installed file;
  * the merged campaign catalog's Kingdoms.xml: the official kingdoms' ruler titles per culture (a reusable localized string)
    and their banner layer layout; and Factions.xml: the official noble clans' background mesh and sizes;
  * worldgen/templates/cultures/<culture>.json: each culture's possible_clan_banner_icon_ids and default_policies.
Nothing geopolitical is copied: no official kingdom, clan, hero, relationship or settlement.
Usage: python tools/make_factions_policy.py [--game GAME_ROOT]
"""
import argparse
import collections
import hashlib
import json
import os
import xml.etree.ElementTree as ET

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--game', default=r'D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord')
    a = ap.parse_args()
    icons_xml = os.path.join(a.game, 'Modules', 'Native', 'ModuleData', 'banner_icons.xml')
    icon_files = sorted(os.path.join(a.game, 'Modules', m, 'ModuleData', 'banner_icons.xml') for m in os.listdir(os.path.join(a.game, 'Modules'))
                        if os.path.isfile(os.path.join(a.game, 'Modules', m, 'ModuleData', 'banner_icons.xml')))
    installed_icons = {int(e.get('id')) for f in icon_files for e in ET.parse(f).getroot().iter('Icon')}
    backgrounds_installed = {int(e.get('id')) for f in icon_files for e in ET.parse(f).getroot().iter('Background')}
    receipt = json.load(open(os.path.join(REPO, 'evidence/runtime/in-game/campaign_catalog-10212.json'), encoding='utf-8-sig'))
    docs = {d['id']: d for d in receipt['outputs']['documents']}
    for name in ('Kingdoms', 'Factions'):
        if sha(docs[name]['path']) != docs[name]['sha256']:
            raise SystemExit('catalog %s differs from its receipt' % name)
    presentations = json.load(open(os.path.join(REPO, 'worldgen/policies/culture-presentations.json'), encoding='utf-8-sig'))

    colors = [{'id': int(c.get('id')), 'argb': c.get('hex')[2:].upper()} for c in ET.parse(icons_xml).getroot().find('.//BannerColors')]
    kingdoms = ET.parse(docs['Kingdoms']['path']).getroot()
    titles = collections.OrderedDict()
    for k in kingdoms:
        culture = k.get('culture').split('.', 1)[1]
        titles.setdefault(culture, k.get('ruler_title'))
    nobles = [e for e in ET.parse(docs['Factions']['path']).getroot() if e.get('is_noble') == 'true']
    backgrounds = collections.Counter(e.get('banner_key').split('.')[0] for e in nobles)
    sizes = collections.Counter(e.get('banner_key').split('.')[3] for e in nobles)
    icon_sizes = collections.Counter(e.get('banner_key').split('.')[13] for e in nobles if len(e.get('banner_key').split('.')) >= 20)
    background_mesh = int(backgrounds.most_common(1)[0][0])
    if background_mesh not in backgrounds_installed:
        raise SystemExit('background mesh %d is in no installed banner_icons.xml' % background_mesh)
    cultures = collections.OrderedDict()
    for culture in presentations['officialCultures']:
        template = json.load(open(os.path.join(REPO, 'worldgen/templates/cultures', culture + '.json'), encoding='utf-8-sig'))
        values = template['values']
        cultures[culture] = {
            'rulerTitle': titles[culture],
            'bannerIcons': [int(i) for i in values['possible_clan_banner_icon_ids']],
            'policies': list(values['default_policies']),
            'template': 'worldgen/templates/cultures/%s.json' % culture,
        }
        missing = [i for i in cultures[culture]['bannerIcons'] if i not in installed_icons]
        if missing:
            raise SystemExit('culture %s banner icons %s are in no installed banner_icons.xml' % (culture, missing))

    policy = collections.OrderedDict([
        ('schema', 'mnbl.factions'), ('version', 1),
        ('notes', 'MMR-31 faction definitions (docs/factions.md), emitting the campaign.json v2 population blocks since MMR-280. Owner: FactionFactory '
                  '(src/MNBL.ProceduralWorld/Campaign/FactionFactory.cs). Kingdoms, noble houses, mercenary companies and outlaw clans are intents over the '
                  'compiler records (MMR-27, the MMR-279 population pass): identities, relationships, wars and ownership come from the generated world; '
                  'assets (cultures, banner meshes, palette, icons, policies, title strings) are the installed public ones listed here. C03 (MMR-32) fills hero bodies '
                  'and rosters, C04 (MMR-33) binds fiefs, C07 (MMR-36) proves registered closure.'),
        ('streams', collections.OrderedDict([
            ('rule', 'value(stream, version, world prefix, entity id, draw) = first 8 bytes of sha256("mnbl.faction-stream/1\\n" + stream + "\\n" + version + "\\n" + prefix + "\\n" + entity + "\\n" + draw), '
                     'big-endian. Each purpose has its own stream and version, so a new or re-versioned cosmetic stream never changes ownership or another stream. '
                     'Ownership and membership use no randomness at all.'),
            ('banner', 1), ('colors', 1), ('relations', 1)])),
        ('sovereign', 'MMR-27 resolves every realm\'s ruler: its sovereign, or the synthesized regent (regent:<realm uid>) when sovereign_uid is null. '
                      'The ruler always leads the ruling clan. A kingdom intent without a ruler, ruling clan or capital is refused.'),
        ('rulingClan', collections.OrderedDict([
            ('source', 'campaign.json v2 kingdoms[].rulingHouse: the planned house of the merged kingdom\'s ruler (MMR-279)'),
            ('tierByFiefs', [{'minFiefs': 1, 'tier': 4}, {'minFiefs': 3, 'tier': 5}, {'minFiefs': 6, 'tier': 6}]),
            ('fiefs', 'towns plus castles of the kingdom')])),
        ('houses', collections.OrderedDict([
            ('rule', 'every planned house (campaign.json v2 houses[], MMR-279) is one noble clan of its kingdom: owner its leader, home its seat (one of its fiefs), '
                     'members its living planned Lords (adults, and minors with both parents set); its dead parents (alive false) belong to it but are not members. '
                     'There is no clan per lord (the vassal-per-lord path is gone, clean break)'),
            ('tierByFiefs', [{'minFiefs': 1, 'tier': 3}, {'minFiefs': 2, 'tier': 4}, {'minFiefs': 3, 'tier': 5}]),
            ('fiefs', 'the towns and castles the house owns (campaign.json v2 houses[].fiefs; at most 4)')])),
        ('minorClans', collections.OrderedDict([
            ('rule', 'every planned mercenary company and outlaw clan (campaign.json v2 mercenaryCompanies[], outlawClans[], MMR-279) is a minor clan with no kingdom: '
                     'owner its leader, home its town, its own tier, members its planned Lords; default_party_template and the 4 minor_faction_character_templates '
                     'are its line of population.json troopTemplates (RT-03), never a lord party template'),
            ('companyFlags', ['is_minor_faction', 'is_clan_type_mercenary']),
            ('outlawFlags', ['is_minor_faction', 'is_outlaw']),
            ('kindFlags', collections.OrderedDict([('mafia', 'is_mafia'), ('nomad', 'is_nomad'), ('sect', 'is_sect')])),
            ('wars', 'an outlaw clan is at war with every kingdom of its culture (the engine declares it, as for the installed outlaw clans); campaign.json v2 '
                     'outlawClans[].wars records them within population.json outlaws.maxWarKingdoms, and the intent keeps them')])),
        ('resources', collections.OrderedDict([
            ('tier', 'Clan.Deserialize sets Tier and renown from it (CalculateInitialRenown); the tier above is the clan resource this ticket sets'),
            ('goldAndInfluence', 'not set here: hero gold and clan influence follow the installed new-game logic once C03 instantiates the heroes')])),
        ('diplomacy', collections.OrderedDict([
            ('stance', 'campaignWars'),
            ('stanceRule', 'each campaign.json v2 wars[] pair (MMR-279, RT-06) starts at war: both kingdoms carry <relationship kingdom="Kingdom.<other>" value="-1" '
                           'isAtWar="true"/>, as the installed spkingdoms.xml writes its starting wars (Kingdom.Deserialize reads only the sign: below 0 calls '
                           'FactionManager.DeclareWar). Every other pair starts neutral, the engine default, and is written nowhere.'),
            ('warRelation', -1),
            ('baseRelation', 0), ('spread', 20),
            ('rulerRelation', 'each unordered kingdom pair once, lower id first: the relation between the two rulers is baseRelation + round(spread * (2 * u - 1)), u from '
                              'the relations stream for "<a>|<b>". It is a hero relation intent for C03 (MMR-32), applied once the heroes exist; it is not written to Kingdoms.xml'),
            ('clanRelations', 'none set: clans start at the engine default')])),
        ('zeroTownRealm', collections.OrderedDict([
            ('action', 'dissolve'),
            ('rule', 'a realm with no town after the compiler and travel exclusions is not a kingdom: no kingdom or clan intent is emitted, its lords become unaffiliated '
                     'and the dissolution is recorded with a reason. MMR-27 refuses a capital outside the realm\'s towns and MMR-28 never removes a town, so the pinned '
                     'worlds have none; the rule closes the policy rather than keeping a kingdom with no valid initial state.')])),
        ('texts', collections.OrderedDict([
            ('kingdomEncyclopedia', 'The {realm} of the {civilization}, ruled from {capital}.'),
            ('clanEncyclopedia', 'A noble house of the {realm}.'),
            ('companyEncyclopedia', 'A company of mercenaries based in {home}.'),
            ('outlawEncyclopedia', 'An outlaw clan based in {home}.'),
            ('sourceNote', ' Its old records name it {source}.'),
            ('escaping', 'names keep their source text; localized strings are {=<id>}text with braces turned into parentheses (TextObject reads braces as variables); '
                         'XML attributes are escaped; control characters are dropped')])),
        ('banners', collections.OrderedDict([
            ('layout', 'two layers of 10 numbers: mesh.color1.color2.sizeX.sizeY.posX.posY.drawStroke.mirror.rotation; layer 1 the background, layer 2 the icon, as the '
                       'installed noble clans and kingdoms use'),
            ('backgroundMesh', background_mesh), ('backgroundSize', int(sizes.most_common(1)[0][0])),
            ('iconSize', int(icon_sizes.most_common(1)[0][0])), ('center', 764),
            ('icons', 'the presentation culture\'s possible_clan_banner_icon_ids (worldgen/templates/cultures)'),
            ('colors', 'two distinct palette colors per kingdom and per minor clan (background, icon), drawn in pairs from the colors stream, kingdoms first by id, then '
                       'minor clans by id; the ordered pair is unique world-wide (an equal or repeated pair draws the next pair). A house uses its kingdom\'s colors'),
            ('iconRule', 'the kingdom\'s icon is one banner-stream draw; each house of the kingdom (ruling house first, then the others by id) draws from the banner '
                         'stream until its icon differs from the kingdom\'s and every earlier house\'s of that kingdom. A kingdom with more banners than icons is refused. '
                         'A minor clan\'s icon is one banner-stream draw from its presentation culture\'s icons'),
            ('unique', 'every banner is unique in the world: colour pairs separate the kingdoms and minor clans, icons separate a kingdom\'s own banners. The colors '
                       'stream never changes an icon and the banner stream never changes a colour'),
            ('palette', colors)])),
        ('names', collections.OrderedDict([
            ('rule', 'kingdom, house, company and outlaw clan names come unique and clean from the compiler\'s name cleaner (MMR-279, MMR-286: no '
                     'Age tag, never an ordinal); a repeated one is refused. The source name stays in the intent (sourceName) and, for a renamed '
                     'kingdom, in its encyclopedia text (texts.sourceNote)')])),
        ('cultures', cultures),
        ('provenance', collections.OrderedDict([
            ('bannerIconsSha256', sha(icons_xml)),
            ('installedBannerIcons', [collections.OrderedDict([('path', os.path.relpath(f, a.game).replace(os.sep, '/')), ('sha256', sha(f))]) for f in icon_files]), ('catalogKingdomsSha256', docs['Kingdoms']['sha256']), ('catalogFactionsSha256', docs['Factions']['sha256']),
            ('culturePresentationsSha256', sha(os.path.join(REPO, 'worldgen/policies/culture-presentations.json'))),
            ('officialNobleBackgrounds', dict(backgrounds)), ('officialNobleSizes', dict(sizes))])),
    ])
    out = os.path.join(REPO, 'worldgen/policies/factions.json')
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(policy, f, indent=1, ensure_ascii=False)
        f.write('\n')
    print('wrote', out, len(colors), 'colors', {c: len(v['bannerIcons']) for c, v in cultures.items()}, 'background', policy['banners']['backgroundMesh'])


if __name__ == '__main__':
    main()
