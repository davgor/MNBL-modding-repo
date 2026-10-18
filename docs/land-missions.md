# Land missions and settlement interactions on generated worlds (MMR-52, 2026-10-18)

MMR-60 proved mission entry and return for one land battle and one settlement location. MMR-42 made every mission scene choice deterministic and checked. MMR-52 plays the land missions that generated-world inputs change, on world A's designated test save:
- a battle on every battle class the world has, a crossing and a retreat;
- a town with a conversation and its tavern;
- a castle and a village;
- a siege assault, a defeat with capture and escape, and a hideout.

World B (MMR-19's fixture world) supplies what A lacks. Stock in-mission mechanics on unchanged stock scenes (keep fights, sally-outs and reliefs, prison breaks, wall breaches, the arena) are not retested.

| | |
|---|---|
| Probe | `land_missions` (`Automation/LandMissionsProbe.cs`). `phase=prepare` writes world A's designated save `mnbl_probe52_A_designated`; `phase=run` loads it, writing it first if it is missing |
| Scenarios and regressions | `tests/Runtime/LandMissionScenarios.json`: the worlds, the rules, 14 cases (player, natural, harness) and every regression with its guard |
| Verdict | `tools/summarize_land_missions.py <pid>` recomputes every case from the run's outputs with the scenario file's rules into `evidence/gameplay/land-missions.json`. The probe's own failures are a subset |
| Regression fixture | `tests/Runtime/fixtures/land-missions-<pid>.json`: the accepted run, trimmed, with user paths replaced |
| Offline | `tools/Test-LandMissions.ps1` runs the scenario file check (`tests/Runtime/check_land_mission_scenarios.py`) and the regression replay (`tests/Runtime/test_land_missions_regressions.py`), logged to `evidence/gameplay/land-missions-tests.txt` |
| Shared pieces | `Automation/AssertCapture.cs` (engine asserts, through a `Debug.DebugManager` wrapper with a canary); `Missions/LocationSceneAudit.cs` (MMR-42's location audit, run live); `GeneratedBattleTerrain.SampleCell` (the battle map's own cell sampling) |
| Review | `evidence/fwg/land-missions-review.md` |

## What a run does

**Setup** (recorded in `outputs.setup`):
- Autosave is disabled through a `NoAutoSave` save manager, so no case writes a save.
- Other parties ignore the player for 60 days (`IgnoreByOtherPartiesTill`).
- Each case runs fail-soft and records its own failures, the engine asserts it raised and its window of the engine log.
- Before each case, campaign time is stopped. Unless the map is idle, the designated save is reloaded first, with the reason recorded. Idle means no mission, encounter, map event, settlement, siege camp, captivity or open menu.

**Terrain:**
- Every cell of the battle map (400 × 400 on A) is sampled as the battle map samples it (`SampleCell`).
- MMR-42's resolver classifies each cell into a battle class: open, woodland, steppe, rock, cold, desert or wetland. Biome comes first, then face terrain.
- Every cell with a land sample counts, as the battle map classes every such cell. A marsh cell with water at its centre is still wetland.
- Each class's first usable sample point is chosen, centre first: valid land navigation, away from settlements, on a patch that names exactly one land scene.
- A crossing cell is one with a Bridge or Fording sample.

**The battles** (each field class, the crossing and the retreat):
- **Setup:** the player's party is placed at the point. A lord party of the next culture present is placed beside it, its kingdom put at war for the battle and peace made after.
  - A lord resting in a settlement leaves it first.
  - A party owning ships fights on land like any other when it is not at sea. War Sails gives most northern lords a fleet.
  - After the battle the enemy is put back where it was, into its settlement if it left one.
- **The battle:** the official encounter and its attack option. Only those two parties may fight, in a land map event.
- **The scene:** the engine must open the scene the patch names. MMR-42's guard must have made its own observation of this battle's choice and agreed.
- **In the mission:**
  - deployment is finished;
  - every active human agent must stand on the scene's navigation mesh, inside its boundaries;
  - every agent's gear is checked;
  - a few seconds run.
- **The end:** the mission ends through `Mission.OnEndMissionResult`. Its installed implementation also leaves by retreating, so the battles and the retreat leave the same way. The retreat calls `Mission.RetreatMission` and must leave the battle to the enemy: the player's side did not win, and the enemy remains with healthy troops.
- **The return:** the party must come back within the vanilla encounter bound, on land, on valid navigation.
- **Substituted classes:** woodland and rock fight on Plain scenes and cold on Steppe scenes, since no Forest, Mountain or Snow battle scene is installed.

**The settlements:**
- **Town:**
  - entry through the official settlement encounter and its menu;
  - the center (`town_streets`), with a conversation through `MissionConversationLogic.StartConversation`, recording the agent and its first line. A notable is preferred, else a merchant;
  - the tavern, through the location encounter (`CreateAndOpenMissionController`, as the tavern door does);
  - the menu's leave path.
- **Castle:** given to the player's clan for the visit, so its gates open without the guard's dialogue, and given back; both steps recorded. Its courtyard, then the leave path.
- **Village:** its center, then the leave path.
- **Each must:** open its mapped scenes and return the party to the gate it entered.

**The siege:**
- **Target:** the castle farthest from its own kingdom's lord parties; the outside menu's besiege option.
- **The camp's own preparation**, observed for 1.5 campaign hours: its rate per hour, and a time series every half hour of the menu, the siege, the progress and the player's map event.
- **The shortcut:** at the installed rate the camp needs about four days, and the garrison sallies out within hours (run 17928), a stock mechanic. So the camp is brought to 0.99 as diagnostic setup, recorded with the progress it reached by itself, and the engine's tick completes it.
- **The assault:** `menu_siege_strategies_lead_assault` must open the castle's mapped siege scene with deployment; the mission must return the party to its camp; the siege is then lifted through the menu.

**The capture:**
- **Setup:** the player's party is reduced, and a lord party three times its size attacks.
- **The defeat:** every agent of the player's team falls. The mission, ended by its own logic, is left as the leave button leaves it.
- **The escape:** once the campaign takes the player prisoner, campaign time is stopped and the installed escape (`EndCaptivityAction.ApplyByEscape`, without notification) frees the party. The release moves the party twice:
  - `PlayerCaptivity.EndCaptivityInternal` puts the party at its captor and teleports it out of the encounter radius;
  - the release's `OnHeroPrisonerReleased` listener then calls `PlayerEncounter.ProtectPlayerSide`, which teleports it once more.

  So the party must be within 2 × 1.25 × the encounter model's land radius of the captor's position.

**World B**, for the classes and the crossing A lacks: started from its fixture after A, sampled as A, one battle each. It keeps its own culture tally. World A's designated save is then loaded again.

**The hideout**, last of all (the game hung at its boss conversation twice while the probe was wrong, below):
- **Timing:** outside the hideout model's night hours, running campaign time past them if needed. Sneaking in by night opens a different, stealth mission.
- **Entry:** the hideout's assault option. The troop selection it opens is answered with the selection it offers.
- **The mission** must open the hideout's mapped `hideout_center` scene:
  - bandits fall only in the controller's fight phases, and the boss only in the boss fight;
  - the boss's dialogue is answered through its own battle option;
  - the mission must be won, the hideout no longer infested, and the party back at the gate.

**Natural checks:**
- **Gear:** every agent of every battle wears body armour and carries a weapon, and every item's meshes are found (a crafted weapon's pieces, else its multimesh). Each culture present must be seen with troops and a hero of a party other than the player's.
- **Settlements:** every settlement's location scenes are checked against the mission contracts.
- **Names and menus:** every generated name and encyclopedia link resolves, and every menu opened has its options.
- **The engine log:** no missing line names a generated object or an opened scene, and every other missing line is of a stock kind the scenario file names (`stockMissingPatterns`). No engine assert fires.
- **Baselines:** checked at start and end, and the run's rules must equal the scenario file's.

## Run 33816 (163/163 recomputed checks, 14/14 cases, status ok)

Loaded from the designated save in 399 s (the first campaign of a module build regenerates world A's navigation caches). World B started in 38 s; world A loaded again for the hideout in 15 s.

MMR-53 changed the shared harness (placements recorded by `Enter`, a stage and screenshot tag, the return check's bound and domain). Run 38972 on that build passed the same 163/163 checks and 14/14 cases (wetland absent), status ok. This document's evidence stays run 33816's.

**Terrain.**
- **World A:** 5,548 land cells (4,695 with a land centre): cold 2,807, desert 1,308, woodland 740, open 663, rock 30. There are 10 crossing cells, all Bridge.
- **World B:** 11,652 land cells: open 11,625, steppe 27, no crossing.
- **Absent:** steppe is absent from A and fought in B. Wetland is absent from both at full resolution, the only case listed as absent.

**The battles.** Each was fought against a lord party of the next culture, and each opened exactly the patch's scene, with MMR-42's guard observing the battle's own choice and agreeing.

| Case | Point (biome) | Scene | Enemy (troops, ships) | Agents | Return |
|---|---|---|---|---|---|
| open | Plain face (ocean-biome coast) | `nord_battle_terrain_f` | aserai (66, 1) | 96 | 0.574 |
| woodland (substituted: Plain) | Plain face (boreal forest) | `battle_terrain_biome_058` | battania (58, 2) | 88 | 0.517 |
| rock (substituted: Plain) | Plain face (exposed rock) | `battle_terrain_020` | empire (62, 2) | 92 | 0.520 |
| cold (substituted: Steppe) | Plain face (land ice) | `battle_terrain_biome_130` | khuzait (64, 2) | 94 | 0.521 |
| desert | Plain face (desert) | `battle_terrain_biome_006` | nord (79, 2) | 109 | 0.558 |
| crossing | Bridge face (forest) | `battle_terrain_031` | sturgia (55, 2) | 85 | 0.541 |
| retreat | the open point | `nord_battle_terrain_f` | vlandia (60, 2) | 90 | 0.550 |
| steppe, world B | Steppe face | `battle_terrain_biome_047` | empire (126, 3) | 156 | 0.565 |

- **Every battle:**
  - only the two parties fought, in a land map event, although every northern lord owned ships;
  - deployment finished, and about 3 s of mission time ran;
  - every agent stood on the navigation mesh inside the boundaries;
  - no agent's gear was incomplete;
  - the party came back within the 0.635 bound, on land, on valid navigation.
- **The retreat** left the battle to the enemy: the map event had no winner, and the enemy kept its 60 healthy troops.
- **The gear tally** (world A, the player's party excluded): every culture present was seen with troops and a hero, none incomplete.

| Culture | Troops | Heroes |
|---|---|---|
| aserai | 162 | 2 |
| battania | 57 | 1 |
| empire | 61 | 1 |
| khuzait | 63 | 1 |
| nord | 338 | 1 |
| sturgia | 54 | 1 |
| vlandia | 59 | 1 |

  The desert bandits of the hideout appear too (20 troops).

**The settlements.**
- **Town** `t_02135fe588`, center `vlandia_town_c` (86 agents):
  - **Conversation:** the notable Cherald of Mekrak (an artisan), first line "Yours is not a face I know. What is your name, stranger?"
  - **Tavern** `vlandia_tavern_interior_a` (24 agents): the tavern keeper, "Good day, sir. How can I help you?"
  - **Agents off the mesh:** 2 seated agents in the center and 11 seated agents plus the musician in the tavern, at the stock scenes' own chairs and music point.
- **Castle** `c_00a466803b`: courtyard `sturgia_castle_siege_001` (15 agents); given to the player and given back.
- **Village** `v_000b8059fa`: center `nord_village_a` (23 agents).
- **Each** opened its mapped scenes and returned the party to its gate (displacement 0).

**The siege**, at castle `c_481845094e`: its kingdom's nearest lord party was 33.4 map units away, and its garrison held 100.
- **Preparation:** the camp built at 0.0106 per hour, about 94 hours for a whole preparation, and reached 0.014 by itself in 1.5 hours. It was then advanced to 0.99 (diagnostic setup) and completed by the engine's tick, 2.6 hours after the siege began, without interruption.
- **The assault** opened `nord_castle_b`, the castle's mapped siege scene, with deployment (375 agents, all on the mesh). It returned the party to its camp, and the siege was lifted.

**The capture.**
- **Setup:** the player's party (4) attacked a lord party of 98 and was defeated in mission.
- **Prisoner:** the campaign took the player prisoner of `h_f1d8309dd9_party_1`. Time was stopped in captivity.
- **The escape** released the party 0.727 from the captor's position, within the double teleport's 1.26, on land.

**The hideout** `o_0046b90bef`:
- **Entry:** at hour 9.0, outside the night hours (23 to 2), through its assault. The troop selection offered 30 and took 15 (8 to 15).
- **The mission** opened its mapped scene `desert_hideout_004_sv` (35 agents; 5 bandits seated at the camp's chairs).
- **The phases**, in order: the first fight (20 bandits fell), the cutscene, the leaders' conversation and the boss fight with all. The boss's dialogue was answered with "I don't fight duels with brigands."
- **The end:** the mission was won (25 fell), the hideout is no longer infested, and the party is back at its gate.

**Natural checks.**
- **Location audit:** all 358 settlements' location scenes meet the contracts, and all 3,009 scene references exist.
- **Names and menus:** 659 names and encyclopedia links resolved; 8 menus opened, each with its options.
- **The engine log:**
  - no case logged a missing generated object or opened scene;
  - every missing line was a stock shader variant compiled on first use or an unvoiced line, 2 to 12 per case;
  - no engine assert fired, and every canary was seen.
- **Baselines:** held at start and end.

Screenshots of every mission are archived with the run (git-ignored: `evidence/runtime/in-game/land_missions-33816-*.bmp`).

## Failures found, and their regression guards

The scenario file lists 19 regressions. Each names the run or the review finding, the cause, the fix and the guard: a mutation of the committed fixture that reintroduces the failure and must fail its case. The replay checks all 19 and the clean fixture (20/20).
- **Development runs 29768, 25280, 17928:**
  - crafted weapons draw from their pieces;
  - lords rest in settlements and own ships;
  - the hideout's options and troop selection;
  - the hideout boss conversation hung the engine while the probe killed agents through its cutscene;
  - the garrison's sally-out against a camp that needs four days;
  - a defeated mission waits for the player's leave;
  - a crossing hides between sampled cells.
- **The review** (`evidence/fwg/land-missions-review.md`): returns, spawns, the log filter, the culture tally, the idle check, water-centre cells, parked parties, the retreat's outcome, the guard's own observation, and the siege shortcut's wording.
- **Confirming runs 3940, 37204, 35564, 43696:**
  - a campaign-map navmesh query hung a tavern mission;
  - seated agents and the stock scenes' usable points stand off the mesh;
  - the escape teleports the party twice.

## What this does not prove

- **Wetland battles.** Neither world has a wetland cell (A has no marsh biome or wetland face; B, the fixture world, has face terrain only). A wetland battle is absent, not passed.
- **Village raids, lord's hall fights and port visits** were not opened. Naval battles and ports are MMR-53's.
- **The siege camp's natural preparation** was observed for 1.5 hours, not completed: about 94 hours at the installed rate. The garrison's sally-out and reliefs are stock mechanics the ticket excludes.
- **The capture's escape** is the installed action applied at once, not the captivity's hourly chance; the captivity menus were not passed.
- **The battles end by retreating:** the installed `OnEndMissionResult` and the retreat both leave the battle, so no field battle was won or lost in the mission (the hideout was won and the capture lost).
- **Gear is checked by data:** armour, weapons and every mesh found for every agent. The screenshots show the player's view of each mission, not each culture's troops close up.
- **World B from a clean checkout:** its fixture placement and catalog receipt are git-ignored research outputs, as for MMR-51. The scenario check notes them when absent.
- **One designated save:** each case starts from world A's save (reloaded when a case leaves anything but an idle map) at its own time of day. The hideout ran at hour 9, so its night stealth mission was not played.
