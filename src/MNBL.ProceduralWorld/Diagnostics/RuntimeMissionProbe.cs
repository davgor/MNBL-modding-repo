using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Helpers;
using MNBL.ProceduralWorld.Automation;
using MNBL.ProceduralWorld.CampaignBootstrap;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;
using TaleWorlds.ScreenSystem;

namespace MNBL.ProceduralWorld.Diagnostics
{
    // MMR-60: a land field battle, a generated-settlement location and a naval battle (with retreat), each entered from
    // and returned to a generated world through the installed campaign paths:
    //   field/naval: PlayerEncounter.RestartPlayerEncounter -> StartBattle -> "encounter" menu "attack"
    //                -> MenuHelper.EncounterAttackConsequence -> DefaultSceneModel.GetBattleSceneForMapPatch(
    //                   IMapScene.GetMapPatchAtPosition(main party), naval) -> CampaignMission.Open(Naval)BattleMission;
    //                retreat through Mission.OnEndMissionResult (OnBattleEnded -> RetreatMission -> EndMission).
    //   settlement:  EncounterManager.StartSettlementEncounter -> "town" menu "town_streets" -> LocationComplex scene.
    // Every scene the engine opens is compared with the scene the generated battle-index map names for that patch.
    // Synthetic and recorded: a war declaration (generated worlds have no bandits), party relocations, a crew and a ship.
    internal static class RuntimeMissionProbe
    {
        internal static IEnumerator Run(ProbeContext context, DiagnosticSandboxBootstrap boot, string worldId, int worldIndex,
            Dictionary<string, object> identity, Dictionary<string, object> report)
        {
            var failures = new List<string>(); report["failures"] = failures;
            var campaign = Campaign.Current;
            report["identity"] = identity;
            report["contract"] = new Dictionary<string, object> {
                ["sceneModel"] = campaign.Models.SceneModel.GetType().FullName,
                ["mapScene"] = campaign.MapSceneWrapper.GetType().FullName,
                ["battleScenePath"] = "MenuHelper.EncounterAttackConsequence -> SceneModel.GetBattleSceneForMapPatch(IMapScene.GetMapPatchAtPosition(MobileParty.MainParty.Position), isNaval) -> CampaignMission.OpenBattleMission / OpenNavalBattleMission (MissionInitializerRecord)",
                ["patchReader"] = "SandBox.MapScene.GetMapPatchAtPosition: cell = floor(position / _terrainSize * (width, height)); sceneIndex = map[2*cell], patch = (map[2*cell+1] & 15, map[2*cell+1] >> 4) / 15",
                ["sceneRule"] = "Scenes whose MapIndices contain sceneIndex and IsNaval == naval: one -> that scene; several naval -> those whose Terrain equals the terrain at the party, else random among them; several land -> random with FailedAssert; none -> silently random among scenes of the party's terrain (FailedAssert only if no scene has that terrain)",
                ["locationPath"] = "Settlement.LocationComplex location scene (generated settlements reuse prototype locations)" };
            report["battleIndexMap"] = BattleMap();
            report["fingerprintBefore"] = Fingerprint(worldId);
            context.Checkpoint();

            var main = MobileParty.MainParty;
            var town1 = Settlement.Find(worldId + "_town_1");
            var town2 = Settlement.Find(worldId + "_town_2");
            if (main.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(main);
            Pause();
            // Generated worlds carry no bandits and no wars: the enemy is the kingdom that does not own town_1, so
            // town_1 stays peaceful for the settlement visit and its port for the naval departure.
            var enemyFaction = Kingdom.All.FirstOrDefault(k => k != town1.MapFaction && !k.IsEliminated);
            if (enemyFaction == null) { failures.Add("No second kingdom to be at war with."); yield break; }
            bool warBefore = enemyFaction.IsAtWarWith(main.MapFaction);
            if (!warBefore) DeclareWarAction.ApplyByDefault(Clan.PlayerClan, enemyFaction);
            report["syntheticWar"] = new Dictionary<string, object> {
                ["playerFaction"] = main.MapFaction.StringId, ["enemyFaction"] = enemyFaction.StringId, ["alreadyAtWar"] = warBefore,
                ["atWarNow"] = enemyFaction.IsAtWarWith(main.MapFaction), ["town1Faction"] = town1.MapFaction.StringId,
                ["action"] = "DeclareWarAction.ApplyByDefault(Clan.PlayerClan, enemy kingdom)" };
            context.Checkpoint();

            foreach (var phase in new[] { "field", "settlement", "naval" })
            {
                var row = new Dictionary<string, object>(); report[phase] = row;
                IEnumerator steps = phase == "field" ? Field(context, worldId, worldIndex, row)
                                  : phase == "settlement" ? Visit(context, worldId, worldIndex, town1, row)
                                  : Naval(context, worldId, worldIndex, town1, row);
                bool ok = true;
                Exception error = null;
                var driven = Drive(boot, steps);
                while (true)
                {
                    try { if (!driven.MoveNext()) break; }
                    catch (Exception e) { error = e; ok = false; break; }
                    yield return driven.Current;
                }
                if (error != null) { row["error"] = error.ToString(); failures.Add(phase + ": " + error.Message); }
                else
                {
                    var phaseFailures = row.ContainsKey("failures") ? (List<string>)row["failures"] : new List<string>();
                    failures.AddRange(phaseFailures.Select(f => phase + ": " + f));
                    ok = phaseFailures.Count == 0;
                }
                row["passed"] = ok;
                context.Checkpoint();
                // A broken mission state would make the next phase meaningless.
                if (error != null && (Mission.Current != null || !(Game.Current.GameStateManager.ActiveState is MapState))) break;
                if (error != null && PlayerEncounter.Current != null && Mission.Current == null) PlayerEncounter.Finish(true);
                Pause();
            }

            var after = Fingerprint(worldId);
            report["fingerprintAfter"] = after;
            var before = (Dictionary<string, object>)report["fingerprintBefore"];
            foreach (var key in new[] { "mapSceneWrapper", "sceneName", "scenePointer", "battleIndexSha256", "settlements", "navigationFaces",
                "landRouteTown1ToTown2", "seaRouteTown1ToTown2", "waterPlane", "heroCount", "parties" })
                if (!Equal(before[key], after[key])) failures.Add("World state changed across missions: " + key);
            report["worldPreserved"] = failures.All(f => !f.StartsWith("World state changed", StringComparison.Ordinal));
            context.Checkpoint();
        }

        private static IEnumerator Drive(DiagnosticSandboxBootstrap boot, IEnumerator steps)
        {
            try
            {
                while (true)
                {
                    boot.Tick(); // re-pauses on the map (HoldPaused); a no-op inside missions
                    if (!steps.MoveNext()) yield break;
                    yield return steps.Current;
                }
            }
            finally { (steps as IDisposable)?.Dispose(); }
        }

        // ---- phases ----

        private static IEnumerator Field(ProbeContext context, string worldId, int worldIndex, Dictionary<string, object> row)
        {
            var failures = new List<string>(); row["failures"] = failures;
            RequireIdleMap();
            var main = MobileParty.MainParty;
            if (main.IsCurrentlyAtSea || !main.Position.IsOnLand) throw new InvalidOperationException("Field battle needs a land start.");
            var enemy = FindHostile(false);
            var point = FindPoint(main.Position, MobileParty.NavigationType.Default, 2, 1);
            row["setup"] = new Dictionary<string, object> {
                ["enemy"] = enemy.StringId, ["enemyFaction"] = enemy.MapFaction.StringId, ["enemyTroops"] = enemy.MemberRoster.TotalManCount,
                ["enemyFrom"] = Xy(enemy.Position), ["enemyTo"] = Xy(point), ["synthetic"] = "existing hostile party relocated next to the player" };
            enemy.Position = point;
            enemy.SetMoveModeHold();
            var settle = SettleCamera(context);
            while (settle.MoveNext()) yield return settle.Current;
            row["before"] = Snapshot(false);
            var steps = Encounter(context, enemy, false, "field", worldIndex, row);
            while (steps.MoveNext()) yield return steps.Current;
            settle = SettleCamera(context);
            while (settle.MoveNext()) yield return settle.Current;
            row["after"] = Snapshot(false);
            CompareReturn(row, failures, false);
        }

        private static IEnumerator Visit(ProbeContext context, string worldId, int worldIndex, Settlement town, Dictionary<string, object> row)
        {
            var failures = new List<string>(); row["failures"] = failures;
            RequireIdleMap();
            var party = MobileParty.MainParty;
            if (town.MapFaction.IsAtWarWith(party.MapFaction)) throw new InvalidOperationException("Settlement visit needs a peaceful town.");
            var center = town.LocationComplex == null ? null : town.LocationComplex.GetLocationWithId("center");
            if (center == null) failures.Add("Generated town has no 'center' location; the location scene cannot be checked.");
            row["setup"] = new Dictionary<string, object> {
                ["settlement"] = town.StringId, ["name"] = town.Name.ToString(), ["from"] = Xy(party.Position), ["gate"] = Xy(town.GatePosition),
                ["expectedScene"] = center == null ? null : center.GetSceneName(town.Town == null ? 0 : town.Town.GetWallLevel()),
                ["synthetic"] = "player placed at the generated gate; official menus from there" };
            party.SetMoveModeHold();
            party.Position = town.GatePosition;
            var settle = SettleCamera(context);
            while (settle.MoveNext()) yield return settle.Current;
            row["before"] = Snapshot(false);
            EncounterManager.StartSettlementEncounter(party, town);
            var wait = WaitMenu(context, "town");
            while (wait.MoveNext()) yield return wait.Current;
            context.Stage("mission_probe_" + worldIndex + "_settlement_entry");
            RunOption("town_streets");
            var steps = ObserveMission(context, "settlement", false, false, worldIndex, row);
            while (steps.MoveNext()) yield return steps.Current;
            wait = WaitMenu(context, "town");
            while (wait.MoveNext()) yield return wait.Current;
            RequireNoSave();
            RunOption("town_leave");
            double start = context.Seconds;
            while (party.CurrentSettlement != null || PlayerEncounter.Current != null || !(Game.Current.GameStateManager.ActiveState is MapState))
            {
                if (context.Seconds - start > 30) throw new TimeoutException("Settlement leave did not complete.");
                yield return null;
            }
            Pause();
            settle = SettleCamera(context);
            while (settle.MoveNext()) yield return settle.Current;
            row["after"] = Snapshot(false);
            var mission = (Dictionary<string, object>)row["mission"];
            if (center != null && !String.Equals((string)mission["scene"], (string)((Dictionary<string, object>)row["setup"])["expectedScene"], StringComparison.Ordinal))
                failures.Add("Location scene " + mission["scene"] + " differs from the settlement's center scene.");
            CompareReturn(row, failures, false);
        }

        private static IEnumerator Naval(ProbeContext context, string worldId, int worldIndex, Settlement town, Dictionary<string, object> row)
        {
            var failures = new List<string>(); row["failures"] = failures;
            RequireIdleMap();
            var main = MobileParty.MainParty;
            var hull = Kingdom.All.Select(k => k.Culture).Concat(new[] { town.Culture }).Where(c => c != null)
                .SelectMany(c => c.AvailableShipHulls).FirstOrDefault();
            if (hull == null) throw new InvalidOperationException("No ship hull available from generated cultures.");
            var ship = new Ship(hull);
            int crew = AddCrew(main, ship.SkeletalCrewCapacity);
            if (main.Ships.Count == 0) ChangeShipOwnerAction.ApplyByLooting(PartyBase.MainParty, ship);
            main.SetSailAtPosition(town.PortPosition);
            Pause();
            yield return null;
            if (!main.IsCurrentlyAtSea) throw new InvalidOperationException("Player did not reach the sea from the generated port.");
            // An attacker/defender pair next to the west map edge: canonical patch sampling at the edge (no periodic
            // seam exists in these worlds; east/west connection is MMR-63/64).
            var edge = EdgeSeaPoint(main.Position.Y);
            row["setup"] = new Dictionary<string, object> {
                ["hull"] = hull.StringId, ["crewAdded"] = crew, ["ships"] = main.Ships.Count, ["port"] = Xy(town.PortPosition),
                ["edgePoint"] = edge.IsValid() ? Xy(edge) : null,
                ["synthetic"] = "crew and ship granted, departure from the generated port by SetSailAtPosition, both parties relocated at sea next to the west edge" };
            if (!edge.IsValid()) throw new InvalidOperationException("No navigable sea within 200 units of the west edge on the player's row.");
            main.SetSailAtPosition(edge); main.SetMoveModeHold();
            var enemy = FindHostile(true);
            var enemyPoint = FindPoint(main.Position, MobileParty.NavigationType.Naval, 2, 1);
            ((Dictionary<string, object>)row["setup"])["enemy"] = enemy.StringId;
            ((Dictionary<string, object>)row["setup"])["enemyShips"] = enemy.Ships.Count;
            ((Dictionary<string, object>)row["setup"])["enemyFrom"] = Xy(enemy.Position);
            ((Dictionary<string, object>)row["setup"])["enemyTo"] = Xy(enemyPoint);
            if (enemy.Ships.Count == 0) throw new InvalidOperationException("Hostile naval party has no ships.");
            enemy.SetSailAtPosition(enemyPoint);
            enemy.SetMoveModeHold();
            var edgeSamples = EdgeSamples(main.Position.Y);
            row["edgePatchSamples"] = edgeSamples;
            // Canonical (clamped) sampling: points past either edge read that edge's cell; no east/west wrap exists yet.
            Func<int, string> cell = i => { var e = (Dictionary<string, object>)edgeSamples[i]; var c = (float[])e["coordinates"]; return e["sceneIndex"] + ":" + c[0] + "," + c[1]; };
            if (cell(0) != cell(5) || cell(3) != cell(4)) failures.Add("Patch sampling past the map edge does not clamp to the edge cell.");
            var settle = SettleCamera(context); // the map camera glides to the relocated party
            while (settle.MoveNext()) yield return settle.Current;
            row["before"] = Snapshot(true);
            var steps = Encounter(context, enemy, true, "naval", worldIndex, row);
            while (steps.MoveNext()) yield return steps.Current;
            settle = SettleCamera(context);
            while (settle.MoveNext()) yield return settle.Current;
            row["after"] = Snapshot(true);
            CompareReturn(row, failures, true);
        }

        // Waits (up to 15 s) until the map camera frame itself has stopped moving (origin moves < 0.002 per frame for
        // 30 consecutive frames) and follows the party (see Camera). The outcome goes into the next snapshot.
        private static Dictionary<string, object> lastSettle;
        internal static IEnumerator SettleCamera(ProbeContext context)
        {
            double start = context.Seconds;
            float[] last = null;
            int steady = 0;
            lastSettle = new Dictionary<string, object> { ["timedOut"] = true };
            while (context.Seconds - start < 15)
            {
                var camera = Camera() as Dictionary<string, object>;
                if (camera != null && camera.ContainsKey("frameOrigin"))
                {
                    var origin = (float[])camera["frameOrigin"];
                    bool still = last != null && Math.Abs(origin[0] - last[0]) < .002f && Math.Abs(origin[1] - last[1]) < .002f && Math.Abs(origin[2] - last[2]) < .002f;
                    steady = still && camera.ContainsKey("frameFollowsParty") && (bool)camera["frameFollowsParty"] ? steady + 1 : 0;
                    last = origin;
                    if (steady >= 30) { lastSettle = new Dictionary<string, object> { ["timedOut"] = false, ["seconds"] = context.Seconds - start }; yield break; }
                }
                yield return null;
            }
            lastSettle["seconds"] = context.Seconds - start;
        }

        // ---- shared mission plumbing (installed official paths; mirrors Automation/BaselineCombatSteps) ----

        private static IEnumerator Encounter(ProbeContext context, MobileParty enemy, bool naval, string name, int worldIndex, Dictionary<string, object> row)
        {
            RequireIdleMap();
            context.Stage("mission_probe_" + worldIndex + "_" + name + "_encounter");
            PlayerEncounter.RestartPlayerEncounter(enemy.Party, MobileParty.MainParty.Party, true, false);
            var encounter = PlayerEncounter.Current;
            try
            {
                var battle = PlayerEncounter.StartBattle();
                if (battle == null || battle.IsNavalMapEvent != naval) throw new InvalidOperationException("Map event has the wrong navigation type.");
                row["mapEvent"] = new Dictionary<string, object> {
                    ["naval"] = battle.IsNavalMapEvent,
                    ["involved"] = battle.InvolvedParties.Select(p => p.MobileParty == null ? "settlement:" + (p.Settlement == null ? "" : p.Settlement.StringId) : p.MobileParty.StringId).ToArray() };
                if (battle.InvolvedParties.Count() != 2) throw new InvalidOperationException("Other parties joined the encounter.");
                GameMenu.ActivateGameMenu("encounter");
                var wait = WaitMenu(context, "encounter");
                while (wait.MoveNext()) yield return wait.Current;
                RunOption("attack");
                var steps = ObserveMission(context, name, true, naval, worldIndex, row);
                while (steps.MoveNext()) yield return steps.Current;
                RequireNoSave();
                if (PlayerEncounter.Current != null)
                {
                    if (PlayerEncounter.Current != encounter) throw new InvalidOperationException("Another encounter replaced the probe encounter.");
                    PlayerEncounter.Finish(true);
                }
                double start = context.Seconds;
                while (PlayerEncounter.Current != null || MobileParty.MainParty.MapEvent != null || !(Game.Current.GameStateManager.ActiveState is MapState))
                {
                    if (context.Seconds - start > 30) throw new TimeoutException(name + " encounter cleanup timed out.");
                    yield return null;
                }
                Pause();
            }
            finally
            {
                if (Mission.Current == null && Game.Current.GameStateManager.ActiveState is MapState && PlayerEncounter.Current == encounter && encounter != null)
                    PlayerEncounter.Finish(true);
                Pause();
            }
        }

        internal static IEnumerator ObserveMission(ProbeContext context, string name, bool combat, bool naval, int worldIndex, Dictionary<string, object> row)
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
                        if (owned.Scene != null && owned.Scene.IsLoadingFinished() && owned.MainAgent != null && IsMissionScreen()
                            && TaleWorlds.Engine.Utilities.GetNumberOfShaderCompilationsInProgress() == 0
                            && (!combat || (owned.InitialPlayerAgent != null && owned.PlayerTeam != null && owned.PlayerEnemyTeam != null))
                            && (!naval || ShipCount(owned) >= 2)) break;
                    }
                    if (context.Seconds - start > 180) throw new TimeoutException(name + " mission readiness timed out.");
                    yield return null;
                }
                var info = MissionInfo(owned, state: Game.Current.GameStateManager.ActiveState);
                info["combat"] = combat; // battle missions are chosen from the map patch; location missions are not
                info["loadSeconds"] = context.Seconds - start;
                info["ships"] = ShipCount(owned);
                row["mission"] = info;
                if (combat && !owned.IsDeploymentFinished)
                {
                    yield return null;
                    var handler = owned.GetMissionBehavior<DeploymentHandler>();
                    if (handler == null) throw new InvalidOperationException("Deployment handler missing.");
                    handler.FinishDeployment();
                }
                start = context.Seconds;
                while (combat && !owned.IsDeploymentFinished)
                {
                    if (context.Seconds - start > 30) throw new TimeoutException(name + " deployment did not finish.");
                    yield return null;
                }
                double t0 = owned.CurrentTime;
                start = context.Seconds;
                while (context.Seconds - start < 3) { RequireMission(owned); yield return null; }
                info["missionTimeAdvanced"] = owned.CurrentTime - t0;
                info["agents"] = owned.Agents.Count;
                string shot = Path.Combine(context.Directory, "mission-" + context.ProcessId + "-" + worldIndex + "-" + name + ".bmp");
                TaleWorlds.Engine.Utilities.TakeScreenshot(shot);
                context.Outputs["world" + worldIndex + Char.ToUpperInvariant(name[0]) + name.Substring(1) + "MissionScreenshot"] = shot;
                start = context.Seconds;
                do
                {
                    yield return null;
                    RequireMission(owned);
                    if (context.Seconds - start > 30) throw new TimeoutException("Mission screenshot was not written.");
                } while (!File.Exists(shot) || new FileInfo(shot).Length == 0);
                context.Stage("mission_probe_" + worldIndex + "_" + name + "_leave");
                if (combat) owned.OnEndMissionResult(); else owned.EndMission();
                requested = true;
                start = context.Seconds;
                while (Mission.Current != null || !(Game.Current.GameStateManager.ActiveState is MapState) || IsMissionScreen())
                {
                    RequireNoSave();
                    if (context.Seconds - start > 90) throw new TimeoutException(name + " mission teardown timed out.");
                    yield return null;
                }
                info["teardownSeconds"] = context.Seconds - start;
            }
            finally
            {
                if (!requested && owned != null && Mission.Current == owned && !owned.MissionEnded) owned.EndMission();
            }
        }

        // ---- observations ----

        internal static Dictionary<string, object> Snapshot(bool naval)
        {
            var main = MobileParty.MainParty;
            var map = Campaign.Current.MapSceneWrapper;
            var position = main.Position;
            var patch = map.GetMapPatchAtPosition(position);
            // The scene model's own terrain for the naval tie-break (GetEnvironmentTerrainTypesCount's out value).
            TerrainType terrainAtPosition;
            map.GetEnvironmentTerrainTypesCount(position, out terrainAtPosition);
            float height = 0;
            bool hasHeight = map.GetHeightAtPoint(position, ref height);
            var face = main.CurrentNavigationFace;
            return new Dictionary<string, object> {
                ["position"] = Xy(position), ["isOnLand"] = position.IsOnLand, ["atSea"] = main.IsCurrentlyAtSea,
                ["face"] = face.IsValid() ? new Dictionary<string, object> { ["index"] = face.FaceIndex, ["group"] = face.FaceGroupIndex,
                    ["terrain"] = ((TerrainType)face.FaceGroupIndex).ToString(), ["island"] = face.FaceIslandIndex } : null,
                ["terrainAtPosition"] = terrainAtPosition.ToString(),
                ["patch"] = new Dictionary<string, object> { ["sceneIndex"] = patch.sceneIndex, ["coordinates"] = new[] { patch.normalizedCoordinates.X, patch.normalizedCoordinates.Y } },
                ["expectedBattleScene"] = ExpectedScene(patch.sceneIndex, naval, terrainAtPosition),
                ["mapHeight"] = hasHeight ? (object)height : null,
                ["camera"] = Camera(), ["cameraSettle"] = lastSettle, ["campaignHours"] = CampaignTime.Now.ToHours,
                ["ships"] = main.Ships.Count, ["troops"] = main.MemberRoster.TotalManCount };
        }

        internal static void CompareReturn(Dictionary<string, object> row, List<string> failures, bool naval)
        {
            var before = (Dictionary<string, object>)row["before"];
            var after = (Dictionary<string, object>)row["after"];
            var mission = row.ContainsKey("mission") ? (Dictionary<string, object>)row["mission"] : null;
            var b = (float[])before["position"]; var a = (float[])after["position"];
            double moved = Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]));
            row["returnDisplacement"] = moved;
            // The vanilla return (MobileParty.TeleportPartyToOutSideOfEncounterRadius) moves the party up to 1.25 x the
            // encounter radius: land 0.5 -> 0.625, NavalDLC naval 1.5 -> 1.875.
            double bound = (naval ? 1.875 : 0.625) + 0.01;
            row["returnBound"] = bound;
            if (moved > bound) failures.Add("Returned " + moved.ToString("0.###") + " map units from the entry position (bound " + bound + ").");
            if ((bool)before["atSea"] != (bool)after["atSea"] || (bool)before["isOnLand"] != (bool)after["isOnLand"])
                failures.Add("Land/sea domain changed across the mission.");
            if (naval && !(bool)after["atSea"]) failures.Add("Naval return is not at sea.");
            foreach (var label in new[] { "before", "after" })
            {
                var camera = (label == "before" ? before : after)["camera"] as Dictionary<string, object>;
                var snapshot = label == "before" ? before : after;
                var settle = snapshot["cameraSettle"] as Dictionary<string, object>;
                if (camera == null || camera.ContainsKey("error") || !camera.ContainsKey("lateralErrorToParty")) { failures.Add("Map camera not readable " + label + " the mission."); continue; }
                if (settle == null || (bool)settle["timedOut"]) failures.Add("Map camera did not settle " + label + " the mission.");
                if (!(bool)camera["frameFollowsParty"])
                    failures.Add("Map camera does not follow the party " + label + " the mission: lateral error " + ((double)camera["lateralErrorToParty"]).ToString("0.###") +
                                 ", along offset " + ((double)camera["alongOffsetToParty"]).ToString("0.###") + ".");
            }
            var bearingBefore = ((Dictionary<string, object>)before["camera"])["bearing"];
            var bearingAfter = ((Dictionary<string, object>)after["camera"])["bearing"];
            if (!Equals(bearingBefore, bearingAfter)) failures.Add("Map camera bearing changed across the mission.");
            // Zoom must not change either, and the forward offset after return must equal the one before (this also
            // catches a return displacement straight along the view axis, which the lateral test cannot see).
            var cb = (Dictionary<string, object>)before["camera"]; var ca = (Dictionary<string, object>)after["camera"];
            if (Math.Abs(Convert.ToDouble(cb["distance"]) - Convert.ToDouble(ca["distance"])) > 1e-3) failures.Add("Map camera distance (zoom) changed across the mission.");
            if (Math.Abs((double)cb["alongOffsetToParty"] - (double)ca["alongOffsetToParty"]) > 0.05)
                failures.Add("Map camera forward offset changed across the mission: " + ((double)cb["alongOffsetToParty"]).ToString("0.###") + " -> " + ((double)ca["alongOffsetToParty"]).ToString("0.###") + ".");
            if (mission != null && row.ContainsKey("before") && before.ContainsKey("expectedBattleScene") && mission.ContainsKey("combat") && (bool)mission["combat"])
            {
                var expected = (Dictionary<string, object>)before["expectedBattleScene"];
                row["sceneMatchesPatch"] = String.Equals((string)expected["scene"], (string)mission["scene"], StringComparison.Ordinal);
                var record = mission.ContainsKey("initializerRecord") ? (Dictionary<string, object>)mission["initializerRecord"] : null;
                var patch = (Dictionary<string, object>)before["patch"];
                var face = before["face"] as Dictionary<string, object>;
                if (record == null) failures.Add("Mission initializer record not found.");
                else
                {
                    var coordinates = (float[])record["PatchCoordinates"]; var expectedCoordinates = (float[])patch["coordinates"];
                    if (!(bool)record["SceneHasMapPatch"] || Math.Abs(coordinates[0] - expectedCoordinates[0]) > 1e-6 || Math.Abs(coordinates[1] - expectedCoordinates[1]) > 1e-6)
                        failures.Add("Initializer record patch differs from the generated patch.");
                    if (face == null || (int)record["TerrainType"] != (int)face["group"])
                        failures.Add("Initializer record terrain differs from the party's generated face terrain.");
                }
                if (!(bool)expected["deterministic"]) failures.Add("Generated patch does not resolve to exactly one battle scene: " + expected["rule"]);
                if (!(bool)row["sceneMatchesPatch"]) failures.Add("Mission scene " + mission["scene"] + " differs from the generated patch's scene " + expected["scene"]);
                // MMR-42: the mismatch guard saw this battle's scene choice and found it agreeing with the generated patch.
                var guard = MNBL.ProceduralWorld.Missions.MissionScenePatches.LastOpening(mission["scene"] as string);
                row["mismatchGuard"] = guard;
                if (guard == null) failures.Add("The battle scene guard did not observe the scene choice of " + mission["scene"] + ".");
                else if (!Equals(guard["agrees"], true)) failures.Add("The battle scene guard flagged the choice: " + guard["problem"]);
            }
        }

        internal static Dictionary<string, object> MissionInfo(Mission mission, object state)
        {
            var info = new Dictionary<string, object> {
                ["scene"] = mission.Scene.GetName(), ["sceneNameProperty"] = mission.SceneName, ["module"] = mission.Scene.GetModulePath(),
                ["terrainType"] = mission.HasValidTerrainType ? mission.TerrainType.ToString() : null,
                ["hasEnemyTeam"] = mission.PlayerEnemyTeam != null, ["screen"] = ScreenManager.TopScreen.GetType().FullName };
            // The initializer record the campaign built (patch coordinates, encounter direction, terrain type).
            foreach (var holder in new object[] { mission, state })
            {
                if (holder == null) continue;
                for (var type = holder.GetType(); type != null; type = type.BaseType)
                    foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (field.FieldType.FullName == "TaleWorlds.Core.MissionInitializerRecord" && !info.ContainsKey("initializerRecord"))
                        {
                            info["initializerRecordSource"] = type.FullName + "." + field.Name;
                            info["initializerRecord"] = Fields(field.GetValue(holder));
                        }
            }
            // Template terrain around the player's agent (the mission terrain is the stock template's, not the map's).
            var samples = new List<object>();
            if (mission.MainAgent != null)
            {
                var a = mission.MainAgent.Position;
                foreach (var d in new[] { new Vec2(0, 0), new Vec2(20, 0), new Vec2(-20, 0), new Vec2(0, 20), new Vec2(0, -20) })
                {
                    var p = new Vec2(a.x + d.X, a.y + d.Y);
                    samples.Add(new Dictionary<string, object> { ["at"] = new[] { p.X, p.Y }, ["terrainHeight"] = mission.Scene.GetTerrainHeight(p, true) });
                }
            }
            info["terrainSamples"] = samples;
            if (mission.MainAgent != null) info["mainAgentPosition"] = new[] { mission.MainAgent.Position.x, mission.MainAgent.Position.y, mission.MainAgent.Position.z };
            return info;
        }

        private static Dictionary<string, object> Fields(object value)
        {
            var row = new Dictionary<string, object>();
            if (value == null) return row;
            foreach (var f in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                var v = f.GetValue(value);
                if (v is Vec2) row[f.Name] = new[] { ((Vec2)v).X, ((Vec2)v).Y };
                else if (v == null || v is string || v is bool || v is int || v is float || v is double) row[f.Name] = v;
                else if (v.GetType().IsEnum) row[f.Name] = v.ToString();
                else row[f.Name] = v.GetType().Name;
            }
            return row;
        }

        // The scene DefaultSceneModel.GetBattleSceneForMapPatch must open for this patch, by its own rule: MMR-42's one implementation
        // (BattleTable.ExpectedScene, shared with the battle map and the mismatch guard; a list naming one distinct scene is deterministic).
        private static Dictionary<string, object> ExpectedScene(int sceneIndex, bool naval, TerrainType terrainAtPosition)
        {
            var e = MNBL.ProceduralWorld.CampaignBootstrap.GeneratedBattleTerrain.Resolver().Table.ExpectedScene(sceneIndex, naval, (int)terrainAtPosition);
            return new Dictionary<string, object> { ["sceneIndex"] = sceneIndex, ["naval"] = naval, ["candidates"] = e.Candidates, ["scene"] = e.Scene, ["deterministic"] = e.Deterministic, ["rule"] = e.Rule };
        }

        private static Dictionary<string, object> BattleMap()
        {
            var map = (SandBox.MapScene)Campaign.Current.MapSceneWrapper;
            var bytes = (byte[])Field("_battleTerrainIndexMap").GetValue(map);
            return new Dictionary<string, object> {
                ["present"] = bytes != null, ["width"] = Field("_battleTerrainIndexMapWidth").GetValue(map), ["height"] = Field("_battleTerrainIndexMapHeight").GetValue(map),
                ["sha256"] = bytes == null ? null : Sha(bytes), ["terrainSize"] = new[] { ((Vec2)Field("_terrainSize").GetValue(map)).X, ((Vec2)Field("_terrainSize").GetValue(map)).Y } };
        }

        private static List<object> EdgeSamples(float y)
        {
            var map = Campaign.Current.MapSceneWrapper;
            var size = (Vec2)Field("_terrainSize").GetValue((SandBox.MapScene)map);
            var samples = new List<object>();
            foreach (float x in new[] { 0.01f, 2f, size.X - 2f, size.X - 0.01f, size.X + 5f, -5f })
            {
                var patch = map.GetMapPatchAtPosition(new CampaignVec2(new Vec2(x, y), false));
                samples.Add(new Dictionary<string, object> { ["x"] = x, ["y"] = y, ["sceneIndex"] = patch.sceneIndex, ["coordinates"] = new[] { patch.normalizedCoordinates.X, patch.normalizedCoordinates.Y } });
            }
            return samples;
        }

        private static CampaignVec2 EdgeSeaPoint(float y)
        {
            var nav = Campaign.Current.Models.PartyNavigationModel;
            for (float x = 3; x < 200; x += 1)
            {
                var p = new CampaignVec2(new Vec2(x, y), false);
                if (p.IsValid() && p.Face.IsValid() && nav.IsTerrainTypeValidForNavigationType(Campaign.Current.MapSceneWrapper.GetFaceTerrainType(p.Face), MobileParty.NavigationType.Naval))
                    return p;
            }
            return CampaignVec2.Invalid;
        }

        internal static object Camera()
        {
            try
            {
                object screen = ScreenManager.TopScreen;
                if (screen != null)
                {
                    object view = null;
                    for (var type = screen.GetType(); type != null && view == null; type = type.BaseType)
                        foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                            if (f.FieldType.Name == "MapCameraView") { view = f.GetValue(screen); break; }
                    if (view == null) return new Dictionary<string, object> { ["error"] = "no MapCameraView on " + screen.GetType().FullName };
                    Func<string, object> get = name => { var p = view.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); return p == null ? null : p.GetValue(view); };
                    var frame = (MatrixFrame)get("CameraFrame");
                    var row = new Dictionary<string, object> {
                        ["screen"] = screen.GetType().FullName, ["frameOrigin"] = new[] { frame.origin.x, frame.origin.y, frame.origin.z },
                        ["distance"] = get("CameraDistance"), ["bearing"] = get("CameraBearing"),
                        ["followMode"] = Convert.ToString(get("CurrentCameraFollowMode")) };
                    var party = MobileParty.MainParty.Position;
                    double frameDistance = Math.Sqrt((frame.origin.x - party.X) * (frame.origin.x - party.X) + (frame.origin.y - party.Y) * (frame.origin.y - party.Y));
                    row["frameDistanceToParty"] = frameDistance;
                    // Following = the party lies on the camera's horizontal view line: lateral error ~0 and the party ahead
                    // of the camera within its distance. Forward = (sin bearing, cos bearing); only bearing 0 is exercised
                    // here (camera behind the party along -y), so the bearing must not change across a mission.
                    double bearing = Convert.ToDouble(get("CameraBearing")), distance = Convert.ToDouble(get("CameraDistance"));
                    double fx = Math.Sin(bearing), fy = Math.Cos(bearing), ox = party.X - frame.origin.x, oy = party.Y - frame.origin.y;
                    double lateral = Math.Abs(ox * fy - oy * fx), along = ox * fx + oy * fy;
                    row["lateralErrorToParty"] = lateral; row["alongOffsetToParty"] = along;
                    row["frameFollowsParty"] = lateral <= 0.1 && along > 0 && along <= distance + 2.0;
                    var target = get("IdealCameraTarget");
                    if (target is Vec3)
                    {
                        var t = (Vec3)target; var p = MobileParty.MainParty.Position;
                        row["idealTarget"] = new[] { t.x, t.y, t.z };
                        row["targetDistanceToParty"] = Math.Sqrt((t.x - p.X) * (t.x - p.X) + (t.y - p.Y) * (t.y - p.Y));
                    }
                    else row["idealTarget"] = Convert.ToString(target);
                    return row;
                }
                return new Dictionary<string, object> { ["error"] = "no top screen" };
            }
            catch (Exception e) { return new Dictionary<string, object> { ["error"] = e.GetType().Name + ": " + e.Message }; }
        }

        private static Dictionary<string, object> Fingerprint(string worldId)
        {
            var map = Campaign.Current.MapSceneWrapper;
            var scene = ((SandBox.MapScene)map).Scene;
            var bytes = (byte[])Field("_battleTerrainIndexMap").GetValue((SandBox.MapScene)map);
            var town1 = Settlement.Find(worldId + "_town_1"); var town2 = Settlement.Find(worldId + "_town_2");
            var nav = Campaign.Current.Models.PartyNavigationModel; var distance = Campaign.Current.Models.MapDistanceModel;
            Func<CampaignVec2, CampaignVec2, MobileParty.NavigationType, object> route = (from, to, type) => {
                float d;
                return map.GetPathDistanceBetweenAIFaces(from.Face, to.Face, from.ToVec2(), to.ToVec2(), nav.GetEmbarkDisembarkThresholdDistance(), Campaign.PathFindingMaxCostLimit,
                    out d, nav.GetInvalidTerrainTypesForNavigationType(type), distance.RegionSwitchCostFromLandToSea, distance.RegionSwitchCostFromSeaToLand) ? (object)d : null;
            };
            var settlements = String.Join("|", Settlement.All.OrderBy(s => s.StringId, StringComparer.Ordinal)
                .Select(s => s.StringId + ":" + s.Name + ":" + s.Position.X.ToString("R") + "," + s.Position.Y.ToString("R")));
            return new Dictionary<string, object> {
                ["mapSceneWrapper"] = RuntimeHelpers.GetHashCode(map), ["sceneName"] = scene.GetName(), ["scenePointer"] = scene.Pointer.ToString(),
                ["battleIndexSha256"] = bytes == null ? null : Sha(bytes), ["settlements"] = settlements,
                ["parties"] = MobileParty.All.Count, ["heroCount"] = Hero.AllAliveHeroes.Count,
                ["navigationFaces"] = map.GetNumberOfNavigationMeshFaces(),
                ["landRouteTown1ToTown2"] = route(town1.GatePosition, town2.GatePosition, MobileParty.NavigationType.Default),
                ["seaRouteTown1ToTown2"] = route(town1.PortPosition, town2.PortPosition, MobileParty.NavigationType.Naval),
                ["waterPlane"] = scene.GetFirstEntityWithName("medit_water_plane") != null };
        }

        // ---- helpers ----

        private static MobileParty FindHostile(bool naval)
        {
            var main = MobileParty.MainParty;
            var party = MobileParty.All.Where(p => p != main && p.IsActive && p.MapEvent == null && p.CurrentSettlement == null && p.Army == null
                    && p.AttachedTo == null && p.BesiegerCamp == null && !p.IsGarrison && !p.IsMilitia && p.MemberRoster.TotalHealthyCount > 0
                    && p.MapFaction != null && p.MapFaction.IsAtWarWith(main.MapFaction)
                    && (naval ? p.Ships.Count > 0 : (p.Ships.Count == 0 && !p.IsCurrentlyAtSea && p.Position.IsOnLand)))
                .OrderBy(p => p.MemberRoster.TotalManCount).ThenBy(p => p.StringId, StringComparer.Ordinal).FirstOrDefault();
            if (party == null) throw new InvalidOperationException("No hostile " + (naval ? "naval" : "land") + " party is available.");
            return party;
        }

        private static int AddCrew(MobileParty party, int wanted)
        {
            int count = Math.Max(0, wanted - party.MemberRoster.TotalHealthyCount);
            var troop = Hero.MainHero.Culture != null && Hero.MainHero.Culture.BasicTroop != null ? Hero.MainHero.Culture.BasicTroop
                      : MBObjectManager.Instance.GetObject<CharacterObject>("imperial_recruit");
            if (troop == null) throw new InvalidOperationException("No basic troop for the diagnostic crew.");
            if (count > 0) party.MemberRoster.AddToCounts(troop, count, false, 0, 0, true, -1);
            return count;
        }

        internal static CampaignVec2 FindPoint(CampaignVec2 center, MobileParty.NavigationType navigation, float max, float min)
        {
            var point = NavigationHelper.FindPointAroundPosition(center, navigation, max, min, true, false);
            if (!point.IsValid() || !point.Face.IsValid() || point.IsOnLand != (navigation == MobileParty.NavigationType.Default))
                throw new InvalidOperationException("No valid diagnostic point near the player for " + navigation + ".");
            return point;
        }

        internal static IEnumerator WaitMenu(ProbeContext context, string id)
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

        internal static void RunOption(string id)
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
                throw new InvalidOperationException("Official menu option unavailable: " + id + " in " + menu.StringId);
            menu.RunMenuOptionConsequence(context, index);
        }

        private static void RequireIdleMap()
        {
            RequireNoSave();
            var p = MobileParty.MainParty;
            if (!(Game.Current.GameStateManager.ActiveState is MapState) || Mission.Current != null
                || PlayerEncounter.Current != null || p.MapEvent != null || p.CurrentSettlement != null || p.Army != null)
                throw new InvalidOperationException("Mission probe requires an idle party on the map.");
        }

        private static void RequireNoSave()
        {
            var manager = Campaign.Current.SandBoxManager.SandBoxSaveManager;
            if (manager == null || !manager.IsAutoSaveDisabled() || manager.GetAutoSaveInterval() >= 0)
                throw new InvalidOperationException("Mission probe requires the diagnostic no-save guard.");
        }

        private static void RequireMission(Mission mission)
        {
            RequireNoSave();
            var state = Game.Current.GameStateManager.ActiveState as MissionState;
            if (Mission.Current != mission || state == null || state.CurrentMission != mission || mission.MissionEnded || !IsMissionScreen())
                throw new InvalidOperationException("Mission changed or ended during measurement.");
        }

        internal static bool IsMissionScreen()
        {
            for (var type = ScreenManager.TopScreen == null ? null : ScreenManager.TopScreen.GetType(); type != null; type = type.BaseType)
                if (type.FullName == "TaleWorlds.MountAndBlade.View.Screens.MissionScreen") return true;
            return false;
        }

        internal static int ShipCount(Mission mission)
        { return mission.MissionObjects.Count(o => o.GetType().FullName == "NavalDLC.Missions.Objects.MissionShip"); }

        internal static void Pause()
        { Campaign.Current.SetTimeControlModeLock(false); Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop; Campaign.Current.SetTimeControlModeLock(true); }

        private static FieldInfo Field(string name)
        {
            var f = typeof(SandBox.MapScene).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (f == null) throw new MissingFieldException("SandBox.MapScene", name);
            return f;
        }

        private static float[] Xy(CampaignVec2 p) { return new[] { p.X, p.Y }; }

        private static string Sha(byte[] bytes)
        { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }

        private static bool Equal(object a, object b) { return a == null ? b == null : a.Equals(b); }
    }
}
