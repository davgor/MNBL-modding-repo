# Combat animation implementation findings

2026-10-25. Scope: action/animation authoring, registration, playback, mounted variants and hit synchronization for the combat overhaul epic.

**Basic original custom-animation playback passed in game in run 30840.** The authored two-second Blender sweep was packaged by the pinned editor-free writer, registered as a module-owned action/child set, and visibly played on the human fixture. This proves the custom asset-to-runtime route without operating the editor. Mounted playback, interruption/channel arbitration, animation-driven damage and production motion quality remain unproven. Receipts and reproducible scripts are in `evidence/combat-overhaul/runtime/animation/`; editable .blend and FBX remain under `build/combat-feasibility/animation/`.

The compiled AnimationProbe checks unique action/set/clip availability, full known fixture animation configuration, playback progress and hand/spine pose samples, then restores the original configuration. Boolean SetActionChannel success alone cannot pass it. There is no public getter for arbitrary existing AnimationSystemData; the default helper is deliberately restricted to the known isolated fixture, or requires a caller-supplied full original configuration.

## Evidence and reuse

- Prior local research: `docs/research/character-overhaul-dd2-redteam.md`, especially lines 271–275, 716, 923 and 1237. It established human skeleton reuse and flagged reach/IK at character-scale extremes. It did not demonstrate a combat animation production pipeline.
- Current REA parent evidence: `ev_ea6023156b03a1c3ca432c6926b08a84f60673e6f59da46d8c51ba41ea62afd5` (`inspect_managed_members`, selected Agent, MBActionSet and ActionIndexCache). Current MountAndBlade DLL SHA-256 `19387f31557ff840d14f378f6bbdf1d58fcff406ab9ff2294dfbb3e49a50b87e`, MVID `4267abec-e4ba-4746-94b7-47cbe3ba79f8`. This matches the prior local REA identity; tokens below are build-local.
- Retained, focused REA method/IL/call-edge subset: `evidence/combat-overhaul/animations/managed-action-contract.json`. This is a selected projection with source identity, not a new claim of complete assembly coverage.
- Installed file manifest: `evidence/combat-overhaul/animations/installed-inputs.json`. Includes shipped skeleton FBXs, Native XML, animation TPACs, current managed DLL and editor executable.
- Parsed XML counts/examples: `evidence/combat-overhaul/animations/resource-summary.json`.
- Supplemental ICSharpCode.Decompiler 9.1.0.7988 extraction was execution-free. Private local output lives in ignored `evidence/combat-overhaul/animations/decompiled/`; Agent extraction is in `build/combat-research/managed/TaleWorlds.MountAndBlade.Agent.cs`. These support readable signatures; native bodies were not decompiled in this lane.

## Registration and authoring route

Official [TaleWorlds animation documentation](https://moddocs.bannerlord.com/asset-management/asset-types/animations/) describes skeletal FBX import, resource-browser animation clips, and action-to-clip mapping. New action names enter `action_types.xml`; mappings enter `action_sets.xml`. It documents 30/60 FPS authoring and a 64-bone limit; its exporter examples are Maya, so they do not certify Blender 5.2 settings.

The local installation contains:

- `modding_resources/skeletons/human_skeleton.fbx` and `horse_skeleton.fbx`.
- `Modules/Native/AssetPackages/animations.tpac` and `animation_clips.tpac`: separate shipped animation/clip packages.
- `Modules/Native/ModuleData/action_types.xml`: 5,167 action entries. Observed attributes are name, type, action_stage and usage_direction.
- `Modules/Native/ModuleData/action_sets.xml`: 102 sets, 88 with as_human prefix. The explicit human skeleton name is human_skeleton, and many sets inherit through base_set. as_human_warrior contains 4,699 action mappings. as_human_female_warrior inherits as_human_warrior; hideout/villager sets form additional inheritance chains.
- `Modules/Native/ModuleData/Layouts/animations_layout.xml`: editor fields identify animation data name, start/end frames, duration, blend-in period and flags.
- `prebaked_animations.xml`: shipped comment describes .rdc animation caches of bone transforms. Presence of this file does not prove every custom clip needs a hand-authored entry.
- `bin/Win64_Shipping_wEditor/` and installed Blender 5.2 exist. This was a file-presence check; editor launch/import/export operability was not tested.

**Implementation choice:** retain the supplied human skeleton contract initially. Author new clips against it; add namespaced actions and a module-owned action set inheriting an appropriate original set. Do not replace Native's broad warrior mappings globally: that would change opted-out agents too. Resolve exactly how module engine XML merges and inherited mappings reach each used action set in a startup probe before adopting this arrangement.

Mounted fighter animation is predominantly a rider/human-skeleton problem, not a requirement to animate the horse skeleton for every attack. Native warrior mappings already include rider pace actions such as act_rider_stand_bow_pace_0 through _5. The proposed alternating left/right Whirlwind needs distinct rider motion and hit windows while preserving the mount's gait. Whether upper-body layering suffices must be measured; do not mark mounted support done because a foot clip can be forced onto a rider.

## Exact managed control seams

REA confirms these methods and their managed-to-native calls:

- `ActionIndexCache.Create(string)`, token `0x06001639`, constructs the cached action identity. Cache only after the module action registry is available; resolve IDs again at load rather than persisting runtime indices.
- `MBActionSet.GetActionSet(string)`, `0x060015e0`, selects a registered set.
- `Agent.SetActionSet(ref AnimationSystemData)`, `0x06000a7b`, calls `IMBAgent.SetActionSet`; it is not a string-only setter. AnimationSystemData includes action set, monster usage set, bone mappings and biped/quadruped data. Preserve/reconstruct the full correct actor data and prove restoration; replacing only a guessed subset is unsafe.
- `Agent.SetActionChannel(int, in ActionIndexCache, bool ignorePriority, AnimFlags, float blendWithNextActionFactor, float actionSpeed, float blendInPeriod, float blendOutPeriodToNoAnim, float startProgress, bool useLinearSmoothing, float blendOutPeriod, int actionShift, bool forceFaceMorphRestart)`, `0x06000a5d`, returns the native bool result. It forwards the cached index plus actionShift to `IMBAgent.SetActionChannel`. Defaults in the inspected wrapper include ignorePriority=false, actionSpeed=1, blendInPeriod=-0.2, blendOutPeriodToNoAnim=0.4, startProgress=0, blendOutPeriod=-0.2. Native meanings/priority behavior are not established by those defaults.
- `Agent.GetCurrentActionProgress(int)`, `0x06000a97`, and current-action/type/stage accessors query native state. Use observed action identity and progress to synchronize windows, not a fixed wall-clock delay started at button press.
- `Agent.GetBoneEntitialFrameAtAnimationProgress(sbyte,int,float)`, `0x06000ac2`, and `GetBoneEntitialFrame(sbyte,bool)`, `0x06000ac3`, provide candidate bone transforms for motion sampling. Coordinate space, blend fidelity and update timing must be validated before treating them as collision truth.
- `MBActionSet.CheckActionAnimationClipExists`, `0x060015e6`, calls `IMBAnimation.CheckAnimationClipExists`; `GetAnimationIndexOfAction`, `0x060015e7`, and duration/name queries can build a registration preflight.
- `GetActionAnimationContinueToAction`, `0x060015ea`, `GetTotalAnimationDurationWithContinueToAction`, `0x060015eb`, `GetActionBlendOutStartProgress`, `0x060015ec`, and displacement/flag queries expose continuation and blend information.
- Supplemental decompilation of MBAnimation shows `PrefetchAnimationClip` and `IsAnyAnimationLoadingFromDisk`. Asset-load latency belongs in first-cast acceptance, not only warm-cache tests.

**Critical distinction:** playing an action is not proof that it produces an attack, a swept weapon collision, a damage event, or multi-target continuation. Action stages/directions are engine-recognized metadata, and playback/collision authority may be separated. The combat hit-owner probe must decide whether to use native attack collision with narrowly altered continuation, or mod-owned timed sweeps with native damage/reporting integration. Never enable both damage paths for the same action.

## Required probes and falsification checks

A1 — **DCC roundtrip.** Author a short unmistakably new human clip in Blender 5.2 using the shipped reference skeleton. Retain .blend, exported FBX, exporter options, unit/axis/bone-name checks, FPS and key range. Import into a module-owned package with the editor, create a clip, register a namespaced action, package it, then cold-start the game without the editor. Pass only if the installed clip is found and visibly plays with correct duration. This resolves tool availability, scale/orientation, skeleton compatibility and package dependencies together.

A2 — **Scoped action registration.** Add a child set/action mapping without changing Native files. Log action index, set name, clip name, existence and duration; spawn one opted-in and one vanilla human, repeat female/inherited set and mounted rider. Disable and restore. Pass only if custom clips are isolated and ordinary locomotion/equipment actions remain correct. Record XML merge/loading phase and collision behavior for duplicate IDs.

A3 — **Channel and interruption ownership.** Exercise playback on candidate channels with walking/running, guarding, hit reaction, knockdown/death, mount/dismount, weapon change and mission exit. Log native bool result, current action/type/stage/progress and channel weights each tick. Demonstrate which movement/AI channels can overwrite the action, and which events must cancel it. An ignorePriority=true test is diagnostic, not a blanket production policy.

A4 — **Animation-clock hit windows.** Use a clip with clearly separated windup, contact and recovery. Compare observed action progress/bone trajectory against visible weapon contact at different frame rates and playback speeds. Repeat at a blend boundary and after interruption. Assert no early/late/duplicate damage, no catch-up hit after cancellation, and a documented multi-hit target ledger. Bone sampling and swept collision tests must include horse movement.

A5 — **Mounted Whirlwind.** At stand/walk/trot/gallop, alternate the rider's left/right sweeps without snapping the pelvis, breaking reins/weapon grips or freezing the horse. Include steering and mid-action dismount. Decide whether one sequence or linked clips is needed; the ability definition must support that sequence, not merely a single animation string.

A6 — **Skeleton-scale and gear integration.** Test agreed current character scales, weapon lengths/classes and representative armor/shields. Character-overhaul research already warns that visual limb scaling does not establish gameplay reach. Use visual and collision overlays together; do not infer weapon reach from mesh alone.

A7 — **Cold asset loading/performance.** Record first cast, repeated casts and many AI actors using the same clips. Verify module-only packages load on a clean installation; report missing clip/dependency failures and compare memory/frame-time costs. Prebaking needs are an experiment, not an assumed mandatory manual pipeline.

## Ticket decomposition for sprint 1

Do not leave custom art inside two generic six-ability code tickets. The minimum separate work packages are:

1. **Animation toolchain and roundtrip (A1)** — source rig, Blender export preset, first original clip, import/clip creation, TPAC packaging, cold launch proof. This precedes production art.
2. **Action registry and opt-in action-set integration (A2)** — namespaced XML, inherited set strategy, startup preflight, exact restore data; verify opted-out control.
3. **Channel/movement/mounted feasibility (A3/A5)** — executable harness and result-driven adapter decision. This gates the runtime controller and mounted production.
4. **Animation event clock and contact contract (A4)** — windup/active/recovery/interrupt markers, sequence/continue handling, speed scaling, bone sampling, damage ownership and diagnostics.
5. **Basic/heavy/guard/jump transition animation pack** — authored source clips, charge/hold/release behavior, guard entry/loop/exit and readable parry feedback. Reuse native locomotion/jump only where the approved design explicitly accepts it; custom attack motion must be real, not a rename.
6. **Foot ability animation production**, split into catalog-aligned batches of 3–4 abilities — each has source assets, export settings, registered clips, timing manifest, contact pass and readable silhouette.
7. **Mounted ability animation production**, independently split into the same batches — rider/gait/IK acceptance for every ability; allowed reuse must be explicitly identified. At least 24 ability-context deliverables must be accounted for (12 foot + 12 mounted), not necessarily 24 unique clips: multi-phase abilities may need more and justified reuse may need fewer.
8. **Animation/combat integration per batch** — bind the actual authored sequence, tune hit windows and interruption/cooldown behavior, verify against shield/block and multiple targets.
9. **Asset build validation and cold package acceptance (A6/A7)** — detect missing actions/clips, wrong skeleton, duplicate IDs and stale resources; retain authoring sources and reproduce the package from documented steps.

All production acceptance needs source assets plus installed-game captures and frame-level timing evidence. An FBX export alone, an editor preview alone, or playback of an existing vanilla animation does not satisfy custom-animation delivery. Build-file presence is not evidence that the editor was operated successfully.


## Runtime result: original animation package

Run 30840, 2026-10-25 07:55:02–07:55:05 UTC, supplies runtime evidence beyond the shared writer checks. [Raw log](../../evidence/combat-overhaul/runtime/animation/run-30840.jsonl), [three-pose filmstrip](../../evidence/combat-overhaul/runtime/animation/run-30840-filmstrip.png), and [independent log/hash review](../../evidence/combat-overhaul/runtime/animation/run-30840-review.json) are retained.

- Action `act_mnbl_probe_sweep` resolved to index 5167; child set `as_mnbl_probe` resolved its `mnbl_probe_sweep` clip. Channel 0 accepted the action after swapping the complete known fixture animation configuration.
- Twenty samples matched the custom action (one start plus nineteen tick samples). Progress advanced monotonically from 0 to 0.974396348 at elapsed 1.95719421; by 2.06134582 the action was -1 and progress 1. Progress/elapsed slope implies 1.99999992 seconds. Screenshot capture stalls stretched wall time, so wall-clock duration alone would be misleading.
- Three in-game captures show distinct extended-arm, sweep and returning-arm poses. Hand-bone samples change and include the deliberately authored held phases; evidence exceeds merely resolving an ID or returning true. The diagnostic motion is visibly rough and is not an approved production attack animation.
- At elapsed 2.20717287, the helper reapplied full known original configuration; the observed set returned to `as_human_warrior` with `restored=true`. This checks set restoration, not every opaque native field or resumption of the prior transient action.
- Read-only post-run hashes of the installed animation TPAC, clip TPAC and RDC exactly match the retained writer manifest. The GUID chain points to the original FBX-derived motion and installed 28-bone human skeleton. The plain Native clip metadata template contributes no native motion.

The basic A1 outcome is satisfied through the alternative writer route: original authoring, export, packaging, module registration and visible client playback. The proposed **editor import procedure itself remains untested**. A2 is only partially demonstrated: one on-foot human using an inherited child set, followed by set restoration. Female variants, opted-out control comparisons, locomotion/equipment regression, duplicate IDs and mounted behavior remain open. A3–A7 are not passed by this run: no custom-clip damage/contact proof, cancellation/hit reactions, multi-actor scale, clean-install asset test or systematic cold-load performance measurement was performed.

The writer's default range would truncate the authored 61 samples; explicit range 0–60 remains required. The proven package uses empty clip flags and 0.3-second blend, not the enforce_all/IK flags proposed for the editor route. See [writer audit and reproduction](../../evidence/combat-overhaul/runtime/animation/editor-free-writer-findings.md) for the pinned source, metadata limitations and `project.mbproj` registration.
