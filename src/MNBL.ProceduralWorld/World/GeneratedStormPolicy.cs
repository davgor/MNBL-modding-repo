using System;
using System.Collections.Generic;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-39: the storm rules of a generated world (docs/water-climate.md). Pure managed; GeneratedStormModel (the MapStormModel chained
    // over War Sails' own) and PeriodicSeam apply them in game.
    //   * Spawn frequency: War Sails tries a spawn at each open-sea weather node every hour with a fixed chance (0.0025) tuned for
    //     Calradia's node spacing (32.5). A generated world's nodes are extent / dimension apart, so the chance is scaled by the area a
    //     node stands for, (spacing / 32.5)^2, and the storm frequency per unit of sea matches Calradia's.
    //   * Where: on a periodic world only canonical nodes between the pole lines can spawn a storm, so a storm is never born in a strip
    //     copy or in the padding beyond a pole (the strip copies' nodes would otherwise double the seam's storms).
    //   * Seam: a storm that drifts out of the canonical range is moved by a whole number of widths (with its trail), so it neither leaves
    //     the map borders (War Sails ends such a storm) nor exists twice; its effects reach parties on either side of the seam through the
    //     party position's copy nearest the storm.
    public static class GeneratedStormPolicy
    {
        public const string Version = "mmr39/1";
        public const double CalradiaNodeSpacing = 32.5;

        public static double SpawnChanceScale(double extent, int nodeDimension)
        {
            if (!(extent > 0) || nodeDimension <= 0) throw new ArgumentException("A positive extent and node dimension are required.");
            double spacing = extent / nodeDimension;
            return spacing * spacing / (CalradiaNodeSpacing * CalradiaNodeSpacing);
        }

        // Whether a storm may be born at a physical point (always on a world that is not periodic).
        public static bool CanSpawnAt(PeriodicWorldCoordinates c, double x, double y)
        {
            return c == null || (c.IsCanonicalX(x) && c.PoleOf(y) == PeriodicWorldCoordinates.Pole.None);
        }

        // The copy of point x nearest a storm at stormX: the position War Sails' planar distance should see.
        public static double NearestCopyX(PeriodicWorldCoordinates c, double stormX, double x)
        {
            return c == null ? x : stormX + c.DeltaX(stormX, x);
        }

        // A storm past a pole line is reflected back across it (the padding beyond the line is the pole itself, one location; its wind is calm,
        // which War Sails turns into a southward drift): the y it is moved to, the same y when it lies between the lines.
        public static double PoleReflectY(PeriodicWorldCoordinates c, double y)
        {
            if (c == null) return y;
            double north = c.OriginY, south = c.OriginY + c.Height;
            return y < north ? 2 * north - y : y > south ? 2 * south - y : y;
        }

        // The shift that brings a storm back into the canonical range (0 when it is canonical).
        public static double RebaseShift(PeriodicWorldCoordinates c, double x)
        {
            return c == null || c.IsCanonicalX(x) ? 0 : c.CanonicalX(x) - x;
        }

        public static SortedDictionary<string, object> Record(PeriodicWorldCoordinates c, double extent, int nodeDimension)
        {
            return new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["version"] = Version, ["nodeSpacing"] = extent / nodeDimension, ["calradiaNodeSpacing"] = CalradiaNodeSpacing,
                ["spawnChanceScale"] = SpawnChanceScale(extent, nodeDimension), ["periodic"] = c != null,
                ["spawnRule"] = c == null ? "every open-sea weather node (War Sails)" : "open-sea weather nodes in the canonical range between the pole lines",
                ["seamRule"] = "a storm leaving the canonical range is moved by whole widths with its trail; effects use the party's copy nearest the storm",
                ["poleRule"] = c == null ? "none" : "a storm past a pole line is reflected back across it, its trail too, and its move direction turned towards the equator" };
        }
    }
}
