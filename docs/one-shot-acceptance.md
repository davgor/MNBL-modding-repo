# One-shot acceptance (MMR-1, 2026-10-19)

The final acceptance of epic `ep_01m4fpnf646t254mnh69jhz107`: the one-shot scenario on the clean local package, with the development module and store unavailable, and the established offline suites on the integrated sources.

**Verdict: the scenario fails.**

| | |
|---|---|
| Passed | Every in-game check of step 1 |
| Observed failures | **Pinned runtime:** a Windows update replaced the producer's pinned UCRT. **Land:** a battle was joined by a third party. **Naval:** an island voyage turned back. **Navigation caches:** they were rebuilt on a load although they already existed |
| Reruns | The land and naval failures did not reproduce when rerun on the same build from another save. Whether they are intermittent or depend on the campaign state is not established |
| Blocked | Every step that needs a second playable world (only seed 42 activates) |
| Not run | The seam camera, picking and sea crossing on the generated world |

By the epic's rule an observed failure in the required scenario remains failed. **The package is a local test package, not release-certified.**

| | |
|---|---|
| Orchestrator | `tools/Test-OneShot.ps1` (raw record `build/mmr1/one-shot-raw.json`, git-ignored) |
| Verdict | `tools/summarize_one_shot.py` → `evidence/one-shot/one-shot-run.json` |
| Offline suites | `tools/Test-OfflineSuites.ps1` → `evidence/one-shot/offline-suites.json` |
| Epic outcomes | `evidence/one-shot/acceptance-index.json` (`python tools/build_acceptance_index.py`) |

## What ran

**Identities:**
- **Package:** `MNBL.ProceduralWorld-v0.0.1.zip`, SHA-256 `ed63d689da10e7c58369bdc6ed1938d8484d0cc5abb4633b06177601132e52aa`, 11 files.
- **Binaries:** module assembly `bc3235345ea2…`, `worldbridge.exe` `776c160626d0…`, `fwg.exe` `564c2629b5b9…` (the pin).
- **Game:** Bannerlord v1.4.8.119303 with War Sails v1.2.8.119303.
- **Source:** commit `55576ca` plus the MMR-1 changes (probe options and the one-shot tooling), recorded by the MMR-1 commit. No source changed after the package was built.

**Setup** (`tools/Test-OneShot.ps1`):
- held the battle-scale lock for the whole sequence;
- moved the development module and the per-user store aside, so the store started empty;
- extracted the package alone (it matched its manifest, with no development receipt);
- ran each step as its own game process, so every load after the first is a cold restart;
- put everything back afterwards.

**Saves:**
- The land and naval scenario files were copied with their own designated save name (`mnbl_oneshot_A_designated`), so no existing save was overwritten.
- Every save from before the sequence (107) and the official files were unchanged afterwards.
- The 8 saves the sequence wrote are bound to its own store, which was set aside, so Continue would have offered them and then refused them. They were moved out of `Game Saves` into `build/mmr1/one-shot-saves/`, and that store is kept in `build/mmr1/one-shot-store-20261019003951/`.

| Run | Probe | Seconds | Result |
|---|---|---|---|
| 23796 | `first_test_profile` (A seed 42, refused 43, B seed 73) | 690 | ok |
| 29176 | `new_campaign_flow` (seed 42) | 180 | ok |
| 42980 | `land_missions` prepare | 105 | ok |
| 48692 | `naval_missions` | 120 | **error**: `route.island` did not arrive; 5 later cases cascaded |
| 29368 | `land_missions` run, `fixtureB=false` | 270 | **error**: `field.rock` had 3 parties |
| 37128 | `campaign_soak`, `fixtureB=false` | 135 | ok |
| 35948 | `two_world_matrix` create, `fixtureB=false` | 540 | ok (its load rebuilt the navigation caches, 397 s) |
| 31404 | `two_world_matrix` cold | 90 | ok |
| 31460 | `compat_matrix` | 75 | ok |
| 7368 | rerun `naval_missions`: same build, development install, from `mnbl_probe52_A_designated` | | ok, all 9 cases |
| 11472 | rerun `land_missions`: same build, development install, from `mnbl_probe52_A_designated` | | ok, all 13 cases world A has |

**World A** (seed 42):
- **Identity:** content id `f596b58cdcfc1879…`, scene `mnbl_world_f596b58cdcfc1879_171df43a3a14`.
- **Timings:** generated in 37.4 s from the New Campaign screen. The map came after 403.4 s, most of it the first build of the navigation caches, during which the game does not respond. Its save loaded in 15.0 s in the same process.

**World B** (seed 73):
- **Generation:** generated in the same process in 56.3 s, content id `f63c6841085725e2…`.
- **Difference from A:** different geography (2,389 vs 4,192 land cells, different coastlines).
- **Not started:** its activation is refused at the village binding.

No generator process was left after any run.

## Step by step

| Step | Status | Notes |
|---|---|---|
| 1. Identities, world A from New Campaign, culture, character creation, play, full globe, source-derived identities | **fail** | **In game, everything passed:** the running module, `fwg.exe` and `worldbridge.exe` are the package's and the pins; every preview settlement and kingdom matches the live campaign; character creation lists exactly the world's cultures; every cell of the 128 × 64 periodic grid is classified. **Failed:** the producer's pinned runtime. A Windows update (KB5127647 and KB5127648, 2026-10-13 and 14) replaced `ucrtbase.dll` after the pin, so the producer contract's preflight fails `ucrtbase_matches_lock` (`evidence/one-shot/ucrt-drift.json`). The in-game generation path does not check the UCRT, and world A's content id is the accepted one |
| 2. Land travel, settlement, trade and recruitment, a land mission, return | **fail** | Travel, trade and recruitment in a generated town passed (`campaign_soak`; through the screens' logic, not the screens). 12 of 13 land mission cases passed. **`field.rock` failed:** an AI lord's party joined the player's battle, which the probe requires to have exactly two parties; the mission itself opened and returned. Rerun 11472, from a different designated save, passed every case. World A has no steppe or wetland, so those battle classes are **blocked** (they need world B). The world identity is not re-checked after a mission (not-run) |
| 3. Port, embark, sail, naval encounter, disembark; raid, blockade, anchor | **fail** | The ship purchase, port voyage and repair passed. **`route.island` failed:** the voyage from Korpor toward the island town Frosrin landed after about 9 hours and walked back to Korpor, where the town menu ended the voyage. The blockade, drop-off, pirate battle, raid and defeat cases then failed only because each starts where the previous one ends. Rerun 7368, from a different designated save, passed all 9 cases (the island voyage in 32.2 h, all at sea). The cause is not identified, and whether the failure depends on the freshly prepared campaign's state is not established |
| 4. East/west crossing for player and AI on land and sea, routes, encounters and visibility, caches, camera and picking | **fail** | On world A the player crossed the seam on land, an AI party pursued across it, a fleet was called across it and an anchored fleet stayed by it, and all of it survived save and load. **Failed:** the navigation caches. They were built at A's first start (key `2a603033…`), then built again in the matrix's same-process load (397 s) although they existed. The cold load, after its scene was swept and re-encoded, read another key (`175479b6…`, 34.9 s). The cause is not identified. **Not run on a generated world:** camera and picking at the seam (shown on the MMR-66 fixture only), the player and an AI party sailing across it, and a naval encounter at the seam |
| 5. Save A with mutable state; B in the same process; save B, cold restart, load A then B; no generator on load | **blocked** | A was saved with changed ownership, a spawned party, a prisoner, a siege, ships and anchors. It loaded in the same process and again after a cold restart (scene swept and re-encoded), equal to its save over 1,006 parties, with no generator process started. B was generated in the same process and differs from A. **Saving and loading B are blocked:** only seed 42 activates |
| 6. Cancel a generation, an invalid copy of world data; recovery, unchanged hashes, no orphans or cross-world callbacks | **blocked** | A second generation was cancelled through the setup screen. Invalid copies of A's save were refused before anything loaded: torn, missing world, other build and a Continue into a missing world (`two_world_matrix`), plus incompatible schema and build, missing DLC and a corrupted manifest hash (`compat_matrix`). A's save, package and bundle were unchanged, and no generator process was left. **Not run:** a separate test root (the store and save paths are fixed). **Blocked:** cross-world callbacks (no second world; with B blocked, the matrix's "nothing of world B is registered" check is vacuous) |

Step 7 is this record: `evidence/one-shot/one-shot-run.json`, and s7 and `finalScenario` in the acceptance index.

## The offline suites

`tools/Test-OfflineSuites.ps1` runs, each in its own process:
- every csc harness, on worlds A and B;
- the producer and bridge suites, including worldbridge's CTest in an isolated build;
- the package path test, the scenario checks and the replays;
- the release tooling tests, the registry check, the producer lock check and the Python format tests.

`tools/Test-GenerationIdentity.ps1` is not run: it rewrites the embedded `worldgen/contracts/producer-seed-width.json`, a build input. MMR-24's evidence stands.

**45 of 46 pass.** The one failure is `Test-ProducerProcess`'s preflight check `ucrtbase_matches_lock`, the UCRT drift above. Its CTest (9/9) and every generation outcome pass. Today's run is `evidence/one-shot/producer-process-contract-20261019.json`; MMR-22's accepted run, from before the update, is unchanged.

**Two regressions fixed on the way:**
- `Test-Deployment.ps1` had not given its fixture the surface template files that the deploy copies since MMR-57.
- The evidence cleanup had moved eight raw run files that harnesses read; they are back, and `docs/runbooks/development.md` lists them.

## After the run

The independent review's points about the orchestrator were applied, without a rerun:
- it creates and holds the battle-scale lock even if the lock file does not exist yet;
- it refuses to start if its designated save already exists;
- `-Restore` refuses while a game is running.

## What this does not establish

- **The second world:** two playable production-generated worlds. Only seed 42 activates (`docs/runbooks/player-worlds.md`, Seeds).
- **The causes of the failures:** the land and naval failures, and the cache rebuild, are untraced. Until the land and naval causes are found, those two probes are unreliable as single-run acceptance.
- **The seam on a generated world:** camera, picking and naval play at the seam.
- **The package lifecycle:** the disable, remove, reinstall and read-only sequence ran on the previous package (MMR-57, `f70b44c0`), not on this one.
- **Deferred certification** (seed corpus, runtime-world sweep, long soaks, endurance, all-season play, resolution and performance certification) is listed in the acceptance index as not run.

## Handoff

Open owner decisions are on the epic:
- the copied official culture text;
- the Visual C++ runtime;
- read-only installations;
- ANSI user names;
- a release on merge into `main`;
- re-pinning the producer's UCRT after the Windows update. That pin is in `worldgen/producer.lock.json`, a world input.

Possible follow-up work:
- make more seeds activate (map epic 2's MMR-251 repairs towns instead of rejecting worlds);
- trace the land and naval failures and the cache rebuild. For the land and naval failures, a rerun from the kept `mnbl_oneshot_A_designated` save and its store would tell intermittent from state-dependent;
- drive camera, picking and naval encounters at the seam on world A.
