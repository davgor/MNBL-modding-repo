using System;
using System.Collections;
using System.IO;
using System.Linq;
using Helpers;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;
using TaleWorlds.ScreenSystem;

namespace MNBL.ProceduralWorld.Automation
{
    // Loaded diagnostic campaign only. Never saves. All grants and relocations are synthetic.
    // Installed signatures/call sites: build/research-baseline/combat-*.il.txt.
    internal static class BaselineCombatSteps
    {
        internal static IEnumerator Run(ProbeContext context)
        {
            ValidateReplay(context);
            context.Outputs["combatScope"] = "Instrumented official-content field/port/naval scenarios. In-memory troop/ship grants and relocations; official menu entry; diagnostic deployment/retreat. No victory or stock-UI acceptance claim.";
            context.Outputs["vanillaRun"] = false;
            var steps = RunField(context);
            try { while (steps.MoveNext()) yield return steps.Current; }
            finally { (steps as IDisposable)?.Dispose(); }
            MobileParty reserved = null;
            try
            {
                if (Replay(context))
                {
                    var candidate = FindEnemy(context, "replayNavalEnemyId");
                    if (candidate.Ai.IsDisabled || candidate.ShouldBeIgnored)
                        throw new InvalidOperationException("Replay enemy already has AI/ignore restrictions; refusing to overwrite them.");
                    reserved = candidate;
                    reserved.Ai.DisableAi();
                    reserved.SetMoveModeHold();
                    reserved.IgnoreByOtherPartiesTill(CampaignTime.Never);
                    context.Outputs["navalReplayReservation"] = new {
                        party = reserved.StringId, synthetic = true,
                        scope = "AI disabled and ignored by other parties after field teardown until naval scenario teardown; no save."
                    };
                    context.Checkpoint();
                }
                steps = RunNaval(context);
                try { while (steps.MoveNext()) yield return steps.Current; }
                finally { (steps as IDisposable)?.Dispose(); }
            }
            finally
            {
                if (reserved != null)
                {
                    Pause();
                    reserved.IgnoreByOtherPartiesTill(CampaignTime.Zero);
                    reserved.Ai.EnableAi();
                    context.Outputs["navalReplayReservationReleased"] = true;
                }
            }
            context.Outputs["combatStepsCompleted"] = true;
            context.Checkpoint();
        }

        internal static IEnumerator RunField(ProbeContext context)
        {
            RequireIdleMap();
            var main = MobileParty.MainParty;
            if (main.IsCurrentlyAtSea || !main.Position.IsOnLand)
                throw new InvalidOperationException("Field diagnostic requires a land starting point.");
            var enemy = FindEnemy(context, "replayFieldEnemyId");
            AddCrew(context, main, "fieldPlayer");
            AddCrew(context, enemy, "fieldEnemy");
            var point = FixturePoint(context, "replayField", main.Position, MobileParty.NavigationType.Default, 2, 1);
            context.Outputs["fieldSyntheticSetup"] = new {
                enemy = enemy.StringId, originalX = enemy.Position.X, originalY = enemy.Position.Y,
                diagnosticX = point.X, diagnosticY = point.Y,
                operation = "Relocate an existing hostile, idle, shipless bandit party; no diplomacy change or roster clearing."
            };
            context.Checkpoint();
            enemy.Position = point;
            enemy.SetMoveModeHold();
            var steps = Encounter(context, enemy, false);
            try { while (steps.MoveNext()) yield return steps.Current; }
            finally { (steps as IDisposable)?.Dispose(); }
        }

        internal static IEnumerator RunNaval(ProbeContext context)
        {
            RequireIdleMap();
            var main = MobileParty.MainParty;
            if (main.IsCurrentlyAtSea || main.Ships.Count != 0)
                throw new InvalidOperationException("Naval fixture requires the shipless land baseline; reload the diagnostic save to repeat.");
            var town = Settlement.All.Where(s => s.IsTown && s.HasPort && !s.IsUnderRaid && !s.IsUnderSiege
                && !s.MapFaction.IsAtWarWith(main.MapFaction)
                && (!Replay(context) || s.StringId == context.Str("replayPortId", "")))
                .OrderBy(s => DistanceSquared(main.Position, s.GatePosition)).FirstOrDefault();
            if (town == null) throw new InvalidOperationException("No eligible peaceful port town; replay never substitutes another port.");
            if (Replay(context)) RequirePosition(town.GatePosition, ReplayPoint(context, "replayPortGate", MobileParty.NavigationType.Default), "port gate");
            context.Outputs["portSyntheticSetup"] = new {
                settlement = town.StringId, originalX = main.Position.X, originalY = main.Position.Y,
                gateX = town.GatePosition.X, gateY = town.GatePosition.Y, teleported = true
            };
            context.Stage("combat_port_encounter_setup");
            context.Checkpoint();
            main.Position = town.GatePosition;
            main.SetMoveModeHold();
            EncounterManager.StartSettlementEncounter(main, town);
            var wait = WaitMenu(context, "town");
            while (wait.MoveNext()) yield return wait.Current;
            RunOption("port");
            wait = WaitMenu(context, "port_menu");
            while (wait.MoveNext()) yield return wait.Current;
            RunOption("enter_port");
            var steps = ObserveMission(context, "port", false, false);
            try { while (steps.MoveNext()) yield return steps.Current; }
            finally { (steps as IDisposable)?.Dispose(); }
            wait = WaitMenu(context, "port_menu");
            while (wait.MoveNext()) yield return wait.Current;
            AddCrew(context, main, "navalPlayer");
            GrantShip(context, main, "navalPlayer");
            main.Anchor.SetSettlement(town);
            context.Stage("combat_official_sail_option");
            RunOption("sail_option");
            double start = context.Seconds;
            while (main.CurrentSettlement != null || PlayerEncounter.Current != null || !main.IsCurrentlyAtSea)
            {
                RequireNoSave();
                if (context.Seconds - start > 30) throw new TimeoutException("Official set-sail consequence did not finish.");
                yield return null;
            }
            RequireIdleMap();
            var before = main.Position;
            if (Replay(context)) RequirePosition(before, ReplayPoint(context, "replaySeaStart", MobileParty.NavigationType.Naval), "embarkation position");
            var target = FixturePoint(context, "replaySeaTarget", before, MobileParty.NavigationType.Naval, 10, 3);
            context.Outputs["seaTravelStart"] = new { x = before.X, y = before.Y, targetX = target.X, targetY = target.Y };
            main.SetMoveGoToPoint(target, MobileParty.NavigationType.Naval);
            Campaign.Current.SetTimeControlModeLock(false);
            Campaign.Current.TimeControlMode = CampaignTimeControlMode.UnstoppablePlay;
            double hours = CampaignTime.Now.ToHours;
            steps = BaselineScenarioProbe.Sample(context, "seaTravelTicks", 10);
            try
            {
                while (steps.MoveNext())
                {
                    RequireNoSave();
                    if (!(Game.Current.GameStateManager.ActiveState is MapState) || PlayerEncounter.Current != null)
                        throw new InvalidOperationException("Sea movement interrupted by an unplanned encounter/state.");
                    yield return steps.Current;
                }
            }
            finally { (steps as IDisposable)?.Dispose(); Pause(); main.SetMoveModeHold(); }
            var after = main.Position;
            context.Outputs["seaTravelEnd"] = new {
                x = after.X, y = after.Y, distance = Math.Sqrt(DistanceSquared(before, after)),
                campaignHours = CampaignTime.Now.ToHours - hours, atSea = main.IsCurrentlyAtSea,
                faceTerrain = Campaign.Current.MapSceneWrapper.GetFaceTerrainType(main.CurrentNavigationFace).ToString()
            };
            context.Checkpoint();
            if (!main.IsCurrentlyAtSea || after.IsOnLand || DistanceSquared(before, after) < 0.01)
                throw new InvalidOperationException("Sea movement did not advance on water.");
            steps = Capture(context, "baseline-sea-travel");
            while (steps.MoveNext()) yield return steps.Current;

            if (Replay(context))
            {
                RequireIdleMap();
                var endpoint = ReplayPoint(context, "replaySeaEncounter", MobileParty.NavigationType.Naval);
                context.Outputs["navalPlayerReplayRelocation"] = new {
                    originalX = main.Position.X, originalY = main.Position.Y,
                    diagnosticX = endpoint.X, diagnosticY = endpoint.Y, synthetic = true,
                    scope = "After measured sea travel and capture; fixes encounter location only, not measured distance or physics."
                };
                context.Checkpoint();
                main.SetSailAtPosition(endpoint);
                main.SetMoveModeHold();
            }
            var enemy = FindEnemy(context, "replayNavalEnemyId");
            AddCrew(context, enemy, "navalEnemy");
            GrantShip(context, enemy, "navalEnemy");
            var enemyPoint = FixturePoint(context, "replayNaval", main.Position, MobileParty.NavigationType.Naval, 2, 1);
            context.Outputs["navalSyntheticSetup"] = new {
                enemy = enemy.StringId, originalX = enemy.Position.X, originalY = enemy.Position.Y,
                diagnosticX = enemyPoint.X, diagnosticY = enemyPoint.Y,
                operation = "Grant a ship and move an existing hostile bandit to sea through SetSailAtPosition; no diplomacy change."
            };
            context.Checkpoint();
            enemy.SetSailAtPosition(enemyPoint);
            enemy.SetMoveModeHold();
            steps = Encounter(context, enemy, true);
            try { while (steps.MoveNext()) yield return steps.Current; }
            finally { (steps as IDisposable)?.Dispose(); }
        }

        private static IEnumerator Encounter(ProbeContext context, MobileParty enemy, bool naval)
        {
            RequireIdleMap();
            string name = naval ? "naval" : "field";
            context.Stage(name + "_synthetic_encounter");
            PlayerEncounter.RestartPlayerEncounter(enemy.Party, MobileParty.MainParty.Party, true, false);
            var encounter = PlayerEncounter.Current;
            try
            {
                var battle = PlayerEncounter.StartBattle();
                if (battle == null || battle.IsNavalMapEvent != naval)
                    throw new InvalidOperationException("Map event has the wrong navigation type.");
                context.Outputs[name + "MapEvent"] = new {
                    naval = battle.IsNavalMapEvent, involvedParties = battle.InvolvedParties.Select(p => p.MobileParty == null ? "settlement" : p.MobileParty.StringId).ToArray()
                };
                if (battle.InvolvedParties.Count() != 2)
                    throw new InvalidOperationException("Nearby parties joined the synthetic encounter; refusing an uncontrolled fixture.");
                GameMenu.ActivateGameMenu("encounter");
                var wait = WaitMenu(context, "encounter");
                while (wait.MoveNext()) yield return wait.Current;
                context.Stage(name + "_official_attack_option");
                RunOption("attack");
                var steps = ObserveMission(context, name, true, naval);
                try { while (steps.MoveNext()) yield return steps.Current; }
                finally { (steps as IDisposable)?.Dispose(); }
                RequireNoSave();
                if (PlayerEncounter.Current != null)
                {
                    if (PlayerEncounter.Current != encounter)
                        throw new InvalidOperationException("Another encounter replaced the diagnostic encounter.");
                    PlayerEncounter.Finish(true);
                }
                double start = context.Seconds;
                while (PlayerEncounter.Current != null || MobileParty.MainParty.MapEvent != null
                    || !(Game.Current.GameStateManager.ActiveState is MapState))
                {
                    if (context.Seconds - start > 30) throw new TimeoutException(name + " encounter cleanup timed out.");
                    yield return null;
                }
                Pause();
                context.Outputs[name + "ReturnedToMap"] = true;
                context.Outputs[name + "OutcomeScope"] = "Mission/deployment/simulation/capture and scripted retreat teardown; no victory or unassisted combat completion claimed.";
                context.Stage(name + "_returned_to_map");
                context.Checkpoint();
            }
            finally
            {
                // Never finish a campaign event while its mission is still using it.
                if (Mission.Current == null && Game.Current.GameStateManager.ActiveState is MapState
                    && PlayerEncounter.Current == encounter) PlayerEncounter.Finish(true);
                Pause();
            }
        }

        private static IEnumerator ObserveMission(ProbeContext context, string name, bool combat, bool naval)
        {
            Mission owned = null;
            bool requested = false;
            double start = context.Seconds;
            try
            {
                while (true)
                {
                    RequireNoSave();
                    var state = Game.Current.GameStateManager.ActiveState as MissionState;
                    if (state != null && state.CurrentMission == Mission.Current && Mission.Current != null)
                    {
                        owned = Mission.Current;
                        if (owned.MissionEnded) throw new InvalidOperationException(name + " mission ended before readiness.");
                        if (owned.Scene != null && owned.Scene.IsLoadingFinished() && owned.MainAgent != null
                            && IsMissionScreen() && TaleWorlds.Engine.Utilities.GetNumberOfShaderCompilationsInProgress() == 0
                            && (!combat || (owned.InitialPlayerAgent != null && owned.PlayerTeam != null && owned.PlayerEnemyTeam != null))
                            && (!naval || ShipCount(owned) >= 2)) break;
                    }
                    if (context.Seconds - start > 180) throw new TimeoutException(name + " mission readiness timed out.");
                    yield return null;
                }
                context.Outputs[name + "CombatMissionReady"] = new {
                    scene = owned.Scene.GetName(), module = owned.Scene.GetModulePath(), loadSeconds = context.Seconds - start,
                    screen = ScreenManager.TopScreen.GetType().FullName, shipObjects = ShipCount(owned)
                };
                if (combat && !owned.IsDeploymentFinished)
                {
                    // Same public base handler used by field and NavalDLC deployment; no Naval reference.
                    yield return null;
                    RequireMission(owned);
                    var handler = owned.GetMissionBehavior<DeploymentHandler>();
                    if (handler == null) throw new InvalidOperationException("Deployment handler missing.");
                    context.Stage(name + "_diagnostic_finish_deployment");
                    handler.FinishDeployment();
                }
                start = context.Seconds;
                while (combat && !owned.IsDeploymentFinished)
                {
                    RequireMission(owned);
                    if (context.Seconds - start > 30) throw new TimeoutException(name + " deployment did not finish.");
                    yield return null;
                }
                double missionTimeBefore=owned.CurrentTime;
                var sample = BaselineScenarioProbe.Sample(context, name + "CombatMissionTicks", 5);
                while (sample.MoveNext()) { RequireMission(owned); yield return sample.Current; }
                double missionTimeAdvanced=owned.CurrentTime-missionTimeBefore;
                context.Outputs[name + "CombatState"] = new {
                    deploymentFinished = owned.IsDeploymentFinished, agents = owned.Agents.Count,
                    ships = ShipCount(owned), missionResultPresent = owned.MissionResult != null,
                    missionTimeBefore=missionTimeBefore, missionTimeAdvanced=missionTimeAdvanced
                };
                context.Checkpoint();
                if (combat && missionTimeAdvanced<2.5)
                    throw new InvalidOperationException(name+" mission simulation did not advance during the five-second sample.");
                var capture = Capture(context, "baseline-" + name + "-combat");
                while (capture.MoveNext()) { RequireMission(owned); yield return capture.Current; }
                context.Stage(name + "_diagnostic_retreat");
                // This is the installed affirmative retreat callback: OnBattleEnded -> RetreatMission -> EndMission.
                if (combat) owned.OnEndMissionResult(); else owned.EndMission();
                requested = true;
                start = context.Seconds;
                while (Mission.Current != null || !(Game.Current.GameStateManager.ActiveState is MapState) || IsMissionScreen())
                {
                    RequireNoSave();
                    if (context.Seconds - start > 90) throw new TimeoutException(name + " mission teardown timed out.");
                    yield return null;
                }
            }
            finally
            {
                if (!requested && owned != null && Mission.Current == owned && !owned.MissionEnded)
                    owned.EndMission(); // Failure teardown only; never synthesize a battle result.
            }
        }

        private static MobileParty FindEnemy(ProbeContext context, string identityKey)
        {
            var main = MobileParty.MainParty;
            var enemy = MobileParty.All.Where(p => p != main && p.IsActive && p.IsBandit && p.MapEvent == null
                && p.CurrentSettlement == null && p.Army == null && p.AttachedTo == null && p.BesiegerCamp == null
                && !p.IsCurrentlyAtSea && p.Position.IsOnLand && p.Ships.Count == 0
                && p.MemberRoster.TotalHealthyCount > 0 && p.MemberRoster.TotalManCount <= 18
                && p.MapFaction.IsAtWarWith(main.MapFaction)
                && (!Replay(context) || p.StringId == context.Str(identityKey, "")))
                .OrderBy(p => DistanceSquared(main.Position, p.Position)).FirstOrDefault();
            if (enemy == null)
            {
                var rejected = MobileParty.All.FirstOrDefault(p => p.StringId == context.Str(identityKey, ""));
                context.Outputs[identityKey + "RejectedState"] = rejected == null ? (object)"not found" : new {
                    active = rejected.IsActive, bandit = rejected.IsBandit, mapEvent = rejected.MapEvent != null,
                    settlement = rejected.CurrentSettlement?.StringId, army = rejected.Army != null,
                    attached = rejected.AttachedTo != null, besieging = rejected.BesiegerCamp != null,
                    atSea = rejected.IsCurrentlyAtSea, onLand = rejected.Position.IsOnLand, ships = rejected.Ships.Count,
                    healthy = rejected.MemberRoster.TotalHealthyCount, total = rejected.MemberRoster.TotalManCount,
                    hostile = rejected.MapFaction.IsAtWarWith(main.MapFaction)
                };
                context.Checkpoint();
                throw new InvalidOperationException("No eligible hostile idle land bandit (1-18 troops, no ships): " + context.Str(identityKey, "nearest") + "; replay never substitutes another party.");
            }
            return enemy;
        }

        private static bool Replay(ProbeContext context) { return context.Bool("replay", false); }

        private static void ValidateReplay(ProbeContext context)
        {
            if (!Replay(context))
            {
                if (context.Args.Keys.Any(k => k.StartsWith("replay", StringComparison.Ordinal) && k != "replay"))
                    throw new ArgumentException("Replay arguments require replay=true.");
                return;
            }
            var required = new[] { "replaySourceSha256", "replaySourceModuleSha256", "replayScene", "replaySceneModule", "replaySaveName", "replaySaveSha256",
                "replayFieldEnemyId", "replayNavalEnemyId", "replayPortId", "replayStartX", "replayStartY", "replayCampaignHours",
                "replayFieldX", "replayFieldY", "replayPortGateX", "replayPortGateY", "replaySeaStartX", "replaySeaStartY",
                "replaySeaTargetX", "replaySeaTargetY", "replaySeaEncounterX", "replaySeaEncounterY", "replayNavalX", "replayNavalY" };
            foreach (string key in required)
                if (!context.Has(key) || string.IsNullOrWhiteSpace(context.Str(key, ""))) throw new ArgumentException("Missing replay input: " + key);
            context.Outputs["combatReplayInputs"] = required.ToDictionary(k => k, k => context.Args[k]);
            context.Outputs["combatReplayScope"] = "Pinned save, party identities, field placement, port, sea route target and naval encounter positions. Ten-second wall travel remains measured; post-travel player relocation is synthetic. No deterministic physics claim.";
            context.Checkpoint();
            RequireIdleMap();
            var scene = ((SandBox.MapScene)Campaign.Current.MapSceneWrapper).Scene;
            if (!string.Equals(scene.GetName(), context.Str("replayScene", ""), StringComparison.Ordinal)
                || !string.Equals(scene.GetModulePath(), context.Str("replaySceneModule", ""), StringComparison.Ordinal))
                throw new InvalidOperationException("Replay campaign scene/module identity mismatch.");
            if (context.Str("saveName", "") != context.Str("replaySaveName", "")) throw new InvalidOperationException("Replay save name mismatch.");
            string savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord", "Game Saves", context.Str("replaySaveName", "") + ".sav");
            using (var stream = File.OpenRead(savePath))
            using (var sha = System.Security.Cryptography.SHA256.Create())
                if (!string.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""), context.Str("replaySaveSha256", ""), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Replay save bytes differ from recorded prelaunch snapshot.");
            RequirePosition(MobileParty.MainParty.Position, ReplayPoint(context, "replayStart", MobileParty.NavigationType.Default), "loaded position");
            double hours = context.Num("replayCampaignHours", double.NaN);
            if (double.IsNaN(hours) || double.IsInfinity(hours) || Math.Abs(CampaignTime.Now.ToHours - hours) > 0.000001)
                throw new InvalidOperationException("Replay loaded campaign time mismatch.");
            // Validate all supplied faces before any synthetic grants or movement.
            ReplayPoint(context, "replayField", MobileParty.NavigationType.Default);
            ReplayPoint(context, "replayPortGate", MobileParty.NavigationType.Default);
            foreach (string key in new[] { "replaySeaStart", "replaySeaTarget", "replaySeaEncounter", "replayNaval" })
                ReplayPoint(context, key, MobileParty.NavigationType.Naval);
        }

        private static void RequirePosition(CampaignVec2 actual, CampaignVec2 expected, string label)
        {
            // JSON's decimal float representation may round by a few ULPs.
            if (actual.IsOnLand != expected.IsOnLand || DistanceSquared(actual, expected) > 0.000001)
                throw new InvalidOperationException("Replay " + label + " mismatch (tolerance 0.001 map units).");
        }

        private static CampaignVec2 ReplayPoint(ProbeContext context, string prefix, MobileParty.NavigationType navigation)
        {
            double x = context.Num(prefix + "X", double.NaN), y = context.Num(prefix + "Y", double.NaN);
            if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y)
                || Math.Abs(x) > float.MaxValue || Math.Abs(y) > float.MaxValue)
                throw new ArgumentException("Non-finite/out-of-range replay coordinates: " + prefix);
            var point = new CampaignVec2(new TaleWorlds.Library.Vec2((float)x, (float)y), navigation == MobileParty.NavigationType.Default);
            if (!point.IsValid() || !point.Face.IsValid()
                || !Campaign.Current.Models.PartyNavigationModel.IsTerrainTypeValidForNavigationType(
                    Campaign.Current.MapSceneWrapper.GetFaceTerrainType(point.Face), navigation))
                throw new InvalidOperationException("Replay face unavailable or invalid navigation: " + prefix);
            return point;
        }

        private static CampaignVec2 FixturePoint(ProbeContext context, string prefix, CampaignVec2 center, MobileParty.NavigationType navigation, float max, float min)
        {
            if (!Replay(context)) return FindPoint(center, navigation, max, min);
            var point = ReplayPoint(context, prefix, navigation);
            double distance = Math.Sqrt(DistanceSquared(center, point));
            if (!center.Face.IsValid() || distance < min - 0.001 || distance > max + 0.001)
                throw new InvalidOperationException("Replay placement is outside the fixture radius: " + prefix);
            var models = Campaign.Current.Models;
            float pathDistance;
            // Same public path check and navigation restrictions used by NavigationHelper, without random selection.
            if (!Campaign.Current.MapSceneWrapper.GetPathDistanceBetweenAIFaces(center.Face, point.Face,
                center.ToVec2(), point.ToVec2(), 0.3f, max, out pathDistance,
                models.PartyNavigationModel.GetInvalidTerrainTypesForNavigationType(navigation),
                models.MapDistanceModel.RegionSwitchCostFromLandToSea, models.MapDistanceModel.RegionSwitchCostFromSeaToLand))
                throw new InvalidOperationException("Replay point has no permitted path: " + prefix);
            return point;
        }

        private static void AddCrew(ProbeContext context, MobileParty party, string name)
        {
            RequireNoSave();
            int before = party.MemberRoster.TotalHealthyCount;
            int count = Math.Max(0, 12 - before);
            if (party.MemberRoster.TotalManCount + count > 18)
                throw new InvalidOperationException("Diagnostic crew exceeds the selected stock hull capacity.");
            var troop = MBObjectManager.Instance.GetObject<CharacterObject>("imperial_recruit");
            if (troop == null) throw new InvalidOperationException("Official imperial_recruit definition missing.");
            if (count > 0) party.MemberRoster.AddToCounts(troop, count, false, 0, 0, true, -1);
            context.Outputs[name + "TroopGrant"] = new { party = party.StringId, troop = troop.StringId, added = count, healthyBefore = before, healthyAfter = party.MemberRoster.TotalHealthyCount, synthetic = true };
            context.Checkpoint();
        }

        private static void GrantShip(ProbeContext context, MobileParty party, string name)
        {
            RequireNoSave();
            if (party.Ships.Count != 0) throw new InvalidOperationException("Refusing to modify an existing fleet.");
            var hull = MBObjectManager.Instance.GetObject<ShipHull>("northern_trade_ship");
            if (hull == null) throw new InvalidOperationException("Official northern_trade_ship hull missing.");
            var ship = new Ship(hull);
            if (party.MemberRoster.TotalHealthyCount < ship.SkeletalCrewCapacity || party.MemberRoster.TotalManCount > ship.TotalCrewCapacity)
                throw new InvalidOperationException("Crew does not fit the loaded hull definition.");
            ChangeShipOwnerAction.ApplyByTransferring(party.Party, ship);
            context.Outputs[name + "ShipGrant"] = new { party = party.StringId, hull = hull.StringId, skeletalCrew = ship.SkeletalCrewCapacity, capacity = ship.TotalCrewCapacity, synthetic = true, purchased = false };
            context.Checkpoint();
        }

        private static CampaignVec2 FindPoint(CampaignVec2 center, MobileParty.NavigationType navigation, float max, float min)
        {
            var point = NavigationHelper.FindPointAroundPosition(center, navigation, max, min, true, false);
            if (!point.IsValid() || !point.Face.IsValid() || DistanceSquared(center, point) < 0.01
                || point.IsOnLand != (navigation == MobileParty.NavigationType.Default)
                || !Campaign.Current.Models.PartyNavigationModel.IsTerrainTypeValidForNavigationType(
                    Campaign.Current.MapSceneWrapper.GetFaceTerrainType(point.Face), navigation))
                throw new InvalidOperationException("No valid reachable diagnostic navigation point.");
            return point;
        }

        private static IEnumerator WaitMenu(ProbeContext context, string id)
        {
            double start = context.Seconds;
            while (true)
            {
                RequireNoSave();
                var map = Game.Current.GameStateManager.ActiveState as MapState;
                if (map != null && map.MenuContext != null && map.MenuContext.GameMenu != null && map.MenuContext.GameMenu.StringId == id) yield break;
                if (context.Seconds - start > 30) throw new TimeoutException("Expected official menu " + id);
                yield return null;
            }
        }

        private static void RunOption(string id)
        {
            RequireNoSave();
            var map = Game.Current.GameStateManager.ActiveState as MapState;
            var context = map == null ? null : map.MenuContext;
            if (context == null || context.GameMenu == null) throw new InvalidOperationException("No menu for " + id);
            var menu = context.GameMenu;
            var options = menu.MenuOptions.ToList();
            int index = options.FindIndex(o => o.IdString == id);
            if (index < 0 || index >= menu.MenuItemAmount || menu.GetMenuOptionIdString(index) != id
                || !options[index].GetConditionsHold(Game.Current, context) || !options[index].IsEnabled)
                throw new InvalidOperationException("Official menu option unavailable: " + id);
            menu.RunMenuOptionConsequence(context, index);
        }

        private static IEnumerator Capture(ProbeContext context, string name)
        {
            BaselineScenarioProbe.Capture(context, name);
            var path = (string)context.Outputs[name + "Screenshot"];
            double start = context.Seconds;
            do
            {
                RequireNoSave();
                yield return null;
                if (context.Seconds - start > 30) throw new TimeoutException("Screenshot was not written: " + name);
            } while (!File.Exists(path) || new FileInfo(path).Length == 0);
        }

        private static void RequireIdleMap()
        {
            RequireNoSave();
            var p = MobileParty.MainParty;
            if (!(Game.Current.GameStateManager.ActiveState is MapState) || Mission.Current != null
                || PlayerEncounter.Current != null || p.MapEvent != null || p.CurrentSettlement != null || p.Army != null)
                throw new InvalidOperationException("Combat fixture requires an idle independent party on the map.");
        }

        private static void RequireNoSave()
        {
            var manager = Campaign.Current.SandBoxManager.SandBoxSaveManager;
            if (manager == null || !manager.IsAutoSaveDisabled() || manager.GetAutoSaveInterval() >= 0)
                throw new InvalidOperationException("Combat fixture requires the root read-only load/save guard.");
        }

        private static void RequireMission(Mission mission)
        {
            RequireNoSave();
            var state = Game.Current.GameStateManager.ActiveState as MissionState;
            if (Mission.Current != mission || state == null || state.CurrentMission != mission || mission.MissionEnded || !IsMissionScreen())
                throw new InvalidOperationException("Mission changed or ended during diagnostic measurement.");
        }

        private static bool IsMissionScreen()
        {
            var type = ScreenManager.TopScreen == null ? null : ScreenManager.TopScreen.GetType();
            for (; type != null; type = type.BaseType)
                if (type.FullName == "TaleWorlds.MountAndBlade.View.Screens.MissionScreen") return true;
            return false;
        }

        private static int ShipCount(Mission mission)
        { return mission.MissionObjects.Count(o => o.GetType().FullName == "NavalDLC.Missions.Objects.MissionShip"); }

        private static void Pause()
        { Campaign.Current.SetTimeControlModeLock(false); Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop; Campaign.Current.SetTimeControlModeLock(true); }

        private static double DistanceSquared(CampaignVec2 a, CampaignVec2 b)
        { double x = a.X - b.X, y = a.Y - b.Y; return x * x + y * y; }
    }
}
