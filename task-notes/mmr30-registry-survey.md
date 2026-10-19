# MMR-30 survey: campaign registry context (2026-10-15, read-only survey before claiming)

**Today.** Nothing calls `RegisterObject`, `CreateObject` or `UnregisterObject`. Every generated object enters through `MBObjectManager.Instance.LoadXml`, in two places:
- new game: `CampaignBootstrap/GeneratedCampaignRegistration.cs:112`;
- saved game: `:160`.

Parties come from vanilla new-game creation.

**Classes.**
- `GeneratedCampaignDataset.cs` is pure managed code (unit-testable). `Build` turns the stock merged XML and the placements into 8 documents.
  - It validates the catalog, emits the world instances (`<world>_ruler_N`, `_clan_N`, `_kingdom_N`, `_town_N`, `_village_N`, `_party_N`) and refuses collisions.
  - It walks the transitive template closure (the `WorldTypes` refusal).
  - Its outputs are 19 instances per world and `RequiredAliases`: main_hero ×2, player_faction, player_party.
- `GeneratedCampaignRegistration.cs`:
  - `Prepare` checks hashes; the placement holds exactly 4 settlements (line 50);
  - `Register` runs once, checks live defaults, then `LoadXml`s each document in order;
  - `ReapplyForSavedGame` replays the 5 permanent-type documents.
- `DiagnosticSandboxBootstrap.cs` is the `SandBoxGameManager` subclass and the de-facto GeneratedGameManager; the decision doc (line 410) says to promote it.
  - `RegisterGeneratedSubModuleObjects` (191–214): substitutes our loader at `SandBoxSubModule`, guards new game vs load and run-once, and takes censuses.
  - `OnLoadFinished` branches between the new-game and load paths.
- `GeneratedWorldPackage.cs` is the immutable per-world package. `WorldIdentity.cs` and `WorldIdentityBehavior.cs` hold the 13 keys under the `mnbl.world.` prefix.

**Order (proven).**
- New game: SPCultures → NPCCharacters → Heroes → Kingdoms → Factions → WorkshopTypes → LocationComplexTemplates → Settlements.
  - Towns come before villages: villages go in a second loop after the towns deserialize, because `Village.Deserialize` dereferences `Bound.IsTown`.
  - main_hero's character, hero and player_faction are emitted last.
- Saved game: only SPCultures, NPCCharacters, WorkshopTypes, LocationComplexTemplates and Settlements. Heroes, Kingdoms and Factions come from the save.
- The CampaignObjectManager lists stay empty until `game_initialization_finished` (`InitializeOnNewGame`), so any closure assertion runs in `RegisterSubModuleObjects` / `AfterRegisterSubModuleObjects`.

**The "two-pass" pattern.** The only source is the gitignored spike plan, `build/research-bootstrap18/plan.json`. `LoadXml` is sequential:
- for each record: `GetPresumedObject`, then Deserialize, then AfterInitialized;
- a forward reference creates a presumed, not-ready object;
- the referenced record's own XML deserializes later in document order;
- vanilla `UnregisterNonReadyObjects` drops any presumed object that never becomes ready.

The plan says never to use `CreateObjectWithoutDeserialize` as a placeholder, and never `ClearAllObjects`. No explicit closure assertion exists yet.

**R05 timing contract (MMR-19, `evidence/runtime/two-world-load-matrix.json`).**
- Read the identity at the menu, then load and verify the package, then deserialize the save; the save resolves by its own ids, with no world registration.
- Then `Game.LoadSaveGame`:
  1. `MBObjectManager.Init` and `RegisterTypes`;
  2. `InitializeObjects` (`BeforeLoad` re-registers saved objects by StringId);
  3. `RegisterSubModuleObjects(true)` **replays the 5 documents**;
  4. `UnregisterNonReadyObjects`.
- The replay must come **after** `BeforeLoad` (earlier orphans the objects) and **before** `UnregisterNonReadyObjects`. The ticket's "before save references deserialize" means identity-before-deserialize plus XML replay after BeforeLoad.

**IDs.**
- Runtime ids are seed-derived (`mnbl_<seed:x8>_town_1`). Compiler ids (MMR-27) are `mnbl_<8hex>_<kind>_<10hex>`, 26 characters.
- Object id regex: `^[a-zA-Z0-9_-]+$`. Scene names are at most 96 characters.
- No engine StringId length limit is known. Runtime-derived ids embed ours (`garrison_party_<town>_<clan>_1`, `militias_of_militias_of_<id>...`).
- **No save type definitions exist** (no `SaveableTypeDefiner`); safe numbering is MMR-50's.
- Aliases `main_hero`, `player_faction` and `player_party` are unprefixed by design.

**A then B (one process).**
- `GeneratedCampaignBootProbe` runs `EndGame`, waits for `Game.Current == null`, then starts B with a fresh MBObjectManager. Nothing is cleared explicitly.
- `Validate` checks for world-A survivors.
- Official templates are reloaded per game (characters 813 → 889 → 905); they are not kept.
- Evidence runs: 31308, 23192, 24212, 31624.

**Tests.** No C# test framework. Tests are standalone exes built with `csc /langversion:7.3 /warnaserror` by `tools/Test-*.ps1`:
- `Test-GeneratedCampaignDataset.ps1` uses `/nostdlib+` with hash-pinned framework references;
- `AutorunCleanupTests.cs` stubs TaleWorlds.

The mod targets net481, C# 7.3, TreatWarningsAsErrors; its csproj compiles every `**\*.cs`, and `tools/Test-ApiPins.ps1` pins the references. JSON parsing uses `JavaScriptSerializer` (System.Web.Extensions); Newtonsoft is unpinned.

**Gaps and risks.**
- The placement contract, `GeneratedMapSupport` and `Validate` hard-code the 2-town fixture ids.
- Nothing in C# reads `campaign.json` or `id-map.json` yet.
- MMR-36 expects MMR-30 to roll back staged registration.
- Generated saves are unsafe from the normal menu (MMR-49).
