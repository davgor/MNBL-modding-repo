# Claude handoff — user-requested usage checkpoint, 2026-10-13

## Incomplete: generated population has not booted

User requested stopping with approximately 3% usage remaining and leaving notes for Claude. Preserve this checkpoint; do not label the epic or MMR18 complete. Terrain rendering and Sandbox on generated terrain are proven. The new fully generated population/ownership integration is implemented but NOT deployed or runtime-tested. Recruitment, trade, usable ports, two populated worlds in one process, and save reconstruction remain unproved.

Workspace: `C:/Users/davgo/Documents/GitHub/MNBL-modding-repo`, branch `codex/fwgc-procedural-campaign`. Most repository contents are untracked; all changes are preserved on disk, not committed. Do not clean/reset. No game/editor process was found at checkpoint. Last owned probe is PID10212, already closed and collected. No pending installation: free War Sails editor content was installed and verified earlier.

## Accepted evidence — do not rerun old probes

- MMR13–15 accepted: editor prerequisites, pinned API research, generation-menu lifecycle.
- **MMR16 accepted** 2026-10-13 18:23 UTC. Aggregate `evidence/runtime/surface-probe.json`, SHA256 `ba7cbfc631d2be3a854e5efc5f4f789a7f6eb781e090b73bd768759eeeb128ee`; independent review `evidence/runtime/surface-coverage-review.json`, SHA256 `1761acbce310a6fbe684b5e88ed6e2ba8c50a7b4be6ef3d3d13ba4db516d2190`. Keep both frozen.
- `evidence/runtime/in-game/runtime_surface-32896*`: calibration plus seed42/800m and seed43/640m, two zoom views each, grounded queries and corrected independent terrain oracle, normal exit, no changed saves. 774 measurement rows; maximum ray/reference error ~0.000344m. Earlier seed43 real click is independently covered by the review; no duplicate run required.
- `evidence/runtime/in-game/sandbox_boot-25008*`: actual paused player party strategic visual anchor agrees with generated terrain; ~0.00902m independent-source error. Sandbox retained official settlement scaffolding. This is not proof of generated population or animated foot contact.
- `evidence/runtime/in-game/campaign_catalog-10212*`: read-only active-module merged XML capture, 24 documents, final OK, normal exit, no save changes. Data: `C:/Users/davgo/AppData/Local/MNBL.ProceduralWorld/autorun/campaign-catalog-10212`. Do not recapture without a specific missing input.
- `docs/runtime-capabilities.md` has the accepted terrain scope and retained failures.

Never repeat PID27392's combined native terrain exports: they crashed and have no identified gameplay consumer. Never overwrite a loaded scene's terrain.bin; the engine retains file handles. Use immutable new scene names. Old PID32944 raw error remains unchanged; review distinguishes corrected oracle from original receipt.

## Immediate next action before ANY generated boot

Read `build/research-bootstrap18/dataset-handoff.json` and its referenced receipt. Actual installed-catalog closure now passes offline, but **root integration currently gathers only eight live default kinds; the dataset requires ten**. `GeneratedCampaignRegistration.cs` lines ~85–88 gathers Skill, Trait, Policy, BuildingType, VillageType, ItemCategory, Feat, CharacterAttribute. Worker additionally requires **SiegeEngineType and SkeletonScale**. Resolve their concrete installed types/registries and readiness, then supply the live inventories. Do not replace live checks with the offline provenance list. This is a known integration gap, not a native crash to probe.

Use **final42/final43**, NOT release42/release43 (names are misleading). Both workers confirm final directories are the validated latest fixtures; `checks.json` explicitly references them.

- `build/research-bootstrap18/final42/placement.json`, SHA256 `008a5d0074bd371adb99f32ab6d2df07c04b484dbbd3d2eeaf64c3c1b9d6abfb`
- `build/research-bootstrap18/final43/placement.json`, SHA256 `b9a93238675fff918092a9f41dde99643b53eba72fafdef68b6d87bb51520c70`
- Their `build-receipt.json` files bind five native scene files. Scene names `mnbl_generated_0000002a` / `mnbl_generated_0000002b`; world IDs `mnbl_0000002a` / `mnbl_0000002b`.
- `build/research-bootstrap18/checks.json`: 104 offline scene checks passed; native runtime unproved.
- `build/research-bootstrap18/support-check/receipt.json`: isolated support compile, 20 pinned assemblies, 16 structural checks passed; native runtime unproved.
- Dataset receipt `build/research-bootstrap18/test-7b43252206ce4cdebf53d6de68c17976/receipt.json`: 55 pure managed checks, both worlds, 24 source hashes, 260 declaration records, 18 descriptor assertions. See worker handoff for precise scope and optional-reference IL justifications.

## Implemented source and ordering

All C# paths below are under `src/MNBL.ProceduralWorld/`.

- `CampaignBootstrap/GeneratedCampaignDataset.cs`: pure managed dependency closure, explicit reusable templates versus owned world objects; two towns, two villages, two clans/rulers/kingdoms plus player aliases. Selected culture XML start point is replaced before character creation. Documents: SPCultures, NPCCharacters, Heroes, Kingdoms, Factions, WorkshopTypes, LocationComplexTemplates, Settlements. Starting-party specifications are not runtime-created-party proof.
- `CampaignBootstrap/GeneratedCampaignRegistration.cs`: binds catalog receipt, source hashes, placement and installed scene hashes; gathers ready live defaults, builds/preflights dataset, loads ordered XML directly. Fix ten-kind gap above before running.
- `CampaignBootstrap/DiagnosticSandboxBootstrap.cs`: new `StartGenerated`; substitutes only SandBox's world-object registration before CampaignObjectManager initialization, preserves other submodules including NavalDLC. Removes exact `BackstoryCampaignBehavior` before event registration because installed IL dereferences official lords/town. No blanket object clearing. Explicitly rejects saved-game registration: load/reconstruction remains unfinished.
- `Diagnostics/CampaignBootstrapProbe.cs`: read-only both-manager censuses with IDs, readiness, ownership, references, runtime identity witnesses. Identity hash is not collision-free proof.
- `CampaignBootstrap/DiagnosticMapScene.cs`: per-world scene selection, owned weather before initialization, generated cache registration in AfterLoad after campaign bounds/cost setup. Remains derived from SandBox.MapScene, required by Naval wrapper's concrete cast. Battle scene index is deliberately unsupported for generated mode.
- `CampaignBootstrap/GeneratedMapSupport.cs`: dry/calm owned diagnostic weather buffers; fresh land/naval/all caches; exact Naval model guards, generated settlement/gate/port validation. No stock geography caches. Snow, wake and production weather unsupported. Uses weak map state, no static old-map retention.
- `Automation/GeneratedCampaignBootProbe.cs`: `generated_campaign`, one or two worlds, registration+census and map checks; normal `MBGameManager.EndGame()` between worlds; no saves. Checks four settlements, owned rulers/clans/kingdoms, actual ruler parties, player start, ports/bindings. This is initialization evidence only, not recruitment/trade acceptance.
- `Automation/CampaignCatalogProbe.cs`: read-only capture already proven by PID10212.
- `tools/scenebin/build_bootstrap_world.py`: fresh scene/nav authoring, generic shared mesh/material assets, no official settlement geography or distance caches. Owns no game/editor state.

Full current-module scratch compile passed with C#7.3 and warnings-as-errors around18:47 UTC, before the final dataset handoff; final integrated build is still required. Last full Build/Deploy was for catalog PID10212. **Current deployed binary and build receipt are stale relative to source.**

## Resume sequence

1. Reconcile latest worker handoff, source hashes, and live-default gap; review installed IL if a type/order is uncertain. Research is in `build/research-bootstrap18/{plan.json,native-dependencies.json,root-startup.il.txt,startup-consumers.il.txt,load-order.il.txt,cache-weather-details.il.txt,end-game.il.txt}`.
2. Ensure no writer remains active. Verify five native file hashes and install final42/final43 into the owned module SceneObj directories, using the project's established module path; do not overwrite anything currently loaded.
3. Run `tools/Build.ps1`, then `tools/Deploy-Dev.ps1`. No concurrent source edits during the hashed build.
4. Run ONE `generated_campaign` world first via `tools/Start-OwnedProbe.ps1`; use absolute paths for placement and catalogReceipt. Args: worldCount=1, seed=42, culture=`empire`, heroTemplate=`spc_legion_of_the_betrayed_leader_0`, townPrototype=`town_EN2`, villagePrototype=`village_EN1_3`, placement=final42/placement.json, catalogReceipt=evidence/runtime/in-game/campaign_catalog-10212.json, holdSeconds=25. The latter paths need workspace prefixes. Preserve build receipt beside the probe evidence immediately after launch. Collect after normal closure with `tools/Collect-OwnedProbe.ps1`.
5. Investigate any NEW concrete initialization failure before another run. Then prove gameplay interactions and worldCount=2 with secondSeed=43/secondPlacement=final43/placement.json. A boot or offline route check is insufficient for MMR18/17 acceptance.
6. Address saved-game registration/reconstruction requirements explicitly. Do not quietly redefine this as optional.

UI: full game control is authorized. Root is sole game/editor owner. Use computer-use skill and `mcp__node_repl__js` / `@oai/sky`; fresh window/PID observation before actions, one action then fresh observation. No old shell UI probe. Workers never deploy or operate UI.

## DM continuation

Epic `ep_01m4fpnf646t254mnh69jhz107`; run `rn_01m4h4jk5wssr7x79xjmrp8ypg`.

- MMR16 ticket `tk_01m4fqfbgkw3f1t5mfhbe8wq6b`, accepted attempt `at_01m4v345ag91214wang6kg1ete`.
- MMR18 ticket `tk_01m4fqfbgkan15ca1rykrzpxt0`, active attempt `at_01m4v78t5ttwqrjbsd33pk97fb`; last heartbeat18:49 UTC, 3600-second lease. Reconcile current DM claim/lease on resume; no claim tokens are stored in this note. Do not mark accepted. Acceptance requires generated ownership, recruitment/trade, census excluding prohibited vanilla world instances, and distinct worlds without stale references.
- MMR17 ticket `tk_01m4fqfbgk3hy3ak115n8w83gp`, unclaimed, waits for18: actual player/AI land/sea/embark/disembark routes across two worlds and obstruction-sensitive routes.
- Continue downstream epic tickets after dependencies; no goal/automation/new chat was created.

Workers asked to stop and release at user checkpoint: Harvey `01a133ad-f54d-7aa0-af5e-7570bd5c4970` (support/builder, confirmed frozen/released), Boyle `01a1353e-062f-7932-b7d6-4490433e5c60` (dataset; see final checkpoint below). No new runtime work was started for this wrap-up.

Final worker checkpoint: Boyle confirmed source frozen and ownership released. Actual-catalog A/B preflight has no remaining offline failures; 55 managed checks, 18 placement assertions and all 24 catalog hashes passed. Dataset source SHA256 is `550520819c6711b8be02acc77e5341021c2f271b8a057f6e8494d18b8adb6699`. Both workers are finished; no delegated work remains running. No additional tests were performed during this checkpoint. Live ten-kind inventory integration remains the immediate next action.
