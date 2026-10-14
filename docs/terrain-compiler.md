# Campaign terrain compiler (MMR-26, 2026-10-14)

`worldbridge --compile-terrain` bakes a split world's layers into the square terrain that the proven surface encoder imports. It goes through the accepted projection (MMR-25, `docs/world-projection.md`).

| | |
|---|---|
| Code | `src/WorldBridge/src/terrain_compiler.cpp`, `src/material_compiler.cpp`; the encoder gained an optional `--surface-weights` input (`surface.cpp`) |
| Engine format (calibration output, not hard-coded) | `worldgen/calibration/terrain-import/terrain-import.json`. The compiler reads every value from it:
  - 4 × 4 nodes, square terrain, 129 or 257 vertices;
  - the extent range and node-size precision;
  - 16-bit heights with a range rounded to 0.001;
  - the 0.03 engine vertex truncation;
  - the two template layers.

  Cited from the MMR-16 receipts and encoder; no new engine calibration |
| Material policy | `worldgen/policies/terrain-materials.json`: every exported biome (13), water class (3), river code (2) and landform (4) is listed. Anything else fails |
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
| Height | **seam-aware bilinear.** Column n−1 is the source's duplicate of column 0, so cells across the seam interpolate correctly. z = (height − sea level) × 0.01 |
| Biome, water class, river, landform | **nearest sample**, with halves rounding up and column n−1 read as column 0. Codes are copied and never mixed. A raster biome id missing from the exported `terrain.biomes` catalogue is refused, even if the policy lists it. Version 1 ignores `layers.biome_variant` (magical variants) |
| Intent (navigation input) | land 0, ocean 1, lake 2, river 3, from `water_type` and `river` through the policy. A river overrides land; a lake stays a lake |
| Coastline coherence | ocean ≤ −0.05, land and river ≥ +0.05; lakes stay as compiled, and lakes below the datum are counted (`lakeBelowDatum`, 0 at every size). The compile is refused (`coast_sign`) if any coastline sign would not survive 16-bit quantization and the engine's 0.03 truncation toward zero |
| Rock weight (`rock_k`) | the larger of the biome's rock weight and the slope rule round-half-even(clamp((slope − 0.6)/0.6, 0, 1) × 255). The slope is taken on the output grid, periodic in x so that seam pairs see the same neighbours. Grass is the full-weight base layer (template) |
| 16-bit height | one global range, hmin/hmax = floor/ceil(min/max × 1000)/1000. A zero range uses hmax = hmin + 1. q = round-half-even(clamp((z − hmin)/(hmax − hmin), 0, 1) × 65535), stored big-endian. The reconstruction error is ≤ half a step, checked. The encoder re-derives the same range and codes from `heightfield.bin`: the difference is exactly 0 |

**Checks made on every compile.**
- **Paired seam columns.** Column c and column c + W/spacing (128 at 257 vertices) must be identical in height, 16-bit code, rock, biome, intent and landform: 33,153 pairs. Logical x comes from the canonical column, so the pairs share bits even when W is not a dyadic number (tested at W = 800.1).
- **Refusals.** Each of these refuses the compile: a non-square physical domain, a side or extent the calibration does not allow, a seam that falls between output columns, heights beyond the calibrated limit, and any unmapped category.
- **Node borders.** They are shared by construction: the encoder cuts each node from the one grid. The evidence checks them on the encoded file (1,560 border samples), together with the content of all 16 nodes.

**Outputs.**
| File | Contents |
|---|---|
| `heightfield.bin` | `MNBLHF01`; the encoder's input at scale 1 |
| `weights.bin` | `MNBLWT01`; rock weights for the encoder's `--surface-weights` |
| `height-q16.bin` | the 16-bit heights |
| `masks.bin` | biome, intent, landform and padding byte planes (padding is 1 on rows beyond a pole line) |
| `projection.json` | the resolved projection |
| `terrain.json` | metadata: identities, ranges, coherence, checks, fidelity and timings |

## Source size: frozen at 129 for this output

The output is capped at 257 vertices across 1600 world units, because the proven encoder writes 4 × 4 nodes of at most 64 cells. A size-129 source has a 6.25 × 3.125 world-unit cell (W/(n−1) across, H/(n−1) along).

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

## Changes to the MMR-25 projection

`poleMarginRatio` changed from 0.5 to 0.75, so the physical domain is a 1600 × 1600 square. That is what the proven encoder needs, and it is the R10 fixture's terrain size. The seam strips are unchanged. The projection evidence was regenerated: `evidence/fwg/projection-export.json` passed, and projection tests passed 104/104.

## Open items

- **The encoder is still the diagnostic encoder.** It writes its own 33 × 33 navmesh. The campaign navmesh from the compiled intent and terrain is MMR-28/38.
- **Material layers are limited to two (grass, rock_k)** in the proven template. Desert, snow and ice are approximated by rock weights; more layers need authored terrain materials (MMR-37).
- **Polar padding and vertical scale:** see the visual probe above.
