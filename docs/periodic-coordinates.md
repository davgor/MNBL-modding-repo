# Periodic coordinates and distance contract (MMR-64)

One canonical coordinate contract for an x-periodic world, and one versioned periodic distance contract over the installed distance surface. Both are pure managed code. Policy version `mmr64/1` (coordinates and distance).

| Piece | File |
|---|---|
| Coordinate contract | `src/MNBL.ProceduralWorld/World/PeriodicWorldCoordinates.cs` |
| Distance contract and coverage lists | `src/MNBL.ProceduralWorld/World/PeriodicDistanceModel.cs` |
| Recorded policy (metric, distortion, tie rule, placements, poles, identity) | `worldgen/policies/periodic-coordinates.json` |
| Offline tests | `tests/World/PeriodicCoordinateTests.cs`, `tools/Test-PeriodicCoordinates.ps1` |
| In-game check | `Diagnostics/WorldWrapProbe.cs`: `DistanceCoverage`, the tie copy variant, the save policy |
| Evidence | `evidence/runtime/periodic-coordinates.json` (from `tools/summarize_periodic_coordinates.py`), `evidence/runtime/periodic-coordinates-tests.json`, `evidence/fwg/periodic-coordinates-review.md` |

**Ownership.**
- This ticket owns the canonical math and the model adapters.
- MMR-65 owns path execution and spatial consumers.
- MMR-66 owns the camera, render copies and picking.
- They read `PeriodicWorldCoordinates` and `PeriodicDistanceModel` and never redefine a rule. Both types are immutable: a layout is fixed at construction, and the coverage entries are read-only.

## Frames and identity

- **Frames.** Physical is native scene XY on a terrain of `[0, Extent]²`. Logical is campaign XY without strips: x in `[0, W)` (periodic), y in `[0, H]`, with 0 the north pole line and H the south pole line.
  - `physical = logical + (OriginX, OriginY)`, where OriginX is the strip and OriginY the pole margin.
  - `FromSurface` reads these from a bundle's projection (`periodicLayout`, `target`, `source.gridSide`, `centralMeridianDeg`).
  - `FromPlacement` reads an MMR-63 placement's `periodic` block.
  - The width and both origins are whole numbers of 1/1024, so the single-precision campaign view equals the double one at the seam.
- **Canonical physical x** is `[MinX, MaxX) = [OriginX, OriginX + W)`.
  - `CanonicalX` returns canonical values exactly.
  - A value within rounding of the seam's upper side returns MinX, never MaxX.
  - The single-precision overload computes in double. MMR-63's computation returned MaxX for the float just below MinX (399.999969).
- **Display copies.** A copy is the canonical location shifted by a whole number of widths (`WrapIndex`, `DisplayOffset`). Copies carry no gameplay identity: campaign state is canonical, and a campaign object's identity is its id.
- **Location keys.** `LocationKey` is canonical logical x and y quantized at 1/1024, with x taken modulo the period.
  - Keys are built from canonical state, never from a display point.
  - Copies computed in double give the same key.
  - A copy rounded to single precision can land in the neighbouring quantum. The offline tests measure how often: `singlePrecisionCopyKeyDisagreement`.
- **Source uid.** `SourceUid` is the nearest source grid node, `r<row>c<column>`, with the column taken modulo `gridSide − 1`. For all 292 settlements of bundle f596b58c it equals the node their uid names.
- **Poles.**
  - Logical y ≤ 0 is the north pole line and y ≥ H the south one; the padding beyond each line repeats it.
  - Every x on a pole line is one location: the source's single pole node replicated across the row. Keys and source uids drop x there (`N`, `S`).
  - North and south are never connected. There is no artificial portal and no regional crop.
- **Cache keys.**
  - Form: `mmr64/1|<identity prefix>|<capability>|<from key><L|S>><to key><L|S>`.
  - Keys are directed and never reordered.
  - The installed settlement cache itself stores one value per unordered pair (`NavigationCacheElement.Sort`), so its distances are symmetric. Direction enters through War Sails' transition adjustment and the region switch costs of path queries.
  - Native face pointers and face indices are session-local and never part of a key.

## One tie rule, and placements

`DeltaX(a, b)` lies in `(−W/2, W/2]`; an exact half width resolves east (+).

`Placements(from, target)` returns the three displacements D = DeltaX, DeltaX + W and DeltaX − W, ordered by |D| with ties going east.
- Each is placed inside the terrain: the target's copy at `from + D`. When that copy lies outside the terrain, the start moves by the fewest whole widths (0, −1, +1, −2, +2) instead.
- The first placement is the short way and never moves a canonical start.
- Every |D| < W is representable in a terrain of 2W with strips of W/2, so a route the long way around is never dropped.
- A route whose path itself leaves the terrain cannot be represented.

`PeriodicDistanceModel.ShortestOverImages` asks the native (planar) query for each placement in that order and keeps the cheapest found path.
- Within 1e-3 the earlier placement wins, so the answer does not depend on which copy the caller passed.
- A placement whose straight-line length cannot beat or tie the best cost is not queried, and the limit tightens to the best so far.
- The query keeps its own capability (excluded terrain), obstacles and directed region switch costs. A placement only moves the endpoints by whole widths, so a blocked corridor or an incompatible region stays unreachable however close a copy is.

| Consumer | Before (MMR-63) | Now |
|---|---|---|
| `PeriodicWorld` | own math, a single-precision seam bug and a dead `NearestImage` | delegates to the coordinates |
| `GeneratedMapScene.NearestTargetImage` (periodic `GetPathBetweenAIFaces`/`GetPathDistanceBetweenAIFaces`, which build the navigation cache) | the target's in-terrain copies only; ties kept the as-given copy | `ShortestOverImages`. The copies passed keep their faces; a moved start's face is the strip copy's face of the same group. A path from a moved start is shifted back, so it begins at the caller's start and runs on continuously (`PeriodicSeam` rebases a party that follows it). Copies are chosen by cost without the path query's extra cost multiplier, which the installed distance query does not take |
| `NavigationPlan` (MMR-38 validator) | as given, then west, sorted by \|dx\|; logical → physical with the MMR-63 modulo | `Placements` (and `Physical` through `ToPhysical`). It tries every placement without pruning and keeps the shortest found path (MMR-38's rule), so it can choose another copy than the cost rule when path length and cost disagree |
| `PeriodicPatches.PeriodicDelta` | `−DeltaX(a, b)` (−W/2 at the tie) | `DeltaX(b, a)` |
| `GeneratedBattleTerrain` | blocks counted from physical x = 0; strip cells sampled where they lie | every cell computed at its canonical image; blocks counted from the seam |

Not changed:
- **C++ projection `wrap_delta`.** It rounds half away from zero and differs from this rule only at \|dx\| = W/2. Its only caller, path splitting of graph edges, never reaches that, since every edge is at most `W / (gridSide − 1)` long. Changing it would rebuild `worldbridge.exe` and rename every content-addressed scene.
- **Two data generators** (`SettlementFactory.Dist`, `GeneratedSpawnModel.CellMap.Distance`). They compute an unsigned `min(|dx|, W − |dx|)` for logical cells in `[0, W)`, which equals \|DeltaX\| there and has no tie. They are left so that generated data stays byte-identical.

## Metric and distortion

- **Metric:** planar equirectangular in the logical frame, x periodic. Movement costs are the engine's path costs on the campaign navmesh, over the placements.
- **Lower bound:** the wrapped geometric distance (`GeometricDistance`, `PeriodicDistanceModel.LowerBound`) bounds a cost from below. It prunes and orders, and is never reused as a cost or a reachability answer.
- **Distortion:** east-west map distance overstates sphere distance by 1/cos(latitude): ×1.155 at 30°, ×2.0 at 60°, ×5.759 at 80°, unbounded on the pole line.
- **Decision:** accepted, with no geographic runtime metric.
  - The pole bands are ocean or padding.
  - The projection records the distortion.
  - MMR-63 run 12348 moved, routed and fought on the planar metric.
  - A world that needs polar land travel needs another projection.

## Distance surface coverage

**The model.** `PeriodicDistanceModel.Manifest` lists every member of the installed `MapDistanceModel` (Bannerlord v1.4.8, War Sails v1.2.8): 18 methods and the `PossibleMaximumMapBoundary` const.
- Each entry has its treatment, whether it is directed, and a reason.
- It is checked against the pinned assemblies offline and against the running type in game. `DefaultMapDistanceModel` and `NavalDLCMapDistanceModel` both declare every method.

| Treatment | Members | How the periodic world reaches it |
|---|---|---|
| cache | settlement-to-settlement distances (both), land ratio, maximum connected distance, neighbours, port-to-gate (War Sails), War Sails `PathExistBetweenPoints` | The settlement navigation cache is built from `IMapScene` path queries answered by `ShortestOverImages`. Neighbours come from `CheckBeingNeighbor`'s path samples and closest-settlement checks. `NavigationCache.GetLandRatioOfPath` measures planar segment lengths of the returned path; the path runs continuously from the canonical start into the strip, so those lengths are exact. |
| party-leg | party to settlement, party to party (both), party to point, settlement to point | Each straight leg (`CampaignVec2.Distance`, 11 sites per model) becomes the wrapped geometric distance. Party to settlement is `d(p, t) − d(E, t) + cache(E, t)`, with E the closest entrance to the party's face; the point overloads are alike. |
| entrance | `GetClosestEntranceToFace`, and through it the party, point and helper formulas | The per-face closest settlement cache. See "Closest entrance" below. |
| capability | Default `PathExistBetweenPoints` | `IsOnLand` only. |
| constant / registration | region switch costs (directed), companion spawn distance, `PossibleMaximumMapBoundary` (const 1e8), `RegisterDistanceCache`; War Sails `GetTransitionCostAdjustment` | The transition adjustment adds the directed region switch costs. The cache distance only decides whether the sign flips. The caches register after `PeriodicPatches` applies, once per session. |

**The helpers.** `PeriodicDistanceModel.HelperCoverage` lists every planar distance call in `Helpers.DistanceHelper` and `Helpers.SettlementHelper`:
- `DistanceHelper.FindClosestDistanceFromMobilePartyToMobileParty`: the bird-fly test, `CampaignVec2.DistanceSquared` < 2500;
- `DistanceHelper.GetDistanceBetweenMobilePartyToMobileParty`: the two straight legs of `d(p1, p2) − d(E1, E2) + cache + transition`;
- `SettlementHelper.FindNearestSettlementToPoint`.

All four sites are transpiled to periodic distances. The list is checked by reading the helpers' IL: offline against the pinned assembly (a reflection IL reader), and in game through Harmony's original instructions.

**Closest entrance.**
- `NavigationCache<Settlement>.GetClosestSettlementToPosition` builds the per-face closest settlement over every navmesh face when a cache registers; `SandBoxNavigationCache.CheckBeingNeighbor` uses it too.
- Both installed callers pass `useEarlyOut = false`. Every candidate's real path distance is therefore evaluated through the periodic `IMapScene`, and the result is the exact nearest in any candidate order.
- The order decides only two things: the search limit (twice the best so far), and which of two exactly equal candidates wins.
- The order comes from the two lambdas of `SandBoxNavigationCache.GetClosestSettlementsToPositionInCache`, by planar squared distance. They now take the wrapped geometric distance.
- The `useEarlyOut` branch (two planar comparisons choosing gate or port, one planar cut-off) never runs and is not patched. Harmony also refuses the generic method: "The given generic instantiation was invalid" (run 14048).

**Patch set and failure handling.** The periodic patch set is 16 entries: MMR-63's 11, the two ordering lambdas, and the three helpers.
- `PeriodicPatches.Apply` is all or nothing: on any failure every patch owned by its Harmony id is removed before the error is rethrown. Run 14048 exercised this on Harmony's refusal.
- `DiagnosticSandboxBootstrap` preflights the whole set (apply, then remove) before a periodic campaign starts or loads, so a refusal ends the start.
- A world whose Load-time `Apply` still fails is frozen: `PeriodicSeamBehavior` stops campaign time, locks it, and shows the reason once.

## Policy participation

There are two identities:
- **`FrameIdentity`** is the SHA-256 of `FrameText`: the coordinate version, tie rule, canonical range, poles, quantum and layout.
- **`Identity`** is the SHA-256 of `PolicyText`: `FrameText` plus the metric, the distance version and the placement rule.

| Where | What |
|---|---|
| Save header | `WorldIdentity` key `coordinatePolicy`: the frame identity, or `none`. `RequireMatch` (through `PeriodicWorldCoordinates.RequireSavedPolicy`) refuses a periodic save made under another frame or none, and a header that disagrees with the package on periodicity. A distance-model change alone does not invalidate saves. `Build` and `RequireMatch` read the coordinates through one factory, `PeriodicWorld.FromPlacement`, which registration uses as well. |
| Navigation report | `coordinatePolicyVersion` and `coordinatePolicy` (the identity). `NavigationGate` refuses a report made under another policy version. |
| Generation job record | `coordinatePolicy` and `coordinatePolicyVersion`; the `generation_job` probe checks them. |
| Cache keys | the version and the identity prefix. |
| Battle-terrain map report | the identity, under `periodic`. |

## Seam splitting for data

- `SplitInterval(x0, x1)` returns canonical pieces of `[0, W]`.
- `SplitSegment(p, q)` splits a straight logical step taken the short way (ties east) at the seam: it ends on x = W and continues from 0 going east, or 0 then W going west, with y interpolated.
  - A step that starts on the seam and goes west starts at W, so there is no zero-length piece.
  - This matches the C++ projection's path pieces.
- `GeneratedBattleTerrain` is the first data consumer. On a periodic world a strip cell holds exactly its canonical cell's bytes, and a block boundary falls on the seam.

## Evidence

**Offline** (`tools/Test-PeriodicCoordinates.ps1`, 82/82; `evidence/runtime/periodic-coordinates-tests.json`, `.txt`):
- **Canonicalization:** the range ends exactly, many wraps, negatives, the double and the float just below xmin (MMR-63's formula returned 1200 for 399.999969).
- **Tie rule:** half-width ties both ways. Width 1000: (998,200) and (2,200) are 4 apart, not 996. 200,000 near-tie double pairs agree with `DeltaX`.
- **Placements:** every displacement shorter than a width is placed, for 4,096 canonical start/target pairs; the long ways from 1100 to 1150; a strip start.
- **Poles and identity:** poles and their aliases; frames round-trip (2,000 points); display copies share keys (computed in double). Single-precision copies one width away differ in 4,812 of 100,000 samples, the documented limit.
- **Settlement source nodes:** the `SourceUid` of all 292 settlements of bundle f596b58c is the node their uid names.
- **Seam splitting** (including a westward step from the seam), **directed cache keys** and **save compatibility** (frame identity accepted; another frame, the full identity, none, or disagreeing periodicity refused).
- **The policy file** matches the code constants.
- **The production layout of bundle f596b58c:** identity `4a111663…`, frame identity `158296e8…`.
- **The grid oracle:**
  - the mixed grid at both strip ratios: 22,568 pairs each, 58 unreachable, all agreeing;
  - two wall grids at the production ratio: 900 and 2,025 pairs, all agreeing, 40 long ways from a moved start;
  - a blocked seam corridor (2 apart, cost 14) and a double wall (unreachable);
  - land-only and sea-only capabilities (unreachable at 1 apart), and directed switch costs (6 against 2), at both ratios.
- **The coverage manifest** against the pinned `TaleWorlds.CampaignSystem` (19 members) and both models.
- **The helper coverage** against the pinned helpers' IL: 3 methods, 4 sites.
- **MMR-38 regression:** `tools/Test-WorldNavigation.ps1`, 36/36.

**In game** (`generated_campaign` 6292 on fixture-c with `wrapProbe`; `evidence/runtime/periodic-coordinates.json`, all 16 checks):
- **Patches:** the whole `MapDistanceModel` matches the manifest, and both models declare it. 16 patches: the ordering lambdas, 1/2/1 helper sites, 11+11 model legs. The helper scan of the running assemblies finds exactly the listed sites.
- **Cross-seam values:**
  - every cache and leg overload is about 75.9 across the seam against 725 in a straight line;
  - the seam legs give 64.57 (0 or −585.2 with the periodic world off) and 5 (795);
  - the nearest town by the seam is town2 across it (town1 without the periodic world).
- **Closest entrance:** correct at 80 samples near the seam, 30 of them seam-sensitive.
- **Placements:** 27,818 queries used a moved start.
- **Battle map:** 80,000 strip cells match their canonical cells, and a block starts at the seam.
- **Settlement cache:** symmetric. Transition adjustments are the installed directed values (direction is not observable with War Sails' 50/50 costs).
- **Save:** the header carries the frame identity; the package check accepts it and refuses another frame or none.
- **Tie:** resolves east whichever copy is passed.
- **MMR-63 regression:** every check rerun passes (travel both ways on land and sea, encounter and battle at the seam, strip click, save near the seam).

**Regressions on the final tree:**
- `world_navigation` 6436 (MMR-38): 33/33;
- `generation_job` 34480 (MMR-44): 17/17, and the job record names `coordinatePolicy` `4a111663…`;
- the MMR-19 save and load pair: 17/17 on the rerun (19384 + 31924). The first pair (33364 + 3820) differed in one recomputed steering value by 2 ulp on a non-periodic world. All recorded in `evidence/runtime/periodic-coordinates-regressions.json`.

**History:**
- Run 14048: Harmony refused the then-planned generic `GetClosestSettlementToPosition` patch, and the all-or-nothing `Apply` removed every patch. The process was later terminated from outside during the All cache, with no crash record.
- Run 6384: before the review, the full probe ran; its only failure was the probe's own AI-party filter.

## Limits and handoffs

- **MMR-40 / MMR-45:**
  - Production campaign factories place settlements at logical positions with no strip or pole-margin offset; they must place at `ToPhysical(logical)`.
  - A production world registers its periodic world from the bundle (`FromSurface`). The single factory `PeriodicWorld.FromPlacement` must then move with it, so that the save header and its check keep reading the same coordinates.
  - The production activation must call `PeriodicPatches.Preflight` before starting a periodic campaign, as `DiagnosticSandboxBootstrap` does.
- **MMR-65:** spatial consumers outside the distance surface and the distance helpers remain planar. Of the MMR-63 survey (`build/research-wrap63/planar_campaign.tsv`, 182 call sites):
  - 29 are now patched (MMR-63/64: the model legs, the closest-settlement order, the helpers);
  - 4 are exact without a patch (the closest-settlement `useEarlyOut` branch, which no caller enables, and `GetLandRatioOfPath` on the continuous image path);
  - 129 are spatial consumers (AI scoring, quests, NavalDLC behaviours and the like);
  - 20 are in views, including `SettlementPositionScript`'s editor-only navigation cache.

  MMR-65 also owns route execution, armies, sieges, raids and embark at the seam, plus routes whose path leaves the terrain.
- **MMR-66:** display copies, labels and picking use `Images`, `WrapIndex` and `DisplayOffset`; keys must come from the canonical object.
- **MMR-41:** a serialized per-world navigation cache must be keyed by `CacheKey` or the identity, never by face pointers.
- **MMR-49 / MMR-50:** resuming and migrating saves. The header's `coordinatePolicy` (the frame identity) is the compatibility key, and a frame change needs an explicit migration.
- **MMR-42:** battle scene choice per terrain. The periodic block rule only fixes the seam.
