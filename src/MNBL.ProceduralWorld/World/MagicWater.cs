using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MNBL.ProceduralWorld.World;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-290: magic water, the vein graph's water stamps (docs/magic-water.md; the route: C:\Users\davgo\m2\results\MMR-290-route.md, recorded
    // in the doc). Pure managed and deterministic: MagicWaterRuntime is the engine side (an IMagicVeinLayer of MagicGroundRuntime).
    //   * Stamps: MMR-288's graph stamps with OnWater (their owner node is on water, or their centre is), so a vein that runs from land into a sea,
    //     lake or river carries on across the shore with the same ids, kinds, boxes and reveal times as the ground stamps beside it.
    //   * Where a stamp draws (its mode, by its centre): "sea" where the ground lies below the datum (the sea plane shows) and "lake" inside a shown
    //     lake plane's box above its ground (GeneratedWaterSystem.Lakes, the plane's z): the decal box is centred on that plane with a thin half
    //     depth, so the campaign map's water shader (water_surface_worldmap.rs, use_decals) draws it on the water surface, additively, and the
    //     terrain path paints only the shore band within the half depth. "bed" on any other water travel cell (a river: rivers have no plane; a sea
    //     or lake cell no plane shows, MMR-285's fill stamps): centred on the ground with the slope depth, over the river and fill stamps.
    //     "coast": a stamp whose centre lies on land beside the water it crosses into (its footprint reaches water), centred on the ground (it
    //     joins the land veins; its box may reach the plane). "inland": a stamp the graph counts as water only because its owner sample has a
    //     water core (an ocean or lake biome sample on a land travel cell, above the datum, outside every lake plane) and whose footprint reaches
    //     no water: it is land, so it draws with the look's ground material on the terrain, closing the vein. Glow (fallback F2 only): a
    //     ground-material tip at each shore crossing on its land side.
    //   * Material: roads.json magicWater material (one decal material for every school; atlas 4 columns by kind x 8 rows by look school). Until it
    //     is published, each stamp falls back to MMR-288's ground material of its look (its 4 x 4 cell: row by the owner node's core group, else
    //     fallback.row); a stamp neither resolves for is not drawn (counted), never thrown. MMR-247 (D): neither material's textures have a rect in
    //     the campaign map's decal atlas (World/DecalAtlas.cs), so each carries the vein stamps' vanilla fallback by kind (roads.json
    //     magic.fallback, with the policy's opacity), which the engine target draws instead.
    //   * Modes (policy): surface (the route); bed (F1: sea and lake stamps only over a bed at most bedMaxDepth below the plane, centred on the bed);
    //     shore (F1 + F2).
    //   * Budget: at most maxDecals stamps (past it the thinnest outermost generated ones go; live stamps and bursts stay); every stamp is drawn
    //     once, at its periodic image nearest the camera (seam nearestImage, as the ground stamps).
    internal static class MagicWaterMode
    {
        public const string Sea = "sea", Lake = "lake", Bed = "bed", Coast = "coast", Inland = "inland", Glow = "glow";
        public static readonly string[] All = { Sea, Lake, Bed, Coast, Inland, Glow };
        // Drawn with the look's ground material (not the magic water material): inland stamps and F2's shore glow.
        public static bool Ground(string mode) { return mode == Inland || mode == Glow; }
    }

    internal sealed class MagicWaterStamp
    {
        public string Id, Kind, Mode, Look, School; public int Node, Zone, Order, Edge = -1, AtlasRow, AtlasColumn; public bool Live;
        public double RevealAt = Double.NegativeInfinity;
        public double PlaneZ = Double.NaN;   // sea and lake: the plane's z, the box centre's; NaN: the terrain height at the centre (bed, coast, glow)
        public double Ground, Depth = Double.NaN;   // the ground at the centre; the plane's z less it (sea and lake)
        public RoadDecal Decal;              // the magic water material (glow: the ground material)
        public RoadDecal Fallback;           // the look's ground material until the magic water material is published (null for glow)
        // The stamp's identity and everything drawn: a stamp with the same key is not redrawn.
        public string Key
        {
            get
            {
                Func<double, string> f = v => v.ToString("R", CultureInfo.InvariantCulture);
                return Id + ":" + Mode + ":" + Decal.Material + ":" + f(Decal.Atlas[2]) + "," + f(Decal.Atlas[3]) + ":" + f(Decal.X) + "," + f(Decal.Y) + "," + f(PlaneZ) + "," + f(Decal.Yaw) + "," +
                       f(Decal.HalfLength) + "," + f(Decal.HalfWidth) + "," + f(Decal.HalfDepth) + (Fallback == null ? "" : ":" + Fallback.Material + "," + f(Fallback.Atlas[3]));
            }
        }
    }

    internal sealed class MagicWaterPlan
    {
        public const string Version = "mmr290/1";
        public MagicVeinGraph Graph; public string Mode;
        public readonly List<MagicWaterStamp> Stamps = new List<MagicWaterStamp>();
        public readonly SortedDictionary<string, int> ByMode = new SortedDictionary<string, int>(StringComparer.Ordinal), BySchool = new SortedDictionary<string, int>(StringComparer.Ordinal),
                                                      ByKind = new SortedDictionary<string, int>(StringComparer.Ordinal), Skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public int Generated, Live, Hidden, Thinned, GraphWaterStamps;
        public double Hours = Double.PositiveInfinity;
        public Dictionary<string, object> Record()
        {
            return new Dictionary<string, object> { ["version"] = Version, ["mode"] = Mode, ["stamps"] = Stamps.Count, ["graphWaterStamps"] = GraphWaterStamps, ["generated"] = Generated, ["live"] = Live,
                ["hiddenLive"] = Hidden, ["thinned"] = Thinned, ["byMode"] = ByMode, ["byKind"] = ByKind, ["bySchool"] = BySchool, ["skipped"] = Skipped };
        }
        public string Sha256()
        {
            var b = new StringBuilder(Version + " " + Mode + "\n");
            foreach (var s in Stamps) b.Append(s.Key).Append('\n');
            return Json.Sha(b.ToString());
        }
        public MagicWaterPlan Revealed(double hours)
        {
            var r = new MagicWaterPlan { Graph = Graph, Mode = Mode, Thinned = Thinned, GraphWaterStamps = GraphWaterStamps, Hours = hours };
            foreach (var kv in Skipped) r.Skipped[kv.Key] = kv.Value;
            foreach (var s in Stamps) if (s.RevealAt <= hours) r.Stamps.Add(s); else r.Hidden++;
            r.Tally();
            return r;
        }
        public double NextReveal(double hours)
        {
            double next = Double.PositiveInfinity;
            foreach (var s in Stamps) if (s.RevealAt > hours && s.RevealAt < next) next = s.RevealAt;
            return next;
        }
        internal void Tally()
        {
            Generated = Live = 0; ByMode.Clear(); BySchool.Clear(); ByKind.Clear();
            foreach (var s in Stamps)
            {
                if (s.Live) Live++; else Generated++;
                MagicGround.Count(ByMode, s.Mode); MagicGround.Count(BySchool, s.Look); MagicGround.Count(ByKind, s.Kind);
            }
        }
    }

    internal static class MagicWater
    {
        public const double ShoreSteps = 16;   // glow: the shore point is found along a crossing edge in this many steps

        // What a physical point shows: "sea" / "lake" (planeZ the plane's z), "bed" (a water travel cell no plane shows) or "land".
        public static string Cover(MagicGroundSource src, IList<GeneratedWaterSystem.Lake> lakes, double x, double y, out double planeZ, out double ground)
        {
            ground = src.Ground == null ? Double.NaN : src.Ground(x, y);
            planeZ = Double.NaN;
            if (ground < GeneratedWaterSystem.DatumZ) { planeZ = GeneratedWaterSystem.DatumZ; return MagicWaterMode.Sea; }
            if (lakes != null)
                foreach (var l in lakes)
                    if (x >= l.MinX && x <= l.MaxX && y >= l.MinY && y <= l.MaxY && l.Z > ground) { planeZ = l.Z; return MagicWaterMode.Lake; }
            int cell = src.CellAt(x, y);
            return cell >= 0 && TravelCells.Water(src.Cells.Classes[cell]) ? MagicWaterMode.Bed : "land";
        }

        // The nearest water point to a footprint's centre among the points of its box (9 x 5 over its half extents, yaw along u); NaN when none
        // of them is water.
        public static double Reach(MagicGroundSource src, IList<GeneratedWaterSystem.Lake> lakes, double x, double y, double yaw, double halfLength, double halfWidth)
        {
            double c = Math.Cos(yaw), s = Math.Sin(yaw), best = Double.NaN, planeZ, ground;
            for (int i = -4; i <= 4; i++)
                for (int j = -2; j <= 2; j++)
                {
                    double u = i / 4.0 * halfLength, v = j / 2.0 * halfWidth;
                    if (Cover(src, lakes, src.Canonical(x - src.Strip + u * c - v * s), y + u * s + v * c, out planeZ, out ground) == "land") continue;
                    double r = Math.Sqrt(u * u + v * v);
                    if (Double.IsNaN(best) || r < best) best = r;
                }
            return best;
        }

        // The water stamps of a vein graph, fully grown (Plan(...).Revealed(hours) is what a campaign time shows). lakes: the scene's shown lake
        // planes (MapDressingPlan.Lakes); none: lake stamps draw on the bed. dropped: the graph stamps of the components the ground overlay does
        // not draw (MagicGroundPlan.Dropped of the plan made with this layer's declaration, IMagicWaterVeins): their water stamps are skipped too,
        // so no water piece below veins.minStamps draws alone.
        public static MagicWaterPlan Plan(MagicVeinGraph g, IList<GeneratedWaterSystem.Lake> lakes, RoadsPolicy.MagicWaterPolicy p, RoadsPolicy.MagicPolicy magic, ICollection<string> dropped = null)
        {
            var plan = new MagicWaterPlan { Graph = g, Mode = p.Mode };
            var src = g.Source;
            foreach (var st in g.Stamps)
            {
                if (!st.OnWater) continue;
                plan.GraphWaterStamps++;
                if (dropped != null && dropped.Contains(st.Id)) { MagicGround.Count(plan.Skipped, "dropped"); continue; }
                var node = g.Nodes[st.Node];
                double planeZ, ground;
                string mode = Cover(src, lakes, st.X, st.Y, out planeZ, out ground);
                if (mode == "land") mode = Double.IsNaN(Reach(src, lakes, st.X, st.Y, st.Yaw, st.Length / 2, st.Width / 2)) ? MagicWaterMode.Inland : MagicWaterMode.Coast;
                double depth = Double.IsNaN(planeZ) ? Double.NaN : planeZ - ground;
                if ((mode == MagicWaterMode.Sea || mode == MagicWaterMode.Lake) && p.Mode != RoadsPolicy.MagicWaterPolicy.Surface)
                {
                    // F1: a plane stamp only where the bed shows through shallow water, drawn on the bed.
                    if (depth > p.BedMaxDepth) { MagicGround.Count(plan.Skipped, "deep"); continue; }
                    mode = MagicWaterMode.Bed; planeZ = Double.NaN;
                }
                var s = new MagicWaterStamp { Id = st.Id, Kind = st.Kind, Mode = mode, Look = node.Look, School = node.School, Node = node.Index, Zone = node.Zone, Order = node.Order, Edge = st.Edge,
                    AtlasRow = p.SchoolRows[node.Look], AtlasColumn = st.AtlasColumn, Live = st.Live, RevealAt = st.RevealAt, PlaneZ = planeZ, Ground = ground, Depth = depth };
                double yaw = magic.FlipU ? st.Yaw + Math.PI : st.Yaw;
                int row = magic.FlipV ? p.Rows - 1 - s.AtlasRow : s.AtlasRow;
                s.Decal = new RoadDecal { Road = -1, Piece = -1, Family = "magicWater", Material = p.Material, X = st.X, Y = st.Y, Yaw = yaw, HalfLength = st.Length / 2, HalfWidth = p.WidthScale * st.Width / 2,
                    HalfDepth = p.HalfDepth, Atlas = new[] { 1f / p.Columns, 1f / p.Rows, st.AtlasColumn / (float)p.Columns, row / (float)p.Rows }, Tint = Tint(p.Opacity) };
                int groundRow = node.CoreRow >= 0 ? node.CoreRow : p.FallbackRow;
                s.Fallback = new RoadDecal { Road = -1, Piece = -1, Family = "magic", Material = p.FallbackPrefix + node.Look, X = st.X, Y = st.Y, Yaw = yaw, HalfLength = st.Length / 2, HalfWidth = st.Width / 2,
                    HalfDepth = p.HalfDepth, Atlas = new[] { .25f, .25f, st.AtlasColumn / 4f, (magic.FlipV ? 3 - groundRow : groundRow) / 4f } };
                // MMR-247 (D): neither module material has a rect in the decal atlas; each draws the vein stamps' vanilla fallback by kind.
                MagicGround.SetFallback(s.Decal, magic, st.Kind, node.Look, p.Opacity);
                MagicGround.SetFallback(s.Fallback, magic, st.Kind, node.Look, 1);
                if (mode == MagicWaterMode.Inland) { s.Decal = s.Fallback; s.Fallback = null; s.AtlasRow = groundRow; }
                plan.Stamps.Add(s);
            }
            if (p.Mode == RoadsPolicy.MagicWaterPolicy.Shore) Glow(plan, g, lakes, p, magic);
            // Past maxDecals, the thinnest outermost generated stamps go first (live stamps and bursts are always drawn), so no vein breaks midway.
            if (plan.Stamps.Count > p.MaxDecals)
            {
                int drop = plan.Stamps.Count - p.MaxDecals;
                var gone = new HashSet<MagicWaterStamp>(plan.Stamps.Where(x => !x.Live && x.Kind != VeinKind.Burst).OrderBy(x => x.Decal.HalfWidth).ThenByDescending(x => x.Order).ThenBy(x => x.Id, StringComparer.Ordinal).Take(drop));
                plan.Thinned = gone.Count;
                plan.Stamps.RemoveAll(gone.Contains);
            }
            // Bed, coast, inland and glow stamps project onto the ground: deep enough for the relief under their footprint (MMR-262), as the ground
            // stamps, and their boxes stay above the water planes their footprint shows (MMR-247's WaterPlanes: the Floor, 7 x 7 samples as the
            // map dressing's road and water decals; the engine target clips with it). Sea and lake boxes are centred on their plane on purpose (the
            // water shader draws them on the surface) and stay whole.
            var planes = new WaterPlanes(lakes);
            foreach (var s in plan.Stamps)
            {
                if (!Double.IsNaN(s.PlaneZ) || src.Ground == null) continue;
                var d = s.Decal;
                d.HalfDepth = MapDressing.SlopeDepth(src.Ground, d.X, d.Y, d.Yaw, d.HalfLength, d.HalfWidth);
                d.Floor = planes.Floor(d.X, d.Y, d.Yaw, d.HalfLength, d.HalfWidth, src.Ground, 7);
                if (s.Fallback == null) continue;
                s.Fallback.HalfDepth = MapDressing.SlopeDepth(src.Ground, d.X, d.Y, d.Yaw, s.Fallback.HalfLength, s.Fallback.HalfWidth);
                s.Fallback.Floor = planes.Floor(d.X, d.Y, d.Yaw, s.Fallback.HalfLength, s.Fallback.HalfWidth, src.Ground, 7);
            }
            plan.Tally();
            return plan;
        }

        // F2: at every shore crossing, a tip of the land node's ground material from the land node toward the water, its end (u 0.85) at the first
        // point of the edge that is water, so "magic reaches this water" shows on land whatever the water surface draws.
        private static void Glow(MagicWaterPlan plan, MagicVeinGraph g, IList<GeneratedWaterSystem.Lake> lakes, RoadsPolicy.MagicWaterPolicy p, RoadsPolicy.MagicPolicy magic)
        {
            var src = g.Source;
            foreach (var e in g.ShoreEdges())
            {
                var land = g.Nodes[g.Nodes[e.Parent].OnWater ? e.Child : e.Parent];
                var water = g.Nodes[g.Nodes[e.Parent].OnWater ? e.Parent : e.Child];
                if (land.CoreRow < 0) { MagicGround.Count(plan.Skipped, "glowNoRow"); continue; }
                double dx = g.DeltaX(land.X, water.X), dy = water.Y - land.Y, length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 1e-9) continue;
                double t = -1, planeZ, ground;
                for (int k = 0; k <= ShoreSteps; k++)
                    if (Cover(src, lakes, src.Canonical(land.X - src.Strip + dx * k / ShoreSteps), land.Y + dy * k / ShoreSteps, out planeZ, out ground) != "land") { t = k / ShoreSteps; break; }
                if (t < 0) { MagicGround.Count(plan.Skipped, "glowNoShore"); continue; }
                double yaw = Math.Atan2(dy, dx), back = (MagicVeins.TipEnd - 0.5) * magic.StampLength;
                double ex = land.X + dx * t, ey = land.Y + dy * t, cx = src.Canonical(ex - Math.Cos(yaw) * back - src.Strip), cy = ey - Math.Sin(yaw) * back;
                var zone = g.Zones[land.Zone];
                string id = (zone.Live ? "L" : "G") + zone.Key.ToString(CultureInfo.InvariantCulture) + "." + (land.Index - zone.FirstNode).ToString(CultureInfo.InvariantCulture) + ".glow" +
                            (water.Index - zone.FirstNode).ToString(CultureInfo.InvariantCulture);
                int row = magic.FlipV ? 3 - land.CoreRow : land.CoreRow;
                var s = new MagicWaterStamp { Id = id, Kind = VeinKind.Tip, Mode = MagicWaterMode.Glow, Look = land.Look, School = land.School, Node = land.Index, Zone = land.Zone, Order = Math.Max(land.Order, water.Order),
                    Edge = g.Edges.IndexOf(e), AtlasRow = land.CoreRow, AtlasColumn = magic.TipColumn, Live = land.Live, RevealAt = Math.Max(land.RevealAt, water.RevealAt) };
                Cover(src, lakes, cx, cy, out planeZ, out ground); s.Ground = ground;
                s.Decal = new RoadDecal { Road = -1, Piece = -1, Family = "magic", Material = p.FallbackPrefix + land.Look, X = cx, Y = cy, Yaw = magic.FlipU ? yaw + Math.PI : yaw, HalfLength = magic.StampLength / 2,
                    HalfWidth = land.Width / 2, HalfDepth = MagicGround.DecalHalfDepth, Atlas = new[] { .25f, .25f, magic.TipColumn / 4f, row / 4f } };
                MagicGround.SetFallback(s.Decal, magic, VeinKind.Tip, land.Look, 1);   // MMR-247 (D)
                plan.Stamps.Add(s);
            }
        }

        // SetFactor1: white with the policy's opacity as alpha.
        public static uint Tint(double opacity) { return ((uint)Math.Round(255 * Math.Max(0, Math.Min(1, opacity))) << 24) | 0x00FFFFFFu; }
    }

    // What the magic water installer draws on: the engine scene (MagicWaterRuntime) or a recording target offline. z: the box centre's height
    // (a water plane's z), NaN for the terrain height at (x, y). AddDecal returns null when the decal's material does not resolve.
    internal interface IMagicWaterTarget
    {
        object AddDecal(RoadDecal decal, double x, double y, double z);
        void MoveDecal(object handle, RoadDecal decal, double x, double y, double z);
        void RemoveDecal(object handle);
        void SetVisible(object handle, bool visible);
    }

    // Draws a magic water plan on a target, each stamp at its periodic image, keyed by stamp id as MagicGroundInstaller does (a live change or a
    // reveal step touches only its own zones' stamps). A stamp draws with the magic water material, else with its fallback (the look's ground
    // material), else not at all (counted); a material that did not resolve is not tried again.
    internal sealed class MagicWaterInstaller
    {
        private sealed class Drawn { public object Handle; public double X; public MagicWaterStamp Stamp; public RoadDecal Decal; public string Key; }
        private readonly IMagicWaterTarget target;
        private readonly Dictionary<string, Drawn> drawn = new Dictionary<string, Drawn>(StringComparer.Ordinal);
        public readonly List<string> Errors = new List<string>();
        public readonly SortedSet<string> MissingMaterials = new SortedSet<string>(StringComparer.Ordinal);
        public MagicWaterPlan Plan { get; private set; }
        public int Created, Removed, Moved, NotDrawn, Dropped, FallbackDrawn;
        public bool Visible = true;
        public int Live { get { return drawn.Count; } }
        public MagicWaterInstaller(IMagicWaterTarget target) { this.target = target; }

        public void Sync(MagicWaterPlan plan, Func<double, double> image)
        {
            var next = new Dictionary<string, MagicWaterStamp>(StringComparer.Ordinal);
            foreach (var s in plan.Stamps) next[s.Id] = s;
            foreach (var key in drawn.Keys.ToList())
            {
                MagicWaterStamp stamp;
                if (next.TryGetValue(key, out stamp) && stamp.Key == drawn[key].Key) continue;
                RemoveOne(key);
            }
            NotDrawn = 0;
            foreach (var s in plan.Stamps)
            {
                if (drawn.ContainsKey(s.Id)) continue;
                double x = image(s.Decal.X);
                object handle = null; RoadDecal used = null;
                foreach (var d in s.Fallback == null ? new[] { s.Decal } : new[] { s.Decal, s.Fallback })
                {
                    if (MissingMaterials.Contains(d.Material)) continue;
                    try
                    {
                        handle = target.AddDecal(d, x, d.Y, s.PlaneZ);
                        if (handle == null) { MissingMaterials.Add(d.Material); continue; }
                        used = d; break;
                    }
                    catch (Exception e) { if (Errors.Count < 20) Errors.Add("create " + s.Id + ": " + e.GetType().Name + ": " + e.Message); break; }
                }
                if (handle == null) { NotDrawn++; continue; }
                if (!ReferenceEquals(used, s.Decal)) FallbackDrawn++;
                if (!Visible) Try("hide " + s.Id, () => target.SetVisible(handle, false));
                drawn[s.Id] = new Drawn { Handle = handle, X = x, Stamp = s, Decal = used, Key = s.Key }; Created++;
            }
            Plan = plan;
        }

        public void Refresh(Func<double, double> image)
        {
            foreach (var d in drawn.Values)
            {
                double x = image(d.Decal.X);
                if (Math.Abs(x - d.X) < 1e-6) continue;
                var one = d;
                if (Try("move " + d.Stamp.Id, () => target.MoveDecal(one.Handle, one.Decal, x, one.Decal.Y, one.Stamp.PlaneZ))) { d.X = x; Moved++; }
            }
        }

        // MMR-247's toggle check (c31): every drawn stamp hidden or shown again, nothing removed.
        public void SetVisible(bool visible)
        {
            Visible = visible;
            foreach (var d in drawn.Values) { var one = d; Try("visible " + d.Stamp.Id, () => target.SetVisible(one.Handle, visible)); }
        }

        // On a live scene. The handle leaves the table first, so it is never removed twice.
        private void RemoveOne(string key)
        {
            var d = drawn[key];
            drawn.Remove(key);
            Try("remove " + key, () => target.RemoveDecal(d.Handle));
            Removed++;
        }

        public void Remove() { foreach (var key in drawn.Keys.ToList()) RemoveOne(key); }
        // MMR-262: lets go of every handle with no target call, for a scene the engine already cleared (MapScreen.OnFinalize's Scene.ClearAll).
        public int Drop() { int n = drawn.Count; drawn.Clear(); Dropped += n; return n; }

        public IEnumerable<KeyValuePair<MagicWaterStamp, RoadDecal>> DrawnStamps() { foreach (var d in drawn.Values) yield return new KeyValuePair<MagicWaterStamp, RoadDecal>(d.Stamp, d.Decal); }

        private bool Try(string what, Action action)
        {
            try { action(); return true; }
            catch (Exception e) { if (Errors.Count < 20) Errors.Add(what + ": " + e.GetType().Name + ": " + e.Message); return false; }
        }

        public Dictionary<string, object> Record()
        {
            var r = Plan == null ? new Dictionary<string, object>() : Plan.Record();
            var drawnByMode = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var d in drawn.Values) MagicGround.Count(drawnByMode, d.Stamp.Mode);
            r["installed"] = new Dictionary<string, object> { ["created"] = Created, ["live"] = drawn.Count, ["liveByMode"] = drawnByMode, ["notDrawn"] = NotDrawn, ["fallbackDrawn"] = FallbackDrawn,
                ["fallbackLive"] = drawn.Values.Count(d => !ReferenceEquals(d.Decal, d.Stamp.Decal)), ["visible"] = Visible, ["moved"] = Moved, ["removed"] = Removed, ["dropped"] = Dropped,
                ["missingMaterials"] = MissingMaterials.ToList(), ["errors"] = Errors.ToList() };
            return r;
        }
    }
}
