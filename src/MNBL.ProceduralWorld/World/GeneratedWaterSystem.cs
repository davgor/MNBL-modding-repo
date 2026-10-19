using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MNBL.ProceduralWorld.CampaignBootstrap;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-39: the water of a generated world (docs/water-climate.md).
    //   * Water body: one plane on the projection's datum (z = 0; projection.json vertical.datumM, the effective sea level), exactly over
    //     the terrain [0, extent]^2 (a production scene has no border entities, so the campaign map's borders are the scene's box: a
    //     larger plane would widen them), with the installed War Sails water material (naval_worldmap_water on editor_plane_high), the
    //     plane physics and the water-body tag, as the MMR-59/63 fixtures' plane (which is twice the terrain, inside border entities). The
    //     native encoder keeps template entities empty, so RuntimeWorldBackend inserts it into the encoded scene, and its version is
    //     part of the content-addressed scene name.
    //   * Water classes: the bundle's intent mask (0 land, 1 ocean, 2 lake, 3 river) with the encoded heights. Navigation keeps the
    //     classes apart (MMR-38: sea, coastal and open sea are naval; rivers are land-passable only at bridges; lakes are typed Lake),
    //     storms spawn only on open sea (War Sails' rule) and only in the canonical range (PeriodicStorms), and the plane at the datum
    //     shows water wherever the terrain lies below it.
    //   * Lake bodies: a lake keeps its own level, apart from the sea. Each 4-connected group of lake samples (intent 2) on the physical
    //     sample grid gets one plane (the same material, physics and tag) at its lake's level: the largest source water_surface (metres)
    //     over the source lake cells its samples map to (the nearest source cell of each sample's canonical location, else a lake cell
    //     among that cell's 8 neighbours), projected as the terrain is (z = (level - datumM) x worldUnitsPerMetre, the vertical scale
    //     that already carries the 2.5x exaggeration, docs/world-projection.md), over
    //     the group's bounding box grown by half a sample, inside the terrain. The plane never rises above a sample that is not the lake's: its height is at
    //     most the lowest other sample within one sample of the box, less 0.001 (the 257 sample grid resamples the 129 source grid, so a
    //     shore sample can lie below the source level); a lake whose plane would then not clear its own bed is left out (counted). A
    //     strip copy of a lake is its own group with the same level. A copy cut by the terrain's east or west edge holds only part of its
    //     lake, so it shows, or is left out, at its whole twin's level one width away (the group holding the copy of its first sample).
    //     A river follows its slope, so a flat plane cannot carry it: rivers have no water surface of their
    //     own (recorded in docs/water-climate.md).
    public static class GeneratedWaterSystem
    {
        public const string Version = "mmr39/2";
        public const double DatumZ = 0;
        public const string Material = "naval_worldmap_water", Mesh = "editor_plane_high", Physics = "bo_editor_plane", Tag = "snapped_water_body";

        public sealed class WaterClasses
        {
            public int Samples, Land, Ocean, Lake, River, LakeAboveDatum, RiverAboveDatum, OceanAboveDatum, LandBelowDatum;
            public SortedDictionary<string, object> Record()
            {
                return new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["samples"] = Samples, ["land"] = Land, ["ocean"] = Ocean, ["lake"] = Lake, ["river"] = River, ["lakeAboveDatum"] = LakeAboveDatum,
                    ["riverAboveDatum"] = RiverAboveDatum, ["oceanAboveDatum"] = OceanAboveDatum, ["landBelowDatum"] = LandBelowDatum };
            }
        }

        public sealed class Lake
        {
            public int Index, Samples, Matched;
            public double MinX, MinY, MaxX, MaxY, LevelM, LevelZ, Z, BedMaxZ, ShoreMinZ;   // ShoreMinZ: the lowest other sample within one sample of the box
            public bool Clamped;
            public double CenterX { get { return (MinX + MaxX) / 2; } }
            public double CenterY { get { return (MinY + MaxY) / 2; } }
            public double FirstSampleX, FirstSampleY;   // a lake sample: a ray there meets the plane
            public SortedDictionary<string, object> Record()
            {
                return new SortedDictionary<string, object>(StringComparer.Ordinal) { ["index"] = Index, ["samples"] = Samples, ["matched"] = Matched, ["levelM"] = LevelM, ["levelZ"] = LevelZ,
                    ["z"] = Z, ["clamped"] = Clamped, ["bedMaxZ"] = BedMaxZ, ["shoreMinZ"] = ShoreMinZ, ["box"] = new[] { MinX, MinY, MaxX, MaxY }, ["sample"] = new[] { FirstSampleX, FirstSampleY } };
            }
        }

        // The lake bodies of a world (see the class comment); lakes whose samples map to no source lake cell are left out and counted.
        internal static List<Lake> Lakes(WorldSurface surface, GeneratedClimateSystem.Source source, PeriodicWorldCoordinates c, out int unmatched)
        {
            int hidden; return Lakes(surface, source, c, out unmatched, out hidden);
        }

        internal static List<Lake> Lakes(WorldSurface surface, GeneratedClimateSystem.Source source, PeriodicWorldCoordinates c, out int unmatched, out int hidden)
        {
            if (surface == null || source == null || c == null) throw new ArgumentNullException();
            if (source.WaterType == null || source.WaterSurface == null) throw new InvalidDataException("The source world has no water_type or water_surface layer.");
            double datum = Convert.ToDouble(surface.Scale["seaDatum"], CultureInfo.InvariantCulture);
            double perMetre = Convert.ToDouble(surface.Scale["verticalWorldUnitsPerMetre"], CultureInfo.InvariantCulture);
            int side = surface.Side, n = source.Side - 1; double spacing = surface.Spacing;
            var heights = surface.Quantized;
            var seen = new bool[side * side];
            var lakes = new List<Lake>(); unmatched = 0; hidden = 0;
            // Every group in scan order (null: no source lake cell), its samples, and each lake sample's group.
            var candidates = new List<Lake>(); var groups = new List<List<int>>(); var owner = new int[side * side];
            for (int i = 0; i < owner.Length; i++) owner[i] = -1;
            for (int start = 0; start < side * side; start++)
            {
                if (seen[start] || surface.Intent(start % side, start / side) != 2) continue;
                var group = new List<int>(); var queue = new Queue<int>(); queue.Enqueue(start); seen[start] = true;
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue(); group.Add(i);
                    int col = i % side, row = i / side;
                    foreach (var d in new[] { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 } })
                    {
                        int cc = col + d[0], rr = row + d[1];
                        if (cc < 0 || rr < 0 || cc >= side || rr >= side) continue;
                        int j = rr * side + cc;
                        if (!seen[j] && surface.Intent(cc, rr) == 2) { seen[j] = true; queue.Enqueue(j); }
                    }
                }
                var lake = new Lake { Samples = group.Count, LevelM = double.NegativeInfinity, MinX = double.MaxValue, MinY = double.MaxValue, MaxX = double.MinValue, MaxY = double.MinValue,
                    BedMaxZ = double.MinValue, ShoreMinZ = double.MaxValue, FirstSampleX = (group[0] % side) * spacing, FirstSampleY = (group[0] / side) * spacing };
                var members = new HashSet<int>(group);
                foreach (int i in group)
                {
                    int col = i % side, row = i / side; double x = col * spacing, y = row * spacing;
                    lake.MinX = Math.Min(lake.MinX, x); lake.MaxX = Math.Max(lake.MaxX, x); lake.MinY = Math.Min(lake.MinY, y); lake.MaxY = Math.Max(lake.MaxY, y);
                    lake.BedMaxZ = Math.Max(lake.BedMaxZ, heights[i]);
                    double gc, gr; GeneratedClimateSystem.SourceGrid(c, x, y, out gc, out gr);
                    int cell = GeneratedClimateSystem.Nearest(source.Side, gc, gr);
                    double level = double.NegativeInfinity;
                    if (source.WaterType[cell] == 2) level = source.WaterSurface[cell];
                    else
                    {
                        int sc = cell % source.Side, sr = cell / source.Side;
                        for (int dr = -1; dr <= 1; dr++)
                            for (int dc = -1; dc <= 1; dc++)
                            {
                                int r2 = sr + dr, c2 = ((sc + dc) % n + n) % n;
                                if (r2 < 0 || r2 > n) continue;
                                int k = r2 * source.Side + c2;
                                if (source.WaterType[k] == 2) level = Math.Max(level, source.WaterSurface[k]);
                            }
                    }
                    if (!double.IsNegativeInfinity(level)) { lake.Matched++; lake.LevelM = Math.Max(lake.LevelM, level); }
                }
                groups.Add(group); foreach (int i in group) owner[i] = groups.Count - 1;
                if (lake.Matched == 0) { unmatched++; candidates.Add(null); continue; }
                // The other samples within one sample of the box (the plane's half-sample margin interpolates towards them).
                // Past the terrain's east or west edge the rim continues in the copy one width away (strip samples copy canonical ones), so a
                // lake cut by the edge has its canonical twin's rim and level.
                int c0 = (int)Math.Round(lake.MinX / spacing) - 1, c1 = (int)Math.Round(lake.MaxX / spacing) + 1, r0 = (int)Math.Round(lake.MinY / spacing) - 1, r1 = (int)Math.Round(lake.MaxY / spacing) + 1;
                int period = (int)Math.Round(c.Width / spacing);
                for (int rr = Math.Max(0, r0); rr <= Math.Min(side - 1, r1); rr++)
                    for (int cc = c0; cc <= c1; cc++)
                    {
                        int wrapped = cc < 0 ? cc + period : cc >= side ? cc - period : cc;
                        if (wrapped < 0 || wrapped >= side) continue;
                        // Inside the terrain the lake's own samples are skipped; across the edge, the copy's lake samples (its continuation).
                        if (cc == wrapped ? members.Contains(rr * side + cc) : surface.Intent(wrapped, rr) == 2) continue;
                        lake.ShoreMinZ = Math.Min(lake.ShoreMinZ, heights[rr * side + wrapped]);
                    }
                // Grown by half a sample, inside the terrain (the scene's box is the campaign map's borders).
                lake.MinX = Math.Max(0, lake.MinX - spacing / 2); lake.MinY = Math.Max(0, lake.MinY - spacing / 2);
                lake.MaxX = Math.Min(surface.Extent, lake.MaxX + spacing / 2); lake.MaxY = Math.Min(surface.Extent, lake.MaxY + spacing / 2);
                lake.LevelZ = (lake.LevelM - datum) * perMetre;
                lake.Z = Math.Min(lake.LevelZ, lake.ShoreMinZ - .001);
                lake.Clamped = lake.Z < lake.LevelZ;
                candidates.Add(lake);
            }
            // A copy cut by the terrain's east or west edge takes its whole twin's verdict and level: otherwise the far edge could show a
            // lake the canonical map leaves out, or at another height (world B and seven more of the 2026-10-19 seed sweep had one).
            int samplePeriod = (int)Math.Round(c.Width / spacing);
            var shown = candidates.Select(l => l != null && l.Z > l.BedMaxZ).ToArray();
            var verdict = (bool[])shown.Clone();
            for (int k = 0; k < candidates.Count; k++)
            {
                var lake = candidates[k];
                if (lake == null || !groups[k].Any(i => i % side == 0 || i % side == side - 1)) continue;
                int first = groups[k][0], col = first % side, row = first / side;
                int copy = col + samplePeriod < side ? col + samplePeriod : col - samplePeriod;
                int twin = copy >= 0 && copy < side ? owner[row * side + copy] : -1;
                if (twin < 0 || twin == k) continue;
                verdict[k] = shown[twin];
                if (!shown[twin]) continue;
                var t = candidates[twin];
                lake.LevelM = t.LevelM; lake.LevelZ = t.LevelZ; lake.Z = t.Z; lake.Clamped = t.Clamped;
            }
            for (int k = 0; k < candidates.Count; k++)
            {
                if (candidates[k] == null) continue;
                if (!verdict[k]) { hidden++; continue; }
                candidates[k].Index = lakes.Count;
                lakes.Add(candidates[k]);
            }
            return lakes;
        }

        internal static WaterClasses Classes(WorldDataBundle bundle) { return Classes(WorldSurface.FromBundle(bundle)); }

        internal static WaterClasses Classes(WorldSurface surface)
        {
            var heights = surface.Quantized;
            var classes = new WaterClasses();
            for (int row = 0; row < surface.Side; row++)
                for (int col = 0; col < surface.Side; col++)
                {
                    byte intent = surface.Intent(col, row);
                    bool above = heights[row * surface.Side + col] > DatumZ;
                    classes.Samples++;
                    switch (intent)
                    {
                        case 0: classes.Land++; if (!above) classes.LandBelowDatum++; break;
                        case 1: classes.Ocean++; if (above) classes.OceanAboveDatum++; break;
                        case 2: classes.Lake++; if (above) classes.LakeAboveDatum++; break;
                        case 3: classes.River++; if (above) classes.RiverAboveDatum++; break;
                        default: throw new InvalidDataException("Unknown water intent " + intent + " at sample " + col + "," + row + ".");
                    }
                }
            return classes;
        }

        // The water body of a generated scene, as a scene.xscene game_entity.
        public static string WaterEntityXml(string name, double extent)
        {
            if (!(extent > 0) || Double.IsInfinity(extent)) throw new ArgumentException("A positive finite extent is required.");
            return PlaneXml(name, extent / 2, extent / 2, DatumZ, extent, extent);
        }

        public static string LakeEntityXml(string name, Lake lake) { return PlaneXml(name, lake.CenterX, lake.CenterY, lake.Z, lake.MaxX - lake.MinX, lake.MaxY - lake.MinY); }

        // A water plane (editor_plane_high is a unit plane centred on its origin, so the scale is its size).
        private static string PlaneXml(string name, double x, double y, double z, double sizeX, double sizeY)
        {
            if (String.IsNullOrEmpty(name) || name.IndexOfAny(new[] { '"', '<', '>', '&' }) >= 0) throw new ArgumentException("A plain entity name is required.");
            foreach (double v in new[] { x, y, z, sizeX, sizeY }) if (Double.IsNaN(v) || Double.IsInfinity(v)) throw new ArgumentException("Finite plane values are required.");
            if (!(sizeX > 0) || !(sizeY > 0)) throw new ArgumentException("A positive plane size is required.");
            Func<double, string> r = v => v.ToString("R", CultureInfo.InvariantCulture);
            return "<game_entity name=\"" + name + "\" mobility=\"1\"><tags><tag name=\"" + Tag + "\" /></tags>" +
                "<transform position=\"" + r(x) + ", " + r(y) + ", " + r(z) + "\" rotation_euler=\"0, 0, 0\" scale=\"" + r(sizeX) + ", " + r(sizeY) + ", 1\" />" +
                "<physics shape=\"" + Physics + "\" /><components><meta_mesh_component name=\"" + Mesh + "\"><mesh name=\"" + Mesh + "\" material=\"" + Material + "\" argument=\"0, 0, 0, 0\" /></meta_mesh_component></components></game_entity>";
        }

        public static string LakeName(string seaName, Lake lake) { return seaName + "_lake_" + lake.Index.ToString(CultureInfo.InvariantCulture); }

        // The encoded scene with the sea plane and the lake planes as its only entities (the encoder writes an empty entity list).
        public static string WithWaterBodies(string sceneXml, string seaName, double extent, IList<Lake> lakes)
        {
            string xml = WithWaterBody(sceneXml, seaName, extent);
            if (lakes == null || lakes.Count == 0) return xml;
            int close = xml.IndexOf("</entities>", StringComparison.Ordinal);
            return xml.Substring(0, close) + String.Concat(lakes.Select(l => LakeEntityXml(LakeName(seaName, l), l))) + xml.Substring(close);
        }

        // The encoded scene with the sea plane as its only entity.
        public static string WithWaterBody(string sceneXml, string name, double extent)
        {
            if (sceneXml == null) throw new ArgumentNullException("sceneXml");
            int open = sceneXml.IndexOf("<entities>", StringComparison.Ordinal), close = sceneXml.IndexOf("</entities>", StringComparison.Ordinal);
            int selfClosing = sceneXml.IndexOf("<entities/>", StringComparison.Ordinal);
            if (selfClosing >= 0 && (open < 0 || selfClosing < open))
                return sceneXml.Substring(0, selfClosing) + "<entities>" + WaterEntityXml(name, extent) + "</entities>" + sceneXml.Substring(selfClosing + "<entities/>".Length);
            if (open < 0 || close < open || sceneXml.IndexOf("<entities>", open + 1, StringComparison.Ordinal) >= 0)
                throw new InvalidDataException("The encoded scene has no single entity list.");
            if (sceneXml.Substring(open + "<entities>".Length, close - open - "<entities>".Length).Trim().Length != 0)
                throw new InvalidDataException("The encoded scene already has entities; the water body is the only one a generated scene gets.");
            return sceneXml.Substring(0, close) + WaterEntityXml(name, extent) + sceneXml.Substring(close);
        }
    }
}
