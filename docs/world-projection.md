# Campaign world projection (MMR-25, 2026-10-14)

**The complete generated globe is one flat campaign map whose east and west edges are the same seam.** One versioned object maps every exported position and terrain sample: `worldbridge::projection::Projection` (`src/WorldBridge/src/projection.cpp`). Its resolved document, `projection.json`, is canonical JSON. The document's SHA-256 is the projection identity, and every export file carries it.

| | |
|---|---|
| Policy | `worldgen/policies/projection.json` (`mnbl.projection-policy` v1) |
| Resolved document | `projection.json`, schema `worldgen/contracts/projection.schema.json` (`mnbl.projection/1`) |
| Code | `src/WorldBridge/src/projection.cpp`, `src/source_nodes.cpp`, `src/projected_world.cpp`; CLI `worldbridge --project SPLIT --contract FILE --policy FILE --out NEW_DIR` |
| Calibration | `worldgen/calibration/axis-sea-datum/calibration.json`. The policy pins it by SHA-256 with a path relative to the policy file; `load_policy` refuses a missing or altered receipt |
| Tests | `src/WorldBridge/tests/projection_tests.cpp` (CTest `projection`, 104 checks), plus 9 new reader cases in `source_reader_tests` (129 checks). Logs: `evidence/fwg/projection-ctest.log`, `evidence/fwg/projection-tests.txt` |
| Real exports | `evidence/fwg/projection-export.json` (`tools/summarize_projection.py`): independent Python recomputation of the pinned size-129 and size-33 worlds, all 17 checks per world passed, repeat export byte-identical |
| Review | `evidence/fwg/projection-review.md`: the independent review and the disposition of each finding |

## Mapping: periodic-equirectangular, version 1

- **x** = W · frac((longitude − centralMeridian)/360 + ½). Canonical x lies in [0, W) and wraps every W. W = 800 world units.
- **y** = H · (90 − latitude)/180, with y = 0 at the north pole line and y = H at the south pole line. H = W/2.
- **z** = (radial height − `effective_config.sea_level`) × 0.01.
- **Source samples:** column c and row r of an n×n layer map to x = W · frac(c/(n−1) − centralMeridian/360) and y = H · r/(n−1).
- **Inverse:** longitude = centralMeridian − 180 + 360 · x/W, normalized to [−180, 180), and latitude = 90 − 180 · y/H.
- **Refused:** non-finite values, coordinates outside the globe, and a point exactly at a pole (singular: a pole has no longitude).

| Property | Value | Why |
|---|---|---|
| Central meridian | 0°, so the seam is the source's duplicate ±180° column at x = 0 = W | The seam has no gameplay effect in the periodic layout (R10). Placing it on the duplicate column means source columns map straight to x |
| Axes | +X east (source columns), +Y south (source rows), origin (0, 0), +Z up | Taken from the accepted MMR-16 calibration (raster column = +X, row = +Y, corner origin). (east, south, up) is a reflection of the geographic (east, north, up) frame, so a vector's north component maps to −y |
| Horizontal scale | W / (2πR) world units per metre. For the pinned 200 km globe (R = 31,831 m) that is 0.004 on the equator and on every meridian | H = W/2 keeps meridians and the equator true. A source cell is 6.25 × 3.125 world units at n = 129, because the source grid has twice the angular step in longitude |
| Distortion | east–west scale × 1/cos(latitude): 1.155 at 30°, 2.0 at 60°, 5.76 at 80°, unbounded on the pole lines | Unavoidable for a cylindrical full-globe map. Gameplay acceptance or a different projection is MMR-64's |
| Vertical | 0.005 world units per metre above sea level since map epic 2 (MMR-248, a starting value MMR-278 finalizes; was 0.01, a 2.5× exaggeration at the pinned size) | A convention, not a measured constant: the MMR-16 generated-seed runs and every campaign fixture used 0.01 (the calibration surface itself used 1). Lowered toward true proportions (findings section 15) with `playability.json` mountainSlope and `terrain-materials.json` slopeRock scaled by the same factor (`docs/biome-presentation.md`) |
| Coverage | the full globe: longitude [−180, 180], latitude [90, −90] | Regional crops are not this contract |

**Difference from the R10 fixture.** The MMR-63 fixture mapped one source cell to one square terrain cell, which stretched north–south distances 2× (H = W). Version 1 uses H = W/2. Only scale changes; the topology R10 proved does not.

## Seam, poles and the periodic layout (R10)

- **Seam.**
  - Logical x wraps every W. The physical terrain is x + strip, with strip = W/2 on each side, so the physical width is 2W and canonical positions lie in [W/2, 3W/2).
  - These are MMR-63's values (run 12348). The policy refuses a strip narrower than W/2, because R10 needs every shortest seam-crossing route to have its image inside the terrain.
  - Campaign objects are canonical. Strip content is display only.
- **Duplicate seam column.** Source column n−1 is bit-identical to column 0 for any central meridian (tested at 0, −10, 22.5, 77.7 and −179). Graph nodes never include it.
- **Poles.**
  - Each pole is one source node, and on the map it is a line: y = 0 (north) or y = H (south).
  - Terrain pole rows are lines of one value. Objects on a pole node are refused. A path through a pole is split there (below).
  - Rows beyond each pole line are padding that repeats the pole row. North and south are never connected.
  - The pole margin is 3W/4 (600), so the physical terrain is a 1600 × 1600 square, the size of the R10 fixture's terrain. MMR-26 changed it from W/2 for the square-only surface encoder.
- **Sea continuity** (also in `projection.json` `seaContinuity`).
  - The seam column equals column 0 in every layer, so heights and water classes agree across x = 0 = W.
  - The strips copy the other side, so a sea crossing the seam is one water body.
  - Each pole row is one value.
- **Navigation inputs.** For every sample the export carries:
  - a below-datum flag: height minus sea level is negative, a sign test;
  - the source `water_type` class, unchanged (0 land, 1 ocean, 2 lake).

  A below-datum lake therefore stays a lake. On the pinned worlds, below-datum and ocean agree on every sample (11,506 of 16,641 at size 129). The review found one below-datum lake sample in the size-257 world.

  Face classification is MMR-28's. MMR-17 classified each face by its main-diagonal centre height.

## Paths: roads and rivers

Roads (`roads.routes[].nodes`) and rivers (`water.river_segments`) are sphere-graph node paths. The reader now retains them.

- Every step must be a graph edge: the 8-neighbourhood with wrapped longitude, where a pole touches its whole ring. Anything else is refused.
- **Seam crossings are split.** A crossing step ends its piece on x = W (or 0) and the next piece starts on x = 0 (or W) at the same, interpolated y. A link "seam" records the continuation. No drawn step is longer than W/2, so a road is never drawn across the whole map.
- **Pole passages are split.** One piece ends on the pole line at its own x; the next starts on the pole line at the outgoing x. A link "pole" joins them.
- **Edge cases:** a path ending exactly on the seam column ends on x = W, the same point as x = 0. A path starting there continues from the far edge with no zero-length piece.
- **The pinned worlds have no seam or pole steps:** size 129 has 34 roads and 53 rivers, size 33 has 17 and 20. The unit tests cover every split case.

## Source graph nodes

`worldbridge::source::NodeGrid` decodes node indices separately from samples. The rules match `SphereGrid::node_index` in the generator:
- node 0 is the north pole;
- then the distinct cells follow row by row (rows 1…n−2, columns 0…n−2);
- the last node, 1 + (n−2)(n−1), is the south pole.

A record's (node, x, z) must agree. All 45 settlements, 221 hamlets and 26 castles of the size-129 world agree, and so do those of the size-33 world. The reader-contract fixture uses a synthetic numbering, and the export refuses it (`node_mismatch`).

## Export (one identity everywhere)

`worldbridge --project` writes a new directory (atomic rename) containing:

| File | Contents |
|---|---|
| `projection.json` | the resolved document (SHA-256 = identity) |
| `terrain.bin` | little-endian: `MNBLPT01`, u32 side, the 64-character identity, side² binary64 z, side² below-datum bytes, side² `water_type` bytes |
| `terrain.json` | the layout of `terrain.bin`, its hash, the z range, the below-datum and water-class counts, any disagreement between the two, and the identity |
| `objects.json` | settlements, hamlets (with their port sea cell), castles, key locations, roads and rivers, each with logical and physical positions, plus the identity |
| `manifest.json` | every file's hash, the identity and the policy hash |

What was checked independently:
- For both pinned worlds, Python recomputed every z, below-datum flag, water class and object position from `world.json` and compared them with the C++ output (exact; the worst z error was 0).
- The identity is the same in all four files.
- A repeated export is byte-identical.

The consumers are the compilers MMR-26 (terrain), MMR-27 (settlements) and MMR-28 (land, sea and ports).

**What the identity covers.**
- The policy hash covers only the mapping members; notes are not hashed, and a `Projection` re-validates and re-hashes the policy it is given.
- Distortion is recorded as a formula, so no transcendental result can change the identity between C runtimes.

## Sea datum: what was fixed and how

**Fixed by the MMR-16 receipts:** axes, origin and raster orientation.

**Not receipt values:**
- The height scale is the convention MMR-16's generated-seed runs used.
- The horizontal scale is this policy's choice. MMR-16 fixes square encoder cells, while a source cell here is 6.25 × 3.125, so MMR-26 resamples the source onto the encoder raster through `Projection::inverse`.

**Sea level:** terrain and navigation keep it at z = 0. No receipt fixed where the engine draws water, so one calibration run was made for that alone: `generated_campaign-4768`, world `mnbl_00059042`, `Diagnostics/SeaDatumProbe.cs`.

| Observed (24 × 24 grid of cell centres) | Value |
|---|---|
| `Scene.GetWaterLevel()`; `GetWaterLevelAtPosition` with the water renderer and with water bodies, at all 576 samples | −100: the scene's `water_level`, with `water_exists=false` |
| `medit_water_plane` entity | absent |
| Terrain z by face: open sea (256 samples) / coastal sea (159) / land (25, all Mountain faces) / no face (136) | −33.03…−0.90 / −32.66…6.38 / 4.37…63.75 / −0.50…24.91 |
| Face lookup | `GetFaceIndex(new CampaignVec2(p, false))`, the sea-flagged position. That is the likely reason low land near the coast returned no face (not separately verified). The grouping does not affect the water-level reading |

The engine has **no water surface at the sea datum** on generated maps. The water visible in screenshots does not come from the scene water renderer, and this run did not identify its source.

For comparison, the official War Sails `Main_map` also sets `water_exists=false` and supplies its sea through scene entities: a `global_water` entity at z 1.8, plus local `water_plane` entities. Placing and verifying a sea surface at z = 0 belongs to MMR-39.

## Limits and open items

- **Terrain size.** The physical domain is 2W × (H + 2·margin) = 1600 × 1600 world units with margin 3W/4, a square for the square-only surface encoder (`surface.cpp`).
  - The polar padding repeats the pole row: ocean for an ocean pole (as in the pinned worlds), land otherwise.
  - It costs navigation-cache time (MMR-41).
- **Sea surface.** Rendering one at the datum is not provided on generated maps (MMR-39).
- **Polar distortion** is recorded, not solved (MMR-64).
- **Seam placement** is fixed at the source seam. Choosing a seam over ocean would need a policy change and gives no gameplay benefit in the periodic layout.
- **No in-game consumer yet.** The campaign fixtures (`tools/scenebin`) predate this object; MMR-26/27/28 move their placement onto it.
