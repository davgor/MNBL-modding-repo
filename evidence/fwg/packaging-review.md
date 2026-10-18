# MMR-57 independent review and fixes

One independent review of the MMR-57 working tree, after the first live clean-install sequence. The reviewer was an agent with read-only access. It did the following without editing files, building, launching the game or touching the installation:
- read the release tooling changes, the local package script, the surface template derivation, the clean-install probe, orchestrator and summarizer, and the path test;
- ran the release tooling's unit tests and registry check;
- re-derived the template in memory from the installed official scene;
- checked the native executables' imports and the embedded resources.

It reported 1 blocker, 9 major and about 20 minor findings. All were fixed in one batch, or are disclosed below as decisions for the owner. One confirming sequence followed.

## Before the review

The first live sequence found the package's real gap:
- **Install:** generation from the package failed. `SurfaceTemplate/scene.xscene` was missing, because the development install had it from `tools/Prepare-RuntimeSurfaceSlot.ps1`, outside the deploy, so the development paths had masked a missing content dependency.
- **Why it could not simply be shipped:** `tools/scenebin/build_slot.py` records that the template's `scene.xscene` and `atmosphere.xml` come from the installed official battle scene. They may not be shipped.
- **Fixed:**
  - `World/SurfaceTemplateSource.cs` derives them on the player's machine from the installed `SandBoxCore/SceneObj/battle_terrain_001` into the per-user store;
  - `tools/scenebin/make_surface_template.py` writes the recipe of this module's values and the expected hashes, and proves the derivation byte for byte;
  - the package carries this module's own `terrain.bin` and `navmesh.bin` and the recipe;
  - `flora.bin` (the 12-byte empty flora header) is written by the module, because the package audit found it byte-identical to many official empty flora files.
- **Boot without the module:** it reached the main menu.
- **Read-only:** the phase stalled at start-up. The review traced that to the denial itself (finding 1).

## Blocker

1. **The orchestrator was not safe on the shared installation.** It did not hold the battle-scale lock, it skipped the restore on an earlier failure, it never timed out waiting for a game, and it moved folders across volumes. Fixed in `tools/Test-CleanInstall.ps1`:
   - it holds `build/battle-scale/native-test.lock` for the whole sequence;
   - every swap is a same-volume rename (`<game drive>\MNBL-mmr57-aside`, the store beside itself);
   - each undo step runs on its own;
   - waits for a game time out after 30 minutes;
   - `-Restore` recovers an interrupted sequence;
   - the probe wait holds one process handle and checks `HasExited`. In the first sequence an exited process stayed listed while handles to it stayed open.

## Major

1. **The read-only denial was wrong.** icacls `W` also denies SYNCHRONIZE and READ_CONTROL, which breaks reads. That fits the first sequence's start-up stall right after the module's descriptor was read. Fixed:
   - the denial is creation, appending, attribute writes and deletion (`WD,AD,WEA,WA,D,DC`);
   - the orchestrator checks, as the user, that the descriptor and the assembly still read, the folder still lists, and file and scene-folder creation fail, before trusting the test.
2. **A read-only hang was dropped by the summary.** Fixed: a stopped or stalled run, and an invalid denial, are findings.
3. **A read-only installation cannot work by design.** Generated scenes must be installed in the module's `SceneObj` folder, the only place the engine reads scenes from. Disclosed as a limit of the ticket's read-only requirement (`docs/packaging.md`).
4. **A missing listed file passed.** Fixed: `mods.py` refuses a package that lacks any listed file or pattern, before it writes anything. `tools/Package.ps1` also checks the staged template files against the recipe.
5. **The client profile was not clean, and a cached template could hide a package gap.** Fixed:
   - the sequence moves the development store aside, so the package starts from an empty store;
   - `SurfaceTemplateSource` checks the packaged files on every call and records whether it derived the template in this process;
   - the summary requires that it did.
6. **The native executables need a runtime the game does not ship.** `worldbridge.exe` and `fwg.exe` import VCRUNTIME140_1. Disclosed:
   - the manifest's `runtimeRequirements` names the Visual C++ 2015-2022 x64 redistributable;
   - `tools/Package.ps1` records every import (`dumpbin /dependents`);
   - the summary checks they are covered by Windows and that runtime.

   Building with a static runtime would change both pinned binaries; that is left as an owner decision.
7. **The assembly embeds copied official text.** `worldgen/templates/cultures/*.json` carry 1,051 strings with their localisation ids copied from the game's culture definitions, 7 of them lore paragraphs. They also feed world compilation, so changing them changes every world's content id. Disclosed as an open decision for the owner: the manifest's `contentNotes`, the summary's findings and `docs/packaging.md`.
8. **"Assets resolve" counted only lines naming this module.** Fixed: every missing-line kind in the install and reload runs' engine logs is compared with the development install's own campaign run (35468), and a kind it did not have fails.
9. **c3 coverage.** Fixed:
   - the sequence also boots with the module present but not enabled;
   - it hashes every file of the world the package made (bundle, package, world save, surface template) before and after;
   - the reload loads that world's save after a reinstall with no generated scene left.

   Not covered, disclosed: loading a vanilla save, and starting a vanilla campaign without the module.

## Minor

Fixed:
- `SurfaceTemplateSource` finds the official scene through the game's module lookup (Workshop installs) and sweeps leftover staging folders;
- `mods.py` refuses before writing, and its name check also covers MCM, ButterLib, UIExtenderEx, Newtonsoft and Steamworks files and `.brf`; the test isolates it;
- `tools/Package.ps1` scans the whole installation except this repository's modules, and refuses extra or missing embedded resources;
- the summary's vacuous comparisons are guarded;
- the orchestrator refuses a launch without a process id;
- the dialog watcher is stopped with its game.

Disclosed rather than changed:
- a template failure reaches the player as a generation failure;
- a read-only module folder reaches the player as the generic "The generated world could not be read" (`BadSource`, offered as retryable), with the denied path as its detail;
- there is no C# unit test of the derivation (the live sequence exercises it);
- the path test runs the executables from PowerShell, not through the module's own argument quoting;
- other modules sit in the installation's Modules folder during the test, excluded from the launch list;
- the launcher, Mark-of-the-Web unblocking and the editor's absence are not exercised.

## Confirming sequence

`tools/Test-CleanInstall.ps1`, run detached, with the battle-scale lock held throughout. It took three runs to get one valid sequence:

1. **Stopped in its record.** Writing the raw record hung on the engine log tail. `ConvertTo-Json` recursed into the extended properties PowerShell 5.1 attaches to `Get-Content` lines. Fixed: the lines are cast to strings. While the hung run still held the lock, a second start waited its 30 minutes for it and gave up without touching anything, as designed.
2. **Invalid read-only phase.** Install, disable, remove and reload passed, but the read-only phase was invalid. The access check recorded reads failing under the denial, and the game stopped right after it listed the module's descriptor, before the probe ran. Cause: icacls adds SYNCHRONIZE to every deny entry, even one of listed rights (stored as `Write, Delete, DeleteSubdirectoriesAndFiles, Synchronize`), and a denied SYNCHRONIZE fails every open. The same cause explains the first sequence's read-only stall, so major finding 1's diagnosis was only half right. Fixed:
   - the denial is set through .NET `FileSystemAccessRule`, which leaves SYNCHRONIZE out (checked on a scratch folder first);
   - the read-only probe runs only when the access check proves that reads work and creation fails;
   - a run that leaves no result is recorded rather than aborting the sequence;
   - every run's engine log is kept in `build/mmr57/engine-logs`.
3. **Valid.** Every phase ran. The undo put back the development module, the development store and the folder's permissions.

The summary first gave 25/27, and both failures were the summary's own:
- **The baseline log was gone.** The development install's campaign run 35468, the missing-line baseline, had already been rotated out of the engine's log folder (it keeps a few logs only), so the comparison had nothing to compare. A fresh baseline was run: the same `clean_install` install phase on the development install (43464: generated the same scene in 38.4 s, the map after 404.8 s, 358 settlements, 0 missing lines of this module). The summary now reads kept logs first and defaults to that baseline.
- **The disabled boot was flagged for the engine's descriptor scan.** The engine logs `LoadWithFullPath … MNBL.ProceduralWorld/SubModule.xml` for every module in the folder, enabled or not (it does the same for BirthAndDeath, FastMode, Multiplayer and every Workshop module). An enabled module adds `Loading assembly`, its GUI resources and its screens, and the disabled boot had none of those. The summary now leaves the scan out and requires no other line of this module. The orchestrator's record does the same for later runs.

Result: 27/27 checks (`evidence/release/clean-install.json`, results in `docs/packaging.md`). The read-only outcome, the code-page limit and the copied culture text are findings.

The confirming sequence's own engine logs were copied into `build/mmr57/engine-logs` by hand right after it finished; the orchestrator keeps them itself only from now on. Its raw record is `build/mmr57/clean-install-raw.json`; the earlier runs' raw records sit beside it (`clean-install-raw-interrupted.json`, `clean-install-raw-sync-denial.json`).
