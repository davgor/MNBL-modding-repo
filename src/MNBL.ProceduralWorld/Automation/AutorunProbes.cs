using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.Diagnostics;
using TaleWorlds.ScreenSystem;

namespace MNBL.ProceduralWorld.Automation
{
    // Probe ids accepted by the autorun request. Each probe is an iterator: one MoveNext per application tick.
    internal static class AutorunProbes
    {
        public static IEnumerator Create(string id, ProbeContext context)
        {
            string name = id, parameter = null;
            int colon = id.IndexOf(':');
            if (colon >= 0) { name = id.Substring(0, colon); parameter = id.Substring(colon + 1); }
            switch (name)
            {
                case "noop": return Noop(context);
                case "capabilities": return CapabilityProbe.Run(context);
                case "generation_menu": return GenerationMenuProbe.Run(context);
                case "generation_job": return GenerationJobProbe.Run(context);
                case "world_activation": return WorldActivationProbe.Run(context);
                case "scene_query": return SceneQueryProbe.Run(context);
                case "scene_sequence": return SceneSequenceProbe.Run(context);
                case "native_render": return NativeTerrainScreen.Run(context);
                case "runtime_surface": return RuntimeSurfaceSequenceProbe.Run(context);
                case "world_surface": return WorldSurfaceProbe.Run(context);
                case "world_navigation": return WorldNavigationProbe.Run(context);
                case "world_setup": return WorldSetupProbe.Run(context);
                case "new_campaign_flow": return NewCampaignFlowProbe.Run(context);
                case "world_recovery": return WorldRecoveryProbe.Run(context);
                case "save_binding": return SaveBindingProbe.Run(context);
                case "two_world_matrix": return TwoWorldMatrixProbe.Run(context);
                case "sandbox_boot": return SandboxBootProbe.Run(context);
                case "campaign_catalog": return CampaignCatalogProbe.Run(context);
                case "generated_campaign": return GeneratedCampaignBootProbe.Run(context);
                case "generated_save_load": return MNBL.ProceduralWorld.Diagnostics.WorldSaveProbe.Load(context);
                case "save_recovery": return MNBL.ProceduralWorld.Diagnostics.InitialSaveProbe.Recover(context);
                case "baseline_scenario": return BaselineScenarioProbe.Run(context);
                case "baseline_load": return BaselineLoadProbe.Run(context);
                case "baseline_combat": return BaselineLoadProbe.Run(context,true);
                case "surface": return StaticReport(context, "surface-probe.json", () => RuntimeSurfaceProbe.Run(SubModule.Store));
                case "nav": return StaticReport(context, "navigation-probe.json", () => RuntimeNavigationProbe.Run(SubModule.Store));
                case "generate": return Generate(context, checked((uint)Seed(parameter, 1)));
                case "render":
                {
                    int seed = Seed(parameter, 1);
                    return ScreenProbe(context, () => new SurfaceProbeScreen(seed), "render-probe-" + seed + ".json", 180);
                }
                case "ray":
                {
                    int seed = Seed(parameter, 1);
                    return ScreenProbe(context, () => new SurfaceProbeScreen(seed, true), "render-probe-" + seed + ".json", 180);
                }
                case "grid":
                {
                    int seed = Seed(parameter, 0);
                    return ScreenProbe(context, () => new HeightfieldProbeScreen(seed), null, 240);
                }
                default: throw new ArgumentException("Unknown probe id '" + id + "'. Known: noop, capabilities, generation_menu, scene_query, scene_sequence, native_render, runtime_surface, world_surface, world_navigation, world_setup, sandbox_boot, baseline_scenario, baseline_load, baseline_combat, surface, nav, generate:<seed>, render:<seed>, ray:<seed>, grid:<seed>.");
            }
        }

        private static int Seed(string parameter, int minimum)
        {
            int seed;
            if (parameter == null || !int.TryParse(parameter, NumberStyles.None, CultureInfo.InvariantCulture, out seed) || seed < minimum)
                throw new ArgumentException("Probe needs a numeric seed suffix >= " + minimum + ", e.g. render:42.");
            return seed;
        }

        private static IEnumerator Noop(ProbeContext context)
        {
            context.Stage("noop_begin");
            var top = ScreenManager.TopScreen;
            context.Outputs["topScreen"] = top == null ? null : top.GetType().FullName;
            context.Outputs["modules"] = TaleWorlds.Engine.Utilities.GetModulesNames();
            context.Outputs["applicationVersion"] = TaleWorlds.Engine.Utilities.GetApplicationVersionWithBuildNumber().ToString();
            yield return null;
            context.Stage("noop_end");
        }

        private static IEnumerator StaticReport(ProbeContext context, string reportName, Func<string> run)
        {
            var report = Path.Combine(SubModule.Store, reportName);
            var before = File.Exists(report) ? File.GetLastWriteTimeUtc(report) : DateTime.MinValue;
            context.Stage("static_probe_begin report=" + reportName);
            context.Outputs["message"] = run();
            context.Stage("static_probe_end");
            AttachReport(context, report, before);
            yield break;
        }

        private static IEnumerator Generate(ProbeContext context, uint seed)
        {
            double timeout = context.Num("timeoutSeconds", 300);
            using (var cancellation = new CancellationTokenSource())
            {
                context.Stage("generate_begin seed=" + seed);
                var token = cancellation.Token;
                var task = Task.Run(() => GenerationProbe.Run(seed, SubModule.Store, token));
                Action cancel = () => { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } };
                context.TrackOwnedWork(cancel, () => task.IsCompleted);
                double start = context.Seconds;
                try
                {
                    while (!task.IsCompleted)
                    {
                        if (!cancellation.IsCancellationRequested && context.Seconds - start > timeout)
                        {
                            context.Stage("generate_timeout_cancel");
                            cancellation.Cancel();
                        }
                        yield return null;
                    }
                    context.Stage("generate_end status=" + task.Status);
                    context.Outputs["elapsedSeconds"] = Math.Round(context.Seconds - start, 3);
                    if (task.IsFaulted) throw new InvalidOperationException("Generation failed.", task.Exception.GetBaseException());
                    if (task.IsCanceled) throw new OperationCanceledException("Generation was cancelled.");
                    context.Outputs["message"] = task.Result;
                    var marker = Path.Combine(SubModule.Store, "latest-heightfield-" + seed + ".json");
                    if (File.Exists(marker)) context.Outputs["marker"] = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(marker));
                }
                finally { if (!task.IsCompleted) cancel(); }
            }
        }

        private static IEnumerator ScreenProbe(ProbeContext context, Func<ScreenBase> factory, string reportName, double timeout)
        {
            var report = reportName == null ? null : Path.Combine(SubModule.Store, reportName);
            var before = report != null && File.Exists(report) ? File.GetLastWriteTimeUtc(report) : DateTime.MinValue;
            context.Stage("screen_create");
            var screen = factory();
            context.Stage("screen_push type=" + screen.GetType().Name);
            ScreenManager.PushScreen(screen);
            double start = context.Seconds;
            bool seen = false;
            while (true)
            {
                yield return null;
                bool onTop = ScreenManager.TopScreen == screen;
                if (onTop && !seen) { seen = true; context.Stage("screen_on_top"); }
                if (seen && !onTop) break;
                if (context.Seconds - start > timeout)
                {
                    context.Stage("screen_timeout");
                    if (onTop) ScreenManager.PopScreen();
                    throw new TimeoutException("Probe screen did not return within " + timeout + " s.");
                }
            }
            context.Stage("screen_returned");
            context.Outputs["screenSeconds"] = Math.Round(context.Seconds - start, 3);
            if (report != null) AttachReport(context, report, before);
        }

        private static void AttachReport(ProbeContext context, string report, DateTime before)
        {
            context.Outputs["reportPath"] = report;
            bool fresh = File.Exists(report) && File.GetLastWriteTimeUtc(report) > before;
            context.Outputs["reportWrittenThisRun"] = fresh;
            if (fresh) context.Outputs["report"] = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(report));
        }
    }
}
