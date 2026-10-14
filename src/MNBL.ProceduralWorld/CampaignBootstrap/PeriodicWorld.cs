using System;
using System.Collections.Generic;
using System.Globalization;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // MMR-63: one logical world periodic in x. Logical x in [0, Width) lives at physical
    // [OriginX, OriginX + Width); the physical terrain carries copies of the world on both sides
    // (strips Width/2 wide), so every shortest cross-seam route has its target image inside the terrain.
    // Campaign state is canonical (physical x in the canonical range); images exist only transiently,
    // in path queries and display placement.
    public sealed class PeriodicWorld
    {
        public readonly float Width, OriginX, Extent;
        // The active periodic campaign, set by the owned map scene's Load and cleared when it is destroyed.
        public static PeriodicWorld Current { get; internal set; }

        public PeriodicWorld(float width, float originX, float extent)
        {
            if (!(width > 0) || !(originX >= width / 2 - 1e-3f) || !(extent >= originX + width + width / 2 - 1e-3f))
                throw new ArgumentException("Periodic layout needs strips at least width/2 on both sides of the canonical world.");
            Width = width; OriginX = originX; Extent = extent;
        }

        public static PeriodicWorld FromPlacement(Dictionary<string, object> placement)
        {
            object value;
            if (!placement.TryGetValue("periodic", out value) || value == null) return null;
            var periodic = (Dictionary<string, object>)value;
            if (!String.Equals(Convert.ToString(periodic["axis"], CultureInfo.InvariantCulture), "x", StringComparison.Ordinal))
                throw new ArgumentException("Only an x-periodic world is supported.");
            return new PeriodicWorld(Convert.ToSingle(periodic["width"], CultureInfo.InvariantCulture),
                                     Convert.ToSingle(periodic["originX"], CultureInfo.InvariantCulture),
                                     Convert.ToSingle(placement["extent"], CultureInfo.InvariantCulture));
        }

        public float MaxX { get { return OriginX + Width; } }
        public bool IsCanonical(float x) { return x >= OriginX && x < MaxX; }

        public float Canonical(float x)
        {
            float t = (x - OriginX) % Width;
            if (t < 0) t += Width;
            return OriginX + t;
        }

        public float LogicalX(float x) { return Canonical(x) - OriginX; }

        // Signed shortest x difference b - a on the circle (ties at exactly Width/2 resolve east, +).
        public float DeltaX(float a, float b)
        {
            float d = (b - a) % Width;
            if (d < 0) d += Width;
            return d > Width / 2 ? d - Width : d;
        }

        public float DistanceSquared(Vec2 a, Vec2 b) { float dx = DeltaX(a.X, b.X), dy = b.Y - a.Y; return dx * dx + dy * dy; }
        public float Distance(Vec2 a, Vec2 b) { return (float)Math.Sqrt(DistanceSquared(a, b)); }

        // Images of p inside the physical terrain, in deterministic preference order: as given, east (+Width), west (-Width).
        public IEnumerable<Vec2> Images(Vec2 p)
        {
            yield return p;
            foreach (float shift in new[] { Width, -Width })
            {
                float x = p.X + shift;
                if (x > 0 && x < Extent) yield return new Vec2(x, p.Y);
            }
        }

        // The image of target nearest to from (planar), ties to the earlier image in Images order.
        public Vec2 NearestImage(Vec2 from, Vec2 target)
        {
            Vec2 best = target; float bestD = float.MaxValue;
            foreach (var image in Images(target))
            {
                float d = from.DistanceSquared(image);
                if (d < bestD - 1e-4f) { best = image; bestD = d; }
            }
            return best;
        }
    }
}
