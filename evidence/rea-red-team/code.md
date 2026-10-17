# CODE lens: periodic navigation versus installed Bannerlord

The search-marker defect is reproduced only with a controlled predicate harness; its occurrence through normal game callers remains unproven. The retained diagnostic history is a static lifetime finding, not a measured memory failure. No game launch, mod build, deployment, native session, or campaign playtest was performed by this lens.

Reviewed fixed dirty-source snapshot `C:/Users/davgo/AppData/Local/Temp/mnbl-rea-review-01a14795`, captured over HEAD `89247468cc3b5e2c006c6473282fa09da4833074`. This is not an integrated atomic build. Source references below are relative to that snapshot. The four files cited for the findings matched the live working tree by SHA-256 at handoff. Do not assume that remains true after Claude's next edits.

## Finding C1: spatial-search ownership is encoded in reusable coordinates

**Mechanism.** `CampaignBootstrap/PeriodicPatches.cs:303-361` identifies an image pass by `(x, y, radiusSquared)` in a permanent thread-local 32-slot ring. An independent search with the same tuple is treated as the earlier image pass and denied its continuation. Conversely, after 32 later marks, an unfinished original image pass loses its marker and can restart. Sequential inner searches during a suspended outer enumeration can consume the ring; depth alone is not the bound. The statement at lines 303-308 that no engine-started search can match an old mark is stronger than the implementation establishes. Raising a radius by one ULP makes it distinct from its own original radius, not distinct from all independently supplied floating-point radii.

**Executed evidence.** The coordinator ran `code-repro.ps1` in a fresh Windows PowerShell process. It extracts the original `ImageSlots` through `TryImage` block verbatim and references the installed `TaleWorlds.Library.Vec2`. Only world constants and invocation are scaffolded. Results in `code-repro-results.txt`:

```text
first=True; unmarkedControl=True; markedImageStops=True;
independentSearchContinues=False; originalImageRestartsAfter32Marks=True;
primaryRadiusSquared=100; expandedRadius=10.000001;
expandedRadiusSquared=100.000023
```

The positive control proves that the same strip search can continue before the mark exists. Expected independent-search continuation is true; observed false. Expected old image termination after unrelated searches is false for `TryImage`; observed true.

**Installed contract.** REA identifies `LocatorGrid<T>.FindNextLocatable` as token `0x060020e5` and `StartFindingLocatablesAroundPosition` as `0x060020e6` in the CampaignSystem artifact below. The search wrapper is owned by the mod; the installed locator does not provide this thread-ring identity. Its strict distance comparison also means the enlarged-radius trick is a geometric change, although no separate gameplay finding is asserted from the one-ULP change.

**Action.** Introduce explicit per-enumeration ownership and cleanup, or a wrapper that owns both passes rather than inferring identity from geometric values. Preserve the caller's original radius and exact acceptance predicate. Instrument normal search call paths to establish the actual maximum interleaving before choosing any bounded storage. Add a world generation identity/reset and regressions for independent same-tuple searches and suspended enumerations.

**Falsification / next validation.** An actual caller analysis proving no independent radius can match and no enumeration can survive 32 marks would narrow this to an API robustness issue. Do not describe the reproduction as missed enemies observed in a running campaign. Re-run actual game searches near a seam with many parties and nested scoring before assigning player impact.

## Finding C2: diagnostic lists retain campaign-long history and Storm objects

**Mechanism.** `CampaignBootstrap/PeriodicSeam.cs:46,64,107` stores every successful party rebase in a static list. `CampaignBootstrap/PeriodicStorms.cs:18-19,34,75` stores every seam/pole correction, including a strong `Storm` reference. The reset is reached on scene load through `World/GeneratedMapScene.cs:178-179`. `GeneratedMapScene.Destroy` at lines 413-437 does not clear either list. The history therefore grows with corrections and can retain storm instances after their manager removes them, including after scene destruction until a later scene reset. This is unconditional production-tick instrumentation; it is not gated on an active probe.

**Action.** Keep totals separately; use a capped ring for diagnostics, stable storm identifiers instead of strong storm references, and explicit teardown cleanup. Allow a bounded detailed capture only while a probe owns it. `UI/PeriodicMapCameraView.cs:127` already caps camera rebase evidence at 200 records and is a useful local precedent.

**Evidence and limits.** Verified by reading the actual append/reset/teardown paths and searching source references to these collections. No memory profile, retained-heap graph, OOM, or campaign-length performance threshold was measured. The finding is unbounded retention, not a claim that ordinary play already crashes. A correction that performs no rebase adds no record. Validate with a long controlled crossing/storm run and a weak-reference check after teardown; no such game run was performed here.

## Opportunity O1: a native-path intelligence layer for the generated world

Build a route inspector that exposes what Bannerlord actually considers reachable: land-only, naval-only and mixed routes; alternate east/west paths across the seam; embark/disembark points; and chokepoints implied by settlement adjacency. This can become the foundation for convoy planning and meaningful port placement instead of relying only on straight-line map distance.

**Verified seams.** Installed `TaleWorlds.CampaignSystem.Map.IMapScene.GetPathBetweenAIFaces` (`0x060020bc`) and `GetPathDistanceBetweenAIFaces` (`0x060020bd`) accept face records, start/end positions, radius, excluded terrain IDs, and directed switch costs. `MapDistanceModel.GetLandRatioOfPathBetweenSettlements` (`0x06001c42`), `GetNeighborsOfFortification` (`0x06001c4e`), and `PartyNavigationModel.GetInvalidTerrainTypesForNavigationType` (`0x06001c30`) expose useful campaign facts. All are confirmed against the installed CampaignSystem assembly; metadata signatures are in `code-evidence.json`.

**Existing insertion points.** `World/GeneratedMapScene.cs:326-395` already evaluates periodic placements; `CampaignBootstrap/PeriodicRouteExecutor.cs:29-67` already exposes route segments, crossings and transition diagnostics. Record each candidate's actual result, capability, endpoints and rejected reason rather than only the winner. Show this read-only first. Then use verified routes to drive explicit player convoy/waypoint orders and evaluate route-sensitive AI without changing the native solver blindly.

**Prerequisites found through REA.** The installed `MobileParty.ComputePath` pushes `extraCostMultiplier = 2` at IL `0x00f1`, then calls `GetRegionSwitchCostFromSeaToLand` at `0x00f7`, then `GetRegionSwitchCostFromLandToSea` at `0x00fd`. `IMapScene` parameter names order these two integers land-to-sea then sea-to-land. `SandBox.MapScene.GetPathBetweenAIFaces` forwards them unchanged. Equal switch costs can hide this slot/name discrepancy. Before adding asymmetric harbor/landing costs, exercise intentionally unequal values and identify which native region receives each cost.

The mod selects its image using the distance API, which lacks `extraCostMultiplier`, then obtains the route with that multiplier (`GeneratedMapScene.cs:321-339`). Native cost ordering equivalence has NOT been established. It is a runtime experiment prerequisite, not a proven current wrong-route bug. REA managed metadata cannot establish the native path solver's weighting semantics. The proposed intelligence layer should expose both candidate selection and executed path, making that discrepancy measurable.

## Opportunity O2: turn the REA evidence into a semantic patch contract

Use REA as a compatibility compiler: preserve each patch target's assembly SHA-256, MVID, resolved method signature, CIL hash, expected call/field anchors and declared semantic role. Before enabling the periodic world, verify this identity and then capture the composed Harmony patch owners/order. This makes more ambitious hooks reviewable across game versions and other mods.

**Current gap.** `Campaign/GeneratedPatches.cs:106-113` admits game/version labels; `CampaignBootstrap/PeriodicSpatialQueries.cs:114,164` verifies operation counts. The older patch group in `PeriodicPatches.cs:191-211` requires merely a nonzero replacement count. `PeriodicSpatialTable.cs:156` stores scanned assembly hashes, but a bounded search of `src` found only that declaration. A count cannot establish that the same operands still mean positions rather than offsets, nor that later patch composition preserves the intended dataflow.

**Concrete first increment.** Export the already generated table as a hash-bound contract; verify hashes and all expected replacement counts during preflight; record exact offending target and foreign Harmony owners when a contract cannot be proven. For supported alternate binaries, regenerate and review the contract instead of weakening the check. REA raw CIL hashes exclude headers/locals/exception sections and normalized tuple hashes omit some control-flow detail; retain the separate metadata/exception records and resolved member identities rather than treating a tuple hash as a semantic proof. No replacement Harmony implementation is required for this increment. Coordinate the hook/ownership data with the separate Harmony investigation.

## Evidence identities

`code-evidence.json` retains complete REA summary/view receipts, exact selected method rows, and focused Mono.Cecil IL supplementation from installed files. REA performed file-backed metadata/CIL parsing, not execution.

- CampaignSystem SHA-256 `1f8e33e2ed73e6ec653d7629180afb70649ddc6e5bd1657a802a264efda1c3ae`; MVID `886629fe-6e60-40d7-9a57-8d46017179d9`.
- CampaignVec2 / LocatorGrid / LocatableSearchData parent Evidence `ev_d235d7f02a27ada6c94b3dcc9db3e6dd4628e2185ca3629c18cfd7044565e278`; complete-selected-types coverage, no issues. Retained methods view `ev_19ab1f6fbfa9d59ae062f9e8132f4abae561243b814bfa36f28e69f3866d4814` is stored in the JSON alongside unchanged rows.
- MobileParty parent Evidence `ev_1c5fcea8fad81c110e1d4989141b952514efc99e6ef348ddf1308253ad5c85f4`.
- IMapScene / MapDistanceModel / PartyNavigationModel parent Evidence `ev_6eced00fb10a0e7e5ee728f912866c80c3ca480ba6d8e5081afee9d47c912e16`.
- SandBox SHA-256 `587c6e94c425745c1478f632f3b66226ff8298666cb27e8e6d9a62488df5956a`; MapScene parent Evidence `ev_f5a57a5a957a6473639aa0b57cf0cdbfc86e33be3d0439e27015346c844def31`.
- Actual Vec2 assembly used by the controlled reproduction: Library SHA-256 `ab4ff23e94a6f4b6f293d0cb4333cae731a0499063fe4a8b6c6a3770de32c4e5`.
- Snapshot `PeriodicPatches.cs`: `bb8c06d7cf6c18b9f17ebca28713a7251ba873b17711874242e7ad49b82d9e2b`; `PeriodicSeam.cs`: `72a0841b54300718246e139c97d78665f5961df485f8b036c862cff8bce61085`; `PeriodicStorms.cs`: `196f865463f86ef9f9ab1ea43d164c6867363b9e67419e3ae5a3a2825c8c49bd`; `GeneratedMapScene.cs`: `91170faa7cdbd44cfd18ae2636748ce8d4efd187a2b049eedea0247487ff3d95`.

## Adversarial review and limitations

- C1 has a controlled actual-predicate reproduction with a positive control. It is not a demonstrated installed-caller sequence or campaign failure. The test uses a minimal world context, not a full `PeriodicWorldCoordinates` campaign.
- C2 is a static retention mechanism. It does not establish user-visible lag or memory exhaustion. Native resource retention through a Storm object was not traced.
- Independent gamer-lens cross-check agreed on the narrow C1 claim (synthetic tuple collision and mark eviction, no installed-game reachability) and confirmed the C2 append/reset/strong-reference mechanism. This is a separate source check, not an additional runtime measurement.
- Managed interface metadata, exact CIL arguments and forwarding are observed; proposed route features and semantic-contract design are analyst recommendations. Native path behavior and Harmony's live composed execution remain unknown.
- The coordinator's supplied dirty snapshot is treated as a fixed review substrate, not evidence that all files build together. Existing run claims in project comments/docs were not replayed or independently accepted as fresh tests.
- Dropped: CampaignNearlyEquals losing the land/sea flag. Installed CampaignVec2.NearlyEquals already delegates only position to Vec2.NearlyEquals; there is no new discrepancy established.
- Dropped: `NavigationPath.PathPoints` returns a copy, making rebase writes ineffective. Installed getter returns its backing array, and OverridePathPointAtIndex writes the same array.
- Dropped: unconditional NavalDLC table entries as a supported no-DLC regression. The live module declares NavalDLC as a dependency and generated startup requires exactly one NavalDLCSubModule; optional comments alone do not establish a supported no-DLC product mode.
- Corrected the first reproduction setup: Add-Type initially failed because the netstandard facade was not referenced. The corrected script references the installed .NET Framework facade and succeeded centrally. No failed attempt is counted as a predicate result. A hash-comparison shell command initially had a PowerShell pipeline syntax error; the corrected read-only command verified the four equal hashes above.

## Re-run commands

From the repository, in a fresh Windows PowerShell process:

```powershell
powershell.exe -NoProfile -File evidence/rea-red-team/code-repro.ps1
```

For source anchors, read the fixed snapshot; for provenance use target-free REA `inspect_managed_members` with the explicit installed paths and exact `type_names` recorded in the JSON. Page the retained parent Evidence through `inspect_analysis_view` rather than repeating the producer. Focused supplemental IL was read with installed Mono.Cecil `AssemblyDefinition.ReadAssembly`, selecting exactly MobileParty.ComputePath and MapScene's two path methods; no target assembly was executed by that inspection.

Suggested coordinator index line: `CODE: spatial-search marker identity (controlled reproduction; live reachability pending), uncapped seam/storm diagnostics, and verified native-route/semantic-patch upgrade seams — evidence/rea-red-team/code.md`.
