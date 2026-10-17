# Settlement entities, gates, ports and blockade geometry (MMR-40)

Every campaign settlement of a compiled world gets one scene entity, named its id, with the hierarchy the installed campaign map gives its kind: siege camps, siege engines, breachable walls and wall engines per wall level, ports, blockade arcs and drop-offs. The entities are baked into the world's scene before it is encoded, and checked in the engine before the world can be activated.

Their positions come from one resolved placement, the settlement layout. It is resolved against:
- the world's navigation: MMR-38's points are binding;
- the world's surface (W01).

The settlement records are bound to the same layout (`SettlementFactory.ApplyResolvedPositions`, checked by `CheckResolvedPositions`).

| Piece | File |
|---|---|
| Placement (sites, ground, policy, resolver, layout, blockade ship replay, refusal) | `World/SettlementPositionResolver.cs` |
| Hierarchy and scene XML, scene levels, installed contract, structural validation | `World/SettlementEntityFactory.cs` |
| In-engine check of a read world scene (production and the world_surface probe) | `World/SettlementEntityChecks.cs`, called by `Generation/GenerationStages.cs` (`RuntimeStage`) |
| Live hierarchy for a settlement the scene lacks (MMR-34 hideouts) | `World/SettlementEntityRuntime.cs`, `World/GeneratedMapScene.cs` (`AddNewEntityToMapScene`) |
| Siege prefab cache for the campaign map | `World/GeneratedMapScene.cs` (`AddSiegePrefabCache`) |
| Production integration | `World/RuntimeWorldBackend.cs` (`Prepare`, `Sweep`) |
| Records | `Campaign/SettlementFactory.cs` (`ApplyResolvedPositions`, `CheckResolvedPositions`), `SettlementLayout.Positions` |
| Placement policy | `worldgen/policies/settlement-placement.json` version 2 (`tools/make_settlement_placement_policy.py`, from the installed War Sails campaign map); the blockade arc and script parameters come from `worldgen/policies/naval-content.json` (MMR-35) |
| Fixture worlds | `tools/settlements/FixtureSettlementEntities.cs`, `tools/Build-FixtureSettlements.ps1` (run after a scenebin build, before `Deploy-SceneSlots.ps1`) |
| Offline tests | `tests/World/SettlementPlacementTests.cs`, `tools/Test-SettlementPlacement.ps1` |
| Campaign probe | `Diagnostics/SettlementEntityProbe.cs` (generated_campaign `settlementEntityProbe=1`, `factoryEntities=1`) |
| Evidence | `evidence/world/settlement-position-report.json` (both worlds' layouts with provenance), `evidence/world/settlement-placement-tests.json` and `.txt`, `evidence/world/settlement-entities.json` (in game), `evidence/fwg/settlement-entities-review.md` |

## The policy

`tools/make_settlement_placement_policy.py` measures the installed War Sails campaign map (`Modules/NavalDLC/SceneObj/Main_map/scene.xscene`, hashed into the policy as `source.sha256`). Every child transform is composed with its parents (position, `rotation_euler` as `Mat3.ApplyEulerAngles`, scale) and expressed in its settlement's gate frame: forward toward `main_map_city_gate`, side to its right, from the centre. Each value is the median over the installed towns and castles:

- gate distance 2.5973;
- the gate-frame markers: outer gate, two siege camps, siege engines 0 to 3, ram, siege tower (the second tower mirrors the first);
- per wall level 1 to 3: two breachable wall sections (slot 0 near (1.66, -0.95), slot 1 near (1.65 to 1.81, 0.91 to 1.01)) with their scene levels `[level_N, civilian, siege, looted]`, the solid and broken wall meshes (`mi_emp_wall_3s`, `mi_emp_wall_2s_collapsed`);
- per wall level: four wall engines (12 slots) with their scene levels `[level_N, civilian, siege]`;
- the fortification circle scale (town 6.994, castle 3.191) and the camp-area scales (`map_camp_area_1` 1.601 x 2.395, `map_camp_area_2` 2.724 x 2.174);
- the port and drop-off reach (port centre maximum 6.9575, port gate maximum 12.1941, drop-off maximum 13.9746);
- the ground rules: pad 25 degrees, marker 35 degrees, cell interior 0.1, minimum separation 0.5, turn step 22.5 degrees.

`RuntimeWorldBackend.Prepare` and the fixture tool refuse a policy measured on another campaign map than the installed one (`SettlementPlacementPolicy.RequireSource`).

## The layout

`SettlementPositionResolver.Resolve` works in logical positions. Physical positions are `PeriodicWorldCoordinates.ToPhysical` (MMR-64): canonical, inside the terrain. Each field below is resolved by its own rule.

**Sites.**
- The bundle's towns, castles and villages come from the travel graph; port towns from the MMR-28 port transitions.
- Harbour villages are the campaign's harbour flag (`compiled/campaign/campaign.json` `villages[].harbour`), each with the harbour cell of any port candidate naming it (`compiled/travel/ports.json`). This is the rule `SettlementFactory` gives harbour villages ports by: 80 on world A, 105 on world B. A harbour village without a harbour cell is refused.
- Hideouts are placed by MMR-34 from the campaign build, so they are not in a compiled bundle. They can be given as sites with a centre.

**Centre.** The settlement's navigation point, resolved and separated (MMR-38). It must be passable land. A pad steeper than 25 degrees is reported, and the centre does not move.

**Gate (towns and castles).**
- It stands at the installed gate distance, facing the road to the nearest connected settlement.
- It must stand on passable land (not mountain) of the centre's land component, within 35 degrees of slope, at least 0.5 from every other settlement's centre and every marker already placed for another settlement.
- Candidates turn about the centre in 22.5-degree steps, then shrink toward it by quarters.
- Where no candidate meets the slope limit, the least steep valid one is taken, reported, and named in the gate's rule. Generated terrain is far steeper than Calradia's: pad slopes reach 60 to 70 degrees at the 99th percentile on worlds A and B.

**Gate-frame markers (towns and castles).** The outer gate (`main_map_castle_gate`), siege camps (`map_camp_area_1/2`), siege engines 0 to 3, ram and two towers. Each is placed at its installed offset in the gate frame, then adjusted like the gate.

**Walls and wall engines (towns and castles).** Per wall level, two breachable wall sections and four wall engines at their installed offsets in the gate frame. They need land of the centre's component and the same clearance from other settlements; no slope limit (installed walls stand on slopes).

**Port (port towns).**
- It is the navigation port point (MMR-38: the port cell's interior point nearest the gate).
- It must be water of the transition's basin, within the travel graph's port-to-gate limit.
- A centre distance beyond the installed maximum is reported.

**Blockade arc (port towns).**
- MMR-35's installed arc offsets apply in the port's seaward frame.
- The search turns in 15-degree steps round the full circle, mirrors, moves the frame seaward by half a cell and by a cell, then shrinks to 3/4 and to 1/2. A mirrored arc swaps its start and end, so the arc keeps its handedness (`FindCenterOfCircle` puts the centre to the right of start to end).
- An arc is accepted only when every ship position `BlockadePositionScript.GetBlockadeArc` would compute for `MaximumNumberOfShips` is water of the port's basin. The script is authored with randomization off (MMR-35), so these are the positions War Sails draws. `SettlementPositionResolver.BlockadeShips` replays the installed 1.4.8 IL with randomization off: `FindCenterOfCircle(end, start)`, arcs from `NumberOfArcs` down, `round(Angle * k / DistanceBetweenShips)` ships per arc (banker's rounding, the step widened when the remaining ships run out), a half step first, then `DistanceBetweenShips / k` apart, turning counterclockwise (`Vec3.RotateAboutZ`).
- MMR-35 fitted the arcs from the staged sea-cell port, a cell further out than the placed port. Hence the seaward move.
- A port where none fits keeps its port but gets no arc and no `BlockadePositionScript`, and is reported. War Sails then draws no blockade ships, and never draws ships on land.

**Drop-off (harbour villages).** The interior point of the harbour cell nearest the village. It must be water of the candidate's basin (or, for a candidate without one, of the cell's).

**Refusals.** Raised in `RuntimeWorldBackend.Prepare`, before anything is encoded; the refused layout is persisted as `<store>/world-settlements/refused-<world content id>.json`. `GenerationRuntime` classifies them as runtime capability failures. A refusal is any of:
- a centre off land;
- a port or drop-off that is not water of its basin;
- a port beyond the gate limit;
- no gate or marker position on the settlement's land clear of other settlements;
- a harbour village without a harbour cell;
- two settlements at one place.

**Determinism and provenance.**
- Every position records its rule and its before and after; every marker its unadjusted position (`from`).
- The report gives each position canonically (`at`) and where the scene builds it (`scene`: the image beside its settlement's centre, so a child across the periodic cut stands in the strip).
- The layout's hash binds it.
- The report is persisted as a derived world artifact at `<store>/world-settlements/<scene>.json`. `RuntimeWorldBackend.Sweep` removes the reports of the scenes it sweeps and every refused report.

## The hierarchy

Every child is named `<id>_<suffix>`. Every gate-frame marker and wall faces the gate: an entity's forward is +Y, so its yaw is the gate angle less a quarter turn.

| Kind | Root | Children |
|---|---|---|
| town | tag `town`; `Town Entity Manager`; `BlockadePositionScript` on a port town with an arc | icon (`mi_emp_keep_1`), selection (`bo_town`, raycast-only sphere), circle (`map_settlement_circle`, installed scale), 4 banner placeholders (`map_banner_placeholder`, each with its one editor-only `volume_box` child; a childless placeholder faults natively, MMR-33), gate (`main_map_city_gate`), outer gate, siege camps (installed scales), siege engines 0 to 3, ram, 2 towers; per wall level 2 breachable wall sections (`map_breachable_wall`, each with a raycast-only collider, a `map_solid_wall` and a `map_broken_wall` child) and 4 wall engines (`map_defensive_engine_0..3`), each with its level's scene levels; port (`main_map_city_port`, a direct child), arc ends (`Blockade_Arc_Start/End`) |
| castle | tag `castle`; `Town Entity Manager` | the same without port and blockade |
| village | tag `village`; `Town Entity Manager` | icons per level (`village_empire_1..3` with `[level_N, civilian]`) and a looted icon (`village_empire_1` with `[level_1..3, looted]`), selection (untagged raycast-only sphere, as installed), circle, 1 banner placeholder, drop-off (`main_map_village_dropoff`) on a harbour village |
| hideout | tag `wm_hideout`; sphere collider; `Town Entity Manager` | icon (`worldmap_hideout_a`) |

**Scene levels.** The generated template declares only `base` and `level_1..3`. `SettlementEntityFactory.DeclareSceneLevels` adds the installed campaign map's `civilian` 16, `siege` 32 and `looted` 64. `SettlementVisual.RefreshLevelMask` asks the scene for them, and `SetSettlementLevelVisibility` shows a child whose level mask holds the visual's whole current mask. So exactly one village icon shows in each state, and each wall level's sections show in civilian and siege. `UpdateDefenderSiegeEntitiesCache` keeps the wall sections and wall engines whose mask has the current wall level's bit. Siege bombardment indexes that section cache with no bounds check.

**Borders and helpers.**
- The borders (`border_min`, `border_max`: the scene's box; `GeneratedMapScene.Load` refuses a scene without them) are added.
- So is the `Town Scene Manager` helper (`mnbl_world_helper_town_scene_manager`, the installed script copied verbatim as variable definitions).
- **The siege prefab cache.** No scene bakes `CampaignMapSiegePrefabEntityCache`. Its `OnInit` reads the campaign's map scene, and an inspection read of a scene carrying one throws (run 25412). `MapScreen.OnInitialize` dereferences it with no null guard. So `GeneratedMapScene.Load` adds it to the campaign map after the read, when the scene has none:
  - a helper entity with the script (`CreateAndAddScriptComponent`, callbacks off);
  - its ten prefab variables set to the installed campaign map's (`SettlementEntityContract.SiegeCacheVariables`);
  - then `CallScriptCallbacks`, whose `OnInit` instantiates the prefabs and caches their frames.
- No colour-grade manager: MMR-45 adds it at activation (MMR-39).

**Contract hash.** The installed contract hashes the helper scripts and the icon mesh references (`SettlementEntityContract`). That hash and the entity XML's hash enter the encoding's identity (`SceneNames.ForWorld`), as MMR-39's water bodies do. The wall meshes are refused unless the installed map references them.

**What reads the entities, and when** (installed 1.4.8 IL):
- **Settlement entity lookup.** `SettlementVisual.OnStartup` finds the entity with `Scene.GetCampaignEntityWithName(id)`, else asks `IMapScene.AddNewEntityToMapScene`.
- **Gate and banners.** It reads `main_map_city_gate` (its navmesh assertion is discarded) and the banner placeholders.
- **Siege.** It takes the camp frames through `IMapScene.GetSiegeCampFrames` into `Town.BesiegerCampPositions1/2`, fills the siege engine, ram, tower, wall section and wall engine arrays from the children (`PopulateSiegeEngineFrameListsFromChildren`), caches the current wall level's (`UpdateDefenderSiegeEntitiesCache`), then removes marker children. After the campaign starts, the scene's name lookups no longer return settlement roots; `SettlementVisual.StrategicEntity` does.
- **Drop-offs.** War Sails' `NavalMapSceneWrapper.InitializeDropOffLocations` scans `main_map_village_dropoff` entities when its wrapper initializes, and sets each village's port from them. That is why the entities are baked into the scene and not made at `OnStartup`.
- **Display copies.** MMR-66's display copies clone the settlement entity at `OnStartup`. The baked hierarchy is complete by then.

**The production check.** `SettlementEntityChecks.Run` runs on the engine thread over the inspection world in the generation job's runtime stage, after the navigation validation; a failure refuses the world (`runtime.settlement_entities`). It checks:
- the borders and the baked helper, and no baked siege cache;
- one root per settlement by `GetCampaignEntityWithName`, with its tag and script, at the layout's centre;
- every marker, wall section and wall engine by name at its layout position (the image beside the centre), with the wall level bits of its levels;
- each wall section's solid and broken child, and a village's four icons;
- gates, markers, walls and wall engines over a land navigation face; ports, arc ends, every replayed blockade ship and drop-offs over a sea face;
- `BlockadePositionScript` exactly on the port towns with an arc;
- the root census (the engine's own seven roots are listed);
- the persisted report.

**A settlement without a scene entity.** For example an MMR-34 hideout. `AddNewEntityToMapScene` builds its kind's full hierarchy from its record (`SettlementEntityFactory.Unresolved`, built live by `SettlementEntityRuntime`), instead of the installed prefab-by-name fallback (MMR-33's run 36452 crashed there):
- the record's centre, gate and port;
- every siege marker, wall section and wall engine at its installed offset in the record gate's frame, with no ground checks;
- no blockade arc.

Scene levels become upgrade level masks (`SetUpgradeLevelMask`) and scales the frame's. The entity is listed in `GeneratedMapScene.RuntimeSettlementEntities`. On a world's scene every town, castle and village is baked, so one built there is also listed in `MissingBakedEntities` (MMR-45's activation refuses a world with any). Both lists are cleared with the world.

## Records

`SettlementFactory.ApplyResolvedPositions(intents, layout.Positions)` writes the layout into the settlement intents:
- the centre;
- a fortification's gate;
- a port town's port;
- a harbour village's drop-off as its port.

A settlement without a layout entry stays staged and is reported. `SettlementFactory.CheckResolvedPositions` lists every record whose centre, gate or port differs from the layout by more than 1e-3, and every record with no placement. Production records are built by MMR-45's activation, which binds them with the first and refuses a world the second lists.

## Fixture worlds

`tools/Build-FixtureSettlements.ps1` rebuilds a scenebin fixture slot's settlement, border and helper entities with the factory. The scenebin water plane stays.
- **Placement.** The resolver runs over the fixture's own terrain (`FixtureGround`: water at or below the datum, slopes from the quantized grid).
- **Scene.** The campaign map's scene levels are declared; no siege cache is baked, so a fixture campaign exercises `AddSiegePrefabCache`.
- **Updates.** The placement's gate positions and scene hash are updated to match.
- **Harbour villages.** `--harbour` designates a fixture village as a harbour village, with its drop-off in its town's port cell: the fixtures have none.

`build/research-entities40/w42f` (world `mnbl_00040042`, world 42's terrain) is the MMR-40 fixture. In a generated_campaign with `settlementFixtures`, MMR-33's castle, its village and a hideout are registered too; with `factoryEntities=1` they take the run-time path instead of their vanilla prefabs.

## Offline tests

`tools/Test-SettlementPlacement.ps1` on world A (`f596b58c`) and world B (`f63c6841`) and the installed campaign map: 52/52 checks (`evidence/world/settlement-placement-tests.json` and `.txt`; both layouts with provenance in `evidence/world/settlement-position-report.json`).

| | World A | World B |
|---|---|---|
| Towns, castles, villages | 45, 26, 215 | 66, 39, 305 |
| Port towns, harbour villages | 30, 80 | 25, 105 |
| Gate-frame markers; walls and wall engines | 710; 1278 | 1050; 1890 |
| Markers adjusted (least steep); least-steep gates | 239 (10); 1 | 331 (35); 6 |
| Blockade arcs fitted; ships, all on their basin | 30 of 30; 300 | 25 of 25; 250 |
| Warnings | 37 steep pads, 11 steep markers, 2 ports beyond the installed centre reach | 46 steep pads, 41 steep markers, 3 ports beyond |
| Scene entities | 289 roots, 6297 entities | 413 roots, 9123 entities |

The checks cover:
- **Re-checks.** The layout is re-checked independently of the resolver: positions, reach, land component and slope, and global clearance (no two settlements' markers closer than 0.5). Every blockade ship is replayed from the installed IL in single precision and must lie on its basin.
- **Engine rules.** One village icon is visible per state. Each wall level holds two wall sections and four engines. Scales and marker yaw are the installed ones.
- **Contract violations.** A mistagged port, a wall without its solid child, undeclared scene levels, a baked siege cache and a missing entity each fail the contract.
- **Refusals.** A centre off land, a port on land, no room for a gate, a drop-off on land, a harbour village without a cell and two settlements at one place are each refused. A port whose arc cannot fit gets no arc. An arc whose ends are on water but whose ships are not is rejected.
- **Run-time hierarchy and records.** The run-time hierarchy is built from a record. Records are applied from the layout and checked against it.

## In game

The runs used module `b56acd37…`. The summary is `evidence/world/settlement-entities.json` (all 24 checks passed).

**Production scenes: world_surface run 29316.** Worlds A, B and a warm A were generated, encoded, installed, read into inspection worlds, and checked by `SettlementEntityChecks`, the check the runtime stage runs. The surface and water checks passed too.

| | A (and warm A) | B |
|---|---|---|
| Settlements found by name | 286 of 286 | 410 of 410 |
| Wall sections; wall engines | 426; 852 (71 fortifications) | 630; 1260 (105) |
| Level masks checked | 2138 (exact, e.g. 50 for `level_1, civilian, siege`) | 3110 |
| `BlockadePositionScript`; replayed ships over sea faces | 30; 300 | 25; 250 |
| Land and sea face checks | 2059 and 470 | 3045 and 430 |
| Positions checked, worst offset | 2455, 1.2e-4 | 3585, 1.2e-4 |
| Root entities (authored + the engine's 7) | 305 (298 + 7) | 472 (465 + 7) |
| Layout | `bc3004a4…` | `6b84dc44…` |

The layouts are the offline tests' layouts. Warm A has the same layout and root count as A.

The first confirming run (34096) found the plain-sphere wall colliders answering ground and ray queries over three villages beside a town's walls (0.5 to 0.96 from them). The colliders are raycast-only now.

**Generation job: run 34656.** The generation job ran the check in its runtime stage on world 42 (bundle `f596b58c`, world A): `checking_settlement_entities`, `navigation_verified`, `settlement_entities_verified`, `ready_for_review`. MMR-44's 17 job checks still pass (`tools/summarize_generation_job.py`). Its longest engine item is 0.188 s (a scene construct), under the 0.25 s bound, so the settlement check held the engine thread for less.

**Campaign: generated_campaign run 17864.** The run used `settlementEntityProbe=1` and `factoryEntities=1` on the rebuilt fixture `mnbl_00040042`, then switched to world `mnbl_00059043`. The two fixture towns and two villages are baked. The MMR-33 castle, its village and the hideout were built at run time from their records. No vanilla prefab was used.
- **Siege cache.** The scene had none; `GeneratedMapScene` added it with the 10 installed variables, and `MapScreen` took it. World `mnbl_00059043`'s older scene carries its own, which was kept.
- **Visuals.** Every settlement visual is bound to its entity.
- **Siege geometry.** Every fortification, the run-time castle included, has:
  - one besieger camp position of each kind;
  - 4 attacker engines, a ram and 2 towers;
  - 6 wall sections and 12 wall engines over all levels, of which 2 and 4 are cached for its wall level.
- **Blockades.** Each port town's `GetBlockadeArc(10)` placed 10 ships, all over water faces.
- **Drop-off.** War Sails took the harbour village's drop-off as its port.
- **Icons.**
  - Exactly one village icon is visible with the campaign's levels (masks 18, 20, 24, 78), the run-time village included.
  - A child without levels reports every level (4294967295).
  - The unspotted hideout is hidden by design.
- **Teardown.** After the switch, no visual of the first world remains. The 4 current visuals match the 4 current settlements, and no runtime, missing or prefab list holds a previous id.

## Limits

- **Steep pads are reported, not avoided.** The centre is MMR-38's binding navigation point. 37 pads on A and 46 on B exceed 25 degrees; 10 and 35 markers and 1 and 6 gates took the least steep position. Whether such a pad is acceptable, or the navigation point should move, is MMR-41's decision.
- **Hideouts are outside the layout.** Production hideouts come from the campaign build (MMR-34), after the scene is encoded. They get their hierarchy at run time from their record. Placing them as layout sites with the bundle's hideout positions is MMR-45's.
- **Production records are not built yet.** `ApplyResolvedPositions` and `CheckResolvedPositions` are tested offline. MMR-45 builds the records with them and refuses a world whose records differ from its scene or whose map lists a `MissingBakedEntities` entry.
- **A run-time fortification is not checked against the ground.** Its markers stand at their installed offsets from its record, with no ground checks. Only hideouts take this path on a world's scene.
- **The wall meshes and icon meshes are the empire's.** Culture-specific meshes and the installed icon scales are not used.
