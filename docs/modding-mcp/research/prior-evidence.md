# Prior evidence reconciliation: MMR-391

Full API coverage and fresh runtime behavior remain unproved. Only targeted file reads, hashes and PE/CLI MVID parsing occurred; no target, SDK tests or Ghidra was run.

Receipts: `evidence/modding-mcp/research/identity/prior-evidence.json`. Captured 2026-10-31T11:46:28.826458+00:00.

## Historical evidence

- **planning-rea-2026-10-30.json: matching-build** (static managed metadata/CIL or native pseudocode/registration). Exact target comparisons only; no new runtime proof.
- **api-audit-rea-2026-10-30.json: matching-build** (static managed metadata/CIL or native pseudocode/registration). Exact target comparisons only; no new runtime proof.
- **native-handler-rea.json: matching-build** (static managed metadata/CIL or native pseudocode/registration). Exact target comparisons only; no new runtime proof.
- **ui-resource-rea.json: matching-build** (static managed metadata/CIL or native pseudocode/registration). Exact target comparisons only; no new runtime proof.
- **publish: unknown** (historical editor UI publish and file verification). Editor host identity not pinned; original dll-before.json hashes Shipping_Client preservation targets only.
- **publish2: unknown** (historical editor UI publish and file verification). Editor host identity not pinned; original dll-before.json hashes Shipping_Client preservation targets only.
- **rea-red-team-2026-10-16: matching-build** (historical static inspection and isolated offline harness). Exact target comparisons only; no new runtime proof.
- **terrain-export-crash-27392: unknown** (historical game native crash reported in review). Combined GetTerrainNodeData/GetTerrainHeightData sequence crashed; individual culprit and editor lifecycle not isolated.
- **occlusion-entity-move: unknown** (historical game runtime fault/bisect). Reported c0000005 at client Native+0x55e625 after moving occlusion-bearing entities following CheckResources; recreate/frozen controls avoided fault.
- **animation-run30840: stale** (historical shipping playback plus post-run file check). Current probe MVID d789c4a5-a676-4925-a641-6555e300e2bf differs from run30840 MVID 4267abec-e4ba-4746-94b7-47cbe3ba79f8. Six package/data hashes still match; per-run engine identity remains unknown. Historical playback remains valid for its original probe, not current probe acceptance.

## Reuse limits

- The two publishing report copies match originals exactly. Their original dll-before manifests name Shipping_Client binaries, not editor binaries. These are client-preservation checks and cannot establish the editor host identity. No headless/API invocation or gameplay-rendering claim follows from historical UI publishing.
- The combined terrain-export crash does not isolate the individual call or editor lifecycle. Occlusion movement/recreation evidence remains historical; never reuse shipping native offsets in the editor.
- Run30840 retained log/capture hashes all match, and six current package/data files match. The current probe module has a different MVID, so that execution is stale for the installed probe. No fresh screenshot review or playback occurred. Preserve explicit src_range=(0,60) for the 61-frame original clip; do not generalize to cloth, morphs, combat hits or editor import.
- SDK source is matching-build at `211907728117ec836998b72908f84238a2d179c2`, clean, MIT. Pin/vendor reviewed code and preserve license/dependencies; the ignored clone cannot satisfy reinstallability. Tests were not run.
- Static managed/native target matches preserve attribution only. Callback names, generic executors, constructor-like registration and Qt wiring are not proven argument, invocation or durable-save contracts.

## Module order and toolchain

The JSON hashes runtime-api-lock, tools.lock, development runbook, launch script and module manifest. Eighteen game-file hashes match the runtime lock. The animation log records historical module order. Configuration and historical launch lists do not certify the currently loaded order or working toolchain; root owns readiness and installation gating. Current MVIDs were parsed from bytes without loading assemblies.

## Actionable gaps

- Root must check current readiness, loaded module order, hooks, installation lease and content completeness; none inspected live here.
- Publishing preservation hashes are client files, not editor-host identities.
- Locate exact per-run engine receipts for terrain PID27392 and occlusion controls before labeling matching-build.
- Animation package/probe matching cannot certify current engine behavior or all SDK writers.
- Reproduce declared toolchain/dependency inputs; historical locks do not prove the currently available compiler works.
- Missing/changed source evidence is recorded separately from binary drift. Editor/client differences are distinct target modes, not automatically stale builds.
- Re-run the original-clip regression with the current probe or recover the exact historical module before treating run30840 as current module acceptance. Do not redeploy implicitly.
