# MMR-40 independent review (settlement entities, gates, ports and blockade geometry)

**The review.** One independent review of the implementation and of the first in-game pair (world_surface run 32940, generated_campaign run 21784). It was done by an agent that:
- read the code;
- read the installed 1.4.8 / War Sails 1.2.8 campaign map code through Mono.Cecil IL dumps (`SettlementVisual`, `BlockadePositionScript`, `CampaignMapSiegePrefabEntityCache`, `MapScreen`, `NavalMapSceneWrapper`);
- ran the offline tests;
- neither edited the repository nor started the game.

**Verdict before the fixes.** c1 met with fixes; c2 and c3 not met.

**Engine facts it confirmed in IL, and the fixes added:**
- **Siege geometry.** `SettlementVisual.OnStartup` fills the wall section and wall engine arrays from the children. `UpdateDefenderSiegeEntitiesCache` keeps the current wall level's (`UpgradeLevelMask` Level1 2, Level2 4, Level3 8). Siege bombardment indexes the wall section cache with no bounds check.
- **Map screen.** `MapScreen.OnInitialize` dereferences the scene's `CampaignMapSiegePrefabEntityCache` with no null guard. The cache's `OnInit` needs the campaign's map scene.
- **Visibility.** `SetSettlementLevelVisibility` shows a child whose level mask holds the visual's whole current mask. `RefreshLevelMask` asks the scene for `civilian`, `siege` and `looted`.
- **Blockade arc.** `GetBlockadeArc` builds its arc from `FindCenterOfCircle(end, start)`. `Vec3.RotateAboutZ` turns counterclockwise, and `MathF.Round` rounds half to even.

All 17 findings were fixed in one batch. The confirming runs are world_surface 29316 (A, B, warm A), generation_job 34656 (the runtime stage ran the check; MMR-44's 17 checks pass) and generated_campaign 17864 (summary `evidence/world/settlement-entities.json`, 24/24 checks). The offline tests passed 52/52. Evidence is in `evidence/world/`.

| # | Severity | Finding | Resolution |
|---|---|---|---|
| 1 | blocker | No `map_breachable_wall` children: siege bombardment indexes an empty wall cache and crashes. Defensive engines need per-level copies with level masks. | Policy v2 measures two wall sections and four wall engines per wall level with their scene levels. The factory bakes each wall with a raycast-only collider, a `map_solid_wall` and a `map_broken_wall` child; the scene declares the campaign map's levels (`DeclareSceneLevels`). In game every fortification caches 2 sections and 4 engines for its current level out of 6 and 12. |
| 2 | major | Production world scenes lack `CampaignMapSiegePrefabEntityCache`, so `MapScreen.OnInitialize` throws; the doc said otherwise. | `GeneratedMapScene.Load` adds the cache to a campaign map without one (installed variables by reflection, then `CallScriptCallbacks`). No scene bakes it; the fixture slot was rebuilt without it, and the campaign probe requires it added and taken by `MapScreen`. |
| 3 | major | Records are not bound to the layout in production; no record/scene mismatch refusal; the fixture castle's run-time gate (centre) differed from its record gate. | `SettlementFactory.CheckResolvedPositions` lists records that differ from the layout (tested: a castle gate moved half a unit, a record with no placement). Production records are MMR-45's (handoff). The run-time hierarchy is now built from the record's own gate and port (`Unresolved`), so it equals the record. |
| 4 | major | Harbour villages: the campaign flag gives 80 on A and 105 on B, but the layout used only the chosen candidates (30 and 25). | `SitesFromBundle` takes the campaign's harbour flag with the harbour cell of any candidate naming the village, as `SettlementFactory` does: 80 and 105, each with a cell. A flagged village without a cell is refused. |
| 5 | major | Blockade clearance checked only the arc ends; mirroring must swap start and end. | `BlockadeShips` replays `GetBlockadeArc` for `MaximumNumberOfShips`; an arc is accepted only with every ship on the port's basin. Mirroring swaps the ends. Offline, the tests replay the IL independently in single precision: 300 and 250 ships on their basin. In game every ship is over a sea face. A test drying only the ships rejects the arc. |
| 6 | major | Probe tautologies: `GetBlockadeArc(0)` cannot fail; the teardown through `GetEntities` proves nothing; the icon level check is trivial. | The probe asks for `MaximumNumberOfShips` and checks each ship over a water face. The teardown goes through `MapScreen.VisualsOfEntities`: no previous world's visual, one per current settlement, each bound. The icon check requires exactly one of a village's four icons visible with the campaign's levels. |
| 7 | major | The run-time fallback can hide a missing baked entity, and a minimal fortification without camps crashes sieges. | The run-time hierarchy is complete (camps, engines, ram, towers, walls, wall engines from the record). On a world's scene a town, castle or village built at run time is listed in `MissingBakedEntities` (MMR-45 refuses it). In game the run-time castle has full siege geometry. |
| 8 | minor | Circle and camp-area scales missing. | `EntitySpec.Scale`; the policy's installed scales (circle town 6.994, castle 3.191; camp areas). |
| 9 | minor | Nested transforms not composed; `castle_village` misclassified. | The policy tool composes full transforms and classifies castle villages. |
| 10 | minor | Cross-settlement marker overlap (12 pairs on A). | Every marker keeps 0.5 from every other settlement's centre and markers. Offline: 0 cross pairs on A and B. |
| 11 | minor | Steep pads reported, not avoided. | Kept as reported (the centre is MMR-38's binding point); acceptance is handed to MMR-41. |
| 12 | minor | Production hideouts are outside the layout. | Documented; handed to MMR-45. |
| 13 | minor | No engine check of settlement entities in `RuntimeStage`; `Prepare` does not check the policy's source against the contract's. | `SettlementEntityChecks.Run` runs in `RuntimeStage` and refuses a world (`runtime.settlement_entities`); world_surface records the same check. `Prepare` calls `RequireSource`. |
| 14 | minor | The report should carry scene positions, marker `from`, least steep in the gate rule, a refused-report persist and sweeping. | Done: `scene`/`gateScene`, `from`, least steep in the rule, `refused-<id>.json`, `Sweep` removes swept and refused reports. |
| 15 | minor | Doc inaccuracies (gate validation, helpers, records, ships on land). | `docs/settlement-entities.md` rewritten. |
| 16 | nit | Marker yaw should be the gate angle less a quarter turn (forward is +Y). | Done for the gate, markers and walls (tested). |
| 17 | nit | Invented `bo_village` tag; the static probe toggle should be a `Prepare` parameter. | The village selection is untagged, as installed. `factoryEntities` is a `GeneratedCampaignRegistration.Prepare` parameter. |

**Found by the confirming runs.**
- **Wall colliders.** World_surface run 34096 found plain-sphere wall colliders answering ground and ray queries over three villages beside a town's walls (0.5 to 0.96 from them). The colliders are raycast-only now, like the selection spheres. Run 29316 passed.
