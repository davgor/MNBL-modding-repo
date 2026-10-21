# Runtime capability investigation — incomplete

## Sea datum on generated maps (2026-10-14, MMR-25 calibration run)

Run `generated_campaign-4768` (module `6f7e3edc…`, world `mnbl_00059042`, `seaDatumProbe=1`, `Diagnostics/SeaDatumProbe.cs`) sampled a 24 × 24 grid of cell centres.

| What was read | Result |
|---|---|
| `Scene.GetWaterLevel()`, and `GetWaterLevelAtPosition` with the water renderer and with water bodies, at all 576 samples | −100 everywhere: the generated scene's `water_level`, with `water_exists=false` |
| `medit_water_plane` entity | absent |
| Terrain height by face: open sea (256 samples) / coastal sea (159) / land (25, all Mountain faces) / no face (136) | −33.03…−0.90 / −32.66…6.38 / 4.37…63.75 / −0.50…24.91 |
| Face lookup | `GetFaceIndex(new CampaignVec2(p, false))`, the sea-flagged position. That is the likely reason low land near the coast returned no face (not separately verified). The grouping does not affect the water-level reading |

- **No water surface at the datum.** The engine has none at the sea datum on generated maps. Terrain and navmesh treat z = 0 as sea level, and the sea floor lies below it.
- **Visible water.** The water seen in screenshots does not come from the scene water renderer; this run did not identify its source.
- **Official map, for comparison.** The War Sails `Main_map` also has `water_exists=false` and supplies its sea with a `global_water` entity at z 1.8 plus local `water_plane` entities.
- **Owner.** A rendered sea surface at z = 0 belongs to MMR-39.
- **Where the datum is recorded:** `worldgen/calibration/axis-sea-datum/calibration.json` and `docs/world-projection.md`.

## Generated-world save identity across a cold restart (2026-10-14, MMR-19)

Two processes ran module `174fcb0a…`, the final tree shared with MMR-59. Process 1, `generated_campaign-31624`, created and saved two generated worlds; process 2, `generated_save_load-8256`, was started fresh and loaded both saves in order. All 17 checks passed. The first passing pair, 1116 and 27612 on the pre-review tree, is kept as history.

| | |
|---|---|
| Evidence | `evidence/runtime/two-world-load-matrix.json`, written by `tools/summarize_world_save_probe.py` from both result files |
| Probe | `src/MNBL.ProceduralWorld/Diagnostics/WorldSaveProbe.cs`: `saveProbe=1` inside `generated_campaign`; `generated_save_load` with `oracle=<file>` |
| Binding | `CampaignBootstrap/WorldIdentityBehavior.cs`, `GeneratedWorldPackage.cs`, `GeneratedCampaignRegistration.FromInstalledPackage`/`ReapplyForSavedGame`, and the load mode of `DiagnosticSandboxBootstrap` |
| Worlds | the MMR-59 fixtures `mnbl_00059042` (saved at sea) and `mnbl_00059043` (saved on land) |
| Research | `task-notes/mmr19-save-identity-research.md` |

**Answer: yes.** A generated world is identified before anything deserializes, and it is loaded after a cold restart from its pinned outputs. No generator runs, and no dataset is rebuilt.

What the load uses:
- the world package;
- the installed scene slot. Its five files are hash-pinned by the package but live outside it, under a slot name derived from the seed. A same-seed regeneration or a slot redeploy would make the save refuse to load (fail closed). Package-owned scene storage was assigned to MMR-62; it is not done there: production worlds use content-addressed scene names (MMR-37) and the binding records the data bundle, and installing the scene at activation and reinstalling it at load belong to MMR-45 and MMR-49 (`docs/save-binding.md` limits).

What the load rebuilds from those inputs, not compared with process 1:
- the navigation caches (about 35 s and 70 s here);
- the battle-index map;
- the dynamic rain texture.

**How it works:**
1. **Identity in the save header.**
   - `WorldIdentityBehavior` adds `mnbl.world.*` keys through `CampaignEvents.CollectMetadataEntriesEvent`: schema, world id, seed, scene, package path, manifest hash, placement/scene-file/document hashes, periodic flag, climate pattern, scene CRCs.
   - `SaveHandler.GetSaveMetaData` dispatches that event for every save mode (IL_02f3).
   - At the main menu, with no game existing, `MBSaveLoad.GetSaveFileWithName(name).MetaData` reads the header without deserializing. No sidecar is needed.
2. **Immutable world package.**
   - Written as soon as a generated world is created, for every generated world, so every later save (autosaves included) carries the identity. It lives at `%LOCALAPPDATA%\MNBL.ProceduralWorld\worlds\<worldId>-<manifest12>\`.
   - `world.json` also records generator and adapter fingerprints:
     - the fixture's build receipt: builder, encoder and source hashes, and the build command and settings;
     - the module assembly hash;
     - the dataset's default-reference hash and instance count.
   - Contents: `placement.json`, the climate files, the eight generated XML documents (the exact bytes the new game loaded) and `world.json`, which pins every hash including the five installed scene files.
   - An existing package must be byte-identical. Loading verifies every hash, checks that the package names the header's world, scene and seed, and fails closed before deserializing.
3. **Load.**
   - `MBSaveLoad.LoadSaveGameData` rebuilds the object graph by the save's own ids, with no StringId lookup.
   - The bootstrap's load mode, `SandBoxGameManager(LoadResult)`, re-merges the five permanent-type documents from the package in `RegisterSubModuleObjects(true)`: SPCultures, NPCCharacters, WorkshopTypes, LocationComplexTemplates and Settlements.
     - This runs after `MBObjectBase.BeforeLoad` has re-registered the saved objects and before `UnregisterNonReadyObjects`.
     - It restores what the save does not store: settlement names, positions, gates, ports and cultures.
   - Heroes, Kingdoms and Factions come from the save.
   - The generated map scene creator is installed in `OnGameInitializationFinished`.
   - `OnLoadFinished` takes the saved branch.

**Results:**

| | World A `mnbl_00059042` | World B `mnbl_00059043` |
|---|---|---|
| Before the save | land travel 5.6 units, then embarked at town 1's port and sailed; at sea, 1 ship | land travel 5.6 units; on land |
| Save header | all 13 `mnbl.world.*` keys equal the identity written at creation; `UniqueGameId` `GO3NSZRJLaMF` | equal; `joNZXru842qx` |
| Cold process, at the menu | identity read with no game existing; package `…-8828c934b3b5` verified and names the header's world | package `…-c25c9d9d4d26`, same |
| Load | deserialized; 5 documents re-applied from the package; MapState reached | same |
| After load against the save-time snapshot | **0 differences** in: scene, CRCs, `UniqueGameId`, campaign time, bounds; 4 settlements (names, cultures, positions, gates, ports, `HasPort`, owners, ready, gate/port faces); 17 parties (5 saved positions each with land flag, next steering point, land/sea, ships, settlement); the player (position, face `[7107, 18, 2]`, at sea); 19 heroes, 3 clans, 2 kingdoms | **0 differences**; player face `[20053, 1, 2]` on land; 20 heroes |
| Scene CRCs | live equals saved (XML `146201570`, navmesh `2944337574`), so `Campaign.CheckMapUpdate` relocated nothing | equal (navmesh `4043892403`) |
| After load | campaign time ran 0.92 h. 30 first-chance `NullReferenceException`s were raised and handled inside engine code during the load. Their source was not captured (no stack recorded); the load, the comparison and the run were unaffected. | 0.91 h; also 30 handled |
| Regeneration | `GeneratedCampaignDataset.Build` ran 0 times in the cold process | 0 |

**Findings:**
- **The scene XML CRC is the same for both worlds** (`146201570`); only the navmesh CRCs differ. It does not identify a generated world, so identity uses the world id and the package manifest hash.
- **Vanilla party StringIds repeat across worlds** (MMR-18). They are compared only within one world's save. World identity is never keyed on them.
- **Load points the engine's quick-save slot at the loaded file** (`MBSaveLoad.ActiveSaveSlotName`). The probe saves under unique names.
- **Statics between worlds:**
  - `Campaign.MapDiagonal`, bounds and path-cost statics keep the previous world's values until the next map loads.
  - The probe asserts bounds after the map is up.
  - The periodic query counters now reset in each `DiagnosticMapScene.Load`.

**Not covered, and owned elsewhere:**
- **Normal main-menu loading:** not routed. The vanilla `SandBoxGameManager(LoadResult)` would load official Settlements/NPCCharacters XML onto a generated save (IL-derived, not run). Routing or refusal belongs to MMR-49, together with the alternating same-process load matrix and seam saves.
- **Autosaves:** not exercised. IL shows every save mode (ironman, SaveAs, quick, auto) builds its metadata through the same `GetSaveMetaData`, and the identity is now filled at creation.
- **Failed saves:** evaluated from IL only.
  - `FileDriver.Save` builds the metadata and the compressed game data in memory, then writes the file once with `FileHelper.SaveFile` (IL_008c).
  - A failure before that write leaves no file, so no header without data.
  - Whether a failed overwrite can leave a partial file is unverified.
- **Missing, corrupt or incompatible packages:** the loader fails closed on any hash mismatch, but no refusal fixture was run (MMR-50).
- **Re-save after load:** the identity behaviour is re-added on load with the header's entries, but no re-save was run. `SyncData` carries no mirror of the identity.
- **Location:** the package lives in local app data for this diagnostic. The header stores its absolute path, which includes the Windows user name. Resolving by world id and manifest hash, and production location and ownership, belong to MMR-62. Done by MMR-62: the header now holds the package directory's name, and every loader resolves `<store>/worlds/<id>-<manifest12>` (`GeneratedWorldPackage.Resolve`, `docs/save-binding.md`).
- **Not part of the snapshot:** anchors and storms (MMR-49). The snapshot compares settlement names and cultures, party positions with their land flag and next steering point, and party, settlement and player faces.
- **First-chance exceptions:** recorded during each load and not asserted. 30 handled `NullReferenceException`s per load, source not captured. They are caught where they are thrown, and no wrong state has been traced to them, but they are unexplained and not yet traced (open; `evidence/one-shot/acceptance-index.json` s6).
- **Embarking was synthetic:** a ship and crew were granted, then `SetSailAtPosition` (as in MMR-17).

## Generated wind, storms and weather rebind (2026-10-14, MMR-59)

Run `generated_campaign-24212` (module `174fcb0a…`, the final tree shared with MMR-19) loaded two generated worlds in one process: `mnbl_00059042` (800 wide) and then `mnbl_00059043` (640 wide). Each carried its own generated climate pattern with a two-zone wind field, and every check passed (23/23).

| | |
|---|---|
| Evidence | `evidence/runtime/environment-rebind-probe.json`, written by `tools/summarize_environment_probe.py`; history runs 32972, 32780 and 32548 (the first passing run, before the review fixes) |
| Probe | `src/MNBL.ProceduralWorld/Diagnostics/RuntimeEnvironmentProbe.cs` (`environmentProbe=1`) |
| Binding | `CampaignBootstrap/GeneratedClimate.cs` (loads and hash-checks the fields) and `GeneratedMapSupport.PrepareWeather` |
| Fixtures | `tools/scenebin/build_climate_world.py` → `build/research-env59/w42` (pattern `along`) and `w43` (pattern `across`) |
| Research | `task-notes/mmr59-environment-research.md` |

**Answer: yes, for wind/flow, the storm spawn area and snow/rain. Season is global by design.**

The consumers are the installed queries. The binding is a reflective write into private `SandBox.MapScene` fields, a recorded and tested contract rather than a public API (see R07 status below).

- Every consumer below read the active world's generated data, not Calradia grids.
- A world switch replaced all of it.
- The previous world's managed objects were garbage-collected.
- No Harmony patch is needed for a non-periodic world.

**The fixtures.** They are the bootstrap worlds plus two generated inputs.
- **Deep sea.** Sea cells 4 or more cells from land become face group 19 (TerrainType `OpenSea`); the coastal ring stays 18. That gives 14,710 and 13,920 OpenSea faces.
- **Climate patterns:**
  - `along`: wind 0.30 from the town_1 port toward the town_2 port everywhere; rain 230/255 west of the middle, none east; no snow.
  - `across`: wind 0.30 perpendicular to that; snow 230/255 in the northern half; no rain.
- **Files.** `climate-wind.f32` (512×512 (x, y) float32 pairs) and `climate-precipitation.u8` (32×32 (snow, rain) bytes). Both use `SandBox.MapScene`'s own layout (index row·dim + col from origin 0, clamped) and are hash-pinned in the placement.

**Binding contract.**
- `DiagnosticMapScene.Load` writes the private fields `_windFlowMapData`, `_snowAndRainData` and `_snowAndRainDataTextureWidth/Height` before `MapWeatherModel.InitializeCaches`, on the loading thread. These fields are the single store every consumer reads.
- It also sets `Campaign.DefaultWeatherNodeDimension = 32` and calls `Scene.CreateDynamicRainTexture(32, 32)`.
- The vanilla binder (`SnowAndRainTextureDefiner` → `MapScene.SetSnowAndRainDataWithDimension`) is deliberately absent.
  - It binds whatever texture the scene's definer names. That is Calradia's `main_map_snow_flowmap` through the stock prefab in `map_icons.xml`.
  - It then overwrites the wind with native `Scene.GetWindFlowMapData` (IL_0068–007e). That this reads a scene's `flowmap.dds` is UNVERIFIED.
  - `Scene.GetWindFlowMapData(float[])` is itself public and could be called directly.
- The probe checked the scene's buffers against the generated files by SHA-256 in both worlds. That shows nothing overwrote them; the consumer rows below are the evidence that they are read.

**Results:**

| Consumer | World A `along` | World B `across` |
|---|---|---|
| Wind: raw texel `MapScene.GetWindAtPosition`, and the War Sails speed/storm query `MapWeatherModel.GetWindForPosition` (NavalDLC model) | equal to the generated field at all 6 samples. The two zones read back distinctly: west (−0.271, 0.128), east (−0.181, 0.085). This tests the 512² layout. | west (0.291, −0.074), east (0.194, −0.049), equal to the generated field |
| Naval speed: the War Sails model's own `CalculateWindBoostForParty`, read during real sailing (25–27 moving, storm-free samples per leg) | speed equals (1 + boost) × a terrain base: coastal 3.467 at boost 0.238 against 2.801 without (ratio 1.238); open sea 5.02 against 4.056 (×1.448 open-sea factor). Speed ÷ (1 + boost) is constant per terrain to 0.1%. With the wind the ship covered 3.22 per hour, against it 2.69. | coastal 3.225 at boost 0.151 against 2.80 without (ratio 1.152); constant to 0.06% |
| Boost formula | matches the engine rule on the generated wind to 3e-8 | matches to 3e-8 |
| Snow/rain: raw `IMapScene.GetSnowAmountAtPosition`/`GetRainAmountAtPosition` | equal to the generated bytes; season-scaled rain 0.82 west against 0 east | equal; season-scaled snow 0.94 north against 0.01 south |
| Weather event (per-node cache, *not asserted*) | LightRain at the wet coastal sample; Clear at the dry samples | Snowy at the snowy Mountain sample; the two snowy open-sea samples report Clear (NavalDLC gives Clear at sea) |
| Open-sea storm spawn area (`StormCampaignBehavior._allOpenSeaWeatherNodePositions`) | 470 nodes, equal to an independent OpenSea recount, all on generated OpenSea faces | 449 nodes; 13 node positions coincide with A's grid |
| Storm spawned at a candidate (`StormManager.CreateStormAtPosition`, the call the hourly spawner makes); 6 and 5 natural storms already existed, recorded with positions | wet cell: generated rain 230, season-scaled 0.831, so the threshold rule gives type 2 and the storm is a `Hurricane` | dry cell: rain 0, type 0, a `Storm` |
| World switch | — | new map scene, campaign, buffers, War Sails manager and storm manager; 0 storms carried over; bounds 640 |
| Previous world released | — | after three full collections none of A's managed objects is alive: map scene, campaign, buffers, War Sails manager, storm manager, storm, A's source climate copies, A's bootstrap manager |

**Findings that bind later work:**
- **The generator must emit OpenSea face groups (19).** Storms spawn only on weather nodes over OpenSea faces. With only coastal sea (18), as in the MMR-17/60 fixtures, no storm ever spawns and nothing reports it.
- **Storm spawn scale is Calradia's.** The rate scales with the number of OpenSea nodes, not sea area: 0.0025 per node per hour, at most 6 storms, 200 m separation, effect radius 20/30/40 × (1 ± 0.2 by intensity), i.e. 16–48 m.
  - The cap of 6 storms was reached within about 20 campaign hours on these 800/640-wide worlds.
  - Tune through a `MapStormModel` subclass (W03/MMR-39).
- **The storm type follows the generated rain at the spawn point**, so climate generation shapes storm severity.
  - `CreateStormAtPosition` takes the type from `NavalDLCStormModel.GetSpawnedStormTypeForPosition`: season-scaled rain ≤0.2 gives type 0, ≤0.6 gives 1, otherwise 2.
  - The probe asserts the spawned type against that rule applied to the season-scaled rain it read before the spawn.
- **The War Sails wind angle is not wrapped.** `CalculateWindBoostForParty` uses `|wind.RotationInRadians − bearing.RotationInRadians|` in degrees and gives no boost at 120° or more.
  - So a 100° crosswind gave no boost on one heading (world B, raw difference above 120°), while 80° gave 0.151.
  - This is the engine's behaviour; generated wind should not rely on symmetric crosswind boosts.
- **Season is global and time-only** (`GetSeasonTimeFactorOfCampaignTime`; summer at campaign start). Position-dependent climate comes only from the snow/rain field, and from atmosphere probes, which neither installed map uses.

**Reusable static resources and generated fields:**
- **Generated, per world:**
  - the wind field;
  - the snow/rain field;
  - the OpenSea face classification (and with it the storm spawn area);
  - the weather-node grid, which is rebuilt from the generated navmesh every session.
- **Reusable and static:**
  - weather and storm model code and constants;
  - the season calendar;
  - `atmosphere.xml` presets;
  - the water material.
- **Created per world, not data:** the dynamic rain texture (`Scene.CreateDynamicRainTexture(32, 32)`).

**Lifetime and threading rules (for MMR-21):**
- Bind on the campaign loading thread inside `IMapScene.Load`, before `InitializeCaches`.
- All weather, wind and storm state is per instance: map scene, campaign, models, behaviours, `NavalDLCManager`, `StormManager`.
- `NavalDLCManager.Instance` is a static field, cleared at game end.
- `Campaign.MapDiagonal`, `MapMinimumPosition` and `MapMaximumPosition` are statics that each map load rewrites.
- A switch needs no explicit clearing beyond binding the new world in its own `Load`.

**R07 status (for MMR-21):**
- **Binding by private-field write:** recorded and tested as a contract.
  - Field names, types and layouts are checked when the map loads, and the probe confirmed every consumer reads the written data.
  - It is not a public API: R07 must accept it explicitly as the diagnostic contract, or require an alternative before production. The candidates are a public `Scene.GetWindFlowMapData` call, or a wind-only `INavalMapSceneWrapper` decorator.
- **Terrain snow texture:** MMR-256 (see `docs/water-climate.md` "Terrain snow"). The terrain reads the texture its scene names (`dynamic_flowmap_texture_name`), so the world's raster is written as a named PNG and loaded by name before the scene's `Read`; `Scene.SetDynamicSnowTexture` is never called (no effect after `Read`, hangs before it, MMR-249). Whether the terrain draws it is MMR-247's live check, with the permanent-white route as the fallback.
- **Native resource release:** unverified. Managed release is proven.
- **Storm wrap across a periodic seam:** not implemented; MMR-65.
- **Authored `flowmap.dds`:** untested.

**Not covered, and recorded limits:**
- **Terrain snow visuals:** the "no safe creation path" of this run was overstated. `Texture.CreateFromByteArray` and `LoadTextureFromPath` work in this build (the world preview), and the memory risk is only the vanilla binder's `GetPixelData` readback, which nothing calls. What MMR-249 found instead is that a texture bound with `Scene.SetDynamicSnowTexture` never reaches the shader. MMR-256 binds the world's snow by name (above); MMR-247 proves it in game.
- **Native resources** owned by the previous engine `Scene` (the dynamic rain texture, the wake renderer) cannot be observed from managed code. Their release is unverified.
- **Authored `flowmap.dds`:** not tested. The binding uses the private-field contract. An authored file in the generated scene folder would be read by `Scene.GetWindFlowMapData` only through the vanilla binder, which also binds the Calradia snow texture.
- **Owned elsewhere:**
  - storm movement paths, wakes, pirate spawns and climate tuning on production geography (MMR-39);
  - storm wrap across a periodic seam (`Storm.set_CurrentPosition` kills storms outside the borders; MMR-65);
  - the saved weather-node index (MMR-49).
- **Synthetic setup:**
  - The player received a culture ship hull and a skeletal crew.
  - The storm was spawned directly with `CreateStormAtPosition`.
  - Natural storms also spawned during sailing. Samples within 1.5× a storm's radius and stationary samples were excluded; no sample was near a storm.

## Periodic east/west seam (2026-10-14, MMR-63)

Run `generated_campaign-12348` (module `82406613…`) ran one periodic fixture world, `mnbl_00063042`, through every check below in one process. The checks covered routes, travel in both directions, proximity, visibility, an engine-started encounter, a battle and return, a strip click, and a save.

| | |
|---|---|
| Evidence | `evidence/runtime/world-wrap-probe.json`, written by `tools/summarize_wrap_probe.py`; development history runs 26492, 26432, 21844, 5344, 14800, 16084, 19568, 3676, 11596, 19752, 31032 and 7136 (listed with their SHA-256; those under `evidence/runtime/in-game/` are collected; the rest are local result files under `%LOCALAPPDATA%`, are not portable, and are recorded by hash only) |
| Probe | `src/MNBL.ProceduralWorld/Diagnostics/WorldWrapProbe.cs` (`wrapProbe=1`; `wrapTravel=0` skips the travel legs for reruns) |
| Runtime layer | `CampaignBootstrap/PeriodicWorld.cs`, `PeriodicSeam.cs`, `PeriodicPatches.cs`, and the periodic queries in `DiagnosticMapScene.cs` |
| Fixture | `tools/scenebin/build_periodic_world.py` → `build/research-wrap63/fixture-c` (FWGC++ seed 42, periodic in x) |
| Research | `task-notes/mmr63-periodic-seam-research.md` |

**Layout and canonical coordinates.**
- The logical world is x ∈ [0, 800). It is stored at physical x = logical + 400 inside a 1600-wide terrain.
- Each side carries a 400-wide strip that copies the other side of the seam. The strips are displayed, and they also carry routing: a seam-crossing path runs to the target's image inside a strip.
- Every campaign object keeps a canonical position in [400, 1200). Settlements are placed only there.
- The strips hold four visual-only settlement copies: scene entities with no campaign identity. The probe found all four, no campaign object named as a copy, and unique settlement IDs.
- Width 800 was chosen as a multiple of the LocatorGrid's 160-unit hash period for an earlier plan that patched the grid's distance filter. That plan was superseded by the wrapper postfixes below, which do not depend on it.

**Poles.**
- The source's first and last rows are single-node pole aliases. The fixture keeps them as rows 64 and 192 and pads above and below with the pole rows' ocean.
- North and south are therefore neither connected nor cropped.
- Polar distortion is recorded in the fixture's placement: east–west map distance overstates sphere distance by 1/cos(latitude). That is ×2.0 at 60° and ×5.76 at 80°, and unbounded at the pole row.
- This needs explicit technical acceptance, or a projection, in the production generator (W07/MMR-64).

**The strategy, and every mandatory bypass.**

The native pathfinder cannot cross the seam: a continent cut by it is two native islands. The layer therefore works in four parts.

1. **Routing (owned `IMapScene`).**
   - `GetPathBetweenAIFaces` and `GetPathDistanceBetweenAIFaces` answer with the target's nearest image (as given, +W or −W). The strips are W/2 wide, so the image of every shortest seam-crossing route lies inside the terrain.
   - Exact pruning: a straight-line lower bound and a tightened native limit.
   - Within 1e-3 the earlier image wins, in the order as given, then +W, then −W. The probe found an exactly equal pair (500→900, W/2 apart), and the as-given image was kept.
     - That pair cannot tell "as given" from "east wins", because the as-given image is the east one there.
     - The code also has two tie rules: `PeriodicWorld.DeltaX` breaks ties east, while `NearestTargetImage` keeps the as-given image. MMR-64 must settle one rule.
   - The settlement navigation cache, which the distance models read, is built through these queries, so settlement-to-settlement distances are periodic with no model replacement.
2. **Rebase pass (`PeriodicSeamBehavior`, every campaign tick).**
   - A party that leaves the canonical range moves by ±W together with its path points, `NextTargetPosition`, `NextLongTermPathPoint` and the main party's map camera fields.
   - Saved order targets that an AI behaviour placed across the seam are moved to their canonical image: `MoveTargetPoint`, `_pathLastPosition`, `_targetPosition` and the navigation-transition end. Planar offsets around a seam settlement land in a strip, and run 19568 caught exactly that.
3. **Harmony patches (Bannerlord.Harmony; decision 002).** These cover the non-virtual planar checks. Each is inert without a periodic world, and all 11 applied:

   | Target | Change | Why |
   |---|---|---|
   | `MobileParty.FindNextLocatable`, `Settlement.FindNextLocatable` | postfix: continue an exhausted search whose circle crosses the seam at the image position | `LocatorGrid<T>.FindNextLocatable` filters with planar distance, and Harmony cannot patch that internal generic ("The given generic instantiation was invalid"). Game code reaches it through these wrappers at 69 + 12 sites, visibility included. Three direct callers bypass them and stay planar: `MobilePartyAi.GetBehaviorForNearbySettlementToFlee`, `PeaceBarterable.Apply` and `MapTracksCampaignBehavior.QuarterHourlyTick` (MMR-65's consumer audit). |
   | `PartyBase.IInteractablePoint.CanPartyInteract` | `Vec2` subtraction → periodic difference | encounter range |
   | `PartyBase.CalculateVisibilityRangeOfMapPoint` | same | visibility |
   | `MenuHelper.EncounterAttackConsequence` | same (2 sites) | the battle's `PatchEncounterDir` |
   | `MapEvent.GetEventDirection` | same | the map-event visual direction |
   | `DefaultMapDistanceModel.GetDistance`, `NavalDLCMapDistanceModel.GetDistance` (all overloads) | `CampaignVec2.Distance` → periodic distance (11 sites each) | the party legs (party→settlement entrance, party→party, party→point) are planar. AI destination and target scoring reads them. |
   | `MapState.ProcessTravel` | prefix: display point → canonical; transpiler: periodic click direction | the terrain-click order (`MapScreen.HandleLeftMouseButtonClick` is its only caller) |
   | `NavigationHelper.FindReachablePointAroundPosition` | postfix: result → canonical image | a random reachable point for map-event finalize, encounter-radius teleports, spawns, army dispersal and land patrols. Outside a tick it could fall in a strip: run 11596 started a battle at x = 1200.29. |
4. **Not needed.** `MapEvent.Position` is one party's position, not a midpoint: the attacker's for field battles, the defender's for raids, forced supplies and volunteers (`MapEvent.Initialize` IL_01c8–01d1). It stays at the parties.
5. **Identified but not implemented** (from IL; none is exercised by the probe):
   - **Army members.** `MobileParty.DoUpdatePosition` IL_00ff–0112 steers an attached member by a planar difference: leader `NextTargetPosition` + formation slot − own position. IL_02a2–02bb then moves it along that difference.
     - The rebase pass moves parties one at a time. A member left on the other side of the seam from its leader would therefore see a difference of about ±800 and walk the long way.
     - Fix: rebase armies together. Owner: MMR-65 (army and fleet followers).
   - **Direct `LocatorGrid` callers:** the three listed in the patch table (MMR-65).
   - **`NavigationHelper.FindPointAroundPosition`** (bandit and pirate spawns, naval patrol targets) has no canonicalizing postfix. The per-tick target pass catches its results only after the next tick (MMR-65).
   - **The save window:** see canonical-at-save below (MMR-49).

**Results.** Each row is asserted by the probe or by a summarizer check, except where marked *not asserted*.

| Criterion | Observed |
|---|---|
| Strategic routes | land town→town 75.5 and 76.4 periodic; the planar route was not found (different native islands). Sea port→port 75.7 against planar 730.2 and 731.4. Straight-line 75.3 against 725. |
| Distance model | NavalDLC `MapDistanceModel`: town→town 75.9 (planar 725). Player→hostile party across the seam 0.30 (planar 799.7). Player→the west town 9.96 (straight periodic 9.96; that path is a planar leg to the nearest entrance plus the cached settlement distance). |
| AI destination choice | indirect only. The AI legs are scripted (`SetDoNotMakeNewDecisions`, then `SetMoveGoToPoint`), which proves AI route selection, not destination choice. Destination choice rests on the distance-model inputs in the row above. The run also had 30 crossings the probe did not drive (40 rebases, 10 probe-driven); they are not attributed to parties. |
| Travel, both directions | player land, AI land, player sea, AI sea, east→west and west→east: 8/8 arrived. Each leg had one rebase, a step of at most 0.37 (no map-width motion), about 71–74 units travelled, and stayed canonical throughout. The map camera's maximum distance from the player was 32.2–32.6 on every player leg, sampled from 1 s into the leg (asserted < 40 by `cameraFollowsAcrossSeam`). |
| Proximity and visibility | the player at x = 1199.8 and a hostile party at 400.1: 0.3 apart through the seam, 799.7 planar. The party locator finds it, it is visible after 0.32 campaign hours, and `CanPartyInteract` is true. |
| Engine encounter | the player intercepted, and the engine's own interaction check started the encounter with the parties on opposite sides (periodic 0.30, planar 799.7). After its meeting conversation, the engine created its own map event and opened the `encounter` menu. |
| Battle and return | battle entered through the MMR-60 entry (map event at the attacker, canonical), `battle_terrain_z` (Plain, 62 agents; scene and agent count *not asserted*). `PatchEncounterDir` (−0.678, −0.735) equals the periodic direction; planar would be (1.0, −0.0006). Return displacement 0.57 (≤ 0.635), canonical. |
| Strip click | `ProcessTravel(1204, y)` (the strip copy of logical 4) stored canonical (404, y). The party crossed in 6.5 units with one rebase. |
| Save near the seam | saved with the player 0.0002 from the seam, then deserialized with the official `MBSaveLoad.LoadSaveGameData`. 19/19 parties and 95 saved `MobileParty` positions equal the live values, with 0 non-canonical. The file was deleted afterwards. |
| No leftovers | no non-canonical party at the end. 26 AI order targets and 10 engine placements were moved to their canonical image during the run (counters, *not asserted*). |

**Canonical-at-save (defined, with one known hole).**
- **Canonical after each tick's rebase pass:** `_position`, `MoveTargetPoint`, `_targetPosition`, `_pathLastPosition` and `EndPositionForNavigationTransition`. NaN is the engine's unset sentinel.
- **Not yet guaranteed at the instant of a save.**
  - Order of a frame: `MapState.OnTick` runs `SaveHandler.SaveTick` (IL_0022) before `OnMapModeTick` (IL_004f). Inside `Campaign.Tick` the rebase listener fires before the hourly AI (`MobilePartyHourlyTick`, `TickPartialHourlyAi`).
  - So a target an AI behaviour places in a strip during a tick stays non-canonical until the next tick, and a save in between would write it. `MobilePartyAi.BehaviorTarget`, also a saved `CampaignVec2`, is neither canonicalized nor checked.
  - The probe's save (0 of 95 non-canonical) was not built to hit this window.
  - Fix, owned by MMR-49: canonicalize in `OnBeforeSaveEvent`, and add `BehaviorTarget` to the canonicalization and to the save check.
- `NextTargetPosition` is steering state. Mid-crossing it can lie in the adjacent image, and `ComputePathAfterLoad` recomputes the path from `MoveTargetPoint` after load.
- Settlements never move.

**Not covered, and recorded limits:**
- **Resume from the save:** the save was deserialized, not resumed. Resuming a generated campaign needs the world restored before deserialization (MMR-49/MMR-19). Fleets, anchors and both-sides seam saves are MMR-49.
- **Labels, selection and tooltips:** these are not bound across the seam. Nameplates use canonical positions, so a settlement across the seam shows no label over its strip copy. The copies are not pickable. Mobile parties have no visual copy at all: the hostile party at 400.1 was logically visible from x = 1199.8 but drawn 800 units from the camera.
  - The seams are named: `PartyNameplateVM.RefreshPosition` and `SettlementNameplateVM` need the image nearest the camera. Display copies must register to the canonical `MapEntityVisual`. The entity click's native `Scene.DoesPathExistBetweenFaces` (`HandleLeftMouseButtonClick` IL_007c) bypasses `IMapScene`.
  - All of that is MMR-66's presentation work. The click test drives `MapState.ProcessTravel` directly; the native scene raycast is not simulated.
  - Resolved by MMR-66 (run 33944, `docs/periodic-presentation.md`): nameplates and visuals at the image nearest the camera, settlement display copies registered to the canonical visual, and a native terrain pick in the strip.
- **Tracks** (`MapTracksVisualManager`), weather and storm wrap, and the camera x clamp at the terrain edge are not exercised. The strips keep the camera inside the terrain. (MMR-65 and MMR-66 later exercised tracks across the seam; MMR-66's canonical camera never reaches the clamp.)
- **Armies, sieges, raids and embark/disembark at the seam** were not run (army steering above; MMR-65).
- **Locator image pass:** tested from the east side only. A party that is non-canonical for one frame skips the image pass, because a non-canonical centre marks the image pass. This gives a one-frame proximity or visibility miss before the rebase.
- **Camera fields:** the camera shift moves every `Vec2`/`Vec3`/`MatrixFrame` field of `MapCameraView` within W/2 of the old party x. That is a heuristic. In this run only the three intended fields moved. It misses the `CampaignVec2` `_lastUsedIdealCameraTarget`. Production should name the fields (MMR-66). MMR-66 replaced the shift with one canonical camera whose named fields are rebased by whole widths.
- **Battle scene choice:** the battle-index map does not wrap (MMR-60), so scene variety may change across the seam. Scenes are chosen from canonical positions only, so the choice is deterministic.
- **Remaining planar heuristics:** the survey counted 121 campaign methods with planar distance calls. That count includes the two `GetDistance` methods now transpiled; the rest (AI scoring, flee helpers, tracks) still measure planar. The research expects them to cause scoring or cosmetic errors near the seam rather than movement errors. That is not verified method by method; MMR-64 owns the distance contract.
- **Cost:** the strip layout doubles the terrain and the navmesh. The All navigation cache took 419 s in the evidence run on this 1600² fixture (Default 19 s, Naval 128 s). Narrower strips with portal stitching are the production option.
- **Campaign time:** StoppablePlay did not advance the setup; the probe uses UnstoppablePlay, as the baseline probes do.

**Synthetic setup (recorded):**
- War was declared on a kingdom that does not own town 1, and peace restored after the battle.
- An existing hostile patrol and the player were placed either side of the seam. The hostile is held in place.
- The player received ships for the sea legs and disembarked with `DisembarkToPosition`.
- After about three fast-forwarded days the party was starving, with every troop wounded and the hero wounded (recorded as `playerStateBeforeBattle`). The probe heals the hero and the wounded troops before the battle, because the encounter `attack` option needs both.
- The AI legs use `SetDoNotMakeNewDecisions`.
- The engine's meeting conversation is closed with `EndConversation`. The engine then created its own map event and opened the `encounter` menu.
- The probe restarts the entry the way the accepted MMR-60 probe does (`RestartPlayerEncounter`, `StartBattle`, the `encounter` menu, `attack`) at the same positions. Run 19568's attempt to use the engine's own menu directly hit the wounded-party condition above, and was not retried after the heal.
- Only a land encounter was run at the seam. A naval encounter at the seam was not run; MMR-60 covers naval battle entry and return away from the seam.
- Caravans are excluded from the naval AI choice. `CaravansCampaignBehavior` re-issues their orders regardless of `SetDoNotMakeNewDecisions`, and in run 19752 the chosen caravan stopped after 9 units without crossing.

## Generated-world mission transitions (2026-10-14, MMR-60)

Run `generated_campaign-22868` used module `19fa06b0…` (the tree at MMR-60 acceptance; it also compiles the then-inactive MMR-63 periodic plumbing). It entered and left three missions in each of the two generated worlds (`mnbl_0000002a`, `mnbl_0000002b`), in one process:

- a land field battle;
- the generated town_1's street location;
- a naval battle at the west map edge, ended by retreat.

| | |
|---|---|
| Evidence | `evidence/runtime/mission-transition-probe.json`, written by `tools/summarize_mission_probe.py`; history runs 25112, 30296, 19676, 5532, 24092 and 17968 (the independent reviewers' reproductions), 14624, 33472 and 13672 (in 13672 world 0's camera was zoomed by outside input, which the zoom assertion added afterwards now catches) |
| Probe | `src/MNBL.ProceduralWorld/Diagnostics/RuntimeMissionProbe.cs` (`missionProbe=1`) |
| Battle-index map | `src/MNBL.ProceduralWorld/CampaignBootstrap/GeneratedBattleTerrain.cs` |

The mission screenshots were reviewed: plains and forest land battles, the town street, and two ships at sea.

**Battle scenes come from the generated map patch, through a managed contract.**

How the engine picks a battle scene (verified in IL):
- `MenuHelper.EncounterAttackConsequence` reads the patch at `MainParty.Position`. It sets `isNaval = PlayerEncounter.IsNavalEncounter()` and calls `DefaultSceneModel.GetBattleSceneForMapPatch(patch, isNaval)`; NavalDLC does not replace the SceneModel.
- It then builds the `MissionInitializerRecord`:
  - `TerrainType = GetFaceTerrainType(MainParty.CurrentNavigationFace)`;
  - `SceneHasMapPatch = true`;
  - `PatchCoordinates = patch.normalizedCoordinates`;
  - `PatchEncounterDir = Normalize(attacker − defender)`, which is planar.
- Finally it calls `OpenBattleMission`, `OpenNavalBattleMission` or `OpenCaravanBattleMission`.

How `SandBox.MapScene.GetMapPatchAtPosition` reads the index map:
- It is managed code over the private `_battleTerrainIndexMap` and its width and height.
- The cell is `floor(position / _terrainSize × dimension)`, clamped to the map.
- Each cell holds two bytes. Byte 0 is an `sp_battle_scenes` map index; byte 1 packs the patch coordinates as `(b & 15, b >> 4) / 15`.
- A null map returns index 0.
- The only native query in the selection path is the navmesh face lookup behind the terrain at the party, run against the generated navmesh. There is no heightfield or texture query.

What `DefaultSceneModel` then does with the index, taking scenes whose `MapIndices` contain it and whose `IsNaval` matches:

| Matches | Result |
|---|---|
| exactly one | that scene, deterministically |
| several naval | those whose `Terrain` equals the terrain at the party; otherwise random among them, silently |
| several land | random, with a `FailedAssert` |
| none | silently random among scenes of the party's terrain; `FailedAssert` only if no scene has that terrain |

Installed data: each of the 180 land indices names exactly one land scene. Only 8 indices also name exactly one naval scene: 35, 58, 110, 116, 117, 122, 207 and 225.

**Before MMR-60 this was a silent failure.** The generated `DiagnosticMapScene` left the map null, so every patch read index 0. Every battle therefore opened a random scene of the party's terrain, with no assert. Run 30296 shows the same silent fallback for a land-only index: a naval battle on index 215 opened `battle_terrain_coastal_01`.

**The generated battle-index map** is built in `AfterLoad`, after generated navigation registration. Its build rules:
- Cells use the reader's own size (`_terrainSize` / dimension; 4 m here).
- Each cell is sampled at 3×3 points against the generated navmesh. Each face is classified by the `PartyNavigationModel` as land-valid, naval-valid (CoastalSea, OpenSea, Lake, River, …) or unreachable.
- A land-only cell gets an index that names exactly one land scene of its terrain.
- A cell a ship can enter gets one of the 8 land-and-naval indices, preferring its sea terrain.
- Terrain without scenes of its own borrows another terrain's scenes, and each substitution is counted. Here: Mountain→Steppe for unreachable mountain cells (1176 and 286), and Lake→CoastalSea (30 and 20).
- Within each 16-cell block, the index comes from a stable hash seeded by the world, and the patch coordinates follow the cell's place in the block.

Results:
- The map's identity includes the SHA-256 of the installed scene table (`3aa0154c…`).
- It builds in 93 ms (200×200) and 101 ms (160×160).
- It is deterministic: the map SHA-256 for each world is identical across runs (`ce4d97bc…`, `93ef0cc0…`). The reviewer re-derived the earlier version bit for bit offline.
- Sampling is not exact coverage: slivers under about a tenth of a cell along diagonal face edges can be missed. A reviewer's offline recount (scratch, not repo evidence) found no naval-reachable cell with a land-only index on these grids.

Observed in every battle (asserted):
- the opened scene equals the single scene the patch names;
- the initializer record has `SceneHasMapPatch`, with `PatchCoordinates` equal to the generated patch;
- `TerrainType` equals the party's generated face group (Plain 1, CoastalSea 18).

The mission terrain is the stock template's, chosen by generated geography; heights were sampled around the player's agent. This is the valid "template reuse". No campaign heightfield reaches the mission.

**Returns preserve the world.** Across all six missions:
- **Position:** the main party came back within 0.62 (land) and 1.86 (naval) map units of its entry position, asserted against the vanilla bounds.
  - That is vanilla `MobileParty.TeleportPartyToOutSideOfEncounterRadius`: up to 1.25 × the encounter radius, so 0.625 on land (radius 0.5) and 1.875 at sea (NavalDLC radius 1.5).
  - It stayed in the same land/sea domain; naval returns are at sea.
- **Camera:** before entry and after return, the map camera's actual frame had stopped moving (origin change under 0.002 per frame for 30 frames; no settle timed out). The party lay on the camera's view line, with a lateral error of at most 0.0083 (asserted ≤ 0.1) at a forward offset of 32.19–32.21 (asserted unchanged across each mission within 0.05). The camera distance stayed 38 and the bearing 0, both asserted unchanged; 0 is the only bearing exercised.
- **World state:** the following were identical before and after each world's missions:
  - the map-scene wrapper object and the native scene pointer;
  - the battle-map SHA-256;
  - settlement IDs, names and positions;
  - party and hero counts;
  - the navmesh face count;
  - the land and sea path distances between the two towns.

  Missions do not dispose or recreate the map scene.
- **Not covered:** party visual entities and nameplate objects are not fingerprinted; labels are covered only through settlement names. The generated scene has no `medit_water_plane` entity, either before or after.

**Settlement locations use prototype scenes.** town_1's `center` location opened `empire_town_q`, which comes from prototype `town_EN2` (`SceneHasMapPatch=false`). The scene is chosen by settlement prototype, not by geography; mapping generated settlements to location scenes is W06/P05 work.

**Edges and the periodic seam. This is open and blocks R07.**
- The naval pair was placed at sea 3 units (world 0) and 15 units (world 1) from the west edge.
- Patch sampling clamps (asserted): points past either edge read that edge's cell.
- These worlds have no east/west seam, so the following is untested:
  - The patch map does not wrap: the east and west edge cells differ, and blocks restart at x = 0.
  - Vanilla `PatchEncounterDir` is planar and has no wrap hook.
- Periodic patch sampling and wrapped encounter direction must be proven by MMR-63/64 before R07 can pass. (MMR-63 has since proven the wrapped encounter direction; see the section above. Patch sampling still does not wrap.)

**Synthetic setup (recorded):**
- `DeclareWarAction.ApplyByDefault(player clan, kingdom 2)`, because generated worlds have no bandits or wars.
- Existing hostile patrol and naval-patrol parties relocated next to the player.
- A crew of culture basic troops and one culture ship hull granted. The ship departs by `SetSailAtPosition` from the generated port.
- The encounter is started directly: `PlayerEncounter.RestartPlayerEncounter`, then `StartBattle`, then `GameMenu.ActivateGameMenu("encounter")`, then the official `attack` option.
- Deployment finished through `DeploymentHandler.FinishDeployment`.
- Retreat through `Mission.OnEndMissionResult`. After the retreat, the encounter is closed with a forced `PlayerEncounter.Finish(true)` rather than an encounter-menu option; the rgl log shows "Battle continues" and MapEvent `WaitingRemoval` at that point.
- No victory is claimed.

**W06 contract and open items.**
- The compiler or runtime must install a battle-index map for the active world, with this layout and these rules, before any encounter. A null or unknown index fails silently: the scene model picks a random scene of the party's terrain with no assert.
- Cells must be classified by the full set of naval-navigable terrain, not only CoastalSea/OpenSea.
- Only 8 installed indices resolve both land and naval battles deterministically, so coasts have little variety.
- Mountain, Forest and Snow terrain need agreed substitution rules or more scenes.
- Location scenes need a generated-settlement mapping.
- Periodic seams (MMR-63/64) block R07.
- Nothing here needs a real campaign heightfield. The map patch is a managed index, not a terrain query.

**Resolved by MMR-42** (`docs/mission-scenes.md`):
- **Battle-index map:** installed for every generated world before any encounter, through `GeneratedEncounterResolver` over a versioned scene catalogue that the game checks against its loaded table.
- **Naval-navigable terrain:** cells are classified by the full set, and each sea terrain gets its own naval scene through the engine's terrain tie-break. That gives 66 deterministic coastal indices instead of 8, and makes River and UnderBridge scenes selectable.
- **Substitution rules:** agreed for Mountain, Forest and Snow, with biome and relief from the compiled masks.
- **Settlement location scenes:** checked against the contracts the installed code reads, with `prototypeAndScenes` claimed and validated.
- **The seam:** canonical patch sampling and the short-way direction, proven at a seam battle in game.
- **Guard:** a mismatch guard records any battle scene choice that departs from the generated patch.

**Played by MMR-52** (`docs/land-missions.md`): on world A's designated save (world B for what A lacks), the following ran in game, each opening its mapped scene and returning the party where the installed path puts it (run 33816, 163/163):
- a battle on every battle class present, including three substituted classes, plus a bridge crossing and a retreat;
- a town center with a notable's conversation, and its tavern;
- a castle and a village;
- a siege assault with deployment;
- a defeat with capture and escape;
- a hideout cleared.

Wetland is absent from both worlds.

**Measured by MMR-56** (`docs/first-test-profile.md`): the F03 profile end to end on one machine, from the setup screen's Confirm to play (run 35468). A new world takes 32.7-55.7 s to generate and about 400 s to start the first time, while its navigation caches are generated on the engine thread (the game does not respond for 383 s). A save loads in 14.5 s afterwards. The game peaks at about 5.1 GB of working set. Seeds 42 and 73 from the same build differ in geography and engine routes. The setup screen states both waits.

**Played by MMR-53** (`docs/naval-missions.md`): on world A's designated save, War Sails' installed paths ran in game (run 46036, 104/104):
- a ship bought through the port screen's trade handler and one repaired at a port;
- port-to-port and island voyages ordered as map clicks, a harbour-village drop-off, and a coastal landing by a click on land, each with re-boarding at the anchor;
- a land siege of a port town that activated War Sails' blockade, its arcs on water;
- a pirate battle at sea (a retreat), a seaborne village raid through War Sails' landing, and a naval defeat with capture and the raft;
- AI sea travel and arrivals, island port towns included, and the pirates' patrol zones exactly the world's served spawn points.

Boarding, crew reinforcement, battle damage and fought outcomes are not exercised (the doc's limits).

## Runtime native surface sequence (2026-10-13, MMR-16 accepted)

The C++ bridge now encodes freshly generated MNBLHF01 fields into native terrain,
navigation and scene bundles. Offline verification passed 11,956 assertions;
the root independently decoded seed 42 and checked heights, normals, weights,
physics and navigation. Receipt: `evidence/runtime/worldbridge-surface-offline.json`.
This does not establish shipping-client behavior by itself.

PID 27392 rendered the asymmetric calibration surface, then crashed inside the
combined terrain-node export stage. The exact failing export member was not
isolated. Neither export has an identified managed gameplay consumer in the
pinned assembly scan; both are withheld from later gameplay query suites.

PIDs 34660 and 12748 completed calibration height, ground, ray, projection and
adapter queries without a native crash. Sampled height errors were at most
0.0262 m; independently planar normal error was 0.640 degrees. One grazing
camera ray at the narrow ridge exceeded the recorded 0.1 m ray tolerance
(0.2002 m); this remains a failure. (Superseded: the 0.2002 m came from a
historical reference model; the same ray has a reference error of 3.8e-05 m
in the accepted run 32896, `docs/runtime-backend-decision.md` R02.) PID 12748 fixes visible camera zoom and
records an actual off-center click matching the quantized source within
0.0303 m through both Scene and SceneView. Native selection returned four
intersections in a scene containing seven engine-created entities; the prior
assumption that empty authored XML implies zero intersections was incorrect.

Both runs failed when replacing the same scene's terrain.bin because the engine
retained its file handle. PID 12748 also used Scene.ClearAll and the installed
screen-pop resource-cleanup call. It detected the lock before changing any
files, exited naturally and changed no saves. Evidence is under
`evidence/runtime/in-game/runtime_surface-<pid>*`.

PID 32944 then completed calibration, freshly generated seed 42 (800 m extent),
and freshly generated seed 43 (640 m) in the same process. Each immutable scene
was registered at runtime through the installed downloaded-map API; resolved
paths and loaded names matched the new module directories. Root observed all
six map/zoom views. Both outside-bounds rays missed at (720,320) after switching
to the smaller map. The game exited naturally with no save changes. The raw
result remains `error`: selection incorrectly required terrain hits to identify
a GameEntity, and the original collision reference used the wrong cell diagonal.

Independent analysis of the saved encoded fields and native measurements found
that 0.03 m vertex-height truncation toward zero, followed by triangles along
the (x,y) to (x+1,y+1) diagonal, predicts the sampled native collision hits.
The empirical collision model is separate from the original encoded source
field. Offline replay passed 654 assertions, including 304 saved ray comparisons
with maximum error 0.000337 m. Exact bin-boundary rounding remains unresolved.

Prospective PID 32896 completed all three maps and both zooms with no failed
checks, including two previously unsampled cell-interior points per field.
It generated and registered each scene after launch and verified the resolved
path against its installed immutable bundle. The root inspected all six saved
frames: calibration has an off-center peak, diagonal channel and narrow ridge;
seed 42 has a broken basin/ridge pattern; seed 43 has a different central valley
and ridge arrangement. Both zooms visibly change framing, with textured terrain
and rocky slopes rather than missing-material surfaces. The distant captures
are hazy; broad art quality and LOD are outside this experiment.

Actual physical clicks were recorded for calibration and seed 42 in PID 32896
with maximum collision-reference errors 0.000000954 m and 0.0000446 m. Its
seed-43 hold expired without a physical click. Seed-43 physical-cursor evidence
instead comes from PID 32944 and the separately recorded offline correction;
that historical error receipt has not been rewritten as a passing run.
All stages finalized and performed post-pop cleanup. Both ray APIs missed at
(720,320) outside the new 640 m field in both zoom rounds, excluding active old
800 m geometry there. Inactive file-cache eviction and allocator timing remain
unproved; immutable scene files are retained as evidence.

Sandbox PID 25008 then passed actual land-party grounding. Its strategic entity,
visual getter and CampaignVec2.AsVec3 agree at (776,280,-25.7340087890625).
The independent encoded-source difference is 0.009022 m, within the 0.05 m
tolerance; entity-to-terrain difference is 0.00000763 m. Campaign time stayed
paused. The earlier PID 34684 failed to resolve the loaded visual-manager type;
the fix uses the already-loaded assembly inventory and records its identity and
hash. This measures a strategic anchor, not animated foot contact or movement.
The campaign still retains official settlement/navigation scaffolding.

Both final processes exited normally with no changed or new saves. Receipts are
`evidence/runtime/in-game/runtime_surface-32896*` and `sandbox_boot-25008*`.
The independent mandatory-path audit is `surface-coverage-review.json`.
MMR-16 was accepted after independent review. Its acceptance is separate from generated campaign-content and routing
acceptance; unused crashing bulk exports and mission-only query paths are not
silently counted as passed.

## Generator menu integration (2026-10-13)

Shipping PID 19240 executed the registered developer-menu callbacks for seed 42,
seed 43, an invalid request, a controlled owned-producer abort, cancellation and
seed-42 recovery. All six outcomes matched. Exact request hashes remain unchanged
for the valid seeds; their heightfields differ, and recovery reproduces seed 42.
The invalid request returned the producer's failure-v2 `INVALID_INPUT` document
for size 2 with exit 2. Abort and cancellation closed the owned child with exit
1223. Each negative case preserved the previously published heightfield marker.

Producer peak working sets were 265,490,432 and 308,269,056 bytes for seeds 42 and
43. The application continued ticking during generation: 2,275 and 2,574 ticks,
with maximum observed gaps of 22.4 and 6.8 ms respectively. These are application
callback intervals, not isolated rendering or CPU costs. Root inspected the live
menu and all six engine captures. The suite invoked the actual registered menu
callbacks programmatically; physical mouse activation is not claimed. Queued
messages can appear in a following capture. The game exited with no save changes.

`evidence/runtime/generator-probe.json` binds the six receipts, pinned producer and
reader, immutable requests, process arguments/PIDs, exit codes, memory samples,
marker preservation and captures. Failure documents are parsed separately from
successful output. Process work stays off the application thread; UI messages
are queued. Cancellation and host interruption dispose the iterator and wait for
tracked job cleanup before finalization. An unresolved cleanup timeout remains
non-final rather than declaring a clean exit.

This is source generation and recovery, not generated campaign activation. The
two deliberate failure actions remain developer diagnostics. Independent review
verified all six receipts, nine owned child-process records and 2,709 source-file
hashes. The managed host-timeout regression passed 128 assertions across five
cases against the actual host/context source; its game API doubles do not prove
native behavior. No further game run was required by that review.

## Editor content installed and reference scene inspected (2026-10-13)

The official-only modding-kit process 5784 did not reach the editor. After the
Workshop descriptor warnings, it reported missing `ui_naval_common_1` and
`conv_aserai_shipyard_night_envmap` textures. This is a separate editor-content
problem; it does not invalidate the successful shipping-client runs below.

At that time the installed Steam manifests registered the base kit depot 1393601
and game War Sails depot 2927200, but no War Sails Modding Kit package 4456490.
[TaleWorlds' package description](https://store.steampowered.com/app/4456490/)
states that this separate free content supplies the files required to use War
Sails in the editor. The user approved installation, and Steam installed depot
4456490, manifest 1015157637537524692 (14,715,683,520 downloaded bytes).

The owned kit process was closed without opening or saving a scene. All seven
official NavalDLC `Main_map` files match their prelaunch hashes. Evidence:
`evidence/baseline/editor-startup-5784.log` and
`evidence/baseline/editor-scene-preservation-5784.json`. Read-only editor
inspection was still unmet at that point.
`tools/Doctor.ps1 -RequireNavalEditor` now refuses missing or uncertain Steam
registration before editor work. Default checks continue to cover the shipping
installation. A registered depot alone never claims editor asset readiness.

After installation, both that check and all 24 API pins passed. Editor process
19324 opened the official NavalDLC Main_map and visibly rendered its terrain,
water and settlement assets. The Inspector showed `water_flowmap` and
`snapped_water_body` on a water entity, and `main_map_city_port` on
`town_ES1/town_port`. Scene Options exposed `level_1`, `level_2`, `level_3`,
`civilian`, `siege` and `looted`. Four captures and the actual inspection scope
are recorded in `evidence/baseline/editor-inspection-19324.json`.

The editor was closed using **No** at its save prompt. All seven tracked core
scene files match their pre-install hashes. The shutdown log reports ERC4095
(non-zero device reference count) after managed finalization; this is not
presented as a warning-free editor session. Static XML/native traces remain
distinct from directly observed Inspector properties. Independent review passed
the representative inventory, including the installed retreat/exit mechanisms
in `evidence/baseline/retreat-exit-contract.json`. This baseline covers disclosed
instrumented official content and repeatable measurements; it does not establish
generated-world gameplay.

## Stock baseline continuation (2026-10-13)

### Repeatable comparison fixture

PID 11588 successfully replayed PID 27112's save, party identities, field
placement, port and sea target. Replay validates the save hash, initial time and
position, loaded scene/module, and permitted navigation before using the inputs;
it refuses substitutions. `tools/Get-BaselineReplayArgs.ps1` generates the exact
arguments from the successful result and its prelaunch save inventory.

The first replay, PID 28868, stopped when the recorded naval enemy became
ineligible during travel; it wrote no saves. The corrected fixture explicitly
holds that party's campaign AI and excludes other-party encounters between field
teardown and naval teardown, then releases both restrictions. Field and naval
simulation advanced 5.0412 and 5.0115 seconds. Sea travel covered 7.1145 units on
the fixed route during the ten-second sample. A separately recorded synthetic
relocation after the measurement pins the naval encounter location. Exact
physics determinism is not claimed.

All four 11588 captures were visually inspected, the game exited normally, and
no existing or new saves were written. Independent review matched all 26 replay
arguments and the capture hashes. See
`evidence/baseline/combat-replay-verification.json`. This closes the random-route
comparison gap; the subsequent editor inspection is described above.

With MNBL disabled, PID 10828 cold-loaded the existing disposable `fwgc.sav`
into Thronderlag port. The official `nord_shipyard` mission rendered and finalized
on return; the exit was assisted by the user because injected keyboard input was
not being consumed. The launcher configuration and existing saves were unchanged.
Evidence: `evidence/baseline/continuation-10828.json` and its two captures.
Desktop control resumed on the next turn, and the stock game exited without saving.
Synthetic Tab/Escape remains unreliable, so the continuation below uses explicit
game-thread orchestration and is not labeled an unmodified vanilla run.

### Instrumented official-content continuation

PID 31116 started an official-content Sandbox, moved 17.4647 map units to
Tegresos in 3.8221 campaign hours, entered `empire_village_003`, returned to the
map, then entered and exited `empire_town_s`. Village arrival used normal party
movement; the town setup explicitly teleported to its gate. Mission entry ran the
official menu consequences; exit called the public `Mission.EndMission` and waited
for teardown. Both scene screenshots were visually inspected.

The run saved to a unique `mnbl_baseline_<guid>` slot in 1.794 seconds. Fresh
process 32644 loaded that save to a ready map in 9.741 seconds; party position and
campaign time matched exactly. Neither run changed any pre-existing saves, and
the load run created no saves. Both exited normally. The map resolves to
`$BASE/Modules/NavalDLC/`. Results and captures are under
`evidence/runtime/in-game/baseline_scenario-31116*` and `baseline_load-32644*`;
`evidence/baseline/save-roundtrip-31116-32644.json` records the comparison.

`baseline_scenario` and `baseline_load` are opt-in autorun probes, not menu actions.
The latter accepts only the unique diagnostic slot prefix. Reproduction uses
`Start-OwnedProbe.ps1`, `Collect-OwnedProbe.ps1`, then `Verify-BaselineSave.ps1`
with the two result paths. The exact module hash is recorded in each result.
Frame measurements are application-tick wall intervals at the configured 200 FPS
cap, not isolated render or simulation CPU cost. Travel sampling stops at arrival
to avoid including settlement-menu time. This official-content save result does
not establish generated-world persistence.

The continuation `baseline_combat` cold-loads that diagnostic save and exits without
saving its synthetic setup. PID 27112 rendered a 26-agent field battle in
`battle_terrain_biome_093b`, retreated to the map, entered `empire_shipyard`, used
the official set-sail menu action with a granted stock Knarr, travelled 7.124 map
units on CoastalSea, and opened `battle_terrain_coastal_04` with 25 agents and two
ships. Both battles completed deployment, advanced their simulation clocks by
5.0404 and 5.0020 seconds during five-second wall samples, and returned to the map
through the installed retreat path. All four captures were visually reviewed.
No existing or new saves were written. PID 28796 is the earlier successful run
without explicit simulation-clock measurement; it is not used to establish that
measurement. See `evidence/baseline/instrumented-gameplay.json` and
`evidence/runtime/in-game/baseline_combat-27112*`.

Troop/ship grants, town/port/bandit relocation and scripted retreats are explicit
instrumentation. Neither battle claims victory or exchanged damage. These results
establish the official-content lifecycle exercised by the harness, not a generated
campaign or unmodified UI playthrough. MMR13 acceptance review remains pending.

`evidence/baseline/scene-contract.json` inventories installed scene/XML references,
hashes, land/naval terrain group semantics and resource-provider collisions.
It distinguishes static findings from runtime/editor unknowns. In particular,
module-qualified scene loading does not isolate globally named materials/prefabs,
and terrain IDs named LandRestriction/SeaRestriction are not universally blocked
by the naval permission predicate. These findings guide the generated navigation
work; they do not establish playable generated routes.

## Current checkpoint: native terrain rendering and Sandbox boot (2026-10-13)

The native scene-bundle route now renders the generated seed-42 heightfield. PID
24860 used `NativeTerrainScreen`, following the installed `VisualTestsScreen`
lifecycle: agent renderer, `Scene.Read`, optimization, scene layer, readiness gate,
tick and engine screenshot. It reported `loading_finished`, readiness true,
800-metre extent, 2048 navigation faces and center height -23.55 m. The decoded
source expectation is -23.5701 m (native height quantization accounts for the small
difference). The screenshot was inspected; the generated relief is textured, with
foggy lighting and a stock outer-mesh backdrop that still needs removing.
The screen finalized and the module unloaded normally without a crash or shader
dialog. Evidence: `evidence/runtime/in-game/native-render-24860.{json,log,png}`.

Sandbox startup reached `MapState` and `NavalDLC.View.Map.NavalMapScreen` in PID
25644. The wrapper was `DiagnosticMapScene`, loading `mnbl_campaign_42` from our
module; rendering reached `loading_finished`. Heights at (260,260), (520,520),
(780,780) were 6.39, -23.55, 5.06999969 metres. The campaign remained paused,
autosaves were disabled, and the game exited cleanly. Existing saves were
hash-checked unchanged. Evidence: `evidence/runtime/in-game/sandbox_boot-25644.*`.
That first screenshot includes the Naval introduction inquiry. Final verification
PID 23152 dismisses that inquiry and captures the unobstructed paused campaign map;
its result is `ok`, final, with the same owned scene and sampled heights. It exited
cleanly and existing saves remain unchanged. Evidence:
`evidence/runtime/in-game/sandbox_boot-23152.{json,log,png}` and
`sandbox_boot-23152-observation.json`. The final build also exposes preview and
paused Sandbox actions in the main menu; the menu click itself has not been
separately exercised. Independent source review found no blocking defects.
The local
`build/campaign-fixture/mnbl_campaign_42` fixture deliberately retains official
campaign IDs, settlement XY, navigation topology/region IDs, weather/battle grids
and distance caches, with generated terrain and grounded settlement/nav vertices.
It is a bootstrap diagnostic, not generated campaign-content or navigation
acceptance. All 16,641 terrain samples match the scaled source within 0.000875 m;
all 540 settlement IDs have scene entities except `retirement_retreat`, which is
also absent from the official scene. See `evidence/runtime/campaign-fixture-check.json`.
The fixture is kept in ignored build output and must not be packaged as owned art.

The diagnostic manager installs its scene creator after the official submodule
initialization callbacks and before map loading. It reimplements `IMapScene.Load`
on a `SandBox.MapScene` subclass so the official/DLC casts remain valid. This
affects only the explicitly requested diagnostic new game; no `Main_map` override,
save-load hook, global distance-cache replacement or Harmony patch is installed.
Installed-build call traces and remaining assumptions are recorded in
`evidence/runtime/sandbox-startup-plan.json`.

The older procedural-mesh failures below are historical evidence; those probes
are not part of the current execution path.

No complete generated campaign backend has been accepted. Native generated terrain
rendering and paused Sandbox initialization are now verified. Production world
generation/activation, picking, generated land/sea routes, periodic queries,
generated campaign content and cold save restoration remain open.

## Backend candidates and stop conditions

The installed-build call-site matrix is
`evidence/runtime/backend-callsite-matrix.json`: 24 operation groups, all 39
`IMapScene` members, 31 concrete `SandBox.MapScene` casts and 32 direct native
screen-query sites. Each group records installed signatures/hashes, thread and
lifetime assumptions, initialization timing, serialization limits and a matching
probe or an explicit missing check. `evidence/runtime/lifecycle-extension-contract.json`
covers registration, load ordering and DLC model snapshots. These are managed
call-site traces, not proof that the native implementations satisfy the contract.

The next engine experiments must distinguish three candidates:

- **Native scene bundle with a `SandBox.MapScene` subclass.** The seed-42 fixture
  already renders and boots a paused Sandbox. It preserves concrete base-class
  casts and native terrain/navigation consumers. Its official identities, XY
  layout, topology, weather, battle grids and caches remain diagnostic scaffolding.
  Production viability requires generated resources to be created and rebound
  locally in the running game, before their consumers, without a per-world editor
  or restart. Stop this candidate if a required resource cannot be replaced or
  loaded safely under those constraints. A prebuilt fixture does not resolve that.
- **Procedural meshes plus a managed map adapter.** Mesh creation works, but the
  mesh does not supply a native terrain heightfield. The previous raycast failures
  and native picking/face bypasses remain counter-evidence to treating this as a
  complete backend. Stop unless every required native consumer has a demonstrated
  supported replacement; do not repeat the old raycast crash probes merely to
  reconfirm their failure.
- **Navigation prefab replacement within an indexed scene.** The two-face test
  establishes that one indexed owned filename can be rewritten and imported into
  a fresh scene. It does not establish campaign route rebuilding, correct land/sea
  transitions, live cache invalidation, ownership or seam connectivity. Stop if
  those required consumers cannot consistently use the generated navigation.

East/west wrapping and pole identity are requirements for every candidate.
`PartyNavigationModel` terrain permission, `IMapScene` routes, `MapDistanceModel`
strategic distances and native party movement are separate consumers. A passing
route or a registered model does not establish consistent topology across them.
The installed movement paths also dispatch work through `TWParallel`; changing
navigation or distance data while those readers are active has no proven safety
contract. Scene mutations must be isolated from campaign simulation, and the
generated immutable world data must be safe for concurrent queries.
The official 1.4.8 references expose these separately:
[MapDistanceModel](https://apidoc.bannerlord.com/v/1.4.8/class_tale_worlds_1_1_campaign_system_1_1_component_interfaces_1_1_map_distance_model.html)
includes distance/cache contracts, while
[PartyNavigationModel](https://apidoc.bannerlord.com/v/1.4.8/class_tale_worlds_1_1_campaign_system_1_1_component_interfaces_1_1_party_navigation_model.html)
includes navigation permission and terrain suitability. Installed call sites,
rather than documentation alone, determine the actual integration requirements.

The `capabilities` autorun entrypoint (`operation: inventory`) inventories public
symbols from the actual loaded assemblies and records their hashes. It invokes
none of the discovered engine methods and reports
`runtimeSupportEstablished: false`. Other operations return `unsupported_operation`; unknown or duplicate
loaded types are explicit outcomes. This is a reproducible diagnostic preflight,
not the engine execution experiment. Existing isolated entrypoints are
`native_render`, `sandbox_boot`, `scene_query`, `scene_sequence`, `nav` and
`generate:<seed>`. Their reports must be interpreted against their actual scope;
the dangerous historical `ray` probe is not part of this planned validation.

Save restoration must bind immutable world resources and object identities before
their first native or deserialization consumer. The successful official-save load
only establishes the stock ordering in `evidence/runtime/save-load-order.json`.
Stop any proposed hook that runs after the relevant references have resolved;
never regenerate a missing world from its seed as a load fallback.

`evidence/runtime/lifecycle-extension-contract.json` distinguishes binary save
deserialization, XML record loading and deferred object initialization. The
installed `RegisterSubModuleTypes()` callback precedes deferred initialization;
the binary object graph has already been read before it. It is therefore not
evidence of a safe hook before saved object references are resolved. That hook
remains unknown and blocks production generated-object restoration.

Model selection scans from the end of the model list, and the generic registration
overload links the new model to its previously selected base. NavalDLC takes its
own model snapshot during `OnGameStart`. For a manager-owned diagnostic, the
candidate insertion point is after `base.InitializeGameStarter` and before that
snapshot. Actual selected campaign, mission and naval model types must be checked;
a later registration or a launcher ordering assumption is insufficient.

The managed-only inventory executed successfully in shipping PID 9408 and exited
without changing or creating saves. All ten requested types were loaded. One
requested method name was corrected from the property name `HasTerrainHeightmap`
to its metadata getter `get_HasTerrainHeightmap` after checking the installed
inventory. The retained PID 9408 result describes the earlier selector and is not
presented as a runtime test of that correction. See
`evidence/runtime/in-game/capabilities-9408.json` and its observation record.

## Historical runtime failures and crash recovery

The separate `SurfaceProbeScreen` test in PID 30732 crashed after activation on 2026-10-09, before its measurement file. PID 25320 narrowed the failure to the raycast stage. PID 14300 isolated it further: visual-only rendering wrote a measurement and finalized normally after 30 seconds, while the separate native-ray test stopped between `before_raycast` and `after_raycast`. The scene remained black and its 60-frame `ReadyToRender` result was false. No rendered surface or successful ray query is claimed.

Crash dismissal no longer requires the user: explicitly activate the returned CSRSS `*_*` dialog window, then press Alt+N. This succeeded for both PID 25320 and PID 14300; fresh window and process checks confirmed exit. Coordinate clicks and Escape previously timed out. No crash files were collected or uploaded.

PID 14300 imported the official navigation control and reached the generated seed-42 import, which returned zero faces. The report previously discarded the earlier control sample on failure; the revised diagnostic preserves all counts and compares a prior-session file against newly written files. Installed `BannerTableau` and `VisualTestsScreen` initialization calls are recorded in `evidence/runtime/scene-creation-traces.json`. The next render experiment bypasses loading-state waits for its empty procedural scene. These experiments do not establish a campaign backend.

PID 29364 confirmed the official control has six faces and five successful routes from face zero. Newly created seed-42/43 filenames returned zero faces, while an identical file present before startup imported two. PID 25128 rewrote that indexed owned file to seed 43 and imported it into a fresh scene without restarting: the two face heights were 1.5 and 2.5, matching the new bytes. This supports a runtime replacement experiment with a fixed indexed filename; generated route execution, resource ownership, repeated replacement and campaign integration remain unproved. Reports are in `evidence/runtime/in-game/navigation-probe-29364.json` and `navigation-probe-25128.json`.

Both bypassing loading-state waits (PID 29364) and reading the installed `scn_item_tableau` scene (PID 25128) still produced a black view and false readiness at frames 60 and 1200. Both returned normally after 30 seconds. PID 29364 also recorded matching load/unload hashes in `evidence/runtime/in-game/lifecycle-29364.json`. PID 22208 used `mono_renderscene` and disabled the global loading window but remained at `filter_shadow_query`.

PID 7700 added `OptimizeScene(true,true)`, enabled scene shadows/decals and called `CheckResources(false)`, following installed official callers captured in `loading-call-traces.json`. It displayed two terrain shader compilation errors (division by zero in `terrain_pixel_functions.rsh`, line 818). Cancelling both errors let loading finish: the procedural colored triangle was visually present and readiness was true at frames 60 and 1200. The separate native raycast then returned false twice without crashing. This proves visual mesh rendering under that initialization, not collision or a usable campaign backend. Reports are `render-probe-7700-42.json`, `render-observation-7700.json` and `raycast-probe-7700-42.json` under `evidence/runtime/in-game/`. The next comparison removes optimization while retaining resource checks to isolate the shader errors.

PID 7700 also alternated the same indexed navigation file through seeds 43/42/43, clearing and recreating its scene each time. Every import had two faces, expected heights (1.5/2.5, 1/1, 1.5/2.5) and a successful 10-unit path between face centers. This is a repeatable small fixture result; full generated grids, region restrictions, wrapping and campaign movement remain unproved.

The official-only baseline in PID 16688 reached a Sandbox campaign, moved to Thronderlag, opened the port/fleet menus, created the separate `fwgc.sav` save and exited normally. All original autosaves remained byte-identical to their backups. `evidence/baseline/gameplay.json` explicitly leaves village/battle/naval travel and cold save load unaccepted.

PID 3960 removed scene optimization and still rendered the triangle after cancelling the same two shader errors. PID 20880 disabled terrain mesh blending when reading the template, then crashed after the read and before initialization completed. Activating the crash prompt and pressing Alt+N closed both prompt and game, with no collection/upload. That option was reverted. PID 24672 disabled forced shader compilation, but both errors remained; after cancellation the triangle rendered, readiness was true at frames 60/1200, and the diagnostic finalized normally. The current experiment removes the template read while retaining resource checks. No successful clean render is claimed yet.

## Independent terrain-query groundwork

The source reader now emits a validated `heightfield.bin` diagnostic stream: ASCII `MNBLHF01`, little-endian uint32 side, then row-major IEEE binary64 heights. It retains a `.pending` suffix until source validation passes. Both real 129-by-129 seed worlds were independently compared against every source height with no numeric loss; each file is 133140 bytes. Hashes and ranges are in `evidence/runtime/heightfield-extraction.json`.

`DiagnosticHeightfield` samples the same two triangles per cell used by its segment-intersection routine. `tools/Test-Heightfield.ps1` passed asymmetric heights, downward/upward/oblique rays, nearest ridge intersection, finite segment bounds and invalid-file cases. The 14 reader integration cases also passed with binary-stream checks. These are source-coordinate mathematical diagnostics; they are not an accepted projection, native collision substitute, periodic campaign query service or playable backend.

## Executed diagnostics

Shipping PID 17292 reached the normal menu with Bannerlord v1.4.8.119303, War Sails v1.2.8.119303 and MNBL.ProceduralWorld loaded. The menu remained interactive during generation and cancellation. Seed 42 completed in 5666 ms (peak 268267520 bytes), seed 43 in 6695 ms (peak 305205248 bytes); cancellation of a subsequent job completed at 4712 ms. Source results and file hashes are preserved in `evidence/runtime/in-game/`. The game exited normally through Exit Game afterward. These are source generation results, not generated campaigns.

The main-thread surface test created an isolated native scene and one procedural triangle for each seed. Native mesh face count was 1 in both cases, but `HasTerrainHeightmap` was false, native terrain height was 0 at the test point versus triangle heights 2.4 and 2.6, and navmesh face count was 0. See `evidence/runtime/in-game/surface-probe.json`. A mesh alone is therefore insufficient. Rendering, collision, picking and replacement of campaign height queries remain unproved. This result does not prove that another backend cannot work.

## Installed API trace

`evidence/runtime/api-inventory.json` and `api-signatures.txt`, produced by `tools/Inspect-Engine.ps1`, record original assembly paths, SHA-256, frameworks, method signatures/parameters and IL bodies. The installed API targets .NET Standard 2.0; the module builds using this PC's .NET Framework 4.8.1 reference pack and MSBuild. Game assemblies are not distributed.

The diagnostic uses `MBSubModuleBase.OnBeforeInitialModuleScreenSetAsRoot` and `Module.AddInitialStateOption`. Generation runs on a background task; messages return through `OnApplicationTick`. A Windows job supervises only owned processes. The separate console harness covers cancellation, pre-cancellation, spaces in paths and producer-hash refusal.

`Mesh.CreateMesh`, `AddTriangle`, normal generation and `GameEntity.AddMesh` execute successfully. `Scene.GetTerrainHeight`, `GetTerrainHeightAndNormal` and native raycasts are independent query surfaces. No public writable heightfield has been established by the current inventory.

`SandBox.MapScene.Load` creates a native Scene, obtains the navigation region mapping from the campaign model, reads `Main_map`, requires `border_min` and `border_max`, obtains native battle scene index data, and establishes campaign late-AI work. `GetFaceIndex` passes `CampaignVec2.IsOnLand` to native navmesh queries; `GetPathBetweenAIFaces` passes directional land/sea transition costs; height/normal queries delegate directly to native Scene. An IMapScene replacement cannot be assumed to cover direct native consumers.

Navigation candidates include `ImportNavigationMeshPrefabWithFrame`, `ClearNavMesh`, `SetNavMeshRegionMap`, face connectivity and path methods. Prefab ownership, cross-tile connectivity, sea typing, route execution and east/west wrapping remain untested. `GameEntity.CookTrianglePhysxMesh` takes native pointers; its call-site ownership contract is being traced before use.

New-game and pre-deserialization object registration, DLC model selection, weather/flow rebinding, save metadata discovery and teardown require remaining MMR-14 through MMR-21/59/60/63 work. The inventory is not an exhaustive native-call audit.

## Resolved launch obstacle

The original direct Bannerlord.Native.exe launch crashed and left a CSRSS crash prompt. After user dismissal, the correct Bannerlord.exe `/singleplayer` launch with an explicit official module list and the diagnostic module worked. No crash files were uploaded. This previous UI obstacle is resolved and is not a backend limitation.
## Clean rendering and remaining native-query crash

PID 20756 removed the template scene read but retained `CheckResources(false)`: both terrain shader dialogs remained. PID 27088 instead preloaded the procedural mesh, disabled static shadows and allowed the scene layer to render without its clean-screen loading gate. Both seeds 42/43 rendered visibly different triangles without any shader dialogs, reported `loading_finished` and readiness true at frames 60/1200, and finalized normally. This supersedes the earlier black-screen and shader-error rendering results.

The separate native-ray test in the same PID still crashed between `before_raycast` and `after_raycast`. The direct accessibility click on No had no effect; activating the actual CSRSS prompt and pressing Alt+N closed the prompt and process. Fresh window/process checks confirmed closure. No files were collected or uploaded. Thus visual readiness alone does not establish native physics initialization. Evidence is in `render-observation-27088.json`, both seed reports and `render-progress-27088.log` under `evidence/runtime/in-game/`.

The subsequent physics experiment below supersedes that query failure for the template-backed scene. Full heightfield rendering and campaign query/navigation acceptance remain open.

PID 25444 loaded the template with physics initialization, added a four-vertex/two-triangle collision body using the installed War Sails pinned-buffer pattern, and rendered without errors. Rays returned false. PID 31440 additionally used Moveable, set the entity global frame and explicitly enabled physics: both ray measurements hit at distance 27.6 from z=30, matching surface height 2.4. A separate sphere control hit at distance 26 as expected. This is the first native collision hit on our procedural geometry. The visual surface remained clean. Reports: `raycast-probe-31440-42.json` and `render-observation-31440.json`. Full-grid calibration, picking, native terrain-height consumers and campaign binding remain open.

## Heightfield integration and calibration failures

The generation command now invokes the pinned reader after the producer, checks its completion and publishes a hash-bound diagnostic heightfield reference. The supervisor harness passed both seeds, cancellation preserving the previous reference, and both binary hash refusals. PID 30776 also completed seed 42 generation and extraction inside Bannerlord (reader 5.616 seconds). These are diagnostic outputs; campaign readiness remains false.

The first 17-by-17 calibration screen crashed after initialization in PID 30776. Reducing collision batches from 256 to 16 quads did not resolve the failure: PID 4528 passed the first scene tick but crashed before reaching landmark measurements. Both crash prompts were dismissed with activation plus Alt+N, and process exit was verified. No full-heightfield render is claimed by these attempts. Evidence is under `evidence/runtime/in-game/calibration-crash-*.json` and `heightfield-progress-*.log`.

PID 30472 used the scene layer's input context and passed `after_escape_input`, then crashed. PID 4408 deferred all new collision registration until five seconds but still crashed after the first frame, before `before_deferred_collision`. Neither changing input context nor withholding collision bodies resolved this failure. Both prompts were dismissed and process exit checked. The untested scene-name ordering change was reverted for the next controlled comparison: acquiring and releasing a mesh write lock around each chunk's triangle construction, with the returned handle passed to both triangle calls. Build passed, but PID 17972 again crashed after `after_escape_input`, before collision registration. The mesh lock alone does not resolve the crash. Evidence: `calibration-crash-17972.json` and `heightfield-progress-17972.log`. No terrain-render acceptance changed.

## Other-mod and official-document research (2026-10-09)

The user requested a focused research break after repeated crashes. No third-party mod has been installed, no third-party assets are included in our deliverable, and no generated campaign backend is accepted by this research.

- **Mount & Warcraft Reborn:** [author's Workshop page](https://steamcommunity.com/sharedfiles/filedetails/?id=3724018893) lists Bannerlord 1.4.8, Naval DLC 1.2.8 and Harmony 2.4.2.0. The author's September 17 comment announces the custom-map public release; October 3 still refers to Azeroth. [Original author page](https://www.moddb.com/mods/mountwarcraft-reborn) describes making heightmaps with Houdini/World Machine for sceners. This establishes a relevant custom-map precedent, but no public source or per-seed runtime terrain implementation was located. Warcraft's content pipeline does not by itself meet our generation, wrapping or save-binding requirements.
- **TAOM / The Age of Men:** [RoadMeshAttacher](https://github.com/haterade22/TAOM/blob/ccef172adced5e25ba78f47bb25250006976e7ed/Main/SceneScripts/Roads/RoadMeshAttacher.cs) creates a material-backed mesh, acquires `LockEditDataWrite`, passes the handle into triangle insertion, unlocks in `finally`, recomputes bounds and attaches through a MetaMesh. Our current grid test used a zero handle, unlike that source. This is a testable discrepancy, not yet a diagnosed crash cause. [Battle-scene grid notes](https://github.com/haterade22/TAOM/blob/ccef172adced5e25ba78f47bb25250006976e7ed/docs/reference/worldmap-battle-scene-grid.md) also identify the campaign battle-scene texture as a separate custom-map integration dependency.
- **Alliance:** [CS_Road](https://github.com/Byak0/Alliance/blob/3698afa69c2f1378b8e4be077e4437b7aaf5ab6c/Alliance.Common/Extensions/CustomScripts/Scripts/CS_Road.cs) independently shows an editable mesh, paired write lock/unlock and the returned handle passed to triangle insertion. It attaches a MetaMesh after recomputing bounds. This is procedural road geometry, not a full runtime campaign terrain writer. The repository is GPL-3.0; no source was copied into this mod.
- **Custom Scene Creator:** [CscNavMeshTestSlot](https://github.com/Bhelogan/bannerlord-custom-scene-editor/blob/8d6c5b3537e4ad393002e66d2f28120cde9a9c39/CustomSceneCreator/Boot/CscNavMeshTestSlot.cs) seeds two fixed SceneObj folders before a game boot, alternates them to avoid recently held file locks, and copies a baked navigation file before loading. Durable outputs live separately from the reusable slots. Its documented startup name-indexing constraint agrees with our measured fresh-name failure and repeated indexed-file success. Its terrain is copied from a stock scene; this does not prove arbitrary terrain generation. Source inspected read-only at the linked commit; MIT license retained in the ignored research checkout.
- **WorldMapSceneMerger:** [author page](https://www.nexusmods.com/mountandblade2bannerlord/mods/2915) describes settlement scene merging and matching terrain/navigation/distance-cache data, including a Naval DLC base. Useful integration precedent, but copying a base map and restarting to rebuild a cache does not meet our per-world UX.

The [official terrain documentation](https://moddocs.bannerlord.com/editor/scene-editor/terrain_creation/) supports external PNG/RAW heightmap import through the editor; it does not demonstrate a writable heightfield in the shipping client. The [official War Sails world-map guide](https://moddocs.bannerlord.com/war-sails/wsworldmap/) specifies typed sea/land navigation, flowmaps, correctly placed ports/drop points and rebuilt settlement distance caches. Campaign water uses custom meshes with `naval_worldmap_water`, flowmap/ambient scripts and the `snapped_water_body` tag. A surface that merely renders is insufficient.

**Revised investigation order:** the concrete mesh-lock discrepancy was tested and did not resolve the previous failing fixture. Prioritize a generated native scene bundle published into pre-indexed owned slots as the alternative to replacing every native terrain consumer. The first bounded feasibility test is reading the installed native terrain container without loss, then changing one known height and verifying the engine loads and queries that height through an owned scene slot. A correct terrain writer or supported runtime terrain import remains unresolved; Custom Scene Creator does not supply it. Editor authoring is acceptable for an installation template, not a manual step for every generated seed. Wrapping, naval flow, mission transitions and cold save restoration remain separate acceptance gates.
