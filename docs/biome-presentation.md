# Shared biome table and map epic 2 policies (MMR-248, 2026-10-19)

One table decides, per FWGC++ biome, the navmesh face type, the terrain layers, the battle scene class, the colour grade and the snow treatment, so the four classifiers that disagreed at biome boundaries (findings section 12) agree. The same ticket froze every new policy key of map epic 2 with strict readers on both sides, the one format capability, and a scripted re-pin chain. Research: `docs/research/map-epic-2-findings.md` sections 3, 4, 5, 7, 12 and 17.

**Where the table lives.** `worldgen/policies/terrain-materials.json` version 2, `biomes[]`. Two other policies restate one column each, and checks keep them equal:

| Column | Restated in | Checked by |
|---|---|---|
| `terrainType` | `playability.json` `cells.landFaceGroups` (the travel compiler's input; MMR-254 types every land cell with it, `docs/travel-graph.md` "Land face types") | `travel::check_face_table` refuses the compile (`face_table_mismatch`); `map2_policy_tests`; `tests/World/Map2PolicyTests.cs`; `tools/summarize_land_faces.py` recounts the compiled faces from the table |
| `battleClass` | `mission-scenes.json` `biomeClass` | `Map2PolicyTests.cs`, `tests/ci/test_map2_policies.py` |
| `colourGrade` | `climate-flow.json` `colorGrade.biomes` | the same two |

**Clean break.** Version 1 of the material policy is refused (`unsupported_version`), and no reader keeps a v1 path (owner decision 1, `docs/save-compatibility.md` "Map epic 2: clean break").

## The table

Water biomes (ocean 0, lake 8) have no face (their cells take a naval group) and no grade (the temperature band of `climate-flow.json`). The never-generated tombstoned labels tundra (1) and snow (6) are listed so the taxonomy stays complete.

| Biome (id) | TerrainType | Layer mix | Battle class | Colour grade | Snow |
|---|---|---|---|---|---|
| desert (2) | Desert | sand 0.6, arid_soil 0.4 | desert | 20 desert | seasonal |
| grassland (3) | Plain | grass 0.7, steppe 0.3 (moisture split: wet grass, dry steppe) | open | 40 steppe; 60 centraleurope when wet | seasonal |
| forest (4) | Forest | forest_floor 0.8, grass 0.2 | woodland | 60 centraleurope | seasonal |
| exposed_rock (5) | Steppe | mountain_rock 0.6, rock 0.25, cold_steppe 0.15 | rock | 80 highlands | seasonal |
| rainforest (7) | Forest | rainforest_floor 0.8, mud 0.2 | woodland | 140 mediterranean | seasonal |
| marsh (13) | Swamp | mud 0.7, grass 0.3 | wetland | 100 normandy | seasonal |
| boreal_forest (15) | Forest | forest_floor 0.6, tundra_moss 0.4 | woodland | 120 north | seasonal |
| cold_tundra (16) | Steppe; Snow where permanent white | cold_steppe 0.5, tundra_moss 0.5 | cold | 120 north | seasonal; permanent where 12 snow months |
| land_ice (17) | Snow | permanent_snow 1.0 | cold | 120 north | permanent |
| tundra (1), snow (6) | Snow | as cold tundra / permanent_snow | cold | 120 | seasonal / permanent |
| ocean (0), lake (8) | none | riverbed and sand / riverbed and mud (beds) | open | temperature band | seasonal |

The face column is section 3's critic-corrected table. No biome takes Dune (it loses the Desert incidents and backdrops), Beach (no mechanics) or Fording (it blocks ships). Seasonal snow everywhere is the dynamic snow texture driven by the FWGC++ snow season (MMR-256), not a layer.

## The 15-slot layer set

`layerSet`, composited from the highest slot down; slot 0 is the base at full weight; slot 15 stays free. Every weighted layer carries no flora (findings section 4). MMR-255 set the scales and textures from MMR-249's answers: `scale` is the tile size in map units (scale 20 repeats every 20 units; 15-20 suits biome layers, 5 reads as noise), the pale `*_mainmap` textures are not used as plain layers (they are blue-white without the vista multiply), and no layer is tinted (`tint` mode 0; mode 1 adds the colour, mode 2 only darkens).

| Slot | Name | Diffuse / normal / specular / height | Scale | Use |
|---|---|---|---|---|
| 0 | grass | `ground_grass_b_d` / `_n` / `_s` / `_h` | 15 | base |
| 1 | steppe | `ground_drygrass_a_d` / `_n` / `_s` / `_h` | 15 | dry grassland |
| 2 | cold_steppe | `ground_drygrass_b_d` / `_n` / `_s` / `_h` | 15 | cold land, rock skirts |
| 3 | tundra_moss | `ground_nord_grass_a_d` / `_n` / `_s` / `_h` (NavalDLC) | 15 | boreal and tundra floor |
| 4 | forest_floor | `ground_forest_b_d` / `ground_forest_b_n` / `_s` / `_h` | 15 | forests (MMR-255: the full-colour set, not `_main_map`) |
| 5 | rainforest_floor (optional) | `forest_a_d` / `_n` / `_s` / `_h` | 15 | rainforest |
| 6 | sand | `desert_floor_a` / `desert_floor_a_n` / `_s` / `_h` | 15 | desert |
| 7 | arid_soil | `desert_floor_c_d` / `_n` / `_s` / `_h` | 15 | desert margins |
| 8 | mud | `ground_mud_b_d` / `_n` / `_s` / `_h` | 15 | marsh, lake beds |
| 9 | beach (optional) | `ground_beach_a_d` / `_n` / none / `_h` | 15 | non-rocky coast band |
| 10 | riverbed | `ground_pebbles_a_d` / `ground_pebbles_a_n` / `_s` / `_h` | 15 | river and lake edges (MMR-285's water-edge layer; MMR-255: not `_mainmap`) |
| 11 | rock | `rock_k_d` / `rock_k_n` / `rock_k_s` / none | 20 | slope rock |
| 12 | mountain_rock | `cliff_b_d` / `_n` / `_s` / `_h` | 20 | exposed rock, mountain landform (MMR-255: `main_map_mountains_d` is pale; default taken, `cliff_b`) |
| 13 | desert_rock (optional) | `stone_desert_a_d` / `_n` / `_s` / `_h` | 20 | slope rock in desert |
| 14 | permanent_snow | `snow_a_d` / `_n` / `_s` / `_h` | 15 | permanent white only |

**The template.** `tools/scenebin/make_surface_template.py` generates the template's material layers from this table (MMR-255): every season the same layer (seasonal variants and masks never switch at run time, MMR-249), `smooth_blend_amount` 1, every layer flag off (`use_transparency_of_ground_slope` would height-blend), no flora meshes, and `use_dynamic_weather_effects="true"` on the terrain. There is no road layer (roads are decals) and no school layer (MMR-249 chose delivery A: one decal overlay). **Snow (MMR-256):** the snow texture's name is per world, so it is not a template substitution: `RuntimeWorldBackend.Prepare` adds `dynamic_flowmap_texture_name` to the world's copy of the template before encoding, and the encoder keeps any terrain attribute it is given (it refuses only `use_dynamic_weather_effects` off and `terrain_render_version="1"`). See `docs/water-climate.md` "Terrain snow".

**Verified names.** `tools/check_layer_textures.py` scans the installed packs (`Modules/*/AssetPackages` and `EmAssetPackages` `.tpac` headers) for every name and its `for_terrain` flag; all 58 texture references above are installed and flagged (re-checked after MMR-255 replaced the forest, riverbed and mountain sets; `evidence/map-epic-2/layer-textures.json`). Corrections to section 4's list found by the scan:
- the sand diffuse is `desert_floor_a`, with no `_d` (its maps are `desert_floor_a_n/_s/_h`);
- the forest floor is `ground_forest_b_d_main_map` (`_main_map`, not `_mainmap`), with normal `ground_forest_b_n_main_map`;
- `snow_b` has no specular map (`snow_b_s` is not installed; the template's winter layer borrows `snow_a_s`), so the snow layer uses the `snow_a` set;
- `ground_beach_a_s` exists but is not flagged `for_terrain`, so the beach layer has no specular map;
- `ice_a_d/_n/_s` exist (no height map); the table uses `snow_a` for land ice so one layer serves the whole permanent-white mask;
- the cracked-mud decal `mainmap_cracked_mud_a` is not a texture name in any pack (section 4 named it as a decal; it is out of the layer set).

**Rules** (`rules`), compiled by the layer compile (`src/WorldBridge/src/terrain_layers.cpp`, `docs/terrain-compiler.md` "Layers"): per sample, the biome row, its wet/dry pair split by moisture (the row's wet share at `split`, all wet at `split + band`, all dry at `split - band`); tent-kernel smoothing over `kernelSamples` within land or within water; then on land, in order: below `belowC` the wet and dry layers move into `cold_steppe`; the `Mountain / ridge` landform mixes in `mountainShare` of `mountain_rock`; the `slopeRock` share of `rock` (`desert_rock` on a Desert face); a coast band of `samples` is `beach`, except a share max(`rocky_coast`, slope share) of `rock`; river samples and samples within `waterEdge.samples` of a lake or river are `riverbed`; `ice_cap` at or above `threshold` is `permanent_snow`; permanent white is `permanent_snow`.

**Alphas** (`alpha`): the over-compositing inverse a_k = f_k / (f_0 + ... + f_k), stored as round-half-even bytes, bytes below `minByte` 3 dropped (the shader skips weights of 2/255 or less), slot 0 at 255. MMR-249 measured alpha-over compositing from slot 15 down, weight linear in the byte divided by `smooth_blend_amount` (1), so the composited coverage of each layer is its target fraction up to byte rounding.

**Map trees** (`flora`, MMR-249's go): forest, boreal forest and rainforest samples plant trees at `density` per square unit times the target fraction of their flora layer (`forest_floor`, or `rainforest_floor`), with the probe's kinds (`map_spruce_mix` variations 0-2, `worldmap_tree_beech_01`, `worldmap_tree_acacia_01`) and scale 0.18-0.32, clear of the sea and lakes (`coastSamples`), slopes past the ramp start, settlement pads (radius plus `padMargin`) and road cells; `maxTrees` thins the density rather than refuse a world. Default taken: rainforest uses beech and acacia (the probe verified only these three kinds; the SandBox map's palms are untested).

**Tree density (MMR-247, acceptance run acc4 V4).** At MMR-255's density 0.45 the forest, boreal forest and rainforest shots looked nearly bare: A' planted 588 canonical trees (1,176 with the strip images). The 0.45 was the probe's uniform dense node, but here it is multiplied by the flora layer's target fraction (0.40 on average over A's forest candidates, the coast, bank and mountain rules taking the rest), and the pad and road clearances then remove half the draws (298 pads and 326 road cells of 1,212 on A'). Counted per occupied 10-unit cell, A' had a median 0.02 trees per square unit (p90 0.09, densest 0.27) against the installed Main_map's 0.20 (p90 0.79, densest 3.02; 69,500 trees). `density` is now 3.6 and `maxTrees` 88,000, the count MMR-249 measured in game (+0.24 ms of main thread); it was 200,000. Measured on the regenerated worlds (`flora.bin`, canonical columns for the cell quantiles, every image for the windows):

| | A' (seed 42) | B' (seed 73) | installed Main_map |
|---|---|---|---|
| trees written (canonical) | 9,144 (4,572) | 15,644 (7,822) | 69,500 |
| canonical by biome: forest / boreal / rainforest | 3,375 / 693 / 504 | 4,312 / 2,736 / 774 | |
| cleared by pads / road cells | 2,401 / 2,730 | 3,633 / 3,982 | |
| per occupied 10-unit cell: median / p75 / p90 / densest | 0.11 / 0.24 / 0.62 / 2.16 | 0.12 / 0.33 / 0.79 / 2.24 | 0.20 / 0.50 / 0.79 / 3.02 |
| most trees in a closest-camera 58 x 37 window / a 116 x 74 window | 1,029 / 1,318 | 1,010 / 2,016 | 2,468 / 5,705 |

Every world total stays under MMR-249's measured 88,000 and the vanilla map's 69,500, and the most in view stays under the vanilla map's. The densest tenth of B's forest cells matches the vanilla map; A's stays under it, and both worlds' medians stay under. The look in game is acc5's.

**MMR-255.** `channels` and each biome's `rock` are gone: the compiler writes `MNBLWT02` (one alpha plane per layer) from the rules above, and both readers refuse the two keys.

## Permanent white

`permanentWhite`: `land_ice` (and the tombstoned `snow`) plus every land sample with 12 months of snow at or above 0.5. The same mask gives the `permanent_snow` layer and the Snow face (`playability.json cells.landFaceGroups.permanentWhite`), so face and look agree. Every other cold biome is white only in its FWGC++ snow season. The terrain compiler computes the mask per terrain sample (MMR-254: `terrain::Compiled::white`, from the nearest source sample's biome and its `seasonal_environment.months[*].snow`); the travel compiler's Snow face reads it, and MMR-255's layer reads the same mask.

## Mutation remap (RT-20, RT-26)

`mutation`. Generated magic variants are **visual only**; only live mutations (MMR-261) retype faces. A school retypes a mutated land face within `landValid` {Plain, Desert, Snow, Forest, Steppe, Swamp}, never Dune, Bridge, Fording, Beach, water or Mountain, and never changes passability.

| School | TerrainType | | Hidden school | Look and type of |
|---|---|---|---|---|
| fire | Desert | | blood | infernal |
| water | Swamp (Snow on cold_tundra, land_ice, boreal_forest) | | void | umbral |
| earth | Steppe | | rot | water |
| air | Plain | | eldritch | weave |
| weave | Forest | | | |
| radiant | Plain | | | |
| umbral | Forest | | | |
| infernal | Desert | | | |

A mutated cell's battle class is `mission-scenes.json faceClass[remapped type]` (Plain open, Forest woodland, Steppe steppe, Snow cold, Desert desert, Swamp wetland). The thresholds are read per world from `world.json` `magic.mutation_threshold` (0.35 on world A) and `magic.dominance_margin` (0.08).

## Home terrain review (RT-27)

The face type around each presentation culture's towns (a 5 × 5 source-cell window at every FWGC++ site; worlds A and B):

| Civilization → culture | World A | World B |
|---|---|---|
| human_desert → aserai | Desert 76%, Forest 18% | Desert 89% |
| human_rainforest → battania | Forest 55%, Desert 38% | Forest 43%, Desert 34%, Plain 23% |
| elf → battania | Forest 73%, Steppe 27% | Forest 66%, Desert 22% |
| gnome → empire | Desert 44%, Forest 40%, Plain 13% | Desert 41%, Forest 37%, Plain 21% |
| human_large_island → khuzait | **Forest 81%**, Plain 12% | (absent) |
| human_cold → nord | Steppe 86%, Forest 9% | Steppe 72%, Forest 22%, Snow 6% |
| dwarf → sturgia | Forest 93% (1 town) | Steppe 40%, Desert 33%, Forest 20% |
| frosthold_dwarf → sturgia | Steppe 100% (1 town) | Steppe 51%, Snow 49% |
| human_heartland → vlandia | Forest 53%, Desert 31% | Desert 43%, Forest 32% |
| hill_dwarf → vlandia | Desert 57%, Forest 20% | Desert 45%, Forest 30% |

Findings:
- **Khuzait on world A lands on 81% forest**, where auto-resolve cuts cavalry by 0.2/0.15 and horse archers by 0.3/0.25 (section 2). This is RT-27's case.
- Empire, vlandia and the hill dwarves sit mostly on Desert (−0.1 speed, no Aserai exemption).
- Aserai, battania, elves, nord and the frost dwarves land on terrain that suits them.

**Default taken: the culture mapping is unchanged here.** Changing `culture-presentations.json` changes every world's factions and the `factions.json` pin. The recommended change, if the balance pass confirms the problem, is `human_large_island → battania`, a forest presentation, or a vlandia/empire swap. MMR-247's balance pass makes that call with soak data, and re-runs the re-pin chain, which covers `factions.json`'s culture-presentations pin.

**MMR-247's balance pass (acc6): no change** (`evidence/map-epic-2/balance-pass.json`). In game, khuzait's home samples on world 1 are Forest 88%, Plain 12%; but world 1 has no khuzait kingdom (its five are vlandia twice, sturgia, aserai, empire), and the four khuzait houses kept every fief and member through the 30-day soak (one gained a fief). The soak shows no cost of the forest home, and the remedy changes every world's factions: left to the owner. The Snow (−0.15) and road (+0.2) terms and the other cultures' homes are unchanged too; Swamp (−0.2) has no face on either world to measure.

## Vertical scale

`projection.json verticalScale` is lowered from 0.01 to the starting value **0.005** (findings section 15: true proportions). `playability.json cells.mountainSlope` (1.0 → 0.5) and `terrain-materials.json slopeRock` (0.6/1.2 → 0.3/0.6) are scaled by the same factor. On seed 42 at size 129, the travel cells (`cells.bin`) are byte-identical to world A's. MMR-278 sets the final value by its gates, updates the absolute-height consumers (`PartyGroundQueries.cs:60` still asserts 0.01; camera, entity, party and water z offsets) and re-runs the re-pin chain.

## The format capability

**`R11`**, "map epic 2 formats: cells.bin v2 (MNBLTC02), weights.bin v2 (MNBLWT02), campaign.json v2". It is one constant on each side:
- `worldbridge::capabilities::kFormatsV2` (`src/WorldBridge/include/worldbridge/capabilities.hpp`);
- `WorldDataBundle.FormatsV2Capability`, which every host profile includes through `WorldDataBundle.HostCapabilities` (the setup screen, the probes, the harnesses).

Every bundle written from this ticket on requires R11. A host without it refuses the bundle at `WorldDataBundle.Open`. The formats themselves arrive with MMR-252/253/254/255/279.

## Policy keys

Every key the epic adds, with its consumer. "Starting" means a later ticket measures or tunes the value.

| File | Key | Value | Consumer |
|---|---|---|---|
| terrain-materials.json v2 | `biomes[].water, terrainType, layers, battleClass, colourGrade, colourGradeWet, snow` | the table above | MMR-254, 255, 256, 260 |
| | `layerSet[]` | 15 slots above (scales starting) | MMR-255, 249 |
| | `rules` | as above (starting) | MMR-255 |
| | `permanentWhite` | land_ice + snow, 12 months ≥ 0.5, permanent_snow, Snow | MMR-254, 255, 256 |
| | `mutation` | remap above; `generatedVariants` visual; `battleClass` faceClass | MMR-260, 261 |
| | `slopeRock` | 0.3 / 0.6 (scaled) | MMR-278 |
| playability.json | `cells.mountainSlope` | 0.5 (scaled) | MMR-278 |
| | `cells.landFaceGroups` | default Plain; biome → face; permanentWhite 12 / 0.5 / Snow | MMR-254 |
| | `repairs.maxTownSnapCells, townCarve, forcedPorts` | 6 (starting), true, true | MMR-251 |
| | `villages` | 3 / 2; clearance 4; minSeparation 1; tiers preferred 0.12–0.35 S, wide 0.08–0.5 S, tight 0–0.65 S (starting); isletExceptions; preference pinned, harbour, diverse, nearest; maxExtraHarbours 1; ownKingdomFirst; protected farming; largestDeficit; `mnbl-quota-village:` | MMR-252 |
| | `roads` | splitAtPoles, skipSeaLegs, passesAreRoad, endAtUndeckedBank, cellFlag; feeders from castle, village, roadlessTown, maxCells 48 (starting), passes, decksOnly | MMR-253 |
| roads.json (new) | `decals` | families empire/khuzait/battania path a/b; culture → family; spacing 1.4, size 0.4, 2 smoothing passes, gateClip 1.0, maxDecals 3000 (starting); seam nearestImage | MMR-257 |
| | `bridges` | `map_bridge_b/c/d/e`, from navigation.json build.bridges, bank ground, L-shaped rotate, scale 1.0 (starting) | MMR-257 |
| | `speed` | chain Road → Naval → Default; road +0.2 off sea; cancel ford on decks; Snow −0.15, Swamp −0.2 (balance pass) | MMR-254, 247 |
| terrain-shaping.json (new) | `stage, despike, coast, pads, lakes, coherence, lowPass, normals, resolution, gates` | after placement; section 15 gates 45/60/50, spikes 0, pads over 25° ≤ 10; travel side 257, `fine` true since MMR-278 (513, MMR-249's go); the step values passed the gates as MMR-248 set them | MMR-278 |
| projection.json | `verticalScale` | 0.005 (starting) | MMR-278 |
| economy.json | `binding.townVillages` | 3 (MMR-252 removed `townMinimum`/`townMaximum`: the quota binds exactly 3/2) | MMR-252 |
| | `villageMix` | coastalFishingShare 0.5; coastal shares and horse-ranch/wheat floors per culture (installed) | MMR-252 |
| | `walls` | installed fortification levels per prosperity tercile | MMR-258 |
| startup.json | `hideouts.biome` | share 0.6; biome → bandit weights; coast 1 cell → sea_raiders weight 1.0 (starting) | MMR-287 |
| climate-flow.json | `wind.bands` | 3 latitude bands, blend 6°, perturbation 0.35 / 40° / 2 octaves (starting) | MMR-256 |
| | `precipitation.snowSeason` | months ≥ 0.5; bytes 12:196, 9:155, 6:113, 3:72, peak 30; ice cap 255 | MMR-256 |
| | `colorGrade.grassland, runtime` | moisture split 0.5, dry 40, wet 60, mode filter 3; Lerp(0.55, −0.1), 0.65 | MMR-256 |
| settlement-looks.json (new) | `catalog, normalize, fit, selector, variants, contract, phase0` | Main_map catalog frozen; scale 0.5 (min 0.35); 3 neighbours, repeat penalty 1.0, usage balance 0.5; 2 walls per level; culture pieces (verified mesh names) | MMR-258 |
| | `ruins` | minGap 5.0, snapCells 1, installed ruin prefabs, scale 0.05 (starting), encyclopedia page | MMR-257 |
| population.json (new) | `base` | 133 fiefs, 57 towns, 76 castles, 9 kingdoms, 82 noble clans, 443 alive lords (405 adults by XML age) | MMR-279 |
| | `targets` | per 133: kingdoms 9 (min 1, 3 houses each, max 17 fiefs = Aserai), houses 82 (max 4 fiefs), adults 4.94 per house, minors ≤ 8.6%, companies 12 (6–10), outlaw clans 10 (4–6), wanderers 0.6f × towns | MMR-279, 281 |
| | `kingdomMerge, wars, houses, companies, outlaws, spill, census` | rules of findings section 16/17; starting wars 1 per kingdom, max 2; outlaw war guard 2 kingdoms; outlaw tier 4 (installed 3 or 4); `census.notableCaps` holds HeroFactory's per-settlement caps (copied from rosters.json by the generator; MMR-279 made it an object) | MMR-279, 280 |
| | `troopTemplates` | per culture, a company line and outlaw lines (below) | MMR-279, 280 |
| | `lordTraits` | 127 installed personality vectors with counts; same-culture shared share 0.5124 | MMR-280 |
| | `wanderers` | sources; rotation 10% daily, unmet unhired only; top-up; epithet; `reserve` 24 runtime names per town culture (campaign.json `wandererReserve`, MMR-281); 78 installed templates with their epithets; `backstoryUnsafe` 38 templates (MMR-287 census, `backstoryCensus`); `fallback` cross-culture order per culture | MMR-280, 281, 287 |
| campaign-content.json | `heroRoles[].bucket, fallbackBucket, seatBuckets` | house/company/wanderer/notable/none (council magistrate and commander → house) | MMR-279 |
| | `names.cleaner` | stripAgeTag, lexiconSuffix dedupe, no numerals, keep sourceName | MMR-279, 286 |
| naval-transitions.json | `faceGroups` + Desert 2, Snow 3, Forest 4, Steppe 5, Swamp 15 | the land face ids | MMR-254 |

**Troop template table** (`population.json troopTemplates`, RT-03, from the installed minor clans):

| Culture | Company line (party template, leaders) | Outlaw lines |
|---|---|---|
| aserai | mercenary_aserai, ghilman | beni_zilal (mafia), jawwal (nomad) |
| battania | outlaw_battania, wolfskins | wolfskins (mafia) |
| empire | mercenary_empire, legion_of_the_betrayed | hidden_hand (mafia), embers_of_flame (sect), eleftheroi (nomad) |
| khuzait | karakhuzaits, karakhuzaits | karakhuzaits (nomad) |
| nord | mercenary_sturgia, skolderbrotva | lakepike (mafia; no nord outlaw clan is installed) |
| sturgia | mercenary_sturgia, lakepike leaders (no sturgian mercenary clan is installed) | lakepike (mafia), forest_people (nomad) |
| vlandia | mercenary_vlandia, company_of_the_boar | brotherhood_of_woods (mafia) |

`tools/make_population_policy.py` regenerates the file from the merged campaign catalog and checks every template is installed.

**Schemas.** `worldgen/contracts/campaign.schema.json` is campaign.json v2: houses replace v1's ruling clans, plus kingdoms' `memberRealms`/`mergeSteps`, `wars`, `mercenaryCompanies`, `outlawClans`, `wanderers`, per-hero `bucket`/sex/age/kin, v2 village fields (`boundKind`, `sourceTown`, `origin`, `tier`), `villageQuota` and `census`. `travel-graph.schema.json` accepts road `kind`/`path`/`decks`/`settlement` (feeders) and grid `landTypes`/`roadCells`. `terrain-metadata.schema.json` accepts `layers`, `permanentWhite`, `shaping`, side 513 and `flora.bin`.

**Readers.** WorldBridge: `material_compiler.cpp` (v2), `travel_graph.cpp` (`load_policy`, `check_face_table`), `campaign_compiler.cpp` (buckets, cleaner) and `policy_map2.cpp` (`load_shaping`, `load_population`). Managed: `World/Map2Policies.cs` (`BiomeTable`, `RoadsPolicy`, `PopulationPolicy`, `SettlementLooksPolicy`, `Map2PolicyBlocks` for the new blocks of playability, economy and startup). Both are strict: unknown members and out-of-rule values are refused. Tests: `map2_policy_tests` (CTest), `tools/Test-Map2Policies.ps1`, `tests/ci/test_map2_policies.py`.

## Re-pin chain

```
python tools/repin_map2.py            # rewrite every pin that differs, print each change
python tools/repin_map2.py --check    # change nothing; exit 1 if a pin differs
```

In order:
1. `docs/formats/navmesh-bin.md` → `naval-transitions.json source.faceGroupTableSha256`;
2. `naval-transitions.json` → `playability.json calibration.sha256` (line 6);
3. `playability.json` → `naval-content.json water.playabilitySha256`; `economy.json` → `startup.json provenance.economySha256`; `culture-presentations.json` → `factions.json provenance.culturePresentationsSha256`; `prototypes.json` → `mission-scenes.json sources[]`;
4. the test literals that restate them: `travel_graph_tests.cpp` (calibration hash, `mountainSlope`), `projection_tests.cpp` (`verticalScale`), `terrain_resampling_tests.cpp` (`slopeRock`).

It is idempotent. Run it after every merge and after tuning any pinned value. `--check` compares against the files as they are, so a chain of stale pins shows its first link; run without `--check` to settle all of them.

The playability, terrain-materials and projection policies are generation-bound world inputs, so a request made before a change is refused (`policy_changed`). That is the clean break working, not a fault.

## Defaults taken

- Feeder-road generation rules live in `playability.json roads.feeders`, not `roads.json`: the travel compiler that builds feeders reads only its playability policy. `roads.json` holds the runtime decals, bridges and speed terms.
- The ruin-marker gap is in `settlement-looks.json ruins`.
- Sturgia companies use lakepike's leader templates (culture sturgia) with the mercenary_sturgia party template. Nord outlaw clans use lakepike's line.
- Decal family per culture: empire and vlandia → empire; aserai and khuzait → khuzait; battania, sturgia and nord → battania.
- The water school freezes (Snow) on cold tundra, land ice and boreal forest.
- The lord-trait draw uses the installed joint distribution, so the shared-personality rate tracks vanilla's (0.5124).
- The culture mapping is kept (see the home terrain review).
