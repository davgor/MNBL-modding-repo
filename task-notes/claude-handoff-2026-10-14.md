# Claude handoff — 2026-10-14 (supersedes the 2026-10-13 checkpoint for current state)

Run `rn_01m4h4jk5wssr7x79xjmrp8ypg`, epic `ep_01m4fpnf646t254mnh69jhz107`, plan revision 3 (thinned; lean verification: one independent review per ticket, batched fixes, targeted runs). Claim tokens are never stored in the repo; reconcile claims through Dark Mechanicus on resume.

## Accepted this session
- MMR-24 (C++ worldbridge identity), MMR-60 (generated-world mission transitions; `evidence/runtime/mission-transition-probe.json`, run 22868).

## MMR-63 periodic east/west seam — ACCEPTED 2026-10-14 17:47 UTC (follow-ups commented on MMR-49/64/65)
- Evidence: `evidence/runtime/world-wrap-probe.json` (run `generated_campaign-12348`, module `82406613…`, all 23 checks passed), summarizer `tools/summarize_wrap_probe.py`, docs top section of `docs/runtime-capabilities.md`, research `task-notes/mmr63-periodic-seam-research.md`.
- Runtime layer: `CampaignBootstrap/PeriodicWorld.cs`, `PeriodicSeam.cs` (rebase pass + order-target canonicalization), `PeriodicPatches.cs` (11 Harmony patches), periodic queries in `DiagnosticMapScene.cs`; probe `Diagnostics/WorldWrapProbe.cs` (`wrapProbe=1`, `wrapTravel=0` skips travel legs).
- Fixture `build/research-wrap63/fixture-c` (scene `mnbl_generated_00063042`, seed 405570) must be installed with `tools/Deploy-SceneSlots.ps1` before a rerun. A full run takes ~14 min (All navigation cache ~419 s).
- Declared limits: labels/selection/nameplates (MMR-66), resume from save (MMR-49), naval encounter at the seam not run, tracks/weather not exercised, 121 planar heuristics unverified.

## MMR-59 environment rebind and MMR-19 save identity — ACCEPTED 2026-10-14 19:06 UTC (final evidence on module 174fcb0a, one shared build)
- MMR-59: `evidence/runtime/environment-rebind-probe.json` (run 24212, 23/23), fixtures `build/research-env59/w42z|w43z` (two-zone climate variants of w42/w43; same installed scenes).
- MMR-19: `evidence/runtime/two-world-load-matrix.json` (process 1 run 31624, process 2 run 8256, 17/17), worlds w42/w43; packages under `%LOCALAPPDATA%/MNBL.ProceduralWorld/worlds`; saves `mnbl_mmr19_*` kept in Game Saves.
- Both reviewed (accept with should-fix); fixes applied and verified in the final runs, then accepted.
- Every generated world now writes its package and save identity at creation (GeneratedCampaignBootProbe).

## MMR-21 backend decision — ACCEPTED 2026-10-14 19:46 UTC
- Deliverables: `docs/runtime-backend-decision.md` (backend = owned native scene bundle + `SandBox.MapScene` subclass; contract matrix, frozen contracts, workarounds, native APIs, unsupported operations with owners, provisional limits, promotion map) and `evidence/runtime/independent-review.md` (reviewer report).
- Telemetry extraction: `task-notes/mmr21-telemetry.md` (no benchmark run). Gaps: game-process RAM, per-probe hardware/settings, offline scene construction time, MMR-19 save duration, in-memory sizes.
- Main provisional limit: navigation cache build (35–75 s per 32,768-face world; 567 s for the 131,072-face periodic world; rebuilt on every load) → MMR-41/MMR-56.

- Review: accept with fixes (M1-M6, S1-S17), all applied; dispositions in the review file. Key correction: navmesh/entity loading from a scene registered after launch is UNOBSERVED (MMR-45; fallback = startup-indexed slot pool).

## MMR-25 projection — ACCEPTED 2026-10-14 20:45 UTC (review accept-with-fixes, all applied; `evidence/fwg/projection-review.md`)
- `docs/world-projection.md`: periodic-equirectangular v1 (W 800, H = W/2, central meridian 0, vertical 0.01/m above effective sea level, R10 strips/pole margin W/2). Code: `src/WorldBridge/src/{projection,source_nodes,projected_world}.cpp`, CLI `worldbridge --project`, reader now keeps radius/sea level/roads/rivers. 93 unit checks (CTest `projection`); real exports verified by `tools/summarize_projection.py` -> `evidence/fwg/projection-export.json`.
- Sea-datum calibration run `generated_campaign-4768` (`Diagnostics/SeaDatumProbe.cs`, `seaDatumProbe=1`): engine water level -100 everywhere on generated maps (no water entity) -> no native water surface at z=0; MMR-39 must add one (official naval Main_map uses `global_water`).
- worldbridge.exe hash is now 5181fa78… (deployed); no static pin needed it. terrain.bin carries z + below-datum + water_type rasters; policy calibration path is relative to the policy and verified at load.
- Open for MMR-26/37: physical domain 1600x1200 vs square-only surface encoder.

## MMR-26 terrain compiler — ACCEPTED 2026-10-14 21:36 UTC (review accept-with-fixes, all applied; `evidence/fwg/terrain-review.md`)
- `docs/terrain-compiler.md`; `worldbridge --compile-terrain` (seam-aware bilinear height, nearest categories, intent, coastline coherence ±0.05, rock = max(biome, slope), 16-bit global range). Encoder accepts `--surface-weights`. Policy `worldgen/policies/terrain-materials.json`, calibration `worldgen/calibration/terrain-import/`, schema `worldgen/contracts/terrain-metadata.schema.json`.
- Projection policy poleMarginRatio 0.5 -> 0.75 (square 1600x1600 physical domain); projection evidence regenerated.
- Evidence `evidence/fwg/terrain-compile.json` (independent re-derivation of size 129; sizes 129/257/513/1025 fidelity -> source size frozen at 129). Visual probe `native_render-4988` (5 views, slot `mnbl_terrain26_s129` deployed). Found: 75% polar padding; spikes from 2.5x vertical exaggeration.

## MMR-27 campaign semantics — claimed 21:36 UTC, in review
- `docs/campaign-semantics.md`; `worldbridge --compile-campaign` -> id-map.json + campaign.json (towns/villages/castles/kingdoms/ruling clans/heroes/ports, typed pending slots, provenance). Stable ids `mnbl_<seedhash8>_<kind>_<hash10>`.
- Policies `worldgen/policies/campaign-content.json` (17 generator hero roles, hamlet roles, statuses, occupations, names, minima) and `culture-presentations.json` (12 civilizations -> official cultures). Schema `worldgen/contracts/id-map.schema.json`.
- Finding: the ticket's '24-city ceiling' is only the explicit settlement_count cap; inferred-settings worlds place by habitat packing (45 cities at 129). hamlets_per_core is not a hard cap (cores have 5).
- Evidence `evidence/fwg/campaign-semantics.json` (independent re-derivation for 33/129/257; 513/1025 summarized; repeat identical).

## Next
- After MMR-27: MMR-28 (needs 25/26/27/17) and MMR-30 (needs 27/21). Then MMR-29.

## Workflow (user, 2026-10-14)
- Big batches: all spec changes → one test run → all fixes → one confirming run → done. Fail-soft probes so one run reports everything.
