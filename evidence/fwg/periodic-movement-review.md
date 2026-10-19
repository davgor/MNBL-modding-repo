# MMR-65 independent review (seam route execution and periodic spatial queries)

One independent review of the implementation, by an agent that read the code and the installed 1.4.8 / NavalDLC 1.2.8 assemblies, ran read-only IL dumps with Mono.Cecil, and neither edited the repository nor started the game. Every finding was fixed in one batch.

**Confirmation.**
- Offline: `tools/Test-PeriodicCoordinates.ps1`, 85/85 (`evidence/world/periodic-movement-tests.json`).
- In game: run 14144, status ok, with no movement failure, no wrap failure and no exception (`evidence/world/periodic-movement.json`).
- The confirming runs also exposed probe-setup defects, listed below.

| # | Severity | Finding | Resolution |
|---|---|---|---|
| 1 | major | Image-pass marks were keyed by the search position alone and never cleared. A later search at the same point (a party standing where an earlier strip-centred image pass was centred) got no seam continuation. | An image pass starts with the primary radius raised by one ulp; the mark holds its centre and that `RadiusSquared`; no engine search matches it. Run 19852's west-centre query showed the defect in game (2 of 4 parties found); run 14144 finds all 4 from every centre. |
| 2 | major (probe) | The reinforcement check called the encounter model after `StartBattle`, which had already run the join pass, and that model skips parties already in a map event. The check could never pass. | The probe asserts the engine's own outcome: the party across the seam is in the battle on the enemy's side; the one outside the radius is in no battle. |
| 3 | minor | The ring index could overflow after 2^31 marks and make every search an image pass, looping. | Index taken modulo the ring size; empty slots never match. |
| 4 | minor | The position gate assumed offsets are shorter than the pole margin without asserting it. | `MaxOffset` = 100; a world whose margin is not above it (+1) is refused at the preflight and at load (`RequireMargin`). |
| 5 | minor | `CampaignVec2 − Vec2` was assumed to be position − offset; `BesiegerCamp.GetSiegeCampPartyPosition` subtracts another party's position. | That subtraction is replaced through the gate (planar for offsets, periodic for positions); the method is in the table. |
| 6 | minor | Grid delegates were built lazily on the first seam search, possibly in a parallel tick; `StartFindingLocatablesAroundPosition` was not validated; only `Grid<Settlement>` was exercised. | Delegates built and validated while the set is applied (a failure refuses it); the probe exercises the party and track wrappers. |
| 7 | minor | Nothing enforced the no-duplicate contract for a radius of W/2 or more. | An image-pass result inside the primary circle is skipped for such radii. |
| 8 | minor | More than 16 nested image passes evicted an outer mark (duplicates). | Fixed by the tag (a search's own data identifies it) and 32 slots. |
| 9 | minor | The audit missed `NearlyEquals` (4 campaign sites), `Vec3` distances and native scene calls; the offline check did not tie the audit to the installed assemblies. | All three are scanned; `NearlyEquals` sites are transpiled; scene and `Vec3` sites need a rule (generation fails otherwise); the harness checks the scanned assemblies' SHA-256. |
| 10 | minor | Four table methods are small enough to be inlined into callers compiled before the patch. | The generator computes the callers of the 11 small table methods (closed over small callers): 27 methods patched unchanged, after their callees. |
| 11 | minor | The probe's step check was periodic, blind to a jump of about k·W. | `Travel` records the planar step less the party's rebase shifts; every section fails on more than 5 units. |
| 12 | minor | No section asserted "no transition at the cut" or a route cost after repeated crossings. | Army, fleet and circumnavigation count transition samples (none allowed); circumnavigation checks each leg's model cost takes the short way and that the state after two crossings prices a route as a fresh state. |
| 13 | nit | Doc wording ("small vectors near zero", "equals the planar one"). | Reworded with the margin precondition and the half-width condition. |
| 14 | nit | `FindPointInsideArea` has no installed callers. | Documented; the postfix stays. |
| 15 | nit | MMR-64's summarizer assumed 16 patch entries. | `--patches` argument. |
| 16 | nit | `Campaign.LoadMapScene`'s entry takes effect only on a later load. | Documented. |
| 17 | nit | The placement postfix canonicalized a failure sentinel; counters not atomic. | The centre returned on failure is left as given; counters use `Interlocked`. |
| 18 | nit | Probe cleanup ran only on success paths. | Every section restores AI flags, war and peace in `finally`; held lords are put back. |
| 19 | nit | Counts were checked only at apply time. | A re-run after the set is applied that finds other counts throws (that patch fails). |

## Found in the confirming runs (probe setup, not product behaviour)

The confirming runs exposed probe-setup defects that hid or faked results. Each was diagnosed from IL and the run's checkpointed result, then fixed in the probe. None changed product code.

| Run | Symptom | Cause | Fix |
|---|---|---|---|
| 3508, 19852 | native crash ("Crash occurred. Asking for dump") at the start of the circumnavigation | the ship was a probe-made lord carrying a looted ship (created lords sort first by id) | ports and circumnavigation use the rulers' own ships only (`Ships()`) |
| 19852 | pursuit `NullReferenceException`; ports' second leg never entered | cleanup read the faction of a party the interception had left leaderless, so peace was never restored and the ship could not enter an enemy port | factions captured at the start; every section cleans up in `finally`; ports makes peace between the two kingdoms for the section |
| 13332 | reinforcement: the party across the seam not in the battle | the wrap regression's AI sea legs leave `ruler_1` at sea; the join pass takes only candidates whose at-sea state is the player's | the probe uses land lords only for land sections and brings the player fully ashore |
| 35064, 28972 | the land army stopped about 30 units before the seam | a new army is held in its gathering phase (`Army.IsWaitingForArmyMembers` while `_armyGatheringStartTime` is 0) and `Army.HourlyTick` sends its leader back to the objective's gate; clearing the objective instead triggers the inactivity disband; grain made the army slow enough to be caught | the objective is the destination and the gathering is marked started with every member attached; the army forms 20 units from the seam; any dispersal reason is recorded |
| 30656 | the land army disbanded 12 units into the march (`LeaderPartyRemoved`, `ArmyLeaderIsDead`) | the war the army needs (an army without an active war disbands) let the rival kingdom's lords, left near the seam by earlier sections, kill its leader | the rival's lords are held in the middle of the world for the army and fleet sections and put back after; a battle the leader enters is recorded and fails the section |
| 28972 | track check found no track near the seam | tracks parties drop are random | the probe lays one track on each side of the seam (`MapTracksCampaignBehavior.AddTrack`) before the search |
| (user note) | parties starve over the multi-day legs (the circumnavigation runs about 16 campaign days) | no food | every party a section uses gets grain (`provisionedGrain` in the report) |
