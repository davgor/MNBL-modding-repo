# MMR-25 independent review

- Reviewer: an independent read-only Claude agent (claude-opus-5-5). It did not write the deliverable, and it built and ran only in its own temp folder.
- Date: 2026-10-14.
- Reviewed: the first submission-ready state. Module `6f7e3edc…`, worldbridge `e8690446…`.
- The disposition of every finding follows the report.

The report below is verbatim.

---

Verdict: ACCEPT WITH FIXES

The coordinate math, node decoding and single-identity export are correct. Path splitting has one real defect: it emits a "seam" link where nothing crossed the seam. It cannot draw a road across the whole map, but it breaks the documented link contract. Nothing in the repo was modified. All my builds and runs went to %TEMP%\mmr25-review.

## Per criterion

**c1 — met.**
- projection.json records the seam (−180°, x = 0 = W), the 360° / W = 800 wrap, full coverage, pole aliases, distortion, scale and the periodic layout.
- The layout matches R10 exactly: strip 400 = W/2, canonical physical x [400, 1200), physical width 1600, pole margin 400. That agrees with `build_periodic_world.py` (STRIP = 64 cells × 6.25).
- The H = W/2 versus R10 H = W difference is disclosed (docs/world-projection.md:32). So is the resulting 1600×1200 terrain that `surface.cpp` cannot yet write (:103).
- The topology R10 proved is unchanged: x wraps, strips are at least W/2, and north and south are never connected. I do not count the H change as breaking the R10 match.
- Gap: sea continuity is recorded only in the doc (:45-47). projection.json has no entry for it.

**c2 — met, with reporting fixes.**
- All 6 cited receipt SHA-256s match.
- Axes and raster orientation really are fixed by MMR-16: terrain-bin.md:114-121 (column = +X, row = +Y) and `surface-collision-calibration.json` `prospectiveLandmarks` / `model.diagonal`.
- Height scale 0.01 is the value the accepted MMR-16 run used for the generated seeds. `RuntimeSurfaceSequenceProbe.cs:178` used 1 for the calibration surface itself. So 0.01 is an adopted convention, but calibration.json:5 calls it "fixed by the receipts".
- calibration.json:5 also claims MMR-16 fixed the horizontal scale. It did not: v1 chooses W, H and non-square 6.25 × 3.125 source cells.
- Run 4768's numbers reproduce from `outputs.worlds[0].seaDatum`: water level −100 at all 576 samples, no failures, open sea −33.03…−0.90, coastal −32.66…6.38, land 4.37…63.75.
- But 136 of the 576 samples got no navmesh face ("noFace", terrain −0.50…24.91). They are left out of calibration.json, docs/runtime-capabilities.md:11 and docs/world-projection.md:95. "Land" covers only 25 Mountain-face samples.
- The run was justified: no receipt covered where the engine draws water.

**c3 — met.**
- The tests cover poles, the antimeridian, and the duplicate seam column in both forward/inverse and node decoding.
- `NodeGrid` matches FWGC++ `grid.cpp` exactly: `node_index`, pole collapse, the seam duplicate, the wrapped 8-neighbourhood, and pole ↔ ring adjacency.
- Every settlement, hamlet and castle (node, x, z) agrees in the 129 and 33 worlds. The 1025 export, which checks every record, also passed.

**c4 — met.**
- The same identity is on projection.json, terrain.bin (header bytes 12–76), terrain.json, objects.json and manifest.json.
- Positions, ports, paths, z values and sea flags all come from the one `Projection`.
- The summarizer is a genuinely independent Python recomputation.
- My own spot-check used the ticket's formula from raw world.json (x, z): every a129 settlement matched with 0 error.

## Checks performed
- **CTest Release:** 5/5 pass, including `source_contract` and `projection` (93/93). The binaries are newer than the sources, and the worldbridge.exe SHA matches the evidence.
- **Fuzz harness** (scratch build): 308,880 random walks on the generator graph. Sizes n ∈ {3,4,5,9,17,33,129,1025}, 117 central meridians including the `nextafter` neighbours of column-aligned values. Walks were biased toward the seam and poles.
  - 0 drawn steps longer than W/2, 0 points out of range, 0 one-point pieces, 0 pole-link errors.
  - Drawn length equals graph-step length in every path.
  - `sample()` agrees with `forward()` (wrap-aware) everywhere.
  - Defect: 19,664 "seam" links with both ends on the same edge.
- **Duplicate seam column:** with central meridian 0 it is exact. With a non-zero meridian (e.g. −10°), `sample(n−1, r)` differs from `sample(0, r)` by about 8e-14.
- **Reader at 1025:** `--read-source` on `split-1025` passes (retained 911,612 B, 36 s).
- **Projector at 1025:** `--project` on `split-1025` passes: 47 roads, 471 rivers, 0 seam or pole splits.
- **Reproducibility:** re-exporting a129 is byte-identical to `build/research-projection25/a129`, and the threads4 source gives the same manifest.
- **Schema:** I validated a129, a33 and my 1025 projection.json against projection.schema.json with my own validator (`jsonschema` is not installed). All pass, and it handles every keyword the schema uses.
- **Sea flag versus the source layers:**
  - a129 and a33: identical to `land == 0` and `water_type == 1`.
  - split-257: one sample is below sea level with `water_type = 2` (a lake), and it is flagged sea.

## Must-fix
1. **src/WorldBridge/src/projection.cpp:198-199 — spurious seam links.** A path that reaches the seam column from the west and then turns back west gets split. Example: nodes (7,3) → (0,4) → (7,5) at n = 9 gives `[(700,150),(800,200)] seam [(800,200),(700,250)]`. Both ends sit at x = W, which breaks the contract in projection.hpp:50-51 and projection.json `paths.seam`, and inflates `roadSeamSplits`. Roads running along ±180° will hit this. Exact correction:
   ```cpp
   if (piece.size() >= 2) { out.pieces.emplace_back(); out.links.push_back("seam"); }
   else if (out.pieces.size() > 1 && out.links.back() == "seam") {   // the previous step ended exactly on the seam and this one turns back
       out.pieces.pop_back(); out.links.pop_back();
       out.pieces.back().push_back(p);
       continue;
   }
   else piece.clear();
   ```
   - Verified in scratch: it removes all 19,664 same-edge links and the existing tests stay 93/93.
   - What remains are links between x = W and x ≈ 1e-14. They occur only for meridians within an ulp of column alignment, never at 0, and are harmless.
   - Add tests after projection_tests.cpp:167:
     - `{(7,3),(0,4),(7,5)}` gives one piece `[(700,150),(800,200),(700,250)]` with no links.
     - `{(0,3),(7,4),(0,5),(7,6)}` gives one piece.
   - Fix docs/world-projection.md:57, which says "The unit tests cover every split case"; that is currently false.

## Should-fix
1. **projection.cpp:137-141.** Reduce the column first, e.g. `const auto c = column % (nodes.side() - 1);`, and compute `u` from `c`. Then column n−1 is exactly column 0 for any central meridian. docs/world-projection.md:40 says "(tested)", but only meridian 0 is tested.
2. **projection.cpp:154-156 and :286, docs/world-projection.md:46.** `is_sea` is a per-sample sign test, not the MMR-17 rule, which classifies faces by the main-diagonal centre (`build_bootstrap_world.py:120-123`). It also flags below-datum lakes as sea (one sample in split-257). Either export `water_type` and define sea as below datum and `water_type == 1`, or stop calling it the "MMR-17 classification" and record the face rule for navigation consumers. Right now "navigation inputs" are covered only by this flag.
3. **calibration.json:60-63, docs/runtime-capabilities.md:11, docs/world-projection.md:95.** Add `noFace`: 136 of 576 samples, terrain −0.50…24.91, no face returned by `GetFaceIndex(new CampaignVec2(p,false))`. State that "land" covers only the 25 Mountain-face samples.
4. **calibration.json:5 and :36.**
   - Reword: axes and raster orientation are fixed by MMR-16; height scale 0.01 is the convention its generated-seed runs used; horizontal scale is a v1 policy choice.
   - State that MMR-26 must resample the non-square source grid onto the square MMR-16 raster through `Projection::inverse`.
5. **projected_world.cpp:257-259.** The calibration pin is only checked in a test. `project_main` should hash `policy.calibration_file` (repo-relative) and refuse if it does not match `policy.calibration_sha256`.
6. **source_reader.cpp:261.** Use `number_or_null()` for `radius_m`, as `effective_config` already does. A null radius currently makes `read_source` fail for every caller, including `generation_driver.cpp:246`.
   - The new `routes()`/`rivers()` refusals (:527-545) also abort generation reads.
   - None of this new parsing is tested: the fixture has `"routes": []` (valid/world.json:93-96) and no `river_segments`. Add fixture rows and refusal cases to `source_reader_tests`.
7. **projection.cpp:71-73.** Refuse unknown members inside `calibration` and require `notes` to be a string. projection.hpp:36 promises "exactly the documented members".
8. **projection.cpp:277.** `eastWestFactorAtLatitude` puts `std::cos` results into the identity-bearing document, so the identity can change with the C runtime's maths library across toolchains. Round them to a fixed number of digits or drop them and keep the formula.
9. **projection.cpp:80-88.** The constructor does not re-check policy ranges and trusts `Policy::sha256`. The tests change `central_meridian_deg` and keep the file hash, so `policySha256` stops describing the policy. Re-validate and recompute, or make `Policy` constructible only through `load_policy`.
10. **projection.cpp:210-315.** Add a sea-continuity entry to projection.json, since the ticket asks for it to be recorded: seam column equals column 0 so sea flags match across x = 0 = W; strips copy the other side; each pole row is one value.
11. **projected_world.cpp:152.** Say "little-endian" in the terrain.bin layout string.

---

## Disposition

Every finding was applied in one batch, then everything was re-verified once: CTest 5/5, projection tests 104/104, reader tests 129/129. The size-129 and size-33 worlds were re-exported with all 17 independent checks passing each, and the repeat export is byte-identical. No game run was needed. The final worldbridge is `5181fa78…`.

| Finding | Disposition |
|---|---|
| Must-fix 1 | Fixed with the reviewer's correction (`projection.cpp`, seam branch). Tests added: (7,3)→(0,4)→(7,5) is one piece with no links; the (0,3),(7,4),(0,5),(7,6) zigzag is one piece. "The unit tests cover every split case" is now true. |
| S1 | Fixed. `sample()` reduces the column first. Column n−1 is now bit-identical to column 0 at meridians 0, −10, 22.5, 77.7 and −179 (tested). |
| S2 | Fixed. `is_sea` is renamed `below_datum` and described as a sign test. The export now also carries the source `water_type` class unchanged (`terrain.bin` gained a third raster) and counts disagreements between the two (0 on both pinned worlds). Face classification is assigned to MMR-28, and the MMR-17 face rule is recorded. |
| S3 | Fixed in `calibration.json`, `docs/runtime-capabilities.md` and `docs/world-projection.md`: samples by face (256/159/25/136), the no-face range, the land samples being all Mountain, and the sea-flagged lookup as the likely cause. |
| S4 | Fixed. The calibration summary now says axes and orientation come from MMR-16, height scale is a convention, and horizontal scale is the policy's choice. MMR-26 must resample through `Projection::inverse`. |
| S5 | Fixed, more strictly than asked. The policy's calibration path is relative to the policy file, and `load_policy` itself refuses a missing receipt (`missing_file`) or an altered one (`calibration_mismatch`), so every caller checks it, `--project` included. Tested. |
| S6 | Fixed. `radius_m` uses `number_or_null`. Nine reader cases were added: routes and rivers parsed, frame absent, null radius and sea level, a one-node route, a route without nodes, a string node, a three-node and a one-node river segment, and a string radius. The new refusals are deliberate: they reject malformed producer output, and the pinned producer's worlds at 33, 129 and 1025 parse. |
| S7 | Fixed. Unknown members inside `calibration` and non-string notes are refused (tested). |
| S8 | Fixed. The numeric `eastWestFactorAtLatitude` is gone; the identity carries the formula and rounded example values as text. |
| S9 | Fixed. The `Projection` constructor re-validates ranges and recomputes the policy hash from the mapping members. An edited policy is refused if out of range, or re-hashed (tested). |
| S10 | Fixed. `projection.json` has `seaContinuity` (seam, strips, poles), and the schema requires it. |
| S11 | Fixed. "little-endian" is in the layout strings and headers. |

Later change (MMR-26): the pole margin is now 3W/4 = 600 (poleMarginRatio 0.75), so the physical domain is a 1600 × 1600 square for the square-only surface encoder. The report above refers to the earlier W/2 = 400. Tests and evidence were regenerated.
