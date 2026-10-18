# Naval gameplay, raids, blockades and mission outcomes (MMR-53, 2026-10-18)

MMR-53 plays War Sails on the generated world. It loads world A's designated test save (MMR-52's) and runs the player's party through War Sails' installed paths, each through the call its menus or a map click make:
- **Shipyard:** buying a ship and repairing one.
- **Voyages:** setting sail at a port, port-to-port and island voyages, a village drop-off with re-embarking at the anchor, and a coastal landing by a click on land with re-embarking at the anchor it leaves.
- **Blockade:** a coastal blockade.
- **Battles:** a pirate battle at sea ending in a retreat, a seaborne village raid, and a naval defeat with capture and the raft.

Meanwhile it records AI sea travel and audits the pirates against MMR-35's spawn points (C06) as MMR-39's wrapper serves them (W03).

Owned elsewhere and cited, not retested:
- wind, wakes, storms and attrition, and where pirates spawn from (MMR-39);
- restricted lakes, rivers and bridge passages at route level (MMR-38, MMR-17);
- naval mission return fidelity on probe worlds (MMR-60);
- embarked and post-battle save/load (MMR-49);
- seam movement (MMR-65) and seam encounter orientation (MMR-42).

| | |
|---|---|
| Probe | `naval_missions` (`Automation/NavalMissionsProbe.cs`), on MMR-52's harness: `LandMissionsProbe`'s state, cases, menus, mission runner, spawn and return checks, and log window |
| Scenarios and regressions | `tests/Runtime/NavalScenarios.json`: the world, the pinned regression targets, the rules, 13 cases and every regression with its guard |
| Verdict | `tools/summarize_naval_missions.py <pid>` recomputes every case from the run's outputs with the scenario file's rules into `evidence/gameplay/naval-scenarios.json` |
| Regression fixture | `tests/Runtime/fixtures/naval-missions-46036.json`: run 46036, trimmed, with user paths replaced |
| Offline | `tools/Test-NavalMissions.ps1`: the scenario check (`tests/Runtime/check_naval_scenarios.py`) and the regression replay (`tests/Runtime/test_naval_regressions.py`), logged to `evidence/gameplay/naval-scenarios-tests.txt` |
| Review | `evidence/fwg/naval-missions-review.md` |

## The installed paths, as the probe takes them

**Order and placement.** The cases run in order and share the party and its fleet:
- a voyage starts at the town where the previous step docked the fleet, with the party at the gate where the leave path put it. `LandMissionsProbe.Enter` records every placement at a gate with where the party came from and how far (`relocations`), and the summary checks each voyage's is within the land return bound;
- the purchase starts from the designated save's position, so its placement at the start town is a relocation;
- the blockade moves the party to the besieged town (`relocatedFrom`) and, after the siege, back to where it found it (`movedBack`);
- the pirate battles move the pirate beside the party (`pirateFrom`, `pirateMoved`) and send it back afterwards.

A step that fails stops the steps that need its result, which report "the previous step did not finish".

**The voyage's settlements** are pinned in the scenario file (`targets`), and the probe uses the pins, each still checked against its rule (a pin that no longer meets it fails the run instead of choosing another). The rule, which chose them first, uses the engine's own reachability (`CampaignPlayability`: land groups, and basins of ports):
- **start:** a peaceful port town of the main land group with a tradeable shallow-draft ship;
- **mainland:** the nearest other port town of the main land group in the same basin;
- **island:** an island's port town (another land group) in that basin;
- **village:** a harbour village reachable by sea.

**Shipyard:**
- **Buying:** the purchase goes through War Sails' `PortScreenTradeModeHandler`, the handler the port screen builds for the town and the player, reached by reflection because `NavalDLC.ViewModelCollection` is not a compile reference. The probe reads the buy button's state (`GetCanBuyShip`: relevant, enabled, cost), then calls `OnBuyShip`, `GetCanConfirm`, then `OnConfirmChanges`, the screen's Done. That pays `ShipCostModel`'s trade value and docks a new fleet at the town.
- **Repairing:** a ship damaged through `Ship.OnShipDamaged` (recorded setup) is repaired by the port menu's `repair_ships`, which runs `RepairShipAction` per damaged docked ship at the model's cost.

**Voyages:**
- **Setting sail:** the town's `port` option opens `port_menu`; `sail_option` runs `SetSailAtPosition` at the port and finishes the encounter. It needs the fleet docked within 0.05 of the port.
- **The voyage:** ordered as a map click orders it. `NavigationHelper.GetInteractionDataForMainParty` gives the navigation type and the port target, then `MobileParty.SetMoveGoToSettlement`. Campaign time runs until arrival, sampled every campaign hour.
- **Arrival at a town by sea:** `naval_town_outside` switches to the port menu for a peaceful town. `EnterSettlementAction` has already put the party on land in the town with its fleet docked. The town center and `town_leave` then put it at the gate.
- **Drop-off at a harbour village by sea:** the party stays at sea with the village as its settlement. The village menu's `disembark` lands it at the gate and docks the fleet at the village's port.
- **Re-embarking:** a click on the anchor, `MobileParty.SetMoveGoToInteractablePoint(anchor, All)`. The anchor's interaction sets sail at it.
- **Coastal landing:** a click on land from the sea, through `MapState.ProcessTravel`, the terrain click's own call. It takes NavigationHelper's embark and disembark data for the player, then `MobileParty.SetMoveGoToPoint` with the navigation `CanPlayerNavigateToPosition` gives. The target is a land point 2 to 4 from the village's gate. The party sails to the coast, makes the sea-to-land transition and walks on, and its anchor stays where it came ashore. The probe then re-boards at that anchor.
- **The water's width** is sampled every campaign hour of a voyage, away from both of its ends (`straitEndDistance`). Across each of 8 axes it is the distance to the first point off naval navigation one way plus the other, in steps of 0.25 up to 8 each way; the narrowest axis is the width. A strait reads its width and open sea reads 16.

**Pirates:** at sea, a pirate party is moved beside the party (recorded setup). The encounter is the campaign's: `PlayerEncounter.Start`, then its non-public `Init` with the attacker and defender, as `EncounterManager.StartPartyEncounter` does. `PlayerEncounter.SetMeetingDone` sits between them, because the at-sea meeting is a conversation mission. The encounter menu's `attack` then opens War Sails' naval battle mission. Both battles check its scene against the one `DefaultSceneModel.GetBattleSceneForMapPatch` must pick for the party's patch (MMR-42's table). Afterwards the pirate goes back where it was found and patrols its assigned zone again (`SetMovePatrolAroundPoint`, as `PiratesCampaignBehavior` sends it).
- **The retreat (pirate.battle):** after a few seconds the mission is left (`OnEndMissionResult`, a retreat) and so is the encounter (the attacker's `leave`). No side wins.
- **The defeat (naval.defeat):**
  - the pirate is another party than the retreat's (excluded by id), three times the player's size;
  - the party is cut to a crew that all goes aboard (only the troops the ships carry fight, and the rest would keep the battle alive);
  - every agent of the player's side falls, and the campaign takes the player prisoner (the probe answers the defeat menu if one shows; in run 46036 none did, the player was a prisoner when the mission closed);
  - the escape is forced (`EndCaptivityAction.ApplyByEscape`), where captivity would otherwise wait on its daily escape chance;
  - at sea, War Sails' raft menus follow (the raft, its wait, its end), and campaign time runs until the raft washes ashore.

**The seaborne raid:**
- **Target:** a harbour village of another kingdom. War is declared for it and peace made after, both recorded.
- **Approach:** the voyage, then the village menu at sea, `hostile_action`, `village_hostile_action/raid_village`, and the encounter's `attack`.
- **Troop selection:** the attack opens the naval troop selection (`MenuContext.OpenNavalTroopSelection`). The probe answers it with the troops and ships it offers, through the menu handler proxy shared with MMR-52's hideout.
- **The mission:** War Sails' naval raid, on the village's center scene in its `naval_raid` level, with its landing setup counted.
- **The landing:** War Sails' `NavalRaidMissionController` runs it. The ships sail the scene's landing paths and ground, and the troops land. The probe waits for the controller's `_hasLandingCompleted`, then counts the player's agents ashore and on a deck as War Sails tracks them (`AgentNavalComponent.SteppedShip`).
- **The fight:** the probe kills the defenders, in the player's agent's name, every half second until the mission's result is the player's victory.
- **Afterwards:**
  - the victory's loot screens are cancelled (nothing taken);
  - the raid's wait menu runs until the village is looted;
  - its end menu follows: `village_player_raid_ended`, or `village_raid_ended_leaded_by_someone_else`;
  - every map event that ends at the village is recorded with what `VillageHostileActionCampaignBehavior.OnMapEventEnded` reads to choose between them: a raid, the player's, the attacker leader the main party, the village raided.

**The blockade:**
- **Setup:** a port town of another kingdom is besieged from land by the party owning its fleet (`town_outside/town_besiege`). A siege cannot start from the sea: `naval_town_outside` has no besiege option.
- **Activation:** War Sails' `SiegeEvent` constructor activates the blockade when a besieger has ships.
- **Checks:** the player's anchor is disabled. The town's `BlockadePositionScript`, built from MMR-40's arc entities, lays its arcs both for the fleet and for its full arc set, every point checked on water.
- **Lifting:** through the siege menu. The party is then moved back to where the case found it.

**Natural checks:**
- **AI sea travel:**
  - every AI land/sea transition (`OnMobilePartyNavigationStateChangedEvent`) is recorded, and each party is checked an hour later on its domain's navigation;
  - arrivals by sea at port towns are counted, island port towns apart. An arrival is a landing and an entry into the town in the same tick: `EnterSettlementAction.ApplyForParty` sets `IsCurrentlyAtSea` false, then the settlement. Entries without that landing are counted as `portEntriesOverLand`.
- **The pirate audit:**
  - the served spawn points match the world's plan, clan by clan, and each is on naval navigation;
  - War Sails' patrol zones (`PiratesCampaignBehavior._patrolZones`) are exactly the served points: as many zones as served points, each one of them;
  - the inner scene's own points (`PirateSpawnPoint` entities, the installed Calradia coordinates' source) are 0 by construction on the generated scene, so the zone count carries the check;
  - every pirate party is at sea, on naval navigation, and assigned a served zone (`_assignedZones`). How far a party is beyond its zone's radius is recorded, not checked: a patrolling party chases and flees.
- **Spawns, logs, baselines:**
  - every mission's agents stand on the navigation mesh or on a scene object (a ship's deck, a seat), inside the boundaries;
  - the engine log has no missing generated asset and no missing line of an unknown kind;
  - no engine assert fires;
  - the baselines hold.

## Setup the probe applies

Every shortcut is recorded in the run's outputs:
- **Autosave off:** `LandMissionsProbe.Setup` replaces the save manager, so no case writes a save.
- **The player ignored:** every other party ignores the player for 60 days (`IgnoreByOtherPartiesTill`). No pirate or lord starts an encounter with the party; every encounter here is the probe's.
- **Added to the party:** a crew up to `crew` (the culture's basic troop), `grain` grain for the voyages, and gold for the purchase up to its price plus `goldMargin`. The defeat cuts the party to `defeatTroops`.
- **Fast-forward:** campaign time runs in unstoppable fast-forward during voyages, the coastal landing, the raft and the raid's wait menu, and is stopped before every case.
- **Wars:** the raid and the blockade declare war on their target's kingdom and make peace after, both recorded.
- **Placements:** the party at each town's gate (`relocations`), at the besieged town and back, and the pirates beside the party and back (see "Order and placement").
- **The escape is forced** at once after the capture (`EndCaptivityAction.ApplyByEscape`).
- **The raid's defenders** are killed by the probe; no AI combat decides the raid.

## Run 46036 (104/104 recomputed checks, 13/13 cases, status ok)

World A's designated save loaded in 404 s (navigation caches rebuilt after the build). The cases ran without a reload, without a failure, without an engine assert and without a missing line of an unknown kind. The voyage's settlements came from the pins.

**Shipyard:**
- **Purchase:** at `mnbl_1609fb72_t_30a1119e46`. The port screen's buy button was enabled at 12000 for Gatopard (`western_light_ship`), the cheapest shallow-draft ship. The probe added 13000 gold, 39 troops and 200 grain. The purchase paid 12000 and docked the fleet at the town.
- **Repair:** at `mnbl_1609fb72_t_e7ed06396a`. Damage of 4320 left 6480 of 10800 hit points; the port menu's repair restored 10800 for the model's 800.

**Voyages** (every hourly sample on naval navigation, every arrival on land in the town with the fleet docked, every return at the gate, every placement 0 from where the previous step left the party):

| Leg | Hours | Sailed | Narrowest water |
|---|---|---|---|
| route.port: `..._t_30a1119e46` to `..._t_e7ed06396a` | 22.27 | 41.68 | 8.5 |
| route.island: `..._t_e7ed06396a` to the island's `..._t_7efe6a0c96` | 32.2 | 77.85 | 8.5 |
| dropoff: the island town to the harbour village `..._v_f924958cd5` | 8.38 | 12.91 | 8.5 |
| raid: the village's waters to `..._v_1b54c2686d` | 17.5 | 41.32 | 11.25 |

The width measurement reaches 8 each way, so 8.5 means land 0.5 off one side and none within 8 on the other: the legs hug a coast, and none crosses a strait narrower than the measurement's reach.

**Drop-off and coastal landing:**
- The village menu's disembark put the party at the gate with the fleet at the village's port, and the click on the anchor took it back aboard.
- The click on land (`All` navigation, 8.34 from the party) brought the party ashore through a 2.02-hour sea-to-land transition, to 0.04 from the clicked point, on land navigation.
- The anchor stayed on naval navigation 1.0 from where the party came ashore, and the click on it took the party back aboard.

**Blockade:**
- **Activation:** the party moved 40.2 to `mnbl_1609fb72_t_db2d888bf1` and besieged it from land. War Sails activated the blockade (1 event), with the player's party, holding 1 ship, as the blockader, and disabled the player's anchor.
- **Arcs:** the fleet's arc (1 point) and the script's full arcs (10 points) were all on water, centred 1.55 from the port.
- **Lifting:** the menu lifted the siege (1 deactivation, the anchor enabled), and the party went back where it was found (0 away).

**Pirate battle (retreat):**
- `southern_pirates_742` (4 ships, 66 troops) was moved 28.3 to the party.
- They met in a naval map event of exactly the two parties. The mission opened the patch's scene `battle_terrain_coastal_021`; 98 agents spawned on the navigation mesh or a deck.
- After 3.05 s of mission time the probe retreated. The party returned at sea 1.755 from its start (bound 1.885) on naval navigation, its one ship at 10800 of 10800.
- The pirate was sent back to patrol its zone.

**Seaborne raid:**
- **Approach:** the probe declared war on `mnbl_1609fb72_k_de9f25ee81` for `mnbl_1609fb72_v_1b54c2686d`.
- **Troop selection:** it offered 40 troops and 1 eligible ship; 32 troops were selected.
- **The mission:** `aserai_village_j_raid` in its `naval_raid` level, with 10 landing, 3 jumping and 1 player spawn entity.
- **The landing:** War Sails' controller read 4 landing paths of 8 frames each, started landing after 51.2 s and completed after 80.5 s. Then 27 of the player's agents were ashore, none on a deck.
- **The fight:** the probe killed 53 defenders, for the player's victory.
- **Afterwards:**
  - the party and inventory screens were cancelled;
  - the wait menu ran until the village was looted;
  - the end menu was `village_raid_ended_leaded_by_someone_else`;
  - the party stayed at sea at the village's port (0 away, naval bound 1.885), and peace was made.

**The raid's end menu, explained.** Both records of the raid map event's end name the player's party as the attacker leader and the player's map event, with the village `BeingRaided`, not raided. The installed engine's order explains it:
- `MapEvent.FinalizeEventAux` dispatches `OnMapEventEnded` (IL_00cd) before the components' `BeforeFinalizeComponent` (IL_0353);
- `RaidEventComponent.OnBeforeFinalize` is the only caller of `ChangeVillageStateAction.ApplyBySettingToLooted`;
- so `VillageHostileActionCampaignBehavior.OnMapEventEnded` reads `Settlement.IsRaided` false and opens `village_raid_ended_leaded_by_someone_else`.

This is the installed engine's order, not the generated world's. A land raid that ends by looting would read the same; that is inferred from the IL, not run.

**Naval defeat:**
- **Setup:** the party was cut to 9 (8 troops and the hero). It met `northern_pirates_1090` (4 ships, 62 troops; the retreat's pirate excluded) on the patch's scene `battle_terrain_coastal_caspian_sea_02`.
- **The defeat:** all 8 troops fell, and the player was a prisoner of `northern_pirates_1090` at sea when the mission closed (no defeat menu showed).
- **The raft:**
  - the forced escape left the party at sea with 0 ships, at `player_raft_state_after_prisoner`;
  - the raft menus followed: its wait, then its end;
  - the raft washed ashore 1.20 from where the party took to it, on land navigation, out of raft state, with no ships to give back.
- **Afterwards:** the pirate was sent back to patrol its zone.

**AI sea travel:**
- **Transitions:** 195 to sea (lords 117, villagers 53, bandits 17, caravans 8) and 86 landings (all lords). All 228 parties checked an hour later were on their domain's navigation.
- **Arrivals from the sea:** 54 at port towns, all lords. Of these, 36 were at island port towns, reaching 15 of the 21.
- **Other port-town entries:** 1069, counted apart.

**Pirates:**
- **Served points:** the wrapper served all 53 of the plan's points: northern 33 and southern 20, each the plan's count for its clan, all on naval navigation.
- **Zones:** War Sails built 53 patrol zones, each on a served point. The inner scene's own points: 0.
- **Parties:** 44 (northern 27, southern 17), all at sea, on naval navigation, each assigned a served zone. One southern party was beyond its zone's radius when read.

## Failures found, and their regression guards

Five development runs before the review and the review itself found faults in the probe, each in the scenario file's `regressions` with the case that guards it and a mutation of the committed fixture that must fail a named check (`tests/Runtime/test_naval_regressions.py`):

| Regression | Found by | Guard: the case fails on |
|---|---|---|
| repair-before-the-menu-view | run 27796 | shipyard: the repair restored the ship |
| traffic-read-at-creation | run 27796 | ai: every AI party checked an hour after its transition |
| raid-loot-screens | run 26772 | raid: the raid won and the village looted |
| raid-end-menu-of-another-leader | run 43188 | raid: still at sea after the raid |
| landing-setup-by-tag-expression | run 26772 | raid: the scene with its landing setup |
| raid-return-checked-on-land | run 36684 | raid: the return checked at sea with the naval bound |
| defeat-with-reserves | run 36684 | naval.defeat: taken prisoner |
| raft-time-locked | run 42652 | naval.defeat: the raft washed ashore |

The review's findings and their fixes are in `evidence/fwg/naval-missions-review.md`.

## What this does not prove

- **Combat as a player fights it:**
  - boarding;
  - crew reinforcement during a battle;
  - ship damage taken in battle (the retreat leaves after 3 s; the repair's damage is applied directly);
  - a battle won by fighting. The raid's defenders are killed by the probe and the pirate battle is a retreat; no AI combat decides an outcome.
- **Return to a ship inside a mission:** not exercised.
- **Visual against functional water:** the checks are functional (navigation, spawns, scene choice, state). The only visual evidence is the archived mission screenshots (git-ignored: `evidence/runtime/in-game/naval_missions-46036-*.bmp`); nothing judges how water, ships or wakes look.
- **Narrow straits:** no leg crossed water narrower than 8.5 (see the voyages). Restricted passages at route level are MMR-38's and MMR-17's.
- **The blockade:** no blockade battle, no AI blockade, and no sea approach. The arcs are one random draw (`GetBlockadeArc` uses `MBRandom` and advances the campaign's random state). The blockade activated for the player's ship docked at the island town, about 40 from the besieged town, as the installed rule allows.
- **Pirates:** no pirate starts an encounter, because the player is ignored. A party's distance from its zone is not checked. C06's points are the plan NavalContentFactory compiles in the session and hands to the wrapper; a production world has no separate on-disk artifact to compare the served points with.
- **AI:** arrivals by sea are counted, not followed: a party's route and its fleet are not checked. Arrivals at harbour villages are not counted.
- **World B:** naval play runs on world A only. The MMR-19 fixture world (w42) has no designated save for these cases.
- **Long-run naval play** (storms, attrition, many days at sea) is MMR-39's and MMR-51's.
