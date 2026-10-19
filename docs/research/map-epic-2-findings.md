# Map epic 2: research findings (2026-10-18)

**Status.** These are research findings for the follow-up to the FWGC++ map epic (`ep_01m4fpnf646t254mnh69jhz107`), not an implementation. Nothing was launched in game.

**Method.**
- **Shipped game code.** Managed assemblies were read with REA (`rea-dotnet-static`: types, call edges, field accesses, user strings) and ildasm for IL bodies (constants, control flow).
  - Assemblies: TaleWorlds.Core, CampaignSystem and Engine; SandBox and SandBox.View; NavalDLC and NavalDLC.View; Player Settlement.
  - **REA has no native provider here.** Anything decided inside `TaleWorlds.Native.dll` is listed as a native unknown (section 13).
- **Data.** Installed scene, prefab and settlements XML, and `.tpac` headers.
- **Our code.** This repo and FWGC++ (`C:/Users/davgo/Documents/GitHub/FWGC++`, pinned `ec5d1c1`) were read as source.
- **Review.** Every topic had one deep dive and one adversarial verifier; where a verifier corrected a claim, the corrected value is used here. A completeness critic then checked conflicts across topics.
- **Scratch evidence** (not in the repo): REA evidence IDs, IL dumps and scan scripts in the session scratchpad.

**Shared-install caveat.** The `TaleWorlds.Native.dll` and `TaleWorlds.MountAndBlade.dll` in the shared install may be the battle-scale session's patched builds (both dated Oct 9 01:40). Native-dependent results in the live test must record their hashes.

## 1. Where the map epic ends

**Corrected 2026-10-18.** An earlier version of this section said MMR-50 was in progress and MMR-51–58 and MMR-1 were not started. That was true when it was written; the map epic has since moved on. The red-team gamer-battles lens caught the change.

Current state:
- **MMR-50 through MMR-58** are completed.
- **The acceptance node MMR-1** is completed: commit `7c56032`, "MMR-1: run the one-shot acceptance on the clean package". The epic itself still shows in progress, pending its checkpoint.
- **MMR-52 and MMR-53 are now tested outcomes**, documented in `docs/land-missions.md` and `docs/naval-missions.md`, not plans.
- **Gaps they recorded:**
  - no wetland battles on either world;
  - no village raid;
  - no lord's-hall fight;
  - no port visit;
  - no field battle won in a mission;
  - no night stealth hideout.
- **No Snow or Forest battle scenes are installed.**

This epic builds on the same branch, scene backend and bundle pipeline. It changes the bundle, scene and navmesh identity of every world (section 12).

## 2. Engine facts (REA, verified)

### Terrain type

- **What decides it.** Terrain type is the navmesh face group id and nothing else.
  - `SandBox.MapScene.GetFaceTerrainType` returns `face.FaceGroupIndex`.
  - No managed code reads terrain material layers for gameplay.
  - No managed API retags a face. Only `SetAbilityOfFacesWithId`, `Merge`/`SeparateFacesWithId` and `ImportNavigationMeshPrefab` exist.
- **`TaleWorlds.Core.TerrainType`:**

  | Group | Types |
  |---|---|
  | Land-valid | Plain 1, Desert 2, Snow 3, Forest 4, Steppe 5, Fording 6, Swamp 15, Dune 16, Bridge 17, Beach 20 |
  | Naval | Lake 8, Water 10, River 11, CoastalSea 18, OpenSea 19, LandRestriction 23, SeaRestriction 24, UnderBridge 25 |
  | Never passable | Mountain 7, Canyon 13, RuralArea 14, Cliff 21, NonNavigableRiver 22 |

  NavalDLC adds no types.
- **Vanilla never uses some of these.** Neither Main_map (SandBox or NavalDLC) navmesh uses Snow, Swamp, Dune, Bridge or Beach faces; this was checked by decoding the installed `navmesh.bin`. Every managed consumer handles them. Native step and ambient sound for them is unknown.

### What each terrain type does (IL constants)

| Effect | Rule |
|---|---|
| Party speed | Forest −0.3 (ForestKin / Battanian feat soften it). Desert and Dune −0.1 (Aserai feat exempt). Fording, Water, River, Bridge and UnderBridge on land −0.3. **Snow and Swamp have no speed term.** Snowy or Blizzard *weather* gives −0.1. Night gives −0.25 |
| Naval speed | OpenSea +0.448, River +0.5; shallow-draft ships ±0.066 × speed |
| Spotting | Forest −30% (Battanian feat +15% sight in forest) |
| Tracks | Last half as long on Snow |
| Perks | ExtendedSkirmish on Snow/Forest. DecisiveBattle on Plain/Steppe/Desert. Foragers on Forest/Steppe. Pathfinder and WaterDiviner only on Plain/Steppe |
| Auto-resolve / AI power | `DefaultMilitaryPowerModel`: Forest → ForestBattle, with cavalry −0.2/−0.15 and horse archers −0.3/−0.25 (attacker/defender). Plain, Steppe, Desert, Dune and Snow use the flat group, with cavalry +0.25/+0.1 and horse archers +0.3/+0.15 |
| Real battles | `Mission.TerrainType` comes from the face. `SandboxAgentStatCalculateModel` picks Tactics captain perks by it (Snow/Forest vs Plain/Steppe) |
| Battle scene | `DefaultSceneModel.GetBattleSceneForMapPatch`: map index first, then a terrain match, then random. Installed land scenes (parsed; corrected by the red-team battles lens, since the first count included 13 commented-out entries): SandBox Plain 39, Steppe 27, Desert 8, Swamp 6, plus 16 NavalDLC Plain; 30 naval scenes. **No Snow or Forest scenes.** The battle index map always names one land scene. It is chosen by biome class except in sea-touching and settlement-ring cells, where MMR-42 falls back to Plain/Steppe (red team, battles F1) |
| Other | Desert and Forest incidents; scouting XP; map-conversation backdrops conv_desert/forest/steppe |

### Pathfinding

`Scene.GetPathBetweenAIFaces` takes excluded face ids, one cost multiplier for the whole query and region-switch costs. There is **no per-face or per-terrain cost**. So the AI cannot be steered onto roads through the engine.

### Weather, snow and seasons

- **Gameplay snow** is byte 0 of a 32×32 weather grid; byte 1 is rain.
  - `DefaultMapWeatherModel` makes a position Snowy above 0.55 and Blizzard-eligible above 0.65, shaped by season.
  - **Snowy draws nothing on the map** except the wet mask. Only Blizzard (at most 10% chance) spawns snow particles.
- **Visual snow** is a dynamic snow texture × season factor in the native shader.
  - Vanilla Main_map terrain sets `use_dynamic_weather_effects="true"` and `dynamic_flowmap_texture_name="main_map_snow_flowmap"`.
  - `MapScreen` calls `MBMapScene.SetSeasonTimeFactor` every frame.
  - **The GPU threshold comes from `MapColorGradeManager.ApplyAtmosphere`**, which calls `SetTerrainDynamicParams(Lerp(0.55,−0.1,season), 0.65, 0)`. **Our production scenes have no `MapColorGradeManager`.**
- **Vanilla terrain has no desert, snow or steppe material layers.** Regional ground colour lives in a Granite vista tileset (`WorldMap.gts/.gtp`), plus `worldmap_colorgrade_grid`.

### Runtime landscape APIs

- **No terrain-layer weight or texture setter exists.** Terrain layers name textures that must be compressed, square, 64–4096 and flagged `for_terrain`. A runtime `Texture.CreateFromByteArray` (RGBA) texture can never be a terrain layer.
- What does exist:
  - `Texture.CreateFromByteArray`, `CreateFromMemory`, `LoadTextureFromPath`, `GetPixelData`;
  - `Material.GetFromResource`, `CreateCopy`, `SetTexture`;
  - `Decal.CreateDecal` + `Scene.AddDecalInstance`;
  - `Scene.SetDynamicSnowTexture`, `SetLandscapeRainMaskData`, `SetExternalInjectionTexture`;
  - `GameEntity.Instantiate`.
- NavalDLC already creates entity-less decals (ship foam) on the campaign map at run time.

### Count limits

- **No cap on towns, castles, villages, kingdoms or clans.** A bounded scan covered 23 IL dumps, including CampaignSystem, SandBox(.View), NavalDLC(.View), MountAndBlade(.View) and SaveSystem.
- Real fixed sizes:
  - **2500 visual-dirty settlements per tick** (`SettlementVisualManager`).
  - **2500 visual-dirty visible parties per tick per manager** (land and naval). Overflow throws `IndexOutOfRangeException` inside `TWParallel.For`.
  - A static `UIntPtr[1024]` for entities tagged `ticked_map_entity` (unused by our scenes).
  - MBGUID: 2^26 objects per type.
- **Distance caches** are nested dictionaries keyed by (settlement, port flag) and grow O(n²). The navigation-cache build is the real scaling cost:
  - world A (45 towns): about 380 s;
  - estimated 20–26 min at 100 towns and 38–56 min at 150, on the first campaign of each world per module build.

### Villages (vanilla rules)

- **Binding.**
  - `Village.Deserialize` sets `Bound` only on a new campaign, and derefs it without a null check.
  - Castle villages get `TradeBound` from `DefaultVillageTradeModel`: the nearest same-faction town within 3 × the average closest-two-towns distance, else the nearest non-hostile town, else null.
  - Consumers null-guard it.
- **Food and garrison.**
  - Food is +6/12/18 per Normal village by hearth tier (<200/<600/≥600).
  - Garrison food factor is 0.5 + 0.5·min(50, Σ5·tier)/50. It saturates only at Σ5·tier ≥ 50, e.g. 4 rich villages; 3 rich villages give 0.95, and 0 villages give 0.5. (Corrected by the red-team settlements lens; this said "about 3–4".)
- **Zero-village crash paths:**
  - `SnareTheWealthyIssueQuest.GetTargetSettlement`: the nearest *other* town with 0 villages throws an NRE.
  - Incident `incident_the_quiet_life` throws in a player fortification with 0 villages.
  - Five villages break nothing.
- **Vanilla counts are only roughly 3 per town and 2 per castle.**
  - War Sails: towns 2/3/4 (22/27/8 towns; 30 of 57 not 3); castles 2 except two with 1.
  - Base game: 26 of 53 towns are not 3.
- **Hardcoded vanilla ids** that a generated world must tolerate: NavalDLC storyline (`town_V8`, `town_V7`, `town_S3`, `town_N1`, `town_A1`, `village_N1_2`) and StoryMode `village_ES3_2`.

### Settlement map visuals

- **Finding the entity.** The visual uses the scene entity named by the settlement id. Otherwise `AddNewEntityToMapScene` instantiates a prefab named exactly the id and never renames it.
- **Tags read:** `bo_town`, `main_map_city_gate`, `main_map_city_port`, `map_settlement_circle`, `map_banner_placeholder` (grouped by parent level 1..3; any other level throws KeyNotFound), `map_camp_area_1/2`, `map_siege_engine*`, `map_defensive_engine*`, `map_siege_ram`, `map_siege_tower`, `map_breachable_wall` (+ `map_solid_wall`/`map_broken_wall`), `siege_preparation`, `main_map_village_dropoff`.
- **Level masks.**
  - Scene levels: level_1 = 2, level_2 = 4, level_3 = 8, civilian = 16, siege = 32, looted = 64.
  - Towns and castles use `level_{fortification level}`; level 0 sets no tier bit. Villages use hearth tiers 200 and 600.
  - A child shows only when its level set contains every bit of the active mask.
- **Walls.** `WallSectionCount` is 2. A count other than 2 per level silently misaligns the wall-HP mapping, or throws in siege visuals.
- **War Sails drop-offs.** War Sails reads `main_map_village_dropoff` entities at wrapper start, matching `Parent.Name` to the village id with **no null check**. A misplaced drop-off throws.

## 3. Terrain types follow the biome (gameplay)

**Today.**
- Every land travel cell becomes a Plain face. The id comes from the bundle's `policies/playability.json` `cells.faceGroups.land` (`RuntimeNavigationBuilder.cs:248`, applied at `:542`).
- The biome is already available there: `NavigationInput.Surface` holds masks plane 0, read through `WorldSurface.BiomeAt` (`WorldSurface.cs:71,98`).
- What blocks more types:
  - the C++ travel compiler accepts exactly 8 face-group keys (`travel_graph.cpp:159-164`);
  - the calibration names only 8 groups;
  - `NavigationValidator` expects Plain at gate, settlement, pass and footprint faces (`:461`, `:526`).

**Design.**
- **Classify in the travel compiler**, which owns cell semantics. Add a `cells.landFaceGroups` table (biome core name → TerrainType name, plus a default) to `playability.json`. The classification:
  - carries the biome plane into the grid (`travel_graph.cpp:803-806`);
  - takes the majority of a land cell's 4 corner biomes, excluding ocean and lake, with ties going to the main diagonal;
  - runs **on the final classes after the village quota pass** (section 9);
  - is written to `cells.bin` v2 (`MNBLTC02`) as a third byte plane.
- **Managed side.**
  - `TravelCells.Read` accepts v1 and v2.
  - The builder takes the per-cell type.
  - The validator loops over the distinct land types (Default-valid, IsLand, Naval-invalid).
  - Gate, settlement, pass and footprint expectations become "any type of the cells touching the point". 15–16% of land-land edges change biome, so a single expected name would fail at random.
  - `GeneratedMapScene.ImageFace` (`:402-410`) must accept a same-region image face when the group differs at the seam.
- **Proposed table** (corrected by the critic so face types and textures agree):

  | FWGC++ biome | TerrainType |
  |---|---|
  | grassland 3 | Plain |
  | desert 2 | Desert |
  | forest 4, rainforest 7, boreal_forest 15 | Forest |
  | exposed_rock 5 | Steppe (open, dry; keeps Pathfinder/WaterDiviner) |
  | marsh 13 | Swamp |
  | cold_tundra 16 | **Steppe** unless permanently white (see section 5); Snow only where snow never melts |
  | land_ice 17 | Snow |
  | tundra 1, snow 6 (never generated) | Snow |

  Do not emit Dune (it loses Desert incidents and backdrops), Beach (no mechanics) or Fording (it blocks ships).
- **One speed model.** Snow and Swamp have no engine speed term. If they should slow parties, use one `GeneratedPartySpeedModel` registered with `AddModel<PartySpeedModel>` and chained over NavalDLC's wrapper. The same model carries the road bonus (section 8). Do not add a separate Harmony postfix.

**Balance shift.** On worlds A and B with the original table (cold_tundra = Snow), land was about 55% Snow, 27–31% Desert, 9–15% Forest and 3% Plain. With cold_tundra as Steppe:
- Plain perks still mostly disappear (Plain becomes rare).
- Desert slows most kingdoms by 10% on about 30% of land.
- Forest changes auto-resolve.

This needs one balance pass across terrain, roads and snow together.

**Edge case.** A battle-map cell whose 3×3 samples miss every land sliver lists no land scene. A Snow or Forest party there falls into the terrain filter, which has no Snow/Forest scenes: FailedAssert, then a random land scene.

## 4. Terrain looks like the biome (visual layers)

**Today.** Two weighted layers (grass, rock_k); a biome only sets a rock weight.
- The cap of 2 is enforced in four places:
  - `material_compiler.cpp:57`;
  - the compiler channel check (`terrain_compiler.cpp:246`);
  - the single-plane `MNBLWT01` format (`terrain_compiler.cpp:342-347`, read by `WorldSurface.cs:96`);
  - the encoder (`surface.cpp:140-143`, `:193-198`).
- The template `build/slots/mnbl_world_b/scene.xscene` is **gitignored build output copied from an installed battle scene**. A tracked, authored template (or generator) is a prerequisite.
- Weighted layers without a vista tileset render: proven for 2 layers (native_render-4988).

**Engine budget.** 16 slots. Vanilla terrain nodes use at most 15, and only 72 of 33,194 vanilla nodes use 15.
- Our globe spans 8 nodes of 400 units, so nearly every layer is present in every node: expect 12–15 per node.
- Per-scene terrain shader compile and the frame cost of that layer count are unmeasured.

**Blending (terrain shader source, `Shaders/Sources/terrain_*.rsh`).**
- Layers composite from index 15 down to 0, and layer 0 has weight 1 everywhere.
- A layer's weight can be multiplied by its diffuse alpha (`use_transparency_of_diffuse_alpha`) and can follow its height map (`use_transparency_of_ground_slope`).
- Tint: `albedo_factor_mode` 1 adds a colour, mode 2 multiplies and saturates (so it can only darken).

**Resolution.** One weight sample per 6.25 units (128×64 over the globe). Biome edges are soft gradients of about 6–30 units with kernel smoothing. Roads and beaches cannot be crisp in layers.

**Installed textures** usable as biome layers, with corrected names:

| Use | Textures |
|---|---|
| Sand | `desert_floor_a` (+`_n`/`_s`) |
| Arid soil | `desert_floor_c_d` |
| Steppe | `ground_drygrass_a_d`, `ground_drygrass_b_d` |
| Forest floor | `ground_forest_b_d_main_map`, `forest_a_d` |
| Mud | `ground_mud_b_d`, `ground_mud_a_d` |
| Beach | `ground_beach_a_d`, `ground_pebbles_a_d_mainmap` |
| Tundra moss | `ground_nord_grass_a_d` (NavalDLC) |
| Rock | `main_map_mountains_d`, `rock_k_d`, `stone_desert_a_d` |
| Snow and ice | `snow_a_d`, `snow_b_d`, `ice_a_d` |
| Ash | `ashes_a_d` |

- No tiling cracked-earth or conifer-needle terrain texture exists. A cracked-mud *decal* exists (`mainmap_cracked_mud_a`).
- The `*_mainmap` textures were tuned for an 848-unit tileset map and have never rendered as weighted layers; scale needs a look in game.

**Design.**
- **Template.** One fixed template within 15 slots, shared with the magic layers (section 6):
  - grass base, steppe, forest floor, sand, arid soil, mud, tundra moss, rock and permanent snow;
  - optional beach;
  - school layers;
  - **no road layer** (roads use decals, section 8);
  - **no seasonal-snow layer** if the dynamic snow texture works (section 5).
- **Weights.** Computed per output sample on canonical periodic columns:
  - target fractions from biome (kernel-smoothed), moisture, slope, height, temperature, `ice_cap`, landform and `rocky_coast`;
  - then converted to bottom-up alphas, calibrated with an in-engine ramp test (the engine may height-blend).
- **Encoder.** `MNBLWT02` (N planes + name table); WGHT per present plane; per-season node masks; a policy-driven `check_template`.
  - Port the existing `tools/scenebin/terrain_bin.py`, which already writes 16 slots and reproduces 99.8% of a vanilla PHYM.
  - Keep the Python encoder in parity.
- **Flora is unscoped.**
  - Template layer 0 carries grass and poppy flora in every season, so grass tufts would sprout on sand and ice.
  - Forests would read as a ground tint, not trees, because no map trees are placed.
  - Strip flora from weighted layers. Deciding on map trees is a separate question.
- **Colour grade.** The per-biome grade exists but is not applied on production scenes, which have no `MapColorGradeManager`. Add the manager at runtime; it is also needed for snow, section 5. Split grassland by moisture and mode-filter it. It is mood, not the biome look.

## 5. Snow and winter

**Today.**
- The snow byte is max(monthly snow, `ice_cap`). That makes many cold cells Snowy at mid-summer.
- "No safe creation path" for terrain snow (`water-climate.md:187`) is overstated: `CreateFromByteArray` works in this build (world preview), and the memory risk is only in the vanilla binder's `GetPixelData` readback.
- Our template terrain lacks `use_dynamic_weather_effects` and `dynamic_flowmap_texture_name`, and no manager drives `SetTerrainDynamicParams`.

**Design.**
- **(a) Gameplay.** Encode the byte from **snow-season length** k (months with snow ≥ 0.5) using the inverse of the installed formula, so the Snowy share of the year equals FWGC++'s snow season:
  - 196 → 12 months, 155 → 9, 113 → 6, 72 → 3, 30 → peak only;
  - `ice_cap` → 255.
  - Test at node level under the real `PeriodicWorldCoordinates` layout.
- **(b) Seasonal visuals.**
  - A higher-resolution RGBA raster (R snow, G rain) bound with `CreateFromByteArray` + `SetDynamicSnowTexture`.
  - Add `use_dynamic_weather_effects="true"` to the template.
  - Add a runtime `MapColorGradeManager` (or our own per-tick `SetTerrainDynamicParams`) so the shader threshold follows the season.
  - Never call the binder or `GetPixelData`.
- **(c) Permanent white.** A terrain snow layer only where snow never melts: `land_ice` and 12-month cells. The same mask drives the Snow face type (section 3), so face and look agree.
- **Hemisphere.** The engine season is global; FWGC++ southern months are inverted. Options: accept the northern phase, or build a latitude-aware weather model (fragile: NavalDLC wraps the model).

**Unknowns** (live test): see section 13.
- Does a vista-less terrain draw the dynamic overlay, and with which channel and orientation?
- Does the current template's winter grass variant (`snow_b_d` + `flora_snow_c`) already whiten the whole map in winter? If it does, neutralise it.
- Thread affinity of the texture bind.

## 6. Magic-mutation ground textures

**Data.** FWGC++ exports:
- `biome_variant`: −1, or an index into 13 cores × 12 schools. A cell mutates when potency ≥ 0.35 and its lead over the next school ≥ 0.08; read the thresholds from `world.json` `magic.mutation_threshold` / `dominance_margin`.
- `dominant_magic`, `ley_<school>`, `magic_density`, `magic_hazard`, `magic_opposition`, `zone_*`.

On world A, 1,784 of 5,063 land cells are mutated. Potency saturates at each network's strength (0.70; umbral and infernal 0.45). Frequent pairs: water×cold_tundra, desert×fire, desert×air, cold_tundra×fire. Hidden schools (blood, void, rot, eldritch) only come from the corruption API and are 0 in every export.

**Concept art.** `FWGC++/concept art/tier-1-style/` holds owner-approved studies for 11 cores, each in summer, winter, storm and ruins, plus a **weave** version of each.
- There is no art for the other 7 schools. Their recipes come from `magic-schools.json` descriptors and colours, `biomes.json` variant names and the catalogue's material briefs (`world-asset-requirements.json` MAT-001..048).
- The art is painterly whole-scene illustration. It gives material language and palette, not rendering. Example: desert.weave "Glass mirages" is pervasive pearl/opal glass sand with warm sandstone contrast and *no* rainbow overlay.

**Engine.**
- Terrain layers need compressed, square, `for_terrain` textures published through the Modding Kit editor (AssetSources → Publish Module → `.tpac`). NavalDLC is the only precedent for module-owned terrain textures.
- `deferred_decal_worldmap` samples Diffuse, Normal, Specular and Heightmap, so there is no emission. "Glow" has to be albedo seams and flecks.
- Spec is R specular, G gloss, B AO; vanilla terrain gloss means are 4–55. Normals are DirectX style.
- The editor console has `publish` / `publish_for_em` commands, reachable through `Utilities.ExecuteCommandLineCommand`. Headless use is untested.

**Pipeline (decided: derived from vanilla).**
1. Extract parents offline from `Native/EmAssetPackages/terrain_textures/**.tpac`. Data blobs are LZ4 blocks, except chunks with csize == usize, which are stored raw. Snow, ice and stone_desert sit in the large packs; the record walk needs fixing there.
2. Derive each set with a deterministic recipe that is luminance-preserving and wrap-aware.
3. Review against gates:
   - macro ΔE ≥ 10 vs the parent and vs neighbouring biome layers;
   - vanilla stat envelopes;
   - seam checks;
   - a BCn round-trip.
4. Import with the vanilla flags and publish.
5. Reference vanilla `_n`/`_s` by name where unchanged.

**Prototype result** (fire and weave, scratch only):
- Seamless and vanilla-grade is achievable; fire-scarred grassland got closest.
- Not production-ready. The weave sets read as dust or holographic glitter, not opal sand or flower drifts. Furnace dunes repeats visibly.
- **Identity at map zoom must come from a strong macro shift and from placement, not from patterns inside the tile.**
- Tile size in map units and px per unit at camera distance 38 must be measured in game first.
- Spec contradictions to settle before authoring:
  - `_h` vs `ground_slope_scale`;
  - parents per core;
  - gloss caps;
  - minimum accent size.

**Deliverable.**
- 8 school sets (2048², BC7 diffuse with alpha / BC5 / DXT1 / BC4).
- A decal stamp atlas per school with baked falloff.
- About 14–17 MB per set; the packs are release inputs (LFS), not CI outputs.
- Hidden schools reuse a visible school's look.

## 7. Runtime landscape mutation

**Requirement.** Heavy magic use (example: fire in an empire) turns its capital into a node of that school, and FWGC++ changes the surrounding landscape. The change must show in game, change gameplay and persist.

**FWGC++ today.**
- The library can plant or raise a ley key point (`LeylineEdit`, `magic.hpp:176-190`, intensity 0–4), and `evaluate_networks` + `add_biome_variants` recompute the landscape layers in about 0.1 s.
- But FWGC++ rules that **the ground moves only at an age boundary** (`world_time.cpp:526-527`):
  - `advance_world_time` refreshes only the ley fields;
  - `advance_ages` runs a 5000-year age;
  - `corrupt`/`summon` re-found settlements.
- There is no CLI that applies operations to an existing world, and no World reader. A world's identity is request + library + operation log.
- Magic never changes the natural biome, height, water or vegetation. It changes `ley_*`, `instability_*`, `magic_density/hazard/growth/opposition`, `dominant_magic`, `biome_variant`, `zone_*` and `lunar_sensitivity`.
- One intensity-4 point changes about 11–27 source cells (radius about 12–19 map units at size 129).
- At 2 of world A's 10 capitals a rival school is at full strength, so the result is contested ground with nothing visible. Nerdun is a capital where fire can win.
- **The exported baseline is stale.** Nomad edits and villain wells are applied after the last field evaluation, so the first re-evaluation moves about 75 variant cells far from the cast unless generation is fixed.

**Engine.**
- **Gameplay is fully reachable.** Every outside caller asks for terrain through `IMapScene`, and `GeneratedMapScene` already implements it, so new `GetFaceTerrainType` / `GetTerrainTypeAtPosition` / `GetEnvironmentTerrainTypes(Count)` members can answer from an overlay.
- `CampaignVec2.IsValid`, `ComputePath` and the embark checks validate the overlaid type. So remap **only within land-valid {Plain, Desert, Snow, Forest, Steppe, Swamp, Dune}**, never Bridge, Fording, Beach, water or Mountain, and never change passability.
- The battle index map is a managed `byte[]` we already build; patch the changed cells in every strip image.
- Native pathfinding and `MapEvent._eventTerrainType` read the face group directly and won't follow the overlay, which is acceptable.
- **Visuals.** Terrain layers cannot change at runtime. The live channels are:
  - runtime ground decals (or mesh decals with `deferred_decal_worldmap` as fallback);
  - particles and point lights at the node, as a separate root entity, not inside the baked settlement;
  - colour-grade grid cells (needs the runtime manager from section 5);
  - snow/wet masks (needs the dynamic snow work).

  `SetExternalInjectionTexture` is screen post-processing, not a terrain mask.
- **Re-baking at load is rejected.** It renames the scene and forces a nav-cache rebuild, or reuses caches across encodings, which the save-compatibility rules forbid.

**Design (decided: FWGC++ computes, look + terrain type, API + debug commands).**
1. **FWGC++ slice (Claude's lane; plan first; G3/G6):**
   - a logged verb `shift_leylines` (LeylineEdits → `evaluate_networks`, `add_biome_variants`, `lunar_sensitivity`; zones frozen; no `refresh_astrology`, no re-founding; refuses hidden schools and unmanifested or magic-off worlds);
   - `fwg apply --request R --operations OPS --delta D`, which writes the operation envelope plus changed cells only;
   - fix the generation baseline.
2. **MNBL:**
   - store the accepted `request.json` in each bundle and re-pin `fwg.exe`;
   - `WorldMutations.Report(MagicEvent)` + console commands; daily batch with thresholds;
   - `fwg apply` on a background thread during play, **never at load**;
   - results applied on the engine thread at a safe point;
   - one string `SyncData` key holding canonical JSON (edits, changed cells, digest);
   - load replays the stored cells only.
3. **Consumers.**
   - Overlay snapshot (base `biome_variant` + mutation cells), swapped atomically.
   - Face-type override, battle-index patch, decals and node effect, colour-grade cells, all on every periodic image.
   - **All magic ground (generated variants and mutations) uses this one runtime overlay** if the early probe shows decals work; then no magic terrain layers are needed. Otherwise generated variants bake into school layers and mutations use same-texture decals.
4. **Rules.**
   - "Capital" = the FWGC++ capital the kingdom still holds, else the ruler clan's home.
   - No decay in v1.
   - After a future producer change, old campaigns keep their stored mutations, and new casts are refused if the producer differs.
   - Nav caches never depend on the overlay; a test asserts it.

## 8. Roads

**FWGC++ output.** `roads.routes[]` (`from`, `to`, `nodes`, `river_crossings`, …) form a **minimum spanning forest between cities only**.
- No castles or villages, no loops.
- World A: 45 cities, 34 routes in 11 components, and 7 cities with no road. World B: 66 cities, 65 routes in 1 component.
- Routes follow the 8-neighbour source lattice: 6.25 × 3.125-unit stair steps.
- 137 of B's 828 road nodes are at 75° latitude or more.
- One B route crosses the south pole and must be split.
- City plans also carry `road_connections` (gate position and direction per route end), which MNBL doesn't read yet.

**Already used.** The travel graph carves mountain cells on roads into passes (A 14, B 45 cells), builds Bridge decks at joining crossings (A 2, B 17; 6 of 19 are L-shaped), validates a land route per road and faces gates toward roads. Roads are not drawn and give no speed.

**Vanilla.** No road terrain type, speed term or path cost.
- Map "roads" are about 1,200 small path decals (`decal_{empire,khuzait,battania}_path_{a,b}`, about 0.35–0.4 units) placed near towns and castles.
- Bridges are entities (`map_bridge_b/c/d/e`, `stone_bridge`, …).

**Design.**
- **Visuals at run time.** No scene or navmesh change, so scene names and the nav cache are unaffected.
  - Build a road network from `roads.routes` with a C# port of the NodeGrid decode, smoothed periodic-safely, split at poles and clipped at gates.
  - Draw entity-less path decals (NavalDLC's foam-decal pattern) with a per-culture family.
  - Instantiate bridge prefabs at existing decks, using the kept `navigation.json` `build.bridges`, because installed-scene loads skip `Prepare`.
  - Refresh decals and bridges to the image nearest the camera at the seam.
- **Speed.** The one `GeneratedPartySpeedModel` (section 3) adds a road factor on road-corridor cells (suggested +20%) and cancels the −0.3 ford penalty on road decks.
  - Better: have the compiler emit a road-flag bit in `cells.bin` v2 from the exact route lattice, instead of a runtime corridor with tie rules.
  - **AI pathing ignores roads.** The engine has no per-face cost, so the bonus helps the player more than the AI.
- **Gaps to decide:**
  - feeder roads to castles and villages (a mod-side generator along travel cells);
  - decks at the 30 (B) / 8 (A) short road crossings that end at a river (a navmesh change);
  - decal count and cost: about 1.4k–2.9k at 1.4-unit segments, up to 7.5k at vanilla spacing; the native cost is unknown.

## 9. Exactly 3 villages per town and 2 per castle

**Today.**
- The compiler binds every village to its core city's town; castles get none (`campaign_compiler.cpp:357-408`).
- `VillageBinding.cs` rebinds at run time: castles take 2 from their garrison town; towns allowed 2–5.
- World A after binding: towns 1:1, 2:9, 3:9, 4:5, 5:21 villages; castles 1:8, 2:18.
- The rule "nothing is ever added or dropped" is documented (`campaign-semantics.md:129`).

**Design: a WorldBridge `village_quota` pass inside `travel::run`.** It runs after placement, because new villages need positions, land groups and travel records; managed code can't add them.
1. **Pin ports.** Each port town's chosen harbour village stays with its town, so every port survives.
2. **Keep 3 per town.** Preference order: the pinned village, then optionally one more harbour village, then land villages that diversify production, then the nearest.
3. **Castles.** Each castle draws its 2 nearest from its garrison town.
4. **Reassign surplus** to short fortifications, own kingdom first.
5. **Drop the rest** with a `quota:` exclusion that doesn't count toward the travel cap. Notables move as today.
6. **Synthesize** missing villages inside a land cell of the fortification's land component:
   - clearance tiers that **scale with town spacing S**;
   - a stable `mnbl-quota-village:<fort uid>:<n>` uid, name, culture and role.
7. **Second analysis pass**, with pass-1 repaired classes frozen. Assert stable ports and no new exclusions.
8. **Record it.** `campaign.json` v2 records `bound` (town or castle), `sourceTown`, `origin` and a `villageQuota` block. `VillageBinding` passes it through for v2.

**World A simulation:** 131 kept, 30 to castles, 14–15 moved, 39–40 dropped, 11–12 synthesized → 187 = 45·3 + 26·2. All 30 ports kept.

**Risks.**
- **World A passes only through the tightest tier, by a 0.006-unit margin**, because of one islet town. World A is the only seed that activates today.
- 14 of 15 reassignments cross kingdoms.
- `exclude(castle)` with bound villages breaks closure; fix it.
- Re-pin chain: `naval-content.json` `water.playabilitySha256` and `startup.json` `economySha256`.
- Never generate a fortification with 0 villages (the vanilla crash paths in section 2).

## 10. Diverse settlement appearance

**Today.**
- Every town and castle uses the empire keep `mi_emp_keep_1` and empire walls; villages use `village_empire_1..3` (`SettlementEntityFactory.cs:54-58,181-191`).
- Settlement entities are **baked into the world scene at generation**. Activation refuses runtime-built settlements (`MissingBakedEntities`), and War Sails reads drop-offs at wrapper start. So the Player Settlement model (ship prefabs, instantiate at run time) does not fit.

**Source of variety: the War Sails Main_map.**
- Every settlement is a root entity named by id: 57 towns, 76 castles, 307 villages, 99 hideouts.
- **Towns:** 57 unique layouts.

  | Culture | Port | Inland |
  |---|---|---|
  | empire | 10 | 9 |
  | aserai | 5 | 3 |
  | vlandia | 4 | 4 |
  | sturgia | 3 | 4 |
  | khuzait | 2 | 4 |
  | battania | 1 | 4 |
  | nord | 4 | 0 |

- **Castles:** about 15 shipped base designs (2–3 per culture).
- Every town and castle hierarchy already meets our siege contract: 2 breachable walls and 4 defensive engines per level, engines 0..3, a ram, 2 towers and camps.
- 100 of the 539 hierarchies need NavalDLC-only assets, including 53 harbour villages through `mi_port_pier_*`.

**Player Settlement findings.**
- Its templates are player-picked, with a random fallback. Non-empire variety is thin: 8–9 distinct castle looks of 15 and 5–8 town looks of 10.
- **All 70 blacklisted templates contain nested prefab-instance entities** (`prefab=…`/`_index_`), and none of the 934 working ones do. Every referenced prefab exists, so the nesting is the trigger.

**The blocker is spacing.**

| Distance | Generated (A/B) | Vanilla |
|---|---|---|
| Town to nearest settlement of any kind, median | 4.7 | 21 |
| Town to nearest settlement of any kind, minimum | 1.25–1.56 (village one cell away) | 7 |
| Town to nearest town, median | 36.5 (A) / 37.7 (B) | 59.5 |
| Fortification to nearest fortification, median | 19.3 / 18.8 | 37.0 |

**Correction (red-team map lens).** The "about 4.5× closer" figure used in earlier drafts of this doc and of the epic rationale (both corrected 2026-10-19) measures towns against their nearest settlement of any kind, which is usually a village. It is a village-hugging problem, not town density. Town-to-town spacing is only about 1.6× tighter than vanilla, and fortifications about 2×. At the decided 0.5 scale, 32 of 45 (A) and 38 of 66 (B) town looks still overlap a neighbour, mostly villages within 2 units.

- Vanilla visible footprint radius: town 7.4, castle 5.7, village 4.0.
- Copied 1:1, 44 of 45 towns on A would overlap a neighbour.

**Design (two phases).**
- **Phase 0, works at today's spacing.** Culture-correct keeps (`mi_<cul>_keep_1..3` per wall level, an explicit table), walls (collapsed meshes exist only for battania, empire, nord and vlandia), `village_<cul>_1..3` and production pieces by village type, inside the existing code-built hierarchy and contract.
- **Phase 1, whole vanilla layouts baked into the scene.** Build a look catalog from the *player's installed* Main_map, so no TaleWorlds XML is shipped. Normalization:
  - inline nested prefabs;
  - strip editor data and SceneLeveler;
  - make every copied collider raycast-only (about 1,010 bodies);
  - replace source ports, arcs and drop-offs with our layout's.
- **Selection and fit.**
  - Selection is deterministic per world: hash, neighbour-repeat penalty, size tier and fit.
  - Port towns are aligned to the generated port. Generated gate-to-port angles run 0–180°, vanilla 80–176°.
  - Children are re-snapped to terrain: child z values are parent-local and fitted to Calradia. Whether the engine applies `align_to_terrain` is unknown.
- **Variants where looks run short:**
  - port-stripped Nord towns for inland Nord;
  - port-grafted inland looks for Battania and Khuzait ports;
  - castle layouts as small towns.
- **Phase 1 needs settlement spacing first**, either from generation or by shrinking or culling looks. Otherwise it delivers little.

**Costs.**
- About 4× more entities (A about 6.3k → 25k), and display copies at the seam double that.
- Scene XML grows by about 18–20 MB per world.
- The look catalog makes scene bytes depend on installed TaleWorlds files, so a game patch changes rebuilds. Freeze the selected look XML in the world package.

## 11. No generated city is lost

**MNBL drops no city today (observed end to end).** Every row of FWGC++ `settlements.sites` becomes exactly one town, with the same uid set, at each stage:

| Stage | World A | World B |
|---|---|---|
| sites | 45 | 66 |
| compiled towns | 45 | 66 |
| travel graph | 45 | 66 |
| bundle and id-map | 45 | 66 |
| in-game Town count | 45 | 66 |

- Towns can never be excluded (`campaign_compiler.cpp:578`).
- Cities on the seam column (15 of 20 parsed worlds) are kept.

**The "24-city max" is inert.**
- FWGC++ refuses (not clamps) an explicit `settlement_count` above 24 (`config.cpp:261`).
- For recipe 3 the limit is the spacing/packing ceiling (`settlement.cpp:229-235`), between 15 and 103 on real worlds.
- MNBL never sends `settlement_count`.
- World size 33's "24 towns" is a coincidence of history.
- Stale wording to fix: FWGC++ `settlements_stage.hpp:33`, `config-controls.json:325`; MNBL `campaign-content.json` notes and `campaign-semantics.md:124`.

**Where cities are really lost.**
1. **The travel gate rejects whole worlds** (`travel_graph.cpp:670-682`, `:695-715`).
   - **What triggers it.** One town with no land cell within 2 cells (`unplaceable_town`) or in a land group that isn't player-safe (`unreachable_town`). So do non-town repair bounds: snaps, passes, footprints and harbour shifts.
   - **What happens next.** Generation retries with a derived seed, which is a different world. After 3 attempts it is exhausted.
   - **How often.** 30 of 31 seeds (43–73) were rejected, every one carrying a town code. At that rate a random seed fails all 3 attempts about 90% of the time, and **every city is lost**. This is why only seed 42 activates.
   - Rejected attempts' city counts are not recorded.
2. **Failures after the gate are never retried.** These throw `GenerationFailure`, and any town-specific runtime problem costs the whole world:
   - bundle compile;
   - `SettlementFactory` refusals;
   - missing settlement positions;
   - graph validation (`graph.silent_discard`);
   - settlement-position refusals.
3. **FWGC++ history ruins about 60% of founded cities** (A 63 of 108, B 99 of 165: war, dragon, monster, magic). `war_survival` defaults to 0.
   - These are FWGC++'s own output and live in `ruins[]` (name, class, civilization, cause, `marker.city_ruins`). MNBL ignores them.
   - Indirect controls exist but are unpublished: `war_survival`, monster/nest density, `villain_rise`, `magic_enabled`.
4. **Latent case.** FWGC++ can report more sites than its target after an age rebuild (survivors are kept unconditionally). MNBL would then refuse at `campaign_compiler.cpp:254` without retry. It doesn't occur in any of the 22 parsed worlds.

**Not cities, not mapped (for reference).**
- `ruins[]` and `ruin_squat` camps;
- `nomads.groups`;
- wizard colleges;
- shrines and cults;
- `key_locations.sites`, including fortification-family sites (hillfort, border fort, …);
- `beast_nests`;
- sky settlements (0 in recipe 3).

**Design.**
- **Repair towns instead of rejecting worlds:**
  - a towns-only larger snap radius;
  - carving a land cell under the town;
  - joining an unreachable town's group with a deck or pass, or a forced port for an island town.
- **Make retries keep the requested seed** when the cause is repairable.
- **Make preservation an acceptance assertion per world:**
  1. sites = placed;
  2. placed = compiled towns = travel towns = id-map towns;
  3. that count = in-game Town;
  4. same uid set;
  5. accepted attempt = attempt 0, or a report of the rejected attempts' counts.

## 12. Cross-cutting constraints

**Save compatibility.** Every C++ change in this epic renames every world's scene, because the scene name hashes `worldbridge.exe` (`RuntimeWorldBackend.cs:174,188`). A swept-scene load is then refused (`GeneratedWorldLoader.cs:402`). Climate identity (`mmr39/1`), the colour-grade sha and the scene contract are also checked on load.
- Keeping old saves would need dual paths in five subsystems:
  - cells v1/v2 + validator version;
  - `MNBLWT01` + the 4-layer template;
  - climate `mmr39/1`;
  - legacy `VillageBinding` + `economy.json`;
  - settlement builder `mmr40/2`.
- Even then, swept-scene saves can't be rescued. **A clean break is the realistic option.**

**One build, one rename.** Each of these alone renames scenes or forces a nav-cache rebuild (about 6.5 min per world, more with more towns):
- face types;
- template layers;
- encoder exe;
- settlement visuals;
- embedded policies.

They must land together for the single live test.

**Layer budget.** Biome, magic, snow and road demands add up to 20+ against 15 usable slots. Resolve it as:
- roads become decals;
- seasonal snow uses the dynamic texture;
- marsh/beach are optional;
- magic is per school or per family (section 6).

**One classifier.** Four classifiers disagree at biome boundaries today:
- travel-cell corner majority (face types);
- smoothed samples (layers);
- mode-filtered grid (colour grade);
- battle-cell centre (scene class).

At minimum they must share one table, so face type, texture and battle scene agree.

**Ownership of `travel_graph.cpp`.** The village quota, biome face types and road flags all edit it. Order:
1. village quota;
2. face classification on the final classes;
3. host-check text.

One owner, serial.

**Policy re-pin chain**, done once in dependency order:
1. `navmesh-bin.md`;
2. calibration `faceGroupTableSha256`;
3. `playability.json:6`;
4. `naval-content.json` and `startup.json` pins;
5. test literals.

`playability.json` and `terrain-import.json` are generation-bound policies, so in-flight requests are refused (`policy_changed`).

**Bundle format.** `cells.bin` v2, `weights.bin` v2 and `campaign.json` v2 should share one `requiredCapabilities` entry. An older module then refuses cleanly at `WorldDataBundle.Open` instead of throwing mid-activation.

**Seam.** Several seam changes need one shared seam checklist in the live test:
- `ImageFace` hardening;
- decal and bridge image refresh;
- canonical columns for weights;
- canonical sampling for the snow raster.

## 13. Native unknowns and the early probe

These can't be settled statically. The early probe settles them; it needs no campaign and no nav cache, follows CLAIM/RELEASE, and records the native DLL hashes.

1. More than 2 weighted layers on our vista-less terrain:
   - the compositing model (alpha-over vs height-blend; ramp test);
   - per-season masks;
   - frame and load cost at 12–15 layers per node;
   - terrain shader compile.
2. Terrain tile size in map units for a layer `scale`, and px per unit at camera distance 38.
3. Runtime decals on the generated map:
   - entity-less path decals over relief;
   - a `CreateCopy`+`SetTexture` decal (pixel diff, not frame checks);
   - the mesh-decal fallback;
   - cost at 200 / 1,000 / 3,000;
   - removal and re-creation.
4. Dynamic snow:
   - `use_dynamic_weather_effects` on our terrain;
   - `SetDynamicSnowTexture` with an asymmetric pattern (channel, orientation);
   - season response with a runtime `MapColorGradeManager` / `SetTerrainDynamicParams`;
   - does the template's winter grass whiten the whole map?
5. Tier-0 tints (mode 1 vs 2) and the full-colour `_d` biome textures at map scale.

Only in the final live test:
- Snow, Swamp and Forest faces in native sound and missions;
- per-group native path cost (route-matrix ratio);
- overlay install before `PreAfterLoad` / `ComputePathAfterLoad`;
- nav-cache time with more settlements.

## 14. Decisions (owner, 2026-10-18)

| # | Decision |
|---|---|
| 1 | **Clean break**: old generated worlds and saves stop loading; one code path |
| 2 | **FWGC++ computes mutations** (new FWGC++ slice) |
| 3 | **One early in-game probe** allowed (section 13); otherwise testing at the end |
| 4 | **Magic textures derived from vanilla**, with concept art as direction |
| 5 | **Scaled-down vanilla layouts** for towns and castles (about 0.5, trimmed to fit), culture pieces as fallback |
| 6 | **Roads:** speed bonus and feeder roads to castles and villages; no new bridges beyond existing decks |
| 7 | **Mutation v1:** look and terrain type; triggered by API and debug commands |
| 8 | **Ruins:** a marker per FWGC++ ruin with its name (no gameplay) |
| — | "No cities dropped" means the world FWGC++ generates keeps every city: repair towns instead of rejecting worlds |

**Added 2026-10-18 (second request):**

| # | Decision |
|---|---|
| 10 | **Smooth the terrain** (section 15) |
| 11 | **Noble houses, mercenary companies and wanderers** replace one-clan-per-lord (section 16) |
| 12 | **FWGC++ notable roles stay notables**, except warlords, who become mercenaries |
| 13 | **Targets scale with fief count at War Sails ratios** (per 133 fiefs: 82 houses, about 4.95 adult lords per house, the owner's 12 companies of 6–10, 35 wanderers) |
| 14 | **Neighbouring same-civilization realms merge into kingdoms.** Former realm rulers become vassal houses that keep their fiefs (default taken; the question was skipped) |
| 15 | **The engine's random child generator stays on**, as in vanilla: it adds about ceil(adults/2) parentless children per house on top of the planned members |

**Defaults taken (change if wrong):**
- `cold_tundra` → Steppe face with seasonal snow; Snow face and permanent white only for `land_ice` and 12-month snow cells.
- Northern-phase seasons everywhere.
- Exactly 3/2 villages, with tight clearance tiers for islets; never reject a world for the quota.
- Snow −0.15 and Swamp −0.2 speed terms, with one balance pass at the live test.
- Hidden schools reuse visible looks.
- FWGC++ zones frozen after generation.
- Map trees and flora placement out of scope (weighted layers carry no flora).

## 15. Terrain smoothing

**Why it looks sharp (measured on worlds A and B; verifier-corrected):**
1. **2.5× vertical exaggeration** is the main cause. `projection.json` sets z = h × 0.01 against a horizontal 0.004 units/m. At true proportions, world A's interior slope p95 is 39° and world B's is 30.7°, close to vanilla's 34.1° / 30.3°.
2. **Coarse sampling.** Every height sample is a raw FWGC++ source sample, 6.25 map units apart, and odd source rows are dropped. Bilinear interpolation is never actually evaluated. Vanilla samples are 24–30× denser (0.21–0.25 units), so the engine draws flat triangles 6.25 units wide: facets and creases.
3. **A peaky source shape.** FWGC++ ridged noise and belt ridges put peaks one sample from low ground (land max/p95 4.1 vs vanilla 1.7). Land also meets deep sea within one sample.
4. **Lakes.** Lakes are already mostly hidden today: 28/33 lake samples on A, 699/839 on B. World B has lake beds at −17 to −28 next to sub-datum land that coherence flattens to 0.05, which is a cliff source of its own.

**Not causes:** quantization (0.00175-unit steps) and coastline coherence, which touches 18 samples.

| World A | Today | Vanilla (War Sails) |
|---|---|---|
| Land slope p95 | 66° | 34° |
| Land slope max | 82.5° | 64° |
| Faces over 45° | 17.3% | 0.7% |
| Crease p95 | 42° | 28.6° |

**What doesn't work alone:**
- bicubic at 257 samples (no-op);
- plain Gaussian (blurs coasts, hides lakes);
- a shaping pass that pins lakes (doesn't converge on B, hides lakes).

**Design, in order of value:**
1. **Lower `verticalScale`** toward true proportions (about 0.005), scaling `mountainSlope` and the `slopeRock` thresholds by the same factor.
   - Travel classes stay byte-identical (0 cells differ on A and B) and lakes are unaffected.
   - Interior p95 becomes about 45° / 36° and steep pads 6 / 9.
   - Review absolute-height consumers: `PartyGroundQueries.cs:60` asserts 0.01; camera, entity and party z offsets.
2. **Light shaping** on the canonical periodic grid, run **after travel placement** so pads use final settlement positions:
   - despike land samples (ridges and valleys kept);
   - coast envelope;
   - settlement pads, with touching blocks merged;
   - **lake handling:** flatten beds below the lake level and keep shores above it;
   - coherence ordering fixed so sub-datum land beside deep lakes doesn't regain cliffs.

   Navigation keeps its own (scaled, unshaped) field. Report the nav/visual coast mismatch.
3. **Smoothed normals.** `surface.cpp` builds normals with Sobel; build them from a smoothed copy of the field. This is cheap and changes neither geometry nor navigation.
4. **A mild low-pass at 257** (about [1,4,1]/6), masked to keep sea, lakes and coast. It recovers most of the crease gain: crease p95 about 23° / 18°.
5. **513-sample cubic B-spline output** (level 7, 128 cells per node), probe-gated. Crease p95 falls further, but it changes:
   - `playability.json` `grid.side`;
   - the encoder;
   - `WorldSurface`;
   - the travel-grid subsampling;
   - `Test-TravelGate.ps1` and `summarize_terrain.py`.

   Level 7 is unproven in our encoder.

**Offline gates (A and B):**
- interior slope p95 ≤ 45°, max ≤ 60°;
- coast p95 ≤ 50°;
- pointy peaks 0;
- pads over 25° ≤ 10;
- travel classes byte-identical;
- no lake newly hidden;
- seam pairs identical;
- C++/Python parity;
- the existing `coast_sign` refusal still passes.

Peak landmark heights no longer match their source metres; that is expected.

## 16. Noble houses, mercenary companies and wanderers

**FWGC++ facts:**
- FWGC++ produces a cast of single named people, not families. There is no sex, age, parent, spouse, house or surname on hero records; the only kin link is pretender ↔ magister of a ruined city.
- Each person's **gender and age** live in the separate NPC roster (`npcs.people[].appearance`), which MNBL ignores today.
- **One realm per culture:** 40 on A (37 with a single city), 54 on B. There is no suzerain hierarchy.
- **Noble anchors:** sovereigns on capital thrones (A 32, B 51); castellans posted at specific fortresses (A 16 of 26, B 23 of 39) and sworn to the sovereign; pretenders (heirs of fallen cities, A 35, B 61); council magistrates and commanders.
- **A roll ledger** gives a grounded candidate for every castle without a castellan.
- **Culture household customs:** naming patterns (house name, clan name, patronymic, workshop, cohort, vessel), inheritance and lexicon roots. Hereditary-lord cultures are desert humans, heartland humans and frosthold dwarves.
- **Mercenary sources:** 17 remnant orders ("the Order of X", each led by a champion whose join condition is "hired"), deserter and bandit bands, ruin camps, and living warlords.
- **Wanderer sources:** champions, magisters, exiles, ruin-warden domain holders. `domain_holder` is not a lord of anything; `docs/campaign-semantics.md:96` is stale.
- **Dreads** are a monster cast (no gender) and **legends** are dead warlords; neither is instantiated.
- **Living heroes:** A 319, B 440.

**MNBL today (world A):**
- 92 clans: 40 ruling + 52 single-lord vassal clans, one per castellan, pretender and founder.
- 241 XML lords, plus 130 parentless children that the engine's `InitialChildGenerationCampaignBehavior` adds.
- 28 wanderers (vanilla 0.6 × towns; templates can repeat; sex from template).
- 0 mercenary clans; notables filled to vanilla targets.

**Vanilla reference** (recounted from installed XML; the War Sails merge restates SandBox and adds Nord):

| | War Sails | SandBox only |
|---|---|---|
| Towns / castles | 57 / 76 (133 fiefs) | 53 / 67 (120 fiefs) |
| Kingdoms | 9 | 8 |
| Noble clans | 82 | 73 |
| Alive XML lords | 443 = 406 adults + 37 under 18 (2–10 per clan, mean 5.40) | 389 = 355 adults + 34 under 18 (mean 5.33) |
| Engine children added at new game | about 187 | about 162 |
| Minor clans | 14 (4 mercenary, 5 mafia, 4 nomad, 1 sect), heroes spawned at runtime from templates | 14 |
| Wanderers at start | 35 (0.6 × 57 towns) | 32 |
| Wanderer templates | 78 | 67 |

**Random child generator** (`InitialChildGenerationCampaignBehavior`, IL):
- **When it runs.** Once, at the new-game follow-up index 0, before any mod listener on that index.
- **Which clans.** Every clan that is not minor, not bandit and not the player clan.
- **How many.** It adds ceil(adults/2) − existing children. Pre-created children count against this.
- **The child.** Aged 2–17 and 49% female. It is cloned from a random same-sex adult of the clan (culture and occupation come from that template hero) and has no parents. Its civilian equipment comes from four culture rosters (Lord + Child / Teen, ± Female), which ship for the 7 vanilla cultures.
- **Fallback.** With no same-sex adult, it draws from another non-bandit clan of the same culture. It throws at new game when no such clan exists, and skips the child after 10 failed draws.
- **For generated houses:**
  - every house has an adult of each sex;
  - lord cultures are vanilla cultures;
  - lords are XML heroes, so they are counted.

**Engine rules (REA, observed):**
- Heirs, party leaders and marriage need Occupation Lord and age 18+. A house needs at least 2 adult Lords to survive its head's death.
- Noble parties: 1 below tier 3, 2 at tiers 3–4, 3 at tier 5+. Minor clans: Clamp(tier, 1, 4).
- **Minor clans** need `is_minor_faction`, an owner, a tier, `default_party_template`, a non-empty `minor_faction_character_templates` and a home settlement.
  - **Correction (red-team politics lens):** `is_clan_type_mercenary` is not required for hiring. `DiplomaticBartersBehavior.DailyTickClan` sends every minor clan to `ConsiderClanJoinAsMercenary`, and the hire dialogue checks `IsMinorFactionHero`.
  - Vanilla's 10 outlaw clans (mafia, nomad, sect) are hireable too, and are at constant war with kingdoms of their own culture.
  - `MinorFactionHeroLimit` is 4 (global); the engine refills below 4 with engine-named male template heroes.
  - `Hero.IsMinorFactionHero` is set only by the engine's template spawner. The hire dialogue, marriage, AI and volunteer models read it, so XML mercenaries must have it set at new game. MNBL's current hook runs after minor-clan parties spawn.
  - Hiring through dialogue needs the player to rule a kingdom; AI kingdoms hire through `ConsiderClanJoinAsMercenary`.
- **Wanderers.** `CompanionsCampaignBehavior` keeps 0.6 × towns (hired included), kills an unhired wanderer at 10% per day and swaps them between taverns. `CreateCompanionAndAddToSettlement` picks its own template, so a controlled top-up must re-implement creation. Templates fix sex: 22 of 78 are female, 2–4 per culture.
- **Kingdom discontinuation.** `FactionDiscontinuationCampaignBehavior` dissolves a kingdom the moment it holds no settlement. Its houses then have 28 days to join another kingdom before every member is removed. This is the largest vanilla path that kills heroes for small kingdoms; merging realms reduces it.
- **After day 0:** AI marriages move spouses between clans, and houses can defect between kingdoms. Day-0 shape is not permanent.

**Design (decisions 10–15):**
- **Kingdoms.** Same-civilization realms that border each other on land (same land group, neighbouring towns) merge into one kingdom.
  - Large components split at a policy maximum (War Sails kingdoms hold about 15 fiefs).
  - The highest-ranked member realm's sovereign rules.
  - Other realm sovereigns, or the synthesized regents of vacant realms, head vassal houses that keep their own cities and castles.
- **Targets per world**, scaled by fiefs F at War Sails ratios:

  | World | F | Houses H = 82·F/133 | Adult lords ≈ 4.95·H | Companies M = 12·F/133 (6–10 each) | Wanderers W = 35·F/133 | Engine children (on top) |
  |---|---|---|---|---|---|---|
  | A | 71 | about 44 | about 218 | 6 | 19 | about 110 |
  | B | 105 | about 65 | about 322 | 9–10 | 28 | about 160 |

  H is at least the number of kingdoms. No house holds more than 4 fiefs: a forced seat house is created when needed.

  **Changed 2026-10-19 by the red team (§17):**
  - Kingdoms merge toward K = 9·F/133 with at least 3 houses each: civilization first, then presentation culture and proximity, across water if needed (RT-01).
  - Outlaw clans are added at O = 10·F/133 (RT-02).
  - Wanderers follow War Sails' own rule, W = 0.6f × towns, with slow rotation (RT-04).

  Expected (K / H / adult lords / M / O / W): A (45 towns) about 5 / 44 / 218 / 6 / 5 / 28; B (66 towns) about 7 / 65 / 322 / 9–10 / 8 / 40. The table above keeps the superseded W = 35·F/133 column for the record.
- **Houses.**
  - **Seat priority:** kingdom rulers → other realm rulers → castellans of their castles (synthesized from the roll ledger where missing) → pretenders and magistrates of non-capital towns.
  - **Members.**
    - Regal FWGC++ people go first: sovereign, pretender, castellan, founder, magistrate, commander.
    - Then synthesized adult kin, using FWGC++ resident names of the civilization and the culture's naming pattern (Age tags stripped, unique without numerals):
      - spouses;
      - siblings, with an `alive="false"` parent hero emitted;
      - adult children.
    - Parented minors are optional, up to about 8% (the War Sails XML share).
  - **Sizes.** Chaotic 3–8 draws (about 4.95 adults on average), at least 2 adult Lords including one of each sex, cap 10. FWGC++ sex and age come from the NPC roster, with clamps.
  - **Fiefs.** Every fief is owned by a house of its kingdom; each castellan heads, or belongs to, the house owning its castle.
- **Mercenary companies.**
  - Captains are FWGC++ remnant champions (join condition "hired"); fallbacks are a living warlord, then a domain holder, then a synthesized captain.
  - Name, culture and home town come from the order and its captain.
  - Members: the ruin's warden, living warlords, domain holders, then synthesized soldiers named from FWGC++ garrison residents.
  - Each company carries the culture's mercenary party template and 4 leader templates; unique colour pairs world-wide; `IsMinorFactionHero` set at the earliest new-game hook.
  - Companies decay toward vanilla's floor of 4.
- **Wanderers.**
  - Picked from champions not used as captains, magisters, exiles and remaining domain holders, rotating by civilization.
  - Each keeps its FWGC++ sex. Templates are distinct within a culture where possible, with a recorded cross-culture fallback.
  - A replaced `DailyTick` keeps W (hired included): no 10% cull, swaps kept, and an own top-up that creates FWGC++-style wanderers.
- **Notables.** Council seats other than magistrate and commander, harbourmasters, reeves, keepers, heresiarchs and prophets stay named notables; vanilla fill tops them up.
- **Every living FWGC++ hero ends in exactly one bucket.** Overflow goes to house members (cap 10), then wanderers, then companies, and is asserted 0 on A and B.
- **The engine's random child generator stays on** and adds parentless children on top, as in vanilla. Births, rebel clans and notable replacement also remain engine hero sources (noble bucket or notables).

## 17. Player red team (2026-10-19) and where each fix lands

**Method.** Six read-only "gamer" lenses: map, politics, settlements, quests, battles and meta. They used REA/IL on the installed DLLs, the installed XML, the committed research bundles (worlds A and B) and the epic's saved revision 3.
- The coordinator re-checked these load-bearing claims in code or IL:
  - the world sweep;
  - the incident kingdom-id literal;
  - the castle-village hearth share;
  - wall level 1;
  - the land-group counts (re-derived by a second lens from compiler fields).
- Corrections to earlier sections are recorded in place: §1, §2 battle scenes, §2 garrison food, §10 spacing, §16 minor clans.

**Owner ruling (2026-10-19):** fold every fix into map epic 2, including the existing-MNBL problems.

**Defaults taken while folding** (state them; change if wrong):
- Kingdoms merge toward vanilla density, not just same-civilization neighbours.
- Outlaw minor clans return at the vanilla ratio.
- Wanderers use War Sails' own 0.6 × towns rule, with slow rotation.
- Generated magic variants are visual only; only live mutations retype terrain.
- Map trees come in, gated by the probe.
- Roads end at the bank of a crossing with no deck (decision 6 keeps out new bridges).
- The exact 3/2 village rule stays (owner decision), and its trade-area cost is recorded.

| RT | What the player notices | Origin | Fix | Ticket |
|---|---|---|---|---|
| 01 | World A ends with 26–40 kingdoms, mostly one house each: no vassals, no elections, nobody to recruit | epic (merge never quantified) | Merge to about 9·F/133 kingdoms with ≥3 houses each: civilization first, then presentation culture and proximity, across water if needed. Record K | MMR-279 |
| 02 | 10 of 14 vanilla minor clans (all outlaw: mafia, nomad, sect) missing, with their 30 troops | existing MNBL; epic kept | Outlaw companies at 10·F/133, vanilla outlaw templates. `is_clan_type_mercenary` is not needed to be hired. Constant war with same-culture kingdoms needs a guard | MMR-279, MMR-280 |
| 03 | Companies may field plain lord troops; battania, khuzait and nord have no mercenary line; MMR-248 said 120 fiefs where 133 was meant | epic (coordinator error) | Per-culture template table (battania → wolfskins/outlaw_battania, khuzait → karakhuzaits, nord → skolderbrotva/mercenary_sturgia); one fief base, 133 | MMR-248, MMR-279, MMR-280 |
| 04 | About 30% fewer tavern wanderers per town, never rotating | epic | W = 0.6f × towns (War Sails' own rule); slow rotation of unmet free wanderers into fresh FWGC++-style ones | MMR-279, MMR-281 |
| 05 | Lords of a culture share 2–6 personalities | existing (template `<Traits>` copied) | Redraw traits per hero from vanilla's lord-trait distribution, seeded | MMR-280 |
| 06 | No wars on day 0 | existing (deliberate neutral start) | Seed about one starting war per kingdom with a neighbour (value −1) | MMR-279, MMR-280 |
| 07 | A one-town kingdom falls and its FWGC++ house is deleted 28 days later | existing; epic kept | Merge (RT-01), plus a rule that a homeless house joins the nearest same-culture kingdom | MMR-281 |
| 08 | Fishing-heavy, horse-poor villages; every Vlandian harbour village is a whaler | existing (VillagePool) | Coastal draw at vanilla's coastal fishing share; world-level floors for each culture's horse ranch and wheat | MMR-252 |
| 09 | The quota drops villages blind to type (A loses half its horse ranches); synthesized villages' type unspecified | epic | Never drop a culture's last ranch or wheat farm; type synthesized villages by the largest deficit | MMR-252 |
| 10 | Castle villages may all get the minimum hearth under campaign v2 | epic × existing hearth rule | Hearth share from `sourceTown` (garrison town for synthesized ones); test the distribution | MMR-252 |
| 11 | FWGC++ reeves of dropped villages silently vanish; the census still counts them | epic | Move them to a kept or synthesized village of the same fortification; the census applies HeroFactory caps | MMR-252, MMR-279 |
| 12 | A repaired island town's forced port may never become or stay a port | epic | Back a forced port with a harbour village; `exclude()` erases only the ports it emptied | MMR-251, MMR-252 |
| 13 | Villages inside town footprints and stacked nameplates (20/23 within 2 units; vanilla none within 5) | existing placement; epic kept | A clearance pass before looks: no village within 4 units of a fortification, no shared points | MMR-252 |
| 14 | Towns repeat streets and siege maps; the map look doesn't match the scene; every wall starts at level 1 | existing + epic MMR-258 | One selector (look = the prototype's own hierarchy when it fits, balanced usage); initial wall levels drawn from the installed distribution | MMR-258 |
| 15 | Villages show no production on the map, and nothing checks it | existing; epic criteria gap | Village look criteria in MMR-258 and MMR-247 | MMR-258, MMR-247 |
| 16 | Rivers and most lakes are invisible but block land parties | existing | Draw rivers and show every lake; a water-visibility gate | MMR-285 |
| 17 | Drawn roads run into rivers with no bridge | epic | End road decals at the bank of crossings with no deck; feeder roads never cross water; offline count | MMR-253, MMR-257 |
| 18 | Every world has the same single east-west wind | existing (humidity arrows used as wind) | Latitude-banded, seed-perturbed winds | MMR-256 |
| 19 | Polar land is stretched 3–6× and the camera pans past the poles | existing projection | Clamp camera y to the pole lines; record the polar share as a known limit | MMR-278 |
| 20 | The magic overlay would retype 35–50% of land, outside the balance plan | epic | Generated variants are visual only; only live mutations retype; record per-type shares | MMR-248, MMR-260 |
| 21 | Forests slow and hide parties but show no trees | epic (deliberate gap) | Forest flora (map trees) through `flora.bin`, checked in the probe | MMR-249, MMR-255 |
| 22 | Ruin markers in the sea and on villages | epic | Snap or skip non-land cells; minimum gap from live settlements | MMR-257 |
| 23 | No gate on frame rate or memory of the combined map | epic | Uncapped fps and memory capture with a vanilla War Sails baseline | MMR-247 |
| 24 | Battles near settlements and water open Plain/Steppe whatever the biome | existing MMR-42 | Robust ring only for port settlements; class-correct land in sea-touching cells | MMR-254 |
| 25 | Forest battles away from water use 1–2 maps | existing | Widen woodland to High ∪ Low forest density | MMR-254 |
| 26 | The mutated zone's battle scene change is undefined; Dune is in the remap set | epic | Mutated cell class = faceClass[remapped type]; re-run Choose; no Dune | MMR-248, MMR-260 |
| 27 | The generated Khuzait homeland becomes forest that penalizes cavalry | epic × culture mapping | Per-culture home-terrain review in the table and the balance pass | MMR-248, MMR-247 |
| 28 | Hideout bandit type follows the nearest town's culture, not the ground | existing | Weight the bandit culture by the hideout cell's biome too | MMR-287 |
| 29 | Four incidents never fire (they compare kingdom ids to "sturgia" and the like) | existing | A world-lifetime transpiler comparing culture ids instead | MMR-287 |
| 30 | Seven terrain incidents depend on Desert/Forest/Steppe faces near the player | existing; epic restores | Offline count of those faces where the predicates sample | MMR-254 |
| 31 | A third 0-village crash path: `incident_wages_of_war_and_diseases` | existing; quota closes | List it; test no 0-village fortification | MMR-252 |
| 32 | Wanderers tell Calradian backstories under FWGC++ names and lose their epithet | existing; epic kept | Restrict to the 54 templates with no Calradian proper nouns, or override the backstory strings; keep the epithet | MMR-287 |
| 33 | The top-up spec omits AdjustEquipments and ChangeState(Active) | epic | Use MMR-34's four creation steps | MMR-281 |
| 34 | StoryMode, the War Sails storyline and achievements are unavailable (as in vanilla Sandbox) and undocumented for players | existing (deliberate) | State it in the player docs | MMR-284 |
| 35 | Generating or previewing a world deletes other worlds' scenes and caches: a 6.4-minute rebuild, or a permanently unloadable save after an update | existing | Never sweep scenes or caches that a bound save or package references | MMR-282 |
| 36 | Almost any content mod blocks generated campaigns; a battle-scene mod likely hangs after the cache build | existing | Pin only what the world references; pre-check at setup naming the mod; catch the AfterLoad battle-table failure | MMR-283 |
| 37 | A producer update permanently disables mutation; a digest mismatch hides all magic ground | epic | Keep earlier `fwg.exe` builds for old campaigns; scope a digest failure to mutations only | MMR-261 |
| 38 | "About 7 minutes" is promised for any world; Continue after an update freezes with no warning | existing; epic enlarges worlds | Per-world wait estimate in the preview; a warning before loads with a missing cache key | MMR-282 |
| 39 | Nobody measures whether a random seed reaches the campaign map | existing; epic gap | An offline BuildWorld pass over the 20-seed sweep; report the reach rate. Another session's 2026-10-19 town repairs (uncommitted) partly cover MMR-251 | MMR-251 |
| 40 | "(Age N)" in kingdom, town, castle, village, clan and hero names; regents named "Regent of …"; "<town> Fields 2" numerals; English-only load messages | existing; epic covers houses only | Strip tags from display names everywhere (`sourceName` keeps them); resident names for regents; FWGC++ naming for villages and castles without numerals; TextObject loader strings | MMR-286 |
| 41 | Every Bannerlord or War Sails update strands every generated campaign | existing | Bind to "a supported game build with a verified catalog", not the exact build string; document update behaviour | MMR-284 |
| 42 | Exact 3/2 cuts a town's trade area from vanilla's median 5 villages to 3 (few castles feed towns) | epic (owner rule) | Kept by owner decision; MMR-247 records the economy effect | MMR-247 |
| 43 | Port-gated issues (caravan ambush, escort, snare) are offered in fewer towns | existing world shape | Keep MMR-251's forced ports to the minimum | MMR-251 |

**Critic pass on the folded epic (2026-10-19, saved as revision 4).** One read-only critic checked every RT fix, success criterion and dependency edge. Its changes, all applied:
- **Game updates (RT-41).** The supported build range has a minimum and no maximum: a range with a fixed top would strand campaigns at the first Steam update. Catalog checks compare referenced vanilla objects by id and type, not by whole-content hash. The incident transpiler finds its targets by `ldstr` literal, not by the compiler-generated lambda name, and an unresolved patch only turns that incident off.
- **Village clearance.** The 4-unit village clearance allows recorded islet exceptions, because world A passes the quota only at the tightest tier. A forced-port town gets a synthesized harbour village.
- **Village types.** These are fixed where they are actually drawn: the quota works on production classes, and `VillagePool`/`FoodPass` in managed code enforce the coastal share and the per-culture floors.
- **Feeder roads** may cross water on existing decks (otherwise villages beyond a deck could never connect).
- **Names.** MMR-279 owns the shared name cleaner, and MMR-286 applies it everywhere else.
- **Wanderers.**
  - W is the smallest integer not below the float32 product 0.6f × towns (A 28, B 40).
  - Wanderer templates come only from MMR-287's backstory-safe pool, at new game and at top-up.
- **Kingdoms.** The kingdom maximum is the largest War Sails kingdom's fief count, not the average.
- **Mutation.** The mutation capital resolves through the merged kingdom's member realms.
- **Missing edges added:**
  - MMR-252 → MMR-278 → MMR-258 (terrain pads after final positions);
  - MMR-283 → MMR-254;
  - MMR-287 → MMR-280;
  - MMR-280 → MMR-261;
  - MMR-281/287 → MMR-284;
  - MMR-257/282/284 → MMR-286;
  - MMR-254/257 → MMR-285.
- **The acceptance node gained:**
  - the balance pass;
  - terrain-incident predicates;
  - village-look and floor checks;
  - the pole clamp;
  - a troop-bearing synthetic mod.
- **Policy keys.** MMR-248 lists the keys that later tickets consume. The re-pin is a script re-run whenever a pinned value is tuned.
