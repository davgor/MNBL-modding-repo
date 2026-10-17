using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MNBL.ProceduralWorld.Automation;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Surface;
using MNBL.ProceduralWorld.UI;
using MNBL.ProceduralWorld.World;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.Diagnostics
{
    // MMR-66: the map presentation across the periodic seam (docs/periodic-presentation.md), on the MMR-63 fixture after the movement probe.
    // Each section is fail-soft (its error is recorded and the next one runs):
    //   copies    the presentation patches; one display copy per settlement at its strip image, its entities registered to the settlement;
    //   follow    the camera following the player across the seam both ways: on the party's drawn entity, no map-width step, the native
    //             camera (what is rendered) on the camera's frame at every sample;
    //   pan       the paused free camera moved across the seam and back, by its ideal target and by injected axis input (the RX/RY
    //             fields controller and keyboard panning feed): continuous, rebased by whole widths, the native camera with it; a lord and a
    //             town across the seam drawn beside the camera, their nameplates on screen;
    //   moves     a fast move to a town across the seam (MapScreen.FastMoveCameraToPosition) takes the short way; teleport and reset land
    //             on the party;
    //   picking   a native ray from the camera into the strip hits the terrain there and its click (MapScreen.HandleLeftMouseButtonClick)
    //             orders the canonical point the short way, its target decal beside the camera; a ray at a town's display copy, resolved
    //             as MapScreen.HandleMouse does (Scene.SelectEntitiesCollidedWith, then each visual component's OnVisualIntersected), is the
    //             town's own visual (the hover, tooltip and click target), and its click targets the canonical town;
    //   tracks    tracks laid at the seam and detected are drawn beside the camera after the camera crosses;
    //   alignment terrain heights and face types at the strip twins equal the canonical ones (beside the seam and at both ports); every
    //             display copy's entity visibility equals its settlement's;
    //   menu      entering a town across the seam and leaving it keeps the party canonical and the camera on it;
    //   mission   the camera after the wrap probe's seam battle and return (recorded there);
    //   budget    20 camera wraps and two party crossings leave the copies, the visual registrations and the scene's root entities
    //             unchanged; managed memory is recorded.
    // Mouse dragging is not injected (it pivots on MapScreen's clicked position, which the camera rebase moves).
    internal static class PeriodicPresentationProbe
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static IEnumerator Run(ProbeContext context, DiagnosticSandboxBootstrap boot, string worldId, Dictionary<string, object> wrapReport, Dictionary<string, object> report)
        {
            var failures = new List<string>(); report["failures"] = failures;
            var world = PeriodicWorld.Current;
            var town1 = Settlement.Find(worldId + "_town_1"); var town2 = Settlement.Find(worldId + "_town_2");
            if (world == null || town1 == null || town2 == null) { failures.Add("No periodic world or seam towns."); yield break; }
            double start = context.Seconds;
            while (PeriodicMapCameraView.Current == null && context.Seconds - start < 10) yield return null;
            if (PeriodicMapCameraView.Current == null) { failures.Add("No map camera."); yield break; }
            report["towns"] = new Dictionary<string, object> { ["east"] = Xy(town1.Position), ["west"] = Xy(town2.Position) };
            var before = Budget();
            foreach (var name in new[] { "copies", "follow", "pan", "moves", "picking", "tracks", "alignment", "menu", "mission", "budget" })
            {
                context.Stage("presentation_section:" + name);
                var row = new Dictionary<string, object>(); report[name] = row;
                var rowFailures = new List<string>(); row["failures"] = rowFailures;
                IEnumerator steps = name == "copies" ? Once(() => Copies(world, row)) : name == "follow" ? Follow(context, boot, world, town1, town2, row)
                    : name == "pan" ? Pan(context, boot, world, town1, town2, row) : name == "moves" ? Moves(context, boot, world, town2, row)
                    : name == "picking" ? Picking(context, boot, world, town2, row) : name == "menu" ? Menu(context, boot, world, town2, row)
                    : name == "tracks" ? Tracks(context, boot, world, town1, row) : name == "alignment" ? Once(() => Alignment(world, town1, town2, row))
                    : name == "mission" ? Once(() => Mission(wrapReport, row)) : Wraps(context, boot, world, town1, town2, before, row);
                Exception error = null;
                while (true)
                {
                    try { if (!steps.MoveNext()) break; } catch (Exception e) { error = e; break; }
                    yield return steps.Current;
                }
                if (error != null) { row["error"] = error.ToString(); failures.Add(name + ": " + error.Message); }
                failures.AddRange(rowFailures.Select(f => name + ": " + f));
                try { DiagnosticSandboxBootstrap.Pause(Campaign.Current); boot.HoldPaused = true; } catch (Exception) { }
                context.Stage("presentation_section_end:" + name + ":" + rowFailures.Count + (error == null ? "" : ":error"));
                context.Checkpoint();
            }
            report["camera"] = new Dictionary<string, object> { ["rebases"] = PeriodicMapCameraView.Rebases, ["events"] = PeriodicMapCameraView.Events.Take(40).ToList(),
                ["nameplateCalls"] = PeriodicMapPresentation.NameplateCalls, ["imageRefreshes"] = PeriodicMapPresentation.Refreshes };
        }

        private static IEnumerator Once(Action action) { action(); yield break; }

        private static float[] Xy(CampaignVec2 p) { return new[] { p.X, p.Y }; }
        private static float[] Xy(Vec3 p) { return new[] { p.x, p.y }; }

        // ---- copies ----
        private static void Copies(PeriodicWorld world, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"];
            row["patches"] = PeriodicPatches.Applied.Where(a => a["target"].ToString().Contains(".View.") || a["target"].ToString().Contains("ViewModelCollection")).Select(a => a["target"] + " (" + a["kind"] + ")").ToList();
            var registry = VisualsOfEntities();
            var copies = new List<object>(); row["copies"] = copies;
            foreach (var c in PeriodicMapPresentation.Copies)
            {
                var at = c.Copy.GlobalPosition;
                object visual = registry != null && registry.Contains(c.Copy.Pointer) ? registry[c.Copy.Pointer] : null;
                var settlement = visual == null ? null : SettlementOf(visual);
                copies.Add(new Dictionary<string, object> { ["settlement"] = c.Settlement, ["entity"] = c.Entity, ["canonicalX"] = c.CanonicalX, ["imageX"] = c.ImageX, ["entityX"] = at.x,
                    ["registeredEntities"] = c.Registered.Count, ["resolvesTo"] = settlement == null ? null : settlement.StringId, ["replacedFixtureEntity"] = c.ReplacedFixtureEntity });
                if (Math.Abs(Math.Abs(c.ImageX - c.CanonicalX) - world.Width) > 0.01f || Math.Abs(at.x - c.ImageX) > 0.01f) failures.Add(c.Settlement + ": the copy is not at the strip image.");
                if (settlement == null || settlement.StringId != c.Settlement) failures.Add(c.Settlement + ": the copy's entity does not resolve to the settlement.");
            }
            var missing = Settlement.All.Where(s => s.Party != null && PeriodicMapPresentation.Copies.All(c => c.Settlement != s.StringId)).Select(s => s.StringId).ToList();
            row["settlementsWithoutCopy"] = missing; row["copyErrors"] = PeriodicMapPresentation.CopyErrors.ToList();
            row["identities"] = new Dictionary<string, object> { ["settlements"] = Settlement.All.Count, ["settlementIdsUnique"] = Settlement.All.Select(s => s.StringId).Distinct().Count() == Settlement.All.Count,
                ["campaignObjectsNamedDisplay"] = Settlement.All.Count(s => s.StringId.Contains("_display_")) + MobileParty.All.Count(p => p.StringId.Contains("_display_")) };
            if (missing.Count > 0) failures.Add("Settlements without a display copy: " + String.Join(", ", missing));
            if (PeriodicMapPresentation.CopyErrors.Count > 0) failures.Add("Copy errors: " + String.Join("; ", PeriodicMapPresentation.CopyErrors));
            if (Settlement.All.Count(s => s.StringId.Contains("_display_")) + MobileParty.All.Count(p => p.StringId.Contains("_display_")) > 0) failures.Add("A display copy has a campaign identity.");
        }

        // ---- follow ----
        private static IEnumerator Follow(ProbeContext context, DiagnosticSandboxBootstrap boot, PeriodicWorld world, Settlement town1, Settlement town2, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"];
            var main = MobileParty.MainParty;
            if (main.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(main);
            if (main.IsCurrentlyAtSea) { failures.Add("The player is at sea."); yield break; }
            main.Position = town1.GatePosition; main.SetMoveModeHold();
            PeriodicMapCameraView.Call("TeleportCameraToMainParty");
            var legs = new List<object>(); row["legs"] = legs;
            foreach (var leg in new[] { Tuple.Create("east town -> west town", town2), Tuple.Create("west town -> east town", town1) })
            {
                var record = new Dictionary<string, object> { ["leg"] = "player " + leg.Item1 + " (camera following)", ["party"] = main.StringId }; legs.Add(record);
                var settled = SettleOnParty(context);
                while (settled.MoveNext()) yield return settled.Current;
                record["settledBeforeLeg"] = Xy(PeriodicMapCameraView.Target());
                main.SetMoveGoToPoint(leg.Item2.GatePosition, MobileParty.NavigationType.Default);
                float maxDistance = 0, maxStep = 0, maxRebaseResidual = 0, maxNative = 0; int samples = 0, nonCanonical = 0, noEntity = 0, rebasesBefore = PeriodicMapCameraView.Rebases;
                Vec3 last = PeriodicMapCameraView.Target(); double totalShift = PeriodicMapCameraView.TotalShift;
                var target = leg.Item2.GatePosition.ToVec2();
                var samplesTrack = new List<float[]>(); record["cameraTrack"] = samplesTrack;
                PeriodicMapCameraView.Trace.Clear(); PeriodicMapCameraView.Tracing = true;
                var steps = WorldWrapProbe.Travel(context, boot, world, main, () =>
                {
                    var t = PeriodicMapCameraView.Target();
                    float shift = (float)(PeriodicMapCameraView.TotalShift - totalShift); totalShift = PeriodicMapCameraView.TotalShift;
                    float step = new Vec2(t.x - shift - last.x, t.y - last.y).Length;
                    if (shift != 0) maxRebaseResidual = Math.Max(maxRebaseResidual, step); else maxStep = Math.Max(maxStep, step);
                    last = t;
                    var drawn = PeriodicMapPresentation.DrawnPosition(main);
                    float px = drawn == null ? float.NaN : drawn.Value.x;
                    if (drawn == null) noEntity++;
                    float d = drawn == null ? 0 : new Vec2(t.x - drawn.Value.x, t.y - drawn.Value.y).Length;
                    maxDistance = Math.Max(maxDistance, d);
                    maxNative = Math.Max(maxNative, NativeLag());
                    if (!world.IsCanonical(t.x)) nonCanonical++;
                    if (samples % 10 == 0 && samplesTrack.Count < 400) samplesTrack.Add(new[] { t.x, t.y, main.Position.X, px, d });
                    samples++;
                    return world.Distance(main.Position.ToVec2(), target) < 1.5f;
                }, 90, record);
                while (steps.MoveNext()) yield return steps.Current;
                PeriodicMapCameraView.Tracing = false;
                if (maxDistance > 40) record["cameraTrace"] = PeriodicMapCameraView.Trace.Where((r, i) => i % 4 == 0).Take(1500).ToList();
                record["cameraMaxDistanceToPartyDrawn"] = maxDistance; record["cameraMaxStep"] = maxStep; record["cameraStepBeyondRebases"] = maxRebaseResidual;
                record["cameraRebases"] = PeriodicMapCameraView.Rebases - rebasesBefore; record["samples"] = samples; record["nativeCameraMaxOffFrame"] = maxNative; record["samplesWithoutPartyEntity"] = noEntity; record["cameraTargetNonCanonicalSamples"] = nonCanonical;
                record["followMode"] = PeriodicMapCameraView.FollowMode();
                if (!(bool)record["arrived"]) failures.Add(leg.Item1 + ": the player did not arrive.");
                if ((int)record["rebases"] < 1) failures.Add(leg.Item1 + ": the player never crossed the seam.");
                if (maxDistance > 40) failures.Add(leg.Item1 + ": the camera was " + maxDistance + " from the party's drawn entity.");
                if (noEntity > 0) failures.Add(leg.Item1 + ": the party had no drawn entity for " + noEntity + " samples.");
                if (maxNative > 0.5f) failures.Add(leg.Item1 + ": the native camera was " + maxNative + " off the camera frame.");
                if (maxStep > 5 || maxRebaseResidual > 5) failures.Add(leg.Item1 + ": the camera target jumped (" + maxStep + ", " + maxRebaseResidual + " beyond rebases).");
                if ((int)record["cameraRebases"] < 1) failures.Add(leg.Item1 + ": the camera never crossed the cut with the party.");
                if (nonCanonical > 0) failures.Add(leg.Item1 + ": the camera target was outside the canonical range for " + nonCanonical + " samples.");
            }
        }

        // ---- pan ----
        private static IEnumerator Pan(ProbeContext context, DiagnosticSandboxBootstrap boot, PeriodicWorld world, Settlement town1, Settlement town2, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"];
            var main = MobileParty.MainParty;
            float y = WorldWrapProbe.LandRow(town1.Position.Y, world.MaxX - 30, world.MaxX - 2, world.OriginX + 2, world.OriginX + 30);
            if (float.IsNaN(y)) { failures.Add("No land row through the seam."); yield break; }
            // A lord held just west of the seam, the player just east of it so the lord is in sight (synthetic placement).
            var lord = MobileParty.All.Where(p => p.IsActive && p.IsLordParty && p != main && p.MapEvent == null && p.Army == null && p.AttachedTo == null && p.CurrentSettlement == null && !p.IsCurrentlyAtSea)
                .OrderBy(p => p.StringId, StringComparer.Ordinal).FirstOrDefault();
            if (lord == null) { failures.Add("No free lord on land."); yield break; }
            lord.Ai.SetDoNotMakeNewDecisions(true);
            try
            {
            lord.Position = new CampaignVec2(new Vec2(world.OriginX + 2, y), true); lord.SetMoveModeHold();
            main.Position = new CampaignVec2(new Vec2(world.MaxX - 2, y), true); main.SetMoveModeHold();
            var settle = RunFor(context, boot, 0.5);   // a short tick refreshes visibility at the new places
            while (settle.MoveNext()) yield return settle.Current;
            row["setup"] = new Dictionary<string, object> { ["lord"] = lord.StringId, ["lordAt"] = Xy(lord.Position), ["playerAt"] = Xy(main.Position), ["lordVisible"] = lord.IsVisible, ["row"] = y };
            PeriodicMapCameraView.SetFollowMode("Free");
            var passes = new List<object>(); row["passes"] = passes;
            foreach (var pass in new[] { Tuple.Create("east -> west through the seam", world.MaxX - 30, 2f), Tuple.Create("west -> east through the seam", world.OriginX + 30, -2f) })
            {
                var record = new Dictionary<string, object> { ["pass"] = pass.Item1 }; passes.Add(record);
                var teleport = CameraTo(context, new Vec3(pass.Item2, y, PeriodicMapCameraView.Target().z));
                while (teleport.MoveNext()) yield return teleport.Current;
                int rebasesBefore = PeriodicMapCameraView.Rebases; double totalShift = PeriodicMapCameraView.TotalShift;
                float travelled = 0, maxStep = 0, maxNative = 0; Vec3 last = PeriodicMapCameraView.Target();
                Dictionary<string, object> nearSeamWest = null, nearSeamEast = null;
                // 60 units of logical motion at 2 per frame (the ideal target advanced as panning input advances it).
                for (int frame = 0; frame < 60 / 2 + 40; frame++)
                {
                    if (frame < 30) { var ideal = PeriodicMapCameraView.Ideal(); ideal.x += pass.Item3; PeriodicMapCameraView.SetIdeal(ideal); }
                    yield return null;
                    var t = PeriodicMapCameraView.Target();
                    float shift = (float)(PeriodicMapCameraView.TotalShift - totalShift); totalShift = PeriodicMapCameraView.TotalShift;
                    float step = Math.Abs(t.x - shift - last.x); travelled += step; maxStep = Math.Max(maxStep, step); last = t;
                    maxNative = Math.Max(maxNative, NativeLag());
                    // Beside the seam on either side, the lord across it must be drawn and labelled beside the camera.
                    if (nearSeamEast == null && world.MaxX - t.x < 6 && world.MaxX - t.x > 2) nearSeamEast = Beside(world, t, lord, town2);
                    if (nearSeamWest == null && t.x - world.OriginX < 6 && t.x - world.OriginX > 2) nearSeamWest = Beside(world, t, lord, town2);
                }
                record["cameraRebases"] = PeriodicMapCameraView.Rebases - rebasesBefore; record["travelled"] = travelled; record["maxStep"] = maxStep;
                record["end"] = Xy(PeriodicMapCameraView.Target()); record["besideSeamEast"] = nearSeamEast; record["besideSeamWest"] = nearSeamWest; record["nativeCameraMaxOffFrame"] = maxNative;
                if (maxNative > 0.5f) failures.Add(pass.Item1 + ": the native camera was " + maxNative + " off the camera frame.");
                float expectedEnd = world.Canonical(pass.Item2 + pass.Item3 * 30);
                if (Math.Abs(world.DeltaX(expectedEnd, PeriodicMapCameraView.Target().x)) > 2) failures.Add(pass.Item1 + ": the camera ended at " + PeriodicMapCameraView.Target().x + ", not " + expectedEnd + ".");
                if (PeriodicMapCameraView.Rebases - rebasesBefore < 1) failures.Add(pass.Item1 + ": the camera never rebased.");
                if (maxStep > 3 || travelled > 75) failures.Add(pass.Item1 + ": the camera did not move continuously (largest step " + maxStep + ", " + travelled + " travelled for 60).");
                foreach (var side in new[] { Tuple.Create("east of the seam", nearSeamEast), Tuple.Create("west of the seam", nearSeamWest) })
                {
                    if (side.Item2 == null) { failures.Add(pass.Item1 + ": no camera sample " + side.Item1 + "."); continue; }
                    var problems = (List<string>)side.Item2["problems"];
                    failures.AddRange(problems.Select(p => pass.Item1 + ", camera " + side.Item1 + ": " + p));
                }
            }
            // Axis input: the RX/RY fields of the camera's InputInformation (controller and keyboard panning) injected for this pass.
            var axis = new Dictionary<string, object>(); row["axisInput"] = axis;
            var start = CameraTo(context, new Vec3(world.MaxX - 12, y, PeriodicMapCameraView.Target().z));
            while (start.MoveNext()) yield return start.Current;
            var injected = AxisPan(context, world, axis);
            while (injected.MoveNext()) yield return injected.Current;
            if (axis.ContainsKey("problems")) failures.AddRange(((List<string>)axis["problems"]).Select(p => "axis input: " + p));
            }
            finally { if (lord.IsActive) lord.Ai.SetDoNotMakeNewDecisions(false); try { PeriodicMapCameraView.Call("TeleportCameraToMainParty"); } catch (Exception) { } }
        }

        // ---- injected axis input ----
        private const string InputHarmonyId = "mnbl.probe.camera-input";
        private static bool injecting; private static float injectRX, injectRY;
        private static void InjectAxisPrefix(object[] __args)
        {
            if (!injecting || __args == null || __args.Length == 0 || __args[0] == null) return;
            var box = __args[0]; var type = box.GetType();
            type.GetField("RX").SetValue(box, injectRX); type.GetField("RY").SetValue(box, injectRY);
            __args[0] = box;
        }

        // Pans with injected axis input until the camera target crosses the seam eastwards (the axis and sign that move it east are found
        // first, the camera bearing deciding them), recording continuity and the native camera.
        private static IEnumerator AxisPan(ProbeContext context, PeriodicWorld world, Dictionary<string, object> axis)
        {
            var problems = new List<string>(); axis["problems"] = problems;
            var harmony = new HarmonyLib.Harmony(InputHarmonyId);
            harmony.Patch(HarmonyLib.AccessTools.Method(PeriodicMapCameraView.View, "GetMapCameraInput"), prefix: new HarmonyLib.HarmonyMethod(typeof(PeriodicPresentationProbe), nameof(InjectAxisPrefix)));
            try
            {
                // Probe each axis for 12 frames: the one that moves x most, with its sign.
                string chosen = null; float sign = 1;
                foreach (var candidate in new[] { "RX", "RY" })
                {
                    Vec3 before = PeriodicMapCameraView.Target(); double shiftBefore = PeriodicMapCameraView.TotalShift;
                    injecting = true; injectRX = candidate == "RX" ? 1 : 0; injectRY = candidate == "RY" ? 1 : 0;
                    for (int i = 0; i < 12; i++) yield return null;
                    injecting = false;
                    for (int i = 0; i < 10; i++) yield return null;
                    var after = PeriodicMapCameraView.Target();
                    float dx = after.x - (float)(PeriodicMapCameraView.TotalShift - shiftBefore) - before.x, dy = after.y - before.y;
                    axis["probe" + candidate] = new[] { dx, dy };
                    if (Math.Abs(dx) > 0.5f && Math.Abs(dx) >= Math.Abs(dy) && chosen == null) { chosen = candidate; sign = Math.Sign(dx); }
                }
                axis["axis"] = chosen; axis["sign"] = sign;
                if (chosen == null) { problems.Add("neither RX nor RY moved the camera along x."); yield break; }
                var back = CameraTo(context, new Vec3(world.MaxX - 12, PeriodicMapCameraView.Target().y, PeriodicMapCameraView.Target().z));
                while (back.MoveNext()) yield return back.Current;
                int rebasesBefore = PeriodicMapCameraView.Rebases; double totalShift = PeriodicMapCameraView.TotalShift;
                float travelled = 0, maxStep = 0, maxNative = 0; Vec3 last = PeriodicMapCameraView.Target(); int frames = 0;
                injecting = true; injectRX = chosen == "RX" ? sign : 0; injectRY = chosen == "RY" ? sign : 0;
                while (frames < 600 && (PeriodicMapCameraView.Rebases == rebasesBefore || travelled < 20))
                {
                    yield return null; frames++;
                    var t = PeriodicMapCameraView.Target();
                    float shift = (float)(PeriodicMapCameraView.TotalShift - totalShift); totalShift = PeriodicMapCameraView.TotalShift;
                    float step = new Vec2(t.x - shift - last.x, t.y - last.y).Length; travelled += step; maxStep = Math.Max(maxStep, step); last = t;
                    maxNative = Math.Max(maxNative, NativeLag());
                }
                injecting = false;
                axis["frames"] = frames; axis["travelled"] = travelled; axis["maxStep"] = maxStep; axis["cameraRebases"] = PeriodicMapCameraView.Rebases - rebasesBefore;
                axis["end"] = Xy(PeriodicMapCameraView.Target()); axis["nativeCameraMaxOffFrame"] = maxNative;
                if (PeriodicMapCameraView.Rebases == rebasesBefore) problems.Add("the camera never crossed the seam in " + frames + " frames.");
                if (maxStep > 3) problems.Add("the camera target jumped " + maxStep + ".");
                if (maxNative > 0.5f) problems.Add("the native camera was " + maxNative + " off the camera frame.");
            }
            finally { injecting = false; harmony.UnpatchAll(InputHarmonyId); }
        }

        // Planar distance between the native engine camera (rendered) and the camera's frame (computed): a frame behind after a rebase
        // shows here as about a width.
        private static float NativeLag()
        {
            var n = PeriodicMapCameraView.NativeOrigin(); var f = PeriodicMapCameraView.FrameOrigin();
            return float.IsNaN(n.x) ? 0 : new Vec2(n.x - f.x, n.y - f.y).Length;
        }

        // What the camera at t shows of the lord and the west town: the lord's drawn entity and both nameplates beside the camera.
        private static Dictionary<string, object> Beside(PeriodicWorld world, Vec3 t, MobileParty lord, Settlement town)
        {
            var problems = new List<string>();
            var lordVisual = PartyEntityPosition(lord);
            var townPlate = SettlementNameplateWorld(town);
            var lordPlate = PartyNameplateScreen(lord);
            var copy = PeriodicMapPresentation.Copies.FirstOrDefault(c => c.Settlement == town.StringId);
            float townDrawn = copy == null ? float.NaN : (Math.Abs(copy.ImageX - t.x) < Math.Abs(copy.CanonicalX - t.x) ? copy.ImageX : copy.CanonicalX);
            if (lordVisual == null) problems.Add("the lord has no drawn entity");
            else if (Math.Abs(lordVisual.Value.x - t.x) > 20) problems.Add("the lord is drawn at x " + lordVisual.Value.x + ", " + Math.Abs(lordVisual.Value.x - t.x) + " from the camera");
            var townBind = SettlementNameplateBind(town);
            if (townPlate == null) problems.Add("no nameplate for " + town.StringId);
            else if (Math.Abs(townPlate.Value.x - t.x) > 60) problems.Add(town.StringId + "'s nameplate is at x " + townPlate.Value.x);
            if (townBind == null || !(bool)townBind["visibleOnMap"] || ((float[])townBind["position"])[0] < -999) problems.Add(town.StringId + "'s nameplate is not shown (" + (townBind == null ? "no binding" : "visible " + townBind["visibleOnMap"]) + ").");
            float width = TaleWorlds.Engine.Screen.RealScreenResolutionWidth, height = TaleWorlds.Engine.Screen.RealScreenResolutionHeight;
            if (lordPlate == null) problems.Add("no nameplate for the lord");
            else if (!(lordPlate[2] > 0 && lordPlate[0] >= 0 && lordPlate[0] <= width && lordPlate[1] >= 0 && lordPlate[1] <= height)) problems.Add("the lord's nameplate is off screen (" + String.Join(", ", lordPlate) + ").");
            if (float.IsNaN(townDrawn) || Math.Abs(townDrawn - t.x) > 60) problems.Add(town.StringId + " is not drawn beside the camera");
            return new Dictionary<string, object> { ["cameraTarget"] = Xy(t), ["lordEntity"] = lordVisual == null ? null : Xy(lordVisual.Value), ["lordCanonical"] = Xy(lord.Position),
                ["lordNameplateScreen"] = lordPlate, ["townNameplateWorld"] = townPlate == null ? null : Xy(townPlate.Value), ["townNameplateBinding"] = townBind, ["townDrawnAt"] = townDrawn, ["problems"] = problems };
        }

        // ---- moves ----
        private static IEnumerator Moves(ProbeContext context, DiagnosticSandboxBootstrap boot, PeriodicWorld world, Settlement town2, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"];
            var main = MobileParty.MainParty;
            PeriodicMapCameraView.SetFollowMode("Free");
            var at = CameraTo(context, new Vec3(world.MaxX - 5, town2.Position.Y, PeriodicMapCameraView.Target().z));
            while (at.MoveNext()) yield return at.Current;
            // A fast move to the west town (as the map's own show-on-map does) from just east of the seam: the short way.
            var screen = MapScreen();
            AccessTools(screen.GetType(), "FastMoveCameraToPosition", typeof(CampaignVec2)).Invoke(screen, new object[] { town2.Position });
            float travelled = 0, maxFromStart = 0; Vec3 begin = PeriodicMapCameraView.Target(), last = begin; double totalShift = PeriodicMapCameraView.TotalShift;
            double t0 = context.Seconds;
            while (context.Seconds - t0 < 10)
            {
                yield return null;
                var t = PeriodicMapCameraView.Target();
                float shift = (float)(PeriodicMapCameraView.TotalShift - totalShift); totalShift = PeriodicMapCameraView.TotalShift;
                travelled += new Vec2(t.x - shift - last.x, t.y - last.y).Length; last = t;
                maxFromStart = Math.Max(maxFromStart, world.Distance(begin.AsVec2, t.AsVec2));
                if (world.Distance(t.AsVec2, town2.Position.ToVec2()) < 1) break;
            }
            row["fastMove"] = new Dictionary<string, object> { ["from"] = Xy(begin), ["to"] = Xy(town2.Position), ["end"] = Xy(PeriodicMapCameraView.Target()), ["travelled"] = travelled,
                ["maxPeriodicDistanceFromStart"] = maxFromStart, ["periodicDistance"] = world.Distance(begin.AsVec2, town2.Position.ToVec2()) };
            if (world.Distance(PeriodicMapCameraView.Target().AsVec2, town2.Position.ToVec2()) > 1.5f) failures.Add("The fast move did not reach the west town.");
            if (travelled > 3 * world.Distance(begin.AsVec2, town2.Position.ToVec2()) + 5) failures.Add("The fast move travelled " + travelled + " (the long way).");
            // Teleport and reset: the camera lands on the party as drawn.
            main.Position = new CampaignVec2(new Vec2(world.OriginX + 3, town2.Position.Y), true);
            var landed = new List<object>(); row["landOnParty"] = landed;
            foreach (var call in new[] { "TeleportCameraToMainParty", "ResetCamera" })
            {
                if (call == "ResetCamera") PeriodicMapCameraView.Call(call, true, true); else PeriodicMapCameraView.Call(call);
                for (int i = 0; i < 5; i++) yield return null;
                var t = PeriodicMapCameraView.Target();
                var drawnAt = PeriodicMapPresentation.DrawnPosition(main);
                float d = drawnAt == null ? float.MaxValue : new Vec2(t.x - drawnAt.Value.x, t.y - drawnAt.Value.y).Length;
                landed.Add(new Dictionary<string, object> { ["call"] = call, ["target"] = Xy(t), ["party"] = Xy(main.Position), ["distance"] = d, ["followMode"] = PeriodicMapCameraView.FollowMode() });
                if (d > 2) failures.Add(call + ": the camera is " + d + " from the party.");
            }
        }

        // ---- picking ----
        private static IEnumerator Picking(ProbeContext context, DiagnosticSandboxBootstrap boot, PeriodicWorld world, Settlement town2, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"];
            var main = MobileParty.MainParty;
            var map = (GeneratedMapScene)Campaign.Current.MapSceneWrapper;
            float y = WorldWrapProbe.LandRow(town2.Position.Y - 6, world.MaxX - 8, world.MaxX - 1, world.OriginX + 1, world.OriginX + 6);
            if (float.IsNaN(y)) { failures.Add("No land row through the seam."); yield break; }
            main.Position = new CampaignVec2(new Vec2(world.MaxX - 8, y), true); main.SetMoveModeHold();
            PeriodicMapCameraView.SetFollowMode("Free");
            var at = CameraTo(context, new Vec3(world.MaxX - 5, y, PeriodicMapCameraView.Target().z));
            while (at.MoveNext()) yield return at.Current;
            for (int i = 0; i < 5; i++) yield return null;
            var scene = map.Scene;
            var eye = PeriodicMapCameraView.FrameOrigin();
            // Terrain: the strip point 4 past the seam (the twin of the canonical point 4 east of OriginX).
            var strip = new Vec2(world.MaxX + 4, y);
            var aim = new Vec3(strip.x, strip.y, scene.GetTerrainHeight(strip, true));
            float distance; Vec3 hit; WeakGameEntity entity;
            bool hitSomething = scene.RayCastForClosestEntityOrTerrain(eye, eye + (aim - eye) * 1.5f, out distance, out hit, out entity, 0.01f, BodyFlags.CommonFocusRayCastExcludeFlags);
            var terrain = new Dictionary<string, object> { ["eye"] = new[] { eye.x, eye.y, eye.z }, ["aim"] = new[] { aim.x, aim.y, aim.z }, ["hit"] = hitSomething ? new[] { hit.x, hit.y, hit.z } : null,
                ["hitEntity"] = entity.IsValid ? entity.Name : null };
            row["terrain"] = terrain;
            if (!hitSomething || hit.AsVec2.Distance(strip) > 1) { failures.Add("The ray into the strip did not hit the terrain there."); yield break; }
            var clicked = new CampaignVec2(hit.AsVec2, true);
            var face = Campaign.Current.MapSceneWrapper.GetFaceIndex(in clicked);
            terrain["faceValid"] = face.IsValid();
            var screen = MapScreen();
            var click = AccessTools(screen.GetType(), "HandleLeftMouseButtonClick", AccessToolsType("SandBox.View.Map.Visuals.MapEntityVisual"), typeof(CampaignVec2), typeof(PathFaceRecord), typeof(bool));
            click.Invoke(screen, new object[] { null, clicked, face, false });
            var order = main.MoveTargetPoint;
            var tick = RunFor(context, boot, 0.2);   // the engine computes the ordered path on its next ticks
            while (tick.MoveNext()) yield return tick.Current;
            var route = PeriodicRouteExecutor.Describe(world, main);
            terrain["order"] = Xy(order); terrain["orderCanonical"] = world.IsCanonical(order.X); terrain["expected"] = new[] { world.Canonical(hit.x), hit.y };
            terrain["route"] = route; terrain["behavior"] = main.DefaultBehavior.ToString();
            if (!world.IsCanonical(order.X) || Math.Abs(world.DeltaX(world.Canonical(hit.x), order.X)) > 0.5f) failures.Add("The click in the strip did not order the canonical point.");
            if (((List<object>)route["seamCrossings"]).Count < 1 || (float)route["pathLength"] > 25) failures.Add("The click's route is not the short way through the seam (length " + route["pathLength"] + ").");
            // The move-target decal (MapScreen.TickCircles) beside the camera, at the clicked point's image.
            var decal = DecalPosition(screen, "_pointTargetInnerDecal");
            terrain["targetDecal"] = decal == null ? null : Xy(decal.Value);
            if (decal == null || Math.Abs(world.DeltaX(decal.Value.x, PeriodicMapCameraView.Nearest(order.X))) > 0.5f || Math.Abs(decal.Value.x - PeriodicMapCameraView.Target().x) > 30)
                failures.Add("The move-target decal is not at the order's image beside the camera (" + (decal == null ? "none" : decal.Value.x.ToString()) + ").");
            main.SetMoveModeHold();
            // A town's display copy: the ray at the copy beside the camera resolves to the town's own visual; its click targets the town.
            var copy = PeriodicMapPresentation.Copies.FirstOrDefault(c => c.Settlement == town2.StringId);
            if (copy == null) { failures.Add("No display copy of " + town2.StringId + "."); yield break; }
            var copyAt = copy.Copy.GlobalPosition;
            var copyAim = new Vec3(copyAt.x, copyAt.y, copyAt.z + 0.5f);
            hitSomething = scene.RayCastForClosestEntityOrTerrain(eye, eye + (copyAim - eye) * 1.5f, out distance, out hit, out entity, 0.01f, BodyFlags.CommonFocusRayCastExcludeFlags);
            // Resolved as MapScreen.HandleMouse resolves a hover or click: the entities the ray collides with, then each visual component.
            var dir = copyAim - eye; float length = dir.Length; dir.Normalize();
            var ray = new Ray(eye, dir, length * 1.5f);
            var infos = new Intersection[64]; var ids = new UIntPtr[64];
            int collided = scene.SelectEntitiesCollidedWith(ref ray, infos, ids);
            object visual = null;
            var manager = HarmonyLib.AccessTools.TypeByName("SandBox.View.SandBoxViewSubModule").GetProperty("SandBoxViewVisualManager", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null);
            var components = manager.GetType().GetMethods(Members).First(m => m.Name == "GetComponents" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 0).Invoke(manager, null) as IEnumerable;
            foreach (var component in components)
            {
                var intersect = component.GetType().GetMethod("OnVisualIntersected", Members);
                var args = new object[] { ray, ids, infos, collided, eye, copyAim, hit, null, null };
                intersect.Invoke(component, args);
                if (args[7] != null) { visual = args[7]; break; }
            }
            var resolved = visual == null ? null : SettlementOf(visual);
            var copyPick = new Dictionary<string, object> { ["copy"] = copy.Entity, ["copyAt"] = Xy(copyAt), ["hit"] = hitSomething ? new[] { hit.x, hit.y, hit.z } : null,
                ["hitEntity"] = entity.IsValid ? entity.Name : null, ["entitiesCollided"] = collided, ["resolvedBy"] = "Scene.SelectEntitiesCollidedWith + OnVisualIntersected", ["resolvesTo"] = resolved == null ? null : resolved.StringId };
            row["copy"] = copyPick;
            if (resolved != town2) { failures.Add("The ray at " + town2.StringId + "'s display copy does not resolve to the town (" + collided + " entities collided)."); yield break; }
            var copyPoint = new CampaignVec2(hit.AsVec2, true);
            var copyFace = Campaign.Current.MapSceneWrapper.GetFaceIndex(in copyPoint);
            click.Invoke(screen, new object[] { visual, copyPoint, copyFace, false });
            copyPick["targetSettlement"] = main.TargetSettlement == null ? null : main.TargetSettlement.StringId; copyPick["behavior"] = main.DefaultBehavior.ToString();
            copyPick["order"] = Xy(main.MoveTargetPoint); copyPick["orderCanonical"] = world.IsCanonical(main.MoveTargetPoint.X);
            if (main.TargetSettlement != town2) failures.Add("The click on the display copy does not target " + town2.StringId + ".");
            if (!world.IsCanonical(main.MoveTargetPoint.X)) failures.Add("The click on the display copy stored a non-canonical target.");
            main.SetMoveModeHold();
            try { PeriodicMapCameraView.Call("TeleportCameraToMainParty"); } catch (Exception) { }
        }

        // ---- menu ----
        private static IEnumerator Menu(ProbeContext context, DiagnosticSandboxBootstrap boot, PeriodicWorld world, Settlement town2, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"];
            var main = MobileParty.MainParty;
            float y = WorldWrapProbe.LandRow(town2.Position.Y, world.MaxX - 3, world.MaxX - 1, world.OriginX + 1);
            if (float.IsNaN(y)) { failures.Add("No land row through the seam."); yield break; }
            main.Position = new CampaignVec2(new Vec2(world.MaxX - 3, y), true);
            PeriodicMapCameraView.Call("TeleportCameraToMainParty");
            main.SetMoveGoToSettlement(town2, MobileParty.NavigationType.Default, false);
            var record = new Dictionary<string, object> { ["leg"] = "player into the west town across the seam", ["party"] = main.StringId }; row["travel"] = record;
            var steps = WorldWrapProbe.Travel(context, boot, world, main, () => main.CurrentSettlement == town2, 60, record);
            while (steps.MoveNext()) yield return steps.Current;
            row["entered"] = main.CurrentSettlement == town2;
            if (main.CurrentSettlement != town2) { failures.Add("The player did not enter the town across the seam."); yield break; }
            for (int i = 0; i < 30; i++) yield return null;
            row["inTown"] = new Dictionary<string, object> { ["party"] = Xy(main.Position), ["camera"] = Xy(PeriodicMapCameraView.Target()), ["menu"] = Menu() };
            // Leave through the town menu's own leave option.
            try { RuntimeMissionProbe.RunOption("town_leave"); row["leftBy"] = "town_leave"; }
            catch (InvalidOperationException e)
            {
                row["leftBy"] = "fallback (" + e.Message + ")";
                if (PlayerEncounter.Current != null) PlayerEncounter.LeaveSettlement();
                if (PlayerEncounter.Current != null) PlayerEncounter.Finish(true);
                if (main.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(main);
                TaleWorlds.CampaignSystem.GameMenus.GameMenu.ExitToLast();
            }
            double left = context.Seconds;
            while ((main.CurrentSettlement != null || Menu() != null) && context.Seconds - left < 5) yield return null;
            PeriodicMapCameraView.Trace.Clear(); PeriodicMapCameraView.Tracing = true;
            var settled = SettleOnParty(context);
            while (settled.MoveNext()) yield return settled.Current;
            PeriodicMapCameraView.Tracing = false;
            var t = PeriodicMapCameraView.Target();
            var drawnAt = PeriodicMapPresentation.DrawnPosition(main);
            float d = drawnAt == null ? float.MaxValue : new Vec2(t.x - drawnAt.Value.x, t.y - drawnAt.Value.y).Length;
            row["afterLeaving"] = new Dictionary<string, object> { ["party"] = Xy(main.Position), ["canonical"] = world.IsCanonical(main.Position.X), ["camera"] = Xy(t), ["cameraToParty"] = d,
                ["followMode"] = PeriodicMapCameraView.FollowMode(), ["menu"] = Menu() };
            if (!world.IsCanonical(main.Position.X)) failures.Add("The player is not canonical after leaving the town.");
            if (d > 10) { failures.Add("After the menu the camera is " + d + " from the party."); row["cameraTrace"] = PeriodicMapCameraView.Trace.Where((r, i) => i % 2 == 0).Take(1500).ToList(); }
        }

        private static string Menu()
        {
            var state = TaleWorlds.Core.Game.Current.GameStateManager.ActiveState as TaleWorlds.CampaignSystem.GameState.MapState;
            return state != null && state.MenuContext != null && state.MenuContext.GameMenu != null ? state.MenuContext.GameMenu.StringId : null;
        }

        // ---- mission (the wrap probe's seam battle) ----
        private static void Mission(Dictionary<string, object> wrapReport, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"];
            var battle = wrapReport != null && wrapReport.ContainsKey("seamEncounter") ? wrapReport["seamEncounter"] as Dictionary<string, object> : null;
            var after = battle != null && battle.ContainsKey("afterReturn") ? battle["afterReturn"] as Dictionary<string, object> : null;
            var camera = after != null && after.ContainsKey("camera") ? after["camera"] as Dictionary<string, object> : null;
            row["source"] = "the wrap probe's battle at the seam (MMR-60 entry), after its return to the map"; row["afterReturn"] = after;
            if (camera == null) { failures.Add("The wrap probe recorded no camera after its seam battle."); return; }
            if (!(bool)camera["targetCanonical"]) failures.Add("After the mission the camera target is not canonical.");
            if ((float)camera["distanceToPartyDrawn"] > 10) failures.Add("After the mission the camera is " + camera["distanceToPartyDrawn"] + " from the party.");
        }

        // ---- budget ----
        private static Dictionary<string, object> Budget()
        {
            var registry = VisualsOfEntities();
            var scene = ((GeneratedMapScene)Campaign.Current.MapSceneWrapper).Scene;
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
                return new Dictionary<string, object> { ["copies"] = PeriodicMapPresentation.Copies.Count, ["copyRegistrations"] = PeriodicMapPresentation.Copies.Sum(c => c.Registered.Count),
                    ["visualsOfEntities"] = registry == null ? -1 : registry.Count, ["rootEntities"] = scene.RootEntityCount, ["managedBytes"] = GC.GetTotalMemory(true),
                    ["privateBytes"] = process.PrivateMemorySize64 };
        }

        private static IEnumerator Wraps(ProbeContext context, DiagnosticSandboxBootstrap boot, PeriodicWorld world, Settlement town1, Settlement town2, Dictionary<string, object> start, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"];
            var main = MobileParty.MainParty;
            if (PlayerEncounter.Current != null) PlayerEncounter.Finish(true);
            if (main.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(main);
            if (Menu() != null) TaleWorlds.CampaignSystem.GameMenus.GameMenu.ExitToLast();
            var before = Budget(); row["before"] = before; row["atProbeStart"] = start;
            PeriodicMapCameraView.SetFollowMode("Free");
            float y = town1.Position.Y;
            var first = CameraTo(context, new Vec3(world.MaxX + 10, y, PeriodicMapCameraView.Target().z));   // start just past the seam
            while (first.MoveNext()) yield return first.Current;
            int rebasesBefore = PeriodicMapCameraView.Rebases;
            for (int i = 0; i < 20; i++)
            {
                var go = CameraTo(context, new Vec3(i % 2 == 0 ? world.MaxX - 10 : world.MaxX + 10, y, PeriodicMapCameraView.Target().z));
                while (go.MoveNext()) yield return go.Current;
            }
            row["cameraWraps"] = PeriodicMapCameraView.Rebases - rebasesBefore;
            var crossings = new List<object>(); row["partyCrossings"] = crossings;
            main.Position = town1.GatePosition;
            PeriodicMapCameraView.Call("TeleportCameraToMainParty");
            foreach (var town in new[] { town2, town1 })
            {
                var record = new Dictionary<string, object> { ["leg"] = "player to " + town.StringId, ["party"] = main.StringId }; crossings.Add(record);
                main.SetMoveGoToPoint(town.GatePosition, MobileParty.NavigationType.Default);
                var target = town.GatePosition.ToVec2();
                var steps = WorldWrapProbe.Travel(context, boot, world, main, () => world.Distance(main.Position.ToVec2(), target) < 1.5f, 90, record);
                while (steps.MoveNext()) yield return steps.Current;
                record.Remove("track");
                if (!(bool)record["arrived"])
                {
                    record["stalledState"] = new Dictionary<string, object> { ["position"] = Xy(main.Position), ["behavior"] = main.DefaultBehavior.ToString(), ["shortTerm"] = main.ShortTermBehavior.ToString(),
                        ["moveTarget"] = Xy(main.MoveTargetPoint), ["encounter"] = PlayerEncounter.Current != null, ["mapEvent"] = main.MapEvent != null, ["settlement"] = main.CurrentSettlement == null ? null : main.CurrentSettlement.StringId,
                        ["menu"] = Menu(), ["speed"] = main.Speed, ["atSea"] = main.IsCurrentlyAtSea, ["forceAiNoPathMode"] = main.ForceAiNoPathMode,
                        ["faceValid"] = main.CurrentNavigationFace.IsValid(), ["route"] = PeriodicRouteExecutor.Describe(world, main) };
                    failures.Add("The party leg to " + town.StringId + " did not arrive.");
                }
            }
            var after = Budget(); row["after"] = after;
            if ((int)row["cameraWraps"] < 20) failures.Add("Only " + row["cameraWraps"] + " camera wraps for 20 seam passes.");
            // Copies, their registrations and the scene's root entities must be unchanged; the visual registry also holds the gameplay's
            // party visuals (a party that despawns leaves it), so only its growth would be the copies'.
            foreach (var key in new[] { "copies", "copyRegistrations", "rootEntities" })
                if (Convert.ToInt64(after[key]) != Convert.ToInt64(before[key])) failures.Add(key + " changed from " + before[key] + " to " + after[key] + " across the wraps.");
            if (Convert.ToInt64(after["visualsOfEntities"]) > Convert.ToInt64(before["visualsOfEntities"])) failures.Add("The visual registry grew from " + before["visualsOfEntities"] + " to " + after["visualsOfEntities"] + " across the wraps.");
            long managed = Convert.ToInt64(after["managedBytes"]) - Convert.ToInt64(before["managedBytes"]);
            row["managedBytesDelta"] = managed; row["privateBytesDelta"] = Convert.ToInt64(after["privateBytes"]) - Convert.ToInt64(before["privateBytes"]);
            row["budget"] = "no growth of display copies, their registrations, the visual registry or the scene's root entities across the wraps; MMR-21 recorded no visible-copy memory limit (memory is recorded, not judged)";
        }

        // ---- helpers ----
        // Moves the free camera to a point the short way (its ideal target at the point's image nearest the camera, as panning input would
        // move it), then waits until the target has arrived and the camera frame is still.
        private static IEnumerator CameraTo(ProbeContext context, Vec3 point)
        {
            var world = PeriodicWorld.Current;
            point.x = PeriodicMapCameraView.Nearest(point.x);
            PeriodicMapCameraView.SetIdeal(point);
            double t0 = context.Seconds;
            Vec3 lastFrame = PeriodicMapCameraView.FrameOrigin(); int still = 0;
            while (context.Seconds - t0 < 6 && still < 10)
            {
                yield return null;
                var t = PeriodicMapCameraView.Target(); var f = PeriodicMapCameraView.FrameOrigin();
                bool frameStill = world.Distance(f.AsVec2, lastFrame.AsVec2) < 0.05f; lastFrame = f;
                still = world.Distance(t.AsVec2, point.AsVec2) < 0.5f && frameStill ? still + 1 : 0;
            }
        }

        // Waits (at most 3 s) until the camera target is on the party as drawn and its frame is still.
        private static IEnumerator SettleOnParty(ProbeContext context)
        {
            var world = PeriodicWorld.Current; var main = MobileParty.MainParty;
            double t0 = context.Seconds;
            Vec3 lastFrame = PeriodicMapCameraView.FrameOrigin(); int still = 0;
            while (context.Seconds - t0 < 3 && still < 10)
            {
                yield return null;
                var t = PeriodicMapCameraView.Target(); var f = PeriodicMapCameraView.FrameOrigin();
                bool frameStill = world.Distance(f.AsVec2, lastFrame.AsVec2) < 0.05f; lastFrame = f;
                var drawnAt = PeriodicMapPresentation.DrawnPosition(main);
                float d = drawnAt == null ? float.MaxValue : new Vec2(t.x - drawnAt.Value.x, t.y - drawnAt.Value.y).Length;
                still = d < 1 && frameStill ? still + 1 : 0;
            }
        }

        private static IEnumerator RunFor(ProbeContext context, DiagnosticSandboxBootstrap boot, double seconds)
        {
            double t0 = context.Seconds;
            boot.HoldPaused = false; DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppablePlay);
            while (context.Seconds - t0 < seconds) { boot.Tick(); yield return null; }
            DiagnosticSandboxBootstrap.Pause(Campaign.Current); boot.HoldPaused = true;
        }

        private static object MapScreen()
        {
            var type = AccessToolsType("SandBox.View.Map.MapScreen");
            return type.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null);
        }

        private static Type AccessToolsType(string name) { return HarmonyLib.AccessTools.TypeByName(name); }
        private static MethodInfo AccessTools(Type type, string name, params Type[] parameters) { return HarmonyLib.AccessTools.Method(type, name, parameters); }

        private static IDictionary VisualsOfEntities()
        {
            var type = AccessToolsType("SandBox.View.Map.MapScreen");
            var property = type == null ? null : type.GetProperty("VisualsOfEntities", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? null : property.GetValue(null) as IDictionary;
        }

        private static Settlement SettlementOf(object visual)
        {
            var property = visual.GetType().GetProperty("MapEntity", Members);
            var party = property == null ? null : property.GetValue(visual) as PartyBase;
            return party == null ? null : party.Settlement;
        }

        private static Vec3? PartyEntityPosition(MobileParty party)
        {
            var managerType = AccessToolsType("SandBox.View.Map.Managers.MobilePartyVisualManager");
            var manager = managerType == null ? null : managerType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            var getter = managerType == null ? null : managerType.GetMethod("GetVisualOfEntity", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(PartyBase) }, null);
            var visual = manager == null || getter == null ? null : getter.Invoke(manager, new object[] { party.Party });
            var entity = visual == null ? null : visual.GetType().GetProperty("StrategicEntity", Members).GetValue(visual) as GameEntity;
            return entity == null ? (Vec3?)null : entity.GetGlobalFrame().origin;
        }

        // The view models of the map's nameplate views: MapScreen._mapViewsContainer.MapViews holds the Gauntlet nameplate views, whose
        // _dataSource is the SettlementNameplatesVM / PartyNameplatesVM.
        private static IEnumerable<object> Nameplates(string collectionType)
        {
            var screen = MapScreen();
            FieldInfo containerField = null;
            for (var st = screen == null ? null : screen.GetType(); st != null && containerField == null; st = st.BaseType) containerField = st.GetField("_mapViewsContainer", Members | BindingFlags.DeclaredOnly);
            var container = containerField == null ? null : containerField.GetValue(screen);
            var viewsField = container == null ? null : container.GetType().GetField("MapViews", Members);
            var views = viewsField == null ? null : viewsField.GetValue(container) as IEnumerable;
            if (views == null) yield break;
            foreach (var view in views)
            {
                if (view == null) continue;
                for (var vt = view.GetType(); vt != null; vt = vt.BaseType)
                    foreach (var vf in vt.GetFields(Members | BindingFlags.DeclaredOnly))
                    {
                        if (vf.FieldType.Name != collectionType) continue;
                        var vm = vf.GetValue(view);
                        var property = vm == null ? null : vm.GetType().GetProperty(collectionType == "SettlementNameplatesVM" ? "AllNameplates" : "Nameplates", Members);
                        var list = property == null ? null : property.GetValue(vm) as IEnumerable;
                        if (list != null) foreach (var item in list) yield return item;
                    }
            }
        }

        private static Vec3? SettlementNameplateWorld(Settlement settlement)
        {
            foreach (var plate in Nameplates("SettlementNameplatesVM"))
            {
                var s = plate.GetType().GetProperty("Settlement", Members);
                if (s == null || s.GetValue(plate) != settlement) continue;
                var f = plate.GetType().GetField("_worldPosWithHeight", Members);
                return f == null ? (Vec3?)null : (Vec3)f.GetValue(plate);
            }
            return null;
        }

        private static Dictionary<string, object> SettlementNameplateBind(Settlement settlement)
        {
            foreach (var plate in Nameplates("SettlementNameplatesVM"))
            {
                var s = plate.GetType().GetProperty("Settlement", Members);
                if (s == null || s.GetValue(plate) != settlement) continue;
                var visible = plate.GetType().GetField("_bindIsVisibleOnMap", Members); var position = plate.GetType().GetField("_bindPosition", Members);
                if (visible == null || position == null) return null;
                var at = (Vec2)position.GetValue(plate);
                return new Dictionary<string, object> { ["visibleOnMap"] = (bool)visible.GetValue(plate), ["position"] = new[] { at.x, at.y } };
            }
            return null;
        }

        private static Vec3? DecalPosition(object screen, string field)
        {
            FieldInfo f = null;
            for (var type = screen.GetType(); type != null && f == null; type = type.BaseType) f = type.GetField(field, Members | BindingFlags.DeclaredOnly);
            var decal = f == null ? null : f.GetValue(screen);
            var entity = decal == null ? null : decal.GetType().GetProperty("GameEntity", Members).GetValue(decal) as GameEntity;
            return entity == null ? (Vec3?)null : entity.GetGlobalFrame().origin;
        }

        // ---- tracks ----
        private static IEnumerator Tracks(ProbeContext context, DiagnosticSandboxBootstrap boot, PeriodicWorld world, Settlement town1, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"];
            float y = WorldWrapProbe.LandRow(town1.Position.Y, world.MaxX - 3, world.MaxX - 12, world.OriginX + 3, world.OriginX + 12);
            if (float.IsNaN(y)) { failures.Add("No land row through the seam."); yield break; }
            var behavior = Campaign.Current.GetCampaignBehavior<TaleWorlds.CampaignSystem.CampaignBehaviors.MapTracksCampaignBehavior>();
            var all = behavior == null ? null : typeof(TaleWorlds.CampaignSystem.CampaignBehaviors.MapTracksCampaignBehavior).GetField("_allTracks", Members).GetValue(behavior) as List<Track>;
            var detected = typeof(TaleWorlds.CampaignSystem.CampaignBehaviors.MapTracksCampaignBehavior).GetMethod("TrackDetected", Members);
            if (all == null || detected == null) { failures.Add("No track behavior."); yield break; }
            // Two tracks just east of the seam, detected (the visual manager draws detected tracks), with a lord's identity.
            var owner = MobileParty.All.FirstOrDefault(p => p.IsActive && p.IsLordParty && p != MobileParty.MainParty) ?? MobileParty.MainParty;
            var laid = new List<Track>();
            foreach (var x in new[] { world.MaxX - 3, world.MaxX - 6 })
            {
                behavior.AddTrack(owner, new CampaignVec2(new Vec2(x, y), true), new Vec2(-1, 0));
                var track = all.LastOrDefault(); if (track == null) continue;
                laid.Add(track); detected.Invoke(behavior, new object[] { track });
            }
            row["laid"] = laid.Select(t => Xy(t.Position)).ToList();
            PeriodicMapCameraView.SetFollowMode("Free");
            var samples = new List<object>(); row["samples"] = samples;
            foreach (var at in new[] { world.MaxX - 8, world.OriginX + 8 })   // east of the seam, then across it
            {
                var go = CameraTo(context, new Vec3(at, y, PeriodicMapCameraView.Target().z));
                while (go.MoveNext()) yield return go.Current;
                for (int i = 0; i < 10; i++) yield return null;
                var camera = PeriodicMapCameraView.Target();
                var drawn = laid.Select(TrackEntityPosition).ToList();
                samples.Add(new Dictionary<string, object> { ["camera"] = Xy(camera), ["tracksDrawnAt"] = drawn.Select(v => v == null ? null : Xy(v.Value)).ToList() });
                for (int k = 0; k < drawn.Count; k++)
                    if (drawn[k] == null) failures.Add("Track " + k + " has no drawn entity (camera at " + camera.x + ").");
                    else if (Math.Abs(drawn[k].Value.x - camera.x) > 20) failures.Add("Track " + k + " is drawn at x " + drawn[k].Value.x + " with the camera at " + camera.x + ".");
            }
            try { PeriodicMapCameraView.Call("TeleportCameraToMainParty"); } catch (Exception) { }
        }

        private static Vec3? TrackEntityPosition(Track track)
        {
            var manager = HarmonyLib.AccessTools.TypeByName("SandBox.View.SandBoxViewSubModule").GetProperty("SandBoxViewVisualManager", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null);
            var components = manager.GetType().GetMethods(Members).First(m => m.Name == "GetComponents" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 0).Invoke(manager, null) as IEnumerable;
            foreach (var component in components)
            {
                if (component.GetType().Name != "MapTracksVisualManager") continue;
                var visuals = component.GetType().GetField("_trackVisuals", Members).GetValue(component) as IDictionary;
                if (visuals == null || !visuals.Contains(track)) return null;
                var pair = visuals[track];
                var entity = pair.GetType().GetField("Item2").GetValue(pair) as GameEntity;
                return entity == null ? (Vec3?)null : entity.GetGlobalFrame().origin;
            }
            return null;
        }

        // ---- alignment ----
        private static void Alignment(PeriodicWorld world, Settlement town1, Settlement town2, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"];
            var map = (GeneratedMapScene)Campaign.Current.MapSceneWrapper;
            var points = new List<CampaignVec2> { town1.PortPosition, town2.PortPosition, town1.GatePosition, town2.GatePosition };
            foreach (var x in new[] { world.OriginX + 1, world.OriginX + 20, world.MaxX - 1, world.MaxX - 20 })
                foreach (var y in new[] { town1.Position.Y, town2.Position.Y })
                    points.Add(new CampaignVec2(new Vec2(x, y), true));
            var compared = new List<object>(); row["twins"] = compared;
            int mismatches = 0;
            foreach (var p in points)
            {
                var twin = world.Coordinates.Images(p.X).Where(i => i.Shift != 0).Select(i => (float)i.X).DefaultIfEmpty(float.NaN).First();
                if (float.IsNaN(twin)) continue;
                var q = new CampaignVec2(new Vec2(twin, p.Y), p.IsOnLand);
                float h1 = 0, h2 = 0; map.GetHeightAtPoint(in p, ref h1); map.GetHeightAtPoint(in q, ref h2);
                var f1 = p.Face; var f2 = q.Face;
                string t1 = f1.IsValid() ? map.GetFaceTerrainType(f1).ToString() : null, t2 = f2.IsValid() ? map.GetFaceTerrainType(f2).ToString() : null;
                bool same = Math.Abs(h1 - h2) < 0.01f && t1 == t2;
                if (!same) mismatches++;
                compared.Add(new Dictionary<string, object> { ["canonical"] = Xy(p), ["twin"] = Xy(q), ["heights"] = new[] { h1, h2 }, ["faces"] = new[] { t1, t2 }, ["same"] = same });
            }
            if (mismatches > 0) failures.Add(mismatches + " strip twins differ from their canonical points.");
            var parity = new List<object>(); row["copyVisibility"] = parity;
            foreach (var c in PeriodicMapPresentation.Copies)
            {
                int differ = 0, n = Math.Min(c.Originals.Count, c.Mirrors.Count);
                for (int i = 0; i < n; i++) if (c.Originals[i].GetVisibilityExcludeParents() != c.Mirrors[i].GetVisibilityExcludeParents()) differ++;
                parity.Add(new Dictionary<string, object> { ["settlement"] = c.Settlement, ["entities"] = n, ["mirrors"] = c.Mirrors.Count, ["visibilityDiffers"] = differ, ["syncs"] = c.Syncs });
                if (differ > 0 || c.Originals.Count != c.Mirrors.Count) failures.Add(c.Settlement + ": the copy's entities differ from the settlement's (" + differ + " visibility, " + c.Originals.Count + " vs " + c.Mirrors.Count + " entities).");
            }
        }

        private static float[] PartyNameplateScreen(MobileParty party)
        {
            foreach (var plate in Nameplates("PartyNameplatesVM"))
            {
                var p = plate.GetType().GetProperty("Party", Members);
                if (p == null || p.GetValue(plate) != party) continue;
                Func<string, float> read = name => { var f = plate.GetType().GetField(name, Members); return f == null ? float.NaN : (float)f.GetValue(plate); };
                return new[] { read("_latestX"), read("_latestY"), read("_latestW") };
            }
            return null;
        }
    }
}
