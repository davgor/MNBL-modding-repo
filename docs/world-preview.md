# World preview, Start Campaign and deliberate regeneration (MMR-46)

A generated world waiting for review is shown before anything is played: a map of the world the player will play, with every settlement and
port where the campaign will put it, the kingdoms, and the world's counts. From there the player starts the campaign in that world (one
action, MMR-45's activation), goes back to the seed with the world kept, or draws a new seed. Leaving character creation returns to the same
world, ready for review, without generating it again.

| Part | Where |
|---|---|
| The preview (pure managed, tested offline) | `src/MNBL.ProceduralWorld/UI/WorldPreview.cs` |
| Its engine texture and Gauntlet texture provider | `UI/WorldPreviewTexture.cs` (`WorldPreviewTextures`, `WorldPreviewTextureProvider`) |
| Preview panel view model | `UI/WorldPreviewViewModel.cs` |
| Setup flow (review, back, reuse, Start Campaign, New Seed) | `UI/WorldSetupModel.cs`, `UI/WorldSetupViewModel.cs`, `UI/WorldSetupScreen.cs` (`HandOff`, `TickPending`, `CoordinatorRunner.Preview`/`StartCampaign`) |
| The player's game manager | `CampaignBootstrap/GeneratedCampaignManager.cs` |
| Withdrawal | `Generation/GenerationCoordinator.cs` (`CommitWithdrawn`), `Generation/JobStateMachine.cs` (version 2), `Generation/ActivationCommitStage.cs` |
| No save before the commit | `Generation/ActivationCommitStage.cs` (`ActivationSaveBlock`) |
| Prefabs | `Modules/MNBL.ProceduralWorld/GUI/Prefabs/WorldPreview.xml` (used by `WorldSetup.xml` as `<WorldPreview DataSource="{Preview}" />`) |
| Offline tests | `tests/UI/WorldPreviewTests.cs` (`tools/Test-WorldPreview.ps1`), the review flow in `tests/UI/WorldSetupTests.cs` (`tools/Test-WorldSetup.ps1`), state machine v2 in `tests/Generation/JobStateMachineTests.cs` (`tools/Test-GenerationJob.ps1`) |
| In-game probe | `new_campaign_flow` (`Automation/NewCampaignFlowProbe.cs`; args `seed`, `culture`) |
| Evidence | `evidence/ui/new-campaign-flow/` |

## What the preview is made from

The preview is built from the job's own compiled world, the inputs the activation compiles (`CoordinatorRunner.Preview`, on the worker,
after the job reached ReadyForReview):
- the job's prepared **surface** (`PreparedSurface.Surface`: heights, normals, biomes, water intent);
- the job's **settlement layout** report (`RuntimeWorldBackend.SettlementReportPath`), refused unless it names the bundle's world and the
  layout the scene was built from (`PreparedSurface.Settlements.Sha256`);
- the bundle's **campaign records** (`compiled/campaign/campaign.json`, `id-map.json`, read verified);
- **FactionFactory** over the module's embedded policies (`GameActivationHost.PolicyBytes`), the same factions the campaign registers;
- **the village binding** (`VillageBinding.FromRecords`: MMR-33's binding over the records, the bundle's travel graph and `economy.json`),
  the same function `SettlementFactory` binds the campaign's villages with.

It records the world's content id, layout sha256 and scene name, so the preview names exactly the world it shows. A world whose preview
cannot be made is not offered for play: the screen says so and asks for another Generate (`PreviewFailed`).

**Raster** (768 x 384, the logical frame, x periodic with the seam at both edges, **oriented as the campaign map shows it**: +y is up on the
campaign map, so row 0 is the top edge and a marker's height on the picture is `1 - (y - originY) / height`; the review found the first
build drawn upside down. The bundle's own frame calls +y south, so "north" in the code's comments means the map's up):
the surface's biome colours with a north-west hillshade;
ocean by depth, lakes and rivers in their own blues; each land sample tinted 30 % toward the nearest fortification's kingdom colour, with
kingdom borders darkened. `RasterSha256` identifies it; building twice gives the same raster.

**Markers**: every layout settlement at its layout position (the scene's placement), and every port at its port position. Names are the
campaign records' (`FactionFactory.Clean`). A town's or castle's kingdom is its record's. A **village's is its bound settlement's** as the
campaign binds it, which is the game's `MapFaction`. The binding moves some villages away from the compiler's town: in world A, 44 are
rebound and 3 of them into another kingdom, the 3 that runs 43992 and 13920 found differing before the preview used the binding.
**Harbour villages** (War Sails: fleets land there, and the game gives them a port) are flagged as such. **Regions**: each kingdom holding a
fortification, labelled with the name the campaign registers (`KingdomIntent.Name`) at its fortifications' periodic mean.

## The panel

- The map is 800 x 400 Gauntlet units inside the 980-unit setup panel. Kinds are told apart by shape and size, not colour alone:
  - town: a 12-unit light square with a dark edge;
  - castle: a 10-unit light square with a dark centre;
  - village: a 5-unit dark dot; a harbour village: a 7-unit dark dot with a blue edge;
  - a town's port: a 9-unit blue-edged square.

  Villages are drawn first and towns last.
- **Labels.** Towns carry their name, shortened to 16 characters with an ellipsis; a label near the east edge goes west of its marker.
  Kingdoms with two or more fortifications carry their name in gold, shortened to 22; pointing at one shows it in full ("The kingdom of …").
- **Full names by mouse, keyboard or controller.** Pointing at any marker shows its full name, kind and kingdom below the map. **Previous
  place / Next place** select the towns and castles one by one, west to east and wrapping, with a gold ring on the selected marker and
  its full name and kingdom below the map. Every kingdom holds a town (FactionFactory dissolves the others), so walking the places names
  every kingdom in full. A full kingdom list under the counts was tried and dropped: 40 names took about ten lines and, with long texts,
  pushed the panel past a 1080-unit page (run 13920). The keys run from the tab keys (Q/E, the controller's shoulder buttons, `SwitchToPreviousTab`/`SwitchToNextTab`)
  or from two buttons showing those keys' glyphs, in their own navigation scope. Each kind has its own whole sentence (`PlaceTown` …
  `PlacePortFree`), so a translation never splices a noun into a sentence.
- **Below the map:** the compiled counts (towns, castles, villages, ports, kingdoms, clans, and the source world's cities and peoples), the
  legend, and, when the engine refused the texture, a plain-language note (the markers still show on a plain field).
- **The texture.** `WorldPreviewTextures.Show` makes an engine texture on the engine thread, once per raster. By default it is made from the
  raster's own bytes (`Texture.CreateFromByteArray`), with no file written; run 43992 showed it renders the same as the PNG. If the bytes
  are refused, it falls back to writing the preview's PNG to `<store>/previews/preview-<raster sha>.png` (only the shown preview's file is
  kept) and loading it (`Texture.LoadTextureFromPath`). The texture is set always-valid and preloaded, then wrapped as `EngineTexture` and
  `TwoDimension.Texture`. A replaced or cleared texture is released three frames later, and the texture is dropped once the campaign plays. The prefab's `TextureWidget` names `WorldPreviewTextureProvider`. Gauntlet finds providers by type
  name, so the screen calls `TextureProviderFactory.RefreshProviderTypes()` when it opens.

## The flow

The setup's states (`SetupState`): Editing, Generating, Generated (the preview), Failed, Starting, Started.

| Action | Keys | What happens |
|---|---|---|
| **Generate** | Confirm (Enter, Numpad Enter, controller left face) | As in MMR-43; when the job reaches review its preview is built on the worker. |
| **Back** in the preview | Exit (Escape, controller right face) | Returns to the seed with the world **kept**: the same job and files, nothing released. The notice says the world for this seed is still ready. The seed cannot be edited under the preview, only here. |
| **Generate** with the kept world's seed | Confirm | Shows the kept world again (`Reused`): no second job, no second preview. Any other seed releases the kept world first and generates. |
| **New Seed** in the preview | Randomize (R, controller top face) | Draws a Random World seed into the seed box (`seedSource: random`) and generates it at once; the reviewed world is released. Committed worlds are not touched. |
| **Previous / Next place** in the preview | the tab keys (Q/E, controller shoulder buttons) | Select the previous or next town or castle; its full name shows below the map. |
| **Start Campaign** in the preview | Confirm | Hands the job to MMR-45's activation (`GenerationCoordinator.Commit` with `ActivationCommitStage`). The setup is locked: no generate, edit, back (the button is disabled), new seed or second start. |

`SetupInput.ForState` maps the generic panel keys per state and per whether the preview is shown:
- in the preview, Confirm is Start Campaign, Randomize is New Seed and the tab keys select places;
- for a generated world without a preview, Confirm generates again and Randomize draws a seed;
- while the campaign starts, no key acts;
- elsewhere the keys keep their MMR-43 meaning and the tab keys do nothing. The buttons show the keys' glyphs. The navigation scope covers
the visible buttons: Back, Diagnostics and New Seed / Start Campaign in the preview; Back, Diagnostics and Generate otherwise.

**Starting.** The activation's game starter is `Start = a => { WorldSetupScreen.HandOff(); return GeneratedCampaignManager.Start(a); }`.
- `HandOff` closes the screen as the new game starts, and keeps its model in `WorldSetupScreen.Pending`. `SubModule.OnApplicationTick`
  ticks the pending model (`TickPending`).
- `GeneratedCampaignManager` is `GeneratedGameManager` with the installed new-game branch: `SandBoxGameManager.OnLoadFinished` plays the
  campaign intro, then character creation, which the player drives. The world's cultures are the only ones offered (MMR-32), on every stage
  and after going back.
- `Progress` reports `map` once the campaign map runs. It reports `withdrawn` when the game ended before the map (the culture stage's Back
  pops the creation state, `CharacterCreationCultureStageView.PreviousStage`), and a failure after the map.

**The start's outcome** (`StartOutcome`, from the commit's continuation; the model takes it on the engine thread):
- **playing:** the activation committed (journal committed, receipt written, initial save published). The runner releases the job (its
  package, bundle and scene stay with the world); the model is Started and the pending model is dropped.
- **withdrawn:** the activation rolled back every boundary. The commit stage throws `CommitWithdrawn` only then, and the coordinator returns
  the job to ReadyForReview (state machine v2, `docs/generation-job.md`). `TickPending` reopens the setup screen at the main menu on the same
  model and world, with the notice "You left character creation. This world is still ready". A second start is a new activation instance:
  the first attempt's journal keeps the request's instance.
- **failed:** `StartOutcome.FromCommit` gives the activation's own message, never the generation's "could not be generated":
  - `activation.failed` (undone; generate the world again to retry);
  - `activation.unresolved` (the failure came after the initial save, which MMR-45 leaves for recovery; the game checks it at its next start);
  - a full disk keeps its category.

  The message shows on the screen if the failure came before the hand-off, otherwise when the screen reopens at the main menu. The job
  (Failed) is kept until the next Generate.
- **A start that never reaches a game** (no game of the manager initialized and none loading 30 s after the start) is reported as a
  failure by `Progress` instead of waiting out the campaign timeout. A setup screen that closes while its world is starting hands its model
  over (`OnFinalize`).

**No save before the commit.** From the moment the campaign map runs until the commit, a rollback or the game's end, `ActivationSaveBlock`
(Harmony `mnbl.proceduralworld.activation-saves`) refuses SaveHandler's manual, quick and auto save entry points (`SaveAs`,
`QuickSaveCurrentGame`, `SignalAutoSave`, `ForceAutoSave`). The only exception is the activation's own reserved initial save; refused saves
are told "The campaign can be saved once it has started". The save manager guard (MMR-45) turns autosave off as well. A recoverable
failure after the initial save leaves the campaign running, so `Recover` releases the guard and the block then; saves are not blocked for
the rest of the session.

## Evidence

**Offline:**
- **`tools/Test-WorldPreview.ps1`:** 16 checks (`evidence/ui/new-campaign-flow/world-preview-tests.json`/`.txt`; previews `preview-A.png` and
  `preview-B.png` with their records). It builds both real compiled worlds with their layouts and checks:
  - every layout settlement and port is a marker at the layout's position, with kinds and names as the campaign records give them, and
    harbour villages as the layout flags them;
  - settlements on land pixels (2 px coastal tolerance), ports beside water;
  - oriented as the campaign map (+y up);
  - regions named as the campaign registers them;
  - every village in the kingdom of its bound settlement as the campaign binds it (world A: 44 rebound, 3 into another kingdom; world B:
    75 and 19);
  - determinism, the PNG, two different worlds, and label shortening.
- **`tools/Test-WorldSetup.ps1`:** 27 checks (`evidence/ui/new-campaign-flow/world-setup-tests.json`/`.txt`). The review flow runs over a
  scripted job with bundle A's real preview:
  - the seed locked under the preview; Back keeping the world; reuse without a second job; a changed seed releasing first;
  - Start Campaign locking the setup; withdrawn back to review on the same job; then committed, with no release by the setup;
  - a failed start, reported or thrown;
  - `StartOutcome.FromCommit`'s messages (undone, unresolved, disk full; never "could not be generated");
  - New Seed; a world without a preview not offered, and keeping Generate and Random World;
  - the keys per state, including the tab keys;
  - the panel's markers, labels, regions, hover, Previous/Next place (every kingdom reached), kingdom hover, long names, the kept-world
    notice and a refused texture;
  - the bindings of `WorldPreview.xml` and `WorldSetup.xml` against their view models: key widgets against the key view model, no list
    element binding anything but its data source, navigation scopes naming widgets.
- **`tools/Test-GenerationJob.ps1`:** 19 checks (`evidence/generation/job-state-machine-tests.json`/`.txt`): state machine v2. A withdrawn
  commit returns to ReadyForReview, timed `withdrawn`; nothing else goes back.
- **Regression:** every other harness the change touches passed after the binding extraction:
  - Activation 240, CompiledWorld 8, GraphValidation 72, CampaignRegistry 59, Settlements 22, NavalContent 15, SpawnPolicy 18;
  - EarlyBinding 58, PeriodicCoordinates 85, Playability 24, SettlementPlacement 52, EncounterResolver 35, and GeneratedCampaignDataset.

**In game:** `evidence/ui/new-campaign-flow/new-campaign-flow.json` (`tools/summarize_new_campaign_flow.py`) recomputes 16 checks from the
archived results (`evidence/runtime/in-game/new_campaign_flow-<pid>.json`/`.log`). Screenshots are in
`evidence/ui/new-campaign-flow/screenshots/`.
- **Run 50304 (accepted):** 16/16, every probe expectation passed, on the pinned engine binaries.
  - **Review:**
    - generation in 35.6 s;
    - the preview of the job's world: content id `f596b58c…`, its scene and layout;
    - +y up, as on the campaign map;
    - the texture from its bytes and the PNG fallback, with one preview file kept;
    - 316 markers;
    - hover;
    - the tab keys walking the 71 towns and castles (glyphs Q and E), every kingdom reached;
    - the panel inside the page and within 1080 units: 850 units, and 943 with every text 40 % longer.
  - **Regenerate:**
    - Back keeping the job;
    - Confirm reusing it with no second job;
    - New Seed: a random seed, the reviewed world released, committed worlds untouched, then cancelled;
    - the same seed again in 34.2 s, the same content id and raster.
  - **Withdraw and commit:**
    - Start Campaign closed the setup for the new game;
    - character creation offered exactly the dataset's creation cultures and kept the culture through FaceGenerator and back;
    - leaving creation withdrew the start (instance `fc42e73d…`: failed journal, no save bound). The setup reopened on the same job without
      generating again;
    - the second start committed (instance `a762a2d3…`, journal committed, receipt), released the job and dropped the texture.
  - **Against the live campaign:**
    - all 316 markers at the live positions, kinds, names and kingdoms;
    - all 286 live towns, castles and villages marked, with the 30 town ports and 80 harbour villages matching `HasPort`;
    - all 40 labelled kingdoms with their live names;
    - the same content id, layout and scene.
- **Earlier runs, kept for the record:**
  - **43992 (first build):** failed 3 village kingdoms. Its screenshots exposed the settings in the preview state, the button row wider than
    the panel and the long-text height.
  - **13920 (after the review's fix batch):** failed two checks. The long-text panel was 1085 units (the kingdom list, since dropped). The
    same 3 villages differed, and 80 harbour villages had no port marker (the binding and harbour villages, since fixed).
- **`world_setup` (MMR-43) on the final build, run 42976 (`evidence/runtime/in-game/world_setup-42976.json`/`.log`):** passed. Generation
  took 33.3 s against the stated 35 s, with a 0.17 s largest frame gap. Exit from the preview returned to the seed; cancel gave an
  interrupted generation with 0 generator processes left; every job was released. `evidence/runtime/world-setup.json` stays MMR-43/44's
  summary of run 13032.

## Limits

- **Physical devices are not driven.** The probe feeds synthetic generic-panel key states through the screen's mapping and drives
  character creation through its managers, one action per frame, as the installed views do. It does not click widgets.
- **Narrow screens are measured, not run.** The probe measures the panel on this machine's page (1600 x 1024, UI scale 0.85) and requires
  it to fit 1080 units, the smallest page height a 16:9 or narrower screen gives at UI scale 1. A UI scale above 1 on a 16:9 screen leaves
  less height than that and is not covered.
- **Localisation is pseudo-localised.** No translation of the mod's strings exists. Long texts are simulated by lengthening every word by
  40 %, and every string is a `{=mnbl_...}` TextObject.
- **Names come from the generator.** Town and kingdom names such as "Korpor (Age 2)" are the generator's and the compiler's; the preview
  shows what the campaign registers.
- **The start time** is stated by the setup screen since MMR-56 (`docs/first-test-profile.md`): about 7 minutes for a world's first start, while its navigation caches are generated on the engine thread and the game stops responding.
- **A recoverable failure after the initial save** (MMR-45: any failure once PublishSave completed) leaves the campaign running, with its
  saves allowed again. The pending model reopens the setup with `activation.unresolved` when the player next reaches the main menu.
- **Hover is driven through the view model in game.** The probe calls the marker view model's hover commands as the widget does. The
  prefab's binding of `Command.HoverBegin`/`HoverEnd` to them is checked offline.
- **The defensive start paths are not exercised.** A game that never initializes, and a screen closing mid-start, are handled but not
  provoked by the probe.
