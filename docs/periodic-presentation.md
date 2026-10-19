# Wrapped camera, render copies and map interaction (MMR-66)

The campaign map shows one canonical world. Its camera, the visuals drawn for campaign objects, their labels and the player's selection all continue across the east/west cut. Gameplay is canonical and is MMR-63..65's (`docs/periodic-coordinates.md`, `docs/periodic-movement.md`). This layer is presentation only and moves no campaign object.

| Piece | File |
|---|---|
| Camera | `UI/PeriodicMapCameraView.cs` |
| Display rule, visual positions, decals, nameplates, settlement display copies | `World/PeriodicMapPresentation.cs` |
| Patch set (applied with the periodic patches; all or nothing) | `CampaignBootstrap/PeriodicPatches.cs` |
| In-game check | `Diagnostics/PeriodicPresentationProbe.cs` (`generated_campaign` with `presentationProbe=1`, after `wrapProbe=1`, on the MMR-63 fixture). On a generated world: the `seam_presentation` probe (`Automation/SeamPresentationProbe.cs`, `save=`) loads a save and runs every section except the wrap probe's mission. Its seam towns are the pair joined by land across the seam that lies nearest it, and it runs on a campaign the game ticks itself (`LoadedCampaignTicker`). The copies check counts the settlements whose strip image lies inside the terrain |
| Evidence | `evidence/world/periodic-camera-picking.json` (`tools/summarize_periodic_presentation.py`), `evidence/fwg/periodic-presentation-review.md` |

SandBox.View, SandBox.ViewModelCollection and NavalDLC.View are not referenced at compile time. Their types are reached by name and patched with Harmony in the periodic set.
- A missing required target refuses the whole set, so a periodic campaign does not start, as for every periodic patch.
- Every transpiler must replace exactly the audited sites, or a site count stated in the code.
- The War Sails views are optional: absent without War Sails.
- With War Sails, the set holds 53 entries: MMR-63..65's 20 and 33 presentation entries. The 33rd (2026-10-19) is the settlement hover and selection prefix below.

## The camera

**One canonical camera.**
- `MapCameraView`'s target stays in the canonical range.
- When the target leaves it, every camera field holding a world position moves by whole widths: `_cameraTarget`, `IdealCameraTarget`, `_lastUsedIdealCameraTarget`, `_cameraAnimationTarget` and `_cameraFrame`'s origin. MapScreen's drag pivot (`_clickedPosition`) moves with them.
- The target can leave the range through free panning, axis input, a drag, a follow, or a move that ran into a strip.
- The rebase runs in a prefix of `UpdateMapCamera`, the method that writes the native camera frame and the sound listener. Both are therefore computed from the rebased fields in the same frame: the rendered camera never lags a rebase.
- The strips beyond the cut hold the terrain's twins, and every visual is drawn at its image nearest the camera, so the view does not change.
- The camera's map-border clamp (`ComputeMapCamera`, `Campaign.MapMinimumPosition` and `MapMaximumPosition` = [0, Extent]) is never reached, because the canonical range lies half a width inside the terrain on each side.

**Targets the short way.** A target the camera is sent to is taken at its image nearest the camera. This covers:
- a followed party (`GetCameraTargetForParty`);
- a position (`GetCameraTargetForPosition`, behind `MapScreen.FastMoveCameraToPosition`, which the map trackers, clan and kingdom screens use to show a position on the map);
- an animation (`StartCameraAnimation`, and the `AsVec3` calls of `OnBeforeTick`'s animation branch);
- follow mode's own target, built in `GetMapCameraInput` from the followed party, its settlement and port, a siege camp or a map event.

Averages of a settlement and its port (and the besieger) are formed with every term at its image beside the settlement. The besieger-camp lerp of `GetCameraTargetForParty` takes the camp frame at its image beside the target. So a settlement whose port or camp lies across the cut is averaged where it stands. An input-driven ideal target already lies beside the camera and is unchanged.

**The followed party's rebase.** When `PeriodicSeam` rebases the main party, the camera does not move. It follows the party's image nearest it (the twin beside it) and rebases itself when its target crosses the cut. This replaces MMR-63's camera shift on the party's rebase, whose field heuristic missed `_lastUsedIdealCameraTarget`.

**Height, zoom and the poles.**
- Camera height, zoom and bearing are untouched.
- North and south do not wrap: the rebase acts on x only, and the camera's y clamp is the engine's.

## What is drawn where

**The display rule.** Every visual that follows a campaign object is drawn at the object's image nearest the camera target (`PeriodicMapPresentation.Display`):
- **Parties and ships** (postfixes on `GetVisualPosition`): their frame is set from the visual position on every visible tick.
- **Tracks** (`MapTracksVisualManager.CalculateTrackFrame`) and **anchors** (`AnchorVisual.GetVisualPosition`): the engine frames these only when they change, so they are also refreshed when the camera wraps or has moved more than 50 units (`RefreshImages`: the tracks are marked dirty, the anchor cache cleared).
- **Storms** (`StormVisual.VisualTick`'s position).
- **Selection and hover decals** (`MapScreen.TickCircles`: the move target, a targeted party, the town circle and hover outlines, the village outline), **blockade arcs and visuals** (`BlockadeVisualHelper`), **battle facings** (`GetMapEventVisualRotation`), **anchor frames** (`AnchorVisual.CalculateAnchorFrame`) and **quest arrows** (`QuestHelper.AddMapArrowFromPointToTarget`). These take their positions at the same images and their directions as periodic differences (through MMR-65's position gate).

A visual's position is presentation only. Gameplay reads `MobileParty.Position` and `Settlement.Position`, which stay canonical.

**Settlements have display copies.** Settlement entities are static scene entities that gameplay reads (siege camp frames), so they are never moved.
- Each settlement gets one display copy at its strip image. The strips are half a width wide, so every canonical x has exactly one strip image.
- The copy is a clone of the settlement's entity (`GameEntity.CopyFrom` with physics and without script callbacks), renamed `<settlement>_display_<east|west>`. It is built when the settlement's visual starts up.
- The native copy leaves out children added at run time. An MMR-34 hideout is built as live entities (`SettlementEntityRuntime`), and its icon child was missing from all 115 hideout copies on seed 73's world (run 29576). The copy's hierarchy is now walked together with the settlement's by child name: each missing child is copied on its own under its mirror parent at the child's local frame (`DisplayCopy.AddedChildren`), and the entity pairs come from that walk.
- The clone carries no tags, so scene-wide tag queries (ports, drop-offs, siege spawns) find only the settlement's entities. Its script components stay, inert: no script callback ever runs on them.
- Every entity of the copy is registered in `MapScreen.VisualsOfEntities` to the settlement's own visual. A click, hover or tooltip on the copy (resolved by `MapScreen.HandleMouse`'s own path) is therefore the settlement itself, with the canonical identity and the canonical interaction position.
- The copy mirrors the visibility of the settlement's entities whenever the settlement visual changes them: `SetSettlementLevelVisibility`, `RefreshWallState`, `RefreshSiegePreparations` and `ValidateIsDirty`.
- It is removed, with its registrations, when the visual's party is removed (`OnPartyRemoved`).
- A fixture's static copy of the same settlement (`tools/scenebin/build_periodic_world.py`) is removed and replaced by the runtime copy.
- No campaign object is created: the copy has no settlement, party or id.

**Hover and selection over overlapping settlements.** `SettlementVisualManager.OnVisualIntersected` walks the collided entities from the last to the first: the first settlement it meets is hovered, the last selected. The native list follows entity creation, not the ray. Display copies are created after the settlements, so along one ray a copy and its settlement could resolve to different settlements where two overlap (a town and its village side by side). On seed 73's world, run 9524, the copy resolved to the village and the settlement to the town. A prefix (periodic worlds only) now moves the entries of every visible settlement but the one the ray meets first behind the count that manager reads, so hover and selection are both the nearest settlement. The arrays keep every entry for the other visual components, which read their own count.

**Labels.** Party and settlement nameplates are placed at the same images:
- `PartyNameplateVM.RefreshPosition` and `PartyPlayerNameplateVM.RefreshPosition` take the position's `AsVec3` there.
- `SettlementNameplateVM.CalculatePosition` takes its `_worldPos` there.
- `SettlementNameplateVM.IsVisible` also measures the camera distance from that image, so a label's visibility, zoom culling and distance sorting all follow it.

Each object therefore has one label, beside the camera, and a copy never has a label of its own.

**Water, terrain and overlays.** The strips' terrain, faces, water and flow are the canonical twins by construction (MMR-63/39), so they align with canonical queries without a copy. Weather sampling is canonical (MMR-39).

## Picking

**Terrain.**
- `MapScreen.GetCursorIntersectionPoint` is a native ray against the scene, so a click in a strip hits the strip's twin terrain and face.
- `MapScreen.HandleLeftMouseButtonClick` checks the path between that face and the party's face natively (the navmesh is continuous through the cut), then orders `MapState.ProcessTravel`. MMR-63's prefix stores the canonical image of the point.
- The move-target decal is drawn at the point's image beside the camera.

**Objects.**
- A party is drawn where its single entity is, so `Scene.SelectEntitiesCollidedWith` finds it.
- A settlement's copy resolves through `VisualsOfEntities` to the settlement's visual, in `OnVisualIntersected`, gated by the canonical visual's visibility.
- The click then uses that visual's canonical interaction position, so the order targets the canonical settlement.

## In game

Run 33944 (module `11737f38…`, the MMR-63 fixture, `wrapProbe=1 presentationProbe=1`; `evidence/world/periodic-camera-picking.json`) passed every section with all 52 patch entries applied and the MMR-63 checks rerun.
- **copies:** One copy per settlement at its strip image, for example `town_1` (canonical x 1134.4) drawn at 334.4, `town_2` (409.4) drawn at 1209.4. Each replaced the fixture's static copy. Their entities (19 per town, 6 per village) resolve to the settlement. There are 4 settlements with unique ids, and no campaign object is named as a display copy.
- **follow:** The player crossed both ways (73–74 periodic units each way) with the camera following:
  - one party rebase and one camera rebase per leg;
  - largest camera step 0.13 (0.06 beyond the rebase);
  - camera within 0.38 of the party's drawn entity;
  - native camera 0 off the camera frame;
  - camera target canonical at every sample.
- **pan:** The paused free camera passed through the seam both ways by its ideal target:
  - each pass took 59 units, 1 rebase and a largest step of 0.92;
  - a lord at x 402 was drawn at 1202 beside the camera east of the seam, and at 402 west of it;
  - `town_2`'s nameplate was bound visible and on screen from both sides, its world position at 1209.4 or 409.4.

  Injected axis input (`RX`) moved the camera 20 units through the seam, with 1 rebase and a largest step of 0.30. The native camera was 0 off the frame in every pass.
- **moves:** `FastMoveCameraToPosition` to `town_2` from beside the east edge travelled 13.4, the short way (periodic distance 14.3). `TeleportCameraToMainParty` and `ResetCamera` landed exactly on the party.
- **picking:** A native ray into the east strip hit the terrain at (1204.0, 1115.9) on a valid face. The click ordered the canonical point (404.0, 1115.9), routed 11.8 units across the seam, and drew the move decal at the hit point. A ray at `town_2`'s east copy hit `town_2_selection`, resolved through `SelectEntitiesCollidedWith` and `OnVisualIntersected` to `town_2`, and its click ordered `GoToSettlement` to the canonical town.
- **tracks:** Tracks laid at x 1197 and 1194 were drawn there with the camera east of the seam, and at 397 and 394 after the camera crossed.
- **alignment:**
  - Heights and face types at 12 strip twins (beside the seam and at both ports) equal the canonical ones.
  - Every copy entity's visibility equals its settlement's (0 differences, after 5–13 sync passes).
- **menu:** The player entered `town_2` across the seam and left by `town_leave`. The party stayed canonical at (409.4, 1121.9), and the camera returned to 0.05 from it, following.
- **mission:** After the wrap probe's field battle at the seam and its return, the party was canonical and the camera was 0.0003 from its drawn entity, following.
- **budget:** 20 camera wraps and two party crossings left the copies (4), their registrations (50), the visual registry (121) and the scene's root entities (370) unchanged. Managed memory grew by 83 KB, and private bytes fell by 122 MB (recorded, not judged).

Earlier runs are kept as history:
- 31860 is the pre-review build: follow failed because the camera target was outside the canonical range for 2 samples per leg.
- 23080 did not start: the ambiguous `GetComponents` lookup in the camera prefix.
- 5880 failed in copies, picking and tracks for the same lookup.

The fixes are recorded in `evidence/fwg/periodic-presentation-review.md`.

## Limits

- **Not opened in game:** the encyclopedia, clan and kingdom screens. Their map navigation was not driven; `FastMoveCameraToPosition` was called directly.
- **Not on the fixture:** blockades, storms and anchors at the seam, roads and faction-region overlays. Their display images are code paths without in-game samples. The fixture's terrain materials in the strips are twins by construction.
- **Mouse dragging is not exercised in game.** It pivots on `MapScreen._clickedPosition`, which the rebase moves. Axis input (the `InputInformation.RX`/`RY` fields that controller and keyboard panning feed) is exercised by injection.
- **Very wide views.** A view wider than half the width would need a second image of the same object; the display rule draws each object once, at its nearest image.
- **Copies are not fully live.**
  - Siege machines, siege missiles and banners exist only on the settlement's own entity: a siege at the cut, seen from the copy's side, shows the walls and levels but not the machines.
  - Engine clicks (`FrameAndVisualOfEngines`) work on the settlement's side.
- **Static framing.** Blockade visuals take their image when they are initialized.
- **Not fixture-tested.** A settlement whose port or siege camp straddles the cut is averaged beside the settlement by construction, but the MMR-63 fixture has none (its ports lie on their town's side).
- **User-interface lists measure periodically** (2026-10-19). The lists sorted or described by distance (the army overlay, the game-menu party list, the clan screen's sorts) and the nearby culture's map music are in the spatial table (`docs/periodic-movement.md`, `VIEW_PERIODIC`). Map step sounds already did: they search through the patched `MobileParty.FindNextLocatable` and sound at the party's visual, which takes the image nearest the camera. Before, these were planar near the cut.
- **The hero behaviour text stays planar.** `CampaignUIHelper.GetHeroBehaviorText` cannot be patched when the periodic set is applied: that runs its class initializer before the campaign's game texts exist, and the failed class breaks character creation (one-shot run, 2026-10-19). Near the cut, the text naming where a hero is can pick the wrong nearby settlement.
- **No memory limit.** MMR-21 recorded no visible-copy memory limit. The budget here is no growth across wraps, with memory recorded rather than judged.
