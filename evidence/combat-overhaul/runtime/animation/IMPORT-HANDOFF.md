# Custom-animation probe: editor handoff

Status: original animation authored and FBX roundtrip validated. No engine import or in-game playback has been observed yet. This is diagnostic motion, not production animation quality.

## Inputs

- `mnbl_probe_sweep.fbx`: animation-only FBX, original rotation keys over the shipped human bind skeleton.
- `mnbl_probe_sweep.blend`: editable original.
- `author_probe.py`: repeatable authoring/export.
- `authoring-receipt.json`: hashes, bone names, settings, phase definitions.
- `validate_export.py`, `roundtrip-validation.json`: FBX reimport verifies all 31 bone names and sampled motion positions with maximum error 0.000000692 armature units.

The imported armature retains the supplied skeleton's 0.01 object scale and bind transform. Do not apply a guessed scale correction before observing the engine preview. No mesh is included. Root object is `bip01_notused`. Exported take is `bip01_notused|Scene`; actual imported engine resource name must be read from the editor.

## Editor procedure

1. Use an isolated editable `MNBL.CombatProbe` module. Keep source FBX under its `AssetSources`; the editor creates actual TPAC resources. Do not fabricate a TPAC or rename the FBX to one.
2. Import the FBX as animation data through Resource Browser. Confirm 30 fps, two seconds, matching human skeleton and a visible right-arm/torso sweep. Rename the imported animation resource `mnbl_probe_sweep_data` if possible; otherwise record its actual resource name and use that in step 3.
3. Resource Browser: Create -> Animation Clip. Set unique clip ID `mnbl_probe_sweep`, `anim_data_name` to the actual imported resource, `duration` 2.0 seconds. Select its full sampled frame range: source authored frames 1–61, but the editor may normalize the range to 0–60. Inspect the import rather than assuming the start frame. Use `blend_in_period` 0.1. For the first stationary diagnostic use `enforce_all`, `disable_hand_ik`, and `disable_foot_ik`; no cyclic flag. Confirm the exported clip actually exists in the module's assets.
4. Stage the two supplied ModuleData XML files without overwriting existing module files. They declare a unique action and a derived human action set. These follow the installed Native XML schema; module loading of these fixed filenames still needs live validation. No unverified SubModule XmlName registration has been added.
5. Resolve `ActionIndexCache.Create("act_mnbl_probe_sweep")` and `MBActionSet.GetActionSet("as_mnbl_probe")`. Log actual indices and invalid results. Preserve the agent's full existing `AnimationSystemData`, changing the action-set field to the probe set; do not construct incomplete monster/bone data. Play on channel 0 using SetActionChannel, observe the boolean result and current action through the 2-second motion.
6. Capture visible distinctive motion, agent pose before/after, action identity/timing, log and asset hashes. Repeating an imported native clip or merely resolving an action ID does not pass this probe.

The authored phases are windup frames 1–25 (right hand rises/back, held), sweep 25–43 (crosses down/front, held), recovery 43–61. This probe tests registration and custom skeletal playback only; animation-driven weapon contacts, attack cancellation, mounted variants and production rig quality require separate tests.

## Evidence and known limits

Blender's FBX importer defaults to adding one frame. The roundtrip validation explicitly uses `anim_offset=0.0`; this fixes the apparent one-frame mismatch without altering the source export.

Official source: https://moddocs.bannerlord.com/asset-management/asset-types/animations/

Official module asset packaging source: https://moddocs.bannerlord.com/asset-management/asset-types/overriding_assets/

The official animation workflow documents FBX animation data -> Animation Clip -> action type/action set. Native `SubModule.xml` does not itself register action_types/action_sets under Xmls; actual custom-module consumption is an explicit runtime probe, not yet a fact.
