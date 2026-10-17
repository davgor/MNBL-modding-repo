# Bannerlord growable agent storage — paused handoff

Paused at the user's request on 2026-10-17. **Live battle growth and removal of the fixed soldier ceiling are incomplete.** No production patch is ready. The existing finite 4,080-entity experiment must not be described as unlimited.

## Resume here

1. Read this handoff and `task-notes/battle-scale.md`. Preserve the frozen evidence and packages listed below. The workspace also contains extensive unrelated Claude changes.
2. Finish the private native probe's **new v2 host/runner**, including the stock-build watchdog shutdown contract. The corrected native binding is built and independently reviewed, but has **not** passed a positive in-game test. Do not rerun the old package expecting it to work.
3. Independently review the completed v2 package and its inert runner tests. Then check for **all** Bannerlord sessions immediately before deployment and launch; wait for Claude/foreign sessions to exit. Run the private constructor/growth/cleanup probe. This is a prerequisite, not visible soldier growth.
4. Finish initial Mission adoption/publication proof and the remaining 12 bounded owner contributions. Implement actual native allocation, activation, access, metadata, iteration and removal routing into the stable storage. Offline storage alone does not redirect engine accesses.
5. Test capacity growth within one live battle, preserving existing Agent addresses while creating visible, fighting soldiers in added storage. Cover exhaustion, reuse, cavalry, ranged combat, sustained performance and teardown.

## Requested outcome and acceptance

- A1: Storage grows during a battle without moving any existing Agent or companion record. Remove the fixed startup capacity as the limiting mechanism; finite RAM/CPU/GPU limits still exist.
- A2: Added capacity produces real visible soldiers that participate in combat, not merely background bookkeeping. Layered simulation is allowed.
- A3: All five pool domains, native consumers and dependent renderer/animation buffers remain coherent. The secondary pool's purpose must not be guessed.
- A4: Exhaustion or allocation failure preserves valid state; holes can be reused; cleanup handles every segment and outstanding lease/reference.
- A5: Cavalry, ranged units and sustained combat pass after live growth, with measured performance and verified teardown.
- A6: Every launch respects Claude's game window. Never stop, attach to or control a foreign session.

None of A1–A5 has complete live-engine acceptance. The earlier r13/r14 trials validate only the finite experimental profile.

## Safe state at pause

- Both owned runtime processes ended: PID556 and PID21268. No Bannerlord or Watchdog process was present at the final checkpoint check.
- All three deployed private module files were removed/restored after each run. Root independently checked that those files are absent.
- Installed `TaleWorlds.Native.dll` still hashes to `2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a`.
- No native engine patch was installed in these two runs. No owned game remains running.
- REA helper session88900 closed normally, exit0. Queue request049 was `close_binary`; its response is retained. Requests001–048 are complete.
- No commit, deployment or automation was created as part of this pause.

## Latest runtime findings

### Original private probe: failed safely before allocation

Run `build/element-probe-host/runs/32541ff2-de18-4f2c-9c47-55e41540ba37`.

The owned game initially stopped at its Safe Mode startup dialog. The existing-settings “No” choice was used. It reached the main menu and ran the armed probe with no Mission. Binding rejected the loaded image with `Loaded native code/header/unwind identity differs`. All allocation/construction counters were zero. The process exited-1 without timeout or forced cleanup; restoration passed. Preserve this run as failed.

### Read-only identity diagnostic: mismatch explained

Run `build/element-probe-host/runs/935d4269-1bbd-46f9-90b8-fddce29cabfe`.

The diagnostic compared the pinned disk image with the loaded image. Only four bytes of `OptionalHeader.ImageBase` differed: RVAs1ca–1cd. Preferred base was180000000; actual loaded base was7ff97ec40000. All executable bytes and the entire `.pdata` region matched. The diagnostic intentionally reports failure to the allocation-probe runner and performs no construction or patching. It also exited-1 and restored all files.

Diagnostic source: `src/MNBL.AgentGrowthProbe/NativeIdentityDiagnostic.cpp`; package: `build/element-identity-diagnostic/package`. Do not confuse this package with a successful allocation probe.

### Corrected binding: accepted offline, not live

New files preserve the frozen original binder:

- `src/MNBL.AgentStorage/RelocatedNativeElementBinding.hpp`
- `src/MNBL.AgentGrowthProbe/RelocatedNativeElementProbe.cpp`
- `tests/AgentStorage/relocated_native_binding_tests.cpp`
- `tools/battle-scale/Test-RelocatedNativeBinding.cmd`

The binder requires the recorded ImageBase to equal the actual retained HMODULE, then normalizes exactly those eight bytes for the header hash. Full disk identity, every other header byte, all executable bytes and `.pdata` remain pinned. MEM_IMAGE/read/execute checks and module lifetime retention remain intact.

Independent receipt: `evidence/engine-extension/native-relocated-binding-independent-review.json`, SHA `b027c74bcf573639f833917e23570c81dfbc819c83f0b215fd9e85792bca7962`.

Frozen DLL: `build/agent-storage/MNBL.RelocatedNativeElementProbe.dll`, SHA `9d1f6a512dd63c833d44a4fd0bf0e7f5ede9750dcb022ef50608144306347d05`.

Tests include optimized/ASAN builds, four relocated bases,256 forged-base bit checks,55 region mutations,1,016 additional non-ImageBase header mutations, wrong-module/memory checks and no-engine ABI/canary tests. Review found and fixed a missing ASAN exit-code gate in the new build script before acceptance.

## Immediate unfinished work: v2 probe host and shutdown

The original host/package and runner are frozen. Reuse their validated request/interop logic in new files; do not overwrite old receipts to make them appear successful.

Original files/package:

- `src/MNBL.AgentGrowthProbe/managed/{ProbeRequest,ProbeInterop,SubModule}.cs`
- `tools/battle-scale/Build-ElementProbeHost.ps1`
- `tools/battle-scale/Test-ElementProbeInGame.ps1`
- `build/element-probe-host/package`, profile SHA `16a6ab94d91af853c892fa9b6443569bc76def76b84bc81dff4f2a99f93ddf14`
- Independent host/runner review SHA `4cf9b331d15cdb266ad5d4dbc7ec96d27a4c18f01305b35a3dceea3bdfb61efb`:44 managed checks and13 inert runner scenarios.

The old runner requires exit0. This build's ordinary shutdown can issue `#TW#-kp` to Watchdog, whose bundled `Process.Kill` calls `TerminateProcess(-1)`. **A bare exit-1 is not acceptance.** The existing `native-watchdog-shutdown-contract.json` is bound to the historical patched candidate and cannot simply be applied to this stock probe.

Next implementation:

1. Create a new stock-build shutdown receipt. Reuse and revalidate exact source-bound native/managed proofs from `tools/battle-scale/audit_watchdog_exit_contract.py`; bind the unchanged original native SHA and pinned Watchdog SHA. No such new stock contract was created before pause.
2. In a new managed-v2 host, record `Utilities.GetReturnCode()` before quitting; require0. The probe's `nativeCode` is a different value and cannot substitute for this.
3. New runner observes the exact owned Watchdog child PID/start/path/hash, retains ordered per-owned-PID shutdown logs and calls the reviewed shutdown verifier. Existing helper: `tools/battle-scale/Test-NativeExitContract.ps1`.
4. Report `verifiedShutdownProtocol` separately from `normalExitVerified`; only exit0 qualifies for the latter. Watchdog shutdown does not certify every remaining process destructor. Private pool cleanup must already be established by the probe's counters.
5. Retain all existing foreign-session checks, identity checks, backup/restore behavior and package pins. Test wrong Watchdog identity, stale/foreign/out-of-order logs, crash markers and partial restoration with inert fixtures before launch.

Expected private probe success: ABI712/version1/status1/stage8/capacity90; pointer checks600, header checks450, allocation attempts21, allocations20, releases20, constructor attempts488; constructed/destroyed `[120,97,90,90,90]`; all four failure/exhaustion/reuse flags1. These are private inactive records, not spawned soldiers.

## Storage and native facts already established

- `AgentDomainStorage.hpp` supports stable five-pool segments, borrowed initial arrays, independent primary/secondary ledgers and wide generation/index bookkeeping. Primary owns pools0/2/3/4; secondary owns pool1.
- `NativeElementFactory.hpp` constructs each destination using original Agent ctor606a20/dtor6070c0,230ctor5e58e0 and2c8ctor5d8860. Never clone an Agent template or invoke whole-pool construction on an added suffix.
- Actual copied original constructor/destructor bodies passed2040→4080→6120 five-pool tests with30,600 construction/destruction callbacks, stable addresses and61,200 header checks. Independent receipt `native-default-element-independent-review.json`, SHA `dcae1bc7d9449f9f4dbcb31b54dc48257ac23f10111b11caadda98e80ae8efd3`. This is isolated original-code execution, not Mission activation.
- `NativeAgentTransactionStorage.hpp` preserves lowest-free allocation across growth/reuse and serializes reset/cleanup with read leases. Independent receipt `native-agent-transaction-independent-review.json`, SHA `61ea6497e63f0bbdcd179b9d30fb94de0b7ed638392361ebccb0f0f5d9e02292`. Transition callbacks are still synthetic; O(N) removal/acquisition paths need later performance work. Its lock does not stop engine workers.
- Native primary allocation commits header/count/table before virtual reset and updates highwater afterward. Reset607390 clears908/b64 and sets558/55c=-1. Independent replay accepted167 cases/149 resets in `native-agent-allocation-independent-review.json`.
- `EmptyNativePoolSnapshot.hpp` is a necessary empty-only gate: exact stock geometry, all count/highwater0 and all header/link values-1. Independent137 producer+20,452 extra checks per optimized/ASAN build. It does not prove construction completion, lifetime or absence of writers.

### Critical phase issue

Do not use the old occupied `NativePoolSnapshot.hpp` rule at arbitrary engine phases. CreateAgent writes the230 payload header at+1c and then its initializer clears it to-1; refresh6948b5 later copies Agent+10 back. Short-table membership persists while that payload header is reset. Removal5d08d0 nevertheless reads the payload header to derive highwater. A table-only replacement also needs removal routing.

Adopt an **empty** initial Mission before first spawn, then grow it during a battle. Candidate5e5b80 is after the native constructor and before this wrapper stores the Mission pointer. It is not yet an approved hook. Alternative5cd168 precedes both CreateAgent allocation branches but is already on a published Mission.

Do not invent a CreateAgent failure return of-1: its registered caller immediately multiplies the returned index and derives an Agent pointer without a failure guard.

## Remaining native investigation

### Ownership

419/431 primary multiplier sites currently have accepted bounded owner contributions;12 remain. This count is provenance evidence, **not** complete consumer routing or hook approval.

Latest exact v4/v5 review: `native-remaining-agent-owner-v4-v5-independent-review.json`, SHA `cf712c40904f1f13e0a9d11a76613d7ba2d3f1a4f7a2750bbcc2115852a6899c`. It accepts6aa081 and713316 after1,460 original instruction rows/full producer replays and independent helper/cookie checks.

The generic v5 alias checker has three reproduced false-negative classes: partial-register pointer survivors, PUSH/POP pointer propagation, and possibly preserved volatile aliases across a call. The exact pinned helpers were independently checked with stricter rules and remain accepted; the generic checker is not reusable without correction. Preserve v5.

V6 correction is now saved, with producer tests passing but **independent review pending**:

- `tools/battle-scale/audit_remaining_agent_owners_v6.py`, SHA `7d031e41a207b1599dd09419d7a65e2f868e93377164bbf8fe7a73946b6affc7`.
- `evidence/engine-extension/native-remaining-agent-owner-proofs-v6.json`, SHA `4234cdf417611934d723d95ebfd7d73e2d259acf8cb71c8b9fdce58ef7b0f453`.

It preserves aliases through partial writes, stack spills and ordinary calls; unsupported stack/SIMD cases reject. Fourteen negative checks, the626-instruction helper and234-instruction caller replay pass. It adds no new site beyond713316.

Remaining sites:65f7c8,686b26,686d9d,686f3d,6872d3,687b3c,68e3c2,68e525,6bfe25,6bfe82,6c5cc7,6d2a14. Strongest next lead is function686450's Mission home atRBP-68, adjacent to the audited home atRBP-60. Check its exact lifetime and overlapping writes before reusing callee-effect contracts.

### Initial Mission pointer publication

Frozen constructor-v2 receipt: `native-constructor-escape-v2.json`, SHA `a8df1ed00eec03b9034ed7557b1853faf05e2262c60b6d39bcc3bb3e9f9c8177`; producer `audit_native_constructor_escape_v2.py`.20 original-instruction cases pass; root integration review is pending. No v3 receipt exists.

Pinned CRT initialization leaves perform only local stores. Resource lookup discards the apparent renderer RCX; later renderer calls receive existing or cloned resource objects and local output/string arguments. Global reset cleanup similarly receives global-derived resources. **These are unresolved dependencies, not demonstrated escapes of the new Mission.** Do not require unrelated global vectors to be empty merely because they invoke callbacks. Continue actual new-Mission argument/global/TLS provenance on ordinary engine execution.

Next REA targets, after reopening the helper:050→5d7bf0,051→41eba0,052→5d9a20,053→0c4530,054→9dfd98. Verify numbering first. These are planned requests only, not executed evidence.

### Other open engineering gaps

- All inline pools end at their short index tables: appending there corrupts metadata. Stable segments require comprehensive address, iteration and metadata routing.
- Native short-index sentinel limits and Agent.Index int32 remain. Multiplayer/replay packed widths are unchanged; current work is single-player only.
- Seven isolated scalar adapters and classified companion/stride consumers are tested offline. No complete native entry replacement is installed.
- TickCompleted is not a full worker fence: the native task finishes after the managed callback tail. The task wait consumes a retained reference and must run outside storage locks, followed by lifecycle revalidation. Render/opaque readers and teardown remain open.
- The secondary pool's role remains unknown; prior cavalry/ranged samples kept its count/highwater0. Do not manufacture activation into it.
- The finite renderer face-morph repair remains finite and asset-dependent; growable agent storage does not make rendering buffers unlimited.

## REA and frozen historical baseline

REA uses Ghidra12.1.4 and JDK21 through `build/battle-scale/native-growth-session.cjs`. The original target is `build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0`. Retained responses are in `build/battle-scale/native-growth-queue`; bundles1–6 are under `evidence/engine-extension`. Bundle6 SHA `af039b6ce64f34fc4fc224f8f1f39523af1d6fb4c0698849e8f7005d70d97c07` retains045–048.049 closes the session. Reopening the helper should preserve existing request/response files; do not overwrite bundles.

Keep `src/MNBL.BattleScale`, accepted r13/r14 packages and their evidence immutable; **do not run `Build-BattleScale.ps1`** while resuming this work. Historical acceptance is `native-pool-runtime-acceptance-r13-r14.json`:4,080 archers and2,040 rider/horse pairs,300-second combat, repeated exhaustion/reuse, restoration. It validates a finite4,080-entity profile only.

Original native SHA: `2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a`.

Historical patched SHA: `392bc9db99bf595ace640436ed47bcf47e168668124cf5320aa8967ded46c350`.

## Worker checkpoint ownership

- Root: integration, runtime ownership, relocated binding and this handoff. No active game/REA session remains.
- `pool_contract_audit`: constructor-v2 frozen; ordinary pointer-provenance continuation pending. Worker stopped without new requests or v3 files.
- `workload_review`: relocated binding review complete; **no v2 host/runner files were created** before pause. No stock watchdog contract exists. Implementation remains the first runtime prerequisite. Saved worker checkpoint: `evidence/engine-extension/private-element-probe-v2-checkpoint.json`.
- `remaining_owner_closure`: stopped;12 remaining owner contributions. V6 producer checks pass; independent review pending, as recorded above.

Resume only when the user resumes the task. Do not create a scheduled continuation or spend the offered usage reset automatically.
