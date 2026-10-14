using System;
using System.IO;
using System.Text;

namespace MNBL.ProceduralWorld.Diagnostics
{
    // Source-grid coordinates only: no accepted engine projection or campaign binding.
    // Surface triangles and queries share the same diagonal (x+1,y)-(x,y+1).
    public sealed class DiagnosticHeightfield
    {
        public int Side { get; private set; }
        private readonly double[] heights;

        private DiagnosticHeightfield(int side, double[] heights)
        {
            Side = side;
            this.heights = heights;
        }

        public static DiagnosticHeightfield Calibration()
        {
            const int side = 17;
            var values = new double[side * side];
            for (int y = 0; y < side; y++) for (int x = 0; x < side; x++)
                values[y * side + x] = 8 + x * .25 + y * .5;
            // Four distinct corners, an off-center peak, a channel and a ridge across it.
            values[11 * side + 4] = 40;
            for (int x = 1; x < 16; x++) values[6 * side + x] = 3 + x * .05;
            for (int y = 1; y < 16; y++) values[y * side + 10] = 30 + y * .1;
            return new DiagnosticHeightfield(side, values);
        }

        public static DiagnosticHeightfield Read(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "MNBLHF01")
                    throw new InvalidDataException("Unsupported diagnostic heightfield header.");
                int side = reader.ReadInt32();
                if (side < 3 || side > 1025 || side % 2 == 0)
                    throw new InvalidDataException("Diagnostic heightfield side must be odd and between 3 and 1025.");
                if (stream.Length != 12L + 8L * side * side)
                    throw new InvalidDataException("Truncated or trailing diagnostic heightfield data.");
                var heights = new double[side * side];
                for (int i = 0; i < heights.Length; i++)
                {
                    heights[i] = reader.ReadDouble();
                    if (!Finite(heights[i])) throw new InvalidDataException("Nonfinite height at cell " + i);
                }
                return new DiagnosticHeightfield(side, heights);
            }
        }

        public double At(int x, int y)
        {
            if (x < 0 || x >= Side || y < 0 || y >= Side) throw new ArgumentOutOfRangeException();
            return heights[y * Side + x];
        }

        public double Sample(double x, double y)
        {
            if (!Finite(x) || !Finite(y) || x < 0 || y < 0 || x > Side - 1 || y > Side - 1)
                throw new ArgumentOutOfRangeException("Point is outside the diagnostic grid.");
            int ix = Math.Min((int)x, Side - 2), iy = Math.Min((int)y, Side - 2);
            double u = x - ix, v = y - iy;
            if (u + v <= 1)
                return At(ix, iy) * (1 - u - v) + At(ix + 1, iy) * u + At(ix, iy + 1) * v;
            return At(ix + 1, iy + 1) * (u + v - 1) + At(ix + 1, iy) * (1 - v) + At(ix, iy + 1) * (1 - u);
        }

        // Segment intersection, including both sides of the surface; fraction is in [0,1].
        // This bounded brute-force diagnostic is deliberately not a production spatial index.
        public bool Intersect(double ax, double ay, double az, double bx, double by, double bz, out double fraction)
        {
            foreach (double coordinate in new[] { ax, ay, az, bx, by, bz })
                if (!Finite(coordinate)) throw new ArgumentException("Nonfinite segment coordinate.");
            var origin = new Point(ax, ay, az);
            var delta = new Point(bx - ax, by - ay, bz - az);
            fraction = double.PositiveInfinity;
            for (int y = 0; y < Side - 1; y++) for (int x = 0; x < Side - 1; x++)
            {
                var a = new Point(x, y, At(x, y));
                var b = new Point(x + 1, y, At(x + 1, y));
                var c = new Point(x, y + 1, At(x, y + 1));
                var d = new Point(x + 1, y + 1, At(x + 1, y + 1));
                TestTriangle(origin, delta, a, b, c, ref fraction);
                TestTriangle(origin, delta, d, c, b, ref fraction);
            }
            return !double.IsPositiveInfinity(fraction);
        }

        private static void TestTriangle(Point origin, Point delta, Point a, Point b, Point c, ref double nearest)
        {
            Point e1 = b - a, e2 = c - a, p = Point.Cross(delta, e2);
            double determinant = Point.Dot(e1, p);
            if (Math.Abs(determinant) < 1e-12) return;
            Point s = origin - a;
            double u = Point.Dot(s, p) / determinant;
            if (u < -1e-10 || u > 1 + 1e-10) return;
            Point q = Point.Cross(s, e1);
            double v = Point.Dot(delta, q) / determinant;
            if (v < -1e-10 || u + v > 1 + 1e-10) return;
            double t = Point.Dot(e2, q) / determinant;
            if (t >= 0 && t <= 1 && t < nearest) nearest = t;
        }

        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private struct Point
        {
            private readonly double x, y, z;
            internal Point(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
            public static Point operator -(Point a, Point b) { return new Point(a.x-b.x, a.y-b.y, a.z-b.z); }
            internal static Point Cross(Point a, Point b) { return new Point(a.y*b.z-a.z*b.y, a.z*b.x-a.x*b.z, a.x*b.y-a.y*b.x); }
            internal static double Dot(Point a, Point b) { return a.x*b.x + a.y*b.y + a.z*b.z; }
        }
    }
}
