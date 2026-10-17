# MMR-37 independent review and disposition (2026-10-15)

Reviewer: an independent read-only agent, which did not edit the repo or launch the game.
- It re-ran `tools/Test-WorldSurface.ps1` (21/21) and checked the model's quantization against the compiler's `height-q16.bin` (0 differing codes on both bundles).
- It recomputed the in-game evidence from `world_surface-34652` and the campaign runs 39604 and 20184.
- It reviewed the first version of the work.

## Review (verbatim)

I found no BLOCKER. There is one MAJOR (resources the backend creates but never owns or cleans up) and a set of MINOR findings, mostly checks that cannot fail or that compare a value with itself. I made no repository edits.

### Findings

1. **MAJOR – resources created per construction are not ledgered, not released, and the docs hand them to tickets that don't own them.**
   - Where: `World/RuntimeWorldBackend.cs:76-88` (job folder), `:115-125` (`SceneObj/<fresh name>` and `PairSceneNameToModuleName`); `docs/runtime-surface.md:140-141`.
   - Every Prepare/Install leaves a staging job under `%LOCALAPPDATA%/MNBL.ProceduralWorld/world-surfaces/<guid>`: 20 MB for the 3 jobs of run 34652, including the refused world C.
   - It also leaves a scene folder of about 5.1 MB (`SceneObj/mnbl_world_f596…_620c…`) inside the Steam game install, plus a scene-name registration that is never undone.
   - `FreshSceneName` adds a random GUID, so every reconstruction of the *same* world adds another folder. Disk use grows with each load, not with each world.
   - The docs hand cleanup to MMR-47/62. MMR-47 cleans only "unreferenced incomplete staging under the resolved world-data root", and its ticket says that root must not be the game install. MMR-62 removes only "abandoned job staging". Neither covers installed `SceneObj` folders.
   - Line 140 also hands "releasing native resources on a world switch" to MMR-49. The MMR-37 ticket keeps world-switch disposal here; only cold-load restore moves to MMR-49.
   - Fix (pick one): add the job folder, scene folder and registration to the ledger with a real release path (delete after the scene is cleared; reuse a content-addressed name when the bytes are identical); or get an owner assigned explicitly and correct lines 140-141.

2. **MINOR – the height model is not what the engine returns; the 0.05 tolerance hides a known offset of up to 0.03.**
   - Where: `World/WorldSurface.cs:23,169-170`, `docs/runtime-surface.md:59`, `WorldSurfaceProbe.cs:296-297`.
   - Native terrain, combined and ground heights equal the *collision* reference (samples truncated toward zero at 0.03, main diagonal) to within 6.5e-6 (world A) and 1e-6 (world B).
   - Against the quantized model they differ by up to 0.0297 / 0.0274, mean 0.014.
   - The main-diagonal claim does hold up: an anti-diagonal model would fail 0.05 at 19 of 42 (A) and 15 of 42 (B) off-vertex landmarks.
   - Fix: check heights against `CollisionHeight` at about 1e-3 and correct the doc and comment.

3. **MINOR – normals are never checked against the model; a backend with flat or wrong normals would pass every check.**
   - Where: `WorldSurfaceProbe.cs:291,298,300,316`; `tools/summarize_world_surface.py`.
   - `native_normals` compares two native calls with each other. `map_scene_agrees` compares the adapter with the same native calls. The border check only bounds the native angle relative to the model angle.
   - `normalVsModelDegrees` is recorded but never asserted. Its recorded maximum is 0.452° (A) and 0.338° (B), so adding the assertion, including in the summarizer against existing evidence, would pass.
   - UVs ("correct normals/UVs" in the ticket) are not addressed anywhere, including the docs.

4. **MINOR – circular checks presented as agreement.**
   - `map_scene:borders_and_size` (`WorldSurfaceProbe.cs:268`) compares `GetMapBorders` and `GetTerrainSize` with model values that `GeneratedMapScene.Inspect` itself wrote into those fields from the same model (`GeneratedMapScene.cs:210-213`). It cannot fail. Populate those fields from `GetTerrainData` / `GetBoundingBox` instead.
   - In the campaign rows (`GeneratedCampaignBootProbe.cs:343-348`), the map scene's ground is compared with `scene.GetHeightAtPoint` using the same masks: the identical native call. Evidence 39604 has `mapSceneLand == nativeLand` bit for bit.
   - The run-20184 fix turned a meaningful comparison into a tautology. The data show ground = max(terrain, water plane at 0): at (600.37, 200.29) ground is 0 and terrain is -18.63. Compare ground with max(terrain, sea datum) within 0.05 instead.

5. **MINOR – the thread evidence is a hard-coded string, and EngineThread binds to whichever thread calls first.**
   - `["preparedOn"] = "worker thread"` is a literal (`WorldSurfaceProbe.cs:57,105`). The summarizer's "prepared off the engine thread … constructed on the engine thread" rests on it.
   - `EngineThread.Require` (`RuntimeWorldBackend.cs:35-40`) adopts the first caller as the engine thread. It cannot detect a wrong first caller, and if a worker ever calls first, every later engine-thread call throws.
   - There is no engine-thread scheduler in the backend; the polling coroutine exists only in the probe. The ticket asks for "engine-thread work scheduling".
   - Fix: seed `EngineThread.Id` in `OnSubModuleLoad`, record the thread ids Prepare/Install/Construct actually ran on, and have the summarizer compare them.

6. **MINOR – Construct is not exception-safe and does not check the scene before reading it.**
   - Where: `RuntimeWorldBackend.cs:135-153`.
   - If `scene.Read` or the identity check throws, the scene and agent renderer are already in a ledger nobody holds. `InspectionScreen.World` stays null, so they leak and the generation stays current.
   - Construct also never checks `TryGetFullFilePathOfScene`. The late-callback test calls Construct on a scene that was never installed, and only the token stops a native Read of a missing scene.
   - Fix: try/catch that calls `ReleaseAll` and rethrows, and a resolve check before Read.

7. **MINOR – campaign-profile ledger and Destroy semantics.**
   - `ReleaseAll` marks the engine-owned entries "released, outstanding 0" when `GeneratedMapScene.Destroy` runs (`WorldLifetime.cs:89`). Nothing observes that `MapScreen.OnFinalize` actually cleared the scene, so the record states something that was never observed.
   - Destroy leaves `loaded = true` and `_scene` set. Until the next world begins, a stale reference still answers guarded queries against the cleared scene.
   - The inherited public `MapScene.Scene` is never guarded, so direct native calls through `previous.Scene` are not refused. This is c1's "direct native calls"; only two IMapScene members are tested.
   - In the `finally` block (`GeneratedMapScene.cs:322`), if `world.Dispose()` throws, `PeriodicPatches.Remove()` is skipped. Nest the two cleanups.

8. **MINOR – latent coupling in the generation guard.**
   - Generations are global (`WorldLifetime.cs`), and `GeneratedMapScene.Live` refuses a map as soon as any later generation begins.
   - Any future `Begin` while a campaign map is live (for example MMR-43/45 preparing a world in the background) would make every IMapScene query from engine callbacks throw, which ends the process.
   - It is safe today: 39604 shows teardown ("world_released gen 1" at 158.8 s) before the next load ("loaded gen 2" at 166.9 s). The installed load order (`CreateMapScene → SetSceneLevels → Load`) leaves no pre-Load query window.
   - Fix: document the ordering rule or enforce it at `Begin`.

9. **MINOR – the campaign probe now holds world 0's map strongly across `EndGame`.**
   - Where: `GeneratedCampaignBootProbe.cs:152`; it contradicts the design note at `:247` ("without … keeping strong references to the old campaign").
   - Through its fields the held map pins world 0's PeriodicWorld, climate, weather buffers and the Scene wrapper during world 1's boot.
   - It does not break MMR-59's `previousWorldReleased` check, because the reference is dropped before that check runs. It does muddy the leak observations, and the summarizer will reject 39604 (its row says "collected"), so the final campaign run is required.

10. **MINOR – F06 members left out without saying so.**
    - `RuntimeWorld` exposes one ray overload. Not exposed, tested or listed as excluded in `docs/runtime-surface.md`:
      - `GetGroundHeightAtPosition` (2 overloads);
      - `RayCastForClosestEntityOrTerrainFixedPhysics` (4 overloads);
      - the two `RayCastForClosestEntityOrTerrain` overloads that return a WeakGameEntity;
      - `GetTerrainHeightData` / `GetTerrainNodeData` (MMR-16: native crash);
      - `GetSoft/HardBoundaryVertex`.
    - `surface-coverage-review.json` gives reasons for excluding most of these. Cite them.
    - `GetFaceVertexZ`, `GetGroundNormal`, `GetTerrainHeightAndNormal` and `GetMapPatchAtPosition` are guarded but never tested for refusal or interface dispatch. A `GetInterfaceMap(typeof(IMapScene))` assertion would cover all seven cheaply.

11. **MINOR – the summarizer does not tie the two in-game runs to one build** (`moduleSha256`), and it does not check normals against the model (see 3).

12. **NITs**
    - `tools/scenebin/build_climate_world.py:135` still writes "DiagnosticMapScene.Load" into its evidence text.
    - Namespace `Surface` lives in folder `World`.
    - `docs/runtime-surface.md:37` says native calls run only on the engine thread, but the guarded IMapScene queries correctly allow parallel campaign callers.
    - `docs/runtime-surface.md:92` says each world "begins" a generation; the caller begins it.
    - The R02 probe keeps its own install path, so "becomes `RuntimeWorldBackend`" is a copy, not a move.
    - `Test-WorldSurface.ps1:39`: the input-change guard is skipped for absolute paths and when not run from the repo root.
    - Prepare never checks its token, so superseded work runs to completion.
    - `WorldGenerations.Log` grows without bound.
    - Bundle B and the "seeds 43–72 rejected" claim exist only in ignored `build/` output; there is no evidence record of the rejections.

### Rename and save safety
- No `SaveableTypeDefiner` subclass exists, and the map scene type is not persisted. The save oracle `oracle-39604.json` contains no MapScene type name.
- No tool or summarizer greps for "DiagnosticMapScene" or the type's full name. `RuntimeMissionProbe` records the name but nothing compares it.
- Load still makes the same native calls in the same order. `_scene` and the agent renderer field are now assigned a few native calls later, and nothing reads them in between.
- Trace names are unchanged apart from a ":generation N" suffix that nothing parses.

### Per-criterion verdict
- **c1: met for the backend on inspection scenes, with gaps.**
  - Met: 70 landmarks per world; rays within 2e-4; selection hit matches the collision reference to 1e-5; B answers all 69 of A's differing positions with its own heights; disposed-world queries refused.
  - Gaps: the map-scene and campaign agreement checks are circular (4); direct `MapScene.Scene` access is not guarded (7); campaign rows involve no compiler landmarks (activation is MMR-45's).
- **c2: seams met; disposal only partly evidenced.**
  - Met: offline, all 1,569 shared edge samples have equal heights, normal bytes and material weights in both worlds; in game, 36 border points per world show no tear.
  - Not shown: normal correctness is not asserted (3), and native release is only recorded as called.
- **c3: met for in-process managed and engine objects; not met for disk and registration resources.**
  - Met: A disposed with 0 outstanding and collected; late world C refused at install and construct; the campaign refusal is still pending the final run.
  - Not met: scene folders, job staging and registrations unless the owner gap in (1) is resolved.

The verdicts depend on the planned final confirming runs. `world_surface-34652` predates the last boot-probe edit, and `result-39604` predates the held-previous-map change.

## Disposition (one fix batch)

All findings were fixed. The namespace/folder nit is noted and left as is.

| # | Disposition |
|---|---|
| 1 | Fixed. Scene names are content-addressed (`SceneNames.ForWorld`: content id plus the encoder, template and extent identity), so the same world encoding reuses its installed folder and is never rewritten. Each install removes the scene store's content-addressed folders that the process did not use; fixture slots such as `mnbl_world_a` never match the strict pattern. The staging job is deleted after install or refusal. The world's ledger hands the store entry to the scene store and the name pairing to the engine index, which has no unregister API. The doc's owners are corrected: world-switch disposal is this ticket's, and MMR-49 owns cold-load restore. The final runs show staging jobs left 0, a sweep removing an unused folder, a reinstall, cross-process reuse, and the store holding exactly the worlds' scenes |
| 2 | Fixed. Heights are checked against the collision reference at 0.001 and the quantized model at 0.031. The worst observed collision error is 6.5e-6 (A) and 8.7e-7 (B). The comments and doc now say the engine returns the collision reference |
| 3 | Fixed. Native normals (both calls) are asserted within 1° of the model at every landmark and both sides of every border; the worst observed is 0.452° (A) and 0.338° (B). The doc now states that UVs are engine-derived from the node grid, with nothing supplying them |
| 4 | Fixed. `Inspect` takes borders, size and maximum height from the native scene (terrain data, bounding box, height range), so the comparison with the model can fail. Campaign ground is compared with max(native terrain, sea datum 0) and height-and-normal with the native terrain |
| 5 | Fixed. `EngineThread` binds the application tick thread on the first tick. Binding at `OnSubModuleLoad`, as suggested, was tried and was wrong: module load runs on another thread (3 versus 30, run 27772), and campaign map loading runs on the tick thread (30, run 39604). The backend records the prepare, install and construct threads, and the summarizer compares them. `EngineWork` is an engine-thread work queue pumped every tick; the late callback is delivered through it |
| 6 | Fixed. `Construct` resolves the name to an installed folder that holds the scene before reading. On any failure it releases what it created and retires the generation |
| 7 | Fixed. Engine-owned resources are recorded as handed to their owner, not as released. A hidden `Scene` property is guarded (it also allows the loading phase that `PrepareWeather` uses), and the campaign test checks that the previous map refuses it. Destroy's cleanups are nested |
| 8 | Fixed. The ordering rule is enforced: `Begin` refuses while a campaign map is live, from `Load` to `Destroy`. It is documented and covered by an offline check |
| 9 | Acknowledged and documented. The hold is deliberate, so the refusal of a stale reference can be observed. The design note is updated, and the reference is dropped after the next world's check |
| 10 | Fixed. The interface map is asserted: all 13 re-implemented members dispatch to `GeneratedMapScene`. Refusal is tested for all 7 guarded members plus `Scene` and the six `RuntimeWorld` queries (14 in all). The doc lists the excluded members with reasons from `surface-coverage-review.json` |
| 11 | Fixed. The summarizer requires both runs to come from one module build and checks normals |
| 12 | Fixed:<br>• the climate builder string;<br>• the doc wording on threads, on who begins a generation, and on the R02 copy;<br>• the harness input guard;<br>• `Prepare` checks its token before starting and before encoding;<br>• the generation log is bounded at 256;<br>• the seed search is recorded (`evidence/world/second-bundle-search.jsonl`).<br>The namespace `Surface` in folder `World` is noted and left |

## Confirming runs

- **Offline.** `tools/Test-WorldSurface.ps1` 24/24 (`evidence/world/surface-tests.txt`), now including the compiler's `height-q16.bin` codes, content-addressed names and the ordering rule.
- **In game, on one module build:**
  - `world_surface` run 2816. Its first install reused A's folder and removed B's, which this process had not used; B was reinstalled. The late world C was refused, with its registration unchanged because the name was already in the startup index. All three constructions passed every check.
  - `generated_campaign` run 38676.
  - `tools/summarize_world_surface.py` writes `evidence/runtime/world-surface.json`.
- **Earlier runs.** Run 42048 (the first confirming run, on the previous build) and run 43076 also passed; 43076 was the first to exercise the sweep. Run 27772 failed on the `OnSubModuleLoad` binding and led to the tick-thread binding.
- **Regression signals:**
  - from campaign run 43700 (previous build): startup audit 33/33, naval content 10/10, settlement fixtures 13/13, graph gate 9/9;
  - with the cold load (`generated_save_load` run 41500): startup audit 36/36, naval content 11/11. The load's only snapshot differences are 10 path-state values of moving AI parties (MMR-62).
  - from the final campaign run 38676 (same build as world_surface 2816): startup audit 33/33, naval content 10/10, settlement fixtures 13/13, graph gate 9/9.
- **Summary.** `evidence/runtime/world-surface.json` passes 32/32 on module build `e0db842c…`.
