# Runtime backend decision (R07, MMR-21, 2026-10-14)

**Selected backend: an owned native scene bundle per world, loaded through an owned `SandBox.MapScene` subclass, with generated campaign objects registered as XML before campaign initialization.**

The selection is automatic and needs no intermediate human approval:
- every mandatory runtime capability R01–R05 and R08–R10 has accepted evidence on this backend;
- the limits, unresolved items and failures below are each scoped to the dependent ticket that owns them;
- loading a navmesh and settlement entities from a scene registered after launch, then booting a campaign on it, is observed by MMR-45. The generation job installs and registers the world's scene in the running game, and the activation boots the campaign on it with its navmesh and baked settlement entities: world_activation runs 22472, 9404 and 43676 (`docs/world-activation.md`).

Acceptance of this ticket satisfies the MMR-21 prerequisite of MMR-25, MMR-29 and MMR-30 (see "Release of dependent work").

| | |
|---|---|
| Decision inputs | the accepted R-tickets below; `docs/runtime-capabilities.md`; `evidence/runtime/backend-callsite-matrix.json`; `evidence/runtime/lifecycle-extension-contract.json` |
| Reference build | game `v1.4.8.119303` with War Sails (NavalDLC). Module `MNBL.ProceduralWorld` at `174fcb0a…`, the tree that produced the MMR-19 and MMR-59 evidence; earlier R-evidence ran on earlier trees (see "Evidence currency"). Bannerlord.Harmony 2.4.2 (Workshop 2859188632, pinned in `config/runtime-api-lock.json`) |
| Independent review | `evidence/runtime/independent-review.md`: the verbatim report, then the disposition of every finding |
| Provisional limits | the section "Provisional limits" below, from recorded telemetry only (`task-notes/mmr21-telemetry.md`) |

## Candidates and why this one

| Candidate | Status | Reason |
|---|---|---|
| **Native scene bundle + `SandBox.MapScene` subclass** | **selected** | It keeps every native consumer the campaign uses: terrain heightfield, navmesh faces and islands, picking, the 31 concrete `SandBox.MapScene` casts, the War Sails map wrapper, the battle-index map and the weather fields. Every R-probe ran on it. |
| Procedural meshes + managed map adapter | rejected | Meshes supply no native heightfield or navmesh. The historical raycast crash and the native picking/face bypasses (`HandleLeftMouseButtonClick` IL_007c `Scene.DoesPathExistBetweenFaces`) have no supported replacement. |
| Navigation prefab replacement inside an indexed scene | rejected as a backend | A strict subset: it covers navigation only, and established no route rebuild, cache invalidation or seam connectivity. Its one observed step, PID 25128 rewriting a navigation file whose name was indexed before startup, rewrites a fixed name. The selected backend forbids that for loaded scenes (names are immutable), and newly created filenames imported 0 faces in that era (PIDs 29364, 14300). It survives only as the fallback in "Still unsupported or unresolved mandatory operations". |

**Tradeoff.** This is the smallest supported surface that covers every capability: one owned scene folder and one owned map-scene class per world, the installed load and registration hooks, and Harmony patches only where a periodic world needs them. It costs private-field writes on `SandBox.MapScene` (listed under "Workarounds") and a per-world navigation-cache build on the first load of each world, which MMR-41 publishes and later loads read back (see "Provisional limits").

## Contract-to-evidence matrix

Status words:
- **passed:** accepted evidence on a tree judged current.
- **passed with limits:** evidence, plus recorded gaps that belong to named tickets.
- **unresolved:** no runtime evidence yet; the owner ticket must produce it.
- **failed:** a reproducer exists.

Absent evidence is never marked passed. "IL" means the behaviour was read from installed IL, not observed.

| R | Contract | Status | Evidence | Limits, unresolved items and owners |
|---|---|---|---|---|
| R01 | In-game generation from a chosen seed with progress, cancel and errors, no external editor or restart | passed with limits | MMR-15: `evidence/runtime/generator-probe.json` (PID 19240: seeds 42 and 43, invalid request, producer abort, cancel, recovery). Producer `fwg.exe` and `worldbridge.exe` are pinned (MMR-22). Generation is reproducible (MMR-24, `evidence/fwg/generation-identity.json`) | Developer-menu callbacks only: the New Campaign screen is MMR-43 and the transactional job is MMR-44. (The developer menu and its `generation_menu` probe were removed on 2026-10-18.) The run used worldbridge `0d8bf53a…`; the deployed pin is now `2a8b3d63…` (see "Evidence currency") |
| R02 | Runtime terrain rendering, heights, ground, rays and picking on freshly generated terrain registered after launch | passed with limits | MMR-16: `evidence/runtime/surface-probe.json`, `runtime_surface-32896*`, review `surface-coverage-review.json` (`trueCurrentBlockers: []`). In one process, seeds 42 (800 m) and 43 (640 m) were generated, encoded, installed under fresh names and registered at runtime (`Utilities.PairSceneNameToModuleName`). Party grounding passed in `sandbox_boot-25008` | **Exact native bin-boundary rounding is unresolved** (MMR-37). **The combined terrain-node exports crash natively** (PID 27392); they are withheld and have no gameplay consumer. **Constraint:** a loaded scene's `terrain.bin` cannot be replaced (file lock), so names are immutable. **Superseded:** the 0.2002 m grazing-ray error recorded for PIDs 34660/12748 was a historical reference model (`legacySourceReferenceError`, "historical_untruncated_anti_diagonal_observation_only"). The same ray (`calibration_200_128:camera`) has `referenceError` 3.8e-05 m in the accepted run 32896 |
| R03 | Generated land and War Sails routes on runtime geometry; player and AI movement; obstruction | passed with limits | MMR-17: `evidence/runtime/navigation-probe.json` (run 2248, two worlds). Land travel and naval sailing re-ran on `174fcb0a` (runs 24212 and 31624) | **Navmesh loading from a scene registered after launch is unobserved.** Every route ran on scene slots installed before launch with `tools/Deploy-SceneSlots.ps1` (MMR-45). The obstruction test toggled faces with `Scene.SetAbilityOfFacesWithId`, which renumbers islands; the probe's `RefreshFaceCaches` refreshes only `MobileParty` positions, not gate and port positions (probe-only; MMR-38). The player's return voyage was not run. No bridges, no stitching, and AI ship choice was scripted |
| R04 | Generated campaign objects register before initialization; no official world objects survive; two worlds in one process | passed with limits | MMR-18: `evidence/runtime/bootstrap-probe.json` (runs 31308, 23192, 24736). Registration re-ran on `174fcb0a` (24212, 31624; census 4 settlements, 3 clans, 2 kingdoms in every world) | `BackstoryCampaignBehavior` and `RetirementCampaignBehavior` are removed in generated mode. 33 engine-literal NPC templates are retained. `fighter_sturgia` is excluded (its stock record references a missing EquipmentRoster). Excluded literals still have live consumers beyond the 2-day window: NavalDLC `dead_lord_7_1`/`naval_storyline_*` and `GetBodyguardOfCulture` (MMR-34). Town meshes were not visible in captures (nameplates only; MMR-40). Town food falls about 70/day (MMR-33). No bandit clans (MMR-34) |
| R05 | Save identity read before generated references deserialize; cold-restart load of two worlds without regeneration | passed with limits | MMR-19: `evidence/runtime/two-world-load-matrix.json` (runs 31624 and 8256; 0 snapshot differences, 0 dataset builds). MMR-62 (`docs/save-binding.md`, `evidence/persistence/initial-save-recovery.json`, runs 33696 and 19888): the header binds the campaign instance and data bundle; the initial save is journaled, read back whole and flushed before it counts as published (Ironman included); a publication killed after the engine save and torn or unbound saves under the reserved name recover at the next start; the reference index fails closed; abandoned staging is removed under a process-wide store lock | Normal-menu load routing or refusal is MMR-49: a vanilla-manager load of a generated save is unsafe. No re-save after load and no alternating same-process load matrix (MMR-49). Anchors and storms are outside the snapshot (MMR-49). Autosave and failed save are evaluated from IL only. Refusal fixtures are MMR-50. Package location is resolved by world id and manifest hash (MMR-62); a fixture world's scene slot still lives outside its package, and scene installation and reinstallation belong to MMR-45/49. Only the initial save is journaled; the later save kinds are MMR-48. 30 handled first-chance exceptions per load, source unknown (MMR-49) |
| R06 | No ticket in the saved plan carries the `r06` tag, so nothing is mapped to it. The former MMR-20 provisional limits are recorded in this document | — | — | — |
| R08 | Generated wind/flow, storm spawn area and snow/rain bound to and read by native and War Sails consumers; replaced on a world switch | passed with limits | MMR-59: `evidence/runtime/environment-rebind-probe.json` (run 24212). MMR-39: `evidence/world/water-climate.json` (runs 27432, 30900, save/load 25508; `world_surface` 32884) | MMR-39 decided the production path: the private-field contract stays, behind the load-time checks (see "Native data contracts"). MMR-39 synthesizes the fields from the source world (`docs/water-climate.md`); scales storm spawning by node area and gates it to the canonical range between the pole lines; wraps storms and weather across a periodic seam; and binds a generated colour grade. Storms need OpenSea faces. The terrain snow texture is unsupported (cosmetic; no safe texture path). Authored `flowmap.dds` is untested. The saved weather-node index is MMR-49. Native resource release is unverified (MMR-49). A production campaign's climate binding (`FromSynthesis`) and colour-grade manager are MMR-45's activation. Non-virtual planar storm checks near the seam are MMR-65 |
| R09 | Generated terrain survives land and naval mission transitions; battle scenes from the generated map patch | passed with limits | MMR-60: `evidence/runtime/mission-transition-probe.json` (run 22868, module `19fa06b0…`; earlier runs including 33472 are history). A field battle at the periodic seam entered and returned in run 12348 (R10) | Only 8 installed battle indices serve both land and naval cells. Mountain, Forest and Snow need substitution rules or more scenes (MMR-42). Location scenes come from settlement prototypes (MMR-42). Mission terrain is the stock template's. The battle-index map does not wrap (see R10). The MMR-60 note that periodic seams block R07 is resolved by R10's encounter-direction and seam-battle evidence; the non-wrapping patch map is a scene-pattern discontinuity, not a correctness failure, because parties are always canonical |
| R10 | Periodic east/west topology for routes, distances, movement, encounters, camera follow, picking, saves and weather; poles | passed with limits | MMR-63: `evidence/runtime/world-wrap-probe.json` (run 12348): routes, distances, travel both ways, proximity and visibility, engine encounter, a field battle and return, camera follow, a strip click through `ProcessTravel`, and a save deserialized near the seam. MMR-64: `evidence/runtime/periodic-coordinates.json` (run 6292, `docs/periodic-coordinates.md`): one coordinate contract and tie rule, the whole `MapDistanceModel` and the distance helpers covered and periodic across the seam, long-way routes, the closest entrance near the seam, the wrapped battle-terrain map, the coordinate frame in the save header, and the MMR-63 checks rerun. MMR-65: `evidence/world/periodic-movement.json` (run 14144, `docs/periodic-movement.md`): the periodic radius query from canonical and strip centres, direct party, settlement and track grid searches, battle joiners across the seam, moving-target pursuit, a land army and a fleet with attached members, route cancellation at the seam, ports entered both ways and a circumnavigation with route costs the short way, with no transition at the cut; 98 audited consumers transpiled (161 calls), 27 inlining callers recompiled, and the MMR-63 checks rerun. MMR-66: `evidence/world/periodic-camera-picking.json` (run 33944, `docs/periodic-presentation.md`): one canonical camera rebased by whole widths before the native camera is written, following the player across both edges and panned through the seam by its target and by injected axis input with the native camera on its frame, fast moves the short way, teleport and reset on the party, a native terrain pick in the strip ordering the canonical point, a settlement display copy picked through the engine's own resolution path to the canonical town, nameplates, decals and tracks at the images beside the camera, strip twins aligned at the seam and both ports, a town menu and a seam battle left with the camera on the canonical party, and 20 camera wraps without growth of copies or visual registrations; 52 patch entries, and the MMR-63 checks rerun | **Inferred, not observed:** mouse dragging (it pivots on the rebased `_clickedPosition`; MMR-66 limits); blockade, storm and anchor visuals at the seam and the encyclopedia's map navigation (code paths without in-game samples; MMR-66 limits). **Unresolved:** resuming a periodic save and cold-loading a periodic world (the seam save was only deserialized; MMR-49); a naval encounter at the seam (MMR-53); sieges, raids and embarking or disembarking at a coast at the seam were not run in game (their spatial calls are audited and transpiled; MMR-65 limits); siege machines are not drawn on a settlement's display copy (MMR-42). **Resolved by MMR-65:** army members crossing the seam beside their leader (land and sea); the direct `LocatorGrid` callers and `FindPointAroundPosition` (periodic); the planar spatial consumers outside the distance surface (audited: 98 transpiled, the rest classified); tracks across the seam; the storm checks MMR-39 left planar are transpiled and its possibly missed damage tick is addressed by strip-centred image passes (the strip-centre query is tested; storm damage itself was not rerun). **Resolved by MMR-66:** the camera fields (one canonical camera replaces the field heuristic), the camera x-clamp (never reached), terrain and object picking at the seam, labels, decals, tracks and settlement display copies bound to canonical identities. **Planar:** distance-sorted interface lists, behaviour texts and map step sounds near the cut (MMR-66 limits); the save-instant canonical window and `BehaviorTarget` (MMR-49). Routes whose path leaves the terrain cannot be represented (MMR-64). **Resolved by MMR-64:** pole identity (pole aliases), the tie rule, the battle-index map wrap, and polar distortion (accepted, `worldgen/policies/periodic-coordinates.json`). **Test coverage:** the locator image pass is tested from both sides and from a strip centre (MMR-65); AI destination choice is shown only indirectly |

**Whole-world periodic navigation has not failed.** Cross-seam routes, movement, encounters and distances were demonstrated in the shipping client (R10). It is not complete either: the items above are owned work, not a reason to ship disconnected edges.

### Evidence currency

The ticket asks for reruns only of stale, missing or contradictory evidence. Each R was judged against what changed after its run (file times of the exercised code, and the deployed pins):

| R | Run (module) | Changed since | Judgement |
|---|---|---|---|
| R01 | 19240 (`e326e3fe…`, bridge `0d8bf53a…`) | worldbridge rebuilt for MMR-22/24; deployed pin `2a8b3d63…`; producer unchanged (`564c2629…`) | Not rerun. Supervising a child process from the game does not depend on the bridge build. The current bridge's reader ran offline in MMR-24, and its encoder built every fixture the `174fcb0a` runs loaded. MMR-44 reruns in-game generation with the current pins |
| R02 | 32896 (`c7a353ae…`, encoder `21da9dc8…`) | encoder rebuilt (`2a8b3d63…`); `RuntimeSurfaceSequenceProbe.cs` unchanged since before the run | Not stale for rendering, heights and rays: terrain from the current encoder loaded and rendered in every `174fcb0a` run. Runtime registration with the current encoder belongs to MMR-45 |
| R03 | 2248 (`5e28cde8…`) | map scene gained the battle map, periodic routing (inert for non-periodic worlds) and climate | Current: navigation caches, land travel and sailing re-ran on `174fcb0a` |
| R04 | 31308, 23192, 24736 | registration extended with the package, identity and load mode | Current: generated registration re-ran on `174fcb0a` with the same census |
| R05, R08 | `174fcb0a` | — | Current |
| R09 | 22868 (`19fa06b0…`) | `RuntimeMissionProbe.cs` extended for MMR-63; `GeneratedBattleTerrain.cs` unchanged since before the run | Not stale: a field battle entered and returned on `82406613` (12348) |
| R10 | 12348 (`82406613…`) | `PeriodicWorld/Seam/Patches.cs` unchanged since before the run; `DiagnosticMapScene.cs` changed for the climate binding (MMR-59) and to reset the periodic counters in `Load` (MMR-19) | Not stale. No periodic world has run on `174fcb0a` |

No evidence was found to contradict another, so nothing was rerun.

## Frozen contracts

### Extension seams (in call order)

1. **Generation.** `fwg.exe` and `worldbridge.exe` run as owned child processes off the game thread, with cancel and cleanup (MMR-15/16). The engine thread only polls.
2. **Scene install and registration.**
   - The bundle is written to `Modules/MNBL.ProceduralWorld/SceneObj/<fresh mnbl_ name>`.
   - Then `Utilities.PairSceneNameToModuleName(name, module)` registers it, and `Utilities.TryGetFullFilePathOfScene` must resolve to the installed folder.
   - Names are immutable: never rewrite a loaded or previously loaded scene.
   - A navmesh and entities load from such a name in game (MMR-45: runs 22472, 9404, 43676).
3. **Game manager.** A `SandBoxGameManager` subclass. New game: `SandBoxGameManager(CampaignCreatorDelegate)`. Load: `SandBoxGameManager(LoadResult)`.
4. **Models** (from IL, not exercised: `src/` has no `AddModel` call).
   - Override `InitializeGameStarter`, call `base`, then append models through `CampaignGameStarter.AddModel`. The last assignable model wins.
   - At that point every `InitializeGameStarter` callback has run, NavalDLC's included, and NavalDLC has not yet taken its model snapshot (in `OnGameStart`).
5. **`OnGameStart`.** Remove `BackstoryCampaignBehavior` and `RetirementCampaignBehavior`; add `WorldIdentityBehavior`; add `PeriodicSeamBehavior` for periodic worlds.
6. **`RegisterSubModuleObjects(isSavedGame)`.** Dispatch every submodule, and replace SandBox's own call with the generated XML.
   - A new game loads all eight documents: SPCultures, NPCCharacters, Heroes, Kingdoms, Factions, WorkshopTypes, LocationComplexTemplates, Settlements.
   - A saved game re-applies only the five permanent-type documents, after `MBObjectBase.BeforeLoad` and before `UnregisterNonReadyObjects`.
7. **`OnGameInitializationFinished`.** After `base`, set `Campaign.MapSceneCreator` to the owned creator, before the map loads.
8. **`IMapScene` re-implementation** on the `SandBox.MapScene` subclass.
   - `Load` is the installed body with an owned `Scene.Read` target.
   - `AfterLoad` builds the navigation caches and the battle-index map.
   - Periodic worlds also re-implement `GetPathBetweenAIFaces` and `GetPathDistanceBetweenAIFaces`.
   - Callers that use these members through the interface reach the re-implementation.
   - **Callers that bypass the interface:** the map screen's native raycast and selection, and `Scene.DoesPathExistBetweenFaces` (`HandleLeftMouseButtonClick` IL_007c). `backend-callsite-matrix.json` counts 32 direct native call sites. They act on the same native scene and see generated geometry. MMR-66 made the strips' picks resolve canonically: a terrain click stores the canonical image, and a settlement's display copy resolves to the settlement (`docs/periodic-presentation.md`).
9. **Save identity.** `CampaignEvents.CollectMetadataEntriesEvent` writes the `mnbl.world.*` keys. `MBSaveLoad.GetSaveFileWithName(name).MetaData` reads them at the main menu, before `LoadSaveGameData`.
10. **Harmony** (Bannerlord.Harmony, decision 002). The periodic patches: MMR-63's eleven plus MMR-64's closest-settlement order and distance helpers (16 entries). They are preflighted before a periodic campaign starts, applied all or nothing only while a periodic world is loaded, and removed in `Destroy`.

### Native data contracts

- **Scene bundle:**
  - files `scene.xscene`, `terrain.bin`, `navmesh.bin`, `flora.bin` and `atmosphere.xml`;
  - entities `border_min` and `border_max`;
  - settlement entities with gate and port children, and banner placeholders each with one `volume_box` child;
  - no `MapSnowAndRainDefiner` and no `SettlementPositionScript`.
- **Navmesh face groups are `TerrainType` values.**
  - The campaign fixtures use Plain 1, Mountain 7 and CoastalSea 18, plus OpenSea 19 in the climate and later fixtures.
  - Steppe 5 and Lake 8 appear only as MMR-17 test stamps (the obstruction wall and the enclosed lake). They are not the product vocabulary.
  - The full mapping from generated biomes to face groups is MMR-26/38.
  - War Sails storms spawn only over OpenSea, so the generator must emit it.
  - The region map comes from `PartyNavigationModel` through `SetNavMeshRegionMap` before `Scene.Read`.
- **Terrain calibration (MMR-16):**
  - vertex heights are truncated toward zero at 0.03 m;
  - triangles split along the (x, y)–(x+1, y+1) diagonal;
  - the terrain size is the node count times the node size;
  - the origin is (0, 0);
  - exact bin-boundary rounding is unresolved.
- **Battle-index map (MMR-60):** 2 bytes per cell (scene index, packed patch coordinates), at the reader's own cell size, installed in `AfterLoad`.
- **Weather (MMR-59):**
  - wind: 512×512 (x, y) float pairs;
  - snow/rain: 32×32 (snow, rain) byte pairs, index row·dim + col from origin 0;
  - `Campaign.DefaultWeatherNodeDimension = 32`;
  - `Scene.CreateDynamicRainTexture(32, 32)` runs before `MapWeatherModel.InitializeCaches`.
  - **The binding writes private `SandBox.MapScene` fields.**
    - Their names and types are checked when the map loads, and every consumer is proven to read them.
    - This is accepted here as the diagnostic contract for continuing work.
    - Production must keep it behind the same load-time contract check, or replace it with a public path (`Scene.GetWindFlowMapData`, or a wind-only `INavalMapSceneWrapper` decorator). **MMR-39 decided: the private-field contract stays, behind the load-time checks.** No public write path exists, `Scene.GetWindFlowMapData` reads the authored flowmap, and a wind-only decorator would not reach the weather model's snow and rain (`docs/water-climate.md`).
- **Save identity (MMR-19):**
  - 13 `mnbl.world.*` metadata keys;
  - an immutable world package: `world.json` pins every hash, including the five scene files;
  - loading fails closed on any mismatch.

### Calling-thread and lifecycle rules

- Producer processes run off the game thread. Every engine, scene and campaign call runs on the game thread.
- All world binding (scene read, weather fields, region map, navigation caches, battle-index map) happens synchronously inside `IMapScene.Load`/`AfterLoad` on the campaign loading thread, before any campaign tick.
- Navigation and distance data are never mutated while campaign simulation runs: the installed movement uses `TWParallel` readers with no mutation contract.
  - The MMR-17 face-ability toggle broke this rule as a probe step only.
  - MMR-38 decided there is no runtime obstruction: barriers are static Mountain faces in the built navmesh (`docs/runtime-navigation.md`).
- Engine statics (`Campaign.MapDiagonal`, bounds, path-cost limits) keep the previous world's values until the next map load. Nothing may read them before `LoadMapScene`.
- **World switch.**
  - A normal `MBGameManager.EndGame` returns to the menu, and the next world binds everything in its own `Load`.
  - After the switch no previous-world managed object stays alive (MMR-59, three full collections).
- **Save names must be unique:** loading points the engine's quick-save slot at the loaded file.

### Object-loading sequence and mission/flow contracts

Order from `lifecycle-extension-contract.json`; the generated steps were observed in runs 24212, 31624 and 8256.

**New game:**
1. Generation, then scene install and registration (seams 1–2).
2. `StartNewGame` with the owned manager.
3. `Campaign.OnInitialize`: `InitializeGameStarter` (models), then `OnGameStart` (behaviours).
4. `OnNewCampaignStart`.
5. `RegisterSubModuleObjects(false)` loads the eight generated documents.
6. `OnGameInitializationFinished` sets the owned map-scene creator.
7. `LoadMapScene` → `Load`/`AfterLoad` binds the world.
8. The first tick.

**Load:**
1. The header identity is read at the menu (`GetSaveFileWithName`).
2. `GeneratedWorldPackage.Load` verifies every hash, including the installed scene files.
3. `WorldIdentity.RequireMatch`.
4. `MBSaveLoad.LoadSaveGameData` reads and deserializes the save. The object graph exists after this step (run 8256: `save_deserialized` at +22823 ms).
5. The owned manager is built from the successful `LoadResult` (`StartGeneratedLoad`).
6. `Game.LoadSaveGame` initializes the loaded objects. `Campaign.OnInitialize` runs `InitializeGameStarter` and `OnGameStart`.
7. `MBObjectBase.BeforeLoad`, then `RegisterSubModuleObjects(true)` re-applies the five permanent-type documents (`generated_registration_dispatch` at +24488 ms), before `UnregisterNonReadyObjects`.
8. `LoadMapScene` → the same `Load`/`AfterLoad` binding.
9. `OnLoadFinished`.

**Missions (MMR-60, R09):**
- **Battle scene choice.**
  - `MenuHelper.EncounterAttackConsequence` reads the battle-index map at the main party and calls `DefaultSceneModel.GetBattleSceneForMapPatch(patch, isNaval)`. NavalDLC does not replace the scene model.
  - The generated map must make each cell name exactly one scene of the right kind. A null or unknown index silently opens a random scene of the party's terrain.
  - Naval-capable cells are classified by the full set of navigable terrains, not only CoastalSea/OpenSea.
- **Mission terrain:** the stock battle scene's own terrain, chosen by generated geography. No campaign heightfield reaches a mission.
- **Settlement locations:** opened from the settlement prototype's scenes (`SceneHasMapPatch = false`). A generated-settlement mapping is MMR-42.
- **Return.**
  - A mission neither disposes nor recreates the map scene: the wrapper, native scene pointer, battle map, navmesh and settlements are identical before and after.
  - The party returns within vanilla `TeleportPartyToOutSideOfEncounterRadius` bounds (1.25 × the encounter radius: 0.625 on land, 1.875 at sea) and stays in its land/sea domain.

### Campaign navigation of compiled worlds (MMR-38)

The R07 native path carries the campaign navmesh: `RuntimeWorldBackend.Prepare` builds `navmesh.bin` from the bundle's travel cells. It has typed faces from the installed TerrainType enum, Bridge decks over UnderBridge faces, no faces beyond the poles, and periodic strips. The world is checked against the travel graph before encoding and through the engine once loaded (`world_navigation`, `evidence/world/navigation-routes.json`); a world that fails either check is refused. Engine facts observed there:
- the installed `MapScene.Load` order (disable the faces invalid for All, then read) does not disable faces read afterwards; campaign path queries exclude the invalid types explicitly;
- `FaceIslandIndex` is not All connectivity: a mountain-closed pocket shares its surroundings' island;
- `GetPathDistanceBetweenAIFaces` includes 50 per embark or disembark;
- a store-mode LZ4 navmesh with zeroed scratch fields loads.

### Periodic-world strategy (R10)

The requested capability is a whole-globe map with connected east/west edges. It is delivered on the selected backend in four layers. Run 12348 observed each layer except where an item is marked.

1. **Geometry.**
   - The terrain is twice the logical width, with W/2 strips that copy the opposite side.
   - Rendering and the camera therefore see continuous terrain at the seam; the camera followed across it in every player leg.
   - Native terrain picking in the strips was observed by MMR-66 (run 33944): a ray into the strip hits the twin terrain and its click orders the canonical point.
2. **Routing.**
   - `GetPathBetweenAIFaces`/`GetPathDistanceBetweenAIFaces` on the owned map scene answer with the cheapest placement (MMR-64 `PeriodicDistanceModel.ShortestOverImages`: three displacements by |D|, ties east; the start moved by whole widths when the target's copy lies outside the terrain).
   - The native pathfinder cannot cross the seam: a continent cut by it is two native islands. The strips are W/2 wide, so the image of every shortest seam-crossing route lies inside the terrain; a route the long way around starts from a moved start (MMR-64).
   - The settlement navigation cache is built through these queries, so settlement distances are periodic without replacing the distance model.
3. **Canonical state.**
   - A per-tick pass rebases parties that leave [W/2, 3W/2) by ±W and canonicalizes their order targets.
   - `ProcessTravel` and `FindReachablePointAroundPosition` return canonical points.
   - A save made 0.0002 from the seam deserialized with 0 non-canonical positions. Resuming from it is MMR-49.
4. **Planar checks made periodic.** The periodic Harmony patch entries (sixteen from MMR-63/64; twenty with MMR-65's placements and spatial table; 52 with MMR-66's presentation entries when War Sails is installed) cover locatable search, interaction, visibility, encounter direction, distance models, placement, the closest-settlement order and the distance helpers (MMR-63, MMR-64).

What remains is owned, not optional:
- **MMR-65 (done):** route execution and diagnostics, the periodic radius query, the audited spatial consumers, armies and fleets across the seam (`docs/periodic-movement.md`); sieges, raids and coastal embarkation at the seam are audited, not run in game;
- **MMR-66 (done):** one canonical camera, the display rule for visuals, decals and nameplates, settlement display copies bound to canonical identities and native picking in the strips (`docs/periodic-presentation.md`); blockades, storms and anchors at the seam have no in-game samples;
- **MMR-64 (done):** one tie rule, the distance-surface and helper coverage, battle-map wrapping, pole identity and the accepted polar distortion (`docs/periodic-coordinates.md`); the planar consumers outside the distance surface are MMR-65's;
- **MMR-49:** the save-instant window and resuming a periodic save;
- **MMR-53:** a naval encounter at the seam, with naval gameplay.

A failure of any of these is a failed requested capability. Disconnected edges are never a substitute.

### Minimum viable campaign (tested configuration, from MMR-18)

- **Tested configuration, not a probed lower bound:** per world, two towns (each with a port) and two villages bound to them; generated kingdoms, clans, rulers and the player; 19–21 heroes.
- **Retained from installed content:**
  - 33 engine-literal NPC templates, referenced by literal id in installed IL;
  - installed culture, troop, item and template records, with recorded provenance.
- **Excluded:** official heroes, settlements, clans and kingdoms (0 survive).
- **Not yet validated:** castles, bandit clans and hideouts (MMR-33/34).

### Coordinate and topology conventions

- World units: +X columns, +Y rows, origin (0, 0). Positions are `CampaignVec2` with an explicit land/sea flag.
- **Periodic worlds (MMR-63):**
  - logical x ∈ [0, W) lives at physical x = logical + W/2, in a terrain 2W wide with W/2 strips copying the other side;
  - campaign state is canonical in [W/2, 3W/2);
  - images exist only in path queries and display.
- **Poles.**
  - The fixture keeps the source's single-node pole aliases as one row each, and pads above and below with the pole rows' ocean, so poles are neither connected nor cropped.
  - No probe checked pole identity; it is unresolved (MMR-64).
  - East–west map distance overstates sphere distance by 1/cos(latitude): ×2.0 at 60°, ×5.76 at 80°, unbounded at the pole row. The production generator needs a projection or explicit technical acceptance of this (MMR-25 records the mapping; MMR-64 owns the contract).
- Settlements are placed only at canonical positions. Display copies carry no campaign identity.

### Compatibility fingerprints

- Game version `v1.4.8.119303`.
- Module assembly hash, recorded in every world package.
- Harmony 0Harmony 2.4.2.0 (`config/runtime-api-lock.json`).
- NavalDLC model types, checked at load.
- Producer and encoder hashes: `tools.lock.json` and `worldbridge.sha256`, also recorded in each package's build receipt.
- The scene-table SHA-256 inside the battle-index identity.

### No per-seed editor dependency

- No editor is involved anywhere. Generation and surface encoding run in the game (R01/R02).
- **Not yet in-process: campaign-scene composition** (settlement entities, navmesh face groups including OpenSea, climate fields, periodic strips).
  - Today the offline builder `tools/scenebin/*.py` does it. Every campaign probe loaded its output through slots installed before launch.
  - Moving it into the in-game pipeline is MMR-26/28/29/40.
  - MMR-45 joins runtime registration and campaign boot.
- Until then the combined path (generate in game, register at runtime, boot the campaign) is **unresolved**.
  - Observed: terrain rendering and queries on a scene registered at runtime (R02), and a campaign on installed slots (R03–R10).
  - Not observed: navmesh and settlement entities loaded from a runtime-registered scene.

## Workarounds in use

**Product path:**
- **Behaviours removed in generated mode:** `BackstoryCampaignBehavior` and `RetirementCampaignBehavior` (hard-coded official heroes and `retirement_retreat`).
- **33 engine-literal NPC templates** retained as dataset roots; `fighter_sturgia` excluded.
- **Banner placeholders** get a `volume_box` child: a childless one faults natively when a lord is inside a town.
- **Private-field writes on `SandBox.MapScene`:**
  - `_scene`, the bounds caches and `_terrainSize`;
  - `_battleTerrainIndexMap` with its width and height;
  - `_windFlowMapData`, `_snowAndRainData` and `_snowAndRainDataTextureWidth/Height`.
- **Fifty-two periodic Harmony patch entries with War Sails** (MMR-63 table, MMR-64 additions, MMR-65's canonical placements and spatial table: 98 methods transpiled, 27 callers recompiled; MMR-66's 32 presentation entries on SandBox.View, SandBox.ViewModelCollection and NavalDLC.View, reached by name). MMR-65's summarizer needs `--patches 52` for runs from this build. `LocatorGrid<T>` and the generic `NavigationCache<T>` methods cannot be patched; direct grid searches go through wrappers. A world whose pole margin is not above 101 units is refused (the periodic measures tell positions from offsets by it).
- **The world package and identity** are written for every generated world at creation.

**Probe-only setup (not product behaviour):**
- synthetic wars and relocations;
- ship and crew grants;
- healing after fast-forward;
- `RestartPlayerEncounter` battle entry and a forced `PlayerEncounter.Finish(true)`;
- caravans excluded from scripted naval AI;
- `SetDoNotMakeNewDecisions` with scripted move orders;
- the face-ability toggle plus `RefreshFaceCaches` (MMR-17);
- `DiagnosticNoAutoSaveManager` (autosaves disabled);
- re-pausing to hold the map paused (HoldPaused).

## Native API dependencies

The installed members the backend calls, by group. Signatures are recorded in several places, not one pinned inventory:
- **Scene, navigation and weather:** `evidence/runtime/api-signatures.txt` and `api-inventory.json`.
- **Scene registration:** `map-scene-trace.json` and `surface-coverage-review.json`.
- **Load order:** `lifecycle-extension-contract.json` and `save-load-order.json`.
- **Harmony targets:** `world-wrap-probe.json` `patches[]`.
- **Save metadata calls:** `CollectMetadataEntriesEvent` and `GetSaveFileWithName` are used only from source (`WorldIdentityBehavior.cs`, `Diagnostics/WorldSaveProbe.cs`) and are not yet in a signature inventory. A pinned inventory of every member below belongs with the version-compatibility checks (MMR-50).

| Group | Members |
|---|---|
| Scene | `Scene.CreateNewScene`, `Read`, `GetTerrainData`, `SetNavMeshRegionMap`, `SetOcclusionMode`, `GetFirstEntityWithName`, `OptimizeScene`, `CreateDynamicRainTexture`, scene XML and navigation-mesh CRCs. `SetAbilityOfFacesWithId` is probe-only |
| Registration | `Utilities.PairSceneNameToModuleName`, `TryGetFullFilePathOfScene`, `SetAllocationAlwaysValidScene` |
| Map scene | `MBMapScene.LoadAtmosphereData`, `ValidateTerrainSoundIds`, `MBAgentRendererSceneController` |
| Campaign | `MBObjectManager.LoadXml`, `SandBoxNavigationCache.GenerateCacheData`, `MapDistanceModel.RegisterDistanceCache`, `CampaignEvents.CollectMetadataEntriesEvent`, `MBSaveLoad.GetSaveFileWithName`, `MBSaveLoad.LoadSaveGameData`, `MBGameManager.StartNewGame`/`EndGame` |
| Private fields (`SandBox.MapScene`) | as listed under "Workarounds" |
| Harmony targets (11) | `MobileParty.FindNextLocatable`, `Settlement.FindNextLocatable`, `PartyBase.IInteractablePoint.CanPartyInteract`, `PartyBase.CalculateVisibilityRangeOfMapPoint`, `MenuHelper.EncounterAttackConsequence`, `MapEvent.GetEventDirection`, `DefaultMapDistanceModel.GetDistance`, `NavalDLCMapDistanceModel.GetDistance`, `NavigationHelper.FindReachablePointAroundPosition`, `MapState.ProcessTravel` (prefix and transpiler) |
| War Sails (reflection) | `NavalDLCManager.Instance` (a static field), `StormManager`, NavalDLC models |

## Still unsupported or unresolved mandatory operations

| Operation | State | Reproducer or source | Dependent scope |
|---|---|---|---|
| Load navmesh and settlement entities from a scene registered after launch, then boot a campaign in one pass | passed (MMR-45): world A's scene, installed and registered by the generation job in the running game, boots a campaign with its navmesh (navigation caches built over it) and every baked settlement entity (none made at run time): world_activation runs 22472, 9404, 43676. Historical fresh-name navigation imports had returned 0 faces | PIDs 29364 and 14300; `backend-callsite-matrix.json` `explicitUnknowns` | MMR-45, with MMR-29/40 for in-process composition. **Bounded fallback:** a pool of slot names indexed at startup, each written once before its first load (PID 25128 imported rewritten bytes from such a name) |
| Load a generated save from the normal menu | passed (MMR-49): a generated save is routed to the module's world loader, which checks it before anything is deserialized; any other path that would read it is refused (`LoadRouting`) | `docs/world-loading.md`; `evidence/persistence/two-world-matrix.json` (runs 48048, 39860, 28224) | none |
| Alternating loads in one process and after a cold restart | passed (MMR-49): world A and the MMR-19 fixture world, alternately loaded in one process and after a cold restart | `evidence/persistence/two-world-matrix.json` | none. The second world is a fixture: only seed 42 activates (`evidence/one-shot/acceptance-index.json` s6) |
| Re-save after load | published and header-verified (MMR-50 run 24328); loading that re-save again was not run | `evidence/persistence/compat-matrix.json` | none |
| Autosave and failed save with the identity | passed (MMR-48): every save kind, autosaves included, carries the binding and is journaled | `docs/save-binding.md`; `evidence/persistence/save-binding.json` | none |
| Resume a periodic save; cold-load a periodic world | passed on world A (MMR-49): a seam crossing, a pursuit across the seam and a fleet called across it survive save and a cold load | `evidence/persistence/two-world-matrix.json` (run 48048, cold run 39860) | none |
| Canonical state at the save instant; `BehaviorTarget` | implemented (MMR-49): `PeriodicSeam.CanonicalizeForSave` runs before every save | `docs/world-loading.md` (Canonical positions at the save) | none |
| Storm and weather wrap across the seam | implemented (MMR-39); the planar storm checks near the seam are transpiled and strip-centred searches get their image pass (MMR-65; the strip-centre query is tested, storm damage was not rerun) | `evidence/world/water-climate.json` (run 27432); `evidence/world/periodic-consumers.tsv` | none |
| Sieges, raids and embarking or disembarking at a coast at the seam | not run in game; their spatial calls are audited and transpiled (siege camp positions, raid and patrol searches, transition points) | `docs/periodic-movement.md` limits | MMR-42 (sieges with settlements), MMR-53 (naval) |
| Naval encounter at the seam | not run | MMR-63 limits | MMR-53 |
| Native terrain picking at the seam; map labels, selection, display copies; the camera fields | implemented and observed (MMR-66) | `evidence/world/periodic-camera-picking.json` (run 33944) | none |
| Mouse dragging across the seam; blockade, storm and anchor visuals at the seam; encyclopedia map navigation | implemented, not observed in game | `docs/periodic-presentation.md` limits | MMR-53 (blockades and storms) |
| Siege machines on a settlement's display copy; distance-sorted interface lists near the cut | not drawn; planar | `docs/periodic-presentation.md` limits | MMR-42 (sieges) |
| Routes whose path leaves the terrain; a small table method inlined into code compiled before the patch under a large `AggressiveInlining` caller | not representable; not covered by the recompile closure | `docs/periodic-coordinates.md`; `docs/periodic-movement.md` limits | none |
| Location scenes for generated settlements; Mountain/Forest/Snow battle scenes | prototype scenes and substitutions | MMR-60 limits | MMR-42 |
| Weather binding through a supported path | private-field contract kept as the production path (MMR-39 decision) | `docs/water-climate.md` | none; MMR-45 binds production worlds at activation |
| Terrain snow cover from the generated field | unsupported: no safe texture-creation path proven; snow weather and season scaling work (MMR-39) | MMR-59 limits, `docs/water-climate.md` | none (cosmetic) |
| Runtime navigation obstruction | not provided: barriers are static Mountain faces (MMR-38) | `evidence/world/navigation-routes.json` (fixture barrier and ring) | none |
| Combined terrain-node exports | native crash | PID 27392 | none (no consumer; withheld) |
| Native resource release on world switch | unverified (not observable from managed code) | — | MMR-49 |

## Provisional limits

These figures come only from telemetry the accepted probes already recorded. No benchmark was run for this ticket.
- **The full extraction** is `task-notes/mmr21-telemetry.md`. It gives every field path and log line, and marks each value as recorded (R) or derived from two log timestamps (D).
- **Single samples.** Each value is one sample on one machine. The closest repeats share a world id but not always the same scene files.
- **These are provisional.** MMR-56 has since measured the real first-test profile end to end (`docs/first-test-profile.md`, `worldgen/validation/first-test-measurements.json`); MMR-57 owns packaging.

**Machine and settings.**
- No accepted probe records hardware or settings. The values below come from the MMR-13 baseline on the same machine (`evidence/baseline/performance.json`, `environment.json`). Nothing proves the settings were unchanged since then.
  - Intel Core Ultra 9 275HX (24 cores), 31.4 GiB RAM.
  - NVIDIA RTX 5080 Laptop GPU (driver 32.0.16.1692), desktop 2560×1600.
  - Game display 1600×1024 (mode 1, 240 Hz, resolution scale 100).
  - Shadowmap type 2, filtering 1, resolution 3.
  - Antialiasing technique 5.
  - Texture quality 2, budget 3, filtering 5.
  - Max framerate 200.
  - Engine config sha `396d1b91b5d086e8`.
- Game v1.4.8.119303 and NavalDLC v1.2.8.119303.
- Producer `fwg.exe` `564c2629b5b92d49`.
- Game start to main menu took 19.5–19.7 s in all 12 runs.

**Input hashes per run** (module DLL sha → worlds by placement or request sha):

| Run | Ticket | Module | Worlds |
|---|---|---|---|
| `generation_menu-19240` | MMR-15 | `e326e3fe67cb64ac` | seed 42 request `f204b0ce8655b7ed`, seed 43 `e8c99292dcdedd03` |
| `runtime_surface-32896` | MMR-16 | `c7a353ae2045f579` | calibration source `69d2cb897d791145`; seed 42 and 43 heightfields `14c4e157ef3bb26e` / `e15ff0e2b4edd91a` (the MMR-15 outputs); encoder `21da9dc8e8a66eb6` |
| `generated_campaign-2248` | MMR-17 | `5e28cde8292a6515` | 2a `08eb6aa6cd1446a4`, 2b `e71b1e6035dc932e` |
| `generated_campaign-31308` / `24736` | MMR-18 | `0c1c1e3774f3ae0f` / `ded4381acbf25d65` | 2a `008a5d0074bd371a`, 2b `b9a93238675fff91` (31308 only) |
| `generated_campaign-23192` | MMR-18 | `3ab256efe35c0a13` | 2a `963802f2c06d4cd4`, 2b `e635214de6567b75` |
| `generated_campaign-22868` | MMR-60 | `19fa06b01cb9813e` | 2a `08eb6aa6…`, 2b `e71b1e60…` |
| `generated_campaign-12348` | MMR-63 | `824066136f7a4259` | 63042 `0702c9f208702939` (periodic, 131,072 faces) |
| `generated_campaign-24212` | MMR-59 | `174fcb0a319dce03` | 59042 `a48041c56615c00f`, 59043 `984d904a768067ef` |
| `generated_campaign-31624` → `generated_save_load-8256` | MMR-19 | `174fcb0a319dce03` | 59042 `a529b12851ad884d`, 59043 `1683f798fceef638` |

All campaign runs used catalog receipt `27bca57ee00ae08a…`.

**Stage durations.**

| Stage | Observed | Notes |
|---|---|---|
| Generation in game (size 129, MMR-15) | 11.2 s (seed 42) and 12.7 s (seed 43) per job: `fwg.exe` 5.5–6.6 s, then the worldbridge reader 5.7–6.1 s | The game kept ticking: maximum tick gap 0.007–0.071 s. Offline (MMR-24, request `9e49bb57af34faf2`), generation producing the same `world.json` took 5.1–8.6 s depending on thread count |
| Conversion (surface encoder, in game) | 72–87 ms | The offline fixture encoder is not timed |
| Runtime scene install, register, read (seed 42, MMR-16; step-start markers) | file install 31 ms; register and resolve 21 ms; `Scene.Read` 47 ms; optimize ≤ 20 ms | |
| Offline campaign-scene construction (`tools/scenebin`) | **not recorded** | The receipts have no timestamps. The stage moves in game under MMR-26/28/29/40, which must time it |
| Navigation caches (Default, Naval, All) per world | 32,768 faces, 800 m: 34.7–36.0 s. 32,768 faces, 640 m: 71.5–74.5 s. Periodic, 131,072 faces: **566.8 s** (All alone 419.0 s; 565.8 s under MMR-41, run 6152) | With the same face count the 640 m worlds take 2.1× longer (Naval 4.2×). Since MMR-41 (`docs/playability.md`) they are built once per world and keyed input set, published, and read back on later loads: 21 ms on a fixture (run 32740) and 95 ms on the periodic fixture (run 35828). The real-scale cost (hundreds of settlements) is unmeasured until MMR-45/MMR-56 |
| Campaign bootstrap (loading screen → map active) | new game 42.1–43.7 s (800 m) and 79.6–82.8 s (640 m); periodic new game **575.4 s**; save load 39.7 s and 75.0 s | Navigation is 82–98.5% of it. Loading screen → owned creator installed (all submodule registration and campaign initialization; on load also package verification and deserialization): 2.0–3.9 s. The whole `IMapScene.Load` (scene creation and read, weather, caches, optimize): 0.13–0.41 s, which includes weather setup (0.04–0.24 s). Battle-index map: 0.05–0.34 s |
| Load-only steps (8256) | 22 ms to verify the world package, 215 ms to deserialize | |
| Mission load (MMR-60, MMR-63) | field 0.94–1.80 s, settlement 2.20–2.21 s, naval 1.46–1.49 s, seam battle 1.72 s; teardown 0.32–0.47 s | Up to 84 agents and 2 ships |
| Save | 0.289 s (MMR-63, near the seam) | The MMR-19 save duration is **not recorded** |
| Campaign throughput | 2.0 days in 40.4 s at UnstoppableFastForward (23192); 1.0 day in 80.0 s at UnstoppablePlay (24736) | Frame time on the generated map is **not recorded** |

**Memory.**
- Child processes (Windows job peak working set): `fwg.exe` 253–294 MiB in game (62–236 MiB offline), worldbridge reader 26 MiB, surface encoder 48 MiB.
- **Game-process peak RAM** was not recorded by the probes this section cites. MMR-56 measured it on the F03 profile: a 5,139-5,209 MB working set and 7,475-7,543 MB of private bytes at their peaks (`docs/first-test-profile.md`).

**Scene resources and sizes.** Sizes are on disk; in-memory sizes are not recorded. Entity counts are authored counts from the build receipts and the xscene, not engine readback; MMR-16 saw engine-created entities beyond the authored ones.

| World | Authored entities | Nav faces | `terrain.bin` | `navmesh.bin` | `scene.xscene` |
|---|---|---|---|---|---|
| 800 m and 640 m fixtures (2a, 2b, 59042, 59043) | 76 | 32,768 (16,641 vertices) | 4,435,680 B | 1,414,542–1,419,667 B | 70.7 KB |
| Periodic fixture-c (63042) | 80 (4 display copies) | 131,072 | 5,024,609 B | 4,977,623 B | 71.8 KB |

- Climate fixtures add `climate-wind.f32` (2,097,152 B) and `climate-precipitation.u8` (2,048 B).
- Generator output per world: 141.7 MB (seed 42; `world.json` 82.8 MB) and 167.0 MB (seed 43). It is staging data; the campaign does not load it.
- In game, every world had 4 settlements, 3 clans, 2 kingdoms, 19–21 heroes, 17 mobile parties, and 905–907 characters across 17 cultures in `MBObjectManager`.

**Save size.**
- MMR-19 saves: 121,809 B (sha `5b6b721350dd4818`) and 121,220 B (`8e0b83844fac7cd3`).
- World packages: 3,599,637 B (manifest `8828c934b3b50901`) and 3,599,671 B (`c25c9d9d4d269573`), 13 files each. `NPCCharacters.xml` (1.43 MB) and the wind field (2.10 MB) make up most of it.
- The MMR-63 save size is not recorded: that file was deleted.

**Provisional limits derived from these figures.** MMR-56's measurements supersede them: `worldgen/policies/one-shot-budget.json` (`provisionalLimits`) and `docs/first-test-profile.md`.

1. **The navigation cache build is the binding cost.**
   - In the one pair available, 4× the faces took 16× the time. That pair also differs in geography, so the scaling law is not established.
   - The same face count took twice as long at 640 m as at 800 m.
   - A periodic new game at 131,072 faces took 9.6 minutes.
   - MMR-41 publishes each world's caches under a key of every input, and later loads of an unchanged world read them back: 95 ms on the periodic fixture instead of 566 s (runs 6152 and 35828). The first campaign start of a world still pays the build.
   - Provisional limit: worlds of 32,768 faces, at most 75 s for the first load, and a periodic fixture-sized world about 9.5 minutes for its first load only.
   - At real settlement counts MMR-56 measured world A's first start at 382-391 s of cache generation, with the engine thread blocked for 383 s (the game does not respond). This is a performance limit, not a failed capability.
2. **Generation:** about 13 s and 300 MiB of producer memory at size 129, with about 170 MB of staging output per world.
3. **Per-world disk:** about 3.6 MB of package plus about 6–10 MB of scene files, outside the save. Each save is about 120 KB.
4. **Game-process memory and frame time:** MMR-56 measured them (the map at a 60 fps cap; the game's peaks above). In-memory geometry is still unmeasured.

## Probe components to promote

The ticket asks that working probe code be promoted rather than rebuilt. The production owners take these components as their starting point:

| Diagnostic component (`src/MNBL.ProceduralWorld/`) | What it proves | Promoted by |
|---|---|---|
| `Automation/RuntimeSurfaceSequenceProbe.cs` (scene install and runtime registration) and the worldbridge child-process driver | R02 | MMR-37, MMR-44 |
| `CampaignBootstrap/DiagnosticMapScene.cs` (including `DiagnosticMapSceneCreator`) | R02, R03, R10 map-scene contract | MMR-37, MMR-38 |
| `CampaignBootstrap/DiagnosticSandboxBootstrap.cs` (new-game and load managers) | R04, R05 | MMR-30, MMR-45, MMR-49 |
| `CampaignBootstrap/GeneratedCampaignDataset.cs`, `GeneratedCampaignRegistration.cs` | R04 | MMR-30, MMR-31–36 |
| `CampaignBootstrap/GeneratedBattleTerrain.cs` | R09 | MMR-42 |
| `CampaignBootstrap/GeneratedClimate.cs`, `GeneratedMapSupport.PrepareWeather` | R08 | MMR-39 (done: schema v2, `FromSynthesis`, colour grade); MMR-45 (production activation) |
| `CampaignBootstrap/PeriodicWorld.cs`, `PeriodicSeam.cs`, `PeriodicPatches.cs` | R10 | MMR-64, MMR-65, MMR-66 (done) |
| `UI/PeriodicMapCameraView.cs`, `World/PeriodicMapPresentation.cs` | R10 presentation | MMR-66 (done); MMR-40 builds settlement entities the display copies clone |
| `CampaignBootstrap/PeriodicSpatialQueries.cs`, `PeriodicSpatialTable.cs` (generated by `tools/periodic/`), `PeriodicRouteExecutor.cs` | R10 | MMR-65 (done); MMR-41 reads the route diagnostics; MMR-66 (done) the view sites |
| `CampaignBootstrap/WorldIdentity.cs`, `WorldIdentityBehavior.cs`, `GeneratedWorldPackage.cs` | R05 | MMR-62 (done: binding keys, resolution by id and hash; `Persistence/` holds the journal, index and cleanup), MMR-48, MMR-49 |
| `tools/scenebin/*.py` (offline campaign-scene composition) | fixtures for R03–R10 | moved in-process by MMR-26, MMR-28, MMR-29, MMR-40 (done: settlement entities, gates, ports, blockade arcs and siege geometry baked by `World/SettlementEntityFactory.cs` from `World/SettlementPositionResolver.cs`, checked in `RuntimeStage` by `World/SettlementEntityChecks.cs`; `docs/settlement-entities.md`) |

The probes themselves (`Diagnostics/*Probe.cs`) and the summarizers stay as regression harnesses.

**Promoted by MMR-37 (2026-10-15, `docs/runtime-surface.md`).**
- `DiagnosticMapScene` and its creator are now `World/GeneratedMapScene.cs` (`GeneratedMapScene`, `GeneratedMapSceneCreator`), with guarded height, normal, bounds, map-patch and face-height members and a generation token.
- The runtime encoding and installation sequence is reimplemented in `World/RuntimeWorldBackend.cs`. It builds worlds from MMR-29 bundles and adds content-addressed scenes, generations, a resource ledger and disposal.
- `RuntimeSurfaceSequenceProbe` stays as the R02 regression harness.

## Release of dependent work

MMR-21 is a prerequisite of three tickets. Its acceptance satisfies that prerequisite with no human gate:

| Ticket | Effect of this acceptance |
|---|---|
| MMR-25 (world presentation and coordinate transform) | ready: its other prerequisites (MMR-11, 17, 23) are accepted |
| MMR-29 (immutable data bundles) | still waits on MMR-26, 27 and 28 |
| MMR-30 (campaign-scoped identities) | still waits on MMR-27 |

Each unresolved item above blocks only its named owner.
