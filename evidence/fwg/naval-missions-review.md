# MMR-53 independent review and fixes

One independent review of the MMR-53 working tree. The reviewer was an agent with read-only access. It did the following without editing files or launching the game:
- read the probe, the shared harness changes in `LandMissionsProbe`, the summarizer, the scenario file, the offline checks and the doc;
- checked engine claims against the installed IL (TaleWorlds.CampaignSystem, SandBox.View, NavalDLC);
- ran the scenario check, the land offline checks and the summarizer's `recompute()`/`trim()` in memory;
- read the raw outputs of development runs 42652 and 45664.

The tree changed during the review: run 45664 and a raid landing wait (rule `landingSeconds`) landed while it read. It reported 2 blockers, 6 major and 13 minor findings. All were fixed in one batch, then the confirming runs followed (below).

## Before the review

Five development runs found the probe's own faults, each fixed and guarded in the scenario file:
- **27796:**
  - the port menu's repair ran before the menu's view attached its handler, and threw;
  - AI parties created at sea were read before their position was set.
- **26772:**
  - the naval raid's loot screens went unanswered, so the raid never progressed;
  - the raid scene's landing entities were counted by exact tag, not by the controller's tag expression.
- **43188:** the raid's end menu for a raid "led by someone else" went unanswered.
- **36684:**
  - the raid's return was checked on land, though a seaborne raid leaves the party at sea;
  - the defeat left troops ashore that kept the player's side alive, so no capture followed.
- **42652:** the raft's wait menu could not start campaign time under the probe's time lock.
- **45664:** passed 81/81 before the review, under the scenario file before `landingSeconds`.

## Blockers

1. **The summarizer did not parse.** The new landing label closed its string at `War Sails'`. Fixed with double quotes before the rest of the batch; the whole summarizer was then reworked (below).
2. **No evidence for the current tree.** Run 45664 predates the landing rule and the fleet rows, the scenario file named no fixture, and the doc's results and limits were unwritten. Fixed: a fresh run after the whole batch, with the evidence, the fixture and the doc's results written from it.

## Major

1. **The landing check passed without a landing.** `Agent.GetSteppedMachine()` returns a `UsableMachine`, and War Sails' `MissionShip` is a `MissionObject`, so every agent read "ashore". Fixed: agents are counted as War Sails tracks them, aboard when `AgentNavalComponent.SteppedShip` is set, ashore when it is null, untracked without the component.
2. **The party was moved between cases without a record, and the "chain" claim was false.** `LandMissionsProbe.Enter` set the party at a gate unrecorded, and the blockade moved it about 43 map units with no record of where from. Fixed:
   - `Enter` records every placement: where the party came from and how far (`relocations`);
   - the summary checks each voyage's placement is within the land return bound of where the previous step left the party;
   - the blockade records where it found the party (`relocatedFrom`) and moves it back there after the siege (`movedBack`, checked);
   - the probe comment and the doc now describe the order and every placement instead of a chain.
3. **Coverage the ticket asks for was missing and undisclosed.**
   - Fixed: the drop-off adds a coastal landing by a click on land (`MapState.ProcessTravel`, the terrain click's own call): the sea-to-land transition, land navigation, the anchor left at the coast, then back aboard at that anchor.
   - Fixed: the pirate battle is called a retreat throughout, and its scene is checked against the patch.
   - Disclosed in the doc's limits: boarding, crew reinforcement, ship damage in battle, defender combat (the probe kills the raid's defenders), return to a ship in a mission, and visual against functional water.
4. **The pirate audit was one-directional.** Extra zones would not have lowered the matched count, and `fromScene == 0` holds by construction. Fixed:
   - the patrol zones must be exactly the served points: as many zones as served points, each on a served point;
   - each pirate clan must be served the plan's points for it (`PlanCountFor`), and the plan must have none for other clans;
   - every served point must be on naval navigation;
   - the doc states that `fromScene` is 0 by construction and that C06's points are the plan NavalContentFactory compiles in the session; a production world has no separate on-disk artifact to compare with.
5. **The raid's end menu was unexplained.** Every run ended at `village_raid_ended_leaded_by_someone_else` after a raid the player led alone. Fixed: every map event that ends at the village is recorded with what `VillageHostileActionCampaignBehavior.OnMapEventEnded` reads (a raid, the player's, the attacker leader, the village raided, diplomatically finished, ended by retreat, the winner), and the summary requires the end menu and those records. The cause found is in the doc.
6. **AI arrivals were over-counted and AI island travel unmeasured.** A party that had ever been at sea counted when it later entered a port town by any route. Fixed: an arrival is a landing and the town's entry in the same tick (`EnterSettlementAction.ApplyForParty` sets `IsCurrentlyAtSea` false, then the settlement); other entries are counted apart (`portEntriesOverLand`); arrivals at island port towns are counted and required.

## Minor

1. **The raft row checked only the end state.** Fixed: the raft state must be seen, its wait and end menus must appear, and the escape must free the party at sea.
2. **"Every party within its zone" was claimed but not checked.** Fixed: the claim is removed. The distance beyond the zone's radius is recorded (`beyondZoneRadius`), and unassigned parties are counted and required to be 0.
3. **Narrow straits were not shown.** The clearance read distance to land in any one direction, so a coast read like a strait. Fixed: the water's width is measured across 8 axes (both ways along each, the narrowest axis), away from both ends of the voyage.
4. **The probe never read the pinned targets.** Fixed: the voyage, the raid village and the blockade town come from the pins, each still checked against its rule; a pin that no longer meets it fails the run.
5. **The defeat's scene was never checked against the patch.** Fixed: the same rule as the returning battle (`PatchScene`).
6. **Two raid regressions shared one mutation.** Fixed: run 36684's return check has its own mutation (`raid-return-checked-on-land`).
7. **Mutations that set an error passed through the generic row.** Fixed: every guard names the check its mutation must fail, and the replay requires that check to fail.
8. **Wrong comments and doc wording.** Fixed:
   - the fleet "given back" after the raft (the end menu gives back the ships the raft started with, none after this defeat);
   - loot screens "taking what they offer" (they are cancelled: nothing taken);
   - the arcs' "12 by default" (the script caps the request; the points laid are recorded);
   - the header's coverage;
   - "prisoner menus passed" (the defeat menu's continue, then the forced escape);
   - the encounter as "War Sails' own" (it is the campaign's `PlayerEncounter`);
   - `ReturnTo`'s comment, which still said "on land".
9. **The purchase recorded the buy button's type name.** Fixed: its state is read (relevant, enabled, cost, tooltip) and the button must be enabled.
10. **`Skip(lose ? 1 : 0)` was unexplained.** Fixed: the defeat excludes the retreat's pirate by id.
11. **State was left behind.** The retreat's pirate stayed in hold mode and the defeat's was not moved back. Fixed: both go back where they were found and patrol their assigned zone (`SetMovePatrolAroundPoint`, as `PiratesCampaignBehavior` sends them), checked.
12. **The shared harness named naval stages and screenshots "land".** Fixed: `State.Tag` gives the stage prefix and screenshot tag; a land_missions confirming run followed the harness changes (below).
13. **Setup the doc did not disclose.** Fixed in the doc's setup section: autosave off, the player ignored by every party for 60 days (no pirate starts an encounter), the crew, grain and gold added, fast-forward, and the forced escape.

Not changed, and recorded as limits in the doc:
- `BlockadePositionScript.GetBlockadeArc` draws with `MBRandom`, so the arcs checked are one draw and the draw advances the campaign's random state.
- The blockade activates for a besieger's ships wherever they are docked; the player's fleet was at the island town.
- No blockade battle and no AI blockade are exercised.
- `AutorunProbes.cs` also registers other tickets' probes, and MMR-53 cannot be committed without MMR-52's `LandMissionsProbe.cs`; both are commit-order notes, not defects of the probe.

## Confirming runs

- **Naval, run 46036:** 104/104 recomputed checks, 13/13 cases, status ok, without a reload. The evidence (`evidence/gameplay/naval-scenarios.json`) and the committed fixture (`tests/Runtime/fixtures/naval-missions-46036.json`) are written from it. After the run the scenario file changed only in its `fixture` key, the guards' named checks and the naval.defeat description; the rules are the run's (checked by the baselines row).
- **Raid end menu:** the ended map events showed the cause (finding 5): the player led the raid's map event, and the village was still `BeingRaided` when `OnMapEventEnded` read it. In the installed IL, `MapEvent.FinalizeEventAux` dispatches the event before the raid component loots the village. Documented as the engine's order.
- **Narrow straits:** the width measurement shows no leg crossing water narrower than 8.5, so the pinned voyages do not exercise a narrow strait. Disclosed in the doc's limits.
- **Offline:** `tools/Test-NavalMissions.ps1`, 2/2: the scenario check (0 problems) and the replay (the fixture 104/104; all 8 regressions fail their named check).
- **Land, run 38972** (after the shared harness changes): 163/163 recomputed checks, 14/14 cases, status ok, wetland absent as before. MMR-52's evidence stays run 33816's; `tools/Test-LandMissions.ps1` passes 2/2.
