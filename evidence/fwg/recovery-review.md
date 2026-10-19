# MMR-47 independent review and fixes

One independent review of the MMR-47 working tree. The reviewer was an agent with read-only access: it ran the offline harness and
`Inspect-World.ps1`, and confirmed its repro claims with a throwaway program, but edited no files and launched no game. It reported 1
blocker, 4 major and 8 minor findings. All were fixed in one batch. The offline harnesses then ran (all 17 green), followed by one
in-game confirming batch on one build: `world_recovery` crash 28280 and resume 47264, then `world_setup` 41480. Every run passed.

Before the review, the author found and fixed one more issue: the setup could have listed or discarded a job of another game process
holding the store lock. Unfinished and Discard are now gated on the lock.

## Blocker

1. **An empty `choice.json` or `timings.json` threw out of `WorldRecoveryService.Jobs`** (a null dereference). That would stop the setup
   from opening and abort the startup cleanup. Fixed in four places:
   - parsing is null-safe (`as IDictionary`), and a torn last event falls back to the record;
   - each job is classified inside its own try/catch, and an unreadable job becomes `Unreadable`, offered for Discard only;
   - `choice.json` and `timings.json` are written atomically (`DurableFile`);
   - the setup model catches anything while listing.

   Tested offline with empty, torn and held files.

## Major

2. **A job that crashed while committing was offered as resumable.** The record is written only at review, so it still said review.
   Fixed: the phase is now the last event's (events are appended for every event, the commit's included), else the record's. Confirmed on
   the real store: `8be23b37…` is now "stopped while starting its campaign", offered for Discard only. The test fixture now matches
   production: a review record with a committing last event.
3. **The inspector called a present but corrupt bundle "missing" on save and activation rows.** Fixed: bundles are looked up by content
   id, else by their directory name. Tested: one damaged byte makes the save and the activation say corrupt, and the save not loadable.
4. **A full disk or changed policies from the worldbridge got the wrong category.** Fixed:
   - `GenerationErrors.FromBridge` classifies the worldbridge in one pure place: disk-full text (not enough space, error 112, ENOSPC)
     gives DiskFull at any exit, and `policy_changed`, `state_request_mismatch` and `frozen_world_mismatch` give IncompatibleBuild;
   - `--new-request` and `--bundle` failures check disk-full text too;
   - unfinished jobs compare their request's policy hashes with the module's (`JobInputs.PolicyMismatch`, giving `OtherPolicies`, Discard
     only).

   Tested offline with a 12-row classification table and the failure matrix (14 cases).
5. **Host problems never reached the new error panel**, because the menu entry was disabled. Fixed: a host problem no longer disables the
   entry. The setup opens refusing to generate and shows each problem with its action and reference. In game, `world_setup` 41480 shows
   the missing War Sails problem with its reference and refuses to generate.

## Minor

6. **Wording.** Every non-resumable job said "another version of the world generator", and jobs with no choice showed "seed 0". Fixed:
   one sentence per kind (other generator or policies, committing, unreadable), with no seed when none was read. Tested.
7. **Fallback.** A torn `choice.json` did not fall back to `request.json`. Fixed and tested.
8. **Schema and capability refusals.** `DataStage` sent them to BadSource. Fixed: they are IncompatibleBuild (`FromBundleProblems`). The
   IncompatibleBuild action no longer says "named above".
9. **Diagnostics.** Fixed in four ways:
   - an undeletable old record is skipped and never fails a new one (`PruneFailures`);
   - a successful retain clears `RetainProblem`;
   - nothing is written under an unverified root (`SaveBindingRuntime.RootProblem` is passed in) or when `diagnostics/` is a link;
   - pruning removes links as links.

   Tested.
10. **A refused Discard** showed only in Diagnostics. Fixed: the notice says it could not be discarded and why. Tested.
11. **Resume losing progress.** A continued job whose start was refused (busy) is now offered again, and that is tested. By design, a
    continued job that then fails or is cancelled ends like any job: its record is final, and it is released on the next Generate. This is
    documented in `docs/recovery.md`.
12. **Checks that passed vacuously.** Fixed:
    - the probe now asserts that the interrupted attempt ran again (attempt 0 run 2, accepted) and that the world is the seed's own;
    - the release check is now meaningful: the reviewed world is released by the next Generate, the cancelled job on close;
    - the failure matrix and the classification table now go through the stage classifiers (`FromBridge`, `FromBundleProblems`).
13. **The inspect program's exit.** It computed a result it never used. Removed: the program exits 0 when the inspection ran.

## Found safe by the reviewer

- Cleanup never targets bundles, packages, activations or saves.
- "Leftover" applies only with no parsable choice or request and no record or events.
- Discard stays within `jobs/<32 hex>`, refuses links, live jobs and an unverified root, and removes links as links.
- Resume validates the directory.
- Unfinished, Discard and Cleanup are all gated on the store lock.
- The request check compares the producer lock, and the bridge refuses another request's attempt state.
- Save headers are capped and parsed defensively.
- No threading problem was found.
