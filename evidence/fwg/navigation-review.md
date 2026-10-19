# MMR-38 independent review and disposition

**Reviewer.** One independent agent, no repository edits and no game launched. It re-ran `tools/Test-WorldNavigation.ps1` (36/36) and `tools/summarize_world_navigation.py` on run 32580 (27/27).

**Verdict.** Accept with fixes: 14 MINOR findings plus NITs. In bridge decks (including the L case), bank fans, inset keys, z values, strip edges, face ordering, RNM1/LZ4/NMG9 encoding and the periodic union, it found no correctness bug.

All findings were fixed in one batch, followed by one confirming batch of runs (bottom). Each finding below is the reviewer's text, condensed, followed by what was done.

## Findings and disposition

**1. MINOR: production routes only checked reachability, not shortest paths. "Under the deck" was not verified, and world A had no under route.**

Fixed:
- Every expected route's engine path length is now bounded by the mesh's own shortest path: at most 1.25 × that path + half a cell. The plan computes it with a face corridor string-pulled by the funnel algorithm. The worst ratio in run 42984 is 1.14.
- Bridge crossing routes must cross their deck's Bridge face, and under routes their UnderBridge face (`RouteCheck.Through`).
- Under routes are now looked for on every deck cell, but only where the water runs straight through. Between adjacent water sides the shortest path may round the corner through the diagonal cell.
- Neither A nor B has a straight-through deck cell; both report this explicitly in `static.notes`. The fixture covers under-bridge routes, including in game with the decks disabled.

**2. MINOR: the engine island check could not fail, and "FaceIslandIndex is the mesh's face connectivity" was unsupported.**

Fixed in docs and code comments:
- The engine island check is stated as weak, because each mesh is one connected piece.
- The doc now claims only what was observed: the fixture's mountain-closed pocket, which is a separate All component, shares the mainland island, so `FaceIslandIndex` is not All connectivity.
- The members' All connectivity is the static `navigation.islands` check plus the All routes.

**3. MINOR: corner-hop transitions were accepted undetected, and the code comment misdescribed them.**

Fixed:
- Region switches are now counted exactly from the engine, as (cost − path length) / 50.
- All routes between groups require at least 2 switches; port, coast and port-gate transitions exactly 1.
- A route whose switches exceed its sampled domain changes is listed in `engine.cornerHops`. The confirming run lists none in A or B.
- The policy decision is explicit in the doc: diagonal land contacts are accepted as All connectivity. Whether a moving party executes such a leg is MMR-45's to observe (handoff).
- The comment is corrected: the cause is diagonal contact between land cells of different groups.

**4. MINOR: the port envelope passed because the port point was moved, and nothing tied that position to production. "No point moved" was wrong. `GetPortToGateDistanceForSettlement` includes the switch cost.**

Fixed in the doc and the report:
- The placed port points (`static.points`) are a binding output for MMR-40, MMR-41 and MMR-45.
- "No snap moved a point" now sits beside the 30 and 25 ports placed (at most 3.98 units, inside their own cells).
- The port-gate contract is restated. The cache value includes the 50 switch cost (MMR-17: 55–56), so the envelope is checked on the engine path's length, with exactly one switch.
- Handoff comments go to MMR-40, MMR-41 and MMR-45.

**5. MINOR: the gate bound only the scene name and navmesh hash, and a corrupted report threw `ArgumentException`.**

Fixed. The record is persisted with the hash of its exact report text. The gate requires:
- the hash to match;
- validator version `mmr38/2`;
- the installed policy hash;
- the scene and its world (the content-id prefix);
- the installed navmesh hash;
- no injected engine faults;
- an activatable report.

Any read or parse failure is a `navigation.report_invalid` refusal. All of these are offline-tested.

**6. MINOR: a gate refusal inside `GeneratedMapScene.Load` could kill the process without diagnostics.**

Fixed: the refusal's diagnostics are traced before the exception escapes.

**7. MINOR: nothing in production creates the report, and the fault report was persisted under the production scene name.**

Fixed:
- The doc says MMR-45's activation must run `NavigationValidator.Run` and `Persist`, or find a persisted report that still passes the gate.
- Fault reports record `engineFaults`, are checked in memory with `NavigationGate.Check` (refused, including `navigation.report_faulted`) and are never persisted.

**8. MINOR: regression risk to MMR-37's world_surface and to generated_campaign was unobserved.**

Fixed: both were re-run on the MMR-38 module (below).

**9. MINOR: the MMR-17 harness was re-implemented, not reused; the sampler was coarser (0.5) and added a tolerance; no AI party was moved.**

Fixed in docs and comments, and the sampler step changed:
- The route function and sampler are described as restated, not reused; the campaign probe is untouched.
- The sampler step is 0.25, MMR-17's.
- The 0.05-unit tolerance is documented as an addition: boundary ties between a sea face and a mountain face are not shortcuts.
- AI party movement on compiled worlds is deferred to MMR-45. The AI party capabilities here are the installed navigation types' excluded faces.

**10. MINOR: "the boats still pass under" rested only on the offline stand-in.**

Fixed: a fixture engine-fault step was added. In game (run 42984), with Bridge faces disabled, every bridge crossing failed and every under-bridge sea route passed.

**11. MINOR: coast transitions were snapped but never routed, and the land entrance's group was unchecked.**

Fixed:
- Every coast transition has an All route from its land point to its sea point with exactly one switch: 42 routes in A, 18 in B.
- A transition whose land cell is not in its group is a diagnostic.

**12. MINOR: host checks that cannot be built were skipped silently.**

Fixed: a missing settlement, road, point or port transition is now `navigation.input_inconsistent`.

**13. MINOR: the tile-boundary test is nominal.**

Fixed in the doc. The mesh is one lattice, with no stitching. The fixture routes cross the interior chunk lines (x, y = 800) and the seam lines (x = 400, 1200); y = 400 and y = 1200 lie in the pole padding.

**14. MINOR: the nearest-image rule differed from MMR-63's.**

Fixed: routes, both static and engine, try every periodic image of the target and keep the shortest found path, as `GeneratedMapScene`'s periodic queries do. A forbidden route must fail in every image.

**15. NIT: dead code and unused policy fields.**

Fixed:
- The fixture's detour bounds come from the policy's `barrier` constants.
- `PathExists` / `DoesPathExistBetweenFaces` were removed.
- `NavCell` lost its unread fields. Domain, water class, cost and passage are now reported per combination (`build.cellSemantics`).

**16. NIT: misleading comments and doc statements.**

Fixed:
- The pre-read disable is described as observed to have no effect; every call returns 0, and this is recorded.
- The fixture label is corrected.
- "Cell for cell" now reads "per-label cell counts".
- The snap wording is corrected.
- `navmesh-bin.md` §2.1 now records that zeroed scratch fields load. (Since 2026-10-19 that answer is in `docs/formats/navmesh-bin-loading.md`: `navmesh-bin.md` is back at the bytes the naval-transitions calibration pins.)

**17. NIT: the sanity routes were listed as forbidden, `group_not_realized` had empty source ids, and portFace's expected type came from the builder's typing.**

Fixed:
- The sanity routes are their own check kind.
- `group_not_realized` names the group's crossings, the unbuilt bridge host checks and the settlements cut off from the group's largest component.
- portFace's expected type now comes from `cells.bin` and the policy names, independently of the builder.

**18. NIT: stability was shown only within one process.**

Fixed: a second process on the same module (run 42956) reproduced the semantic reports of the fixture, A and B byte for byte.

## Confirming runs (one batch, module built after all fixes)

**Offline.** `tools/Test-WorldNavigation.ps1`: 36/36 (`evidence/world/navigation-tests.txt`). `tools/Test-WorldSurface.ps1`: 24/24 (MMR-37 harness; scene names now include the navmesh hash).

**In game, `world_navigation`.** Runs 42984 and 42956; `tools/summarize_world_navigation.py`: 34/34 (`evidence/world/navigation-routes.json`).
- **World A:** 826 routes and 393 faces agree.
- **World B:** 1,118 routes and 586 faces agree.
- **Fixture:** 44 gating routes and 23 faces agree.
- **Static faults:** refused in `Prepare` with source ids.
- **Engine faults:** on the fixture and on A, refused at the gate and not persisted.
- **Warm A:** identical to cold A.
- **Second process:** identical reports.

**Regression on the same module** (`evidence/world/navigation-regression.json`):
- `world_surface` 26828 with `generated_campaign` 34660: `tools/summarize_world_surface.py` 32/32;
- campaign 34660: startup audit 33/33, naval content 10/10, settlement fixtures 13/13, graph gate 9/9.

**Earlier runs, kept as history.**
- 43964: first engine contact. Distances included switch costs, and islands were compared with All components.
- 32580: before the review.
- 35320: stopped by an in-memory report's typed `failures` list in `NavigationGate.Check`. `Json.A` now accepts any sequence, and the offline test covers an in-memory failing report.
