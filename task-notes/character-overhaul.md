# Character overhaul

## REA follow-up — 2026-10-16

Requested: inspect DD2 character creation (installed demo or full game) with REA and determine the Bannerlord integration path. This is investigation, not authorization to launch production from the existing epic.

- R1 (root, accepted): REA inspected installed Core, MountAndBlade and creator view-model assemblies. Artifact hashes, metadata identities, method anchors and call edges are retained in `evidence/character-overhaul/rea-2026-10-16.json`; focused findings are in `bannerlord-static-findings.json` beside it.
- R2 (/root/dd2_reference, accepted by root): `evidence/character-overhaul/dd2-static-findings.json` records installed demo/full-game identities and scoped local enum evidence. Root read the findings, checked representative source sections, independently recounted the three principal enums and verified the header hash.
- R3 (root, accepted): integration assessment and next experiment below reconcile the new evidence with the existing P0-P5 plan. `evidence/character-overhaul/verification.json` records source-hash, enum-count and retained-evidence-reference checks.
- This static feasibility pass is complete. DD2 native implementation, live control mapping, full parity and the mod itself remain unverified/unimplemented. REA reported Ghidra unconfigured, IDA unconfigured and Hopper unsupported on Windows. No native session was opened, game launched, game file changed, tool installed or production epic started.

### New evidence and its meaning

The full game is now Steam build **25920212**; the installed Character Creator & Storage demo is build **24118868**. The demo executable is `D:/SteamLibrary/steamapps/common/Dragons Dogma 2 Character Creator & Storage/DD2CCS.exe`. The full game has existing REFramework files; the demo has no existing loader/dump in the inspected installation. Fresh compatibility is unknown: the full-game historical log includes a crash-dump attempt.

A local full-game `Enums_Internal.hpp` now exists. It contains **610 editor entries, 71 face-edit keys and 35 body-edit keys** at lines 17258, 20178 and 113985 respectively. These are different catalogs, not additive counts of visible human sliders. They include UI headings, presets, adjustment subkeys and nonhuman entries. The header predates the current executable and is not authenticated to its build; use it as an inspection index, not current binary truth.

Named body candidates include torso length, arm length, separate upper/lower muscle and fat, neck length/thickness and chest/waist/pelvis dimensions. Detail candidates include per-eye settings, hair root/tip colors, skin roughness, and layered tattoo placement/roughness/metallic. Names also expose body/face catalogs, clamp and influence maps, material baking and facial-animation components. They do not establish evaluation order, deformation equations, ranges or save compatibility.

REA established these file-backed Bannerlord call edges:

`FaceGenVM.UpdateFace -> BodyGenerator.RefreshFace -> MBBodyProperties.ProduceNumericKeyWithParams -> IMBFaceGen`

`BodyGenerator.SaveCurrentCharacter -> BasicCharacterObject.UpdatePlayerCharacterBodyProperties`

The first path rebuilds packed appearance; the second is a player-appearance update boundary, not proof of completed disk-save persistence. `FaceGenVM` also has undo/redo/reset, parameter refresh and a call to `ClampForMultiplayer`; multiplayer needs its own validation. The current procedural-world `GeneratedCharacterCreation.cs` handles culture choices, not this visual overhaul.

REA confirms eight UInt64 static key fields (512 bits), separate age/weight/build fields and `FaceGenerationParams.Create` allocating **320 float weights**. The existing XML still declares **63 deform keys for each of ten human skins**, all on `human_skeleton`; some keys are hidden/system controls. Neither 320 weights nor 512 packed bits proves spare independently saved controls. Native packing, constraints and deformation remain the decisive unknowns.

Primary REA records: Core fields `ev_c72f2419bcb08e15ad978b07ed21c2b0e15a296cd46553e69696a0a4a7f8751b`; native interface/wrappers `ev_6a0db2ec86951852142de9fdd68ae6f8e3321a553fc006c06a5aa91a9fbca27e`; parameter allocation `ev_0f30c14c9d5abf58d381bbadaf9a0aa147f3aa5ef7c3bf0f4c8503580b3d48b9`; BodyGenerator `ev_dcff05fc6928c038211a919575387a8539e7ffb6404a60b5aff6e3563551c2d9`; FaceGenVM `ev_fc55bcd0947794f7cd5e9d179d6897caaa28f041ded3c692f46e11365f0d8d29`. All are static observations, not executed behavior.

### Recommended integration design (inference, not implemented)

1. **Measure DD2 controls and author Bannerlord equivalents.** Obtain current-build type/object metadata, then record baseline/min/mid/max/restore for visible human options with fixed-camera views. Classify each as geometry, joint transform, surface detail or preset. Similar names are not a numerical conversion formula; a DD2 save cannot yet be translated into Bannerlord appearance.
2. **Reuse the Bannerlord creator and appearance pipeline where proven.** Wrap its refresh/history/preset flow; map supported controls onto native parameters. For example, DD2 eye-distance and mouth-width keys have plausible native counterparts, but require visual calibration. Preserve native key semantics and existing character identities.
3. **Separate body and surface extensions.** Arm/torso proportion changes require skeleton, animation, collision and clothing proofs. Independent skin/hair/eye/tattoo parameters need material/compositing and per-character resource isolation. A facial-slider UI does not establish either subsystem. Original heads/bodies/materials and their detail levels remain substantial art work.
4. **Prove storage before extending it.** Round-trip any added parameter through native packing first. If unsupported, use a versioned mod-owned appearance record keyed to stable characters, with separately proven render application and cache identity. Extra storage alone cannot make the engine deform a mesh. Deterministic troop appearances, aging, portraits, mission spawns and old-save migration remain explicit tests.

The official [asset override documentation](https://moddocs.bannerlord.com/asset-management/asset-types/overriding_assets/) supports replacing mesh/material/texture resources in a module; mesh replacement covers the entire resource. [XML merging](https://moddocs.bannerlord.com/bestpractices/merging_module_xml_files_with_native/) documents `soln_skins`. These support the asset/configuration approach without proving arbitrary runtime morphs. [REFramework Object Explorer](https://cursey.github.io/reframework-book/object_explorer/object_explorer.html) documents inspecting TDB types/fields and generating an offline SDK dump. Rechecked 2026-10-16.

### Smallest decisive experiment

First acquire a current-build creator baseline through the full game's existing REFramework, or use the demo after separately validating its tooling. Inspect `GUICharaEditData`, `CharacterEditManager`, `FaceEditor`, `BodyDetailEditor`, body/face table catalogs and clamp/influence catalogs. Read actual fields/resources before invoking methods. The exact scoped baseline acquisition plan is in the DD2 evidence file; no capture script was installed or run here.

Then measure and recreate one facial control, one difficult body control (arm length), and one surface control (skin roughness) on original diagnostic assets. Require visible minimum/maximum effects, restoration, save/reload, a hero and troop, helmet/armor fit, blinking/speech and mounted animation. If native persistence fails, test the extended-record path; if body or material application fails, resolve that mechanism before expanding production. One working control proves its path only; full coverage still requires the P0 inventory and P1 capacity/composition tests. The existing all-human P2-P5 quality, maturity, equipment and battle-performance gates remain in force.


## Dark Mechanicus planning handoff

User requested a fully vetted Dark Mechanicus epic on 2026-10-09. The epic is saved and technically reviewed; implementation remains unstarted. The user confirmed equivalent DD2 controls and quality with an original asset catalog, rather than reproducing DD2's entire human catalog. Single-player versus multiplayer scope was asked and remains pending; the saved contract gate forbids dependent production from guessing it.

Canonical implementation record: **DD2-quality human character overhaul and creator for Bannerlord**, epic `ep_01m4gbx49w72je0ezgjcfkxtjx`, saved revision 1 `rv_01m4gcnmgf4120yena1dg3pn7n`. It contains 44 required work tickets plus three milestone acceptance nodes, with 80 dependency links. Dark Mechanicus validated it with zero errors and zero warnings; saved-plan readback confirmed every required ticket is covered, no draft/pending write/conflict remains, and no run has started. The saved epic supersedes the preliminary sequence below as the execution plan. The remaining mode decision is explicit, so the scope is not yet production-finalized.

Root was the sole Dark Mechanicus writer. Independent architecture and technical-art reviewers inspected the full plan; fixes covered complete option capacity, body/render prerequisites, native channel preservation, bounded future-production expansion, full asset/equipment/maturity coverage, shared-editor ownership, cache isolation, old-save identity, diagnostic attachment fixtures, mixed-outfit ownership and naval baselines. Review record: `cm_01m4gcpz7a8yn3dkf9nv3x0wmj`. Local authoring inputs and readback evidence are under `.darkmechanicus/local/character-planning/`; `verification.json` records the final checks. No game implementation or unrelated epic mutation occurred in this planning task.

## Requested outcome

Reverse engineer Dragon's Dogma 2's character creator and bring comparable visual quality and customization to Bannerlord. Scope confirmed by the user: all human characters, including troops.

The user subsequently selected **feasibility investigation and a concrete development plan first**, rather than implementation now. This turn's deliverable is the investigation and plan. The eventual complete overhaul remains the product objective. No asset quality or engine capability is considered proven without inspection and runtime evidence.

## Acceptance

- A1: Identify the local Bannerlord/DD2 installations and available modding inputs. Verify through manifests and files.
- A2: Establish Bannerlord customization entry points and constraints from local files and primary documentation. Clearly separate verified facts from unknowns.
- A3: Establish an evidence-based method for inspecting DD2's creator and its parameters. Do not invent parameter counts or claim parity without comparison.
- A4: Produce a concrete staged development plan with acceptance gates for all humans, armor, facial animation, persistence and troop performance. Verify against findings from A1-A3.
- Future implementation acceptance (not requested this turn): implement the overhaul and visually verify against DD2 and the user's expectations in actual gameplay.

## Work

- Local inventory (A1): accepted; owner root; Steam manifests, executables, version XML and installed content inspected. This is installation presence/version evidence, not a full Steam integrity verification.
- Bannerlord feasibility (A2): accepted; worker /root/bannerlord_pipeline; root independently verified skins, asset-override documentation and installed managed metadata using bundled Mono.Cecil.
- DD2 inspection approach (A3): accepted; worker /root/dd2_inspection; root checked installed log, packed asset inventory, Object Explorer documentation and the importer's disabled blend-shape path.
- Development plan (A4): accepted; owner root; reviewed against installed-file findings by /root/bannerlord_pipeline. Its required channel-preservation gate was incorporated. Implementation intentionally deferred by the user's explicit choice. This planning pass is complete; the overhaul is not implemented.

## Constraints and evidence

- User prefers clarification over guessed requirements, concise reporting and explicit disclosure of incomplete work.
- No existing project AGENTS.md found in the workspace or inspected ancestors; instructions provided in chat apply.
- Existing untracked .darkmechanicus/ and .mcp.json are unrelated and must be preserved.
- Steam library metadata: C:/Program Files (x86)/Steam and D:/SteamLibrary.
- Existing .darkmechanicus epic concerns procedural campaigns with War Sails, not this character overhaul; it was inspected but not modified. This task note is the sole character-overhaul record.

## Feasibility finding — 2026-10-09

A substantial all-human visual overhaul has a credible implementation path. Exact DD2 option parity and final visual quality remain unproven. The current evidence establishes asset replacement and parameter entry points; it does not establish working new morphs, unrestricted body proportions, equivalent skin rendering, or acceptable battle performance.

The recommended approach is to study DD2's observable controls and deformation behavior, then author a Bannerlord-compatible character system and art assets. Directly importing a static DD2 model would not establish creator functionality, animation compatibility or rendering parity.

### Verified local inputs

- Bannerlord and its Modding Kit share `D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord`. Native/SubModule.xml and the client Version.xml identify v1.4.8. Steam manifests identify game build 24573425 and kit build 24575232; different app build IDs do not by themselves indicate incompatibility. Kit startup has not been tested.
- `Modules/Native/ModuleData/skins.xml` contains one human race and ten skins: male/female across adult, teenager, tween, child and toddler. All reference `human_skeleton`. Male skins reference `head_male_a`; female skins reference `head_female_a`. Body references vary by maturity, with some shared across stages. Each skin declares 63 deform-key elements. This is a content count, not a proven engine limit or a count of independent visible face sliders.
- The same XML explicitly couples some face deformations to hair and helmet scaling. A new head must satisfy these relationships and neck/body stitching rather than merely look correct in isolation.
- Read-only metadata inspection of installed `TaleWorlds.Core.dll` and `TaleWorlds.MountAndBlade.dll` confirms eight UInt64 parts in `StaticBodyProperties`, age/weight/build dynamic fields, and a fixed-array marshal size of 320 for `FaceGenerationParams.KeyWeights`. `GetNumEditableDeformKeys`, `GetDeformKeyData` and `ProduceNumericKeyWithParams` call the native `IMBFaceGen` interface. Neither storage size proves spare capacity or support for an arbitrary number of independent saved controls. Root reproduced these checks using the installed `bin/Win64_Shipping_Client/mono/lib/mono/gac/Mono.Cecil/0.11.0.0__0738eb9f132ed756/Mono.Cecil.dll`; an initial assumed Cecil path was absent, then corrected by file discovery.
- DD2 is installed at `D:/SteamLibrary/steamapps/common/Dragons Dogma 2`. The main PAK is 68,963,914,239 bytes. REFramework's `dinput8.dll` and autorun scripts are present. The previous session log identifies DD2 and REFramework commit `0a07faae46a6fc1c51cd1699a0cf99a3f9c2679f` (tag v1.5.9.1 plus 505 commits). No fresh compatibility test was run.
- The DD2 investigation found no loose creator mesh/material/user-resource files or existing offline SDK dump in the inspected installation. Its complete slider inventory, parameter schema and deformation implementation have not been reverse engineered in this pass.

### Primary-source findings

- TaleWorlds documents replacing meshes, materials and textures through module assets, with load-order precedence. Mesh replacement includes the entire mesh resource, so its detail levels must be supplied correctly. [Asset overrides](https://moddocs.bannerlord.com/asset-management/asset-types/overriding_assets/).
- TaleWorlds documents native XML merging, including `soln_skins` registered through `project.mbproj`. This provides a candidate configuration path to test on installed v1.4.8. [XML merging](https://moddocs.bannerlord.com/bestpractices/merging_module_xml_files_with_native/).
- Bannerlord's mesh system groups levels of detail; close-up quality and distant troop cost must be designed together. [Meshes and LODs](https://moddocs.bannerlord.com/asset-management/asset-types/meshes/).
- REFramework Object Explorer provides runtime type/object inspection and an SDK-dump facility. Begin with targeted creator inspection. [Object Explorer](https://cursey.github.io/reframework-book/object_explorer/object_explorer.html).
- RE Mesh Editor supports DD2 formats, but its current parser sets `IMPORT_BLEND_SHAPES = False` because that importer path is broken. Mesh import support does not establish a complete creator-deformation extraction pipeline. Runtime observation and independently authored morphs avoid making that importer a mandatory dependency. [Parser source](https://raw.githubusercontent.com/NSACloud/RE-Mesh-Editor/main/modules/mesh/file_re_mesh.py).
- Relevant modding work already exists: [Detailed Character Creation](https://github.com/PoPoWanObi/CharacterCreation) includes an additional-sliders module; [Noble Nobles](https://www.nexusmods.com/mountandblade2bannerlord/mods/194) describes NPC/troop appearance changes. These establish prior related work, not DD2 parity or compatibility with installed v1.4.8.

## Proposed development sequence

### P0 — Measure the reference and baseline

Owner: reverse-engineering/programming work. Inputs: installed DD2 + REFramework; untouched Bannerlord v1.4.8 baseline. Can run alongside P1.

Record every visible human-creator option in DD2, its default/range, preset dependency, reset behavior and visible effect. Capture front/profile/three-quarter comparisons while changing one control at a time, including combinations at extremes. Use Object Explorer to associate controls with actual fields, resource references and mesh/bone/material changes; do not infer the implementation from labels alone. Keep an explicit unknown/unmapped list.

Capture Bannerlord reference faces, dialogue, equipment and troop scenes on this PC with recorded settings. Measure median and p95 frame time, GPU memory and loading time in repeatable battles at several troop counts up to the intended maximum supported by the setup.

Exit evidence: a sourced option inventory, reproducible visual references, recorded game/tool versions, and measured Bannerlord baselines. No hard performance or parity promise before these exist.

### P1 — Prove the engine extension path

Owner: engine integration. Dependencies: installed editor and source/build toolchain verified; no production art dependency.

Create a separate diagnostic module. Test a custom head with a known deformation and multiple detail levels; verify module override/skins merging and the existing morph path. Establish how import preserves the current 63 deform channels and the separate facial-animation requirements. Then test an additional independent parameter through native face generation, character creation, mission spawn, export/import and cold save/reload. Check supported ranges and precision rather than interpreting array sizes as a supported slider count.

If native persistence cannot represent the required options, prototype a versioned extension record keyed to stable hero identity and deterministic troop appearance seeds. Prove that the engine can actually apply the extra deformation before selecting this architecture: storing an extra float alone is not a solution. If neither route works, record the unsupported options explicitly and revisit scope before producing production assets.

Exit evidence: one custom head renders and deforms correctly, and one additional control survives the full supported lifecycle without corrupting native appearance data. Sweep all 63 declared deform keys, including hidden age/head/ear controls, and preserve their intended ID/time-point meanings or prove an explicit migration. Test a representative corpus of existing hero face keys and troop templates rather than only newly created faces. This is a technical probe, not the finished visual upgrade.

### P2 — Produce a visible quality benchmark

Owner: character/technical art, with integration support. Dependencies: P0 reference measurements and P1 import/deformation evidence.

Create one production-quality adult male and female head, matching neck/body interfaces, with eyes, teeth, skin materials, representative hair and all required expressions. Author topology and deformation targets for the chosen Bannerlord path. Preserve the existing skeleton initially to retain a tractable animation/armor compatibility surface; expand only when a specific measured requirement demands it.

Evaluate fixed screenshots in the creator, dialogue, daylight and interiors. Test blinking, speech, extreme faces, helmet fit, hair deformation and every supplied detail level. Compare against agreed DD2 reference traits: facial structure, silhouette, eye quality, skin response and deformation quality. A flattering creator screenshot alone does not pass.

Exit evidence: a visibly improved male/female pair in actual Bannerlord gameplay with working animation/equipment. Repeat the P1 channel sweep and existing-face/template corpus on the production assets so a good-looking default face cannot conceal broken controls. User review here establishes the art benchmark before scaling production.

### P3 — Build the body and equipment pipeline

Owner: technical art + engine integration. Dependencies: P1 native/custom control decision; P0 body-option inventory. Run body feasibility probes early during P2 because they may determine attainable parity.

For each desired control, identify whether it changes mesh shape, skeletal proportions, material appearance or several of these. Prove deformation on the body and representative tight clothing, rigid armor, boots, gloves and helmets. Test mounted poses, combat, hand/foot contact, body seams and collision alignment at extremes. Establish whether existing equipment can be adapted generically or needs authored corrective assets.

Exit evidence: supported body controls function when clothed and animated. Any options requiring unresolved engine changes or extensive armor refits remain explicitly unimplemented; do not silently substitute naked-body-only support.

### P4 — Extend the creator and apply it to the population

Owner: UI/gameplay programming + art. Dependencies: validated controls and production assets from P1-P3.

Build grouped controls, numeric precision where useful, resets, presets, undo and import/export around actual supported data. Replace all relevant human skin assets, including age-specific bodies and adult aging behavior. Apply coherent appearance distributions to nobles, companions, villagers, bandits and troops; cover births, maturation, inheritance and newly generated characters. Test deterministic troop generation and stable hero identity across saves. Avoid generating high-detail meshes every frame or storing identical state for every disposable troop.

Exit evidence: representative characters from each category and all ten native skin definitions use the new system in gameplay; appearance remains correct after reload, aging and repeated mission spawns. Editor-only or player-only coverage does not pass the all-human requirement.

### P5 — Optimize and validate the complete mod

Owner: integration/QA + technical art. Dependencies: P4 complete.

Use shared materials, reusable resources and carefully authored detail levels. Compare the same battles/settings to P0, recording frame-time distributions and memory rather than average FPS alone. Include crowded towns, dialogue, inventory portraits, sieges and mounted combat. Set the performance acceptance budget from the measured baseline before asset production scales. Check cold starts, save/reload, extreme slider combinations and load-order conflicts. Validate existing-save migration separately from new campaigns; do not advertise it before the test passes.

Exit evidence: a version-pinned module with visual comparisons, population coverage, persistence results and measured performance inside the agreed budget. Exact DD2 parity is accepted only against the P0 inventory and art benchmark.

## Work scale and remaining uncertainty

This requires character modeling, sculpting, texture/material work, deformation/rigging, hair, equipment compatibility and optimization as well as C# and UI work. Code alone does not supply the requested quality. The amount of body/armor work and native engine extension determines the schedule; a credible completion estimate requires P0-P1 and the body feasibility probe.

No game was launched or modified during this pass. No assets were extracted, copied into the repository or replaced. No prototype, visual parity, complete option parity, save compatibility or battle performance has been demonstrated. These are deliberately future gates under the user's planning-only instruction.

## Recommended first implementation milestone

Run P0 and P1 together, then deliver the P2 head benchmark. Success is a visibly better animated male/female pair on both a hero and ordinary troops, with one verified additional persistent control and a measured crowd test. This is the first proof toward the full all-human overhaul, not a reduction of its scope.
