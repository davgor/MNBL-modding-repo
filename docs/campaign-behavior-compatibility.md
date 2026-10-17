# Campaign behaviours, startup and spawns on a generated world (MMR-34, 2026-10-15)

A generated campaign is a Sandbox campaign: `GeneratedGameManager` builds a `Campaign` in `CampaignGameMode.Campaign`, so StoryMode's flow is never entered. Every installed campaign behaviour and game model then runs on the generated world. This ticket covers three things:
- which installed behaviours and models assume Calradia, and how each is kept, adapted, left inactive or excluded;
- what the world must supply before they start: starts, hideouts, bandit clans, the retirement retreat, and its notables and companions;
- where parties may spawn.

| | |
|---|---|
| Scan | `tools/Scan-CampaignBehaviors.ps1` (compiles `tools/BehaviorScan/BehaviorScan.cs`) reads the installed IL of TaleWorlds.CampaignSystem, SandBox, StoryMode and NavalDLC, plus the game models of TaleWorlds.Core and TaleWorlds.MountAndBlade, and writes `evidence/fwg/behavior-scan.json` |
| Inventory | `worldgen/policies/campaign-compatibility.json`, written by `tools/make_compatibility_inventory.py` from the scan plus a reviewed classification of every type with a world finding |
| Startup | `src/MNBL.ProceduralWorld/Campaign/GeneratedCampaignInitializer.cs` (pure) with `worldgen/policies/startup.json`, written by `tools/make_startup_policy.py` from the installed campaign |
| Spawns | `Campaign/GeneratedSpawnModel.cs` (pure: travel cells, land groups, spawn anchors); `Campaign/GeneratedSpawnAssertions.cs` (in game: live parties against the engine navmesh) |
| Runtime | `Campaign/GeneratedStartupBehavior.cs` (the runtime plan); `Campaign/GeneratedPatches.cs` (every patch of installed code, version guard); `Campaign/GeneratedGameManager.cs` (behaviour removal, world patches, teardown record) |
| Tests | `tests/Campaign/SpawnPolicyTests.cs` via `tools/Test-SpawnPolicy.ps1`, on the real size-129 world: 18 checks (`evidence/fwg/startup-tests.txt`); the startup aliases in `tests/Campaign/RegistrationTests.cs` (`evidence/fwg/registry-tests.txt`) |
| In game | the `generated_campaign` probe with `startupAudit=<repo>` (`Automation/StartupAuditProbe.cs`), summarized by `tools/summarize_startup_audit.py` (`evidence/runtime/startup-audit.json`) |

## The audit

**Scan.** The scanner decodes the IL of every type deriving from `CampaignBehaviorBase` or `GameModel`, together with the lambdas and state machines nested in it. It records:
- string literals equal to an installed settlement, hero, clan or kingdom id (the catalog's ids). Literals that are also culture ids are recorded apart: the bandit clans and most kingdoms share their culture's id;
- calls that find a world object by id (`Settlement.Find`, `Hero.Find`, `Clan.FindFirst`, `MBObjectManager.GetObject<T>(string)`), with the literal argument. For a predicate lookup (`Settlement.FindFirst(s => s.StringId == "town_ES4")`) the literal sits in a compiler-generated lambda; the scanner follows the lambda the call consumes (the nearest `ldftn` before it) and records the world ids it compares as `lambda:"id"`;
- map-extent and map-scene calls (`Campaign.MapMinimumPosition` and similar, `MapSceneWrapper`);
- `Vec2`/`CampaignVec2` constructions from float constants (Calradia coordinates);
- the item ids `DefaultItems` creates in code (grain, iron, tools and others), which no XML declares.

It found 515 behaviour and model types (306 TaleWorlds.CampaignSystem, 109 NavalDLC, 43 SandBox, 33 StoryMode, 21 TaleWorlds.MountAndBlade, 3 TaleWorlds.Core). 83 have a finding other than map extents (73 without counting lookups whose argument is computed); 108 counting extents. 20 name a world id, and 4 compare one inside a predicate lookup's lambda.

**Classification** (the inventory):

| Status | Count | Meaning |
|---|---|---|
| retained | 443 | Works unchanged on a generated world. It has one of these: no finding; only map extents or scene queries, which the generated map supplies (their values are MMR-37's and MMR-63's); only literal lookups of installed templates (characters, items, equipment, item modifiers, party templates), which every world registers; or only literal comparisons that are never true in a generated world |
| adapted | 26 | Its world dependency is supplied through a seam or a manifest-listed patch (below; MMR-35 adds the pirate behaviour and the ship-ownership and ship-distribution models) |
| inactive | 12 | Registered, but its world-dependent part never runs in a Sandbox campaign (the War Sails storyline) |
| excluded | 34 | Removed or never added, with a product reason (StoryMode's 33 types, and the vanilla backstory) |

A retained type with a world finding names why the finding is harmless (`basis`):
- `NavalDLCVoiceOverModel`: accent checks;
- `NavalForceStartNavalMissionCampaignBehavior`: a developer cheat;

A predicate lookup that compares a world id is never a harmless comparison: whatever it finds is used. The tests fail on any unclassified world literal, world lookup (predicate lookups included), bandit clan literal or coordinate, and on any retained type with a predicate world lookup. The generator refuses to write the inventory if one appears.

**Comparisons with installed ids.** Some types compare a culture or kingdom with an installed id (`MapFaction.StringId == "empire"`). The scan cannot tell a culture comparison from a kingdom comparison, because the kingdoms share their culture's id:
- a culture comparison works, because generated worlds present through the installed cultures (MMR-31);
- a kingdom comparison is never true, because generated kingdoms have world-prefixed ids, so that branch never runs. `IncidentsCampaignBehaviour`'s faction checks against sturgia, khuzait, aserai, empire and vlandia are of this kind: those incidents never fire on a generated world.

**Scale constants.** `DefaultBanditDensityModel` keeps its Calradia-scale constants (looters capped at 270 parties; 7 initial and 9 maximum infested hideouts per bandit faction). A world with fewer hideouts fills fewer (`FillANewHideout` returns when none is free). Bandit pressure in play is MMR-51's.

**Adapted, and how:**

| Dependency (installed IL) | Types | Seam |
|---|---|---|
| The land bandit clans looked up by id: `looters`, `deserters`, `sea_raiders`, `mountain_bandits`, `forest_bandits`, `desert_bandits`, `steppe_bandits` | `BanditSpawnCampaignBehavior`, `HideoutCampaignBehavior`, `BanditInteractionsCampaignBehavior`, `DesertersCampaignBehavior`, `DefaultAlleyModel`, `DefaultBanditDensityModel`, `NavalDLCBanditDensityModel`, `DefaultEncounterModel`, `DefaultPartySpeedCalculatingModel`, `DefaultVoiceOverModel`, `ExtortionByDesertersIssueBehavior`, `CaravanAmbushIssueBehavior`, `NotableWantsDaughterFoundIssueBehavior`, `IncidentsCampaignBehaviour` | The world re-creates the installed clans with their installed ids as world aliases (`CampaignIdentity.Aliases`), each homed at a generated hideout of its culture |
| Bandits spawn only around hideouts | `BanditSpawnCampaignBehavior` | Generated hideouts (below) |
| `Settlement.Find("retirement_retreat")`, dereferenced every hour by `CheckRetirementSettlementVisibility` | `RetirementCampaignBehavior` | The installed retreat record (`CustomSettlementComponent` → `RetirementSettlementComponent`) re-created as a world alias at a generated land point. The behaviour is kept; before MMR-34 it was removed |
| Game-start notables: one `CreateNotable` per target, from random templates | `NotablesCampaignBehavior` | The MMR-32 notable intents (`GeneratedStartupBehavior`); daily replenishment unchanged |
| Game-start companions: 0.6 × towns, random templates and towns | `CompanionsCampaignBehavior` | The MMR-32 companion intents; daily spawning unchanged |
| New-game workshop initialization regenerates each owner's whole name (`InitializeWorkshopAction.ApplyByNewGame`, `NameGenerator`) | `WorkshopsCampaignBehavior` | The startup restores the intent names once the new game's follow-ups end (run 37940 showed the rename). Later workshop changes retitle notables as the installed game does |
| Creation cultures and the start position | `CharacterCreationCampaignBehavior`, `NavalCharacterCreationCampaignBehavior` | The MMR-32 culture filter. `CultureObject.StartingPoint` of every creation culture, offered or not, is re-emitted at a generated start gate (`CharacterCreationManager.ApplyFinalEffects` places the main party there when it is navigable), so no installed Calradia start point survives |
| `conversation_wanderer_introduction_on_condition` sets IMPERIALCAPITAL from `Settlement.FindFirst(s => s.StringId == "town_ES4").Name` with no null check, so every wanderer introduction would throw | `LordConversationsCampaignBehavior` | The world transpiler `mnbl.proceduralworld.world` routes that one call through `GeneratedPatches.FindFirstOrGenerated`: the installed result, else the generated empire-culture town with the highest prosperity, else the highest-prosperity town |
| Party distance legs and settlement distances | `DefaultMapDistanceModel`, `NavalDLCMapDistanceModel` | The navigation caches `GeneratedMapSupport` builds for the generated map; the party legs are transpiled to periodic distances (`mnbl.proceduralworld.periodic`, MMR-63); every member's periodic treatment is in `PeriodicDistanceModel.Manifest` (MMR-64) |

**Excluded and inactive:**
- **`BackstoryCampaignBehavior`.** Its new-game handler writes the vanilla lords' backstory log entries and dereferences `lord_1_1`, `dead_lord_*` and `town_V6`. A generated world has no vanilla lords, and generated history is not part of the product. `GeneratedGameManager` removes it before event registration.
- **StoryMode.** Its behaviours and models are added only to StoryMode's own game type. They reference its tutorial village (`village_ES3_2`), the training field and the player's family characters.
- **The War Sails storyline** (12 NavalDLC types) is inactive in a Sandbox campaign:
  - `NavalStorylineData.StartNavalStoryline` is called only from `NavalInitializationCampaignBehavior`'s popup, and only StoryMode's stealth-tutorial event shows that popup;
  - the first act marks itself done when `StoryModeManager` is absent;
  - its quests and set pieces are bound to Calradia (`village_N1_2`, `town_N1`, fixed coordinates).
  The startup audit checks `IsNavalStorylineActive` stays false.

**Live roster.** The installed rule "the last assignable model wins" is not assumed. The startup audit reads, after registration, the actual roster:
- every behaviour (`Campaign.GetCampaignBehaviors`);
- every registered model, from any assembly (`GameModelsManager.GetGameModels`; this includes superseded ones);
- the effective model of every `GameModels` contract.

Each must be in the inventory; no excluded one may be enabled; Retirement must be kept exactly when the world supplies the retreat. This module adds two models, on worlds with naval content: MMR-35's `GeneratedShipDistributionModel` and `GeneratedClanShipOwnershipModel`, which wrap War Sails' own (docs/naval-content.md).

**Listeners.** Each of this module's behaviours subscribes once per event (counted over the campaign's non-serialized listener lists). `CampaignEvents` belongs to its campaign (created in the `Campaign` constructor), so a later campaign's lists can never hold an earlier campaign's listener unless it is re-added. The teardown is therefore checked where it happens:
- when a generated campaign ends (`GeneratedGameManager.OnGameEnd`, which runs before `Campaign.OnDestroy`), this module's behaviours are removed from the ending campaign's events, and the startup, world and creation patches are removed. What is left is recorded (`LastTeardown`, with weak references only);
- the next world's audit fails if anything was left, if the new campaign reuses the old events, if any old behaviour listens to the new events, if the periodic world is not the new world's, or if a fixture static survived.

A new game that ends without `OnGameEnd` cannot leave a stale startup behind: the startup prefixes run only for the campaign that activated them, and otherwise remove themselves and let the installed code run.

## Patches

Every patch of installed code is in `GeneratedPatches.Manifest`, with its owner, purpose and lifetime. It is used only where no supported extension point exists. Every target was read from Bannerlord v1.4.8.119303 and War Sails v1.2.8: `GeneratedPatches.RequireInstalledVersion` runs before any patch is applied (startup, world, periodic, creation) and refuses another version with a message naming both.

| Patch (Harmony id) | Target | Why | Lifetime |
|---|---|---|---|
| `mnbl.proceduralworld.creation` (MMR-32) | `CharacterCreationCultureStageVM.SortCultureList` (prefix) | The installed sort places the six vanilla cultures with `Enumerable.Single` and throws when fewer are present | Character creation with a restricted list |
| `mnbl.proceduralworld.startup` (MMR-34) | `NotablesCampaignBehavior.SpawnNotablesAtGameStart` (prefix) | It creates notables from random templates and ignores the world's intents; no model or event lets a world supply its own | From a new game with a startup plan until its follow-ups end, or the game ends |
| `mnbl.proceduralworld.startup` (MMR-34) | `CompanionsCampaignBehavior.OnNewGameCreated` (prefix) | The same, for companions; the prefix initializes the template list as the installed handler does | As above |
| `mnbl.proceduralworld.world` (MMR-34) | `LordConversationsCampaignBehavior.conversation_wanderer_introduction_on_condition` (transpiler) | Its `town_ES4` lookup is dereferenced unchecked; the one `Settlement.FindFirst` call becomes `FindFirstOrGenerated` (the patch refuses unless it replaces exactly one call) | From the start or load of a generated campaign until it ends |
| `mnbl.proceduralworld.world` (MMR-42) | `DefaultSceneModel.GetBattleSceneForMapPatch` (postfix) | Observe only: records each battle scene the model picks on a generated map against the generated patch (`Missions/MissionScenePatches.cs`, `docs/mission-scenes.md`), so a missing battle map, a non-deterministic patch or another scene is logged, never silent; the result is unchanged. Applied with the world transpiler, all or nothing | From the start or load of a generated campaign until it ends |
| `mnbl.proceduralworld.periodic` (MMR-63, MMR-64) | `MobileParty`/`Settlement.FindNextLocatable` (postfixes); `PartyBase.CanPartyInteract`, `PartyBase.CalculateVisibilityRangeOfMapPoint`, `MenuHelper.EncounterAttackConsequence`, `MapEvent.GetEventDirection`, the Default and NavalDLC `MapDistanceModel.GetDistance` (transpilers); `NavigationHelper.FindReachablePointAroundPosition` (postfix); `MapState.ProcessTravel` (prefix and transpiler); MMR-64: the two ordering lambdas of `SandBoxNavigationCache.GetClosestSettlementsToPositionInCache`, `DistanceHelper.FindClosestDistanceFromMobilePartyToMobileParty`, `DistanceHelper.GetDistanceBetweenMobilePartyToMobileParty` and `SettlementHelper.FindNearestSettlementToPoint` (transpilers) | The planar checks that must see the periodic world (`CampaignBootstrap/PeriodicPatches.cs`; every rule from `World/PeriodicWorldCoordinates.cs`, `docs/periodic-coordinates.md`). Applied all or nothing, after a preflight before the campaign starts | While a periodic generated map scene is loaded |

## What the world supplies (startup)

`GeneratedCampaignInitializer` builds the startup from these inputs:
- the MMR-28 travel graph, cells and ports;
- the MMR-31 factions;
- the MMR-32 heroes;
- the MMR-33 settlements;
- `startup.json`.

Its construction randomness comes from named, versioned streams (`hideouts`, `starts`). The campaign keeps `MBRandom` for mutable gameplay, including where the installed behaviours spawn parties.

**Starts.** Every creation culture gets a start. The creation cultures are the installed main cultures with a start point (aserai, battania, empire, khuzait, nord, sturgia, vlandia): the cultures character creation can offer, whether or not the world presents through them. A start is one of the culture's towns that meets all of these:
- a port town: its own port is the operational port, its shipyard is the ship source, and the player embarks only at a town port (MMR-17);
- a trade-area food supply of at least 25 (the installed towns' 10th percentile);
- notables to recruit from;
- a port in an open MMR-28 sea basin (the initializer checks the basin's `open` flag) shared with another port town, the sea destination.

The starts stream picks among qualifying towns. Fallbacks are reported:
- a culture whose towns do not qualify takes the qualifying town nearest its towns;
- a culture with no town takes the qualifying town the starts stream draws for it.

A world without any qualifying town is refused. Every creation culture's `start_point_position` is re-emitted (SPCultures), so no installed start point survives.

At size 129, all 7 cultures start in their own towns, with food 52-142 and their sea destination in the same open basin. The tests also exercise both fallbacks.

**Refusals.** A world that cannot supply the startup is refused with `StartupException`. It carries quality codes from `startup.json` (`quality.codes`), and it is retryable only when every code is:

| Code | Retryable | Meaning |
|---|---|---|
| `quality.startup_no_start` | yes | No town qualifies as a player start |
| `quality.startup_hideouts` | yes | No hideout could be placed, or fewer hideouts than required bandit cultures |
| `quality.startup_retreat` | yes | No passable cell for the retirement retreat |
| `quality.startup_unplaceable_spawn` | yes | A spawn anchor is not passable land connected to a town |
| `quality.startup_map` | yes | The travel cells disagree with the land groups, or fewer than two towns |
| `quality.startup_catalog` | no | An installed record the startup re-creates is missing |
| `quality.startup_plan` | no | A runtime hero intent has no template or generated home (an MMR-32 defect) |
| `quality.startup_policy` | no | The startup policy is malformed or disagrees with the catalog |

The initializer is verified offline on the full world. MMR-45's activation calls it on the full world (`CompiledWorldDataset`, BuildWorld), and its runtime plan runs in the activated campaign (`GeneratedStartupBehavior`).

**Hideouts.**
- **Count:** the installed density, 1.74 per town (99 for 57 towns), shared over the MMR-28 land groups with towns in proportion to their towns.
- **Placement:** each takes a passable cell at least 0.125 × the town spacing from every settlement (the installed 10th percentile) and 0.256 × spacing from every other hideout (the installed minimum). Candidates come in stream order.
- **Culture:** drawn from the installed weights for its nearest town's culture. For example, the installed aserai towns have 20 desert and 3 steppe hideouts nearest them.

At size 129: 72 hideouts (forest 31, sea raiders 21, mountain 10, desert 6, steppe 4). Four one-town islands are too small for their share (two hold one, two none); this is reported, not refused.

**Coverage.** Every bandit culture that `BanditSpawnCampaignBehavior` spawns for has at least one hideout: desert, forest, mountain, sea raiders and steppe. `requiredCultures` follows the engine's rule (`Clan.IsBanditFaction`: a bandit culture that can have settlements and has no naval capability), applied to the installed land bandit clans.
- `CacheHideouts` keys hideouts by culture, and at new game `GetInfestedHideoutCount` reads that map with each bandit clan's culture without a check.
- A bandit culture with no hideout throws `KeyNotFoundException` and ends the new game. Run 37036 crashed this way: the fixture world then had only its forest hideout.
- A missing culture takes the hideout whose nearest town weighs it most, among the hideouts whose culture has more than one.
- A world with fewer hideouts than cultures is refused (tested with a hideout spacing too large for five).
- Test: with every weight pointing at forest bandits, the planner still yields all five cultures.

**Bandit clans.** The 7 installed land bandit clans are re-emitted with their installed records as world aliases. Each is homed at the first generated hideout of its culture; `looters` and `deserters`, whose culture has no hideouts, at the first hideout. The pirate clans are MMR-35's. The record builders and the home rule (`BanditClanRecord`, `RetreatRecord`, `StartCultureRecord`, `BanditHome`, `NearestTownCulture`) are shared with the in-game fixture world, so the fixture registers exactly the records the initializer emits.

**Retirement retreat.** The installed record is re-emitted as a world alias at the passable cell of the main land group farthest from every settlement and hideout: 31.2 at size 129, a hidden spot like the installed one. Its culture is the nearest town's.

**Runtime plan.** `GeneratedStartupBehavior` instantiates the plan at new game:
- **Notables (909 at size 129):** `HeroCreator.CreateSpecialHero(template, home, null, null, age)` and `SetName` (the intent's name as the full name, its first word as the first name). `NotablesCampaignBehavior.OnHeroCreated` places them, as it does its own.
- **Companions (28):** the installed `CreateCompanionAndAddToSettlement` steps (`CreateSpecialHero`, `AdjustEquipments`, `ChangeState(Active)`, `EnterSettlementAction`) with the intent's template, home and age, then `SetName`.
- **The 780 MMR-31 ruler relations:** `CharacterRelationManager.SetHeroRelation`, once every new-game follow-up has run.

The engine mints a runtime hero's StringId, so the behaviour keeps the map from intent id to hero and saves it with the campaign. The world package records whether the world supplies a startup (`adapter.dataset.suppliesStartup`), so a loaded world keeps Retirement and the startup behaviour, and the saved map comes back. A missing template or home is a recorded problem naming the intent, never a fallback. Engine-dispatched code never throws into the dispatch: errors become problems, which are traced and fail the audit.

## Spawns

**Offline** (`GeneratedSpawnModel`):
- **Passability.** A position is passable when its travel cell (`cells.bin`, after repairs) is land.
- **Land groups.** Land components are 4-neighbour connected and wrap east-west. Each takes the MMR-28 land group of the settlements on it: the compiler's groups join components across its required bridge, which a land-only flood fill splits. The first test run caught this: a village on a component with no town, which the compiler's group 3 (components 3 and 9) joins to its towns.
- **Anchors.** These are the positions installed behaviours spawn parties at:
  - the player at the start gate;
  - caravans at town gates;
  - villagers at village centres;
  - lords at their clan's home settlement gate;
  - bandits at hideouts.

  Each must be passable, in a land group with a town. A failing anchor is refused with the subject, the cell, its class and the fix (the MMR-28 placement, or MMR-38's point snapping). At size 129, all 431 anchors pass: 7 player, 45 caravan, 215 villager, 92 lord and 72 bandit.

**In game** (`GeneratedSpawnAssertions`, registered by diagnostic builds). Every party created, and every party on the map each campaign hour, is checked against the active world's navigation:
- a valid navmesh face whose terrain the party's current navigation type may use (`PartyNavigationModel.IsTerrainTypeValidForNavigationType`);
- inside the map extents;
- on a periodic world, at its canonical x.

A failure is recorded with the party, its kind, position and reason. The party-created listener keeps a plain list and checks at the next hour: a party is still inside `MobileParty.CreateParty` when the event fires, and hashing it there can throw into the engine.

## In game: runs 39816 and 14820 (36/36)

Run 39816: the `generated_campaign` probe with `startupAudit=<repo>`, `settlementFixtures=<repo>` and `saveProbe=1`, two worlds, a 2-day gameplay window per world, then a save of each. Run 14820: the `generated_save_load` probe loads both saves cold. Summary: `evidence/runtime/startup-audit.json` (`tools/summarize_startup_audit.py RUN LOAD`).

**The first world** carries the startup fixtures, built with the initializer's shared record builders:
- one hideout of each bandit culture: the fixture forest hideout plus four on verified land cells;
- the 7 bandit aliases, homed by the initializer's rule;
- the retreat, with the nearest town's culture;
- every creation culture's start at the fixture town's gate;
- a plan of 19 notables at the installed targets for 2 towns and 3 villages, the installed loop's 2 companions, and one ruler relation.

What it showed:
- **Roster:**
  - all 237 enabled behaviours and all 209 registered models are in the inventory, none excluded. The models come from TaleWorlds.CampaignSystem (124), NavalDLC (67), SandBox (11), TaleWorlds.MountAndBlade (4) and TaleWorlds.Core (3). The effective model of each of the 124 contracts is listed too;
  - Backstory is off and Retirement is kept;
  - this module's behaviours each subscribe once per event;
  - the War Sails storyline behaviour is not even in the roster.
- **Patches:** on v1.4.8.119303 / NavalDLC v1.2.8.119303, the world transpiler is the only patch applied once the new game has run; the manifest lists 9 entries (MMR-34's run; MMR-42 adds the observe-only world postfix on `DefaultSceneModel.GetBattleSceneForMapPatch`).
- **Literals:** of the 5 world ids the scan found in enabled types, only this world's aliases resolve (`deserters`, `retirement_retreat`). All 44 literal template lookups resolve.
- **Wanderer introduction:**
  - the patched method calls `FindFirstOrGenerated` and no longer `Settlement.FindFirst`, and `town_ES4` finds nothing;
  - the installed condition, run with the generated companion "Wanderer 1" as the one-to-one conversation hero, set IMPERIALCAPITAL to the generated town "Harbor 2";
  - it then stopped at `CampaignMission.Current.Location`. That read needs the tavern mission a real conversation runs in; on the campaign map there is none.
- **Start:**
  - all 7 creation cultures' `StartingPoint` is the planned gate, none its installed Calradia point (for example empire's [776, 280] became [609.4, 167.5]);
  - the main party stands at empire's start on valid navigation.
- **Embarkation:**
  - the start town's gate is on valid land, and its port on valid water 4.38 from the gate (the MMR-17 envelope is 2 cells, 12.5);
  - the engine's port-to-gate route exists (`GetPortToGateDistanceForSettlement` 54.4; not a straight-line distance, so it is reported, not bounded);
  - a naval route of 699 reaches the other port town, and there is a level-1 shipyard.
- **Startup:**
  - every bandit culture has a hideout, and every bandit alias exists and is homed by the rule;
  - the retreat is a `RetirementSettlementComponent` on valid land navigation;
  - the 19 notables are exactly each settlement's, named with the intent's full name and first name, and placed;
  - the 2 companions were created, the intent map holds 21 heroes, and the relation is -23;
  - the startup patches ended with the new game, and no startup problem was traced.
- **Over 2 campaign days** no party was off valid navigation. Natural checks (what the game spawned itself): bandit 1,862, caravan 174, lord 94, player 48, others 192 (garrisons, militias, patrols); no villager party moved in this world's window.
  - The player is ignored by other parties during the window (`IgnoreByOtherPartiesTill`): the probe plays no encounters, and run 39908 stalled when looters engaged it.
  - Spawned through the installed factories at installed spawn points, all on valid navigation: a caravan, a villager party, a fresh lord party (a new lord of the first ruler's clan, `MobilePartyHelper.SpawnLordParty`) and a bandit party.

**The second world** has no startup fixtures:
- Retirement is removed, and the roster, literals and patches are clean;
- the wanderer condition did not run, because this world had no wanderer; the transpiler check passed;
- natural checks: caravan 177, lord 88, villager 31, player 48, others 196.

**Teardown:**
- when the first campaign ended, its 3 module behaviours' 4 listeners were removed from its events (0 left), and no startup, world or creation patch survived it;
- the second campaign has its own events, none of the first campaign's 240 behaviours listens to them, and the fixture statics are cleared.

**Save and load (run 14820):**
- world 0 loads with its startup: the package restores the flag, Retirement is kept for the retreat, and the startup behaviour is back with its 21 saved intents, all heroes alive;
- world 1 loads without a startup; the world patch is applied on both.
- MMR-19's exact snapshot comparison flagged 8 values, all path state of moving AI parties: the next target of a ruler party, a patrol and a caravan, and the patrol's last path position. These are re-derived when the party's path is rebuilt on load. Nothing about settlements, heroes, clans, the main party or the startup differed. This is left to the save tickets (comment on MMR-62; MMR-62 loads nothing and forwarded it to MMR-49, which owns the load and its position and cache resolution).
- Run 35880 (before the fix) crashed on load in `SettlementVisual.OnStartup`: the fixture settlements' map prefabs were not restored. The package now records them (`adapter.dataset.fixturePrefabs`), and the loader restores them before the map loads.

**Earlier runs:**
- 25476: 33/33 before the prefab fix (its load, 35880, crashed as above).
- 17284: 27/27 before the review fixes.
- 3664 crashed natively right after the startup heroes were created. The cause was not visible: the watchdog could not write a dump, because the game's `crashes` folder is a symlink to a missing Workshop cache. Run 37036 found it.
- 37036: `BanditSpawnCampaignBehavior` reads its hideouts-by-culture map unchecked, and the fixture world had only a forest hideout. This led to the coverage rule.
- 38812: town notable mismatch. 37940 showed the vanilla workshop rename, which led to the name restore.
- 39908: the gameplay probe stalled on a bandit encounter.
- 31864: entering the town without an encounter broke the town menu.

## What this does not prove

- **Live multi-day behaviour:** trade, recruitment, raids, sieges, rebellion, succession and prisoner escape over many days are MMR-51's. The startup audit covers a two-day window.
- **The full world in game:** MMR-45 registers the full world, with its 72 hideouts, the retreat and 7 starts, and runs the startup plan in its campaign. The activation checks the registry's closure and the hideouts' run-time entities, not each notable: MMR-51/52 exercise them in play. The fixture world shares the initializer's record builders and rules, but not its placement (hideouts, retreat and starts are fixture points there).
- **A real wanderer conversation:** the condition was run on the campaign map with a stand-in conversation agent, up to the point that needs the tavern mission.
- **Exact save fidelity of moving parties:** see the path-state differences above (MMR-49, forwarded by MMR-62).
- **Templates of the full world:** every literal template lookup names an installed record or an engine default item, but the full world's template closure must keep them (MMR-45).
- **Pirates:** MMR-35 re-creates the pirate clans and emits their spawn-point intents (docs/naval-content.md); the spawn-point entities are not placed yet (MMR-40 placed the settlement entities; handed to MMR-53).
- **Engine snapping:** in a world, ports are MMR-38's navigation points and gates MMR-40's resolved gates (`docs/settlement-entities.md`); hideouts are MMR-34's. The offline passability check uses the travel cells, not the navmesh.
- **Prisoner escape and quest destinations** choose among the world's settlements at runtime. The scan found no Calradia id in their behaviours; they are exercised in play by MMR-51.
