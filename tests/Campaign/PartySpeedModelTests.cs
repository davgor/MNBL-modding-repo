using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.World;

// MMR-254: the one party speed model of a generated world. Constructed offline as CampaignGameStarter.AddModel<PartySpeedModel> chains it
// (each model's Initialize takes the previous one: the installed DefaultPartySpeedCalculatingModel, War Sails' NavalDLCPartySpeedCalculationModel
// over it, GeneratedPartySpeedModel over that), the chain is Road -> Naval -> Default as roads.json speed.chain says; its terms are the policy's
// factors (Snow, Swamp, road on land only, the installed ford factor cancelled on a road deck); and the road cells of a compiled world read
// from cells.bin v2 match the travel graph. Usage: PartySpeedModelTests REPO GAME_ROOT BUNDLE
internal static class PartySpeedModelTests
{
    private static int checks, failures;
    private static void Check(bool ok, string what)
    {
        checks++;
        if (!ok) failures++;
        Console.WriteLine((ok ? "ok    " : "FAIL  ") + what);
    }

    private static int Main(string[] args)
    {
        string gameRoot = args[1];
        AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (var dir in new[] { "bin/Win64_Shipping_Client", "Modules/NavalDLC/bin/Win64_Shipping_Client", "Modules/SandBox/bin/Win64_Shipping_Client" })
            {
                string path = Path.Combine(gameRoot, dir, name);
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        try { Run(args[0], args[2]); }
        catch (Exception error) { Check(false, "unexpected " + error); }
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static object BaseOf(object model)
    {
        var p = model.GetType().GetProperty("BaseModel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return p == null ? null : p.GetValue(model);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run(string repo, string bundle)
    {
        var policy = RoadsPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, RoadsPolicy.PolicyPath)));
        // ---- the chain ----
        var naval = Type.GetType("NavalDLC.GameComponents.NavalDLCPartySpeedCalculationModel, NavalDLC", true);
        // The installed constructors read game texts (GameTexts.FindText needs a running game); the chain needs only the instances.
        var installed = (TaleWorlds.CampaignSystem.GameComponents.DefaultPartySpeedCalculatingModel)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(TaleWorlds.CampaignSystem.GameComponents.DefaultPartySpeedCalculatingModel));
        var war = (TaleWorlds.CampaignSystem.ComponentInterfaces.PartySpeedModel)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(naval);
        war.Initialize(installed);
        var model = new GeneratedPartySpeedModel(policy);
        model.Initialize(war);
        var chain = new List<object>();
        for (object m = model; m != null && chain.Count < 8; m = BaseOf(m)) chain.Add(m);
        var names = chain.Select(m => m.GetType().FullName).ToList();
        var expected = new Dictionary<string, string> { ["Road"] = typeof(GeneratedPartySpeedModel).FullName, ["Naval"] = naval.FullName, ["Default"] = typeof(TaleWorlds.CampaignSystem.GameComponents.DefaultPartySpeedCalculatingModel).FullName };
        Check(policy.SpeedChain.Select(key => expected[key]).SequenceEqual(names) && ReferenceEquals(model.Installed, war),
              "the speed chain is " + String.Join(" -> ", names) + ", roads.json's " + String.Join(" -> ", policy.SpeedChain) + " (AddModel<PartySpeedModel> over War Sails' model; no Harmony)");
        Check(model.BaseSpeed == installed.BaseSpeed && model.MinimumSpeed == installed.MinimumSpeed, "base and minimum speed are the installed chain's (" + model.BaseSpeed + ", " + model.MinimumSpeed + ")");
        // The engine computes party speeds on TWParallel workers (CampaignTickCacheDataStore's parallel movement ticks): the model keeps no
        // instance arrays, and every writable static array is [ThreadStatic], so concurrent CalculateFinalSpeed calls never share scratch terms.
        var fields = typeof(GeneratedPartySpeedModel).GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        var shared = fields.Where(fi => fi.FieldType.IsArray && (!fi.IsStatic || !fi.IsInitOnly && !fi.IsDefined(typeof(ThreadStaticAttribute), false))).Select(fi => fi.Name).ToList();
        var perThread = fields.Where(fi => fi.IsStatic && fi.IsDefined(typeof(ThreadStaticAttribute), false)).Select(fi => fi.Name).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Check(shared.Count == 0 && perThread.SequenceEqual(new[] { "tFactors", "tKinds" }),
              "the speed model's scratch terms are per thread (" + String.Join(", ", perThread) + " [ThreadStatic]); no shared array fields" + (shared.Count == 0 ? "" : ": " + String.Join(", ", shared)));
        float ford = GeneratedPartySpeedModel.InstalledFordFactor();
        Check(Math.Abs(ford - (-0.3f)) < 1e-6, "the installed ford factor (DefaultPartySpeedCalculatingModel.MovingAtWaterEffect) is " + ford.ToString(CultureInfo.InvariantCulture));

        // ---- the terms ----
        var terms = model.Terms;
        Func<string, int> id = n => (int)Enum.Parse(typeof(TaleWorlds.Core.TerrainType), n, false);
        var f = new float[3]; var k = new int[3];
        Func<string, bool, bool, string> describe = (type, sea, road) =>
        {
            int n = terms.Terms(id(type), sea, road, f, k);
            return String.Join(" ", Enumerable.Range(0, n).Select(i => RoadSpeed.TermNames[k[i]] + " " + f[i].ToString("0.###", CultureInfo.InvariantCulture)));
        };
        var cases = new[] {
            new { Type = "Snow", Sea = false, Road = false, Expect = "terrain -0.15" },
            new { Type = "Swamp", Sea = false, Road = false, Expect = "terrain -0.2" },
            new { Type = "Plain", Sea = false, Road = false, Expect = "" },
            new { Type = "Forest", Sea = false, Road = false, Expect = "" },
            new { Type = "Desert", Sea = false, Road = false, Expect = "" },
            new { Type = "Plain", Sea = false, Road = true, Expect = "road 0.2" },
            new { Type = "Snow", Sea = false, Road = true, Expect = "terrain -0.15 road 0.2" },
            new { Type = "Swamp", Sea = false, Road = true, Expect = "terrain -0.2 road 0.2" },
            new { Type = "Bridge", Sea = false, Road = true, Expect = "road 0.2 ford 0.3" },
            new { Type = "Bridge", Sea = false, Road = false, Expect = "" },
            new { Type = "CoastalSea", Sea = true, Road = true, Expect = "" },
            new { Type = "River", Sea = true, Road = false, Expect = "" },
            new { Type = "Snow", Sea = true, Road = false, Expect = "" } };
        var wrong = cases.Where(c => describe(c.Type, c.Sea, c.Road) != c.Expect).Select(c => c.Type + (c.Sea ? " at sea" : "") + (c.Road ? " on a road" : "") + ": " + describe(c.Type, c.Sea, c.Road) + " (want " + c.Expect + ")").ToList();
        Check(wrong.Count == 0, "terms: Snow -0.15 and Swamp -0.2 on land, road +0.2 on a road cell when not at sea, the -0.3 ford factor cancelled on a road deck (Bridge), nothing at sea" +
              (wrong.Count == 0 ? "" : ": " + String.Join("; ", wrong)));
        Check(terms.RoadFactor == (float)policy.RoadFactor && terms.TerrainFactor(id("Snow")) == (float)policy.TerrainFactors["Snow"] && terms.TerrainFactor(id("Swamp")) == (float)policy.TerrainFactors["Swamp"] &&
              !terms.RoadAtSea && terms.CancelFordOnDecks && terms.FordCancel == -ford,
              "every factor is roads.json's (road " + policy.RoadFactor + ", Snow " + policy.TerrainFactors["Snow"] + ", Swamp " + policy.TerrainFactors["Swamp"] + ", roadAtSea " + policy.RoadAtSea + ", cancelFordOnDecks " + policy.CancelFordOnDecks + ")");
        var doubled = RoadsPolicy.FromBytes(new System.Text.UTF8Encoding(false).GetBytes(File.ReadAllText(Path.Combine(repo, RoadsPolicy.PolicyPath)).Replace("\"road\": 0.2", "\"road\": 0.4").Replace("\"Snow\": -0.15", "\"Snow\": -0.3")));
        var other = new RoadSpeed(doubled, ford, id, 25);
        int m2 = other.Terms(id("Snow"), false, true, f, k);
        Check(m2 == 2 && f[0] == -0.3f && f[1] == 0.4f, "a tuned policy changes the terms (Snow -0.3, road 0.4): the factors are read, not built in");

        // ---- a compiled world's road cells (cells.bin v2) ----
        var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        var graph = (IDictionary<string, object>)json.DeserializeObject(File.ReadAllText(Path.Combine(bundle, "compiled", "travel", "travel-graph.json")));
        var grid = (IDictionary<string, object>)graph["grid"];
        var bin = File.ReadAllBytes(Path.Combine(bundle, "compiled", "travel", "cells.bin"));
        var cells = RoadSpeed.Cells.FromGrid(grid, bin);
        int recorded = Convert.ToInt32(grid["roadCells"], CultureInfo.InvariantCulture);
        // Every road cell of every road piece reads as a road at its centre (physical: + strip, + pole margin) and at its seam image.
        int pieceCells = 0, misses = 0, imageMisses = 0;
        double width = cells.Cols * cells.Spacing;
        foreach (IDictionary<string, object> road in (object[])graph["roads"])
            foreach (IDictionary<string, object> piece in (object[])road["pieces"])
                foreach (object[] cell in (object[])piece["path"])
                {
                    int r = Convert.ToInt32(cell[0], CultureInfo.InvariantCulture), c = Convert.ToInt32(cell[1], CultureInfo.InvariantCulture);
                    double x = cells.OriginX + (c + .5) * cells.Spacing, y = cells.OriginY + (r + .5) * cells.Spacing;
                    pieceCells++;
                    if (!cells.At(x, y)) misses++;
                    if (!cells.At(x + (x - cells.OriginX < width / 2 ? width : -width), y)) imageMisses++;
                }
        Check(cells.RoadCells == recorded && recorded > 0 && pieceCells > 0 && misses == 0 && imageMisses == 0 && !cells.At(cells.OriginX + 1, cells.OriginY - 1),
              "the world's road cells (" + cells.RoadCells + " in cells.bin v2, " + recorded + " in travel-graph.json grid.roadCells) cover every road piece cell (" + pieceCells + ") at its centre and its seam image; beyond the pole line is no road");
    }
}
