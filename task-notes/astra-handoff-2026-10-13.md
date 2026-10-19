# Astra handoff from Claude, 2026-10-13 (continues claude-handoff-2026-10-13.md)

## Update (22:20Z)

- **MMR-22 accepted** (attempt 2, `at_01m4vmc8jhfsec4x50pn74h6gn`) after an independent re-review. Open residuals:
  - R1: the bridge binary hash (1dc1d8d8…, reproducible via `/Brepro`) is not pinned in any lock.
  - R2: `read_lock` does not enforce `producer-lock.schema.json`, and it accepts a flattened `"producer.sha256"` key.
  - The C# host still launches fwg.exe directly.

## Update (07:50Z, 2026-10-14)

- **After the user's gaming pause (both leases expired):**
  - MMR-60 was reconciled as resubmitted.
  - MMR-63 was reconciled as abandoned and reclaimed as attempt 2 (`at_01m4wevqgxvgpm7ffegs3aby6t`).
- **MMR-60:**
  - A second independent review confirmed fixes 1, 2 and 4–11. The camera check was weak (distance-based); it now asserts that the settled camera frame has the party on its view line (lateral error ≤ 0.1).
  - The return bound is per domain (land 0.625, sea 1.875).
  - New accepted run: `generated_campaign-13672`. The reviewer's confirmation is pending, then accept.
- **MMR-63 phase 1 code is applied, not yet built or run:**
  - `PeriodicWorld.cs`, `PeriodicSeam.cs` (rebase behavior plus camera shift), `WorldWrapProbe.cs` (`wrapProbe=1`).
  - Periodic `IMapScene` path and distance methods in `DiagnosticMapScene`, and the plumbing from placement `periodic` through registration and bootstrap.
  - Fixture: `build/research-wrap63/fixture-c`, world `mnbl_00063042`, seed 405570 (0x63042). Install it with `Deploy-SceneSlots -Map` together with the 42/43 scenes. Probe args: `worldCount 1`, `seed 405570`, placement `fixture-c/placement.json`, `wrapProbe 1`.
  - Phase 2 drafts in the scratchpad (`mmr63/PeriodicPatches.cs`): Harmony patches for the LocatorGrid distance filter and `CanPartyInteract`.
  - Phase 2 needs the Bannerlord.Harmony dependency: `SubModule.xml`, the `Start-OwnedProbe` module list, the csproj reference, and an API pin with a workshop scope.

## Update (02:50Z, 2026-10-14)

- **MMR-24 accepted** (three review passes; follow-ups A–E verified).
- **MMR-60 implemented and passing; independent review next.**
  - Run `generated_campaign-19676` covered both worlds with a field battle, a town_1 street visit and a naval battle at the west edge with retreat.
  - Every battle scene came from the generated patch deterministically, and returns preserved position, domain, camera and the world fingerprint.
  - New files: `CampaignBootstrap/GeneratedBattleTerrain.cs` (battle-index map installed in AfterLoad) and `Diagnostics/RuntimeMissionProbe.cs` (`missionProbe=1`); `tools/summarize_mission_probe.py` writes `evidence/runtime/mission-transition-probe.json`.
  - Docs: the new section at the top of `docs/runtime-capabilities.md`.
  - Gotchas:
    - The first naval run in world 43 put a sea party in a cell whose centre was land, so the index was land-only and the scene pick fell back to random. Fixed with 3×3 sampling; cells touching sea use one of the 8 land+naval indices.
    - Town missions also have an enemy team, so "combat" must come from the encounter type.

## Update (02:40Z, 2026-10-14)

- **MMR-24:** re-review found no blocking defects. Follow-ups fixed:
  - path-free parser reasons for any `--work` spelling (canonical/8.3/case);
  - exact 2^53 check for every number spelling, and in `build_request`;
  - non-ANSI `--work` refused up front (`unrepresentable_path`);
  - `--new-request` requires the three policies and the ceiling;
  - `work_collision` JSON refusal.
  - Tests 75/75. Evidence rerunning, then submit/accept.
- **MMR-60 claimed** (`at_01m4w2dy2m8rdw0q08xa09wdwm`). Map of the ground:
  - Probe hook: `Automation/GeneratedCampaignBootProbe.cs`. Add a `missionProbe=1` block after the navigation block (~line 103), same shape as `GeneratedNavigationProbe.Run(context, boot, worldId, placementPath, report)`. `boot.Tick()` every frame; `Travel` pattern in GeneratedNavigationProbe 455-509.
  - Reusable official-content steps: `Automation/BaselineCombatSteps.cs` covers:
    - `Encounter` (199-251): `PlayerEncounter.RestartPlayerEncounter`, then `StartBattle`, then the `encounter` menu's `attack`;
    - `ObserveMission` (253-328): `FinishDeployment`, then retreat via `OnEndMissionResult`, then `PlayerEncounter.Finish`;
    - `RunNaval` (92-197): port menus, `GrantShip`, `sail_option`.
  - Settlement visits are in `BaselineMissionSteps.Visit` (39-168).
  - **Key finding:** `DiagnosticMapScene.Load` leaves `_battleTerrainIndexMap` null in generated mode (DiagnosticMapScene.cs:146-152). So `GetMapPatchAtPosition` returns sceneIndex 0, and no SandBox/NavalDLC battle scene lists map index 0. `DefaultSceneModel.GetBattleSceneForMapPatch` then hits its FailedAssert fallback and picks a random scene matching the terrain type. That is the silent failure the ticket forbids; the fix route is `docs/formats/scene-bundle.md` row 6 (write the private fields by reflection after Load).
  - Generated worlds have no bandits and no wars; a field battle needs a synthetic war or hostile party. Player starts at the town_1 gate with no ships.
  - Location scenes come from the prototypes:
    - town center `empire_town_q`
    - port `empire_shipyard`
    - arena `arena_empire_a`
    - village center `empire_village_x_navalraid`
  - Map camera via reflection: `ScreenManager.TopScreen` (NavalMapScreen : MapScreen), then `MapCameraView` (`CameraFrame`/`CameraDistance`/`CameraBearing`/`IdealCameraTarget`).
  - No decompiler is installed. API lookups:
    - `build/scenetrace/il/*.tsv` (921 MB call index; grep it);
    - `build/research-baseline/ApiTrace.ps1 -Mode methods -Needles @('Ns.Type::Method') -Out x.il.txt`;
    - `tools/Inspect-Engine.ps1 -ExtraTypes`.
  - Run: Build, then Deploy-Dev, then `Deploy-SceneSlots -Map 'build/scene-install/mnbl_generated_0000002a=SceneObj/mnbl_generated_0000002a','…2b=…2b'` (already installed). Then `Start-OwnedProbe -Probe generated_campaign -ProbeTimeoutSeconds 1800 -ArgsJson {worldCount 2, seed 42/43, culture empire, heroTemplate spc_legion_of_the_betrayed_leader_0, townPrototype town_EN2, villagePrototype village_EN1_3, placement final42e/43e placement.json, catalogReceipt evidence/runtime/in-game/campaign_catalog-10212.json, …}`, then Collect-OwnedProbe.

## Update (02:05Z, 2026-10-14)

- **MMR-24 review rework** (first independent review found blocking defects; same reviewer re-reviews). Changes:
  - Attempt work is request-scoped and exclusive: `<work>/<requestId[0:16]>/attempt-<i>-run-<n>`; an existing dir throws `WorkCollision` (state stays `running`, next invocation reruns as run n+1; nothing in the old dir is read).
  - `--generate` takes the quality gate, source contract and attempt limit only from the request-bound `--policies` (`product-policy`, `world-quality`, `source-compatibility` all required); `maxAttempts` above `maxQualityAttempts` → `policy_violation`.
  - New `--seed-width worldgen/contracts/producer-seed-width.json`, written by the evidence script after probing the pinned binary; refused unless it names the locked producer sha256 (`seed_width_unverified`).
  - Frozen accepted state re-verifies its world's WorldContentId (`frozen_world_mismatch`). Supervisor events go to `supervisor-events.log` per attempt; exit code must agree with `outcome.json`, whose `job` must be this attempt's.
  - Schedule: exclusive `<state>.lock` (delete-on-close), durable write (flush + write-through rename), per-attempt run `history`, `canonicalJson` marker, strict `load_state` (seeds must re-derive, only the last attempt non-rejected, status/attempt consistency), request consistency on resume, runner exceptions → mechanical `bridge.exception`.
  - IDs domain-separated: RequestId = sha256("mnbl.request-id/1 mnbl.canonical-json/1\n" + canonical); ConfigurationId prefix gains " mnbl.canonical-json/1". Override integers beyond ±2^53 refused (decimal compare; 2^53+1 rounds to 2^53 as a double). WorldContentId refuses reparse points and hard links. All paths cross as UTF-8 (`json::utf8` / `json::from_utf8`).
  - Tests 67/67. Gotcha: the supervisor refuses request/job paths outside the ANSI code page (producer argv), so a CJK work dir fails as a recorded mechanical attempt (`producer.invalid_request`).

## Update (01:30Z, 2026-10-14)

- **MMR-24 claimed (`at_01m4vyqd83t3qfmnfg0j5vq1wg`) and implemented; evidence regenerating, then independent review.**
  - `cjson` (`src/WorldBridge/src/canonical_json.cpp`): RFC 8785 canonical JSON plus SHA-256.
  - `identity`: request validation and bindings, recipe bytes, and the Request/Configuration/SourceInput/WorldContent IDs.
  - `schedule`: the `mnbl.retry-seed/1` derivation, persisted resumable attempts, failure classes, ceiling and exhaustion, frozen final states.
  - `worldbridge --new-request` and `--generate`, the latter driving the supervised producer, `read_source` and the quality gate in `worldgen/policies/world-quality.json`.
  - Contracts: `worldgen/contracts/generation-request.schema.json` and `generation-attempts.schema.json`.
  - Tests: `identity_retry_tests` (52 checks; CTest `identity_retry`). Evidence: `tools/Test-GenerationIdentity.ps1` writes `evidence/fwg/generation-identity.json`.
  - Supervisor change (MMR-22 code, additive): `--produce --threads N` sets `FWG_THREADS` in the minimal environment and records it.
  - PowerShell 5.1 gotchas hit again:
    - `Set-Content -Encoding utf8` writes a BOM, which the strict readers refuse;
    - `ConvertTo-Json` on `Get-Content` strings blows up memory (15 GB), so cast them to `[string]`;
    - native stderr under `ErrorActionPreference=Stop` throws.

## Update (01:00Z, 2026-10-14)

- **MMR-23 accepted** after two independent reviews; the first found 14 defects, all fixed. `read_source` now enforces:
  - 64-bit version range checks;
  - required record arrays;
  - bidirectional joins (cores ↔ hamlets/forts; one plan per record);
  - the fort no-garrison sentinel `-1`/`""`;
  - path alias, hard-link and junction refusal;
  - memory budgets: 16 MiB of open keys and 16 MiB retained, with the root member index charged too;
  - a strict identity contract (`worldgen/contracts/source-identity.schema.json`).
- Tests: 120 checks. Evidence: `evidence/fwg/source-reader.json`.
- Open follow-up: the legacy `worldbridge --source` heightfield path (from MMR-12/16) still uses the old validator. Move it to `read_source` when a consumer ticket touches it.
- Ready now: MMR-24, MMR-59, MMR-60 and MMR-63. MMR-25 waits on MMR-21, which needs MMR-19/20, which need MMR-59/60/63.

## Update (00:20Z, 2026-10-14)

- **MMR-17 accepted** after an independent review and a re-review (no blocking defects). Evidence: `evidence/runtime/navigation-probe.json`.
- **MMR-23 (bounded split reader) claimed and implemented; independent review running.**
  - New: `worldbridge::json::Stream`, a pull tokenizer with exact file/byte/line/column/JSONPath errors (`src/WorldBridge/src/json_stream.cpp`).
  - New: `worldbridge::source::read_source` (`src/source_reader.cpp`), against the contract `worldgen/contracts/source-compatibility.json`.
  - New CLI: `worldbridge --read-source DIR --contract FILE [--layers a,b] [--report F]`.
  - Real exports from the pinned producer, seed 42, sizes 129/257/513/1025, are in `build/research-source23/split-*`. The 1025 export is 2.1 GB, and every one reads at about 10.9 MB peak working set.
  - Tests: `source_reader_tests` (53 checks) runs as the `source_contract` CTest. Evidence: `tools/Test-SourceReader.ps1` writes `evidence/fwg/source-reader.json`.
  - FWGC++ format facts worth knowing:
    - `split.scene` may be null.
    - NaN is written as `null` and infinity as `1e999`.
    - Harbour hamlets have kind "hamlet". Their roles include "landing", so dispatch on the fields, not the role.
    - Frontier forts have null `core_id`/`core_uid`.
    - `key_location_plans` can be `{status: "failed"}`.
    - Plan filenames are sanitized, digest-shortened above 48 characters, and `-2`-disambiguated, so always read them from the index.
    - `world.json` is written last, without a temp file.
- **MMR-17 update (23:55Z):** after review, the accepted run is now `generated_campaign-2248` (both worlds pass). Fixtures are final42e/43e; only placement differs from 42d/43d.
  - **Important engine finding:** `Scene.SetAbilityOfFacesWithId` renumbers navmesh islands. `CampaignVec2` caches its face (`_faceCache`), and `MobileParty` only moves when the current and next faces share an island. So **every party that cached a face before a toggle stops moving.** The "stuck at spawn" lords were caused by the probe's own toggles, not by spawning.
  - The probe's workaround is `RefreshFaceCaches`. A follow-up chip was raised for a supported version.
  - The AI now goes from its natural spawn to the other landmass and back. The player walks out and back, then sets sail from the gate.
- **MMR-17 update (23:10Z):** the runtime work is done and passes in both worlds (run `generated_campaign-28652`). An independent review is running, then submission. Evidence: `evidence/runtime/navigation-probe.json`, generated by `tools/summarize_navigation_probe.py --notes build/research-navigation17/notes.json`.
  - Fixtures are now **final42d/final43d**. The builder's wall is a ≥2-cell-thick band of group 5 across a seed-chosen land route from town_1. A site is accepted only if every shortest 4-connected land route crosses the wall and a detour at least 4 cells longer exists. The old 1-cell strip had a diagonal corner gap. Only `navmesh.bin` differs from 42b/43b, and both are installed.
  - The AI trip starts from town_1's gate (Enter/LeaveSettlement). At stock spawn points, generated rulers held valid paths but never moved (runs 22588 and 21284). The cause is not isolated, and a follow-up chip was raised. This may also explain ruler_1 barely moving in MMR-18 gameplay.
  - The player now sails to whichever coast is nearer by engine naval distance (the other landmass in both worlds). That cuts the voyage from 389 s to 68 s and 204 s.
- **MMR-17 claimed** (`at_01m4vn9hc29jrjxw0pv4vb7mf2`).
  - The new probe is `Diagnostics/GeneratedNavigationProbe.cs`. It runs when `generated_campaign` gets `navigationProbe=1` and covers:
    - the native route matrix
    - obstruction toggling with `SetAbilityOfFacesWithId(5)`
    - player land, sea and disembark movement
    - an AI ruler crossing between landmasses
  - Its fixtures are `build/research-bootstrap18/final42c` and `final43c`:
    - The builder stamps a steppe strip (group 5) between town_1 and village_1, plus a land-ringed 3×3 lake (group 8).
    - It records the points in `placement.json` under `navigationTests`.
    - Only `navmesh.bin` differs from final42b/43b. Both are now installed in `SceneObj`.
- **Careful:** I accidentally overwrote the pre-existing `Diagnostics/RuntimeNavigationProbe.cs` (the isolated prefab-import probe behind the AutorunProbes `nav` case). I rebuilt it from the deployed DLL's IL, and the rebuilt method's IL matches the original instruction for instruction. Backup of the pre-change DLL: Claude's scratchpad, `deployed-pre-mmr17.dll` (sha 3ab256ef…).

## Update (21:45Z)

- **MMR-18 accepted** after an independent read-only review: c1 and c2 PASS-WITH-CAVEATS, c3 PASS. The summary was corrected for six review findings; see the `independentReview` section of `evidence/runtime/bootstrap-probe.json`.
- **MMR-22 submitted** (`at_01m4vk95gf9y20r32t0rbgdmz8`), with an independent review running. Changes:
  - `worldbridge --produce/--preflight` implements the host/bridge producer protocol v1 (`src/WorldBridge/include/worldbridge/protocol.hpp`).
  - New lock files: `worldgen/producer.lock.json` and its schema.
  - Real-producer evidence: `evidence/fwg/producer-process-contract.json`, reproducible with `tools/Test-ProducerProcess.ps1`.
  - Through the bridge, seed 42 republishes the accepted root hash byte for byte.
  - The C# host still launches fwg.exe directly.
- **MMR-17 (routes) is next and ready.** Facts so far:
  - In both worlds the two towns share one land component.
  - Seed 43 has 6 separate water components (natural lakes).
  - The builder's navigation face labels are 18 = sea, 7 = slope > 1, 1 = plain.
  - A research agent is extracting TerrainType values, navigation-type terrain sets, path APIs and embark/disembark rules from IL.

## Earlier status

MMR-18 attempt 2 (`at_01m4vd7qn9690kfjs10n8mk8rt`) was submitted, then accepted (see update above). Independent review is still needed. Astra's attempt 1 lease expired and was reconciled as `abandon`; its work products were kept and carried forward. Nothing is committed: the branch still has only the initial commit, with everything else untracked on disk.

Both generated worlds (seed 42 and 43) boot in one process with a normal `EndGame` between them. Each runs 2 campaign days at fast-forward with zero first-chance exceptions, AI movement and settlement entries, volunteer regeneration, and village and treasury ticks. One recruitment and one market purchase are applied per world. Evidence summary: `evidence/runtime/bootstrap-probe.json`. Best run: `evidence/runtime/in-game/generated_campaign-23192.json`.

## Use these inputs now

- Placements and scenes: **`build/research-bootstrap18/final42b` and `final43b`** supersede final42/final43. Only `scene.xscene` changed; terrain, navmesh, flora, atmosphere and every coordinate are byte-identical. Both scenes are installed in the module's `SceneObj`.
- Catalog receipt: `evidence/runtime/in-game/campaign_catalog-10212.json`. Its document paths point at `%LOCALAPPDATA%\MNBL.ProceduralWorld\autorun\campaign-catalog-10212`, which existed only in the Codex MSIX cache (`AppData\Local\Packages\OpenAI.Codex_2p2nqsd0c76g0\LocalCache\Local\...`). Game runs you launch from Codex write AppData there. Claude copied it to the real path; 24/24 hashes match.

## Fixes made (all diagnosed from runtime evidence)

1. `GeneratedCampaignRegistration`: gathers all ten live default kinds; each kind must have at least one ready object. IL shows SiegeEngines load in `Game.InitializeDefaultGameObjects` and SkeletonScales in `LoadBasicFiles`, both before submodule registration.
2. `CampaignBootstrapProbe`: `CampaignObjectManager.Settlements` is null until `InitializeOnNewGame`. `Rows(null)` threw inside `OnCampaignStart` and crashed PID8152. Lists are now captured per list.
3. `DiagnosticSandboxBootstrap`: generated engine callbacks trace exceptions before rethrowing. `HoldPaused` switch added. `RetirementCampaignBehavior` is removed in generated mode (it caches `Settlement.Find("retirement_retreat")` and its hourly tick NREs).
4. `GeneratedCampaignDataset`: `Options.EngineCharacterIds` roots 33 reusable NPC characters that installed IL looks up by literal ID. `BodyGeneratorView..ctor` dereferences `facgen_template_test_char_0..9` without a null check. Scan: `build/research-bootstrap18/engine-character-literals.json`. `fighter_sturgia` is excluded because its stock roster is missing; revisit before generating a Sturgia world.
5. `tools/scenebin/build_bootstrap_world.py`: each banner placeholder gets the installed `volume_box` child, and towns get four placeholders. `SettlementVisual.GetBannerPositionForParty` calls `GetChild(0)`; a childless placeholder faulted at `TaleWorlds.Native.dll+0x4c4897` once a lord was inside a town with time running.
6. `Automation/GeneratedCampaignGameplay.cs`: an opt-in phase driven by probe args `gameplayDays`, `gameplaySpeed=fast|play` and `gameplayEntryProbe=1`. It records breadcrumbs flushed per line and throw-site stacks via `AppDomain.FirstChanceException`. Breadcrumb listeners must never throw inside engine event dispatch; one did (PID27472) and is now guarded.
7. `tools/Watch-ProbeDialogs.ps1`, auto-started by `Start-OwnedProbe.ps1`. It clicks No on Safe Mode and Don't send/No/Close/OK on crash dialogs, never Yes or Send, and only for #32770 windows of the probe's Bannerlord, its Watchdog, or matching WerFault processes. `Collect-OwnedProbe.ps1` copies its log as `*.dialogs.log`. The Win32 path is verified on synthetic dialogs; the first live run used an older UIA-only version that failed.

## Gotchas

- `C:\ProgramData\Mount and Blade II Bannerlord\crashes` is a symlink to a missing Steam Workshop cache folder. Watchdog cannot write dumps, crashes itself, and kills the game. Faulting modules are still in the Windows Application event log (ID 1000). This is the user's system configuration; leave it alone.
- `Build.ps1` rebuilds `worldbridge.exe` non-reproducibly (hash changes), but terrain output was identical.
- Autorun requests expire 10 minutes after launch; a blocked Safe Mode dialog can outlast that.

## Not done / next

- Save/load: `RegisterSubModuleObjects(isSavedGame: true)` throws `NotSupportedException` (MMR-19). The ticket body also asks for the loaded-save path.
- Economy: town food falls ~70/day (300 → ~160 in 2 days). Expect starvation within days; no run longer than 2 days yet.
- Town visuals are a single keep icon (official towns use level-tagged wall/keep hierarchies). Recruitment and trade went through domain actions, not UI screens. Bandit clans are absent and their behaviors are unexercised.
- MMR-17 (routes) is now unblocked by evidence but still waits on MMR-18 acceptance.
