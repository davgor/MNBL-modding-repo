using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MNBL.ProceduralWorld.World;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-291: veins, rivers and magic water as terrain overlay meshes (docs/overlay-meshes.md; the route: C:\Users\davgo\m2\results\MMR-291-route.md).
    // acc5 and the decal lab proved the campaign map draws scene decals only from Native's prebaked decal atlas, so the module's art drew nothing
    // as decals and no decal drew on our sea. This is the pure, deterministic half: it turns the plans the decals were drawn from into
    // chunked meshes (vertices in a chunk's local frame, UVs, vertex colours, triangles); OverlayMeshRuntime is the engine side.
    //   * Veins (land): every drawn MMR-288 land stamp (MagicGroundPlan.Stamps; trunk, fork, tip) as a terrain-draped quad grid of its box (L along
    //     the edge, W the pipe-model width of the edge's child) mapped onto its atlas cell; a burst as a draped disc (the radial art fills the
    //     cell's inscribed circle). The stamps sit along the graph's edges (each edge is one growth step, 0.75 L, and its trunk or tip is centred
    //     on it), so the quads form ribbons that thin to the tips (the art and the widths), forks blend alpha-over as the chained art expects,
    //     and a live cast's quads appear by its reveal times (one mesh per reveal band). Magic water's inland and shore-glow stamps (drawn with
    //     the look's ground material) join the vein meshes of their look. Cells of a vein quad lying wholly over shown water are left out.
    //   * Magic water: every other MMR-290 water stamp as a quad of the magic water atlas cell (4 x 8): flat on its plane (sea z 0, a lake's z)
    //     plus magicWater.lift, or (bed, coast) draped over the terrain.
    //   * Rivers: MMR-247-W's smoothed river chains (MapDressingPlan.RiverLines) as ribbons: per cross-section 5 vertices, the inner three on a
    //     level surface at the centre's ground + rivers.depth (never under a shown plane there), the edges at min(level, ground - dip), so the
    //     water shader's depth fade blends the banks; pools (lone river cells and patches) and, by policy, fills as discs the same way.
    //   * Draping: a vertex's z is the ground (the encoded terrain) plus the layer's lift. Between vertices the ground is checked at 16 points per
    //     grid cell; while it rises above the mesh by more than segment.maxError the piece is subdivided (to segment.min); the largest rise and
    //     fall per layer are recorded. Pieces are not raised onto water planes or river surfaces: the water paints over what lies under it
    //     (acc6lab; the refraction shows it only through shallow water), and magic water lies on the planes in its own pass (OverlayPass).
    //   * Chunks: a piece goes to the chunk (chunk x chunk units, canonical frame) of its centre; its vertices are relative to the chunk's centre
    //     with the shortest periodic x offset, so a piece across the seam stays whole and a chunk moves to its periodic image as one entity.
    //     One mesh per (layer, material, chunk, reveal band).
    internal static class OverlayLayer
    {
        public const string Veins = "veins", Rivers = "rivers", MagicWater = "magicWater";
        public static readonly string[] All = { Veins, Rivers, MagicWater };
    }

    // MMR-291-W: how a vein or magic water material draws against the map's water (roads.json overlayMeshes.<layer>.pass; docs/overlay-meshes.md
    // "Over the water"; evidence C:\Users\davgo\m2\results\MMR-291-W.md). acc6lab: the template copies (alpha blended, forward, no depth write)
    // drew on land and nothing on the sea, even flat on the sea plane + lift, while naval_worldmap_water ribbons drew over the sea. The engine draws
    // its alpha-blended forward meshes before the water (TaleWorlds.Native.dll's pass names: "Alpha Blended Forward Meshes" with the opaque forward
    // and post effect passes; "Render Water Mesh", "depth_target_with_water", "Gpu particles render", "Depth Reading Alpha Blended Forward Meshes" in
    // the water's), and the water (opaque, depth tested, no depth write) paints over every pixel that holds no nearer depth. The passes, as
    // TaleWorlds.Engine.MaterialFlags bits on the layer's material:
    //   blend        the template as it is (MMR-291);
    //   depth        depth written (NoModifyDepthBuffer cleared) behind pbr_shading's alpha_test at overlayMeshes.alphaTest: the texels that pass keep the
    //                water off as every opaque map mesh (a ship, an icon) does; the rest stays water;
    //   forward      + RequiresForwardRendering (needs_forward_rendering) and AlphaBlendSort (alpha_blend_sort): Native's worldmap_cloud flags;
    //   afterPostfx  + render_after_postfx (managed name AlwaysDepthTest; the native name is its CustomEngineStructMemberData): crosshair_material's queue.
    // Rivers keep their water material (its own queue). A material key may name its pass ("<key>@<pass>": the decal lab's rows); else the layer's.
    internal static class OverlayPass
    {
        public const uint NoModifyDepthBuffer = 0x8, AlphaBlendSort = 0x40, RequiresForwardRendering = 0x100000, RenderAfterPostfx = 0x20000000;
        public static uint Flags(string pass, uint flags)
        {
            switch (pass)
            {
                case RoadsPolicy.OverlayPolicy.Blend: return flags;
                case RoadsPolicy.OverlayPolicy.Depth: return flags & ~NoModifyDepthBuffer;
                case RoadsPolicy.OverlayPolicy.Forward: return flags | RequiresForwardRendering | AlphaBlendSort;
                case RoadsPolicy.OverlayPolicy.AfterPostfx: return flags | RenderAfterPostfx;
                default: throw new ArgumentException("unknown overlay pass " + pass);
            }
        }
        public static bool AlphaTest(string pass) { return pass == RoadsPolicy.OverlayPolicy.Depth; }
        public static string Key(string material, string pass) { return pass == null ? material : material + "@" + pass; }
        // The key without its pass, and the pass (null: the layer's).
        public static string Split(string key, out string pass)
        {
            int i = key.LastIndexOf('@');
            pass = i < 0 ? null : key.Substring(i + 1);
            return i < 0 ? key : key.Substring(0, i);
        }
    }

    // One chunk mesh: one entity in game.
    internal sealed class OverlayMesh
    {
        public string Layer, Material, Look;        // Material: "vein:<look>", "magicWater" or "river"
        public int ChunkX, ChunkY;
        public double OriginX, OriginY;             // canonical physical; vertex x and y are relative to it
        public double RevealAt = Double.NegativeInfinity; public bool Live;
        public double Glow;                         // the mesh's vector argument w (self-illumination of the template)
        public readonly List<float> Positions = new List<float>(), Uvs = new List<float>();
        public readonly List<uint> Colours = new List<uint>();
        public readonly List<int> Indices = new List<int>();
        public readonly List<string> PieceIds = new List<string>();
        public double MinX = Double.MaxValue, MinY = Double.MaxValue, MaxX = Double.MinValue, MaxY = Double.MinValue, MinZ = Double.MaxValue, MaxZ = Double.MinValue;
        public int Vertices { get { return Colours.Count; } }
        public int Triangles { get { return Indices.Count / 3; } }
        public string Key { get { return Layer + "|" + Material + "|" + ChunkX.ToString(CultureInfo.InvariantCulture) + "," + ChunkY.ToString(CultureInfo.InvariantCulture) + "|" + Band; } }
        public string Band { get { return Double.IsNegativeInfinity(RevealAt) ? "g" : RevealAt.ToString("R", CultureInfo.InvariantCulture); } }
        private string digest;
        // The geometry's identity (a mesh whose key and digest are drawn is not drawn again).
        public string Digest
        {
            get
            {
                if (digest != null) return digest;
                using (var sha = SHA256.Create())
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(Key); w.Write(OriginX); w.Write(OriginY); w.Write(Glow);
                    foreach (var v in Positions) w.Write(v);
                    foreach (var v in Uvs) w.Write(v);
                    foreach (var v in Colours) w.Write(v);
                    foreach (var v in Indices) w.Write(v);
                    w.Flush();
                    digest = BitConverter.ToString(sha.ComputeHash(ms.ToArray())).Replace("-", "").ToLowerInvariant();
                }
                return digest;
            }
        }
        internal int Vertex(double lx, double ly, double z, double u, double v, uint colour)
        {
            Positions.Add((float)lx); Positions.Add((float)ly); Positions.Add((float)z);
            Uvs.Add((float)u); Uvs.Add((float)v); Colours.Add(colour);
            MinX = Math.Min(MinX, lx); MaxX = Math.Max(MaxX, lx); MinY = Math.Min(MinY, ly); MaxY = Math.Max(MaxY, ly); MinZ = Math.Min(MinZ, z); MaxZ = Math.Max(MaxZ, z);
            digest = null;
            return Colours.Count - 1;
        }
        internal void Triangle(int a, int b, int c) { Indices.Add(a); Indices.Add(b); Indices.Add(c); digest = null; }
    }

    // How closely a layer's draped vertices follow their target surface: d = the mesh's z less its lift, less the target, sampled inside every
    // grid cell. Above: the most the target rises over the draped mesh (the terrain shows through when it reaches the lift); Below: the most
    // the mesh floats over it.
    internal sealed class FollowError
    {
        public long Samples; public double Above, Below, MaxRaise; public int Pieces, Refined, OverBound, Raised;
        public string WorstPiece; public double WorstX, WorstY;
        internal void Add(string id, double above, double below, long samples, bool refined, bool overBound, double x, double y)
        {
            Pieces++; Samples += samples; if (refined) Refined++; if (overBound) OverBound++;
            if (above > Above) { Above = above; WorstPiece = id; WorstX = x; WorstY = y; }
            Below = Math.Max(Below, below);
        }
        public Dictionary<string, object> Record()
        {
            return new Dictionary<string, object> { ["pieces"] = Pieces, ["samples"] = Samples, ["maxAbove"] = Math.Round(Above, 4), ["maxBelow"] = Math.Round(Below, 4), ["refinedPieces"] = Refined, ["raisedPieces"] = OverBound, ["raisedVertices"] = Raised, ["maxRaise"] = Math.Round(MaxRaise, 4),
                ["worst"] = WorstPiece == null ? null : new Dictionary<string, object> { ["piece"] = WorstPiece, ["at"] = new[] { Math.Round(WorstX, 3), Math.Round(WorstY, 3) } } };
        }
    }

    internal sealed class OverlayMeshPlan
    {
        public const string Version = "mmr291/1";
        public double Width, Strip, Chunk, Extent;
        public readonly List<OverlayMesh> Meshes = new List<OverlayMesh>();
        public readonly SortedDictionary<string, int> Pieces = new SortedDictionary<string, int>(StringComparer.Ordinal), Skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, FollowError> Errors = new SortedDictionary<string, FollowError>(StringComparer.Ordinal);
        public double Hours = Double.PositiveInfinity;
        public int Hidden;

        public IEnumerable<OverlayMesh> Of(string layer) { return Meshes.Where(m => m.Layer == layer); }
        public OverlayMeshPlan Revealed(double hours)
        {
            var r = new OverlayMeshPlan { Width = Width, Strip = Strip, Chunk = Chunk, Extent = Extent, Hours = hours };
            foreach (var kv in Pieces) r.Pieces[kv.Key] = kv.Value;
            foreach (var kv in Skipped) r.Skipped[kv.Key] = kv.Value;
            foreach (var kv in Errors) r.Errors[kv.Key] = kv.Value;
            foreach (var m in Meshes) if (m.RevealAt <= hours) r.Meshes.Add(m); else r.Hidden++;
            return r;
        }
        public double NextReveal(double hours)
        {
            double next = Double.PositiveInfinity;
            foreach (var m in Meshes) if (m.RevealAt > hours && m.RevealAt < next) next = m.RevealAt;
            return next;
        }
        public string Sha256()
        {
            var b = new StringBuilder(Version + "\n");
            foreach (var m in Meshes) b.Append(m.Key).Append(' ').Append(m.Digest).Append('\n');
            return Json.Sha(b.ToString());
        }
        public Dictionary<string, object> Totals(IEnumerable<OverlayMesh> meshes)
        {
            var list = meshes.ToList();
            return new Dictionary<string, object> { ["entities"] = list.Count, ["pieces"] = list.Sum(m => m.PieceIds.Count), ["vertices"] = list.Sum(m => m.Vertices), ["triangles"] = list.Sum(m => m.Triangles) };
        }
        public Dictionary<string, object> Record()
        {
            var layers = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var layer in OverlayLayer.All)
            {
                var t = Totals(Of(layer));
                FollowError e; if (Errors.TryGetValue(layer, out e)) t["follow"] = e.Record();
                t["live"] = Of(layer).Count(m => m.Live);
                layers[layer] = t;
            }
            return new Dictionary<string, object> { ["version"] = Version, ["chunk"] = Chunk, ["layers"] = layers, ["total"] = Totals(Meshes), ["hiddenLive"] = Hidden, ["skipped"] = Skipped };
        }
        // The busiest window (w x h units, stepped by step, x periodic) by entities, with its vertices and triangles, per layer and in total:
        // what a camera of that view draws at most (a mesh counts when its bounds meet the window).
        public Dictionary<string, object> InView(double w, double h, double step)
        {
            var r = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var layer in OverlayLayer.All.Concat(new[] { "total" }))
            {
                var meshes = layer == "total" ? Meshes : Of(layer).ToList();
                int bestE = 0, bestV = 0, bestT = 0; double[] at = null;
                if (meshes.Count > 0)
                {
                    double y1 = meshes.Max(m => m.OriginY + m.MaxY), y0 = meshes.Min(m => m.OriginY + m.MinY);
                    for (double wx = Strip; wx < Strip + Width; wx += step)
                        for (double wy = y0; wy < y1; wy += step)
                        {
                            int e = 0, v = 0, t = 0;
                            foreach (var m in meshes)
                            {
                                double ox = OverlayMeshes.DeltaX(wx, m.OriginX, Width) + wx;   // the mesh's image nearest the window
                                if (ox + m.MaxX < wx || ox + m.MinX > wx + w || m.OriginY + m.MaxY < wy || m.OriginY + m.MinY > wy + h) continue;
                                e++; v += m.Vertices; t += m.Triangles;
                            }
                            if (e > bestE || (e == bestE && t > bestT)) { bestE = e; bestV = v; bestT = t; at = new[] { wx, wy }; }
                        }
                }
                r[layer] = new Dictionary<string, object> { ["entities"] = bestE, ["vertices"] = bestV, ["triangles"] = bestT, ["at"] = at };
            }
            return new Dictionary<string, object> { ["window"] = new[] { w, h }, ["step"] = step, ["busiest"] = r };
        }
    }

    // The river surfaces a plan drew (ribbon centre lines with their levels, discs): what a vein or magic water stamp over a river rides on, and
    // whether a point shows river water (the in-game water check counts a river cell drawn when a ribbon or disc covers its centre).
    internal sealed class OverlayRiverSurface
    {
        private sealed class Segment { public double Ax, Ay, Bx, By, La, Lb, Half; }
        private sealed class Disc { public double X, Y, Radius, Level; }
        private readonly List<Segment> segments = new List<Segment>();
        private readonly List<Disc> discs = new List<Disc>();
        private readonly Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();
        private const double Cell = 8;
        public readonly double Width, Strip;
        public OverlayRiverSurface(double width, double strip) { Width = width; Strip = strip; }
        public int Segments { get { return segments.Count; } }
        public int Discs { get { return discs.Count; } }
        private long Key(int cx, int cy) { return ((long)cx << 32) ^ (uint)cy; }
        private double Canon(double x) { double c = (x - Strip) % Width; if (c < 0) c += Width; return c; }
        internal void AddSegment(double ax, double ay, double bx, double by, double la, double lb, double half)
        {
            // Stored with a canonical first end; b keeps its offset from a.
            double cax = Canon(ax); bx = cax + (bx - ax); ax = cax;
            segments.Add(new Segment { Ax = ax, Ay = ay, Bx = bx, By = by, La = la, Lb = lb, Half = half });
            int id = segments.Count - 1;
            int x0 = (int)Math.Floor((Math.Min(ax, bx) - half) / Cell), x1 = (int)Math.Floor((Math.Max(ax, bx) + half) / Cell);
            int y0 = (int)Math.Floor((Math.Min(ay, by) - half) / Cell), y1 = (int)Math.Floor((Math.Max(ay, by) + half) / Cell);
            int period = (int)Math.Ceiling(Width / Cell);
            for (int cx = x0; cx <= x1; cx++)
                for (int cy = y0; cy <= y1; cy++)
                {
                    long k = Key(((cx % period) + period) % period, cy);
                    List<int> list; if (!grid.TryGetValue(k, out list)) grid[k] = list = new List<int>();
                    list.Add(id);
                }
        }
        internal void AddDisc(double x, double y, double radius, double level) { discs.Add(new Disc { X = Canon(x), Y = y, Radius = radius, Level = level }); }
        // The river level at a physical point within share x a ribbon's half width of its centre line (or share x a disc's radius), else NaN.
        public double Level(double x, double y, double share = 1)
        {
            double cx = Canon(x), best = Double.NaN, bestD = Double.MaxValue;
            int period = (int)Math.Ceiling(Width / Cell);
            List<int> list;
            if (grid.TryGetValue(Key(((int)Math.Floor(cx / Cell) % period + period) % period, (int)Math.Floor(y / Cell)), out list))
                foreach (int id in list)
                {
                    var s = segments[id];
                    double px = s.Ax + OverlayMeshes.DeltaX(s.Ax, cx, Width), dx = s.Bx - s.Ax, dy = s.By - s.Ay, len2 = dx * dx + dy * dy;
                    double t = len2 <= 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((px - s.Ax) * dx + (y - s.Ay) * dy) / len2));
                    double qx = s.Ax + t * dx - px, qy = s.Ay + t * dy - y, d = Math.Sqrt(qx * qx + qy * qy);
                    if (d <= s.Half * share && d < bestD) { bestD = d; best = s.La + t * (s.Lb - s.La); }
                }
            foreach (var c in discs)
            {
                double dx = OverlayMeshes.DeltaX(c.X, cx, Width), dy = y - c.Y, d = Math.Sqrt(dx * dx + dy * dy);
                if (d <= c.Radius * share && (Double.IsNaN(best) || c.Level > best)) best = c.Level;
            }
            return best;
        }
        public bool Covers(double x, double y, double share = 1) { return !Double.IsNaN(Level(x, y, share)); }
        // Every centre point with its level: each ribbon segment's two ends and each disc's centre (physical x; the frame here is strip-relative).
        public IEnumerable<double[]> Levels()
        {
            foreach (var s in segments) { yield return new[] { Strip + s.Ax, s.Ay, s.La }; yield return new[] { Strip + s.Bx, s.By, s.Lb }; }
            foreach (var c in discs) yield return new[] { Strip + c.X, c.Y, c.Level };
        }
    }

    internal static class OverlayMeshes
    {
        public const double RiverInner = 0.6;   // the share of a ribbon's half width (and a disc's radius) on the level surface
        public const int GridSamples = 4;       // per grid cell side, for the follow check (16 points per cell)

        public static double DeltaX(double a, double b, double width) { double d = b - a; return d - Math.Round(d / width) * width; }

        private sealed class Builder
        {
            public OverlayMeshPlan Plan; public RoadsPolicy.OverlayPolicy P;
            public Func<double, double, double> Ground;      // at a canonical physical point
            public readonly SortedDictionary<string, OverlayMesh> ByKey = new SortedDictionary<string, OverlayMesh>(StringComparer.Ordinal);
            public double Canon(double x) { double c = (x - Plan.Strip) % Plan.Width; if (c < 0) c += Plan.Width; return c; }
            public double G(double x, double y) { return Ground(Plan.Strip + Canon(x), y); }
            public OverlayMesh Mesh(string layer, string material, string look, double x, double y, double revealAt, bool live, double glow)
            {
                int cx = (int)Math.Floor(Canon(x) / Plan.Chunk), cy = (int)Math.Floor(y / Plan.Chunk);
                var probe = new OverlayMesh { Layer = layer, Material = material, ChunkX = cx, ChunkY = cy, RevealAt = revealAt };
                OverlayMesh m;
                if (!ByKey.TryGetValue(probe.Key, out m))
                {
                    m = probe; m.Look = look; m.Live = live; m.Glow = glow;
                    m.OriginX = Plan.Strip + (cx + .5) * Plan.Chunk; m.OriginY = (cy + .5) * Plan.Chunk;
                    ByKey[m.Key] = m;
                }
                return m;
            }
            public void Count(string what) { int n; Plan.Pieces.TryGetValue(what, out n); Plan.Pieces[what] = n + 1; }
            public void Skip(string what) { int n; Plan.Skipped.TryGetValue(what, out n); Plan.Skipped[what] = n + 1; }
            public FollowError Error(string layer) { FollowError e; if (!Plan.Errors.TryGetValue(layer, out e)) Plan.Errors[layer] = e = new FollowError(); return e; }
            public OverlayMeshPlan Finish() { foreach (var m in ByKey.Values) if (m.Triangles > 0) Plan.Meshes.Add(m); return Plan; }
        }

        private static uint Colour(double alpha) { return ((uint)Math.Round(255 * Math.Max(0, Math.Min(1, alpha))) << 24) | 0x00FFFFFFu; }

        // The texture rect (u0, v0, u1, v1) of a decal atlas argument (scaleU, scaleV, offsetU, offsetV); flipV mirrors the texture's rows.
        public static double[] Rect(float[] atlas, bool flipV)
        {
            double u0 = atlas[2], u1 = atlas[2] + atlas[0], v0 = atlas[3], v1 = atlas[3] + atlas[1];
            return flipV ? new[] { u0, 1 - v0, u1, 1 - v1 } : new[] { u0, v0, u1, v1 };
        }

        // The surface a draped piece follows at a physical point: the ground (the encoded terrain, MapDressing.GroundOf). overWater: a shown water
        // plane covers the point (its ground under the sea datum or inside a lake plane under its z). A piece over water is not raised to the
        // water surface: the campaign map's water writes no depth, so a blended mesh under it still draws (inference, the decal lab's sea spot
        // settles it), and a vein cell wholly over water is left out (the magic water layer draws there).
        private static double Ground(Builder b, double x, double y) { return b.Ground(b.Plan.Strip + b.Canon(x), y); }
        private static bool OverWater(Builder b, WaterPlanes planes, double x, double y)
        {
            double cx = b.Plan.Strip + b.Canon(x);
            return planes != null && !Double.IsNaN(planes.Level(cx, y, b.Ground(cx, y)));
        }
        // The planes a piece of radius r around (x, y) can meet (all of them when it crosses the canonical seam).
        private static WaterPlanes Near(Builder b, WaterPlanes planes, double x, double y, double r)
        {
            if (planes == null) return null;
            double cx = b.Plan.Strip + b.Canon(x);
            if (cx - r < b.Plan.Strip || cx + r > b.Plan.Strip + b.Plan.Width) return planes;
            return new WaterPlanes(planes.Lakes.Where(l => l.MaxX >= cx - r && l.MinX <= cx + r && l.MaxY >= y - r && l.MinY <= y + r).ToList());
        }

        // A draped quad grid: centre (x, y), yaw, half extents; u runs from the back (s -1) to the front (s +1) over the rect, v from side t -1 to
        // side t +1. The grid starts at segment.max per cell and halves while the target rises over the mesh by more than segment.maxError
        // (down to segment.min). dropWet: cells whose four corners lie over shown water are left out (a land vein does not paint the water).
        private static void Quad(Builder b, OverlayMesh m, string id, double x, double y, double yaw, double halfL, double halfW, double[] rect, uint colour, double lift,
                                 WaterPlanes planes, bool dropWet, FollowError err)
        {
            double c = Math.Cos(yaw), s = Math.Sin(yaw), seg = b.P.MaxSegment;
            int nu = Math.Max(1, (int)Math.Ceiling(2 * halfL / seg - 1e-9)), nv = Math.Max(1, (int)Math.Ceiling(2 * halfW / seg - 1e-9));
            double[] z = null; bool[] wet = null; double above = 0, below = 0; long samples = 0; bool refined = false;
            double[] cellAbove = null;
            var near = dropWet ? Near(b, planes, x, y, Math.Sqrt(halfL * halfL + halfW * halfW) + 1) : null;
            // The ground's largest rise over the mesh (and fall) at 16 points per cell, per cell into cellAbove.
            Action measure = () =>
            {
                above = 0; below = 0; samples = 0;
                for (int i = 0; i < nu; i++)
                    for (int j = 0; j < nv; j++)
                    {
                        double z00 = z[i * (nv + 1) + j], z10 = z[(i + 1) * (nv + 1) + j], z01 = z[i * (nv + 1) + j + 1], z11 = z[(i + 1) * (nv + 1) + j + 1], ca = Double.MinValue;
                        for (int a = 0; a < GridSamples; a++)
                            for (int bb = 0; bb < GridSamples; bb++)
                            {
                                double fa = (a + .5) / GridSamples, fb = (bb + .5) / GridSamples;
                                double mz = fa >= fb ? z00 + fa * (z10 - z00) + fb * (z11 - z10) : z00 + fb * (z01 - z00) + fa * (z11 - z01);
                                double su = -1 + 2.0 * (i + fa) / nu, tv = -1 + 2.0 * (j + fb) / nv, px = x + su * halfL * c - tv * halfW * s, py = y + su * halfL * s + tv * halfW * c;
                                double tz = Ground(b, px, py);
                                ca = Math.Max(ca, tz - mz); below = Math.Max(below, mz - tz); samples++;
                            }
                        cellAbove[i * nv + j] = ca; above = Math.Max(above, ca);
                    }
            };
            for (;;)
            {
                z = new double[(nu + 1) * (nv + 1)]; wet = new bool[z.Length]; cellAbove = new double[nu * nv];
                for (int i = 0; i <= nu; i++)
                    for (int j = 0; j <= nv; j++)
                    {
                        double su = -1 + 2.0 * i / nu, tv = -1 + 2.0 * j / nv, px = x + su * halfL * c - tv * halfW * s, py = y + su * halfL * s + tv * halfW * c;
                        z[i * (nv + 1) + j] = Ground(b, px, py);
                        wet[i * (nv + 1) + j] = dropWet && OverWater(b, near, px, py);
                    }
                measure();
                double cell = Math.Max(2 * halfL / nu, 2 * halfW / nv);
                if (above <= b.P.MaxError || cell / 2 < b.P.MinSegment - 1e-9) break;
                nu *= 2; nv *= 2; refined = true;
            }
            // At segment.min a sharp crest of the terrain (a single heightfield vertex standing out) can still rise over a cell: its four corners
            // are raised by that rise, so the mesh stays over the ground (it floats there instead), and the check is made again.
            bool over = above > b.P.MaxError;
            if (over)
            {
                var raise = new double[z.Length];
                for (int i = 0; i < nu; i++)
                    for (int j = 0; j < nv; j++)
                    {
                        double ca = cellAbove[i * nv + j];
                        if (ca <= b.P.MaxError) continue;
                        foreach (int k in new[] { i * (nv + 1) + j, (i + 1) * (nv + 1) + j, i * (nv + 1) + j + 1, (i + 1) * (nv + 1) + j + 1 }) raise[k] = Math.Max(raise[k], ca);
                    }
                for (int k = 0; k < z.Length; k++) if (raise[k] > 0) { z[k] += raise[k]; err.Raised++; err.MaxRaise = Math.Max(err.MaxRaise, raise[k]); }
                measure();
            }
            err.Add(id, above, below, samples, refined, over, x, y);
            var index = new int[z.Length];
            for (int k = 0; k < index.Length; k++) index[k] = -1;
            int drawnCells = 0;
            for (int i = 0; i < nu; i++)
                for (int j = 0; j < nv; j++)
                {
                    int k00 = i * (nv + 1) + j, k10 = (i + 1) * (nv + 1) + j, k01 = k00 + 1, k11 = k10 + 1;
                    if (dropWet && wet[k00] && wet[k10] && wet[k01] && wet[k11]) continue;
                    foreach (int k in new[] { k00, k10, k01, k11 })
                    {
                        if (index[k] >= 0) continue;
                        int ii = k / (nv + 1), jj = k % (nv + 1);
                        double su = -1 + 2.0 * ii / nu, tv = -1 + 2.0 * jj / nv, px = x + su * halfL * c - tv * halfW * s, py = y + su * halfL * s + tv * halfW * c;
                        index[k] = m.Vertex(DeltaX(m.OriginX, px, b.Plan.Width), py - m.OriginY, z[k] + lift, rect[0] + (su + 1) / 2 * (rect[2] - rect[0]), rect[1] + (tv + 1) / 2 * (rect[3] - rect[1]), colour);
                    }
                    m.Triangle(index[k00], index[k10], index[k11]); m.Triangle(index[k00], index[k11], index[k01]);
                    drawnCells++;
                }
            if (drawnCells == 0) { b.Skip(m.Layer + ":wholly over water"); return; }
            if (drawnCells < nu * nv) b.Skip(m.Layer + ":cells over water");
            m.PieceIds.Add(id);
        }

        // A draped disc (a burst's radial art on the cell's inscribed circle, yaw turning its u axis): a centre and rings of segments, at least
        // burst.rings x burst.segments and about segment.max apart, doubled while the ground rises over it by more than segment.maxError.
        private static void BurstDisc(Builder b, OverlayMesh m, string id, double x, double y, double yaw, double radius, double[] rect, uint colour, double lift, FollowError err)
        {
            int rings = Math.Max(b.P.BurstRings, (int)Math.Ceiling(radius / b.P.MaxSegment - 1e-9)), segments = Math.Max(b.P.BurstSegments, (int)Math.Ceiling(2 * Math.PI * radius / b.P.MaxSegment - 1e-9));
            double c = Math.Cos(yaw), s = Math.Sin(yaw), above = 0, below = 0; long samples = 0; bool refined = false;
            Func<double, double, double[]> at = (su, tv) => new[] { x + su * radius * c - tv * radius * s, y + su * radius * s + tv * radius * c };
            List<double[]> pts; List<int[]> tris;
            // The ground's largest rise over the disc (and fall) at 4 points per triangle; per triangle into perTriangle when given.
            Action<List<double[]>, List<int[]>, double[]> measure = (ps, ts, perTriangle) =>
            {
                above = 0; below = 0; samples = 0;
                for (int ti = 0; ti < ts.Count; ti++)
                {
                    var t = ts[ti]; double ta = Double.MinValue;
                    foreach (var bc in new[] { new[] { 1 / 3.0, 1 / 3.0 }, new[] { .6, .2 }, new[] { .2, .6 }, new[] { .2, .2 } })
                    {
                        double l0 = bc[0], l1 = bc[1], l2 = 1 - l0 - l1;
                        double su = ps[t[0]][0] * l0 + ps[t[1]][0] * l1 + ps[t[2]][0] * l2, tv = ps[t[0]][1] * l0 + ps[t[1]][1] * l1 + ps[t[2]][1] * l2;
                        double mz = ps[t[0]][2] * l0 + ps[t[1]][2] * l1 + ps[t[2]][2] * l2;
                        var w = at(su, tv); double tz = Ground(b, w[0], w[1]);
                        ta = Math.Max(ta, tz - mz); below = Math.Max(below, mz - tz); samples++;
                    }
                    above = Math.Max(above, ta);
                    if (perTriangle != null) perTriangle[ti] = ta;
                }
            };
            for (;;)
            {
                pts = new List<double[]> { new double[] { 0, 0, 0 } };   // su, tv, z
                for (int r = 1; r <= rings; r++)
                    for (int k = 0; k < segments; k++)
                    {
                        double a = 2 * Math.PI * k / segments, rr = (double)r / rings;
                        pts.Add(new[] { rr * Math.Cos(a), rr * Math.Sin(a), 0 });
                    }
                foreach (var p in pts) { var w = at(p[0], p[1]); p[2] = Ground(b, w[0], w[1]); }
                int segCount = segments;
                Func<int, int, int> idx = (r, k) => r == 0 ? 0 : 1 + (r - 1) * segCount + ((k % segCount) + segCount) % segCount;
                tris = new List<int[]>();
                for (int k = 0; k < segments; k++) tris.Add(new[] { idx(0, 0), idx(1, k), idx(1, k + 1) });
                for (int r = 1; r < rings; r++)
                    for (int k = 0; k < segments; k++) { tris.Add(new[] { idx(r, k), idx(r + 1, k), idx(r + 1, k + 1) }); tris.Add(new[] { idx(r, k), idx(r + 1, k + 1), idx(r, k + 1) }); }
                measure(pts, tris, null);
                if (above <= b.P.MaxError || radius / rings / 2 < b.P.MinSegment - 1e-9) break;
                rings *= 2; segments *= 2; refined = true;
            }
            // A sharp crest still rising over a triangle at segment.min: its corners are raised by that rise (see Quad).
            bool over = above > b.P.MaxError;
            if (over)
            {
                var triAbove = new double[tris.Count];
                measure(pts, tris, triAbove);
                var raise = new double[pts.Count];
                for (int t = 0; t < tris.Count; t++) if (triAbove[t] > b.P.MaxError) foreach (int k in tris[t]) raise[k] = Math.Max(raise[k], triAbove[t]);
                for (int k = 0; k < pts.Count; k++) if (raise[k] > 0) { pts[k][2] += raise[k]; err.Raised++; err.MaxRaise = Math.Max(err.MaxRaise, raise[k]); }
                measure(pts, tris, null);
            }
            err.Add(id, above, below, samples, refined, over, x, y);
            var map = new int[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                var w = at(pts[i][0], pts[i][1]);
                map[i] = m.Vertex(DeltaX(m.OriginX, w[0], b.Plan.Width), w[1] - m.OriginY, pts[i][2] + lift, rect[0] + (pts[i][0] + 1) / 2 * (rect[2] - rect[0]), rect[1] + (pts[i][1] + 1) / 2 * (rect[3] - rect[1]), colour);
            }
            foreach (var t in tris) m.Triangle(map[t[0]], map[t[1]], map[t[2]]);
            m.PieceIds.Add(id);
        }

        // A flat quad on a water plane (sea or lake stamps): 2 x 2 vertices at the plane's z + lift; land around it is higher and hides it.
        private static void FlatQuad(Builder b, OverlayMesh m, string id, double x, double y, double yaw, double halfL, double halfW, double[] rect, uint colour, double z)
        {
            double c = Math.Cos(yaw), s = Math.Sin(yaw);
            var k = new int[4];
            for (int i = 0; i < 4; i++)
            {
                double su = (i & 1) == 0 ? -1 : 1, tv = (i & 2) == 0 ? -1 : 1, px = x + su * halfL * c - tv * halfW * s, py = y + su * halfL * s + tv * halfW * c;
                k[i] = m.Vertex(DeltaX(m.OriginX, px, b.Plan.Width), py - m.OriginY, z, rect[0] + (su + 1) / 2 * (rect[2] - rect[0]), rect[1] + (tv + 1) / 2 * (rect[3] - rect[1]), colour);
            }
            m.Triangle(k[0], k[1], k[3]); m.Triangle(k[0], k[3], k[2]);
            m.PieceIds.Add(id);
        }

        private static OverlayMeshPlan NewPlan(RoadsPolicy.OverlayPolicy p, double width, double strip, double extent)
        {
            return new OverlayMeshPlan { Width = width, Strip = strip, Chunk = p.Chunk, Extent = extent };
        }

        // ---- veins and magic water (MagicGroundRuntime's layer) ----
        // ground: MMR-288's plan (its drawn land stamps; a Revealed copy draws only what a time shows, the full plan carries every reveal band);
        // water: MMR-290's plan over the same graph (null: no magic water); lakes: the shown lake planes; rivers: the river surfaces the rivers
        // layer drew (null: none, the veins drape on the ground there). veins / magicWater: the runtime's own say (a layer whose materials do not
        // resolve stays decals), on top of the policy's modes.
        public static OverlayMeshPlan Magic(MagicGroundPlan ground, MagicWaterPlan water, IList<GeneratedWaterSystem.Lake> lakes, RoadsPolicy.OverlayPolicy p,
                                            bool veins = true, bool magicWater = true)
        {
            var src = ground.Graph.Source;
            var b = new Builder { P = p, Plan = NewPlan(p, src.Width, src.Strip, src.Width + 2 * src.Strip), Ground = src.Ground };
            var planes = new WaterPlanes(lakes);
            var veinError = b.Error(OverlayLayer.Veins);
            bool drawVeins = veins && p.DrawsVeins;
            if (drawVeins)
                foreach (var s in ground.Stamps)
                {
                    var d = s.Decal;
                    var m = b.Mesh(OverlayLayer.Veins, "vein:" + s.Look, s.Look, d.X, d.Y, s.RevealAt, s.Live, p.Glow(s.Look));
                    var rect = Rect(d.Atlas, p.FlipV);
                    if (s.Kind == VeinKind.Burst) BurstDisc(b, m, s.Id, d.X, d.Y, d.Yaw, Math.Max(d.HalfLength, d.HalfWidth), rect, Colour(s.Alpha), p.VeinLift, veinError);
                    else Quad(b, m, s.Id, d.X, d.Y, d.Yaw, d.HalfLength, d.HalfWidth, rect, Colour(s.Alpha), p.VeinLift, planes, true, veinError);
                    b.Count(OverlayLayer.Veins + ":" + s.Kind);
                }
            if (water != null && magicWater && p.DrawsMagicWater)
            {
                var waterError = b.Error(OverlayLayer.MagicWater);
                foreach (var s in water.Stamps)
                {
                    var d = s.Decal;
                    uint colour = d.Tint == 0 ? 0xFFFFFFFFu : (d.Tint & 0xFF000000u) | 0x00FFFFFFu;
                    if (MagicWaterMode.Ground(s.Mode))
                    {
                        // inland and shore glow: the look's ground art, with the land veins
                        if (!drawVeins) { b.Skip(OverlayLayer.MagicWater + ":ground stamp, veins not meshed"); continue; }
                        var gm = b.Mesh(OverlayLayer.Veins, "vein:" + s.Look, s.Look, d.X, d.Y, s.RevealAt, s.Live, p.Glow(s.Look));
                        Quad(b, gm, s.Id, d.X, d.Y, d.Yaw, d.HalfLength, d.HalfWidth, Rect(d.Atlas, p.FlipV), 0xFFFFFFFFu, p.VeinLift, planes, true, veinError);
                        b.Count(OverlayLayer.Veins + ":" + s.Mode);
                        continue;
                    }
                    var m = b.Mesh(OverlayLayer.MagicWater, OverlayLayer.MagicWater, s.Look, d.X, d.Y, s.RevealAt, s.Live, p.WaterGlow);
                    var rect = Rect(d.Atlas, p.FlipV);
                    if (!Double.IsNaN(s.PlaneZ)) FlatQuad(b, m, s.Id, d.X, d.Y, d.Yaw, d.HalfLength, d.HalfWidth, rect, colour, s.PlaneZ + p.WaterLift);
                    else Quad(b, m, s.Id, d.X, d.Y, d.Yaw, d.HalfLength, d.HalfWidth, rect, colour, p.WaterLift, planes, false, waterError);
                    b.Count(OverlayLayer.MagicWater + ":" + s.Mode);
                }
            }
            return b.Finish();
        }

        // ---- rivers (MapDressingRuntime's install) ----
        // The dressing's river chains as ribbons and its pools (and fills, by policy) as discs, over its ground and shown planes; surface: the
        // river surfaces drawn, for the veins and the in-game water check.
        public static OverlayMeshPlan Rivers(MapDressingPlan d, Func<double, double, double> ground, double extent, RoadsPolicy.OverlayPolicy p, out OverlayRiverSurface surface)
        {
            var b = new Builder { P = p, Plan = NewPlan(p, d.Width, d.Strip, extent), Ground = ground };
            surface = new OverlayRiverSurface(d.Width, d.Strip);
            if (!p.DrawsRivers || ground == null) return b.Finish();
            var planes = new WaterPlanes(d.Lakes);
            double half = p.RiverWidth / 2;
            Func<double, double, double> level = (x, y) =>
            {
                double cx = d.Strip + b.Canon(x), g = ground(cx, y), l = g + p.RiverDepth, plane = planes.Level(cx, y, g);
                return Double.IsNaN(plane) ? l : Math.Max(l, plane + p.RiverLift);
            };
            const uint White = 0xFFFFFFFFu;
            int chain = 0;
            foreach (var line in d.RiverLines)
            {
                string id = "river." + (chain++).ToString(CultureInfo.InvariantCulture);
                double length = MapDressing.Length(line);
                if (line.Count < 2 || length < 1e-6) { b.Skip(OverlayLayer.Rivers + ":degenerate chain"); continue; }
                int n = Math.Max(1, (int)Math.Ceiling(length / p.MaxSegment - 1e-9));
                var centres = new List<double[]>();
                for (int i = 0; i <= n; i++) centres.Add(At(line, length * i / n));
                // One cross-section per centre: the level and the 5 vertices (t -1, -inner, 0, inner, 1 of the half width).
                double[] ts = { -1, -RiverInner, 0, RiverInner, 1 };
                OverlayMesh current = null; int[] last = null; double[] lastSection = null;
                for (int i = 0; i <= n; i++)
                {
                    var c = centres[i];
                    var prev = centres[Math.Max(0, i - 1)]; var next = centres[Math.Min(n, i + 1)];
                    double tx = next[0] - prev[0], ty = next[1] - prev[1], tl = Math.Sqrt(tx * tx + ty * ty);
                    if (tl < 1e-9) { tx = 1; ty = 0; tl = 1; }
                    double nx = -ty / tl, ny = tx / tl, lv = level(c[0], c[1]);
                    var section = new double[15];
                    for (int k = 0; k < 5; k++)
                    {
                        double px = c[0] + ts[k] * half * nx, py = c[1] + ts[k] * half * ny;
                        double z = Math.Abs(ts[k]) < 1 ? lv : Math.Min(lv, b.G(px, py) - p.RiverDip);
                        section[3 * k] = px; section[3 * k + 1] = py; section[3 * k + 2] = z;
                    }
                    if (i > 0) surface.AddSegment(centres[i - 1][0], centres[i - 1][1], c[0], c[1], lastSection == null ? lv : lastSection[8], lv, half);   // [8]: the centre vertex's z
                    // A segment belongs to the chunk of its midpoint; a new chunk repeats the previous section in its own mesh.
                    var mid = i == 0 ? c : new[] { (centres[i - 1][0] + c[0]) / 2, (centres[i - 1][1] + c[1]) / 2 };
                    var m = b.Mesh(OverlayLayer.Rivers, "river", null, mid[0], mid[1], Double.NegativeInfinity, false, 0);
                    if (!ReferenceEquals(m, current))
                    {
                        if (lastSection != null) last = Section(b, m, lastSection, ts, half, White);
                        if (!m.PieceIds.Contains(id)) m.PieceIds.Add(id);
                        current = m;
                    }
                    var now = Section(b, m, section, ts, half, White);
                    if (i > 0 && last != null)
                        for (int k = 0; k < 4; k++) { m.Triangle(last[k], now[k], now[k + 1]); m.Triangle(last[k], now[k + 1], last[k + 1]); }
                    last = now; lastSection = section;
                }
                b.Count(OverlayLayer.Rivers + ":chain");
            }
            // Pools (lone river cells and the centres the strips miss) and, by policy, fills: discs on the same level surface.
            int discIndex = 0;
            foreach (var w in d.Water)
            {
                if (w.Kind == "river") continue;
                if (w.Kind == "fill" && !p.RiverFills) continue;
                double radius = w.Kind == "pool" ? half * p.PoolScale : Math.Max(w.HalfLength, w.HalfWidth);
                string id = w.Kind + "." + (discIndex++).ToString(CultureInfo.InvariantCulture);
                var m = b.Mesh(OverlayLayer.Rivers, "river", null, w.X, w.Y, Double.NegativeInfinity, false, 0);
                double lv = level(w.X, w.Y);
                int segments = p.DiscSegments;
                int centre = m.Vertex(DeltaX(m.OriginX, w.X, d.Width), w.Y - m.OriginY, lv, .5, .5, White);
                var rings = new[] { RiverInner, 1.0 };
                var idx = new int[2, segments];
                for (int r = 0; r < 2; r++)
                    for (int k = 0; k < segments; k++)
                    {
                        double a = 2 * Math.PI * k / segments, px = w.X + rings[r] * radius * Math.Cos(a), py = w.Y + rings[r] * radius * Math.Sin(a);
                        double z = r == 0 ? lv : Math.Min(lv, b.G(px, py) - p.RiverDip);
                        idx[r, k] = m.Vertex(DeltaX(m.OriginX, px, d.Width), py - m.OriginY, z, .5 + .5 * rings[r] * Math.Cos(a), .5 + .5 * rings[r] * Math.Sin(a), White);
                    }
                for (int k = 0; k < segments; k++)
                {
                    int k1 = (k + 1) % segments;
                    m.Triangle(centre, idx[0, k], idx[0, k1]);
                    m.Triangle(idx[0, k], idx[1, k], idx[1, k1]); m.Triangle(idx[0, k], idx[1, k1], idx[0, k1]);
                }
                surface.AddDisc(w.X, w.Y, radius, lv);
                m.PieceIds.Add(id);
                b.Count(OverlayLayer.Rivers + ":" + w.Kind);
            }
            return b.Finish();
        }

        // ---- single pieces: MMR-247's decal lab (mesh columns), one mesh with its origin at (x, y) ----
        // A quad grid (segment units per cell) over surface + lift, the rect's u along +x.
        public static OverlayMesh LabQuad(string layer, string material, string look, double x, double y, double halfL, double halfW, double[] rect, uint colour, double glow,
                                          Func<double, double, double> surface, double lift, double segment)
        {
            var m = new OverlayMesh { Layer = layer, Material = material, Look = look, OriginX = x, OriginY = y, Glow = glow };
            int nu = Math.Max(1, (int)Math.Ceiling(2 * halfL / segment - 1e-9)), nv = Math.Max(1, (int)Math.Ceiling(2 * halfW / segment - 1e-9));
            var k = new int[nu + 1, nv + 1];
            for (int i = 0; i <= nu; i++)
                for (int j = 0; j <= nv; j++)
                {
                    double su = -1 + 2.0 * i / nu, tv = -1 + 2.0 * j / nv;
                    k[i, j] = m.Vertex(su * halfL, tv * halfW, surface(x + su * halfL, y + tv * halfW) + lift, rect[0] + (su + 1) / 2 * (rect[2] - rect[0]), rect[1] + (tv + 1) / 2 * (rect[3] - rect[1]), colour);
                }
            for (int i = 0; i < nu; i++)
                for (int j = 0; j < nv; j++) { m.Triangle(k[i, j], k[i + 1, j], k[i + 1, j + 1]); m.Triangle(k[i, j], k[i + 1, j + 1], k[i, j + 1]); }
            m.PieceIds.Add("lab");
            return m;
        }
        // A river ribbon along +x (2 halfL long, 2 half wide): five vertices across, the inner three at the centre's ground + depth, the edges at
        // min(level, ground - dip), as the rivers layer draws them.
        public static OverlayMesh LabRibbon(string material, double x, double y, double halfL, double half, Func<double, double, double> ground, double depth, double dip, double segment)
        {
            var m = new OverlayMesh { Layer = OverlayLayer.Rivers, Material = material, OriginX = x, OriginY = y };
            int n = Math.Max(1, (int)Math.Ceiling(2 * halfL / segment - 1e-9));
            double[] ts = { -1, -RiverInner, 0, RiverInner, 1 };
            int[] last = null;
            for (int i = 0; i <= n; i++)
            {
                double lx = -halfL + 2 * halfL * i / n, level = ground(x + lx, y) + depth;
                var now = new int[5];
                for (int k = 0; k < 5; k++)
                {
                    double ly = ts[k] * half, z = Math.Abs(ts[k]) < 1 ? level : Math.Min(level, ground(x + lx, y + ly) - dip);
                    now[k] = m.Vertex(lx, ly, z, (ts[k] + 1) / 2, (double)i / n, 0xFFFFFFFFu);
                }
                if (last != null) for (int k = 0; k < 4; k++) { m.Triangle(last[k], now[k], now[k + 1]); m.Triangle(last[k], now[k + 1], last[k + 1]); }
                last = now;
            }
            m.PieceIds.Add("lab");
            return m;
        }

        private static int[] Section(Builder b, OverlayMesh m, double[] section, double[] ts, double half, uint colour)
        {
            var r = new int[5];
            for (int k = 0; k < 5; k++) r[k] = m.Vertex(DeltaX(m.OriginX, section[3 * k], b.Plan.Width), section[3 * k + 1] - m.OriginY, section[3 * k + 2], (ts[k] + 1) / 2, 0, colour);
            return r;
        }

        // The point at arc length s along a polyline.
        private static double[] At(List<double[]> points, double s)
        {
            for (int i = 0; i + 1 < points.Count; i++)
            {
                double dx = points[i + 1][0] - points[i][0], dy = points[i + 1][1] - points[i][1], d = Math.Sqrt(dx * dx + dy * dy);
                if (d <= 1e-12) continue;
                if (s <= d || i + 2 == points.Count) { double t = Math.Min(1, Math.Max(0, s / d)); return new[] { points[i][0] + dx * t, points[i][1] + dy * t }; }
                s -= d;
            }
            return (double[])points[points.Count - 1].Clone();
        }
    }

    // What the installer draws on: the engine scene (OverlayMeshRuntime) or a recording target offline. x: the drawn origin x (the image of
    // the mesh's OriginX nearest the camera). Add returns null when the mesh's material does not resolve.
    internal interface IOverlayMeshTarget
    {
        object Add(OverlayMesh mesh, double x);
        void Move(object handle, OverlayMesh mesh, double x);
        void Remove(object handle);
        void SetVisible(object handle, bool visible);
    }

    // Draws an overlay plan on a target, one entity per mesh, keyed by mesh key and geometry digest (a live change, a reveal step or a re-plan
    // touches only the meshes whose geometry changed); each at its periodic image; hides and shows a layer (MMR-247's toggle); removes
    // everything natively on a live scene, or (MMR-262) lets go of the handles with no engine call after Scene.ClearAll.
    internal sealed class OverlayMeshInstaller
    {
        private sealed class Drawn { public object Handle; public double X; public OverlayMesh Mesh; }
        private readonly IOverlayMeshTarget target;
        private readonly Dictionary<string, Drawn> drawn = new Dictionary<string, Drawn>(StringComparer.Ordinal);
        public readonly List<string> Errors = new List<string>();
        public readonly SortedSet<string> MissingMaterials = new SortedSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> HiddenLayers = new HashSet<string>(StringComparer.Ordinal);
        public OverlayMeshPlan Plan { get; private set; }
        public int Created, Removed, Moved, NotDrawn, Dropped;
        public int Live { get { return drawn.Count; } }
        public OverlayMeshInstaller(IOverlayMeshTarget target) { this.target = target; }

        public void Sync(OverlayMeshPlan plan, Func<double, double> image)
        {
            var next = new Dictionary<string, OverlayMesh>(StringComparer.Ordinal);
            foreach (var m in plan.Meshes) next[m.Key] = m;
            foreach (var key in drawn.Keys.ToList())
            {
                OverlayMesh m;
                if (next.TryGetValue(key, out m) && m.Digest == drawn[key].Mesh.Digest) continue;
                RemoveOne(key);
            }
            NotDrawn = 0;
            foreach (var m in plan.Meshes)
            {
                if (drawn.ContainsKey(m.Key)) continue;
                if (MissingMaterials.Contains(m.Material)) { NotDrawn++; continue; }
                double x = image(m.OriginX);
                object handle = null;
                try { handle = target.Add(m, x); }
                catch (Exception e) { if (Errors.Count < 20) Errors.Add("create " + m.Key + ": " + e.GetType().Name + ": " + e.Message); }
                if (handle == null) { MissingMaterials.Add(m.Material); NotDrawn++; continue; }
                if (HiddenLayers.Contains(m.Layer)) { var h = handle; Try("hide " + m.Key, () => target.SetVisible(h, false)); }
                drawn[m.Key] = new Drawn { Handle = handle, X = x, Mesh = m }; Created++;
            }
            Plan = plan;
        }

        public void Refresh(Func<double, double> image)
        {
            foreach (var d in drawn.Values)
            {
                double x = image(d.Mesh.OriginX);
                if (Math.Abs(x - d.X) < 1e-6) continue;
                var one = d;
                if (Try("move " + d.Mesh.Key, () => target.Move(one.Handle, one.Mesh, x))) { d.X = x; Moved++; }
            }
        }

        public void SetVisible(string layer, bool visible)
        {
            if (visible) HiddenLayers.Remove(layer); else HiddenLayers.Add(layer);
            foreach (var d in drawn.Values) if (d.Mesh.Layer == layer) { var one = d; Try("visible " + d.Mesh.Key, () => target.SetVisible(one.Handle, visible)); }
        }

        private void RemoveOne(string key)
        {
            var d = drawn[key];
            drawn.Remove(key);
            Try("remove " + key, () => target.Remove(d.Handle));
            Removed++;
        }
        public void Remove() { foreach (var key in drawn.Keys.ToList()) RemoveOne(key); }
        public int Drop() { int n = drawn.Count; drawn.Clear(); Dropped += n; return n; }

        public IEnumerable<KeyValuePair<OverlayMesh, double>> DrawnMeshes() { foreach (var d in drawn.Values) yield return new KeyValuePair<OverlayMesh, double>(d.Mesh, d.X); }
        // Pieces (stamps, chains, discs) a layer draws now.
        public int Pieces(string layer) { return drawn.Values.Where(d => d.Mesh.Layer == layer).Sum(d => d.Mesh.PieceIds.Count); }

        private bool Try(string what, Action action)
        {
            try { action(); return true; }
            catch (Exception e) { if (Errors.Count < 20) Errors.Add(what + ": " + e.GetType().Name + ": " + e.Message); return false; }
        }

        public Dictionary<string, object> Record()
        {
            var r = Plan == null ? new Dictionary<string, object>() : Plan.Record();
            var live = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var layer in OverlayLayer.All)
            {
                var list = drawn.Values.Where(d => d.Mesh.Layer == layer).ToList();
                live[layer] = new Dictionary<string, object> { ["entities"] = list.Count, ["pieces"] = list.Sum(d => d.Mesh.PieceIds.Count), ["triangles"] = list.Sum(d => d.Mesh.Triangles), ["hidden"] = HiddenLayers.Contains(layer) };
            }
            r["installed"] = new Dictionary<string, object> { ["created"] = Created, ["live"] = drawn.Count, ["byLayer"] = live, ["notDrawn"] = NotDrawn, ["moved"] = Moved, ["removed"] = Removed, ["dropped"] = Dropped,
                ["missingMaterials"] = MissingMaterials.ToList(), ["errors"] = Errors.ToList() };
            return r;
        }
    }
}
