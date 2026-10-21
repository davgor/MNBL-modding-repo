# Overlay meshes: veins, rivers and magic water (MMR-291), 2026-10-21

acc5 and the decal lab (acc5lab) proved that the campaign map draws scene decals only from Native's prebaked world-map decal atlas
(`decal_atlas_worldmap`, laid out by Native's `decal_textures_worldmap.xml`, which the engine reads from Native alone): the module's vein
atlases, `mnbl_water_surface` and `mnbl_magicwater` resolve and draw nothing as decals, and no decal at all draws on our generated sea (R1
negative). Owner decision (2026-10-21): draw veins, rivers and magic water as runtime meshes on separate entities. Nothing here has been seen in
game; the decal lab's mesh grid confirms the route in the next live launch and MMR-247 grades it (c24, c29, c31, c33).

Code: `World/OverlayMeshes.cs` (pure: the builder, the river surfaces, the installer), `World/OverlayMeshRuntime.cs` (engine side: an
`IMagicVeinLayer` and `IMagicWaterVeins` of `MagicGroundRuntime`, and the rivers' install from `MapDressingRuntime`). Policy: `roads.json`
`overlayMeshes` (`RoadsPolicy.OverlayPolicy`, strict). Tests: `tools/Test-OverlayMeshes.ps1 -BundleA -BundleB [-Evidence] [-Dump DIR]`
(`tests/World/OverlayMeshTests.cs`); evidence `evidence/world/overlay-meshes.json`; preview `tools/render_overlay_meshes.py`. The route with its
IL, material and shader evidence: `C:\Users\davgo\m2\results\MMR-291-route.md` (scripts and extracts in `C:\Users\davgo\m2\results\mmr291\route\`).

## Route (static evidence)

- **Runtime meshes** (TaleWorlds.Engine): `Mesh.CreateMeshWithMaterial(material)`, `LockEditDataWrite()`, `AddFaceCorner(position, normal, uv,
  colour, lock)` per vertex (the uint is the vertex colour), `AddFace(a, b, c, lock)` per triangle, `ComputeNormals`, `ComputeTangents`,
  `RecomputeBoundingBox`, `UnlockEditDataWrite` (the order of TaleWorlds' `BoundaryWallView.CreateBoundaryMesh`); `Mesh.Color` (the shader's
  factor colour), `SetVectorArgument`, `CullingMode`, `VisibilityMask`; one `GameEntity.CreateEmpty` root per mesh with `AddMesh(mesh, true)`,
  `SetGlobalFrame`, `SetReadyToRender`; `Scene.RemoveEntity` on the live scene.
- **Materials**, by layer:
  - veins: a copy of vanilla `torch_flame_base` (`pbr_shading`, alpha blend `modulate`, `dont_draw_to_gbuffer`, `no_modify_depth_buffer`,
    `two_sided`; shader flags `use_vertex_colors`, `self_illumination`) with the published `mnbl_magic_<look>_d` as DiffuseMap and DiffuseMap2
    (the self-illumination's colour), `_n` and `_s`: SandBox.View's `MobilePartyVisual` makes its campaign-map banner materials the same way
    (`Material.CreateCopy`, `SetTexture`). A published `mnbl_overlay_vein_<look>` is taken by name first (none is published: publish request P
    in the route file is the fallback if the copies draw nothing).
  - magic water: the same template with `mnbl_magicwater_d` / `_n` (one material, the school in the atlas row; `mnbl_overlay_magicwater` first).
  - rivers: `naval_worldmap_water` as it is (the material of our sea and lake planes, `water_surface_worldmap`), then `worldmap_water`.
- **Why it draws** (shader sources): forward `pbr_shading` takes alpha from the factor colour, the vertex colour and the diffuse alpha and blends
  it `modulate`; `self_illumination` adds DiffuseMap2 x factor x vertex colour x the mesh's vector argument w (light independent of the time of
  day). `water_surface_worldmap` takes its surface from the mesh's own vertices and colours it by the depth to the terrain in the depth buffer
  (water writes no depth), so a ribbon whose edges meet the ground fades into its banks.
- **No new texture and no publish** for the first live run: every texture is in the published `AssetPackages/pack0.tpac`.
- **Default taken: stamps, not a continuous strip, for the veins.** The vein atlases are laid out for chained stamps (a trunk is periodic over
  0.75 of its cell but feathered over its first and last 20 %), so a continuous UV strip would kink or need a new strip texture. A quad per drawn
  stamp along the graph's edges keeps MMR-288's art and tuning (widths thinning to the tips, fitted forks, a burst per source).

## Geometry (`OverlayMeshes`)

- **Veins**: every drawn land stamp of MMR-288's plan (`MagicGroundPlan.Stamps`, the same ids, boxes, yaws, alphas and reveal times as the
  decals) as a quad grid of its box (L along the edge, W across) on its atlas cell (u from the back to the front of the box, v across; the
  rect is the decal's `SetVectorArgument` cell; `flipV` mirrors the texture rows); a burst as a disc on the cell's inscribed circle (its radial
  art). Vertex colour white with the stamp's alpha (its fade). Magic water's inland and shore-glow stamps (the look's ground art) join the vein
  meshes. Cells of a vein quad whose four corners lie over a shown water plane are left out (the magic water layer draws there).
- **Magic water**: every other MMR-290 water stamp as a quad of its `mnbl_magicwater` cell (4 x 8): sea and lake stamps flat on their plane
  (sea z 0, a lake's z) plus `magicWater.lift`; bed and coast stamps draped. The land around a flat quad is higher and hides it (depth test).
- **Rivers**: MMR-247-W's smoothed river chains (`MapDressingPlan.RiverLines`, new) as ribbons `rivers.width` wide, a cross-section every
  `segment.max` units: five vertices across, the inner three on a level surface at the centre's ground + `rivers.depth` (raised to a shown
  plane's z + `rivers.lift` where one covers the centre, so a mouth joins the sea or lake surface), the edges at min(level, ground - `rivers.dip`):
  the water shader's depth fade blends the banks. Pools (lone river cells and the centres the strips miss; `rivers.poolScale` x the half width)
  and fills (`rivers.fills`, the cell size) as discs on the same rule. The water decals these replace are not drawn.
- **Draping and the error bound**: a draped vertex stands at the ground (`MapDressing.GroundOf`, the encoded terrain) plus the layer's lift.
  Inside every grid cell the ground is sampled at 16 points (4 per disc triangle); while it rises over the mesh by more than
  `segment.maxError` the piece's grid is halved, down to `segment.min`. Where a single sharp heightfield crest still rises over a cell at that
  size, the cell's corners are raised by that rise: the mesh then never lies under the sampled ground by more than `segment.maxError` (0.05,
  under the vein lift 0.08), and floats over such crests instead (recorded). Pieces are not raised onto water planes or river surfaces: the
  water paints over what lies under it (acc6lab; only its shallow refraction shows the ground), and magic water lies on the planes in its own
  pass ("Over the water").
- **Chunks and seam images**: a piece goes to the chunk (`chunk` x `chunk` units of the canonical frame) of its centre, its vertices relative to
  the chunk's centre with the shortest periodic x offset (a piece across the seam stays whole). One mesh per (layer, material, chunk, reveal
  band); the runtime frames each at the image of its centre nearest the camera and moves whole chunks on a seam refresh (seam `chunkImage`).
- **Live casts**: a live stamp's piece goes to the mesh of its reveal band (`RevealAt`), so a cast's veins appear outward by Order as the
  decals did; a reveal step adds only its band's meshes; a cast re-plans the graph and the installer recreates only the chunk meshes whose
  geometry changed (Nerdun: 8 of 161 on A').

## Runtime (`OverlayMeshRuntime`)

- **Rivers** follow the map dressing: `MapDressingRuntime.Install` leaves the replaced water decals out of its installer
  (`MapDressingInstaller.SkipWater`, `waterReplacedByMeshes` in its record) and calls `InstallRivers`; `RefreshRivers` on a seam refresh;
  `ReleaseRivers(true)` on a re-install on the live scene, `ReleaseRivers(false)` after `Scene.ClearAll` (MMR-262: handles only).
- **Veins and magic water** are the third vein layer of `MagicGroundRuntime.Layers` (after the features and the magic water decals): `Install`
  at the map's AfterLoad (the plan of the loaded graph, so a Continue rebuilds a cast from its stored state, revealed by the campaign time),
  `Changed` at a live cast's safe point and at each reveal step, `RefreshImages`, `Release(true/false)`. It declares `IMagicWaterVeins` (the
  ground plans with the water veins drawn). While it draws the veins, `MagicGroundRuntime` syncs an empty decal plan (`WithoutStamps`); while it
  draws the magic water, `MagicWaterRuntime` draws no decals and its `Drawn`, `SetVisible` (MMR-247's toggle) and `Record` speak for the meshes.
- **Ready**: a layer is drawn as meshes when its mode is `mesh` and its materials can resolve (a published material, else the template and the
  diffuse texture; rivers: one of the materials); otherwise its decals stay. What resolves and draws nothing is the lab's to show.
- **Records** (`OverlayMeshRuntime.Record()`, in the acceptance probe's audit `overlayMeshes`, the dressing record's `riverMeshes`, the magic
  water record's `overlayMeshes`, the decal lab's row): policy, readiness, per part the plan totals, the follow error, the installed entities
  and pieces per layer, the materials (published or copy, template, textures, shader, blend), timings and errors.

## Over the water (MMR-291-W)

acc6lab (decal lab, world 1): on land the vein and magic water copies drew; on the sea they drew nothing, not even flat on the sea plane plus
the lift, while `naval_worldmap_water` ribbons on the seabed drew over the sea. Static evidence (C:\Users\davgo\m2\results\MMR-291-W.md):

- The engine names its queues (`TaleWorlds.Native.dll` strings): `Opaque Forward Meshes`, `Alpha Blended Forward Meshes`, `Forward Decal Meshes`,
  ..., `Postfx`, `Rendered After Postfx Meshes` in the main sequence; `handle_refraction`, `Render Water Mesh`, `water_refraction_rt_pre`,
  `depth_target_with_water`, `Gpu particles render`, `Depth Reading Alpha Blended Forward Meshes` in the water's own.
- `water_surface_worldmap` is opaque (`no_alpha_blend`), tests depth (`[earlydepthstencil]`) and writes none; its colour is the refraction copy
  taken before it, faded to the deep colour by the depth to whatever the depth buffer holds (l. 199-208, 457-469).
- So a blended mesh drawn before the water and writing no depth (the template: `no_modify_depth_buffer`) is painted over: at sea the depth
  buffer holds the seabed 6-14 units down, and the refraction of it is gone past half a unit. What draws over our sea: meshes of the water
  material (the water's own queue), GPU particles (War Sails' ship trails and storms) and the depth reading queue after it, every opaque mesh
  (ships, icons: their depth keeps the water off), and the crosshair (`render_after_postfx`).

`overlayMeshes.<layer>.pass` sets how a layer's material (copy or published) draws against the water (`OverlayPass`):

| pass | material change | where it draws |
|---|---|---|
| `blend` | none (MMR-291) | land; under the water, never |
| `depth` (magic water's default) | `no_modify_depth_buffer` cleared, `pbr_shading`'s `alpha_test` at `overlayMeshes.alphaTest` (0.3) | its texels past the test write depth, so the water drawn after them keeps off; the rest stays water |
| `forward` | + `needs_forward_rendering`, `alpha_blend_sort` (Native's `worldmap_cloud`, SandBox Main_map's `cloud_plane` at z 40) | unproven: no evidence ties these flags to a queue after the water |
| `afterPostfx` | + `render_after_postfx` (`crosshair_material`'s) | after the water and the post effects (no tone mapping, fog or bloom on it) |

Defaults taken: magic water `depth` (it lies on the planes), the veins `blend` (they lie on land; their wet cells are left out). `alphaTest` 0.3
keeps the art's cores (alpha 0.5 to 1; 14 % of a trunk cell) and drops the halos (at most 0.25): over the sea magic water draws as its cores, and
an edge texel blends with the lit seabed under it (the water is off there). The alpha test is a runtime shader flag on the copy (a new permutation,
as `TaleWorlds.MountAndBlade.View` adds `use_double_colormap_with_mask_texture` to material copies); its value's scale (0-1 assumed) and the
flag bits are recorded in game (`meshMaterials`, the lab's `passReferences`). **lab291w** (decal lab on MMR-291-W, sea spot): `blend` and
`forward` drew nothing over the sea, `depth` and `afterPostfx` drew (magic water meanDiff 45.2 and 32.2); the alpha test scale is 0-1 (`beards_h`
0.627) and the `depth` copy applied without `passError` (`C:\Users\davgo\m2\t291-W\build\acceptance\lab291w\decal-lab\decal-lab.json`).

## Policy (`roads.json overlayMeshes`)

`enabled`; `chunk` 64; `segment` {`max` 1.0, `min` 0.25, `maxError` 0.05}; `budget` {`vertices` 800,000, `triangles` 1,200,000, `entities`
2,500, `view` {`width` 116, `height` 74, `entities` 200, `triangles` 250,000}, `entityMicroseconds` 0.36}; `flipV` false; `alphaTest` 0.3
(MMR-291-W); `veins` {`mode` mesh, `material` `mnbl_overlay_vein_{look}`, `template` `torch_flame_base`, `textures` `mnbl_magic_{look}`, `lift`
0.08, `pass` blend, `burst` {segments 16, rings 3}, `glow` per visible school}; `magicWater` {`mode` mesh, `material` `mnbl_overlay_magicwater`,
`template` `torch_flame_base`, `textures` `mnbl_magicwater`, `lift` 0.04, `pass` depth, `glow` 1.0}; `rivers` {`mode` mesh, `materials` [`naval_worldmap_water`, `worldmap_water`], `width` 4.0,
`depth` 0.4, `dip` 0.15, `lift` 0.03, `poolScale` 1.25, `fills` true, `discSegments` 12}; `seam` chunkImage. The reader refuses unknown members,
a mode other than mesh or decal, a vein material named as a ground decal material, a glow missing a visible school, a vein lift within the
draping tolerance, a segment over a quarter chunk, rivers without a material, a pass other than blend, depth, forward or afterPostfx, an
`alphaTest` outside (0, 1). **The switch back**: a layer's `mode` `decal` (or `enabled`
false) draws that layer's decals as before (MMR-247 D's tinted vanilla fallbacks, MMR-290's magic water decals and its F1/F2 modes). Defaults
taken (look and cost choices from offline previews; MMR-247 judges them): the template, the glows, the lifts, river width 4 (the decals' 6 was
a cell), depth 0.4, the chunk size, fills meshed.

## Numbers (A' 1db4cf74, B' 59e8487d; `evidence/world/overlay-meshes.json`)

| | A' (seed 42) | B' (seed 73) |
|---|---|---|
| Veins: pieces / entities / vertices / triangles | 775 / 80 / 74,514 / 123,910 | 2,016 / 142 / 160,385 / 261,268 |
| Magic water | 1,708 / 75 / 46,501 / 74,594 | 1,466 / 74 / 86,956 / 147,600 |
| Rivers (chains + pools + fills; pieces per chunk) | 23 + 8 + 193; 230 / 54 / 6,610 / 9,532 | 92 + 32 + 368; 505 / 74 / 16,150 / 23,384 |
| Whole world: entities / vertices / triangles | 209 / 127,625 / 208,036 | 290 / 263,491 / 432,252 |
| Busiest 116 x 74 view: entities / triangles (58 x 37) | 27 / 38,277 (14 / 23,646) | 28 / 52,213 (15 / 25,994) |
| Main-thread estimate in that view (entities x 0.36 us) | 0.010 ms | 0.010 ms |
| Veins follow: max rise / max float; refined / raised pieces (max raise) | 0.05 / 0.19; 132 / 28 (0.18) | 0.05 / 0.83; 191 / 45 (0.79) |
| Magic water bed and coast follow | 0.05 / 0.14; 76 / 14 (0.14) | 0.05 / 1.51; 155 / 39 (1.51) |
| Vein cells left out over shown water (pieces affected) | 31 | 52 |
| River cells covered by a ribbon or disc (on the level surface) / under a plane | 37 of 37 (37) / 0 | 145 of 145 (139) / 6 |
| Plan time offline: veins and magic water / rivers | 400 ms / 6 ms | 926 ms / 7.5 ms |

The largest floats (B' 0.8 and 1.5 units) are cells raised over single steep heightfield crests (one sample standing out). The Nerdun cast
(A'): 10 live pieces in 6 band meshes, shown 1 / 1 / 5 / 9 / 10 at 0 / 12 / 24 / 36 / 48 h after its batch day; 3 reveal steps each add only
their band; the cast recreates 8 of 161 chunk meshes. The meshes replace decals: of the whole-world decal totals in `docs/magic-water.md` (A'
5,060, B' 7,373) only the road decals stay (A' 2,234, B' 2,929; every water decal is replaced while fills are meshed).

## Offline tests and preview

`Test-OverlayMeshes` builds the plans as the runtime does and checks one piece per drawn stamp, chain and disc, determinism (two builds, equal
digests), every triangle inside one atlas cell, the chunk frame (pieces across the seam whole), the follow error, river levels and dipped edges
over every river cell centre, the budgets (whole world and the busiest view window), the Nerdun cast's reveal by band (a step adds only its
band, a Continue draws it grown, the cast recreates only changed chunks), the installer over a recording target (one entity per mesh at the
image nearest the camera, a move across the seam, the magic water toggle, removal, the handle drop after `Scene.ClearAll` with no engine call,
nothing drawn and nothing thrown without materials), the decal switch and the runtime wiring (source checks). `-Dump DIR` writes
`DIR/<A|B|A-nerdun>/overlay.json` (and `river.json`: the longest river) for `tools/render_overlay_meshes.py`, which draws the meshes triangle by
triangle (UVs onto the atlases, vertex alpha, glow; rivers by depth) over a plain base map. Previews: `C:\Users\davgo\m2\results\mmr291\`.

## Decal lab mesh grid

`docs/map-epic-2-acceptance.md` "Decal lab": after each spot's decal grid, columns `mesh:vein`, `mesh:river`, `mesh:magicWater`, shot as
`lab-<spot>-mesh-<view>` and read by `tools/decal_lab_sheet.py` as spots `land-mesh` and `sea-mesh`. MMR-291's rows (full, cell, factor, plane,
large) ran in acc6lab; MMR-291-W's rows are the passes: `blend`, `depth`, `under` (depth, 0.05 under the surface), `forward`, `afterPostfx`, the
vein and magic water cell pieces on the spot's surface (the sea datum at the sea spot, where the camera aims), the river ribbon the control.

## Not verified (MMR-247)

- acc6lab settled M1 (the copies draw on land) and M2 against the blend (nothing over the sea); lab291w settled the `depth` pass (it draws
  magic water over the sea; alpha test scale 0-1). Not verified: the look of the cores over the sea in a world shot (fringes against the
  seabed), the self-illumination strength (glows) and the vein look under the map's lighting.
- The rendered terrain's level of detail at distance against the encoded heights the meshes follow (M3).
- GPU cost of the triangles (the main-thread estimate counts entities only) and the install and cast-time cost in the engine (offline plan
  time is recorded; mesh creation is about one interop call per vertex and per triangle).
