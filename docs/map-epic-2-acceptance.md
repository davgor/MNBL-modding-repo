# Map epic 2 acceptance (MMR-247)

The epic's one integrated live test, apart from MMR-249's probe: the build that ran on the shared game install, world 1 (seed 42) and world 2 (seed 73) through the New Campaign screen, every check of the ticket's step 2, a 30-day soak, a synthetic content mod, and the install put back as found. One run finds everything; one fix batch (with the balance pass) fixes it; one confirming run re-runs the same harness.

**Status:** acc4 ran (2026-10-20) and hung in world 1's forest battle; its triage (`C:\Users\davgo\m2\results\acc4-triage.md`) reordered world 1 and fixed the probe and grading defects below (MMR-247-P). Nothing of the reordered harness has run in game yet.

| | |
|---|---|
| Orchestrator | `tools/Run-Map2Acceptance.ps1` (state `build/acceptance/<RunId>/state.json`, git-ignored) |
| Probe | `map2_acceptance` (`src/MNBL.ProceduralWorld/Automation/Map2AcceptanceProbe.cs`, with `Map2AcceptanceChecks.cs` and the pure `Map2AcceptancePlan.cs`) |
| Synthetic content module | `tools/Build-AcceptanceContentModule.ps1` → `build/acceptance-content/MNBL.AcceptanceContent` (derived from the installed game at build time; never committed or packaged) |
| Verdict | `tools/summarize_map2_acceptance.py` → `evidence/map-epic-2/acceptance.json` |
| Offline tests | `tools/Test-Map2Acceptance.ps1` (the place choices on A'/B'), `python -m unittest tests.ci.test_map2_acceptance` (the summarizer) |
| Screenshots | `build/acceptance/<RunId>/in-game/map2_acceptance-<pid>-shot-<label>.bmp` (git-ignored; `acceptance.json` lists each with its SHA-256) |

## Before the run

- **CLAIM** with the battle-scale session (the shared install rule in [runbooks/development.md](runbooks/development.md#run-it-in-the-game)). The text of the CLAIM, or "no partner alive: <how it was checked>", goes to `-Claim`; the orchestrator refuses to touch the install without it. RELEASE when the run is done.
- The game must not be running. The orchestrator polls until no `Bannerlord`, `Bannerlord.Native` or `Watchdog` process runs, and until it can open `C:\Users\davgo\Documents\GitHub\MNBL-modding-repo\build\battle-scale\native-test.lock` with exclusive sharing. It holds the lock until the run ends and never stops a process it did not start.
- The run tag (`-RunId`, letters and digits) names every save the probe writes (`mnbl_acc_<RunId>_…`). Use a new one for each run.

## Commands

```powershell
# the whole run (about 3 to 3.5 hours; see the phases below)
./tools/Run-Map2Acceptance.ps1 -RunId acc1 -Claim "CLAIM sent 2026-10-21 09:00 to the battle-scale session, OK received"

# offline only: the build, the offline tests
./tools/Run-Map2Acceptance.ps1 -RunId acc1 -Phases build,preflight

# one phase again (a phase already done is skipped unless -Redo names it; the launch saves under its own tag, acc1r<attempt>)
./tools/Run-Map2Acceptance.ps1 -RunId acc1 -Phases world1 -Redo world1 -Claim "..."

# world 1 again from its newest good save, skipping the steps that already finished and the steps a game hung in, then every phase after it
# again (after a restore it deploys again first and puts the run's saves back into Game Saves)
./tools/Run-Map2Acceptance.ps1 -RunId acc1 -Phases world1,world2,mods,restore,summarize -Resume -Claim "..."

# steps left out of a launch (probe step names, or prefixes with *)
./tools/Run-Map2Acceptance.ps1 -RunId acc1 -Phases world1 -Redo world1 -Skip w1.siege,w1.battle.* -Claim "..."

# grade again (after an attestation, say; the committed attestations and balance pass are read by default)
python tools/summarize_map2_acceptance.py --run build/acceptance/acc1

# the hang repro alone from a decal lab's save (MMR-247 K): one launch per variant, a full dump of each block, its analysis
./tools/Run-Map2Acceptance.ps1 -RunId acc5rep -Phases build,deploy,hangrepro,restore -LabSave mnbl_acc_acc5lab_w1_lab -LabSaveFrom C:\Users\davgo\m2\int\build\acceptance\acc5lab\saves -Claim "..."

# read a dump again (or any full dump of a hung .NET Framework process)
./tools/Analyze-HangDump.ps1 -Dump build/acceptance/acc5rep/logs/hang-<pid>.dmp
```

Other switches: `-Seed1 42 -Seed2 73`, `-SoakDays 30`, `-BaselineDays 30`, `-Frames 600`, `-ForceVsyncOff` (MMR-249: it did not lift the 60 fps cap), `-NoDeploy` (run the installed build), `-CleanStore` (restore also removes the store entries the run added), `-FreshMapAfterMission` (world 1's land cases reload the save after a mission, MMR-247 H's isolation; off by default, c32 needs it off). `-Redo w1.battle.forest` (a step name, or a prefix with `*`) lets a resume run a step a game hung in again.

## Phases

| Phase | What it does | Expected time |
|---|---|---|
| `build` | `tools/Build.ps1`: the one build that runs (its receipt is what Deploy-Dev verifies) | 6–10 min |
| `preflight` | the commit and build hashes; `Test-GameUpdates` (MMR-284, c23), `Test-WorldStore` (MMR-282, c21), `Test-Map2Acceptance` and the summarizer's unit tests, evidence into the run folder | 2–4 min |
| `deploy` | backs up `Modules\MNBL.ProceduralWorld` (every file but the `SceneObj\mnbl_world_*` folders that exist already, which are tree-hashed instead), lists the saves, the store, the engine config and the terrain shader caches; `tools/Deploy-Dev.ps1`; the tracked `evidence/runtime/build-api-inputs.json` put back as committed | 1–2 min |
| `baseline` | launch 1, `phase=baseline`: the official War Sails sandbox (Calradia, empire, through the diagnostic bootstrap): frames at the two fixed cameras, memory after one day, the economy before and after `BaselineDays` | 10–15 min |
| `world1` | launch 2, `phase=world1`: below | 75–110 min (the first start builds the caches: about 6.5 min for A') |
| `world2` | launch 3, `phase=world2`: seed 73, its census and water, saved; then world 1 continued from its soak save | 35–50 min (B's first cache build: about 14 min) |
| `mods` | builds and deploys the synthetic module; launch 4, `phase=mods` with it enabled; removes it whatever happened | 15–25 min |
| `restore` | the synthetic module removed; the installed module put back file for file (the run's world scenes removed: the store's `world-scenes` archive keeps copies); the run's saves moved into `build/acceptance/<RunId>/saves`; `engine_config.txt`'s `safely_exited` line put back; the terrain shader caches of the run's scenes removed; the lock released | 1–3 min |
| `summarize` | `acceptance.json` | seconds |

Each launch is one game process (`tools/Start-OwnedProbe.ps1`), waited for with a hang rule: its CPU time flat for 5 minutes (`-HangFlatSeconds`; a running game renders and uses CPU, and the navigation cache build keeps it busy; "not responding" is recorded but not required, since `Process.Responding` reads true for a game without a visible window; the `hangrepro` launches also stop after 90 s flat with the game not responding in every poll), or past its deadline. A full memory dump of the game is written first (`logs/hang-<pid>.dmp`, MMR-247 K, below). Then this run's own game is ended with `taskkill /F /T /PID` (the MMR-49 hang survived `Stop-Process`; `/T` also takes its Watchdog child), and `Stop-Process` if it still runs. If it survives both, the run stops at once with the PID to end (then `-Phases restore,summarize -Redo restore`), instead of every later phase waiting for a free game. Each launch is collected into the run folder (`tools/Collect-OwnedProbe.ps1 -Destination`), its engine log kept. The native DLL hashes are taken before and after every launch and are never restored. A launch refuses to start when the install was restored after the last deploy.

Every launch of a phase after its first saves under its own tag (`<RunId>r<attempt>`), so a redone world 1 or world 2 never finds its earlier attempt's saves (`SaveStep` refuses to overwrite one). The summarizer keeps the `w1.start` / `w2.start` row that measured the cache build when a later start reused the caches.

`-Resume` (world 1 only) loads the newest good save: `save_w1_soak` when the soak unit finished, else `save_w1_cast` when the cast unit's Continue finished, else `save_w1_day0`. It skips the steps an earlier attempt finished, except the load, `w1.identity` and `w1.faces`, and the steps a game hung in (`state.hungSteps`) unless `-Redo` names them. The cast, its save and the Continue are one unit (the Continue loads this attempt's cast save), as are the soak and its save: a unit is skipped only when its last step finished. The cast save is passed as `castSave=` for `w1.battle.mutated`.

**Hangs (acc4).** A native hang blocks the engine thread at 0 CPU, so no in-process timeout can catch it; acc4 lost everything after `w1.battle.forest`. Three guards now:

- **The order.** World 1 runs its core first (no mission, no encounter), the risky tail last (below), so one hang in a mission can no longer lose the core.
- **In process.** Every step has a wall-clock budget (`Map2AcceptancePlan.Budget`: conversation 90 s, a field battle 300 s, the zone battle 600 s, a settlement case 300 s, the siege 1800 s, the season 2700 s, the soak 1800 s, the cast and the Continue 1200 s; `budgetScale=` scales them). Past it the step ends as `timeout`: its iterator is disposed (the finally blocks end its mission, clear the encounter, put parties back, make peace) and the campaign is recovered (`recover`: the mission ended, the encounter cleared, menus and the settlement left, then, if the map is still not idle, the land cases' save loaded again). A step that finds the map not idle tries the same recovery before it is blocked.
- **Out of process.** The probe writes `map2_acceptance:<step>` at each step's start and `map2_acceptance:<step>:end <status>` at its end to its progress log, which is flushed per line. When a launch is stopped as hung (CPU flat, or past its deadline), the orchestrator reads the log: the step with a start line and no end line is `launch.progress.hungStep`, recorded in `state.hungSteps`. World 1 is then launched again from the newest good save, with the finished and hung steps skipped (at most 3 relaunches). `phases.world1.detail` lists each attempt (`pid`, `stopped`, `killed`, `hungStep`, `lastStage`, `lastStageUtc`, `fromSave`, `skip`).

Inside a field battle's setup each native call is preceded by a breadcrumb stage (`acc_missions:<case>:<call>`: `rebuildWait`, `teleport`, `enemy`, `leaveSettlement`, `declareWar`, `findPoint`, `placeEnemy`, `isolate:<party>`, `settle`, `snapshot`, `restartEncounter`, `startBattle`, `menu`, `attack`, `returned`, `finish`, `idle`, `hour`, `settleAfter`, `restore`), so a repeat of acc4's hang names its call. The two suspects of acc4's hang are out of the setup: the teleport waits until the map has rebuilt after the last mission (no shader compiling, the scene loaded, 120 frames, the camera settled; 60 s at most), and the enemy's point and the isolation targets come from `FindPointAroundPosition` without its native path search (`requirePath: false`, kept on the origin's navmesh island). Each case takes a fresh enemy (not the previous case's party or faction where another fits), and one campaign hour runs after the encounter is finished so the old map event is removed before the next case. The battle points lie 3 units clear of water cells (no river, bridge or deck beside them).

**Hangs (acc5).** acc5 blocked the engine thread three more times, again CPU flat: `field.forest:settle` (after the snow battle), `castle` (after the town's two missions) and `siege` (after the village's), each in the first frames of a case that teleported the player and changed its diplomacy (a war declared, a castle given to it) on a map that had already run a mission since it loaded. The same actions with no mission since the map loaded (the snow, settlement and mutated battles; MMR-52's 17 cases on the pre-epic build, wars after missions included) never blocked. The call is not proven (results/MMR-247-H.md). Two changes:

- **A fresh map per case (off by default since MMR-247 X).** With `FreshMapAfterMission` (probe argument `freshMapAfterMission=true`, runner switch `-FreshMapAfterMission`), a land case that would start on a map where a mission has run since it loaded first loads the land cases' save again (`freshMapBefore` in the case row: the reason and the load; stage `acc_missions:<case>:freshMap`). The castle is not given back after its mission then (`givenBack: skipped`), since the next case reloads. Peace after a mission (each case's finally) stays: acc5 made peace after two missions without a block. The rule was MMR-247 H's isolation of the acc4/acc5 freezes; MMR-247 X found their cause (moved ruin markers writing through stale occluder records, re-created now instead), so the run takes the cases after missions without it and c32 grades that. Every case row records `missionOnThisMapBefore` (a mission ran on the loaded map before the case, before any reload) and the probe's outputs `freshMapAfterMission`.
- **A crash.** A launch that exits by itself with a probe step open crashed in it: the launch keeps `progress.openStep` and `crash` (the Windows Application log's fault event: module, exception code, offset), `state.crashedSteps` lists it, and a world1 launch is launched again from the newest good save with the crashed step skipped, as after a hang. The summarizer lists it under `crashes` and fails the step.
- **A hang capture.** While a harness probe runs, `Automation/HangWatchdog` watches the application tick from a background thread. After 60 s without one it writes `hang-<pid>-<k>.txt` in the autorun folder (the stall, the probe's last stage, the MNBL section the engine thread was in (`EngineMarks`: the probe step, the map dressing and atmosphere ticks, the camera's image refreshes), the engine thread's managed stack, walked while it is blocked) and `hang-<pid>-<k>.dmp` (every thread's native stack, modules, handles; no heap; a few MB), at most three per process. A stall that ends (a first start's navigation cache build) is marked `resumed` with its length. `Collect-OwnedProbe.ps1` copies them as `in-game/<probe>-<pid>-hang-<k>.*`; the summarizer lists them under each hang (`captures`). `tools/Test-HangWatchdog.ps1` checks it offline on a thread blocked in a managed wait and in native code.

**Map incidents.** An incident (1.4.8: due on `MapState.NextIncident`, then shown by `GauntletMapIncidentView`, which stops time and pauses the engine) is not idle: `Idle`, `AdvanceDays` and the land cases' `Idle` drop a due one and confirm an open one as a player does (its first option, `MapIncidentVM.ExecuteConfirm`), recorded under `incidents`.

## The probe's steps

Every step records `done`, `error`, `skipped` or `blocked` (with the reason) and what it measured; the summarizer grades. A step that needs the map first makes it idle: a conversation, the map conversation and the encounter ended in that order, an open encyclopedia closed (it keeps the map state disabled), menus left, an open mission ended, time stopped.

Campaign time (the memory day, the season legs, the soak, the baseline economy) runs in fast forward with the player sheltered and fed. A clock that does not move for 15 s is restarted and counted; after 2 minutes without progress the advance ends recorded as `frozen` (with the time state) and the next step runs. An advance also ends after 40 s of wall clock per requested day plus 2 minutes.

**world1** (the ticket's step 2, then 3), in `Map2AcceptancePlan.World1Core` then `World1Tail` order: the core (`identity` to `saveSoak`, no mission or encounter), then the risky tail (`conversation`, the battles, the settlement cases, the siege last):

| Step | What |
|---|---|
| `w1.start` | the setup screen: seed 42 typed and confirmed, the preview's CountsText (the per-world wait estimate, MMR-282) and the model's seconds; Start Campaign, character creation as a player drives it; the start timed; the cache build read from the map's acquired caches (`generatedNavigationCaches`) |
| `w1.identity` | the world's binding, scene and bundle; the patch signatures (MMR-284), the speed model chain (MMR-254), the incident transpiler (MMR-287), the wanderer tick and minor-faction marking (MMR-281), map trees, the atmosphere driver and snow texture (MMR-256), map dressing, magic ground, the mutation state and the console commands (MMR-261), the ruins page registration |
| `w1.census0` | every settlement, kingdom, clan and hero of the world, the population behaviour's census, the economy, each culture's home terrain, every wanderer's backstory lines |
| `w1.save0` | the day-0 save (the land cases reload it when one leaves the campaign unusable) |
| `w1.frames`, `w1.memoryDay` | the fixed cameras (following the party, time stopped, distance 30 and 100; 600 frames each: frame interval, main-thread and renderer fps), memory before and after one day |
| `w1.faces` | every navmesh face by face type; the most interior free face of each type (and a road cell): same-kind travel cells in the 5 x 5 cells around the face (faces are about one per cell, so acc4's same-type faces in 3-unit buckets put every pick in the dense cluster beside a town) |
| `w1.biomes` | a screenshot per land biome of the shared table present on the world (its most interior spot on the bundle's biome plane), 40 face samples per biome (information; `invalid` off land); the face read at the centre of up to 50 land travel cells per land type (`cellSamples`: class, cells.bin land type, face, face index) |
| `w1.relief` | two mountains, two port coasts, the pad slope of every town and castle, the camera aimed past each pole line |
| `w1.water` | MMR-285's `WaterVisibilityProbe` (lake planes, water decals, every impassable water cell, three screenshots), MMR-291's overlay mesh record (`overlayMeshes`) and the river meshes at the densest river cluster shown and hidden from the free camera (`river-mesh`, `river-mesh-hidden`, `toggle`; c24, c33), then every road and magic decal centre tested for water |
| `w1.dressing` | roads, bridges and ruin markers drawn against the plan, every settlement's distance to its nearest road decal, screenshots of a road at a town gate, a bridge, a ruin marker |
| `w1.magic` | generated magic ground drawn, no cell retyped before a cast; the densest cluster (`magic-ground`) and the densest clusters of two other schools (`magic-zone-1`, `-2`) screenshotted (c29), the zones taken from `MagicGroundRuntime.Plan` (the decal installer holds no land stamps while MMR-291's meshes draw the veins); MMR-291's overlay mesh record and the densest zone's vein meshes shown and hidden from the free camera (`magic-veins`, `magic-veins-hidden`, `toggle`; c29, c33); MMR-288's vein graph record; MMR-289's features drawn, none on water, a road decal or within 5 units of a settlement, one screenshotted (`magic-features`, c30). The magic modules are read by reflection: a build without them records "not in this build" |
| `w1.magicWater` | MMR-290's `MagicWaterRuntime.Record()` (with MMR-291 it speaks for the magic water meshes) and the overlay mesh record, the camera at 38 over the densest generated magic water cluster (`magic-water`), the stamps or meshes hidden (`SetVisible(false)`) and the same view (`magic-water-hidden`), shown again (c31, c33); blocked on a build without MMR-290 |
| `w1.looks` | a town, a castle and a village per culture close up; the initial wall levels |
| `w1.speed` | the speed tooltip's explained numbers on each face type (off road cells) and on a Plain or Steppe road cell; the world's land types (`cellTypes`) |
| `w1.seam` | a crossing of the seam from one land biome into another (the shortest such crossing on land), the track, both sides' speed numbers and screenshots |
| `w1.incidents` | the four culture incidents' conditions run with the player's last visited settlement one of a kingdom of the culture; every incident condition that reads the face type, its face types, evaluated at a Desert, Forest and Steppe face |
| `w1.ruinsPage` | the ruins page's rows against the plan, opened through the encyclopedia's link, a screenshot |
| `w1.season` | the season run to its first extreme and then the other (at most 90 days each); at each, time run on to midday ([11, 13)) while the extreme holds, then the same cameras over a cold region, its mirror, a warm region and land ice, each shot waiting up to 60 s for the scene to settle (`hour`, `settled` recorded); the weather event (`GetWeatherEventInPosition`: Clear, LightRain, HeavyRain, Snowy, Blizzard, Storm) at those places every day, with the terrain effect beside it (1.4.8 has only Default and Wet); each place's weather-node snow byte and its yearly Snowy share (`snowBytes`, `snowyShareExpected`); winds by latitude |
| `w1.cast` | `mnbl_magic.cast fire Nerdun` (the console command's code), the background `fwg apply`, the overlay before and after, the retyped cells' face types; screenshots before, right after the change, mid-reveal and fully grown (`cast-before`, `cast-after`, `cast-mid`, `cast-full`: MMR-288's veins grow out over its reveal time, else 24 h and 48 h); MMR-289's features of the cast. No battle (it is `w1.battle.mutated`) |
| `w1.saveCast`, `w1.continue` | the cast saved, the game ended, the save continued from the main menu: no generator process, the caches reused, the mutation as saved; the forecast with the caches hidden (MMR-282's warning); MMR-289's features and MMR-290's water after the Continue |
| `w1.soak`, `w1.saveSoak` | `SoakDays` days (sheltered, grain), every kingdom and house change and mercenary contract heard, the census, economy and home terrain again; saved (the risky tail's reload save) |
| `w1.conversation` | (risky tail) a lord at peace met on the map (its meeting opens the map conversation), a screenshot, left as a player leaves it |
| `w1.battle.snow`, `.forest`, `.settlement` | MMR-52's field battle at a dry Snow face, a dry Forest face (preferring a point whose battle cell the resolver classes as the type's biome class) and beside a non-port castle (else village) |
| `w1.battle.mutated` | the cast save loaded again, MMR-52's field battle at the first free retyped cell of its overlay |
| `w1.town`, `.castle`, `.village`, `.siege` | MMR-52's cases: the entered scene against the mapped one; the siege (last) with a map screenshot of the siege state (see "The siege case") |

**The siege case (MMR-247 F, acc6).** acc6's garrison (194) sallied out against the 150-troop camp 2.06 campaign hours after it went up, before the camp advanced to 0.99 at 1.5 hours completed (0.94 hours more at the installed 0.0106 per hour): the stock `SallyOutsCampaignBehavior` checks a besieged fortification in every fourth campaign hour and sallies when the defenders' strength exceeds twice the besiegers'. The case now (`LandMissionsProbe.Siege`; pure rules in `Map2AcceptancePlan`, tested by Test-Map2Acceptance; values in `LandMissionScenarios.json siegeSetup`, apart from `rules`, which MMR-52's recorded fixture must match):
- **Times each attempt before the next check hour.** The camp's own preparation is observed for `siegeObserveHours` (1.5), shortened to end `siegeCompletionHours + siegeTimingMarginHours` (0.1 + 0.15) before the check hour, never under `siegeObserveMinHours` (0.25: MMR-52 requires the camp to build by itself first); then it is advanced to what the installed rate builds in its last `siegeCompletionHours` (never below `siegeCompletionFrom`), and the engine's tick completes it (and creates the siege object). When even the shortest observation does not fit, campaign time first runs at peace, before the war and the camp, until the check hour has passed (`waitedForSallyCheck`).
- **Keeps the garrison in.** Before the camp, the besieger is topped up with its culture's basic troop until twice its strength is at least `siegeSallyMargin` (1.5) x the defenders' (the settlement's SallyOut parties, as the engine computes both), at most `siegeCrewLimit` troops (`sallyGuard`: both strengths, the troops added).
- **Retries once.** An attempt that does not prepare is ended by peace (which ends a sally-out or relief and the siege, as acc6's own end did at `hostile_action_end_by_peace`), its menus passed until the party is idle (`endedForRetry`), then the next castle in order is besieged; `siegeAttempts` (2) in all. `setup` is the last attempt's (the summaries read it) with `setup.attempts` listing every attempt.
- **Always takes the siege shot when a siege stands.** `siege-map` is taken once per case: when the camp is prepared; else when an attempt ends unprepared with its siege standing and no map event; and on the retry's camp as soon as it is up, before any campaign time runs (the record says which, and whether the camp was prepared).

**world2:** `w2.world1Before` (world 1's scene and caches), `w2.start`, `w2.identity`, `w2.census0`, `w2.water`, `w2.save0`, `w2.world1After`, `w2.continue1` (world 1's soak save continued), `w2.after1`.

**mods:** `mods.modules`, `mods.setup` (the setup screen's host problems; when it allows, seed 42 generated to review), `mods.load` (world 1's save through the load screen; the installed different-modules question, heard on `InformationManager.OnShowInquiry`, answered Yes; if the helper's question is open and was not heard within 5 s, its `LoadGameAction` is called directly, as its Yes does; loaded, or refused naming the module, before any cache build).

## Decal lab (MMR-247 D, after acc5)

acc5 drew 2,826 decals with the newly published module materials (water 343, veins 775, magic water 1,708) and none showed. The static cause
(C:\Users\davgo\m2\results\MMR-247-D.md): the campaign map draws scene decals from Native's prebaked atlas `decal_atlas_worldmap` only, at the
rects of Native's `ModuleData/decal_textures_worldmap.xml`, which the engine reads from Native alone, so no module texture can draw there; the
fix draws vanilla fallbacks (`roads.json decalAtlas`, `magic.fallback`). One launch confirms it on world 1:

```powershell
# a separate run id; build and deploy this checkout, generate world 1 (seed 42), run w1.decalLab and the hang repro, put the install back
./tools/Run-Map2Acceptance.ps1 -RunId acc5lab -Phases build,preflight,deploy,decallab,restore -Claim "..."
```

Phase `decallab` (not a default phase) launches `map2_acceptance phase=decallab school=<-LabSchool, fire>` with a world:

- **`-LabSave <save>`** (or a world 1 save this run made): `fromSave=<save>`, step `w1.load`. The save must load on this build. **acc5's saves
  do not** (default taken at the integration, 2026-10-21): the snow route's switch to `permanentWhite` changed the climate identity, and the
  loader refuses a save whose rebuilt climate is not the saved one (`GeneratedWorldLoader`, "climate"). A missing world scene is not the
  obstacle: the loader takes it from the store's `world-scenes` archive, where acc5's restore left it.
- **No save** (the default): world 1 is generated from the New Campaign screen as the world1 phase does (`seed=<-Seed1, 42>`, step
  `w1.start`, the first start's navigation cache build included, about 7.5 min in acc5), then saved (`w1.saveLab`,
  `mnbl_acc_<RunId>_w1_lab`). Its scene folder and save go in `restore` like world 1's (the store entries stay unless `-CleanStore`).

Then `w1.decalLab` (`Automation/Map2AcceptanceDecalLab.cs`; budget 900 s). At the flattest dry spot clear of the world's own decals and at the
nearest deep sea spot it draws a grid (`Map2AcceptancePlan.LabLayout`), columns west to east, rows south to north (nearest the camera first):

- columns: `vanilla` (`mainmap_decal_undervillage`, in the atlas, W's tint), `vanilla+waterTexture` (a copy of it with `mnbl_water_surface_d`
  as its diffuse), `water` (`mnbl_water_surface`, its river cell), `water+vanillaTexture` (a copy of it with the vanilla stamp's diffuse),
  `magicWater` (`mnbl_magicwater`), `magic` (`mnbl_magic_<school>`), `stroke` and `strokeTurned` (the vein fallback, `decal_khuzait_path_a`
  tinted, at yaw 0 and turned a quarter);
- rows: `full` (atlas [1, 1, 0, 0]), `cell` (the column's cell), `factor` (SetFactor1), `fit` (W's FitBox pool box, clipped), `clip` (half
  depth 3, clipped), `plane` (z the sea datum, half depth 0.2: MMR-290's sea stamps), `large`.

**Mesh grid (MMR-291, overlay meshes).** After each spot's decal grid is removed, a second grid on the same spot draws each overlay mesh
layer's material on test pieces through `OverlayMeshRuntime`'s own engine target: columns `mesh:vein` (the published `mnbl_overlay_vein_<school>`
when it resolves, else the runtime copy of vanilla `torch_flame_base` with `mnbl_magic_<school>_d/_n/_s`), `mesh:river` (`naval_worldmap_water`
on a ribbon), `mesh:magicWater` (the copy with `mnbl_magicwater_d/_n`). acc6lab ran MMR-291's rows (full, cell, factor, plane, large): the
copies drew on land, nothing on the sea (plane row included), the river ribbons drew over the sea. **MMR-291-W rows**
(`Map2AcceptancePlan.LabMeshRowDefs`, recorded as `meshRows`): the passes against the water (`docs/overlay-meshes.md` "Over the water"): `blend`
(the copy as it is: the A), `depth` (depth write and alpha test: magic water's default, the B), `under` (depth, 0.05 under the surface),
`forward`, `afterPostfx`; each the vein or magic water cell piece (material key `<key>@<pass>`) on the spot's surface (the ground at the land
spot, the sea datum at the sea spot) plus the layer's lift; the river column's ribbon on the ground in every row is the control. At the sea spot
the camera aims at the sea datum. Shots `lab-<spot>-mesh-<close|map|far|hidden>`; the record holds each piece (pass, z, drawn, vertices,
triangles), the target's material records (`meshMaterials`: published or copy, template, textures, shader, blend, and per pass the flags and
alpha test before and after) and `passReferences` (the flags, blend, shader flags and alpha test of `torch_flame_base`, `worldmap_cloud`,
`wm_clouds`, `crosshair_material`, `naval_worldmap_water`, `worldmap_water`, `beards_h`, `thatched_roof_b_alpha`). The sheet reads them as
spots `land-mesh` and `sea-mesh` (rows `meshRows`; `meshPasses` the policy's passes). Readings: a pass drawn at the sea spot shows magic water
over our sea; c33's lab part fails when magic water's own pass row (`overlayMeshes.magicWater.pass`) draws nothing there. If `depth` fails and
another pass draws, set `magicWater.pass` to it; if none draws, magic water falls back to MMR-290's shore modes (`magicWater.mode` decal).

Shots `lab-<land|sea>-<close|map|far>` (distance 16, 38, 60) and `lab-<spot>-hidden` (38, the lab hidden); each records every lab decal's
viewport point. Then `fix-river`, `fix-magic-ground`, `fix-magic-water` at the acc5 places with the fix. The record names each material's
shader, diffuse texture and atlas residency, each lab texture's size and load state, and the fix's target records. The phase then runs
`tools/decal_lab_sheet.py` (labelled PNGs, contact sheets, and per decal the mean difference against the hidden shot: drawn yes / no) into
`build/acceptance/<RunId>/decal-lab/`.

Readings: atlas columns (`vanilla`, `water+vanillaTexture`) draw and module-texture columns (`vanilla+waterTexture`, `water`, `magicWater`,
`magic`) do not: the atlas is the cause and the fix stands. Module textures draw: the atlas is not, and the rows say which frame, factor or size
hid them. `fit` blank where `clip` draws: W's tight water boxes miss the rendered terrain (set `roads.json water.minHalfDepth`). The sea
grid's `plane` row: whether the water shader draws decals on our scene (MMR-290's R1). `stroke` / `strokeTurned`: which way the vanilla
stroke runs (the fallback turns it a quarter; the road decals do not).

**The hang repro (MMR-247 H).** After `w1.decalLab` ends (its record checkpointed, its shots checked on disk: `hangRepro.labShots`,
`labShotsMissing`), the launch runs the deliberate repro of the acc4/acc5 blocks with the hang capture on (`hangRepro.hangCapture`) and the
fresh-map rule off (`FreshMapAfterMission = false`). Each variant is its own step (budget 900 s), from a map with no mission since its load
(the lab's save loaded again before the second, `freshMap`):

- `w1.hangRepro.war`: one field battle (`FaceBattle`: a dry Snow face, else Plain, Steppe, Forest; war on the enemy's kingdom, the battle,
  peace, one hour, as acc5's snow battle), the map rebuilt (`MapRebuilt`), the player teleported about 256 units (`Map2AcceptancePlan.FarPoint`,
  at least 150) and war declared on the kingdom at peace with the most settlements other than the battle's (the forest case's order);
- `w1.hangRepro.castle`: the same battle, then the first peaceful castle by id given to the player and the player teleported to its gate (the
  castle case's order).

Then the camera settles and 300 frames run (a stage `map2_acceptance:<variant>:frames <n>` every 30); `ran` with `blocked: false` when they
do. Nothing is undone after (no peace, no castle given back), so the only diplomacy change after the battle is the block's own. If the engine
blocks, the orchestrator's CPU-flat rule ends the game (5 minutes); HangWatchdog's `hang-<pid>-<k>.txt`/`.dmp` (written after 60 s) are
collected as `in-game/map2_acceptance-<pid>-hang-<k>.*`. `Run-Map2Acceptance.ps1` records that as the variant's outcome (`decallab` phase
detail `hangRepro.<variant>`: `engine blocked`, the stop, the last stage, the captures), not as the lab's failure: the phase is `done` when
`w1.decalLab` is. It launches again from the lab's save (at most twice, `w1.decalLab` and the variants with an outcome skipped) for the
variants left. The acceptance summarizer ignores the lab's launches (its hangs are deliberate).

**acc5lab and what K added (MMR-247 K).** acc5lab reproduced both variants: each blocked within about 1 s of `settle` (war: pid 33368,
castle: pid 26860), CPU flat. HangWatchdog wrote no capture for either (only the `resumed` one of the first start's cache build), so the
in-process watchdog thread had stopped too: most likely the whole managed runtime was held (a runtime suspension that never ended, or every
thread frozen). Its in-process minidump cannot see that, so the orchestrator now dumps the game from outside:

- **A full dump before every kill.** Before `Run-Map2Acceptance.ps1` kills its own game (CPU flat or the deadline), `tools/Write-ProcessDump.ps1`
  writes `logs/hang-<pid>.dmp` with the dumper Windows ships (`rundll32 comsvcs.dll, MiniDump <pid> <file> full`; started without the shell,
  since through `Start-Process` comsvcs ignores `full`; it waits until the file stops growing, checks the full-memory flag, and gives the user
  full control of the file, whose DACL comsvcs sets to SYSTEM and Administrators only). Just before the dump it records each thread's state and
  wait reason (`Wait/Suspended` = held by SuspendThread or a debugger) and whether a debugger is attached. Then
  `tools/Analyze-HangDump.ps1` reads it (below) into `logs/hang-<pid>.dmp.analysis.txt` and `.json`; the launch record keeps `wait.dump` and
  `analysis` (its findings). `-NoHangDump` and `-NoDumpAnalysis` turn them off. A dump is about the game's memory (several GB).
- **The repro alone (`hangrepro` phase).** One launch per variant (`-ReproKinds`) from a lab save (`-LabSave`, copied into Game
  Saves from `-LabSaveFrom` when missing and moved out again in `restore`), the probe's `decallab` phase with `w1.decalLab` and the other
  variants skipped. Its launches are stopped after `-ReproFlatSeconds` (90: the in-process capture at 60 s goes first) of flat CPU with the
  game not responding in every 5 s poll; the 5-minute rule stays as the fallback. The phase detail `hangRepro.<variant>` adds the dump and the
  analysis. Command and reading: `C:\Users\davgo\m2\results\MMR-247-K.md`.

**What acc5rep showed and the bisect (MMR-247 X).** The "hang" is a crash: both acc5rep variants faulted in the first frames of `settle`
(`c0000005` in `TaleWorlds.Native.dll` at offset `0x55e625`, Application log event 1000), then the game's Watchdog failed to write its dump
(`DirectoryNotFoundException` on the crashes folder). The fault is the software occlusion rasterizer writing a moved entity's frame through
a stale occluder record (`docs/map-dressing.md` "Seam"; `C:\Users\davgo\m2\results\MMR-247-X.md`). The probe's variants
(`Map2AcceptancePlan.HangRepros`) set three switches for their launch, put back after the variant: the map dressing's bridges and markers on
a new image (`MapDressingInstaller.EntityImages`: `Recreate` the fix, `Move` the pre-fix control, `Frozen` none), the magic layers' refresh
(`MagicGroundRuntime.RefreshEnabled`) and the display copies' sync (`PeriodicMapPresentation.CopySyncEnabled`):

| variant | dressing entities | magic refresh | copy sync | expected if the dressing entities are the cause |
|---|---|---|---|---|
| `w1.hangRepro.warControl` | Move | on | on | crashed |
| `w1.hangRepro.war` | Recreate | on | on | ran (the fix) |
| `w1.hangRepro.warNoDressingMoves` | Frozen | on | on | ran |
| `w1.hangRepro.warNoMagic` | Move | off | on | crashed |
| `w1.hangRepro.warNoCopySync` | Move | on | off | crashed |
| `w1.hangRepro.castle` | Recreate | on | on | ran (the fix) |
| `w1.hangRepro.castleControl` | Move | on | on | crashed (not in the default list) |

`-ReproKinds` defaults to the first six. The decal lab's own launches run `war` and `castle` only. Each variant writes progress lines for a
crash's reading: `switches ...`, `sceneView enabled <bool> framesSinceActivated <n>` after the map rebuild (enabled: `Scene.CheckResources`
ran since the battle), and `dressingRefresh moved <n> (entities <n>) recreated <n> frozen <n>` for each dressing refresh that changed
something. A game that exits by itself with a variant's step open is `crashed` (the launch's `progress.openStep`), with `crash`: the
Application log's fault event for its process id (application, module, module time stamp, exception code, fault offset) and the Watchdog's
exception line when it faulted too. The summarizer leaves the `hangrepro` launches out of the run's hangs, as it does the lab's.
- **The analyzer** (`tools/hangdump/HangDump.cs`, built by `tools/hangdump/Build-HangDump.ps1` into `build/hangdump/`): it hosts the debugger
  engine in `C:\Windows\System32\dbgeng.dll` and loads the SOS beside the dumped process's `clr.dll` (nothing installed), runs `~`, `kn`,
  `!ip2md`, `!threads`, `!threads -special`, `!syncblk`, `!dso` and `!clrstack`, and leads with its findings: Monitor deadlock cycles (owner
  from `!syncblk`, waiter = a thread in `Monitor.Enter`/`Wait` with a locked object it does not own on its stack), a GC or runtime suspension
  in progress (the `SuspendEE` thread) and the cooperative-mode threads it waits for, exceptions in flight (managed, and the native dispatch
  path), threads stopped outside a system call, `PEB.BeingDebugged`, and the engine thread's native top frames as `module+0xRVA` (what REA
  reads; a name only where it can be trusted: a PDB, or an export within 0x1000). The engine thread is the one in
  `TaleWorlds.DotNet.Managed.ApplicationTick`, else the one through the 1.4.8 frame loop `TaleWorlds_Native+0x457bc`, else thread 0, unless
  `-EngineThread` names it (the in-process capture's native id is passed when there is one). No symbols by default (no network);
  `-SymbolServer` fetches Microsoft's PDBs into `build/symbols` (names clr's GC and ntdll's lock functions).
  `tools/Test-HangDump.ps1` checks it on a process it blocks itself: a Monitor deadlock (named, with both locks), a native wait, a GC whose
  suspension never completes (the runtime stopped: the suspending thread and the cooperative thread it waits for), an exception held
  mid-dispatch, and the runner's own hang rule, dump, kill and analysis.

## What to look at in the screenshots

The summarizer grades what it can measure. These need eyes (the owner's review, step 7):

| Label | Look for |
|---|---|
| `A-preview`, `B-preview` | the preview and its wait text (`about N minutes`) |
| `biome-<biome>` | the biome's own layers (desert sand, grass, forest with map trees, marsh, rock, tundra, white land ice); no layer bleeding from a neighbour biome |
| `mountain-<n>`, `coast-<n>` | smoothed relief: no blade ridges, spikes or sea cliffs; beaches meet the water |
| `pole-north`, `pole-south` | the view stops at the pole line, no polar padding |
| `water-lake`, `water-river`, `water-coastFill` | lakes and rivers read as water; fill stamps on coasts read as shallows, not paint |
| `road-at-town`, `bridge`, `ruin-marker` | roads as lines to the gate, a bridge at its deck, a ruin marker on land |
| `magic-ground`, `magic-zone-<n>`, `cast-before`, `cast-after`, `cast-mid`, `cast-full` | the school's stamps from the published packs (not blank, not tiled squares); veins fanning out from their sources, not round splotches (c29); the zone spreading out from Nerdun after the cast |
| `magic-features` | school-specific features standing in a generated zone (c30) |
| `magic-water`, `magic-water-hidden` | magic water over magical water continuing the land veins; the second with the stamps hidden (c31) |
| `magic-veins`, `magic-veins-hidden`, `river-mesh`, `river-mesh-hidden` | MMR-291's vein meshes (veins thinning to their tips, forks blended, no square quad edges) and river meshes (rivers read as water, not paint), each shown and hidden (c24, c29, c33) |
| `look-<culture>-<kind>` | culture-correct pieces at about half scale; a village's production pieces for its type |
| `siege-map` | the besieged castle with its siege camp and wall pieces |
| `seam-west`, `seam-east` | the two biomes on either side; nothing drawn twice or missing at the cut |
| `map-conversation` | the map conversation open over the map |
| `ruins-page` | the ruins list with names, civilizations and causes |
| `season-<extreme>-<place>` | cold region white in winter and not in summer; warm region not whitened in winter (it may get darker: the winter grade and cloud shade); land ice white in both; the mirror region (the opposite hemisphere) for orientation |
| `frames-near`, `frames-far` | the fixed cameras the frame times were taken at (same in vanilla) |

## Grading (`evidence/map-epic-2/acceptance.json`)

Every ticket step (1, the 17 checks of step 2, 3–7) and every criterion c1–c33 is `pass`, `fail`, `blocked` or `not-run`, with the details that decided it. A step that timed out grades as `fail`, as does the step a game hung or crashed in (unless a later attempt finished it). A crash is listed under the top-level `crashes` (phase, pid, step, last stage, the fault event). A hang is reported: the top-level `hangs` list (phase, pid, how it stopped, the hung step, the last stage, the last engine log line), `phases.<phase>.detail` filled in when the run left it empty, and each `not-run` item prefixed `not run: world 1 hung at <step> (<last stage>)`. Rules that cannot be measured come from the attestations: the committed `evidence/map-epic-2/attestations.json` (the owner's approval of 2026-10-21 for c12, c29 and c31, quoted; MMR-262's review pass for c15), then the run's own `build/acceptance/<RunId>/attestations.json`, whose keys replace the committed ones; `--attest FILE` (repeatable) names the files instead. `acceptance.json` lists the files read under `attestations`. The format:

```json
{ "c12": { "status": "pass", "by": "owner", "note": "magic textures reviewed against the concept art" },
  "c15": { "status": "pass", "by": "MMR-262", "note": "every finding fixed or deferred (results/MMR-262.md)" },
  "balance": { "by": "fix batch", "note": "replaces the committed balance-pass.json for c28" },
  "c27Accepted": { "by": "owner", "note": "the 22% main-thread cost accepted" },
  "handoff": { "status": "pass", "by": "owner", "note": "looks and terrain reviewed; findings section 13 updated" } }
```

Without them c12, c15, c29 (the owner's review of the vein look), c31 (unless the screenshot step recorded the mesh layer visible) and step 7 stay `blocked`; c27 fails on a main-thread drop over 20% until accepted.

**The balance pass (c28)** is the committed `evidence/map-epic-2/balance-pass.json` (`--balance FILE`), written by the fix batch from acc6's soak: one item per speed term (`speed.Snow`, `speed.Swamp`, `speed.road`), the trade-area economy (`economy.tradeArea`, RT-42) and each culture the run's soak names in its home terrain (`home.<culture>`, RT-27), each with its observation, its decision (`none`, or `change` with what changed) and the reason. c28's balance part passes when every item the run needs is there, and is `blocked` naming the ones missing (a culture the review never saw, say); an attestation `balance` replaces it. acc6's review changed no policy (the decisions and reasons are in the file and `results/MMR-247-F.md`).

Grading rules the acc4 triage changed (defaults taken where the owner may override):

- **Faces (c1, c7).** The face the game reads at a land travel cell's centre against that cell's own land type in cells.bin (99%, points within 0.05 of a cell edge skipped); acc4's face samples are mapped onto the kept bundle's cells.bin the same way. Plus the offline recount (`tools/summarize_land_faces.py`: cells.bin is the corner majority of the table types). The per-biome table agreement is information only (biomes thinner than a cell lose the corner vote).
- **Speed tooltip (c7).** Each type's own term: Desert `Desert`, Forest `Forest`, Snow and Swamp `Terrain` (the label the model uses), the road sample `Road` on a road cell, a bridge `Bridge on a road`. **Swamp is n/a on a world without a Swamp cell** (owner default; A' and B' have none, marsh never wins a corner majority; Test-PartySpeed covers the term offline).
- **Battles (c7, c13).** The expected scene class is the battle class of the battle cell's centre biome, the resolver's own input (`fieldAt.biome`), not the face's or the point's biome.
- **Villages (c5).** Each culture keeps the protected types (wheat farm, horse ranch) its installed villages have (`economy.json villageTypes.pools`, from the installed settlements.xml): Nord has no horse ranch, so wheat farm only.
- **Parity (c26).** A culture incident whose condition never ran is `not-run`, not `fail`; **one whose cultures have no kingdom on the world is n/a** (owner default: deer_departed without a khuzait kingdom). Hideouts: the drawn culture is weighed against the nearest town of the hideout's own land group (as GeneratedCampaignInitializer draws it).
- **Water (c24).** A lake is visible when its ray meets any shown plane above the terrain: its own, the sea plane for a lake below the datum, or another lake's whose box holds the sample. Road and magic decals on water are tested analytically (ground below the datum, or under a shown lake plane), on both worlds.
- **Snow (c8, c9).** A white pixel has every channel at 160 or more within a spread of 40 (the map's tonemapping keeps snow below 200: acc4's land ice scores 0.74, its desert 0.06). The warm region fails only when it gains white in winter (winter share at least summer share + 0.1) or stands at land ice's level in winter (0.5 or more): pale desert sand in clear summer light counts as near-white on its own (acc6: 0.46 in summer, 0.00 in winter, same hour and camera), so a one-sided rule (MMR-247 F). A season shot outside the daylight window or not settled is `blocked`, not `fail`. The Snowy share is compared with the share the installed snow rule gives the place's weather-node byte over the sampled days (within 0.15).
- **Cache estimate (c21, step 2.generate).** A world start that reused its caches (acc6's world 1: acc6lab had built them for the same world 26 minutes earlier) is graded on the build that made them: the same cache key (`cacheBuild.record.keySha256`: module, scene, settlements, game and navigation models) measured by another start of the run, else of a sibling run folder (`build/acceptance/<other>`; `--builds-from FOLDER` names them instead), the latest that started before the reusing start. The detail names the run, phase and pid it came from. Without such a build the estimate fails as before.
- **Census (c18).** The engine's added children against the compiled estimate and against the engine's rule per house (InitialChildGenerationCampaignBehavior: ceil(adult lords / 2) less the children already there).
- **Soak (c20).** A hire is `JoinAsMercenary` of a minor clan (not `LeaveAsMercenary`), or a company serving at the end that did not serve at day 0; the houses of every dissolved kingdom must still be in the census, neither removed nor eliminated.
- **Magic (c29, c30, c31).** A look never passes on records alone (revision 6). It passes when the screenshot step recorded the overlay mesh layer visible: the shown shot's `meshLayer` record (`OverlayMeshRuntime.InView`: drawn as meshes, not hidden, entities whose drawn rectangle meets the view, 116 x 74 units at distance 38 scaled with the distance) and the hidden shot's (hidden), and the toggle diff (at least 2% of the central half changing by 8 or more); else it is `blocked` on the owner's review of the screenshots (`attestations.json`, which also decides it when present). c29: the zone and cast screenshots, the veins drawn as overlay meshes (c33's vein record), the vein look shown (`magic-veins`), and the owner's review (`c29`), which the criterion keeps. c30: MMR-289's features drawn in the generated zone, around the cast and after the Continue, none on water, a road or a settlement's clearance, within c27's frame limit. c31: MMR-290's stamps (or MMR-291's meshes) drawn without errors, the screenshot, and the look shown (`magic-water`; `c31`). A toggle diff under the gate names the decal lab's mesh column and MMR-291's publish request P.
- **No freeze (c32, MMR-247 X).** No world 1 step hung or crashed; the fresh-map rule off in every world 1 launch and no `freshMapBefore` reload (a reload fails it; the rule on is `not-run`); `w1.battle.snow`, `.forest`, `.settlement`, `.mutated`, `w1.castle` and `w1.siege` ran (their own checks are other criteria's), at least one with `missionOnThisMapBefore` (else `not-run`: the freezes' condition was not met); and where the hang repro ran (phase `hangrepro`, or the decal lab's own `war` and `castle`), every variant with the fix on `ran` (`engine blocked`, `crashed`, `error` or `timeout` fail it). The pre-fix controls (`warControl`, `warNoMagic`, `warNoCopySync`, `castleControl`) are listed only: they crash by design.
- **Overlay meshes (c33, MMR-291).** Per layer (veins at `w1.magic`, rivers at `w1.water`, magic water at `w1.magicWater`), from the probe's `overlayMeshes` record: drawn as meshes (policy mode `mesh`, materials ready), entities live, no missing material or error, every material of the layer resolved (published, a template copy, or a vanilla water material), the toggle taken both ways, and both screenshots on disk; the decal lab's mesh grid when `decallab` ran in the run (`decal-lab/decal-lab.json`: a `mesh:*` column drawn nowhere fails); c27's frame cost. The grade's last line says the look needs the screenshot review (c24, c29, c31).

**MMR-256's snow route** is decided from the season screenshots (`snowRoute` in `acceptance.json`): if the cold region whitens in winter and land ice stays white, the named-texture route stands; if either fails, set `precipitation.snowTexture.route` to `permanentWhite` in `worldgen/policies/climate-flow.json` and rebuild (one build, the scene names change). When the evidence is missing (an extreme not reached, a screenshot missing, the season step not run or failed) the decision is `undecided`. **acc5 decided it:** the route is `permanentWhite` since the integration after acc5 (`docs/water-climate.md` "The decision"). On a build that takes it (world 1's `w1.identity` snow texture record names no texture), the cold region must stay unwhitened (winter less summer white share below 0.15) and land ice white in both seasons; the decision reads `permanentWhite (in force): confirmed`, `fails` or `undecided`.

## The fix batch and the confirming run

1. Read `acceptance.json`: every `fail` and `blocked` with its details, and the screenshots above.
2. Fix everything in one batch, including the **balance pass** (the soak's Snow, Swamp and road speed terms and each culture's home terrain: policy factors or the culture mapping), the snow route decision, and the cache model calibration (MMR-282: the two measured worlds' cache builds, `w1.start` / `w2.start` `cacheBuild.generateSeconds` and `preview.settlements`, into `worldgen/policies/one-shot-budget.json` `navigationCacheBuild.worlds`, the pre-epic rows dropped). Record the balance pass in `evidence/map-epic-2/balance-pass.json` (an item per term, the economy and each culture; see "The balance pass").
3. The confirming run: the same command with a new `-RunId`. Every phase runs again from the build. A third run only for a genuinely new blocker.

## Known limits of the harness

- **Vanilla baseline.** The baseline is the official map and content with this module loaded (it hosts the probe) through the diagnostic bootstrap, not an unmodified game.
- **Frame cap.** MMR-249 found the 60 fps cap held with `SetForceVsync(false)`; the main-thread time is the cost signal and the cap is recorded per view.
- **The speed tooltip** is read from the party speed model's explained numbers (what the tooltip lists), not from the hovered widget.
- **Incident conditions** run with the player's last visited settlement set directly; a condition can still be false for its other clauses (party size, time), and the record says which.
- **The free camera.** Screenshots aim the map camera's own free mode; where it does not reach a point (within 2 units), the party is placed beside it and followed (`mode: party` in the shot record).
- **Seam crossings** on A' and B' exist only a few units from the cut (the biome field is continuous across it): the crossing goes from the nearest land biome west of the cut to the nearest different one east of it, over land.
- **Biome planes are speckled** at the probe's resolution (A' grassland peaks at 41% of a 26-unit window): a biome's screenshot is at its densest spot, which can still show neighbours.
