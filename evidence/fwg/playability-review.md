# MMR-41 independent review (per-world navigation caches, runtime playability)

**The review.** One independent review of the implementation and of the first in-game runs:
- world_navigation 33340;
- generation_job 18640;
- generated_campaign 24608 (cold), 32996 and 11304 (reuse), 27968 (corrupted copy).

It was done by an agent that:
- read the code;
- read the installed 1.4.8 / War Sails 1.2.8 code through Mono.Cecil IL dumps (`NavigationCache<T>` Serialize, Deserialize and `GetSettlementToSettlementDistanceWithLandRatio`, `SandBoxNavigationCache`, `SettlementPositionScript`, `NavalMapSceneWrapper.InitializeDropOffLocations`, `Settlement.SetPortPosition`);
- ran the offline harnesses (`tools/Test-Playability.ps1` 23/23, `tools/Test-WorldNavigation.ps1` 36/36: the `EvaluateRoute` refactor preserved MMR-38's matrix);
- neither edited the repository nor started the game.

**Verdict before the fixes.** c1 not met; c2 met with fixes; c3 met with fixes.

**Engine facts it confirmed in IL, and the fixes used:**
- **Drop-off ports.** War Sails sets harbour-village ports after `AfterLoad`, when `NavalMapSceneWrapper` starts. `InitializeDropOffLocations` takes each `main_map_village_dropoff` entity, finds the village its parent names in `Village.All`, and calls `SetPortPosition` with the entity's global position at sea. `SetPortPosition` also sets `HasPort`.
- **Cache misses.** The installed `GetSettlementToSettlementDistanceWithLandRatio` computes a missing pair one way and stores it. A cached pair is the mean of both directions (`AddClosestEntrancePairBase`).

All 14 findings were fixed in one batch. The confirming runs are listed in `evidence/world/playability-report.json` (summary). The offline tests passed 24/24, and MMR-38's harness 36/36.

| # | Severity | Finding | Resolution |
|---|---|---|---|
| 1 | blocker | The caches and their key are built in `AfterLoad`, before War Sails sets harbour villages' ports: the drop-off ports were missing from both. | `RegisterNavigationCaches` sets the same ports first: each drop-off entity's village (the settlement its parent names, because `Village.All` is still empty at `AfterLoad`, runs 17060 and 17064; else the factory's child name), at the entity's global position at sea. In game the key holds `village_1\|village+port`, the Naval cache 3 pairs (2 before), and the key rebuilt at campaign start, after War Sails' wrapper, equals the acquired key in every world. |
| 2 | major | The campaign cache check queried `MapDistanceModel.GetDistance`, which computes and stores a missing pair, so it could not tell a hit from a live computation and changed the cache; the symmetry check was always true. | `CampaignPlayability` snapshots the registered caches before any query (their installed serialization, parsed) and requires each route's entrance pair in the type's cache; no model query, no symmetry check. The doc says cache distances are two-way means and direction is tested by the path queries. |
| 3 | major | `RegisterNavigationCaches` kept the fixture-ID contract, so no compiled world could reach the cache code; the periodic path never ran under MMR-41; real-scale cost unmeasured. | The fixture contract applies only to fixture slots; a compiled world's scene (MMR-45) goes straight to the cache. The periodic fixture (131,072 faces) ran cold and reused under the new code. A compiled world's campaign does not exist before MMR-45, so its cost stays unmeasured (a stated limit and handoff). |
| 4 | major | The key missed inputs the content depends on: the periodic coordinate identity, the distance model version, this module's code (the periodic path queries and closest-settlement order). | The key holds `PeriodicWorldCoordinates.Identity` with the policy version, `PeriodicDistanceModel.Version`, the module's SHA-256 and the settlement registration order (ties in the face cache). |
| 5 | major | The campaign route set treated harbour villages as port towns and skipped the open-basin rule. | Only towns have ports in the route set. Clan capability comes from the game's fleet rule (`GeneratedClanShipOwnershipModel`'s landlocked clans), else a port town in the home's land group, as recorded. |
| 6 | major | The campaign start routes repeated the same 8 routes for 7 cultures and tested zero-length start-point routes. | Starts are grouped by town (one route set per start town); a starting point equal to the gate adds no route; a zero path cost between distinct points fails. The fixture's single land group and basin is a stated limit. |
| 7 | major | The start-candidate rule approximated MMR-34, and an empty set was activatable. | The doc states why the candidates hold every start MMR-34 can pick (it picks only among towns that are port towns on an open basin with another port town there, fallbacks included, then also requires food, notables and a shipyard; its sea destination is the nearest such port). No candidate is a refused route. |
| 8 | major | `Require` checked only the file, the scene and `activatable`; a stale report survived a later failed run. | The record embeds the report text and its hash, as `NavigationGate` does. `Require` checks: schema, validator version, scene, world, no engine faults, activatable, and the report bound to the scene's persisted navigation report hash and settlement layout hash. Validation deletes the scene's previous report first. The job and world_navigation call it. Offline: 6 refusals and a pass. |
| 9 | minor | Capital-fief used the ruling clan's capability both ways. | Capability is per clan: the ruling clan going, the fief's owner coming back (tested with a landlocked owner across water). |
| 10 | minor | Faction access took the geometrically nearest foreign fief, refusing a shipless kingdom that could reach another by land. | The nearest foreign fief both directions can make under their clans' capabilities; none is a refusal (tested). |
| 11 | minor | A throw while registering read caches escaped `AfterLoad`. | Caught: the dictionary is cleared and the caches generated. |
| 12 | minor | W05/U03 were hard-coded; the report hid other pending items. | Flags come from the graph policy's `pendingRuntime`; an entry without a resolver fails (tested). The report lists the content slots (`validation.json` `pendingSlots`) and the product policy's `pendingRuntimePolicies` as not flags. |
| 13 | minor | Evidence not sealed (status-error reuse run, missing files, the job's counts not recorded). | The summary takes only status-ok runs. The job probe records the route and flag counts and the gate result. The evidence files and this record exist. |
| 14 | nit | Settlement order and ties; check-then-read race; the sweep removing caches of retained scenes; Euclidean nearest on periodic worlds. | The order is keyed. The race is benign under one key. The sweep removes only the caches of scenes it deleted. The Euclidean nearest choice is stated as a limit. |

**Found by the confirming runs.**
- **Village matching.** Run 17060's campaign-start key check failed because the drop-off was not applied: `Village.All` is empty at `AfterLoad` (run 17064 recorded 0 villages). The village is now found by `Settlement.Find`, which run 2516 confirmed.
- **Periodic starts.** Periodic run 9908 refused the six other creation cultures' installed Calradia start points (12 routes, no engine path; the generated start passed). Fixture-c's placement has no navigation-test points, so it cannot take the settlement and startup fixtures that replace them (run 31352).
  - The probe now takes `playabilityStartCultures`, recorded in the report as `startCultures` and `excludedStarts`. The periodic runs 6152 and 35828 route the selected culture's start only.
  - Run 9908 is kept as evidence that surviving installed start points are refused (summary check `installedStartsRefused`).
