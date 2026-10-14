# MMR-21 telemetry extraction (provisional limits input)

Extracted 2026-10-14 from the accepted probe evidence only. No new benchmark was run.
- **R** = a recorded field (path given). **D** = derived by subtracting two log offsets. An offset is the `+Nms` column of `evidence/runtime/in-game/<run>.log`, counted from that run's `request_taken` line (UTC in §1).
- Hashes are shortened prefixes (usually 16 hex characters). The full value is in the cited field.
- Every in-game run below is one sample on one machine (§0). No run was repeated with identical inputs; see §11 item 8.

## 0. Hardware, settings, versions
None of the accepted probes records hardware or graphics settings. The values below come from the MMR-13 baseline on the same machine.

| Item | Value | Source |
|---|---|---|
| CPU | Intel Core Ultra 9 275HX, 24 cores / 24 logical | evidence/baseline/performance.json `cpu[0]` (baseline pid 10828) |
| GPU / display | NVIDIA GeForce RTX 5080 Laptop GPU (driver 32.0.16.1692) + Intel Graphics; 2560x1600 | performance.json `gpu[]` |
| RAM | 33,690,267,648 B (31.4 GiB) | performance.json `ramBytes`; environment.json `memory.totalBytes` |
| Engine settings | texture_filtering=5, texture_budget=3, texture_quality=2, max_framerate=200; engine config sha 396d1b91b5d086e8 | performance.json `settings`, `engineConfigSha256` |
| Game | v1.4.8.119303; NavalDLC v1.2.8.119303; Steam buildid 24573425 (app 261550), 24575232 (app 1393600) | config/runtime-api-lock.json `gameVersion`/`navalVersion`; tools.lock.json `game`; baseline-startup.log "Build Version: 119303"; environment.json `installed.manifests[].values.buildid` |
| Official descriptors | Native 38e8f6fa52498346, SandBoxCore d0b0ffe3c335d16d, SandBox 179168441d5696c6, StoryMode fc2d9ad4f60d52e0, NavalDLC 64b0cdc985667266 | environment.json `installed.modules[].sha256`; in-game/*.deployment.json `officialDescriptors` |
| Producer | fwg.exe 564c2629b5b92d49 (FWGC++ commit ec5d1c10, MSVC 19.44.35228.0, x64 Release) | tools.lock.json `producer`, `compiler` |
| Game start | 19,476–19,680 ms from request_taken to the main menu (all 12 runs) | raw `<run>.json` `timings.moduleLoadToMenuMs` (R); `menu_visible` log line for 19240 |

## 1. Accepted runs and their input hashes

| Run (pid) | Ticket | Worlds (placement sha / fixture) | MNBL DLL sha (`moduleSha256`) | request_taken UTC |
|---|---|---|---|---|
| generation_menu-19240 | MMR-15 | seed42/43; heightfield 14c4e157ef3bb26e / e15ff0e2b4edd91a | e326e3fe67cb64ac | 2026-10-13T16:50:30.694Z |
| runtime_surface-32896 | MMR-16 | calibration (src 69d2cb897d791145), seed42, seed43 | c7a353ae2045f579 | 2026-10-13T18:08:38.557Z |
| generated_campaign-2248 | MMR-17 | 2a 08eb6aa6cd1446a4 (final42e), 2b e71b1e6035dc932e (final43e) | 5e28cde8292a6515 | 2026-10-13T23:32:08.102Z |
| generated_campaign-31308 | MMR-18 | 2a 008a5d0074bd371a (final42), 2b b9a93238675fff91 (final43) | 0c1c1e3774f3ae0f | 2026-10-13T20:06:04.263Z |
| generated_campaign-24736 | MMR-18 | 2a 008a5d0074bd371a (final42) | ded4381acbf25d65 | 2026-10-13T20:47:36.067Z |
| generated_campaign-23192 | MMR-18 | 2a 963802f2c06d4cd4 (final42b), 2b e635214de6567b75 (final43b) | 3ab256efe35c0a13 | 2026-10-13T21:09:42.705Z |
| generated_campaign-22868 | MMR-60 (accepted per mission-transition-probe.json `accepted`) | 2a 08eb6aa6 (final42e), 2b e71b1e60 (final43e) | 19fa06b01cb9813e | 2026-10-14T07:55:41.338Z |
| generated_campaign-33472 | MMR-60 history (`history[]`, status ok) | same as 22868 | a31f3d0e4906031d | 2026-10-14T03:09:15.479Z |
| generated_campaign-12348 | MMR-63 | 63042 0702c9f208702939 (fixture-c) | 824066136f7a4259 | 2026-10-14T17:11:02.804Z |
| generated_campaign-24212 | MMR-59 | 59042 a48041c56615c00f (w42z), 59043 984d904a768067ef (w43z) | 174fcb0a319dce03 | 2026-10-14T18:54:34.501Z |
| generated_campaign-31624 | MMR-19 save | 59042 a529b12851ad884d (w42), 59043 1683f798fceef638 (w43) | 174fcb0a319dce03 | 2026-10-14T18:58:26.332Z |
| generated_save_load-8256 | MMR-19 load | same packages as 31624 | 174fcb0a319dce03 | 2026-10-14T19:01:54.892Z |

Installed scene hashes for each world: raw `outputs.worlds[i].registration.installedSceneHashes`. Their sizes are in §4. All campaign runs use catalog receipt 27bca57ee00ae08a (sha256 of `evidence/runtime/in-game/campaign_catalog-10212.json`; also `bootstrap-probe.json inputs.catalogReceipt`).

## 2. Generation (FWGC++), in game (MMR-15) and offline (MMR-24)

| Case | Total job | fwg.exe | Producer peak WS | Output (files / bytes) | Hashes | Source |
|---|---|---|---|---|---|---|
| seed42, size 129 | 11,197 ms R | 5,473 ms R | 265,490,432 B (253.2 MiB) | 868 / 141,680,220 (world.json 82,845,783; scene.json 19,527,691; 866 plans 39,306,746) | req f204b0ce8655b7ed, world.json e9264b8d1e0ad5b4 | generator-probe.json `cases[0].elapsedMilliseconds`, `processes[0].elapsedMilliseconds`, `producerPeakWorkingSetBytes`; byte totals = sum of generator-menu-19240/seed42/result.json `files[].length` (D sum) |
| seed43, size 129 | 12,656 ms R | 6,565 ms R | 308,269,056 B (294.0 MiB) | 973 / 167,047,568 (world.json 91,465,992; scene.json 26,635,572; 971 plans 48,946,004) | req e8c99292dcdedd03, world.json bb3a8f6d083cf437 | `cases[1]`; seed43/result.json |
| recovery (seed42 rerun) | 11,189 ms R | 5,516 ms R | 274,153,472 B (261.5 MiB) | same as seed42 | same | `cases[5]` |
| invalid / abort / cancel | 48 / 171 / 312 ms R | 36 / 161 / 300 ms | 25.8 / 51.0 / 69.9 MiB | none | — | `cases[2..4]` |

- Game responsiveness while generating (R, `maxApplicationTickGapSeconds`): 0.0224 s (seed42), 0.0068 s (seed43), 0.0709 s (recovery). Application ticks: 2,275 / 2,574 / 2,263.
- stages.log for seed42 (generator-menu-19240/seed42/stages.log): `16:50:53.3201210Z producer_started` → `16:50:58.7682929Z producer_closed` = 5,448 ms D, then `16:50:58.7898206Z reader_started` → `16:51:04.4708159Z reader_closed` = 5,681 ms D.
- MMR-24 offline (build/research-identity24/run-3428e1108c114ff9a3eab72124eb3c9d/<case>-threads<N>-work/*/attempt-0-run-1/job/run-record.json, linked from evidence/fwg/generation-identity.json `work`). Fields: `elapsedMilliseconds` / `peakJobMemoryBytes`. Producer 564c2629b5b92d49, bridge 2a8b3d638dbe778e.
  - size 129, seed 42 (request 9e49bb57af34faf2): threads=0: 5,147 ms / 246,964,224 B; threads=1: 8,568 ms / 171,548,672 B; threads=4: 5,692 ms / 178,229,248 B. world.json is 82,845,783 B, sha e9264b8d1e0ad5b4, the same as in-game seed42.
  - size 33 (request 41bd083d9d825893): threads=0: 1,358 ms / 147,972,096 B; threads=1: 2,476 ms / 64,704,512 B; threads=4: 1,566 ms / 73,142,272 B. world.json is 18,706,309 B (sha fcefbd922434181b). The threads=4 copy on disk is now 18,706,310 B with a different hash; it looks like the deliberately altered file from the `frozen.alteredWorld` check.
- MMR-16 in-game surface sequence (runtime_surface-32896.log), D, covering generation + reader + encoder: seed42 `+78605ms seed42_generation_begin` → `+89992ms seed42_install_fresh_scene` = 11,387 ms; seed43 `+146695` → `+159491` = 12,796 ms.

## 3. Conversion / worldbridge encoding

| Step | Value | Source |
|---|---|---|
| Reader (worldbridge --source → diagnostic), in game | 5,700 / 6,077 / 5,654 ms R (seed42/43/recovery); peak WS 27,467,776 / 26,931,200 / 26,943,488 B | generator-probe.json `cases[i].processes[1]`; bridge 0d8bf53a942ec673 |
| Surface encoder (heightfield → scene bundle), in game | calibration 87 ms, seed42 72 ms, seed43 73 ms R; peak WS 50,266,112 / 50,290,688 / 50,266,112 B | runtime_surface-32896.json `outputs.stages[i].encoder`; exe 21da9dc8e8a66eb6 |
| Install → register → create → read → optimize (seed42) | step-start markers +89992 install → +90023 register → +90044 create → +90045 read → +90092 optimize → +90112 next screen: file install 31 ms, register and resolve 21 ms, create 1 ms, `Scene.Read` 47 ms, optimize ≤ 20 ms (D) | runtime_surface-32896.log; markers are written at the start of each step (`RuntimeSurfaceSequenceProbe.cs`) |
| Offline fixture encoder | not timed. encoder.stdout.log only says "Surface bundle completed: 129x129…" | build/research-*/*/encoder.stdout.log |

## 4. Offline scene construction: fixture sizes and counts (no timing recorded)

| Fixture (world) | Extent / terrain grid | terrain.bin | navmesh.bin | scene.xscene | quantized-hf | Entities | Settl/ports | Nav faces / verts / edges | Placement sha |
|---|---|---|---|---|---|---|---|---|---|
| bootstrap18/final42e (2a) | 800 / 129 (6.25 m) | 4,435,680 | 1,414,542 | 70,740 | 133,140 | 76 | 4 / 2 | 32,768 / 16,641 / 49,408 | 08eb6aa6cd1446a4 |
| bootstrap18/final43e (2b) | 640 / 129 (5 m) | 4,435,680 | 1,416,994 | 70,701 | 133,140 | 76 | 4 / 2 | 32,768 / 16,641 / 49,408 | e71b1e6035dc932e |
| env59/w42 (59042) | 800 / 129 | 4,435,680 | 1,417,390 | 70,740 | 133,140 | 76 | 4 / 2 | 32,768 / 16,641 / 49,408; openSea 14,710 | a529b12851ad884d |
| env59/w43 (59043) | 640 / 129 | 4,435,680 | 1,419,667 | 70,701 | 133,140 | 76 | 4 / 2 | 32,768 / 16,641 / 49,408; openSea 13,920 | 1683f798fceef638 |
| wrap63/fixture-c (63042) | 1600 / 257 (6.25 m); period 800 | 5,024,609 | 4,977,623 | 71,802 | 528,404 | 80 (D: `<game_entity` count) | 4 + 4 display copies | 131,072 / not recorded / not recorded | 0702c9f208702939 |

- In every fixture flora.bin is 12 B (a711b21e6cf9374a) and atmosphere.xml is 4,941 B (8375ecccc7a721b5). env59 fixtures add climate-wind.f32 (2,097,152 B) and climate-precipitation.u8 (2,048 B).
- Sources: `build-receipt.json` fields `validation.entityCount`, `settlementCount`, `portCount`, `navFaces`, `navValidation.counts`, `openSeaFaces`, `files{}` (sha), `placementSha256`, `command --surface-extent`. Grid and spacing come from `encoder-surface.json` (`gridSide`, `spacing`). Byte sizes are from the files on disk, and every listed sha matches the receipt.
- Builder input hashes (`inputs[]`): build_bootstrap_world.py 2fac6a61ca873d69, navmesh_bin.py 043047fd7ddd4b29, terrain_bin.py e3098c32e458b088. worldbridge is 1dc1d8d8a4448258 (final42e/43e) or 2a8b3d638dbe778e (w42/w43/fixture-c). build_periodic_world.py ef984fc53708a0c3.
- Older scene files used by MMR-18 runs, identified by hash: final42 (31308/24736 2a) scene 63,090 B, 60 entities, nav 1,414,472 B (ac1a515a21d6). final43 (31308 2b) scene 63,051 B, 60 entities, nav 1,416,914 B (6a31528719d7). 23192 used final42b/43b: 76 entities, scene 70,740/70,701 B, nav 1,414,472/1,416,914 B. Terrain is unchanged in all of them.

## 5. Per-world navigation cache build (ms, R)
Source: raw `<run>.json` `outputs.worlds[i].mapSupport.generatedNavigationCaches.caches[k].generationMilliseconds` and `.elapsedMilliseconds` (total). Registration was ≤1.71 ms per cache. Cross-check: for all 54 caches, the log span `generated_navigation_generate_begin:<world>:<kind>` → `generated_navigation_register_begin` matches the recorded value within 1 ms. Example, 2248 world 2a All: `23:32:43.1207066Z +35019ms …generate_begin…All` → `23:33:09.6623388Z +61562ms …register_begin…All` = 26,543 ms. navigation-probe.json `accepted.navigationCacheBuildSeconds` repeats the 2248 values.

| Run | World | Default | Naval | All | Total (`elapsedMilliseconds`) |
|---|---|---|---|---|---|
| 2248 | 2a / 2b | 2,325 / 3,352 | 5,945 / 25,803 | 26,542 / 44,366 | 35,331 / 74,479 |
| 31308 | 2a / 2b | 2,272 / 3,364 | 6,071 / 25,600 | 26,256 / 43,699 | 35,139 / 73,541 |
| 24736 | 2a | 2,267 | 5,896 | 26,138 | 34,791 |
| 23192 | 2a / 2b | 2,258 / 3,225 | 5,884 / 24,932 | 26,048 / 42,626 | 34,671 / 71,624 |
| 22868 | 2a / 2b | 2,350 / 3,234 | 6,024 / 26,305 | 26,716 / 43,954 | 35,592 / 74,461 |
| 33472 (hist) | 2a / 2b | 2,402 / 3,175 | 6,058 / 24,833 | 26,980 / 42,537 | 35,980 / 71,470 |
| 12348 | 63042 (131,072 faces) | 19,092 | 128,297 | 418,966 | **566,815** |
| 24212 | 59042 / 59043 | 2,326 / 3,281 | 6,074 / 24,905 | 26,153 / 43,157 | 35,035 / 72,238 |
| 31624 | 59042 / 59043 | 2,311 / 3,207 | 5,882 / 24,936 | 26,229 / 43,057 | 34,883 / 72,049 |
| 8256 (load) | 59042 / 59043 | 2,300 / 3,218 | 5,989 / 25,067 | 26,799 / 43,191 | 35,235 / 71,720 |

- Every world has 32,768 nav faces except 63042 (raw `navFaces`). With the same face count, the seed-43 / 640-extent worlds (2b, 59043) take about 2.1x longer in total than the seed-42 / 800-extent worlds: Default 1.4x, Naval 4.2x, All 1.65x.
- Caches are built again on every save load (8256) and are never serialized (`serialized=false`, `officialCachesRead=false`).

## 6. Campaign bootstrap (ms, D from log offsets; nav total is R from §5)
Start is the `top_screen_changed to=…GameLoadingScreen` line (new game), or `save_identity_read` for the load run. End is `<world>:diagnostic_map_state_active`. "Reg" runs from start to `diagnostic_creator_installed`: all submodule registration and campaign initialization, and in the load run also package verification and deserialization (corrected after review; first draft called it dataset registration). "Scene.Read" runs `diagnostic_scene_read_begin` → `diagnostic_scene_loaded`, which is the whole `IMapScene.Load` (CreateNewScene, Read, weather setup, caches, OptimizeScene, tick), so it includes the weather setup below. The tail runs from `character_creation_active` to map active (automated character creation), or from `generated_save_loaded` for the load run.

| Run | World | Start→end offsets | **Total** | Reg | Scene.Read (read_begin→loaded) | Nav | Tail |
|---|---|---|---|---|---|---|---|
| 2248 | 2a | +23008→+65725 | **42,717** | 2,970 | 218 (+26096→+26314) | 35,331 | 2,409 |
| 2248 | 2b | +182778→+265545 | **82,767** | 3,743 | 389 (+186771→+187160) | 74,479 | 2,800 |
| 31308 | 2a / 2b | +22957→+65782 / +91940→+173376 | **42,825 / 81,436** | 3,010 / 3,590 | 221 / 363 | 35,139 / 73,541 | 2,644 / 2,639 |
| 24736 | 2a | +22825→+65130 | **42,305** | 2,919 | 233 | 34,791 | 2,503 |
| 23192 | 2a / 2b | +22878→+64969 / +119686→+199398 | **42,091 / 79,712** | 2,978 / 3,739 | 215 / 351 | 34,671 / 71,624 | 2,439 / 2,697 |
| 22868 | 2a / 2b | +22963→+66418 / +103706→+186553 | **43,455 / 82,847** | 3,102 / 3,747 | 248 / 389 | 35,592 / 74,461 | 2,486 / 2,695 |
| 33472 (hist) | 2a / 2b | +22998→+66686 / +102155→+181752 | **43,688 / 79,597** | 3,007 / 3,613 | 244 / 407 | 35,980 / 71,470 | 2,448 / 2,614 |
| 12348 | 63042 | +22854→+598254 | **575,400** | 3,140 | 358 | 566,815 | 2,533 |
| 24212 | 59042 / 59043 | +22880→+65729 / +96012→+176801 | **42,849 / 80,789** | 3,059 / 3,925 | 234 / 388 | 35,035 / 72,238 | 2,511 / 2,740 |
| 31624 | 59042 / 59043 | +22805→+65381 / +87831→+168084 | **42,576 / 80,253** | 3,002 / 3,738 | 219 / 398 | 34,883 / 72,049 | 2,493 / 2,602 |
| 8256 load | 59042 / 59043 | +22586→+62307 / +72838→+147829 | **39,721 / 74,991** | 2,421 / 1,977 | 127 / 127 | 35,235 / 71,720 | 220 / 122 |

- Navigation build is 82–83% of bootstrap for 800-extent worlds, 89–90% for 640-extent worlds, 89%/96% on save load, and 98.5% for 63042.
- Load run, world 59042: `+22586 save_identity_read` → `+22608 world_package_verified` (22 ms) → `+22823 save_deserialized` (215 ms after verification).
- Other recorded setup times (R): weather setup 94–240 ms (`mapSupport.generatedWeather.elapsedMilliseconds`; 36.6/53.4 ms on load). Battle-terrain index build 49.7–101.6 ms for the 800/640 worlds and 340.4 ms for 63042 (`mapSupport.battleSceneSelection.buildMilliseconds`; MMR-18 and 2248 report it as unsupported).
- Campaign throughput (R, `gameplay.realSecondsForTime` / `campaignDaysAdvanced`): 23192 ran 2.0 days in 40.39 s (2a) and 40.31 s (2b) at UnstoppableFastForward. 24736 ran 1.0 day in 79.95 s at UnstoppablePlay.

## 7. Mission load (MMR-60, MMR-63), seconds R

| Run / world | Field (scene) | Settlement | Naval | Teardown | Source |
|---|---|---|---|---|---|
| 22868 2a | 1.803 (battle_terrain_030) | 2.204 (empire_town_q) | 1.462 (battle_terrain_coastal_021) | 0.419 / 0.346 / 0.342 | mission-transition-probe.json `worlds[0].<kind>.mission.loadSeconds`/`teardownSeconds` (= raw 22868 `missions.<kind>.mission`) |
| 22868 2b | 0.936 (battle_terrain_t) | 2.209 | 1.487 (battle_terrain_coastal_03) | 0.315 / 0.368 / 0.355 | `worlds[1]` |
| 33472 (hist) 2a / 2b | 1.711 / 0.851 | 2.196 / 2.135 | 1.446 / 1.530 | 0.38–0.42 / 0.31–0.40 | raw 33472 `outputs.worlds[i].missions` |
| 12348 seam encounter | 1.724 | — | — | 0.470 | world-wrap-probe.json `seamEncounter.mission.mission.loadSeconds` |

- Agents / ships (R, `…mission.agents`/`ships`, run 22868): field 20/0, settlement 73/0 (2a) and 69/0 (2b), naval 84/2.
- For comparison, official content (MMR-13, performance.json `instrumented`): village mission 1.89 s, town 6.13 s, save 1.79 s, load to ready 9.74 s.

## 8. Saves and world packages

| Item | Value | Source |
|---|---|---|
| MMR-63 save near the seam | saveSeconds **0.2886 s** R; 19 parties reloaded, 95 fields compared, 0 mismatched | world-wrap-probe.json `seamSave.saveSeconds`, `.reloaded` (save file deleted afterwards, `saveDeleted=true`, so its size was not recorded) |
| MMR-19 save, world 59042 | file **121,809 B**, sha 5b6b721350dd4818 | Game Saves\mnbl_mmr19_0_5041d953d78842c6aa7865bbd850cda7.sav (name from two-world-load-matrix.json `saves[0].saveName`; size and hash read from disk) |
| MMR-19 save, world 59043 | file **121,220 B**, sha 8e0b83844fac7cd3 | mnbl_mmr19_1_c3608f2b55314ef0890d113d7d27b3ef.sav |
| MMR-19 save duration | **not recorded** (no saveSeconds field and no save log line in 31624) | — |
| World package 59042 (31624/8256) | **3,599,637 B**, 13 files. Manifest 8828c934b3b50901, placement a529b12851ad884d, scene files c2b1feaad3fff7cb, documents 41ee98324fb414a8 | %LOCALAPPDATA%\MNBL.ProceduralWorld\worlds\mnbl_00059042-8828c934b3b5 (size from disk); hashes from raw 31624 `worlds[0].identity` |
| World package 59043 (31624/8256) | **3,599,671 B**, 13 files. Manifest c25c9d9d4d269573, placement 1683f798fceef638, scene files 24b5ccce575524d5, documents cc119b0122152789 | …\mnbl_00059043-c25c9d9d4d26 |
| Package contents (59042) | NPCCharacters.xml 1,426,140; climate-wind.f32 2,097,152; SPCultures.xml 20,674; WorkshopTypes.xml 19,605; placement.json 8,251; Settlements.xml 6,146; world.json 5,912; build-receipt.json 5,692; other files under 6 KB | disk |
| MMR-59 packages (24212) | 59042-d0de74269b84: 3,593,832 B; 59043-886ae20fb57c: 3,593,866 B | disk; raw 24212 `worlds[i].package` |

- Scene files are not inside the package; they are installed under Modules/MNBL.ProceduralWorld/SceneObj (sizes in §4). Not accepted: history run 1116 saves mnbl_mmr19_0_72a4… (121,412 B) and mnbl_mmr19_1_2ff3… (122,351 B), and packages 280a65849d74 / 2ac2868e6228.

## 9. Scene resource counts in game

| Count | Value | Source |
|---|---|---|
| Settlements / clans / kingdoms | 4 / 3 / 2 in every world of every run | raw `worlds[i].finalCensus.campaignObjectManager.<list>.objects` (count, D) at phase `visible_paused_map`; bootstrap-probe.json `runs[].worlds[].campaignObjectCounts` (31308) |
| Alive heroes / dead heroes | 19–21 / 0 (varies by run) | same, `aliveHeroes` |
| Mobile parties | 17 (19 after the MMR-63 seam setup, `seamSave.reloaded.parties`) | `parties` |
| MBObjectManager characters / cultures | 905–907 / 17 | `finalCensus.mbObjectManager.characters` / `cultures` |
| Nav faces loaded | 32,768 (all bootstrap/env59 worlds); 131,072 (63042) | raw `worlds[i].navFaces` |
| Weather / climate buffers | snowRainBytes 2,048; weatherDimension 32, windDimension 512 | raw `mapSupport.generatedWeather.snowRainBytes`; environment-rebind-probe.json `worlds[i].weatherSetup` |
| Surface probe terrain | grid 129; extents 256/800/640; stableReadyFrames 30 | runtime_surface-32896.json `outputs.stages[i].manifest.gridSide`, `extent`, `stableReadyFrames` |

## 10. Peak RAM
- Recorded only for child processes (Windows job peak working set): fwg.exe 253.2–294.0 MiB in game (§2) and 61.7–235.5 MiB offline in MMR-24 (`peakJobMemoryBytes`), worldbridge reader 25.7–26.2 MiB, surface encoder 47.9–48.0 MiB.
- **Not recorded for the game process (Bannerlord.exe).** No accepted result, observation or log has a working set, private bytes, GC heap or virtual memory field. The only memory figure is installed RAM (§0).

## 11. Not recorded
1. Game-process peak RAM, working set, private bytes and GC heap, in every accepted run.
2. Hardware and graphics settings for each probe. Only the MMR-13 baseline records them (§0), so there is no proof the settings were unchanged.
3. Offline scene construction time (builder, worldbridge surface encode, nav build) for every fixture. Receipts have no timestamps.
4. MMR-19 save duration. The MMR-63 save size (the file was deleted).
5. In-memory sizes of loaded geometry and navigation. Only on-disk file sizes and face counts exist. Nav vertex and edge counts for fixture-c (only `navFaces` is in its receipt).
6. Scene entity counts read back from the engine at runtime. Entity counts are offline only (receipt or xscene count).
7. Frame time or tick cost on the generated map. The only tick figure is the MMR-15 menu tick gap, measured during generation.
8. Variance: each world/run value is a single sample. The closest repeats share a world ID but not always the same scene files or placement (§1): 2a bootstrap 42.1–43.7 s and nav 34.7–36.0 s across 6 runs; 2b bootstrap 79.6–82.8 s and nav 71.5–74.5 s across 5 runs.
