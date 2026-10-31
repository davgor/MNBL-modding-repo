# Bannerlord map-art republish — Codex

Published and restored on 2026-10-20 (America/Chicago). **Limitations/departures:** `do_not_blend_normals` was unavailable and omitted as permitted. No explicit `sRGB` texture flag was exposed or set; diffuse gamma handling is inferred from the installed shader source, detailed below. The editor automatically refreshed all 24 vein textures before the first screenshot, so there is no screenshot of the old splotches. Only air diffuse/normal received an additional manual Reimport command. CustomBattle was inherited enabled alongside the requested modules. No in-game visual test was performed.

## Deliverables

- `out/MNBL.ProceduralWorld/AssetPackages/pack0.tpac`: **70,834,304 bytes**; SHA-256 **`36CE4D89BAA4B30CD6FF6548E96B5348124084DA9ECF4D353693B4C0B951AF96`**.
- The pack contains exactly **39 records: 29 textures and 10 materials**. No extra/missing names. All texture data chains decode successfully.
- `assets/Assets/magic_ground/`: 24 refreshed texture records and eight unchanged materials.
- `assets/Assets/magic_water/`: two textures and `mnbl_magicwater`.
- `assets/Assets/water_surface/`: three textures and `mnbl_water_surface`.
- `out/MNBL.ProceduralWorld/` is the editor's whole-module export, including existing module content. The requested integration artifacts are the pack and the 39 records under `assets/Assets/`.
- `restored-session/` retains files moved out during restoration; `restored-Bannerlord.Harmony/` retains the temporary dependency. These are recovery evidence, not additional integration inputs.

## Shared-install checks and launch

Read `../publish/REPORT.md` before `CODEX-TASK.md`. Claude's install reservation was supplied in the task. Fresh preflight at 22:52:21 -05:00 found no Bannerlord, Bannerlord.Native, TaleWorlds.MountAndBlade.Launcher or Watchdog process; opened and closed `native-test.lock` with FileShare.None. Repeated those checks immediately before the single Steam launch (`launch-check-1.json`). No process was killed or attached to.

Recorded both native DLL hashes, snapshotted and backed up all 132 in-scope module files, saved directory inventory, SubModule.xml, LauncherData.xml and engine config. The traversal explicitly excluded `SceneObj/mnbl_world_*` before descending into those directories. Inputs were hashed separately.

Staged the supplied records/sources. Reused the prior session's retained Harmony 2.4.2.248 module, including its editor binaries, as temporary `Modules/Bannerlord.Harmony`. Copied the installed MNBL client-bin dependencies into its temporary `bin/Win64_Shipping_wEditor` folder. Did not change SubModule.xml or any repository content.

Started through Steam Library > Mount & Blade II: Bannerlord - Modding Kit > Launch > Play > Editor. Hosting process: `bin/Win64_Shipping_wEditor/TaleWorlds.MountAndBlade.Launcher.exe`, PID 51248; session Watchdog PID 40844. Enabled Harmony, Native, SandBoxCore, Sandbox, StoryMode, NavalDLC and MNBL.ProceduralWorld. BattleScale stayed disabled. CustomBattle was already enabled and remained enabled during the session; original launcher state was restored afterward. No direct executable launch was attempted.

## Vein refresh and pixel evidence

Opening Resource Browser on MNBL showed the new thin branching veins in a 4 x 4 atlas grid immediately: source-change detection had already refreshed all 24 records. Explicitly invoked Reimport again on `mnbl_magic_air_d` and `mnbl_magic_air_n`. The before/after screenshots therefore show first-observed refreshed veins and the result after the additional air reimports, rather than old/new source art.

For all eight schools (weave, umbral, infernal, radiant, fire, water, earth, air), preserved the existing texture GUIDs and settings: 2048 x 2048, 12 mips; diffuse Albedo_HQ/BC7 with `has_alpha`, `dont_resize_in_atlas`; normal Normal/BC5 with `is_bumpmap`; specular Specular/DXT1 with `is_specularmap`. All eight original material files are byte-identical to the supplied input records.

Decoded every texture's top mip from the published pack and compared it with the supplied PNG. Every one of the 24 vein textures has a different decoded-pixel SHA-256 from the prior published pack. Per-channel mean absolute errors against the new sources are at most 2.216/255 for those 24 textures. Across all 29 textures the maximum relevant-channel mean error is 5.409/255 (magic-water normal), consistent with lossy compression. BC5 stores RG only, so its discarded blue channel is excluded from that comparison. Water-surface specular matches its source exactly. Full per-texture metrics and prior-pixel hashes are in `pack-verification.json`.

## New texture settings actually saved

- `mnbl_magicwater_d`: 2048 x 2048, 12 mips, Albedo_HQ/BC7; `has_alpha`, `dont_resize_in_atlas`.
- `mnbl_magicwater_n`: 2048 x 2048, 12 mips, Normal/BC5; `is_bumpmap`.
- `mnbl_water_surface_d`: 1024 x 1024, 11 mips, Albedo_HQ/BC7; `has_alpha`, `dont_resize_in_atlas`.
- `mnbl_water_surface_n`: 1024 x 1024, 11 mips, Normal/BC5; `is_bumpmap`.
- `mnbl_water_surface_s`: 1024 x 1024, 11 mips, Specular/DXT1; `is_specularmap`.

No separate sRGB control appeared in the Resource Browser texture inspector or full Texture Editor, and no serialized `srgb` flag was present. Read-only inspection of installed `Shaders/Sources/deferred_decal.rsh` found `INPUT_TEX_GAMMA(pp_modifiable.diffuse_sample.rgb)` immediately after diffuse sampling. `definitions.rsh` defines that macro as the polynomial gamma-to-linear conversion `x * (x * (x * 0.305306011 + 0.682171111) + 0.012522878)`. This supports shader-based interpretation of gamma-encoded diffuse color, not a claim that an explicit texture sRGB flag was enabled or runtime color was tested. Source paths, hashes and exact lines are saved in `srgb-shader-evidence.json`. An earlier clarification about the missing control became unnecessary after finding this evidence; publishing proceeded with this documented limitation.

## New materials

Created each using Create Override > Material of Native `mainmap_decal_undervillage`, then renamed the custom override before editing. Native's material was not edited. Both retain the template's `deferred_decal` shader.

`mnbl_magicwater`:

- Flags: `gbuffer_alpha_blend`, `use_albedo`, `render_on_terrain`, `use_normalmap`.
- Diffuse `mnbl_magicwater_d`; normal `mnbl_magicwater_n`; specular None; heightmap None.
- No `use_specular`, `water_foam_decal`, `albedo_multiply`, `hardlight_blend`, `render_on_objects`, `render_on_grass`, `height_masked` or `override_visibility_checks`.

`mnbl_water_surface`:

- Flags: `gbuffer_alpha_blend`, `use_albedo`, `render_on_terrain`, `use_normalmap`, `use_specular`.
- Diffuse `mnbl_water_surface_d`; normal `mnbl_water_surface_n`; specular `mnbl_water_surface_s`; heightmap None.
- `do_not_blend_normals` was absent from both the Resource Browser inspector and full Material Editor. Omitted under the task's explicit exception.
- No `render_on_objects`, `render_on_grass`, `albedo_multiply`, `hardlight_blend`, `water_foam_decal`, `height_masked`, `override_visibility_checks` or `is_road`.
- Template Specular Coef, Gloss Coef and Normal Depth stayed at 1.000. The freshly created template metadata is retained as `surface-template-before.tpac`.

The saved records' ordered texture-slot GUIDs and flags were checked. All ten serialized material record bodies occur byte-for-byte in the published header, confirming that the packed materials match the saved assets (`asset-verification.json`, `pack-verification.json`).

## Publish and screenshots

Saved textures/materials; File > Publish Module > MNBL.ProceduralWorld; selected Compile Shaders and Client packing; chose `C:/Users/davgo/m2/publish2/out/`. Waited for 100% and the completed pack at approximately 23:21. Exited with Alt+F4 and selected No on the blank-scene save prompt. Waited for launcher and Watchdog to finish normally, then repeated the process and exclusive-lock checks before restoring.

Screenshots in `shots/`:

- `veins-before-reimport.jpg`, `veins-after-air-reimport.jpg` — qualification above applies.
- `material-magicwater-settings.jpg`, `material-water-surface-settings.jpg`.
- `magicwater-d-settings.jpg`, `magicwater-n-settings.jpg`.
- `surface-d-settings.jpg`, `surface-n-settings.jpg`, `surface-s-settings.jpg`.
- `surface-d-texture-editor-no-srgb.jpg`, `surface-full-shader-flags.jpg`.
- `publish-done.jpg` — 100% completion.

## Restore verification

Copied all three Assets folders to `assets/Assets/` before removing session files. Re-ran asset verification on that retained copy: all 39 records pass. Restored module content from the fresh backup, removed added empty directories, moved temporary Harmony out, restored LauncherData.xml, and checked safely_exited.

The first restore pass encountered PowerShell 5 JSON-array wrapping: it retained original files as well as added files under `restored-session/`, then stopped before copying the baseline back. No files were discarded. Corrected array deserialization and resumed from the complete backup. Final verification passes; this was a recovered scripting failure, not an unresolved restore gap. The pixel checker also initially lacked NumPy; replaced that dependency with Pillow's ImageChops/ImageStat and reran successfully.

- `module-after.json` equals `module-before.json`: 132 files, zero relative-path/size/SHA-256 differences; zero directory differences.
- SubModule.xml and LauncherData.xml are byte-identical to their before copies.
- `safely_exited = 1` before and after; no config repair needed.
- Temporary Harmony is absent again; its 73 staged files matched their staging hashes before removal.
- Added AssetSources, Assets, editor-bin dependencies, published pack, caches and shaders inside the module were removed from the install. Session copies are retained outside it.
- `SceneObj/mnbl_world_*` remained excluded from traversal and restoration; no direct operations edited those scenes. The editor's whole-module publishing can export existing scenes. No other module was directly modified except the explicitly authorized temporary Harmony.
- All 61 input files are unchanged by path, size and SHA-256 (`inputs-verification.json`).
- No process was killed. Post-exit shared-install process and exclusive-lock checks passed (`pre-restore-check.json`).

Native DLL SHA-256, identical before and after:

- `TaleWorlds.Native.dll`: `2A5E0E0B15513EBB7052D747A621B82C50EE834553EBA283B33BBBC33766E12A`.
- `TaleWorlds.MountAndBlade.dll`: `19387F31557FF840D14F378F6BBDF1D58FCFF406AB9FF2294DFBB3E49A50B87E`.

Evidence: `restore-verification.json`, `dll-before.json`, `dll-after.json`, `pre-restore-check.json`, `module-before.json`, `module-after.json`, `asset-verification.json`, `pack-verification.json`. No repository integration or in-game rendering test was performed.
