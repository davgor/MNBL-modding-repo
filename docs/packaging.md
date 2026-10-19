# Packaging Procedural World and the clean installed build (MMR-57, 2026-10-18)

MMR-57 packages the Procedural World module for local installation through the per-mod release pipeline (`docs/releases.md`), and tests the package alone in the game: installed in place of the development module, removed, booted without, reinstalled, and installed read-only.

| | |
|---|---|
| Release staging | `tools/ci/CiBuild.ps1` `Copy-MnblProceduralWorldStage`: the one staging step that both the release build (`tools/ci/Build-ProceduralWorld.ps1`) and the local package use |
| Packer | `tools/ci/mods.py package`: the zip, its `.sha256`, release notes, and `<id>-<version>.manifest.json`. A mod's `package` block in `config/mods.json` lists the only files a package may hold and the provenance of each; no file may carry an official game, DLC or dependency name |
| Local package | `tools/Package.ps1`: built from this PC's verified build (`tools/Build.ps1`), with two audits the CI cannot run: no file is byte-identical to an installed official file, and the module embeds exactly the repository's worldgen files. Writes `artifacts/release/` (git-ignored) and `evidence/release/package-manifest.json` |
| Paths | `tools/Test-PackagePaths.ps1`: the packaged `worldbridge.exe` and `fwg.exe` run a whole generation under paths with spaces and non-ASCII letters (`build/mmr57/paths.json`) |
| Clean install | `tools/Test-CleanInstall.ps1` with the `clean_install` probe (`Automation/CleanInstallProbe.cs`); verdict by `tools/summarize_clean_install.py` into `evidence/release/clean-install.json` |
| Review | `evidence/fwg/packaging-review.md` |

## What the package holds

Extract the zip into `<Bannerlord install>/Modules/`, so that `Modules/MNBL.ProceduralWorld/SubModule.xml` exists. Enable the module in the launcher, with Harmony (Steam Workshop 2859188632) and War Sails.

| File | Provenance |
|---|---|
| `SubModule.xml`, `GUI/Prefabs/*.xml` | repository files |
| `bin/Win64_Shipping_Client/MNBL.ProceduralWorld.dll` | built from `src/MNBL.ProceduralWorld`. It embeds the worldgen policies, profiles, contracts, calibration and culture templates it reads (36 resources, each checked against the repository) |
| `bin/Win64_Shipping_Client/worldbridge.exe` | built from `src/WorldBridge` |
| `bin/Win64_Shipping_Client/fwg.exe` | the FWGC++ producer pinned by `tools.lock.json` |
| `bin/Win64_Shipping_Client/*.sha256` | the pins the module checks before running either executable |
| `SurfaceTemplate/terrain.bin`, `navmesh.bin` | this module's own surface template binaries (`tools/scenebin/build_slot.py`'s writers) |
| `SurfaceTemplate/template-recipe.json` | how to derive the rest of the template on the player's machine (below) |

**Not redistributed:**
- the game, its official modules and War Sails;
- Harmony;
- development caches, research outputs and user worlds;
- generated scenes;
- the two official-derived template files.

## The surface template is derived on the player's machine

The surface encoder starts every generated scene from a five-file template. Two of its files, `terrain.bin` and `navmesh.bin`, are this module's own and ship. `flora.bin` is the empty flora file, a 12-byte header byte-identical to many official empty flora files, so the module writes it rather than ship it. The other two come from the installed official battle scene `SandBoxCore/SceneObj/battle_terrain_001` (as `tools/scenebin/build_slot.py` says, "local test fixtures, not package content"):
- `scene.xscene` is that scene's text with this module's values substituted;
- `atmosphere.xml` is a verbatim copy.

A package may not carry them, and the development install had masked their absence. The first clean install failed: "Could not find a part of the path `…\Modules\MNBL.ProceduralWorld\SurfaceTemplate\scene.xscene`".

**The fix:**
- `World/SurfaceTemplateSource.cs` derives them from the player's own installation into the per-user store (`<store>/surface-template/<recipe>/`), with the values `SurfaceTemplate/template-recipe.json` records:
  - the scene's name;
  - the terrain's node grid and heights;
  - the flora and physics bounds;
  - the terrain nodes block.
- Every file must have the recipe's SHA-256, so the template, and with it every scene name, is the same on every installation of this game version. A different official scene refuses with its reason.
- `tools/scenebin/make_surface_template.py` writes the recipe and proves it: deriving from the installed official scene reproduces the template byte for byte.

## Results (2026-10-18)

The tested package is `MNBL.ProceduralWorld-v0.0.1.zip`: 11 files, SHA-256 `f70b44c0879d4389fced8c16e61ea5fb331995ae862acdd8bfe1ddf8d86b314f`, module assembly `a61d6dc7…`. `evidence/release/clean-install.json` holds the verdict, and `evidence/release/package-manifest-f70b44c0.json` holds its manifest.

**Rebuilt after these results (2026-10-18).** The developer "FWGC: …" menu entries and their probe were removed, so the package was rebuilt:
- **New package:** SHA-256 `b06d222abd87d4f3606281785ad4f7f726bbbb647b3548a07e1d280b48fa8f9c`, module assembly `c2200e51…`, manifest `evidence/release/package-manifest.json`.
- **Unchanged:** `worldbridge.exe` and `fwg.exe`. The package audits passed again.
- **Not yet tested:** the new package has not been through the clean-install sequence. The previous package is kept locally under `artifacts/release/previous-f70b44c0/`.

**The final package (2026-10-19).** `e20f26676ea91410f82a8176368d9af602507324bc7b0c645e5c08bae069e0a5`, module assembly `3070d8bc…`, `worldbridge.exe` `2bfdbb9d…`, `fwg.exe` unchanged; the package the final one-shot run passed on (`docs/one-shot-acceptance.md`). The whole clean-install sequence ran on it again: install 31240, reload 50880 and read-only 25652, with the boots in between, 27 of 27 checks once the package path test had run on the same archive (`evidence/release/clean-install.json`). The read-only and code-page findings below still hold. Earlier packages are kept locally under `artifacts/release/previous-*/`.
- **Raw outputs archived:** the raw in-game outputs of the runs below were moved out of the repository in the evidence cleanup. The summaries stay, but `summarize_clean_install.py` can no longer re-derive them. The confirming sequence holds the battle-scale lock for its whole length. It moves the development module and the development store aside, and puts both back at the end.

| Step | Run | Outcome |
|---|---|---|
| Install | probe 9276 | The package extracted alone matches the manifest (11 of 11 files, no development receipt). The game loaded the module from it, with War Sails and Harmony. The surface template was derived in this run from the installed official scene and matches its recipe. New Campaign generated seed 42 in 37.3 s, as the same scene the development install makes (`mnbl_world_f596b58cdcfc1879_171df43a3a14`). Start Campaign reached the map after 427.7 s, most of it the first build of the world's navigation caches, with 358 settlements, and the save was written. The engine log has no missing asset of this module or its world (12 stock lines). Every kind of missing line in the install and reload runs' engine logs also appears in the development install's own run of the same probe (43464: the same scene in 38.4 s, the map after 404.8 s, 358 settlements) |
| Present, not enabled | boot 40572 | Main menu in 30.1 s. Beyond the start-up scan that reads every module's descriptor, enabled or not, the engine log has no line of this module: no assembly load and no GUI resources |
| Removed | boot 31356 | Main menu in 30.1 s |
| Kept | snapshots | Through disabling and removal: the world the package made is unchanged byte for byte (bundle, package, world save and surface template: 906 files), every save from before is unchanged (103), and the official modules' descriptors and the engine binaries are unchanged |
| Reinstalled | probe 40996 | With no generated scene left in the module folder, the save from the install loaded to the map. The scene was re-encoded and installed again |
| Read-only | probe 47592 | Reads and listing work; creating files and folders, modifying and deleting are refused. The game starts and the module loads. Generation fails after 31.2 s, and the save's load is refused (`not-rebuildable`); both say "Access to the path `…\Modules\MNBL.ProceduralWorld\SceneObj\mnbl_world_…` is denied". No hang, no vanilla campaign, and no partial scene left |
| Paths | `tools/Test-PackagePaths.ps1` | The packaged executables generate seed 42 under a path with spaces, `&` and accented letters (code page 1252) and publish the expected world (`f596b58c…`). Under a path with CJK letters, `worldbridge.exe` refuses cleanly (`unrepresentable_path`) |
| Audits | `tools/Package.ps1` | No packaged file is byte-identical to any of 30,170 installed game files. The assembly embeds exactly the repository's 36 worldgen files. The surface template ships only this module's two binaries and the recipe |

The read-only denial is set through .NET (`FileSystemAccessRule`), not `icacls /deny`. icacls adds SYNCHRONIZE to every deny entry, even one of listed rights, and a denied SYNCHRONIZE fails every open. The game then stops right after it lists the module's descriptor. That happened in two earlier sequences, so the orchestrator now runs the read-only probe only after checking, as the user, that reads work and creation fails.

## Limits and open decisions

- **A read-only installation is not supported.** Generated scenes must be installed in the module's own `SceneObj` folder, the only place the engine reads scenes from. Both a new world and every reload that re-encodes a scene write there. Only the surface template moved to the per-user store. A Steam installation is writable by its user, so this matters only for installations made read-only on purpose. The failure is clean (Results), but the player sees the generic "The generated world could not be read" (category `BadSource`) with the denied path as its detail, not a message that names the read-only folder.
- **Windows user names outside the system ANSI code page cannot generate a world.** Jobs run in the per-user store under `%LOCALAPPDATA%`, and `worldbridge.exe` refuses a path the ANSI code page cannot spell (`unrepresentable_path`), because `fwg.exe` takes narrow arguments. Accented letters of the code page (1252 here) work. The refusal is clean, but it stops generation.
- **The Visual C++ 2015-2022 x64 runtime is required.** `worldbridge.exe` and `fwg.exe` import `MSVCP140`, `VCRUNTIME140` and `VCRUNTIME140_1`; the game ships the first two only. The manifest states the requirement (`runtimeRequirements`). Building both with the static runtime would remove it, but would change both pinned binaries (owner decision).
- **Open: copied official text in the assembly.** The module embeds `worldgen/templates/cultures/*.json`. They carry 1,051 name and lore strings, with their localisation ids, copied from the game's culture definitions (7 are lore paragraphs). No game file is redistributed, but this text is the game's. It also feeds world compilation, so resolving it from the player's installation instead would change every world's content id. The manifest records it (`contentNotes`), and the owner decides.
- **Harmony comes from the Steam Workshop**, installed separately.
- **What the test does not exercise:**
  - The launcher. Every run starts the game directly with a module list (official modules, Harmony and this one).
  - Other modules present in the installation's `Modules` folder, which are excluded from that list.
  - Windows' Mark-of-the-Web block on a downloaded zip (the zip is extracted locally).
  - The editor's absence. It is installed on the test machine; nothing in the package refers to it.
  - A vanilla save or campaign without the module. The boots without the module reach the main menu only.
- **The release build is not this package.** CI builds against reference assemblies (`docs/releases.md`). This package is built from this PC's verified build, from a working tree with uncommitted changes (`ownedFilesUncommitted` in the manifest). A release from a commit through the workflow is a separate action.
