# Generated campaign scene bundle — load path, file set and slot strategy

**Status: research specification. Nothing here has been tested in-game.** Statements marked *IL* were read from installed assemblies with Mono.Cecil. Statements marked *file* were read from installed files or engine logs. Everything marked *hypothesis* still needs the in-game tests in section 7. The machine-readable call-site table is `evidence/runtime/map-scene-trace.json` (45 call sites, assembly paths and SHA-256 values, scene skeletons, and the terrain survey).

Target: Bannerlord v1.4.8.119303 with War Sails (NavalDLC) v1.2.8.119303. The game was not launched for this work. The scripts and raw dumps are in `build/scenetrace/` (git-ignored).

## 1. Scene-name resolution

| Question | Answer | Evidence |
| --- | --- | --- |
| Is there a managed list of scene paths? | **No.** Only two managed sites build SceneObj paths: `SandBox.MapScene.GetMainMapModule` and the Multiplayer custom-server client helper. Scene-name-to-folder resolution is done in native code. | *IL*: `Utilities.GetFullFilePathOfScene` → `IUtil.GetFullFilePathOfScene`, which returns the sentinel `SCENE_NOT_FOUND`. `Scene.Read(name, …)` → `IScene.Read`. `Scene.Read(name, moduleId, …)` → `IScene.ReadInModule`. *file*: the native DLL contains the strings `%s/%sSceneObj/%s`, `SceneObj/%s/TileSets/`, `/terrain.bin`, `/navmesh.bin`, `%s/flora.bin`, `/river_triangles.bin`, `/flowmap.dds` and `/atmosphere.xml`. |
| How does NavalDLC's `Main_map` win over SandBox's? | **Module order, resolved in managed code.** `GetMainMapModule` loops over `ModuleHelper.GetActiveModules()`. It keeps the **last** module that has `SceneObj/Main_map/scene.xscene` (the loop has no `break`). `MapScene.Load` then calls `Scene.Read("Main_map", thatModule.Id, …)`, the module-qualified `ReadInModule`. There is no override list and no alternative scene name. | *IL*: `SandBox.MapScene::GetMainMapModule` IL_0002–IL_0041; `MapScene::Load` IL_00ba–IL_00db. The active-module order is the order of the launch list: `Module.Initialize` IL_0073/IL_0079 inserts modules into `_loadedModules` in `GetModulesNames()` order, and `GetModules` enumerates that dictionary. *file*: `rgl_log_18444.txt` (an older build, 115628) loads `$BASE/Modules/NavalDLC/SceneObj/Main_map/scene.xscene`. |
| Can a module add a **new** scene name? | **Yes, if the folder exists at startup.** 567 installed folders contain `scene.xscene`. Only `Main_map` appears in two modules. NavalDLC alone adds 88 unique names. Every folder's root `<scene name>` equals its folder name (567/567). | *file*: `build/scenetrace/terrain_survey.json` and the root-name check. |
| Is a name created mid-session found? | **No, for names created after startup.** Our own probes found that a freshly created nav-prefab name imported 0 faces, while rewritten bytes under an already-indexed name worked (`docs/runtime-capabilities.md`). The Custom Scene Creator notes report the same for scene folders: a bare relative path appears in the log and the game dies. *Third-party claim, not re-tested by us.* We did observe an unindexed name falling back to the working directory: our `SetName("mnbl_render_probe")` probe created `<game>/bin/Win64_Shipping_Client/SceneObj/mnbl_render_probe/ShaderCache/D3D11/terrain_shaders_header_data.bin` (8 zero bytes). | *file* |
| Is there a managed API that re-indexes scenes? | **One official candidate.** `TaleWorlds.Engine.Utilities.PairSceneNameToModuleName(string scene, string module)` is public and static. The official Multiplayer client (`DedicatedCustomServerClientHelperSubModule.<DownloadMapFromHost>d__7.MoveNext`) uses it mid-session: `TryGetFullFilePathOfScene`, then `ExecuteCommandLineCommand("resource.shader.invalidate_temp_shader_cache_of_scene Multiplayer/<scene>")`, then `Directory.Move` into `Modules/Multiplayer/SceneObj/<scene>`, then `PairSceneNameToModuleName(scene, "Multiplayer")`, which logs "RGL has been informed of the module pairing". If the name already belongs to another module, the client asks the user to restart. **Whether this pairing lets singleplayer `Scene.Read` load a fresh folder is a hypothesis.** | *IL*: IL_011f, IL_01d0, IL_01da, IL_0269. The assembly is `Modules/Multiplayer/bin/…/TaleWorlds.MountAndBlade.DedicatedCustomServer.ClientHelper.dll`, SHA-256 `54040db3…ae123`. |
| Harmony | No official assembly references `0Harmony`, and no Harmony file exists under the game root. | *IL/file* |

## 2. Campaign map load path

The sequence below is all *IL*. Full IL is in `build/scenetrace/il-*.txt`.

1. `SandBox.SandBoxSubModule.OnGameInitializationFinished` (IL_0020) sets `Campaign.MapSceneCreator = new SandBox.MapSceneCreator()`. The setter is **public**. `MBGameManager.OnGameInitializationFinished` calls submodules in load order, so a later module can replace the creator.
2. `Campaign.DoLoadingForGameType` runs in stages. Stage 0 is `Game.Initialize`, which runs `Campaign.OnInitialize`. That method ends by calling the submodules (`OnGameInitializationFinished`, IL_02ed) after `LoadBehaviorData` (IL_0247). Stage 2 (IL_0037) calls `Campaign.LoadMapScene`.
3. `Campaign.LoadMapScene` calls `CreateMapScene()`, then `SetSceneLevels(level_1, level_2, level_3, siege, raid, burned)`, then `Load()`, then `GetMapBorders()`. From the borders it sets `MapMinimumPosition`, `MapMaximumPosition`, `MapMaximumHeight`, `MapDiagonal` and `PathFindingMaxCostLimit`, then calls `AfterLoad()`.
4. `SandBox.MapScene.Load` does the following:
   - `CreateNewScene(true, true, 1, "MapScene")`.
   - Sets up the agent renderer.
   - Builds `SceneInitializationData(true)` with `UsePhysicsMaterials`, `EnableFloraPhysics`, `UseTerrainMeshBlending` and `CreateOros` set to false.
   - `SetFetchCrcInfoOfScene(true)`.
   - `DisableUnwalkableNavigationMeshes`, then `SetNavMeshRegionMap(GetRegionMapping(PartyNavigationModel))`.
   - **`Read("Main_map", module, …)`**.
   - Looks up `border_min` and `border_max` (no null check).
   - `DisableStaticShadows`, `InvalidateTerrainPhysicsMaterials`, `SetDontLoadInvisibleEntities`.
   - `medit_water_plane` (optional) and the `Town Scene Manager` check (warning only).
   - `MBMapScene.LoadAtmosphereData` (native), `MapWeatherModel.InitializeCaches`, `MBMapScene.ValidateTerrainSoundIds`.
   - `OptimizeScene(true, false)`, then `GetTerrainData`, which sets `_terrainSize = nodes × nodeSize`.
   - `MBMapScene.GetBattleSceneIndexMap` (native).
   - `Tick(0.1)`.
   - `Campaign.CampaignLateAITickTask = AsyncTask(Campaign.LateAITick)`.
5. Views and the naval layer:
   - `MapScreen.OnInitialize` takes `((MapScene)MapSceneWrapper).Scene`. It requires entities with `CampaignMapSiegePrefabEntityCache` (IL_0229) and `MapColorGradeManager` (IL_02cc).
   - `NavalDLCViewSubModule.OnNewGameCreated` and `OnAfterGameLoaded` construct `NavalMapSceneWrapper`, which reads the pirate spawn points and `main_map_village_dropoff`, and calls `SetupWaterWake(128, 8)`.
   - `OnAfterGameInitializationFinished` casts the wrapper to `SandBox.MapScene` and runs `VisualShipFactory.InitializeShipEntityCache`.

**Structural constraint (IL):** 31 instructions in 22 methods across NavalDLC, NavalDLC.View, SandBox, SandBox.View and SandBox.GauntletUI convert the campaign map scene to `SandBox.MapScene`: 25 are `castclass` and 6 are `isinst`. They are listed in `call_sites_castclass_mapscene` in the evidence file. Any replacement must therefore derive from `SandBox.MapScene`. The class is public and not sealed. Its `IMapScene` members are `virtual final`, so a subclass has to re-implement `IMapScene` to redirect `Load`. Every API that `Load` uses is public; this was checked, including `Campaign.LateAITick`, `Campaign.CampaignLateAITickTask`, `AsyncTask.CreateWithDelegate`, the `MBMapScene.*` statics and `MBAgentRendererSceneController.*`. The private state (`_scene`, `_terrainSize`, the borders, `_battleTerrainIndexMap*`, `_agentRendererSceneController`) would be set through reflection. Harmony is not needed.

**What the scene name controls (IL).**

- `NavalDLC.Missions.Objects.ShipWaterEffects.OnInit` sets `_inCampaignMode = Scene.GetName() == "Main_map"` (IL_00fe), so a slot with a different name changes ship wake and particle behaviour.
- Distance caches are chosen per **module**, not per scene. For each active module, `SettlementPositionScript` checks `<module>/ModuleData/DistanceCaches/settlements_distance_cache_<Default|Naval|All>.bin`, and the last match wins. When none is found it calls `GenerateCacheData()`, which is slow, and raises a FailedAssert.
- `NavigationCache.Deserialize` reads the two CRCs from the cache and **discards** them, so caches are never validated against the scene.
- `SettlementPositionScript.InitializeCachedVariables` derives the map's module from `Scene.GetModulePath()`. When that module is neither NavalDLC nor Sandbox but NavalDLC is active, it registers the Default, Naval and All caches.

**Save binding (IL).**

- A new game stores `GetSceneXmlCrc` and `GetSceneNavigationMeshCrc` (`DoLoadingForGameType` IL_010a/IL_011b).
- When a save is loaded and either CRC differs, `Campaign.CheckMapUpdate` silently re-snaps settlements, map events, armies and parties.
- A generated world's save must therefore be loaded against byte-identical slot files.

## 3. Official `Main_map` skeleton and comparison

Extracted by `build/scenetrace/xscene_skeleton.py`. Counts are game entities, including children.

| | SandBox `Main_map` | NavalDLC `Main_map` | SandBoxCore `battle_terrain_001` |
| --- | --- | --- | --- |
| Top-level elements | `flora_bounding_rect, levels, environment_properties, entities, terrain, editor_data, camera_data, Paths` | same | same |
| Levels | base, level_1, level_2, level_3, civilian, siege, looted | same | base, level_1, level_2, level_3 |
| Terrain | 16×16 nodes × 53 m (848 m), heightmap level 8, `lod_multiplier` 3, `vista_tileset="WorldMap"`, `terrain_render_version="1"`, `use_dynamic_weather_effects="true"`, `dynamic_flowmap_texture_name="main_map_snow_flowmap"`, `colormap_detail_level` 0, `ao_intensity` 4 | 16×16 × 65 m (1040 m), level 8, `lod_multiplier` 16, `vista_tileset="worldmap"`, otherwise the same | 5×8 × 100 m, level 6, `vista_tileset=""`, no `terrain_render_version`, `colormap_detail_level` 10 |
| Terrain children | `outer_mesh`, `layers` (8 layers: default, flora_forest, rock, grass, dirt, dirt2, forest2, river), `nodes` (256 × `{min/max height, layer masks, texture_levels}`) | 7 layers: default, default_1, rock, grass, dirt, dirt2, forest | 4 layers, 40 nodes |
| Entities | 37 918 | 41 834 | 402 |
| Inline scripts | VolumeBox 1145, Town Entity Manager 644, path_converger 18, water_body 8, 1 each of SettlementPositionScript, Town Scene Manager, MapColorGradeManager, CampaignMapSiegePrefabEntityCache, ReflectionCapturer, SceneLeveler, river_generator | VolumeBox 1297, Town Entity Manager 553, PirateSpawnPoint 48, sound_emitter 37, BlockadePositionScript 29, path_converger 18, SceneLeveler 14, water_flowmap 3, CampaignMapAmbientOccluder 2, 1 each of the singletons | TacticalPosition 9, TacticalRegion 7, ReflectionCapturer, AmbientSoundEmitter |
| Prefab-provided scripts | `MapSnowAndRainDefiner` (`SnowAndRainTextureDefiner`, texture `main_map_snow_flowmap`, grid 32), `water_plane` ×8 | `MapSnowAndRainDefiner`, vineyard `sound_emitter`s | — |
| Settlement entities (named = settlement id) | town 53, castle 199, village 141, hideout 43 | town 57, castle 226, village 157, hideout 43 | — |
| Key tags | siege_preparation 947, map_banner_placeholder 798, wall tags, map_camp_area_1/2, main_map_city_gate 120, map_settlement_circle 119, main_map_castle_gate 53 | adds main_map_village_dropoff 144, main_map_city_port 29, Blockade_Arc_Start/End 30, bridge 22, **snapped_water_body 2** | — |
| Water | 8 `water_body` prefabs (`worldmap_lake_clear`) | `water_plane` 2744 × 2744 at z 2.57, tag `snapped_water_body`, material `naval_worldmap_water`, scripts `water_flowmap` and `CampaignMapAmbientOccluder`, physics `bo_editor_plane` | none |
| Folder files | atmosphere.xml, flora.bin, navmesh.bin, scene.xscene, terrain.bin, ShaderCache/, TileSets/ (WorldMap.gts plus 2.1 GB of pages) | adds flowmap.dds and river_triangles.bin; TileSets 3.2 GB | atmosphere.xml, flora.bin, navmesh.bin, scene.xscene, terrain.bin, ShaderCache/ |

The installed scripts split into managed and native types.

- **Managed:** SettlementPositionScript, SnowAndRainTextureDefiner, BlockadePositionScript (SandBox.View); CampaignMapSiegePrefabEntityCache (SandBox); MapColorGradeManager (MountAndBlade.View); PirateSpawnPoint (NavalDLC.View); CampaignMapAmbientOccluder (NavalDLC); SceneLeveler, VolumeBox and MapAtmosphereProbe (MountAndBlade).
- **Native only:** Town Scene Manager, Town Entity Manager, path_converger, water_body, water_flowmap, river_generator, ReflectionCapturer, sound_emitter. These have no managed type, and their names appear in `TaleWorlds.Native.dll`.

**`terrain.bin` directory (file).** The file starts with `ZGR6RTRN` and u32 2, followed by 5 entries of the form `(tag, u32 version, u32 offset, u32 storedSize, u32 rawSize)`; the meaning of the last two fields is inferred.

- Each section starts with a per-node u32 offset table.
- **Both `Main_map` files have empty `NRML` and `WGHT` sections (size 0); `battle_terrain_001` has both.**
- `PHYM` rawSize fits `nodes × ((2^L+1)² × 4 + 32)` exactly in all three files. That suggests a float32 vertex grid per node, but the meaning is a hypothesis.
- In `battle_terrain_001`, the MIDX, HGHT, NRML and WGHT byte ranges each contain at least one embedded PNG signature. PHYM contains none, and its storedSize (16 904) is far below its rawSize (677 280), so it is compressed by an unidentified method; a raw zlib decode at the section start failed.
- This format belongs to the terrain stream; it is recorded here only for the vista question below.

**Can a campaign scene use `vista_tileset=""` with ordinary layer materials?** This is a **hypothesis, rated plausible.**

Evidence for:

- 485 of the 487 installed terrain scenes use `vista_tileset=""` and have no TileSets folder. Only the two `Main_map` scenes set a tileset and `terrain_render_version`.
- Terrains of the same node count exist without a tileset: `SandBoxCore/aserai_town_g` is 16×16 nodes × 100 m at level 7.
- Much larger extents exist without a tileset: the NavalDLC `river_bt_*_4x4km` scenes are 10×10 × 400 m (4 km) at level 8.
- Battle scenes with `vista_tileset=""` load in-game. The June logs (build 115628, an older client) show `battle_terrain_030` and `battle_terrain_biome_130` loading.

Evidence against, or untested:

- The main map stores no normals or weights in `terrain.bin`; they evidently come from the virtual texture. A tileset-free campaign scene would therefore need populated `NRML` and `WGHT` sections like a battle scene.
- `terrain_render_version="1"`, `use_dynamic_weather_effects` and `dynamic_flowmap_texture_name` may assume the tileset path.
- Campaign camera distances, the political/colour-grade overlays and performance are untested.
- The engine has "Vista … not found" warnings, but nothing we found proves that a missing tileset is fatal.

## 4. Minimal generated campaign scene bundle (owned slot)

### 4a. Slot folder contents

These are written by our code into `Modules/MNBL.ProceduralWorld/SceneObj/<slot>/`.

| File | Required | Content / source |
| --- | --- | --- |
| `scene.xscene` | yes | Generated. The root `<scene name="<slot>" version="2">` must equal the folder name (all 567 installed scenes follow this). Section order: `flora_bounding_rect`, `levels` (copy the 7 `Main_map` levels), `environment_properties` (copy the template, set `physics_world_min`/`physics_world_max_`, water props with `water_exists=false`), `entities` (see 4b), `terrain` (4c), `editor_data`, `camera_data`, and optionally `Paths`. |
| `terrain.bin` | yes | Generated. Owned by the terrain-writer stream: `ZGR6RTRN` v2 with MIDX/HGHT/NRML/WGHT/PHYM. With `vista_tileset=""` the NRML and WGHT sections must be populated (hypothesis). The per-node min/max heights in `scene.xscene` must agree with it. |
| `navmesh.bin` | yes | Generated. `RNM1`/`NMG9`; face group ids = `TerrainType` values (Plain=1 … OpenSea=19, LandRestriction=23, SeaRestriction=24, UnderBridge=25). Land/sea comes from `SetNavMeshRegionMap`. |
| `flora.bin` | yes (all 487 terrain scenes have one) | The 12-byte empty form `FLR2, u32 4, u32 0` ships in 85 terrain scenes, including 16×16-node ones. |
| `atmosphere.xml` | yes | Copy of the official `Main_map` `atmosphere.xml` (about 5 KB). It is loaded by `LoadAtmosphereData`. |
| `flowmap.dds` | naval, optional | 1 048 704-byte DDS in 16 shipped scenes. Native consumer only. |
| `river_triangles.bin` | naval, optional? | Only NavalDLC `Main_map` has one. Native consumer only; whether it is required is unknown. |
| `ShaderCache/` | no | Engine-generated. A probe-named scene got an 8-byte `terrain_shaders_header_data.bin`. The official one mirrors node data: level 8, 256 nodes, layer masks. |
| `TileSets/` | no, if `vista_tileset=""` (hypothesis) | The native path is `SceneObj/%s/TileSets/`. A 2–3 GB Granite set is not practical to generate. |

### 4b. Required entities

| Entity | Why | Proof |
| --- | --- | --- |
| `border_min`, `border_max` (names) | Map extents and `MapMaximumHeight` (= `border_max.z`). Dereferenced without a null check. | IL |
| One entity with script `Town Scene Manager` | Warning only if missing. | IL |
| `settlements_scripts` with `SettlementPositionScript` | Registers distance caches. | IL |
| Prefab `MapSnowAndRainDefiner` (or call `MapScene.SetSnowAndRainDataWithDimension` ourselves) | The only allocator of the snow/rain/wind arrays read by the weather model and the naval wind. | IL |
| Entity with `CampaignMapSiegePrefabEntityCache` (with `projectile_position` children as in the template) | `MapScreen.OnInitialize` dereferences it without a check (crash if missing is a hypothesis). | IL |
| `color_grade_manager` with `MapColorGradeManager` | `MapScreen.OnInitialize`. | IL |
| One entity per settlement, **named exactly the settlement StringId** | `SettlementVisual.OnStartup`. Children carry: `main_map_city_gate`, `main_map_city_port` (ports), `main_map_village_dropoff` (villages), `map_settlement_circle`, `map_banner_placeholder`, `siege_preparation`, the wall tags, `map_siege_*` and `map_camp_area_1`/`map_camp_area_2`. Fallback: `GameEntity.Instantiate(prefab named StringId)`, so the prefab must be registered at startup. | IL |
| `PirateSpawnPoint` entities (`ClanStringId`, `Radius`) | Naval pirate spawns. | IL |
| `BlockadePositionScript` under ports, with `Blockade_Arc_Start`/`Blockade_Arc_End` children | Naval blockades. | IL |
| `water_plane`: tag `snapped_water_body`, material `naval_worldmap_water`, scripts `water_flowmap` and `CampaignMapAmbientOccluder`, physics `bo_editor_plane` | Campaign sea. The consumers are native. The official War Sails guide also requires this. | file |
| `river_script` (`river_generator`), `envmap_probe` (`ReflectionCapturer`) | Rivers and lighting. Copied from the template. | file |

### 4c. Terrain element

Copy the attributes of a tileset-free scene and set:

- `node_dimension_x/y` and `node_size` for the world extent;
- `min_height`/`max_height`;
- `vista_tileset=""` and every `vista_*` texture name `""`;
- `use_dynamic_weather_effects="true"` and `dynamic_flowmap_texture_name="main_map_snow_flowmap"` (hypothesis);
- a `layers` block with ordinary `ground_*_mainmap` materials from the template;
- a `nodes` block of nx×ny `<node idx idy min_height max_height layer_is_used_mask_*>` with `texture_levels`, using `heightmap_level` 8 as in `Main_map`.

### 4d. Data that lives outside the scene folder

These are global or per-module resources.

| Data | Location / consumer | Generated-world plan |
| --- | --- | --- |
| Distance caches | `<module>/ModuleData/DistanceCaches/settlements_distance_cache_{Default,Naval,All}.bin`; the last active module wins; CRCs are ignored. | Write our module's three files per world (`NavigationCache<T>.Serialize` is public), or accept the first-load generation. **They must be removed or neutralised when an official campaign is played with our module active.** |
| Map textures | Global named resources in `Native/AssetPackages/pack_*.tpac` and `Native/EmAssetPackages/world_map/world_map.tpac`, overridden by NavalDLC packs: `main_map_physics_heightmap`, `world_map_heightmap`, `worldmap_battle_scene_grid`, `main_map_snow_flowmap`, `worldmap_river_mask`/`undermask`/`normal`, `worldmap_ocean_gradient`, `worldmap_water_normal_map`. | Per-seed textures are not generable as `.tpac`. Managed consumers can be fed directly: the battle index map through MapScene private fields, and snow/rain through `SetSnowAndRainDataWithDimension(Texture.CreateFromByteArray(…), 32)` (hypothesis). Native consumers (river generator, physics heightmap and others) are open. |
| `sp_battle_scenes.xml` `map_indices` | `Campaign.InitializeScenes`, read per active module. | Must agree with the battle index map. |

## 5. Load-path hook table

| # | Substitution point | Official consumer | Least invasive hook | Kind |
| --- | --- | --- | --- | --- |
| 1 | Which scene is read | `MapScene.Load` → `GetMainMapModule` → `Read("Main_map", module)` | **A:** ship `SceneObj/Main_map/scene.xscene` in our module (last in the launch list), with no code. **B:** `Campaign.MapSceneCreator` set in our `OnGameInitializationFinished`, returning `MnblMapScene : SandBox.MapScene, IMapScene` whose `Load` copies the official sequence with `Read(slot, "MNBL.ProceduralWorld")`. | A: file. B: official extension point plus reflection. |
| 2 | Region map / face types | `GetRegionMapping(PartyNavigationModel)` | Type navmesh faces with `TerrainType` ids. No model change. | file |
| 3 | Borders and max height | `border_min`/`border_max` | Entities in `scene.xscene`. | file |
| 4 | Terrain height, normal, ray and pick | Native `Scene.GetHeightAtPoint`, `GetTerrainHeightAndNormal`, `GetNormalAt`, `SceneView.ProjectedMousePositionOnGround` | `terrain.bin`; no managed hook exists. | file |
| 5 | Navigation and paths | Native navmesh via `GetNavMeshFaceIndex`, `GetPathBetweenAIFaces`, `GetPathDistanceBetweenAIFaces` (dozens of managed call sites; see `imapscene_member_call_counts`) | `navmesh.bin`. | file |
| 6 | Battle-scene patch | `GetMapPatchAtPosition` ← `_battleTerrainIndexMap` | Overwrite private fields after Load (B only), or keep the global texture. | reflection |
| 7 | Snow, rain, wind | `SnowAndRainTextureDefiner` → `SetSnowAndRainDataWithDimension` | Prefab in the scene, or call the public method with our texture. | official extension point |
| 8 | Atmosphere / colour grade | `LoadAtmosphereData` (native), `MapColorGradeManager` | `atmosphere.xml` plus the template entity. | file |
| 9 | Settlement visuals and siege data | `SettlementVisual.OnStartup`, `GetSiegeCampFrames` | Settlement entities named by StringId. | file |
| 10 | Distance caches | `SettlementPositionScript` | Our module's `ModuleData/DistanceCaches`. | file |
| 11 | Naval: pirates, drop-offs, ports, blockades, wake | `NavalMapSceneWrapper`, `SettlementVisual`, `BlockadePositionScript` | Scene entities and tags. | file |
| 12 | Ship campaign mode | `ShipWaterEffects` scene name `== "Main_map"` | Option A keeps the name. Option B needs `Scene.SetName("Main_map")` after `Read`, which is a hypothesis with a ShaderCache side-effect risk. | official extension point |
| 13 | Save binding | `CheckMapUpdate` CRC compare | Republish the identical slot before load, choosing the world in our `OnGameInitializationFinished` (after `LoadBehaviorData`, before stage 2). Whether behaviour data is readable there is a hypothesis. | file |

**No row needs Harmony.**

## 6. Name indexing constraints and slot strategy

Constraints:

- Scene folder names must exist at startup. A fresh name fails: our nav-prefab result plus the third-party scene report.
- Bytes under an indexed name can be rewritten. We measured this for nav prefabs; the CSC notes report it for `terrain.bin`/`scene.xscene`/`navmesh.bin`.
- Scene identity in saves is the CRC pair.
- Distance caches are per module.

Strategy, in the order to test:

1. **Slot A, the `Main_map` override (recommended first).** Ship `Modules/MNBL.ProceduralWorld/SceneObj/Main_map/`, created by `Deploy-Dev.ps1` so it exists at startup.
   - It is *armed* only while a generated campaign loads: `scene.xscene` and the distance caches are present. It is *disarmed* otherwise: rename `scene.xscene` to `scene.xscene.disarmed` and move the caches aside in `OnSubModuleLoad` and on game end.
   - `GetMainMapModule`'s `File.Exists` is evaluated on every load, so disarming is managed and needs no Harmony.
   - It keeps the scene name `Main_map`, which satisfies `ShipWaterEffects` and the native strings `Main_map`/`main_map`.
   - Risk: one physical slot. Rewriting it is only safe when no generated map is loaded in this process; return to the main menu first, then publish.
2. **Slots B, `mnbl_world_a` / `mnbl_world_b`.** Both folders are created at deploy time and alternate to avoid held files. A `MnblMapScene` subclass reads the slot. The worlds themselves live in a durable store outside `SceneObj` (for example `Modules/MNBL.ProceduralWorld/Worlds/<worldId>/`, or the user documents folder) and are copied into a slot before each load.
3. **Slot C, fresh names.** Write `SceneObj/mnbl_world_<id>/`, then call `Utilities.PairSceneNameToModuleName(name, "MNBL.ProceduralWorld")` and `ExecuteCommandLineCommand("resource.shader.invalidate_temp_shader_cache_of_scene MNBL.ProceduralWorld/<name>")`. This mirrors the official Multiplayer download flow. If it works, the restart constraint disappears.

## 7. Open questions to test in-game (cheapest first)

1. **T1:** copy NavalDLC `Main_map` minus TileSets into our `SceneObj/Main_map` with the `vista_tileset` attribute untouched. Then check that an rgl log line names `$BASE/Modules/MNBL.ProceduralWorld/SceneObj/Main_map/scene.xscene`, which proves precedence and `ReadInModule` from our module. Repeat with TileSets omitted to see whether a missing tileset is fatal.
2. **T2:** same as T1 but with `vista_tileset=""` and battle-style materials. Compare rendering, picking (`ProjectedMousePositionOnGround`) and party movement.
3. **T3:** with the folder present at startup but `scene.xscene` disarmed, arm it mid-session and start a campaign. Does native `ReadInModule` find it, or does indexing depend on file presence at boot?
4. **T4:** `PairSceneNameToModuleName` with a fresh folder (slot C); read `Utilities.TryGetFullFilePathOfScene` before and after.
5. **T5:** `MnblMapScene` subclass with interface re-implementation (slot B). Check that every `castclass` site works and that `Scene.SetName("Main_map")` does not redirect ShaderCache writes into NavalDLC's folder.
6. **T6:** a 12-byte `flora.bin`, an absent `river_triangles.bin`/`flowmap.dds`, a missing `MapSnowAndRainDefiner` and a missing `CampaignMapSiegePrefabEntityCache`; record crash or no crash for each.
7. **T7:** distance cache arm/disarm, and an official campaign started afterwards in the same process still uses NavalDLC's caches.
8. **T8:** cold save load against republished identical bytes (no `CheckMapUpdate` repositioning), and against changed bytes (observe repositioning).
9. Native consumers of the global map textures (`main_map_physics_heightmap`, `world_map_heightmap`, `worldmap_battle_scene_grid`) when the generated world's extent differs from the official one. Unknown.
