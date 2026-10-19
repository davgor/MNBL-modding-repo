# MMR-49 independent review and fixes

One independent review of the MMR-49 working tree. The reviewer was an agent with read-only access: it read the loader, the routing, the
policy, the reconstruction, the probe and the installed IL the design relies on, and compiled nothing into the game; it did not edit files
or launch the game. It reported 1 blocker, 4 major and 12 minor findings. All were fixed in one batch. Then:
- the offline harnesses ran, all 19 green: `Test-WorldLoader` 42/42 (was 30/30), `Test-SaveBinding` 77/77 (was 71/71), the rest unchanged;
- the module and the transfer tool compiled under C# 7.3 with warnings as errors;
- the confirming in-game runs followed on the fixed build (see Results).

The fixes rest on the installed 1.4.8 IL read again for this batch (`SandBox.View.PreloadScreen`, `SandBox.SandBoxSaveHelper.TryLoadSave`
and `LoadGameAction`, `SandBoxViewSubModule.OnSaveHelperStateChange` and the main menu's Continue, `MapEvent`, `Track`,
`MapTracksCampaignBehavior`).

## Blocker

1. **Continue restarted the load forever.** After its delay, `PreloadScreen.OnFrameTick` calls `TryLoadSave` on every frame (its counter
   stops at the delay) until a game starts; the prefix began a new load on each call, and a refusal left the player under the loading
   window with no way back. Fixed (`LoadRouting`, `LoadPolicy.Routing`):
   - the routing prefix moved to `LoadGameAction`, which the installed checks and prompts end in; a prefix on `TryLoadSave` ignores a
     generated save's repeated calls from the preload state it already took the save from, and calls while the installed prompt is open;
   - the loader shows the global loading window as the installed load's states do (`SandBoxViewSubModule.OnSaveHelperStateChange`), and
     hides it for a refusal;
   - a refusal's OK returns from the preload state to the main menu (`GeneratedWorldLoader.Dismissed`).

   Tested offline (routing decisions) and in game: A loads through Continue with one load for the preload screen's repeated calls; a
   refused Continue returns to the main menu.

## Major

2. **An in-game Load ended the running campaign before any check.** Fixed: the order is now discover, check everything (scene, reports
   through their gates, reconstruction, replay plan, map scene, periodic patch targets), then end the running campaign, then install,
   deserialize and start. `EndRunningGame` refuses a campaign that is not on its map. One check cannot move: a swept scene cannot be
   re-encoded while another campaign's map is live (`WorldGenerations`), so that case encodes after the end (documented). Tested in game:
   three refusals from inside the running world B leave it running on its map.
3. **Nothing blocked other actions during a load.** Fixed: the loading window; a busy refusal (`LoadPolicy.BusyReason`) while another load,
   a generation job (`GenerationRuntime.ActiveJobPhase`), an activation (`ActivationSaveBlock.Active`) runs or the campaign is off its map;
   `ReturnToMenu` ends only the loader's own game on its map (`GeneratedLoadManager.OwnsCurrentGame`); `onCancel` runs on every refusal,
   the busy one included.
4. **A transferred save could not load on a machine without its activation evidence.** Fixed: the export format is
   `mnbl.save-export.v2`. Export carries each compiled campaign's navigation, playability and settlement layout reports
   (`evidence/<instance>/`) and refuses a campaign without them; verify requires them; import restores them to
   `activations/<instance>/evidence/` through temporary files, reuses identical ones and refuses different ones before writing.
   Tested offline (`SaveBindingTests`, 6 new checks, including a crash's temporary file removed by the startup cleanup).
5. **A failed routing silently sent generated saves down the vanilla path.** Fixed: a version-agnostic guard on
   `MBSaveLoad.LoadSaveGameData`, applied first and on its own, refuses a generated save to every caller but the loader
   (`LoadRouting.Deserialize`); the two diagnostic probes that deserialize generated saves use the same allowance.

## Minor

6. **`Done` flipped before the report was written.** Fixed: `Done` is set last in `finally`, after `WriteReport`.
7. **The engine-stopped detector could not fire** (cumulative processor time; other threads). Fixed: `LoadPolicy.Stall` measures the engine
   thread (`EngineThread.NativeId`) per 5-second window, stops after 120 s of idle windows, and has a 30-minute deadline. Tested offline
   (busy, idle, interrupted idle, trickle, deadline).
8. **`GeneratedWorldLoader.Current` kept the ended campaign alive.** Fixed: `ReleaseEnded` (from `GeneratedGameManager.OnGameEnd`) drops the
   loader's game manager and a finished loader; the manager drops its game at the end; a tracker (`load: loader holds a game`) counts it.
9. **The summarizer expected 3 restored reports on a re-install.** Fixed: the encoder re-derives the settlement layout (now checked against
   the kept one), so a cold load restores navigation and playability and re-derives the layout; the summarizer checks exactly that.
10. **The save-canonicalization check passed vacuously.** Fixed: `CanonicalizeForSave` and `NonCanonicalSaved` cover map event and map track
    positions (updating the tracks' locator); the probe plants non-canonical values (a `BehaviorTarget`, an anchor's last disembark
    position, a track, a map event when one exists) before each save and checks each was written at its canonical image.
11. **Pre-start failures showed as a generic `load-failed`; a locked file was called damaged; the encoder had no limit.** Fixed:
    `save-unavailable` for a header another program holds; `damaged-save` for an invalid campaign instance; `damaged-world` for a malformed
    package manifest; `not-rebuildable` for re-encoding, report, replay plan, map scene and periodic patch failures; a 20-minute encoder
    limit; any other error before the start is a refusal that changed nothing.
12. **Refusals still wrote files.** Fixed by ordering: kept reports are read during the checks and written at the install; the re-encoded
    scene stays in staging until the install (a failed copy removes its partial folder); a settlement report the encoder wrote is removed
    on a refusal; the terrain context is registered at the start.
13. **The installed-scene path never paired or resolved the scene.** Fixed: `RuntimeWorldBackend.UseInstalled` marks, pairs and resolves it
    during the checks; a scene that does not resolve to its folder refuses.
14. **The isolation census skipped types; the listeners tracker was always 0.** Fixed: `WorldSnapshot.Objects` counts every
    `CampaignObjectRegistry.CensusTypes` type and every campaign list. The listeners tracker stays (the teardown's own measurement) and a
    `campaign events: teardown failed` tracker joins it.
15. **Evidence gaps.** The snapshot now records each party's flagship and each ship's owner; a start failure records the setup's error.
    World B is still MMR-19's fixture world, not a second compiled world: the production generator yields one activatable world today
    (seed 42; seeds 43–72 refused by the quality gate, 73 by the village binding), recorded as a limit below.
16. **Vanilla's module-mismatch and newer-save prompts were skipped.** Fixed by the move to `LoadGameAction`: the installed prompts run
    unchanged before it.
17. **The cold-phase B check only confirmed that time runs.** Fixed: after B's load, time runs, parties move, the town the player took and
    the lord the player holds stay so, and party positions and targets stay canonical; B's loaded state is also compared value for value
    with its save in both phases.

## Found by the confirming runs

Two create runs on the fixed build were not accepted. Each one's every check is kept in `evidence/runtime/in-game/two_world_matrix-<pid>.json`. Two of their findings were real defects, fixed in the loader and the save pass.

**Run 11568** found:
- **A real defect: a missing bundle was classified `damaged-world`.** OpenForSave's own problem ("missing bundle <id>") was not recognized as missing; only the verifier's "not a bundle directory" was. This hit the in-game and the Continue refusals. Fixed in `WorldInspector.Kind`. Everything else in those refusals held: the running campaign kept running, the cancel ran, and Continue returned to the menu.
- **Probe defects:**
  - The probe planted a track value on the player's movement arrow, a pointer track the game releases.
  - The seam-crossing row was searched only ±40 around the player.
  - Fixture world B has no idle lord, so B's party and prisoner steps failed.
  - The fleet arrival was read after 30 s of unpaused time.
  - One AI party chose a new movement target before the snapshot. The comparison now reports such retargets apart, within a bound, and with a no-load control.

**Run 44940** passed the rest:
- the Continue load: one load for 288 per-frame calls;
- A equal to its save: 0 AI retargets of 1023 parties;
- the pursuit;
- B loaded from inside A;
- the three in-game refusals and the refused Continue;
- every baseline.

It found:
- **A real defect: the save pass skipped values off a navmesh face.** It required `CampaignVec2.IsValid`, which also checks the navmesh face against the value's land flag. A strip value whose flag does not match its terrain would be saved non-canonical, and nothing would report it. `Vec2.Invalid` is NaN, so the set/unset test needs only NaN and zero. Fixed: the save pass and `NonCanonicalSaved` use that test, and the save pass also rebases such party positions.
- **Probe defects:**
  - The probe planted images off the map's edge.
  - The encounter's game menu kept campaign time stopped, so the fleet could not arrive. The fleet itself kept its target and transit through the load.

**Runs 35184 (create) and 46320 (cold, a diagnostic run on 35184's saves)** passed every check but the fleet's arrival. Run 46320 exercised the cold path:
- the swept scene was re-encoded by the surface encoder only;
- navigation and playability were restored from the kept evidence, and the settlement layout re-derived;
- A was equal to its save;
- B loaded from inside A;
- the baseline held.

Its time samples showed campaign time not advancing after the pursuit, with mode, lock, menu, encounter and inquiry all clear. The game's log for the run explained it: the encounter's meeting menu had started a conversation with the bandit leader. `PlayerEncounter.Finish` does not end that conversation, and the map stays frozen while it is open. A player ends it by talking. The probe now ends it as the game does (`ConversationManager.EndConversation`, `MapState.OnMapConversationOver`) before finishing the encounter. A probe defect, not a loader one: the fleet kept its target and transit through the load in every run.

**Runs 16324, 5088 and 2804 (create)** narrowed the last probe issues:
- **Run 16324.** With the conversation ended, time ran and the fleet completed its transit. The probe read the anchor's position in the gap before War Sails' next hourly tick sets it to the target, so it now runs time on until the position is set.
- **Run 5088.** Cut off mid-load at the user's pause: the process exited during the navigation cache build, with no exception and no dump. Not counted.
- **Run 2804.** Passed every check, the fleet included, except world B's prisoner. The lord was held at the save and at the load (B's comparison passed), then escaped within the next 2.4 campaign hours through the installed hourly escape. The check now requires the prisoner held at the load, and afterwards still held or escaped and free; held by another party, dead or gone still fail.

## Results

The accepted runs are on one build, worldbridge `776C160626D029E6`. `tools/summarize_two_world_matrix.py 48048 39860 28224` writes `evidence/persistence/two-world-matrix.json`. Every check is recomputed from the archived outputs, not taken from the probe's pass lines.

**Create, run 48048: 37/37, status ok.**
- **World A (seed 42) setup:**
  - an AI lord embarked at sea;
  - an AI lord fleet after a naval battle with pirates;
  - ownership, a spawned party, a prisoner, a relation;
  - a siege;
  - a fleet called across the seam and moving at the save, another anchored by the seam;
  - the player saved just after crossing the seam, with a bandit pursuing from across it;
  - four planted non-canonical values (BehaviorTarget, last disembark, track, map event), each written at its canonical image.
- **World B (fixture `mnbl_00059042`):** ownership, prisoner and relation.
- **A through Continue.** One load for 285 per-frame calls. A equals its save: 0 AI retargets of 1007 parties, flagships and ship owners included. Then:
  - the embarked party is still at sea;
  - the pursuit ends in one encounter with no second crossing;
  - the fleet arrives at its port.
- **B through the load screen from inside A** (the loader checks, then ends A). B equals its save; after the load its time runs and its changes hold.
- **Refusals from inside the running B** (torn → `damaged-save`, missing bundle → `missing-world`, other game version → `incompatible-build`). Each left B running on its map and ran the caller's cancel. World A's package and bundle were unchanged.
- **A refused Continue** returned to the main menu.
- **Baselines.** Every baseline held: 20 tracked statics at zero after each game.

**Cold, run 39860 (a new process): 20/20, status ok.**
- A's scene and reports were swept, then A was loaded. The scene was re-encoded from the bundle by the surface encoder only (14 s). Navigation and playability were restored from the activation evidence, and the settlement layout was re-derived and checked against the kept one. The load took 6.4 s, because this build's navigation caches were already built.
- A equals its save, and the pursuit and the fleet behave as in the create run.
- B was loaded from inside A and equals its save.
- The probe's saves and the campaigns' initial saves were deleted, and the baseline held.

**Load check, run 28224 (before the fixes): 3/3.** The first production load of a compiled world.

**Criteria.**
- **c1:** met for the ticket's sequence. The same-process and cold A/B loads restore both worlds' land and naval state, embarked and post-naval-battle fleets included, with owner, membership, flagship and crew compared value for value.
- **c2:** met. No generator process ran in any load (only the surface encoder, once, to re-encode a swept scene). No object, frame or terrain context of one world was present after loading the other (the registry's full census).
- **c3:** met. Missing and corrupt inputs refused before anything changed, from the menu and from a running campaign, and every baseline held after disposal.

## Limits

- World B is a fixture world (`mnbl_00059042`, MMR-19). It is loaded through the same routing and loader but has no bundle and no re-encoding. A second compiled world needs a generator or policy change (production seed yield), outside this ticket.
- A failure after the start (`ReturnToMenu`) is implemented but not exercised in game. Every refusal before the start is. The stopped-engine detector is tested offline only.
- The deferred re-encode (a swept scene loaded from inside another running compiled campaign) is implemented, but no run swept the scene of the world loaded from inside another; the cold run's re-encode loaded from the main menu.
