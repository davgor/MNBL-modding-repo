# MMR-19: generated-world save identity across a cold restart (IL research)

Read-only IL and code survey, Bannerlord v1.4.8 + War Sails. Nothing was run, built or deployed. Statements cite IL offsets. **UNVERIFIED** marks inferences that were not checked in IL or at runtime. Method dumps are in the session scratchpad (`mmr19/*.il.txt`): `metadata`, `load`, `campaign`, `objects`, `misc` and `naval`. `load-callbacks.tsv` lists every `[LoadInitializationCallback]`/`[LateLoadInitializationCallback]` (Cecil attribute scan), and `saveable.txt` lists saveable members.

## Summary

- **The save deserializes before any game exists and needs no world registration.** `MBSaveLoad.LoadSaveGameData` → `SaveManager.Load(name, driver, true)` → `LoadContext.Load`. It creates, resolves and fills the whole object graph by save-internal object ids.
  - No `IObjectResolver` implementation ships (only `IConflictResolver`/`IEnumResolver` implementers), so nothing resolves by StringId at this stage.
  - This is why MMR-63's in-session deserialize worked, and a cold one should too (UNVERIFIED at runtime).
- **StringId binding happens later, inside `Game.LoadSaveGame`.**
  - A fresh `MBObjectManager` is created.
  - `MBObjectBase.BeforeLoad` re-registers every saved object whose `IsRegistered` is true.
  - XML is then re-merged onto those objects by StringId in `RegisterSubModuleObjects(true)`.
  - `UnregisterNonReadyObjects` drops every permanent-type object that nothing marked ready.
- **A generated save must therefore re-apply its generated XML on load.**
  - `MBObjectBase` saves only `StringId`, `Id` and `IsRegistered`; `IsReady` is not saved.
  - `Settlement` does not save its name, position, gate, port or culture. Those come only from `Settlement.Deserialize`, i.e. from XML.
  - Without re-applying the generated Settlements XML (and NPCCharacters), generated settlements and characters stay not-ready. `Campaign.OnInitialize` IL_028d then unregisters them, and the settlements have no positions. (Derived from IL; UNVERIFIED at runtime.)
- **Re-registering before `LoadResult.InitializeObjects` would be wrong.**
  - `RegisterMBObjectWithoutInitialization` skips any StringId that is already present (IL_003f/0044).
  - Objects created from XML first would therefore orphan the deserialized objects that parties and heroes actually reference.
  - The correct slot is the saved-game `RegisterSubModuleObjects(true)`. It runs after the re-registration and before `UnregisterNonReadyObjects`.
- **World identity can be read at the main menu without deserializing.**
  - Write it with `CampaignEvents.CollectMetadataEntriesEvent` (every save mode, autosaves included).
  - Read it with `MBSaveLoad.GetSaveFileWithName(name).MetaData`, which reads only the JSON header.
  - No per-save sidecar is needed. The generated XML documents belong in an immutable installed world package, verified by hash.
- **Map scene creator:** `Campaign.MapSceneCreator` is not saved. `SandBoxSubModule.OnGameInitializationFinished` IL_0020 resets it on both paths. Our existing override already replaces it after `base`, before `LoadMapScene`.
- **Built-in map identity check:** the save stores `_campaignMapSceneXmlCrc` and `_campaignMapSceneNavigationMeshCrc`. `Campaign.CheckMapUpdate` compares them on load and, on a mismatch, silently relocates settlements, map events, armies and parties. The probe must assert equality.

## Recommended protocol (probe `generated_save_identity`, two processes)

**Process 1: create and save**
1. At the menu, verify both world packages: placement, climate files, the 5 scene files and the 8 generated documents against the manifest. Record the hashes.
2. Create A with `StartGenerated` and reach MapState. Travel on land, embark at A's port and sail (crossing the seam if periodic), then pause.
3. Take snapshot S_A (fields listed below). Run `SaveHandler.SaveAs("mnbl_mmr19_<guid>")` through an observer.
4. Read the file back with `MBSaveLoad.GetSaveFileWithName` and assert the `mnbl.world.*` keys. Optionally run the MMR-63 in-session deserialize compare.
5. `MBGameManager.EndGame` and wait for `Game.Current == null`. Assert `PeriodicWorld.Current == null`, the Harmony id is unpatched and the map statics are recorded.
6. Create B (save on land or at sea, the other case from A), take S_B and save. End the game, then exit the process normally.

**Process 2: cold restart (nothing generated, nothing rebuilt)**
1. At the menu with `Game.Current == null`, read both saves' MetaData. Resolve each package by `mnbl.world.id`. Verify every hash against the metadata, and fail closed before deserializing.
2. Load A:
   - `LoadSaveGameData`. Assert `LoadResult.MetaData` matches the pre-read.
   - Inspect `LoadResult.Root`: `Campaign.UniqueGameId`, the two saved CRCs, and that the settlement StringId set equals the manifest.
   - Start `GeneratedSaveLoader(LoadResult, package)` via `MBGameManager.StartNewGame` and wait for MapState and a ready scene.
3. Verify A against S_A:
   - **Scene:** scene name and module; live `IMapScene.GetSceneXmlCrc`/`GetSceneNavigationMeshCrc` equal the saved values (so `CheckMapUpdate` did no fix-up).
   - **Map statics:** `Campaign.MapMinimumPosition`/`MapMaximumPosition`/`MapDiagonal`/`PathFindingMaxCostLimit` equal A's.
   - **Settlements:** exactly A's 4 settlements, all IsReady, with official ids (e.g. `town_V6`) absent. Position, gate and port positions, `HasPort`, and gate and port face index/group/island equal S_A.
   - **Parties:** every MobileParty's saved CampaignVec2 fields equal S_A bit for bit (NaN equals NaN), and all are canonical when periodic. `IsCurrentlyAtSea`, ship count and anchor state equal S_A. The main party's face equals S_A.
   - **Registry and support:** hero, clan and kingdom ids equal S_A; 3 navigation caches registered; generated battle-index map installed; periodic patch count matches.
   - **Time and run:** `CampaignTime` equals S_A. Then run a few hours with no first-chance exceptions and no save.
4. EndGame and assert the statics are released. Load B the same way and assert every value is B's, with no A id or A static left.
5. Delete only probe-created saves, and only after the evidence is written.

**Snapshot S_X fields:**
- **Identity:** save name, metadata dump, world id, seed, scene name, placement/scene/document/manifest hashes, `UniqueGameId`, live and saved CRCs.
- **Map:** `CampaignTime` ticks and the Campaign map statics.
- **Settlements:** id, the three positions, `HasPort`, owner, and gate/port faces.
- **Parties:** id, `_position`, `MoveTargetPoint`, `_targetPosition`, `_pathLastPosition`, `EndPositionForNavigationTransition`, `IsCurrentlyAtSea`, current settlement, anchor fields, main-party face.
- **State:** registry census, behavior types, navigation cache count, periodic state.

Process 1 writes S_X to the evidence JSON. That is a test oracle, not runtime state.

## Required code changes

1. **`MnblWorldIdentityBehavior : CampaignBehaviorBase`**, added in `OnGameStart` on both the new and load paths.
   - `RegisterEvents` adds a `CampaignEvents.CollectMetadataEntriesEvent.AddNonSerializedListener` that writes the `mnbl.world.*` keys. Keys must be `mnbl.`-prefixed because the OtherData values are written with `set_Item` and can overwrite built-in keys.
   - Keys:
     - `schema`, `id`, `seed`, `scene`;
     - `placementSha256`, `sceneFilesSha256`, `documentsSha256` (or one per document), `manifestSha256`;
     - `periodic`, `datasetVersion`, `sceneXmlCrc`, `navMeshCrc`.
   - Mirror the same keys in `SyncData` as an in-graph cross-check, readable after `LoadBehaviorData`. A re-save of a loaded world keeps identity only if this behavior is also added on load.
2. **Installed immutable world package** written at creation, e.g. `Modules/MNBL.ProceduralWorld/ModuleData/GeneratedWorlds/<worldId>/`. Adding files to the SceneObj folder is UNVERIFIED with `Scene.Read`.
   - Contents: `placement.json`, the climate binaries, the 8 `Dataset.Documents` XML files (exact `OuterXml` bytes, which `Document.Sha256` already hashes), and `world.json` listing every hash including the 5 scene files.
   - Load reads only this package and the scene folder. It needs no catalog receipt, no `GeneratedCampaignDataset.Build` and no generator. An optional cross-check rebuild must not be a load dependency.
3. **`GeneratedCampaignRegistration.FromInstalledPackage(path)`** (load) next to `Prepare`.
   - It verifies the hashes and builds `PeriodicWorld` and `GeneratedClimate` from the installed placement.
   - Add `ReapplyForSavedGame(trace)`, which runs `LoadXml` (after a per-document hash check) only on the saved-game subset, in the new-game order: SPCultures, NPCCharacters, WorkshopTypes, LocationComplexTemplates, Settlements.
   - Skip Heroes, Kingdoms and Factions. These are temporary types whose state lives in the save, matching vanilla `InitializeSandboxXMLs(true)`.
4. **`GeneratedSaveLoader : SandBoxGameManager`** with `ctor(LoadResult, package)` calling `base(LoadResult)`. It can be a mode of `DiagnosticSandboxBootstrap`, keeping the new-game guard. Overrides:
   - `OnGameStart`: the same behavior removals, plus the identity behavior and `PeriodicSeamBehavior`.
   - `RegisterSubModuleObjects(true)`: dispatch other submodules with `true` and substitute SandBox with `ReapplyForSavedGame`, with census before and after.
   - `OnGameInitializationFinished`: `base`, then install `DiagnosticMapSceneCreator(scene, trace, worldId, report, periodic, climate)` and the save guard.
   - `OnLoadFinished`: guard, then `base` (saved branch restores MapState and the menu), then pause. It must never enter character creation.
5. **Probe checks:**
   - Assert CRC equality, i.e. no `CheckMapUpdate` relocation.
   - Assert the settlement set and IsReady state after `InitializeCampaignObjectsOnAfterLoad`.
   - Keep the existing first-chance breadcrumbs, since load-only listeners (`OnGameEarlyLoaded`, `OnGameLoaded`, `OnGameLoadFinished`) are untested code.
6. **Reset `DiagnosticMapScene.PeriodicQueries`/`ImageWins`/`NativeQueries`/`Pruned` in `Load`.** Today they accumulate across worlds.
7. **Later, MMR-49:** normal-menu routing. `SavedGameVM.ExecuteSaveLoad` → `SandBoxSaveHelper.TryLoadSave` → `SavedGameVM.StartGame` builds a vanilla `SandBoxGameManager(LoadResult)`.
   - A vanilla-manager load of a generated save would load official Settlements and NPCCharacters XML, and the generated settlements would be unregistered and have no position (UNVERIFIED at runtime).
   - Fix: a Harmony prefix on `SandBoxSaveHelper.TryLoadSave` or `SavedGameVM.StartGame` that routes saves carrying `mnbl.world.*` to the loader, or refuses them.

## Details

### 1. Metadata (write, read early, autosaves)

**Write path**
- `SaveHandler.SaveTick` builds metadata for every mode with `SaveHandler.GetSaveMetaData`:
  - ironman IL_0067, SaveAs IL_00ab, quick IL_00c9, auto IL_00e1;
  - after `OnBeforeSave` at IL_005a.
- `GetSaveMetaData`:
  - builds a `List<KeyValuePair<string,string>>` of built-ins: `UniqueGameId` IL_0006, `MainHeroLevel` … `IronmanMode`, `HealthPercentage`, `NewGameVersion` IL_02bb;
  - calls `CampaignEventDispatcher.CollectMetadataEntries(list)` IL_02f3, which dispatches to `CampaignEvents.CollectMetadataEntries` → `_collectMetadataEntriesEvent.Invoke` (public getter `CampaignEvents.get_CollectMetadataEntriesEvent`, type `IMbEvent<List<KeyValuePair<string,string>>>`);
  - returns `new CampaignSaveMetaDataArgs(moduleNames, list.ToArray())` IL_032c.
  - Vanilla example: `StoryMode…AchievementsCampaignBehavior.CollectMetadataEntries` adds `AchievementsDisabled`; `StoryMode.Extensions.MetaDataExtensions.AreAchievementsDisabled` reads it back with `TryGetValue`.
- `MBSaveLoad.SaveAsCurrentGame`, `QuickSaveCurrentGame` and `AutoSaveCurrentGame` all call `OverwriteSaveAux` → `MBSaveLoad.GetSaveMetaData(args)` IL_0015, which writes:
  - `Modules` (set_Item IL_004f) and `Module_<id>` (set_Item IL_008e);
  - `ApplicationVersion` (`Add`, IL_00c6) and `CreationTime` (`Add`, IL_00e8);
  - each OtherData pair with `set_Item` (IL_0114).
- `FileDriver.Save` then calls `MetaData.Add("Version")` IL_0026 (`Dictionary.Add`, which throws on a duplicate key) and serializes the metadata first (IL_002d: Newtonsoft JSON, length-prefixed). The deflated game data follows at IL_0049.

**Read path, no deserialization**
- `FileDriver.LoadMetaData` IL_0010 → `FileHelper.GetMetaDataContent` → `PlatformFileHelperPC.GetMetaDataContent`, which reads an Int32 length and that many bytes (IL_0024..005d), then `MetaData.Deserialize` IL_001f.
- Entry points:
  - `MBSaveLoad.GetSaveFiles` IL_0005 → `FileDriver.GetSaveGameFileInfos`, which calls `SaveManager.LoadMetaData` per `*.sav` (IL_003b) and stores it in `SaveGameFileInfo.MetaData`;
  - `MBSaveLoad.GetSaveFileWithName` IL_0017 filters that list;
  - `new FileDriver().LoadMetaData(name)` is public.
- **Readable at the main menu:** the driver comes from `MBSaveLoad..cctor` IL_000b (`new FileDriver()`) and needs no Game. Vanilla calls `GetSaveFiles` at the initial menu (`SandBoxViewSubModule.OnInitialState` IL_002d).
- `LoadResult.MetaData` holds the same header after a full load. `MBSaveLoad.OnStartGame` reads only `ApplicationVersion` from it.

### 2. Campaign load order (installed IL)

| # | Step | Where | Offset |
|---|---|---|---|
| 0 | Header plus full graph build (create, resolve, fill); callbacks deferred because `loadAsLateInitialize=true` | `LoadSaveGameData`; `LoadContext.Load` (IL_048a/048b skip callbacks) | LoadSaveGameData IL_000f, LoadContext.Load IL_01e3/0322/03bf/0472 |
| 1 | `Module.OnBeforeGameStart` → `MBSubModuleBase.OnBeforeGameStart(manager, list)` | `MBGameManager.StartNewGame` | IL_0006 |
| 2 | `LoadModuleData(true)`; `MBGlobals.InitializeReferences` | `SandBoxGameManager.DoLoadingForGameManager` | IL_002c; IL_0046 |
| 3 | `MBSaveLoad.OnStartGame`; `MBObjectManager.Init` (new instance) | `Game.LoadSaveGame` (called at DoLoadingForGameManager IL_00b7) | IL_0001; IL_0006 |
| 4 | `Game.RegisterTypes`: `GameType.BeforeRegisterTypes` IL_0005, core types, `Campaign.OnRegisterTypes` IL_0149, **`GameManagerBase.RegisterSubModuleTypes` IL_0152** | Game.LoadSaveGame | IL_001f |
| 5 | **`LoadResult.InitializeObjects`**: `[LoadInitializationCallback]`s. Includes `MBObjectBase.BeforeLoad` → `OnBeforeLoad` IL_000e `TryRegisterObjectWithoutInitialization` (re-registers by StringId and keeps the saved MBGUID), plus `Campaign.OnLoad`, `Settlement.OnLoad`, `Hero.OnLoad`, `Clan.OnLoad`, `MobileParty.OnLoad`, `PartyBase.OnLoad`, `CharacterObject.OnLoad` and others | Game.LoadSaveGame | IL_0025 |
| 6 | `MBObjectManager.ReInitialize`: each record's `_objCount = max SubId + 1` (`ObjectTypeRecord.ReInitialize` IL_004d). No registration | Game.LoadSaveGame | IL_002f |
| 7 | `LoadResult.AfterInitializeObjects`: late callbacks `Settlement`/`MobileParty`/`MobilePartyAi`/`MapEvent.OnLateLoad`, `Building.OnLoad`, `VisualTrackerManager.OnLateLoad` | Game.LoadSaveGame | IL_0035 |
| 8 | `Game.BeginLoading` (Game.Current is set here); `Campaign.SetLoadingParameters(2)`; `Game.DoLoading` | LoadSaveGame IL_0048; DoLoadingForGameManager IL_00c7/00f4 | |
| 9 | State 0: `Game.Initialize` IL_0045 → **`Campaign.OnInitialize`**: `SandBoxManager.Initialize(starter)` IL_00c7, `InitializeGameStarter` IL_00d9, `OnGameStart` IL_0108, `InitializeDefaultCampaignObjects` IL_014f (Items/EquipmentRosters/partyTemplates XML), `BeginGameStart` IL_017d, `InitializeBasicObjectXmls` IL_0198 (SPCultures, Concepts) | Campaign.DoLoadingForGameType IL_0020 | |
| 10 | `SandBoxManager.OnCampaignStart(starter, gm, true)` IL_01ce → **`RegisterSubModuleObjects(true)`** IL_0002 (SandBox: `InitializeSandboxXMLs(true)` reloads NPCCharacters, WorkshopTypes, LocationComplexTemplates and Settlements, and skips Heroes/Kingdoms/Factions, IL_0010/0040) → `AfterRegisterSubModuleObjects(true)` IL_0009 (`InitializeCharactersAfterLoad`) → `RemoveTemporaryTypes` IL_0024 | Campaign.OnInitialize | |
| 11 | `GameManagerBase.OnGameLoaded` IL_022b; behaviors `InitializeCampaignBehaviors` IL_023c, **`LoadBehaviorData` (SyncData)** IL_0247, `RegisterEvents` IL_0252; **`UnregisterNonReadyObjects`** IL_028d; `InitializeCampaignObjectsOnAfterLoad` IL_029c (`CampaignObjectManager.InitializeOnLoad`); `OnGameInitializationFinished` IL_02ed (SandBox sets `MapSceneCreator` here) | Campaign.OnInitialize | |
| 12 | State 2: **`LoadMapScene`**: `MapSceneCreator.CreateMapScene` IL_0002, `IMapScene.Load` IL_0069, static bounds and limits IL_0080..00f4, `IMapScene.AfterLoad` IL_00ff | DoLoadingForGameType IL_0037 | |
| 13 | State 3: `CheckMapUpdate` IL_0059 (CRC compare IL_001f/0028), `OnDataLoadFinished`, `OnAfterGameLoaded` IL_007d, **`Campaign.OnGameLoaded`** IL_0084 (MBObjectManager/CampaignObjectManager `PreAfterLoad`/`AfterLoad`; events `OnGameEarlyLoaded` IL_006e and `OnGameLoaded` IL_0079; `InitializeForSavedGame` IL_007f), **`OnSessionStart`** IL_008b (events `OnSessionLaunched` IL_0006 and `OnAfterSessionLaunched` IL_0011) | DoLoadingForGameType | |
| 14 | `SandBoxGameManager.OnLoadFinished` (saved branch): `MapState` IL_013a, menu restore, `OnGameLoadFinished` event IL_0200, `MapState.OnLoadingFinished` IL_020b | | |

**What runs on load and what does not**
- XML-read objects are always fully deserialized first. XML is re-read for a subset of types and merged by StringId:
  - `MBObjectManager.LoadXml` → `GetPresumedObject` returns the registered object (IL_0026) → `Deserialize` IL_00c9 → `AfterInitialized` IL_00ce, which sets IsReady.
  - `Settlement.Deserialize` has explicit saved-game branches: it keeps `LocationComplex` (IL_0290), skips alleys (IL_0476) and skips `owner` (IL_0507).
- Not called on load: `GameManagerBase.OnNewCampaignStart` (skipped at OnInitialize IL_01a4) and `Campaign.OnNewCampaignStart` (skipped at IL_0189).

**Runtime-created vs XML objects**
- Both are saved as full data, with no difference at the SaveSystem level.
- Afterwards:
  - **Permanent types** (Settlement, CharacterObject, CultureObject, components) need IsReady from an XML re-merge or from their owner system (e.g. `CharacterObject.InitializeHeroCharacterOnAfterLoad`, `CraftingCampaignBehavior.InitializeCraftedItemData`). Otherwise `UnregisterNonReadyObjects` removes them from the registry. The objects stay in the graph but are no longer registered.
  - **Temporary types** are registered with `isTemporary = 1`: MobileParty (OnRegisterTypes IL_000e), Clan ("Faction"), Kingdom and Hero. `RemoveTemporaryTypes` drops them from the registry. They live in the saved `CampaignObjectManager` lists and are made ready in `CampaignObjectType<T>.AfterLoad` (state 3).

### 3. Hooks relative to `LoadResult.InitializeObjects` (Game.LoadSaveGame IL_0025)

**Before:**
- the main menu: metadata read, `LoadSaveGameData`, inspection of `LoadResult.Root`;
- the manager subclass constructor (`SandBoxGameManager(LoadResult)` sets `LoadingSavedGame` and `_loadedGameResult`, ctor IL_0008/000f);
- `MBSubModuleBase.OnBeforeGameStart`;
- `MBGameManager.RegisterSubModuleTypes` and `MBSubModuleBase.RegisterSubModuleTypes` (needed only for custom MBObjectBase types; otherwise `TryRegisterObjectWithoutInitialization` asserts, IL_0071).

**After:**
- `InitializeGameStarter`, `OnGameStart`, `BeginGameStart`;
- `RegisterSubModuleObjects(true)` and `AfterRegisterSubModuleObjects(true)`;
- `MBSubModuleBase.OnGameLoaded` / `MBGameManager.OnGameLoaded`, then behaviors' `SyncData`;
- `OnGameInitializationFinished`, `LoadMapScene`, `OnAfterGameLoaded`, CampaignEvents `OnGameEarlyLoaded`/`OnGameLoaded`/`OnSessionLaunched`, `OnLoadFinished`/`OnGameLoadFinished`.

**Use:**
- (a) read identity at the menu and pass it to the loader constructor;
- (b) re-merge the generated XML in `RegisterSubModuleObjects(true)` (after BeforeLoad, before `UnregisterNonReadyObjects`);
- (c) install the map scene creator in `OnGameInitializationFinished`, after `base`.

There are no ObjectManager XML-loading hooks: `MBObjectManagerExtensions.LoadXML` merges module `Xmls`, and our `SubModule.xml` declares none, which is correct.

### 4. Why it throws today, and what changes

- `DiagnosticSandboxBootstrap.RegisterGeneratedSubModuleObjects` (DiagnosticSandboxBootstrap.cs:154) throws `NotSupportedException` when `isSavedGame` is true.
- The vanilla route to it is `SandBoxManager.OnCampaignStart` IL_0002.
- In practice it is unreachable:
  - the only constructor is new-game (`base(() => new Campaign(...))`, line 53);
  - `OnLoadFinished` throws if `LoadingSavedGame` (line 197).
- It is a guard. Reusing `registerWorldObjects` → `GeneratedCampaignRegistration.Register` would:
  - rebuild the dataset (`GeneratedCampaignDataset.Build`, line 101), which needs the AppData catalog receipt (line 57) and live defaults;
  - load all 8 documents, including Heroes, Kingdoms and Factions, onto saved temporary-type state;
  - trip its run-once guard (line 89).

**What changes (see Required code changes):**
- **Registration timing:** keep it in `RegisterSubModuleObjects(true)` with the saved-game subset.
- **Dataset:** the installed package replaces the rebuild.
- **Map scene creator:** unchanged slot.
- **Navigation caches:** `GeneratedMapSupport.RegisterNavigationCaches` runs in `AfterLoad` (state 2), after the settlement XML positions (state 0). Its `_navigationCaches.Count == 0` precondition should hold because models are new per Campaign (UNVERIFIED on load). Vanilla registers caches from the scene script `SandBox.View.Map.SettlementPositionScript.RegisterNavigationCachesOnGameLoad`, which is absent from our scene.
- **Battle index map:** `GeneratedBattleTerrain.Install` in `AfterLoad` is unchanged.
- **Periodic world (MMR-63):**
  - `PeriodicWorld`/patches come from the installed placement and are applied in `Load` (state 2).
  - The load and late-load callbacks above make no face or MapScene calls (scan of `objects.il.txt`).
  - Parties' face and `Path` are recomputed after the scene (`ComputePathAfterLoad`, which calls `CampaignVec2.get_Face` → `Campaign.Current.MapSceneWrapper`).
  - Saved positions are canonical (MMR-63). `PeriodicSeamBehavior` stores no data (`SyncData` is empty), so it only has to be re-added in `OnGameStart`.
- **Climate:** `GeneratedClimate.FromPlacement` reads its wind and rain binaries next to `placement.json`, so they must be in the package.

### 5. World-specific statics to clear or check between sequential worlds

- **Ours:**
  - `PeriodicWorld.Current`, set in `DiagnosticMapScene.Load` line 110 and cleared in `Destroy` line 270.
  - `PeriodicPatches` (Harmony instance, `Applied`, `LastError`, counters), removed in `Destroy` line 269.
  - `PeriodicSeam.Events`, `Rebases` and `TargetsCanonicalized`, reset in `Load` line 111.
  - `DiagnosticMapScene.Periodic*` counters (line 184): **never reset**.
  - `GeneratedMapSupport.States`: a `ConditionalWeakTable` keyed by scene, so nothing is retained.
  - `GeneratedBattleTerrain.LandSubstitute` and `GeneratedCampaignDataset` sets: immutable.
- **Engine, Campaign statics written only in `LoadMapScene`:** `MapMinimumPosition`, `MapMaximumPosition`, `MapMaximumHeight`, `MapDiagonal`, `MapDiagonalSquared`, `PathFindingMaxCostLimit`, `PlayerRegionSwitchCostFromLandToSea`.
  - `Campaign.OnDestroy` does not clear them; it only sets `Campaign.Current = null`.
  - From game end until state 2 of the next game (InitializeObjects, XML re-merge, behaviors) they still hold the previous world's values. Assert them after the map loads, and never rely on them earlier.
- **Engine, other statics:**
  - `MBSaveLoad.ActiveSaveSlotName` and `AutoSaveIndex` are set by `LoadSaveGameData` (IL_0001/001e), so a later quick save targets the loaded slot. Always use `SaveAs` with unique names.
  - `LastLoadedGameVersion`.
  - `SaveManager._isLoading` and `OperatingVersion` are reset after load.
  - `GameSceneDataManager.Instance` is re-initialized and destroyed per campaign.
  - `MBObjectManager.Instance` is destroyed in `Game.Destroy` IL_005c and newly created at load.
  - View and DLC statics (`MapScreen.Instance`, `MBMapScene.ApplyRainColorGrade`, `NavalDLCManager.Instance`): reset is UNVERIFIED.
  - Native scene-name → module pairing persists but is keyed by the unique scene name.
  - `Utilities.SetAllocationAlwaysValidScene` global: UNVERIFIED.

### 6. Sidecar decision and residual risks

- **No per-save sidecar.** The metadata is readable at the menu and carries identity and hashes. The installed world package is shared, immutable world output, not save state. The S_X evidence JSON is only the test oracle.
- **Risks** (all UNVERIFIED at runtime):
  - load-only listeners in official behaviors, e.g. `NavalStorylineCampaignBehavior.OnGameLoadFinished` touches `StoryModeHeroes.LittleSister` (guarded by version and the IL_0006 registration branch);
  - whether `CheckMapUpdate` CRCs are deterministic across processes;
  - `_navigationCaches` empty on load;
  - a vanilla-menu load of a generated save corrupting it (needs routing, MMR-49).
