# MMR-64 independent review and disposition

**Reviewer.** One independent agent. It made no tracked-file edits and launched no game.
- It re-ran `tools/Test-PeriodicCoordinates.ps1` (67/67 at the time) and `tools/Test-WorldNavigation.ps1` (36/36).
- It re-ran `tools/summarize_periodic_coordinates.py` on run 6384, and `tools/summarize_wrap_probe.py` on 12348 (identical to the accepted evidence).
- It read the installed IL, and reproduced B1 with the test's own grid engine at the production ratio.

**Verdict.**
- c1, c2 and c3 each partially met.
- Two blockers, nine should-fix findings, eleven nits.
- It confirmed the canonical math (`CanonicalX`, `DeltaX`, `WrapIndex`, poles; `SourceUid` against all 292 settlements of bundle f596b58c).
- It confirmed the IL facts the design rests on: both callers pass `useEarlyOut = false`; 18 methods plus a const field; 11 legs per model; War Sails' transition formula; `GetLandRatioOfPath` exact on a continuous path; the C++ `wrap_delta` reasoning.

All findings were fixed in one batch, followed by confirming runs (bottom). Each finding below is the reviewer's text, condensed, followed by what was done.

## Blockers

**B1. The production layout (strips W/2, terrain 2W) cannot represent long-way-around routes. A target copy outside the terrain was dropped, so such a route was reported unreachable; the test grid used strips W and hid it.**

Fixed:
- `PeriodicWorldCoordinates.Placements` places each of the three displacements (DeltaX, DeltaX ± W; by |D|, ties east). It puts the target's copy at start + D, or, when that copy lies outside the terrain, moves the start by the fewest whole widths. Every |D| < W is representable.
- `ShortestOverImages` runs over placements. `GeneratedMapScene` queries from the moved start's strip face and shifts a returned path back by the same widths, so it starts at the caller's start; `PeriodicSeam` rebases a party following it.
- `NavigationPlan` uses the same placements, in both its static and its engine loops.
- The grid oracle now runs at both ratios, plus two wall grids at the production ratio. The reviewer's wall case agrees on 900/900 pairs, with 40 long ways from a moved start. (0,0) → (6,0) past the wall is the long way west, cost 10.
- A strip start (x = 1600) whose nearest copy lies outside is moved a width.
- In game (run 6292), 27,818 queries used a moved start.
- `PeriodicWorld`'s stale comment is corrected.
- **Remaining limit:** a route whose path itself leaves the terrain cannot be represented (MMR-65).

**B2. Used distance helpers outside `MapDistanceModel` stayed planar: `DistanceHelper.FindClosestDistanceFromMobilePartyToMobileParty` (bird-fly test), `DistanceHelper.GetDistanceBetweenMobilePartyToMobileParty` (two legs) and `SettlementHelper.FindNearestSettlementToPoint`.**

Fixed:
- All four sites are transpiled: `CampaignVec2.DistanceSquared(CampaignVec2)` → periodic, `CampaignVec2.Distance` → periodic.
- `PeriodicDistanceModel.HelperCoverage` lists every planar distance call in both helpers. It is checked against the pinned assembly offline (a reflection IL reader over all methods and nested types) and in game (Harmony's original instructions).
- In 6292, near the seam:
  - the bird-fly helper gives 5 (795 without the periodic world);
  - `GetDistanceBetweenMobilePartyToMobileParty` gives 64.57 (−585.2 without it);
  - the nearest town is town2 across the seam (town1 without it).
- The periodic patch set is 16 entries.

## Should-fix

| Finding | Disposition |
|---|---|
| **S1.** A patch failure ran the world half periodic. | Fixed: `PeriodicPatches.Preflight` applies and removes the whole set before `DiagnosticSandboxBootstrap` starts or loads a periodic campaign, so a refusal ends the start. A world whose Load-time `Apply` still fails is frozen: `PeriodicSeamBehavior` stops and locks campaign time and shows the reason. Handoff: MMR-45's activation must preflight too. |
| **S2.** `Build` (registration) and `RequireMatch` (recomputed from the placement) could diverge once MMR-45 registers through `FromSurface`. | Fixed: both read the coordinates through `PeriodicWorld.FromPlacement`, the factory registration uses. In game (6292) the saved key equals the world's and `RequireMatch` accepts it. Handoff: MMR-45 must move the single factory. |
| **S3.** In-game overload checks put the party and point on the gates, so planar and periodic gave the same values. | Fixed: seam legs place the party and point by the east seam, where the closest entrance (town2) lies across it. Every value must be short, never below the wrapped lower bound, and differ from the same call with the periodic world switched off. 6292: party→settlement, party→point and settlement→point 64.57 (0 without); model party→party 5 (795). |
| **S4.** The validator does not apply `ShortestOverImages` (it picks by polyline length) despite the comments. | Claims corrected: the validator tries the same placements in the same order without pruning and keeps the shortest found path (MMR-38's rule). The two can pick different copies when path length and cost disagree. Documented in the code, `docs/periodic-coordinates.md` and `docs/runtime-navigation.md`. |
| **S5.** The single-precision test was vacuous. | Fixed: it uses the float just below xmin (399.999969). MMR-63's formula returns 1200 for it; `CanonicalX` returns a canonical value. |
| **S6.** "Every copy has the same identity" is untrue for single-precision copies. | Documented in the code, the doc and the policy file: keys come from canonical state, never from a display point. The offline evidence records the measured rate (`singlePrecisionCopyKeyDisagreement`). The test now claims identity only for copies computed in double. |
| **S7.** The installed settlement cache is symmetric (`NavigationCacheElement.Sort`). | Fixed in the manifest: cache entries are not directed; party, point and transition entries are, through War Sails' transition adjustment. Docs and policy file updated. 6292 records the cache as symmetric (`settlementCacheSymmetric`) and transition direction as not observable (War Sails costs 50/50; the offline grid uses 5/1). |
| **S8.** Docs and evidence were stale. | Fixed: offline evidence regenerated on the final tree (82/82); `docs/runtime-backend-decision.md` R10 row and table updated; `docs/runtime-navigation.md` gate conditions now include the coordinate policy version. |
| **S9.** The MMR-19 save regression was not scheduled. | Run on the final tree (see "Confirming runs"). |

## Nits

| Nit | Disposition |
|---|---|
| N1. `SplitSegment` emitted a zero-length piece for a westward step starting on the seam. | Fixed: it starts at W. Test added. |
| N2. `ImagesNearestFirst` and `DeltaX` disagreed near half-width ties for doubles. | Fixed: placements are derived from `DeltaX`. 200,000 near-tie pairs agree exactly. `ImagesNearestFirst` is removed. |
| N3. `PeriodicDelta` gave −W/2 at the tie. | Fixed: `DeltaX(b, a)`. |
| N4. Manifest and comment inaccuracies. | Fixed: `PossibleMaximumMapBoundary` is const 1e8; the transition adjustment's cache distance only gates the sign flip; the party legs follow `d(p, t) − d(E, t) + cache(E, t)`; War Sails `PathExistBetweenPoints` lists cache; the bounded party-to-party entry is the installed distance; the early-out branch is described as two gate/port comparisons plus one cut-off. |
| N5. Duplicate periodic math. | `NavigationPlan.Physical` now uses `ToPhysical`. `SettlementFactory.Dist` and `GeneratedSpawnModel.CellMap.Distance` compute an unsigned magnitude for cells in [0, W), equal to \|DeltaX\| there; they are left so that generated data stays byte-identical, and the policy file's consumer line says so. |
| N6. Constructor validation gaps. | Fixed: every value must be finite, and the width and both origins must be whole key quanta. Tests added. |
| N7. A throwing `UnpatchAll` stranded patches. | Fixed: unpatching goes by Harmony id through a fresh instance, in `Apply`'s failure path and in `Remove`. |
| N8. Mutable manifest entries. | Fixed: read-only properties and collections. |
| N9. Weak tests. | Fixed: copies are compared by periodic difference; every layout field is varied for both identities; the no-op check is removed; the bundle `SourceUid` cross-check is added (292/292). |
| N10. A distance-only version bump would invalidate every save. | Fixed: the save key is `FrameIdentity` (coordinate frame and rules). The full `Identity` keys caches, reports and the job record. |
| N11. Image selection ignores the path query's extra cost multiplier. | Documented: the installed distance query takes no multiplier. |

## Found while confirming

**Run 6384 (before the review).**
- Its only failure was the probe's own AI-party filter: it required no ships, and War Sails lords carry them.
- Fixed by choosing any active party on land, lords first.

## Confirming runs (final tree)

| Run | Result |
|---|---|
| `tools/Test-PeriodicCoordinates.ps1` | 82/82 (`evidence/runtime/periodic-coordinates-tests.json`, `.txt`) |
| `tools/Test-WorldNavigation.ps1` (MMR-38) | 36/36 |
| `generated_campaign` 6292 (fixture-c, `wrapProbe`) | ok; `tools/summarize_periodic_coordinates.py`: all 16 checks, including the full MMR-63 regression → `evidence/runtime/periodic-coordinates.json` |
| `world_navigation` 6436 (MMR-38) | ok; `tools/summarize_world_navigation.py` 33/33 |
| `generation_job` 34480 (MMR-44) | ok; `tools/summarize_generation_job.py` 17/17; the job record carries `coordinatePolicy`/`coordinatePolicyVersion` |
| MMR-19 save and load, first pair (`generated_campaign` 33364 + `generated_save_load` 3820) | 15/17: one value differed after the cold load, `naval_patrol_party_1_party_3.nextTarget` x 9.53487396 → 9.53487587 (2 ulp of steering state recomputed after load) on a non-periodic world, where no MMR-64 code runs. Handed to MMR-49 as a determinism observation |
| MMR-19 save and load, rerun pair (`generated_campaign` 19384 + `generated_save_load` 31924) | 17/17 |

After the confirming in-game runs, the only source change was one comment in `PeriodicWorldCoordinates.cs` (the single-precision key rate now points at the evidence, not "about 3%"). The offline harness was re-run on the final source (82/82), and the summary regenerated.

All regressions are recorded in `evidence/runtime/periodic-coordinates-regressions.json`. Each was recomputed by its own summarizer; the accepted evidence files of those tickets are unchanged.
