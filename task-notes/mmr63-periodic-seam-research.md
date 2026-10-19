# MMR-63 research: periodic east/west seam (2026-10-14)

Started as a read-only IL survey; the runtime findings (runs through 12348) are in their own section below, and the accepted evidence is `evidence/runtime/world-wrap-probe.json`. The IL dumps are in `build/research-wrap63/`: `movement`, `core`, `view`, `visuals`, `distance`, `encounter`, `naval`, `weather`, `click`, `mapevent` and `savedmembers` (`.il.txt`), plus `planar_campaign.tsv` (182 planar distance calls in 121 campaign methods). Items marked UNVERIFIED are inferences.

## Engine facts

- **Bounds**
  - `Campaign.LoadMapScene` sets Map{Minimum,Maximum}Position, MaximumHeight and Diagonal from `IMapScene.GetMapBorders`.
  - Min/max are read only by `MapCameraView.ComputeMapCamera`, which clamps the camera target and the last ideal target with ClampFloat. Widening x after LoadMapScene lifts the clamp.
  - `MapDiagonal` is only a sentinel or score normaliser. Leave it.
  - These clamp: `NavigationHelper.FindPointAroundPosition` and `FindPointInsideArea`. `NavalDLC Storm.set_CurrentPosition` deactivates a storm outside the borders.
  - `MapScene.GetMapPatchAtPosition` clamps.
  - `DefaultMapWeatherModel.GetNodePositionForWeather` does not clamp: x outside the terrain gives a wrong row, or IndexOutOfRange at the first or last row.
- **Movement**
  - Per tick: `FillCurrentTickMoveData…` → `DoAiPathMode` → `DoUpdatePosition`, where `Next = Current + bearing * dist` and bearing = `NextTargetPosition − VisualPosition` (planar).
  - `TryToMoveThePartyWithCurrentTickMoveData` requires the next face to be valid and its `FaceIslandIndex` to equal the current face's. Only managed code reads it, plus one quest.
  - `ComputePath` calls `IMapScene.GetPathBetweenAIFaces` and fills a `NavigationPath`. It recomputes only when the target face or position changes.
  - `set_Position` writes only `_position` and updates the locator. Path, PathBegin, NextTargetPosition, PathLastFace, `_pathLastPosition` and `_lastNavigationFace` stay stale, so a teleported party walks its old points back across the map.
  - `SetMoveGoToPoint` does no bounds check; it fails only on an invalid face.
- **Distances**
  - The Default and Naval MapDistanceModels use the `INavigationCache` settlement path distances, built at load through `IMapScene.GetPathDistanceBetweenAIFaces`, plus planar legs (Naval GetDistance IL_0098/015c/0165, party→party IL_0083/00d8). All of these are virtual and can be replaced by a GameModels subclass.
  - Hard-coded planar gates: `PartyBase.CanPartyInteract` (encounter radius), `CalculateVisibilityRangeOfMapPoint`, the MobilePartyAi flee helpers, MapTracks.
  - Do not patch `CampaignVec2.Distance` globally; it is likely inlined (UNVERIFIED).
- **LocatorGrid(5f, 32, 32)**, used for parties, settlements and tracks, is already a toroidal hash with a 160-unit period.
  - `FindNextLocatable` filters with planar `Vec2.DistanceSquared` (IL_0055/00aa).
  - Fix: make W a multiple of 160 and replace those two calls with an x-periodic distance. Keep y planar so the poles stay separate. That covers about 22 query sites.
- **Camera, picking, labels, tracks**
  - `UpdateMapCamera` lerps toward `IdealCameraTarget`. Rebase `_cameraTarget`, `_cameraFrame.origin`, `IdealCameraTarget` and `_lastUsedIdealCameraTarget` in the same frame as the main party.
  - Entity picking goes through `MapScreen.VisualsOfEntities` (`Dictionary<UIntPtr, MapEntityVisual>`). A display copy registered to the same visual resolves to the same PartyBase, with no second identity.
  - `HandleLeftMouseButtonClick` IL_007c calls native `Scene.DoesPathExistBetweenFaces` directly, bypassing IMapScene. Terrain clicks use the overridable `CanPlayerNavigateToPosition`.
  - Nameplates (`PartyNameplateVM.RefreshPosition`, `SettlementNameplateVM.CalculatePosition`) and tracks (`MapTracksVisualManager.GetTrackOnMouse`) need the image nearest the camera.
- **Saves**
  - Saved `CampaignVec2` fields:
    - MobileParty: `_position`, `MoveTargetPoint`, `NextTargetPosition`, `_pathLastPosition`, `_targetPosition`, `EndPositionForNavigationTransition`.
    - Others: `MobilePartyAi.BehaviorTarget`, `MapEvent.Position`, `AnchorPoint.{Position, TargetPosition, _lastUsedDisembarkPosition}`, `Track.Position`; `MapMarker.Position` (Vec3).
  - Not saved: the face cache and Path (`ComputePathAfterLoad` recomputes it). Keep every saved field canonical and add a check before save.
- **Naval:** storm and weather grids and `SetupWaterWake(128, 8)` depend on bounds (wake extent UNVERIFIED).

## Strategy assessment

- **(a) Navmesh overlap strip alone: not viable.** The native pathfinder connects only faces in one native island, so a continent cut by the seam is two islands and the strip copy is a third. Strip faces also have different IDs.
- **(b) Managed periodic layer inside our `IMapScene` (`DiagnosticMapScene : MapScene, IMapScene`): recommended.** All navigation goes through IMapScene (14 GetFaceIndex, 15 GetPathDistance, 3 GetPath sites); the only native bypass is the click check above.
  1. `GetFaceIndex`: canonicalise x and merge `FaceIslandIndex` into periodic island IDs (union-find over seam portals).
  2. `GetPath*BetweenAIFaces`: take the minimum of the direct route and the routes through east/west seam portals (path to the portal plus path from its image). Return points unwrapped relative to the start. Ties within ε go east, then to the lowest portal index.
  3. Canonicalise inputs to the height, terrain, patch and nearest-face queries.
  4. Rebase pass after each tick, outside the parallel phase: shift the party position and its path points, NextTargetPosition, `_pathLastPosition`, NextLongTermPathPoint, MoveTargetPoint and EndPositionForNavigationTransition by ∓W. Rebase armies together and the camera in the same frame.
  5. Steering and gate patches:
     - `GetTargetCampaignPosition`, returning the nearest image;
     - the `DoUpdatePosition` reads of `MapEvent.Position` and the army leader's position;
     - `CanPartyInteract`, visibility, the LocatorGrid distance, `DoesPathExistBetweenFaces`;
     - a MapDistanceModel subclass;
     - weather and storm wrapping, an unclamped camera x, and visuals and nameplates at the image nearest the camera.
  - Physical layout: terrain likely cannot use negative x (UNVERIFIED), so logical [0, W) maps to physical [S, S+W), with duplicate terrain strips inside the terrain for display.
- **(c) Rejected:**
  - a global patch on `Vec2`/`CampaignVec2.Distance` (inlining);
  - a native periodic navmesh (faces are embedded in 3D space).

## Chosen plan (MMR-63 claimed 2026-10-14, attempt at_01m4w6ernfjstgtm3hvz9shvgw)

- **Layout: strips half the world wide.**
  - The logical world is x ∈ [0, W). Physical x = logical + S, with S = W/2.
  - The physical terrain is 2W wide: a left strip copying logical [W/2, W), the canonical [0, W), and a right strip copying [0, W/2).
  - A seam-crossing route is shortest only when |dx| > W/2, so the target's nearest image T ± W always lies inside the physical terrain. Native `GetPath*` on that image works unchanged, and no portal stitching is needed.
  - The cost is a terrain twice as wide; production might use narrower strips with stitching (a recorded limit).
- **Fixture: FWGC++ source 42.** Its heightfield is periodic in x: column 0 equals column 128, and row 0 is a pole alias of constant value.
  - Extended 257² field: 64 strip cells, then 128 logical, then 64 strip cells. The 129 source rows sit in the middle, padded with deep ocean above and below, so the poles are not connected and not cropped.
  - 6.25 m cells give W = 800 m, a multiple of the LocatorGrid's 160 period, and physical extent 1600 m.
  - Settlements go only at canonical positions; strips hold visual-only copies. Required: a land route and a sea route across the seam, a port near it, and parties on both sides.
- **Islands.**
  - The canonical east part plus the right strip form one native island; the canonical west part plus the left strip form another.
  - The rebase moves a party, and its path points by −W, the moment it leaves [S, S+W). Current and next faces then stay in one island.
- **Runtime layer:**
  1. `IMapScene` path and distance queries pick the nearest image of the target in the start's island, with deterministic ties.
  2. A rebase pass for parties, paths, targets and anchors, plus the camera.
  3. Harmony patches: `CanPartyInteract`, visibility, the LocatorGrid distance, target-position reads, the click path check.
  4. A periodic MapDistanceModel.
  5. Camera unclamp and rebase.
  6. Settlement display copies, and nameplates at the image nearest the camera.
- **Save/reload.** Generated-campaign save/load is NotSupported until MMR-19/49. MMR-63 defines canonical-at-save (all saved `CampaignVec2` canonical) and tests what is possible; a full reload belongs to the R05 matrix.

## Runtime findings (2026-10-14, attempt 3)

- **Phase 1 proven (runs 26492, 26432):** periodic land and sea routes, the distance model, the deterministic tie, and eight travel legs (player and AI, land and sea, both directions). Each leg had one rebase, a step of at most 0.37, and stayed canonical throughout.
- **LocatorGrid cannot be patched.** Harmony rejects the generic `LocatorGrid<T>.FindNextLocatable`: "The given generic instantiation was invalid" from `PatchFunctions.UpdateWrapper` (internal generic, by-ref struct argument).
  - Game code reaches it only through `MobileParty.FindNextLocatable` (69 call sites) and `Settlement.FindNextLocatable` (12), visibility included.
  - Postfixes on those wrappers continue an exhausted search whose circle crosses the seam as a fresh search at the image position. The engine's own planar filter then finds the canonical objects. A non-canonical search position marks the image pass.
- **`IInteractablePoint.CanPartyInteract(party)` measures `party` against *its own* AI target point** (`GetEncounterTargetPoint`), not against the receiver. It also requires both parties to share `IsCurrentlyAtSea`.
  - Setting `Position` does not change that flag. Use `MobileParty.DisembarkToPosition` (what the game menus use) to bring a party ashore.
- **`MapEvent.Position` is one party's position**, not a midpoint (`MapEvent.Initialize` IL_01c9/01d1), so it is safe across the seam.
  - The direction values are planar: `PatchEncounterDir` in `MenuHelper.EncounterAttackConsequence` and `MapEvent.GetEventDirection`. Both are transpiled.
- **Terrain clicks:** `MapScreen.HandleLeftMouseButtonClick` passes the picked point to `MapState.ProcessTravel`, which is its only caller. A prefix there translates strip (display) coordinates to canonical ones.
- **Campaign time:** StoppablePlay did not advance the seam setup within 30 s. Use UnstoppablePlay, as the baseline probes do.
- **Party distances:** the Default and NavalDLC `MapDistanceModel.GetDistance` overloads add planar `CampaignVec2.Distance` legs for parties (11 sites each). These include party→entrance, party→party and party→point. They are transpiled to a periodic distance; run 7136 measured player→hostile 0.30 against planar 799.7.
- **Placement outside a tick:** `NavigationHelper.FindReachablePointAroundPosition` serves map-event finalize, encounter-radius teleports, spawns, army dispersal and land patrols. It returned strip points: run 11596 started a battle at x = 1200.29. AI patrols also stored strip `MoveTargetPoint`s, which run 19568's save caught. Both are fixed with a postfix that returns the canonical image, plus the per-tick canonicalization of order targets.
- **Probe state after fast travel:** about three fast-forwarded days left the party starving, with every troop wounded and the hero wounded. The encounter `attack` option is then unavailable, so the probe heals before the battle. Caravans are excluded from the naval AI choice because `CaravansCampaignBehavior` re-issues their orders.
- **Save:** `ComputePathAfterLoad` recomputes the path from `MoveTargetPoint`. `NextTargetPosition` is therefore steering state: mid-crossing it can lie in the adjacent image. The other saved MobileParty positions stay canonical.

## Dependency decision (resolved)

The user chose to depend on the Workshop Bannerlord.Harmony module (and also allows ButterLib, UIExtenderEx and MCM). See docs/decisions/002-runtime-patching-dependency.md. PlayerSettlement (Workshop 3720376888) is a reference for runtime settlement creation.

## Original blocking decision (needed the user; now resolved)

Several required gates are non-virtual engine code: the LocatorGrid distance, `CanPartyInteract`, visibility, the click path check, the target-position reads and the `DoUpdatePosition` reads. Changing them at runtime needs a patching library (Harmony/MonoMod). The project currently ships without Harmony: no official assembly references it and none is installed. Choosing between bundling `0Harmony.dll`, depending on the Workshop Bannerlord.Harmony module, or rejecting the periodic product requirement is a distribution decision.

## Riskiest unknowns to prove in-game

1. Rebasing mid-path causes no stall or oscillation, including for armies, embark/disembark, siege and raid.
2. The merged island IDs are accepted.
3. The cost of stitched path queries and of building the cache at load.
4. Seamless visual and camera rebase (terrain, flora, water, snow/rain, atmosphere).
5. Settlement entities can be duplicated or moved.
6. Weather and storm wrapping doesn't throw.
7. Save/load keeps positions canonical and rebuilds the locator.
8. The remaining planar heuristics (121 methods) cause only cosmetic or scoring errors.
