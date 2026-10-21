# Magic water (MMR-290), 2026-10-20

Where magic touches water, the water changes too: MMR-288's magic ground veins carry on over magical seas, lakes and rivers as luminous threads in each school's colour. FWGC++'s biome variants include ocean and lake cores, so generated worlds already have magic water; live casts add more. Nothing here has been seen in game; MMR-247 checks it (c31) with the toggle check below.

**MMR-291 (2026-10-21): magic water is drawn as overlay meshes.** The decal lab (acc5lab) showed no decal draws on our sea or lake surface (R1 negative) and module textures draw nothing as decals. With `roads.json overlayMeshes.magicWater.mode` `mesh` (the default) every stamp of this plan is drawn as a mesh piece (sea and lake stamps flat on their plane plus a lift, bed and coast stamps draped; inland and glow stamps with the land veins) and `MagicWaterRuntime` draws no decals (its `Drawn`, `SetVisible` and `Record` then speak for the meshes); mode `decal` brings this layer's decals back. See `docs/overlay-meshes.md`.

Code: `World/MagicWater.cs` (pure: the plan, the cover rule, the installer), `World/MagicWaterRuntime.cs` (engine side, an `IMagicVeinLayer` and `IMagicWaterVeins` of `MagicGroundRuntime`, in its one `Layers` initializer after MMR-289's features). Policy: `roads.json` `magicWater` (`RoadsPolicy.MagicWaterPolicy`, strict). Art: `tools/art/magic_water.py`, sources `Modules/MNBL.ProceduralWorld/AssetSources/magic_water/mnbl_magicwater_{d,n}.png` (LFS), evidence `evidence/art/magic-water.json`. Tests: `tools/Test-MagicWater.ps1 -BundleA -BundleB [-Dump DIR]` (`tests/World/MagicWaterTests.cs`), `tests/test_magic_water.py`; evidence `evidence/world/magic-water.json`; preview `tools/render_magic_water.py`. Ground veins: `docs/magic-ground.md`; water: `docs/map-dressing.md` (MMR-285).

## Route (static evidence, no launch)

The route was chosen from the engine's shader sources, the installed materials and IL before any code (full record: `C:\Users\davgo\m2\results\MMR-290-route.md`). `S` is the game's `Shaders\Sources`.
- **Chosen: (a) water-surface decals.** The campaign map's water (`naval_worldmap_water` on `editor_plane_high`; `naval_worldmap_river` too) uses the shader `water_surface_worldmap` with the option `use_decals`. `S\water_surface_worldmap.rs` `:298-433` projects each water pixel (the surface itself) into every decal box of its tile and draws the decal when the surface point lies inside it. No `render_on_*` flag is tested. Non-foam decals sample the decal atlas with their atlas argument (`SetVectorArgument`), take `factor_color_1` (`SetFactor1`, alpha included) and are lit (`:455`), then **added** to the water colour (`:468`, weighted by `shallow_depth`, about 1 past 0.1 units of depth).
- The list the water reads is the second half of `visible_decals`, culled by the tile's side planes only (`S\tiled_envmap_culling.rs :610-622, :774-786`), so a thin box on the plane over deep water is in the water list and not in the terrain list.
- NavalDLC draws ship foam on the campaign-map water the same way (`NavalMobilePartyVisual::TickFoamDecals`: `Decal.CreateDecal`, `SetMaterial`, `Scene.AddDecalInstance(d, "editor_set", true)`, `SetVectorArgument`, `Frame`), which is MMR-249's entity-less decal technique our dressing already uses.
- Rejected: (b) per-school water planes (the water material is `no_alpha_blend`: a hard rectangle, not veins; `g_mesh_factor_color` tints only the refracted bed, invisible past about 0.5 units of depth; a second full water pass); a global water colour map (shared by every War Sails map); runtime mesh ribbons (no evidence how they sort against forward water that writes no depth); particles (cost, not veins).
- **Additive only** (risk R2): a decal can brighten and tint water, never darken it. Every school is a glow; umbral is a violet glow.
- **Open risk R1**: the water list exists only when the engine sets `cull_decals_without_depth` (`tiled_envmap_culling.rs :774`). War Sails' ship foam relies on it on the same material; whether native sets it for our generated scene was not established (TaleWorlds.Native.dll was not disassembled). If it is off, surface stamps over deep water draw nothing and only the shore band shows: switch to the fallback.

**Fallback (policy `magicWater.mode`, the same graph and stamps):**
- `bed` (F1): sea and lake stamps are drawn only where the bed lies at most `bedMaxDepth` (0.3) below the plane, centred on the bed, through the terrain decal path (they show through shallow water); deeper ones are skipped. A thin band: 64 of 1,524 plane stamps on A', 137 of 1,005 on B'.
- `shore` (F1 + F2): also a ground-material tip at every shore crossing, on its land side, ending at the shoreline (A' 184, B' 397), so "magic reaches this water" shows on land whatever the water draws.

## Placement

The stamps are MMR-288's: `MagicVeins.Build` already covers water samples (ocean and lake cores, samples on sea, lake and river travel cells), and its stamps with `OnWater` (owner node on water, or centre on a water cell or water-core sample) are this plan's. They keep the graph's ids, kinds (trunk, fork, tip, burst), boxes (L along the vein, W across), yaws and reveal times, so a vein crosses the shore with no seam in its structure and a live cast's water veins grow with its land veins: each water stamp shows at its own `RevealAt` (see Runtime, reveal steps).

What draws a stamp (`MagicWater.Cover`, by its centre):
- **sea**: the ground lies below the datum (the sea plane shows). The box is centred on z 0 with half depth `halfDepth` (0.2).
- **lake**: inside a shown lake plane's box above its bed (`GeneratedWaterSystem.Lakes`, read in game from the installed map dressing's plan). Centred on the lake's z, half depth 0.2.
- **bed**: any other water travel cell: a river (rivers have no plane; their stamps lie over MMR-285's river stamps) or a sea or lake cell no plane shows (MMR-285's fill stamps). Centred on the ground with the slope depth (`MapDressing.SlopeDepth`, at least 3).
- **coast**: the centre lies on land beside the water the vein crosses into (its footprint reaches water). Centred on the ground with the slope depth; its box is clipped over the water planes (below), so it paints the land side of the shoreline and the plane stamps beside it carry the water side.
- **inland**: the graph counts it as water only because its owner sample has a water core (an ocean or lake biome sample on a land travel cell, above the datum, outside every lake plane) and no water lies under its footprint. It is land, so it draws with the look's ground material (row by core group, else `fallback.row`), closing the vein (B' 4, A' 1).

A plane stamp's thin box also paints the terrain within 0.2 of the waterline: that band is the visible join with the land veins on the shore.

**With the ground overlay (integration of MMR-288's gaps fix).** In mode `surface` the layer declares `IMagicWaterVeins.DrawsWaterStamps`, so `MagicGround.Plan` keeps the land branches that hang off water nodes (they continue the water veins) instead of re-rooting or dropping them, and this plan skips the water stamps of the components the ground drops below `veins.minStamps` (`MagicGroundPlan.Dropped`; `MagicWater.Plan(..., dropped)`, counted as skipped `dropped`). The fallback modes skip deep plane stamps, so there the layer does not declare it and the ground plans as with no water layer.

**Box clip (MMR-247's `WaterPlanes`, `docs/map-dressing.md`).** A stamp centred on the terrain (bed, coast, inland, glow) gets the plan's `Floor`, the highest water plane its footprint shows (7 x 7 samples, as the dressing's road and water decals), and the engine target raises its box bottom to `Clearance` (0.01) over it (`WaterPlanes.Box`, the planes of the lakes the layer planned over: `WaterPlanes.Of(MapDressingRuntime.CurrentPlan())` whenever a dressing is installed), so it never paints the water surface in a ground colour. Sea and lake stamps are centred on their plane on purpose and stay whole. `Record()` counts `clippedOverWater` and `submerged` (wholly under a plane: a sliver that paints nothing).

Material and atlas: one decal material `mnbl_magicwater` for every school, `SetVectorArgument(0.25, 0.125, column / 4, row / 8)`: column by kind (`magic.atlas.columns`), row by look school (`magicWater.atlas.schoolRows`: weave 0, umbral 1, infernal 2, radiant 3, fire 4, water 5, earth 6, air 7; hidden schools take their look). `magic.atlas.flipV` uses `(7 - row) / 8`; `magic.atlas.flipU` turns the yaw by 180 degrees, as the ground stamps do. The box width is `widthScale` (1.07) x the vein's width: the cell's inner height is 224 / 256 of the box, a ground cell's 480 / 512. `SetFactor1` is white with alpha `opacity` (1.0).

**Deviation from the route (default taken):** the route named the material `mnbl_magic_water`. That is already the water school's ground decal (`mnbl_magic_<school>` with school `water`, published by MMR-259), so `Material.GetFromResource("mnbl_magic_water")` would return the ground material and read it with the wrong atlas. The magic water material is `mnbl_magicwater` (textures `mnbl_magicwater_d`, `_n`); the reader refuses a `magicWater.material` that starts with the ground prefix.

**Until it is published** (the interim): each stamp falls back to the look's ground material `mnbl_magic_<look>` with its 4 x 4 cell (row by the owner node's core group, else `fallback.row` 3, cold, for water cores; box W not widened). A material that does not resolve is remembered and not tried again; a stamp neither resolves for is not drawn (counted), never thrown. The ground cells are ground-coloured, not luminous; on the water surface they add their colour, so the interim look is a pale tint, not the glow.

Budget: at most `maxDecals` (2,500) water stamps per world; past it the thinnest outermost generated stamps go (live stamps and bursts stay), as the ground stamps. Not reached on A' or B'. They share the scene's decal budget (MMR-249: 5,000 in view) with the ground veins, roads and MMR-285's water.

Seam images: each stamp is drawn once at its periodic image nearest the camera and moved on a camera wrap (`RefreshImages`), as the ground stamps; plane z is the same for every image (a strip copy of a lake has its twin's level).

## Runtime

`MagicWaterRuntime` follows `MagicGroundRuntime`'s lifetime on the engine thread:
- `DrawsWaterStamps` (MMR-288's `IMagicWaterVeins`, read by the ground's plan before the layers install): `roads.json magicWater.mode` is `surface`.
- `Install` (AfterLoad, after the ground decals and the map dressing): plans over the dressing's lake planes without the ground plan's dropped components (`MagicGroundRuntime.Plan.Dropped`) and draws what the campaign time shows (`MagicWaterPlan.Revealed(hours)`).
- `Changed` (a live cast at its safe point, a reveal step): a new graph is re-planned (with the new ground plan's dropped set); the installer redraws only the stamps whose key changed (a cast's zones).
- Reveal steps: `MagicGroundRuntime.Advance` ticks at `MagicGroundPlan.NextReveal`, which scans the vein graph's nodes as well as the land stamps (MMR-290 fix). Every water stamp (and MMR-289 feature) takes its owner node's `RevealAt`, so it shows at its own time. Before the fix only land stamps set the steps: water stamps at an Order with no land stamp showed at the next land step, and those above the cast's highest land Order never showed in the session (a synthetic coastal fire cast with a cast day: 20 of 45 on A', 13 of 42 on B'; the Nerdun cast: 2 of 8 late, at 288 h instead of 276 h). `Test-MagicWater` simulates the schedule (ticking only at the returned times) for both casts.
- `RefreshImages` (a camera wrap): moves each stamp to its image.
- `Release(true)` removes natively (a new Begin on the same live scene); `Release(false)` after `Scene.ClearAll` only drops the handles, with no engine call (MMR-262); a second Release does nothing.
- For MMR-247: `MagicWaterRuntime.Record()` (plan and installer counts: by mode, fallback decals, missing materials, not drawn, errors), `Drawn`, `Installer`, and `SetVisible(bool)` for the toggle check. `WorldLifetimeTrackers` tracks "map: magic water drawn".
- Decal atlas (MMR-247 D, acc5): the campaign map's tiled decal loops, the water shader's included (`water_surface_worldmap.rs` samples `decal_atlas_texture` at the decal's atlas rects like the terrain's), read only Native's prebaked atlas, where no module texture has a rect; acc5's 1,708 magic water decals drew nothing, so the toggle shots matched. The engine target judges each material by its diffuse texture (`DecalAtlasChooser`, `Record()["target"]`): `mnbl_magicwater` and the `mnbl_magic_<look>` fallback both lack a rect, and each stamp draws the vein stamps' vanilla fallback by kind (docs/magic-ground.md "Fallback"; the magic water decal's with `opacity`). R1 (whether the water shader's decal list is filled on our scene) is still open: the decal lab's sea grid (rows `plane` and `large` of the vanilla columns) answers it, and the toggle check of the next run.

## Art

`tools/art/magic_water.py` (Pillow, deterministic: two runs give byte-identical atlases and evidence). 2048 x 2048: 4 columns x 8 rows of 512 x 256 cells, 16 px transparent margin on every side.
- Geometry: MMR-288's vein kits (`magic_ground.py` `vein_skeleton`, `vein_masks`, `periodic`, `head_blend`, `apply_feather`; contract C1 chaining at 0.75 L), drawn in a 512 px square, **mirrored about v = 0.5** (the larger of each mask and its flip, so the stamps are exactly symmetric) and squashed to the 224 px inner height (colour and alpha apart, box filter).
- Vanilla derivation (owner decision 4): the edge break-up and the lace inside threads and halo come from Native `foam_shore`'s alpha (the shore-foam network War Sails draws on coasts); the halo's soft stain from Native `decal_water_a`'s alpha (the wetness decal). Colour is each school's luminous palette (hot axis and source, thread, faint halo, alpha <= 0.25) modulated by that lace; weave uses MMR-288's opal ramp. Earth is ochre-bronze and air silver-white, so that on water they part from fire's orange and water's cyan (default taken after review: the first palette had fire and earth threads 10.4 apart, water and air 14.0). Codex was not used.
- `_n`: a gently raised thread (slopes clamped to 0.6) for the water normal's UDN blend.
- Gates (all 8 schools, 32 cells: 0 failures): margins alpha 0; v-symmetry max |a(u,v) - a(u,1-v)| <= 2/255; chain: the trunk's raw alpha repeats every 384 texels and the feathered head over tail composites to one stamp's alpha (MAE 1.26-1.67 and 0.84-0.94 /255, limit 4); reads on water: the core added to each acc4 water colour has CIEDE2000 30.5-74.3 (limit >= 15) and the halo 0.6-7.6 (limit <= 8); school separation: every pair of schools' thread colours (weave: its opal mean) added to each acc4 water colour differ by CIEDE2000 >= 15 (closest: weave/umbral 17.7, infernal/fire 19.8, radiant/earth 19.8, fire/earth 19.8; the hot cores are near white and not gated); no clipping (0 core texels at 254+, limit 1 %); DXT5 stand-in for BC7: diffuse alpha-weighted MAE 3.61 (limit 4.87), alpha 1.77.
- Adaptation: the route's "core inside the vanilla world-map decal envelope" is replaced by the no-clipping gate: a luminous additive thread is not a ground albedo, and that envelope is for ground.

## Publish request

One decal material and two textures, in the shared Codex Modding Kit session (contract C3), by the MMR-259 procedure (`docs/magic-ground-textures.md` "Publishing": Create Override from vanilla, rename, set textures, Publish Module outside the module, copy `AssetPackages/*.tpac` and `Assets/` into the repository).
- **Material `mnbl_magicwater`** (folder `magic_water`): Create Override of Native `mainmap_decal_undervillage` (shader `deferred_decal`, as `mnbl_magic_<school>`), renamed. Flags `gbuffer_alpha_blend`, `use_albedo`, `render_on_terrain`, `use_normalmap`. Do not set `water_foam_decal` (it routes the decal through the foam-only path), `albedo_multiply`, `hardlight_blend`, `render_on_objects`, `render_on_grass`, `height_masked`, `use_specular`, `override_visibility_checks`. Diffuse `mnbl_magicwater_d`, normal `mnbl_magicwater_n`; no specular.
- **Texture `mnbl_magicwater_d`**: `Modules/MNBL.ProceduralWorld/AssetSources/magic_water/mnbl_magicwater_d.png`, 2048 x 2048, Albedo_HQ **BC7**, `has_alpha`, `dont_resize_in_atlas`, full mip chain. sha256 `d3f7dfbab332811de0808c33a1cda182452311d1912887ce9df14043d1ab9846`.
- **Texture `mnbl_magicwater_n`**: `.../magic_water/mnbl_magicwater_n.png`, 2048 x 2048, Normal **BC5**, `is_bumpmap` (DXT5 Normalmap off), full mip chain. sha256 `9d773acc0f278cf59ca127c8ecf5bdea76039bea60e863994661bce070f0ae94`.
- Header check after the publish: `python tools/art/check_magic_packs.py --water --out evidence/art/magic-water-packs.json` (2 textures + 1 material, 0 problems).
- Until then the interim fallback above draws.
- **Published 2026-10-20** in the shared Codex session (pack0.tpac `36ce4d89…`; see `docs/magic-ground-textures.md` "Publishing"):
  `mnbl_magicwater` with the four flags and no specular; the header check passes with 0 problems
  (`evidence/art/magic-water-packs.json`).

## In-game check (MMR-247, c31)

1. **Toggle diff (settles R1).** Over a seed 42 magic ocean zone at camera distance 38: screenshot, `MagicWaterRuntime.SetVisible(false)`, screenshot, `SetVisible(true)`. Mean |dRGB| inside open-sea stamp footprints (depth > 1): pass at >= 8/255. Repeat at a lake and at a shoreline. If open sea fails and the shore passes, R1 is off: set `magicWater.mode` to `shore` (F1 + F2) and record it.
2. Screenshots: a generated magic sea zone, a magic lake, a magic river, and the Nerdun cast (its delta touches the lake beside Nerdun: 3 live water stamps, bed 1, lake 1, coast 1). The veins must continue from land across the shoreline.
3. Counts from `MagicWaterRuntime.Record()`: stamps planned / live by mode, `fallbackDrawn` (yes while `mnbl_magicwater` is unpublished), `notDrawn`, `missingMaterials`, errors; in-view decal totals with the ground and dressing.
4. Also check `flipU` / `flipV` on the water stamps (they follow `roads.json magic`).
5. Side observation for the triage: with the toggle, note whether MMR-285's fill stamps, road decals and the land veins near the shore paint the sea surface (their boxes hold the plane; route section 3).

## Numbers (`evidence/world/magic-water.json`)

| | A' (seed 42, 1db4cf74...) | B' (seed 73, 59e8487d...) |
|---|---|---|
| Water stamps (graph water stamps; skipped with the ground's dropped components) | 1,709 (1,745; 36) | 1,470 (1,501; 31) |
| By mode | sea 1,509, lake 15, bed 120 (river 26), coast 64, inland 1 | sea 866, lake 139, bed 350 (river 103), coast 111, inland 4 |
| By kind | trunk 878, fork 163, tip 618, burst 50 | trunk 711, fork 132, tip 583, burst 44 |
| By school | water 516, fire 456, umbral 247, weave 174, earth 168, radiant 72, air 55, infernal 21 | fire 594, earth 283, water 233, radiant 148, air 106, weave 73, infernal 29, umbral 4 |
| Ground land stamps with / without the water layer | 774 / 559 | 2,012 / 1,443 |
| Terrain-centred boxes (bed, coast, inland): raised over a plane / holding water after the clip | 136 of 185 / 0 | 285 of 465 / 0 |
| Depth under plane stamps (p10 / median / p90) | 1.07 / 8.53 / 14.24 | 0.08 / 4.74 / 13.06 |
| Shore crossings (both ends drawn; in dropped components) | 193 (193; 4) | 408 (408; 2 end on a water-core sample on land; 2) |
| Decals whole world: ground + roads + water + magic water | 774 + 2,234 + 343 + 1,709 = 5,060 | 2,012 + 2,929 + 962 + 1,470 = 7,373 |
| Most in view 58 x 37 / 116 x 74 (magic water in the first) | 183 / 529 (66) | 148 / 510 (62) |
| F1 (bed) / F1 + F2 (shore) stamps | 255 / 439 | 620 / 1,017 |

Nerdun fire cast (A', MMR-261's recorded delta, 5 water travel cells): 3 live water stamps (bed 1, lake 1, coast 1) beside its 7 land stamps, revealed 0 / 0 / 2 / 3 / 3 at 0 / 12 / 24 / 36 / 48 h. A synthetic coastal fire cast adds 39 (A') / 38 (B') live water stamps, every one shown at its own reveal time by the runtime's schedule (30 / 20 of them above the cast's highest land Order). Before the integration (MMR-290 alone, on 288's earlier graph, the 31bb9dc3 / 186f0c32 fixtures and no dropped set): 1,948 / 1,746 water stamps, Nerdun 8.

The whole-world totals pass 5,000 (the in-view budget, MMR-249); the in-view maxima stay near a tenth of it. The cost of out-of-view decals is unmeasured (MMR-247).
## Not verified

Everything in game: R1, the look on the water surface and through the bed, the shore band, flipU / flipV, draw order against the river and fill stamps (R4), the per-tile cap of 127 decals in the frustum-only list at far zoom (R3), the distance fade at 120 units (R5), storm cells (angle rejection fades the stamps), and GPU cost (R6). Whether `Decal.SetAlpha` would serve the reveal is unused: the reveal adds and removes stamps, as the ground veins do.
