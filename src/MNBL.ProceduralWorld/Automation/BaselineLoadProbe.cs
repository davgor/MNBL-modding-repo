using System;
using System.Collections;
using SandBox;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.SaveSystem.Load;

namespace MNBL.ProceduralWorld.Automation
{
    internal static class BaselineLoadProbe
    {
        internal static IEnumerator Run(ProbeContext context, bool combat=false)
        {
            string name=context.Str("saveName", "");
            Guid identity;
            if (!name.StartsWith("mnbl_baseline_",StringComparison.Ordinal) ||
                !Guid.TryParseExact(name.Substring("mnbl_baseline_".Length),"N",out identity))
                throw new ArgumentException("Only a uniquely named baseline diagnostic save may be loaded.");
            if (Game.Current!=null) throw new InvalidOperationException("Cold load requires the initial menu.");
            if (!MBSaveLoad.IsSaveGameFileExists(name)) throw new InvalidOperationException("Diagnostic save missing.");
            context.Outputs["vanillaRun"]=false;
            context.Outputs["saveName"]=name;
            context.Outputs["modules"]=TaleWorlds.Engine.Utilities.GetModulesNames();
            double start=context.Seconds;
            context.Stage("baseline_cold_load_start");
            var data=MBSaveLoad.LoadSaveGameData(name);
            if (data==null || !data.Successful) throw new InvalidOperationException("Save deserialization failed.");
            context.Outputs["deserializeSeconds"]=context.Seconds-start;
            var manager=new Loader(data);
            MBGameManager.StartNewGame(manager);
            while (!manager.Completed)
            {
                manager.CheckSaveGuard();
                if (context.Seconds-start>300) throw new TimeoutException("Cold campaign load timed out.");
                yield return null;
            }
            if (!(Game.Current.GameStateManager.ActiveState is MapState))
                throw new InvalidOperationException("Load did not reach MapState.");
            var scene=((MapScene)Campaign.Current.MapSceneWrapper).Scene;
            while (!scene.IsLoadingFinished() || TaleWorlds.Engine.Utilities.GetNumberOfShaderCompilationsInProgress()>0)
            {
                manager.CheckSaveGuard();
                if (context.Seconds-start>420) throw new TimeoutException("Loaded map did not finish rendering.");
                yield return null;
            }
            context.Outputs["loadToReadySeconds"]=context.Seconds-start;
            context.Outputs["scene"]=scene.GetName();
            context.Outputs["sceneModule"]=scene.GetModulePath();
            context.Outputs["hero"]=Hero.MainHero.Name.ToString();
            var position=MobileParty.MainParty.Position;
            context.Outputs["position"]=new {x=position.X,y=position.Y};
            context.Outputs["campaignHours"]=CampaignTime.Now.ToHours;
            if (combat)
            {
                context.Outputs["scope"]="Instrumented combat/naval continuation from diagnostic save; changes are not saved.";
                var steps=BaselineCombatSteps.Run(context);
                try
                {
                    while(steps.MoveNext()) { manager.CheckSaveGuard(); yield return steps.Current; }
                }
                finally { (steps as IDisposable)?.Dispose(); }
                manager.CheckSaveGuard();
                context.Checkpoint();
                yield break;
            }
            var sample=BaselineScenarioProbe.Sample(context,"loadedMap",10);
            while(sample.MoveNext()) { manager.CheckSaveGuard(); yield return sample.Current; }
            manager.CheckSaveGuard();
            BaselineScenarioProbe.Capture(context,"cold-loaded-map");
            context.Checkpoint();
            for(int i=0;i<10;i++) { manager.CheckSaveGuard(); yield return null; }
        }

        private sealed class Loader : SandBoxGameManager, ISaveManager
        {
            internal bool Completed;
            private string unexpectedSave;
            internal Loader(LoadResult result) : base(result) { }
            public override void OnGameInitializationFinished(Game game)
            { base.OnGameInitializationFinished(game); InstallGuard(); }
            public override void OnLoadFinished()
            { InstallGuard(); base.OnLoadFinished(); InstallGuard(); Completed=true; }
            private void InstallGuard()
            {
                Campaign.Current.SandBoxManager.SandBoxSaveManager=this;
                Campaign.Current.SetTimeControlModeLock(false);
                Campaign.Current.TimeControlMode=CampaignTimeControlMode.Stop;
                Campaign.Current.SetTimeControlModeLock(true);
            }
            public int GetAutoSaveInterval() { return -1; }
            public bool IsAutoSaveDisabled() { return true; }
            public void OnSaveOver(bool success,string name)
            { unexpectedSave=name ?? "<unnamed>"; }
            internal void CheckSaveGuard()
            {
                if (unexpectedSave!=null)
                    throw new InvalidOperationException("Unexpected save during read-only baseline load: "+unexpectedSave);
            }
        }
    }
}
