# One-shot acceptance (MMR-1, 2026-10-19)

The final acceptance of epic `ep_01m4fpnf646t254mnh69jhz107`: the one-shot scenario on the clean local package, with the development module and store unavailable, and the established offline suites on the integrated sources.

**Verdict: the scenario passes.** Every aspect of steps 1–6 and of the sequence passed in one run on one package (`evidence/one-shot/one-shot-run.json`). **The package is a local test package, not release-certified.**

| | |
|---|---|
| Passed | Steps 1–6 and the sequence, every aspect, on package `51abd1cc…` (the re-run after the seed-43 fixes, 2026-10-19 evening) |
| Earlier pass | Package `e20f2667…`, the same day; its record is kept as `build/mmr1/one-shot-run-e20f2667.json` |
| Found and fixed before the final run | Five earlier runs that day found failures; each was fixed before the final run (below) |
| Seed 43 | The profile's "next seed" now generates: its second attempt's world `8cb59e18…`, with all 792 playability routes passing. On `e20f2667` it was refused at runtime navigation |
| Deferred | Broad seed certification, long soaks, endurance, all-season play, resolution and performance certification (the acceptance index, not run) |

| | |
|---|---|
| Orchestrator | `tools/Test-OneShot.ps1` (raw record `build/mmr1/one-shot-raw.json`, git-ignored) |
| Verdict | `tools/summarize_one_shot.py` → `evidence/one-shot/one-shot-run.json` |
| Offline suites | `tools/Test-OfflineSuites.ps1` → `evidence/one-shot/offline-suites.json` |
| Epic outcomes | `evidence/one-shot/acceptance-index.json` (`python tools/build_acceptance_index.py`) |

## What ran

**Identities:**
- **Package:** `MNBL.ProceduralWorld-v0.0.1.zip`, SHA-256 `51abd1cc9b88dc3d2a7e3d0d8f5b15f9301a54d9288fbde23864a5d542789a66`, 11 files.
- **Binaries:** module assembly `bb6c6fd2da1a…`, `worldbridge.exe` `2bfdbb9d0e2a…`, `fwg.exe` `564c2629b5b9…` (the pin).
- **Game:** Bannerlord v1.4.8.119303 with War Sails v1.2.8.119303.
- **Source:** commit `6d34cef` (the seed-43 fixes) plus the uncommitted probe fixes of the evening runs. The run records `sourceTreeClean: false`; the commit that follows records them. No source changed after the package was built.

**Setup** (`tools/Test-OneShot.ps1`):
- held the battle-scale lock for the whole sequence;
- moved the development module and the per-user store aside, so the store started empty;
- extracted the package alone (it matched its manifest, with no development receipt);
- ran each step as its own game process, so every load after the first is a cold restart;
- put everything back afterwards.

**Saves:** the land and naval scenario files were copied with their own designated save name (`mnbl_oneshot_A_designated`), so no existing save was overwritten. Every save from before the sequence (143) and the official files were unchanged afterwards. The sequence's store is kept in `build/mmr1/one-shot-store-20261019203149/`.

| Run | Probe | Seconds | Result |
|---|---|---|---|
| 23852 | `first_test_profile` (A seed 42, next seed 43, B seed 73) | 660 | ok |
| 35592 | `new_campaign_flow` (seed 42; a second generation cancelled) | 150 | ok |
| 46848 | `land_missions` prepare | 90 | ok |
| 51924 | `naval_missions` | 375 | ok: 117/117 checks, 15/15 cases |
| 52204 | `land_missions` run, `seedB=73` | 1186 | ok: 163/163 checks, 14/14 cases (no world has steppe) |
| 7156 | `campaign_soak` | 135 | ok |
| 54052 | `two_world_matrix` create, `seedB=73` | 1081 | ok: 38/38 |
| 30068 | `two_world_matrix` cold | 120 | ok: 20/20 (the saves kept for the next steps, as asked) |
| 54116 | `seam_presentation` on B's matrix save | 120 | ok |
| 36200 | `compat_matrix` | 75 | ok: 13/13 |

The counts are recomputed from each probe's record into `evidence/gameplay/naval-scenarios.json`, `evidence/gameplay/land-missions.json`, `evidence/persistence/two-world-matrix.json` and `evidence/persistence/compat-matrix.json`.

**World A** (seed 42): content id `f596b58cdcfc1879…`, scene `mnbl_world_f596b58cdcfc1879_019bf8ec927e`. Generated in 35.0 s from the New Campaign screen. The map came after 395.3 s, most of it the first build of the navigation caches, during which the game does not respond. Its save loaded in 14.4 s in the same process.

**World B** (seed 73): content id `f63c6841085725e2…`, scene `mnbl_world_f63c6841085725e2_c36448e016ee`. Generated in the same process in 54.7 s; 2,389 vs 4,192 land cells and different coastlines from A. Started from the setup screen in the matrix, played in the land missions (its wetland battle, which A lacks) and in the seam presentation.

**Seed 43** (the profile's next seed): its first attempt was rejected by the quality gate. Its second attempt's world (`8cb59e18…`, scene `mnbl_world_8cb59e185502c942_89dba7127cfd`) generated in 40.8 s, its 792 playability routes all passing; the profile reviews it and goes back. On `e20f2667` that world was refused at runtime navigation (one All route across the seam was 1.29 × the mesh's shortest path against 1.25). Commit `6d34cef` fixed that refusal and the seam flag behind it (`docs/runtime-navigation.md`, `docs/playability.md`).

No generator process was left after any run. The navigation caches were built once per world and reused by every load (A `dadc84ff`, B `ebc5475a`).

## Step by step

| Step | Status | Notes |
|---|---|---|
| 1. Identities, world A from New Campaign, culture, character creation, play, full globe, source-derived identities | pass | The running module, `fwg.exe` and `worldbridge.exe` are the package's and the pins; the producer's pinned runtime matches (the UCRT pin was renewed after a Windows update); every preview settlement and kingdom matches the live campaign; creation lists exactly the world's cultures; every cell of the 128 × 64 periodic grid is classified |
| 2. Land travel, settlement, trade and recruitment, a land mission, return | pass | Travel, trade and recruitment in a generated town (`campaign_soak`; through the screens' logic, not the screens). Every land case of A and B's wetland battle; 14 mission returns with the world identity equal before and after |
| 3. Port, embark, sail, naval encounter, disembark; raid, blockade, anchor | pass | Shipyard purchase and repair, port and island voyages, drop-off and coastal landing, blockade and anchor, a pirate battle, a voyage and a pirate encounter across the seam, a seaborne raid won, defeat with the raft |
| 4. East/west crossing for player and AI on land and sea, routes, encounters and visibility, caches, camera and picking | pass | On A the player crossed the seam on land, an AI party pursued across it, a fleet was called across it and an anchored fleet stayed by it, all through save and load. On B: display copies, camera follow and pan, fast moves, picking, tracks, alignment, a town menu across the seam and the wrap budget between two towns joined by land across it. The caches were reused by every load |
| 5. Save A with mutable state; B in the same process; save B, cold restart, load A then B; no generator on load | pass | A saved with changed ownership, a spawned party, a prisoner, a siege, ships and anchors, and an AI lord's fleet after a naval battle; loaded in the same process and after a cold restart (scene swept and re-encoded), equal to its save. B generated, started, changed and saved; loaded in the same process from inside A and after the cold restart |
| 6. Cancel a generation, an invalid copy of world data; recovery, unchanged hashes, no orphans or cross-world callbacks | pass | A second generation cancelled through the setup screen; torn, missing-world and other-build saves and a Continue into a missing world refused; incompatible schema and build, missing DLC, a corrupted manifest hash and an isolated damaged copy of A's world data refused; the bundles unchanged; no generator process left; no cross-world callbacks after any load |

Step 7 is this record: `evidence/one-shot/one-shot-run.json`, and s7 and `finalScenario` in the acceptance index.

## Found and fixed on the way

The evening's re-runs after the seed-43 fixes, on packages `58eb06ff…`, `7417125e…` and `bc382a0f…` (raw records `build/mmr1/one-shot-raw-<package>.json`). Each failure was in a probe, not in the module, and was fixed before the next run:
- **The pirate encounter across the seam "returned 799 units away".** The return push carried the party over the seam line, where x wraps by the width, and the return was measured planar. Returns are now measured across the seam (`RuntimeMissionProbe.CompareReturn`, `LandMissionsProbe.ReturnTo`).
- **The game crashed natively in the matrix after world B loaded** (`c0000005`, both create and cold). Looters met the one-man party, and `AfterLoadB` called `PlayerEncounter.Finish` every frame while their map conversation opened. It now ends the conversation first (`ClearEncounter`), as world A's run does. A replay on the kept save (run 5452) met the same looters and ran on.
- **The soak found no volunteer on arrival.** The town chosen offered 10 and had none left 8.7 hours later; lords recruit from a town's notables as they pass, the likely cause. The party now goes on to the nearest other offering town, at most 3 towns, and records each emptied town.
- **The seam presentation saw a still map.** Looters stopped the party in its first leg. The probe now shelters the player, as the soak and world A do.
- **The seam presentation's wrap leg did not move.** After the teleport, a move order to the point the party was last sent to kept the old path. That path ran 781 units straight from the new start. The party is now held after each teleport before the order.

The day's runs before the `e20f2667` pass, on packages `4a390d27…` and `f91a42f4…` (the second's record is `build/mmr1/one-shot-run-f91a42f4.json`):
- **Character creation broke** when the spatial table first included `CampaignUIHelper.GetHeroBehaviorText`: patching it ran the class's initializer before the game texts existed. That method is left out (`docs/periodic-presentation.md`).
- **The naval raid stalled** when War Sails' landing left the player's agent down: the probe killed defenders only with the main agent. The killer is now any active agent of the player's team (regression `raid-main-agent-down`).
- **The matrix's AI fleet battle was left to chance:** a lord with 92 men lost to 67 pirates and had no fleet to save. The pirate now keeps at most a third of the lord's men.

The offline seed sweep before the runs fixed the bridge and the runtime for worlds other than A and B (`docs/travel-graph.md`, Yield): ports on bridged cells or beyond the engine's port envelope, harbours on closed basins, decks shared by two joins, unplaceable villages crashing three runtime readers, and lake copies cut by the terrain's edge.

## The offline suites

`tools/Test-OfflineSuites.ps1` runs, each in its own process:
- every csc harness, on worlds A and B;
- the producer and bridge suites, including worldbridge's CTest in an isolated build;
- the package path test, the scenario checks and the replays (the naval fixture is now the final run's, `naval-missions-39772.json`, with the seam cases);
- the release tooling tests, the registry check, the producer lock check and the Python format tests.

`tools/Test-GenerationIdentity.ps1` is not run: it rewrites the embedded `worldgen/contracts/producer-seed-width.json`, a build input. MMR-24's evidence stands.

## What this does not establish

- **Every seed:** 19 of 32 seeds (42–73) are accepted at one attempt and pass the activation's managed stages offline. Only seeds 42 and 73 were played in game, and seed 43 generated and started. Seeds 56 and 67 were refused in game at settlement placement (`settlement.port_far`) on this package. A later fix, not in this package, turns a port town's gate toward its port (`docs/settlement-entities.md`), and with it both generate and start (runs 51808, 25336).
- **The named presentation limits** (`docs/periodic-presentation.md`, Limits): the hero behaviour text measures planar near the cut; siege machines are drawn on the settlement's side only; a route whose path leaves the terrain cannot be represented.
- **Deferred certification** (seed corpus, runtime-world sweep, long soaks, endurance, all-season play, resolution and performance certification) is listed in the acceptance index as not run.

## Handoff

Open owner decisions are on the epic:
- the copied official culture text;
- the Visual C++ runtime;
- read-only installations;
- ANSI user names;
- a release on merge into `main`.

Possible follow-up work:
- a refused attempt falling through to the next derived seed;
- the in-game activation of the other accepted seeds (a runtime-world sweep);
- siege machines on the display copies.
