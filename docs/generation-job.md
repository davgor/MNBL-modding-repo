# Generation job (MMR-44)

The transactional generation job: from the player's choice on the setup screen (`WorldChoice`) to a world ready for review. It promotes the components earlier tickets proved, in place, behind one coordinator. It is not a second pipeline.

| Part | Where |
|---|---|
| State machine (pure) | `src/MNBL.ProceduralWorld/Generation/JobStateMachine.cs` |
| Progress events and the consumer's sink (pure) | `Generation/ProgressEvents.cs`, contract `worldgen/contracts/progress-event.schema.json` |
| Engine-thread scheduler and job tickets (pure, engine thread injected) | `Generation/EngineWorkScheduler.cs`, pumped by `SubModule.OnApplicationTick` beside MMR-37's `EngineWork` (two queues: `EngineWork` serves the MMR-37/38 probes) |
| Coordinator, job, cancellation, ownership, record (pure) | `Generation/GenerationCoordinator.cs` |
| Production stages and the game's coordinator | `Generation/GenerationStages.cs` (`GenerationRuntime.Coordinator`) |
| Setup integration | `UI/WorldSetupScreen.cs` `CoordinatorRunner`; `UI/WorldSetupModel.cs` takes the progress events |
| Offline tests | `tests/Generation/JobStateMachineTests.cs`, run by `tools/Test-GenerationJob.ps1` |
| In-game probe and summary | `generation_job` (`Automation/GenerationJobProbe.cs`; args `seed`, `otherSeed`); `tools/summarize_generation_job.py` writes `evidence/generation/generation-job.json` |

## States

```
Idle -> ValidatingRequest -> GeneratingSource -> Compiling -> ValidatingData -> BuildingScene -> RegisteringCampaign -> ValidatingRuntime
     -> ReadyForReview -> Committing -> Playing
```

The state machine is version 2 (`JobStateMachine.Version`, carried in every event and record). Version 2 (MMR-46) adds one backward transition: a withdrawn commit returns Committing to ReadyForReview.
- **Transitions.** A job moves only to the next phase, to **Cancelled**, or to **Failed**:
  - Cancelled is possible from any phase up to and including ReadyForReview, that is, before commit begins.
  - Failed is possible from any running phase.
  - From **Committing** a job finishes (Playing), recovers (Failed after the commit stage's `Recover`), or is **withdrawn** back to ReadyForReview; it is never cancelled.
  - **Withdrawn** (MMR-46): the player left character creation before the campaign map. The commit stage throws `CommitWithdrawn` only when the activation rolled everything back. The coordinator then calls `Recover`, returns the job to ReadyForReview (Committing is timed with outcome `withdrawn`), notes `commit withdrawn` in the job's cleanup, and publishes `commit_withdrawn`. The job's staging and ticket stay as they were, so the same world can be started again without generating it twice. No other phase goes back.
- **Final states.** Playing, Cancelled and Failed are final.
- **Timings.** Each phase records its own start and end on the job's clock. They go to the job's `timings.json`, never into a hashed artifact.

The phase groups the cancellation tests cover:
- **source:** ValidatingRequest, GeneratingSource;
- **compilation:** Compiling, ValidatingData;
- **engine:** BuildingScene, RegisteringCampaign, ValidatingRuntime.

## Stages (what each phase runs)

| Phase | Stage | Promotes | Thread |
|---|---|---|---|
| ValidatingRequest | `RequestStage` | `worldbridge --new-request` (MMR-24): the canonical request from the choice, binding 9 policies, the producer lock and the attempt ceiling. `--seed-source random` (added here, only with `--seed`) records a seed the setup itself drew with Random World. The written request must match the choice. | worker |
| GeneratingSource | `SourceStage` | `worldbridge --generate` (MMR-22/24). The supervised producer runs in the bridge's own job. Quality attempts are retried with derived seeds, and the static travel gate runs per attempt (playability bound). Progress comes from the bridge's persisted attempt state: `completed` is the number of attempts rejected so far. | worker |
| Compiling | `CompileStage` | `worldbridge --bundle` (MMR-29): the immutable bundle under `SubModule.Store/bundles/<WorldContentId>`, or reuse of identical content already published | worker |
| ValidatingData | `DataStage` | `WorldDataBundle.Open` (MMR-29), with this host's game and War Sails versions (`WorldSetupHost.BundleHost`) | worker |
| BuildingScene | `SceneStage` | `RuntimeWorldBackend.Prepare` (MMR-37/38: surface model, campaign navmesh and its static plan, the pinned encoder), then `InstallFiles` (copy and hash into the scene store). Only `Register` (the engine's name pairing and resolution) runs on the engine thread. `Sweep` runs on the worker. | worker + one engine item |
| RegisteringCampaign | `CampaignStage` | Reports `registration_at_campaign_start` and does nothing. Under R07 (`docs/runtime-backend-decision.md`, "New game order"), campaign objects register inside the new game's `RegisterSubModuleObjects`, which is MMR-45's commit. Registering them earlier would break that ordering. | worker |
| ValidatingRuntime | `RuntimeStage` | `RuntimeWorldBackend.Construct` plus the first scene tick (torn down if the tick throws), then `NavigationValidator.Steps` (MMR-38's validator in steps: face table, face checks, each route) in engine items of about 25 ms each (`ValidationSliceMs`), then the inspection world's disposal. `NavigationGate.Persist`/`Require` run on the worker. | worker + engine items |
| ReadyForReview | (none) | The world waits for review: the setup builds its preview (`CoordinatorRunner.Preview`, `docs/world-preview.md`) and offers Start Campaign. | worker (preview) |
| Committing, Playing | `ICommitStage`: MMR-45's `ActivationCommitStage` (`docs/world-activation.md`) | `GenerationCoordinator.Commit` runs it from ReadyForReview only. Owned staging the commit did not take over is released before the job is Playing. The activation takes the bundle over from the job, because the save binding names it. Tested offline with scripted commit stages, and in game by `world_activation`. | worker + engine items |

**Choice and resume (MMR-47).** A job writes its choice (`choice.json`) when it starts. With `WorldChoice.ResumeJobRoot`, `Start` continues an unfinished job's directory under `jobs/`:
- the same id;
- a stale pending request and cancel file removed;
- the accepted `request.json` reused, after checking it against the current producer lock (another generator's is refused: `resume.other_generator`);
- `worldbridge --generate` resumed from the same `--state`.

`WorldRecoveryService` finds unfinished jobs and discards or cleans them (`docs/recovery.md`).

**Job inputs.** The job's policy inputs are embedded in the module and written into `<job>/inputs/` with the repository's layout (`JobInputs.Layout`), then read back byte for byte:
- 9 policies, the producer lock, the seed width, the runtime lock;
- the axis-sea-datum and naval-transitions calibration receipts.

The layout matters: the projection and playability policies name their sha256-pinned calibration receipts relative to themselves. The first in-game run, which wrote the inputs flat, failed every attempt as a parser failure (`compiler.missing_file`).

**Process ownership.** The bridge commands run as owned children of the game (`GenerationProbe.RunBridge`, a kill-on-close job object), and the bridge owns its producer child in its own job.
- **Generation:** `--generate` is stopped through its cancel file. The owned job ends it if it has not stopped after 10 s (`SourceStage.StopGraceMs`). In run 21316 it stopped through the cancel file (exit 4) without being terminated.
- **Bundle:** `--bundle` is ended at once.

**Time limits.** No limit ever kills a generation:
- The producer timeout `--generate` inherits from MMR-22 (600 s by default) is the only limit on generation.
- The coordinator's own waits are not measured and kill nothing: up to 60 s for an engine teardown item (`TeardownTimeoutSeconds`), and up to 120 s for a release in `CoordinatorRunner`.

## Progress events

Contract: `worldgen/contracts/progress-event.schema.json`. Each event has:
- `protocolVersion` 1 and `stateMachineVersion` 2 (`worldgen/contracts/progress-event.schema.json`);
- the job id (32 hex);
- a `sequence` starting at 0 and increasing by one per event of the job;
- `phase`;
- `completed`/`total` only when the size is known (the source generation's attempts);
- a lower_snake `messageCode`;
- `seconds` since the job started;
- on failure, a structured `error` (MMR-47 category, code, retryable). The detail stays in the job's record.

Events are also appended to `<job>/events.jsonl`.

A consumer follows one job with a `ProgressSink`. It drops any event of another job (stale) and any event not newer than the last it accepted (out of order). `Start`'s created callback lets the consumer follow the job before its first event. The setup model drains its event queue on the engine thread through its sink.

The setup shows the phases as player text (`StageText`), for example:
- "Shaping the globe: land, seas, climate and peoples (attempt 1 of 3)...";
- "Building the campaign data: terrain, settlements and routes...";
- "Building the world map...";
- "Lords, towns and parties are created when the campaign starts.";
- "Checking travel by land and sea...".

## Engine-thread work and stale callbacks

- **Running engine work.** Engine mutations run as items of the `EngineWorkScheduler`, pumped on the engine thread every application tick.
  - Each pump has a budget of 8 ms; at least one item runs per pump.
  - The queue holds at most 256 items.
  - Each item is one bounded native step: register, construct, about 25 ms of validation, dispose. In the final run (21316) the longest was construct, at 0.16 s.
  - **Frame gaps.** The probes fail a run whose largest frame gap reaches 1 s (a tripwire). The summaries check the accepted runs at 0.5 s: the largest measured was 0.41 s, across all six scenarios.
  - The job's worker waits for its items; the engine thread never waits.
- **Job tickets.** Every item carries its job's ticket. The ticket stops being live when its job is cancelled or a later job begins.
  - A queued item of a cancelled job is withdrawn by its worker or dropped unrun by the pump.
  - An item already running finishes. What it made is torn down on the engine thread (its discard action) before the job stops.
  - Teardown items carry no ticket and always run.
- **World generations.** The job begins a world generation (`WorldGenerations.Begin`) for the scene it builds. `RuntimeWorldBackend` refuses a superseded generation, so a late callback cannot attach old resources to a later world (MMR-37).
- **Thread affinity.**
  - **Enforcement:** `EngineThread.Require` in every native step, and the scheduler's `Pump` runs nothing off the engine thread.
  - **Counters:** the job record also counts stages run on the engine thread and engine items run off it. These are tripwires: by construction (worker tasks, the pump's guard) they stay zero, and they were zero in every run.
- **Probe hold.** `EngineWorkScheduler.Held` (probes only) keeps job items queued so a probe can cancel with one provably queued; teardown items still run.

## Cancellation and ownership

Each stage records what it creates, together with its release:
- every directory the bundle run adds under the bundle root, until it knows the outcome (staging, or a publish the cancellation landed after);
- a bundle this job published (not one it reused);
- the surface staging;
- the world generation;
- the inspection world.

On cancellation or failure these are released in reverse order, each once, and nothing else is touched. The job directory (logs, events, timings) stays until the job is released.

**Release** (`GenerationCoordinator.Release`):
- It cancels an unfinished job, then waits until the job's completion task stops changing: the worker, a review cancellation, or a commit.
- It then deletes the job directory, retrying briefly while an exiting child holds a file.
- Releasing twice gives the same release.

The record, timings and events are deleted with the directory; durable logs are MMR-47's.

**One job at a time.**
- `Start` refuses (`GenerationBusyException`) while another job is unfinished or still being released.
- `CoordinatorRunner` waits for a release in progress (`AwaitIdle`) before starting. A remaining refusal reaches the player as "An earlier world is still being finished. Try again in a moment." (interrupted generation, `generation.busy`).
- The setup's menu entry is disabled while a job is unfinished and no setup screen is open.
- The setup releases the previous job before a new Generate and releases the last job when the screen closes; Back while generating cancels.

**Files a cancelled job leaves** (none of them are processes or staging):
- **Scene store:** after BuildingScene's copy, the content-addressed scene folder (about 7 MB) stays in the scene store (MMR-37: reused by name; an install of another world removes it unless this process used it).
- **Navigation report:** after ValidatingRuntime, the persisted navigation report for that scene stays (`world-navigation/<scene>.json`).

**Forward risk for MMR-45/49.** Every install sweeps other worlds' scene folders this process has not used, so loading a saved generated world must re-install its scene from its bundle.

## Evidence

**Offline:** `tools/Test-GenerationJob.ps1` runs 17 checks and 2 harness checks (`evidence/generation/job-state-machine-tests.json`, `.txt`). They cover:
- the state machine and phase groups;
- the sink;
- the scheduler: the engine-thread guard, the per-tick budget, withdrawal, stale tickets, capacity, the probes' hold;
- the full pipeline over scripted stages, with schema-valid events and no sequence gaps;
- cancellation in each phase group, with reverse-order release;
- engine-side cancellation, both queued and running;
- stale callbacks against a later job;
- failures;
- commit and recovery;
- one job at a time across releases, and a release during a commit;
- the `--seed-source` CLI rule;
- the job inputs embedded from the right repository files.

**In game, accepted run `generation_job` 21316.** `evidence/generation/generation-job.json` recomputes 17 checks from it.
- **Full pipeline.** Seed 42 reached review in 28.3 s, with a largest frame gap of 0.27 s and a longest engine item of 0.16 s. Phase times:

  | Phase | Time |
  |---|---|
  | Request | 0.2 s |
  | Source | 10.8 s |
  | Bundle | 8.0 s |
  | Verification | 0.3 s |
  | Scene | 8.0 s |
  | Runtime navigation | 1.0 s |

  The bundle's WorldContentId is `f596b58c…`, identical to research bundle A.
- **Cancellation during source generation:** Cancelled; the bridge stopped through its cancel file (exit 4); the request recorded `seedSource: random`.
- **Cancellation while the bundle was being staged:** Cancelled once `.staging-38282e…` appeared; its staging was removed and nothing else in the bundle root changed.
- **Cancellation with an engine item queued:** the item is the scene registration (the copy runs on the worker since the review fixes; the scenario's label still says "scene install"). The item was dropped unrun.
- **Cancellation after the inspection world was constructed:** the world was torn down on the engine thread.
- **Killed producer:** Failed as a bad source (`generate.crashed`).
- **Late callback of a cancelled job:** its engine item was dropped unrun, and its late progress event, delivered to the later job's consumer, was dropped as stale.
- **After each scenario:** no generator process left, the job's own files released, the menu usable, saves unchanged.

**Earlier runs, for the record:**
- **30220:** failed at record writing.
- **27272:** passed, but validation was a single item of 0.76 s.
- **27284 and 29872:** passed on interim fix builds.

**In game, `world_setup` 13032:** the setup screen ran the same job, with Confirm to result in 28.1 s, a 0.19 s largest frame gap, and both jobs released (`evidence/runtime/world-setup.json`).

## Limits

- **Registration, commit and play** are MMR-45's activation (`docs/world-activation.md`). It keeps R07's order: register inside the new game, then load the map.
- **The preview and its Start Campaign action** are MMR-46 (`docs/world-preview.md`).
- **Bundles and scenes.**
  - A bundle the job published is deleted when the job is released without commit; a reused bundle is left alone.
  - Retention of committed worlds is MMR-47/MMR-48.
  - The scene store and navigation report leftovers are listed above.
- **Crash testing.** The producer crash test kills the fwg process this job started (by start time); it is not a natural crash. The bridge classifies the result as a mechanical failure and never retries it (MMR-24).
- **Waiting time.** Generation to review and, since MMR-56, the first campaign start, from measured runs on this machine (`one-shot-budget.json`, provisional; `docs/first-test-profile.md`). A run longer than the stated time fails the setup probe and adds its measurement.
