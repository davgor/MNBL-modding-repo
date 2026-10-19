# MMR-56 independent review and fixes

One independent review of the MMR-56 working tree, after test run 17872. The reviewer was an agent with read-only access. It did the following without editing files, building or launching the game:
- read the probe, the setup's wait-time code and tests, the summarizer, the budget, the seeds file, the measurements and the doc draft;
- re-ran the summarizer's functions in memory against run 17872, and recomputed the coastline, the land components and every doc number;
- checked the `cells.bin` decoding against `travel_graph.cpp`'s writer;
- read the producer's supervision (`producer_process.cpp`), `ActivationCommitStage` and the navigation cache build.

It reported 1 blocker, 8 major and 10 minor findings. All were fixed in one batch, then one confirming run followed: run 35468, 20/20 checks, status ok.

## Before the review

Test run 17872 passed 15/17. Its play-speed sample used `StoppablePlay`, which holds time while the player's party stands idle (0 campaign hours in 20 s). The probe now uses `UnstoppablePlay`.

## Blocker

1. **The start phase's responsiveness was unmeasured, and the doc called the cost harmless.** Fixed:
   - the start loop times every frame and records each gap over 1 s with what was on screen;
   - the summary checks that the start was measured, and lists the freeze under `findings`;
   - the doc states it, and the wait text warns that "the game may stop responding".

   Run 35468 shows one 383.1 s frame under the loading screen, while the navigation caches are generated on the engine thread.

## Major

2. **The "producer" memory was worldbridge's.** `fwg.exe` runs in worldbridge's job object, so worldbridge's receipt is only the supervising process. Fixed: the probe reads the producer's `run-record.json` (elapsed time and the job's peak memory). The summary and the doc label each process: the producer peaked at 247 and 270 MB, not 51 and 57.
3. **A's generation was warm.** Its bundle was already published, and the doc's claim about the saving compared different worlds. Fixed:
   - A's bundle was moved out of the store for run 35468, so A generated cold;
   - the summary checks the bundle did not exist;
   - the unsupported claim is removed. Run 17872's warm 35.6 s is reported as such.

   The original bundle was restored afterwards. The regenerated copy differed only in the manifest's runtime-lock hash, and it is kept in `build/mmr56-cold-bundle/`.
4. **The sub-timings were implied but not measured.** Fixed: the doc splits what can be split:
   - the producer against worldbridge around it;
   - the bundle's read and convert against its write;
   - the scene's encode against its install;
   - the preview and summary after the job;
   - the start's caches, creation screen and remainder;
   - the job directory's size every 2 s, for the temporary disk peak.

   It states what cannot be split: the producer's JSON write and the bundle's JSON read are inside their processes' times.
5. **`startCampaign` included character creation.** Fixed: the probe measures the time the creation screen was up, and the start phase subtracts it. Run 17872's value, which predates this, is marked as about 2.4 s high.
6. **The budget writer could lose or understate measurements.** Fixed:
   - each run is merged once, by pid, without dropping repeated values;
   - only starts whose caches were generated count;
   - the provisional limits are recomputed from everything merged.
7. **"Up to 7 minutes" read as a guarantee.** Fixed: "about 7 minutes", with the freeze warning.
8. **The seed rationale overclaimed.** Fixed in `acceptance-seeds.json`:
   - MMR-37 tried each seed with one bundle attempt, while F03 gives 3, so seeds 44-72's rejection may not carry over and 73 is not shown to be the first;
   - the uncited seed 50 claim is removed.
9. **The build fingerprints were incomplete.** Fixed:
   - the probe hashes the native engine (`Bannerlord.exe`, `TaleWorlds.Native.dll`) and the embedded setup documents at run time;
   - the summary checks that the run's module is the one the build recorded.

## Minor

10. **Budget values not matching the run** (temporary disk, the private-bytes limit): the limits are now computed from the merged measurements.
11. **Frame-rate wording.** The doc gives fps from the mean. It states the 60 fps cap (`max_framerate` 200, yet every sample at 16.7 ms), and the GPU that rendered, from the engine's 16,003 MB of GPU memory.
12. **OS and power.** Windows is read from the registry (build 26200; the product name still reads "Windows 10 Home", as Windows 11 keeps it), along with AC power and the power scheme.
13. **The memory sampler.** The phase is read once per sample and the process handle disposed; the paged and virtual peaks are recorded at the end.
14. **The summarizer and the routes.**
    - The comparisons guard against missing values.
    - The generation's longest frame is checked against MMR-43's 0.5 s.
    - Each persisted navigation report is checked to be the job's, by its hash.
15. **The setup summary on old evidence.** `summarize_world_setup.py` takes the budget as an argument, so re-summarizing an MMR-43 run needs the budget its build embedded. `docs/world-setup.md` says so.
16. **Stale docs updated:** `docs/world-setup.md`, `docs/world-preview.md`, `docs/generation-job.md` and `docs/runtime-backend-decision.md`, plus a pointer in `docs/runtime-capabilities.md`.
17. **Doc gaps closed:** this record; the two land-component definitions; B's 107 naval routes, all unreachable as its plan expects; the profile's `secondSeed` 43, refused and recorded.
18. **Resource facts stated:**
    - the store's leftovers from earlier runs;
    - the cache regeneration on the first load after a module update;
    - the initial save's size;
    - the two saves each run leaves.
19. **The wait-text test** computes its expected values from the budget instead of hard-coding them.

## Confirming run

Run 35468 passed 20/20 checks, status ok:
- **A:** generated cold in 32.7 s. Its first start took 398.9 s of activation work, with the game not responding for 383 s. A save loaded in 14.5 s.
- **The other seeds:** seed 43 was refused, and seed 73 generated in 55.7 s.
- **A against B:** different in geography and engine routes.
- **The wait text:** the live screen read "about 60 seconds" and "about 7 minutes more … may stop responding".
- **Offline:** `tools/Test-WorldSetup.ps1` passes 29/29 with the merged budget.

Not changed: no C++ bench or end-to-end bundle test was written. The doc explains why: the bridge times itself, and the game path measures it end to end.
