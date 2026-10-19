# One-shot acceptance (MMR-1, 2026-10-19)

The final acceptance of epic `ep_01m4fpnf646t254mnh69jhz107`: the one-shot scenario on the clean local package, with the development module and store unavailable, and the established offline suites on the integrated sources.

**Verdict: the scenario passes.** Every aspect of steps 1–6 and of the sequence passed in one run on one package (`evidence/one-shot/one-shot-run.json`). **The package is a local test package, not release-certified.**

| | |
|---|---|
| Passed | Steps 1–6 and the sequence, every aspect, on package `e20f2667…` |
| Found and fixed before the final run | Two runs earlier the same day found three failures; each was fixed before the final run (below) |
| Not part of the verdict | Seed 43, the profile's "next seed", was refused at runtime navigation with its error and action, as the step records (s2 in the acceptance index) |
| Deferred | Broad seed certification, long soaks, endurance, all-season play, resolution and performance certification (the acceptance index, not run) |

| | |
|---|---|
| Orchestrator | `tools/Test-OneShot.ps1` (raw record `build/mmr1/one-shot-raw.json`, git-ignored) |
| Verdict | `tools/summarize_one_shot.py` → `evidence/one-shot/one-shot-run.json` |
| Offline suites | `tools/Test-OfflineSuites.ps1` → `evidence/one-shot/offline-suites.json` |
| Epic outcomes | `evidence/one-shot/acceptance-index.json` (`python tools/build_acceptance_index.py`) |

## What ran

**Identities:**
- **Package:** `MNBL.ProceduralWorld-v0.0.1.zip`, SHA-256 `e20f26676ea91410f82a8176368d9af602507324bc7b0c645e5c08bae069e0a5`, 11 files.
- **Binaries:** module assembly `3070d8bc3ae0…`, `worldbridge.exe` `2bfdbb9d0e2a…`, `fwg.exe` `564c2629b5b9…` (the pin).
- **Game:** Bannerlord v1.4.8.119303 with War Sails v1.2.8.119303.
- **Source:** commit `7c56032` plus the uncommitted 2026-10-19 fixes. The run records `sourceTreeClean: false`; the commit that follows records them. No source changed after the package was built.

**Setup** (`tools/Test-OneShot.ps1`):
- held the battle-scale lock for the whole sequence;
- moved the development module and the per-user store aside, so the store started empty;
- extracted the package alone (it matched its manifest, with no development receipt);
- ran each step as its own game process, so every load after the first is a cold restart;
- put everything back afterwards.

**Saves:** the land and naval scenario files were copied with their own designated save name (`mnbl_oneshot_A_designated`), so no existing save was overwritten. Every save from before the sequence (123) and the official files were unchanged afterwards. The sequence's store is kept in `build/mmr1/one-shot-store-20261019122025/`.

| Run | Probe | Seconds | Result |
|---|---|---|---|
| 40412 | `first_test_profile` (A seed 42, next seed 43, B seed 73) | 661 | ok |
| 50244 | `new_campaign_flow` (seed 42; a second generation cancelled) | 150 | ok |
| 48948 | `land_missions` prepare | 90 | ok |
| 39772 | `naval_missions` | 360 | ok: 117/117 checks, 15/15 cases |
| 18492 | `land_missions` run, `seedB=73` | 1186 | ok: 163/163 checks, 14/14 cases (no world has steppe) |
| 20424 | `campaign_soak` | 120 | ok |
| 43576 | `two_world_matrix` create, `seedB=73` | 1081 | ok: 38/38 |
| 44704 | `two_world_matrix` cold | 120 | ok: 20/20 (the saves kept for the next steps, as asked) |
| 44028 | `seam_presentation` on B's matrix save | 120 | ok |
| 49820 | `compat_matrix` | 75 | ok: 13/13 |

**World A** (seed 42): content id `f596b58cdcfc1879…`, scene `mnbl_world_f596b58cdcfc1879_019bf8ec927e`. Generated in 35.7 s from the New Campaign screen. The map came after 395.3 s, most of it the first build of the navigation caches, during which the game does not respond. Its save loaded in 14.3 s in the same process.

**World B** (seed 73): content id `f63c6841085725e2…`, scene `mnbl_world_f63c6841085725e2_c36448e016ee`. Generated in the same process in 56.3 s; 2,389 vs 4,192 land cells and different coastlines from A. Started from the setup screen in the matrix, played in the land missions (its wetland battle, which A lacks) and in the seam presentation.

**Seed 43** (the profile's next seed): its first attempt was rejected by the quality gate; its second attempt's world (`8cb59e18…`) was refused by the in-game navigation validator (`runtime.navigation_refused`: one sea route across the seam is 1.29 × the mesh's shortest path, against the bound of 1.25), shown with its error code and a Generate-again action. A refusal at that stage ends the job; it does not try the next derived seed.

No generator process was left after any run. The navigation caches were built once per world and reused by every load (A `b00bfd27`, B `59e4ce7f`).

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

The day's runs before the final one, on packages `4a390d27…` and `f91a42f4…` (the second's record is `build/mmr1/one-shot-run-f91a42f4.json`):
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

- **Every seed:** 19 of 32 seeds (42–73) are accepted at one attempt and pass the activation's managed stages offline, but only seeds 42 and 73 were played in game, and seed 43 was refused at runtime navigation.
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
- the engine's sea-route detour that refused seed 43's world, or a refused attempt falling through to the next derived seed;
- the in-game activation of the other accepted seeds (a runtime-world sweep);
- siege machines on the display copies.
