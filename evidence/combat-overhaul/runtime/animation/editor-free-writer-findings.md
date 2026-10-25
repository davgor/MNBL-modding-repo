# Background custom-animation packaging probe

**Runtime update: basic original custom-animation playback passed in run 30840.** Root deployed and ran the staged candidate; the animation worker subsequently reviewed the log, filmstrip and installed hashes read-only. The background work described below had not launched or installed anything; its static-only receipts retain their original historical status.

The custom action resolved to 5167 in `as_mnbl_probe`, with clip `mnbl_probe_sweep`; channel 0 accepted playback. Twenty matching samples comprise the start sample and nineteen ticks. Progress reached 0.974396348 at elapsed 1.95719421, then completed by 2.06134582. Progress slope gives 1.99999992 seconds, matching the authored two-second clip. Three screenshots show distinct arm/weapon poses; hand-bone trajectory changes and held intervals support actual motion playback. Combined with original-source provenance and matching package hashes, this passes the basic original-clip proof rather than just an action-registration check.

At elapsed 2.20717287 the helper restored `as_human_warrior` and logged `restored=true`. The full known fixture configuration was supplied to the setter; the observable verification is set identity, not every native field. The prior transient action was not resumed. Captures show diagnostic motion only; animation polish, mounted variants, channel interruptions/cancellation, custom-clip damage and hit synchronization remain unproven. The editor import route remains untested; this proves the pinned writer route on this installation.

Retained evidence: `run-30840.jsonl`, three `run-30840-custom-animation-*.png` captures, `run-30840-filmstrip.png`, and `run-30840-review.json`. The review manifest includes evidence hashes, post-run installed package comparisons and exact completion observations. All three installed TPAC/RDC hashes match `sdk-writer-result.json`. Post-run hash comparison is not an execution-time asset-read trace. `summarize_runtime.py` reproduces the numerical/hash review.

Installed managed Engine Resource/Utilities did not expose an FBX importer. Native editor strings locate `Fbx_import_settings_dialog`, `rglFBX_skeleton_importer.cpp` and `AnimationClipInspector` in Qt/native code. No supported headless import command was verified.

Alternative writer primary source: https://github.com/theartur2000/bannerlord-editor-free-sdk, commit `211907728117ec836998b72908f84238a2d179c2` (MIT). It targets v1.4.8.119303; installed Native identifies v1.4.8. Exact build compatibility is not established by that version string alone. Reviewed pure-Python parsing/writing paths ran through isolated Blender Python; no dependency installation or game/install harness execution.

`build/combat-feasibility/animation/build_packages.py` creates animation TPAC, clip TPAC and runtime cache under `sdk-scratch/`. Motion is exclusively the original authored FBX. The clip uses a plain local Native metadata template (`chicken_run_gait`), not its animation data. Audit verifies empty sound/voice/facial/follow-up/partner/combat strings, empty flags and parameter lists (no locomotion or IK blocks). Forty-one shipped clips share the remaining metadata, including weapon-only flail/sling clips as well as sheep/chicken. Some remaining scalar fields are opaque; shared bytes support absence of bird-specific metadata but do not fully establish their semantics. Exact record differences and GUID chain are recorded in `sdk-writer-audit.json`.

Important correction: the SDK default clip writer assumes editor resampling at 12.548 keys/second, but its FBX writer retained all 61 samples. Default clip creation would select only keys 0–25 and truncate the sweep. Local build explicitly supplies `src_range=(0,60)` at duration 2 seconds; cache contains all 61 frames.

Both TPACs parse/repack byte-identically. SDK validation checks runtime bones against installed human skeleton, finite normalized quaternion keys, monotonic times, owner GUID, clip references and cache: zero problems/advisories. These checks share SDK code and are not independent engine proof. The Blender rig's 31 bones comprise indices 0–27 plus three `_notused` helpers; audit enumerates every index and confirms the generated animation references installed 28-bone human_skeleton. Clip -> animation -> import source GUIDs were checked explicitly. Hashes are in `sdk-writer-result.json`.

Native `ModuleData/project.mbproj` explicitly registers `soln_action_sets`/`action_set` and `soln_action_types`/`action_type`. Scratch registration uses those canonical IDs and validates against the installed files. Actual runtime merge still needs observation.

Root owns deployment/runtime. Candidate stage: `sdk-scratch/Assets/*.tpac`, `RuntimeDataCache/*.rdc`, `ModuleData/*`. Do not deploy the SDK repository. Action `act_mnbl_probe_sweep`; set `as_mnbl_probe`; clip `mnbl_probe_sweep`; data `mnbl_probe_sweep_data`. Unlike the editor handoff, this package currently uses empty clip flags and 0.3-second blend from the neutral metadata template. This is a playback diagnostic, not an attack-class clip.

`src/MNBL.CombatProbe/AnimationProbe.cs` compiles against installed assemblies. It checks availability, applies a complete known configuration with only its action set changed, records identity/progress/hand and spine positions, and restores full configuration. The default constructor is restricted to the completed isolated combat fixture; arbitrary callers must supply the captured original configuration. Acceptance/index resolution never implies visual playback success.

## Reproduction on this machine

Run from `C:\Users\davgo\Documents\GitHub\MNBL-modding-repo`. Python scripts use their own folder as the working artifact root; evidence copies are archival sources, not commands to execute in place. Existing executable scripts and assets live in `build/combat-feasibility/animation/`.

Prerequisites: Blender 5.2.1 at `C:\Program Files\Blender Foundation\Blender 5.2\blender.exe` (bundled NumPy 2.3.4, verified); installed Bannerlord + Modding Kit at `D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord`; SDK clone under `build/combat-feasibility/animation/editor-free-sdk`, pinned commit above. SDK requirements.txt lists `numpy>=1.24`; no packages were installed. Plain system Python here lacks NumPy, so use Blender's isolated background process.

```powershell
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' -b --factory-startup --python-exit-code 1 --python build/combat-feasibility/animation/author_probe.py
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' -b --factory-startup --python-exit-code 1 --python build/combat-feasibility/animation/validate_export.py
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' -b --factory-startup --python-exit-code 1 --python build/combat-feasibility/animation/build_packages.py
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' -b --factory-startup --python-exit-code 1 --python build/combat-feasibility/animation/audit_packages.py
```

To reconstruct ignored build inputs, copy `author_probe.py`, `validate_export.py`, `build_packages.py`, `audit_packages.py` from this evidence folder into the artifact root above. Clone the SDK URL into its `editor-free-sdk` child and check out the pinned commit. Run the author/export commands first to recreate `.blend`, `.fbx` and receipt; copy retained `ModuleData` to `sdk-scratch/ModuleData` before the audit. The game skeleton path is explicitly bound in `author_probe.py`. No module installation command is part of this procedure.

Successful build output reports 61 keys, duration 2.0, end key 60.0, cache 61 frames. Audit must report zero problems/advisories; generated `sdk-scratch/result.json` and `audit.json` contain hashes, references, exact changed bytes and bone mapping. Fresh builds generate new UUIDs, so package hashes can differ while invariants remain identical; rebuilding an existing stage preserves IDs.
