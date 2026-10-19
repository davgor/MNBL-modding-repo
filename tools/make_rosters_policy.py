"""MMR-32: write worldgen/policies/rosters.json, the versioned hero and roster policy.

The rules (ages, families, notable targets and caps, companion bound, troop roots, RNG streams) are authored here; the
numbers the installed engine itself uses are taken from its IL and recorded with the assembly hash:
  * DefaultNotableSpawnModel.GetTargetNotableCountForSettlement: town Merchant 2, GangLeader 2, Artisan 1; village
    Headman 1, RuralNotable 2 (NotablesCampaignBehavior.SpawnNotablesAtGameStart spawns exactly these, unconditionally);
  * CompanionsCampaignBehavior.OnNewGameCreated: for (i = 0; (float)i < 0.6f * towns; i++), and companion age
    HeroComesOfAge + 5 + [0, 12);
  * the Occupation enum values.
Template pools are not copied: CharacterTemplateResolver reads each installed culture's lord_templates, notable_templates,
wanderer templates and troop roots from the merged campaign catalog (hash-pinned below) at build time.
The engine numbers are tied to the TaleWorlds.CampaignSystem assembly whose hash is recorded; tests/Campaign/CharacterTests.cs
checks that hash against config/runtime-api-lock.json, which Build.ps1 verifies against the installed game, so a game update
fails the tests until the numbers are re-read from the new IL.
Usage: python tools/make_rosters_policy.py [--game GAME_ROOT]
"""
import argparse
import collections
import hashlib
import json
import os

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CATALOG = 'evidence/runtime/in-game/campaign_catalog-10212.json'
DEFAULTS = 'evidence/runtime/in-game/generated_campaign-21284.json'
DOCUMENTS = ['NPCCharacters', 'SPCultures', 'partyTemplates', 'EquipmentRosters', 'Items', 'SkillSets', 'BodyProperties', 'ShipHulls', 'Factions']


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def od(*pairs):
    return collections.OrderedDict(pairs)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--game', default=r'D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord')
    a = ap.parse_args()
    receipt = json.load(open(os.path.join(REPO, CATALOG), encoding='utf-8-sig'))
    docs = {d['id']: d for d in receipt['outputs']['documents']}
    for name in DOCUMENTS:
        if sha(docs[name]['path']) != docs[name]['sha256']:
            raise SystemExit('catalog %s differs from its receipt' % name)
    defaults = json.load(open(os.path.join(REPO, DEFAULTS), encoding='utf-8-sig'))['outputs']['worlds'][0]['registration']['verifiedLiveDefaultReferences']
    campaign_dll = os.path.join(a.game, 'bin', 'Win64_Shipping_Client', 'TaleWorlds.CampaignSystem.dll')

    policy = od(
        ('schema', 'mnbl.rosters'), ('version', 1),
        ('notes', 'MMR-32 heroes and rosters (docs/heroes.md). Owners: HeroFactory and CharacterTemplateResolver (src/MNBL.ProceduralWorld/Campaign/). '
                  'Lords and their synthesized families are XML heroes (NPCCharacters + Heroes) for the MMR-30 registry; notables and companions are '
                  'runtime creation intents, because the installed engine creates them through HeroCreator at new game (MMR-34 replaces the vanilla '
                  'spawns and instantiates these with HeroCreator.CreateSpecialHero(template, settlement, null, null, age) and SetName, keeping the '
                  'intent id in its own map: the engine mints the StringId). Body ranges, equipment, skills and troops are installed templates; '
                  'identities and names come from the world.'),
        ('streams', od(
            ('rule', 'value(stream, version, world prefix, entity, draw) = first 8 bytes of sha256("mnbl.hero-stream/1\\n" + stream + "\\n" + version + "\\n" + '
                     'prefix + "\\n" + entity + "\\n" + draw), big-endian. Each purpose has its own stream; re-versioning one changes what it draws and '
                     'what is derived from that: a notable\'s or companion\'s sex follows its template, a synthesized name follows the sex, a family '
                     'follows the ages. Ownership, occupations and clans use no stream.'),
            ('templates', 1), ('ages', 1), ('sex', 1), ('names', 1), ('family', 1), ('companions', 1), ('bodies', 1))),
        ('occupations', od(
            ('engine', od(('Lord', 3), ('Wanderer', 16), ('Artisan', 17), ('Merchant', 18), ('Preacher', 19), ('Headman', 20), ('GangLeader', 21), ('RuralNotable', 22))),
            ('engineSource', 'TaleWorlds.CampaignSystem.Occupation enum literals (installed IL)'),
            ('fromCompiler', od(('Lord', 'Lord'), ('Merchant', 'Merchant'), ('Headman', 'Headman'), ('Preacher', 'Preacher'), ('GangLeader', 'GangLeader'), ('Wanderer', 'Wanderer'))))),
        ('lords', od(
            ('templates', 'the presentation culture\'s lord_templates (installed is_template Lord NPCCharacters), one drawn from the templates stream; the hero keeps '
                          'the template\'s body range (face key template), skill set and traits, and takes the world\'s id, name, culture, age and sex'),
            ('equipment', 'no installed lord template is female and their rosters carry men\'s gear, so a man wears the template\'s equipment sets and a woman '
                          'one battle and one civilian roster of her culture flagged IsLordTemplate and IsFemaleTemplate (never child or teenager), from the '
                          'templates stream. Every worn item must exist, fit its slot and be usable by the wearer\'s sex'),
            ('bodies', 'an XML hero gets an explicit body: HeroBodies (in game) draws it with BodyProperties.GetRandomBodyProperties(race 0, sex, the '
                       'template\'s body range, the range\'s hair/beard/tattoo tags, seed from the bodies stream). Hero.Deserialize otherwise gives every '
                       'hero of a template the range minimum'),
            ('bound', 'the source lords are MMR-27\'s lord records (sovereign or regent per realm, castellans, pretenders, founders), never one per person of a '
                      'population; with relatives, XML heroes number at most source lords + families.maxPerClan per clan'),
            ('ages', od(('sovereign', [35, 62]), ('regent', [40, 66]), ('pretender', [22, 45]), ('castellan', [28, 56]), ('founder', [25, 50]))),
            ('minimumAge', 20),
            ('femaleShare', 0.25),
            ('race', 'every hero uses the installed human body; the source raceId is kept as metadata (custom skeletons are out of scope)'))),
        ('families', od(
            ('rule', 'every clan leader of at least minimumLeaderAge gets one synthesized spouse of the opposite sex, in the leader\'s clan; each couple gets 0..maxChildren '
                     'children (family stream), in the clan, with father and mother set. Spouse links are written on both heroes. Synthesized relatives are '
                     'adapter-synthesized, never FWGC++ facts'),
            ('minimumLeaderAge', 25), ('spouseAgeGap', [-8, 6]), ('spouseMinimumAge', 20), ('maxChildren', 2),
            ('childAge', 'adult heirs: uniform in [18, min(min(father age, mother age) - 18, 30)]; a couple whose upper bound is below 18 has no heir'),
            ('childFemaleShare', 0.5),
            ('names', 'the presentation culture\'s installed male_names / female_names, drawn from the names stream'),
            ('maxPerClan', 3))),
        ('notables', od(
            ('targets', od(('town', od(('Merchant', 2), ('GangLeader', 2), ('Artisan', 1))), ('village', od(('Headman', 1), ('RuralNotable', 2))))),
            ('targetsSource', 'DefaultNotableSpawnModel.GetTargetNotableCountForSettlement (installed IL); NotablesCampaignBehavior.SpawnNotablesAtGameStart '
                              'creates exactly these at new game, so recruitment, workshops and issues expect them'),
            ('caps', od(('town', od(('Merchant', 4), ('GangLeader', 2), ('Artisan', 2), ('Preacher', 1))), ('village', od(('Headman', 1), ('RuralNotable', 2))))),
            ('rule', 'source notables fill their settlement first, in uid order, up to the cap of their occupation; a source notable beyond the cap is not '
                     'instantiated (disposition bounded). Every target not met by source notables is filled with synthesized notables. Castles have none'),
            ('ages', [25, 65]),
            ('templates', 'the settlement culture\'s notable_templates of that occupation; the template decides the sex'),
            ('synthesizedName', '{name} of {settlement}'))),
        ('companions', od(
            ('factor', 0.6), ('factorSource', 'CompanionsCampaignBehavior.OnNewGameCreated: for (i = 0; (float)i < 0.6f * Town.AllTowns.Count; i++) (installed IL); '
                                              '0.6f * 45 = 27.000002, so 28 companions at 45 towns'),
            ('rule', 'as many source wanderers as that loop creates companions become companions, chosen by companions-stream rank of their uid; the rest '
                     'are not instantiated (disposition bounded). None is synthesized: the source supplies more than the bound'),
            ('ages', [23, 34]), ('agesSource', 'CompanionsCampaignBehavior.CreateCompanionAndAddToSettlement: HeroComesOfAge (18) + 5 + MBRandom.RandomInt(12)'),
            ('home', 'a town of the companion\'s presentation culture (templates stream), else any town'),
            ('templates', 'the presentation culture\'s is_template Wanderer NPCCharacters; the template decides the sex'))),
        ('troops', od(
            ('characterRoots', ['basic_troop', 'elite_basic_troop', 'melee_militia_troop', 'ranged_militia_troop', 'melee_elite_militia_troop', 'ranged_elite_militia_troop',
                                'militia_veteran_archer', 'caravan_master', 'caravan_guard', 'prison_guard', 'guard', 'tournament_master', 'villager', 'basic_mercenary_troops',
                                'shipwright', 'shipyard_worker', 'bandit_bandit', 'bandit_raider', 'bandit_chief', 'bandit_boss']),
            ('partyTemplateRoots', ['default_party_template', 'villager_party_template', 'militia_party_template', 'rebels_party_template', 'vassal_reward_party_template',
                                    'bandit_boss_party_template', 'settlement_patrol_template_level_1', 'settlement_patrol_template_level_2', 'settlement_patrol_template_level_3',
                                    'settlement_patrol_template_coastal', 'fishing_party_template', 'caravan_party_templates', 'elite_caravan_party_templates']),
            ('ships', ['available_ship_hulls']),
            ('roles', od(
                ('recruitment', 'basic_troop and elite_basic_troop trees: notables offer volunteers of the settlement culture'),
                ('garrison', 'default_party_template: GarrisonTroopsCampaignBehavior.FillGarrisonPartyOnNewGame fills garrisons from Town.Culture.DefaultPartyTemplate (installed IL)'),
                ('militia', 'melee/ranged (elite) militia troops and militia_party_template'),
                ('bandits', 'every installed bandit culture (is_bandit): bandit_* troops and bandit_boss_party_template'),
                ('naval', 'settlement_patrol_template_coastal, fishing_party_template, shipwright, shipyard_worker and available_ship_hulls (NavalDLC)'))),
            ('banditClans', 'the default_party_template of every installed bandit clan (Factions.xml is_bandit), from which BanditSpawnCampaignBehavior spawns'),
            ('rule', 'for every presentation culture and every installed bandit culture, each root and every troop reachable through upgrade targets and party '
                     'template stacks (and the ship hulls under ship_hulls) must resolve, with its equipment, items (existing, fitting their slot, usable by '
                     'the troop\'s sex), face key template and skill set; an upgrade cycle, a missing target or a broken required root refuses the build. A '
                     'broken optional hero template or lord roster is excluded from its pool and reported; only an empty pool refuses'))),
        ('characterCreation', od(
            ('cultures', 'CharacterCreationCampaignBehavior.InitializeCharacterCreationCultures adds aserai, battania, empire, khuzait, sturgia and vlandia, and '
                         'NavalCharacterCreationCampaignBehavior adds nord (installed IL). GeneratedCharacterCreation registers a content handler after both '
                         '(priority 2000; theirs are 800 and 1000) that leaves only the cultures the generated world presents through. The installed '
                         'culture screen (CharacterCreationCultureStageVM.SortCultureList) places the six vanilla cultures with Enumerable.Single and '
                         'crashes when one is missing, so while a restricted list is active a Harmony prefix skips that sort'),
            ('origins', 'parent options check the culture; later menus check the parent occupation (education: urban or rural) and the youth options set '
                        'the title type that names the player\'s creation rosters (player_char_creation_<culture>_<title>_<m|f>). The in-game audit selects '
                        'every parent origin, evaluates every later menu for it, selects every youth option for its title type and resolves every menu '
                        'character\'s rosters and items. The start position (CultureObject.StartingPoint) belongs to MMR-34'))),
        ('provenance', od(
            ('catalogReceipt', CATALOG),
            ('catalogDocuments', od(*[(n, docs[n]['sha256']) for n in DOCUMENTS])),
            ('defaultReferences', DEFAULTS), ('defaultReferencesSha256', sha(os.path.join(REPO, DEFAULTS))), ('defaultReferenceCount', len(defaults)),
            ('campaignSystemSha256', sha(campaign_dll)))),
    )
    out = os.path.join(REPO, 'worldgen/policies/rosters.json')
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(policy, f, indent=1, ensure_ascii=False)
        f.write('\n')
    print('wrote', out, len(defaults), 'default references')


if __name__ == '__main__':
    main()
