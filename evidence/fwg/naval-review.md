# MMR-35 independent review (2026-10-15)

The reviewer was an independent read-only agent: no repository edits, no game launch. It re-ran `tools/Test-NavalContent.ps1` (15/15, intents `ff814ecef2bc`) and checked the claims against the installed IL (`%TEMP%\mmr32-il`, `%TEMP%\mmr35-il\mb.il`, `naval-survey\navalview.il`), the hash-checked catalog and the installed War Sails map scene.

The report is below verbatim, followed by the disposition of every finding.

---

## MMR-35 independent review

I re-ran the offline harness and got the same result: 15/15, intents `ff814ecef2bc` (receipt `build/research-naval35/test-d06ae3cc…`). I changed no repository files. My scratch scripts are in `%TEMP%\claude\mmr35-review\`.

### BLOCKER

**B1. The fleet rule does not stop a landlocked clan getting ships.**
- **Where:** `Campaign/GeneratedClanShipOwnershipModel.cs:8-12, 26-30`; `docs/naval-content.md:20, 56-59`; the `fleets` rule text in `tools/make_naval_policy.py:285-291`; `tools/make_compatibility_inventory.py:94-96`.
- **Evidence (naval.il):**
  - `ShipTradeCampaignBehavior::DailyTickClan` reads `GetIdealShipNumberForClan` once, only as `if GetTotalNumberOfWarShipsInClan > ideal → ConsiderSellingShips`. Selling then happens with `RandomFloat < 0.1`.
  - Buying is `ConsiderPurchasingShip`: `RandomFloat < GetClanShipPurchaseChance` (a constant 0.5), then `GetPartyToGiveShipTo`, `GetTownToBuyShipFrom` and `TryPurchasingShipFromTown`. That path checks `ShipDistributionModel.CanPartyTakeShip` and gold, never the ideal number.
  - Across all the IL dumps, `DailyTickClan` is the only caller of `GetIdealShipNumberForClan` outside the model classes.
- **Effect:** returning 0 does not prevent buying. A landlocked clan still buys at the new-game follow-ups and on any day, then sells on about 10% of days, so it churns ships.
- **The docs and inventory are wrong too:** doc line 20 ("sells them … up to GetIdealShipNumberForClan") and the inventory's "its ideal fleet … drives ShipTrade… which buys" misstate the installed contract.
- **The evidence cannot catch this:**
  - The fixture plan is hard-coded empty (`GeneratedCampaignDataset.cs:137`), and size 129 also has 0 landlocked clans.
  - `NavalContentProbe.FleetRule` only checks the model's return value.
  - The "none with a landlocked clan" checks therefore pass vacuously.
- **Fix:**
  - Add a generated `NavalDLC.ComponentInterfaces.ShipDistributionModel` over `NavalDLCShipDistributionModel` whose `CanPartyTakeShip` returns false for parties whose `ActualClan` is landlocked. Keep the ownership model so existing ships get sold.
  - Make one fixture clan landlocked at new game and assert it holds 0 ships after the follow-ups and after the gameplay window.
  - Correct the doc, policy and inventory text.

### MAJOR

**M1. Most of the hull chain the ticket asks for is not validated.**
- **Where:** `make_naval_policy.py:109-133`; `NavalContentFactory.cs:188-197`; `NavalContentProbe.cs:49-60`.
- **What is checked:** only that the mission-ship id exists in MissionShips.xml, that slot ids exist, and that upgrade-piece TargetSlots exist.
- **What is not checked:** mission prefab (e.g. `liburna_ship_nested`), deck navigation (`NavalDLC/NavMeshPrefabs/*_navmesh.bin`), crew/captain/reinforcement spawns, equipment, and crew troop roles. The policy copies crew capacities only.
- **Overstatement:** the docstring at `make_naval_policy.py:7-8` says "upgrade pieces and their siege engines" are checked; siege engines are not checked.
- **Docs:** `docs/naval-content.md` neither claims these checks nor lists them as not proven.
- **Consequence:** c1's "crew … references" is not met.
- **Fix:** probe `GameEntity.PrefabExists(prefab)` and the navmesh for each policy hull, and resolve the spawn tags and crew roles. Otherwise defer them explicitly to W03/P06 in the doc and the ticket.

**M2. Blockade arcs are not tied to the generated coastline.**
- **Where:** `NavalContentFactory.cs:177-183`; `NavalContentTests.cs:143-146`.
- **What happens:**
  - The arc frame is the centre-to-port direction.
  - The stored `Orientation` (gate to port) is never used for the arc.
  - 14 of 30 arcs have an end off water. There is no diagnostic, and the test only reports the count.
- **Fix:** take the along-axis from the local water (e.g. the mean vector from the port to open-basin cells within k cells). Require both ends to be water of the port's basin; rotate or mirror if not, and emit a named diagnostic if that fails.

### MINOR

- **m1. The pirate spawn calibration misreads the installed scene** (`make_naval_policy.py:204-209, 278-284`; doc line 66).
  - The 48 `PirateSpawnPoint` entities sit at only 28 distinct positions. 20 positions stack a same-clan pair with radii 30 and 50.
  - `FindIdenticalZone` merges zones only when both position and radius match, so both zones of a pair stay live.
  - The generated world places 50 distinct points at least one radius apart, all at radius 50. That is about 1.8× the installed distinct patrol locations per port town, and the 30-radius inner zones are dropped. The p10 port distance also double-counts stacked points.
  - Fix: cluster by position first.
- **m2. The shipyard shares exclude 3 towns that do get a shipyard.**
  - `BuildingsCampaignBehavior.BuildDevelopmentsAtGameStart` adds missing allowed buildings at `StartLevel`. `NavalBuildingTypes.InitializeAll` passes variance 0 and startLevel 1.
  - So the 3 "no shipyard" installed port towns do get level 1 and build ships. The real installed shares are 9/1/19 of 29.
  - The claim "could produce no ships" (`make_naval_policy.py:237-243`, doc line 41) is false, and size 129 gets 10 level-3 yards instead of 9.
- **m3. The sanctioned-path probe skips part of the sanctioned path** (`NavalContentProbe.cs:93-104`).
  - It uses `new Ship(hull)` plus `ApplyByProduction` directly, skipping `CreateShip`'s upgrade-piece equipping and its `OnShipCreated` dispatch.
  - The probe ship stays in ruler_1's party, so the fleet census taken afterwards counts it (clan_1 shows 5).
  - `anchorValid` is recorded but asserted neither in the probe nor in the summary.
  - Rendering is not checked, although the ticket requires that "ownership, party collection and rendering agree" (`SetNavalVisualAsDirty` is the visual hook).
- **m4. "Policy disagrees with the catalog" is never checked by the factory.** The `quality.naval_policy` meaning says it is, but `NavalContentFactory` never compares `policy.provenance` hashes with the catalogs it is given. Only the harness checks, and it throws a plain `InvalidOperationException`.
- **m5. A rejected landing still leaves the village a port.** `SettlementFactory.cs:307` still writes `port_posX`, so `HasPort` stays true although the doc says "the village gets no landing". This is latent (0 cases at size 129). Feed rejected villages back, the way `ShipyardLevels` is fed, or refuse the world.
- **m6. The c2 island check covers only land groups with towns** (`NavalContentFactory.cs:260`). Groups 6 and 10 each hold a village and no town, and are not diagnosed. Cover any group with a settlement.
- **m7. The load evidence filter is too coarse.**
  - `summarize_naval_content.py` drops any "values differ from the saved snapshot" failure.
  - The raw failures show only the first differing path ("4 values differ … first: patrol nextTarget"; "2 values differ … caravan").
  - So doc line 124's "only path state of moving AI parties" is unverified for 3 of 4 diffs in world 0 and 1 of 2 in world 1.
  - The load check also never compares the restored landlocked set with the saved one.
- **m8. The doc overstates who compares the pirate ids.** Doc line 25 and `CampaignIdentity.cs:22` say only `NavalDLCVoiceOverModel` compares them. In fact:
  - `northern_pirates` also appears in closures inside `CreateHoundsParty`, `CreateSeaHoundParty`, `SpawnBanditParty` and `SpawnPirates`;
  - `southern_pirates` also appears in closures inside `SpawnMainCorsairParty` (storyline code).
  - The decision to keep the ids still stands.
- **m9. "Rejected before campaign commit" is not wired yet.** Nothing calls the factory; this waits for MMR-45, which the doc admits. MMR-34 set the same precedent.

### NIT

- Doc line 20: purchases come from the clan's `MapFaction.Fiefs`, else 20% of the time from any matching town, not "its own port towns".
- `Test-NavalContent.ps1:32`: the receipt schema and scope text are copied from the startup harness.
- `landings.installed` is 144, but that is 2 tagged entities for each of 72 villages.
- The policy's arc-end walk ignores parent scale (0.9 or 1.108 on 4 town chains); the effect on the medians is negligible.
- The closed-basin "landlocked" reason (`NavalContentFactory.cs:226-227`, doc line 51) can never fire: a closed-basin port refuses the world at line 165, and MMR-28 already rejects `closed_basin` candidates.

### Checked and found correct

- **Model timing:**
  - `Campaign.OnInitialize` calls `GameManagerBase.InitializeGameStarter`, which loops over every submodule (NavalDLC's `AddModels` included), before `OnGameStart`.
  - `CampaignGameStarter.AddModel<T>` calls `Initialize(GetModel<T>())`, picking the last model, and `GameModelsManager.GetGameModel` also takes the last.
  - `NavalDLCManager.OnGameStart` builds `NavalDLC.GameModels` from `starter.Models`.
  - Load goes through the same path, with the plan taken from the package. Runs 37592 and 23236 show the model is effective.
- **Tiers:** `CanTownCreateShipFromHull` follows `ShipType` (Light=0 needs level > 0, Medium=1 needs > 1, Heavy=2 needs == 3). `GetShipyard` returning null would throw. `NavalDLCBuildingModel` requires `IsTown && HasPort`.
- **Owner setter:** `ChangeShipOwnerAction.ApplyInternal` handles gold, anchor, `SetNavalVisualAsDirty` and `OnShipOwnerChanged`; `Ship.set_Owner` only moves the ship between lists.
- **Pirate behaviour:** `IsPirateClan` is structural. `GetMaxSupportedNumberOfLootersForClan` equals `GetSpawnPoints(id).Count`, so a clan with no spawn point spawns nothing.
- **Scene and XML hooks:**
  - `Settlement.Deserialize` reads `port_posX` and calls `SetPortPosition`, which sets `HasPort`.
  - `town_complex` defines a `port` location.
  - `InitializeDropOffLocations` reads `main_map_village_dropoff`.
  - `BlockadePositionScript` (SandBox.View) reads `Blockade_Arc_Start`/`Blockade_Arc_End`.
- **Policy numbers:** they reproduce from the hash-checked scene and catalog: 29 blockade scripts and their medians, 22/26 spawn points, drop-off maximum 13.975, shipyard levels 9/1/16/3.
- **Offline tests:** the refusal tests are falsifiable, and output is deterministic across rebuilds and reordered records.
- **Pirate records:** the alias records carry every installed attribute except `initial_home_settlement`. The `corsair_band` non-alias refusal test passes.
- **API lock:** only `compile` was flipped, the sha256 is unchanged, `build-api-inputs.json` matches, and `Modules/MNBL.ProceduralWorld/SubModule.xml` declares the NavalDLC dependency.
- **Water and basins:** consistent with MMR-28 (sea, lake and river are Naval-valid; closed basins are rejected upstream). No DLC files are copied.

### Verdicts

- **c1: Not met.**
  - Resolved: hulls, slots, mission-ship ids, culture role templates and characters, pirate clans and templates, port locations.
  - Not resolved: crew, prefab and deck references (M1).
  - The landlocked/coastal roles are distinguished as data, but "landlocked" has no working runtime effect (B1).
- **c2: Met** for town islands (10 of 10 have a transition) and for impossible ports (15 named diagnostics). There is a minor gap for land groups with no town (m6).
- **c3: Not met.** The fleet rule depends on a misread War Sails contract (B1). The tiers, port/HasPort, pirate rules and model seam timing do agree with the installed IL.

---

## Disposition

Every finding was fixed in one batch, except m9, which is by design (see below). Confirmed by: offline `Test-NavalContent` 15/15 (`evidence/fwg/naval-tests.txt`), with `Test-SpawnPolicy` 18/18 and `Test-CampaignRegistry` 57/57 rerun; in game, runs 21180 (audit and saves) and 10724 (cold load): naval 11/11 (`evidence/runtime/naval-content.json`), and the MMR-34 startup audit 36/36 on the same runs.

| Finding | Fix | Evidence |
|---|---|---|
| **B1** the fleet rule does not stop buying | New `GeneratedShipDistributionModel` over `NavalDLCShipDistributionModel`: `CanPartyTakeShip` and `CanSendShipToParty` refuse a landlocked clan's parties, and everything else delegates. The ownership model stays and makes such a clan sell what it holds. Both are registered in `InitializeGameStarter`. The fixture declares clan 2 landlocked (documented as a fixture declaration, since its town is a port), so the rule acts on a real clan. The doc, policy and inventory now state the installed contract (fixed purchase chance, distribution model decides buying, ideal fleet decides selling); `NavalDLCShipDistributionModel` is inventoried as adapted | In game: clan 2 holds 0 ships after the new game and after the 2-day window, while clan 1 holds 4-5. Its ideal fleet is 0 (installed 6). Ruler 2's party is refused ships and transfers where the installed model allows both; other parties get the installed answers. The landlocked set survives the save and load |
| **M1** hull chain mostly unvalidated | `make_naval_policy.py` now verifies, per hull: the mission ship, its nested prefab folder in `AssetPackages/nested_prefabs_packed.xml` (hashed in provenance), and the deck navmesh its `MissionShip` script names (`NavMeshPrefabs/<name>.bin`). It also checks every upgrade piece's siege engine against the verified live defaults. The factory refuses a hull without a verified chain. In game: mission-ship records name the policy's prefabs, and all 13 siege engines are registered. `GameEntity.PrefabExists` is false for all ship prefabs on the campaign map (run 6636), so it is recorded only. Crew, captain and reinforcement spawn points live in the prefab's nested links and are resolved only when a mission builds the ship; they are deferred explicitly to MMR-42/MMR-51 (doc and comment). Crew troops come from the party roster, not the hull | Offline check 1 (22 hulls, 13 siege engines); in-game hulls check |
| **M2** blockade arcs not tied to the coastline | The orientation is seaward: the mean direction from the port to its basin's water within three cells. The installed arc offsets are applied in that frame. If an end is not water of the port's basin, the frame turns in 15-degree steps up to 90 either way, then mirrors; otherwise the facility gets a named blockade diagnostic | Offline check 5: all 30 arcs have both ends on water of their basin (19 unturned, 11 turned or mirrored), with the installed geometry kept (ends 4.59 and 4.56 from the port, 143 degrees apart) |
| m1 pirate calibration | Spawn points are clustered at 1 unit: 28 installed positions, 20 with a stacked inner zone. Policy: 0.966 positions per port town, outer radius 0.748 and inner radius 0.449 of the spacing, inner share 0.714, minimum port distance 0.131 (p10 over positions). The factory emits an inner zone per position with that share | Offline: 29 positions, 53 zones (24 inner) |
| m2 shipyard shares | The 3 installed port towns without a shipyard in XML count at level 1 (they get one at start level 1). Shares are now 9/1/19 of 29, and size 129 gets 9/1/20. The false "could produce no ships" claim is removed | Offline check 3 |
| m3 sanctioned path incomplete | The audit now produces with the installed `ShipProductionCampaignBehavior.CreateShip` (hull choice, upgrade pieces, `OnShipCreated`, `ApplyByProduction`), then trades with `ApplyByTrade`. It runs after the fleet census. It asserts the anchor (the engine anchors a buyer that already had ships) and the naval-visual dirty flag. Rendering: a second produced ship moves to the player's party (`ApplyByTransferring`, no gold), and the audit asserts its visual was refreshed by the end of the window. `NavalMobilePartyVisual.Tick` refreshes only parties the player can see; run 39556 showed an off-screen lord stays dirty | In-game sanctioned-path check |
| m4 policy-catalog agreement unchecked | The factory refuses a policy whose culture hull lists, verified hull chains, culture naval roles or pirate templates disagree with the catalog it is given. The code's meaning text matches | Offline refusal "a policy whose hull list disagrees with the catalog" → `quality.naval_policy` |
| m5 rejected landing keeps a port | A harbour village whose port is not water of an open basin, or lies beyond the drop-off envelope, now refuses the world (`quality.naval_port`) | Offline refusal "a harbour village whose harbour cell is land" |
| m6 islands only with towns | Every land group other than the main one that holds any settlement is diagnosed, including village-only groups 6 and 10, which each have a coast transition. A group with a town and no transition refuses the world | Offline check 8: 12 groups diagnosed |
| m7 load evidence too coarse | The summary checks every snapshot mismatch (the load rows list them all), not just the first. The save oracle records the landlocked clans, and the load compares them and counts their ships | In-game load check: 8 path-state values only; landlocked set equal; 0 ships |
| m8 pirate id users | The doc and `CampaignIdentity` now name the storyline factories as well (inactive in Sandbox) | - |
| m9 not wired before campaign commit | By design for this ticket, as for MMR-34: the factory's refusals are the rejection mechanism, and MMR-45 wires it into generation (comment on MMR-45) | - |
| NITs | The doc gives the real purchase sources. The harness receipt has its own schema and scope. Landings report 72 installed villages (144 entities). The arc walk applies parent scale. The unreachable closed-basin reason is removed | - |

Runs during the fix batch, recorded in the doc:
- 6636: `PrefabExists` is false on the map;
- 42236: MMR-33's grain price check at 3.3×, new-game market variance, comment on MMR-51;
- 39556: an off-screen visual stays dirty;
- 37080: the player's purchase drained the gold the gameplay window needs.
