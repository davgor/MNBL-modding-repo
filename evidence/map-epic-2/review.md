# Map epic 2 review pass (MMR-262)

The epic's single review before the live test (MMR-247), over `09ca458..map2/int` and FWGC++ `ec5d1c1..0ae1f3e`. Scope: what would
waste the live run or harm the shared install. Final check on `map2/int` at `015776a`: every offline suite passes on A' `31bb9dc3...`
and B' `186f0c32...` (nothing run in game).

## Findings
| id | severity | title | outcome |
|---|---|---|---|
| F01 | blocker | GeneratedMapScene.Destroy removed map-dressing and magic-ground decals and entities natively after MapScreen.OnFinalize's Scene.ClearAll, on every exit from a generated campaign | fixed `8be68b7` |
| F02 | should | GeneratedPartySpeedModel shared its scratch factor/kind arrays across the parallel party movement tick | fixed `13bd5b1` |
| F03 | nit | The default snow route (load and rename a PNG texture before Read) has never run in game and sits on every generated scene read; the fallback renames every scene | deferred: needs an in-game observation, and the only fallback is a route switch that renames every scene; MMR-247 step 2 checks it first |
| F04 | - | The re-pin chain does not cover the surface template recipe | refuted: tests/ci test_surface_template already checks the recipe against terrain-materials.json, and the live run edits no layerSet |
| F05 | should | The wanderer top-up and rotation could only use the templates the day-0 wanderers use | fixed `1387c28` |
| F06 | should | The population census could not answer MMR-247's soak questions and counted the player clan as a minor clan | fixed `1387c28` (doc fix `29c0815`) |
| F07 | - | No tool deploys or removes MMR-247's synthetic content module | refuted: MMR-247 owns the deploy and removal (its harness builds and removes it) |
| F08 | nit | Camera zoom-out cap and far clip shrank with the new vertical scale | fixed `0b9b222` |
| F09 | nit | Some impassable mountain cells look like gentle, rock-free ground on the shaped surface | deferred: the code fix (a mountain mask into compile_layers, mirrored in Python, evidence regenerated) is not cheap and the look is the owner's call; accepted-limit note in docs/terrain-compiler.md (`dfe70ca`) |
| F10 | - | Shared-install lock check from a worktree; launch and deploy scripts check different process lists | refuted: a worktree check fails loudly, and the CLAIM protocol plus the three-name process check cover it |
| F11 | - | Start-OwnedProbe cannot give the 30-day soak enough time | refuted: measured 20.1 s per campaign day puts the soak near 750-1,700 s, under the 3,600 s cap |
| F12 | - | No snapshot of the installed module before Deploy-Dev | refuted: the deploy writes only inside Modules/MNBL.ProceduralWorld behind its guards, and MMR-247's harness backs the module up |
| F13 | - | Test-GenerationProbe cannot compile since MMR-261 | refuted as a live-run risk (the module builds); the offline harness was fixed in `015776a` (FC1) |
| F14 | nit | Package.ps1 always refused once the magic-ground pack was published | fixed `dfe70ca` |
| F15 | nit | An fwg apply timeout was treated as a cancellation and relaunched on the next tick | fixed `6aa0ca3` |
| F16 | - | The loader worker serializes support-report dictionaries the engine thread still writes | refuted: the first-tick writes finish before the serialize; a later collision is under 0.1% and costs one load report |
| F17 | - | A battle table Preflight refuses still waits for the nav-cache build | refuted: CheckModuleTables refuses earlier on the same input, and step 5 reuses world 1's caches |
| F18 | should | The activation's post-build check threw KeyNotFoundException on a failed battle-map record | fixed `0b9b222` |
| F19 | nit | Re-pinning the catalog with Make-CatalogPin is not needed | deferred: the current pin refuses nothing and covers every object MMR-280 worlds reference; INTEGRATION.md's "Make-CatalogPin looks stale" item is closed as not needed |
| F24 | nit | A cancelled fwg apply left its job folder under `<store>/mutations` | fixed `6aa0ca3` |
| F28 | nit | Fixed decal projection depth (+-3) clipped magic decals and river stamps on shaped slopes | fixed `8be68b7` |
| F29 | nit | Vanilla absolute-z constants act on much lower terrain (party tilt below z 2.58, rain camera effect below z 65) | deferred: cosmetic constants in installed TaleWorlds/NavalDLC code; a gated transpiler is outside the pre-live batch; known-behaviour notes for MMR-247 |
| F36 | nit | Test-NavalContent on B' failed 3 checks on world-A literals | fixed `dfe70ca` |
| FC1 | nit | Final check: Test-GenerationProbe compile break (CS0246 'Surface', from MMR-261) | fixed `015776a` (test-only stand-ins; no module change) |
| FC2 | nit | Final check: Characters, SpawnPolicy and CompiledWorld failed on B' over world-A literals (7 presentation cultures, A's layout report) | fixed `015776a` |
| FC3 | nit | Final check: Test-OfflineSuites defaulted to the pre-epic junction bundles (refused; some receipts land in the main checkout) and gave Test-PackagePaths no content id | fixed `015776a` |
| FC4 | nit | The release package does not ship ModuleData/Languages/std_mnbl_procedural_world.xml (MMR-286) | deferred: not on MMR-247's path (Deploy-Dev deploys it); needs a package-block entry and staging step before any release |
| FC5 | should | The MMR-247 harness branch (`map2/MMR-247`) was cut at `a35f892`, before every MMR-262 fix | handed to MMR-247: merge `map2/int` (at `015776a` or later) before the build step; `git merge-tree` shows no conflict |

## In-game checks handed to MMR-247
- **F03 (snow route), step 2, early.** After world 1's generation, before the nav-cache build: read the generation record's snow texture
  (`status`, `engineName`, `resolvesByName`) and search `rgl_log_<pid>.txt` for `Unable to find texture: mnbl_snow_`. If the status is not
  `loaded`, or the read hung, set `climate-flow.json` `precipitation.snowTexture.route` to `permanentWhite`, re-run the re-pin chain
  (`tools/repin_map2.py`) and rebuild before any world is finished. At the first map load, expect the trace row
  `climate_snow_texture:<name>:loaded:resolvesByName True` and a map that is not white all over. Save at day 0 and advance to winter early,
  on a separate save branch, to compare winter and summer snow and its orientation; any route or orientation change rides in the fix batch.
- **F01 (teardown).** On the early exit after world 1's first map load, expect `map_dressing_released:handles <n>:engine calls 0` and
  `magic_ground_released:handles <n>:nodes <n>:engine calls 0`, and no `*_removed` lines. Whether ClearAll deletes the decals it took is
  unproven: stay at the menu about a minute after the exit (or start the next load) before going on, so a finalizer fault shows there.
- **F09 (mountain look), c17.** Some impassable range-edge cells look gentle and rock-free while parties route around them. Do not file it
  as a navigation bug; note in acceptance.json where it shows.
- **F29 (vanilla constants).** With War Sails, parties stand upright on ground below absolute z 2.58 (about half the land on A' and B');
  the camera rain overlay starts below camera z 65 (50 in a storm), a slightly wider zoom than vanilla. Both expected.
- **F08.** Zoom-out cap 180 and far plane 720 on both worlds; the camera stops at the pole lines. Record c27 frames at the default distance
  38 and elevation 0.635 rad on both maps.
- **F02.** The parallel movement tick is exercised only live: speed tooltip terms (Snow, Swamp, Forest, Desert, road) stay consistent
  through the soak, with no exception from the speed model in the log.
- **F05/F06 (soak).** Top-ups and rotations should draw own-culture templates among all 40 backstory-safe ones; the 30-day census fills
  `minorClanDetail`, `houseDetail`, `housesEliminated`, `fiefOwners`, `kingdomChanges` (mercenary joins included for c20).
- **F18, step 5.** The synthetic module's extra scene sits at an unused map index, so it should be absorbed. Any refusal must be a
  content conflict naming the module (the PostBuild path now throws `ContentConflictException`), never a `KeyNotFoundException`. The engine's `GameSceneDataManager.LoadSPBattleScenes` throws `TWXmlLoadException` unless the file's second child node is
  `<SPBattleScenes>`, so the synthetic `sp_battle_scenes.xml` keeps its XML declaration first and no comment before the root
  (`tools/Build-AcceptanceContentModule.ps1` on map2/MMR-247 already writes it that way).
- **F15/F24.** After the Nerdun fire cast, save and Continue: no `apply-*` folder is left under `<store>/mutations`; a timed-out apply
  (if one happens) waits for the next daily batch rather than relaunching each tick.
- **F28.** Magic decals and river stamps on shaped slopes are not clipped; look at the B' coast step near physical (684, 959), where land
  stands 7-9 units over the seabed one sample away.
- **Identity.** C3/C4 change the world documents and the map scene name: expect one nav-cache build on each world's first start, and
  generate fresh worlds (a save from an earlier build is refused on Continue, by design). If the fix batch changes any world document, the
  confirming run generates new worlds too.
