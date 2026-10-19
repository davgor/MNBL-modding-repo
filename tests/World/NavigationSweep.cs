// The navigation half of a seed sweep (tools/Test-NavigationSweep.ps1): each given world bundle through the runtime navigation build, its
// static plan and the validator over the managed stand-in engine, as activation runs them (RuntimeNavigationBuilder, NavigationPlan,
// NavigationValidator), with no game process. One JSON line per bundle: the build's diagnostics, the plan's, the validator's verdict, and the
// host-check and transition flags the playability validator resolves from that report (RuntimePlayabilityValidator.ResolveFlags).
// Usage: NavigationSweep REPO GAME_ROOT OUT_JSONL BUNDLE...
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Surface;

internal static class NavigationSweep
{
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static string gameRoot;

    private static int Main(string[] args)
    {
        if (args.Length < 4) { Console.Error.WriteLine("usage: NavigationSweep REPO GAME_ROOT OUT_JSONL BUNDLE..."); return 2; }
        gameRoot = args[1];
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
        return Run(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        string repo = args[0];
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", new[] { "R01", "R02", "R03", "R04", "R05", "R06", "R07", "R08", "R09", "R10" });
        var policy = NavigationPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, NavigationPolicy.PolicyPath)));
        var calibration = (IDictionary<string, object>)Serializer.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen/calibration/naval-transitions/naval-transitions.json")).TrimStart('﻿'));
        NavigationModel model;
        try { model = NavigationModel.Installed(calibration); } catch (Exception) { model = NavigationModel.FromCalibration(calibration); }
        int refused = 0;
        using (var output = new StreamWriter(args[2], false, new UTF8Encoding(false)))
            foreach (var path in args.Skip(3))
            {
                var row = new Dictionary<string, object> { ["bundle"] = Path.GetFileName(path) };
                try
                {
                    var bundle = WorldDataBundle.Open(path, host);
                    row["worldContentId"] = bundle.WorldContentId;
                    var input = NavigationInput.FromBundle(bundle, WorldSurface.FromBundle(bundle), policy);
                    var build = RuntimeNavigationBuilder.Build(input, policy);
                    row["buildDiagnostics"] = build.Diagnostics.Select(d => (object)(d.Code + " " + d.Subject)).Take(12).ToList();
                    var plan = NavigationPlan.Create(build, model);
                    row["planActivatable"] = plan.Activatable;
                    row["planDiagnostics"] = plan.Diagnostics.Select(d => (object)(d.Code + " " + d.Subject + ": " + d.Detail)).Take(12).ToList();
                    var report = NavigationValidator.Run(plan, new MeshNavigationEngine(build.Mesh, model), "sweep", null);
                    row["activatable"] = report["activatable"];
                    row["failures"] = ((System.Collections.IEnumerable)report["failures"]).Cast<Dictionary<string, object>>().Take(12).Select(f => (object)(f["subject"] + ": " + f["detail"])).ToList();
                    // The host checks and transitions the activation's playability report resolves from this navigation report (the settlement
                    // entity checks and semantic routes need the game: taken as passed here, so only the navigation-decided flags count).
                    var flags = RuntimePlayabilityValidator.ResolveFlags(bundle.ReadVerified("compiled/travel/travel-graph.json"), bundle.ReadVerified("compiled/travel/ports.json"), new string[0], report,
                        new Dictionary<string, object> { ["passed"] = true }, true, 0);
                    var failedFlags = flags.Where(f => (string)f["status"] != "resolved" && ((string)f["kind"] == "hostCheck" || (string)f["kind"] == "transition")).ToList();
                    row["flagsFailed"] = failedFlags.Take(12).Select(f => (object)(f["kind"] + " " + f["check"] + " " + f["subject"] + ": " + f["by"])).ToList();
                    if (!(bool)report["activatable"] || build.Diagnostics.Count > 0 || failedFlags.Count > 0) refused++;
                }
                catch (Exception e) { row["error"] = e.GetType().Name + ": " + e.Message; row["stack"] = e.StackTrace; refused++; }
                output.WriteLine(Serializer.Serialize(row));
                output.Flush();
                Console.WriteLine(row["bundle"] + " " + (row.ContainsKey("error") ? "error " + row["error"] : "activatable " + row["activatable"] + (row.ContainsKey("flagsFailed") && ((List<object>)row["flagsFailed"]).Count > 0 ? ", flags failed " + ((List<object>)row["flagsFailed"]).Count : "")));
            }
        Console.WriteLine("navigation sweep: " + (args.Length - 3 - refused) + "/" + (args.Length - 3) + " activatable");
        return refused == 0 ? 0 : 1;
    }
}
