using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.Library;

// MMR-39 water and climate contract tests, without the game:
//   WaterClimateTests REPO BUNDLE OUT_JSON
// The deterministic climate synthesis (GeneratedClimateSystem) on a real compiled bundle's source and layout: the source read, the
// engine layouts, determinism and identity, seam copies, pole aliases, smoothness, the precipitation mapping recomputed independently, the
// colour-grade grid (biome, moisture and temperature bands, mode filter, seam copies, cells recomputed), the in-memory binding's engine
// indexing, and the policy file; the water system (GeneratedWaterSystem): the water body on the datum, the water classes of the bundle, the
// scene's support entities; and the storm rules (GeneratedStormPolicy).
// MMR-256: the snow byte from the snow-season length (the inverse installed formula; every weather node's Snowy share of the year, simulated
// with the installed TaleWorlds.Library maths, equals its FWGC++ season length), the snow texture (layout, orientation, seams, scene name),
// and the latitude-banded, seed-perturbed winds (RT-18).
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
        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.water-climate-offline", ["version"] = 2, ["ticket"] = "MMR-39, MMR-256",
            ["climateVersion"] = GeneratedClimateSystem.Version, ["waterVersion"] = GeneratedWaterSystem.Version };
        try { Run(args, evidence); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        evidence["checks"] = checks; evidence["failures"] = failures; evidence["rows"] = Rows;
        File.WriteAllText(args[2], Serializer.Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static GeneratedClimateSystem.Policy policy;
    private static byte[] policyBytes;

    private static void Run(string[] args, SortedDictionary<string, object> evidence)
    {
        string repo = args[0];
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", WorldDataBundle.HostCapabilities);
        var bundle = WorldDataBundle.Open(args[1], host);
        var surface = WorldSurface.FromBundle(bundle);
        var c = PeriodicWorldCoordinates.FromSurface(surface);
        policyBytes = File.ReadAllBytes(Path.Combine(repo, "worldgen/policies/climate-flow.json"));
        policy = GeneratedClimateSystem.Policy.FromBytes(policyBytes);
        GeneratedClimateSystem.Source source;
        using (var stream = new MemoryStream(bundle.ReadVerified("source/world.json"))) source = GeneratedClimateSystem.ReadSource(stream, Sha(bundle.ReadVerified("source/world.json")));
        evidence["bundle"] = bundle.WorldContentId;
        evidence["source"] = new Dictionary<string, object> { ["sha256"] = source.Sha256, ["seed"] = source.Seed, ["months"] = source.Months, ["side"] = source.Side, ["rainfallMax"] = source.RainfallMax };
        Check(source.Side == 129 && source.Months == 12 && source.Snow.Length == 12 && source.Moisture != null && source.RainfallMax > 0,
            "bundle " + bundle.WorldContentId.Substring(0, 12) + ": the source climate is read (129 grid, 12 snow months, moisture, rainfall; seed " + source.Seed + ")");

        var r = GeneratedClimateSystem.Synthesize(source, c, policy);
        var again = GeneratedClimateSystem.Synthesize(source, c, policy);
        evidence["synthesis"] = r.Record;
        Check(r.Wind.Length == 512 * 512 * 2 && r.Precipitation.Length == 32 * 32 * 2 && r.SnowTexture.Length == 512 * 512 * 4, "the fields have SandBox.MapScene's layouts (512^2 (x, y) floats, 32^2 (snow, rain) bytes) and the snow texture is 512^2 RGBA");
        Check(r.Identity == again.Identity && r.WindSha256 == again.WindSha256 && r.PrecipitationSha256 == again.PrecipitationSha256 && r.SnowTextureSha256 == again.SnowTextureSha256 && r.PolicyText.Contains(source.Sha256) && r.PolicyText.Contains(c.Identity)
              && r.PolicyText.Contains(";seed=" + source.Seed + ";") && r.Identity == Sha(new UTF8Encoding(false).GetBytes(GeneratedClimateSystem.IdentityText(r.PolicyText, r.WindSha256, r.PrecipitationSha256, r.ColorGradeSha256, r.SnowTextureSha256))),
            "the synthesis is deterministic; its identity covers the source, the seed, the coordinate layout, the policy and every field (the snow texture too)");

        // Seam: strip samples are exact copies of their canonical samples (256 wind samples and 16 weather nodes per width).
        int windPeriod = (int)Math.Round(c.Width / (c.Extent / 512)), nodePeriod = (int)Math.Round(c.Width / (c.Extent / 32));
        int windOrigin = (int)Math.Round(c.OriginX / (c.Extent / 512)), nodeOrigin = (int)Math.Round(c.OriginX / (c.Extent / 32));
        int windDiff = 0, nodeDiff = 0, texDiff = 0;
        for (int row = 0; row < 512; row++)
            for (int col = 0; col < 512; col++)
            {
                if (col >= windOrigin && col < windOrigin + windPeriod) continue;
                int canonical = windOrigin + (((col - windOrigin) % windPeriod) + windPeriod) % windPeriod;
                int i = 2 * (row * 512 + col), j = 2 * (row * 512 + canonical);
                if (r.Wind[i] != r.Wind[j] || r.Wind[i + 1] != r.Wind[j + 1]) windDiff++;
                for (int ch = 0; ch < 4; ch++) if (r.SnowTexture[4 * (row * 512 + col) + ch] != r.SnowTexture[4 * (row * 512 + canonical) + ch]) { texDiff++; break; }
            }
        for (int row = 0; row < 32; row++)
            for (int col = 0; col < 32; col++)
            {
                if (col >= nodeOrigin && col < nodeOrigin + nodePeriod) continue;
                int canonical = nodeOrigin + (((col - nodeOrigin) % nodePeriod) + nodePeriod) % nodePeriod;
                int i = 2 * (row * 32 + col), j = 2 * (row * 32 + canonical);
                if (r.Precipitation[i] != r.Precipitation[j] || r.Precipitation[i + 1] != r.Precipitation[j + 1]) nodeDiff++;
            }
        Check(windPeriod == 256 && nodePeriod == 16 && windDiff == 0 && nodeDiff == 0 && texDiff == 0, "every strip sample is an exact copy of its canonical sample (wind, snow/rain and the snow texture): the seam has no discontinuity",
            new { windPeriod, nodePeriod, windDiff, nodeDiff, texDiff });

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

        // Smoothness: the bands turn through calm over blendDeg, so a neighbour step is bounded by the blend.
        double maxStep = Convert.ToDouble(r.Record["windMaxNeighbourStep"]);
        var magnitude = (IDictionary<string, object>)r.Record["windMagnitude"];
        Check(maxStep <= 0.39 && Convert.ToDouble(magnitude["max"]) <= GeneratedClimateSystem.WindScale + 1e-6, "the wind field is smooth (largest neighbour step " + maxStep + ", at a band edge; below the official War Sails flowmap's mean step, 0.39) and bounded by the policy scale");

        SnowSeason(source, c, r, evidence);
        SnowTexture(source, c, r);

        // The in-memory binding indexes the fields the way the engine does (MapScene.GetTextureDataIndexForPosition).
        var bound = GeneratedClimate.FromSynthesis(r, (float)c.Extent);
        var probe = new Vec2(700.5f, 812.25f);
        int wi = 2 * ((int)Math.Floor(812.25 / c.Extent * 512) * 512 + (int)Math.Floor(700.5 / c.Extent * 512));
        int pi = 2 * ((int)Math.Floor(812.25 / c.Extent * 32) * 32 + (int)Math.Floor(700.5 / c.Extent * 32));
        int gi = (int)Math.Floor(812.25 / c.Extent * 512) * 512 + (int)Math.Floor(700.5 / c.Extent * 512);
        Check(bound.Identity == r.Identity && bound.WindAt(probe).X == r.Wind[wi] && bound.WindAt(probe).Y == r.Wind[wi + 1] && bound.SnowAt(probe) == r.Precipitation[pi] && bound.RainAt(probe) == r.Precipitation[pi + 1]
              && bound.HasColorGrade && bound.ColorGradeAt(probe) == r.ColorGrade[gi] && bound.ColorGradeSha256 == r.ColorGradeSha256
              && bound.SnowTextureName == r.SnowTextureName && bound.SnowTextureSha256 == r.SnowTextureSha256 && Sha(bound.CopySnowTexture()) == r.SnowTextureSha256,
            "the in-memory binding answers with the engine's index (row * dim + col, col = floor(x / extent * dim)) for wind, snow/rain and colour grade, and carries the snow texture");

        ColorGrade(source, c, r, again, evidence);
        Winds(source, c, r, evidence);
        bool refused = false;
        try { GeneratedClimateSystem.Synthesize(source, new PeriodicWorldCoordinates(799, 400, 400, 600, 1600, 128), policy); }
        catch (InvalidDataException e) { refused = e.Message.Contains("whole number"); }
        Check(refused, "a layout whose width is not a whole number of samples of every grid is refused (strip samples could not copy canonical ones)");
        Storms(c, evidence);

        // The MMR-63 fixture layout (pole rows 400..1200) synthesizes from the same source.
        var fixture = new PeriodicWorldCoordinates(800, 800, 400, 400, 1600, 128);
        var rf = GeneratedClimateSystem.Synthesize(source, fixture, policy);
        Check(rf.Identity != r.Identity && Convert.ToDouble(((IDictionary<string, object>)rf.Record["windMagnitude"])["max"]) <= GeneratedClimateSystem.WindScale + 1e-6, "the MMR-63 fixture layout synthesizes its own fields from the same source (a different identity)");

        // The policy file names the code's constants, and a table the inverse rule does not give is refused.
        var doc = (IDictionary<string, object>)Serializer.DeserializeObject(Encoding.UTF8.GetString(policyBytes).TrimStart((char)0xFEFF));
        var wind = (IDictionary<string, object>)doc["wind"];
        Check((string)doc["version"] == GeneratedClimateSystem.Version && Convert.ToDouble(wind["scale"]) == GeneratedClimateSystem.WindScale && Convert.ToDouble(wind["poleTaperRows"]) == GeneratedClimateSystem.PoleTaperRows
              && Convert.ToInt32(wind["dimension"]) == GeneratedClimateSystem.WindDimension, "worldgen/policies/climate-flow.json names the code's version and constants");
        string text = Encoding.UTF8.GetString(policyBytes);
        Func<string, bool> refuses = changed => { try { GeneratedClimateSystem.Policy.FromBytes(Encoding.UTF8.GetBytes(changed)); return false; } catch (InvalidDataException) { return true; } };
        Check(text.Contains("\"6\": 113") && refuses(text.Replace("\"6\": 113", "\"6\": 120")) && text.Contains("\"route\": \"" + policy.SnowRoute + "\"") && refuses(text.Replace("\"route\": \"" + policy.SnowRoute + "\"", "\"route\": \"dynamic\"")) && refuses(text.Replace("\"version\": \"mmr256/1\"", "\"version\": \"mmr39/1\"")),
            "the policy reader refuses a snow-season byte the inverse rule does not give, an unknown snow route and another version");

        Water(bundle, surface, c, source, evidence);
    }

    // ---- MMR-256: the snow byte from the snow-season length (a1) ----
    // The installed snow season factor (TaleWorlds.CampaignSystem DefaultMapWeatherModel.CalculateTimeFactorForSnow, IL) at a year progress
    // in seasons [0, 4).
    private static float SeasonFactor(float p)
    {
        if (p > 1.5f && p <= 3.5) return MBMath.Map(p, 1.5f, 3.5f, 0f, 1f);
        if (p <= 1.5f) return MBMath.Map(p, 0f, 1.5f, 0.75f, 0f);
        return MBMath.Map(p, 3.5f, 4f, 1f, 0.75f);
    }

    // Whether a snow byte is Snowy at a season factor, with the installed maths.
    private static bool SnowyAt(byte b, float s)
    {
        float c = MBMath.Lerp(0.55f, -0.1f, s, 1E-05f), v = MBMath.SmoothStep(c - 0.65f, c + 0.65f, b / 255f);
        return MBMath.Lerp(0f, v, v, 1E-05f) > 0.55f;
    }

    // The share of the year a snow byte is Snowy (GetSnowAndRainDataForPosition's snow above 0.55), with the installed maths.
    private static double SnowyShare(byte b, int samples = 4800)
    {
        int snowy = 0;
        for (int i = 0; i < samples; i++)
        {
            float s = SeasonFactor(4f * (i + .5f) / samples), c = MBMath.Lerp(0.55f, -0.1f, s, 1E-05f);
            float v = MBMath.SmoothStep(c - 0.65f, c + 0.65f, b / 255f), snow = MBMath.Lerp(0f, v, v, 1E-05f);
            if (snow > 0.55f) snowy++;
        }
        return snowy / (double)samples;
    }

    private static void SnowSeason(GeneratedClimateSystem.Source source, PeriodicWorldCoordinates c, GeneratedClimateSystem.Result r, SortedDictionary<string, object> evidence)
    {
        // The policy's table is the inverse rule, and each byte's simulated Snowy share is its season (12, 9, 6, 3 months; the peak only).
        var table = new SortedDictionary<string, object>(StringComparer.Ordinal);
        bool tableOk = true;
        foreach (var kv in policy.SeasonBytes)
        {
            double f = kv.Key == "peak" ? 0 : Int32.Parse(kv.Key) / 12.0, share = SnowyShare((byte)kv.Value), below = kv.Value > 1 ? SnowyShare((byte)(kv.Value - 1)) : 0;
            table[kv.Key] = new Dictionary<string, object> { ["byte"] = kv.Value, ["snowyShare"] = Math.Round(share, 4), ["shareOfByteBelow"] = Math.Round(below, 4) };
            if (kv.Key == "peak" ? !(share < 0.01 && SnowyAt((byte)kv.Value, 1f) && !SnowyAt((byte)(kv.Value - 1), 1f)) : !(share >= f - 1e-3 && share <= f + 0.0075 && below < f)) tableOk = false;
        }
        evidence["snowSeasonTable"] = table;
        Check(tableOk && policy.IceCapByte == 255 && SnowyShare(255) == 1, "the snow-season bytes are the inverse of the installed formula: 196 is Snowy all year, 155 for 9 months, 113 for 6, 72 for 3, 30 only at the peak, each the smallest such byte; the ice cap (255) all year", table);

        // Every weather node (a1): the Snowy share of the year its byte gives equals the mean FWGC++ season length of its 8 x 8 footprint
        // (recomputed here from the monthly snow), and a node that is mostly ice cap is Snowy all year. Under the real periodic layout.
        double nodeStep = c.Extent / 32, worst = 0; int nodes = 0, seasonal = 0, ice = 0, wrong = 0;
        var rows = new List<object>();
        for (int row = 0; row < 32; row++)
            for (int col = 0; col < 32; col++)
            {
                double months = 0; int iceSamples = 0;
                for (int sy = 0; sy < 8; sy++)
                    for (int sx = 0; sx < 8; sx++)
                    {
                        double gc, gr; GeneratedClimateSystem.SourceGrid(c, (col + (sx + .5) / 8) * nodeStep, (row + (sy + .5) / 8) * nodeStep, out gc, out gr);
                        if (GeneratedClimateSystem.Bilinear(source.IceCap, 129, gc, gr) >= 0.5) { iceSamples++; months += 12; continue; }
                        foreach (var month in source.Snow) if (GeneratedClimateSystem.Bilinear(month, 129, gc, gr) >= 0.5) months++;
                    }
                double f = months / 64 / 12;
                byte b = r.Precipitation[2 * (row * 32 + col)];
                double share = SnowyShare(b), error = share - f;
                nodes++;
                if (2 * iceSamples >= 64) { ice++; if (b != 255 || share != 1) wrong++; continue; }
                if (f > 0) seasonal++;
                bool ok = f == 0 ? b == 0 && share == 0 : error >= -1e-3 && error <= 0.0075;
                if (!ok) { wrong++; if (rows.Count < 10) rows.Add(new { row, col, f, b, share }); }
                worst = Math.Max(worst, Math.Abs(error));
            }
        evidence["snowSeasonNodes"] = new Dictionary<string, object> { ["nodes"] = nodes, ["seasonal"] = seasonal, ["iceCap"] = ice, ["wrong"] = wrong, ["worstShareError"] = Math.Round(worst, 5) };
        Check(wrong == 0 && seasonal > 0 && ice > 0, "every weather node's Snowy share of the year equals its FWGC++ snow-season length (" + seasonal + " seasonal nodes, " + ice +
            " ice-cap nodes Snowy all year; worst share error " + Math.Round(worst, 4) + " of a year): the snow byte is the inverse installed formula", rows);
        Check(Convert.ToInt32(r.Record["snowNodes"]) > 0 && Convert.ToInt32(r.Record["rainNodes"]) > 0, "the world has snowy and rainy weather nodes (" + r.Record["snowNodes"] + " and " + r.Record["rainNodes"] + " of 1024)");

        // Rain, one node recomputed independently: the footprint mean of rainfall over its maximum.
        int nodeRow = (int)Math.Round((c.OriginY + c.Height / 2) / nodeStep), nodeCol = (int)Math.Round(c.OriginX / nodeStep) + 5;
        double rain = 0;
        for (int sy = 0; sy < 8; sy++)
            for (int sx = 0; sx < 8; sx++)
            {
                double gc, gr; GeneratedClimateSystem.SourceGrid(c, (nodeCol + (sx + .5) / 8) * nodeStep, (nodeRow + (sy + .5) / 8) * nodeStep, out gc, out gr);
                rain += GeneratedClimateSystem.Bilinear(source.Rainfall, 129, gc, gr) / source.RainfallMax;
            }
        byte expectRain = (byte)Math.Round(255 * Math.Min(1, rain / 64), MidpointRounding.AwayFromZero);
        Check(r.Precipitation[2 * (nodeRow * 32 + nodeCol) + 1] == expectRain, "a weather node's rain recomputes from the source (8 x 8 footprint mean of rainfall over the layer maximum)");
    }

    // ---- MMR-256: the snow texture ----
    private static void SnowTexture(GeneratedClimateSystem.Source source, PeriodicWorldCoordinates c, GeneratedClimateSystem.Result r)
    {
        // Pixels recomputed at the shader's orientation: row 0 at physical y = extent (v = 1 - y / extent), column 0 at x = 0.
        double e = c.Extent / 512; int wrong = 0, checkedPixels = 0;
        for (int row = 3; row < 512; row += 23)
            for (int col = 5; col < 512; col += 29)
            {
                double gc, gr; GeneratedClimateSystem.SourceGrid(c, (col + .5) * e, c.Extent - (row + .5) * e, out gc, out gr);
                byte snow;
                if (GeneratedClimateSystem.Bilinear(source.IceCap, 129, gc, gr) >= 0.5) snow = 255;
                else { int k = source.Snow.Count(m => GeneratedClimateSystem.Bilinear(m, 129, gc, gr) >= 0.5); snow = GeneratedClimateSystem.SnowByte(k / 12.0); }
                byte rain = (byte)Math.Round(255 * Math.Min(1, GeneratedClimateSystem.Bilinear(source.Rainfall, 129, gc, gr) / source.RainfallMax), MidpointRounding.AwayFromZero);
                int i = 4 * (row * 512 + col);
                checkedPixels++;
                if (r.SnowTexture[i] != snow || r.SnowTexture[i + 1] != rain || r.SnowTexture[i + 2] != 0 || r.SnowTexture[i + 3] != 255) wrong++;
            }
        Check(wrong == 0, "snow texture pixels recompute from the source (R the snow byte, G rain, B 0, A 255; row 0 at physical y = extent): " + checkedPixels + " pixels", new { wrong });
        // MMR-247 (acc5): the policy's route is permanentWhite (the engine never found the named texture); the named route stays checked on a
        // copy of the policy with route namedTexture, the fallback on the policy's own result.
        var namedPolicy = GeneratedClimateSystem.Policy.FromBytes(policyBytes); namedPolicy.SnowRoute = GeneratedClimateSystem.Policy.NamedTexture;
        var fallback = GeneratedClimateSystem.Policy.FromBytes(policyBytes); fallback.SnowRoute = GeneratedClimateSystem.Policy.PermanentWhite;
        var rw = r.SnowRoute == GeneratedClimateSystem.Policy.PermanentWhite ? r : GeneratedClimateSystem.Synthesize(source, c, fallback);
        r = r.SnowRoute == GeneratedClimateSystem.Policy.NamedTexture ? r : GeneratedClimateSystem.Synthesize(source, c, namedPolicy);
        // The source's north pole line is physical y = OriginY (y runs south), so it is the texture's bottom rows; land ice there is white all year.
        int northRow = 511 - (int)Math.Floor(c.OriginY / e), southRow = 511 - (int)Math.Ceiling((c.OriginY + c.Height) / e);
        Check(northRow > southRow && r.SnowTextureName == "mnbl_snow_" + r.SnowTextureSha256.Substring(0, 16) + ".png" && r.SnowRoute == GeneratedClimateSystem.Policy.NamedTexture
              && policy.SnowRoute == GeneratedClimateSystem.Policy.PermanentWhite && rw.SnowTextureSha256 == r.SnowTextureSha256,
            "the texture's rows run against physical y (the north pole line at row " + northRow + ", the south at row " + southRow + ") and on the namedTexture route it is named by its hash (" + r.SnowTextureName + "); the policy takes the permanentWhite route (acc5)");
        // MMR-247 (acc4): the engine keeps a texture loaded from a file under the file's name with its extension (acc4 recorded engineName
        // "mnbl_snow_6842edaae0521518.png" while the scene named "mnbl_snow_6842edaae0521518": "Cannot find texture" at every map read). The
        // scene's name is the PNG's file name GeneratedSnowTexture writes and loads, so the two match; an extension-less name is refused.
        string stem = r.SnowTextureName.Substring(0, r.SnowTextureName.Length - GeneratedClimateSystem.SnowTextureExtension.Length);
        bool stemRefused = false;
        try { GeneratedClimateSystem.WithSnowTexture("<scene><terrain enabled=\"true\"></terrain></scene>", stem); } catch (ArgumentException) { stemRefused = true; }
        Check(Path.GetFileName(r.SnowTextureName) == r.SnowTextureName && Path.GetExtension(r.SnowTextureName) == ".png" && GeneratedClimateSystem.IsSnowTextureName(r.SnowTextureName)
              && !GeneratedClimateSystem.IsSnowTextureName(stem) && stemRefused && GeneratedClimateSystem.SnowTextureName("6842edaae0521518" + new string('0', 48)) == "mnbl_snow_6842edaae0521518.png",
            "the snow texture's scene name is its PNG's file name with the extension, the name the engine gives a texture loaded from that file (acc4: engineName mnbl_snow_6842edaae0521518.png); the extension-less name acc4's scene used is refused");
        string scene = "<scene name=\"mnbl_world_test\"><terrain enabled=\"true\" use_dynamic_weather_effects=\"true\" node_size=\"100\"><nodes/></terrain></scene>";
        string named = GeneratedClimateSystem.WithSnowTexture(scene, r.SnowTextureName);
        bool twice = false, bad = false;
        try { GeneratedClimateSystem.WithSnowTexture(named, r.SnowTextureName); } catch (InvalidDataException) { twice = true; }
        try { GeneratedClimateSystem.WithSnowTexture(scene, "main_map_snow_flowmap"); } catch (ArgumentException) { bad = true; }
        var doc = new System.Xml.XmlDocument(); doc.LoadXml(named);
        Check(GeneratedClimateSystem.SnowTextureOf(named) == r.SnowTextureName && GeneratedClimateSystem.SnowTextureOf(scene) == null && twice && bad
              && ((System.Xml.XmlElement)doc.SelectSingleNode("/scene/terrain")).GetAttribute(GeneratedClimateSystem.SnowTextureAttribute) == r.SnowTextureName,
            "the scene's terrain names the world's snow texture once (dynamic_flowmap_texture_name), never a vanilla one; the name reads back");
        Check(rw.SnowTextureName == null && rw.SnowRoute == GeneratedClimateSystem.Policy.PermanentWhite && rw.Identity != r.Identity && rw.PrecipitationSha256 == r.PrecipitationSha256,
            "the permanent-white route names no texture (the scene names none), keeps the gameplay bytes and is a different climate identity");
    }

    private static void ColorGrade(GeneratedClimateSystem.Source source, PeriodicWorldCoordinates c, GeneratedClimateSystem.Result r, GeneratedClimateSystem.Result again, SortedDictionary<string, object> evidence)
    {
        var grades = new HashSet<byte>(new byte[] { GeneratedClimateSystem.GradeDesert, GeneratedClimateSystem.GradeSteppe, GeneratedClimateSystem.GradeCentralEurope, GeneratedClimateSystem.GradeHighlands,
            GeneratedClimateSystem.GradeNormandy, GeneratedClimateSystem.GradeNorth, GeneratedClimateSystem.GradeMediterranean });
        var biomes = source.Biome.Select(b => (int)b).GroupBy(b => b).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => (object)g.Count());
        evidence["sourceBiomes"] = biomes;
        Check(r.ColorGrade.Length == 512 * 512 && r.ColorGrade.All(grades.Contains) && r.ColorGradeSha256 == again.ColorGradeSha256,
            "the colour-grade grid has MapColorGradeManager's layout (512^2 bytes), only installed grade values, and is deterministic", r.Record["colorGrades"]);
        Check(((IDictionary<string, object>)r.Record["colorGrades"]).Count >= 3, "the world shows several climate grades (" + String.Join(", ", ((IDictionary<string, object>)r.Record["colorGrades"]).Keys) + ")");
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
        // Cells recomputed from the source: each 3 x 3 neighbour's grade (nearest cell's biome, grassland by moisture, water and other ids by
        // temperature band), then the mode with the cell's own grade winning ties, else the smallest.
        double step = c.Extent / 512;
        Func<int, int, byte> raw = (row, col) =>
        {
            row = Math.Max(0, Math.Min(511, row)); while (col < 0) col += period; while (col >= 512) col -= period;
            double gc, gr; GeneratedClimateSystem.SourceGrid(c, (col + .5) * step, (row + .5) * step, out gc, out gr);
            int n = 128, sc = ((int)Math.Round(gc, MidpointRounding.AwayFromZero) % n + n) % n, sr = (int)Math.Round(Math.Max(0, Math.Min(n, gr)), MidpointRounding.AwayFromZero);
            int biome = (int)source.Biome[sr * 129 + sc];
            double t = GeneratedClimateSystem.Bilinear(source.Temperature, 129, gc, gr);
            switch (biome)
            {
                case 2: return 20;
                case 3: return (byte)(source.Moisture[sr * 129 + sc] < 0.5 ? 40 : 60);
                case 4: return 60;
                case 5: return 80;
                case 7: return 140;
                case 13: return 100;
                case 1: case 6: case 15: case 16: case 17: return 120;
                default: return (byte)(t < 2 ? 120 : t < 18 ? 100 : 140);
            }
        };
        int checkedCells = 0, wrong = 0, filtered = 0, wet = 0;
        foreach (int row in new[] { (int)Math.Round((c.OriginY + c.Height * .3) / step), (int)Math.Round((c.OriginY + c.Height * .5) / step), (int)Math.Round((c.OriginY + c.Height * .7) / step) })
            for (int col = origin; col < origin + period; col += 7)
            {
                var counts = new Dictionary<byte, int>();
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) { byte g = raw(row + dy, col + dx); int k; counts[g] = counts.TryGetValue(g, out k) ? k + 1 : 1; }
                byte own = raw(row, col);
                int best = counts.Values.Max();
                byte expected = counts[own] == best ? own : counts.Where(kv => kv.Value == best).Min(kv => kv.Key);
                checkedCells++; if (r.ColorGrade[row * 512 + col] != expected) wrong++; if (expected != own) filtered++;
            }
        for (int i = 0; i < r.ColorGrade.Length; i++) if (r.ColorGrade[i] == 60) wet++;
        Check(wrong == 0, "colour-grade cells recompute from the source (nearest cell's biome, grassland split by moisture, water and other ids by temperature band, 3 x 3 mode filter): " + checkedCells + " cells, " + filtered + " changed by the filter", new { wrong });
    }

    // ---- MMR-256: latitude-banded, seed-perturbed winds (RT-18) ----
    private static void Winds(GeneratedClimateSystem.Source source, PeriodicWorldCoordinates c, GeneratedClimateSystem.Result r, SortedDictionary<string, object> evidence)
    {
        evidence["windBands"] = r.Record["windBands"];
        // Without the perturbation, a sample mid-band is its band's direction (north mirrored in the south), at the policy scale.
        var plain = GeneratedClimateSystem.Policy.FromBytes(policyBytes); plain.Amplitude = 0;
        var rp = GeneratedClimateSystem.Synthesize(source, c, plain);
        double windStep = c.Extent / 512; int n = 128, wrongBand = 0, checkedSamples = 0;
        int col0 = (int)Math.Round(c.OriginX / windStep) + 17;
        for (int row = 0; row < 512; row++)
        {
            double gc, gr; GeneratedClimateSystem.SourceGrid(c, (col0 + .5) * windStep, (row + .5) * windStep, out gc, out gr);
            double lat = 90 - 180 * gr / n, a = Math.Abs(lat);
            if (Math.Min(gr, n - gr) < GeneratedClimateSystem.PoleTaperRows) continue;
            double lower = 0; GeneratedClimateSystem.Policy.Band band = null;
            foreach (var b in plain.Bands) { if (a >= lower + plain.BlendDeg && a <= b.ToLatDeg - plain.BlendDeg) band = b; lower = b.ToLatDeg; }
            if (band == null) continue;
            double east = band.East, northward = band.North * Math.Sign(lat);
            int i = 2 * (row * 512 + col0);
            checkedSamples++;
            if (Math.Abs(rp.Wind[i] - GeneratedClimateSystem.WindScale * east) > 1e-5 || Math.Abs(rp.Wind[i + 1] + GeneratedClimateSystem.WindScale * northward) > 1e-5) wrongBand++;
        }
        Check(checkedSamples > 50 && wrongBand == 0, "without the perturbation a mid-band sample is its band's direction at the policy scale, the north component mirrored in the southern hemisphere (map vector (east, -north); " + checkedSamples + " samples)", new { wrongBand });
        // The bands make the wind vary by latitude: neighbouring bands' mean east components have opposite signs in each hemisphere.
        var bands = ((IEnumerable<object>)r.Record["windBands"]).Cast<IDictionary<string, object>>().Where(b => b["meanMapVector"] != null).ToList();
        bool alternate = true;
        foreach (string hemisphere in new[] { "north", "south" })
        {
            var mean = bands.Where(b => (string)b["hemisphere"] == hemisphere).Select(b => ((double[])b["meanMapVector"])[0]).ToList();
            for (int k = 1; k < mean.Count; k++) if (Math.Sign(mean[k]) == Math.Sign(mean[k - 1])) alternate = false;
            if (mean.Count < 2) alternate = false;
        }
        Check(alternate, "the wind varies by latitude: neighbouring bands blow east and west in turn in both hemispheres, not one east-west wind (RT-18)", bands);
        // The perturbation is seeded by the world: the same seed gives the same wind, another seed another; it turns the wind by up to the amplitude.
        var other = new GeneratedClimateSystem.Source { Side = source.Side, Rainfall = source.Rainfall, IceCap = source.IceCap, SnowMax = source.SnowMax, Snow = source.Snow, Biome = source.Biome,
            Temperature = source.Temperature, Moisture = source.Moisture, Sha256 = source.Sha256, Months = source.Months, RainfallMax = source.RainfallMax, Seed = source.Seed + 1 };
        var ro = GeneratedClimateSystem.Synthesize(source, c, policy); var rs = GeneratedClimateSystem.Synthesize(other, c, policy);
        var turn = (IDictionary<string, object>)r.Record["windPerturbationRadians"];
        Check(ro.WindSha256 == r.WindSha256 && rs.WindSha256 != r.WindSha256 && rs.Identity != r.Identity && rp.WindSha256 != r.WindSha256
              && Convert.ToDouble(turn["max"]) <= policy.Amplitude + 1e-9 && Convert.ToDouble(turn["min"]) >= -policy.Amplitude - 1e-9 && Convert.ToDouble(turn["max"]) - Convert.ToDouble(turn["min"]) > policy.Amplitude * .5,
            "the perturbation is the world's (seed " + source.Seed + " repeats, seed " + (source.Seed + 1) + " differs) and turns the wind within +-" + policy.Amplitude + " rad (" + turn["min"] + " to " + turn["max"] + ")");
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
