# First-test profile measurements and A/B seed differentiation (MMR-56, 2026-10-18)

MMR-56 measures the F03 diagnostic profile (`worldgen/profiles/one-shot.json`: recipe 3, size 129, phase 16, 3 quality attempts) through the player's own path, on one pinned build. It also shows that two seeds from that build give different worlds.

| | |
|---|---|
| Probe | `first_test_profile` (`Automation/FirstTestProfileProbe.cs`; `seedA`, `seedRejected`, `seedB`, `culture`) |
| Report | `worldgen/validation/first-test-measurements.json`, written by `tools/summarize_first_test.py <pid>`. It recomputes every check from the run's outputs, lists what stays unresolved under `findings`, and replaces user paths |
| Budget | `worldgen/policies/one-shot-budget.json`: the measured phases the setup screen states, the tested settings and the provisional limits (`--budget` merges each run once, by pid) |
| Seeds | `worldgen/fixtures/acceptance-seeds.json`: chosen, rejected, and why |
| Review | `evidence/fwg/first-test-profile-review.md` |

## What a run does

**World A (seed 42), request to play:**
- **Generate.**
  - The New Campaign setup screen opens from the main menu, the seed is typed, and Confirm is pressed through the screen's own mapping.
  - The wait is timed from Confirm to the world ready for review, with every frame's length (the job runs off the engine thread), and the job directory's size every 2 s.
- **The job record.** `GenerationCoordinator.Record` is read while the job is live. It gives:
  - each phase's time (request, source, compile, data validation, scene, runtime navigation);
  - worldbridge's receipts for `--new-request`, `--generate` and `--bundle`, each with its elapsed time and peak working set;
  - the producer's own run: `run-record.json`, which `worldbridge --generate` writes for `fwg.exe` in its job object, with its elapsed time and the job's peak memory;
  - the bundle's `compileSeconds` (reading the source and converting it) and `wallSeconds` (with writing and hashing the bundle);
  - the scene's encode time.
- **Start Campaign.**
  - The preview's Start Campaign, then character creation as a player drives it (`NewCampaignFlowProbe.CreationDriver`), until the campaign map.
  - Every frame is timed through the start, and each gap over 1 s is recorded with what was on screen. So is the time the creation screen was up, which is the player's.
  - The activation host's record gives each step's seconds:
    - object construction (`BuildWorld`);
    - the campaign start, with each navigation type's cache generated or deserialized;
    - `PostBuild`;
    - the initial save (`PublishSave`).
- **The map.** Frames for 20 s paused, 20 s at play speed (`UnstoppablePlay`), and in fast forward until 24 campaign hours pass or 60 s elapse, with campaign hours per real second. The player is ignored by other parties for two days (recorded).
- **Save and load.** A manual save (`SaveHandler.SaveAs`, timed until its world binding is published), and its load through the normal load entry (`SandBoxSaveHelper.TryLoadSave`), timed to the map with the loader's phases.

**Seed 43, the profile's next seed:** Confirm, and how the generation refuses it.

**World B (seed 73):** Confirm, to review only, with the same job record. Its activation is refused by the village binding, so it is not started.

**Throughout:**
- **Memory:** the game process's working set and private bytes, four times a second off the engine thread, kept per phase, and its peaks at the end.
- **Disk:** the store's directories and the game's saves after each step.
- **Machine:**
  - CPU, logical processors and physical memory;
  - display adapters, and the GPU memory the engine reports (which adapter rendered);
  - Windows from the registry, AC power and the power scheme;
  - the game's settings files.
- **Build:**
  - the module assembly, `worldbridge.exe`, the producer and its tools lock;
  - the embedded setup documents (profile, budget, producer lock, product policy);
  - the game and War Sails versions;
  - the managed and native engine binaries (`Bannerlord.exe`, `TaleWorlds.Native.dll`).

**A and B compared:**
- **Geography,** from each bundle:
  - the hashes of `height-q16.bin`, `heightfield.bin`, `masks.bin` and `cells.bin`;
  - the travel cells counted by class;
  - the coastline, meaning land and mountain cells with a water 4-neighbour (columns wrap), counted and hashed;
  - land components of land and mountain cells (the compiler's own count separates land by mountains, so it is higher);
  - the class grids compared cell by cell.
- **Traversability,** from each job's in-engine runtime validation (the persisted navigation report, checked to be the job's by its hash):
  - the route matrix (every route's type, endpoints, reachability and path length), hashed and summarised by type;
  - the navigation mesh's faces by terrain type.

## Run 35468 (20/20 checks, status ok)

Earlier test run 17872 (15/17) found that `StoppablePlay` holds time while the player's party stands idle. The independent review then found further gaps, all fixed before this run (`evidence/fwg/first-test-profile-review.md`).

**Machine and settings:**
- Intel Core Ultra 9 275HX (24 logical processors), 32 GB of memory, on AC power.
- NVIDIA GeForce RTX 5080 Laptop GPU and Intel Graphics. The engine reports 16,003 MB of GPU memory, so the RTX rendered.
- Windows build 26200 (25H2): the registry's product name reads "Windows 10 Home", as Windows 11 keeps it, and the game process reports the compatibility version NT 6.2.9200.
- The game's settings files are recorded in the report: `max_framerate` 200, texture quality 2, `graphics_adapter` 0.

**Build:**
- module assembly `e10a2e69…`, `worldbridge.exe` `776c1606…`;
- producer `564c2629…` (tools lock `ad813831…`);
- game v1.4.8.119303, War Sails v1.2.8.119303;
- `TaleWorlds.MountAndBlade.dll` `19387f31…`, `TaleWorlds.Native.dll` `2a5e0e0b…`, `Bannerlord.exe` `5b50b56a…`;
- embedded budget `824279bc…`.

**Generation (A, seed 42, cold): 32.7 s from Confirm to review.** A's published bundle was moved out of the store before the run, so the bundle was built anew, as for a player's new world.

| Phase | Seconds | What it contains |
|---|---|---|
| Request | 0.16 | `worldbridge --new-request` 0.08 s |
| Source | 8.56 | `worldbridge --generate` 8.55 s: the producer (`fwg.exe`) generating the source and writing its JSON, 5.66 s, peak 247 MB; worldbridge around it, 2.89 s (the request, the split, its validation), peak 51 MB |
| Compile | 5.94 | `worldbridge --bundle` 5.93 s, peak 255 MB: reading the source and converting it, 2.82 s; writing and hashing the bundle, 3.06 s |
| Data validation | 0.19 | the bundle opened and verified |
| Scene | 13.82 | the surface encoded 13.72 s, installed 0.10 s |
| Runtime navigation | 1.78 | the engine's route matrix on the world's navigation mesh |
| Preview and summary | 2.25 | after the job is ready: the world summary and the preview |

- The longest frame while generating was 0.19 s; median 16.7 ms.
- The job directory peaked at 136 MB, released after review.
- The JSON write and read cannot be split further. The producer writes its JSON as part of its 5.66 s, and the bundle's 2.82 s reads it and converts it together; neither process times them apart.

**Generation varies with the world.** Seed 73 took 55.7 s:
- source 13.53 s (producer 9.33 s, peak 270 MB);
- compile 8.76 s (read and convert 4.13 s, write 4.59 s, peak 322 MB);
- scene 26.69 s (encode 23.96 s);
- runtime navigation 3.19 s;
- preview 3.15 s; longest frame 0.20 s.

Its world has 1.75 times A's land. In test run 17872, A with its bundle already published took 35.6 s and B 56.6 s.

**First campaign start (A): 398.9 s of activation work, with the game not responding for 383 s of it.**

| Step | Seconds |
|---|---|
| Admit, VerifyData, VerifyRuntime, PromotePackage, BindIdentity | 0.53 |
| BuildWorld (object construction) | 6.03 |
| StartCampaign | 391.86 |
| — navigation caches generated: All 300.19, Default 48.39, Naval 33.38 | 382.31 |
| — character creation on screen (the probe's, a player's time) | 1.42 |
| — the rest (the campaign created, the scene loaded and registered, the map reached) | 8.14 |
| PostBuild | 0.42 |
| PublishSave (the initial save 1.26 s) | 1.48 |

- **The game freezes.** The caches are generated on the engine thread under the loading screen. One frame lasted 383.1 s, so the game drew nothing and took no input for 6.4 minutes. Windows may show it as not responding.
- **When:** the caches are keyed by the module build's hash, so this happens on the first start of every new world, and on the first start or load of any world after a module update. Later starts and loads in the same build deserialize them.
- **Wait:** Start Campaign to the map took 398.3 s. The total wait for a new world is 431.6 s (32.7 s generating plus 398.9 s starting, the creation screen excluded).

**The map** (1,018 parties at the start, 1,074 after a day; 359 settlements):

| Mode | Frames | Mean fps | Longest frame | Campaign hours per second |
|---|---|---|---|---|
| Paused, 20 s | 1,201 | 60.0 | 0.028 s | 0 |
| Play speed, 20 s | 1,188 | 59.4 | 0.129 s | 0.30 (6.0 h) |
| Fast forward, 20 s | 1,201 | 60.0 | 0.046 s | 1.20 (24.0 h) |

Every sample sits at 16.7 ms although `max_framerate` is 200. Something outside these settings caps the game at 60 fps, so these numbers show the cap held, not headroom.

**Save and load:**
- **Save:** published in 1.08 s, 3.7 MB.
- **Load:** to the map in 14.5 s, with the caches reused (installed at 6.8 s, deserialized by 8.0 s, loaded at 14.4 s). Afterwards the map ran at 60 fps (longest frame 0.072 s).

**Memory (the game process, largest per phase):**

| Phase | Working set MB | Private MB |
|---|---|---|
| Generation A | 4,100 | 5,968 |
| Campaign start | 4,970 | 7,411 |
| Map | 4,623 | 7,150 |
| Save | 4,929 | 7,365 |
| Load | 5,049 | 7,474 |
| Generation B | 5,109 | 6,960 |

- **Peaks over the whole run:** working set 5,139 MB, paged 7,475 MB.
- **Child processes:** the producer peaked at 247 MB (A) and 270 MB (B), `worldbridge --bundle` at 255 MB and 322 MB, and `worldbridge --generate` at 51 MB and 57 MB.

**Disk:**
- **Bundles:** A's is 137 MB and B's 185 MB.
- **Generating:** the job directory adds 136-184 MB while a world is reviewed, released after.
- **Starting:** a start adds 2.2 MB of package, 2.3 MB of activation record and 7 MB of navigation caches. Its initial save is 3.3 MB, and a manual save 3.7 MB.
- **Leftovers:** the store already held 272 MB of job leftovers and 438 MB of earlier builds' navigation caches. They are not swept by this path.
- **Free space** stayed above 1 TB.

**Seeds:**
- **A (42):** one quality attempt.
- **43:** refused after 3 quality attempts in 27.6 s (`generate.exhausted`: unreachable and unplaceable towns, the repair bound exceeded).
- **B (73):** one quality attempt.

**A against B:**

| | A (42) | B (73) |
|---|---|---|
| Land cells (land and mountain) | 2,389 | 4,192 |
| Coast cells | 808 | 1,171 |
| Land components (land and mountain; the compiler's, land only) | 15; 28 | 27; 41 |
| Largest land component | 1,136 | 4,165 |
| Engine routes (reachable, forbidden) | 826 (735, 91) | 1,118 (942, 176) |
| Default routes reachable | 560 of 613 | 826 of 885 |
| Naval routes reachable | 20 of 55 | 0 of 107 |
| Navigation mesh faces | 16,422 | 16,704 |
| Playability routes | 810 | 1,018 |
| Bundle | 137 MB | 185 MB |

- **Geography:** every hash differs (heights, masks, cells, coastline). The same cell has the same class in 36.8 % of the 8,192 cells, and land against water differs in 54.5 %.
- **Traversability:** the route matrices and the navigation meshes differ. Each world's engine routes agree with its own static plan. B's 107 naval routes are all expected unreachable by its plan, which they agree with.
- **Determinism:** same-request determinism is MMR-24's (`evidence/fwg/generation-identity.json`), not re-run.

## The waiting text

The setup screen reads the budget (MMR-43), and it now states both phases. The live screen in run 35468 read: "Generating the world takes about 60 seconds. The first start of its campaign takes about 7 minutes more while the world's navigation is prepared; the game may stop responding until it is done."
- **Generation:** the largest measurement rounded up to 5 s. That is 60 s, from seed 73's 56.6 s. Before MMR-56 the screen said 35, and seed 42's 35.6 s in run 17872 already exceeded it.
- **The first start:** the largest measurement in whole minutes, rounded up. That is 7, from 410.5 s in run 17872 (which included about 2.4 s of the probe's character creation) and 398.9 s in run 35468.

## Provisional limits

`one-shot-budget.json` (`provisionalLimits`) records implementer-chosen limits, about 1.5 times the largest measurement, rounded up. The summarizer recomputes them from every merged run:

| Limit | Value | Largest measured |
|---|---|---|
| Generation | 90 s | 56.6 s |
| First campaign start | 630 s | 410.5 s |
| Load | 25 s | 16.1 s |
| Longest frame while generating (MMR-43's rule) | 0.5 s | 0.33 s |
| Game working set | 8,192 MB | 5,209 MB |
| Game private bytes | 11,776 MB | 7,543 MB |
| Producer | 448 MB | 270 MB |
| `worldbridge --bundle` | 512 MB | 322 MB |
| Store per world | 300 MB | 185 MB |
| Temporary job files | 300 MB | 184 MB |
| Free disk before generating | 768 MB | |

They are not enforced and not a guarantee. A measurement above one is recorded and re-measured. A crash, an unresponsive required operation or a failed correctness check still fails.

## Findings

- **The game stops responding for about 6.4 minutes on a world's first start** while its navigation caches are generated on the engine thread. The game recovers on its own and the wait text warns of it, but it is not resolved here. Generating the caches off the engine thread, or keeping them across module builds, would remove it.
- **The first start after any module build regenerates every world's caches.** So the first load of a save after a mod update takes about as long as a first start (MMR-49's run 48048: 396 s), not the 14.5 s measured here.

## What this does not prove

- **One machine.** Every number is this laptop's, at a 60 fps cap. Another CPU, GPU, memory, disk or frame cap changes them.
- **Size 129 only.** No other source size was measured, and no world was regenerated at another size.
- **Frames are the map's.** No mission frame rate is measured here. MMR-52 and MMR-53 open missions; MMR-42 owns their scenes.
- **B is not played.** Seed 73 is refused at activation. The second playable world of the gameplay tickets is the MMR-19 fixture (seed 364610), which the F03 pipeline does not produce.
- **Seeds 44-72** were not re-tried on this build. MMR-37 rejected them with one bundle attempt each on an earlier build; F03 gives each request 3 quality attempts, so some might generate now.
- **The probe's own placements:**
  - A's bundle was moved aside for the cold run and the original put back afterwards. The regenerated bundle's 889 content files were identical, and only the manifest's `runtimeLockSha256` differed, because the runtime lock had changed since the original was published. The regenerated copy is kept in `build/mmr56-cold-bundle/`.
  - Each run leaves two saves in Game Saves (the initial save and the manual one) and its activation package in the store. Run 35468's are bound to the regenerated bundle's manifest.
- **The embedded budget.** The module run 35468 used embeds the budget as it stood before this run's measurements were merged (`824279bc…`). The stated values, 60 seconds and 7 minutes, are unchanged by the merge.
- **No C++ bench or end-to-end bundle test.** `src/WorldBridge/bench/compile_world_bench.cpp` and `src/WorldBridge/tests/end_to_end_bundle_tests.cpp` were not written. The bridge already times its own work (`compileSeconds`, `wallSeconds`, the producer's run record), and the game path here measures it end to end. A compiler-only bench would not be an end-to-end measurement.
