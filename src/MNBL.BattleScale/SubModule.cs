using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.CustomBattle;
using TaleWorlds.MountAndBlade.View;

namespace MNBL.BattleScale
{
    public sealed class SubModule : MBSubModuleBase
    {
        internal static readonly string Store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MNBL.BattleScale");
        internal static bool Probe;
        internal static bool Crowd;
        internal static bool NativePools;
        internal static string NativeProbeRunId;
        private bool registered, started;
        private float menuTime;
        internal static void Log(string message) { Directory.CreateDirectory(Store); File.AppendAllText(Path.Combine(Store, "module.log"), DateTime.UtcNow.ToString("O") + " " + message + "\n"); }
        internal static void Report(object value) { Directory.CreateDirectory(Store); File.WriteAllText(Path.Combine(Store, "result-" + Process.GetCurrentProcess().Id + ".json"), new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Serialize(value)); }

        protected override void OnSubModuleLoad()
        {
            Directory.CreateDirectory(Store);
            string request = Path.Combine(Store, "probe.once");
            if (File.Exists(request))
            {
                // A stale request cannot start an unattended mission on a future game launch.
                Probe = DateTime.UtcNow - File.GetLastWriteTimeUtc(request) < TimeSpan.FromMinutes(5);
                string probeRequest = File.ReadAllText(request).Trim();
                if (Probe && probeRequest.StartsWith("native-pools", StringComparison.Ordinal))
                {
                    Guid runId;
                    if (!probeRequest.StartsWith("native-pools:", StringComparison.Ordinal)
                        || !Guid.TryParseExact(probeRequest.Substring("native-pools:".Length), "D", out runId)
                        || runId == Guid.Empty)
                        throw new InvalidOperationException("Native pool probe requires a fresh nonempty run GUID.");
                    NativePools = true;
                    NativeProbeRunId = runId.ToString("D");
                }
                Crowd = Probe && (probeRequest == "crowd" || probeRequest.StartsWith("crowd:", StringComparison.Ordinal));
                if (Crowd && probeRequest.StartsWith("crowd:", StringComparison.Ordinal))
                {
                    int count;
                    if (!int.TryParse(probeRequest.Substring(6), out count) || count < 1 || count > 8192)
                        throw new InvalidOperationException("Invalid crowd probe population.");
                    VisualCrowdMission.TargetCount = count;
                }
                File.Move(request, request + ".claimed-" + Guid.NewGuid().ToString("N"));
            }
            Log("loaded; probe=" + Probe);
        }
        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            if (registered) return;
            registered = true;
            TaleWorlds.MountAndBlade.Module.CurrentModule.AddInitialStateOption(new InitialStateOption("battle_scale_demo",
                new TextObject("Battle Scale: experimental 100,000-troop simulation"), 90, () => { Crowd = false; NativePools = false; StartDemo(); },
                () => (started, new TextObject("Demo is starting")), null, null));
        }
        protected override void OnApplicationTick(float dt)
        {
            NativePoolProbeMission.OnApplicationTick();
            if (!Probe || !registered || started) return;
            menuTime += dt;
            if (menuTime > 3) StartDemo();
        }
        public override void OnGameEnd(Game game)
        {
            started = false;
            // A probe is single-shot; returning to the menu must not start it again.
            Probe = false;
            NativePools = false;
        }
        private void StartDemo()
        {
            if (started) return;
            try
            {
                // This adapter is verified only against this exact installed managed engine build.
                string path = typeof(Mission).Assembly.Location;
                string hash;
                using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                if (!NativePools && hash != "19387f31557ff840d14f378f6bbdf1d58fcff406ab9ff2294dfbb3e49a50b87e") throw new InvalidOperationException("Unverified Bannerlord build; Battle Scale disabled.");
                if (NativePools) NativePoolProbeMission.ValidateProfile();
                started = true;
                Log("starting isolated custom game");
                MBGameManager.StartNewGame(new BattleScaleGameManager());
            }
            catch (Exception error)
            {
                Log(error.ToString());
                Report(new { status = "failed", runId = NativeProbeRunId, error = error.ToString() });
                InformationManager.DisplayMessage(new InformationMessage("Battle Scale could not start: " + error.Message));
                if (Probe) TaleWorlds.Engine.Utilities.QuitGame();
                started = false;
            }
        }
    }

    internal sealed class BattleScaleGameManager : CustomGameManager
    {
        public override void OnLoadFinished()
        {
            base.OnLoadFinished();
            if (SubModule.NativePools)
            {
                try { NativePoolProbeMission.ValidateProfile(); }
                catch (Exception error)
                {
                    SubModule.Log(error.ToString());
                    SubModule.Report(new { status = "failed", runId = SubModule.NativeProbeRunId, error = error.ToString() });
                    TaleWorlds.Engine.Utilities.QuitGame();
                    return;
                }
            }
            SubModule.Log("opening benchmark scene with independent spawn logic");
            var record = new MissionInitializerRecord("benchmark_battle_11") { PlayingInCampaignMode = false };
            MissionState.OpenNew("BattleScale", record, mission => SubModule.NativePools ? new MissionBehavior[] {
                new NativePoolProbeMission(), new AgentHumanAILogic(),
                ViewCreator.CreateMissionSingleplayerEscapeMenu(false),
                ViewCreator.CreateMissionSpectatorControlView(mission)
            } : SubModule.Crowd ? new MissionBehavior[] {
                new VisualCrowdMission(), ViewCreator.CreateMissionSingleplayerEscapeMenu(false)
            } : new MissionBehavior[] {
                new LayeredBattleMission(), new AgentHumanAILogic(),
                ViewCreator.CreateMissionSingleplayerEscapeMenu(false),
                ViewCreator.CreateMissionAgentStatusUIHandler(mission),
                ViewCreator.CreateMissionSpectatorControlView(mission)
            }, true, true);
        }
    }
}
