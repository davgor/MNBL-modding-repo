using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Surface;

// MMR-39 water and climate contract tests, without the game:
//   WaterClimateTests REPO BUNDLE OUT_JSON
// The deterministic climate synthesis (GeneratedClimateSystem) on a real compiled bundle's source and layout: the source read, the
// engine layouts, determinism and identity, seam copies, pole aliases, smoothness, the source wind bearing, the precipitation
// mapping recomputed independently, the colour-grade grid (biome and temperature bands, seam copies, one cell recomputed), the in-memory
// binding's engine indexing, and the policy file; the water system (GeneratedWaterSystem): the water body on the datum, the water classes
// of the bundle, the scene's support entities; and the storm rules (GeneratedStormPolicy).
internal static class WaterClimateTests
{
    private static int checks, failures;
    private static readonly List<object> Rows = new List<object>();
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };

    private static void Check(bool condition, string label, object detail = null)
    {
        checks++; if (!condition) failures++;
        Console.WriteLine((condition ? "ok    " : "FAIL  ") + label + (detail == null || condition ? "" : "  [" + Serializer.Serialize(detail) + "]"));
        Rows.Add(new Dictionary<string, object> { ["check"] = label, ["passed"] = condition, ["detail"] = detail });
    }

    private static int Main(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("usage: WaterClimateTests REPO BUNDLE OUT_JSON"); return 2; }
        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.water-climate-offline", ["version"] = 1, ["ticket"] = "MMR-39",
            ["climateVersion"] = GeneratedClimateSystem.Version, ["waterVersion"] = GeneratedWaterSystem.Version };
        try { Run(args, evidence); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        evidence["checks"] = checks; evidence["failures"] = failures; evidence["rows"] = Rows;
        File.WriteAllText(args[2], Serializer.Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string[] args, SortedDictionary<string, object> evidence)
    {
        string repo = args[0];
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", new[] { "R01", "R02", "R03", "R04", "R05", "R06", "R07", "R08", "R09", "R10" });
        var bundle = WorldDataBundle.Open(args[1], host);
        var surface = WorldSurface.FromBundle(bundle);
        var c = PeriodicWorldCoordinates.FromSurface(surface);
        GeneratedClimateSystem.Source source;
        using (var stream = new MemoryStream(bundle.ReadVerified("source/world.json"))) source = GeneratedClimateSystem.ReadSource(stream, Sha(bundle.ReadVerified("source/world.json")));
        evidence["source"] = new Dictionary<string, object> { ["sha256"] = source.Sha256, ["arrows"] = source.Arrows.Count, ["months"] = source.Months, ["side"] = source.Side, ["rainfallMax"] = source.RainfallMax };
        Check(source.Side == 129 && source.Months == 12 && source.Arrows.Count == 48 && source.RainfallMax > 0,
            "bundle " + bundle.WorldContentId.Substring(0, 12) + ": the source climate is read (129 grid, 12 snow months, 48 wind arrows, rainfall)");
        bool allEast = source.Arrows.All(a => Math.Abs(a.East - 1) < 1e-9 && Math.Abs(a.North) < 1e-9);
        evidence["sourceWindBearing"] = allEast ? "every arrow due east (fixed compass bearing)" : "varied";
        Check(source.Arrows.All(a => Math.Abs(Math.Sqrt(a.East * a.East + a.North * a.North) - 1) < 1e-9), "every source wind arrow is a unit tangent vector (its local east/north components)");

        var r = GeneratedClimateSystem.Synthesize(source, c);
        var again = GeneratedClimateSystem.Synthesize(source, c);
        evidence["synthesis"] = r.Record;
        Check(r.Wind.Length == 512 * 512 * 2 && r.Precipitation.Length == 32 * 32 * 2, "the fields have SandBox.MapScene's layouts (512^2 (x, y) floats, 32^2 (snow, rain) bytes)");
        Check(r.Identity == again.Identity && r.WindSha256 == again.WindSha256 && r.PrecipitationSha256 == again.PrecipitationSha256 && r.PolicyText.Contains(source.Sha256) && r.PolicyText.Contains(c.Identity),
            "the synthesis is deterministic; its identity covers the source, the coordinate layout and the policy");

        // Seam: strip samples are exact copies of their canonical samples (256 wind samples and 16 weather nodes per width).
        int windPeriod = (int)Math.Round(c.Width / (c.Extent / 512)), nodePeriod = (int)Math.Round(c.Width / (c.Extent / 32));
        int windOrigin = (int)Math.Round(c.OriginX / (c.Extent / 512)), nodeOrigin = (int)Math.Round(c.OriginX / (c.Extent / 32));
        int windDiff = 0, nodeDiff = 0;
        for (int row = 0; row < 512; row++)
            for (int col = 0; col < 512; col++)
            {
                if (col >= windOrigin && col < windOrigin + windPeriod) continue;
                int canonical = windOrigin + (((col - windOrigin) % windPeriod) + windPeriod) % windPeriod;
                int i = 2 * (row * 512 + col), j = 2 * (row * 512 + canonical);
                if (r.Wind[i] != r.Wind[j] || r.Wind[i + 1] != r.Wind[j + 1]) windDiff++;
            }
        for (int row = 0; row < 32; row++)
            for (int col = 0; col < 32; col++)
            {
                if (col >= nodeOrigin && col < nodeOrigin + nodePeriod) continue;
                int canonical = nodeOrigin + (((col - nodeOrigin) % nodePeriod) + nodePeriod) % nodePeriod;
                int i = 2 * (row * 32 + col), j = 2 * (row * 32 + canonical);
                if (r.Precipitation[i] != r.Precipitation[j] || r.Precipitation[i + 1] != r.Precipitation[j + 1]) nodeDiff++;
            }
        Check(windPeriod == 256 && nodePeriod == 16 && windDiff == 0 && nodeDiff == 0, "every strip sample is an exact copy of its canonical sample (wind and snow/rain): the seam has no discontinuity",
            new { windPeriod, nodePeriod, windDiff, nodeDiff });

        // Poles: the rows beyond each pole line carry one value (wind 0; the pole's snow/rain), the same in every column.
        int northWindRow = (int)Math.Floor(c.OriginY / (c.Extent / 512)), southWindRow = (int)Math.Ceiling((c.OriginY + c.Height) / (c.Extent / 512));
        bool windPoles = true;
        for (int row = 0; row < 512; row++)
        {
            if (row > northWindRow - 1 && row < southWindRow) continue;
            for (int col = 0; col < 512; col++) if (r.Wind[2 * (row * 512 + col)] != 0 || r.Wind[2 * (row * 512 + col) + 1] != 0) windPoles = false;
        }
        int northNode = (int)Math.Floor(c.OriginY / (c.Extent / 32)) - 1, southNode = (int)Math.Ceiling((c.OriginY + c.Height) / (c.Extent / 32));
        bool nodePoles = true;
        foreach (int row in Enumerable.Range(0, 32).Where(rw => rw <= northNode || rw >= southNode))
            for (int col = 1; col < 32; col++)
                if (r.Precipitation[2 * (row * 32 + col)] != r.Precipitation[2 * (row * 32)] || r.Precipitation[2 * (row * 32 + col) + 1] != r.Precipitation[2 * (row * 32) + 1]) nodePoles = false;
        Check(windPoles && nodePoles, "beyond the pole lines every column has one value (wind calm; the pole node's snow and rain): the pole is one location", new { northWindRow, southWindRow, northNode, southNode });

        // Smoothness and bearing.
        double maxStep = Convert.ToDouble(r.Record["windMaxNeighbourStep"]);
        var magnitude = (IDictionary<string, object>)r.Record["windMagnitude"];
        Check(maxStep <= 0.04 && Convert.ToDouble(magnitude["max"]) <= GeneratedClimateSystem.WindScale + 1e-6, "the wind field is smooth (largest neighbour step " + maxStep + ", the official War Sails flowmap's mean step is 0.39) and bounded by the policy scale");
        int mid = 2 * (((int)Math.Round((c.OriginY + c.Height / 2) / (c.Extent / 512))) * 512 + windOrigin + 10);
        Check(!allEast || (Math.Abs(r.Wind[mid] - GeneratedClimateSystem.WindScale) < 1e-6 && Math.Abs(r.Wind[mid + 1]) < 1e-6),
            "mid-latitude wind is the source's bearing at the policy scale: due east (+x) at " + GeneratedClimateSystem.WindScale, new { x = r.Wind[mid], y = r.Wind[mid + 1] });

        // Precipitation: one node recomputed independently from the source layers.
        int nodeRow = (int)Math.Round((c.OriginY + c.Height / 2) / (c.Extent / 32)), nodeCol = nodeOrigin + 5;
        double snow = 0, rain = 0;
        for (int sy = 0; sy < 8; sy++)
            for (int sx = 0; sx < 8; sx++)
            {
                double px = (nodeCol + (sx + .5) / 8) * (c.Extent / 32), py = (nodeRow + (sy + .5) / 8) * (c.Extent / 32), gc, gr;
                GeneratedClimateSystem.SourceGrid(c, px, py, out gc, out gr);
                snow += Math.Max(GeneratedClimateSystem.Bilinear(source.SnowMax, 129, gc, gr), GeneratedClimateSystem.Bilinear(source.IceCap, 129, gc, gr));
                rain += GeneratedClimateSystem.Bilinear(source.Rainfall, 129, gc, gr) / source.RainfallMax;
            }
        byte expectSnow = (byte)Math.Round(255 * Math.Min(1, snow / 64), MidpointRounding.AwayFromZero), expectRain = (byte)Math.Round(255 * Math.Min(1, rain / 64), MidpointRounding.AwayFromZero);
        int k = 2 * (nodeRow * 32 + nodeCol);
        Check(r.Precipitation[k] == expectSnow && r.Precipitation[k + 1] == expectRain, "a weather node's snow and rain recompute from the source layers (8 x 8 footprint mean; rain over the layer maximum; snow the monthly maximum or the ice cap)",
            new { node = new[] { nodeRow, nodeCol }, got = new[] { r.Precipitation[k], r.Precipitation[k + 1] }, expected = new[] { expectSnow, expectRain } });
        Check(Convert.ToInt32(r.Record["snowNodes"]) > 0 && Convert.ToInt32(r.Record["rainNodes"]) > 0, "the world has snowy and rainy weather nodes (" + r.Record["snowNodes"] + " and " + r.Record["rainNodes"] + " of 1024)");

        // The in-memory binding indexes the fields the way the engine does (MapScene.GetTextureDataIndexForPosition).
        var bound = GeneratedClimate.FromSynthesis(r, (float)c.Extent);
        var probe = new TaleWorlds.Library.Vec2(700.5f, 812.25f);
        int wi = 2 * ((int)Math.Floor(812.25 / c.Extent * 512) * 512 + (int)Math.Floor(700.5 / c.Extent * 512));
        int pi = 2 * ((int)Math.Floor(812.25 / c.Extent * 32) * 32 + (int)Math.Floor(700.5 / c.Extent * 32));
        int gi = (int)Math.Floor(812.25 / c.Extent * 512) * 512 + (int)Math.Floor(700.5 / c.Extent * 512);
        Check(bound.Identity == r.Identity && bound.WindAt(probe).X == r.Wind[wi] && bound.WindAt(probe).Y == r.Wind[wi + 1] && bound.SnowAt(probe) == r.Precipitation[pi] && bound.RainAt(probe) == r.Precipitation[pi + 1]
              && bound.HasColorGrade && bound.ColorGradeAt(probe) == r.ColorGrade[gi] && bound.ColorGradeSha256 == r.ColorGradeSha256,
            "the in-memory binding answers with the engine's index (row * dim + col, col = floor(x / extent * dim)) for wind, snow/rain and colour grade");

        ColorGrade(source, c, r, again, evidence);
        WindDirections(source, c);
        bool refused = false;
        try { GeneratedClimateSystem.Synthesize(source, new PeriodicWorldCoordinates(799, 400, 400, 600, 1600, 128)); }
        catch (InvalidDataException e) { refused = e.Message.Contains("whole number"); }
        Check(refused, "a layout whose width is not a whole number of samples of every grid is refused (strip samples could not copy canonical ones)");
        Storms(c, evidence);

        // The MMR-63 fixture layout (pole rows 400..1200) synthesizes from the same source.
        var fixture = new PeriodicWorldCoordinates(800, 800, 400, 400, 1600, 128);
        var rf = GeneratedClimateSystem.Synthesize(source, fixture);
        Check(rf.Identity != r.Identity && Convert.ToDouble(((IDictionary<string, object>)rf.Record["windMagnitude"])["max"]) <= GeneratedClimateSystem.WindScale + 1e-6, "the MMR-63 fixture layout synthesizes its own fields from the same source (a different identity)");

        // The policy file names the code's constants.
        var policy = (IDictionary<string, object>)Serializer.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen/policies/climate-flow.json")).TrimStart('﻿'));
        var wind = (IDictionary<string, object>)policy["wind"];
        Check((string)policy["version"] == GeneratedClimateSystem.Version && Convert.ToDouble(wind["scale"]) == GeneratedClimateSystem.WindScale && Convert.ToDouble(wind["poleTaperRows"]) == GeneratedClimateSystem.PoleTaperRows
              && Convert.ToInt32(wind["dimension"]) == GeneratedClimateSystem.WindDimension, "worldgen/policies/climate-flow.json names the code's version and constants");

        Water(bundle, surface, c, source, evidence);
    }

    private static void ColorGrade(GeneratedClimateSystem.Source source, PeriodicWorldCoordinates c, GeneratedClimateSystem.Result r, GeneratedClimateSystem.Result again, SortedDictionary<string, object> evidence)
    {
        var grades = new HashSet<byte>(new byte[] { GeneratedClimateSystem.GradeDesert, GeneratedClimateSystem.GradeSteppe, GeneratedClimateSystem.GradeCentralEurope, GeneratedClimateSystem.GradeHighlands,
            GeneratedClimateSystem.GradeNormandy, GeneratedClimateSystem.GradeNorth, GeneratedClimateSystem.GradeMediterranean });
        var biomes = source.Biome.Select(b => (int)b).GroupBy(b => b).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => (object)g.Count());
        evidence["sourceBiomes"] = biomes;
        Check(r.ColorGrade.Length == 512 * 512 && r.ColorGrade.All(grades.Contains) && r.ColorGradeSha256 == again.ColorGradeSha256,
            "the colour-grade grid has MapColorGradeManager's layout (512^2 bytes), only installed grade values, and is deterministic", r.Record["colorGrades"]);
        Check(((IDictionary<string, object>)r.Record["colorGrades"]).Count >= 3, "the seed-42 world shows several climate grades (" + String.Join(", ", ((IDictionary<string, object>)r.Record["colorGrades"]).Keys) + ")");
        int period = (int)Math.Round(c.Width / (c.Extent / 512)), origin = (int)Math.Round(c.OriginX / (c.Extent / 512)), diff = 0;
        for (int row = 0; row < 512; row++)
            for (int col = 0; col < 512; col++)
            {
                if (col >= origin && col < origin + period) continue;
                int canonical = origin + (((col - origin) % period) + period) % period;
                if (r.ColorGrade[row * 512 + col] != r.ColorGrade[row * 512 + canonical]) diff++;
            }
        int north = (int)Math.Floor(c.OriginY / (c.Extent / 512)) - 1, south = (int)Math.Ceiling((c.OriginY + c.Height) / (c.Extent / 512));
        bool poles = true;
        foreach (int row in Enumerable.Range(0, 512).Where(rw => rw <= north || rw >= south))
            for (int col = 1; col < 512; col++) if (r.ColorGrade[row * 512 + col] != r.ColorGrade[row * 512]) poles = false;
        Check(diff == 0 && poles, "colour-grade strip cells copy their canonical cells and the rows beyond each pole line carry one grade", new { diff, north, south });
        // One land and one sea cell recomputed from the source layers.
        int checkedCells = 0, wrong = 0;
        foreach (int row in new[] { (int)Math.Round((c.OriginY + c.Height * .3) / (c.Extent / 512)), (int)Math.Round((c.OriginY + c.Height * .5) / (c.Extent / 512)), (int)Math.Round((c.OriginY + c.Height * .7) / (c.Extent / 512)) })
            for (int col = origin; col < origin + period; col += 37)
            {
                double gc, gr; GeneratedClimateSystem.SourceGrid(c, (col + .5) * (c.Extent / 512), (row + .5) * (c.Extent / 512), out gc, out gr);
                int n = 128, sc = ((int)Math.Round(gc, MidpointRounding.AwayFromZero) % n + n) % n, sr = (int)Math.Round(Math.Max(0, Math.Min(n, gr)), MidpointRounding.AwayFromZero);
                int biome = (int)source.Biome[sr * 129 + sc];
                double t = GeneratedClimateSystem.Bilinear(source.Temperature, 129, gc, gr);
                byte expected;
                switch (biome)
                {
                    case 2: expected = 20; break;
                    case 3: expected = 40; break;
                    case 4: expected = 60; break;
                    case 5: expected = 80; break;
                    case 7: expected = 140; break;
                    case 13: expected = 100; break;
                    case 1: case 6: case 15: case 16: case 17: expected = 120; break;
                    default: expected = (byte)(t < 2 ? 120 : t < 18 ? 100 : 140); break;
                }
                checkedCells++; if (r.ColorGrade[row * 512 + col] != expected) wrong++;
            }
        Check(wrong == 0, "colour-grade cells recompute from the source (nearest cell's biome; water and other ids by temperature band): " + checkedCells + " cells", new { wrong });
    }

    // The wind's direction maths on synthetic arrows (the seed-42 arrows all point east): all north gives (0, -scale) at mid latitudes
    // (physical y runs south); east and west in alternate lattice columns interpolate through calm between them.
    private static void WindDirections(GeneratedClimateSystem.Source source, PeriodicWorldCoordinates c)
    {
        Func<Func<GeneratedClimateSystem.Arrow, double[]>, GeneratedClimateSystem.Result> with = make =>
        {
            var copy = new GeneratedClimateSystem.Source { Side = source.Side, Rainfall = source.Rainfall, IceCap = source.IceCap, SnowMax = source.SnowMax, Biome = source.Biome,
                Temperature = source.Temperature, Sha256 = source.Sha256, Months = source.Months, RainfallMax = source.RainfallMax };
            foreach (var a in source.Arrows) { var v = make(a); copy.Arrows.Add(new GeneratedClimateSystem.Arrow { Column = a.Column, Row = a.Row, East = v[0], North = v[1] }); }
            return GeneratedClimateSystem.Synthesize(copy, c);
        };
        var columns = source.Arrows.Select(a => a.Column).Distinct().OrderBy(x => x).ToList();
        var north = with(a => new[] { 0.0, 1.0 });
        var alternating = with(a => new[] { columns.IndexOf(a.Column) % 2 == 0 ? 1.0 : -1.0, 0.0 });
        int mid = (int)Math.Round((c.OriginY + c.Height / 2) / (c.Extent / 512));
        bool allNorth = true; double sum = 0, max = 0, min = 0;
        for (int col = 0; col < 512; col++)
        {
            int i = 2 * (mid * 512 + col);
            if (Math.Abs(north.Wind[i]) > 1e-6 || Math.Abs(north.Wind[i + 1] + GeneratedClimateSystem.WindScale) > 1e-6) allNorth = false;
            sum += alternating.Wind[i]; max = Math.Max(max, alternating.Wind[i]); min = Math.Min(min, alternating.Wind[i]);
        }
        Check(allNorth, "arrows pointing north give the map vector (0, -" + GeneratedClimateSystem.WindScale + ") at mid latitude (physical y runs south)");
        Check(max > .3 && min < -.3 && Math.Abs(sum / 512) < .01, "east and west arrows in alternate lattice columns interpolate through calm between them (mid-row range " + Math.Round(min, 3) + " to " + Math.Round(max, 3) + ", mean " + Math.Round(sum / 512, 4) + ")");
    }

    private static void Storms(PeriodicWorldCoordinates c, SortedDictionary<string, object> evidence)
    {
        Check(GeneratedStormPolicy.PoleReflectY(c, c.OriginY - 2) == c.OriginY + 2 && GeneratedStormPolicy.PoleReflectY(c, c.OriginY + c.Height + 2) == c.OriginY + c.Height - 2
              && GeneratedStormPolicy.PoleReflectY(c, c.OriginY + 5) == c.OriginY + 5 && GeneratedStormPolicy.PoleReflectY(null, -7) == -7,
            "a storm past a pole line is reflected back across it (one between the lines stays)");
        double scale = GeneratedStormPolicy.SpawnChanceScale(c.Extent, 32);
        evidence["stormPolicy"] = GeneratedStormPolicy.Record(c, c.Extent, 32);
        Check(Math.Abs(scale - Math.Pow(c.Extent / 32 / 32.5, 2)) < 1e-12 && Math.Abs(GeneratedStormPolicy.SpawnChanceScale(32.5 * 32, 32) - 1) < 1e-12,
            "the storm spawn chance is scaled by the area of a weather node against Calradia's (1 at Calradia's spacing; " + Math.Round(scale, 4) + " here)");
        double midY = c.OriginY + c.Height / 2;
        Check(GeneratedStormPolicy.CanSpawnAt(c, c.OriginX + 1, midY) && !GeneratedStormPolicy.CanSpawnAt(c, c.OriginX - 1, midY) && !GeneratedStormPolicy.CanSpawnAt(c, c.OriginX + c.Width + 1, midY)
              && !GeneratedStormPolicy.CanSpawnAt(c, c.OriginX + 1, c.OriginY - 1) && !GeneratedStormPolicy.CanSpawnAt(c, c.OriginX + 1, c.OriginY + c.Height + 1) && GeneratedStormPolicy.CanSpawnAt(null, -5, -5),
            "storms are born only at canonical positions between the pole lines (never in a strip or beyond a pole; anywhere on a world that is not periodic)");
        double east = c.OriginX + c.Width - 2, west = c.OriginX + 3;
        Check(Math.Abs(GeneratedStormPolicy.NearestCopyX(c, east, west) - (west + c.Width)) < 1e-9 && GeneratedStormPolicy.NearestCopyX(c, east, east - 10) == east - 10 && GeneratedStormPolicy.NearestCopyX(null, east, west) == west,
            "a position across the seam from a storm is measured at its copy beside the storm (5 units away, not width - 5)");
        Check(Math.Abs(GeneratedStormPolicy.RebaseShift(c, c.OriginX + c.Width + 4) + c.Width) < 1e-9 && Math.Abs(GeneratedStormPolicy.RebaseShift(c, c.OriginX - 4) - c.Width) < 1e-9 && GeneratedStormPolicy.RebaseShift(c, c.OriginX + 4) == 0,
            "a storm that leaves the canonical range is moved back by one width (none while canonical)");
    }

    private static void Water(WorldDataBundle bundle, WorldSurface surface, PeriodicWorldCoordinates c, GeneratedClimateSystem.Source source, SortedDictionary<string, object> evidence)
    {
        var classes = GeneratedWaterSystem.Classes(bundle);
        evidence["waterClasses"] = classes.Record();
        Check(classes.Ocean > 0 && classes.Lake >= 0 && classes.River >= 0 && classes.Ocean + classes.Lake + classes.River + classes.Land == classes.Samples, "the bundle's water classes are read (ocean, lake, river, land)", classes.Record());
        var xml = GeneratedWaterSystem.WaterEntityXml("mnbl_test", surface.Extent);
        var doc = new System.Xml.XmlDocument(); doc.LoadXml(xml);
        var transform = (System.Xml.XmlElement)doc.DocumentElement.SelectSingleNode("transform");
        var position = transform.GetAttribute("position").Split(',').Select(v => Double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var scale = transform.GetAttribute("scale").Split(',').Select(v => Double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Check(position[2] == GeneratedWaterSystem.DatumZ && GeneratedWaterSystem.DatumZ == 0 && scale[0] >= surface.Extent && scale[1] >= surface.Extent
              && Math.Abs(position[0] - surface.Extent / 2) < 1e-9 && Math.Abs(position[1] - surface.Extent / 2) < 1e-9,
            "the water body lies on the projection's datum (z = 0) and covers the whole terrain", new { position, scale });
        Check(xml.Contains("naval_worldmap_water") && xml.Contains("snapped_water_body") && xml.Contains("bo_editor_plane"), "the water body uses the installed War Sails water material, its water-body tag and the plane physics (as the MMR-59/63 fixtures)");
        // A production scene's support entities: the encoder's empty entity list gets the water body and the colour-grade manager.
        string scene = "<scene name=\"mnbl_generated_test\"><entities>\n\t</entities></scene>";   // the encoder writes the template's empty list
        string supportXml = GeneratedClimateSystem.WithColorGradeManager(GeneratedWaterSystem.WithWaterBody(scene, "mnbl_generated_test_water", surface.Extent), "mnbl_generated_test_colorgrade");
        var support = new System.Xml.XmlDocument(); support.LoadXml(supportXml);
        var entities = support.SelectNodes("/scene/entities/game_entity").Cast<System.Xml.XmlElement>().ToList();
        bool refused = false;
        try { GeneratedClimateSystem.WithColorGradeManager(supportXml, "again"); } catch (InvalidDataException) { refused = true; }
        Check(entities.Count == 2 && entities[0].GetAttribute("name").EndsWith("_water") && entities[1].SelectNodes(".//script").Count == 1
              && ((System.Xml.XmlElement)entities[1].SelectSingleNode(".//script")).GetAttribute("name") == "MapColorGradeManager" && refused,
            "a generated scene carries one water body and one colour-grade manager (a second manager is refused)");

        // Lakes keep their own level: one plane per lake group at its source water level, projected as the terrain.
        int unmatched, hidden;
        var lakes = GeneratedWaterSystem.Lakes(surface, source, c, out unmatched, out hidden);
        double perMetre = Convert.ToDouble(surface.Scale["verticalWorldUnitsPerMetre"]);   // carries the 2.5x exaggeration (docs/world-projection.md)
        int lakeSamples = 0;
        for (int row = 0; row < surface.Side; row++) for (int col = 0; col < surface.Side; col++) if (surface.Intent(col, row) == 2) lakeSamples++;
        evidence["lakes"] = new Dictionary<string, object> { ["count"] = lakes.Count, ["hidden"] = hidden, ["unmatched"] = unmatched, ["lakeSamples"] = lakeSamples, ["clamped"] = lakes.Count(l => l.Clamped),
            ["perMetre"] = perMetre, ["rows"] = lakes.Select(l => (object)l.Record()).ToList() };
        Check(lakes.Count > 0 && unmatched == 0 && lakes.All(l => l.Matched == l.Samples),
            "every lake body's level comes from the source's lake cells (" + lakes.Count + " bodies shown, " + hidden + " too shallow to clear their beds, " + lakeSamples + " lake samples)", new { unmatched, hidden });
        Check(lakes.All(l => l.MinX >= 0 && l.MinY >= 0 && l.MaxX <= surface.Extent && l.MaxY <= surface.Extent), "every lake plane lies inside the terrain (the scene's box is the campaign map's borders)");
        Check(lakes.All(l => l.Z > l.BedMaxZ && l.Z <= l.ShoreMinZ - .001 + 1e-12 && Math.Abs(l.LevelZ - l.LevelM * perMetre) < 1e-9 && l.Z <= l.LevelZ && l.Clamped == (l.Z < l.LevelZ)),
            "each lake's plane clears its bed, never rises above another sample within one sample of its box, and is its source level projected as the terrain (level x " + perMetre + ") unless that would",
            lakes.Select(l => new { l.Z, l.LevelZ, l.BedMaxZ, l.ShoreMinZ }));
        int copies = 0, copyMismatch = 0;
        int cutCopies = 0;
        foreach (var l in lakes.Where(l => !c.IsCanonicalX(l.CenterX)))
        {
            // A copy cut by the terrain's edge holds part of its lake: its twin is the canonical lake holding the copy of its first sample.
            bool cut = l.MinX <= 1e-9 || l.MaxX >= surface.Extent - 1e-9;
            double copyX = l.FirstSampleX + (c.IsCanonicalX(l.FirstSampleX + c.Width) ? c.Width : -c.Width);
            var twin = cut ? lakes.FirstOrDefault(o => o != l && c.IsCanonicalX(o.CenterX) && copyX >= o.MinX - 1e-6 && copyX <= o.MaxX + 1e-6 && l.FirstSampleY >= o.MinY - 1e-6 && l.FirstSampleY <= o.MaxY + 1e-6)
                           : lakes.FirstOrDefault(o => c.IsCanonicalX(o.CenterX) && Math.Abs(Math.Abs(o.CenterX - l.CenterX) - c.Width) < 1e-6 && Math.Abs(o.CenterY - l.CenterY) < 1e-6);
            copies++; if (cut) cutCopies++;
            if (twin == null || twin.Z != l.Z || (!cut && twin.Samples != l.Samples)) copyMismatch++;
        }
        Check(copyMismatch == 0, "every strip lake is a copy of a canonical lake one width away, at the same level, a copy cut by the terrain's edge at its whole twin's (" +
              copies + " strip copies, " + cutCopies + " cut by the edge)", new { copies, cutCopies, copyMismatch });
        string withLakes = GeneratedWaterSystem.WithWaterBodies(scene, "mnbl_generated_test_water", surface.Extent, lakes);
        var lakeDoc = new System.Xml.XmlDocument(); lakeDoc.LoadXml(withLakes);
        Check(lakeDoc.SelectNodes("/scene/entities/game_entity").Count == 1 + lakes.Count && lakeDoc.SelectNodes("/scene/entities/game_entity/tags/tag[@name='snapped_water_body']").Count == 1 + lakes.Count,
            "a generated scene carries the sea plane and one plane per lake body, each a War Sails water body");
    }

    private static string Sha(byte[] bytes) { return GeneratedClimateSystem.Sha(bytes); }
}
