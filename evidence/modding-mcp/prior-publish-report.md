# Magic-ground publishing report — Astra

Published successfully. The installed MNBL module was restored exactly to the fresh post-Steam-validation baseline. **Exceptions:** the unchanged supplied pack checker reports eight false missing-material results because its name regex consumes the following metadata byte; a length-prefixed check passes all 32 records. This editor calls its normal-map flag `is_bumpmap`, rather than the spec’s `is_normalmap`. Two editor-side settings files outside the module were rewritten and left in place as instructed. No in-game visual/runtime test was performed.

## Outputs

- Published pack: `out/MNBL.ProceduralWorld/AssetPackages/pack0.tpac` — 94,687,675 bytes, SHA-256 `1A44EE55CF5EB1F39A970F4347B17E7DCC785C2B9778B5D178D2A6BA60773615`.
- Editor database: `assets/Assets/magic_ground/` — 24 texture records and eight material records.
- `assets/` also preserves every other session-added non-AssetSources file with module-relative paths: RuntimeDataCache, published AssetPackages, editor-bin dependency staging, Shaders, and shader caches for non-excluded scenes. These additional files are not all required for repository integration.
- `out/MNBL.ProceduralWorld/` is the editor’s whole-module export and includes existing module content. Integrate the requested AssetPackages and Assets, rather than treating the whole export as a new deployment.
- Original source PNGs remain unchanged. `restored-session/` retains session-added files removed from the install, including the installed source copies. `restored-Bannerlord.Harmony/` retains the temporary dependency module.

## Fresh preflight and coordination

Earlier attempt files were treated as stale. The failed fresh startup attempt is isolated under `attempt2-startup-failure/`; it was restored before retrying. After David confirmed Steam validation completed, a new preflight at 2026-10-20 01:40:41 -05:00 found no Bannerlord/Watchdog process, opened and closed `native-test.lock` with FileShare.None, recorded both native DLL hashes, snapshotted 132 in-scope module files, backed them up, and saved SubModule.xml. Before subsequent launches, process and exclusive-lock checks were repeated.
Claude was contacted through its desktop app as **Astra, not David**. Claude replied: “OK, Astra. The editor session is yours exclusively until you send RELEASE.” Claude confirmed no battle-scale session was running. CLAIM evidence is in `coordination.json` and the archived coordination screenshot. RELEASE is recorded separately after restoration.

## Startup and editor work

The initial direct startup failed with “There is no target file! Please verify your files.” David validated the Modding Kit through Steam. Steam Library → Mount & Blade II: Bannerlord - Modding Kit → Launch then worked. The hosting process was `bin/Win64_Shipping_wEditor/TaleWorlds.MountAndBlade.Launcher.exe`; Play → Editor entered Edit Mode.
Enabled Harmony first, Native, SandBoxCore, Sandbox, StoryMode, War Sails (NavalDLC), and MNBL.ProceduralWorld. BattleScale remained disabled. To satisfy David’s explicit instruction to keep metadata and install missing dependencies, copied the existing Workshop Harmony 2.4.2.248 module into a temporary Bannerlord.Harmony module and staged Harmony and MNBL client binaries under their editor-bin folders. No SubModule metadata was changed. All temporary installed dependencies were removed during restoration. The assets-only proposal was never applied.
Created the module Assets/magic_ground folder, restarted normally so the Resource Browser registered it, imported all 24 PNGs using the discovered-source import workflow, and saved each texture’s settings. PNG import succeeded; no TGA fallback was needed.
Protected vanilla materials exposed no Duplicate command. Used the editor’s Create Override → Material workflow to copy mainmap_decal_undervillage into MNBL, immediately renamed it mnbl_magic_weave, and duplicated that custom copy for the other seven materials. No vanilla material was edited.

## Actual texture settings

Every texture is 2048 × 2048 with 12 mip levels. Diffuse Type was Albedo_HQ (BC7-RGBA8), with has-alpha and Dont Resize in Atlas. Normal Type was Normal (BC5), with Is Bumpmap enabled (`is_bumpmap` in this build); DXT5 Normalmap was not enabled. Specular Type was Specular (DXT1), with Is Specularmap enabled. DXT1 matches the vanilla terrain/decal specular convention. Published textures have full data chunks and source paths under `$BASE/Modules/MNBL.ProceduralWorld/AssetSources/magic_ground/`.

| Texture | Compression | Actual flags |
|---|---|---|
| `mnbl_magic_air_d` | BC7 | has_alpha, dont_resize_in_atlas |
| `mnbl_magic_air_n` | BC5 | is_bumpmap |
| `mnbl_magic_air_s` | DXT1 | is_specularmap |
| `mnbl_magic_earth_d` | BC7 | has_alpha, dont_resize_in_atlas |
| `mnbl_magic_earth_n` | BC5 | is_bumpmap |
| `mnbl_magic_earth_s` | DXT1 | is_specularmap |
| `mnbl_magic_fire_d` | BC7 | has_alpha, dont_resize_in_atlas |
| `mnbl_magic_fire_n` | BC5 | is_bumpmap |
| `mnbl_magic_fire_s` | DXT1 | is_specularmap |
| `mnbl_magic_infernal_d` | BC7 | has_alpha, dont_resize_in_atlas |
| `mnbl_magic_infernal_n` | BC5 | is_bumpmap |
| `mnbl_magic_infernal_s` | DXT1 | is_specularmap |
| `mnbl_magic_radiant_d` | BC7 | has_alpha, dont_resize_in_atlas |
| `mnbl_magic_radiant_n` | BC5 | is_bumpmap |
| `mnbl_magic_radiant_s` | DXT1 | is_specularmap |
| `mnbl_magic_umbral_d` | BC7 | has_alpha, dont_resize_in_atlas |
| `mnbl_magic_umbral_n` | BC5 | is_bumpmap |
| `mnbl_magic_umbral_s` | DXT1 | is_specularmap |
| `mnbl_magic_water_d` | BC7 | has_alpha, dont_resize_in_atlas |
| `mnbl_magic_water_n` | BC5 | is_bumpmap |
| `mnbl_magic_water_s` | DXT1 | is_specularmap |
| `mnbl_magic_weave_d` | BC7 | has_alpha, dont_resize_in_atlas |
| `mnbl_magic_weave_n` | BC5 | is_bumpmap |
| `mnbl_magic_weave_s` | DXT1 | is_specularmap |

## Materials

All eight retain shader `deferred_decal` from the vanilla template and have `gbuffer_alpha_blend`, `use_albedo`, `render_on_terrain`, `use_normalmap`, and `use_specular`. Heightmap remains None. All three texture-slot GUIDs were independently checked against the corresponding texture records and published header.

| Material | Diffuse | Normal | Specular |
|---|---|---|---|
| `mnbl_magic_weave` | `mnbl_magic_weave_d` | `mnbl_magic_weave_n` | `mnbl_magic_weave_s` |
| `mnbl_magic_umbral` | `mnbl_magic_umbral_d` | `mnbl_magic_umbral_n` | `mnbl_magic_umbral_s` |
| `mnbl_magic_infernal` | `mnbl_magic_infernal_d` | `mnbl_magic_infernal_n` | `mnbl_magic_infernal_s` |
| `mnbl_magic_radiant` | `mnbl_magic_radiant_d` | `mnbl_magic_radiant_n` | `mnbl_magic_radiant_s` |
| `mnbl_magic_fire` | `mnbl_magic_fire_d` | `mnbl_magic_fire_n` | `mnbl_magic_fire_s` |
| `mnbl_magic_water` | `mnbl_magic_water_d` | `mnbl_magic_water_n` | `mnbl_magic_water_s` |
| `mnbl_magic_earth` | `mnbl_magic_earth_d` | `mnbl_magic_earth_n` | `mnbl_magic_earth_s` |
| `mnbl_magic_air` | `mnbl_magic_air_d` | `mnbl_magic_air_n` | `mnbl_magic_air_s` |

## Publish and validation

Used File → Publish Module, selected MNBL.ProceduralWorld, Client packing and Compile Shaders, and chose `C:/Users/davgo/m2/publish/out/`. Waited until progress reached 100% and pack0.tpac was complete, then exited normally and declined saving the blank scene. No process was killed. The compile report lists zero mesh shader variants; the task authored resource materials, and no in-game rendering claim is made.
The original `check_magic_packs.py` was run read-only with output `magic-packs-check.json`: all 24 texture checks pass, while its eight material-name checks fail. In each material record the next byte after the length-prefixed name is 0x62, which its greedy `[a-z_]+` regex appends as a “b”, making the subsequent length comparison fail. `check_packs_length_prefixed.py` replaces only material-name extraction in memory: **32 records pass, zero problems** (`magic-packs-length-prefixed-check.json`). `verify_material_records.py` additionally verifies all five flags and ordered diffuse/normal/specular GUID links (`material-record-verification.json`). Repository scripts were not modified.
Screenshots: `shots/import-air-d-bc7.jpg`, `shots/import-air-n-bc5.jpg`, `shots/import-air-s-dxt1.jpg`, `shots/material-air-finished.jpg`, and `shots/publish-complete.jpg`. The editor did not show a separate success dialog; the completion image shows 100% and the exported pack supplies completion evidence.

## Restoration

`module-after.json` equals `module-before.json`: 132 files, zero file differences and zero directory differences. SubModule.xml is byte-identical. Session-added files were retained outside the install, and new empty directories removed. No original in-scope module file changed. No direct operation modified or removed `SceneObj/mnbl_world_*`; those directories remained excluded from hashing/restoration. Publishing itself exported existing scenes as part of its whole-module output. No excluded-scene file had a session-time modification timestamp in the post-session read-only inspection.
Temporary Bannerlord.Harmony hashes matched the staging manifest before it was moved out of the install; it is absent again. The original Workshop copy is untouched. LauncherData.xml was backed up before module selection and restored byte-for-byte. Engine `safely_exited` is 1 before and after, so no crash repair was needed.
Outside-module path/size/timestamp comparison found only these rewritten files; no vanilla files were deleted:

- `bin/Win64_Shipping_wEditor/imgui.ini` — 85 bytes.
- `bin/Win64_Shipping_wEditor/steam_appid.txt` — 6 bytes.
These are retained as instructed; outside comparison is metadata-based, not an assertion that every game file was content-hashed. See `outside-changes.json` and `restore-verification.json`.

Both native DLL SHA-256 hashes are unchanged:

- TaleWorlds.Native.dll: `2A5E0E0B15513EBB7052D747A621B82C50EE834553EBA283B33BBBC33766E12A` before and after.
- TaleWorlds.MountAndBlade.dll: `19387F31557FF840D14F378F6BBDF1D58FCFF406AB9FF2294DFBB3E49A50B87E` before and after.

No repository content was edited, no packs were integrated into git, and no in-game visual test was requested or performed. Claude can integrate the exported pack and editor database.

Startup research: [official FAQ](https://moddocs.bannerlord.com/asset-management/faq/), [asset overrides](https://moddocs.bannerlord.com/asset-management/asset-types/overriding_assets/), [material editor](https://moddocs.bannerlord.com/editor/resource-editors/material_editor/).

RELEASE sent to Claude’s Maps epic 2 desktop session as Astra after restoration and report completion. See coordination.json and shots/release-sent.jpg.
