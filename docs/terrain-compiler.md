# Campaign terrain compiler (MMR-26, 2026-10-14)

`worldbridge --compile-terrain` bakes a split world's layers into the square terrain that the proven surface encoder imports. It goes through the accepted projection (MMR-25, `docs/world-projection.md`).

| | |
|---|---|
| Code | `src/WorldBridge/src/terrain_compiler.cpp`, `src/material_compiler.cpp`, `src/terrain_layers.cpp` (the layer compile and map trees, MMR-255); the encoder takes `--surface-weights` (weights.bin v2, required since MMR-255) and `--surface-flora` (`surface.cpp`) |
| Engine format (calibration output, not hard-coded) | `worldgen/calibration/terrain-import/terrain-import.json`. The compiler reads every value from it:
  - 4 × 4 nodes, square terrain, 129 or 257 vertices;
  - the extent range and node-size precision;
  - 16-bit heights with a range rounded to 0.001;
  - the 0.03 engine vertex truncation;
  - the 16 weight slots (the template's layers are `terrain-materials.json` layerSet, MMR-255).

  Cited from the MMR-16 receipts and encoder; no new engine calibration |
| Material policy | `worldgen/policies/terrain-materials.json` version 2 (map epic 2, MMR-248): every exported biome (13), water class (3), river code (2) and landform (4) is listed, and each biome is a row of the shared biome table (`docs/biome-presentation.md`). Anything else fails |
| Output contract | `worldgen/contracts/terrain-metadata.schema.json` (`terrain.json`) |
| Tests | `src/WorldBridge/tests/terrain_resampling_tests.cpp` (CTest `terrain`), plus taxonomy reader cases in `source_reader_tests`. Logs: `evidence/fwg/terrain-tests.txt`, `evidence/fwg/terrain-ctest.log` |
| Review | `evidence/fwg/terrain-review.md`: the independent review and the disposition of each finding |
| Real compiles | `evidence/fwg/terrain-compile.json` (`tools/summarize_terrain.py`) covers source sizes 129, 257, 513 and 1025. For the size-129 world the Python verifier independently:
  - re-derived every output sample and every fidelity metric;
  - decoded the encoded `terrain.bin` and compared all 16 nodes with the compiled rasters, shared borders included.

  All checks passed |
| Visual probe | `evidence/runtime/in-game/native_render-4988*`: the compiled size-129 terrain encoded with `tools/encode_compiled_terrain.py`, then rendered in the engine from five camera views |

```
worldbridge --compile-terrain SPLIT --contract worldgen/contracts/source-compatibility.json \
  --projection-policy worldgen/policies/projection.json --materials worldgen/policies/terrain-materials.json \
  --import worldgen/calibration/terrain-import/terrain-import.json --side 257 --out NEW_DIR
```

## What each output sample is

Output sample (row r, column c) sits at physical (c, r) × spacing, where spacing = extent / (side − 1). The physical domain is the projection's: logical position plus (strip W/2, pole margin 3W/4). It is 1600 × 1600, so spacing is 6.25 at 257 vertices. The source position of each sample is the projection's inverse. Rows beyond a pole line repeat the pole row, and x wraps every W.

| Layer | Sampling |
|---|---|
| Height | **seam-aware bilinear.** Column n−1 is the source's duplicate of column 0, so cells across the seam interpolate correctly. z = (height − sea level) × verticalScale (0.005 since map epic 2; 0.01 before) |
| Biome, water class, river, landform | **nearest sample**, with halves rounding up and column n−1 read as column 0. Codes are copied and never mixed. A raster biome id missing from the exported `terrain.biomes` catalogue is refused, even if the policy lists it. Version 1 ignores `layers.biome_variant` (magical variants) |
| Intent (navigation input) | land 0, ocean 1, lake 2, river 3, from `water_type` and `river` through the policy. A river overrides land; a lake stays a lake |
| Coastline coherence | ocean ≤ −0.05, land and river ≥ +0.05; lakes stay as compiled, and lakes below the datum are counted (`lakeBelowDatum`, 0 at every size). The compile is refused (`coast_sign`) if any coastline sign would not survive 16-bit quantization and the engine's 0.03 truncation toward zero |
| Climate (temperature, moisture, `ice_cap`, `rocky_coast`, snow months) | **nearest sample**, like the categories (MMR-255). Snow months count `seasonal_environment.months[k].snow` at or above `permanentWhite.snowThreshold` |
| Layer weights | the layer compile (MMR-255, "Layers" below): one alpha plane per layerSet slot, periodic in x so that seam pairs see the same neighbours |
| 16-bit height | one global range, hmin/hmax = floor/ceil(min/max × 1000)/1000. A zero range uses hmax = hmin + 1. q = round-half-even(clamp((z − hmin)/(hmax − hmin), 0, 1) × 65535), stored big-endian. The reconstruction error is ≤ half a step, checked. The encoder re-derives the same range and codes from `heightfield.bin`: the difference is exactly 0 |

**Checks made on every compile.**
- **Paired seam columns.** Column c and column c + W/spacing (128 at 257 vertices) must be identical in height, 16-bit code, biome, intent, landform and every layer plane: 33,153 pairs. Logical x comes from the canonical column, so the pairs share bits even when W is not a dyadic number (tested at W = 800.1).
- **Refusals.** Each of these refuses the compile: a non-square physical domain, a side or extent the calibration does not allow, a seam that falls between output columns, heights beyond the calibrated limit, and any unmapped category.
- **Node borders.** They are shared by construction: the encoder cuts each node from the one grid. The evidence checks them on the encoded file (1,560 border samples), together with the content of all 16 nodes.

**Outputs.**
| File | Contents |
|---|---|
| `heightfield.bin` | `MNBLHF01`; the encoder's input at scale 1 |
| `weights.bin` | `MNBLWT02` (MMR-255): u32 side, u32 layer count N, N × (u32 length, UTF-8 name) in slot order, then N planes of side² alpha bytes; plane 0 is 255. The encoder's `--surface-weights` |
| `height-q16.bin` | the 16-bit heights |
| `masks.bin` | biome, intent, landform and padding byte planes (padding is 1 on rows beyond a pole line) |
| `projection.json` | the resolved projection |
| `terrain.json` | metadata: identities, ranges, coherence, checks, fidelity, timings, and (MMR-255) `layers` (per slot, the share of canonical land where it is the dominant visible layer) and `permanentWhite` |

## Source size: frozen at 129 for this output

The travel compile (the navigation field) stays at 257 vertices across 1600 world units, the encoder's 64-cell nodes of MMR-16. Since MMR-278 the encoder also writes 128-cell nodes (513 vertices, level 7), which the shaped surface uses (see "Shaping"). A size-129 source has a 6.25 × 3.125 world-unit cell (W/(n−1) across, H/(n−1) along).

| Source n | Output samples per source cell (across, along) | Source samples unrepresented | Height loss at source samples, RMS / max (world units) | Coast mismatches | Land components lost (samples) | Compile wall time / peak working set |
|---|---|---|---|---|---|---|
| **129** | 1, 0.5 | 50.4 % (odd rows) | 0.86 / 12.9 | 2.3 % | **0 of 12** | 2.0 s / 10 MiB |
| 257 | 0.5, 0.25 | 87.6 % | 1.23 / 22.9 | 3.5 % | 3 of 15 (3) | 3.6 s / 12 MiB |
| 513 | 0.25, 0.125 | 96.9 % | 1.36 / 23.8 | 3.7 % | 5 of 19 (19) | 9.3 s / 21 MiB |
| 1025 | 0.125, 0.0625 | 99.2 % | 1.43 / 27.5 | 4.0 % | 11 of 33 (104) | 31.1 s / 61 MiB |

- **Size 129 is frozen for this output:** across, it maps one-to-one onto the output, it loses no land, and it compiles in about 2 s.
- **One world only.** These numbers come from one world (layout seed 1555514970434891308, the size-N exports of seed 42).
- **Why rows are skipped.** The 2:1 meridional decimation comes from heightRatio 0.5 (true-scale meridians) combined with the square encoder raster.
- **Larger sources add detail the output cannot show.** They only lose more of it: islands and ridges fall between output samples.
- **Output pixels add no source detail.** Between source samples the terrain is bilinear. At size 129 the odd source rows are skipped, because the meridional cell is half the output spacing; the RMS/max loss above measures that.
- **Limitation:** `layers.height` is the only full-map continuous height the producer exports (there is no public continuous-height CLI), and the 512 m patch API is not a campaign exporter.

## Visual probe (`native_render-4988`)

The compiled size-129 terrain was encoded (257 × 257, extent 1600, compiled rock weights), installed as `mnbl_terrain26_s129` and rendered in the engine from five views: an overview, two coastlines, the seam at x = 1200, and its strip copy at x = 400. The run status is ok and it rendered at the full extent.

What the screenshots show:
- **No node seams or tile discontinuities.**
- **The seam view and its strip copy have identical relief.** Pixels still differ, because the material textures tile with a different phase.
- **Biome rock weights are visible:** rocky sea floor and exposed rock against smoother grassland.
- **No water.** The generated scene has no water surface (MMR-25 sea-datum run), so the coastline shows only as relief. A sea surface is MMR-39's.
- **The square domain is 75 % polar padding.** Only the middle 400 of the 1600-unit rows is globe. This is navigation-cache cost: MMR-41 can tune the margin, or MMR-37 can make the encoder write rectangular terrain.
  - The padding repeats the pole row: ocean for an ocean pole, land for a land pole.
  - `terrain.json` records each pole's intent, and `masks.bin` flags the padding rows for MMR-28. Both poles are ocean in the compiled world.
- **Isolated peaks render as sharp spikes.** The 0.01 vertical scale is a 2.5× exaggeration against the 0.004 horizontal scale, at 6.25-unit spacing. The vertical scale is a projection-policy convention that can be revisited.

## Shaping (MMR-278, map epic 2)

The bundle compile shapes the campaign surface after travel placement, so settlement pads use the final positions (findings section 15).
Code: `src/WorldBridge/src/terrain_shaping.cpp` (`terrain::shape`, `include/worldbridge/terrain_shaping.hpp`), called by `bundle::build`
(`bundle_writer.cpp`); policy `worldgen/policies/terrain-shaping.json`; Python reference `tools/terrain_shaping.py`.

**Two fields.**
- **Navigation** keeps its own field: the travel compile at `playability.json grid.side` (257, 6.25 units), scaled and unshaped. The bundle
  publishes it as `compiled/terrain/navigation-heightfield.bin` and `navigation-masks.bin`, and `travel-graph.json` `inputs` name those two
  files. Travel classes therefore never see the shaping: on worlds A' and B' `cells.bin` is byte-identical to the pre-smoothing compile
  (verticalScale 0.01, mountainSlope 1.0, slopeRock 0.6/1.2).
- **The surface** is the shaped field at `resolution.fineSide` 513 (3.125 units; level 7, 4 x 4 nodes of 128 cells, MMR-249's go):
  `compiled/terrain/heightfield.bin`, `masks.bin`, `weights.bin` and `height-q16.bin`. Its masks are the terrain compile at side 513; their
  even samples are the travel compile's samples exactly, so the travel grid is the surface's every second sample.

**Steps.** The coarse steps run on the canonical periodic grid (one seam period of columns, wrapping), between the pole lines, in
`coherence.order`:

| Step | Rule |
|---|---|
| lakes | each 4-connected lake group at the runtime lake level (the GeneratedWaterSystem rule: largest source water_surface over the nearest source lake cells): bed samples at most level - `bedBelowLevel`, 8-adjacent land samples at least level + `shoreAboveLevel` |
| coast | land and sea capped by their step distance d from the shoreline over `bandSamples`: land <= eps + tan(landMaxSlopeDeg) x spacing x (d - 1/2), sea >= -eps - tan(seaMaxSlopeDeg) x spacing x (d - 1/2) |
| pads | each kept settlement's pad is every land sample of the cells its disk (`radius` by kind) overlaps; pads whose 513 blocks share or touch samples merge into one union; a union lies on the least-squares plane of the heights it covers, its gradient capped at `maxSlopeDeg`, blended over `blendSamples` |
| despike | Jacobi sweeps (at most `sweeps`): a land sample that is a strict extremum of its 8 neighbours and steeper than `maxSlopeDeg` to one of them, and not a ridge or valley (`ridgeNeighbours` opposite neighbours within the bound, `keepRidges`), moves to its lowest (highest) neighbour plus (minus) the bound |
| lowPass | separable `kernel` ([1,4,1]/6) on free land samples only: sea, lakes and their edges, pads and the pole and padding rows are kept |

Then a cubic B-spline 2x (at sample i (p[i-1] + 4 p[i] + p[i+1]) / 6, at i + 1/2 (p[i-1] + 23 p[i] + 23 p[i+1] + p[i+2]) / 48; columns
wrap, rows clamp) gives the 513 field, and the fine fix-ups follow: the rows at and beyond the pole lines as compiled (so pole rows stay
single-valued), sign coherence from the fine masks (ocean <= -eps, land and river >= eps), the pads again on their planes, lake beds and
shores again, and every matched lake group shown under the runtime plane rule. That last step lowers a hidden group's bed below the plane
its own shore allows (other lakes' samples left out, as the runtime rule leaves them out since MMR-285, so lowering one lake never lowers
another's target, and every matched group shows); a group the unshaped compile showed that is still hidden goes below its whole shore (a
guard; with the MMR-285 rule it never fires).
The layer compile then runs again on the shaped slopes (MMR-255). The `coast_sign` and seam-pair refusals (every layer plane included) apply to the shaped surface.

**Record.** `compiled/terrain/shaping.json` (`worldgen/contracts/terrain-shaping-record.schema.json`) carries the surface range and the
shaping block of `terrain-metadata.schema.json`: policy sha, sweeps, residual, samples changed, slope, coast and crease p95/max before (the
travel compile) and after, isolated spikes, pads (count, merged, over 25 degrees), lakes (groups, shown before and after, newly hidden, and
one record per group with its level, plane, bed and shore status) and the nav/visual coast mismatch (travel Land, Mountain or Sea cells whose
class disagrees with the sign of the surface at the cell centre). The stand-alone `--compile-terrain` has no travel placements and does not
shape; its `terrain.json` has no shaping block.

**Accepted limit: Mountain cells that look gentle.** A second nav/visual mismatch, from the same two fields. Travel classes come from the
unshaped 257 field: a cell is Mountain (impassable) when its corner slope, the range of its four corner heights over the spacing, exceeds
`mountainSlope` 1.0. Shaping lowers many of those slopes, so some Mountain cells look gentle on the shaped surface. Measured there with the
same corner slope: on A' 216 of the 491 Mountain cells are at most 0.5, and 31 are at most 0.3 with a rock weight below 0.25 (they look like
open ground); on B' 248 of 456, and 59. The record does not count them. The code fix is deferred (MMR-262 review, F09).

**Parity and gates.** `python tools/summarize_terrain.py --shaping A=<bundle> --shaping B=<bundle> --out evidence/fwg/terrain-shaping.json`
re-derives the shaped surface in Python from the bundle (z error 0, codes and range equal; since MMR-255 the layer planes too, `tools/terrain_layers.py`), recomputes the record and evaluates
the gates. Results on A' (seed 42) and B' (seed 73):

| | A' before | A' after | B' before | B' after | Gate |
|---|---|---|---|---|---|
| Interior slope p95 / max | 44.5 / 75.2 | 26.8 / 48.1 | 35.5 / 70.9 | 25.3 / 57.0 | <= 45 / <= 60 |
| Coast slope p95 | 59.3 | 44.2 | 56.5 | 40.3 | <= 50 |
| Crease p95 | 25.9 | 8.1 | 19.3 | 6.2 | |
| Isolated spikes | 1 | 0 | 0 | 0 | 0 |
| Pads over 25 degrees | | 2 of 258 | | 3 of 381 | <= 10 |
| Lake groups shown (of 35 / 159) | 0 | 35 | 18 | 159 | none newly hidden (MMR-285: all shown; 31 and 127 under the old runtime rule) |

Travel classes are byte-identical, seam pairs identical (131,841 per world) and no coastline sign is lost. "Before" is the travel compile
at verticalScale 0.005; at the pre-epic 0.01 the interior p95 was 66 degrees (findings section 15).

**Defaults taken.**
- `verticalScale` stays at MMR-248's 0.005 (1.25 x exaggeration): every gate passes with margin, and the travel classes are unchanged.
- `playability.json grid.side` stays 257: the travel compile is the 513 surface's even samples, so travel keeps 6.25-unit cells without a
  subsampling stage; `RuntimeNavigationBuilder` accepts a travel spacing that is a whole multiple of the surface spacing.
- Pads lie on capped planes rather than flat discs: clustered villages (a median 4.7 units from their towns) merge into unions, and one flat
  height over a union cut terraces into slopes.
- Smoothed normals: the runtime encodes with `--surface-normals smoothed` (Sobel of the codes smoothed by [1,4,6,4,1]/16, the policy's
  `normals.kernel`); heights, weights and physics are unchanged. MMR-249 saw no visible change at level 6.

## Layers (MMR-255)

The layer compile (`src/WorldBridge/src/terrain_layers.cpp`, `include/worldbridge/terrain_layers.hpp`; Python reference
`tools/terrain_layers.py`) turns the shared biome table into the encoder's material weights. It runs on the travel compile and the fine
compile (`compile()`), and again in `shape()` on the shaped surface, so slope rock follows the shaped slopes. Findings section 4 and the
MMR-249 probe (section 13) set its rules.

**Target fractions** over `terrain-materials.json` layerSet, per output sample, summing to 1, on canonical periodic columns (a neighbour
column past an edge wraps by the seam period; rows clamp), so seam pairs get identical planes:

| Step | Rule |
|---|---|
| biome | the sample's biome row; the moisture biome (grassland) splits its wet/dry pair by moisture: the row's wet share at `rules.moisture.split`, all wet at split + `band`, all dry at split - band, the pair's total kept |
| kernel | the tent-weighted mean, weights (k + 1 - \|dr\|)(k + 1 - \|dc\|) over (2 `kernelSamples` + 1)², of the neighbours of the same class (land: land and river intents; water: ocean and lake) |
| cold | land below `temperature.belowC`: the wet and dry layers move into the cold layer |
| mountain | the `Mountain / ridge` landform mixes in `mountainShare` of the mountain layer (the rest scaled by 1 - share) |
| slope | the `slopeRock` ramp share s of the slope layer (the desert slope layer on a Desert-face biome), the rest scaled by 1 - s; slope is the central-difference gradient |
| coast | land within `coast.samples` of an ocean sample: all coast layer, except a share max(`rocky_coast`, s) of the rocky layer |
| water edge | a river sample, or land within `waterEdge.samples` of a lake or river: all water-edge layer |
| ice cap | `ice_cap` at or above `iceCap.threshold`: all ice-cap layer |
| permanent white | a `permanentWhite` biome, or `snowMonths` months of snow at or above `snowThreshold`: all `permanentWhite.layer` |

**Alphas.** MMR-249 measured alpha-over compositing from slot 15 down, the weight linear in the byte divided by `smooth_blend_amount`;
`use_transparency_of_ground_slope` height-blends and stays off. So the stored byte of slot k is round-half-even(a_k × 255) with the
over-compositing inverse a_k = f_k / (f_0 + ... + f_k) (`alpha.rule` overInverse), bytes below `alpha.minByte` (3) dropped, slot 0 at
255: the composited coverage a_k × prod_{j>k}(1 - a_j) is f_k up to byte rounding. Masks and variants never switch per season at run time,
so the encoder writes the same mask in all four seasons.

**Encoder.** `--surface-weights` takes `MNBLWT02` and refuses a template whose material layers are not its layers in slot order, a layer
that differs between seasons, flora meshes, a flora or mesh-blend layer, `use_dynamic_weather_effects` off or `terrain_render_version` 1.
A node carries the base and every layer with a nonzero sample in it (WGHT plane, mask bit, weightmap level). PHYM is the dominant visible
layer. `tools/scenebin/terrain_bin.py build_terrain` is the Python reference: `tests/WorldBridgeSurfaceTests.py` decodes every PNG and
PHYM byte of the C++ output against it (15 planes, absent and partly present layers, 129 / 257 / 513 samples).

**Map trees.** `bundle::build` plants map trees (`terrain::flora_bytes`, `compiled/terrain/flora.bin`, FLR2) from `terrain-materials.json`
`flora`: on canonical fine samples of a flora biome, about density × cell area × the flora layer's target fraction trees (a per-sample
deterministic stream seeded by the layout seed), clear of the sea and lakes, slopes past the ramp start, settlement pads (shaping pad radius
plus `padMargin`) and travel road cells, at the quantized surface height, written at every periodic image; `maxTrees` thins the density.
The encoder's `--surface-flora` passes the file through (every tree inside the terrain, names lower-case identifiers).

**Record.** The bundle's `shaping.json` carries `layers` (dominant-layer share per slot), `permanentWhite`, `layerCompile` (mean target
fraction per slot, each biome's mean composited coverage, the plain interior samples checked against their row, the seam pairs checked on
every plane) and `flora` (trees, canonical trees, cleared by pads and roads, the density factor, per kind and per biome).

**Evidence** (`evidence/map-epic-2/terrain-layers.json`, `python tools/summarize_terrain.py --layers A=<bundle> --layers B=<bundle>`):

| | A' (seed 42) | B' (seed 73) |
|---|---|---|
| layers / planes byte-identical to the Python reference | 15 / 0 differing | 15 / 0 differing |
| seam pairs identical on every plane (weights.bin; encoded terrain.bin heights, planes, PHYM) | 131,841 | 131,841 |
| plain interior samples, max error against the table row | 554, 0.002 | 1,486, 0.002 |
| layers per node | 3 to 15 | 2 to 15 |
| permanent-white canonical samples (all land ice) | 924 | 3,372 |
| map trees written (canonical) | 1,176 (588); 9,144 (4,572) at MMR-247's density 3.6 | 1,990 (995); 15,644 (7,822) at 3.6 |

The plain interior samples (no rule changed them, every kernel neighbour land of the same biome) composite to their biome row within byte
rounding. A biome's mean coverage over all its samples is lower where the rules take their share: on these island worlds coasts (beach),
river and lake banks (riverbed), the mountain landform and steep slopes cover much of the small forest and rainforest patches (forest
floor is 23-27% of forest samples, beach 8-24%, riverbed 11-25%); the per-biome means are in the evidence.

**Defaults taken.** Kernel class split land/water (coasts stay crisp); the coast's rocky share is max(rocky_coast, slope share); the
moisture band 0.25; `alpha.minByte` 3; layer scales 15 (ground) and 20 (rock); the three replaced textures (`ground_forest_b_d`,
`ground_pebbles_a_d`, `cliff_b_d`) are full-colour sets not seen in the probe gallery; tree density 0.45 per square unit at full weight
(the probe's dense node; MMR-247 raised it to 3.6, see biome-presentation.md "Map trees"), rainforest with beech and acacia.

## Changes to the MMR-25 projection

`poleMarginRatio` changed from 0.5 to 0.75, so the physical domain is a 1600 × 1600 square. That is what the proven encoder needs, and it is the R10 fixture's terrain size. The seam strips are unchanged. The projection evidence was regenerated: `evidence/fwg/projection-export.json` passed, and projection tests passed 104/104.

## Open items

- **The encoder is still the diagnostic encoder.** It writes its own 33 × 33 navmesh. The campaign navmesh from the compiled intent and terrain is MMR-28/38.
- **Material layers.** Resolved by MMR-255: the 15-layer set and the layer compile above. The look of the generated layers and trees on the campaign map is MMR-247's live check.
- **Polar padding and vertical scale:** see the visual probe above.
