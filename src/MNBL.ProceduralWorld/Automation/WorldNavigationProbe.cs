using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.Engine;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-38: production navigation on compiled worlds through the engine (world_navigation; bundleA=<dir> bundleB=<dir>), no campaign:
    //   1. static faults on world A (a village in the open sea, a village on another land component, the joining crossings dropped):
    //      Prepare refuses each with source-id diagnostics; nothing is encoded or installed;
    //   2. the navigation fixture (world A's terrain, designed travel cells: the MMR-17 barrier, lake and channel cases, three bridges over
    //      under-bridge faces, tile lines and the periodic seam), the fixture and world A each with Bridge faces disabled in the loaded scene
    //      (the engine faults), world A, world B and world A again (warm), each: Prepare (worker) -> Install -> Construct (the installed
    //      MapScene.Load navigation setup before the read) -> NavigationValidator over the native scene (engine face table, face checks, the
    //      route matrix in both directions for Default, Naval and All, islands) -> NavigationGate: a production report is persisted and
    //      required from the store; a faulted report is checked in memory (refused) and never persisted -> dispose. The warm world's semantic
    //      report must equal the cold one's.
    internal static class WorldNavigationProbe
    {
        private static readonly WorldDataBundle.HostProfile Host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", WorldDataBundle.HostCapabilities);

        internal static IEnumerator Run(ProbeContext context)
        {
            context.Outputs["scope"] = "Production campaign navigation (MMR-38) on compiled world bundles through native scene calls: static refusals, the navigation fixture, the engine fault, worlds A and B, warm A. No campaign: party movement on a compiled world is MMR-45's activation; SetSailAtPosition and GetPortToGateDistanceForSettlement are campaign calls (MMR-40/41).";
            context.Outputs["engineThread"] = EngineThread.Id;
            context.Outputs["probeThread"] = Thread.CurrentThread.ManagedThreadId;
            var bundleA = WorldDataBundle.Open(context.Str("bundleA", ""), Host);
            var bundleB = WorldDataBundle.Open(context.Str("bundleB", ""), Host);
            if (bundleA.WorldContentId == bundleB.WorldContentId) throw new ArgumentException("Two different compiled worlds are required.");
            var calibration = NavigationModel.InstalledCalibration();
            var model = NavigationModel.Installed(calibration);
            var observed = NavigationModel.FromCalibration(calibration);
            context.Outputs["model"] = model.Record();
            context.Outputs["modelEqualsCalibration"] = model.SameAs(observed) && model.SameRegions(observed.RegionMap);
            string policySha = NavigationPolicy.Installed().Sha256;
            context.Outputs["policySha256"] = policySha;
            context.Outputs["validatorVersion"] = NavigationValidator.Version;
            context.Outputs["sceneStoreBefore"] = Directory.GetDirectories(RuntimeWorldBackend.SceneStore()).Select(Path.GetFileName).Where(SceneNames.IsWorldScene).ToList();

            // 1. Static faults: refused by Prepare, before any encoding.
            var faults = new List<object>(); context.Outputs["staticFaults"] = faults;
            foreach (var fault in NavigationVariants.Faults)
            {
                var token = WorldGenerations.Begin("navigation " + fault);
                Exception error = null; PreparedSurface prepared = null;
                var job = Task.Run(() => { try { prepared = RuntimeWorldBackend.Prepare(bundleA, token, CancellationToken.None, fault); } catch (Exception e) { error = e; } });
                while (!job.IsCompleted) yield return null;
                if (prepared != null) prepared.Discard();
                WorldGenerations.Retire(token);
                var refused = error as NavigationRefusedException;
                faults.Add(new Dictionary<string, object> { ["variant"] = fault, ["refused"] = refused != null, ["installed"] = prepared != null,
                    ["error"] = error == null ? null : error.GetType().Name + ": " + error.Message,
                    ["diagnostics"] = refused == null ? null : refused.Diagnostics.Select(d => d.Record()).ToList() });
                context.Checkpoint();
            }

            // 2. Worlds through the engine.
            var worlds = new List<object>(); context.Outputs["worlds"] = worlds;
            string coldReportA = null;
            foreach (var step in new[] { "fixture", "fixture (engine fault: Bridge faces disabled)", "A", "A (engine fault: Bridge faces disabled)", "B", "A (warm)" })
            {
                var bundle = step == "B" ? bundleB : bundleA;
                string variant = step.StartsWith("fixture", StringComparison.Ordinal) ? NavigationFixtures.Variant : null;
                bool fault = step.Contains("engine fault");
                var token = WorldGenerations.Begin("navigation " + step);
                PreparedSurface prepared = null; Exception prepareError = null;
                var job = Task.Run(() => { try { prepared = RuntimeWorldBackend.Prepare(bundle, token, CancellationToken.None, variant); } catch (Exception e) { prepareError = e; } });
                while (!job.IsCompleted) yield return null;
                if (prepareError != null) throw new InvalidOperationException("Preparing " + step + " failed.", prepareError);
                var record = new Dictionary<string, object> { ["label"] = step, ["worldContentId"] = bundle.WorldContentId, ["variant"] = prepared.Navigation.Input.Variant, ["generation"] = token.Id,
                    ["sceneName"] = prepared.SceneName, ["navmeshSha256"] = prepared.Navigation.Sha256, ["installedAlready"] = prepared.InstalledAlready, ["preparedOnThread"] = prepared.ThreadId,
                    ["prepareSeconds"] = prepared.EncodeSeconds, ["build"] = prepared.Navigation.Record() };
                worlds.Add(record); context.Checkpoint();
                record["install"] = RuntimeWorldBackend.Install(prepared);
                context.Stage("navigation_" + step + "_construct");
                RuntimeWorld world = null;
                try
                {
                    world = RuntimeWorldBackend.Construct(prepared.SceneName, token, prepared.Surface, model);
                    world.Scene.Tick(0);
                    record["constructed"] = world.Record();
                    record["engineCrc"] = world.NavCrc();
                    var injected = new List<string>();
                    if (fault) { record["disabledBridgeFaces"] = world.DisableNavFaces(prepared.Navigation.FaceGroups["bridge"]); injected.Add("Bridge faces disabled after the read (Scene.SetAbilityOfFacesWithId)"); }
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var report = NavigationValidator.Run(prepared.NavigationPlan, new SceneNavigationEngine(world), prepared.SceneName, s => context.Stage("navigation_" + step + ":" + s), injected);
                    record["validationSeconds"] = watch.Elapsed.TotalSeconds;
                    record["report"] = report;
                    record["activatable"] = report["activatable"]; record["reportSha256"] = report["reportSha256"];
                    string navmesh = Path.Combine(RuntimeWorldBackend.SceneFolder(prepared.SceneName), "navmesh.bin");
                    try
                    {
                        if (fault) NavigationGate.Check(report, prepared.SceneName, navmesh, policySha);
                        else { record["reportPath"] = NavigationGate.Persist(SubModule.Store, report); NavigationGate.Require(SubModule.Store, prepared.SceneName, navmesh, policySha); }
                        record["gate"] = "activation allowed";
                    }
                    catch (NavigationRefusedException refusal) { record["gate"] = "activation refused"; record["gateDiagnostics"] = refusal.Diagnostics.Select(d => d.Record()).ToList(); }
                    record["persisted"] = !fault;
                    // MMR-41: the semantic route set and the pending-runtime flags on every compiled world (the faulted one in memory only: its
                    // bridge routes must fail and its report must not be activatable).
                    if (variant == null)
                    {
                        context.Stage("navigation_" + step + ":playability");
                        if (!fault) RuntimePlayabilityValidator.Delete(SubModule.Store, prepared.SceneName);
                        var playWatch = System.Diagnostics.Stopwatch.StartNew();
                        var settlementChecks = SettlementEntityChecks.Run(world, prepared);
                        var semantic = RuntimePlayabilityValidator.RouteSet(PlayabilityWorld.FromBundle(prepared.NavigationPlan.Build.Input, bundle.ReadVerified("compiled/campaign/campaign.json"), prepared.Settlements, prepared.SettlementGround));
                        var playRows = new List<Dictionary<string, object>>();
                        foreach (var s in RuntimePlayabilityValidator.Steps(prepared.NavigationPlan, new SceneNavigationEngine(world), semantic, playRows)) { }
                        int playFailed = playRows.Count(r => !Equals(r["agrees"], true));
                        var flags = RuntimePlayabilityValidator.ResolveFlags(bundle.ReadVerified("compiled/travel/travel-graph.json"), bundle.ReadVerified("compiled/travel/ports.json"),
                            RuntimePlayabilityValidator.PendingRuntime(MNBL.ProceduralWorld.CampaignRegistry.CampaignGraphValidator.InstalledPolicy()), report, settlementChecks, playFailed == 0, playFailed);
                        var notFlags = RuntimePlayabilityValidator.NotFlags(bundle.ReadVerified("compiled/validation.json"), RuntimePlayabilityValidator.Embedded("product-policy.json"));
                        var playability = RuntimePlayabilityValidator.Report(bundle.WorldContentId, prepared.SceneName, prepared.Settlements, report, settlementChecks, semantic, playRows, flags, "scene (inspection world)", injected, notFlags);
                        record["playabilitySeconds"] = playWatch.Elapsed.TotalSeconds;
                        record["playability"] = playability;
                        if (!fault) { record["playabilityPath"] = RuntimePlayabilityValidator.Persist(SubModule.Store, playability); }
                        try { if (!fault) RuntimePlayabilityValidator.Require(SubModule.Store, prepared.SceneName, NavigationGate.ReportPath(SubModule.Store, prepared.SceneName), RuntimeWorldBackend.SettlementReportPath(prepared.SceneName)); record["playabilityGate"] = fault ? (Equals(playability["activatable"], true) ? "activatable (in memory)" : "not activatable (in memory)") : "activation allowed"; }
                        catch (InvalidOperationException refusal) { record["playabilityGate"] = "activation refused: " + refusal.Message; }
                    }
                    if (step == "A") coldReportA = (string)report["reportSha256"];
                    if (step == "A (warm)") record["sameReportAsColdA"] = coldReportA != null && coldReportA == (string)report["reportSha256"];
                }
                finally
                {
                    context.Stage("navigation_" + step + "_dispose");
                    if (world != null) { world.Dispose(); record["afterDispose"] = world.Record(); }
                    else WorldGenerations.Retire(token);
                }
                context.Checkpoint();
                double released = context.Seconds; while (context.Seconds - released < 1) yield return null;
            }
            context.Outputs["sceneStoreAfter"] = Directory.GetDirectories(RuntimeWorldBackend.SceneStore()).Select(Path.GetFileName).Where(SceneNames.IsWorldScene).ToList();
            context.Outputs["jobsLeft"] = Directory.Exists(Path.Combine(SubModule.Store, "world-surfaces")) ? Directory.GetDirectories(Path.Combine(SubModule.Store, "world-surfaces")).Length : 0;
            context.Outputs["generations"] = WorldGenerations.Log;
            context.Outputs["completed"] = true;
            context.Checkpoint();
        }
    }
}
