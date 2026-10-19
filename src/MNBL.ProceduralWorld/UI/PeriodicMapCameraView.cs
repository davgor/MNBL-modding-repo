using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MNBL.ProceduralWorld.CampaignBootstrap;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.UI
{
    // MMR-66: the map camera over a periodic world (docs/periodic-presentation.md). SandBox.View is not referenced: MapCameraView is reached
    // by name and patched with the periodic set (PeriodicMapPresentation.ApplyAll; all or nothing).
    //  * The camera target stays in the canonical range. When it leaves (free panning, axis input, a drag, a follow or a move that ran into a
    //    strip), every camera field holding a world position moves by whole widths (Rebase), in a prefix of UpdateMapCamera: that is the
    //    method that writes the native camera frame and the sound listener, so both are computed from the rebased fields in the same frame.
    //    The strips beyond the cut hold the terrain's twins and every visual is drawn at its image nearest the camera, so the view does not
    //    change. MapScreen's drag pivot (_clickedPosition) moves with it.
    //  * A target the camera is sent to is taken at its image nearest the camera: a followed party, a position (FastMoveCameraToPosition, the
    //    entry the map's trackers, clan and kingdom screens use to show a position), an animation, and follow mode's target, which
    //    GetMapCameraInput builds from the followed party, its settlement and port, a siege camp or a map event. Averages of a settlement and
    //    its port (and besieger) are formed with every term at its image beside the settlement, and the besieger camp lerp of
    //    GetCameraTargetForParty with the camp frame at its image beside the target, so a settlement whose port or camp lies across the cut
    //    is averaged where it stands. An input-driven ideal target lies beside the camera already and is unchanged.
    //  * When the followed main party is rebased (PeriodicSeam), the camera needs no move: it follows the party's image nearest it and
    //    rebases itself when its target crosses the cut. This replaces MMR-63's camera shift on the party's rebase.
    //  * When the camera wraps or has moved far, visuals that are framed only on their own changes (tracks, anchors) are refreshed
    //    (PeriodicMapPresentation.RefreshImages).
    internal static class PeriodicMapCameraView
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static Type View;
        private static AccessTools.FieldRef<object, Vec3> target, ideal;
        private static AccessTools.FieldRef<object, CampaignVec2> lastIdeal, animationTarget;
        private static AccessTools.FieldRef<object, MatrixFrame> frame;
        private static AccessTools.FieldRef<object, Vec3> clickedPosition;   // MapScreen._clickedPosition (the drag pivot)
        private static PropertyInfo nativeCamera, screenInstance;
        private static object current;   // the live MapCameraView (the last one ticked)
        private static float refreshedAtX = float.NaN;

        public static int Rebases { get; private set; }        // the camera's own rebases (its target left the canonical range)
        public static double TotalShift { get; private set; }  // every shift applied to the camera, summed (probes difference it)
        public static readonly List<Dictionary<string, object>> Events = new List<Dictionary<string, object>>();   // the first 200 camera moves
        // Probe trace: per camera tick, before it and after it (phase, time, target x/y, ideal x, last ideal x, frame x, native camera x).
        internal static bool Tracing;
        internal static readonly List<float[]> Trace = new List<float[]>();
        // The camera target's x the display rule measures from (NaN before the first camera tick of a world).
        public static float TargetX { get; private set; } = float.NaN;
        public static object Current { get { return current; } }

        internal static void Apply(Harmony harmony, List<Dictionary<string, object>> applied)
        {
            View = AccessTools.TypeByName("SandBox.View.Map.MapCameraView");
            var screen = AccessTools.TypeByName("SandBox.View.Map.MapScreen");
            if (View == null || screen == null) throw new MissingMemberException("SandBox.View.Map.MapCameraView/MapScreen is not loaded.");
            target = Field<Vec3>(View, "_cameraTarget"); ideal = Field<Vec3>(View, "<IdealCameraTarget>k__BackingField");
            lastIdeal = Field<CampaignVec2>(View, "_lastUsedIdealCameraTarget"); animationTarget = Field<CampaignVec2>(View, "_cameraAnimationTarget");
            frame = Field<MatrixFrame>(View, "_cameraFrame"); clickedPosition = Field<Vec3>(screen, "_clickedPosition");
            nativeCamera = View.GetProperty("Camera", Members);
            screenInstance = screen.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (nativeCamera == null || screenInstance == null) throw new MissingMemberException("MapCameraView.Camera or MapScreen.Instance is not installed.");
            Rebases = 0; TotalShift = 0; Events.Clear(); Trace.Clear(); Tracing = false; TargetX = float.NaN; current = null; refreshedAtX = float.NaN;
            Patch(harmony, applied, "OnBeforeTick", nameof(BeforeTickPrefix), nameof(BeforeTickPostfix), "camera target kept canonical (named fields); probe trace");
            Patch(harmony, applied, "UpdateMapCamera", nameof(UpdateMapCameraPrefix), null, "camera target kept canonical before the native camera frame is written");
            Patch(harmony, applied, "GetCameraTargetForPosition", null, nameof(NearestTargetPostfix), "a position the camera goes to -> its image nearest the camera");
            Patch(harmony, applied, "GetCameraTargetForParty", null, nameof(NearestTargetPostfix), "a party the camera follows -> its image nearest the camera");
            Patch(harmony, applied, "StartCameraAnimation", nameof(AnimationPrefix), null, "an animation target -> its image nearest the camera");
            Patch(harmony, applied, "GetMapCameraInput", null, nameof(IdealNearestPostfix), "the follow target (and any ideal target) -> its image nearest the camera");
            Transpile(harmony, applied, "OnBeforeTick", nameof(FollowTargetTranspiler), "the followed party's and the animation's position -> image nearest the camera", 2);
            Transpile(harmony, applied, "GetMapCameraInput", nameof(PortAverageTranspiler), "settlement + port (+ besieger) average with every term beside the settlement", 2);
            Transpile(harmony, applied, "GetCameraTargetForParty", nameof(CampLerpTranspiler), "besieger camp lerp with the camp frame beside the target", 1);
        }

        internal static void Release() { current = null; TargetX = float.NaN; refreshedAtX = float.NaN; }

        private static AccessTools.FieldRef<object, F> Field<F>(Type type, string name)
        {
            var f = type.GetField(name, Members);
            if (f == null || f.FieldType != typeof(F)) throw new MissingFieldException(type.FullName, name);
            return AccessTools.FieldRefAccess<F>(type, name);
        }

        private static void Patch(Harmony harmony, List<Dictionary<string, object>> applied, string method, string prefix, string postfix, string label)
        {
            var targetMethod = AccessTools.Method(View, method);
            if (targetMethod == null) throw new MissingMethodException(View.FullName, method);
            harmony.Patch(targetMethod, prefix: prefix == null ? null : new HarmonyMethod(typeof(PeriodicMapCameraView), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(PeriodicMapCameraView), postfix));
            applied.Add(new Dictionary<string, object> { ["target"] = View.FullName + "::" + method, ["patch"] = label, ["kind"] = prefix != null ? "prefix" : "postfix" });
        }

        private static int replacedNow;
        private static void Transpile(Harmony harmony, List<Dictionary<string, object>> applied, string method, string transpiler, string label, int expected)
        {
            replacedNow = 0;
            harmony.Patch(AccessTools.Method(View, method), transpiler: new HarmonyMethod(typeof(PeriodicMapCameraView), transpiler));
            if (replacedNow != expected) throw new InvalidOperationException("MapCameraView." + method + ": " + replacedNow + " sites replaced, " + expected + " audited (" + label + ").");
            applied.Add(new Dictionary<string, object> { ["target"] = View.FullName + "::" + method, ["patch"] = label, ["kind"] = "transpiler", ["replacedCalls"] = replacedNow });
        }

        // ---- the rebase ----
        private static void BeforeTickPrefix(object __instance)
        {
            current = __instance;
            if (PeriodicWorld.Current == null) return;
            if (Tracing) Record(__instance, 0);
            Rebase(__instance);
        }

        private static void UpdateMapCameraPrefix(object __instance)
        {
            current = __instance;
            if (PeriodicWorld.Current != null) Rebase(__instance);
        }

        private static void BeforeTickPostfix(object __instance) { if (Tracing && PeriodicWorld.Current != null) Record(__instance, 1); }

        private static void Rebase(object view)
        {
            var world = PeriodicWorld.Current;
            float x = target(view).x;
            if (!world.IsCanonical(x))
            {
                float shift = world.Canonical(x) - x;
                Shift(view, shift);
                Rebases++; TotalShift += shift;
                if (Events.Count < 200) Events.Add(new Dictionary<string, object> { ["fromX"] = x, ["toX"] = x + shift, ["y"] = target(view).y, ["shift"] = shift });
                refreshedAtX = float.NaN;
                x += shift;
            }
            TargetX = x;
            // Visuals framed only on their own changes take their images again after a wrap or a long move.
            if (float.IsNaN(refreshedAtX) || Math.Abs(world.DeltaX(refreshedAtX, x)) > 50)
            {
                refreshedAtX = x;
                MNBL.ProceduralWorld.World.PeriodicMapPresentation.RefreshImages();
            }
        }

        private static void Record(object view, int phase)
        {
            if (Trace.Count >= 20000) return;
            var t = target(view); var f = frame(view).origin;
            Trace.Add(new[] { phase, (float)TaleWorlds.Engine.Time.ApplicationTime, t.x, t.y, ideal(view).x, lastIdeal(view).X, f.x, NativeOrigin(view).x });
        }

        // Every named camera field holding a world position (and MapScreen's drag pivot) moved by shift in x.
        private static void Shift(object view, float shift)
        {
            target(view).x += shift;
            ideal(view).x += shift;
            ShiftSet(ref lastIdeal(view), shift);
            ShiftSet(ref animationTarget(view), shift);
            frame(view).origin.x += shift;
            var screen = screenInstance.GetValue(null);
            if (screen != null && (clickedPosition(screen).x != 0 || clickedPosition(screen).y != 0)) clickedPosition(screen).x += shift;
        }

        private static void ShiftSet(ref CampaignVec2 v, float shift)
        {
            if (float.IsNaN(v.X) || (v.X == 0 && v.Y == 0)) return;   // unset (an animation target before the first animation)
            v = new CampaignVec2(new Vec2(v.X + shift, v.Y), v.IsOnLand);
        }

        // ---- the nearest image ----
        // The image of x nearest the camera target, inside the terrain (x itself before the first camera tick or off a periodic world).
        internal static float Nearest(float x)
        {
            var world = PeriodicWorld.Current;
            float cx = TargetX;
            if (world == null || float.IsNaN(cx) || float.IsNaN(x)) return x;
            float image = cx + world.DeltaX(cx, x);
            if (image < 0) image += world.Width; else if (image > world.Extent) image -= world.Width;   // the in-terrain image next to it
            return image;
        }

        // The image of x nearest the reference x (for averages formed beside a settlement).
        private static float Beside(float reference, float x)
        {
            var world = PeriodicWorld.Current;
            return world == null || float.IsNaN(reference) || float.IsNaN(x) ? x : reference + world.DeltaX(reference, x);
        }

        private static void NearestTargetPostfix(ref Vec3 __result) { __result.x = Nearest(__result.x); }

        private static void IdealNearestPostfix(object __instance)
        {
            if (PeriodicWorld.Current == null) return;
            ideal(__instance).x = Nearest(ideal(__instance).x);
        }

        private static void AnimationPrefix(ref CampaignVec2 __0)
        {
            float x = Nearest(__0.X);
            if (x != __0.X) __0 = new CampaignVec2(new Vec2(x, __0.Y), __0.IsOnLand);
        }

        private static IEnumerable<CodeInstruction> FollowTargetTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var asVec3 = AccessTools.Method(typeof(CampaignVec2), nameof(CampaignVec2.AsVec3));
            var nearest = AccessTools.Method(typeof(PeriodicMapCameraView), nameof(NearestAsVec3));
            foreach (var i in instructions)
            {
                if (i.Calls(asVec3)) { i.opcode = OpCodes.Call; i.operand = nearest; replacedNow++; }
                yield return i;
            }
        }

        // CampaignVec2.AsVec3() at the image nearest the camera (same stack shape: the address of the position).
        public static Vec3 NearestAsVec3(ref CampaignVec2 position) { var v = position.AsVec3(); v.x = Nearest(v.x); return v; }

        // GetMapCameraInput's follow branch: settlement + port, then (+ besieger) / 3 or * 0.5. The two Vec2 additions after
        // Settlement.get_PortPosition add each term at its image beside the settlement (the running sum holds one, then two, positions).
        private static IEnumerable<CodeInstruction> PortAverageTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var port = AccessTools.PropertyGetter(typeof(TaleWorlds.CampaignSystem.Settlements.Settlement), "PortPosition");
            var add = AccessTools.Method(typeof(Vec2), "op_Addition", new[] { typeof(Vec2), typeof(Vec2) });
            var helpers = new[] { AccessTools.Method(typeof(PeriodicMapCameraView), nameof(AddBesideOne)), AccessTools.Method(typeof(PeriodicMapCameraView), nameof(AddBesideTwo)) };
            bool afterPort = false; int next = 0;
            foreach (var i in instructions)
            {
                if (i.Calls(port)) afterPort = true;
                else if (afterPort && next < 2 && i.Calls(add)) { i.opcode = OpCodes.Call; i.operand = helpers[next++]; replacedNow++; }
                yield return i;
            }
        }

        public static Vec2 AddBesideOne(Vec2 sum, Vec2 term) { return new Vec2(sum.x + Beside(sum.x, term.x), sum.y + term.y); }
        public static Vec2 AddBesideTwo(Vec2 sum, Vec2 term) { return new Vec2(sum.x + Beside(sum.x / 2, term.x), sum.y + term.y); }

        private static IEnumerable<CodeInstruction> CampLerpTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var lerp = AccessTools.Method(typeof(Vec2), nameof(Vec2.Lerp), new[] { typeof(Vec2), typeof(Vec2), typeof(float) });
            var beside = AccessTools.Method(typeof(PeriodicMapCameraView), nameof(LerpBeside));
            foreach (var i in instructions)
            {
                if (i.Calls(lerp)) { i.opcode = OpCodes.Call; i.operand = beside; replacedNow++; }
                yield return i;
            }
        }

        public static Vec2 LerpBeside(Vec2 from, Vec2 to, float t) { return Vec2.Lerp(from, new Vec2(Beside(from.x, to.x), to.y), t); }

        // ---- probe access (the live camera's named fields and public methods) ----
        internal static Vec3 Target() { return target(current); }
        internal static Vec3 Ideal() { return ideal(current); }
        internal static void SetIdeal(Vec3 v) { ideal(current) = v; }
        internal static Vec3 FrameOrigin() { return frame(current).origin; }
        // The native engine camera's position (what is rendered).
        internal static Vec3 NativeOrigin() { return NativeOrigin(current); }
        private static Vec3 NativeOrigin(object view)
        {
            var camera = nativeCamera.GetValue(view) as TaleWorlds.Engine.Camera;
            return camera == null ? new Vec3(float.NaN, float.NaN, float.NaN) : camera.Frame.origin;
        }
        internal static string FollowMode() { var p = View.GetProperty("CurrentCameraFollowMode", Members); return p == null ? null : p.GetValue(current).ToString(); }
        internal static void SetFollowMode(string mode)
        {
            var enumType = View.GetNestedType("CameraFollowMode", BindingFlags.Public | BindingFlags.NonPublic);
            AccessTools.Method(View, "SetCameraMode").Invoke(current, new[] { Enum.Parse(enumType, mode) });
        }
        internal static void Call(string method, params object[] args) { AccessTools.Method(View, method).Invoke(current, args); }
    }
}
