using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.Surface;
using MNBL.ProceduralWorld.UI;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.ObjectSystem;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-247: what the acceptance probe reads from the running game (Map2AcceptanceProbe drives the steps): the map camera, frames and memory,
    // the live census, the economy, faces and speed, the map's support report. Every reader is fail-soft: an error is recorded in its row.
    internal static class Map2AcceptanceChecks
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static IEnumerator Frames(int count) { for (int i = 0; i < count; i++) yield return null; }

        internal static Scene MapScene() { var w = Campaign.Current == null ? null : Campaign.Current.MapSceneWrapper as SandBox.MapScene; return w == null ? null : w.Scene; }

        internal static double[] P(Vec2 v) { return new double[] { v.X, v.Y }; }
        internal static object F(double v) { return ProbeContext.F(v); }

        // ---------------------------------------------------------------- the map camera

        // MapScreen.Instance.MapCameraView (the installed view, vanilla or generated; PeriodicMapCameraView only patches it on generated maps).
        internal static object CameraView()
        {
            var screen = AccessTools.TypeByName("SandBox.View.Map.MapScreen");
            var instanceProperty = screen == null ? null : AccessTools.Property(screen, "Instance");
            var instance = instanceProperty == null ? null : instanceProperty.GetValue(null);
            if (instance == null) return null;
            var view = AccessTools.Property(screen, "MapCameraView");
            return view == null ? null : view.GetValue(instance);
        }

        // MapScreen.Instance.EncyclopediaScreenManager (SandBox.View's MapEncyclopediaView). An open encyclopedia keeps a disable request on the
        // map state (time frozen) until its CloseEncyclopedia.
        internal static object EncyclopediaView()
        {
            try
            {
                var screen = AccessTools.TypeByName("SandBox.View.Map.MapScreen");
                var instanceProperty = screen == null ? null : AccessTools.Property(screen, "Instance");
                var instance = instanceProperty == null ? null : instanceProperty.GetValue(null);
                var view = instance == null ? null : AccessTools.Property(screen, "EncyclopediaScreenManager");
                return view == null ? null : view.GetValue(instance);
            }
            catch (Exception) { return null; }
        }

        internal static bool EncyclopediaOpen()
        {
            var view = EncyclopediaView();
            var open = view == null ? null : AccessTools.Property(view.GetType(), "IsEncyclopediaOpen");
            try { return open != null && (bool)open.GetValue(view); } catch (Exception) { return false; }
        }

        // Closed through the view's own CloseEncyclopedia when open; null when it was not open.
        internal static string CloseEncyclopediaIfOpen()
        {
            if (!EncyclopediaOpen()) return null;
            var view = EncyclopediaView();
            var close = AccessTools.Method(view.GetType(), "CloseEncyclopedia", new Type[0]);
            if (close == null) return "no CloseEncyclopedia on " + view.GetType().FullName;
            try { close.Invoke(view, new object[0]); } catch (Exception e) { return "CloseEncyclopedia threw " + (e.InnerException ?? e).GetType().Name; }
            return EncyclopediaOpen() ? "still open after CloseEncyclopedia" : "closed";
        }

        private static FieldInfo Field(object o, string name)
        {
            var f = AccessTools.Field(o.GetType(), name);
            if (f == null) throw new MissingFieldException(o.GetType().FullName, name);
            return f;
        }

        internal static Dictionary<string, object> CameraState()
        {
            var row = new Dictionary<string, object>();
            try
            {
                var view = CameraView();
                if (view == null) { row["error"] = "no map camera view"; return row; }
                var target = (Vec3)Field(view, "_cameraTarget").GetValue(view);
                var frame = (MatrixFrame)Field(view, "_cameraFrame").GetValue(view);
                row["target"] = new double[] { target.x, target.y, target.z };
                row["frameOrigin"] = new double[] { frame.origin.x, frame.origin.y, frame.origin.z };
                row["distance"] = F((float)Field(view, "<CameraDistance>k__BackingField").GetValue(view));
                row["targetDistance"] = F((float)Field(view, "<TargetCameraDistance>k__BackingField").GetValue(view));
                row["bearing"] = F((float)Field(view, "<CameraBearing>k__BackingField").GetValue(view));
                row["followMode"] = Field(view, "<CurrentCameraFollowMode>k__BackingField").GetValue(view).ToString();
            }
            catch (Exception e) { row["error"] = e.GetType().Name + ": " + e.Message; }
            return row;
        }

        // The free camera over a physical point at a fixed distance and bearing (the image of x nearest the camera on a periodic map). The
        // target, the ideal target and the distances are set together so the view jumps there.
        internal static void AimFree(double x, double y, float distance, float bearing, float? targetZ = null)
        {
            var view = CameraView();
            if (view == null) throw new InvalidOperationException("no map camera view");
            float px = PeriodicWorld.Current != null && PeriodicMapCameraView.Current != null ? PeriodicMapCameraView.Nearest((float)x) : (float)x;
            var scene = MapScene();
            float z = targetZ ?? (scene == null ? 0 : scene.GetTerrainHeight(new Vec2(px, (float)y), true));   // MMR-291-W: an explicit height (the lab's sea mesh grid)
            var target = new Vec3(px, (float)y, z);
            var mode = Field(view, "<CurrentCameraFollowMode>k__BackingField");
            mode.SetValue(view, Enum.ToObject(mode.FieldType, 0));   // Free
            Field(view, "<IdealCameraTarget>k__BackingField").SetValue(view, target);
            Field(view, "_cameraTarget").SetValue(view, target);
            SetDistance(view, distance);
            Field(view, "<CameraBearing>k__BackingField").SetValue(view, bearing);
        }

        internal static void SetDistance(object view, float distance)
        {
            Field(view, "<CameraDistance>k__BackingField").SetValue(view, distance);
            Field(view, "<TargetCameraDistance>k__BackingField").SetValue(view, distance);
        }

        // Back to following the main party (as the map's own follow key does), at the given distance.
        internal static void FollowParty(float distance)
        {
            var view = CameraView();
            if (view == null) return;
            try
            {
                var mode = Field(view, "<CurrentCameraFollowMode>k__BackingField");
                mode.SetValue(view, Enum.ToObject(mode.FieldType, 1));   // FollowParty
                SetDistance(view, distance);
                var teleport = AccessTools.Method(view.GetType(), "TeleportCameraToMainParty");
                if (teleport != null && teleport.GetParameters().Length == 0) teleport.Invoke(view, new object[0]);
            }
            catch (Exception) { }
        }

        // Until the camera frame stands still and the scene has loaded what it shows (no shader compiling), at most `seconds`; then `extra`
        // frames. The record says how it ended.
        internal static IEnumerator Settle(ProbeContext context, Dictionary<string, object> record, double seconds, int extra)
        {
            double start = context.Seconds; int still = 0; double[] last = null;
            var scene = MapScene();
            while (context.Seconds - start < seconds)
            {
                yield return null;
                var state = CameraState();
                var origin = state.ContainsKey("frameOrigin") ? (double[])state["frameOrigin"] : null;
                bool same = origin != null && last != null && Math.Abs(origin[0] - last[0]) < 0.01 && Math.Abs(origin[1] - last[1]) < 0.01 && Math.Abs(origin[2] - last[2]) < 0.01;
                last = origin;
                bool loaded = scene == null || (scene.IsLoadingFinished() && Utilities.GetNumberOfShaderCompilationsInProgress() == 0);
                still = same && loaded ? still + 1 : 0;
                if (still >= 12) break;
            }
            record["settleSeconds"] = Math.Round(context.Seconds - start, 2); record["settled"] = still >= 12;
            for (int i = 0; i < extra; i++) yield return null;
            record["camera"] = CameraState();
        }

        // ---------------------------------------------------------------- frames and memory

        // Frames at the current view, time stopped: the frame interval (wall clock between ticks), the engine's main-thread and renderer fps
        // (Utilities.GetMainFps / GetRendererFps; MMR-249: the main-thread time is the cost signal under the 60 fps cap).
        internal static IEnumerator MeasureFrames(ProbeContext context, Dictionary<string, object> record, int frames)
        {
            var deltas = new List<double>(); double main = 0, renderer = 0; int mainSamples = 0;
            yield return null;
            double last = context.Seconds;
            for (int i = 0; i < frames; i++)
            {
                yield return null;
                double now = context.Seconds; deltas.Add((now - last) * 1000.0); last = now;
                float m = Utilities.GetMainFps(), r = Utilities.GetRendererFps();
                if (m > 0 && !float.IsNaN(m) && !float.IsInfinity(m)) { main += m; mainSamples++; }
                if (r > 0 && !float.IsNaN(r) && !float.IsInfinity(r)) renderer += r;
            }
            deltas.Sort();
            double mean = deltas.Average();
            record["frames"] = deltas.Count; record["meanFrameMs"] = mean; record["p50FrameMs"] = deltas[deltas.Count / 2]; record["p95FrameMs"] = deltas[(int)(deltas.Count * .95)]; record["maxFrameMs"] = deltas[deltas.Count - 1];
            record["meanMainFps"] = mainSamples == 0 ? null : (object)(main / mainSamples); record["meanMainMs"] = mainSamples == 0 ? null : (object)(1000.0 * mainSamples / main);
            record["meanRendererFps"] = renderer / frames;
            // A mean interval within 0.4 ms of 1/60 s is the frame cap MMR-249 could not lift: the interval is then no cost measure.
            record["frameCapSuspected"] = Math.Abs(mean - 1000.0 / 60) < 0.4;
            record["memory"] = Memory();
        }

        internal static Dictionary<string, object> Memory()
        {
            var row = new Dictionary<string, object>();
            try
            {
                using (var p = Process.GetCurrentProcess()) { row["workingSetBytes"] = p.WorkingSet64; row["privateBytes"] = p.PrivateMemorySize64; row["peakWorkingSetBytes"] = p.PeakWorkingSet64; row["virtualBytes"] = p.VirtualMemorySize64; }
                row["gcHeapBytes"] = GC.GetTotalMemory(false);
                try { row["engineCpuMemoryMB"] = Utilities.GetCurrentCpuMemoryUsageMB(); } catch (Exception e) { row["engineCpuMemoryMB"] = e.GetType().Name; }
                try { row["engineGpuMemoryMB"] = Utilities.GetGPUMemoryMB(); } catch (Exception e) { row["engineGpuMemoryMB"] = e.GetType().Name; }
            }
            catch (Exception e) { row["error"] = e.GetType().Name + ": " + e.Message; }
            return row;
        }

        // ---------------------------------------------------------------- the map's support report and dressing

        // The generated map's support report (GeneratedMapScene's private record: climate visuals, map dressing, magic ground, the battle map,
        // the navigation caches), the keys named copied; null on a vanilla map.
        internal static Dictionary<string, object> Support(params string[] keys)
        {
            var map = Campaign.Current == null ? null : Campaign.Current.MapSceneWrapper as GeneratedMapScene;
            if (map == null) return null;
            var field = typeof(GeneratedMapScene).GetField("supportReport", Members);
            var report = field == null ? null : field.GetValue(map) as Dictionary<string, object>;
            if (report == null) return new Dictionary<string, object> { ["error"] = "no support report" };
            var row = new Dictionary<string, object> { ["keys"] = report.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList() };
            foreach (var k in keys) { object v; if (report.TryGetValue(k, out v)) row[k] = v; }
            return row;
        }

        // The navigation caches the map acquired: status (reused, generated, regenerated) and the per-type generate times, summed.
        internal static Dictionary<string, object> CacheRecord(Dictionary<string, object> support)
        {
            var row = new Dictionary<string, object>();
            object caches; Dictionary<string, object> acquired = null;
            if (support != null && support.TryGetValue("generatedNavigationCaches", out caches) && caches is Dictionary<string, object>)
                acquired = ((Dictionary<string, object>)caches).ContainsKey("navigationCache") ? ((Dictionary<string, object>)caches)["navigationCache"] as Dictionary<string, object> : null;
            if (acquired == null) { row["error"] = "no navigation cache record in the support report"; return row; }
            row["status"] = acquired.ContainsKey("status") ? acquired["status"] : null;
            double generated = 0; bool any = false;
            if (acquired.ContainsKey("caches") && acquired["caches"] is IEnumerable)
                foreach (var c in ((IEnumerable)acquired["caches"]).OfType<Dictionary<string, object>>())
                    if (c.ContainsKey("generateMilliseconds")) { generated += Convert.ToDouble(c["generateMilliseconds"], CultureInfo.InvariantCulture); any = true; }
            row["generateSeconds"] = any ? (object)(generated / 1000.0) : null;
            row["elapsedMilliseconds"] = acquired.ContainsKey("elapsedMilliseconds") ? acquired["elapsedMilliseconds"] : null;
            row["record"] = acquired;
            return row;
        }

        // The published caches' own record of this scene (CacheForecast, as the loader predicts it): reuse, kind, the measured build seconds.
        internal static Dictionary<string, object> Forecast(string scene, bool hideScene)
        {
            try
            {
                var manifests = WorldNavigationCache.PublishedManifests();
                if (hideScene) manifests = manifests.Where(m => m.Value == null || !Equals(m.Value.ContainsKey("scene") ? m.Value["scene"] : null, scene)).ToList();
                var f = CacheForecast.Predict(manifests, scene, WorldNavigationCache.ModuleSha256(), GeneratedPatches.InstalledVersions());
                var r = f.Report(); r["hidden"] = hideScene; r["published"] = manifests.Count;
                return r;
            }
            catch (Exception e) { return new Dictionary<string, object> { ["error"] = e.GetType().Name + ": " + e.Message }; }
        }

        // ---------------------------------------------------------------- terrain, faces and speed

        // The face type the game reads at a point, "invalid" where the point has no valid land face (acc4 triage fails-1: GeneratedMapScene
        // falls back to the installed Plain for an invalid face, so sea, lake, river and mountain points read as Plain).
        internal static string TerrainAt(Vec2 p)
        {
            try { var c = new CampaignVec2(p, true); return c.Face.IsValid() ? Campaign.Current.MapSceneWrapper.GetTerrainTypeAtPosition(c).ToString() : Invalid; }
            catch (Exception e) { return "error: " + e.GetType().Name; }
        }

        internal const string Invalid = "invalid";

        // The face record at a point: its type and index ("invalid", -1 when the face is invalid).
        internal static object[] FaceAt(Vec2 p)
        {
            try
            {
                var face = new CampaignVec2(p, true).Face;
                return face.IsValid() ? new object[] { Campaign.Current.MapSceneWrapper.GetFaceTerrainType(face).ToString(), face.FaceIndex } : new object[] { Invalid, -1 };
            }
            catch (Exception e) { return new object[] { "error: " + e.GetType().Name, -1 }; }
        }

        internal static string FaceTypeAt(CampaignVec2 p)
        {
            try { var face = p.Face; return face.IsValid() ? Campaign.Current.MapSceneWrapper.GetFaceTerrainType(face).ToString() : Invalid; }
            catch (Exception e) { return "error: " + e.GetType().Name; }
        }

        // The tooltip's numbers: the party speed model's base and final explained numbers with every line (name, value), as the party
        // speed tooltip lists them (MobileParty.SpeedExplained), plus the face the party stands on and whether it is a road cell.
        internal static Dictionary<string, object> Speed(MobileParty party)
        {
            var row = new Dictionary<string, object>();
            try
            {
                var model = Campaign.Current.Models.PartySpeedCalculatingModel;
                var b = model.CalculateBaseSpeed(party, true, 0, 0);
                var f = model.CalculateFinalSpeed(party, b);
                row["base"] = b.ResultNumber; row["final"] = f.ResultNumber;
                row["lines"] = Lines(f);
                row["face"] = FaceTypeAt(party.Position);
                var roads = GeneratedPartySpeedModel.Active;
                var at = party.Position.ToVec2();
                row["roadCell"] = roads == null ? null : (object)roads.At(at.X, at.Y);
                row["position"] = P(at);
            }
            catch (Exception e) { row["error"] = e.GetType().Name + ": " + e.Message; }
            return row;
        }

        // ExplainedNumber.GetLines (List of (name, value)) read by reflection, so no tuple type is compiled in.
        internal static List<object> Lines(ExplainedNumber number)
        {
            var list = new List<object>();
            object boxed = number;
            var lines = boxed.GetType().GetMethod("GetLines", BindingFlags.Instance | BindingFlags.Public);
            var result = lines == null ? null : lines.Invoke(boxed, new object[0]) as IEnumerable;
            if (result == null) return list;
            foreach (var item in result)
            {
                var t = item.GetType();
                var name = t.GetField("Item1"); var value = t.GetField("Item2");
                list.Add(new object[] { name == null ? null : Convert.ToString(name.GetValue(item), CultureInfo.InvariantCulture), value == null ? null : F(Convert.ToDouble(value.GetValue(item), CultureInfo.InvariantCulture)) });
            }
            return list;
        }

        internal sealed class FacePoint { public string Type, Under; public double X, Y; public int Neighbours; public int Face; }   // Under: a Road point's own face type

        // Every navmesh face of the loaded map (canonical images only on a periodic map) bucketed by face type in a 3-unit hash; per type the
        // `keep` face centres with the most same-type faces in their 3 x 3 buckets (interior faces first), and the road-cell faces likewise.
        // score (when given) replaces that count as the interior measure (the probe's: same-kind travel cells around the face).
        internal static IEnumerator FacePoints(ProbeContext context, Dictionary<string, object> record, Dictionary<string, List<FacePoint>> byType, int keep, Func<FacePoint, int> score = null)
        {
            var scene = MapScene(); var wrapper = Campaign.Current.MapSceneWrapper; var periodic = PeriodicWorld.Current; var roads = GeneratedPartySpeedModel.Active;
            int n = scene.GetNavMeshFaceCount();
            var all = new List<FacePoint>(); var buckets = new Dictionary<long, Dictionary<string, int>>();
            Func<double, double, long> key = (x, y) => ((long)Math.Floor(x / 3) << 32) ^ (long)(uint)(int)Math.Floor(y / 3);
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                var face = scene.GetNavMeshPathFaceRecord(i);
                var centre = Vec3.Zero; scene.GetNavMeshCenterPosition(i, ref centre);
                if (periodic != null && !periodic.IsCanonical(centre.x)) { if (i % 20000 == 0) yield return null; continue; }
                string type = wrapper.GetFaceTerrainType(face).ToString();
                if (roads != null && roads.At(centre.x, centre.y)) { var road = new FacePoint { Type = "Road", Under = type, X = centre.x, Y = centre.y, Face = i }; all.Add(road); Bump(buckets, key(centre.x, centre.y), "Road"); }
                all.Add(new FacePoint { Type = type, X = centre.x, Y = centre.y, Face = i });
                Bump(buckets, key(centre.x, centre.y), type);
                int c; counts.TryGetValue(type, out c); counts[type] = c + 1;
                if (i % 20000 == 0) yield return null;
            }
            foreach (var p in all)
            {
                int sum = 0;
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++)
                {
                    Dictionary<string, int> b;
                    if (buckets.TryGetValue(key(p.X + dx * 3, p.Y + dy * 3), out b)) { int v; if (b.TryGetValue(p.Type, out v)) sum += v; }
                }
                p.Neighbours = score == null ? sum : score(p);
            }
            foreach (var g in all.GroupBy(p => p.Type)) byType[g.Key] = g.OrderByDescending(p => p.Neighbours).ThenBy(p => p.Face).Take(keep).ToList();
            record["faces"] = n; record["facesByType"] = counts; record["roadFaces"] = all.Count(p => p.Type == "Road");
        }

        private static void Bump(Dictionary<long, Dictionary<string, int>> buckets, long k, string type)
        {
            Dictionary<string, int> b; if (!buckets.TryGetValue(k, out b)) buckets[k] = b = new Dictionary<string, int>(StringComparer.Ordinal);
            int v; b.TryGetValue(type, out v); b[type] = v + 1;
        }

        // A point of the list that is valid land for a party, away from every settlement of the world by `clearance` (periodic distance), and
        // that `accept` takes (when given).
        internal static FacePoint FreeLand(IEnumerable<FacePoint> points, string prefix, double clearance, Func<FacePoint, bool> accept = null)
        {
            return FreeLands(points, prefix, clearance, accept).FirstOrDefault();
        }

        internal static IEnumerable<FacePoint> FreeLands(IEnumerable<FacePoint> points, string prefix, double clearance, Func<FacePoint, bool> accept = null)
        {
            if (points == null) yield break;
            var settlements = Settlement.All.Where(s => prefix == null || s.StringId.StartsWith(prefix, StringComparison.Ordinal)).SelectMany(s => new[] { s.Position.ToVec2(), s.GatePosition.ToVec2() }).ToList();
            foreach (var p in points)
            {
                if (accept != null && !accept(p)) continue;
                var v = new Vec2((float)p.X, (float)p.Y);
                var c = new CampaignVec2(v, true);
                if (!c.IsValid() || GeneratedSpawnAssertions.Problem(c, MobileParty.NavigationType.Default) != null) continue;
                if (settlements.Any(s => Distance(s, v) < clearance)) continue;
                yield return p;
            }
        }

        internal static float Distance(Vec2 a, Vec2 b) { var periodic = PeriodicWorld.Current; return periodic != null ? periodic.Distance(a, b) : a.Distance(b); }

        // The main party at a point (held, sheltered); the point it stood at is returned.
        internal static CampaignVec2 Place(Vec2 at)
        {
            var main = MobileParty.MainParty; var was = main.Position;
            main.SetMoveModeHold();
            main.Position = new CampaignVec2(at, true);
            return was;
        }

        // ---------------------------------------------------------------- the live census

        private static bool Flag(object o, string property)
        {
            var p = o == null ? null : o.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return p != null && p.PropertyType == typeof(bool) && (bool)p.GetValue(o);
        }

        private static string Id(MBObjectBase o) { return o == null ? null : o.StringId; }
        private static string Name(TaleWorlds.Localization.TextObject t) { return t == null ? null : t.ToString(); }

        // Every generated settlement, kingdom, clan and hero of the world (prefix; null: all), with what the summarizer compares against the
        // compiled campaign: owners, bound villages, village types, ports, wall levels, houses and their members, minor clans and their flags,
        // wanderers (name, sex, hired), lord traits, children; and the population behaviour's own census (MMR-281).
        internal static Dictionary<string, object> Census(string prefix)
        {
            var row = new Dictionary<string, object> { ["day"] = CampaignTime.Now.ToDays, ["prefix"] = prefix };
            Func<MBObjectBase, bool> mine = o => o != null && (prefix == null || o.StringId.StartsWith(prefix, StringComparison.Ordinal));
            Step(row, "towns", () => Settlement.All.Where(s => s.IsTown && mine(s)).OrderBy(s => s.StringId, StringComparer.Ordinal).Select(s => (object)Fief(s)).ToList());
            Step(row, "castles", () => Settlement.All.Where(s => s.IsCastle && mine(s)).OrderBy(s => s.StringId, StringComparer.Ordinal).Select(s => (object)Fief(s)).ToList());
            Step(row, "villages", () => Settlement.All.Where(s => s.IsVillage && mine(s)).OrderBy(s => s.StringId, StringComparer.Ordinal).Select(s => (object)new Dictionary<string, object> {
                ["id"] = s.StringId, ["name"] = Name(s.Name), ["culture"] = Id(s.Culture), ["bound"] = s.Village.Bound == null ? null : s.Village.Bound.StringId,
                ["type"] = s.Village.VillageType == null ? null : s.Village.VillageType.StringId, ["hearth"] = F(s.Village.Hearth), ["state"] = s.Village.VillageState.ToString(),
                ["port"] = s.HasPort, ["items"] = Items(s), ["position"] = P(s.Position.ToVec2()), ["kingdom"] = s.MapFaction == null ? null : s.MapFaction.StringId }).ToList());
            Step(row, "hideouts", () => Settlement.All.Where(s => s.IsHideout && mine(s)).OrderBy(s => s.StringId, StringComparer.Ordinal).Select(s => (object)new Dictionary<string, object> {
                ["id"] = s.StringId, ["culture"] = Id(s.Culture), ["position"] = P(s.Position.ToVec2()), ["active"] = s.Hideout != null && s.Hideout.IsInfested,
                ["parties"] = MobileParty.All.Where(p => p.IsActive && p.IsBandit && (p.CurrentSettlement == s || p.HomeSettlement == s)).Select(p => (object)(p.StringId + "|" + Id(p.ActualClan) + "|" + Id(p.ActualClan == null ? null : p.ActualClan.Culture))).Take(12).ToList() }).ToList());
            Step(row, "kingdoms", () => Kingdom.All.Where(k => mine(k)).OrderBy(k => k.StringId, StringComparer.Ordinal).Select(k => (object)new Dictionary<string, object> {
                ["id"] = k.StringId, ["name"] = Name(k.Name), ["culture"] = Id(k.Culture), ["eliminated"] = k.IsEliminated, ["rulingClan"] = Id(k.RulingClan), ["ruler"] = Id(k.Leader),
                ["clans"] = k.Clans.Select(c => (object)c.StringId).ToList(), ["fiefs"] = k.Fiefs.Count, ["towns"] = k.Fiefs.Count(f => f.IsTown), ["castles"] = k.Fiefs.Count(f => f.IsCastle),
                ["warsWith"] = Kingdom.All.Where(o => o != k && !o.IsEliminated && k.IsAtWarWith(o)).Select(o => (object)o.StringId).ToList(),
                ["mercenaries"] = k.Clans.Where(c => c.IsUnderMercenaryService).Select(c => (object)c.StringId).ToList() }).ToList());
            Step(row, "clans", () => Clan.All.Where(c => mine(c)).OrderBy(c => c.StringId, StringComparer.Ordinal).Select(c => (object)ClanRow(c)).ToList());
            Step(row, "heroes", () => Hero.AllAliveHeroes.Concat(Hero.DeadOrDisabledHeroes).Where(h => mine(h)).OrderBy(h => h.StringId, StringComparer.Ordinal).Select(h => (object)HeroRow(h)).ToList());
            Step(row, "wanderers", () => Hero.AllAliveHeroes.Where(h => h.IsWanderer).OrderBy(h => h.StringId, StringComparer.Ordinal).Select(h => (object)new Dictionary<string, object> {
                ["id"] = h.StringId, ["name"] = Name(h.Name), ["firstName"] = Name(h.FirstName), ["female"] = h.IsFemale, ["template"] = h.Template == null ? null : h.Template.StringId,
                ["culture"] = Id(h.Culture), ["settlement"] = h.CurrentSettlement == null ? null : h.CurrentSettlement.StringId, ["hired"] = h.CompanionOf != null, ["generated"] = mine(h) }).ToList());
            Step(row, "traits", () => Traits(Hero.AllAliveHeroes.Where(h => mine(h) && h.IsLord && h.Clan != null && !h.Clan.IsMinorFaction).ToList()));
            Step(row, "population", () => GeneratedPopulationBehavior.Census());
            Step(row, "counts", () => new Dictionary<string, object> { ["parties"] = MobileParty.All.Count, ["activeParties"] = MobileParty.All.Count(p => p.IsActive), ["settlements"] = Settlement.All.Count,
                ["aliveHeroes"] = Hero.AllAliveHeroes.Count, ["kingdoms"] = Kingdom.All.Count(k => !k.IsEliminated), ["clans"] = Clan.All.Count });
            return row;
        }

        private static void Step(Dictionary<string, object> row, string name, Func<object> read)
        {
            try { row[name] = read(); }
            catch (Exception e) { row[name] = new Dictionary<string, object> { ["error"] = e.GetType().Name + ": " + e.Message }; }
        }

        private static int Items(Settlement s)
        {
            int n = 0;
            for (int i = 0; i < s.ItemRoster.Count; i++) n += Math.Max(0, s.ItemRoster.GetElementCopyAtIndex(i).Amount);
            return n;
        }

        private static Dictionary<string, object> Fief(Settlement s)
        {
            var t = s.Town;
            return new Dictionary<string, object> { ["id"] = s.StringId, ["name"] = Name(s.Name), ["culture"] = Id(s.Culture), ["kingdom"] = s.MapFaction == null ? null : s.MapFaction.StringId,
                ["owner"] = Id(s.OwnerClan), ["port"] = s.HasPort, ["villages"] = s.BoundVillages.Select(v => (object)v.Settlement.StringId).ToList(),
                ["wall"] = t == null ? -1 : t.GetWallLevel(), ["prosperity"] = t == null ? null : F(t.Prosperity), ["food"] = t == null ? null : F(t.FoodStocks), ["gold"] = t == null ? 0 : t.Gold,
                ["loyalty"] = t == null ? null : F(t.Loyalty), ["security"] = t == null ? null : F(t.Security), ["position"] = P(s.Position.ToVec2()), ["gate"] = P(s.GatePosition.ToVec2()),
                ["items"] = Items(s), ["notables"] = s.Notables.Count, ["underSiege"] = s.SiegeEvent != null };
        }

        private static Dictionary<string, object> ClanRow(Clan c)
        {
            var alive = c.Heroes.Where(h => h.IsAlive).ToList();
            return new Dictionary<string, object> { ["id"] = c.StringId, ["name"] = Name(c.Name), ["kingdom"] = Id(c.Kingdom), ["culture"] = Id(c.Culture), ["leader"] = Id(c.Leader),
                ["home"] = c.HomeSettlement == null ? null : c.HomeSettlement.StringId, ["tier"] = c.Tier, ["eliminated"] = c.IsEliminated,
                ["minor"] = c.IsMinorFaction, ["mercenary"] = Flag(c, "IsClanTypeMercenary"), ["underMercenaryService"] = c.IsUnderMercenaryService, ["outlaw"] = Flag(c, "IsOutlaw"),
                ["bandit"] = c.IsBanditFaction, ["nomad"] = Flag(c, "IsNomad"), ["mafia"] = Flag(c, "IsMafia"), ["sect"] = Flag(c, "IsSect"),
                ["fiefs"] = c.Fiefs.Select(f => (object)f.Settlement.StringId).ToList(),
                ["alive"] = alive.Count, ["adults"] = alive.Count(h => !h.IsChild), ["children"] = alive.Count(h => h.IsChild), ["female"] = alive.Count(h => h.IsFemale && !h.IsChild),
                ["male"] = alive.Count(h => !h.IsFemale && !h.IsChild), ["members"] = alive.Select(h => (object)h.StringId).ToList() };
        }

        private static Dictionary<string, object> HeroRow(Hero h)
        {
            return new Dictionary<string, object> { ["id"] = h.StringId, ["name"] = Name(h.Name), ["firstName"] = Name(h.FirstName), ["clan"] = Id(h.Clan), ["female"] = h.IsFemale,
                ["age"] = F(h.Age), ["alive"] = h.IsAlive, ["child"] = h.IsChild, ["occupation"] = h.Occupation.ToString(), ["minorFactionHero"] = h.IsMinorFactionHero,
                ["companionOf"] = Id(h.CompanionOf), ["father"] = Id(h.Father), ["mother"] = Id(h.Mother), ["spouse"] = Id(h.Spouse) };
        }

        // Each lord's personality traits (honor, valor, mercy, generosity, calculating) counted per value, and the distinct vectors.
        private static Dictionary<string, object> Traits(List<Hero> lords)
        {
            var traits = new[] { DefaultTraits.Honor, DefaultTraits.Valor, DefaultTraits.Mercy, DefaultTraits.Generosity, DefaultTraits.Calculating };
            var result = new Dictionary<string, object> { ["lords"] = lords.Count };
            foreach (var t in traits) result[t.StringId] = lords.GroupBy(h => h.GetTraitLevel(t)).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => (object)g.Count());
            result["distinctVectors"] = lords.Select(h => String.Join(",", traits.Select(t => h.GetTraitLevel(t).ToString(CultureInfo.InvariantCulture)))).Distinct().Count();
            return result;
        }

        // The economy of the towns and villages (prefix; null: all): totals and means of prosperity, food, gold, hearth and village stock.
        internal static Dictionary<string, object> Economy(string prefix)
        {
            var row = new Dictionary<string, object> { ["day"] = CampaignTime.Now.ToDays };
            try
            {
                Func<Settlement, bool> mine = s => prefix == null || s.StringId.StartsWith(prefix, StringComparison.Ordinal);
                var towns = Settlement.All.Where(s => s.IsTown && mine(s)).ToList();
                var villages = Settlement.All.Where(s => s.IsVillage && mine(s)).ToList();
                row["towns"] = towns.Count; row["villages"] = villages.Count;
                row["meanTownProsperity"] = towns.Count == 0 ? null : F(towns.Average(s => s.Town.Prosperity));
                row["meanTownFood"] = towns.Count == 0 ? null : F(towns.Average(s => s.Town.FoodStocks));
                row["meanTownGold"] = towns.Count == 0 ? null : F(towns.Average(s => (double)s.Town.Gold));
                row["meanTownMarketItems"] = towns.Count == 0 ? null : F(towns.Average(s => (double)Items(s)));
                row["meanVillageHearth"] = villages.Count == 0 ? null : F(villages.Average(s => s.Village.Hearth));
                row["meanVillageStock"] = villages.Count == 0 ? null : F(villages.Average(s => (double)Items(s)));
                row["villagesLooted"] = villages.Count(s => s.Village.VillageState != Village.VillageStates.Normal);
                row["townProsperity"] = towns.OrderBy(s => s.StringId, StringComparer.Ordinal).ToDictionary(s => s.StringId, s => F(s.Town.Prosperity));
                row["villageHearth"] = villages.OrderBy(s => s.StringId, StringComparer.Ordinal).ToDictionary(s => s.StringId, s => F(s.Village.Hearth));
            }
            catch (Exception e) { row["error"] = e.GetType().Name + ": " + e.Message; }
            return row;
        }

        // RT-27: the face types each presentation culture's fiefs and villages stand on (the fief point and a ring of 8 at 4 units).
        internal static Dictionary<string, object> HomeTerrain(string prefix)
        {
            var row = new Dictionary<string, object>();
            try
            {
                foreach (var g in Settlement.All.Where(s => (s.IsTown || s.IsCastle || s.IsVillage) && s.StringId.StartsWith(prefix, StringComparison.Ordinal) && s.Culture != null).GroupBy(s => s.Culture.StringId).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
                    foreach (var s in g)
                        foreach (var p in Map2AcceptancePlan.Ring(s.Position.ToVec2().X, s.Position.ToVec2().Y, 4, 8))
                        {
                            string t = TerrainAt(new Vec2((float)p[0], (float)p[1]));
                            int n; counts.TryGetValue(t, out n); counts[t] = n + 1;
                        }
                    row[g.Key] = new Dictionary<string, object> { ["settlements"] = g.Count(), ["faces"] = counts };
                }
            }
            catch (Exception e) { row["error"] = e.GetType().Name + ": " + e.Message; }
            return row;
        }

        // ---------------------------------------------------------------- map incidents (acc4 triage notrun-4)

        // A map incident: 1.4.8's IncidentsCampaignBehaviour.TryInvokeIncident sets MapState.NextIncident, Campaign.Tick starts it, and
        // GauntletMapIncidentView stops time, locks the time control and pauses the engine until it is confirmed. A due one is dropped before
        // the tick shows it; an open one is confirmed as a player confirms it (its first option selected, MapIncidentVM.ExecuteConfirm, whose
        // close removes the view: time control unlocked, engine unpaused), else its view removed. What was done goes to record["incidents"].
        internal static bool ResolveIncident(Dictionary<string, object> record)
        {
            bool did = false;
            try
            {
                var map = Game.Current == null ? null : Game.Current.GameStateManager.ActiveState as MapState;
                if (map == null) return false;
                if (map.NextIncident != null) { Note(record, "dropped due incident " + map.NextIncident.StringId); map.NextIncident = null; did = true; }
                if (IncidentOpen()) { Note(record, ConfirmIncident()); did = true; }
            }
            catch (Exception e) { Note(record, "error " + e.GetType().Name + ": " + e.Message); }
            return did;
        }

        private static void Note(Dictionary<string, object> record, string what)
        {
            if (record == null) return;
            var list = record.ContainsKey("incidents") ? record["incidents"] as List<object> : null;
            if (list == null) record["incidents"] = list = new List<object>();
            if (list.Count < 20) list.Add(what);
        }

        private static object MapScreenInstance()
        {
            var screen = AccessTools.TypeByName("SandBox.View.Map.MapScreen");
            var p = screen == null ? null : AccessTools.Property(screen, "Instance");
            return p == null ? null : p.GetValue(null);
        }

        internal static bool IncidentOpen()
        {
            try { var s = MapScreenInstance(); var p = s == null ? null : AccessTools.Property(s.GetType(), "IsMapIncidentActive"); return p != null && (bool)p.GetValue(s); }
            catch (Exception) { return false; }
        }

        private static string ConfirmIncident()
        {
            var screen = MapScreenInstance();
            var viewType = AccessTools.TypeByName("SandBox.View.Map.MapIncidentView");
            var screenType = AccessTools.TypeByName("SandBox.View.Map.MapScreen");
            var get = screenType == null || viewType == null ? null : screenType.GetMethods(Members).FirstOrDefault(m => m.Name == "GetMapView" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            var view = get == null || screen == null ? null : get.MakeGenericMethod(viewType).Invoke(screen, new object[0]);
            if (view == null) return "an incident is open but no MapIncidentView was found";
            var vmField = AccessTools.Field(view.GetType(), "_dataSource");
            var vm = vmField == null ? null : vmField.GetValue(view);
            string id = null;
            try { var f = vm == null ? null : AccessTools.Field(vm.GetType(), "_incident"); var inc = f == null ? null : f.GetValue(vm) as MBObjectBase; id = inc == null ? null : inc.StringId; } catch (Exception) { }
            if (vm != null)
            {
                var selected = AccessTools.Property(vm.GetType(), "SelectedOption");
                var optionsProperty = AccessTools.Property(vm.GetType(), "Options");
                var options = optionsProperty == null ? null : optionsProperty.GetValue(vm) as IList;
                if (selected != null && selected.GetValue(vm) == null && options != null && options.Count > 0)
                {
                    var pick = AccessTools.Method(vm.GetType(), "OnOptionSelected");
                    if (pick != null) pick.Invoke(vm, new[] { options[0] }); else selected.SetValue(vm, options[0]);
                }
                var confirm = AccessTools.Method(vm.GetType(), "ExecuteConfirm");
                if (confirm != null) { confirm.Invoke(vm, new object[0]); return "confirmed incident " + id + " (option 0)"; }
            }
            var remove = screenType.GetMethods(Members).FirstOrDefault(m => m.Name == "RemoveMapView" && m.GetParameters().Length == 1);
            if (remove != null) { remove.Invoke(screen, new[] { view }); return "removed the incident view of " + id + " (no confirm)"; }
            return "an incident is open and could not be confirmed";
        }

        // ---------------------------------------------------------------- campaign time

        // Campaign time run in fast forward for `days` (or until `until` holds), the player sheltered and holding with grain; an encounter the
        // player is pulled into is left as the player leaves it (conversation ended first, then the map conversation, then the encounter, as
        // TwoWorldMatrixProbe.ClearEncounter does), a map incident confirmed (ResolveIncident), inquiries hidden, menus left, an open
        // encyclopedia closed; a stalled clock (15 s without
        // progress) is restarted and counted. The advance ends early, recorded as frozen with the time state, after FrozenStalls stalls in a row
        // (2 minutes without campaign time moving), and in any case after SecondsPerDay per requested day plus 2 minutes. perDay runs once per new
        // campaign day (engine thread, outside any dispatch).
        private const int FrozenStalls = 8;
        private const double SecondsPerDay = 40;
        internal static IEnumerator AdvanceDays(ProbeContext context, Dictionary<string, object> report, double days, Func<bool> until, Action<int> perDay)
        {
            var campaign = Campaign.Current; var main = MobileParty.MainParty;
            main.IgnoreByOtherPartiesTill(CampaignTime.Now + CampaignTime.Days((float)days + 30));
            if (main.CurrentSettlement == null && PlayerEncounter.Current == null) main.SetMoveModeHold();
            var grain = MBObjectManager.Instance.GetObject<ItemObject>("grain");
            int grainAdded = grain == null ? 0 : Math.Max(20, (int)Math.Ceiling(days * Math.Max(1, main.MemberRoster.TotalManCount) / 8.0) + 20);
            if (grain != null) main.ItemRoster.AddToCounts(grain, grainAdded);
            double start = CampaignTime.Now.ToDays, begun = context.Seconds, lastProgress = context.Seconds, last = start;
            int stalls = 0, inRow = 0, lastDay = (int)Math.Floor(start), encounters = 0, incidents = 0;
            bool frozen = false;
            var cleared = new Dictionary<string, object>();
            double cap = SecondsPerDay * Math.Max(1, days) + 120;
            DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward);
            while (CampaignTime.Now.ToDays - start < days && context.Seconds - begun < cap && (until == null || !until()))
            {
                double now = CampaignTime.Now.ToDays;
                if (Game.Current.GameStateManager.ActiveState is MapState)
                {
                    if (ResolveIncident(cleared)) incidents++;
                    if (PlayerEncounter.Current != null || Campaign.Current.ConversationManager.IsConversationInProgress) { encounters++; TwoWorldMatrixProbe.ClearEncounter(cleared); }
                    else TwoWorldMatrixProbe.ExitMenu(cleared);
                    if (InformationManager.IsAnyInquiryActive()) InformationManager.HideInquiry();
                    string encyclopedia = CloseEncyclopediaIfOpen();
                    if (encyclopedia != null) cleared["encyclopedia"] = encyclopedia;
                }
                if (now > last) { last = now; lastProgress = context.Seconds; inRow = 0; }
                else if (context.Seconds - lastProgress > 15)
                {
                    stalls++; inRow++; lastProgress = context.Seconds;
                    if (report.ContainsKey("stallStates") == false) report["stallStates"] = new List<object>();
                    var list = (List<object>)report["stallStates"]; if (list.Count < 10) list.Add(SafeTimeState(context));
                    if (inRow >= FrozenStalls) { frozen = true; report["frozenState"] = SafeTimeState(context); break; }
                    DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward);
                }
                if (campaign.TimeControlMode == CampaignTimeControlMode.Stop) DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward);
                int day = (int)Math.Floor(now);
                if (day > lastDay) { lastDay = day; if (perDay != null) { try { perDay(day); } catch (Exception e) { report["perDayError"] = e.GetType().Name + ": " + e.Message; } } }
                yield return null;
            }
            DiagnosticSandboxBootstrap.Pause(campaign);
            report["daysRequested"] = days; report["daysAdvanced"] = Math.Round(CampaignTime.Now.ToDays - start, 2); report["realSeconds"] = Math.Round(context.Seconds - begun, 1);
            report["stalls"] = stalls; report["frozen"] = frozen; report["capSeconds"] = cap; report["capReached"] = !frozen && context.Seconds - begun >= cap; report["grainAdded"] = grainAdded; report["encountersCleared"] = encounters; report["incidentsResolved"] = incidents; report["cleared"] = cleared;
            report["playerPrisoner"] = Hero.MainHero.IsPrisoner; report["playerTroops"] = main.MemberRoster.TotalManCount; report["playerFood"] = F(main.Food);
            report["untilMet"] = until != null && until();
        }

        internal static object SafeTimeState(ProbeContext context)
        {
            try { return TwoWorldMatrixProbe.TimeState(context.Seconds); }
            catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
        }

        // ---------------------------------------------------------------- the build that runs

        internal static Dictionary<string, object> Build()
        {
            var row = new Dictionary<string, object>();
            try
            {
                string assembly = typeof(SubModule).Assembly.Location, bin = Path.GetDirectoryName(assembly);
                row["moduleAssembly"] = assembly; row["moduleAssemblySha256"] = Diagnostics.GenerationProbe.Hash(assembly);
                foreach (var name in new[] { "worldbridge.exe", "fwg.exe" }) { string p = Path.Combine(bin, name); row[name] = File.Exists(p) ? Diagnostics.GenerationProbe.Hash(p) : null; }
                string engine = Path.GetDirectoryName(typeof(Scene).Assembly.Location);
                row["nativeBinaries"] = new[] { "TaleWorlds.Native.dll", "TaleWorlds.MountAndBlade.dll", "Bannerlord.exe" }.ToDictionary(n => n, n => { string p = Path.Combine(engine, n); return (object)(File.Exists(p) ? Diagnostics.GenerationProbe.Hash(p) : null); });
                row["game"] = GeneratedPatches.InstalledVersions();
                row["modules"] = Utilities.GetModulesNames();
                var docs = new SortedDictionary<string, object>(StringComparer.Ordinal);
                foreach (var resource in typeof(SubModule).Assembly.GetManifestResourceNames().Where(n => n.EndsWith(".json", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
                    using (var stream = typeof(SubModule).Assembly.GetManifestResourceStream(resource))
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                        docs[resource] = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                row["embeddedPolicies"] = docs;
                row["forceVsyncSetFalse"] = false;
            }
            catch (Exception e) { row["error"] = e.GetType().Name + ": " + e.Message; }
            return row;
        }
    }
}
