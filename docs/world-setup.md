# New Campaign setup screen (MMR-43)

The player's way into a procedural world: a main-menu entry, **New Procedural Campaign**, opens a Gauntlet screen where the player picks a seed (or Random World) and generates the world. It is the first-test entry the product contract fixes: the F03 one-shot profile with the Seed, Random World and Generate actions.

| Part | Where |
|---|---|
| Logic (pure managed, tested offline) | `src/MNBL.ProceduralWorld/UI/WorldSetupModel.cs` |
| Error categories (MMR-47's vocabulary) | `src/MNBL.ProceduralWorld/Generation/GenerationErrors.cs` |
| View model | `src/MNBL.ProceduralWorld/UI/WorldSetupViewModel.cs` |
| Screen, menu entry, host checks, the generation job runner (`CoordinatorRunner`) | `src/MNBL.ProceduralWorld/UI/WorldSetupScreen.cs` |
| Prefab | `Modules/MNBL.ProceduralWorld/GUI/Prefabs/WorldSetup.xml` (deployed by `tools/Deploy-Dev.ps1`, which also removes stale installed prefabs; packaged by `tools/Package-Probe.ps1`) |
| Inputs (embedded in the module; build inputs via `tools/Test-ApiPins.ps1`) | `worldgen/profiles/one-shot.json`, `worldgen/contracts/producer-controls.json`, `worldgen/contracts/producer-seed-width.json`, `worldgen/producer.lock.json`, `worldgen/policies/product-policy.json`, `worldgen/policies/culture-presentations.json`, `worldgen/policies/one-shot-budget.json` |
| Offline tests | `tests/UI/WorldSetupTests.cs`, run by `tools/Test-WorldSetup.ps1 -BundleA <bundle> -BundleB <bundle>` |
| In-game probe | `world_setup` (`src/MNBL.ProceduralWorld/Automation/WorldSetupProbe.cs`; args `seed`, `cancelSeed`). Any failed expectation fails the run |
| Evidence summary | `tools/summarize_world_setup.py` writes `evidence/runtime/world-setup.json` |

## Entry and lifecycle

- **The entry.**
  - `WorldSetupScreen.Register()` runs from `SubModule.OnBeforeInitialModuleScreenSetAsRoot`. It adds the initial-state option `MNBLProceduralCampaign` at order 4, directly after Sandbox (vanilla order: Saved Games 0, Continue 1, New Campaign 2, SandBox 3).
  - This is the extension point R01 used for its developer entries: an `InitialStateOption`, executed by `Module.ExecuteInitialStateOptionWithId`.
  - It is the module's only main-menu entry. The developer "FWGC: …" entries, and the `generation_menu` probe that drove them, were removed on 2026-10-18; `world_setup` checks that no `fwgc_` entry is registered.
- **Disabled with a reason** in two cases (MMR-47: a host problem no longer disables it; the setup opens refusing to generate and shows each problem with its action and reference, see Errors below):
  - a generation job is unfinished or still being released;
  - a harness probe other than `world_setup`, `new_campaign_flow` or `world_recovery` is measuring.
- **Lifecycle stage.**
  - The setup and its generation run on the main menu, before any `Game` exists. Nothing is registered with the engine; generation produces files only.
  - The world's cultures are registered when its campaign starts. `GeneratedGameManager` registers the world's objects before initialization (R04), and `GeneratedCharacterCreation` restricts character creation to the cultures the world presents through (MMR-32, `docs/heroes.md`).
  - That hand-off is MMR-45's activation, started by MMR-46's one Start Campaign action after the preview (`docs/world-preview.md`).
  - The probe records that no game exists while the screen is open or after generation. This is a guard against a future setup creating one, not a proof of the creation ordering.
- **What the player sees of the world.** After generation, the screen shows three things:
  - The generated world's own peoples. These are the `civilizations.entities` names of its cities' population profiles (`humans.cores`), which are civilization ids.
  - The official culture each people is shown through, from `culture-presentations.json`. That policy is the one the campaign compiler applies.
  - The cultures character creation offers for this world, which is the set of those presentations.

  These come from the source world plus the policy, not from the compiled campaign (the screen reads the bundle's source split). On both tested bundles (45 and 66 cities), the set equals the presentations of the compiled campaign's towns and kingdoms (offline test). A world whose compiler excluded every city of one civilization could differ. MMR-46's preview is built from the compiled world (its counts, settlements and kingdoms), and in game the probe checks the cultures character creation offers against the activation's compiled dataset.

## The contract the screen shows

`WorldSetupContract.FromDocuments` reads the seven documents. It refuses (`InvalidDataException`) anything the screen could not state truthfully:

- **Profile.** Recipe 3, size 129, phase 16, default seed 42, 3 quality attempts, full-globe coverage.
- **Overrides.** Only controls the pinned producer publishes (`producer-controls.json`, a verbatim copy of the probe-controls contract), each within its published range (size 3..1025, phase 1..16).
- **Quality attempts.** At most the product policy's maximum (3).
- **Coverage.** Must equal the product policy's coverage.
- **Seed width.**
  - It must be verified (maximum 4294967295; the producer refuses 4294967296, `Test-GenerationIdentity.ps1`) and fit the request's 32-bit seed.
  - It must have been verified for the producer that `worldgen/producer.lock.json` locks.
- **Presentation cultures.** Every civilization must be shown through an official culture, and every official culture needs a setup name.
- **Producer lock.** The request's `producerLockSha256` is sha256(`worldgen/producer.lock.json`), `3e9640…`. The profile's own `producerLockSha256` field (`ad8138…`) is sha256(`tools.lock.json`): the tools lock the profile was resolved under. It appears in diagnostics as "profile tools lock" and is never sent as the request's lock.

The screen shows:

- **Profile and coverage** as fixed text. The whole globe is generated and shown as one wrapping map; there is no region to choose.
- **Seed** as exact text, under the worldbridge rule (`generation_driver.cpp` `decimal()`):
  - digits 0-9 only: no sign, space, separator, exponent or non-ASCII digit;
  - no leading zero except "0";
  - at most the verified maximum.

  Each refusal has its own reason and blocks Generate. The edit box takes at most 20 characters, so an oversized seed still shows its reason. The same seed always makes the same world, and replay is allowed.
- **Random World** draws uniformly from `[0, maxSeed]` with the operating system's cryptographic generator (rejection sampling). No account or network is involved.
  - The choice records `seedSource: "random"` (the request schema's enum). Typing over it makes it `"explicit"` again.
  - The **Randomize** key also triggers it: R, or the controller's top face button. The key is ignored while the seed box has keyboard focus, so typing an R never randomizes.
- **Settings.** Only the controls the profile fixes, each locked with why:
  - **World size 129:** "a different size makes a different world, with more or fewer settlements, not the same world in more detail". FWGC++ size changes content, so it is never presented as quality.
  - **Generation stages 16 of 16:** a campaign needs the complete world.

  No other producer option appears, working or otherwise.
- **Waiting time.** Before MMR-56: "Generating the world takes about 35 seconds", the budget's largest generation measurement rounded up to 5 s. Without a measurement the text is hidden. MMR-56 measured the F03 profile end to end (`docs/first-test-profile.md`), and the screen now reads: "Generating the world takes about 60 seconds. The first start of its campaign takes about 7 minutes more while the world's navigation is prepared; the game may stop responding until it is done." The first start is stated in whole minutes, rounded up, once the budget holds a `startCampaign` measurement.

  **MMR-282 (RT-38):** one time for every world was wrong: seed 73's world waits about 14 minutes. The setup now says only "The first start of its campaign takes longer while the world's navigation is prepared; the preview says how long for that world." The preview's counts line adds this world's own wait (`mnbl_ws_wait_navigation`): about `MINUTES` minutes for its `SETTLEMENTS` towns, castles and villages, from the budget's `navigationCacheBuild` model (see [world-loading.md](world-loading.md), "Navigation rebuild warning"). Without the model nothing is promised.
  - **Measurements:** MMR-44's whole generation job, from job start or Confirm to the world ready for review: 28.6, 27.2 and 29.4 s (`generation_job` runs 30220, 27272 and 29872), and 28.3 and 30.4 s (`world_setup` runs 41228 and 26012). Run 26012 exceeded the 30 s then stated, so its measurement was added. The earlier 11.2-15.4 s measured the R01 job, which the setup no longer runs.
  - **Not used:** those runs' own Confirm-to-result times (20.1 and 26.7 s). They included a world.json parse on the engine thread, which the review found and the screen no longer does.
  - **Run 13032 (MMR-44 build):** Confirm to result took 28.1 s, with a largest frame gap of 0.19 s.
  - **Campaign-start time:** MMR-56 added the `startCampaign` phase (410.5 s and 398.9 s, the navigation caches built), the tested settings and the provisional limits. Its generations (35.6, 56.6, 32.7 and 55.7 s; seed 73's world is larger) raised the stated generation time to 60 s.
  - The budget stays **provisional**: its values are one machine's.
  - Re-summarizing an MMR-43 run needs the budget that run's build embedded (`--budget`), since the stated times have changed since.
  - The probe fails a run whose generation exceeds the stated time.

## Generate, cancel, back

- **Generate** (button, or Confirm: Enter, Numpad Enter, or the controller's left face button, X on Xbox).
  - Builds a `WorldChoice`. Its `RequestFields()` are the player-determined fields of the generation request (`worldgen/contracts/generation-request.schema.json`): campaign instance id, master seed, seed source, recipe version, overrides, maximum attempts and producer lock. They are valid against the schema (offline test). MMR-44 adds `schema`, `version` and `policies`.
  - The job and the world summary run off the engine thread. The summary is a one-pass reader of world.json (about 80 MB) that keeps only the shown fields.
  - `WorldSetupModel.Tick` copies their state on the engine thread. The view rebuilds its bound values only when the model's revision changes.
  - Seed edits and Random World are locked while generation runs. The job's phases show as player text (`StageText`), with the attempt count while the source is generated. Only the current job's progress events are taken, and only in order (`ProgressSink`).
- **The generation job.** `CoordinatorRunner` hands the choice to MMR-44's `GenerationCoordinator` (`docs/generation-job.md`): request, source with quality attempts, bundle, verification, scene, runtime navigation, to a world ready for review. The result's world summary reads the bundle's `source/world.json`. A new Generate waits for the previous job's release (one job at a time).
- **Job files.** A generation job holds its directory (about 170 MB in about 900 files) and the bundle it published (about 140 MB).
  - The setup keeps one job: it releases the previous job when a new one starts, and the last when the screen closes (a running job once it has stopped).
  - Releasing goes through `CoordinatorRunner.Release` and `GenerationCoordinator.Release` (`docs/generation-job.md`): the job is cancelled if unfinished, its owned bundle and staging are released, and its directory is deleted. It runs off the engine thread.
  - Developer generations are not touched.
- **Back** (button, or Exit: Escape, or the controller's right face button, B on Xbox).
  - While generating, it cancels and stays on the screen. The result is interrupted generation (whatever the job throws after a cancel), and no generator process is left.
  - Otherwise it returns to the main menu.
  - Exit wins over Confirm when both are released in one frame.
- **The world ready for review** (MMR-46) shows its preview in place of the seed and settings, with Back, New Seed and Start Campaign. Back there returns to the seed with the world kept; Generate with the same seed shows it again without a second job. See `docs/world-preview.md`.

## Errors

Player-facing failures use the categories MMR-47's ticket names (`GenerationErrorCategory`): missing tool or DLC, incompatible build, refused recipe, exhausted quality attempts, disk full, bad source, runtime capability failure, interrupted generation. Each category has a localisable title and action. Messages name only what a player can act on: the game, War Sails and their versions. Producer codes, executable names, paths and hashes go to the error's Detail, which only the diagnostics view shows.

MMR-47 (`docs/recovery.md`) adds two things:
- **References.** Every failure shown, host problems included, gets a reference: a correlation id with its diagnostics and the job's records retained under `<store>/diagnostics/<id>/`. The error panel (`GenerationErrorViewModel`) shows it beside the message and action, and its own Details toggle shows the code, the detail and the folder.
- **Unfinished jobs.** A job a crash left unfinished is offered when the setup opens: continue it in place, or discard it.

- **Host problems** (`GenerationErrors.Host`, `WorldSetupHost.Problems`) are shown before anything can be generated. Since MMR-47 the menu entry opens anyway: the screen lists every problem with its message, action and reference, and refuses to generate. The cases:
  - War Sails not installed or not enabled: missing DLC.
  - A game or War Sails build below this release's minimum (MMR-284: `SupportedBuilds`, v1.4.8 and v1.2.8, any change set; no maximum): incompatible build, naming the minimum and the installed build (`host.game_version`, `host.war_sails_version`).
  - On a supported build, a patch target whose installed signature changed (`GeneratedPatches.Verify`): incompatible build, one error per patch, naming the patch and the member (`host.patch_signature`). Then MMR-283's content checks.
  - Generator binaries missing, unreadable or differing from their pins: missing tool. An IO error here is a problem, never an exception in the menu callback.

  NavalDLC is a hard `DependedModule` of this module, so the launcher normally prevents the War Sails case. The screen still refuses it, and the probe exercises it through `WorldSetupHost.Override`.
- **Generation outcomes** (`GenerationErrors.FromGeneration`, from the job's recorded status and exception):
  - cancelled: interrupted;
  - producer refusal: refused recipe;
  - protocol error, exit failure or reader failure: bad source;
  - missing or changed generator binary: missing tool;
  - disk full;
  - quality exhausted;
  - **any other status: bad source, never success.**

## Player text, input, scaling

- **Localisation.** Every player-facing string is a `{=mnbl_...}default` TextObject (90 ids with MMR-46's preview texts, each naming one text). MMR-286: every module string (setup, preview, generation errors, load refusals and messages) is in `Modules/MNBL.ProceduralWorld/ModuleData/Languages/std_mnbl_procedural_world.xml`, generated by `tools/make_module_strings.py` (`tests/ci/test_module_strings.py` keeps it current); the game shows the inline English text, a translation copies the file. There is no game, and so no `CultureObject`, on the main menu, so the seven official culture names are the setup's own.
- **Diagnostics stay out of the ordinary flow.** Hashes, paths, request fields and the job directory appear only in the **Diagnostics** toggle. It is built only while shown, and its lines are separated by " | " because the rich text widget draws a line feed as a box.
- **Input.**
  - The screen's layer is a focus layer with all input restricted to it, and it registers `GenericPanelGameKeyCategory`. Bindings read from the v1.4.8 IL of `RegisterHotKeys`:
    - Exit: `Escape`, `ControllerRRight`;
    - Confirm: `Enter`, `NumpadEnter`, `ControllerRLeft`;
    - Randomize: `R`, `ControllerRUp`;
    - MMR-46, the preview only: `SwitchToPreviousTab` and `SwitchToNextTab` (Q/E, the controller's shoulder buttons) select places. In the
      preview, Confirm is Start Campaign and Randomize is New Seed (`docs/world-preview.md`).
  - The buttons show these keys' glyphs (`InputKeyItemVM`, console-only, so they appear when a controller is in use).
  - Navigation scopes cover the seed row (edit box, Random World) and the buttons. The settings are locked, so they hold nothing focusable.
  - Keyboard: Enter generates, Escape goes back, R draws a Random World seed when not typing. The mouse reaches everything.
  - The seed box is not focused automatically.
- **Scaling.** The panel is 980 px wide with centred content. A `DimensionSyncWidget` makes its height follow the content (checked in the screenshots), and the Gauntlet resolution scale applies.

## Evidence

- **Offline:** `tools/Test-WorldSetup.ps1`, 29 checks since MMR-46 and MMR-56 (`evidence/runtime/world-setup-tests.json`, log `world-setup-tests.txt`). They cover:
  - the contract and nine refusals;
  - a 17-row seed table;
  - Random World uniformity;
  - settings and wait text;
  - host and generation categories, with codes kept out of messages;
  - request fields against the request schema;
  - the state machine over a scripted job: generate, lock, stages, cancel (including a throw after cancel), fail, host refusal, job release on a new Generate and on close;
  - the one-pass reader against a whole-document reading on two bundles;
  - the presentation sets against both compiled campaigns;
  - the reader on nested look-alikes, escapes and truncation;
  - the key mapping;
  - localisation ids;
  - every one of the prefab's bindings and commands against the view models (49 at MMR-43; 72 in WorldSetup.xml and 33 in WorldPreview.xml with MMR-46, whose review-flow checks are listed in `docs/world-preview.md`).
- **In game:** `world_setup`. `evidence/runtime/world-setup.json` recomputes 19 checks from accepted run **13032** (the MMR-44 generation job; runs up to 28596 used the MMR-43 interim job). Run 13032 covered:
  - the entry at order 4;
  - the movie loading;
  - seed refusals;
  - Random World by button and key, not while typing;
  - the locked settings;
  - the wait text (35 s);
  - Exit to the main menu;
  - Confirm generating seed 42 through the whole generation job in 28.1 s, with a 0.19 s largest frame gap (45 cities, 10 peoples, 7 cultures);
  - the request fields;
  - diagnostics hidden until asked for (screenshot);
  - cancel (interrupted, 0 generator processes);
  - both jobs released;
  - the War Sails refusal.

  Earlier runs are kept for the record:
  - **21304:** passed under the first build, with a 15 s wait text and no timing check.
  - **31804:** failed its timing check: 26.7 s against 25 s, caused by the engine-thread parse.
  - **26092:** passed on the review-fix build before the diagnostics separator change.
  - **28596:** MMR-43's accepted run (interim job, 15.9 s, 0.022 s largest frame gap).
  - **41228:** first MMR-44 build. Generation took 28.3 s against a stated 20 s, and the run exposed the asynchronous release (a second Generate refused) and a closed-screen input error. Both are fixed.
  - **28788:** an interim MMR-44 build. Confirm to result took 33.6 s.
  - **26012:** 30.4 s against a stated 30 s; its measurement was added to the budget.
- **Regression:** `generation_menu` run 26188 passed. The developer entries were still registered then, and every case ran under the harness after the menu gating. (The entries and that probe were removed on 2026-10-18.)

## Limits

- **Physical keyboard and controller devices are not driven.** The probe feeds synthetic key states through the screen's own mapping (`SetupInput.Map` via `WorldSetupScreen.HandleInput`, including the typing gate). The edit box's two-way binding is checked statically (prefab against view model), and the probe writes the seed through the bound property, not through the widget.
- **The campaign start** is MMR-46's Start Campaign over MMR-45's activation (`docs/world-preview.md`, `docs/world-activation.md`). The cultures the setup names come from the source world and the presentation policy; their equality with the compiled campaign is shown on two bundles offline and on the started world in game, not proven for every world.
- **Waiting times are provisional**: one machine's measurements (`docs/first-test-profile.md`).
- **Harness fix found here.** `AutorunHost.TryClaim` could not claim a request when an earlier run with the same, reused process id had left `request.<pid>.taken.json`; the game then idled at the menu. Such files are now renamed `*.stale-<utc>`, never deleted.
