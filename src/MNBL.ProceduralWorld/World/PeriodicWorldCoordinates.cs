using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-64: the one canonical coordinate contract of an x-periodic world (docs/periodic-coordinates.md). Pure managed and immutable:
    // the campaign (PeriodicWorld, PeriodicPatches, GeneratedMapScene), the navigation validator and the offline tests share it, and the
    // later consumers (MMR-65 route execution and spatial queries, MMR-66 camera, render copies and picking) read it, never redefine it.
    //   * Frames. Physical: native scene XY, terrain [0, Extent]^2. Logical: campaign XY without strips, x in [0, Width) periodic, y in
    //     [0, Height] (0 = the north pole line, Height = the south pole line). physical = logical + (OriginX, OriginY), OriginX being the
    //     strip and OriginY the pole margin. Canonical physical x is [MinX, MaxX) = [OriginX, OriginX + Width).
    //   * Display copies. The strips (at least Width/2 on both sides) carry copies of the other side of the seam: a copy is the canonical
    //     point shifted by a whole number of widths (WrapIndex, DisplayOffset). Copies have no gameplay identity of their own: campaign state
    //     is canonical and a campaign object's identity is its id. A location's key (LocationKey) is built from canonical state; copies
    //     computed in double give the same key, while a copy rounded to single precision can fall in the neighbouring quantum.
    //   * Tie rule (one for every consumer). DeltaX(a, b), the signed shortest x difference b - a, lies in (-Width/2, Width/2]: an exact
    //     half-width difference resolves east (+). Placements orders the displacements to a target by |D| from DeltaX with the same rule.
    //   * Placements. A planar path query reaches the target through one of three displacements D (DeltaX, DeltaX +/- Width). Each is
    //     placed inside the terrain: the target's copy at start + D, or, when that copy lies outside, the start moved by whole widths with
    //     the target copy beside it. Every displacement shorter than a width is representable in a terrain of two widths with strips of
    //     half a width, so a route the long way around is never dropped; a route whose path leaves the terrain still cannot be represented.
    //   * Poles. Logical y <= 0 is the north pole line and y >= Height the south one (the padding beyond repeats them): every x on a pole
    //     line is one location, the source's single pole node replicated across the row ("pole aliases"), so its keys and source uid drop x.
    //   * Metric. Planar equirectangular in the logical frame, periodic in x. A geometric (straight-line) distance is only a lower bound of a
    //     travel cost: reachability and cost come from path queries over the placements (PeriodicDistanceModel), never from proximity.
    //     East-west map distance overstates sphere distance by 1/cos(latitude) (x2.0 at 60 degrees, x5.759 at 80, unbounded on the pole line),
    //     accepted because the pole bands are ocean or padding (worldgen/policies/periodic-coordinates.json).
    //   * Identity. FrameText names the coordinate frame and its rules; FrameIdentity (its SHA-256) is the save header's "coordinatePolicy",
    //     so a save is refused only when its coordinates would mean something else. PolicyText adds the metric and the distance version;
    //     Identity (its SHA-256) enters cache keys, the navigation report and the generation job record.
    //   * Native face pointers and face indices are session-local and never part of a key.
    public sealed class PeriodicWorldCoordinates
    {
        public const string PolicyVersion = "mmr64/1";
        public const string Metric = "planar-equirectangular";
        public const string TieRule = "east";
        public const double KeyQuantum = 1.0 / 1024;   // exact in binary; the width and both origins are whole numbers of quanta
        public enum Pole { None, North, South }
        public struct Image { public double X; public int Shift; }   // X = canonical x + Shift * Width
        // One displacement placed inside the terrain: the query runs from FromX (the given start + StartShift * Width) to
        // TargetX = FromX + Delta (the canonical target + TargetShift * Width).
        public struct Placement { public double FromX, TargetX, Delta; public int StartShift, TargetShift; }

        public readonly double Width, Height, OriginX, OriginY, Extent, CentralMeridianDeg;
        public readonly int SourceColumns;   // source grid columns per period (gridSide - 1); 0 = no source grid
        public string FrameText { get; private set; }
        public string FrameIdentity { get; private set; }
        public string PolicyText { get; private set; }
        public string Identity { get; private set; }
        public double MinX { get { return OriginX; } }
        public double MaxX { get { return OriginX + Width; } }
        private readonly long periodQuanta;

        public PeriodicWorldCoordinates(double width, double height, double originX, double originY, double extent, int sourceColumns = 0, double centralMeridianDeg = 0)
        {
            foreach (double v in new[] { width, height, originX, originY, extent, centralMeridianDeg })
                if (Double.IsNaN(v) || Double.IsInfinity(v)) throw new ArgumentException("A periodic layout needs finite values.");
            if (!(width > 0) || !(height > 0)) throw new ArgumentException("A periodic world needs a positive width and height.");
            if (!(originX >= width / 2 - 1e-3) || !(extent >= originX + width + width / 2 - 1e-3))
                throw new ArgumentException("Periodic layout needs strips at least width/2 on both sides of the canonical world.");
            if (!(originY >= 0) || !(originY + height <= extent + 1e-3)) throw new ArgumentException("The logical rows must lie inside the terrain.");
            if (sourceColumns < 0) throw new ArgumentException("The source column count cannot be negative.");
            foreach (double v in new[] { width, originX, originY })
                if (Math.Abs(v / KeyQuantum - Math.Round(v / KeyQuantum)) > 1e-6) throw new ArgumentException("The width and both origins must be whole numbers of key quanta (1/1024).");
            Width = width; Height = height; OriginX = originX; OriginY = originY; Extent = extent; SourceColumns = sourceColumns; CentralMeridianDeg = centralMeridianDeg;
            periodQuanta = (long)Math.Round(width / KeyQuantum);
            Func<double, string> r = v => v.ToString("R", CultureInfo.InvariantCulture);
            FrameText = "schema=mnbl.periodic-frame;version=" + PolicyVersion + ";axis=x;tie=" + TieRule + ";delta=(-W/2,W/2];canonical=[originX,originX+W)" +
                ";poles=one location per pole line;keyQuantum=1/1024;width=" + r(width) + ";height=" + r(height) + ";originX=" + r(originX) + ";originY=" + r(originY) +
                ";extent=" + r(extent) + ";sourceColumns=" + sourceColumns.ToString(CultureInfo.InvariantCulture) + ";centralMeridianDeg=" + r(centralMeridianDeg);
            PolicyText = FrameText + ";metric=" + Metric + ";distance=" + PeriodicDistanceModel.Version +
                ";placements=displacements DeltaX and DeltaX+/-W by |D| ties east, target copy inside the terrain else the start moved by whole widths";
            FrameIdentity = Sha(FrameText); Identity = Sha(PolicyText);
        }

        private static string Sha(string text)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false).GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        // A compiled bundle's layout (projection.json periodicLayout, target and source).
        internal static PeriodicWorldCoordinates FromSurface(WorldSurface surface)
        {
            if (surface == null) throw new ArgumentNullException("surface");
            return new PeriodicWorldCoordinates(surface.LogicalWidth, surface.LogicalHeight, surface.Strip, surface.PoleMargin, surface.Extent,
                surface.SourceGridSide - 1, surface.CentralMeridianDeg);
        }

        // A placement's "periodic" block (the MMR-63 fixtures): width, originX, the pole rows and source cells; null when the world is not periodic.
        public static PeriodicWorldCoordinates FromPlacement(IDictionary<string, object> placement)
        {
            object value;
            if (placement == null || !placement.TryGetValue("periodic", out value) || value == null) return null;
            var periodic = (IDictionary<string, object>)value;
            if (!String.Equals(Convert.ToString(periodic["axis"], CultureInfo.InvariantCulture), "x", StringComparison.Ordinal))
                throw new ArgumentException("Only an x-periodic world is supported.");
            double width = Number(periodic, "width"), originX = Number(periodic, "originX"), extent = Number(placement, "extent");
            double originY = 0, height = extent; int columns = 0;
            object rows;
            if (periodic.TryGetValue("poleRows", out rows) && rows != null)
            {
                var poles = (IDictionary<string, object>)rows; double cell = Number(periodic, "cell");
                originY = Number(poles, "north") * cell; height = (Number(poles, "south") - Number(poles, "north")) * cell;
            }
            if (periodic.ContainsKey("sourceCells")) columns = (int)Number(periodic, "sourceCells");
            // MMR-45: a compiled world's placement (ToPlacement) states its rows and central meridian exactly, so its frame is the surface's.
            if (periodic.ContainsKey("originY")) { originY = Number(periodic, "originY"); height = Number(periodic, "height"); }
            double meridian = periodic.ContainsKey("centralMeridianDeg") ? Number(periodic, "centralMeridianDeg") : 0;
            return new PeriodicWorldCoordinates(width, height, originX, originY, extent, columns, meridian);
        }

        // MMR-45: the "periodic" block (and extent) of a compiled world's placement, read back by FromPlacement as these same coordinates.
        public Dictionary<string, object> ToPlacement()
        {
            return new Dictionary<string, object> { ["axis"] = "x", ["width"] = Width, ["originX"] = OriginX, ["originY"] = OriginY, ["height"] = Height,
                ["sourceCells"] = SourceColumns, ["centralMeridianDeg"] = CentralMeridianDeg };
        }

        private static double Number(IDictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) throw new ArgumentException("The periodic layout lacks '" + key + "'.");
            return Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }

        // ---- canonical x ----
        public bool IsCanonicalX(double x) { return x >= MinX && x < MaxX; }

        // Into [MinX, MaxX). A value within rounding of the seam's upper side returns MinX, never MaxX.
        public double CanonicalX(double x)
        {
            if (Double.IsNaN(x) || Double.IsInfinity(x)) throw new ArgumentException("A coordinate must be finite.");
            if (x >= MinX && x < MaxX) return x;   // canonical values are returned exactly
            double t = (x - OriginX) % Width;
            if (t < 0) t += Width;
            if (t >= Width) t = 0;
            double c = OriginX + t;
            return c >= MaxX ? MinX : c;
        }

        // Single precision for campaign positions: computed in double, and a result that rounds to MaxX in single precision is MinX.
        public float CanonicalX(float x)
        {
            float c = (float)CanonicalX((double)x);
            return c >= (float)MaxX ? (float)MinX : c;
        }

        // The display copy a physical x lies in: 0 canonical, +1 the east strip, -1 the west strip.
        public int WrapIndex(double x) { return (int)Math.Round((x - CanonicalX(x)) / Width); }
        public double DisplayOffset(double x) { return WrapIndex(x) * Width; }

        // Signed shortest difference b - a on the circle, in (-Width/2, Width/2]; the exact half width resolves east (+).
        public double DeltaX(double a, double b)
        {
            double d = (b - a) % Width;
            if (d < 0) d += Width;
            if (d >= Width) d = 0;
            return d > Width / 2 ? d - Width : d;
        }

        public double GeometricDistanceSquared(double ax, double ay, double bx, double by) { double dx = DeltaX(ax, bx), dy = by - ay; return dx * dx + dy * dy; }
        // A lower bound of any travel cost between the two locations; never a cost or a reachability answer.
        public double GeometricDistance(double ax, double ay, double bx, double by) { return Math.Sqrt(GeometricDistanceSquared(ax, ay, bx, by)); }

        // ---- images and placements ----
        // The physical x of every copy of x's location inside the terrain [0, Extent]: the canonical one, then east (+Width), then west (-Width).
        public List<Image> Images(double x)
        {
            double c = CanonicalX(x);
            var images = new List<Image>(3);
            foreach (int k in new[] { 0, 1, -1 })
            {
                double ix = c + k * Width;
                if (ix >= 0 && ix <= Extent) images.Add(new Image { X = ix, Shift = k });
            }
            return images;
        }

        // The three displacements from a physical start to a target location (DeltaX, then the other two by |D|, ties east), each placed
        // inside the terrain with the start moved by the fewest widths (0, then -1, +1, -2, +2). The first is the short way and always needs
        // no move from a canonical start; the other two are the long ways around. A displacement no move can place is omitted.
        public List<Placement> Placements(double fromX, double targetX)
        {
            double d0 = DeltaX(fromX, targetX), c = CanonicalX(targetX);
            var deltas = new List<double> { d0, d0 + Width, d0 - Width };
            deltas.Sort((a, b) => { int s = Math.Abs(a).CompareTo(Math.Abs(b)); return s != 0 ? s : b.CompareTo(a); });
            var placements = new List<Placement>(3);
            foreach (double d in deltas)
                foreach (int move in new[] { 0, -1, 1, -2, 2 })
                {
                    double from = fromX + move * Width, to = from + d;
                    if (from < 0 || from > Extent || to < 0 || to > Extent) continue;
                    placements.Add(new Placement { FromX = from, TargetX = to, Delta = d, StartShift = move, TargetShift = (int)Math.Round((to - c) / Width) });
                    break;
                }
            return placements;
        }

        // ---- logical <-> physical ----
        public double LogicalX(double physicalX) { return CanonicalX(physicalX) - OriginX; }
        public double[] ToLogical(double physicalX, double physicalY) { return new[] { LogicalX(physicalX), physicalY - OriginY }; }
        // Logical x wraps; the result is canonical.
        public double[] ToPhysical(double logicalX, double logicalY) { return new[] { CanonicalX(OriginX + logicalX), OriginY + logicalY }; }

        // ---- poles and identity ----
        public Pole PoleOf(double physicalY)
        {
            double y = physicalY - OriginY;
            if (y <= KeyQuantum / 2) return Pole.North;
            if (y >= Height - KeyQuantum / 2) return Pole.South;
            return Pole.None;
        }

        // The canonical logical location of a physical point, quantized at 1/1024; a pole line is one location. Build keys from canonical
        // campaign state (an object's own position), never from a display point: copies computed in double give the same key, but a copy
        // rounded to single precision can land in the neighbouring quantum (measured: evidence/runtime/periodic-coordinates-tests.json).
        public string LocationKey(double physicalX, double physicalY)
        {
            var pole = PoleOf(physicalY);
            if (pole != Pole.None) return pole == Pole.North ? "N" : "S";
            long qx = (long)Math.Round(LogicalX(physicalX) / KeyQuantum) % periodQuanta;
            if (qx < 0) qx += periodQuanta;
            long qy = (long)Math.Round((physicalY - OriginY) / KeyQuantum);
            return qx.ToString(CultureInfo.InvariantCulture) + ":" + qy.ToString(CultureInfo.InvariantCulture);
        }

        // The nearest source grid node of a physical point (projection.json inverse.sourceGrid): "r<row>c<column>", the column modulo the
        // period, and "N" / "S" for the single pole nodes. The canonical source uid of every copy of the location.
        public string SourceUid(double physicalX, double physicalY)
        {
            if (SourceColumns <= 0) throw new InvalidOperationException("This layout has no source grid.");
            double y = Math.Max(0, Math.Min(Height, physicalY - OriginY));
            long row = (long)Math.Round(SourceColumns * y / Height);   // rows per pole-to-pole span: gridSide - 1, as columns per period
            if (row <= 0) return "N";
            if (row >= SourceColumns) return "S";
            long column = (long)Math.Round(SourceColumns * (LogicalX(physicalX) / Width + CentralMeridianDeg / 360.0)) % SourceColumns;
            if (column < 0) column += SourceColumns;
            return "r" + row.ToString(CultureInfo.InvariantCulture) + "c" + column.ToString(CultureInfo.InvariantCulture);
        }

        // A directed cache key: policy version and identity, the navigation capability, and both canonical locations with their region.
        // From and to are never reordered: transition costs differ by direction (land to sea is not sea to land).
        public string CacheKey(string capability, double fromX, double fromY, bool fromLand, double toX, double toY, bool toLand)
        {
            if (String.IsNullOrEmpty(capability) || capability.IndexOf('|') >= 0) throw new ArgumentException("A capability name is required and cannot contain '|'.");
            return PolicyVersion + "|" + Identity.Substring(0, 16) + "|" + capability + "|" + LocationKey(fromX, fromY) + (fromLand ? "L" : "S") + ">" + LocationKey(toX, toY) + (toLand ? "L" : "S");
        }

        // ---- seam splitting for data (logical frame) ----
        // A logical x interval [x0, x1] (x0 <= x1, at most Width long) as canonical pieces of [0, Width]: one, or two when it crosses the seam.
        public List<double[]> SplitInterval(double x0, double x1)
        {
            if (!(x1 >= x0) || x1 - x0 > Width + 1e-9) throw new ArgumentException("An interval runs east and is at most one width long.");
            if (x1 - x0 >= Width) return new List<double[]> { new[] { 0.0, Width } };
            double a = CanonicalX(OriginX + x0) - OriginX, b = a + (x1 - x0);
            if (b <= Width) return new List<double[]> { new[] { a, b } };
            return new List<double[]> { new[] { a, Width }, new[] { 0.0, b - Width } };
        }

        // A straight logical step from p to q the short way (DeltaX, ties east), as canonical pieces: one, or two split on the seam (x = Width
        // then 0 going east, x = 0 then Width going west) with y interpolated. A step ending exactly on the seam is one piece ending at Width
        // or 0; a step starting on the seam and going west starts at Width.
        public List<double[][]> SplitSegment(double[] p, double[] q)
        {
            double delta = DeltaX(p[0], q[0]), sx = CanonicalX(OriginX + p[0]) - OriginX;
            if (sx == 0 && delta < 0) sx = Width;
            double ex = sx + delta;
            if (ex >= 0 && ex <= Width) return new List<double[][]> { new[] { new[] { sx, p[1] }, new[] { ex, q[1] } } };
            double boundary = ex > Width ? Width : 0, t = (boundary - sx) / (ex - sx), y = p[1] + t * (q[1] - p[1]);
            double other = boundary == Width ? 0 : Width, shifted = ex > Width ? ex - Width : ex + Width;
            return new List<double[][]> { new[] { new[] { sx, p[1] }, new[] { boundary, y } }, new[] { new[] { other, y }, new[] { shifted, q[1] } } };
        }

        // Save compatibility (WorldIdentity.RequireMatch): a header's "periodic" flag must match whether the world is periodic, and a periodic
        // header must carry exactly this build's frame identity for the world's layout ("coordinatePolicy"); a save made under another frame
        // or none is refused, never reinterpreted. A distance-model change alone does not invalidate saves.
        public static void RequireSavedPolicy(IDictionary<string, string> header, PeriodicWorldCoordinates coordinates)
        {
            if (header == null) throw new ArgumentNullException("header");
            string periodic, policy;
            header.TryGetValue("periodic", out periodic); header.TryGetValue("coordinatePolicy", out policy);
            if ((periodic == "true") != (coordinates != null))
                throw new System.IO.InvalidDataException("The save header (periodic " + (periodic ?? "missing") + ") and its world disagree on whether the world is periodic.");
            if (coordinates != null && policy != coordinates.FrameIdentity)
                throw new System.IO.InvalidDataException("The save was made under periodic coordinate frame " + (policy ?? "(none)") + "; this build reads the world under " +
                    coordinates.FrameIdentity + " (" + PolicyVersion + ").");
        }

        public SortedDictionary<string, object> Record()
        {
            return new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["version"] = PolicyVersion, ["distanceVersion"] = PeriodicDistanceModel.Version, ["identity"] = Identity, ["frameIdentity"] = FrameIdentity,
                ["metric"] = Metric, ["tie"] = TieRule, ["width"] = Width, ["height"] = Height, ["originX"] = OriginX, ["originY"] = OriginY, ["extent"] = Extent,
                ["sourceColumns"] = SourceColumns, ["centralMeridianDeg"] = CentralMeridianDeg };
        }
    }
}
