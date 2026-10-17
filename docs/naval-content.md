# Ports, fleets and naval faction records on a generated world (MMR-35, 2026-10-15)

A generated world's coastal towns and harbour villages must fit the installed War Sails contracts: port towns with shipyards that build their culture's ships, villages where fleets land, fleets only for clans that can board them, and the pirate clans with somewhere to sail. This ticket compiles those definitions and their links. MMR-40 places the map-scene entities they describe (port markers, blockade arcs, village drop-offs, pirate spawn points), and MMR-51 tests naval gameplay.

| | |
|---|---|
| Factory | `src/MNBL.ProceduralWorld/Campaign/NavalContentFactory.cs` (pure) with `worldgen/policies/naval-content.json`, written by `tools/make_naval_policy.py` from the installed catalog and the installed War Sails campaign map scene (`Modules/NavalDLC/SceneObj/Main_map`, hash-checked against `evidence/baseline/scene-contract.json`) |
| Runtime | `Campaign/GeneratedShipDistributionModel.cs` and `Campaign/GeneratedClanShipOwnershipModel.cs` (the fleet rule on War Sails' model seams); `GeneratedGameManager.InitializeGameStarter` registers them; the world package keeps the landlocked clans |
| Tests | `tests/Campaign/NavalContentTests.cs` via `tools/Test-NavalContent.ps1`, on the real size-129 world: 15 checks (`evidence/fwg/naval-tests.txt`, `evidence/fwg/naval.json`) |
| In game | the `generated_campaign` probe with `navalAudit=<repo>` (`Automation/NavalContentProbe.cs`), summarized by `tools/summarize_naval_content.py` (`evidence/runtime/naval-content.json`, 11/11) |

## The installed contract

What War Sails reads, and where (from the installed IL and XML):
- **Port.** Only `port_posX`/`port_posY` on a `Settlement` carry naval data (`Settlement.Deserialize` → `SetPortPosition`, which sets `HasPort`). The town's location complex always defines a `port` location; a town's `Location id="port"` only overrides its scene.
- **Shipyard.** `building_shipyard` (`NavalBuildingTypes.SettlementShipyard`). `NavalDLCBuildingModel` allows it only for `IsTown && HasPort`, and `BuildingsCampaignBehavior` adds it at game start if missing. Its level gates hulls (`ShipProductionCampaignBehavior.CanTownCreateShipFromHull`): light from level 1, medium from 2, heavy at 3. A port town without a shipyard would throw there.
- **Hulls.** The chain is:
  - `ShipHull` → `mission_ship` → `MissionShipObject`;
  - its nested prefab (`AssetPackages/nested_prefabs_packed.xml`), whose `MissionShip` script names the deck navmesh (`NavMeshPrefabName` → `NavMeshPrefabs/<name>.bin`);
  - `AvailableSlots` → `ShipSlot`, with upgrade pieces that target slots and name a `SiegeEngineType`.
  A culture offers `available_ship_hulls`; its `fishing_party_template` and `settlement_patrol_template_coastal` carry `ship_hulls` stacks.
- **Fleets at new game.**
  - `ShipProductionCampaignBehavior` builds ships at port towns: culture hulls up to the shipyard tier, weighted by `production_build_weight`, given by `ChangeShipOwnerAction.ApplyByProduction`.
  - `ShipTradeCampaignBehavior` then sells them to clan war parties (`ApplyByTrade`). For each clan it buys with a fixed chance (0.5 a day, and at the new game's follow-ups). The seller is one of its map faction's fiefs, or 20% of the time any non-hostile town. The buyer must be a party `ShipDistributionModel.CanPartyTakeShip` accepts.
  - It sells a clan's ships when the clan holds more than `ClanShipOwnershipModel.GetIdealShipNumberForClan`; the ideal number never limits buying.
  - Nothing on that path checks that the clan can reach the port.
  - Setting `Ship.Owner` directly would skip the owner-changed event, the naval visual refresh, the anchor and the gold: the sanctioned paths are the party-template stacks and `ChangeShipOwnerAction`.
- **Blockades.** `SiegeEvent` starts a blockade when the besieged settlement `HasPort` and a besieger owns ships. The visuals come from the settlement entity's `BlockadePositionScript` and its `Blockade_Arc_Start`/`Blockade_Arc_End` children. With no script there are simply no blockade ships. No coastline orientation exists outside those entities.
- **Landings.** `NavalMapSceneWrapper.InitializeDropOffLocations` sets a village's port from a `main_map_village_dropoff` entity under the village entity. XML is the baseline.
- **Pirates.**
  - `PiratesCampaignBehavior` finds pirate clans structurally: a bandit clan whose culture cannot have settlements and whose default template has ship hulls.
  - It spawns them in the patrol zones `INavalMapSceneWrapper.GetSpawnPoints(clan id)` reads from `PirateSpawnPoint` entities, at most one party per zone (`NavalDLCBanditDensityModel`).
  - The installed scene stacks an inner zone (radius 30) on 20 of its 28 positions (radius 50). `FindIdenticalZone` merges only equal position and radius, so both zones stay live.
  - With no spawn point a clan spawns nothing.
  - The ids `northern_pirates` and `southern_pirates` are compared by `NavalDLCVoiceOverModel` and looked up by the War Sails storyline's party factories (inactive in a Sandbox campaign).

## What the world supplies

`NavalContentFactory` builds from:
- the MMR-27 records (the compiler's port records and their MMR-35 slots);
- the MMR-28 travel graph, cells and ports;
- the MMR-31 factions;
- the MMR-33 settlements;
- the MMR-34 hideouts;
- `naval-content.json`.

**Agreement.** The factory refuses a policy that disagrees with the catalog it is given: the creation cultures' hull lists, every hull's verified prefab and deck navmesh, the cultures' naval roles, and the pirate templates.

**Water.** Water cells (sea, lake, river) joined by edges, columns wrapping. A component takes the MMR-28 basin id of the transitions on it, and that basin's open flag. A sea cell is open sea when every cell within 4 (Chebyshev) is sea, the MMR-28 rule.

**Port facilities** (one per port town):
- **Basin.** The port must be water of an open basin, else the world is refused.
- **Shipyard level.** The installed port towns' shares are level 3: 9, level 2: 1, level 1: 19 of 29. The 3 port towns without a shipyard in XML count at level 1, because `BuildingsCampaignBehavior` gives them one at its start level 1. Levels are assigned by the MMR-28 port's harbour quality, best first. `NavalContentFactory.ShipyardLevels` runs before the settlements are written (`SettlementFactory.Build` takes it), so the Settlements document carries the levels.
- **Hulls.** The town culture's available hulls up to its tier, each in the policy's verified chain: mission ship, nested prefab, deck navmesh and slots. Every upgrade piece's siege engine is a verified live default.
- **Orientation.** Seaward: the mean direction from the port to its basin's water cells within three cells (the coastline normal).
- **Blockade arc.** The two arc ends at the installed median offsets from the port, (1.73 along, 4.25 left) and (1.17, -4.40). These were measured in the installed scene's centre-to-port frame, its only orientation, and are applied in the seaward frame.
  - If an end is not water of the port's basin, the frame turns in 15-degree steps either way up to 90 degrees, then mirrors.
  - If no placement fits, the facility gets a named blockade diagnostic.
  - The installed script parameters are medians: 10 ships, 4 arcs, 1.555 apart, angle 1.931, `dromon_ship_nested` at scale 0.045.
  - The port marker sits at the port. MMR-40 bakes the port marker, the blockade arc and the drop-off into a world's scene (`docs/settlement-entities.md`).

At size 129: 30 port towns, with shipyards at level 3 (9), level 2 (1) and level 1 (20). Level-1 yards build about 2 light hulls of their culture; level-3 yards 5-6. All 30 blockade arcs have both ends on water of their basin: 19 unturned, 11 turned or mirrored.

**Landings.** Each harbour village gets a drop-off intent at its MMR-33 port (its MMR-28 harbour cell). That port must be water of an open basin, within the installed drop-off distance from the village centre (13.98, measured over 72 villages) plus one cell. Otherwise the world is refused (`quality.naval_port`), because the village's XML port would still give it `HasPort`. At size 129, all 80 harbour villages land.

**Naval roles:**
- a kingdom is **coastal** when one of its towns is a port town on an open basin; otherwise it is **landlocked**, with a reason (no port town, or only closed-basin ports);
- a clan **may hold ships** when its home's MMR-28 land group holds such a port town. Its parties can reach an anchor there.

At size 129: 28 coastal and 12 landlocked kingdoms. All 92 clans may hold ships: every clan's home shares a land group with a port, including the clans of landlocked kingdoms.

**The fleet rule (runtime).** It sits on two War Sails model seams:
- `GeneratedShipDistributionModel` (over `NavalDLCShipDistributionModel`) refuses ships, and fleet transfers, to the parties of a landlocked clan (`CanPartyTakeShip`, `CanSendShipToParty`). That stops the installed trade buying for them.
- `GeneratedClanShipOwnershipModel` (over `NavalDLCClanShipOwnershipModel`) gives a landlocked clan an ideal fleet of 0, so any ship it still holds, for example from loot, is sold.
- Both delegate everything else to the installed models unchanged.
- `GeneratedGameManager.InitializeGameStarter` adds both after every submodule's models, War Sails' included. `CampaignGameStarter.AddModel` makes each installed model the new one's `BaseModel`. This happens before the submodules' `OnGameStart`, where War Sails builds its model set and takes the last model of each type.
- Initial fleets come from the installed path, so ownership, party lists, events and visuals stay the engine's own.
- The landlocked clans are saved with the world package (`adapter.dataset.navalLandlocked`), so a load registers the same rule.

This is a supported seam, not a patch. It needs the `NavalDLC` compile reference, a declared module dependency already pinned in `config/runtime-api-lock.json`; this ticket marked that pin as a compile reference, with its bytes unchanged.

**Pirates.**
- **Clans.** The two installed pirate clans are re-emitted with their installed records and ids as world aliases (`CampaignIdentity.Aliases`). Each is homed at the generated hideout nearest its first spawn position.
- **Spawn positions.**
  - Density: 0.966 per port town (the installed 28 positions for 29 port towns), shared over the open basins in proportion to their port towns.
  - Each is on an open-sea cell, at least 0.131 × the town spacing from every port (the installed 10th percentile), and one outer radius from the others.
  - Each gets an outer zone of 0.748 × spacing (the installed 50) and, drawn with the installed share 0.714, an inner zone of 0.449 × spacing (the installed 30).
- **Clan.** Each position's clan is drawn from the installed weights of its nearest port town's culture. For example, the installed nord ports have 6 northern positions, and the aserai ports 5 southern.

At size 129: 29 positions with 53 zones (24 inner), northern 34 zones and southern 19.

**Diagnostics:**
- Every town the MMR-28 port compiler could not give a port is named with its reasons: at size 129, 14 with `port_too_far_from_gate` (one also `harbour_village_excluded`), and 1 coastal town without a harbour record. These are landlocked towns: no port, no shipyard.
- Every land group other than the main one that holds a settlement lists its naval transitions. A group with a town and no transition refuses the world. At size 129, all 10 island groups with towns have at least one port or coast transition to an open basin. The 2 village-only groups each have a coast transition.

**Refusals.** A world whose naval content cannot be built is refused with `NavalException`. Its quality codes come from `naval-content.json`:

| Code | Retryable | Meaning |
|---|---|---|
| `quality.naval_unreachable_island` | yes | A land group with towns has no port or coast transition to an open basin |
| `quality.naval_port` | yes | A port town's or harbour village's port is not water of an open basin, or a village port is beyond the installed drop-off envelope |
| `quality.naval_catalog` | no | An installed hull, slot, mission ship, template or pirate clan is missing |
| `quality.naval_policy` | no | The naval policy is malformed, or disagrees with the catalog it is given (hull lists, culture naval roles, pirate templates) |

**Slots.** All 60 MMR-35 slots of the compiler's port records (`navalFacility` and `shipyardAndFleet` of each port) are resolved.

## Tests (offline, size 129): 15/15

`tools/Test-NavalContent.ps1` runs the accepted MMR-31 to MMR-34 factories, then `NavalContentFactory`, and rechecks every rule:
- hull chains, naval roles and templates;
- shipyard shares by quality;
- facilities and their buildable hulls;
- blockade geometry;
- landings;
- naval roles;
- islands and impossible ports;
- pirate spawn points and clan records;
- the MMR-30 registry plan of the whole world with its startup and naval records;
- the 60 slots;
- determinism;
- four refusals with their codes.

## In game: runs 21180 and 10724 (11/11)

Run 21180: the `generated_campaign` probe with `navalAudit=<repo>`, `settlementFixtures=<repo>`, `startupAudit=<repo>` and `saveProbe=1`, two worlds and a 2-day gameplay window. Run 10724: `generated_save_load` loads both saves cold. Summary: `evidence/runtime/naval-content.json` (`tools/summarize_naval_content.py RUN OUT LOAD`).

The first world carries the naval fixtures:
- the two pirate clans as world aliases homed at the fixture hideout, built with the same record builder;
- the fleet rule with **clan 2 declared landlocked**. Both fixture towns are port towns, so by the factory's rule no fixture clan would be landlocked; the declaration lets the audit watch the rule act on a real clan through the new game's ship production and trade.

What it showed:
- **Hulls:** all 38 creation-culture hulls are registered `ShipHull`s of the policy's type. Their `MissionShipObject` records name the policy's 21 prefabs, whose nested prefabs and deck navmeshes `make_naval_policy.py` verified in the installed files. All 13 upgrade-piece siege engines are registered.
  - `GameEntity.PrefabExists` reports none of the ship prefabs loaded on the campaign map (run 6636), so it is recorded, not required.
- **Tiers:** the engine's `CanTownCreateShipFromHull` matches the policy for every empire hull at both fixture shipyards. Level 1 builds 3 of 7; level 3 builds all 7.
- **Fleets:**
  - after the new game, the installed production and trade hold 18 ships at settlements and 18 in parties: clan 1 has 4, and the landlocked clan 2 has none;
  - after the 2-day window, 20 and 20, and clan 2 still has none;
  - every ship is where its owner says.
- **The fleet rule:**
  - War Sails reads both generated models, chained over `NavalDLCClanShipOwnershipModel` and `NavalDLCShipDistributionModel`;
  - ideal fleets: clan 1 6 (installed 6), clan 2 0 (installed 6), the player 6;
  - ruler 2's party (landlocked) is refused ships and fleet transfers, where the installed model would allow both;
  - the other parties get the installed answers.
- **The sanctioned path:**
  - a ship the installed `ShipProductionCampaignBehavior.CreateShip` built at the level-3 town (an `eastern_heavy_ship`) and ruler 1's party bought (`ApplyByTrade`) is owned and listed by that party alone (4 → 5 ships);
  - the party gains naval navigation and a valid anchor (the trade anchors a buyer that already had ships), and its naval visual is marked for refresh;
  - a second produced ship moved to the player's party (`ApplyByTransferring`) had its naval visual refreshed by the visual layer by the end of the window. `NavalMobilePartyVisual.Tick` refreshes only parties the player can see.
- **Pirates:** both pirate clans are pirate clans to `PiratesCampaignBehavior`, homed at a hideout, with ship templates. They have 0 patrol zones, because the generated scene has no `PirateSpawnPoint` entities yet (not placed by MMR-40, which placed the settlement entities; handed to MMR-53).
- **Load:** world 0 loads with both models effective, the same landlocked clan (clan 2) holding no ship, and its 2 pirate clans. World 1 loads without the models.
  - MMR-19's exact snapshot comparison flags only 8 path-state values of moving AI parties (checked one by one), left to MMR-62 and forwarded by it to MMR-49 (the load).
- The MMR-34 startup audit on the same runs: 36/36.

Earlier runs:
- 7108:
  - War Sails had built its model set before the model was added in `OnGameStart`; models now go in through `InitializeGameStarter`;
  - the mission-ship type is `TaleWorlds.Core.MissionShipObject`;
  - `PartyTemplateObject.ShipHulls` is a field.
- 37592 + 23236: 11/11 before the review. The review then found the fleet rule ineffective (the ideal fleet only governs selling) and the evidence vacuous, because no clan was landlocked.
- 6636: `GameEntity.PrefabExists` is false for every ship prefab on the campaign map.
- 42236: MMR-33's settlement-fixture check failed with grain at 3.3× its base value, against a limit of 3. Its other runs gave 1.2 to 1.9× with the same market code, so this is new-game market variance (comment on MMR-51).
- 39556: ruler 1's naval visual stayed dirty, because the party was never on screen.
- 37080: the player's purchase drained the gold the gameplay window recruits and trades with.

## What this does not prove

- **Scene entities:** MMR-40 bakes port markers, blockade arcs (every ship on water) and village drop-offs into a world's scene (`docs/settlement-entities.md`). Pirate spawn points are still intents (handed to MMR-53), so no pirate spawns (no patrol zone).
- **Crew, captain and reinforcement spawn points, and deck geometry in a mission:** the chain is verified down to each hull's nested prefab and deck navmesh file. The spawn points (`sp_troop_captain`, `sp_troop_crew_spawn` and the deck spawns) live in the prefab's nested links and are resolved only when a mission builds the ship. MMR-42 (mission scenes) and MMR-51 (naval play) own that. Crew troops come from the party roster, not from the hull.
- **Ships a landlocked clan gains other than by trade** (loot after a battle) are sold by the installed trade logic over time, not refused.
- **Engine snapping:** a world's ports are MMR-38's navigation port points, and MMR-40's port entities stand on them.
- **Naval gameplay:** blockades, raids, naval battles and pirate encounters over many days are MMR-51's.
- **The full world in game:** MMR-45 registers the full world (facilities and landings in its records) and gives its fleet rule and pirate spawn points to the naval scene wrapper. The facilities, landings and spawn points are verified offline. In play they are MMR-53's.
