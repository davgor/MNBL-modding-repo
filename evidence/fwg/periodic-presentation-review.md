# MMR-66 independent review (wrapped camera, render copies and map interaction)

**The review.** One independent review of the implementation, by an agent that:
- read the code;
- read the installed 1.4.8 / NavalDLC 1.2.8 view assemblies through Mono.Cecil IL dumps;
- neither edited the repository nor started the game.

Every finding was fixed in one batch. The confirming runs are recorded in `evidence/world/periodic-camera-picking.json`.

**Before the review.** Dev runs had found two follow defects:
- The follow target is built in `GetMapCameraInput`, not through `GetCameraTargetForParty`, so after a crossing the camera flew the long way across the map. Fixed by the ideal-target postfix.
- MMR-63's camera shift on the party's rebase pushed the camera out of the canonical range. It was removed: the camera follows the party's nearest image and rebases itself.

| # | Severity | Finding | Resolution |
|---|---|---|---|
| 1 | blocker | `SettlementNameplateVM.IsVisible` measured the camera distance from the canonical `_worldPos`: a settlement across the cut was hidden at normal zoom (villages at every zoom), sorted wrong, and the probe could not see it. | `IsVisible`'s `_worldPos` address load is replaced by a local holding the display position (and `CalculatePosition`'s load as before). The probe asserts the nameplate's bound visibility and on-screen position. |
| 2 | major | The rebase ran in `OnBeforeTick`'s postfix, after `UpdateMapCamera` had written the native camera frame and sound listener: one rendered frame per crossing W off. | The rebase runs in a prefix of `UpdateMapCamera` (which `OnActivate` also calls). The probe compares the native camera with the camera frame on every follow, pan and axis-input sample. |
| 3 | major (latent) | Averaged follow targets (settlement + port (+ besieger) in `GetMapCameraInput`; the besieger camp lerp in `GetCameraTargetForParty`) used planar sums: a port or camp across the cut put the target mid-world. | The two additions after `PortPosition` add each term at its image beside the settlement; the lerp takes the camp frame beside the target. Exact site counts are checked. The fixture has no straddling settlement (documented). |
| 4 | major | `MapScreen.TickCircles` drew selection and hover decals at canonical positions. | `TickCircles` is transpiled: `AsVec3`, `ToVec3`, `CircleLocalFrame` and `GetVisualPosition` take their display images. The probe checks the move-target decal after a click in the strip. |
| 5 | major | Track entities are framed only when tracks are detected or lost: after the camera crossed they stayed on the old image. | When the camera wraps or has moved more than 50 units, the track manager is marked dirty (and the anchor cache cleared): `RefreshImages`. The probe lays and detects tracks at the seam and checks their entities after a camera crossing. |
| 6 | major | Display copies were snapshots taken before the first level refresh and never followed level, wall, siege preparation or discovery changes. | The copy's entities mirror the settlement's entity visibility after `SetSettlementLevelVisibility`, `RefreshWallState`, `RefreshSiegePreparations` and `ValidateIsDirty`. The probe checks visibility parity. Siege machines and missiles are not copied (documented). |
| 7 | major | War Sails visuals: storms are positioned directly, anchors framed only on change, blockade helpers used planar subtraction. | `StormVisual.VisualTick`'s position takes its image; anchors are refreshed with tracks; `BlockadeVisualHelper`'s positions take their images and its differences are periodic. Blockade visuals take their image at initialization (documented). |
| 8 | major (probe) | Tautological camera checks (measured against `Nearest`), no native camera, panning only through the ideal target, picking outside the real resolution path, untested items. | The camera is measured against the party's drawn entity; the native camera is checked; axis input (`RX`/`RY`) is injected through a probe-owned Harmony prefix; copy picks go through `Scene.SelectEntitiesCollidedWith` and each component's `OnVisualIntersected`. New tracks and alignment sections (strip twins at the seam and both ports; copy parity). The summarizer checks a fixed patch count. Mouse drag stays untested (documented). |
| 9 | minor | Copy lifetime: `OnPartyRemoved` left the copy and its registrations. | Postfix on `SettlementVisual.OnPartyRemoved` removes the copy and unregisters it. |
| 10 | minor | Copies duplicated tagged children (ports, drop-offs), which scene-wide tag scans would find. | Tags are removed from every copy entity. Script components stay inert, because removing them would call removal callbacks on uninitialised scripts (documented). |
| 11 | minor | The drag collision pivot (`MapScreen._clickedPosition`) was not rebased. | Moved with the camera fields. |
| 12 | minor | Other planar view code (battle facing, anchor frame, quest arrows, list sorting, step sounds). | Battle facings, anchor frames and quest arrows use periodic differences; list sorting, texts and step sounds documented as limits. |
| 13 | nit | `Nearest` could return a position W away; per-frame reflection boxing; dead `RebaseEvent.Camera`/`CameraFields`. | `Nearest` takes the in-terrain image next to the nearest; field access through `AccessTools.FieldRefAccess`; dead fields removed. |
| 14 | nit | Docs claims not backed by code. | Rewritten against the fixed code. |

## Found in the confirming runs

| Run | Symptom | Cause | Fix |
|---|---|---|---|
| 23080 | the campaign did not start (`AmbiguousMatchException` in `MapScreen.OnActivate`) | `RefreshImages` looked up `GetComponents` by name, which has a generic overload; it ran inside the camera's prefix, so the exception escaped into the engine | the non-generic method is selected explicitly, and `RefreshImages` never throws (a failure is recorded) |
| 5880 | the copies section reported refresh errors; picking and tracks raised the same exception | `Type.EmptyTypes` still matched the parameterless generic `GetComponents<T>()` | explicit selection of the non-generic, parameterless method in the module and the probe |
