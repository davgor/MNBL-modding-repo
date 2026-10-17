# Seam route execution and periodic spatial queries (MMR-65)

Parties, armies and fleets cross the periodic seam as one continuous motion. Every campaign query that measures positions sees the periodic world: proximity, interception, visibility, nearest destination, flee, army formation, tracks, spawns, quests and War Sails behaviours. This builds on the R10 mechanism (MMR-63) and the coordinate and distance contracts (MMR-64, `docs/periodic-coordinates.md`), and never redefines their rules.

| Piece | File |
|---|---|
| Route execution (the R10 mechanism) and its diagnostics | `CampaignBootstrap/PeriodicSeam.cs`, `World/GeneratedMapScene.cs` (placements), `CampaignBootstrap/PeriodicRouteExecutor.cs` |
| Spatial consumers (generated table, transpiler, grid wrappers) | `CampaignBootstrap/PeriodicSpatialQueries.cs`, `CampaignBootstrap/PeriodicSpatialTable.cs` (generated) |
| Locator image pass, canonical placements | `CampaignBootstrap/PeriodicPatches.cs` |
| Consumer audit | `tools/periodic/Scan-PlanarSites.ps1`, `tools/periodic/build_spatial_table.py`, `evidence/world/periodic-consumers.tsv` |
| Offline check of the table | `tests/World/PeriodicCoordinateTests.cs` (`SpatialTable`), `tools/Test-PeriodicCoordinates.ps1` (`evidence/world/periodic-movement-tests.json`, `.txt`) |
| In-game check | `Diagnostics/PeriodicMovementProbe.cs` (`generated_campaign` with `movementProbe=1`, after `wrapProbe=1`, on the MMR-63 fixture) |
| Evidence | `evidence/world/periodic-movement.json` (`tools/summarize_periodic_movement.py`), `evidence/fwg/periodic-movement-review.md` |

**Ownership.**
- This ticket owns route execution and spatial consumers.
- MMR-66 owns the camera, render copies, picking and the views: the 35 view sites in the audit.
- MMR-49 owns canonical state at the save instant.
- MMR-41 builds the per-world caches and reads the route diagnostics.

## Route execution

The mechanism is MMR-63's, approved under R10. MMR-65 adds no second mechanism; it states the contract and exposes diagnostics.

1. **Path query.** A query takes the target's placement whose native path is cheapest: `GeneratedMapScene` over `PeriodicDistanceModel.ShortestOverImages`. A path that crosses the seam runs into the strip beyond it, so it is one continuous planar polyline in the party's frame. A long-way route from a moved start (MMR-64) is shifted back to the caller's start.
2. **Following.** The party follows the path natively. Its position, its path points and its next target share one frame, so the movement's own differences are exact. Every planar measure between two campaign positions is periodic (next section).
3. **Rebase.** Once the party leaves the canonical range, `PeriodicSeam` moves it by whole widths on the same campaign tick, together with its path, its next targets and, for the player, the camera. It then canonicalizes its target fields. A path's points beyond the terrain edge are never reached, because the rebase comes first.
4. **Armies and fleets.** An attached member steers by its leader's target plus its formation slot, minus its own position (`MobileParty.DoUpdatePosition`). It takes its slot from `Army.GetRelativePositionForParty`. Both differences are now periodic, so a member on the other side of the seam from its leader takes the short way, crosses, and is rebased on its own.
5. **Domain transitions.** Embarking and disembarking are the engine's (`CheckTransitionParallel`, `NavigationHelper.CalculateTransitionStartAndEndPosition`, both in the table). Crossing the longitude cut is not a transition and charges no embark cost, because the rebase does not touch the navigation type. The army, fleet and circumnavigation crossings record no transition sample.
6. **Saved state.** Saves hold coordinates only. No native face or path handle is persisted: a face is re-derived from its position.

**Diagnostics (`PeriodicRouteExecutor`).**
- `Segments` lists a party's current path as segments in its own frame, each seam crossing marked east or west.
- `Describe` adds the domain (at sea, navigation capability, desired navigation), any transition in progress with its end, the path length, the longest segment and the party's rebases.
- MMR-41's cache verification reads these, and the probe records them for every moving section.

## Spatial consumers

**Audit.** `tools/periodic/Scan-PlanarSites.ps1` scans the installed campaign assemblies, every method including nested and compiler-generated types: TaleWorlds.CampaignSystem and its ViewModelCollection, SandBox, SandBox.View, NavalDLC, NavalDLC.View and StoryMode. It records:
- every call to `CampaignVec2`/`Vec2` `Distance`, `DistanceSquared` or `NearlyEquals`;
- every subtraction (`Vec2 − Vec2`, `CampaignVec2 − CampaignVec2`, `CampaignVec2 − Vec2`);
- every direct `LocatorGrid<T>` search, every call to the locator wrappers and to the point-around-position helpers;
- every native scene call (`Engine.Scene`, `MBMapScene`, `IMapScene`) and every `Vec3` distance, so that no consumer bypasses the campaign's map scene;
- for every small method holding such a call (an inlining candidate: IL of 100 bytes or less, or `AggressiveInlining`), its callers, closed over small callers;
- the SHA-256 of each scanned assembly.

`tools/periodic/build_spatial_table.py` gives each site one disposition, in this order. A native scene call or `Vec3` distance that no rule classifies stops the generation: a new consumer of either kind needs a rule before a table can be written.

| Disposition | Rule | Measures and grid searches | Native scene calls | `Vec3` distances |
|---|---|---|---|---|
| exact | the measure itself (`CampaignVec2`'s own members) | 8 | | |
| view | SandBox.View, NavalDLC.View, ViewModelCollection, the quest map arrow, the atmosphere grid (cosmetic; MMR-66) | 37 | 154 | 18 |
| mission | mission-scene coordinates (naval missions, agents, conversations, DWA steering, board games, tournaments, mission weather) | 125 | 326 | 118 |
| inactive | War Sails storyline, story mode, cheats: none runs on a generated world (the startup audit checks the storyline is inactive) | 26 | 49 | 16 |
| covered | MMR-63/64 patches (distance-model legs, distance helpers, encounter and visibility ranges, directions, click translation, closest-settlement order), MMR-39's storm model, the locator wrappers, grid search starts; native calls through `IMapScene` (GeneratedMapScene re-implements it) or inside `SandBox.MapScene` (the implementation it derives from) | 27 | 155 | |
| generic | `LocatorGrid<T>` and `NavigationCache<T>` (Harmony refuses generic types); the grid's own planar filter is answered by the image pass; the cache's are MMR-64's findings | 5 | | |
| callee-patched | callers of the locator wrappers and the point helpers (their callee carries the image pass or the canonical postfix) | 65 | | |
| periodic | transpiled from the table | 124 rows (98 methods, 161 calls) | | |
| recompile | callers of the table's 11 small methods: 27 methods patched unchanged | | | |

Counts are audit rows (one per method and callee) in `evidence/world/periodic-consumers.tsv`.

**One transpiler, exact counts.**
- `PeriodicSpatialQueries` patches every table method once.
- Each `Distance`, `DistanceSquared`, `NearlyEquals` and subtraction becomes its periodic counterpart. Each direct `LocatorGrid.FindNextLocatable` becomes a wrapper that continues an exhausted search at the seam image.
- The calls it finds must equal the table's counts, per column. Otherwise the whole periodic set is refused: `PeriodicPatches.Apply` is all or nothing, the preflight refuses the campaign start, and a world whose load-time patches fail is frozen. A required consumer is never left planar silently.
- The grid delegates are built while the set is applied, so a failure there refuses the set too.
- When another patch later recompiles a table method, the transpiler runs again. If it then finds other counts, it throws, so that patch fails instead of leaving the method planar.
- **Inlining.** Harmony marks a patched method as not inlinable for code compiled after the patch. Code compiled before it, such as a vanilla campaign earlier in the same process, can hold an inlined planar copy of a small table method. The 27 recompile entries are the callers of the 11 small table methods. They are patched with their IL unchanged, after their callees, so they are compiled again and call the periodic bodies.
- **Offline check.** The harness checks against the pinned assemblies:
  - the same counts;
  - that every recompiled caller calls a table method or another recompiled caller;
  - that the audit scanned exactly the installed assemblies (SHA-256 equal).

**When a measure becomes periodic.**
- A replaced measure is periodic only when both operands are campaign positions: inside the terrain, and within the logical rows between the pole lines (one unit of tolerance), where every campaign object lies.
- Anything else is measured as before: an offset, a direction or a velocity. So `position − offset` still yields a position, and directions and speeds are unchanged.
- That holds only while every offset is shorter than the pole margin. Formation slots and flee and patrol offsets are tens of units at most; `MaxOffset` is 100. A world whose margin is not above `MaxOffset` + 1 is refused at the preflight and at load (`RequireMargin`). The fixture's margin is 400; production margins are larger.
- `CampaignVec2 − Vec2` goes through the same gate. It is usually a position minus an offset (planar). In `BesiegerCamp.GetSiegeCampPartyPosition` it is a position minus another party's position (periodic).
- `NearlyEquals` compares the short-way x difference with the tolerance, as the planar test compares |dx|.
- When both operands are positions in one frame (a party and its own path) closer than half the width, the periodic result equals the planar one (to within one rounding).

**What the table covers.**
- AI scoring and initiative, engage and military behaviours.
- Flee position and behaviour, defending and patrol positions.
- Army formation and gathering.
- Party movement internals: `DoAIMove`, `DoUpdatePosition`, `DoAiPathMode`, `ComputeIsWaiting`, `CalculateSpeed`, the transition checks.
- Tracks and their detection.
- Bandit, deserter and hideout behaviours.
- Healing and recruitment near settlements.
- Map-event positions.
- Anchor points.
- Escort and ambush quests.
- War Sails pirate, fishing, stormrider, storm, weather and visibility behaviours. These include the storm spawn separation, storm collisions and the wet-weather test that MMR-39 left planar.
- Load-time map-change migration.
- The direct searches of `MobilePartyAi.GetBehaviorForNearbySettlementToFlee`, `PeaceBarterable.Apply` and `MapTracksCampaignBehavior.QuarterHourlyTick`.

## The periodic radius query

**Wrapped searches.** `MobileParty`/`Settlement.FindNextLocatable` (MMR-63), and the direct `LocatorGrid` searches (`PeriodicSpatialQueries.GridNext*`), continue an exhausted search at the seam image:
- for a canonical centre within the radius of the seam, at its copy across the seam;
- for a centre in a strip (a party or storm between its move and its rebase), at its canonical copy.

**The mark.** An image pass is recognised from its own search data.
- It starts with the primary radius raised by one ulp. The squares of adjacent floats differ after rounding, so no search the engine starts at the same centre has the same `RadiusSquared`.
- Its centre and that `RadiusSquared` are marked in a thread-local ring (`PeriodicPatches.MarkImage`, 32 slots).
- A search that is not an image pass never matches a mark, however old. A party standing exactly where an earlier image pass was centred still gets its own image pass. A nested search never evicts its caller's mark.
- MMR-63 marked by a non-canonical centre, which skipped strip-centred searches. The first MMR-65 build marked by position alone, which let a stale mark refuse a later search at the same point (run 19852's west-centre query). This closes MMR-63's handoff and MMR-39's missed storm-damage tick.

**No duplicates.**
- For a radius below W/2 the primary and image circles cover disjoint parts of the canonical range, and campaign objects are canonical, so no object is found twice.
- For a wider radius, an image-pass result inside the primary circle is skipped. No installed search is that wide; the rule keeps the contract regardless.
- No clone instance exists: the strips hold display copies only, with no campaign identity.

**Final filtering stays with the consumers.** Their distance tests between positions are periodic (table). Their domain tests are the engine's.

**Canonical results.** `NavigationHelper.FindPointAroundPosition` (bandit, pirate and fishing spawns, naval patrol points) returns canonical points, like MMR-63's `FindReachablePointAroundPosition`.
- A helper that finds no point returns its centre. That answer is left as given, because callers detect the failure by comparing the two.
- Both `FindPointInsideArea` overloads get the same postfix, but nothing in the installed assemblies calls them.

## In game

**The run.** `generated_campaign` with `wrapProbe=1` and `movementProbe=1` on the MMR-63 fixture (`build/research-wrap63/fixture-c`, world `mnbl_00063042`, seed 405570): run 14144, status ok. Summary: `evidence/world/periodic-movement.json` (`tools/summarize_periodic_movement.py`); raw result: `evidence/runtime/in-game/generated_campaign-14144.json`.
- No movement failure and no wrap failure.
- No exception thrown while the sections ran. A first-chance logger writes every exception to the progress log, with the stack and IL offsets.
- No unattached party left non-canonical at the end.

**Patches.**
- 20 periodic patch entries, including the spatial table: 98 methods with 161 replaced calls, and 27 recompiled callers.
- The MMR-63 wrap regression passes every one of its checks with 20 entries.

**Synthetic setup.** All of it is recorded in the report.
- The fixture has one ruler per kingdom, so a section that needs more lords creates them in the ruler's clan (5 created).
- Every party a section uses gets 300 grain. The sections run campaign time for days, and starving parties lose troops and disband armies.
- War is declared where a section needs it, held lords are put out of the way, and everything is restored in `finally`.
- Ports and the circumnavigation use the rulers' own ships.

| Section | Result |
|---|---|
| Radius query | Four lords placed at x 1192, 1197, 403, 408. A canonical centre east of the seam, a centre in the east strip and a canonical centre west of it each find all four exactly once, matching the periodic radius; a centre in the middle finds none. The direct settlement grid from the east seam finds the west town; the direct party grid from the west seam finds the four. `HeroHelper.GetClosestSettlement` at the east seam answers the west town, the periodic nearest. |
| Reinforcement (encounter joiners) | The player battles a lord at x 1199.4. A lord of the same kingdom at x 400.4 (periodic 1.0, planar 799, joining radius 3) is in the battle on the enemy's side after `StartBattle`'s own join pass. The one 8.4 away is in none. |
| Pursuit (moving target) | The target starts 3 before the seam heading 60 past it; the pursuer 25 before it. Both cross. The interception is at x 431.6 (logical 31.6, canonical), the pursuer having travelled 56.6 with one rebase. The largest step is 0.06 periodic and 0.06 planar beyond its rebases. |
| Track grid | One track laid each side of the seam. The direct track grid search from x 1199 (radius 15) finds 12 of 37 tracks, exactly the 12 within the periodic radius, 9 of them across the seam. |
| Army (land) | Formed 20 before the seam with two attached members. It arrives 20 past it in 38.6 travelled, leader and members rebased once each. No member detached and no transition sample; the army is still active. |
| Fleet | Port to port across the seam in 73.3. Members attached throughout, each rebased once. No transition sample, all at sea. |
| Cancellation | The player's route is cancelled 3 before and 3 after the seam: the held party drifts 0, and the reversed route returns the short way (62.5 and 66.5). |
| Ports | A ruler's ship enters the west port from the east and the east port from the west, one rebase each (81.9 each way). The largest step before the entry is 0.22; entering moves the party 6.25 from the port to the settlement. |
| Circumnavigation | Four legs 250 east: two crossings, 997.5 logical advance over 998.1 travelled, 381 campaign hours. Each leg's model route cost is the short way (250.4–252.9; the long way is 550), and no transition sample. After the crossings, the cost to the east port from the ship's own state equals a fresh state's at the same point (165.03, the same face; 165.03 from the port back). |

**Earlier runs (history in the summary).** These were not evidence runs.
- **19852.** Partial: the process hung natively in the circumnavigation; its checkpoint was copied from the autorun folder. It showed the stale image mark in game: the canonical-west query found 2 of 4.
- **13332.** Everything passed except reinforcement (an at-sea candidate).
- **30656.** Everything passed except the army, whose leader the rival kingdom's lords killed.
- **Probe fixes.** The probe fixes these runs led to are in `evidence/fwg/periodic-movement-review.md`.

## Limits

- **Paths that leave the terrain.** A route whose path itself leaves the terrain cannot be represented (MMR-64).
- **Unaudited planar comparisons.** The audit covers distance, near-equality and subtraction calls, direct grid searches, native scene calls and `Vec3` distances. A planar comparison written as raw component arithmetic (`a.X − b.X` on floats) is not found by it. None is known on a campaign path; the review read the movement and AI core.
- **Differences reused as operands.** A difference of two positions whose y exceeds the pole margin, used again as an operand, would pass the gate as a position. No table site is known to do this. The fixture's height (800) exceeds its margin (400), so the gate's precondition is stated for offsets, not for such differences.
- **JIT inlining.**
  - The recompile entries cover the table's small methods.
  - MMR-63/64's own patch targets keep MMR-64's statement.
  - A caller of a recompiled method that is itself inlined into older code would need another level. The closure follows small callers to any depth, so this applies only to large callers inlined under `AggressiveInlining`.
- **`Campaign.LoadMapScene`.** It is on the stack when the set is applied at load, so its own replaced measure (the map borders, which lie outside the rows anyway) takes effect only on a later load in the same process.
- **Views (MMR-66, done: `docs/periodic-presentation.md`) and save-instant canonical state (MMR-49).**
