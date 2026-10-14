using System;
using System.Collections;
using System.IO;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;

namespace MNBL.ProceduralWorld.Automation
{
    // Opt-in official-content scenario. Teleport/encounter setup and EndMission are instrumentation.
    // Installed IL and option registrations: build/research-baseline/*mission*.il.txt, menu-*.il.txt.
    internal static class BaselineMissionSteps
    {
        internal static IEnumerator Run(ProbeContext context, Settlement village)
        {
            if (village == null || !village.IsVillage)
                throw new ArgumentException("Baseline mission steps require a village.", nameof(village));
            RequireNoAutoSave();
            context.Outputs["settlementMissionScope"] = "Instrumented official village/town missions; gate teleport if needed, official menu consequences, forced diagnostic mission exit. Not stock UI or travel-to-settlement acceptance.";
            var step = Visit(context, village, "village", "village_center", "leave");
            while (step.MoveNext()) yield return step.Current;

            var town = Settlement.All.Where(s => s.IsTown && !s.IsUnderSiege && !s.IsUnderRaid
                && !s.MapFaction.IsAtWarWith(MobileParty.MainParty.MapFaction))
                .OrderBy(s => DistanceSquared(MobileParty.MainParty.Position, s.GatePosition)).FirstOrDefault();
            if (town == null) throw new InvalidOperationException("No peaceful town available for the diagnostic.");
            step = Visit(context, town, "town", "town_streets", "town_leave");
            while (step.MoveNext()) yield return step.Current;
            context.Outputs["settlementMissionStepsCompleted"] = true;
            context.Checkpoint();
        }

        private static IEnumerator Visit(ProbeContext context, Settlement settlement, string name,
            string entryOption, string leaveOption)
        {
            var party = MobileParty.MainParty;
            if (!(Game.Current.GameStateManager.ActiveState is MapState) || Mission.Current != null)
                throw new InvalidOperationException("Settlement diagnostic requires an active map and no mission.");
            if (party.MapEvent != null || settlement.IsUnderSiege || settlement.IsUnderRaid
                || settlement.MapFaction.IsAtWarWith(party.MapFaction) || party.IsCurrentlyAtSea)
                throw new InvalidOperationException("Settlement diagnostic requires a peaceful land encounter.");
            RequireNoAutoSave();
            bool alreadyEntered = party.CurrentSettlement == settlement && PlayerEncounter.LocationEncounter != null;
            if (!alreadyEntered && (PlayerEncounter.Current != null || party.CurrentSettlement != null))
                throw new InvalidOperationException("Refusing to replace an unrelated encounter.");

            context.Outputs[name + "Settlement"] = settlement.StringId;
            context.Outputs[name + "Setup"] = new {
                reusedExistingEncounter = alreadyEntered, teleportedToGate = !alreadyEntered,
                startX = party.Position.X, startY = party.Position.Y,
                gateX = settlement.GatePosition.X, gateY = settlement.GatePosition.Y
            };
            context.Stage(name + "_encounter_setup");
            context.Checkpoint();
            if (!alreadyEntered)
            {
                party.SetMoveModeHold();
                party.Position = settlement.GatePosition;
                EncounterManager.StartSettlementEncounter(party, settlement);
            }

            double start = context.Seconds;
            while (!HasMenu(name) || party.CurrentSettlement != settlement || PlayerEncounter.LocationEncounter == null)
            {
                if (context.Seconds - start > 30)
                    throw new TimeoutException(name + " encounter did not produce the expected settlement menu.");
                yield return null;
            }
            context.Stage(name + "_official_entry_option");
            RunOption(entryOption);
            Mission ownedMission = null;
            bool endRequested = false;
            try
            {
                start = context.Seconds;
                while (true)
                {
                    var state = Game.Current.GameStateManager.ActiveState as MissionState;
                    if (state != null && state.CurrentMission != null && state.CurrentMission == Mission.Current)
                    {
                        ownedMission = state.CurrentMission;
                        if (ownedMission.MissionEnded)
                            throw new InvalidOperationException(name + " mission ended before measurement.");
                        if (ownedMission.Scene != null && ownedMission.Scene.IsLoadingFinished()
                            && ownedMission.MainAgent != null && IsMissionScreen()
                            && TaleWorlds.Engine.Utilities.GetNumberOfShaderCompilationsInProgress() == 0)
                            break;
                    }
                    if (context.Seconds - start > 180)
                        throw new TimeoutException(name + " mission readiness timed out.");
                    yield return null;
                }
                context.Outputs[name + "MissionReady"] = new {
                    loadSeconds = context.Seconds - start,
                    scene = ownedMission.Scene.GetName(), module = ownedMission.Scene.GetModulePath(),
                    screen = ScreenManager.TopScreen.GetType().FullName,
                    loadingFinished = true, mainAgentPresent = true,
                    evidence = "Active MissionState, matching mission, loaded scene, mission screen, main agent, no pending shaders; screenshot requires visual review."
                };
                context.Stage(name + "_mission_ready");
                var sample = BaselineScenarioProbe.Sample(context, name + "MissionTicks", 5);
                while (sample.MoveNext())
                {
                    RequireOwnedMission(ownedMission);
                    yield return sample.Current;
                }
                RequireOwnedMission(ownedMission);
                string captureName = "baseline-" + name + "-mission";
                BaselineScenarioProbe.Capture(context, captureName);
                string capturePath = (string)context.Outputs[captureName + "Screenshot"];
                start = context.Seconds;
                do
                {
                    yield return null;
                    RequireOwnedMission(ownedMission);
                    if (context.Seconds - start > 30)
                        throw new TimeoutException(name + " screenshot was not written.");
                } while (!File.Exists(capturePath) || new FileInfo(capturePath).Length == 0);
                context.Outputs[name + "ScreenshotWritten"] = true;
                context.Stage(name + "_diagnostic_end_requested");
                ownedMission.EndMission();
                endRequested = true;
                start = context.Seconds;
                while (Mission.Current != null || !(Game.Current.GameStateManager.ActiveState is MapState) || IsMissionScreen())
                {
                    if (context.Seconds - start > 90)
                        throw new TimeoutException(name + " mission did not finish normal teardown.");
                    yield return null;
                }
                context.Outputs[name + "MissionReturnedToMap"] = true;
                context.Outputs[name + "MissionTeardownSeconds"] = context.Seconds - start;
            }
            finally
            {
                // On managed failure, request normal teardown only for the mission we observed.
                // Never pop game states or free a scene manually.
                if (!endRequested && ownedMission != null && Mission.Current == ownedMission && !ownedMission.MissionEnded)
                    ownedMission.EndMission();
            }

            start = context.Seconds;
            while (!HasMenu(name))
            {
                if (context.Seconds - start > 30) throw new TimeoutException(name + " menu did not return.");
                yield return null;
            }
            RequireNoAutoSave();
            context.Outputs[name + "LeaveAutosavePolicy"] = "Official leave consequence signals autosave; existing harness ISaveManager must disable it.";
            RunOption(leaveOption);
            start = context.Seconds;
            while (party.CurrentSettlement != null || PlayerEncounter.Current != null
                || !(Game.Current.GameStateManager.ActiveState is MapState))
            {
                if (context.Seconds - start > 30) throw new TimeoutException(name + " settlement leave did not complete.");
                yield return null;
            }
            Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
            Campaign.Current.SetTimeControlModeLock(true);
            context.Outputs[name + "SettlementLeft"] = true;
            context.Stage(name + "_returned_to_map");
            context.Checkpoint();
        }

        private static bool HasMenu(string id)
        {
            var map = Game.Current.GameStateManager.ActiveState as MapState;
            return map != null && map.MenuContext != null && map.MenuContext.GameMenu != null
                && map.MenuContext.GameMenu.StringId == id;
        }

        private static void RunOption(string id)
        {
            var map = Game.Current.GameStateManager.ActiveState as MapState;
            if (map == null || map.MenuContext == null || map.MenuContext.GameMenu == null)
                throw new InvalidOperationException("No active menu for " + id);
            var context = map.MenuContext;
            var menu = context.GameMenu;
            var options = menu.MenuOptions.ToList();
            int index = options.FindIndex(o => o.IdString == id);
            if (index < 0 || index >= menu.MenuItemAmount || menu.GetMenuOptionIdString(index) != id)
                throw new InvalidOperationException("Missing or mismatched official menu option " + id);
            if (!options[index].GetConditionsHold(Game.Current, context) || !options[index].IsEnabled)
                throw new InvalidOperationException("Official menu option unavailable: " + id);
            menu.RunMenuOptionConsequence(context, index);
        }

        private static void RequireNoAutoSave()
        {
            var manager = Campaign.Current.SandBoxManager.SandBoxSaveManager;
            if (manager == null || !manager.IsAutoSaveDisabled() || manager.GetAutoSaveInterval() >= 0)
                throw new InvalidOperationException("Settlement diagnostic requires the harness no-autosave manager.");
        }

        private static bool IsMissionScreen()
        {
            // Avoid adding a View assembly reference solely for this read-only screen identity check.
            var type = ScreenManager.TopScreen == null ? null : ScreenManager.TopScreen.GetType();
            for (; type != null; type = type.BaseType)
                if (type.FullName == "TaleWorlds.MountAndBlade.View.Screens.MissionScreen") return true;
            return false;
        }

        private static void RequireOwnedMission(Mission mission)
        {
            var state = Game.Current.GameStateManager.ActiveState as MissionState;
            if (Mission.Current != mission || state == null || state.CurrentMission != mission
                || mission.MissionEnded || !IsMissionScreen())
                throw new InvalidOperationException("Diagnostic mission changed during sampling or capture.");
        }

        private static double DistanceSquared(CampaignVec2 a, CampaignVec2 b)
        { double x = a.X - b.X, y = a.Y - b.Y; return x * x + y * y; }
    }
}
