# Settlement looks from scaled vanilla layouts (MMR-258)

Every town, castle and village of a generated world takes its map look from a vanilla War Sails settlement of its culture: the settlement's whole hierarchy on the installed campaign map, scaled down, fitted to the clearance around it and baked into the world's scene. Where no look fits, the settlement gets its culture's pieces (phase 0). The record takes the look's settlement as its prototype, so the map look matches the scene the player enters (RT-14). Findings section 10, owner decision 5; the keys are in `worldgen/policies/settlement-looks.json` (MMR-248).

| Piece | File |
|---|---|
| Catalog, normalizer, look extraction, selector, frozen looks, look builder, the plan | `World/SettlementLooks.cs` |
| Markers at a look's offsets (`SettlementFrame`, `SettlementSite.Look`) | `World/SettlementPositionResolver.cs` |
| The hierarchy from a look, phase 0, the relaxed contract (`Version` `mmr258/1`) | `World/SettlementEntityFactory.cs` |
| Production use | `World/RuntimeWorldBackend.cs` (`Prepare`), `Generation/ActivationCommitStage.cs` (package), `Persistence/GeneratedWorldLoader.cs` (re-encode) |
| Records: the look's prototype, wall levels, the village type plan | `Campaign/SettlementFactory.cs` (`LookPrototypes`, `WallLevel`, `PlanVillageTypes`), `Campaign/CompiledWorldDataset.cs` (`Looks`) |
| Policy reader | `World/Map2Policies.cs` (`SettlementLooksPolicy`) |
| Offline tests | `tests/World/SettlementPlacementTests.cs` (`tools/Test-SettlementPlacement.ps1 -Looks`), `tests/World/SettlementEntityRulesTests.cs` (`tools/Test-SettlementEntityRules.ps1`, MMR-247), `tests/Campaign/SettlementTests.cs`, `tests/Campaign/CompiledWorldTests.cs` |
| Evidence | `evidence/world/settlement-looks.json` (catalog, per world the look usage and reuse report, entity counts and scene sizes) |

## The catalog

`SettlementLookCatalog.FromInstalled` reads the player's installed `Modules/NavalDLC/SceneObj/Main_map/scene.xscene` at generation. Every root named by a settlement of the prototype pools (`worldgen/templates/settlements/prototypes.json`: town_port, town_inland, castle, village_coastal, village_inland of the seven presentation cultures) becomes a look: 57 towns, 76 castles, 307 villages, none missing.

**Normalization** (`normalize`, counted per catalog in the evidence):
- **Nested prefabs** (190): an instance (`prefab=`, `_index_`) becomes an entity holding the installed prefab (`Modules/{NavalDLC,SandBox,Native}/Prefabs`, the first definition wins) under its own transform, levels and tags. The nesting is what broke Player Settlement's templates.
  - **Component overrides (MMR-247, acc4 V2).** An instance's `<components>` edit its prefab's: the n-th `meta_mesh_component` (or `decal_component`) of the instance is the n-th of the prefab's root, and every installed override is nameless (it lists the prefab's meshes with new arguments, factors or materials). Left on the holder after inlining, a nameless meta mesh names no mesh, and the engine drew it as its magenta "REPLACE ME" box: in acc4, beside Hakndum, on the iron mine village Sulkkas (`castle_village_ES5_2`'s `map_icons_productions_saltmine_c`; A' had 10 such pieces in 10 villages, salt mines and piers). `MergeOverrides` now moves each override onto the inlined prefab's component: its attributes, and each mesh entry onto the prefab's mesh of that name (appended when the prefab lists none); one with no component to edit goes (`prefabOverridesMerged`, `prefabOverridesDropped`).
- **Editor data:** `old_prefab_name`, `_index_` and `edit_mode_data` (1,178) go, and every untagged editor-only entity (511) except a banner placeholder's volume, which `SettlementVisual` reads as child 0. The tagged editor-only entities are the siege markers; the scene rebuilds those.
- **SceneLeveler** scripts (13) go, and so do the 74 Town Entity Manager scripts below a root: the scene's root carries its own.
- **Colliders:** every physics shape becomes raycast-only (957 bodies).
- **Source ports, arcs and drop-offs** (29, 58, 144) go. Their directions from the centre are kept (`portAngle`, `dropoffAngle`) to turn the look.

A look is kept as XML (`<look id kind culture pool port portAngle dropoffAngle>` with the root's normalized children), and everything else is extracted from that XML:
- **Towns and castles:** the city gate (its direction is the look's gate frame), the gate-frame markers (outer gate, camps, siege engines, ram, towers), two wall sections per level with their solid and broken pieces, four wall engines per level, the circle's scale, and whether its banner placeholders conform.
- **Villages:** the icons per hearth level and the looted icon, and the production pieces.
- **All kinds:** the footprint radius (every visible piece) and the core radius. The core is what culling never removes: level-bound pieces and markers, or a village's icons.

**The contract per look.** A look needs exactly 2 wall sections and 4 wall engines per level, at most one solid and one broken piece per wall, one gate, camps, a ram and towers, and for a village one icon per hearth level and a looted one. Every installed town, castle and village meets it:
- 8 Battanian castles have two `map_defensive_engine_3` and no `_0` at level 3; four engines per level is the rule.
- 78 castle wall sections have no solid piece (7 castles have neither piece). Such a wall is built as installed.
- Castle villages' icons carry `siege` too.

A look that breaks the contract is never selected.

## The selector

`SettlementLookSelector.Select`, deterministic per world. It takes the towns first, then castles, then villages, each by id.

**Pool.** The culture's looks of the settlement's prototype pool, conforming to the contract and, through `MissionScenes.PrototypeProblems`, to the mission scene contracts: the look's id becomes the record's prototype. A village falls back to the other pool, as `SettlementFactory` does.

**Variants:**
- **Port-stripped.** An inland town of a culture in `portStripped` (Nord) with no inland look takes a port look. Its pier and dock pieces go; the record's prototype is that look, as the factory would adapt it.
- **Port-grafted.** A port town of a culture in `portGrafted` (Battania, Khuzait) may take an inland look, grafted with a pier at its port.
- **Castle as small town.** A small town may take a castle look of its culture.

A grafted or castle look leaves the record's prototype to the factory's pick, so there the entered scene differs from the map look. The report counts these.

**Order.** The first look that fits wins, in this order:
1. fewest neighbourhood repeats. The neighbourhood is the settlement's `neighbours` (3) nearest of its kind and those whose nearest it is. While `repeatPenalty` > 0 this is strict: neighbours differ wherever a fitting look is free.
2. a look whose prototype the record takes, before a grafted or castle variant;
3. the score: `repeatPenalty` × repeats, + `usageBalance` × the look's uses so far, + 0.25 for a variant, + 0.25 for a town look off its class's size (small above the culture's median core radius, capital below), + a tie-break from the world's stream (`mnbl.settlement-looks/1`).

**Fit.**
- **Clearance.** From each other settlement within 60 units: its claimed radius when it is already chosen, else a share of the distance by kind weight (town 3, castle 2, village 1). Then less `clearanceMargin`.
- **Scale.** Towns and castles take `fit.scale` (0.5); villages take 1. A look's core radius at that scale must fit; else the look shrinks, down to `minScale/scale` of the kind's scale (0.35 for towns and castles). A look that still does not fit is skipped.
- **Culling.** Towns' and castles' visible pieces that no wall level holds are culled beyond the clearance. A village's production pieces are drawn in to it.

**Phase 0** happens when no look fits or the pool is empty:
- a town or castle gets its culture's keep per wall level (`mi_<code>_keep_N`) and wall segments (`mi_<code>_wall_Ns`, collapsed `..._collapsed`, or the empire's where the culture has none);
- a village gets its culture's icons with three of its type's production pieces.

Its markers stand at the installed medians (`settlement-placement.json`).

## Building a look

**Placement.** `SettlementLookPlan.Resolve` sets each fortification's frame (`SettlementFrame.FromLook`): the look's gate distance and every marker, wall section and wall engine at the look's own offsets in its gate frame, times the choice's scale. A port town whose own look has a harbour also gets a first gate direction that turns the look's harbour toward the world's port. Then `SettlementPositionResolver` places everything with its usual rules: land, slope and clearance, each marker turned or shrunk only when needed. The layout hash includes each look, and the layout report's rows carry it (`look`), with the reuse report in `looks`.

**Hierarchy** (`SettlementEntityFactory.Settlement`):
- **Town or castle.**
  - The look's tree goes in `<id>_look`, turned so the look's gate faces the resolved gate, at the choice's scale.
  - **Cleaning.** Every functional entity goes with its subtree. One that also holds plain visible pieces stays as an untagged holder of them. The functional tags are those `SettlementVisual` or War Sails read anywhere in the hierarchy: gate, port, circle, selection, camps, engines, ram, towers, walls, wall engines, drop-offs and arcs. Banner placeholders go too, unless they conform.
  - The functional entities are rebuilt as direct children at the layout's positions: the gate, every marker, two wall sections per level carrying the look's solid and broken pieces (a raycast collider beside them), four wall engines per level, and the circle at the look's scale. Each marker is turned as its look entity was once the look is turned onto the gate.
  - **Banners.** The look's own placeholders stay when each wall level has one under a parent with that level's bit, each with its one child (133 installed looks). Otherwise the policy's placeholders are used.
- **Village.** The look's icons become `<id>_icon`, `_icon_2`, `_icon_3` and `_icon_looted` with the contract's levels. Its production pieces take the village type's meshes in turn (`phase0.production`); a look without any gets three. The rest of the look goes in `<id>_look`. A harbour village's look is turned so its source drop-off direction faces the world's drop-off, and it gets a pier at the drop-off when the look has none.
  - **Icon twins (MMR-247).** A look piece whose level set is an icon's (most looks' level-3 production piece, some level-2 or looted pieces: 306 on A', 410 on B') moves under that icon, without its own levels, at the same global frame. The icon's levels then gate it as its own did. Phase 0's third production piece is the level-3 icon's child the same way. So no entity of a village but its icons has an icon's level set (`Validate`). Acceptance run acc1's engine read lost every icon that shared a level set with a look piece of its village; see "Engine read model" below.
- **Re-snap** (`resnapChildren`). Every topmost visible piece moves straight up or down onto the terrain under it: the parent's axes are solved by Cramer's rule, so it never moves sideways.
- **Names.** Every look entity is renamed `<id>_l<n>`, so names are unique scene-wide.
- **Ports, arcs and drop-offs** are the scene's own. Drop-offs are direct children named under the village id, because War Sails matches `Parent.Name` to the village with no null check.

**The relaxed contract** (`Validate`, and `SettlementEntityChecks` in the engine):
- at most one solid and one broken piece per wall section (one of each in phase 0);
- exactly 2 wall sections and 4 wall engines per level;
- a look's banner placeholders under parents with wall level bits, one at each level;
- no nested prefab instance;
- no solid collider under a town, castle or village;
- no functional tag below the direct children;
- (MMR-247) no entity of a village but its icons with an icon's level set;
- (MMR-247, acc4 V2) every visual piece resolves (`SettlementEntityRules.UnresolvedVisual`): a meta mesh component names a mesh, and no editor helper mesh (volume boxes, editor cubes and planes, barriers, occluders, spawn, camera and animation-stand icons, debug meshes: `EditorHelperMeshes`) draws with its editor material outside an editor-only entity (a primitive given a material of its own is deliberate geometry: installed `town_EN1`'s `editor_cube` in `desert_border_mat_c`, `town_S3`'s editor planes and cylinder in `ground_soil_b`). `LookBuilder` filters such components out of every look tree (`FilterUnresolvedVisuals`), so the rule holds for any installed look, and `Validate` refuses a scene that still has one.

**Engine read model (MMR-247).** Acceptance run acc1 refused both worlds at `runtime.settlement_entities`: "201 settlement entity failures, first: `<village>`: no icon `<village>_icon_3`." The baked XML was right: every icon was a direct child with its levels. The engine's inspection read (`Scene.Read`, then `OptimizeScene(true, true)`) did not keep them. The native code was not inspected (no decompiler is configured here), so the rule is inferred from the run. In each settlement's hierarchy, in document order, an entity with a visual component (meta mesh or decal) and an explicit level set that an earlier such entity of the same settlement already has is gone. `XmlEntityScene`'s `same-level-visuals` read applies that rule. On the in-game A' scene it gives the in-game detail exactly, and on the B' bake it gives B's: 259 and 345 failures, every one a village icon. Fortifications, whose checked children have no visual component, pass. The pre-MMR-258 scene that passed in game has no such pair. Default taken: the factory avoids the pairs, and the inspection read is unchanged. The campaign map's read (`OptimizeScene(true, false)`, as SandBox `MapScene.Load` does it) differs, so whether the campaign itself loses these entities is not known.

`SettlementEntityChecks` now runs in steps, one settlement each. The runtime stage gives the engine thread back between slices of about 25 ms, as it does for the navigation routes (`settlementEntityItems`).

## Frozen into the world package

`PreparedSurface.Looks` keeps the looks a scene was built from. PromotePackage writes them into the world package as two parts:
- `looks/selection.json` (`mnbl.settlement-looks-selection` v1): every settlement's choice, and each used look's hash;
- `looks/catalog/<look id>.xml`: the normalized look.

A load that re-encodes a swept scene reads these (`FrozenLooks.Read`), not the installed campaign map, so a game patch since generation leaves the scene as it was built. A frozen look whose hash differs refuses the load as `damaged-world`; a selection that is not the world's fails the re-encode (`not-rebuildable`). No TaleWorlds XML ships with the module: a package holds only the player's own installed looks. Per world this is 6.6 MB (A, 217 files) and 8.2 MB (B, 290 files).

## Records

- **Prototype (RT-14).** `CompiledWorldDataset` reads the layout's looks, and `SettlementFactory.LookPrototypes` gives each record its look's prototype (mission-scene contracts checked; outside its own pool it is an adapted prototype). Phase 0 and variant settlements pick from their pool as before.
- **Village type.** The production pieces show the village's type before the campaign exists. `SettlementFactory.PlanVillageTypes` runs the same village pass the build runs (binding, draws, culture floors and the food pass) from the campaign records, travel graph and ports alone: the settlement cultures come from `HeroFactory.SettlementCulturesOf`, the world prefix from `FactionFactory.WorldPrefixOf`, and the defaults from the catalog pin. `CompiledWorldDataset` refuses a world whose records' village types differ from the looks'.
- **Initial wall levels (RT-14).** The fortifications building is drawn from `economy.json walls`: the installed level counts of the settlement's prosperity tercile. Towns go by class; castles by prosperity rank among the world's castles, as `tools/make_economy_policy.py` counted the installed ones. The draw uses the prototypes stream, entity `<id>|walls`.
  - A': 45 at level 1, 17 at 2, 9 at 3 of 71.
  - B': 71, 23 and 11 of 105.

## Offline results

From `tools/Test-SettlementPlacement.ps1` (74 checks pass) on A' (seed 42) and B' (seed 73).

| | A' | B' |
|---|---|---|
| Towns: looks, phase 0 (no look fits) | 41, 4 | 58, 8 |
| Towns: distinct looks, most uses | 35, 4 | 36, 6 |
| Town variants: castle, port-stripped, grafted | 8, 3, 1 | 2, 9, 3 |
| Town scale: median, shrunk below 0.5 | 0.40, 30 | 0.46, 37 |
| Castles: looks, phase 0, distinct | 26, 0, 24 | 38, 1, 36 |
| Villages: looks, distinct, most uses | 187, 161, 6 | 276, 217, 12 |
| Records whose prototype is their look (towns) | 32 of 41 | 53 of 58 |
| Nearest-neighbour pairs sharing a look (avoidable) | 0 of 774 (0) | 1 of 1,143 (0) |
| Scene entities, roots | 21,242, 261 | 31,953, 384 |
| Settlement entity XML | 9.4 MB | 14.0 MB |
| Mean entities per town, castle, village | 278, 186, 21 | 295, 177, 20 |
| Re-snapped pieces | 13,091 | 20,187 |

On world B, the one repeated pair is a village whose pool had no fitting look free of its neighbours.

The checks cover:
- **Catalog.** Every look is normalized (no nested prefab, no solid collider, no SceneLeveler, edit data or untagged editor entity, no source port, arc or drop-off), stable through its XML, and contract-conforming. A look with three wall sections or without a gate is refused.
- **Per world.** Determinism of the plan and the XML. The relaxed contract, with no nested prefab and no solid collider in the baked scene. The engine's level rules (one village icon per state; two wall sections and four engines cached per level). Marker yaws. Every village's production meshes are its type's. No functional tag inside a look. Every re-snapped piece is on the terrain (worst 0). Culture-correct looks, and prototypes equal to looks. The reuse report. The frozen looks rebuild the same layout and XML without the installed map.
- **Faults.** A wall with two solid pieces, a nested prefab, a solid collider, a functional tag inside a look, a banner whose parent has no level bits, a phase-0 wall without its solid piece: each is refused.

`tools/Test-Settlements.ps1` checks the wall-level distribution and that `PlanVillageTypes` equals the built types (A' 187, B' 276 villages). `tools/Test-CompiledWorld.ps1` checks that the records take their looks' prototypes and that a mismatched village look is refused.

## Defaults taken

- **Village scale.** Villages fit at scale 1, down to 0.7. Owner decision 5 scales town and castle layouts, and the installed village icons are already at 0.44.
- **Clearance.** A share of the distance by kind weight (town 3, castle 2, village 1); a phase-0 settlement claims 1.0.
- **Strict neighbour rule.** While `repeatPenalty` > 0, a look a neighbour holds never beats a free one. Matching looks rank before grafted or castle variants.
- **Village yaw.** A non-harbour village keeps its look's own orientation.
- **Walls draw from the prototypes stream** (`<id>|walls`), so no new stream key is added to the pinned `economy.json`.
- **Not a graph policy.** `settlement-looks.json` is read by the scene build, not the campaign factories. Its effect is in the scene name, through the settlement XML hash, and the layout hash.
- **The `wall` template** (`mi_{code}_wall_{level}`) names no Battanian mesh and is unused. Phase 0 uses `wallSegment` and `collapsed`.
- **Fixtures and the run-time path** (hideouts) keep MMR-40's empire pieces; there is no look there.

## Limits

- **Nothing is verified in game.** Still unverified:
  - how the looks render at scale, and the culled pieces;
  - whether vanilla `align_to_terrain` flags fight the re-snap;
  - raycast-only colliders on vanilla meshes;
  - banner grouping under look parents;
  - the frame cost of about 21k and 32k entities;
  - the entity check's slices.

  All of these belong to MMR-247.
- **Towns are tight.** At A'/B' spacing, 30 and 37 town looks shrink below 0.5 (to 0.35 at least), and 4 and 8 towns fall back to phase 0.
- **Variants change the entered scene.** Variant towns (9 on A', 5 on B') enter their factory-picked scene, not the look's.
- **Package size.** The frozen looks add 6.6 and 8.2 MB to a world package.
- **Medians in two harnesses.** `tools/Test-Playability.ps1` and the fixture tool still place at the installed medians (phase 0). `tools/Test-NavigationSweep.ps1` places with the looks, as production does.
