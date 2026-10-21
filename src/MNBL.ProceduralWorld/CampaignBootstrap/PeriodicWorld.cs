using System;
using System.Collections.Generic;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // MMR-63: one logical world periodic in x. Logical x in [0, Width) lives at physical
    // [OriginX, OriginX + Width); the physical terrain carries copies of the world on both sides
    // (strips Width/2 wide), so every shortest cross-seam route has its target image inside the terrain.
    // Campaign state is canonical (physical x in the canonical range); images exist only transiently,
    // in path queries and display placement.
    // MMR-64: the campaign's single-precision view of PeriodicWorldCoordinates, which owns every rule (canonical range, tie, images,
    // identity); nothing here redefines them.
    public sealed class PeriodicWorld
    {
        public readonly float Width, OriginX, Extent;
        public readonly PeriodicWorldCoordinates Coordinates;
        // The active periodic campaign, set by the owned map scene's Load and cleared when it is destroyed.
        public static PeriodicWorld Current { get; internal set; }

        public PeriodicWorld(PeriodicWorldCoordinates coordinates)
        {
            if (coordinates == null) throw new ArgumentNullException("coordinates");
            Coordinates = coordinates;
            Width = (float)coordinates.Width; OriginX = (float)coordinates.OriginX; Extent = (float)coordinates.Extent;
        }

        public static PeriodicWorld FromPlacement(Dictionary<string, object> placement)
        {
            var coordinates = PeriodicWorldCoordinates.FromPlacement(placement);
            return coordinates == null ? null : new PeriodicWorld(coordinates);
        }

        public float MaxX { get { return OriginX + Width; } }
        public bool IsCanonical(float x) { return x >= OriginX && x < MaxX; }
        public float Canonical(float x) { return Coordinates.CanonicalX(x); }
        public float LogicalX(float x) { return (float)Coordinates.LogicalX(x); }
        // MMR-278 (RT-19): y held between the pole lines, in single precision.
        public float ClampY(float y) { return (float)Coordinates.ClampY(y); }

        // Signed shortest x difference b - a on the circle, in (-Width/2, Width/2] (the half width resolves east, +).
        public float DeltaX(float a, float b) { return (float)Coordinates.DeltaX(a, b); }

        public float DistanceSquared(Vec2 a, Vec2 b) { return (float)Coordinates.GeometricDistanceSquared(a.X, a.Y, b.X, b.Y); }
        // The wrapped geometric distance: a lower bound of any travel cost (PeriodicDistanceModel.LowerBound).
        public float Distance(Vec2 a, Vec2 b) { return (float)Coordinates.GeometricDistance(a.X, a.Y, b.X, b.Y); }
    }
}
