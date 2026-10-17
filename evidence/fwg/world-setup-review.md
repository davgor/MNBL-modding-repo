# MMR-43 independent review and disposition

**Reviewer.** One independent agent. It made no repository edits and launched no game. It re-ran `tools/Test-WorldSetup.ps1` (16/16 at the time), dumped installed IL, timed the world.json parse offline, and read the in-game runs 21304 and 31804.

**Verdict.**
- One BLOCKER, nine SHOULD-FIX findings and nine NITs.
- Per criterion: c1, c2 and c3 each partially met.
- Checked and found fine: seed parsing against worldbridge's `decimal()`, Random World uniformity, the volatile/engine-thread split, focus release on pop, the `RealText` binding and navigation targeters (as in vanilla `TextQueryPopup`), the v1.4.8 key bindings, the two new API pins, the Deploy-Dev manifest indices, and `producer-controls.json` byte-identical to its source.

All findings were fixed in one batch, followed by one confirming batch of runs (bottom). Each finding below is the reviewer's text, condensed, followed by what was done.

## Findings and disposition

**B1. BLOCKER: world.json (82.8 MB) was parsed on the engine thread at the end of every generation, freezing the game for 6–11 s. The "measured" wait values were mostly that freeze.**

The reviewer derived the freeze from job timestamps: the job directory to `result.json` took 13.8 and 15.4 s, while the probe recorded 20.1 and 26.7 s. I re-measured from the job directories: 13.75 s and 15.37 s. Fixed:
- The world summary is computed on the worker, inside the job task, after the runner returns. It uses a one-pass reader (`WorldJsonScan`) that keeps only `civilizations.entities[].{id,name}` and `humans.cores[].population_profile` and skips everything else without building it.
- Offline, it equals the whole-document reading on both bundles. It ignores nested look-alikes, decodes escapes and refuses truncation.
- The probe's own independent re-reading now runs on a worker thread.
- The probe measures the engine thread's largest frame gap while generating and fails above 0.5 s. Run 28596 measured 0.022 s.
- The budget's 20.1 and 26.7 s are removed. It now holds the job spans 13.75 and 15.37 s (plus MMR-21's 11.2 and 12.7 s), so the screen states "about 20 seconds". Run 28596 generated in 15.9 s.

**S1. SHOULD-FIX: the probe reported `status: ok` while its failures list was not empty (run 31804).**

Fixed: `WorldSetupProbe` throws when any expectation failed, as `GeneratedCampaignBootProbe` does.

**S2. SHOULD-FIX: `WorldChoice` was not a valid GenerationRequest despite its comment.**

The problems: seedSource `random-world` is not in the schema's enum, `profile` is an extra field, and `producerLockSha256` carried the profile's `ad8138…` (sha256 of `tools.lock.json`) instead of sha256(`worldgen/producer.lock.json`) = `3e9640…`. worldbridge would refuse with `producer_changed`. Fixed:
- seedSource is `random`.
- `RequestFields()` returns only request properties.
- The producer lock is computed from the now-embedded `worldgen/producer.lock.json`. The contract refuses a seed width verified for another producer than the locked one.
- The profile's field is kept as `ProfileToolsLockSha256` and labelled "profile tools lock" in diagnostics.
- An offline test checks every field against `generation-request.schema.json` (property, enum, const, pattern, minimum, maximum). The schema's required fields not supplied are exactly `schema`, `version` and `policies`, which MMR-44 adds.

**S3. SHOULD-FIX: the start-campaign label ("about 85 seconds") came from non-periodic fixture worlds, for an action this build does not offer.**

Fixed: the phase is removed from the budget, and the text now states only the generation time. MMR-56 adds the start phase with MMR-46's action, measured on F03 worlds.

**S4. SHOULD-FIX: editing an embedded setup document did not make the build receipt stale.**

Fixed: `Get-MnblSourceInputs` (`tools/Test-ApiPins.ps1`) adds every `EmbeddedResource` of the csproj, and refuses one outside the repository. `evidence/runtime/build-api-inputs.json` now lists the budget, presentation policy and producer lock, as well as the MMR-36 and MMR-38 resources.

**S5. SHOULD-FIX: the docs cited a missing `evidence/runtime/world-setup.json` and overstated run 21304.**

Fixed:
- `tools/summarize_world_setup.py` writes `evidence/runtime/world-setup.json`, recomputing 19 checks from run 28596 (final tree). Earlier runs are listed with their status.
- `docs/world-setup.md` describes 21304 (first build, 15 s text, no timing check) and 31804 (failed its timing check) as they were.

**S6. SHOULD-FIX: each Generate left about 166 MB (about 900 files) under `jobs`, with nothing removing it.**

Fixed:
- The setup keeps one job. It releases the previous job when a new one starts, and the last when the screen closes (a running job once it has stopped).
- `InterimGenerationRunner.Release` deletes only a directory directly under the store's `jobs` directory, plus the heightfield marker that job published or left pending. It runs off the engine thread (`WorldSetupModel.Releasing`).
- Offline: the scripted runner's release order. In game: both setup jobs released and `setupJobsLeft` = 0.
- Developer generations and the earlier runs' job directories are not touched.

**S7. SHOULD-FIX: developer details remained in the ordinary flow.**

The details were the seven "FWGC: …" main-menu entries, the raw producer code in the refused-recipe message, and executable names in tool messages. Fixed:
- The developer entries are registered only under a harness probe or with a `developer` marker file in the store. The `generation_menu` regression (run 26188) still drives them.
- Producer codes and executable names moved to Detail. The offline test checks that no message carries them.

**S8. SHOULD-FIX: keyboard navigation reached only Generate and Back.**

Partly fixed:
- The engine's `Randomize` hotkey (R, the top face button) draws a Random World seed and shows its glyph. It is ignored while the seed box has keyboard focus (`GauntletLayer.IsFocusedOnInput`), so typing an R never randomizes. Tested offline and in game (the typing case refused, the key accepted).
- **Not done:** auto-focusing the seed box, and a key for Diagnostics, which the mouse reaches. Both are recorded in the doc's input section.

**S9. SHOULD-FIX: `Refresh()` rebuilt every text, and serialised the choice, every frame.**

Fixed: the model keeps a revision counter, and the view model rebuilds only when it changes or diagnostics are toggled. The diagnostics text is built only while shown.

**N1. NIT: a cancel that surfaced as another exception was classified by that exception; a cancel after the last stage still ended Generated.**

Fixed: once the player asked to cancel, the outcome is interrupted generation. Offline: a runner that throws `TimeoutException` after a cancel.

**N2. NIT: host problems showed only the first problem's action.** Fixed: every message is shown, and each distinct action once (offline).

**N3. NIT: a seed width above 2^32 − 1 was accepted.** Fixed: refused (offline).

**N4. NIT: unguarded file IO ran in the main-menu entry callback.** Fixed: an IO or access error is reported as a tool problem.

**N5. NIT: the setup entry stayed enabled while another harness probe ran.** Fixed: it is disabled while a harness probe other than `world_setup` runs. `AutorunHost.RunningProbe` was added for this.

**N6. NIT: Deploy-Dev never removed stale prefabs.** Fixed: installed GUI files that are not in the manifest are removed and listed in the receipt (`removedStale`). `tools/Test-Deployment.ps1` passes.

**N7. NIT: the edit box had no `MaxLength`.** Fixed: `MaxLength="20"`, which still lets an oversized seed show its reason. Checked offline.

**N8. NIT: the diagnostics-visible layout was never captured.** Fixed: a diagnostics screenshot was added. It showed line feeds drawn as box glyphs, so lines are now joined with " | ". Run 28596 shows the text fitting inside the panel.

**N9. NIT: the localisation check missed the screen's busy text.** Fixed: the text moved to `WorldSetupText.Busy` and is covered (67 ids).

**c1 verdict, partially met: "the 10 peoples shown are not what character creation lists".**

Fixed: each people is shown with the official culture it presents through (`culture-presentations.json`, the compiler's own policy). The screen also lists the cultures character creation offers.
- Offline, these sets equal the compiled campaigns' town and kingdom presentations on both bundles: A has 7 cultures over 45 cities, B has 6 over 66.
- The reviewer's other c1 points stand as recorded scope:
  - The probe's no-game check is a guard, not proof of the creation ordering.
  - The hand-off to `GeneratedGameManager`, and so the creation ordering in this flow, is MMR-45 and MMR-46. Building it here would bypass MMR-45's activation transaction.
- The decision comment on MMR-43 records this for the acceptor.

## Found while confirming

**Harness: process id reuse.**
- **Symptom:** a confirming launch (PID 17968) never ran. `AutorunHost.TryClaim` could not move `request.json` onto a `request.17968.taken.json` that a 14 October run with the same process id had left, so the game idled at the main menu.
- **Recovery:** I stopped my own idle process and set the unclaimed request aside (`request.json.unclaimed-pid-reuse`). The next start's Safe Mode prompt was answered No by the watcher.
- **Fix:** `TryClaim` now renames an earlier run's `request.<pid>.taken.json`, `progress-<pid>.log` and `result-<pid>.json` to `*.stale-<utc>` before claiming. Nothing is deleted.

## Confirming runs

| Run | Result |
|---|---|
| `tools/Test-WorldSetup.ps1` | 20/20 (`evidence/runtime/world-setup-tests.json`, `.txt`) |
| `world_setup` 26092 | ok (review-fix build, before the diagnostics separator) |
| `world_setup` 28596 | ok, final tree; `tools/summarize_world_setup.py` 19/19 → `evidence/runtime/world-setup.json` |
| `generation_menu` 26188 | ok: the developer entries under the harness, all cases, menu recovery |
| `tools/Test-Deployment.ps1` | PASS (GUI files counted; stale removal in the receipt) |
