using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MNBL.ProceduralWorld.CampaignBootstrap;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.Automation
{
    // Explicit harness only: official content with diagnostic orchestration, not a vanilla run.
    internal static class BaselineScenarioProbe
    {
        internal static IEnumerator Run(ProbeContext context)
        {
            context.Outputs["scope"] = "Instrumented official-content Sandbox; no generated terrain or campaign records.";
            context.Outputs["vanillaRun"] = false;
            context.Outputs["modules"] = TaleWorlds.Engine.Utilities.GetModulesNames();
            double start = context.Seconds;
            var boot = DiagnosticSandboxBootstrap.Start(true, new SandBox.MapSceneCreator(),
                "empire", "MNBL Baseline", context.Stage);
            while (!boot.Completed)
            {
                boot.Tick();
                if (boot.Failed) throw new InvalidOperationException(boot.Error);
                if (context.Seconds-start > 300) throw new TimeoutException("Official-content startup timed out.");
                yield return null;
            }
            context.Outputs["newCampaignBootSeconds"] = context.Seconds-start;
            var scene = ((SandBox.MapScene)Campaign.Current.MapSceneWrapper).Scene;
            context.Outputs["scene"] = scene.GetName();
            context.Outputs["sceneModule"] = scene.GetModulePath();
            start = context.Seconds;
            while (!scene.IsLoadingFinished() || TaleWorlds.Engine.Utilities.GetNumberOfShaderCompilationsInProgress() > 0)
            {
                if (context.Seconds-start > 120) throw new TimeoutException("Official map readiness timed out.");
                yield return null;
            }
            InformationManager.HideInquiry();
            start = context.Seconds;
            while (context.Seconds-start < 5) yield return null;
            var sample = Sample(context, "pausedMap", 10);
            while (sample.MoveNext()) yield return sample.Current;
            Capture(context, "official-map");

            var party = MobileParty.MainParty;
            var before = party.Position;
            var village = Settlement.All.Where(s => s.IsVillage)
                .OrderBy(s => DistanceSquared(before, s.Position)).First();
            context.Outputs["landTarget"] = village.StringId;
            context.Outputs["landStart"] = new { x=before.X, y=before.Y };
            party.SetMoveGoToSettlement(village, MobileParty.NavigationType.Default, false);
            Campaign.Current.SetTimeControlModeLock(false);
            Campaign.Current.TimeControlMode = CampaignTimeControlMode.UnstoppablePlay;
            double hours = CampaignTime.Now.ToHours;
            var ownedCampaign=Campaign.Current;
            try
            {
                sample = Sample(context, "landTravel", 15, () =>
                    Campaign.Current==ownedCampaign && MobileParty.MainParty==party &&
                    TaleWorlds.Core.Game.Current.GameStateManager.ActiveState is TaleWorlds.CampaignSystem.GameState.MapState &&
                    party.CurrentSettlement==null && party.MapEvent==null &&
                    TaleWorlds.CampaignSystem.Encounters.PlayerEncounter.Current==null);
                while (sample.MoveNext()) yield return sample.Current;
            }
            finally
            {
                Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
                Campaign.Current.SetTimeControlModeLock(true);
                party.SetMoveModeHold();
            }
            var after = party.Position;
            context.Outputs["landEnd"] = new { x=after.X, y=after.Y };
            context.Outputs["campaignHoursAdvanced"] = CampaignTime.Now.ToHours-hours;
            context.Outputs["landDistance"] = Math.Sqrt(DistanceSquared(before,after));
            Capture(context, "land-travel");
            context.Checkpoint();
            if (DistanceSquared(before,after)<0.01) throw new InvalidOperationException("Land movement did not advance.");
            var missions=BaselineMissionSteps.Run(context,village);
            while(missions.MoveNext()) yield return missions.Current;
            string saveName="mnbl_baseline_"+Guid.NewGuid().ToString("N");
            if (TaleWorlds.Core.MBSaveLoad.IsSaveGameFileExists(saveName))
                throw new InvalidOperationException("Refusing to replace an existing save.");
            var saves=new BaselineSaveObserver(saveName);
            Campaign.Current.SandBoxManager.SandBoxSaveManager=saves;
            context.Outputs["saveName"]=saveName;
            var savePosition=party.Position;
            context.Outputs["savePosition"]=new {x=savePosition.X,y=savePosition.Y};
            context.Outputs["saveCampaignHours"]=CampaignTime.Now.ToHours;
            context.Stage("baseline_save_requested");
            start=context.Seconds;
            Campaign.Current.SaveHandler.SaveAs(saveName);
            while (!saves.Completed)
            {
                if (context.Seconds-start>120) throw new TimeoutException("Baseline save did not complete.");
                yield return null;
            }
            context.Outputs["saveSeconds"]=context.Seconds-start;
            context.Outputs["saveSucceeded"]=saves.Success;
            context.Checkpoint();
            if (!saves.Success || !TaleWorlds.Core.MBSaveLoad.IsSaveGameFileExists(saveName))
                throw new InvalidOperationException("Baseline save failed.");
        }

        private static double DistanceSquared(CampaignVec2 a, CampaignVec2 b)
        { double x=a.X-b.X, y=a.Y-b.Y; return x*x+y*y; }

        private sealed class BaselineSaveObserver : ISaveManager
        {
            private readonly string expected;
            internal bool Completed, Success;
            internal BaselineSaveObserver(string expected) { this.expected=expected; }
            public int GetAutoSaveInterval() { return -1; }
            public bool IsAutoSaveDisabled() { return true; }
            public void OnSaveOver(bool success, string name)
            { Success=success && name==expected; Completed=true; }
        }

        internal static IEnumerator Sample(ProbeContext context, string name, double seconds, Func<bool> continueSample=null)
        {
            var intervals = new List<double>();
            double start=context.Seconds, previous=start;
            bool interrupted=false;
            while (context.Seconds-start < seconds)
            {
                if (continueSample!=null && !continueSample()) { interrupted=true; break; }
                yield return null;
                if (continueSample!=null && !continueSample()) { interrupted=true; break; }
                double now=context.Seconds;
                intervals.Add((now-previous)*1000);
                previous=now;
            }
            if (intervals.Count==0) throw new InvalidOperationException(name+" produced no valid samples.");
            intervals.Sort();
            context.Outputs[name] = new {
                measurement="Application-tick wall intervals, includes simulation/render scheduling; not isolated CPU or GPU cost",
                elapsedSeconds=previous-start, samples=intervals.Count,
                interruptedByStateChange=interrupted,
                meanMilliseconds=intervals.Average(),
                medianMilliseconds=intervals[intervals.Count/2],
                p95Milliseconds=intervals[(int)((intervals.Count-1)*0.95)],
                maximumMilliseconds=intervals[intervals.Count-1]
            };
            context.Checkpoint();
        }

        internal static void Capture(ProbeContext context, string name)
        {
            string path=System.IO.Path.Combine(context.Directory,name+"-"+context.ProcessId+".bmp");
            TaleWorlds.Engine.Utilities.TakeScreenshot(path);
            context.Outputs[name+"Screenshot"]=path;
            context.Stage(name+"_captured");
        }
    }
}
