using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Diagnostics;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;
using MNBL.ProceduralWorld.Surface;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Automation
{
    internal static class SandboxBootProbe
    {
        internal static IEnumerator Run(ProbeContext context)
        {
            context.Outputs["productionReady"] = false;
            context.Outputs["scope"] = "Generated terrain with retained official campaign scaffolding; paused boot only.";
            PartyGroundQueries.Fixture partyFixture = null;
            Dictionary<string, object> partyReport = null;
            if (context.Bool("partyGround", false))
            {
                partyReport = new Dictionary<string, object>(); context.Outputs["partyGround"] = partyReport;
                context.Stage("party_ground_verify_fixture");
                try
                {
                    partyFixture = PartyGroundQueries.Prepare(context.Str("partyGroundManifest", ""),
                        context.Str("partyGroundHeightfield", ""), context.Str("partyGroundInterpolation", ""), partyReport);
                }
                catch (Exception error)
                {
                    partyReport["status"] = "incomplete"; partyReport["passed"] = false; partyReport["exception"] = error.ToString();
                    context.Checkpoint(); throw;
                }
                context.Checkpoint();
            }
            context.Stage("sandbox_boot_start");
            var boot = DiagnosticSandboxBootstrap.Start(true, context.Stage);
            double start = context.Seconds;
            while (!boot.Completed)
            {
                boot.Tick();
                if (boot.Failed) throw new InvalidOperationException(boot.Error);
                if (context.Seconds - start > 300) throw new TimeoutException("Sandbox startup did not reach MapState.");
                yield return null;
            }
            var campaign = Campaign.Current;
            var wrapper = campaign.MapSceneWrapper as GeneratedMapScene;
            if (wrapper == null) throw new InvalidOperationException("Generated scene creator was replaced.");
            var scene = wrapper.Scene;
            if (scene.GetName() != GeneratedMapSceneCreator.Slot) throw new InvalidOperationException("Wrong scene.");
            context.Outputs["activeState"] = Game.Current.GameStateManager.ActiveState.GetType().FullName;
            context.Outputs["mapWrapper"] = wrapper.GetType().FullName;
            context.Outputs["sceneName"] = scene.GetName();
            context.Outputs["sceneModule"] = scene.GetModulePath();
            context.Outputs["timeControl"] = campaign.TimeControlMode.ToString();
            context.Outputs["navFaces"] = scene.GetNavMeshFaceCount();
            var heights = new List<object>();
            foreach (var p in new[] { new Vec2(260,260), new Vec2(520,520), new Vec2(780,780) })
                heights.Add(new { x=p.x, y=p.y, height=scene.GetTerrainHeight(p) });
            context.Outputs["heights"] = heights;
            context.Checkpoint();
            double readyStart = context.Seconds;
            while (!scene.IsLoadingFinished() || TaleWorlds.Engine.Utilities.GetNumberOfShaderCompilationsInProgress() > 0)
            {
                boot.Tick();
                if (context.Seconds-readyStart > 120) throw new TimeoutException("Map rendering: " + scene.GetLoadingStateName());
                yield return null;
            }
            // Let MapScreen render after readiness before the captured frame.
            double visibleStart = context.Seconds;
            while (context.Seconds-visibleStart < 8) { boot.Tick(); yield return null; }
            // Installed Naval tutorial inquiry has no action callbacks. Hide only
            // inside this explicit diagnostic after normal campaign initialization.
            InformationManager.HideInquiry();
            for (int i=0; i<5; i++) yield return null;
            if (!(Game.Current.GameStateManager.ActiveState is MapState)) throw new InvalidOperationException("MapState was displaced.");
            context.Outputs["topScreen"] = ScreenManager.TopScreen.GetType().FullName;
            context.Outputs["loadingState"] = scene.GetLoadingStateName();
            if (partyFixture != null)
            {
                boot.Tick();
                PartyGroundQueries.Measure(wrapper, partyFixture, partyReport, name => { context.Stage(name); context.Checkpoint(); });
                context.Checkpoint();
            }
            string screenshot = Path.Combine(context.Directory, "sandbox-" + context.ProcessId + ".bmp");
            TaleWorlds.Engine.Utilities.TakeScreenshot(screenshot);
            context.Outputs["screenshot"] = screenshot;
            context.Stage("sandbox_map_captured");
            context.Checkpoint();
            visibleStart = context.Seconds;
            while (context.Seconds-visibleStart < context.Num("holdSeconds", 5)) { boot.Tick(); yield return null; }
            if (partyReport != null && !(bool)partyReport["passed"])
                throw new InvalidOperationException("Paused party grounding did not pass; inspect partyGround failures/gaps and fixture provenance.");
        }
    }
}
