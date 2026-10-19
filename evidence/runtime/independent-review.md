# MMR-21 independent review

- Reviewer: an independent read-only Claude agent (claude-opus-5-5). It was given the ticket text and criteria only, did not write the deliverable, and was not allowed to edit files, run the game or use Dark Mechanicus.
- Date: 2026-10-14.
- Reviewed: `docs/runtime-backend-decision.md` and `task-notes/mmr21-telemetry.md`, both as first drafted.
- Every finding was applied before submission. The disposition of each one follows the report.

The report below is verbatim.

---

Verdict: ACCEPT WITH FIXES

The backend choice (native scene bundle plus a `SandBox.MapScene` subclass, with generated XML registered before campaign init) is supported by the accepted evidence. The telemetry numbers I checked are accurate. None of the fixes below needs a new game run. They are reclassifications, omitted gaps, mislabelled timing spans and broken pointers. Until they are applied, c1 is not met. I did not check two things because the DM tools were off-limits: whether MMR-25, MMR-29 and MMR-30 really depend only on MMR-21, and the claim that no ticket carries an "r06" tag.

## Criteria
- **c1: not met as written.** Some limits are presented as passed without evidence, and several source gaps are left out of the matrix:
  - In R10, terrain picking at the seam was never observed through the native pick.
  - Pole identity is asserted with no probe behind it.
  - The seam save was only deserialized, never resumed.
  - Storm/weather wrap across the seam, the naval encounter at the seam, and armies/sieges/raids at the seam are missing.
  - One stale R02 "failure" is presented as current.
  - The claim that each half of generate→register→boot is proven overstates the evidence: no probe has loaded a navmesh from a scene registered at runtime.
- **c2: met with fixes.** One backend is selected and there is no editor dependency. But the frozen load sequence puts deserialization in the wrong place, the seam list is out of call order for Models, the doc wrongly says every caller goes through the interface, and two MMR-17 test face groups are frozen as if they were product groups.
- **c3: met with one fix.** Dependent work is released with no human gate, and failures have a scope. The grazing-ray failure is not genuine (see M2).
- **c4: met with fixes.** Values come from recorded telemetry with input hashes, and gaps are stated rather than invented. But two stage durations are mislabelled, the unsupported-operations table is incomplete, and the pointer to native API signatures is wrong.

## Spot checks (file → result)
1. **Generation** seed 42/43: 11,197 / 12,656 ms; fwg 5,473 / 6,565 ms; reader 5,700 / 6,077 ms; peak working set 265,490,432 / 308,269,056 B; tick gaps 0.0224 / 0.0068 / 0.0709 s (`generator-probe.json` cases). Match.
2. **MMR-24 offline:** 5,147 / 8,568 / 5,692 ms and 246,964,224 B (`build/research-identity24/.../run-record.json`). Match.
3. **Navigation caches**, all 18 worlds (Default/Naval/All and totals, including 566,815 ms for 63042), from raw `outputs.worlds[].mapSupport.generatedNavigationCaches`. Match. Log span example for 2248 All (+35019 → +61562): match.
4. **Bootstrap totals:** 2248 2a 42,717 ms; 12348 575,400 ms; 22868 2b 82,847 ms, plus the Reg/Read/Tail sub-spans (logs). Arithmetic matches; labels are wrong (S2).
5. **Load run 8256:** 22 ms package verify, 215 ms deserialize (log lines 5–7). Match.
6. **Missions:** load 1.803 / 0.936 / 2.204 / 2.209 / 1.462 / 1.487 s, teardowns, 84 agents / 2 ships (`mission-transition-probe.json`); seam battle 1.724 / 0.470 s (`world-wrap-probe.json`). Match.
7. **MMR-19 saves on disk:** 121,809 B (sha `5b6b7213…`) and 121,220 B (`8e0b8384…`). Match.
8. **World packages on disk:** 3,599,637 and 3,599,671 B, 13 files each. The MMR-59 package is 3,593,832 B. Match.
9. **Fixture files** (`build/research-*`): `terrain.bin` 4,435,680 / 5,024,609 B; `navmesh.bin` 1,414,542 / 1,417,390 / 4,977,623 B; xscene 70,740 / 71,802 B; 76 / 80 `<game_entity>`. Match.
10. **Census:** 4/3/2 settlements/clans/kingdoms, 19–21 heroes, 17 parties, 905–907 characters, 17 cultures in every world (raw `finalCensus`). Match.
11. **Game start:** 19,476–19,680 ms across all 12 runs. Match.
12. **Throughput:** 2.0 days in 40.39 / 40.31 s (UnstoppableFastForward); 1.0 day in 79.95 s (UnstoppablePlay). Match.
13. **Weather setup** 93.98–239.96 ms; **battle-index build** 49.69–101.59 ms and 340.35 ms. Match.
14. **Code contracts:**
    - `WorldIdentity.Keys` has 13 entries. Match.
    - Eight new-game documents; five `SavedGameDocuments`. Match.
    - 11 Harmony patches in `world-wrap-probe.json patches[]`, applied in `DiagnosticMapScene.Load` and removed in `Destroy` (12348 log `periodic_world_released`). Match.
    - Periodic layout: W 800, originX 400, extent 1600, canonical [400, 1200]. Match.
15. **Hardware** (`evidence/baseline/performance.json`): CPU, RAM, GPU and config sha match. The game's render resolution is not 2560×1600 (S9).
16. **Reference build:** `src/.../obj/Release/MNBL.ProceduralWorld.dll` sha256 = `174fcb0a…`. Match. Harmony 2.4.2.0 pin in `config/runtime-api-lock.json`: match.
17. **Install→register timing:** mislabelled (S1). `RuntimeSurfaceSequenceProbe.cs` lines 74–77 show the stage markers are step-start markers.
18. **Grazing ray:** the accepted run 32896 passes it (M2).
19. **Catalog receipt prefix** in the telemetry note is wrong (S16).

## Must-fix
- **M1, decision line 9:** "Independent review | `evidence/runtime/independent-review.md`". No such file exists anywhere in the repo. Remove the row or point it at a real file.
- **M2, line 29 and unsupported-table line 189:** "**Failed:** a grazing camera ray at a narrow ridge missed by 0.2002 m against 0.1 m (PID 34660/12748)".
  - The same ray (`calibration_200_128:camera`, distance 296.78271484375, identical in 12748 and 32896) has `referenceError` 3.82e-05 m in the accepted run 32896.
  - The 0.2001 m figure is `legacySourceReferenceError`, classified "historical_untruncated_anti_diagonal_observation_only".
  - `surface-coverage-review.json` lists `trueCurrentBlockers: []`.
  - Correction: drop it as a failure and drop the MMR-37/MMR-66 scope for it. Record it as a superseded reference-model error. The real remaining limit is "exact native bin-boundary rounding unresolved".
- **M3, R10 row (line 36), periodic strategy (lines 123–132) and pole convention (line 148):**
  - **(a) Picking.** "picking (terrain)" and "terrain picking therefore see continuous terrain at the seam … proven in run 12348". `world-wrap-probe.json picking.click.path` says "the scene pick itself is not simulated". Mark seam picking as inferred, not observed (MMR-66).
  - **(b) Poles.** "each pole … kept as one row, so it has one logical identity". No probe checks poles. `runtime-capabilities.md` lines 210–213 say only "neither connected nor cropped" and that distortion is "unbounded at the pole row". Mark pole identity unresolved (MMR-64).
  - **(c) Saves.** The seam save was "deserialized … resuming a generated campaign from the file is MMR-49" (`seamSave.reloaded.scope`), and no periodic world was ever cold-loaded. Add both.
  - **(d) Weather.** "Storm wrap across a periodic seam: not implemented; MMR-65" (`runtime-capabilities.md` lines 174 and 183) is missing from R08, R10 and the "What remains" list, although decision 001 requires weather across the edges.
  - **(e) Seam encounters.** "A naval encounter at the seam was not run" (line 297) and "Armies, sieges, raids and embark/disembark at the seam were not run" (line 281). Add both.
- **M4, line 160** ("each half is proven") **and unsupported row 185:**
  - The runtime-registration half (MMR-16, run 32896) never checked whether the engine loaded navmesh faces. Neither the log nor `RuntimeSurfaceSequenceProbe` reads a face count.
  - Every campaign probe used scene slots installed before launch with `Deploy-SceneSlots.ps1`.
  - `backend-callsite-matrix.json explicitUnknowns` lists "Native scene/index/resource behavior for newly generated scene names before process startup".
  - Historical runs found that fresh-name navigation imports returned 0 faces (PIDs 29364/14300).
  - Correction: say that loading navmesh and settlement entities from a scene registered at runtime is unobserved, as part of the MMR-45 scope.
- **M5, load sequence (lines 101–110):** "4. `LoadSaveGameData` … 6. `RegisterSubModuleObjects(true)` … 7. Deserialization".
  - Deserialization happens at step 4. The 8256 log shows `save_deserialized` at +22823 ms, before `generated_registration_dispatch` at +24488 ms.
  - `StartGeneratedLoad` requires `saved.Successful`, and `lifecycle-extension-contract` confirms the binary graph is already read by then.
  - Correction: remove or rename step 7, and add `MBObjectBase.BeforeLoad` before step 6.
- **M6, unsupported-operations table (lines 181–192):** the ticket says to list every still-unsupported mandatory operation, but the table omits items the doc itself names elsewhere. Add rows for:
  - battle-patch map wrap (MMR-64/MMR-42);
  - pole identity and polar projection (MMR-64);
  - canonical state at the save instant plus `BehaviorTarget` (MMR-49);
  - direct `LocatorGrid` callers and `FindPointAroundPosition` (MMR-65);
  - the tie-rule conflict (MMR-64);
  - storm wrap (MMR-65);
  - naval encounter at the seam;
  - resuming a periodic save;
  - generated-settlement location scenes and Mountain/Forest/Snow battle scenes (MMR-42);
  - the private-field weather binding pending MMR-39;
  - re-save after load and the alternating same-process load matrix (MMR-49);
  - autosave and failed save, which are evaluated from IL only.

## Should-fix
- **S1, line 229** ("31 ms register, 47 ms read and optimize") **and telemetry §3 line 63:** the stage markers mark the start of each step, so 31 ms is the file install. Register plus resolve is 21 ms (+90023 → +90044), `Scene.Read` is 47 ms, and optimize is ≤20 ms (the next marker is +90112).
- **S2, line 232 and telemetry §6:**
  - "`Scene.Read` 0.13–0.41 s" actually spans `diagnostic_scene_read_begin` → `diagnostic_scene_loaded`. That is the whole `IMapScene.Load` (CreateNewScene, Read, `PrepareWeather`, `InitializeCaches`, `OptimizeScene`, Tick; `DiagnosticMapScene.cs` lines 121–178). It already includes the separately listed "weather setup 0.04–0.24 s".
  - "Dataset registration takes 2.0–3.9 s" spans GameLoadingScreen → creator installed, which covers all submodule registration and campaign init. In run 8256 it also covers package verification and deserialization.
- **S3, seam 7 (line 50):** "Every caller goes through the interface, so the re-implementation is not bypassed". `backend-callsite-matrix.json routingSeparation.picking` says "MapScreen native Scene raycast/selection/DoesPathExistBetweenFaces; bypasses IMapScene adapter", and there are 32 direct native call sites. Limit the claim to the members actually re-implemented.
- **S4, seam 8 (line 51):** Models is listed after `IMapScene` in a list headed "in call order". `lifecycle-extension-contract` gives InitializeGameStarter → OnGameStart → model snapshots. `src/` has no `AddModel` call, so this seam is unexercised. Move it before seam 4 and mark it as IL-derived.
- **S5, line 174:** "Signatures and hashes are in `api-signatures.txt` and `backend-callsite-matrix.json`". Neither file contains `PairSceneNameToModuleName`, `TryGetFullFilePathOfScene`, `CollectMetadataEntriesEvent` or `GetSaveFileWithName`, or the Harmony targets `FindNextLocatable`, `ProcessTravel` and `CanPartyInteract`; `LoadSaveGameData` is absent from both too. Fix the pointer, and list the 11 Harmony targets and the private `MapScene` fields as dependencies.
- **S6, face groups (lines 61–64):** Steppe 5 and Lake 8 are MMR-17 test stamps (`build_bootstrap_world.py` lines 137–138: the obstruction wall and the enclosed lake). The builder's terrain classes are Plain 1, Mountain 7 and CoastalSea 18, plus OpenSea 19 in the climate fixtures. Do not freeze these as the product's vocabulary.
- **S7, line 18:** "its single proven step (writing an owned file into a fresh scene) is part of the selected backend anyway". PID 25128 rewrote a pre-indexed fixed filename (`runtime-capabilities.md` line 800), which the selected backend forbids because names are immutable. Fresh filenames imported 0 faces.
- **S8, workarounds and API list:**
  - Add `DiagnosticNoAutoSaveManager` (autosaves disabled) and the HoldPaused re-pause to the probe-only setup.
  - `SetAbilityOfFacesWithId` plus `RefreshFaceCaches` is probe-only. The navigation probe's known gaps say gate and port caches are not refreshed, and listing it contradicts the rule that navigation data is never mutated while the campaign runs. Qualify it or remove it from lines 169 and 175.
- **S9, lines 203–205:** "2560×1600" is the desktop resolution. `performance.json settings` shows the game at display 1600×1024, with shadow and AA settings. Add those, and add telemetry §11.2's note that nothing proves the settings were unchanged since the baseline.
- **S10, evidence provenance:** R01 (`e326e3fe`, bridge `0d8bf53a`), R02 (`c7a353ae`, encoder `21da9dc8`), R03, R04, R09 (`19fa06b0`) and R10 (`82406613`) all ran on earlier trees. `DiagnosticMapScene.cs` was modified after run 12348, and the deployed `worldbridge.sha256` is now `2a8b3d63…`. The ticket asks to rerun only stale evidence, so record a staleness judgement for each R.
- **S11, omitted limits by row:**
  - **R03:** gate and port caches not refreshed; the player's return voyage was not run.
  - **R04:** town meshes not visible ("nameplates only"); `fighter_sturgia` excluded; `dead_lord_7_1` and `GetBodyguardOfCulture` still have live consumers beyond the 2-day window.
  - **R05:** no re-save after load; anchors and storms are outside the snapshot.
  - **R08:** storm spawn scale is still Calradia's (MMR-39); the saved weather-node index (MMR-49); authored `flowmap.dds` untested.
  - **R09:** name an owner for the Mountain/Forest/Snow substitution. Also reconcile with `runtime-capabilities.md` lines 392 and 409 ("Periodic seams … block R07"), since patch sampling still does not wrap.
  - **R10:** the camera-field heuristic misses `_lastUsedIdealCameraTarget` (MMR-66); tracks and the camera x-clamp were not exercised; the locator image pass was tested from the east side only; AI destination choice is indirect only.
- **S12, line 227:** "the same request took 5.1–8.6 s". The offline request was `9e49bb57af34faf2` and the in-game one `f204b0ce8655b7ed`; only the `world.json` output is identical. Say "same output".
- **S13, line 263:** "a new game or load takes about 10 minutes". A periodic load was never measured, so mark that part inferred. The "faster than linear" claim rests on one pair of points that also differ in geography.
- **S14, lines 246–247:** entity counts are authored counts from receipts and the xscene, not engine readback (telemetry §11.6). MMR-16 saw engine-created entities beyond the authored ones.
- **S15, line 136:** "Diagnostic minimum" is the configuration that was tested. No lower bound was probed, so label it that way.
- **S16, telemetry line 39:** "catalog receipt 27bca57ee00ab628" is not a prefix of the hash. sha256 of `campaign_catalog-10212.json` is `27bca57ee00ae08a…` (also `bootstrap-probe.json inputs.catalogReceipt`).
- **S17, input-hash table (lines 215 and 217):** the runtime_surface-32896 seed rows and the MMR-18 rows give no input hashes. The telemetry already has the MMR-18 placements: `008a5d00…`, `b9a93238…`, `963802f2…`, `e635214d…`.

Files reviewed: `C:\Users\davgo\Documents\GitHub\MNBL-modding-repo\docs\runtime-backend-decision.md` and `C:\Users\davgo\Documents\GitHub\MNBL-modding-repo\task-notes\mmr21-telemetry.md`.

---

## Disposition

Every finding was applied to `docs/runtime-backend-decision.md` (and the telemetry note where named) before submission. No game run was needed.

| Finding | Disposition |
|---|---|
| M1 | Fixed. This file now exists, holding the verbatim report and this disposition, and the decision doc points at it. |
| M2 | Fixed. The grazing ray is recorded under R02 as a superseded reference-model error (3.8e-05 m in accepted run 32896; `trueCurrentBlockers: []`). Its MMR-37/MMR-66 failure scope and its unsupported-table row are removed. "Exact native bin-boundary rounding is unresolved" is added to R02 and to the calibration contract. |
| M3 | Fixed. In R10, the periodic strategy and the pole convention: (a) seam picking is marked inferred (MMR-66); (b) pole identity is marked unresolved (MMR-64); (c) resuming a periodic save and cold-loading a periodic world are added (MMR-49); (d) storm and weather wrap are added to R08, R10, "what remains" and the unsupported table (MMR-65); (e) the naval encounter at the seam (MMR-53/65) and armies, sieges, raids and embarkation at the seam (MMR-65) are added. |
| M4 | Fixed. "Each half is proven" is replaced. Loading a navmesh and settlement entities from a runtime-registered scene is unresolved (PIDs 29364/14300, `explicitUnknowns`), owned by MMR-45, with the startup-indexed slot pool as the bounded fallback. R03 now states that every route ran on slots installed before launch. |
| M5 | Fixed. Load sequence: deserialization is step 4 (`LoadSaveGameData`, +22823 ms), and `MBObjectBase.BeforeLoad` precedes `RegisterSubModuleObjects(true)` (+24488 ms). |
| M6 | Fixed. Every listed item has its own row in the unsupported table, plus runtime navigation obstruction. |
| S1 | Fixed in both files. Install 31 ms, register and resolve 21 ms, `Scene.Read` 47 ms, optimize ≤ 20 ms (step-start markers). |
| S2 | Fixed in both files. The spans are relabelled: the whole `IMapScene.Load` includes weather setup; "loading screen → creator installed" covers all registration and initialization, and on load also package verification and deserialization. |
| S3 | Fixed. Seam 8 limits the claim to the re-implemented members and names the bypassing native callers (32 direct call sites). |
| S4 | Fixed. Models moved to seam 4, inside `InitializeGameStarter` before `OnGameStart`, and marked IL-derived and unexercised. |
| S5 | Fixed. The pointer now names each source file, states that the save-metadata calls are in no signature inventory yet (MMR-50), and lists the 11 Harmony targets and the private fields. |
| S6 | Fixed. The face groups are Plain 1, Mountain 7, CoastalSea 18 and OpenSea 19. Steppe 5 and Lake 8 are recorded as MMR-17 test stamps, and the full vocabulary is MMR-26/38. |
| S7 | Fixed. The prefab candidate now records that PID 25128 rewrote a fixed, startup-indexed name, which the backend forbids for loaded scenes. It survives only as the M4 fallback. |
| S8 | Fixed. `DiagnosticNoAutoSaveManager` and HoldPaused were added to the probe-only setup. The face toggle and `RefreshFaceCaches` moved to probe-only, with the gate/port gap, a lifecycle-rule note and an MMR-38 row. |
| S9 | Fixed. Desktop and game display resolutions, shadow, antialiasing and texture settings are listed, with the note that nothing proves the settings were unchanged. |
| S10 | Fixed. A new "Evidence currency" section gives a staleness judgement for each R (code file times, deployed pins, re-exercise on `174fcb0a`). Nothing was judged stale or contradictory, so nothing was rerun. |
| S11 | Fixed. Every listed limit was added to its R row. The MMR-60 "periodic seams block R07" note is reconciled in R09: the patch map not wrapping is a discontinuity only, because parties are canonical. |
| S12 | Fixed. The text now reads "generation producing the same `world.json`", with the offline request hash. |
| S13 | Fixed. The periodic load is marked inferred, and the scaling claim is qualified as a single pair that also differs in geography. |
| S14 | Fixed. The column is now "authored entities", noting that the count is not engine readback. |
| S15 | Fixed. The section is relabelled "tested configuration, not a probed lower bound". |
| S16 | Fixed in both files: `27bca57ee00ae08a…`. |
| S17 | Fixed. Input hashes added for 32896 (calibration source, heightfields, encoder) and for each MMR-18 run (placements). |

Not checked by the reviewer, verified by the author through Dark Mechanicus:
- MMR-21 is a prerequisite of MMR-25, MMR-29 and MMR-30. The first draft wrongly said they depend only on it. MMR-29 also waits on MMR-26/27/28, and MMR-30 on MMR-27. The release section now says that only MMR-25 becomes ready.
- No ticket in the saved plan carries an `r06` tag. The tags run r01–r05 and r07–r10.
