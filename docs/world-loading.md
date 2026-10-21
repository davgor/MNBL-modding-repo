# Loading a generated campaign (MMR-49)

A generated campaign's save loads from the normal **Load** and **Continue** entries:
- the world is restored from its immutable inputs before anything deserializes;
- no generator runs;
- repeated loads in one process never mix two worlds;
- every check runs before anything changes, so a refused load leaves a running campaign running and writes no file.

| Piece | File |
|---|---|
| Routing from the normal entries, and the deserialization guard | `Persistence/LoadRouting.cs` (Harmony prefixes on `SandBox.SandBoxSaveHelper.LoadGameAction` and `TryLoadSave`, and on `TaleWorlds.Core.MBSaveLoad.LoadSaveGameData`) |
| The load sequence | `Persistence/GeneratedWorldLoader.cs` |
| The load's decisions (pure) | `Persistence/LoadPolicy.cs`: compatibility, rebuilt documents, scene state, rebuilt values, routing, busy, stall |
| The campaign rebuilt from the bundle | `Generation/WorldReconstruction.cs` (shared with the activation's BuildWorld) |
| The compiled world's replay | `CampaignBootstrap/CompiledWorldRegistration.cs` (`ForLoad`, `Replay`, `ReplayPreflight`) |
| The load's game manager | `CampaignBootstrap/GeneratedLoadManager.cs` |
| The activation evidence a transferred save needs | `Persistence/WorldSaveTransfer.cs` (export format `mnbl.save-export.v2`) |
| What a world leaves in the process | `World/WorldLifetimeScope.cs`, `Campaign/WorldLifetimeTrackers.cs` |
| Positions canonical at the save | `CampaignBootstrap/PeriodicSeam.cs` (`CanonicalizeForSave`) |
| Offline tests | `tests/Persistence/LoaderTests.cs`, `tools/Test-WorldLoader.ps1` → `evidence/persistence/loader-tests.json`, `.txt`; the transfer in `tests/Persistence/SaveBindingTests.cs` |
| In-game matrix | `Automation/TwoWorldMatrixProbe.cs` (`two_world_matrix`), `Automation/WorldSnapshot.cs` → `evidence/persistence/two-world-matrix.json` (`tools/summarize_two_world_matrix.py`) |
| Review | `evidence/fwg/world-loading-review.md` |

## Routing

The installed entries are:
- the Load screen (`SavedGameVM.ExecuteSaveLoad`);
- `SandBoxViewSubModule.ContinueCampaign`;
- the main menu's Continue, which pushes a `PreloadState` whose screen (`PreloadScreen.OnFrameTick`) starts the latest save.

All of them call `SandBoxSaveHelper.TryLoadSave(saveInfo, onStartGame, onCancel)`. That method runs the installed checks and prompts (module mismatches, a save from a newer version) and ends in `LoadGameAction`. `LoadGameAction` deserializes the save, and `onStartGame` starts the vanilla `SandBoxGameManager(LoadResult)`, which would read the official world's XML onto a generated save (installed 1.4.8 IL).

`LoadRouting` is applied once per process at the main menu, with three prefixes.

**On `LoadGameAction`.** A save whose header carries a world binding (`mnbl.world.id`) goes to `GeneratedWorldLoader`, and the installed method is skipped. The installed prompts before it are unchanged, because their Yes calls `LoadGameAction` too. Every other save loads as installed.

**On `TryLoadSave`: repeated calls.** After its delay, the preload screen calls `TryLoadSave` on every frame until a game starts, including while the installed prompt or a refusal is up. For a generated save, those repeated calls are ignored (`LoadPolicy.Routing`):
- a call from the preload state the routing already took the save from;
- a call while the installed prompt is open.

So one Continue starts one load.

**On `MBSaveLoad.LoadSaveGameData`: the guard.** It is version-agnostic and applied first. A generated save is refused with a message to every caller but the loader (`LoadRouting.Deserialize`). If the routing cannot be applied (a changed patch signature, MMR-284), or another path reaches the deserializer, a generated save is never read onto the official world. Two diagnostic probes that deserialize generated saves in session use the same allowance.

A save the game lists as corrupted has no metadata and keeps the installed behaviour.

## The load sequence

`GeneratedWorldLoader.Begin` starts on the engine thread.

**Refusal when busy.** A load cannot start while any of these holds:
- another load runs (a message only, for repeated clicks);
- a generation job is running or being released;
- an activation runs;
- the running campaign is not on its map, because ending a game that is not on its map stops the engine.

Refusing means the player sees a message and the caller's cancel runs.

**When it starts.** As the installed load does, `Begin` shows the global loading window, which also keeps the player from acting meanwhile, and stops a running campaign's time. The rest runs on a worker, and every engine call is posted to the engine thread (`EngineWork`).

1. **Discover the binding and the immutable world.**
   - **The save's header**, read from the game's save list (MMR-48). It can be readable; torn (`damaged-save`); or held by another program (`save-unavailable`, never taken for damage).
   - **The binding.** The `mnbl.world.*` binding must be well formed, the campaign instance valid, and the file whole (`SaveFileCheck`).
   - **The recorded build**, checked against the running one (`LoadPolicy.Compatibility`, below).
   - **The package** the binding names. Every file is checked against its manifest, and the header's scene and seed against the package's.
   - **The data bundle**, for a compiled world, by its content id (`WorldDataBundle.OpenForSave`).
2. **Check the derived resources and the campaign.** Nothing is written in this step.
   - **The installed scene.** Every file must have the package's hash (`sceneFiles`). The scene is then paired with this module and resolved to its folder (`RuntimeWorldBackend.UseInstalled`). A partial or different folder refuses, because an installed scene is never patched.
   - **An archived scene (MMR-282).** A scene missing from the module (a module update or reinstall replaced the folder) whose copy in `<store>/world-scenes/<scene>` has the package's hashes is copied back in step 4. No encoder runs, so a later encoder that would name the world differently does not matter.
   - **A swept scene** with no archived copy. It is re-encoded from the bundle by the surface encoder (never the generator) into staging, with a 20-minute limit. It must be the package's byte for byte. The encoder also re-derives the settlement layout report beside the scene.
   - **The navigation, settlement layout and playability reports.** Each one is the installed copy, the encoder's re-derivation (the layout), or the copy the activation kept with the campaign (`<store>/activations/<instance>/evidence/`). Each passes its gate:
     - navigation against the scene's navmesh, installed, archived or re-encoded (`NavigationGate.RequireAt`);
     - playability against that navigation and layout (`RuntimePlayabilityValidator.RequireAt`).

     A layout that is not the one the campaign was activated with refuses. Kept copies are read now and written only in step 4.
   - **The campaign.** It is rebuilt from the bundle (`WorldReconstruction`: the graph against the pinned installed catalog, the climate, the terrain context). The rebuilt documents must be the package's byte for byte, and the coordinate frame and climate pattern the header's.
   - **The replay plan** (`ReplayPreflight`), **the map scene and the periodic patch targets.** These are what `GeneratedLoadManager.Start` would refuse on. An exception inside the load's engine callbacks stops the engine, so nothing knowable waits for the game to start.
2b. **Warn before a navigation rebuild (MMR-282).** See "Navigation rebuild warning" below. Cancel ends the load with nothing changed (phase `Declined`).
3. **End the running campaign**, if any, through the installed exit (`MBGameManager.EndGame`, on its map only). Its teardown releases the old world before the next one opens.
4. **Install what step 2 proved.**
   - The archived or re-encoded scene goes into the scene store and its name is registered. A failed copy removes its partial folder, which would otherwise read as a different scene to every later load. An installed scene that has no archived copy yet gets one (`sceneArchive` in the report).
   - The reports are restored from the campaign's copies.
   - The navigation gate is run again on the installed files.
5. **Deserialize** the save (`MBSaveLoad.LoadSaveGameData`, through the guard's allowance).
6. **Start the load** (`GeneratedLoadManager`, `GeneratedGameManager`'s seams in load mode).
   - The documents replay onto the deserialized objects in `RegisterSubModuleObjects(true)`, in the saved-game phases (`RegistrationPlan.SavedGameOrder`). Heroes, Kingdoms and Factions are temporary types whose state the save holds, and vanilla skips them on load too.
   - The map scene loads with its periodic frame, climate and colour grade. The navigation caches and the battle terrain install, and the War Sails map wrapper is installed after the load.
   - The installed saved branch resumes the campaign. Positions and caches resolve as the engine loads them, and time runs once the map is up.

**Navigation rebuild warning (MMR-282, RT-38).** The navigation cache key is built from the live campaign mid-load, so the loader predicts it before anything changes (`CacheForecast`, `World/NavigationCacheForecast.cs`). The caches are expected to be reused only when a published cache's manifest names the save's scene, this module build (`moduleSha256`) and this game and War Sails (the key's `game` line). Anything else is a rebuild: no cache of the scene (`mnbl_load_rebuild_missing`), another module build (`mnbl_load_rebuild_module`), or another game (`mnbl_load_rebuild_game`).
- **The warning** (`mnbl_load_rebuild_title`, `mnbl_load_rebuild` or `mnbl_load_rebuild_measured`, buttons `mnbl_load_rebuild_continue` and `mnbl_load_rebuild_cancel`) states the wait in minutes.
  - When an earlier manifest of the same scene records its build (`durations`: Default, Naval and All `GenerateMilliseconds`), that time on this computer is used.
  - Otherwise the model is used: `seconds(n) = s0 * (n / n0) ^ p` in n = towns + castles + villages (the compiled campaign's counts).
- **The model** is fitted by least squares on log n and log seconds through each measured world's median build in `worldgen/policies/one-shot-budget.json` `navigationCacheBuild`. With one world, p = 2, the square law of the distance pairs.
  - Pre-epic measurements: world A, 286 settlements, median 381.1 s of 15 runs (the engine froze 383 s); world B, 410 settlements, median 829.1 s of 4 runs. They give p = 2.16, and the model reproduces both.
  - **MMR-247 calibrates it:** add the epic build's two measured worlds and remove the pre-epic rows.
- **A probe run** (`AutorunHost.Busy`) is answered "load" for it, and the report records it.
- **The report** keeps `cacheForecast`: the reuse, kind, reason, measured time, settlement count, estimate and warning answer.
- **A forecast that fails** (an unreadable store) does not stop the load; the report says why.
- **Fixture worlds** get no forecast.

**The one deferred check.** A swept scene cannot be re-encoded while another campaign's map is live: a new world generation begins only after the live map is destroyed (`WorldGenerations`). So a load from inside a running campaign whose target scene was swept encodes it after step 3, then runs the checks that read the encoding. A refusal there leaves the player at the main menu, with every save and world unchanged.

**Process audit.** The loader records the processes this module started during the load (`GenerationProbe.ProcessStarts`, by role):
- any role but `surface_encoder` is the generator's (`request`, `generate`, `bundle`, `producer`);
- a load starts none of them.

**Trace.**
- Every trace line, the loader's and the load's game manager's, is appended at once to `<store>/loads/trace-<pid>.log`.
- Each load's report goes to `<store>/loads/load-<pid>-<n>.json`; the newest 20 are kept.
- `Done` is set last, after the report is written.

## Refusals and failures

**Before step 3, a refusal changes nothing:**
- no file is written;
- the encoder's staging is deleted, and so is a settlement report the encoder wrote that was not there before;
- a running campaign keeps running on its map.

**What the player sees.** A title, a message saying what to do, and the code and detail. MMR-286: every title, message and loader message (already loading, restored, the closed running campaign, back at the menu, OK, the load routing's and the periodic freeze's messages) is a localizable TextObject string with an id (`LoadPolicy` `Titles`, `Messages`, `Texts`; `mnbl_load_*`, in the module's language file); the code and detail stay as diagnostics. The loading window is hidden for it. The message's OK runs the caller's cancel, as the installed load's cancel does. Behind Continue, OK also returns from the preload state to the main menu; otherwise the preload screen would wait forever for a game that will not start.

| Code | When | The player is told |
|---|---|---|
| `missing-world` | no package, or no bundle, for the binding | copy the save with its world (`tools/Transfer-WorldSave.ps1`) |
| `damaged-world` | a package or bundle file differs, or the package's manifest is malformed | copy the world again |
| `damaged-save` | a torn header, torn game data, a malformed binding or campaign instance, a binding naming another world, a save that does not deserialize | load an earlier save |
| `save-unavailable` | another program holds the save file | close it and load again |
| `incompatible-build` | another binding contract, save id base or identity schema; a game or War Sails build below the minimum, saved or running; a patch target whose signature changed (named); a bundle built for a build below the minimum | use a version that supports the campaign |
| `not-rebuildable` | a rebuilt document, coordinate frame or climate differs; the scene re-encodes differently, or not within its limit; a report is missing and not kept, damaged, or fails its gate; the layout is not the activated one; the replay plan, the map scene or a periodic patch target refuses | use the version the campaign was made with |
| `busy` | another load, a generation job, an activation, a campaign off its map | wait, or return to the campaign map |
| `not-generated` | the save does not belong to a generated world (`LoadPolicy.NotGenerated`) | load it as a normal campaign |
| `load-failed` | an unexpected error before the start (nothing was changed), or a failure after it | — |

**Compatibility.**
- **Refuses:** a different binding contract, save id base or identity schema: the save definitions are pinned to them. MMR-284: a game or War Sails build only below this release's minimum (`SupportedBuilds`, no maximum), and a War Sails save on a game without War Sails. Then every patch target is checked by signature (`GeneratedPatches.Verify`) before anything changes; a changed one refuses, naming the patch.
- **Notes, does not refuse:** another game or War Sails build at or above the minimum, older or newer than the one the campaign was made with. The catalog (MMR-283) and the patch signatures are checked instead.
- **Does not refuse:** a different module build, backend or navigation cache key. The load rebuilds the world from its immutable bundle and requires every rebuilt artifact to be the saved world's. A module update that changes none of them loads; one that changes any refuses there.
- **Saves made before MMR-48** record no build keys. Their reconstruction is checked the same way.

**After step 3.** A refusal or failure leaves the player at the main menu, and the message says the running campaign was closed for the load.

**After the start.** A failure ends this loader's own game through the installed exit, but only once it has reached its map: ending a game while it loads stops the engine (observed in this ticket's runs).

**A stopped engine.** An exception inside the engine's loading callbacks stops the engine, and nothing can recover it. `LoadPolicy.Stall` tells that case from a busy engine using the engine thread's processor time (`EngineThread.NativeId`), measured per 5-second window:
- **stopped:** 120 s in a row with less than 0.05 s of processor time per window, or a poll unanswered for 30 minutes whatever the processor does;
- **busy:** the first load of a world in a module build keeps the engine thread busy for about 6.5 minutes while it builds the navigation caches.

The loader then records the failure and writes its report and trace. The checks in step 2 exist to keep that case from happening.

## A transferred save

On another computer, a compiled campaign's save needs its activation's navigation, playability and settlement layout reports whenever the scene must be re-encoded. The navigation report is the engine's validation and cannot be redone offline.

`WorldSaveTransfer` (export format `mnbl.save-export.v2`) handles them:
- **Export** carries them as `evidence/<instance>/`, and refuses a compiled campaign without them.
- **Verify** requires them for every compiled campaign's save.
- **Import** restores them to `activations/<instance>/evidence/`, each through a temporary file renamed into place, before the saves. Identical evidence is reused; different evidence refuses before anything is written.

## Repeated sessions in one process

`WorldLifetimeScope` names every static a generated campaign can leave in the process, and `WorldLifetimeTrackers` registers the installed ones (20):
- the campaign and map patches, excluding the routing's process-lifetime patches;
- the map-scene registries: fixture prefabs, runtime settlement entities, missing baked entities, the siege prefab cache source;
- the live campaign map and the current world generation token;
- the battle terrain contexts and the battle-scene observations;
- the periodic frame and its patches, and the colour grade;
- the naval map wrapper;
- the save binding and the activation's save block;
- the startup behaviour;
- the last teardown's own measurements: the module's listeners still on the ended campaign's events after it removed them, and whether the teardown failed;
- a load in progress, and a loader still holding a game. The loader drops its game manager when that game ends (`GeneratedWorldLoader.ReleaseEnded`, from `GeneratedGameManager.OnGameEnd`).

At the main menu with no campaign each count is zero, the baseline:
- a campaign's teardown (`GeneratedGameManager.OnGameEnd`) and its map's destroy (`GeneratedMapScene.Destroy`) release what it held;
- `Held` names anything they did not.

MMR-49 found two that outlived a committed campaign, the battle terrain context and the battle-scene observations; the teardown now clears both.

**Late callbacks.** They are refused by the world generation tokens (`WorldGenerations`, MMR-37): a callback carrying a retired token never reaches the next world.

**Isolation.** After loading one world, the matrix checks that nothing of the other world is registered. The census is the registry's (`CampaignObjectRegistry.CensusTypes`): cultures, characters, heroes, factions, settlements and their components, parties, workshop types, location templates, and every campaign list. The matrix also checks that the frame and the terrain context are the loaded world's only.

## Canonical positions at the save

MMR-63 left a hole. The per-tick seam pass runs before the hourly AI, so a target an AI behaviour placed in a strip during a tick stayed non-canonical until the next tick, and a save in between wrote it.

`PeriodicSeamBehavior` now listens to `CampaignEvents.OnBeforeSaveEvent`, which `SaveHandler.SaveTick` raises before it collects the metadata and writes. `PeriodicSeam.CanonicalizeForSave` moves these to their canonical image:
- every party's position, its order targets and `MobilePartyAi.BehaviorTarget`;
- every War Sails anchor's position, target and last disembark position;
- every map event's position;
- every map track's position, updating the tracks' locator.

**What counts.** A value counts when it is set: not `Vec2.Invalid` (NaN) and not zero. It counts whether or not it lies on a navmesh face valid for its land flag (`CampaignVec2.IsValid`). Requiring that would let a strip value whose flag does not match its terrain be saved non-canonical, and nothing would report it. The save pass also rebases such party positions; the per-tick pass keeps MMR-63's rule.

`NonCanonicalSaved` lists anything still not canonical. The matrix plants non-canonical values (images in the duplicate strip) in the fields the per-tick pass does not cover, then asserts that the save wrote each at its canonical image and that nothing is left.

**Anchors.** War Sails saves an anchor's position (invalid while the fleet is in transit), its target and its arrival time.
- `HourlyTick` sets the position to the target once the fleet arrives.
- `CheckPositionsForMapChangeAndUpdateIfNeeded` moves a position only when it is not on naval navigation.

With canonical positions, a fleet called across the seam keeps its target and its arrival time through a load, and is never snapped to the player.

## Evidence

**Offline.**
- `tools/Test-WorldLoader.ps1`: 42/42, written to `evidence/persistence/loader-tests.json` and `.txt`. It covers compatibility, rebuilt documents, scene state, rebuilt values, routing, busy, stall, every refusal's text, and the lifetime scope.
- `tools/Test-SaveBinding.ps1`: 77/77, including the v2 export's activation evidence (exported, verified, restored, reused, a conflict refused, a crash's temporary file cleaned).

**In game.** The runs are summarized in `evidence/persistence/two-world-matrix.json` by `tools/summarize_two_world_matrix.py 48048 39860 28224`, which recomputes every check from the archived outputs.

**Create, run 48048: 37/37.**
- **World A (seed 42) setup:** an AI lord embarked at sea; an AI fleet after a naval battle with pirates; ownership, a spawned party, a prisoner, a relation; a siege; a fleet called across the seam and moving at the save, another anchored by the seam; the player saved just after crossing the seam with a bandit pursuing from across it; non-canonical values planted and saved canonical.
- **World B (MMR-19's fixture world):** with its changes.
- **A through Continue:** one load for 285 per-frame calls. A equals its save, flagships and ship owners included. The embarked party is still at sea, the pursuit ends in one encounter with no second crossing, and the fleet arrives at its port.
- **B through the load screen from inside A.** B equals its save.
- **Three refusals from inside the running B, and a refused Continue:** each before anything changed.
- **Every baseline held.**

**Cold, run 39860 (a new process): 20/20.**
- A's swept scene was re-encoded by the surface encoder only (14 s). Navigation and playability were restored from the activation evidence, and the settlement layout was re-derived.
- A and B equal their saves.
- The probe's saves were deleted, and the baseline held.

**Limits.**
- World B is a fixture world: the production generator yields one activatable world today.
- A failure after the start and a re-encode deferred behind a running campaign are implemented but not exercised in game.
- The review and the runs that were not accepted are in `evidence/fwg/world-loading-review.md`.
