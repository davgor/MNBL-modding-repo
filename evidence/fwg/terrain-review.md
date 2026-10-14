# MMR-26 independent review

- Reviewer: an independent read-only Claude agent (claude-opus-5-5). It did not write the deliverable, and it built and ran only in its own temp folder.
- Date: 2026-10-14.
- Reviewed: the first submission-ready state, worldbridge `06c17565…`.
- The disposition of every finding follows the report.

The report below is verbatim.

---

Verdict: ACCEPT WITH FIXES

The compiler logic is correct and reproducible. I re-ran the s129 compile and got byte-identical outputs. Two verified gaps need fixing before acceptance: one in c2 enforcement, and one c2 evidence check that can never fail.

## Criteria
- **c1: met.**
  - The CTest control points pass.
  - In a scratch harness linked to the built libraries I tried central meridians 10, 22.5 and −137.3, n = 3, 4, 5 and 129, and sides 129 and 257. An independent longitude-based bilinear reference matched to ≤1.4e-14. Forward-projected source control points had 0 category mismatches.
  - terrain-import.json matches surface.cpp: make_grid 129/257, level 5/6, the floor/ceil 0.001 range, even_round, big-endian HGHT, slot 0 at 255, slot 1 weights, PHYM x-major ≥128, the extent rule and the 33×33 navmesh.
  - All 5 cited receipt SHA-256s match. The 0.03 truncation model is in surface-collision-calibration.json. Run 32896 shows 129-vertex surfaces at extents 256 and 800. MMR-63 did use the worldbridge encoder at 257/1600.
  - No engine calibration was repeated. Caveats are in should-fix 1 and 2.
- **c2: met on the delivered compiles, with enforcement gaps.**
  - The roundtrip check is enforced.
  - The seam-pair check is real: columns c and c+period are computed independently, and rock periodicity holds at columns 0 and 256.
  - Orientation is documented and tested.
  - The engine-sign tolerance is only recorded, never enforced (must-fix 1).
  - The tile-edge check is tautological (must-fix 2). I confirmed the property itself on the encoded terrain.bin: 1,560 node-border compares with 0 mismatches. Its HGHT and rock_k also equal height-q16.bin and weights.bin at all 67,600 node samples.
- **c3: met.**
  - The policy's 13 biomes equal the exported terrain.biomes (same ids and core names).
  - Water types 0–2, river 0/1 and the 4 landforms are explicit. Unknown values are refused, and the refusals are tested.
  - Lake and river stay distinct in intent (2 and 3). Biome ids are never mixed.
- **c4: met.**
  - I re-derived all eight fidelity metrics for source sizes 129 and 257 in my own Python, including the union-find with wrap and pole adjacency. All match exactly.
  - The table in the doc matches the evidence. Choosing 129 is sound given the 6.25 spacing cap. It rests on one seed (should-fix 9).

## Checks performed
- CTest 6/6 passed. terrain_resampling_tests gives 41/41 and matches evidence/fwg/terrain-tests.txt.
- Recompiled s129 at sides 257 and 129 into %TEMP%; the 257 output equals build/research-terrain26/s129.
- tools/summarize_terrain.py re-run into %TEMP% reproduces the "verified" and "encoded" blocks.
- Adversarial harness (results above):
  - W = 800.1 is refused with seam_mismatch.
  - Vertical range ×300 or ×1000 compiles successfully with 18 or 22 engine-sign violations.
- Source facts for the 129 world:
  - All ocean is below the datum (max −0.91 m) and all land above it (min +0.15 m). Rivers lie only on land. Lakes sit 330–1660 m above the datum. Poles are ocean.
  - So coherence moves only 36 samples, by at most 0.0485, and leaves lakes alone. The engine's 0.03 truncation leaves the sign intact: −0.05 ± 0.0009 truncates to −0.03.
- Encoder without --surface-weights: output is unchanged, the default weightSource string is unchanged, and the quantized heightfield is identical. A bad weights file is refused.
- Visual probe: the seam view (view3) and its strip copy (view4) have identical relief.
- Projection policy change (poleMarginRatio 0.75) is consistent in docs, tests (104/104) and projection-export.json, which carries the new policy SHA.

## Must-fix
1. **Engine-sign violations are recorded but don't fail the compile.** At src/WorldBridge/src/terrain_compiler.cpp:282-285, after the `quantization_error` check add `if (t.engine_sign_violations) refuse("coast_sign", "Coastline sign does not survive 16-bit quantization and the engine's 0.03 truncation");`. In worldgen/contracts/terrain-metadata.schema.json:55 set `engineSignViolations` to `{"const": 0}`. Add a CTest case that scales the synthetic heights ×300 and expects `coast_sign`.
2. **The tile-edge check compares each sample with itself.**
   - Where: terrain_compiler.cpp:295-310 and tools/summarize_terrain.py:148-157. `tile(ix,iy,k,cells)` and `tile(ix+1,iy,k,0)` are the same global index, as are the vertical pairs.
   - Fix: replace it with a real check in summarize_terrain.py. Decode the encoded terrain.bin node HGHT/WGHT PNGs (tools/scenebin/terrain_bin can parse it), compare the shared node borders, and compare the nodes with height-q16.bin and weights.bin. Then drop or relabel `tileEdgeSamples`: state that borders are shared by construction (surface.cpp:183-187) in terrain_compiler.cpp, schema lines 59-60 and docs/terrain-compiler.md:36.

## Should-fix
1. terrain_compiler.cpp:242 uses `heightLimit` (a height bound) as the extent limit. load_import (179-199) ignores `extentRange` and `nodeSizeRule`, and z is never checked against ±heightLimit. Parse `extentRange`, require `extent/4*1000` to be integral, and check max|z| ≤ heightLimit.
2. The 0.03 truncation (terrain_compiler.cpp:282-283) and the 16-bit format are hard-coded; terrain-import.json has prose only. Add numeric fields (`height.bits: 16`, `height.engineVertexTruncation: 0.03`) and read them.
3. Seam pairs depend on `c*spacing - strip` (line 254) being exact in floating point, so a non-dyadic W is refused (W = 800.1 is), and MMR-41 may tune W. Compute x from the canonical column instead: `((c - strip/spacing) mod period) * spacing`.
4. material_compiler.hpp:31 says the catalogue must be "exactly" the policy's, but the code only checks exported ⊆ policy. Raster ids are validated against the policy, not the export. Refuse raster ids missing from the exported catalogue, or correct the comment.
5. Neither terrain-materials.json nor the docs say what happens to `layers.biome_variant` (magical variants). Add an explicit "v1 ignores biome_variant" note.
6. Pole padding repeats the pole row, so a land pole would give 600 units of land padding. docs/world-projection.md:124 ("The polar padding is ocean navmesh") is true only for ocean poles. Refuse or flag land poles (the R10 fixture required ocean poles), and consider a padding flag in masks.bin for MMR-28.
7. The producer's water method allows inland lakes below sea level. Record a `lakeBelowDatum` count in `coherence` for MMR-39 (it is 0 for this world).
8. docs/terrain-compiler.md:71 says the seam view and strip copy "render identically", but 93% of pixels differ (mean |Δ| 4.9, from texture phase). Reword to "identical relief; material texture phase differs".
9. docs/terrain-compiler.md:60: "loses no land" and the size freeze come from one world (layoutSeed 1555514970434891308). Say so. Optionally note that the 2:1 meridional decimation comes from heightRatio 0.5 combined with the square encoder.
10. tools/summarize_terrain.py:196-206 copies the fidelity numbers from terrain.json instead of re-deriving them. Line 190 checks the encoded weights only through the weightSource string, even though the encoded scene (16:02) predates the final compile (16:09). I verified both are identical. Add the re-derivation and the weights check, or state the limitation (docs:12).
11. Test gaps:
    - No real half-even tie is tested: `slope_rock(0.9)` is 127.50000000000003, and the quantize test has no tie.
    - A central meridian other than 0 is not in CTest.
    - The reader extension (source_reader.cpp:294-295, 546-561) has no reader test.
12. material_compiler.cpp:121 uses `std::nearbyint`, which depends on the floating-point environment. Use the same even_round helper as the compiler and encoder.
13. terrain_compiler.cpp:347 runs `fs::remove_all(pending)` and silently deletes an existing `<out>.partial`. Refuse if it exists.
14. evidence/fwg/projection-review.md:20 still says "pole margin 400". Add a note that MMR-26 changed it to 600.

---

## Disposition

Every finding was applied in one batch, then everything was re-verified once:
- CTest 6/6; terrain tests 50/50; reader tests 132/132; projection tests 104/104.
- All four sizes recompiled.
- The size-129 compile re-verified with 28 independent checks.
- The projection evidence regenerated.

No new game run was needed. The final size-129 compile re-encodes byte-identically to the scene the visual probe rendered. The final worldbridge is `488d91f7…`.

| Finding | Disposition |
|---|---|
| Must-fix 1 | Fixed. The compile is refused with `coast_sign` when any sample violates. The schema sets `engineSignViolations` to `{"const": 0}`. Test: an import whose truncation (0.06) exceeds coastEpsilon is refused. A truncation test is deterministic where a ×300 height scale is not. |
| Must-fix 2 | Fixed. The tautological in-compiler loop and `tileEdgeSamples` are gone. `checks.nodeBorders` states that borders are shared by construction (surface.cpp `terrain()`). `tools/summarize_terrain.py` now decodes the encoded `terrain.bin` with `tools/scenebin/terrain_bin.py`: all 16 nodes match `height-q16.bin` and `weights.bin` at 67,600 samples, and the shared borders match at 1,560. Doc, header and schema updated. |
| S1 | Fixed. `load_import` reads `extentRange` and `nodeSizeDecimals`. The compile checks the extent range, node-size precision and max |z| ≤ `heightLimit`. Tests cover the extent beyond range and heights beyond the limit. |
| S2 | Fixed. `height.bits`, `rangeDecimals`, `zeroRangeSpan` and `engineVertexTruncation` are numeric fields that the compiler reads; `quantize` takes them, and bits other than 16 are refused. |
| S3 | Fixed. When the strip is a whole number of columns, logical x comes from the canonical column, ((c − strip/spacing) mod period) × spacing. W = 800.1 now compiles with exact seam pairs (tested). |
| S4 | Fixed. Raster biome ids missing from the exported catalogue are refused (tested), and the header comment states the real rule. |
| S5 | Fixed. The policy notes and the doc say version 1 ignores `layers.biome_variant`. |
| S6 | Flagged, not refused, because a generated world may have a land pole. `terrain.json` records each pole's intent and the padding-row count; `masks.bin` has a padding plane (tested); `docs/world-projection.md` is corrected. The compiled world has two ocean poles. |
| S7 | Fixed: `coherence.lakeBelowDatum`, 0 at every size. |
| S8 | Fixed: "identical relief; material texture phase differs". |
| S9 | Fixed. The doc names the single world (layout seed 1555514970434891308) and explains the 2:1 decimation. |
| S10 | Fixed. The verifier re-derives all eight fidelity metrics for the size-129 world, matching C++ `llround` for coast lookups. Encoded weights are checked through the decoded rock_k planes, not the weightSource string. |
| S11 | Fixed. Tie tests for quantization (32767.5 → 32768) and the slope ramp (127.5 → 128); with the odd multipliers 65535 and 255 the midpoint is the only exact tie. A central meridian of 22.5 is now in CTest. Reader tests added for `terrain.biomes`/`landforms`: accepted, biome without core, non-string landform. |
| S12 | Fixed. `slope_rock` uses the encoder's `even_round`. |
| S13 | Fixed. An existing `<out>.partial` is refused, not deleted. |
| S14 | Fixed. `evidence/fwg/projection-review.md` notes the margin change to 600. |
