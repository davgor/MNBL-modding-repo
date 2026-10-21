# Map dressing: road decals, bridges, ruin markers and water decals (MMR-257, MMR-285, MMR-247)

The campaign map draws the world's roads, bridges at the existing decks, a marker per FWGC++ ruin and water decals on the water cells no
plane shows (rivers, and the sea and lake cells navigation calls water). This is presentation only: no scene,
navmesh, scene name or nav cache changes, and no campaign object is added. Findings §8 (roads) and §11 (ruins); the decal technique is
MMR-249's probe (findings §13). Nothing here ran in game yet: MMR-247 shows it.

MMR-291 (2026-10-21): rivers, pools and fills are drawn as water meshes (`roads.json overlayMeshes.rivers`: ribbons along the river chains below with `naval_worldmap_water`, the lake planes' material; discs for pools and fills); their water decals are left out of the installer (`MapDressingInstaller.SkipWater`, counted in `waterReplacedByMeshes`). Mode `decal` brings them back. See `docs/overlay-meshes.md`.

| Piece | File |
|---|---|
| Plan builders (pure managed) and the installer | `World/MapDressing.cs` |
| Engine adapter, seam refresh, hover, teardown | `World/MapDressingRuntime.cs` |
| Ruins encyclopedia page | `Campaign/RuinsEncyclopediaPage.cs` |
| Source read (ruins, civilization names) | `World/GeneratedClimateSystem.cs` (`Source.Ruins`, `Source.CivilizationNames`, read in the climate's pass over `world.json`) |
| Policies | `worldgen/policies/roads.json` (`decals`, `bridges`, `water`), `worldgen/policies/settlement-looks.json` (`ruins`) |
| Offline harnesses | `tools/Test-MapDressing.ps1 -BundleA -BundleB` (`tests/World/MapDressingTests.cs`); `tools/Test-WaterVisibility.ps1 -BundleA -BundleB [-Dump DIR]` (`tests/World/WaterVisibilityTests.cs`, MMR-285, MMR-247) |
| Water decal art (MMR-247) | `tools/art/water_decal.py` (derivation, gates, the published-pack check), `tools/art/water_preview.py` (top-down review of a `-Dump`), sources `Modules/MNBL.ProceduralWorld/AssetSources/water_surface/` (LFS), evidence `evidence/art/water-decal.json` |
| In-game check (MMR-247) | `Diagnostics/WaterVisibilityProbe.cs` (`world_activation` with `waterVisibility=1`) |
| Evidence | `evidence/world/map-dressing.json`, `evidence/world/water-visibility.json` |

## Lifetime

- `WorldReconstruction.Build` builds the plan with the campaign, the same way at activation and at load. It reads `travel-graph.json`
  `roads[]`, `campaign.json` (presentation cultures), `cells.bin`, the world's kept navigation report (`build.bridges`: an installed-scene
  load skips `Prepare`; read with `NavigationGate.ReadAt`, since the persisted file is the gate record around the report, MMR-247) and the
  source's `ruins[]`.
- The plan waits in `MapDressingRuntime.Plans` under the scene name, like the battle terrain context. Activation sets it with the context
  and removes it on a failed start; the loader sets it before the load starts. `GeneratedGameManager`'s teardown clears it.
- `GeneratedMapScene.AfterLoad` installs it (`supportReport["mapDressing"]` holds the counts, the install time and any error; nothing
  throws).
- Teardown (MMR-262). `GeneratedMapScene.Destroy` runs after `MapScreen.OnFinalize` has called `Scene.ClearAll` (installed IL: the
  screens are finalized before `OnStateStackEmpty` reaches `Game.Destroy` and `Campaign.OnDestroy`). ClearAll has taken every drawn decal
  and entity and freed the scene's decal container, which `RemoveDecalInstance` reads unchecked (a native access violation). So Destroy
  only lets go of the handles (`MapDressingRuntime.ReleaseHandles`, the installer's `Drop`; trace `map_dressing_released:handles n:engine
  calls 0`), with no engine call. Native removal (`Release`) is kept for an install on the same live scene; an install still held on
  another scene is dropped the same way. On a live scene each handle is removed once (the engine's decal set erases its end for a decal
  it does not hold).
- `WorldLifetimeTrackers` counts the waiting plans and the drawn objects (both 0 at the main menu).

## Roads

- Each `roads[]` piece (routes and feeders) is a polyline: its start point, the centres of its cells, its end point. Columns are
  unwrapped, so a piece across the seam stays continuous; poles are split by the compiler already.
- Smoothing: `smoothingPasses` rounds of Chaikin corner cutting with both ends kept. The curve stays inside the piece's cells.
- Ends:
  - `gate`: clipped `gateClip` units. A decal within `gateClip` of any gate is dropped, also where another road passes a settlement.
  - `bank` (a crossing with no deck, RT-17): the last decal ends at the bank point, never past it.
  - `pole`, `junction`: drawn to the end.
- Decals every `spacing` units of path, `size` wide and 1.1 × spacing long (default taken: the policy's `size` is the width, so the road
  reads as a line; neighbours meet on curves). A decal is dropped where its footprint's ends or centre lie on a water cell (decks are water
  cells: the bridge covers them), where they lie under the sea plane or a shown lake plane (MMR-247: a land cell's ground can dip under a
  plane; `MapDressing.PlaneCover`, counted in `droppedOnWater` and, of those, `droppedUnderPlanes`), and where another road's decal already
  stands within half a spacing (feeders end on routes). The lake planes are computed before the roads for this.
- Cap: past `maxDecals` (3,000) the spacing stretches until the world fits. The stretch is set by the drawn length (MMR-247, after
  MMR-247-R's gate spurs): the decals are placed at `spacing` first, and the road they cover after the water, gate and overlap drops
  (kept decals x spacing, `drawnLength`) over `maxDecals` is the new spacing, placed again (and stretched again while the count still
  passes the cap, never beyond the whole sampled length over `maxDecals`, at which it cannot). The sampled length counted a spur's second
  segment, which runs along the road it joins and is dropped as an overlap, so it thinned every road of B'.
- Family: the road's presentation culture's (`cultureFamilies`). A route takes its `from` town's culture, a feeder its settlement's (a
  castle through its garrison town, a village through its bound settlement). Each road keeps one material of its family (road index
  modulo the family's materials; default taken). Atlas arguments are the probe's.
- Drawing: `Decal.CreateDecal(null)`, `SetMaterial(Material.GetFromResource(...))`, `Scene.AddDecalInstance(d, "editor_set", true)`,
  `SetIsVisible(true)`, `SetVectorArgument(atlas)`, `Frame` (rotation columns scaled by the half extents, half depth 3 for road decals,
  z the terrain height at the centre); removed with `RemoveDecalInstance(d, "editor_set")` on a live scene only (see Lifetime).
- Depth on slopes (MMR-262). The deferred decal shader clips ground outside the box along z and fades it by 1 - |z|^8. MMR-260's magic
  decals sample the ground (`MapDressing.Ground`) at their footprint's 4 corners and 4 edge midpoints and take half depth max(3, the
  farthest from the centre height / 0.85) (`MapDressing.SlopeDepth`); z stays the centre's height. Road decals (half extents 0.4 to 0.77)
  keep 3. Water stamps fit their box to their core instead (MMR-247, `MapDressing.FitBox`, see Water). The depth is not part of either
  plan's digest.
- Boxes over water (MMR-247). The campaign map's water shader draws every decal whose box holds the water surface, added to the water
  colour (MMR-290's route study, section 1: `water_surface_worldmap.rs`, no `render_on_*` test), so a box centred on the ground near a
  coast or a lake also painted the sea or the lake. `WaterPlanes` keeps every box above the planes its footprint shows: `Floor` is the
  highest plane whose water a footprint sample shows (the datum where a sample's ground lies under it, a shown lake where a sample inside
  its box lies under its Z; -infinity over dry ground), and `Clip` raises the box's bottom to `Clearance` (0.01) over it and keeps its top.
  Terrain under the plane is under water anyway. The plan sets `Floor` on every road and water decal (7 x 7 samples);
  `MapDressingRuntime.EngineTarget` clips every frame it sets with it, and for a decal without one (MMR-260's magic ground, `Floor` NaN)
  samples 3 x 3 itself against the planes of the dressing drawn on the map. A box wholly under a plane becomes a sliver over it that paints
  nothing. A' / B': unclipped, 222 / 368 water and 2 / 1 road boxes would hold water their footprint shows; clipped, none.
  - MMR-288/289/290 (merged): a decal drawn through `MapDressingRuntime.EngineTarget` (MMR-288's land veins) is clipped with no change on
    their side (3 x 3 runtime samples; their plan sets no `Floor`). MMR-290's `MagicWaterRuntime.EngineTarget` (its own frames) clips every
    stamp centred on the terrain (bed, coast, inland, glow) with `planes.Box(d, x, y, ground, out z, out half)` over the planes of the
    lakes it planned over (`WaterPlanes.Of(MapDressingRuntime.CurrentPlan())` whenever a dressing is installed), with the 7 x 7 `Floor`
    its plan sets; its sea and lake stamps, centred on the plane on purpose, are not clipped (`docs/magic-water.md`).

## Bridges

- One run of bridge pieces per straight leg of each deck in the kept `build.bridges`, from bank edge to bank edge. An L-shaped deck turns
  at its corner cell (`lShaped` `rotate`); `skip` leaves it out.
- Pieces: the installed `map_bridge_*` prefabs (Native `map_icon_parts.xml`). River decks use the wooden piece (`prefabs[0]`); other decks
  the stone end, side and middle pieces (`prefabs[3]`, `[2]`, `[1]`), the first half turned outward, as the vanilla `stone_bridge` is built.
- Scale 2.885 and a piece every 0.42 local units along local +y: measured on the vanilla SandBox campaign map's `stone_bridge`.
- z is the higher bank's ground.

## Ruins

- One record per `world.json` `ruins[]` entry: uid, name, civilization (its `civilizations.entities[]` name), cause, reason, founding and
  destruction ages, node and source sample.
- Position: the projection's source sample (`projection.json` `forward.sourceSample`). A ruin on a sea, lake or river cell snaps to the
  nearest land or mountain cell within `snapCells` cells, or is skipped (`water`); a pole sample is skipped (`pole`). MMR-247 (acc5): a
  point a shown water plane covers is water too (the sea below the datum, or a lake plane over a land cell's sample, by the lower of the
  encoded and the collision height, `MapDressing.LowGroundOf`): such a ruin snaps, and no snap target is such a cell centre. On B' one
  marker had snapped onto lake 94's sample (the acceptance probe's lake ray met it, not the plane).
- Skipped within `minGap` units of a live settlement (`nearSettlement`, RT-22) and within the hover radius of a marker already placed
  (`sharedSite`: a city ruined twice).
- Marker: one installed ruin piece (`pieces`, chosen by the uid's hash, yawed by it) at `scale`, z the ground. Its name shows (a hint)
  while the cursor's ground point is within 1.5 units (`MapDressing.MarkerHoverRadius`), polled on the application tick while the top
  screen is the campaign map (`CampaignMapScreen.Is`: MapScreen or War Sails' NavalMapScreen subclass, MMR-247).
- Names are read through `RuinNames.Display` only. MMR-286: the compiler cleans every ruin name with the shared name cleaner
  (campaign.json `ruins[]`: uid, name, and `sourceName` when it differs; no Age tag, unique among the ruins by a lexicon suffix);
  `RuinNames.Apply` gives each ruin its `DisplayName` when the plan is built, and `RuinNames.Cleaner` (default `Cleaned`) shows it. A
  ruin the campaign does not name refuses the plan. The source name stays `SourceName`.

## Ruins encyclopedia page

- Lists every ruin of the world (also the skipped ones): the name, then "civilization - cause (age n)". Rows are read each time the list
  is built. No gameplay and no detail page: a click does nothing (`GetObject` is null, so `EncyclopediaManager.GoToLink` returns).
- Registration (`SubModule.OnGameInitializationFinished`, generated campaigns only). The installed page constructor reads its identifier
  types only from the internal `EncyclopediaModel` attribute, so the page is created without its constructor and its private fields are
  set as the constructor would. Then it joins the manager's page table, after SandBox built it. A changed installed member leaves the
  encyclopedia unchanged (`RuinsEncyclopediaPage.LastError`).

## Water (MMR-285, MMR-247)

Lake (8) and River (11) faces, and sea faces, are impassable for land parties, so every such travel cell must render as water (findings
§17 RT-16). Three things draw water:
- **The sea plane** on the datum, wherever the ground lies below it.
- **The lake planes** (`GeneratedWaterSystem.Lakes`, docs/water-climate.md). Since MMR-285 another lake's samples are not shore, and MMR-278's
  shaping lowers every matched group's bed under the plane its shore allows, so every lake group shows its plane: A' 35 of 35, B' 159 of
  159.
- **Water decals** (`roads.json` `water`), for what no plane shows. A river follows its slope, so a flat plane cannot carry it.
  - River graph: River cells joined along edges between 8-neighbours (a diagonal only where no river cell shares an edge with both ends);
    a river end (at most one river edge) also joins the sea and lake cells beside it (the mouth).
  - River strips (MMR-247): the graph splits at every end and junction into chains of cell centres (columns unwrapped; sea and lake cells
    always end a chain; cycles left over start at their first unused edge). Each chain is smoothed (`smoothingPasses` Chaikin passes,
    ends kept) and carries a strip every `riverSpacing` units, turned along the flow, `riverWidth` wide and 1.5 steps long either way
    (`MapDressing.RiverStampLength`), so the strips chain. The strip is a round-ended capsule in the atlas, so strips round a bend leave no
    corners.
  - Pools: a river cell with no edge gets a round pool `riverWidth` across; so does any river cell centre the strips do not paint (a
    patch: the smoothed curve cut its corner).
  - Fills: every other Sea, Lake or River cell whose centre no plane and no river stamp paints gets a pool the size of its cell
    (`fillScale` 1), so it paints nothing past its own cell. These are the coast cells whose navigation centre lies below the datum while
    the shaped surface keeps land there (the nav/visual coast mismatch MMR-278 recorded; mostly a flat 0.05 over the datum), and the lake
    cells only one corner of which carries lake intent.
  - Boxes (MMR-247, `MapDressing.FitBox`): a stamp's box spans the ground of its core (7 x 7 samples over half its half extents; a strip
    over its whole length; ground under a shown plane taken at the plane) inside 0.85 of the box, plus 0.05, centred `BoxOffset` over the
    centre's ground. A pool's or fill's box tops out 0.25 over its centre's ground (`FillHeadroom`): it floods the low ground of its cell,
    not the slope behind it. Ground of a stamp's rim above its box (a bank) is not painted, so water does not climb the banks; a tight box
    also keeps ground just over a water plane painted once the engine target clips the bottom to that plane (see Boxes over water). A water
    material renders on terrain only, so a tight box over-projects nothing.
  - Coverage: a stamp paints a point inside `opaqueCore` x its half extents (an ellipse) whose ground lies within 0.85 of its clipped box
    (`WaterPlanes.Paints`). Both stamps are opaque to about half their half extents (the derived atlas: alpha at least 0.9 inside half;
    `mainmap_decal_undervillage`: measured by MMR-285).
  - Material (MMR-247): `mnbl_water_surface`, a published decal material (below), at atlas cell (1 / columns, 1 / rows, column / columns,
    row / rows): strips in `riverColumn`, pools and fills in `poolColumn`, the row (a variant) by a hash of the point; `tint` white (as
    published). While it does not resolve, the engine target draws each decal's `fallback`: the vanilla stamp `mainmap_decal_undervillage`
    tinted (77, 137, 216), so its opaque core lands on the water colour (`evidence/art/water-decal.json` `fallback`). The target counts what
    it drew per material and how many fell back (`supportReport["mapDressing"]["target"]`, trace `map_dressing_installed:...:materials
    ...:fallbacks n:clipped n`).
  - Decal atlas (MMR-247 D, acc5): the campaign map draws every scene decal from one prebaked atlas, Native's `decal_atlas_worldmap`
    (core.tpac, 4096 x 4096), at the rects of Native's `ModuleData/decal_textures_worldmap.xml`; the engine reads those lists from Native alone,
    so no module texture has a rect and a decal sampling one draws nothing (acc5: `mnbl_water_surface` resolved, 343 decals drawn, rivers
    invisible). The engine target judges each material by its diffuse texture against that list (`World/DecalAtlas.cs`,
    `DecalAtlasRuntime`; `roads.json decalAtlas`) and draws the decal's `fallback` when the material's texture has no rect, so the water draws
    the tinted vanilla stamp until the art can reach the atlas (or another draw path). The target's record names each material's diffuse
    texture, whether the atlas holds it and the decals passed over (`target.textures`, `target.unresident`, `target.atlas`).
    `water.minHalfDepth` (0: FitBox's own) is the least half depth a water box draws with, held for the decal lab's verdict on boxes a few
    centimetres deep (FitBox on flat ground gives `BoxPad` 0.05).
  - Cap: past `maxDecals` (1,500) the river spacing stretches. Road decals (3,000) and water decals together stay under MMR-249's 5,000
    budget; MMR-260's magic overlay draws its own share.
  - Drawing, seam images and teardown are the road decals' (kind 3 in `MapDressingInstaller`).
- **Decks** (river and lake cells with a road bridge) are drawn as water too; the bridge carries land parties over them.

The riverbed layer (MMR-255's `riverbed`, the `waterEdge` rule on lake and river samples) stays as the river's bank; no other layer was
needed.

### Water surface decal (MMR-247)

acc4 showed MMR-285's stamps as blue gravel (triage V1). Their colour was already close to the sea's (the stamp's lit median sits 3.5
CIEDE2000 from the acc4 sea), but the tinted earth stamp keeps the ground's grain in its albedo (luminance spread 33 in its core) and the
deferred decal adds its normals to the terrain's (UDN), so the gravel shows through. Route (default taken): a vanilla-derived water decal
material. Terrain carving with per-river water planes was not taken: a flat plane cannot follow a river down its slope, planes are scene
identity, and terrain shaping is outside this batch.
- **Art** (`tools/art/water_decal.py`, deterministic: two runs give the same bytes; Pillow only). Three 1024 x 1024 atlases, 2 x 2 cells of
  512 px with a 16 px transparent margin; column 0 the river strip (a capsule along u), column 1 the pool; rows are variants (a V
  convention unlike D3D's only swaps variants).
  - `_d`: one water colour (the close-view acc4 sea, (31, 58, 84) and (43, 61, 78), a quarter of the way to the shallow lake (62, 98, 98);
    lighting factor taken as 1), with a 4 % large-scale variation from NavalDLC's `water_noise`; one colour throughout, so overlapping
    stamps show no rings. Alpha: the capsule or pool falloff, its edge broken by two octaves of `water_noise`.
  - `_n`: Native `worldmap_water_normal_map` (the campaign map water's own ripples), xy scaled 0.55.
  - `_s`: the mean of Native `worldmap_swamp_decal_3_s`'s puddles (its top-decile gloss pixels): R 0, gloss 231, occlusion 239 (values DXT1
    holds exactly).
  - No Codex imagery.
- **Gates** (`evidence/art/water-decal.json`): seams (alpha 0 in every margin); opaque core (alpha at least 0.9 inside half the half
  extents: 0.989); chain (strips at the policy's step stay opaque along their middle: 1.0); reads as water (CIEDE2000 of the core against
  the acc4 sea samples 5.2 and 7.7, at most 12; against the acc4 ground samples 26 to 54, at least 20); no grain (core luminance spread
  0.35 against the vanilla stamp's 33.3); BCn round trip (DXT5 for BC7 2.94 and alpha 0.37 against 4.87 and 3; BC5 0.69 against 1.25 x
  vanilla's 1.21; DXT1 0.0 against 1.25 x vanilla's 0.62).
- **Publish request** (one Modding Kit session, the MMR-259 procedure in docs/magic-ground-textures.md "Publishing"; it can share the
  session with MMR-290's `mnbl_magicwater`):
  1. Copy `Modules/MNBL.ProceduralWorld/AssetSources/water_surface/` (after `git lfs pull`) into the installed module's `AssetSources/`.
  2. Import the three PNGs into a module folder `water_surface`:
     - `mnbl_water_surface_d`: 1024 x 1024, Albedo_HQ (BC7), has alpha, Dont Resize in Atlas, sRGB;
     - `mnbl_water_surface_n`: 1024 x 1024, Normal (BC5), Is Bumpmap (`is_bumpmap` in this build);
     - `mnbl_water_surface_s`: 1024 x 1024, Specular (DXT1), Is Specularmap.
  3. Material `mnbl_water_surface`: Create Override > Material of Native `mainmap_decal_undervillage` (shader `deferred_decal`), rename it.
     Keep `gbuffer_alpha_blend`, `use_albedo`, `render_on_terrain`, `use_normalmap`; add `use_specular` (as the magic materials) and
     `do_not_blend_normals` (vanilla's world-map swamp decals `worldmap_swamp_1..3` carry it with `render_on_terrain`: the water's own
     ripples replace the terrain's normals instead of adding to them, so no grain shows). Do not set `render_on_objects` (bridges and
     settlements stand on the water cells), `render_on_grass`, `albedo_multiply`, `hardlight_blend`, `water_foam_decal`, `height_masked`,
     `override_visibility_checks` or `is_road`. Diffuse `_d`, normal `_n`, specular `_s`. Leave the template's specular and gloss
     coefficients and normal-map power.
  4. Publish Module to a folder outside the module; bring `AssetPackages/*.tpac` and the editor's `Assets/water_surface/` into the
     repository (LFS); leave the install as found.
  5. Verify: `python tools/art/water_decal.py check --out evidence/art/water-packs.json` must pass (the three textures with their formats,
     size, flags and sources; the material with the six flags and none of the unwanted ones). Until then it reports the four records
     missing.
  - If the editor does not offer `do_not_blend_normals`, publish without it: the stamp still reads as smooth water in colour, but the
    terrain's relief lighting shows through.
  - **Published 2026-10-20** (Codex Modding Kit session, `C:\Users\davgo\m2\publish2\REPORT.md`): pack0.tpac `36ce4d89…`
    (70,834,304 bytes) holds the three textures and the material with five flags. The v1.4.8 editor offers no `do_not_blend_normals`,
    so the checker records it as optional and absent (`optionalAbsent` in `evidence/art/water-packs.json`). Whether relief lighting shows
    through is for MMR-247's screenshots.

**Offline gate** (`Test-WaterVisibility`, `evidence/world/water-visibility.json`). Over `cells.bin` v2, recomputed from the bundle, not from
the plan's own covers, each decal box clipped as the engine target clips it:
- every lake group shows its plane (0 hidden, 0 unmatched; the compiler's shaping record agrees);
- every River cell's centre is painted by a river stamp (inside its opaque core and clipped box) or lies under a plane, decks included;
- every impassable water cell (Sea, Lake or River without a road deck) shows water at its centre: the ground below the datum, a shown lake
  plane above the ground, or a water stamp painting it. A centre whose ground lies at most the clearance (0.01) over the plane a stamp
  covering it is clipped to is the shoreline itself, which no clipped box can paint: counted as `waterline` (B' 2: lake cells 0.001 over
  their lake, whose plane sits 0.001 under its lowest shore sample), not dry;
- no decal box (water or road) holds a water surface its footprint shows once clipped;
- no road decal's footprint lies under the sea plane or a shown lake plane;
- fills paint no land or mountain cell; the river strips' paint on land cells is recorded (diagonal links and smoothed bends cross land
  corners).

| | A' | B' |
|---|---|---|
| Lake groups shown | 35 of 35 | 159 of 159 |
| River cells drawn (decks) | 37 of 37 (2) | 145 of 145 (12) |
| Impassable water cells: sea plane / lake plane / river stamp / fill / waterline / dry | 5,541 / 26 / 41 / 193 / 0 / 0 | 3,274 / 200 / 145 / 362 / 2 / 0 |
| River edges / mouths / chains / strips / lone pools / patches | 24 / 16 / 23 / 142 / 1 / 7 | 133 / 29 / 92 / 562 / 1 / 31 |
| Water decals (river + fill) / with road decals | 343 (150 + 193) / 2,577 | 962 (594 + 368) / 3,891 |
| Boxes that would hold visible water unclipped: water / road | 222 / 2 | 368 / 1 |
| River strip paint on land cells (units², of the strips' painted ground) | 651 of 4,421 | 1,866 of 18,922 |
| Land cells whose centre shows water (allowed, recorded) | 93 of 2,389 | 130 of 4,192 |

Before MMR-247 (MMR-285's plan on the same fixtures): 331 water decals on A' (132 river, 199 fill), 903 on B' (517, 386).

Land cells may show water (a land cell whose centre lies below the datum or under a lake plane); that is allowed and recorded.

**Review** (`tools/art/water_preview.py` over a `-Dump`): top-down composites of the densest river window and the largest lake of each
world, before (MMR-285), with the fallback, and with the derived atlas.

**In game** (`Diagnostics/WaterVisibilityProbe.cs`, for MMR-247). On the loaded map it checks the scene's water bodies (the sea plane and one
plane per lake, a ray at each lake meeting its plane above the terrain), that every water decal is drawn and its stamp resolves, and that
every impassable water cell shows water at its centre in the live scene (a ray meets a water body above the terrain, or a drawn stamp
covers it). It takes screenshots over the largest lake, a river and a filled coast cell. Its "stamp material drew" test reads the dressing
record's `target.materials` (what the engine target drew), so it passes with `mnbl_water_surface` or, until that is published, its
fallback; the row records `drawnWith` and the fallback count. MMR-247 (acc5 c24): a River cell counts as drawn when a river stamp covers its
centre or the centre's ray meets a shown plane (the offline rule above; `cells.riversUnderPlane`; B' cell (16, 65) lies under lake 51's
plane, which the strips are clipped over and no patch is made for), and a lake ray that meets a mesh above the planes (a ruin marker, a
bridge) is cast on from just under it, at most 3 times (`bodies.raysThroughMeshes`).

## Seam

Every object is drawn at its image nearest the camera (`PeriodicMapCameraView.Nearest`). After a camera wrap or a long move
(`PeriodicMapCameraView`, beside `PeriodicMapPresentation.RefreshImages`) the installer takes each object whose image changed to it:
decals move in place (`Decal.Frame`); bridges and ruin markers are removed and instantiated again at the new image, every removal of a
refresh before its first creation (MMR-247 X, `EntityImageMode.Recreate`).

Why not move them: a prefab entity can carry an occlusion body (`occlusion_body_name`; two of the three ruin pieces do,
`ruinous_battania_castle_tower_a` and `ruinous_battania_castle_wall_a`; the bridges and `ruinous_castle_building_a` do not), which gives it
an occluder record in the scene's software occlusion rasterizer. `Scene.CheckResources` rebuilds that rasterizer without re-creating the
records entities hold, and `MapScreen` calls it every time its scene view is enabled again (the first time after a load, after a
conversation, a mission or another screen). Moving an entity makes the next scene tick write its frame through its record; through a stale
one that writes freed memory, and after a mission it faulted (acc4, acc5, acc5rep: `c0000005` at `TaleWorlds.Native.dll+0x55e625`). A new
entity takes its record from the current rasterizer: each is instantiated at its frame (`GameEntity.Instantiate(scene, prefab, frame)`,
whose native create writes the frame and the record together; no later `SetGlobalFrame` waits for a scene tick), and `PeriodicMapCameraView`
refreshes only while `MapScreen`'s scene view is enabled (nothing is drawn while it is not; `MapScreen.BeforeTick` enables it, with the
`CheckResources`, before it ticks the camera), so a creation never precedes the rebuild of its frame. Vanilla's campaign map never moves an entity with an occlusion body (no map icon
prefab has one). Details and the native evidence: `C:\Users\davgo\m2\results\MMR-247-X.md`. `EntityImageMode.Move` (the pre-fix move) and
`Frozen` (no move) exist for the hang repro's bisect only.

## Counts (A' seed 42 1db4cf74..., B' seed 73 59e8487d...; `evidence/world/map-dressing.json`)

| | A' | B' |
|---|---|---|
| Road pieces (gate / bank ends) | 255 (333 / 18) | 398 (512 / 52) |
| Road decals (battania / empire / khuzait) | 2,234 (956 / 846 / 432) | 2,929 (1,711 / 961 / 257) |
| Spacing used (sampled / drawn length) | 1.4 (4,007 / 3,128) | 1.969 (7,645 / 5,907: stretched to the 3,000 cap by the drawn length; by the sampled length it was 2.55, 2,227 decals) |
| Dropped: water (of which under a plane, MMR-247) / overlap / gates | 59 (38) / 357 / 85 | 140 (42) / 525 / 106 |
| Decks / bridge pieces | 2 / 12 | 17 / 102 |
| Ruins / markers (snapped) | 63 / 17 (2) | 99 / 47 (12) |
| Ruins skipped | 45 near a settlement, 1 shared site | 52 near a settlement |

The decal total per world stays at or under 3,000, all of it drawn (the in-view share is smaller), under MMR-249's 5,000 in-view
budget, which the magic overlay shares. With the magic ground veins (drawn with the water layer) and MMR-290's magic water
(`docs/magic-water.md`), the whole-world totals are A' 5,060 and B' 7,373 (both past 5,000 whole-world), and the most in a closest-camera
58 x 37 window 183 and 148 (116 x 74: 529 and 510).

## Not measured offline

The look in game: the decal width and length, the bridge piece scale on 6.25-unit decks and the ruin scale (0.05). Also whether the path
materials draw in the generated scene's decal atlas, the hint, and the encyclopedia page. For water (MMR-247's next live run): the
published `mnbl_water_surface` in the generated scene's decal atlas (V direction, gloss, ripples), the fallback's look until then, the
box clip against the live water shader (no stamp colour on the sea or lake surface), whether pools on lake fill cells beside a lake
plane's straight box edge read as water (their box floods only the cell's low ground, so on rising ground they paint thin bands), and the
frame cost of road and water decals together.
