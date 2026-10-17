# Mission scenes from the generated map (MMR-42)

**What it does.** A generated world reuses the installed mission scenes, as the product contract requires. MMR-42 makes every choice of them deterministic, geographic and checked:
- field and naval battles take a scene the generated map names for the party's place: its biome or land terrain, and the sea terrain under a ship;
- every settlement's location scenes (center at each wall level, lord's hall, port, village center, hideout center) are installed scenes that meet the mission contract the installed code reads;
- a battle on a generated map can never silently use a random scene (Calradia indexing): the choice is checked against the generated patch and a mismatch is recorded.

| Piece | File |
|---|---|
| The catalogue: installed battle, conversation and location scenes, inventoried, with the mission contracts and the terrain rules | `worldgen/policies/mission-scenes.json`, written by `tools/make_mission_scenes.py`, embedded in the module |
| Catalogue model, contract checks, location references | `src/MNBL.ProceduralWorld/Missions/MissionScenes.cs` |
| Battle table, expected scene, cell choice, battle map, seam helpers, mismatch check | `src/MNBL.ProceduralWorld/Missions/GeneratedEncounterResolver.cs` |
| The battle map in game | `src/MNBL.ProceduralWorld/CampaignBootstrap/GeneratedBattleTerrain.cs` (`GeneratedMapScene.AfterLoad`) |
| The mismatch guard | `src/MNBL.ProceduralWorld/Missions/MissionScenePatches.cs`, applied with the world patches (`Campaign/GeneratedPatches.cs`) |
| Settlement prototypes and the `prototypeAndScenes` slot | `Campaign/SettlementFactory.cs` (`Conform`, `Slots`), checked by `Campaign/CampaignGraphValidator.cs` |
| Biome lookup | `World/WorldSurface.cs` (`BiomeAt`, masks plane 0) |
| Probes | `Automation/GeneratedCampaignBootProbe.cs` (`missionScenesProbe`), `Diagnostics/RuntimeMissionProbe.cs` (`missionProbe`), `Diagnostics/WorldWrapProbe.cs` (`wrapProbe`, the seam battle) |
| Offline tests | `tests/Missions/EncounterResolverTests.cs`, `tools/Test-EncounterResolver.ps1`; the graph test (`tests/Campaign/GraphFailureFixtures.cs`) |
| Evidence | `evidence/world/mission-scenes-tests.json` and `.txt`, `evidence/world/mission-scenes-report.json` (in game), `evidence/fwg/mission-scenes-review.md` |

## The catalogue

`tools/make_mission_scenes.py` reads the installed game, hash-pinned:
- **Battle scenes:** every module's `sp_battle_scenes.xml` in the order the game loads them (SandBox 80, NavalDLC 46 scenes). The catalogue records the table hash `GeneratedBattleTerrain` computes in game, `3aa0154c…`, which MMR-60 recorded in its runs. The resolver refuses a loaded table whose hash differs, so a game update cannot silently change what an index means.
- **Conversation scenes:** `conversation_scenes.xml` (10 entries).
- **Location scenes:** every scene a settlement prototype names, per location and wall level, from the campaign catalog's Settlements document the prototypes were made from (539 prototypes).
- **Inventory of each scene's `scene.xscene`, parsed as XML.** Nothing is inferred from a scene's name. The catalogue records:
  - module, SHA-256 and size;
  - the scene levels (`<scene><levels>`);
  - every contract tag, tag prefix and script, with the scene levels its entity is visible in (an entity's own `<levels>`, else its parent's; none means every level);
  - the hierarchy: entities, root entities and maximum depth;
  - the paths and spawn paths (`spawn_path_*`, the patch placement) and the spawn points (`sp_*`, `spawnpoint*`);
  - the siege-readiness scripts (`CastleGate`, `DeploymentPoint`, siege engine spawners, `WallSegment`, `StrategicArea`);
  - terrain layers, and how many have an enabled winter variant;
  - where the scene is used and the contracts it meets there.

  For battle scenes the catalogue adds their capacity (spawn paths, entities) and the land classes they serve.
- **Inventory hash.** It covers forest density and ground (TerrainTypes), which the class preferences read and the table hash does not. The game compares it with its loaded rows.

All 370 scenes are installed (SandBoxCore 179, SandBox 124, NavalDLC 67), all in the allowed modules (Native, SandBoxCore, SandBox, NavalDLC).

## Mission contracts

Each contract is what the installed code (1.4.8 / War Sails 1.2.8, read in IL) requires of a scene:

| Contract | Scene | Requires | Read by |
|---|---|---|---|
| fieldBattle | `sp_battle_scenes`, land | a spawn path | `GetBattleSceneForMapPatch(patch, false)`; the patch places the armies on `spawn_path_*` (`MBSceneUtilities.GetAllSpawnPaths`) |
| navalBattle | `sp_battle_scenes`, naval | a spawn path | `GetBattleSceneForMapPatch(patch, true)` |
| settlementCenter | `center`, the scene wall level N opens | level `level_N` | visits: `DefaultLocationModel.GetUpgradeLevelTag(wall level)` |
| siege | `center`, the scene wall level N opens | levels `level_N` and `siege` | `SandBoxMissions.OpenSiegeMissionWithDeployment` / `NoDeployment`: `GetUpgradeLevelTag + " siege"` |
| siegeLordsHall | `lordshall`, the scene wall level N opens | level `siege` | `SandBoxMissions.OpenSiegeLordsHallFightMission` (`GetLocationWithId("lordshall").GetSceneName(wall level)`) |
| port | a port town's `port` | a scene | War Sails' shipyard location |
| villageCenter | `village_center` | a scene | village visits |
| navalRaid | a harbour village's `village_center` | level `naval_raid`; `landing_*` and `jumping_*` points visible in that level | `NavalMissions.OpenNavalRaidMission` (`GetScene("village_center")`, SceneLevels `naval_raid`); `NavalRaidMissionController.OnBehaviorInitialize` collects `landing(_\d+)*` and `jumping(_\d+)*` |
| hideout | `hideout_center` | levels `level_1` and `level_2`; `spawnpoint_player`; `CommonAreaMarker` areas | `SandBoxMissions.OpenHideoutBattleMission`; `HideoutMissionController.InitializeMission` and `SpawnTroops`; `SandBoxHelpers.SpawnPlayer` |

**The scene a wall level opens.** "The scene wall level N opens" is `Location.GetSceneName(N)`: level N's own scene, else level 0's. The checks read that effective scene, so a hall with no level-3 scene and an empty level 0 is refused.

**Optional elements, inventoried but not required:**
- `player_spawn_frame` and the `attacker_`/`defender_` formation spawns of a raid are used when present (`NavalRaidMissionDeploymentPlanningLogic` falls back otherwise).
- A land raid battle (`PlayerEncounter.StartVillageBattleMission`) opens the village center with level `land_raid` when the scene has it. Most installed village scenes do not.
- The siege mission's entity needs are inventoried (gates, deployment points, siege engine spawners) but not required, because prototypes are reused unmodified.
- The port contract checks only that the scene is installed; no level or entity requirement was traced.

**There is no separate siege scene.** A siege opens the fortification's center scene at its wall level with the `siege` level. MMR-40's settlement entities carry the siege level masks on the campaign map.

**Every installed prototype meets its contracts.** That is 28 inland and 29 port towns, 76 castles, 72 coastal villages and 99 hideouts. Only 5 of 235 inland village prototypes are raid-capable, but every culture with fiefs has coastal village prototypes. A harbour village built with its port therefore always draws from a raid-capable pool.

## Settlements

- **Contract check on the factory's pick (`Conform`).** `SettlementFactory` keeps its MMR-33 pick (culture and kind) and checks it against the kind's contracts. A town's port location is checked only for port towns; `Element()` drops it for inland towns. A failing pick is re-picked deterministically among the pool's conforming prototypes (stream entity `<id>|missionScenes`), with the reason recorded. No conforming prototype is a refusal. Every installed prototype conforms, so no pick changes today.
- **Location references (C04/C06).** Every town and castle claims its compiler slot `prototypeAndScenes`, owned by MMR-42. The slot carries the prototype, the contracts and a reference record: the number of location, level and scene references, the distinct scenes, and the SHA-256 of `location|level|scene|module:scene sha` lines bound to the catalogue's hash.
- **Graph validation.** MMR-42 is now a delivered owner in `worldgen/policies/graph-validation.json`, and nothing is deferred.
  - `CampaignGraphValidator` shows a claimed `prototypeAndScenes` slot by checking the staged settlement's own locations against the contracts.
  - The claim is bound to what is staged: the slot's reference record must be the staged locations' own.
  - A staged town whose center scene is not installed, and a claim whose record is not the staged one, each fail that slot (offline tests).
  - The catalogue is a resolving policy in the identity snapshot.
- **Fixtures, villages and hideouts.** Villages and hideouts have no compiler slot; `Conform` checks them when they are built.
  - In game the fixture factory checks its picks against the installed catalogue.
  - A fixture placement's harbour village is built with its port, so it takes a coastal prototype. Before this, run 46016 showed it taking an inland one; see the review record.
  - The in-game probe checks every generated settlement's live locations (below).

## Battles

**The engine's rule** (MMR-60's chain, `DefaultSceneModel.GetBattleSceneForMapPatch`, re-read in IL for the naval case):
- The scenes listing the patch's index with the encounter's IsNaval are taken.
- One scene is used as is.
- Several naval scenes are filtered by `Terrain ==` the terrain under the main party, `SandBox.MapScene.GetTerrainTypeAtPosition` (the `GetEnvironmentTerrainTypesCount` out value). A random one is taken; with none of that terrain, a random one of all.
- Several land scenes are random with an assert.
- None is a silent random fallback.

`BattleTable.ExpectedScene` is that rule, deterministic when the final list names one distinct scene. The battle map, the guard and the probes share it.

**Why this widens the coasts.** MMR-60 used only the 8 indices naming exactly one naval scene. Through the terrain tie-break, 66 installed indices resolve both a land battle and a CoastalSea or OpenSea naval battle deterministically. River and UnderBridge scenes are selectable for the first time.

**Cell choice (`GeneratedEncounterResolver.Choose`).**
- **Sampling.** Each 4-unit battle-map cell is sampled at 3×3 points. Land and sea faces are classified by the navigation model: land-valid, naval-valid or blocked.
- **The guarantee, for every sampled party.** A land party (where land was sampled) finds exactly one land scene. A ship on each sampled sea terrain finds exactly one naval scene. A dense 6 × 6 check of every cell of both compiled worlds found no unsampled party position that resolves randomly (Offline tests).
- **Settlement cells.** The cells around every settlement's position, gate and port, a ring of one cell, take an index every naval-valid terrain resolves (28 installed). Naval map events at a settlement read the patch at the main party, which may stand on land there.
- **Preference.** Among the indices that meet that guarantee, the cell's geography decides:

| Class | Biomes (compiled worlds) | Face terrain (without a biome) | Scene terrain | Preference | Fallback |
|---|---|---|---|---|---|
| open | grassland (ocean, lake) | Plain, RuralArea, Beach, Fording, Bridge | Plain | none | Steppe |
| woodland | forest, boreal forest, rainforest | Forest | Plain | forest density High, else Low | none |
| steppe | | Steppe | Steppe | none | Plain |
| rock | exposed rock | Mountain, Cliff, Canyon | Plain | Mountain or Canyon ground (the installed scenes with such ground are Plain) | Steppe |
| cold | tundra, cold tundra, snow, land ice | Snow | Steppe | none | Plain |
| desert | desert | Desert, Dune | Desert | none | Steppe, Plain |
| wetland | marsh | Swamp | Swamp | none | Plain |

**Substitutions.** No installed battle scene is a Forest, Snow or Mountain one; these are the agreed substitution rules. Snow appearance comes from the campaign's season and weather: every installed battle scene has winter terrain layers (inventoried), so season is not a selection input.

**Relief.** The compiled masks' landform plane gives each cell its relief: Submerged, Plain / hillside, Mountain / ridge or Valley. Within the class, a ridge prefers scenes with Mountain or Canyon ground and a valley scenes with River or Water ground. This is the slope input: the generator's own relief classification, not a threshold on the heightfield.

**Sea.** A naval scene of each sampled sea terrain itself is preferred: CoastalSea, OpenSea, River, UnderBridge. Lake and Water have no naval scenes of their own, so they take an index naming one naval scene only, a CoastalSea one where possible, which the engine then uses whatever the terrain.

**Tiers, best first:**
1. class land with the cell's own sea;
2. class terrain;
3. each fallback terrain;
4. any land;
5. then the same with any deterministic sea scene;
6. naval only, for cells where no land was sampled.

Then two narrowing steps. Relief narrows the chosen tier to the indices whose ground shows the landform, when any do. A settlement cell narrows it to the naval-robust indices, or searches them with the class's terrain first.

The pick among the tier's indices is the MMR-60 stable hash of the world and the cell's 16-cell block.

**The periodic seam.**
- Cells are computed in canonical columns, so a strip cell holds its canonical cell's bytes and a block starts at the seam (MMR-64's rule, kept). `GetMapPatchAtPosition` at a point and at its other image reads the same patch.
- The battle's encounter direction is MMR-63's transpiled attacker − defender, the short way through the seam.
- Together these close MMR-60's open item. The offline seam fixture and both compiled worlds check them; the in-game seam battle asserts them.

**The mismatch guard.**
- `MissionScenePatches.BattleScenePostfix` observes every `GetBattleSceneForMapPatch` result on a generated map and checks it with `GeneratedEncounterResolver.Check`, using the model's own terrain input (`IMapScene.GetTerrainTypeAtPosition`).
- It is an observe-only Harmony postfix in the world patch set (all or nothing), declared in `GeneratedPatches.Manifest` and `docs/campaign-behavior-compatibility.md`.
- A patch index 0 (no generated battle map), a patch that does not resolve deterministically, or an opened scene other than the patch's is a mismatch. Each one is logged and counted; the last 256 observations are kept; the result is never changed.
- The battle map itself is refused at load if the loaded table or its inventory is not the catalogue's.

**Conversations.**
- Map conversations reach `GetConversationSceneForMapPosition` (`SandBoxMissions.OpenConversationMission` without a special scene, IL).
- It counts only 9 terrains (`_conversationTerrains`: Plain, Desert, Swamp, Steppe, OpenSea, CoastalSea, Lake, River, Water) among 57 samples around the party. A Snow first sample counts as Plain, and Plain is used when nothing is counted. Then it takes a random conversation scene of that terrain, else a random one.
- On compiled worlds, Plain and every generated sea terrain have conversation scenes of their own. Bridge and UnderBridge are not counted: the dominant counted terrain around them decides.
- This is cosmetic and random by design (engine behaviour).

**Biome and relief in game.**
- `GeneratedBattleTerrain.ContextSources[scene]` takes a world's terrain context lookup (`WorldSurface.BiomeAt` and `LandformAt` over the bundle's masks).
- **Registered by the activation.** MMR-45 registers the lookup for the world scene it activates, from the world's surface and `terrain-materials.json`, and removes it when an activation is rolled back.
- Fixture worlds have no biome and use their face terrain.

## Offline tests

`tools/Test-EncounterResolver.ps1` passes 35/35 (`evidence/world/mission-scenes-tests.json` and `.txt`). It runs on the installed game's files and compiled worlds A and B. It uses the managed navmesh stand-in, which is never evidence of engine behaviour; there is no game process.

| Area | Checked |
|---|---|
| Catalogue | The installed battle and conversation tables are its sources. The table hash is the one MMR-60 recorded in game (`3aa0154c…`) and the inventory hash is the catalogue's. All 370 scenes are installed in allowed modules, and 40 sampled scene files hash as inventoried. Every raid scene's landing and jumping points are visible in `naval_raid`. Every battle scene has spawn paths |
| Contracts | Every installed prototype meets its kind's contracts, and every culture with fiefs has raid-capable coastal villages. Refused: a battle scene as a wall level 2 center, a missing lord's hall, a port town without a port scene, an inland village scene for a harbour village, a village scene as a hideout, a scene that is not installed, a lord's hall with no scene at wall level 3 |
| Cells | 19 geography probes: grassland, forest, boreal forest, desert, marsh, snow, tundra, rock, Steppe face, coast, desert coast, open sea, shelf, river bank, bridge crossing, ford, lake shore, river mouth, blocked mountain. Each resolves deterministically, of the class's terrain. Forest takes a High-density Plain scene, rock and a ridge mountain ground, a valley water ground, desert and marsh Desert and Swamp scenes. A desert coast falls back to Steppe. A ship under a bridge gets an UnderBridge scene and on a river a River scene; a lake gets a CoastalSea scene. Settlement cells get naval-robust indices. All 247 combinations of 13 biomes, 10 sea terrain sets and land present or not resolve. 66 installed indices resolve both a land battle and a CoastalSea or OpenSea one; MMR-60 used 8 |
| World A | 400 × 400 cells of 4 units: strip 100, period 200 (whole cells). 5,548 cells with land and 16,076 (cell, sea terrain) pairs, all deterministic (15,692 of the sea terrain itself). 79,673 of 80,000 canonical cells take their class's preferred scenes; 1,013 relief-preferred; 1,661 settlement cells robust. Dense check: 678,457 party points, 0 random. Biomes: ocean 73,715, cold tundra 2,965, desert 1,438 and 7 more |
| World B | 10,591 land cells and 12,172 pairs, all deterministic (10,060 own terrain). 79,039 preferred, 1,706 relief, 2,460 settlement cells robust. Dense check: 682,478 points, 0 random. Biomes: land ice 61,970, cold tundra 4,474, desert 3,114, lake 2,048 and 7 more |
| Seam | On A and B every strip cell holds its canonical cell's bytes, and a block starts at the seam column. A seam-adjacent pair reads the same patch at a point and its other image, with the short-way encounter direction (`PeriodicWorldCoordinates.DeltaX`). A synthetic 80 × 16 fixture checks the same |
| Mismatch | Flagged: patch index 0, a random naval index (46 on CoastalSea), another opened scene, an unlisted index. The patch's own scene agrees |
| Conversations | The engine's counting rule on the generated terrains |

**The other harnesses.** The graph test (`tools/Test-GraphValidation.ps1`) passes 72/72. It expects all 71 `prototypeAndScenes` slots of world A resolved, plus the two new refusals. Settlements 22/22, NavalContent 15/15, SpawnPolicy 18/18, GeneratedCampaignDataset 60/60, SettlementPlacement 52/52 and Playability 24/24 pass with the catalogue compiled in.

## In game

`evidence/world/mission-scenes-report.json` (`tools/summarize_mission_scenes.py`) passes all 11 checks. Every value is copied from the runs.

**Fixture worlds** (run 32960; two worlds; settlement, startup and naval fixtures; `missionProbe=1`, `missionScenesProbe=1`):

| | World 0 (`mnbl_00040042`) | World 1 (`mnbl_00059043`) |
|---|---|---|
| Battle map | installed by the resolver in 162 ms. 11,652 land cells and 30,570 (cell, sea terrain) pairs, all deterministic (30,534 own terrain); 110 settlement cells robust | 98 ms. 8,851 and 18,499, all deterministic (18,483 own terrain); 45 settlement cells robust |
| Field battle | `battle_terrain_biome_030` (index 30, a single scene) | `nord_battle_terrain_f` (index 207) |
| Settlement location | `empire_town_f` (town center) | `empire_town_q` |
| Naval battle | `battle_terrain_opensea_northern` (index 203, the one naval scene of OpenSea under the party: the terrain tie-break) | `coastal_terrain_north_of_the_north_sea_01` (index 222, CoastalSea under the party) |
| Settlements checked | 11: 2 towns, a castle, 3 villages (one a harbour village, raid-capable), 5 hideouts; 113 scene references, all found by the engine | 4; 78 references |
| Guard | 2 battle choices observed, 0 mismatches | 2 and 0 |

- **Table and model checks:** in both worlds the loaded table and inventory hashes equal the catalogue's (`3aa0154c…`, `d592b24e…`), and the live navigation model's terrain sets equal the catalogue's.
- **Lord's halls:** the probe reads every fortification's lord's hall at wall levels 1–3 (`empire_castle_keep_a_l1/l2/l3_interior`).

**The periodic seam** (run 17596; fixture-c; `wrapProbe=1`, `missionScenesProbe=1`):
- **Battle map:** 400 × 400 cells with strip 100 and period 200, computed in canonical columns. 11,668 land cells and 68,748 pairs, all deterministic.
- **The seam battle:** the main party stood 0.12 units from the seam (x = 1199.880). The patch there and at its other image (x = 399.880) is the same cell: index 109, coordinates (0.467, 0.6).
  - The model opened `battle_terrain_031`, the patch's scene; the guard agreed.
  - The battle's encounter direction (−0.403, 0.915) is the short way through the seam; planar would be (1.0, 0.0006).
- **Settlements:** all 4 meet the contracts.

**History** (kept in the summary's sources):
- **46016:** the harbour-village defect.
- **41380:** MMR-33's fixture contract rejected the harbour village's port.
- **47808:** passed, before the review fixes.
- **30828:** the first seam run, passed. Its run-level guard summary was taken before the seam battle; it is now taken after.

## Limits

- **A compiled world's campaign in game (MMR-45).** World A's battle map was built in its activated campaign, with the world's masks as its terrain context. In run 9404, every one of 5,548 land cells and 16,076 sea terrain checks gave one scene, and all 358 settlements' live location scenes met the contracts. The fixture worlds have no biome or landform source (face terrain only).
- **Missions entered in game:** field, settlement (town center) and naval battles on two fixture worlds, plus a land battle at the periodic seam.
  - Sieges, lord's hall fights, hideout battles, village raids (land and seaborne) and port visits were **not** opened. Their scenes are checked against the contracts above and found by the engine's scene lookup, but entering them is MMR-52/53's.
  - A naval battle at the seam is MMR-53's.
- **Contracts are what the installed code reads, as traced.** Siege entity needs and the port scene's contents are inventoried, not required. Our settlements copy their prototype's whole component and locations, so workshop and notable markers come from the same installed settlement; their counts are not compared with the settlement's data (the Player Settlement mod guards these mismatches: `docs/research/player-settlement.md`).
- **Passages:** the installed location scenes carry no passage scripts. Location targets are the prototype's location complex (the catalogue's per-location scenes), not entities in the scenes.
- **Fail-closed table.** A game whose battle scene table or its inventory differs from the catalogue's, for example a mod adding `sp_battle_scenes` entries, refuses the generated battle map: a generated campaign does not load. Regenerate the catalogue (`tools/make_mission_scenes.py`) for such a setup.
- **Sampling.** The 3×3 sampling cannot see sub-cell slivers. The dense check found none on A and B, and settlement cells are naval-robust. `ReadPatch` computes in double where the engine uses float32, so an image exactly on a cell boundary can land in an adjacent cell.
- **The seam assertion** relies on MMR-63/65 keeping the main party canonical.
- **Conversation scenes** are random by the engine's design. Bridge and UnderBridge are not counted by its rule.
- **Vanilla paths outside the generated content** are not covered: a hard-coded `OpenHideoutBattleMission("sea_bandit_a")` quest path.
- **Land ice** maps to the cold class (Steppe scenes); no Snow battle scene is installed.
