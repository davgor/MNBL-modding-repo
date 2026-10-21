// The navigation half of a seed sweep (tools/Test-NavigationSweep.ps1): each given world bundle through the runtime navigation build, its
// static plan and the validator over the managed stand-in engine, as activation runs them (RuntimeNavigationBuilder, NavigationPlan,
// NavigationValidator), with no game process. One JSON line per bundle: the build's diagnostics, the plan's, the validator's verdict, and the
// host-check and transition flags the playability validator resolves from that report (RuntimePlayabilityValidator.ResolveFlags), and
// the settlement placement activation resolves right after the plan (SettlementLookPlan: the looks, then SettlementPositionResolver over the
// navigation, as RuntimeWorldBackend.Prepare does; 2026-10-19: seeds 56 and 67 passed the sweep and were refused there in game, settlement.port_far).
// With --placement-only (before the bundles) the validator and the flags are skipped.
// Usage: NavigationSweep REPO GAME_ROOT OUT_JSONL [--placement-only] BUNDLE...
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
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", WorldDataBundle.HostCapabilities);
        var policy = NavigationPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, NavigationPolicy.PolicyPath)));
        var calibration = (IDictionary<string, object>)Serializer.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen/calibration/naval-transitions/naval-transitions.json")).TrimStart('﻿'));
        NavigationModel model;
        try { model = NavigationModel.Installed(calibration); } catch (Exception) { model = NavigationModel.FromCalibration(calibration); }
        var placementPolicy = SettlementPlacementPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, SettlementPlacementPolicy.PolicyPath)), File.ReadAllBytes(Path.Combine(repo, SettlementPlacementPolicy.NavalPath)));
        // MMR-258: the placement at the looks' offsets, as RuntimeWorldBackend.Prepare places it (the installed catalog, once).
        var looksPolicy = MNBL.ProceduralWorld.World.SettlementLooksPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, MNBL.ProceduralWorld.World.SettlementLooksPolicy.PolicyPath)));
        byte[] prototypesBytes = File.ReadAllBytes(Path.Combine(repo, "worldgen/templates/settlements/prototypes.json")), economyBytes = File.ReadAllBytes(Path.Combine(repo, "worldgen/policies/economy.json"));
        var pinDefaults = new HashSet<string>(((object[])((IDictionary<string, object>)Serializer.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen/policies/campaign-catalog.json")).TrimStart('﻿')))["defaults"]).Cast<string>(), StringComparer.Ordinal);
        var missionScenes = MNBL.ProceduralWorld.Missions.MissionScenes.FromBytes(File.ReadAllBytes(Path.Combine(repo, "worldgen/policies/mission-scenes.json")));
        var lookCatalog = SettlementLookCatalog.FromInstalled(File.ReadAllBytes(Path.Combine(gameRoot, "Modules", "NavalDLC", "SceneObj", "Main_map", "scene.xscene")),
            SettlementLookCatalog.PrefabResolver(new[] { "NavalDLC", "SandBox", "Native" }.Select(m => Path.Combine(gameRoot, "Modules", m, "Prefabs"))), prototypesBytes, looksPolicy);
        bool placementOnly = args[3] == "--placement-only";
        var paths = args.Skip(placementOnly ? 4 : 3).ToList();
        int refused = 0;
        using (var output = new StreamWriter(args[2], false, new UTF8Encoding(false)))
            foreach (var path in paths)
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
                    var lookInputs = SettlementLookPlan.ForCampaign(bundle.ReadVerified("compiled/campaign/campaign.json"), bundle.ReadVerified("compiled/travel/travel-graph.json"), bundle.ReadVerified("compiled/travel/ports.json"),
                        economyBytes, prototypesBytes, pinDefaults.Contains, looksPolicy);
                    lookInputs.Catalog = lookCatalog; lookInputs.Conforms = (prototype, kind, port) => missionScenes.PrototypeProblems(prototype, kind, port).Count == 0;
                    FrozenLooks looks;
                    var layout = SettlementLookPlan.Resolve(SettlementPositionResolver.SitesFromBundle(input, bundle.ReadVerified("compiled/campaign/campaign.json"), bundle.ReadVerified("compiled/travel/ports.json")),
                        plan, new NavigationGround(build), placementPolicy, lookInputs, out looks);
                    row["placementActivatable"] = layout.Activatable; row["layoutSha256"] = layout.Sha256;
                    row["placementProblems"] = layout.Problems.Where(p => p.Refusal).Take(12).Select(p => (object)p.ToString()).ToList();
                    var refusedIds = new HashSet<string>(layout.Problems.Where(p => p.Refusal).Select(p => p.Subject), StringComparer.Ordinal);
                    row["placementRefused"] = ((System.Collections.IEnumerable)layout.Report(new NavigationGround(build))["settlements"]).Cast<Dictionary<string, object>>()
                        .Where(s => refusedIds.Contains((string)s["id"])).Take(12).Select(s => (object)new Dictionary<string, object> { ["id"] = s["id"], ["centre"] = s["centre"], ["gate"] = s["gate"],
                            ["gateRule"] = s["gateRule"], ["port"] = s["port"], ["portEntrance"] = s["portEntrance"], ["provenance"] = s["provenance"] }).ToList();
                    if (placementOnly) { row["activatable"] = plan.Activatable && layout.Activatable; if (!(bool)row["activatable"] || build.Diagnostics.Count > 0) refused++; goto written; }
                    var report = NavigationValidator.Run(plan, new MeshNavigationEngine(build.Mesh, model), "sweep", null);
                    row["activatable"] = report["activatable"];
                    row["failures"] = ((System.Collections.IEnumerable)report["failures"]).Cast<Dictionary<string, object>>().Take(12).Select(f => (object)(f["subject"] + ": " + f["detail"])).ToList();
                    // The host checks and transitions the activation's playability report resolves from this navigation report (the settlement
                    // entity checks and semantic routes need the game: taken as passed here, so only the navigation-decided flags count).
                    var flags = RuntimePlayabilityValidator.ResolveFlags(bundle.ReadVerified("compiled/travel/travel-graph.json"), bundle.ReadVerified("compiled/travel/ports.json"), new string[0], report,
                        new Dictionary<string, object> { ["passed"] = true }, true, 0);
                    var failedFlags = flags.Where(f => (string)f["status"] != "resolved" && ((string)f["kind"] == "hostCheck" || (string)f["kind"] == "transition")).ToList();
                    row["flagsFailed"] = failedFlags.Take(12).Select(f => (object)(f["kind"] + " " + f["check"] + " " + f["subject"] + ": " + f["by"])).ToList();
                    if (!(bool)report["activatable"] || build.Diagnostics.Count > 0 || failedFlags.Count > 0 || !layout.Activatable) refused++;
                }
                catch (Exception e) { row["error"] = e.GetType().Name + ": " + e.Message; row["stack"] = e.StackTrace; refused++; }
                written:
                output.WriteLine(Serializer.Serialize(row));
                output.Flush();
                Console.WriteLine(row["bundle"] + " " + (row.ContainsKey("error") ? "error " + row["error"] : "activatable " + row["activatable"] + (row.ContainsKey("flagsFailed") && ((List<object>)row["flagsFailed"]).Count > 0 ? ", flags failed " + ((List<object>)row["flagsFailed"]).Count : "")
                    + (Equals(row["placementActivatable"], false) ? ", placement refused: " + ((List<object>)row["placementProblems"])[0] : "")));
            }
        Console.WriteLine("navigation sweep: " + (paths.Count - refused) + "/" + paths.Count + " activatable");
        return refused == 0 ? 0 : 1;
    }
}
