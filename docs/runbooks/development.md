# Developing MNBL Procedural World

How to set up, build, test, deploy and package the Procedural World module from this repository. Every command below exists in `tools/` as of 2026-10-18. Where a script assumes this PC's paths, that is said. The player side is in [player-worlds.md](player-worlds.md). The per-mod release process is in [../releases.md](../releases.md).

**What the pieces are:**
- **`MNBL.ProceduralWorld.dll`** is the Bannerlord module (C# 7.3, .NET Framework 4.8.1). It adds New Procedural Campaign, runs the generation job, builds the campaign and binds saves to worlds.
- **`fwg.exe`** is the FWGC++ world producer, built from the sibling FWGC++ repository at a pinned commit. This repository never modifies FWGC++.
- **`worldbridge.exe`** (C++20, `src/WorldBridge`) supervises the producer, reads its split world and compiles terrain, campaign records, travel graph and the world's data bundle.

Players generate worlds inside the game. No per-world editor build, module publish or restart is involved. The Modding Kit is a research and authoring aid only. No build, test, deploy or player step needs it. Only the installation inventory, `tools/Doctor.ps1`, requires it, and that inventory is optional (below).

## Prerequisites

| | Version | Checked by |
|---|---|---|
| Windows | 11 x64 | |
| Bannerlord, Steam | v1.4.8.119303 | `config/runtime-api-lock.json` and `tools.lock.json`, by every build and deploy |
| War Sails (NavalDLC) | v1.2.8.119303 | as above |
| Harmony | Bannerlord.Harmony v2.4.2, Steam Workshop 2859188632 (never copied into the mod) | the API lock (scope `workshop`). `tools/Doctor.ps1` does not check it |
| Visual Studio 2022 Build Tools | MSBuild; MSVC 19.44.35228.0 (toolset 14.44.35207); Windows SDK 10.0.26100.0; the bundled CMake (3.31) | paths hard-coded in `tools/Build.ps1` and `tools/Build-Fwg.ps1`. The offline harnesses use its Roslyn `csc.exe` |
| .NET Framework 4.8.1 targeting pack | reference assemblies under `Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8.1` | the API lock |
| Python | 3.x (3.12.10 is what this PC uses; no minimum is established) | |
| FWGC++ | a clean checkout at commit `ec5d1c10f5ae4c32fdcef092d23ad7d51e2a30de` (`https://github.com/davgor/FWGC-.git`) | `tests/check_producer_lock.py` |
| Modding Kit | Steam app 1393600; War Sails Modding Kit 4456490 | Optional for development. `tools/Doctor.ps1` fails without app 1393600 and its `bin/Win64_Shipping_wEditor/Bannerlord.exe`; `-RequireNavalEditor` also requires 4456490 |
| `gh` CLI, `dumpbin` | | releases and the producer upload; `tools/Package.ps1`'s import audit |

**Paths are this PC's by default.**
- Every script that touches the game takes `-GameRoot`. The default is `D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord`.
- The csproj has `GameRoot` and `WorkshopRoot` MSBuild properties with the same defaults. `tools/Build.ps1` passes only `GameRoot`. On another Steam library, also change the csproj's `WorkshopRoot` default, because Build.ps1 cannot pass it.
- On another installation you must also edit `workshopRoot` and `frameworkRoot` in `config/runtime-api-lock.json`. It is an embedded resource, so that changes the build's inputs.
- `tools/Start-OwnedProbe.ps1` has the game root hard-coded.
- `config/local.paths.example.json` is not read by any script.
- The FWGC++ location defaults to this PC's sibling checkout. Pass `-Source` (`Build-Fwg.ps1`) or `-ProducerRoot` (`Doctor.ps1`).

## Set up and build

```powershell
git -C <path to FWGC++> checkout ec5d1c10f5ae4c32fdcef092d23ad7d51e2a30de
./tools/Build-Fwg.ps1 -Source <path to FWGC++>   # build/producer/apps/Release/fwg.exe, then its CTest
python tests/check_producer_lock.py --source <path to FWGC++>   # the built fwg.exe, the lock and the checkout agree with the pin
./tools/Test-ApiPins.ps1                # "PASS: exact runtime API pins verified (31 files)"
./tools/Build.ps1                       # the module and worldbridge
# optional, needs the Modding Kit: the installation inventory
./tools/Doctor.ps1 -Output <scratch path>/environment.json
```

**Which commands rewrite tracked evidence:**
- `Doctor.ps1` without `-Output` rewrites `evidence/baseline/environment.json`, even when it fails;
- `Build.ps1` rewrites `evidence/runtime/build-api-inputs.json`;
- `Package.ps1` rewrites `evidence/release/package-manifest.json`.

The acceptance index cites all three by hash, so re-run `python tools/build_acceptance_index.py` after an intended change, and keep `-Output` on exploratory Doctor runs.

**The producer:**
- `Build-Fwg.ps1` does not check out the pinned commit or check the hash. `check_producer_lock.py` does.
- The deploy, the package and the release build all refuse any `fwg.exe` whose SHA-256 is not `564c2629b5b92d49e52cd466a5a1aaa9339380464207ddcae669dd487d2f695e`.
- Whether a fresh build reproduces those bytes has not been shown. If yours differs, use the published binary, as the release build does:

  ```powershell
  gh release download fwg-producer/ec5d1c10f5ae --pattern fwg.exe --dir build/producer/apps/Release
  ```

  Then run `check_producer_lock.py` again.
- **`Build.ps1`:**
  1. Verifies every pinned game, DLC, framework and Harmony assembly by hash, and checks the csproj references against the lock (a hook re-checks the resolved references before compiling).
  2. Rebuilds `build/module/MNBL.ProceduralWorld.dll` (Release), then `build/worldbridge/Release/worldbridge.exe` with a clean CMake build. It runs no CTest; that is in `tools/ci/Build-ProceduralWorld.ps1` and `tools/Test-ProducerProcess.ps1`.
  3. Records every source and output hash in `evidence/runtime/build-api-inputs.json` (`status: built`).

  `Deploy-Dev.ps1` refuses a build whose recorded inputs (sources and pins) have changed since. `Package.ps1` checks only that the outputs are the recorded ones.
- **The release build** runs on GitHub against reference assemblies: `tools/ci/Build-ProceduralWorld.ps1 -References <dir> -Stage <dir> -Work <dir>`, after `python tools/ci/mods.py references --out build/ci/references`. See [../releases.md](../releases.md).

## Test without the game

**Release tooling and lock checks:**
```powershell
python -m unittest discover -s tests/ci
python tools/ci/mods.py check --base origin/main
python tests/check_producer_lock.py --source <path to FWGC++>
```

**C# harnesses.** Each compiles the named sources with `tests/**.cs` and writes `evidence/...` (common options `-Evidence`, `-Log`).
- No world needed: `Test-Activation`, `Test-AutorunCleanup`, `Test-EarlyBinding`, `Test-GenerationJob`, `Test-SaveBinding`, `Test-WorldLoader`, `Test-Heightfield`, `Test-GeneratedCampaignDataset`, `Test-Doctor`, `Test-Deployment` (deploy refusals on disposable copies).
- One world (`-Bundle <bundle dir>`): `Test-CampaignRegistry`, `Test-Characters`, `Test-CompiledWorld`, `Test-Factions`, `Test-GraphValidation`, `Test-NavalContent`, `Test-PeriodicCoordinates`, `Test-Settlements`, `Test-SpawnPolicy`, `Test-WaterClimate`, `Test-WorldDataBundle`, `Test-Compatibility`.
- Two worlds (`-BundleA`, `-BundleB`): `Test-EncounterResolver`, `Test-Playability`, `Test-SettlementPlacement`, `Test-WorldNavigation`, `Test-WorldPreview`, `Test-WorldSetup`, `Test-WorldSurface`. `Test-Recovery` takes `-BundleA`.

**Producer and bridge** (they need the built `fwg.exe` and `worldbridge.exe`): `Test-GenerationProbe`, `Test-ProducerProcess`, `Test-TravelGate`, `Test-SourceReader`, `Test-WorldBridgeSurface`, `Test-PackagePaths -Zip <zip>`.
- `Test-GenerationIdentity` rewrites the embedded `worldgen/contracts/producer-seed-width.json`, which invalidates the build receipt. Rebuild after it.

**Python:**
- `tests/test_source_reader.py` (needs `worldbridge.exe`);
- `tests/test_terrain_bin.py` and `tests/test_navmesh_bin.py`, whose game-dependent cases read `MNBL_GAME_DIR` / `BANNERLORD_ROOT` and skip without them;
- `tests/WorldBridgeSurfaceTests.py`, `tools/test_bundle_parity.py`, `tools/check_terrain_seams.py`, `tests/check_campaign_fixture.py`;
- `tests/Runtime/*.py`, the scenario checks behind `Test-CampaignSoak`, `Test-LandMissions` and `Test-NavalMissions`.

**The test worlds are not in git.** The bundles the harnesses take (world A `f596b58c…`, seed 42; world B `f63c6841…`, seed 73) live under the ignored `build/`. Make them either way:
- generate seed 42 and seed 73 in the game (below); their bundles appear in `%LOCALAPPDATA%\MNBL.ProceduralWorld\bundles\<worldContentId>`;
- run `worldbridge --bundle` over a producer split, with the arguments in [../world-bundle.md](../world-bundle.md).

The same seed, game, War Sails and policies give the same content id either way.

## Deploy to the game

```powershell
./tools/Deploy-Dev.ps1 -DryRun     # prints the receipt, writes nothing
./tools/Deploy-Dev.ps1
```

**What it refuses:**
- while Bannerlord is running;
- on any API pin or build-receipt mismatch, or official module versions other than the lock's;
- with a producer other than the pin;
- for a redirected path or a destination outside `Modules/MNBL.ProceduralWorld`.

**What it copies**, each file hash-verified:
- `SubModule.xml`;
- `bin/Win64_Shipping_Client/{MNBL.ProceduralWorld.dll, fwg.exe, worldbridge.exe}`;
- `SurfaceTemplate/{terrain.bin, navmesh.bin, template-recipe.json}`;
- `GUI/**`. Stale GUI files are removed.

It then writes the `fwg.sha256` and `worldbridge.sha256` pins the module checks before running either, and the receipt `deployment.json`. It never deploys Harmony or anything into `SceneObj`.

**The surface template's official-derived files are made at run time.** The module derives them per user from the installed `SandBoxCore/SceneObj/battle_terrain_001` into `%LOCALAPPDATA%\MNBL.ProceduralWorld\surface-template\` (`World/SurfaceTemplateSource.cs`; `docs/packaging.md`). `tools/scenebin/make_surface_template.py` rewrites the recipe and proves the derivation. The older `tools/Prepare-RuntimeSurfaceSlot.ps1` is superseded by this, and no doc or script uses it.

**Rolling back a deployed build.** There is no undeploy script, and the deploy keeps no copy of the previous module. Either:
- check out the earlier commit, then `./tools/Build.ps1` and `./tools/Deploy-Dev.ps1`;
- or remove `Modules/MNBL.ProceduralWorld` and extract that version's package zip.

Leave the store and the saves alone. Under the save rules (`docs/save-compatibility.md`), a campaign loads on another module build only when its world rebuilds byte for byte; otherwise it is refused `not-rebuildable`. The first start or load per build rebuilds the navigation caches (about 6.5 minutes). Rolling back to an older version has not been tested; installing, removing and reinstalling the same package has (`docs/packaging.md`).

## Run it in the game

**A shared installation.** This PC's game installation is shared with other sessions (the Battle Scale work). Before every deploy or launch:
- check that no `Bannerlord`, `Bannerlord.Native` or `Watchdog` process is running;
- check that `build/battle-scale/native-test.lock` is free: open it with exclusive sharing; if that fails, someone holds it.

`Test-CleanInstall.ps1` holds the lock for its whole sequence. `Start-OwnedProbe.ps1` and `Invoke-InGameProbe.ps1` only refuse while a game process exists; they do not take the lock. Never stop a game process you did not start.

**By hand.** Start the game with the module enabled. Its only main-menu entry is New Procedural Campaign: the developer "FWGC: …" diagnostics were removed on 2026-10-18. The automated probes below cover what they did.

**Automated probes.** A probe is a request file the module claims at start-up (`Automation/AutorunHost.cs`). It runs a named scenario and writes its result.
```powershell
./tools/Start-OwnedProbe.ps1 -Probe first_test_profile -ProbeTimeoutSeconds 3600 -ArgsJson '{}'
# after the game exits (it reads build/owned-probe.json, which Start-OwnedProbe wrote):
./tools/Collect-OwnedProbe.ps1
```

**`Start-OwnedProbe.ps1`:**
1. Records the hashes of the existing saves.
2. Starts the game with `Bannerlord.Harmony, Native, SandBoxCore, CustomBattle, Sandbox, StoryMode, NavalDLC, MNBL.ProceduralWorld`.
3. Starts a dialog watcher and writes `build/owned-probe.json`.

`Collect-OwnedProbe.ps1` copies the result, log and captures into `evidence/runtime/in-game/<probe>-<pid>*`. That folder is git-ignored, and the summaries in `evidence/` are what is kept.

**Probes** (`Automation/AutorunProbes.cs`):
- Player flow: `world_setup`, `new_campaign_flow`, `generation_job`, `world_activation`, `world_recovery`.
- Saves and loading: `save_binding`, `two_world_matrix`, `compat_matrix`, `generated_save_load`, `save_recovery`.
- Gameplay: `campaign_soak`, `land_missions`, `naval_missions`.
- Measurement and packaging: `first_test_profile`, `clean_install`.
- World and runtime: `world_surface`, `world_navigation`, `runtime_surface`, `scene_query`, `scene_sequence`, `native_render`, `capabilities`.
- Diagnostics and baselines: `sandbox_boot`, `campaign_catalog`, `generated_campaign`, `baseline_scenario`, `baseline_load`, `baseline_combat`, `surface`, `nav`, `noop`.
- Seeded: `generate:<seed>`, `render:<seed>`, `ray:<seed>`, `grid:<seed>`.

Each ticket's doc names its probe, arguments, orchestrator (`tools/Test-*.ps1`) and summarizer (`tools/summarize_*.py`).

**Two gotchas:**
- `Invoke-InGameProbe.ps1`'s default `-Modules` omits Harmony. Pass the list above, or use `Start-OwnedProbe.ps1`.
- The first campaign start or load per module build blocks the engine thread for about 6.5 minutes while the navigation caches build. That is busy, not hung.

## Package

```powershell
./tools/Build.ps1
./tools/Package.ps1                                   # artifacts/release/ (git-ignored) and evidence/release/package-manifest.json
./tools/Test-PackagePaths.ps1 -Zip artifacts/release/MNBL.ProceduralWorld-v0.0.1.zip
./tools/Test-CleanInstall.ps1 -Zip artifacts/release/MNBL.ProceduralWorld-v0.0.1.zip -Manifest artifacts/release/package-manifest.json
python tools/summarize_clean_install.py               # evidence/release/clean-install.json
```

**`Package.ps1`:**
- Stages exactly the files `config/mods.json`'s `package` block allows, then runs `mods.py package`.
- Audits that no file is byte-identical to an installed game file, that the embedded resources equal the repository's, that the surface template ships only this module's files, and records the native imports.

**`Test-CleanInstall.ps1`:**
- Swaps the package in for the development module and the store, then installs, disables, removes, reinstalls and tests a read-only folder.
- Puts everything back at the end. If it was interrupted, `-Restore` does that.

Results and limits are in [../packaging.md](../packaging.md). A local package is a test package, never release-certified. Its manifest records whether it was built from uncommitted files (`ownedFilesUncommitted`).

## Where things are

| What | Where |
|---|---|
| Per-user store | `%LOCALAPPDATA%\MNBL.ProceduralWorld\`: `bundles/`, `worlds/`, `activations/`, `saves/` (journals), `loads/`, `jobs/`, `navigation-caches/`, `surface-template/`, `previews/`, `diagnostics/`, `world-navigation/`, `world-settlements/`, `world-playability/`, `world-surfaces/`, `autorun/`, `store.lock`, `module.log` |
| Generated scenes | `<GameRoot>\Modules\MNBL.ProceduralWorld\SceneObj\mnbl_world_…\` (world A: `mnbl_world_f596b58cdcfc1879_171df43a3a14`) |
| Saves | `Documents\Mount and Blade II Bannerlord\Game Saves\` |
| Engine logs | `C:\ProgramData\Mount and Blade II Bannerlord\logs\rgl_log_<pid>.txt`. The engine keeps only a few, so copy the ones you need right after a run |
| Build outputs | `build/` (ignored): `module/`, `worldbridge/`, `producer/`, `release/` |
| Evidence summaries | `evidence/**`; one-shot index `evidence/one-shot/acceptance-index.json` (`python tools/build_acceptance_index.py`) |
| Raw run outputs | `evidence/runtime/in-game/` (git-ignored). Eight of them are inputs that the offline harnesses and the scenario files name, so keep them when clearing the folder: `baseline_combat-27112`, `campaign_catalog-10212`, `first_test_profile-35468`, `generated_campaign-21284`, `-2248`, `-4768`, `generation_job-30220`, `runtime_surface-32896` (`.json`) |
| Inspect and transfer worlds | `tools/Inspect-World.ps1` (read-only); `tools/Transfer-WorldSave.ps1 -Action export|verify|import` |

## Known tooling gaps

- The default paths are this PC's (see Prerequisites), and `config/local.paths.example.json` is unused.
- `Build-Fwg.ps1` does not pin the FWGC++ commit, and a reproducible producer build is not shown.
- `Doctor.ps1` requires the Modding Kit although nothing else does. It does not check Harmony, and `evidence/baseline/environment.json` predates its War Sails editor fields.
- `check_producer_lock.py`'s default FWGC++ location is this PC's (`localSource` in `worldgen/producer.lock.json`); pass `--source`.
- A merge into `main` publishes a release. See the warning in [../releases.md](../releases.md).
- The two-world harnesses need bundles that only exist after a generation (see Test without the game).
- `Invoke-InGameProbe.ps1` omits Harmony by default.
- There is no undeploy script.
