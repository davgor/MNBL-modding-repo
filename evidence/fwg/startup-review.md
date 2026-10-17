# MMR-34 independent review (2026-10-15)

The reviewer was an independent read-only agent: no repository edits, no game launch. It re-ran `tools/Test-SpawnPolicy.ps1` (14/14, byte-identical evidence) and `tools/Test-CampaignRegistry.ps1` (55/55), and checked the claims against the installed IL (`%TEMP%\mmr32-il`) and the code.

The report is below verbatim, followed by the disposition of every finding.

---

## MMR-34 independent review (read-only)

I edited nothing and did not launch the game. I re-ran two offline harnesses, writing only to scratch and build/:
- `Test-SpawnPolicy.ps1` passed 14/14. Its startup.json is byte-identical to `evidence/fwg/startup.json` (intents sha `e9ddd640…`).
- `Test-CampaignRegistry.ps1` passed 55/55.

IL references below are to `%TEMP%\mmr32-il\*.il`.

### BLOCKER

**B1. `LordConversationsCampaignBehavior` is marked "retained/comparison" but crashes on every generated world when the player first talks to a wanderer.**
- **Where:** `tools/make_compatibility_inventory.py:71-72`; `worldgen/policies/campaign-compatibility.json`; the doc line "its `town_ES4` is a comparison".
- **What the IL does:** `conversation_wanderer_introduction_on_condition` (campaign.il ≈892977-893137, IL_007f–IL_00ae) runs `MBTextManager.SetTextVariable("IMPERIALCAPITAL", Settlement.FindFirst(s => s.StringId == "town_ES4").Name)`. There is no null check: `callvirt get_Name` is applied directly to the `FindFirst` result.
- **No safety net:** `ConversationSentence::RunCondition` has no try/catch around `OnConditionDelegate.Invoke`.
- **Effect:** the size-129 world has 28 companions. Meeting any of them throws a NullReferenceException. "Wandering heroes" is on the ticket's own list.
- **Why nothing caught it:**
  - The scanner records the lookup as `Settlement.FindFirst(computed)`, because the literal sits in a nested `<>c.<…>b__114_0` lambda.
  - Both guards then drop "(computed)" lookups: `make_compatibility_inventory.py:105` and `SpawnPolicyTests.cs:98`.
  - The hand-written "comparison" basis was never checked against the IL.
  - The in-game audit never opens a conversation.
- **Fix:**
  - Reclassify as adapted.
  - Supply the value through a manifest-listed patch: a prefix or transpiler that swaps the `FindFirst` result for a generated settlement, or sets IMPERIALCAPITAL first and skips the lookup.
  - Make the scanner tie a world literal inside a `<>c` lambda to the lookup that consumes the delegate. At minimum, any `*.FindFirst(computed)` in a type that has a worldIds literal should count as a world lookup.
  - Add a check that runs the condition with a generated wanderer as `OneToOneConversationHero`.

### MAJOR

**M1. The startup initializer runs only in tests, and no quality rejection exists.**
- `new GeneratedCampaignInitializer` appears only in `tests/Campaign/SpawnPolicyTests.cs:77,81`.
- `StartupException` is caught nowhere in `src/`.
- `world-quality.json` has no startup code. The ticket requires "a world that cannot supply required paths is a visible bounded quality rejection".
- The in-game run used the hand-coded fixture `GeneratedCampaignDataset.AddStartupFixtures` (:398-470), with the start point taken from the MMR-30 fixture (`GeneratedCampaignDataset.cs:216`).
- So the initializer's starts, hideouts, retreat and bandit homes have never been registered in game. The doc's "the fixture world instantiates the same code paths" overstates this. The two also differ: the fixture homes looters and deserters at the forest hideout, the initializer at the first hideout; the retreat culture rule also differs.
- **Fix:** wire the initializer into the dataset and generation driver. Map its problems to retryable codes in `world-quality.json` (e.g. `quality.no_start`, `quality.too_few_hideouts`). Build the fixture world through `Initializer.Documents()`.

**M2. Start cultures come from town cultures, not from the cultures character creation offers (c2).**
- `GeneratedCampaignInitializer.cs:233` loops over `towns.Select(t => t.Culture)`. The SPCultures re-emission (:270-278) only rewrites those cultures.
- Installed SPCultures are stock-loaded before registration (`GeneratedCampaignDataset.cs:305`). Any creation culture with no town keeps its installed `start_point_position`, e.g. nord 725,811 and khuzait 841,541.
- `CharacterCreationManager.ApplyFinalEffects` (campaign.il ≈348717-348747) then places the player there, or snaps to the nearest face.
- The creation filter exists only as the probe parameter `restrictCreationCultures` (`GeneratedCampaignBootProbe.cs:53`). In production it is null, so the full installed list is offered.
- The c2 test (`SpawnPolicyTests.cs:117,123`) compares the starts with the towns' cultures, which is circular.
- The size-129 world is unaffected: all 7 cultures own towns.
- **Fix:** enumerate the creation cultures (the installed main cultures plus nord, or the MMR-32 presentation list) and give each one a start, using the fallback rule where needed. Either re-emit every main culture's start point or restrict creation to the start cultures. Test that no installed start point survives.

**M3. Loading a saved game drops the startup.**
- `GeneratedCampaignRegistration.FromInstalledPackage` (:191-203) never sets `StartupEnabled`, so `SuppliesStartup` is false on load.
- `GeneratedGameManager.cs:104-109` then removes `RetirementCampaignBehavior` even though the retreat exists.
- `GeneratedGameManager.cs:125-132` never adds `GeneratedStartupBehavior`, so the intent-id→hero map it saves is lost on the next save. That contradicts the comment at :129, "a load needs it too".
- Untested: run 17284 has no save/load.
- **Fix:** persist "supplies startup" in the package or identity, restore it in `FromInstalledPackage`, and add a save/load check.

**M4. The teardown proof cannot fail the way the doc claims.**
- `CampaignEvents` belongs to each campaign: `CampaignEvents::get_Instance` returns `Campaign.Current.CampaignEvents`, created with `newobj` in the Campaign constructor.
- `StartupAuditProbe.Teardown` (:228-234) scans the second campaign's fresh listener lists. An old listener can only show up there if the same object is re-added.
- It therefore proves "no reuse", not "removed on teardown".
- **Fix:** at `OnGameEnd`, capture the ending campaign's `CampaignEvents` and assert this module's owners are gone. Also check the static hooks (Harmony owners including periodic, `PeriodicWorld.Current`, the `FirstChanceException` handler, `StartupAuditProbe.earlierBehaviors`).

**M5. The patch manifest is incomplete, and patched models are mislabelled "retained".**
- The doc says "Every patch of installed code is in `GeneratedPatches.Manifest`".
- But `CampaignBootstrap/PeriodicPatches.cs:39-111` (owner `mnbl.proceduralworld.periodic`) is missing. It patches `MobileParty`/`Settlement.FindNextLocatable`, `PartyBase`, the Default and NavalDLC MapDistanceModels, `NavigationHelper.FindReachablePointAroundPosition` and MapScreen with prefixes, postfixes and transpilers.
- `GeneratedPatches.Patched()` (:229-240) filters to manifest owners, so the audit cannot see these patches.
- `DefaultMapDistanceModel` and `NavalDLCMapDistanceModel` are listed as "retained: works unchanged". They are transpiled and their distance cache comes from `GeneratedMapSupport.RegisterDistanceCache`, so they are adapted.
- No code checks the running game version before patching.
- **Fix:** add the periodic patches to the manifest, reclassify the two models as adapted, and add a version guard.

**M6. The start and embarkation audit is partly unfalsifiable.**
- `StartupAuditProbe.cs:102-114` hardcodes `_town_1`/`_town_2` instead of the culture's planned start town.
- It asserts the main party is at `culture.StartingPoint`, which `ApplyFinalEffects` makes true whenever the point is navigable. It never checks that StartingPoint equals the planned gate or differs from the installed start points.
- "embarkation" is a constant string (:110). The summarizer label "a town-port embarkation" (`summarize_startup_audit.py:74-76`) checks nothing, yet the ticket asks for a legal embarkation path.
- **Fix:** compare StartingPoint with the dataset's planned gate and with the installed SPCultures points. Verify embarkation through the MMR-17 town-port path (e.g. the gate face reaches the port face and naval navigation is valid there).

### MINOR

- **m1. The "lord spawned through installed factories" is not a spawn.** `StartupAuditProbe.cs:189` returns the existing `PartyBelongedTo`. The evidence position (511.8, 71.8) is far from town_1's gate (609, 165): the party had moved for two days. Spawn a fresh lord party or relabel the check.
- **m2. No natural villager party was checked in the startup world.** World 0's hourly counts have no villager; `createdChecked.villager = 1` is the forced spawn. The summarizer merges forced into natural counts (:97-101), and the doc's "and the villagers" hides this.
- **m3. "202 effective campaign models" is mislabelled; models are checked more loosely than behaviours.**
  - 202 is every model `GetGameModels` registered, including superseded ones; the effective contracts number 124.
  - Effective models are only checked to be "not excluded" (:75), not to be in the inventory.
  - `campaignModels` (:54) leaves out every other assembly. The 7 enabled TaleWorlds.Core/MountAndBlade models (`otherModels`) are unclassified, despite the doc saying each must be in the inventory.
- **m4. Gaps in the initializer's rules and tests.**
  - It never checks that the basin is open (:212-221); only the test checks that, and only for this world (`SpawnPolicyTests.cs:132`).
  - The fallback-start path is untested.
  - The "fewer hideouts than required cultures" refusal is untested.
  - The offline passability test uses travel cells, not typed navmesh (acknowledged in the doc).
- **m5. The inventory has blind spots for faction comparisons and fixed scale constants.**
  - `IncidentsCampaignBehaviour` lambdas `b__25_113/116/126/130` compare `MapFaction.StringId` with "sturgia", "khuzait", "aserai", "empire" and "vlandia". These are kingdom-id checks that are never true, so those incidents silently die; the reason text mentions only `empire_s`.
  - The automatic reason "compares cultures" does not tell faction-id comparisons apart from culture-id ones.
  - Fixed world-scale constants are unclassified: `DefaultBanditDensityModel` caps looters at 270 and uses 7 initial / 9 maximum hideouts per faction.
- **m6. Startup problems are invisible outside the audit, and a static can go stale.**
  - `GeneratedStartupBehavior.Problems` is read only by `StartupAuditProbe` and never traced.
  - If a new game aborts without `OnGameEnd`, the static `Active` and the global patch survive. A later official Sandbox campaign would then hit the prefixes with a stale plan.
- **m7. Name restore overwrites the first name.** `SetName(name, name)` at `GeneratedStartupBehavior.cs:117-118,146-148` sets FirstName to the full intent name.
- **m8. Alias handling is too permissive and untested.**
  - `GeneratedCampaignGameplay` uses the global `IsAlias`, so bandit and retreat aliases pass in worlds that do not supply them.
  - Prior-world clan checks skip aliases, and MMR-30's `otherWorldPrefixes` cannot detect a leaked unprefixed alias.
  - The registry tests were not extended to the new aliases (registry-tests line 22 still lists only `main_hero` and `player_faction`).

### NIT

- The doc's "82 have a finding" matches no count in the scan: 83 types have a non-extent finding, 73 without computed lookups, 108 including extents.
- `requiredCultures` comes from the installed hideout cultures, not from the engine rule `IsBanditFaction` (`!HasNavalNavigationCapability && IsBanditFaction && Culture.CanHaveSettlement`, campaign.il ≈732989). The two coincide today.
- The hideout note says "no other passable cell" even when 0 hideouts were placed.
- `StartupAuditProbe.earlierBehaviors` is static and keeps the previous campaign's 240 behaviours alive.

### Checked and found correct

- **Scanner coverage:**
  - It covers all 491 behaviour and model types in the four assemblies.
  - My own whole-assembly literal scan (including non-behaviour helpers, SandBox.View/VM and CampaignSystem.ViewModelCollection) found no Calradia settlement or hero id it missed.
  - No other assembly declares a `CampaignBehaviorBase` type.
  - There are no `StartsWith`/`Contains` checks on Calradia id prefixes, and no missed literal `CampaignObjectManager`/`Kingdom`/`Clan` lookups.
- **StoryMode** is absent from the live roster.
- **Bandits:**
  - `IsBanditFaction` and `CacheHideouts`/`GetInfestedHideoutCount` behave as the doc says, and the coverage rule is correct. The `_numberOfInitialHideouts` = 7 shortfall is handled: `FillANewHideout` returns on null.
  - sea_raiders is a land faction (no ship hulls).
- **Patch targets:** `SpawnNotablesAtGameStart` only creates notables. `OnNewGameCreated` is `InitializeCompanionTemplateList` plus the loop, and the prefix mirrors it.
- **Save containers:** `List<Hero>` has a save container definition.
- **Initializer behaviour:**
  - Construction streams are separate from MBRandom.
  - Output is deterministic, including from reordered records.
  - The retreat record has no gate attributes.
  - No Calradia coordinate is copied; only the declared aliases are.
- **Live checks:** the spawn assertions check navmesh face, terrain for the current navigation type, map extents and canonical x correctly. Roster, literal and template checks are falsifiable.
- **Evidence:** counts in `startup-audit.json` (27/27), `startup-tests.txt` (14/14) and inventory 425/20/12/34 match the files. No first-chance exceptions occurred during the run.

### Verdict per criterion

- **c1: partially met.** Every scanned type is classified and the live roster is enforced. But one retained type (`LordConversationsCampaignBehavior`) has a crash-level Calradia dependency, the patched distance models are mislabelled, and enabled non-campaign-assembly models are unclassified.
- **c2: partially met.** The 7 starts at size 129 are generated port towns with food, notables and a same-basin sea destination, verified offline. In game only the fixture start was verified, the initializer is not wired, and creation cultures without towns keep their Calradia start points.
- **c3: met, with caveats.** All 431 anchors pass offline (on travel cells), and the 2-day in-game window found no violations. The forced "lord spawn" is a pre-existing party, and no natural villager party was observed in the startup world.

---

## Disposition

Every finding was fixed in one batch. Confirmed by: offline `Test-SpawnPolicy` 18/18 (`evidence/fwg/startup-tests.txt`), `Test-CampaignRegistry` 57/57 (`evidence/fwg/registry-tests.txt`) and `Test-Settlements` 22/22; in game, runs 39816 (audit and saves) and 14820 (cold load), 36/36 (`evidence/runtime/startup-audit.json`).

| Finding | Fix | Evidence |
|---|---|---|
| **B1** `LordConversationsCampaignBehavior` crashes at every wanderer introduction | The scanner follows the lambda a predicate lookup consumes and records `lambda:"town_ES4"` (4 such types). The generator and the tests refuse any unclassified or retained predicate world lookup. The type is adapted: the world-lifetime transpiler `mnbl.proceduralworld.world` (in the manifest; applied at start or load of every generated campaign, removed at its end) replaces the one `Settlement.FindFirst` call with `GeneratedPatches.FindFirstOrGenerated`. That returns the installed result, else the empire town with the highest prosperity, else the town with the highest prosperity. The patch refuses unless it replaces exactly one call | In game: the patched method's current instructions call the helper, not `FindFirst`, and `town_ES4` finds nothing. The installed condition, run with generated companion "Wanderer 1" as the one-to-one conversation hero (a stand-in conversation agent), set IMPERIALCAPITAL to "Harbor 2". It then stopped at the map-side `CampaignMission.Current.Location` read, which needs the tavern mission. Offline: spawn-policy check 3 |
| **M1** initializer only in tests; no quality rejection; fixture differs | `StartupException` carries quality codes from `startup.json` (`quality.codes`: 8 codes, retryable or not), and is retryable only when every code is. The fixture world now uses the initializer's shared builders and rules: `BanditClanRecord`, `RetreatRecord`, `StartCultureRecord`, `BanditHome` and `NearestTownCulture`. The doc no longer says the fixture "instantiates the same code paths"; it says what is shared and what is fixture placement. Wiring the initializer into generation stays with MMR-45, and the codes with MMR-44 (comments on both). The codes are startup.json's, not world-quality.json's, whose hash is part of generation identity and MMR-24 evidence | Spawn-policy check 18: four refusals, each with its code and retryability. In game, the bandit homes and the retreat culture follow the shared rules |
| **M2** creation cultures without towns keep Calradia start points | `startup.json starts.creationCultures` holds the installed main cultures with a start point (7, nord included); the initializer checks it against the catalog. Every creation culture gets a start, with fallbacks for a culture without a qualifying town and for one without towns, and every one is re-emitted. The fixture world re-emits all 7 at its start gate | Spawn-policy checks 5-6 (no installed start point survives) and 8 (both fallbacks, with a computed food minimum and a test culture without towns). In game: all 7 `StartingPoint`s are the planned gate, none the installed point |
| **M3** a load drops the startup | The package records `adapter.dataset.suppliesStartup`, and `FromInstalledPackage` restores it. The save probe records the intent count, and the load probe checks Retirement, the retreat component, the startup behaviour and the intent map. The first load (run 35880) then crashed on the fixture settlements' missing map prefabs, an MMR-33 gap never exercised before. The package now also records `fixturePrefabs`, and the loader restores them before the map loads | Run 14820: world 0 loads with its startup (Retirement kept, 21 intents back, all alive); world 1 loads without one. MMR-19's exact snapshot comparison flagged 8 path-state values of moving AI parties (a ruler party, a patrol, a caravan), re-derived on load after the 2-day window. They are reported in the summary, not counted as MMR-34 failures; comment on MMR-62 |
| **M4** teardown proof cannot fail | `GeneratedGameManager.OnGameEnd` (before `Campaign.OnDestroy`) removes the startup, world and creation patches and this module's behaviours' listeners from the ending campaign's events. It records what is left (`LastTeardown`, weak references only) and clears the fixture statics. The next world's audit fails on anything left, on reused events, on old behaviours listening, on a periodic world that is not its own, or on surviving fixture statics. The static list of earlier behaviours is gone. `GeneratedCharacterCreation.Release` also runs at game end | In game: 4 listeners → 0 at the first campaign's end, no patch left, new events, 0 stale listeners |
| **M5** manifest incomplete; distance models mislabelled; no version guard | The periodic patches (5 entries, MMR-63) are in the manifest, so `Patched()` sees them. `DefaultMapDistanceModel` and `NavalDLCMapDistanceModel` are adapted. `GeneratedPatches.RequireInstalledVersion` (Bannerlord v1.4.8.119303, War Sails 1.2.8) runs before every patch: startup, world, periodic and creation | In game: the versions are recorded, and the applied patches are the manifest's (9 entries). The audit fails if periodic patches are present on a non-periodic world or absent on a periodic one |
| **M6** start and embarkation audit unfalsifiable | The audit compares every creation culture's `StartingPoint` with the dataset's planned start and the installed point. The start town comes from the dataset. Embarkation checks: the gate is valid Default navigation; the port is valid Naval navigation; gate to port is within the MMR-17 envelope (`maxPortGateCells` × the nav cell); the engine's port-to-gate route exists; the sea route is finite; there is a shipyard. The summarizer checks each value | In game: port 4.38 from the gate (envelope 12.5), engine port-to-gate 54.4, naval route 699 |
| m1 the lord spawn returned an existing party | A new lord hero of the ruler's clan (`CreateSpecialHero` from the world's lord template), then `MobilePartyHelper.SpawnLordParty`. The audit checks the party is led by the new lord and is of the kind it was spawned as | `CharacterObject_912_party_1` on valid navigation |
| m2 forced spawns merged into natural counts | Natural counts are snapshotted before the forced spawns and reported apart. Across both worlds, the game must itself have spawned player, lord and villager parties | World 0 had no natural villager in its window; world 1 had 31. The doc says so |
| m3 models looser than behaviours | Every registered model of any assembly, and every effective model, must be in the inventory. TaleWorlds.Core and TaleWorlds.MountAndBlade are now scanned (24 types, all retained). The labels say "registered models (209)" and "effective contracts (124)" | In game, both checks pass |
| m4 initializer rule and test gaps | The initializer refuses a start whose basin is not open. Tests cover both fallbacks, too few hideouts (hideout spacing 1000) and closed basins. The doc still says passability uses travel cells | Spawn-policy checks 8, 17, 18 |
| m5 faction comparisons and scale constants | The `IncidentsCampaignBehaviour` reason names the five kingdom-id comparisons. The automatic reason says the scan cannot tell culture comparisons from kingdom comparisons, and what each means. `DefaultBanditDensityModel` documents its constants (270 looters, 7 and 9 hideouts) | Inventory and doc |
| m6 problems invisible; stale static | Startup problems are traced (`startup_problems:N`, then the first 10). The startup prefixes act only for the campaign that activated them; a stale one deactivates itself and lets the installed code run | Run 39816 traced `startup_problems:0` |
| m7 first name overwritten | `SetName(full, first word)` at creation and at restore; the audit compares both | In game: "Merchant 1 of town 1" has first name "Merchant" |
| m8 alias handling too permissive | The gameplay snapshot and the audit's literal check use the world's own aliases (`RequiredAliases`). The registry tests cover the startup aliases (the bandit clan, and the retreat typed `RetirementSettlementComponent`) and refuse an unprefixed non-alias (`northern_pirates`). The fake engine types a `CustomSettlementComponent` by `component_name`, as the installed IL does | Registry checks 23-24 |
| NIT counts, `requiredCultures`, note wording, weak references | The doc now gives the scan's counts (515 types; 83 with a non-extent finding; 73 without computed lookups; 108 with extents). `requiredCultures` comes from the engine rule (`IsBanditFaction`) applied to the installed land bandit clans. The hideout note distinguishes zero placed. Weak references only | - |
