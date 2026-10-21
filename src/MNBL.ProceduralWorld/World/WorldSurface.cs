using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-37: the surface a world bundle defines, as RuntimeWorldBackend builds and queries it (docs/runtime-surface.md). Pure managed:
    // the offline contract tests and the in-game backend share it.
    //   * Inputs: the bundle's compiled terrain (heightfield.bin, the encoder input at scale 1; weights.bin v2, one alpha plane per material
    //     layer, MMR-255; masks.bin)
    //     and projection.json. The physical domain is the projection's square periodic layout (physicalWidth = physicalHeight); campaign
    //     XY (logical, strip-free) is kept separate from it and from source metres, and the vertical exaggeration is recorded.
    //   * Encoding model: exactly the surface encoder's (src/WorldBridge/surface.cpp make_grid): bounds floor/ceil at 0.001, 16-bit
    //     round-half-even quantization; RuntimeWorldBackend refuses an encoding whose published quantized heights differ from this model.
    //   * Chunks: the native terrain nodes, 4 x 4, each (cells + 1)^2 samples sharing its border samples with its neighbours; bounds,
    //     height range and material layers (the base, and every other layer with a nonzero sample in the node, as the encoder masks them)
    //     are deterministic.
    //   * Queries: the quantized height is the main-diagonal (x,y)-(x+1,y+1) interpolation of the encoded samples; the collision reference
    //     interpolates the samples truncated toward zero at 0.03 (MMR-16 calibration) the same way, and is what the engine's terrain,
    //     ground and ray queries return (to 1e-5 in the MMR-37 runs; within 0.03 of the quantized height); normals are the main-diagonal
    //     triangle's.
    //   * Landmarks: the points runtime results must agree with: compiler settlement positions (logical + strip/pole margin), chunk
    //     corners and centres, the MMR-16 inset grid points, coast samples (the compiler's coast sign), and the periodic seam pairs
    //     (physical x and x + logical width hold the same samples).
    // A query outside the domain throws: no shortcut returns another world's (or Calradia's) heights.
    internal sealed class WorldSurface
    {
        public sealed class Chunk
        {
            public int X, Y, FirstColumn, FirstRow, Cells, Mask;   // Mask: bit k set when layer k is present in the node (the base always)
            public double MinX, MinY, MaxX, MaxY, MinHeight, MaxHeight;
        }
        public sealed class Landmark { public string Name, Kind; public double X, Y; }

        public const int Nodes = 4;
        public const double CollisionStep = .03;
        public string WorldContentId { get; private set; }
        public int Side { get; private set; }
        public int Cells { get; private set; }
        public int HeightmapLevel { get; private set; }
        public double Extent { get; private set; }
        public double Spacing { get; private set; }
        public double NodeSize { get { return Extent / Nodes; } }
        public double Low { get; private set; }
        public double High { get; private set; }
        public double Step { get; private set; }
        public double Strip { get; private set; }
        public double PoleMargin { get; private set; }
        public double LogicalWidth { get; private set; }
        public double LogicalHeight { get; private set; }
        public int SeamColumns { get; private set; }
        public int SourceGridSide { get; private set; }          // MMR-64: projection source.gridSide (column n-1 duplicates column 0)
        public double CentralMeridianDeg { get; private set; }
        public SortedDictionary<string, object> Scale { get; private set; }
        public string InputsSha256 { get; private set; }
        public List<Chunk> Chunks { get; private set; }
        // MMR-255: the material layers weights.bin v2 names, in slot order (terrain-materials.json layerSet).
        public IList<string> LayerNames { get; private set; }
        private double[] source, quantized, collision;
        private int[] q;
        private byte[][] weights;
        private byte[] intent, biome, landform;

        public double[] Quantized { get { return (double[])quantized.Clone(); } }

        // MMR-42: the biome id (masks.bin plane 0; worldgen/policies/terrain-materials.json biomes) of the sample nearest a physical point,
        // clamped to the domain. The strips hold their canonical samples, so a strip point reads its canonical biome.
        public int BiomeAt(double x, double y) { return biome[Nearest(x, y)]; }
        // MMR-42: the landform index (masks.bin plane 2: 0 submerged, 1 plain / hillside, 2 mountain / ridge, 3 valley) at the nearest sample.
        public int LandformAt(double x, double y) { return landform[Nearest(x, y)]; }
        // MMR-46: the sample's water intent (terrain-materials.json intents: 0 land, 1 ocean, 2 lake, 3 river) at a physical position.
        public int IntentAt(double x, double y) { return intent[Nearest(x, y)]; }
        private int Nearest(double x, double y)
        {
            int c = (int)Math.Max(0, Math.Min(Side - 1, Math.Round(x / Spacing))), r = (int)Math.Max(0, Math.Min(Side - 1, Math.Round(y / Spacing)));
            return r * Side + c;
        }

        public static WorldSurface FromBundle(WorldDataBundle bundle)
        {
            if (bundle == null) throw new ArgumentNullException("bundle");
            return FromFiles(bundle.WorldContentId, bundle.ReadVerified("compiled/terrain/heightfield.bin"), bundle.ReadVerified("compiled/terrain/weights.bin"),
                bundle.ReadVerified("compiled/terrain/masks.bin"), new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/terrain/projection.json")));
        }

        public static WorldSurface FromFiles(string contentId, byte[] heightfield, byte[] weightBytes, byte[] masks, string projectionJson)
        {
            var s = new WorldSurface { WorldContentId = contentId };
            s.source = ReadHeights(heightfield, "MNBLHF01");
            int side = (int)BitConverter.ToUInt32(heightfield, 8);
            if (side != 129 && side != 257 && side != 513) throw new InvalidDataException("The surface encoder takes a 129, 257 or 513 sample heightfield, not " + side + ".");
            s.Side = side; s.Cells = (side - 1) / Nodes; s.HeightmapLevel = s.Cells == 32 ? 5 : s.Cells == 64 ? 6 : 7;   // MMR-278: the shaped surface is 513 (level 7)
            string[] names;
            s.weights = Weights(weightBytes, side, out names);
            s.LayerNames = Array.AsReadOnly(names);
            s.intent = Plane(masks, "MNBLMK01", side, 4, 1);
            s.biome = Plane(masks, "MNBLMK01", side, 4, 0);   // MMR-42: the biome plane (terrain-materials.json biome ids)
            s.landform = Plane(masks, "MNBLMK01", side, 4, 2);   // MMR-42: the landform plane (terrain-materials.json landforms)
            var projection = (IDictionary<string, object>)new JavaScriptSerializer().DeserializeObject(projectionJson.TrimStart('﻿'));
            if (Text(projection, "schema") != "mnbl.projection" || Number(projection, "version") != 1) throw new InvalidDataException("Not an mnbl.projection version 1.");
            var layout = Obj(projection, "periodicLayout"); var target = Obj(projection, "target"); var vertical = Obj(projection, "vertical"); var horizontal = Obj(projection, "horizontal");
            double width = Number(layout, "physicalWidth"), height = Number(layout, "physicalHeight");
            if (!(width > 0) || width != height) throw new InvalidDataException("The native terrain is square: physicalWidth " + width + " and physicalHeight " + height + " differ.");
            s.Extent = width; s.Spacing = width / (side - 1);
            if (Math.Abs(s.Extent / 4 * 1000 - Math.Round(s.Extent / 4 * 1000)) > 1e-7) throw new InvalidDataException("The node size must be representable at 0.001.");
            s.Strip = Number(layout, "strip"); s.PoleMargin = Number(layout, "poleMargin");
            s.LogicalWidth = Number(target, "logicalWidth"); s.LogicalHeight = Number(target, "logicalHeight");
            double period = s.LogicalWidth / s.Spacing;
            if (Math.Abs(period - Math.Round(period)) > 1e-9) throw new InvalidDataException("The logical width is not a whole number of samples.");
            s.SeamColumns = (int)Math.Round(period);
            double gridSide = Number(Obj(projection, "source"), "gridSide");
            if (gridSide < 3 || gridSide != Math.Floor(gridSide)) throw new InvalidDataException("The projection's source grid side " + gridSide + " is not a whole number of at least 3.");
            s.SourceGridSide = (int)gridSide; s.CentralMeridianDeg = Number(projection, "centralMeridianDeg");
            s.Scale = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["physicalExtent"] = s.Extent, ["sampleSpacing"] = s.Spacing, ["logicalWidth"] = s.LogicalWidth, ["logicalHeight"] = s.LogicalHeight,
                ["strip"] = s.Strip, ["poleMargin"] = s.PoleMargin, ["campaignFromPhysical"] = "logical = physical - (strip, poleMargin); logical x wraps every logicalWidth",
                ["horizontalWorldUnitsPerMetre"] = Number(horizontal, "equatorWorldUnitsPerMetre"), ["verticalWorldUnitsPerMetre"] = Number(vertical, "worldUnitsPerMetre"),
                ["verticalExaggeration"] = Number(vertical, "exaggeration"), ["seaDatum"] = Number(vertical, "datumM") };
            // The encoder's quantization (surface.cpp make_grid), bit for bit.
            double low = s.source.Min(), high = s.source.Max();
            s.Low = Math.Floor(low * 1000) / 1000; s.High = Math.Ceiling(high * 1000) / 1000;
            if (s.High <= s.Low) s.High = s.Low + 1;
            s.Step = (s.High - s.Low) / 65535;
            int n = side * side;
            s.q = new int[n]; s.quantized = new double[n]; s.collision = new double[n];
            for (int i = 0; i < n; i++)
            {
                s.q[i] = (int)Math.Max(0, Math.Min(65535, Math.Round((s.source[i] - s.Low) / (s.High - s.Low) * 65535, MidpointRounding.ToEven)));
                s.quantized[i] = s.Low + s.q[i] * s.Step;
                s.collision[i] = CollisionStep * Math.Truncate(s.quantized[i] / CollisionStep);
            }
            s.Chunks = new List<Chunk>();
            for (int iy = 0; iy < Nodes; iy++) for (int ix = 0; ix < Nodes; ix++)
            {
                int qmin = 65535, qmax = 0, mask = 1;
                for (int r = 0; r <= s.Cells; r++) for (int c = 0; c <= s.Cells; c++)
                {
                    int i = (iy * s.Cells + r) * side + ix * s.Cells + c;
                    qmin = Math.Min(qmin, s.q[i]); qmax = Math.Max(qmax, s.q[i]);
                    for (int k = 1; k < s.weights.Length; k++) if (s.weights[k][i] != 0) mask |= 1 << k;
                }
                s.Chunks.Add(new Chunk { X = ix, Y = iy, FirstColumn = ix * s.Cells, FirstRow = iy * s.Cells, Cells = s.Cells, Mask = mask,
                    MinX = ix * s.NodeSize, MinY = iy * s.NodeSize, MaxX = (ix + 1) * s.NodeSize, MaxY = (iy + 1) * s.NodeSize,
                    MinHeight = s.Low + qmin * s.Step, MaxHeight = s.Low + qmax * s.Step });
            }
            using (var sha = SHA256.Create())
            {
                foreach (var part in new[] { heightfield, weightBytes, masks, Encoding.UTF8.GetBytes(projectionJson) })
                    sha.TransformBlock(part, 0, part.Length, null, 0);
                sha.TransformFinalBlock(new byte[0], 0, 0);
                s.InputsSha256 = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
            return s;
        }

        // The encoder's published quantized heightfield (MNBLHF01, side^2 binary64) for this model, for byte comparison.
        public byte[] QuantizedHeightfieldBytes()
        {
            var bytes = new byte[12 + quantized.Length * 8];
            Encoding.ASCII.GetBytes("MNBLHF01").CopyTo(bytes, 0); BitConverter.GetBytes((uint)Side).CopyTo(bytes, 8);
            Buffer.BlockCopy(quantized, 0, bytes, 12, quantized.Length * 8);
            return bytes;
        }

        public double Sample(int column, int row) { return quantized[Index(column, row)]; }
        public int Code(int column, int row) { return q[Index(column, row)]; }
        // The stored alpha of material layer `layer` (slot order) at a sample.
        public byte Weight(int layer, int column, int row) { return weights[layer][Index(column, row)]; }
        public byte Intent(int column, int row) { return intent[Index(column, row)]; }
        private int Index(int column, int row)
        {
            if (column < 0 || row < 0 || column >= Side || row >= Side) throw new ArgumentOutOfRangeException("sample", "Sample (" + column + ", " + row + ") is outside the " + Side + " sample grid.");
            return row * Side + column;
        }

        public bool Contains(double x, double y) { return x >= 0 && y >= 0 && x <= Extent && y <= Extent && !double.IsNaN(x) && !double.IsNaN(y); }
        private void Cell(double x, double y, out int ix, out int iy, out double u, out double v)
        {
            if (!Contains(x, y)) throw new ArgumentOutOfRangeException("position", "(" + x + ", " + y + ") is outside the world's " + Extent + " unit terrain.");
            double gx = x / Spacing, gy = y / Spacing;
            ix = Math.Min((int)gx, Side - 2); iy = Math.Min((int)gy, Side - 2); u = gx - ix; v = gy - iy;
        }
        private static double MainTriangle(double a, double b, double c, double d, double u, double v)
        {
            return u >= v ? a * (1 - u) + b * (u - v) + d * v : a * (1 - v) + c * (v - u) + d * u;
        }
        private double Interpolate(double[] field, double x, double y)
        {
            int ix, iy; double u, v; Cell(x, y, out ix, out iy, out u, out v);
            int i = iy * Side + ix;
            return MainTriangle(field[i], field[i + 1], field[i + Side], field[i + Side + 1], u, v);
        }
        // The quantized height (encoded samples, main diagonal); native heights are within 0.03 of it (the truncation).
        public double Height(double x, double y) { return Interpolate(quantized, x, y); }
        // The collision reference (samples truncated toward zero at 0.03, main diagonal): native height queries return it, and ray and
        // picking points agree with it.
        public double CollisionHeight(double x, double y) { return Interpolate(collision, x, y); }
        // The main-diagonal triangle's unit normal (x, y, z) at a point.
        public double[] Normal(double x, double y)
        {
            int ix, iy; double u, v; Cell(x, y, out ix, out iy, out u, out v);
            int i = iy * Side + ix;
            double a = quantized[i], b = quantized[i + 1], c = quantized[i + Side], d = quantized[i + Side + 1], dx, dy;
            if (u >= v) { dx = b - a; dy = d - b; } else { dx = d - c; dy = c - a; }
            double nx = -dx / Spacing, ny = -dy / Spacing, length = Math.Sqrt(nx * nx + ny * ny + 1);
            return new[] { nx / length, ny / length, 1 / length };
        }
        // A material layer's alpha interpolated like heights (the engine blends per sample; the chunk seam check compares samples).
        public double LayerWeight(int layer, double x, double y)
        {
            int ix, iy; double u, v; Cell(x, y, out ix, out iy, out u, out v);
            int i = iy * Side + ix; var w = weights[layer];
            return MainTriangle(w[i], w[i + 1], w[i + Side], w[i + Side + 1], u, v);
        }

        // The earliest intersection of a segment with the collision reference (both triangles of every cell), as a fraction of it.
        public bool Intersect(double[] from, double[] to, out double fraction)
        {
            fraction = double.PositiveInfinity;
            double[] delta = { to[0] - from[0], to[1] - from[1], to[2] - from[2] };
            // Only the cells the segment's XY bounding box touches can be hit.
            int c0 = Math.Max(0, (int)Math.Floor(Math.Min(from[0], to[0]) / Spacing) - 1), c1 = Math.Min(Side - 2, (int)Math.Floor(Math.Max(from[0], to[0]) / Spacing) + 1);
            int r0 = Math.Max(0, (int)Math.Floor(Math.Min(from[1], to[1]) / Spacing) - 1), r1 = Math.Min(Side - 2, (int)Math.Floor(Math.Max(from[1], to[1]) / Spacing) + 1);
            for (int y = r0; y <= r1; y++) for (int x = c0; x <= c1; x++)
            {
                double[] a = { x * Spacing, y * Spacing, collision[y * Side + x] }, b = { (x + 1) * Spacing, y * Spacing, collision[y * Side + x + 1] };
                double[] c = { x * Spacing, (y + 1) * Spacing, collision[(y + 1) * Side + x] }, d = { (x + 1) * Spacing, (y + 1) * Spacing, collision[(y + 1) * Side + x + 1] };
                Triangle(from, delta, a, b, d, ref fraction); Triangle(from, delta, a, d, c, ref fraction);
            }
            return !double.IsPositiveInfinity(fraction);
        }
        private static void Triangle(double[] o, double[] delta, double[] a, double[] b, double[] c, ref double nearest)
        {
            double[] e1 = Sub(b, a), e2 = Sub(c, a), p = Cross(delta, e2);
            double det = Dot(e1, p);
            if (Math.Abs(det) < 1e-12) return;
            double[] s = Sub(o, a); double u = Dot(s, p) / det;
            if (u < -1e-10 || u > 1 + 1e-10) return;
            double[] qv = Cross(s, e1); double v = Dot(delta, qv) / det;
            if (v < -1e-10 || u + v > 1 + 1e-10) return;
            double t = Dot(e2, qv) / det;
            if (t >= 0 && t <= 1 && t < nearest) nearest = t;
        }
        private static double[] Sub(double[] a, double[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
        private static double[] Cross(double[] a, double[] b) { return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }
        private static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }

        // Physical position of a campaign (logical) position.
        public double[] Physical(double logicalX, double logicalY) { return new[] { logicalX + Strip, logicalY + PoleMargin }; }

        // The landmarks runtime terrain, query and picking results must agree with. settlements: the compiler's travel graph
        // (compiled/travel/travel-graph.json), whose settlement points are logical; null for none.
        public List<Landmark> Landmarks(IDictionary<string, object> travelGraph, int settlementCount, int coastCount)
        {
            var list = new List<Landmark>();
            int end = Side - 2, mid = (Side - 1) / 2;
            foreach (var g in new[] { new[] { 1, 1 }, new[] { end, 1 }, new[] { 1, end }, new[] { end, end }, new[] { mid, mid }, new[] { mid, mid / 2 }, new[] { mid / 2, mid } })
                list.Add(new Landmark { Kind = "grid", Name = "grid_" + g[0] + "_" + g[1], X = g[0] * Spacing, Y = g[1] * Spacing });
            foreach (var p in new[] { new[] { 50.25, 64.6 }, new[] { 80.75, 40.2 } })
                list.Add(new Landmark { Kind = "grid", Name = "off_vertex_" + p[0] + "_" + p[1], X = p[0] * (Side - 1) / 128.0 * Spacing, Y = p[1] * (Side - 1) / 128.0 * Spacing });
            foreach (var chunk in Chunks)
            {
                list.Add(new Landmark { Kind = "chunk", Name = "chunk_centre_" + chunk.X + "_" + chunk.Y, X = (chunk.MinX + chunk.MaxX) / 2 + Spacing / 3, Y = (chunk.MinY + chunk.MaxY) / 2 + Spacing / 5 });
                if (chunk.X > 0 && chunk.Y > 0) list.Add(new Landmark { Kind = "chunk", Name = "chunk_corner_" + chunk.X + "_" + chunk.Y, X = chunk.MinX, Y = chunk.MinY });
            }
            if (travelGraph != null)
            {
                // Unplaceable (excluded) settlements have no point.
                var settlements = List(travelGraph, "settlements").OfType<IDictionary<string, object>>().Where(e => Obj(e, "point") != null).OrderBy(e => Text(e, "id"), StringComparer.Ordinal).ToList();
                int take = Math.Min(settlementCount, settlements.Count);
                for (int i = 0; i < take; i++)
                {
                    var e = settlements[(int)((long)i * settlements.Count / take)];
                    var point = Obj(e, "point"); var physical = Physical(Number(point, "x"), Number(point, "y"));
                    list.Add(new Landmark { Kind = "settlement", Name = "settlement_" + Text(e, "kind") + "_" + Text(e, "id"), X = physical[0], Y = physical[1] });
                }
            }
            // Coast samples: land samples next to ocean (intent 0 beside 1), in row-major order, evenly spread.
            var coast = new List<int[]>();
            for (int r = 1; r < Side - 1; r++) for (int c = 1; c < Side - 1; c++)
                if (intent[r * Side + c] == 0 && (intent[r * Side + c + 1] == 1 || intent[r * Side + c - 1] == 1 || intent[(r + 1) * Side + c] == 1 || intent[(r - 1) * Side + c] == 1))
                    coast.Add(new[] { c, r });
            for (int i = 0; i < Math.Min(coastCount, coast.Count); i++)
            {
                var cr = coast[(int)((long)i * coast.Count / Math.Min(coastCount, coast.Count))];
                list.Add(new Landmark { Kind = "coast", Name = "coast_" + cr[0] + "_" + cr[1], X = cr[0] * Spacing, Y = cr[1] * Spacing });
            }
            return list;
        }

        // Points either side of every interior chunk border (vertical lines x = k * nodeSize and horizontal lines y = k * nodeSize), at
        // positions along the border that are off the sample grid, offset by +/- epsilon across it.
        public List<KeyValuePair<string, double[]>> BorderSamples(int perBorder, double epsilon)
        {
            var list = new List<KeyValuePair<string, double[]>>();
            for (int k = 1; k < Nodes; k++)
                for (int j = 0; j < perBorder; j++)
                {
                    double along = (j + .5) * Extent / perBorder + Spacing * .37, line = k * NodeSize;
                    along = Math.Min(Extent - Spacing, along);
                    list.Add(new KeyValuePair<string, double[]>("vertical_" + k + "_" + j, new[] { line - epsilon, along, line + epsilon, along }));
                    list.Add(new KeyValuePair<string, double[]>("horizontal_" + k + "_" + j, new[] { along, line - epsilon, along, line + epsilon }));
                }
            return list;
        }

        // Periodic seam pairs: physical x and x + logicalWidth (the strips copy the other side of the seam) at off-grid positions.
        public List<KeyValuePair<string, double[]>> SeamPairs(int count)
        {
            var list = new List<KeyValuePair<string, double[]>>();
            for (int i = 0; i < count; i++)
            {
                double x = (i + .41) * (Extent - LogicalWidth) / count, y = PoleMargin + (i + .29) * LogicalHeight / count;
                list.Add(new KeyValuePair<string, double[]>("seam_" + i, new[] { x, y, x + LogicalWidth, y }));
            }
            return list;
        }

        // Every sample column c and c + seamColumns that the strips duplicate, whole columns compared (heights and every material layer
        // plane); the number of samples that differ.
        public int SeamMismatches()
        {
            int bad = 0;
            for (int c = 0; c + SeamColumns < Side; c++) for (int r = 0; r < Side; r++)
            {
                int a = r * Side + c, b = a + SeamColumns;
                if (q[a] != q[b] || weights.Any(w => w[a] != w[b])) bad++;
            }
            return bad;
        }

        private static double[] ReadHeights(byte[] bytes, string magic)
        {
            if (bytes == null || bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 8) != magic) throw new InvalidDataException("Not " + magic + ".");
            int side = (int)BitConverter.ToUInt32(bytes, 8);
            if (side < 3 || bytes.Length != 12 + (long)side * side * 8) throw new InvalidDataException(magic + " length does not match its side.");
            var values = new double[side * side];
            Buffer.BlockCopy(bytes, 12, values, 0, values.Length * 8);
            foreach (double v in values) if (double.IsNaN(v) || double.IsInfinity(v)) throw new InvalidDataException(magic + " holds a non-finite height.");
            return values;
        }
        // weights.bin v2 (MMR-255): "MNBLWT02", u32 side, u32 N, N x (u32 length, UTF-8 name), N planes of side^2 alpha bytes; plane 0 is
        // the base at 255 everywhere.
        private static byte[][] Weights(byte[] bytes, int side, out string[] names)
        {
            if (bytes == null || bytes.Length < 16 || Encoding.ASCII.GetString(bytes, 0, 8) != "MNBLWT02" || BitConverter.ToUInt32(bytes, 8) != side)
                throw new InvalidDataException("weights.bin is not MNBLWT02 at the heightfield's " + side + " samples (pre-epic MNBLWT01 bundles are refused).");
            int count = (int)BitConverter.ToUInt32(bytes, 12), p = 16;
            if (count < 1 || count > 16) throw new InvalidDataException("weights.bin holds " + count + " layers, not 1 to 16.");
            names = new string[count];
            for (int k = 0; k < count; k++)
            {
                if (p + 4 > bytes.Length) throw new InvalidDataException("weights.bin's layer names are truncated.");
                int length = (int)BitConverter.ToUInt32(bytes, p); p += 4;
                if (length < 1 || length > 64 || p + length > bytes.Length) throw new InvalidDataException("weights.bin's layer name " + k + " is malformed.");
                names[k] = Encoding.UTF8.GetString(bytes, p, length); p += length;
            }
            long n = (long)side * side;
            if (bytes.Length != p + count * n || names.Distinct(StringComparer.Ordinal).Count() != count) throw new InvalidDataException("weights.bin's planes do not match its layer table.");
            var planes = new byte[count][];
            for (int k = 0; k < count; k++) { planes[k] = new byte[n]; Buffer.BlockCopy(bytes, p + (int)(k * n), planes[k], 0, (int)n); }
            if (planes[0].Any(v => v != 255)) throw new InvalidDataException("weights.bin's base layer is not 255 everywhere.");
            return planes;
        }
        private static byte[] Plane(byte[] bytes, string magic, int side, int planes, int plane)
        {
            if (bytes == null || bytes.Length != 12 + (long)side * side * planes || Encoding.ASCII.GetString(bytes, 0, 8) != magic || BitConverter.ToUInt32(bytes, 8) != side)
                throw new InvalidDataException(magic + " does not match the heightfield's " + side + " samples.");
            var values = new byte[side * side];
            Buffer.BlockCopy(bytes, 12 + plane * side * side, values, 0, values.Length);
            return values;
        }
        private static IDictionary<string, object> Obj(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v as IDictionary<string, object> : null; }
        private static string Text(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v as string : null; }
        private static double Number(IDictionary<string, object> o, string key) { object v; if (o == null || !o.TryGetValue(key, out v) || v == null) throw new InvalidDataException("projection field " + key + " is missing"); return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
        private static IEnumerable<object> List(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v is IEnumerable && !(v is string) ? ((IEnumerable)v).Cast<object>() : Enumerable.Empty<object>(); }
    }
}
