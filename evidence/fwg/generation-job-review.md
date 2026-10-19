# MMR-44 independent review and disposition

**Reviewer.** One independent agent. It made no repository edits and launched no game.
- It re-ran `tools/Test-GenerationJob.ps1` (16/16 at the time, plus the CLI and embedded-inputs checks) and `tools/Test-WorldSetup.ps1` (20/20).
- It checked by reflection that the deployed coordinator IL matched the source.
- It read in-game runs 30220, 27272 and 41228.

**Verdict.**
- No code-level blocker: no deadlock, no deletion of anything the job does not own, no orphan processes.
- One blocker on evidence: the setup screen's use of the job had no passing in-game run on the current tree.
- 9 should-fix findings and 13 nits.
- Per criterion: c1 partially met, c2 met at the coordinator level but not through the setup screen, c3 met.
- Scope decisions (a) to (e) all accepted, (b) after fixing S3.

All findings were fixed in one batch (except where noted), followed by confirming runs (bottom). Each finding below is the reviewer's text, condensed, followed by what was done.

## Findings and disposition

**B1. No passing in-game run of the setup screen on this tree.**

Fixed:
- `world_setup` run 13032 on the final tree passed: Confirm to result in 28.1 s, a 0.19 s largest frame gap, both jobs released, cancel interrupted with 0 generator processes. `evidence/runtime/world-setup.json` recomputes 19/19 from it.
- `docs/world-setup.md` names it, and lists runs 28596 and earlier as MMR-43's interim job.

**S1. Reopening the screen quickly gives a false "bad source" error, because the next job starts during the previous job's release.**

Fixed:
- `GenerationJob.Released` records a release in progress.
- `Start` refuses with a typed `GenerationBusyException` while a job is unfinished or still being released.
- `CoordinatorRunner` waits for the release (`AwaitIdle`) before starting. A remaining refusal reaches the player as "An earlier world is still being finished. Try again in a moment." (interrupted generation, `generation.busy`).
- The menu entry is disabled while a job is unfinished and no setup screen is open.
- Offline: a new job is busy during a release and starts after `AwaitIdle`. In game (13032), the second Generate starts after the first job's release.

**S2. A cancellation at the end of compilation could leave a published bundle nobody owns.**

Fixed: the compile stage owns every directory its run adds under the bundle root (staging, or a publish the cancellation landed after) until it knows the outcome. On success it hands over to the published bundle (owned) or to the reused one (not owned).

**S3. `Completion` swaps raced with Release, and Commit released after Playing.**

Fixed:
- `Cancel` (at review) and `Commit` swap `Completion` under the job's lock.
- `Release` waits until `Completion` stops changing; releasing twice returns the same release.
- `Commit` releases leftover staging and ends the ticket before Playing.
- Offline: a release during a commit waits for Playing before deleting the job.

**S4. Engine-thread work was not bounded in time (validation 0.76 s as one item; Install's file work on the engine thread), and the probe threshold was loosened after a failure.**

Fixed:
- `NavigationValidator.Steps` runs MMR-38's validator in steps. `Run` drains them, so the report and its hash are unchanged.
- The job takes steps in engine items of about 25 ms each.
- `RuntimeWorldBackend.Install` is split: `InstallFiles` (copy and hash) and `Sweep` run on the worker, and only `Register` (name pairing and resolution) runs on the engine thread. `Install` keeps its behaviour for the MMR-37/38 probes.
- Run 21316: longest engine item 0.16 s (construct); largest frame gap 0.27 s in the full pipeline, at most 0.41 s in any scenario.
- The probes' 1 s check stays as a tripwire. The summaries now check the accepted runs at 0.5 s and pass.
- **Interim cost found on the way:** one engine item per route meant 835 round trips and lifted the pipeline to 32–33.6 s (runs 27284 and 28788). The 25 ms slicing brought it back to 28–29 s.

**S5. Attempt progress was mislabelled.**

Fixed: `completed` counts only rejected attempts. `source_accepted` reports the attempts rejected before the accepted one, so an acceptance on the first attempt reads "attempt 1 of 3".

**S6. The inspection world leaked if the first scene tick threw.** Fixed: the world is disposed and the exception rethrown inside the engine item.

**S7. The in-game compilation cancel did not test staging cleanup.**

Fixed:
- The cancel now triggers when the run's own `.staging-*` directory appears. Run 21316 saw `.staging-38282e…`.
- The probe asserts that nothing was added to, or removed from, the bundle root.

**S8. Doc claims the code or evidence did not support.**

Fixed:
- `evidence/generation/generation-job.json` now exists (17/17, run 21316).
- Check counts corrected (17 offline; 74 localisation ids).
- The phase table comes from the accepted run.
- The coordinator's own waits (60 s teardown, 120 s release) are named; neither kills a generation.
- `docs/world-setup.md` describes the coordinator's release, not the R01 heightfield markers.
- The probe comments no longer say "interim job".

**S9. The setup timing check is fragile (stated 30 s against measurements up to 28.6 s).**

Handled by the budget's own rule, now written in it: "a run that takes longer adds its measurement".
- Run 26012 measured 30.4 s on the stepwise build. It was added (with 29.4 s from run 29872), and the stated time is now 35 s.
- Runs 27284 and 28788 (the per-route interim build) are excluded and named in the budget's source.
- The final run took 28.1 s.

**Nits.**

| Nit | Disposition |
|---|---|
| N1. First progress events could be dropped. | Fixed: `Start` takes a created callback that runs before the job's first event; the setup follows the job there. |
| N2. Engine-phase cancels leave files. | Documented: the scene store folder (store-owned, swept by another world's install) and the persisted navigation report. |
| N3. The affinity counters cannot fail. | Documented as tripwires; enforcement is `EngineThread.Require` and the pump's guard. A new offline check shows the guard: a pump from another thread runs nothing, is counted, and the item waits for the engine thread. |
| N4. Offline tests could flake. | Fixed: they wait until the job's completion has run (`Settle`). |
| N5. The scripted runner did not enforce one job at a time. | Fixed: it refuses a second job until the first is released, like the coordinator. |
| N6. The record is deleted on release. | Documented; durable logs are MMR-47's. |
| N7. Deletion was not retried. | Fixed: up to 10 attempts, 200 ms apart. |
| N8. Token callbacks ran under the job lock. | Fixed: `Cancel` sets the flag under the lock and runs `CancelSource.Cancel()` outside it. Transitions check the flag. |
| N9. Per-item wait handles are not disposed. | Not changed: a task's wait handle is released with the task. |
| N10. Two engine queues. | Documented. |
| N11. The probe killed fwg by name and could leave the hold set. | Fixed: it kills only fwg processes started since the crash job began, and `Run`'s finally lifts the hold. |
| N12. Stale-progress dropping was evidenced offline only. | Fixed: the cancelled job's late event is delivered to the later job's consumer and dropped as stale (run 21316). |
| N13. The source cancel did not show how the bridge stopped. | Fixed: the bridge's stop is recorded. Run 21316: stop requested at 2.1 s, exit 4, not terminated. |

## Found while confirming

**The first in-game run (30220) failed every source attempt as a parser failure.** The job wrote its policy inputs flat, so the projection policy's relative calibration receipt was missing.
- **Fix:** `JobInputs.Layout` keeps the repository's layout and embeds the axis-sea-datum calibration receipt.
- **Verified offline** with a store-like job directory as the working directory: `--generate` accepted in 10.1 s, and `--bundle` published `f596b58c…`, identical to research bundle A.

**Run 30220's full pipeline reached review, then failed while writing its record:** JavaScriptSerializer met a `Dictionary<int,int>` artifact.
- **Fix:** artifacts are copied as plain JSON values, and a record problem can no longer fail a job.

## Confirming runs

| Run | Result |
|---|---|
| `tools/Test-GenerationJob.ps1` | 17/17 + CLI + embedded inputs (`evidence/generation/job-state-machine-tests.json`, `.txt`) |
| `tools/Test-WorldSetup.ps1` | 20/20 (`evidence/runtime/world-setup-tests.json`, `.txt`) |
| `generation_job` 21316 | ok, final tree; `tools/summarize_generation_job.py` 17/17 → `evidence/generation/generation-job.json` |
| `world_setup` 13032 | ok, final tree; `tools/summarize_world_setup.py` 19/19 → `evidence/runtime/world-setup.json` |
