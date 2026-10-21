"""MMR-34: write worldgen/policies/campaign-compatibility.json, the compatibility inventory of every installed campaign behaviour and game model
(docs/campaign-behavior-compatibility.md).

Input: evidence/fwg/behavior-scan.json (tools/Scan-CampaignBehaviors.ps1, the installed IL). Every scanned type gets a status:
  retained  works unchanged on a generated world: no world literal, id lookup, map extent or coordinate; or only map extents and scene
            queries the generated map supplies; or only literal lookups of installed templates (characters, items, equipment, item
            modifiers, party templates), which every generated world registers; or only literal comparisons that are simply never true;
  adapted   its world dependency is supplied through a supported seam (world aliases, generated hideouts, the retreat, culture start points,
            runtime intents);
  inactive  registered but its world-dependent part never runs in a Sandbox campaign (the War Sails storyline);
  excluded  removed or never added in the generated mode, with a product reason (StoryMode, the vanilla backstory).
The in-game startup audit checks the live roster against this file: every enabled type must be listed, excluded types must be absent,
adapted types present with their adaptation.
Usage: python tools/make_compatibility_inventory.py
"""
import collections
import hashlib
import json
import os

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCAN = 'evidence/fwg/behavior-scan.json'

BANDITS = ('adapted', 'finds the installed land bandit clans by id (behavior scan); the generated world re-creates them as world aliases with '
                      'generated hideouts as homes (startup.json banditClans, GeneratedCampaignInitializer)')
# A retained type with a world finding names why the finding is harmless (basis): a literal comparison that is never true in a generated
# world, a developer cheat, or a dependency another ticket supplies.
CURATED = {
    'TaleWorlds.CampaignSystem.CampaignBehaviors.BackstoryCampaignBehavior':
        ('excluded', 'its new-game handler writes the vanilla lords\' backstory log entries and dereferences lord_1_1, dead_lord_* and town_V6; a '
                     'generated world has no vanilla lords or towns, and generated history is not part of the product; GeneratedGameManager removes '
                     'it before event registration'),
    'SandBox.CampaignBehaviors.RetirementCampaignBehavior':
        ('adapted', 'finds retirement_retreat by id and dereferences it every hour; the generated world re-creates the installed retreat as a world '
                    'alias at a generated land point (startup.json retreat), so the behaviour is kept'),
    'TaleWorlds.CampaignSystem.CampaignBehaviors.NotablesCampaignBehavior':
        ('adapted', 'its game-start spawn (SpawnNotablesAtGameStart, one CreateNotable per target) is replaced by the MMR-32 notable intents '
                    '(GeneratedStartupBehavior, Harmony prefix mnbl.proceduralworld.startup); its daily replenishment is kept'),
    'TaleWorlds.CampaignSystem.CampaignBehaviors.CompanionsCampaignBehavior':
        ('adapted', 'its game-start companions (OnNewGameCreated, 0.6 x towns from random templates and towns) are replaced by the MMR-32 '
                    'companion intents (GeneratedStartupBehavior, Harmony prefix); its daily tick is replaced (MMR-281, world-lifetime prefix '
                    'mnbl.proceduralworld.world located by IL content, CompanionsTickPatch): SwapCompanions runs as installed, TryKillCompanion '
                    'never runs; GeneratedPopulationBehavior rotates unmet, unhired wanderers at the installed 10% daily chance without a death '
                    'notice and tops up below W (campaign census, hired included, heroes counted) with FWGC++-style wanderers made in the '
                    'installed four steps (CreateSpecialHero from a backstory-safe template, AdjustEquipments, ChangeState(Active), '
                    'EnterSettlementAction); get__desiredTotalCompanionCount is never patched; its item-modifier lookups are kept'),
    'TaleWorlds.CampaignSystem.CampaignBehaviors.HeroSpawnCampaignBehavior':
        ('retained', 'kept as installed (MMR-281): its minor-clan refill (SpawnMinorFactionHeroes) tops a minor clan up to MinorFactionHeroLimit 4 '
                     'alive lords, and every generated mercenary company starts with 6 to 10 and every outlaw clan with 4 to 6 XML lords, so it '
                     'adds none at new game; it spawns the clans\' parties in its new-game follow-ups, after the module marks the companies\' and '
                     'outlaw clans\' heroes IsMinorFactionHero (right after registration, GeneratedPopulationBehavior); no world literal, id '
                     'lookup, map extent or coordinate in its IL', None),
    'TaleWorlds.CampaignSystem.CampaignBehaviors.FactionDiscontinuationCampaignBehavior':
        ('retained', 'kept as installed (MMR-281): a kingdom without a settlement is dissolved and its clans have 28 days to join another before '
                     'they are destroyed; the homeless-house rule (GeneratedPopulationBehavior, RT-07) moves a generated house that left a '
                     'destroyed kingdom into the nearest kingdom of its culture (else the nearest) at the next daily tick through '
                     'ChangeKingdomAction.ApplyByJoinToKingdom, never the player\'s clan; no world literal, id lookup, map extent or coordinate '
                     'in its IL', None),
    'TaleWorlds.CampaignSystem.CampaignBehaviors.InitialChildGenerationCampaignBehavior':
        ('retained', 'kept on as in vanilla (owner decision 15, MMR-281): its one new-game run adds ceil(adults / 2) less the existing children to '
                     'each noble clan, cloned from a same-sex adult of the clan; the generated houses\' lords are XML heroes registered before '
                     'it runs, every house has an adult of each sex and lord cultures are the 7 vanilla cultures with child and teen equipment '
                     '(MMR-279/280), and nothing the module creates later is assigned to a house before it runs; no world literal, id lookup, '
                     'map extent or coordinate in its IL', None),
    'TaleWorlds.CampaignSystem.CampaignBehaviors.WorkshopsCampaignBehavior':
        ('adapted', "its new-game workshop initialization (InitializeWorkshopAction.ApplyByNewGame) regenerates each owner's whole name "
                    "(NameGenerator), which would discard the world's source names; the startup restores the intent names once the new game's "
                    "follow-ups end (GeneratedStartupBehavior, run 37940); later workshop changes in play retitle notables as the installed game does"),
    'TaleWorlds.CampaignSystem.CampaignBehaviors.CharacterCreationCampaignBehavior':
        ('adapted', 'lists the creation cultures (filtered to the world\'s presentation cultures, MMR-32 GeneratedCharacterCreation); the start '
                    'position is CultureObject.StartingPoint, which the generated world sets to each culture\'s start gate (startup.json starts)'),
    'NavalDLC.CampaignBehaviors.NavalCharacterCreationCampaignBehavior':
        ('adapted', 'adds nord to character creation; filtered with the other cultures (MMR-32); nord is looked up by its installed culture id'),
    'TaleWorlds.CampaignSystem.CampaignBehaviors.BanditSpawnCampaignBehavior':
        ('adapted', 'spawns bandits around hideouts and looters near settlements, and treats deserters by id; the generated world supplies hideouts '
                    'and the bandit clans as aliases'),
    'TaleWorlds.CampaignSystem.CampaignBehaviors.HideoutCampaignBehavior': BANDITS,
    'TaleWorlds.CampaignSystem.CampaignBehaviors.BanditInteractionsCampaignBehavior': BANDITS,
    'TaleWorlds.CampaignSystem.CampaignBehaviors.DesertersCampaignBehavior': BANDITS,
    'TaleWorlds.CampaignSystem.GameComponents.DefaultAlleyModel': BANDITS,
    'TaleWorlds.CampaignSystem.GameComponents.DefaultBanditDensityModel':
        ('adapted', BANDITS[1] + "; its counts are Calradia-scale constants (looters capped at 270 parties, 7 initial and 9 maximum infested "
                                 "hideouts per bandit faction), kept unchanged: a generated world with fewer hideouts fills fewer (FillANewHideout "
                                 "returns on none); bandit pressure in play is MMR-51's"),
    'NavalDLC.GameComponents.NavalDLCBanditDensityModel': BANDITS,
    'TaleWorlds.CampaignSystem.GameComponents.DefaultEncounterModel': BANDITS,
    'TaleWorlds.CampaignSystem.GameComponents.DefaultPartySpeedCalculatingModel': BANDITS,
    'TaleWorlds.CampaignSystem.GameComponents.DefaultVoiceOverModel': BANDITS,
    'TaleWorlds.CampaignSystem.Issues.ExtortionByDesertersIssueBehavior': BANDITS,
    'TaleWorlds.CampaignSystem.Issues.CaravanAmbushIssueBehavior': BANDITS,
    'SandBox.Issues.NotableWantsDaughterFoundIssueBehavior':
        ('adapted', 'finds the bandit clans by id (aliases supplied); its empire_s/empire_w literals are kingdom-id comparisons that are never true '
                    'in a generated world, so those branches never run'),
    'TaleWorlds.CampaignSystem.CampaignBehaviors.IncidentsCampaignBehaviour':
        ('adapted', "finds desert_bandits and steppe_bandits by id (aliases supplied); the trigger conditions of Vedmak's Treasure, The Deer Departed, "
                    'Kannic Splendors and Soldier is Wanted Criminal compare MapFaction.StringId with the kingdom ids sturgia, khuzait, aserai, empire and '
                    'vlandia, never true for a generated kingdom (world-prefixed ids): the world-lifetime transpiler mnbl.proceduralworld.world '
                    "(IncidentCulturePatch, MMR-287) makes each compare the faction's culture id, locating the conditions by IL content (incident id, "
                    'first delegate, kingdom literal), and leaves an unresolved one off; its empire_s culture literal is never true (no such culture, '
                    'as in vanilla); its item lookups are installed templates'),
    'TaleWorlds.CampaignSystem.CampaignBehaviors.LordConversationsCampaignBehavior':
        ('adapted', 'conversation_wanderer_introduction_on_condition sets IMPERIALCAPITAL from Settlement.FindFirst(s => s.StringId == "town_ES4").Name '
                    'without a null check (a NullReferenceException at every wanderer introduction on a generated world); the world-lifetime transpiler '
                    'mnbl.proceduralworld.world routes that one call through GeneratedPatches.FindFirstOrGenerated, which falls back to the '
                    'generated town of the empire culture with the highest prosperity (else the highest-prosperity town)'),
    'TaleWorlds.CampaignSystem.GameComponents.DefaultMapDistanceModel':
        ('adapted', 'its settlement-to-settlement distances come from the navigation cache GeneratedMapSupport.RegisterNavigationCaches builds for '
                    'the generated map, and its party distance legs are transpiled to periodic distances (mnbl.proceduralworld.periodic, MMR-63)'),
    'NavalDLC.GameComponents.NavalDLCMapDistanceModel':
        ('adapted', 'as DefaultMapDistanceModel: navigation caches built for the generated map (GeneratedMapSupport), party legs transpiled to periodic '
                    'distances (mnbl.proceduralworld.periodic, MMR-63)'),
    'NavalDLC.CampaignBehaviors.NavalForceStartNavalMissionCampaignBehavior':
        ('retained', 'a developer cheat (console command only); its troop lookups are installed templates', 'cheat'),
    'NavalDLC.CampaignBehaviors.PiratesCampaignBehavior':
        ('adapted', 'finds the pirate clans structurally (a bandit clan whose culture cannot have settlements and whose default template has ship '
                    'hulls); the world re-creates the installed pirate clans as world aliases homed at a generated hideout (MMR-35 NavalContentFactory). '
                    'It spawns pirates only in the patrol zones INavalMapSceneWrapper.GetSpawnPoints reports from PirateSpawnPoint map entities: '
                    'NavalContentFactory emits their intents, MMR-40 places the entities; until then the generated scene has none and no pirate spawns; '
                    'reads the active map\'s extents'),
    'NavalDLC.GameComponents.NavalDLCClanShipOwnershipModel':
        ('adapted', 'its ideal fleet per clan decides when ShipTradeCampaignBehavior sells a clan\'s ships (buying is decided by the ship '
                    'distribution model); the generated ClanShipOwnershipModel (MMR-35) wraps it as its BaseModel, returns 0 for a clan without a '
                    'port town on an open basin in its home\'s land group (so any ship it holds is sold), and delegates every other clan unchanged'),
    'NavalDLC.GameComponents.NavalDLCShipDistributionModel':
        ('adapted', 'CanPartyTakeShip and CanSendShipToParty decide which parties ShipTradeCampaignBehavior buys ships for and fleet transfers '
                    'reach, with no reachability check; the generated ShipDistributionModel (MMR-35) wraps it as its BaseModel, refuses the parties '
                    'of a clan without a port town on an open basin in its home\'s land group, and delegates everything else unchanged'),
    'NavalDLC.GameComponents.NavalDLCVoiceOverModel':
        ('retained', 'its nord and southern_pirates literals are accent checks against a culture or clan id; the pirate clans keep their installed ids (MMR-35 world aliases), so the check works as installed', 'comparison'),
}
STORYLINE = ('inactive', 'the War Sails storyline: NavalStorylineData.StartNavalStoryline is called only from NavalInitializationCampaignBehavior\'s '
                         'popup, which only StoryMode\'s stealth-tutorial event shows, and the first act marks itself done when StoryModeManager is '
                         'absent; its quests and set pieces are bound to Calradia (village_N1_2, town_N1, fixed coordinates). In a Sandbox '
                         "campaign it stays inactive; on the installed versions none of these types is even in a generated Sandbox campaign's roster (startup "
                         "audit: no NavalStorylineCampaignBehavior)")
STORYMODE = ('excluded', 'StoryMode\'s campaign: a generated campaign is a Sandbox campaign (GeneratedGameManager, CampaignGameMode.Campaign), and '
                         'StoryMode adds these only to its own game type; its tutorial settlements and family characters do not exist in a '
                         'generated world')


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def classify(t):
    if t['type'] in CURATED:
        entry = CURATED[t['type']]
        return entry if len(entry) == 3 else (entry[0], entry[1], None)
    if t['assembly'] == 'StoryMode':
        return STORYMODE + (None,)
    if t['assembly'] == 'NavalDLC' and '.Storyline.' in t['type']:
        return STORYLINE + (None,)
    if t['worldIds']:
        raise SystemExit('unclassified world literal in %s: %s' % (t['type'], list(t['worldIds'])))
    world_lookups = [l for l in t['lookups'] if '(computed)' not in l and any(k in l for k in ('Settlement.', 'Hero.', 'Clan.', 'Kingdom.'))]
    if world_lookups:
        raise SystemExit('unclassified world lookup in %s: %s' % (t['type'], world_lookups))
    # A predicate lookup whose lambda compares a world id (scan: lambda:"id"), or a computed world lookup in a type that names a world id.
    lambda_lookups = [l for l in t['lookups'] if 'lambda:' in l]
    computed_world = [l for l in t['lookups'] if '(computed)' in l and any(l.startswith(k) for k in ('Settlement.', 'Hero.', 'Clan.', 'Kingdom.')) and t['worldIds']]
    if lambda_lookups or computed_world:
        raise SystemExit('unclassified predicate world lookup in %s: %s' % (t['type'], lambda_lookups + computed_world))
    if any(k.startswith('clan|') for k in t['sharedIds']):
        raise SystemExit('unclassified bandit clan literal in %s: %s' % (t['type'], list(t['sharedIds'])))
    if t['coordinates']:
        raise SystemExit('unclassified coordinates in %s: %s' % (t['type'], t['coordinates']))
    reasons = []
    if t['extents']:
        reasons.append('reads the active map\'s extents or scene through Campaign (the generated map supplies them; MMR-37/MMR-63 own their values)')
    if any('(computed)' not in l for l in t['lookups']):
        reasons.append('looks up installed templates by literal id (characters, items, equipment, item modifiers, party templates), which every '
                       'generated world registers (checked against the installed catalog by the spawn-policy tests)')
    if any(k.startswith('kingdom|') for k in t['sharedIds']):
        reasons.append('compares an installed culture or kingdom id (the scan cannot tell which): a culture comparison works, because generated worlds '
                       'present through the installed cultures (MMR-31); a kingdom-id comparison is never true, because generated kingdoms have '
                       'world-prefixed ids, so that branch never runs')
    if not reasons:
        reasons.append('no world literal, id lookup, map extent or coordinate in its IL')
    return ('retained', '; '.join(reasons), None)


def main():
    scan = json.load(open(os.path.join(REPO, SCAN), encoding='utf-8-sig'))
    types = collections.OrderedDict()
    for t in sorted(scan['types'], key=lambda x: x['type']):
        status, reason, basis = classify(t)
        types[t['type']] = collections.OrderedDict([('assembly', t['assembly']), ('kind', t['kind']), ('status', status), ('reason', reason), ('basis', basis),
                                                    ('findings', collections.OrderedDict([('worldIds', sorted(t['worldIds'])), ('sharedIds', sorted(t['sharedIds'])),
                                                                                          ('literalLookups', sorted(l for l in t['lookups'] if '(computed)' not in l)),
                                                                                          ('extents', len(t['extents'])), ('coordinates', t['coordinates'])]))])
    missing = sorted(set(CURATED) - set(types))
    if missing:
        raise SystemExit('curated types not in the scan: %s' % missing)
    counts = collections.Counter(v['status'] for v in types.values())
    out = collections.OrderedDict([
        ('schema', 'mnbl.campaign-compatibility'), ('version', 1),
        ('rule', __doc__.split('Input:')[1].split('Usage:')[0].strip()),
        ('scan', collections.OrderedDict([('path', SCAN), ('sha256', sha(os.path.join(REPO, SCAN)))])),
        ('counts', collections.OrderedDict(sorted(counts.items()))),
        ('types', types),
    ])
    path = os.path.join(REPO, 'worldgen/policies/campaign-compatibility.json')
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(out, f, indent=1)
        f.write('\n')
    print('wrote', path, dict(counts))


if __name__ == '__main__':
    main()
