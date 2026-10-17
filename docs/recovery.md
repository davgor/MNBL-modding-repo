# Recoverable errors, world inspection and cleanup (MMR-47)

How a failure reaches the player and stays diagnosable, how a crash's unfinished world is offered back, what the store's cleanup may
remove, and how to inspect the store without the game.

| Part | Where |
|---|---|
| Correlation ids and retained diagnostics | `src/MNBL.ProceduralWorld/Generation/GenerationDiagnostics.cs` |
| Error categories, messages and actions (MMR-43/44's vocabulary) | `Generation/GenerationErrors.cs` (`CorrelationId`, `RetainProblem`) |
| The failure as the player sees it | `UI/GenerationErrorViewModel.cs`; the setup's error panel and host problems in `GUI/Prefabs/WorldSetup.xml` |
| Data root, unfinished jobs, discard, cleanup | `Generation/WorldRecoveryService.cs`; startup in `Persistence/SaveBindingRuntime.RunStartup` |
| Resume in place | `GenerationCoordinator.Start` (`WorldChoice.ResumeJobRoot`, `choice.json`), `GenerationStages.RequestStage` |
| Inspection | `Persistence/WorldInspector.cs`; command `tools/Inspect-World.ps1` (`tools/InspectWorld/Program.cs`) |
| Offline tests | `tests/Generation/RecoveryTests.cs` (`tools/Test-Recovery.ps1 -BundleA <bundle>`); the setup's flow in `tests/UI/WorldSetupTests.cs` |
| In-game probe | `world_recovery` (`Automation/WorldRecoveryProbe.cs`; `phase=crash`, then `phase=resume` in the next launch) |
| Evidence | `evidence/generation/recovery/` |

## Failures the player sees (c1)

Every failure shown in the setup uses one of the eight categories (`GenerationErrorCategory`). Each category has its own localisable
title and action, and the message names only what a player can act on:

| Category | Comes from | Action |
|---|---|---|
| Missing tool or DLC | War Sails missing or disabled; a generator binary missing or changed (host check, `FromGeneration`) | Install or enable it, restart |
| Incompatible build or schema | Another game or War Sails build; a bundle built for other versions, capabilities or another schema (`FromBundleProblems`); the worldbridge's changed policies or attempt state after an update (`FromBridge`: `policy_changed`, `state_request_mismatch`); an unfinished job made by another generator (`resume.other_generator`) | Install the versions the mod was made for, or a matching mod version |
| Refused recipe | The producer refused the request | Try another seed; reinstall if it repeats |
| Exhausted quality attempts | Every attempt failed the world-quality checks | Try another seed (Random World) |
| Disk full | `IOException` disk full (0x70, 0x27), or the worldbridge's own output saying so (not enough space, error 112, ENOSPC; `DiskFullText`), whatever its exit code | Free space, generate again |
| Bad source | Reader, protocol or exit failure; any unknown status (never success) | Generate again; reinstall if it repeats |
| Runtime capability failure | Map, navigation or campaign start failed (`activation.failed`, `activation.unresolved`) | Generate again; check versions |
| Interrupted generation | Cancelled (by the player or a stop) | Generate again; the same seed gives the same world |

**Correlation id.** Each failure the player is shown is retained once (`GenerationDiagnostics.Retain`, from `WorldSetupModel`) under a
correlation id such as `MNBL-20261017-104812-3fa9c1` (UTC time plus 24 random bits, unique within the store). This covers a failed
generation, a failed campaign start, and each host problem when the setup opens.
- **The record:** `<store>/diagnostics/<id>/error.json` holds the category, the code, the message, title and action ids, the variables, the
  detail, the job id, the time and the seed.
- **The job's own files:** `timings.json`, `choice.json`, `request.json`, `state.json`, the last 60 events, and every bridge and producer
  log cut at 64 KB. They are copied because the job's directory is deleted when the setup releases it.
- **Pruning:** the newest 100 records are kept.
- **A failed write** is recorded on the error (`RetainProblem`) and never thrown.

The worldbridge's failures map to these categories in one pure place, `GenerationErrors.FromBridge`, and the bundle verifier's refusals in
`GenerationErrors.FromBundleProblems`. The offline tests reach both.

**Not retained.** A record is never written under an unverified data root, or when `diagnostics/` is a link. The player still gets the
reference, and the error notes why it was not retained (`RetainProblem`); retaining it later under a verified root writes it and clears
that note. An old record that cannot be removed is skipped and never fails a new one.

**What the screen shows.** `GenerationErrorViewModel` shows the title, the message, the action and "Reference <id> (include it when you
report this)". Details shows the category, the code, the detail and the diagnostics folder. The ordinary panel never shows a path, a hash
or a code. Host problems get the same panel, one per problem. The menu entry is no longer disabled by a host problem: the setup opens,
refuses to generate, and shows each problem with its action and reference. Before this, a disabled entry showed only a tooltip.

## The data root (c2)

The store is `%LOCALAPPDATA%\MNBL.ProceduralWorld` (`SubModule.Store`). `WorldRecoveryService.VerifyRoot` (through
`SaveBindingRuntime.RootProblem`) accepts it only when all of these hold:
- it is that per-user folder, resolved;
- it is not inside, or around, the game installation, Program Files or a Steam library;
- no folder on its path, from the drive root down, is a link (a junction cannot move it).

Nothing is cleaned or discarded under a root that does not verify; the startup report says why.

**Incoming bundle paths.** These are refused by the bundle verifier (MMR-29): `..`, rooted, device and trailing-dot names, and inventory
paths that escape the bundle; links and hard links; unlisted files. The inspector reports such a bundle as corrupt (checked offline).

## Crash recovery: unfinished jobs (c2)

A generation job's directory under `jobs/` that no live process owns, and whose job never ended, is an **unfinished job**. Every job
persists its choice as `choice.json` when it starts: seed, seed source, campaign instance, recipe, overrides, attempts and producer lock.
A job stopped before that, or whose `choice.json` is empty or torn, falls back to its `request.json`. Both files and the record are written
atomically (`DurableFile`).

A job's phase is its **last event's** (`events.jsonl`, appended for every event, the commit's included), else its record's. The record is
written only at review and at the end, so a job stopped while committing still has a record saying review: the review found this.
`WorldRecoveryService.Jobs` sorts the directories, newest first; one job's unreadable files never stop the others being listed:

| State found | Offered | Why |
|---|---|---|
| Generating (any phase before review) or ready for review | **Continue** or Discard | The bridge's attempt state and the job's request are there |
| Made by another generator (another producer lock) or under other policies (its request binds other policy hashes than the module's, `JobInputs.PolicyMismatch`) | Discard only | An old world is never regenerated under a new generator |
| Its records cannot be read (a file held or damaged), or its choice was not recorded | Discard only | Nothing safe to continue |
| Stopped while committing (the last event) | Discard only | The activation's recovery (MMR-45) decides the campaign; continuing could start it twice |
| A record says it ended (Playing, Cancelled, Failed) | not offered | The cleanup removes it |
| Nothing recorded (no choice, request, record or events): an earlier version's or an emptied directory | not offered | The cleanup removes it |
| This process's live job, a link, a non-job folder | skipped | |

**In the setup.** The newest unfinished job is offered: "A world from an earlier session was not finished (seed N). You can continue it or
discard it", with the count of others. A job that cannot be continued gets its own sentence:
- made by another version of the generator;
- stopped while its campaign was starting;
- left behind but unreadable (no seed is shown when none was read).

A refused discard says why on the screen. The list is read only while this process holds the store lock: another game process's jobs are
never offered. A continued job that never started (the generation job was busy) is offered again. A continued job that then fails or is
cancelled ends like any job: its record is final, and the setup releases it on the next Generate.
- **Continue** (`ResumeUnfinished`) starts a job with the unfinished one's own choice (seed, seed source, campaign instance) and
  `ResumeJobRoot`. The coordinator reuses that directory and id, removing only a pending request and a stale cancel file. The request stage
  keeps the accepted `request.json` (`worldbridge --new-request` never replaces one), after checking it against this generator's producer
  lock and the choice. The source stage runs `worldbridge --generate` with the same `--state`:
  - an attempt in progress runs again (a new `attempt-<i>-run-<n>`);
  - an accepted state is frozen and its world reused (never regenerated);
  - compilation reuses the identical published bundle.
- **Discard** (`DiscardUnfinished`, `WorldRecoveryService.Discard`) removes only that job's directory, and only:
  - under the verified root's `jobs/`;
  - never a live job, never through a link;
  - while this process holds the store lock.

  Bundles, packages, activations and saves are untouched. A refusal says why and the job stays.

## Cleanup (c2)

At startup, `SaveBindingRuntime.RunStartup` runs `WorldRecoveryService.Cleanup`. It runs only with the store lock held and the root
verified, after MMR-62's save recovery and MMR-45's activation recovery. It removes:
- **abandoned staging** (`StagingCleanup`, MMR-62): bundle staging, package and journal temporaries, and encoder staging;
- **finished job directories** a stopped process left;
- **job directories with nothing recorded.**

Unfinished jobs stay for the setup. A bundle, a world package, an activation record or a save is never a candidate, active or not.
Bundle retention belongs to MMR-48. The report (`<store>/saves/startup-<pid>.json`, `cleanup`) lists what was removed and every job kept,
with its reason.

## Inspection without the game (c3)

`tools/Inspect-World.ps1` reads the store (default the per-user store and the game's "Game Saves" folder) or one bundle (`-Bundle <dir>`,
any directory name). It prints a summary and writes the full JSON report (`-Out`). It reads only.
- **Bundles.** Each one is classified for this host:
  - `valid`;
  - `missing`: not a bundle directory;
  - `incompatible`: built for another Bannerlord or War Sails version, requires capabilities this host lacks, or uses another manifest or
    DATA_READY schema version;
  - `corrupt`: anything else the verifier refuses, such as changed bytes, missing or unlisted files, unsafe paths and links.

  Each row also lists every problem, the versions and the generator (`compiler`), and the saves and activations that reference it.
- **Saves.** Each save's world binding is read from its header: Bannerlord's metadata is a length-prefixed JSON list, and the
  `mnbl.world.*` keys hold the binding. A row gives the campaign instance, the world, the bundle's status, whether the package is present,
  and whether the save can be loaded. Saves of no generated world are left out; a torn header is reported.
- **Activations:** each one's state, world, bundle status and receipt.
- **Unfinished jobs, and the staging the next startup removes.**

## Evidence

- **Offline:**
  - `tools/Test-Recovery.ps1`: 10 checks (`evidence/generation/recovery/recovery-tests.json`/`.txt`); the review's fixes added the worldbridge classification table, the diagnostics edge cases (a held record, an unverified root), production-like committing, empty and torn files, other policies, an unreadable job, and a damaged bundle on save rows:
    - the failure matrix: 11 cases over all 8 categories, 8 distinct actions, a unique id and record each;
    - pruning;
    - the data root: other folder, inside the game, Steam library, through a junction;
    - unfinished jobs;
    - discard and its refusals;
    - cleanup: unverified root, staging, finished and leftover jobs, everything else kept;
    - bundle inspection: valid, missing, incompatible versions and schema, corrupt byte, `../` path, junction, repaired copy;
    - store inspection: save headers, loadable or not, references.
  - `tools/Test-WorldSetup.ps1`, the MMR-47 additions:
    - references on a failed generation and on host problems;
    - details behind the toggle;
    - an unfinished job offered, continued with its own choice and directory;
    - a job made by another generator offered only for Discard;
    - a refused discard;
    - error and host-problem bindings in the prefab (scope-aware binding check).
- **The real store before its first MMR-47 startup:** `evidence/generation/recovery/inspection-before-cleanup.json`.
  - One valid bundle (world A), referenced by 7 saves and 16 activations.
  - 46 bound saves, all loadable.
  - 2 unfinished seed-42 jobs left at review by earlier probe runs.
  - 11 directories of the pre-MMR-44 interim job (170–200 MB each), which the cleanup removes as leftovers.
- **In game:** `evidence/generation/recovery/world-recovery.json` (`tools/summarize_world_recovery.py`) recomputes 8 checks from the
  archived runs. Screenshots are in `evidence/generation/recovery/screenshots/`.
  - **Accepted pair: crash 28280, resume 47264. 8/8, every expectation passed.**
    - The game was terminated during the source generation (seed 42); the bridge and producer died with it.
    - The next startup verified the root, removed the planted abandoned staging and the earlier version's job directory, and kept the
      crashed job. No bundle, package, activation or save changed.
    - The setup offered the crashed job.
    - Continue finished it in its own directory in 36.6 s. Its request was resumed, the interrupted attempt ran again (attempt 0, run 2,
      accepted), and it gave the seed's own world, the bundle the store already held.
    - A planted job of another generator was offered only for Discard, and Discard removed only it.
    - A cancelled generation showed its message with its reference, and its record and events were retained.
    - The reviewed world was released by the next Generate, and the cancelled job on close.
  - **`world_setup` 41480** (the MMR-43 regression on this build): passed. Without War Sails the entry opens, the setup refuses to
    generate and shows the problem with its reference. Generation took 34.3 s against the stated 35 s, with a 0.16 s largest frame gap.
  - **Earlier pair 50292/49612,** kept for the record: 5/8 under today's checks. The flow worked, but the store held two earlier
    resumable seed-42 jobs that were offered before the planted one. Its discard step therefore removed one of them (a job directory of an
    earlier probe run; its bundle untouched).
- **The real store after this build's first startups** (`evidence/generation/recovery/inspection-after-cleanup.json`):
  - the 11 interim-job leftovers are gone;
  - the one remaining job (`8be23b37…`) is classified as stopped while committing, offered for Discard only (the review's finding,
    confirmed);
  - the bundle and all 46 bound saves are unchanged and loadable.

## Limits

- **Resume is of the generation job, up to review.** A crash while committing is MMR-45's activation recovery.
- **Retention of bundles and packages** belongs to MMR-48. The cleanup never removes one.
- **Physical devices are not driven** by the probe; the setup's buttons and keys are driven through the view model and the screen's mapping.
