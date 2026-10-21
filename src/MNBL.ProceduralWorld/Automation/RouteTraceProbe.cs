using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Surface;

namespace MNBL.ProceduralWorld.Automation
{
    // Diagnostic (2026-10-19, seed 43's runtime navigation refusal): one compiled world's route checks whose id contains `routes`, each through
    // the native scene and through the managed stand-in, with both polylines and the terrain type of the face at every engine waypoint
    // (route_trace; bundle=<dir> routes=<id substring>). Prepare -> Install -> Construct as the generation job's validation does; no campaign.
    internal static class RouteTraceProbe
    {
        private static readonly WorldDataBundle.HostProfile Host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", WorldDataBundle.HostCapabilities);

        internal static IEnumerator Run(ProbeContext context)
        {
            context.Outputs["scope"] = "Route trace: the engine's and the stand-in's paths for the named route checks of one compiled world. No campaign.";
            var bundle = WorldDataBundle.Open(context.Str("bundle", ""), Host);
            string filter = context.Str("routes", "land to group");
            var model = NavigationModel.Installed(NavigationModel.InstalledCalibration());
            var token = WorldGenerations.Begin("route trace");
            PreparedSurface prepared = null; Exception error = null;
            var job = Task.Run(() => { try { prepared = RuntimeWorldBackend.Prepare(bundle, token, CancellationToken.None, null); } catch (Exception e) { error = e; } });
            while (!job.IsCompleted) yield return null;
            if (error != null) { WorldGenerations.Retire(token); throw new InvalidOperationException("Preparing the world failed.", error); }
            context.Outputs["worldContentId"] = bundle.WorldContentId; context.Outputs["sceneName"] = prepared.SceneName;
            context.Outputs["install"] = RuntimeWorldBackend.Install(prepared);
            context.Checkpoint();
            var rows = new List<object>(); context.Outputs["routes"] = rows;
            RuntimeWorld world = null;
            try
            {
                world = RuntimeWorldBackend.Construct(prepared.SceneName, token, prepared.Surface, model);
                world.Scene.Tick(0);
                var plan = prepared.NavigationPlan;
                var engine = new SceneNavigationEngine(world);
                var standIn = new MeshNavigationEngine(plan.Build.Mesh, model);
                foreach (var r in plan.Routes.Where(r => r.Id.IndexOf(filter, StringComparison.Ordinal) >= 0).ToList())
                {
                    var e = NavigationValidator.EvaluateRoute(plan, engine, r, null);
                    var m = NavigationValidator.EvaluateRoute(plan, standIn, r, null);
                    rows.Add(new Dictionary<string, object> {
                        ["id"] = r.Id, ["type"] = r.Type, ["agrees"] = e.Agrees, ["problems"] = e.Problems,
                        ["engineLength"] = Round(e.Length), ["standInLength"] = Round(m.Length), ["staticLength"] = Round(r.StaticLength), ["straight"] = Round(e.Straight),
                        ["engineDistance"] = Round(e.Distance), ["engineSwitches"] = e.Switches,
                        ["enginePoints"] = Points(e.Points), ["standInPoints"] = Points(m.Points), ["engineTypes"] = Types(engine, e.Points) });
                    context.Checkpoint();
                    yield return null;
                }
            }
            finally
            {
                if (world != null) world.Dispose(); else WorldGenerations.Retire(token);
            }
            context.Outputs["completed"] = true;
        }

        private static object Round(double v) { return double.IsNaN(v) ? null : (object)Math.Round(v, 3); }

        private static List<object> Points(List<double[]> points)
        {
            return points == null ? null : points.Select(p => (object)new[] { Math.Round(p[0], 3), Math.Round(p[1], 3) }).ToList();
        }

        // The terrain type of the sea face (else the land face) at each waypoint and at each segment's middle.
        private static List<object> Types(INavigationEngine engine, List<double[]> points)
        {
            if (points == null) return null;
            var types = new List<object>();
            Func<double, double, string> at = (x, y) =>
            {
                int face, group, island;
                if (engine.Face(x, y, false, out face, out group, out island) || engine.Face(x, y, true, out face, out group, out island)) return TerrainTypes.Name(group) ?? group.ToString();
                return "none";
            };
            for (int i = 0; i < points.Count; i++)
            {
                types.Add(at(points[i][0], points[i][1]));
                if (i + 1 < points.Count) types.Add("~" + at((points[i][0] + points[i + 1][0]) / 2, (points[i][1] + points[i + 1][1]) / 2));
            }
            return types;
        }
    }
}
