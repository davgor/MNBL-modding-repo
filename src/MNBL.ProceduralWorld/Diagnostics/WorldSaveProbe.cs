using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.Automation;
using MNBL.ProceduralWorld.CampaignBootstrap;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Diagnostics
{
    // MMR-19: generated-world save identity across a cold restart.
    //   Process 1 (generated_campaign, saveProbe=1), per world: travel on land (world 0 also embarks and sails), write the
    //     immutable world package, put the world identity into the save metadata, save under a unique name, snapshot the
    //     saved state and read the identity back from the save header. The snapshots go to an oracle file.
    //   Process 2 (generated_save_load, oracle=<file>), cold: for each save in order, read the identity from the header at
    //     the main menu (no game exists), verify the package by hash, deserialize, load through the generated loader (package
    //     XML re-applied in RegisterSubModuleObjects(true)), and compare the live world with the snapshot.
    internal static class WorldSaveProbe
    {
        private static readonly string[] SavedPositions = { "_position", "MoveTargetPoint", "_targetPosition", "_pathLastPosition", "<EndPositionForNavigationTransition>k__BackingField" };

        // ---- process 1 ----
        internal static IEnumerator Save(ProbeContext context, DiagnosticSandboxBootstrap boot, GeneratedCampaignRegistration registration,
            int worldIndex, Dictionary<string, object> row)
        {
            var failures = new List<string>(); row["failures"] = failures;
            var campaign = Campaign.Current; var main = MobileParty.MainParty; string worldId = registration.WorldId;
            var town1 = Settlement.Find(worldId + "_town_1"); var town2 = Settlement.Find(worldId + "_town_2"); var village1 = Settlement.Find(worldId + "_village_1");
            RuntimeMissionProbe.Pause();
            if (main.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(main);
            var travel = new Dictionary<string, object>(); row["travel"] = travel;
            var land = Run(context, boot, campaign, () => main.SetMoveGoToPoint(village1.GatePosition, MobileParty.NavigationType.Default), 4, travel, "land");
            while (land.MoveNext()) yield return land.Current;
            if (worldIndex == 0)
            {
                // Embark at town 1's port (a culture ship hull and a skeletal crew are granted) and sail toward town 2's port.
                if (main.Ships.Count == 0)
                {
                    var hull = Kingdom.All.Select(k => k.Culture).Concat(new[] { town1.Culture }).Where(c => c != null).SelectMany(c => c.AvailableShipHulls).First();
                    int crew = Math.Max(0, new Ship(hull).SkeletalCrewCapacity - main.MemberRoster.TotalHealthyCount);
                    if (crew > 0) main.MemberRoster.AddToCounts(Hero.MainHero.Culture.BasicTroop, crew, false, 0, 0, true, -1);
                    ChangeShipOwnerAction.ApplyByLooting(PartyBase.MainParty, new Ship(hull));
                }
                main.SetSailAtPosition(town1.PortPosition);
                var sea = Run(context, boot, campaign, () => main.SetMoveGoToPoint(town2.PortPosition, MobileParty.NavigationType.Naval), 4, travel, "sea");
                while (sea.MoveNext()) yield return sea.Current;
                if (!main.IsCurrentlyAtSea) failures.Add("The player is not at sea before world 0's save.");
            }
            else if (main.IsCurrentlyAtSea) failures.Add("The player is at sea before world 1's save (expected land).");

            // The package and identity were written when the world was created (GeneratedCampaignBootProbe).
            if (registration.Package == null || boot.Identity == null || boot.Identity.Entries.Count != WorldIdentity.Keys.Length)
            { failures.Add("The world has no package or identity from creation."); yield break; }
            row["identity"] = new Dictionary<string, string>(boot.Identity.Entries); row["package"] = registration.Package.Directory;

            string saveName = "mnbl_mmr19_" + worldIndex + "_" + Guid.NewGuid().ToString("N");
            if (MBSaveLoad.IsSaveGameFileExists(saveName)) { failures.Add("Refusing to replace an existing save."); yield break; }
            var previous = campaign.SandBoxManager.SandBoxSaveManager;
            var observer = new SaveObserver(saveName);
            campaign.SandBoxManager.SandBoxSaveManager = observer;
            double start = context.Seconds;
            campaign.SaveHandler.SaveAs(saveName);
            while (!observer.Completed && context.Seconds - start < 120) yield return null;
            campaign.SandBoxManager.SandBoxSaveManager = previous;
            row["saveName"] = saveName; row["saveSucceeded"] = observer.Success; row["metadataCollections"] = boot.Identity.Collections;
            if (!observer.Success || !MBSaveLoad.IsSaveGameFileExists(saveName)) { failures.Add("The save did not complete."); yield break; }
            // The save serializes on a later frame and runs before-save hooks; the snapshot is the paused state after it.
            var snapshot = Snapshot(worldId); row["snapshot"] = snapshot;
            var header = Header(saveName); row["header"] = header;
            foreach (var entry in boot.Identity.Entries)
            {
                object value;
                if (!header.TryGetValue(entry.Key, out value) || (string)value != entry.Value) failures.Add("The save header lacks or differs on " + WorldIdentityBehavior.Prefix + entry.Key + ".");
            }
            var oraclePath = Path.Combine(SubModule.Store, "worldsaves", "oracle-" + context.ProcessId + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(oraclePath));
            var oracle = File.Exists(oraclePath) ? new ArrayList((object[])new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(File.ReadAllText(oraclePath))) : new ArrayList();
            oracle.Add(new Dictionary<string, object> { ["worldId"] = worldId, ["saveName"] = saveName, ["identity"] = boot.Identity.Entries.ToDictionary(k => k.Key, k => (object)k.Value), ["snapshot"] = snapshot });
            File.WriteAllText(oraclePath, new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(oracle));
            row["oracle"] = oraclePath;
            context.Outputs["saveOracle"] = oraclePath;
        }

        private static IEnumerator Run(ProbeContext context, DiagnosticSandboxBootstrap boot, Campaign campaign, Action order, double seconds,
            Dictionary<string, object> record, string name)
        {
            var main = MobileParty.MainParty;
            var from = main.Position.ToVec2(); double hours = CampaignTime.Now.ToHours;
            order();
            boot.HoldPaused = false;
            DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppablePlay);
            double start = context.Seconds;
            while (context.Seconds - start < seconds)
            {
                boot.Tick();
                if (boot.Failed) throw new InvalidOperationException(boot.Error);
                if (campaign.TimeControlMode == CampaignTimeControlMode.Stop) DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppablePlay);
                yield return null;
            }
            DiagnosticSandboxBootstrap.Pause(campaign);
            boot.HoldPaused = true;
            record[name] = new Dictionary<string, object> { ["from"] = new[] { Number(from.X), Number(from.Y) }, ["to"] = Xy(main.Position), ["distance"] = (double)from.Distance(main.Position.ToVec2()),
                ["campaignHours"] = CampaignTime.Now.ToHours - hours, ["atSea"] = main.IsCurrentlyAtSea };
        }

        // The save's metadata header (no deserialization): the mnbl.world.* keys without the prefix, plus UniqueGameId.
        private static Dictionary<string, object> Header(string saveName)
        {
            var info = MBSaveLoad.GetSaveFileWithName(saveName);
            if (info == null) throw new FileNotFoundException("Save not listed: " + saveName);
            var header = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var key in WorldIdentity.Keys)
            {
                string value;
                if (info.MetaData.TryGetValue(WorldIdentityBehavior.Prefix + key, out value)) header[key] = value;
            }
            string unique;
            if (info.MetaData.TryGetValue("UniqueGameId", out unique)) header["UniqueGameId"] = unique;
            return header;
        }

        // ---- process 2 ----
        internal static IEnumerator Load(ProbeContext context)
        {
            context.Outputs["productionReady"] = false;
            context.Outputs["scope"] = "MMR-19 cold load of generated saves through the generated loader; normal-menu routing is MMR-49";
            var failures = new List<string>(); context.Outputs["failures"] = failures;
            if (Game.Current != null) throw new InvalidOperationException("Cold load requires the initial menu.");
            string oraclePath = context.Str("oracle", "");
            var oracle = ((object[])new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(File.ReadAllText(oraclePath))).Cast<Dictionary<string, object>>().ToList();
            context.Outputs["oracle"] = oraclePath; context.Outputs["oracleSha256"] = GeneratedWorldPackage.Sha256(File.ReadAllBytes(oraclePath));
            context.Outputs["datasetBuildsAtStart"] = GeneratedCampaignDataset.BuildCount;
            var worlds = new List<object>(); context.Outputs["worlds"] = worlds;
            for (int index = 0; index < oracle.Count; index++)
            {
                var entry = oracle[index];
                var row = new Dictionary<string, object> { ["worldId"] = entry["worldId"], ["saveName"] = entry["saveName"] }; worlds.Add(row);
                var rowFailures = new List<string>(); row["failures"] = rowFailures;
                var steps = LoadOne(context, entry, index, row, rowFailures);
                Exception error = null;
                while (true)
                {
                    try { if (!steps.MoveNext()) break; } catch (Exception e) { error = e; break; }
                    yield return steps.Current;
                }
                if (error != null) { row["error"] = error.ToString(); rowFailures.Add(error.GetType().Name + ": " + error.Message); }
                failures.AddRange(rowFailures.Select(f => entry["worldId"] + ": " + f));
                if (Game.Current != null)
                {
                    context.Stage("save_load_game_end:" + entry["worldId"]);
                    MBGameManager.EndGame();
                    double start = context.Seconds;
                    while (Game.Current != null || MBGameManager.Current != null)
                    {
                        if (context.Seconds - start > 90) throw new TimeoutException("Normal game-end lifecycle did not return to menu.");
                        yield return null;
                    }
                    row["gameEnded"] = true;
                    start = context.Seconds;
                    while (context.Seconds - start < 2) yield return null;
                }
            }
            context.Outputs["datasetBuildsAtEnd"] = GeneratedCampaignDataset.BuildCount;
            if (GeneratedCampaignDataset.BuildCount != 0) failures.Add("A generated dataset was built during the cold load (" + GeneratedCampaignDataset.BuildCount + ").");
            context.Checkpoint();
            if (failures.Count != 0) throw new InvalidOperationException("Generated save load failed: " + String.Join("; ", failures));
        }

        private static IEnumerator LoadOne(ProbeContext context, Dictionary<string, object> entry, int index, Dictionary<string, object> row, List<string> failures)
        {
            string saveName = (string)entry["saveName"], worldId = (string)entry["worldId"];
            Action<string> trace = stage => { context.Stage(worldId + ":" + stage); context.Checkpoint(); };
            // 1. Identity from the save header at the main menu, before anything deserializes.
            row["gameExistsAtIdentityRead"] = Game.Current != null;
            var header = Header(saveName); row["header"] = header;
            var expected = (Dictionary<string, object>)entry["identity"];
            foreach (var key in WorldIdentity.Keys)
                if (!header.ContainsKey(key) || (string)header[key] != Convert.ToString(expected[key], CultureInfo.InvariantCulture)) failures.Add("Header key " + key + " is missing or differs from the oracle.");
            if (!header.ContainsKey("id") || (string)header["id"] != worldId) { failures.Add("The save header does not name this world."); yield break; }
            trace("save_identity_read");
            // 2. The world package, verified by hash before deserializing (fails closed).
            var package = GeneratedWorldPackage.Load((string)header["package"], (string)header["manifestSha256"]);
            WorldIdentity.RequireMatch(header.ToDictionary(k => k.Key, k => (string)k.Value), package);
            row["packageVerified"] = true; row["packageNamesHeaderWorld"] = true; row["package"] = package.Directory;
            row["packageManifest"] = new Dictionary<string, object> { ["generator"] = package.Manifest["generator"], ["adapter"] = package.Manifest["adapter"] };
            trace("world_package_verified");
            // 3. Deserialize.
            var data = MBSaveLoad.LoadSaveGameData(saveName);
            row["deserialized"] = data != null && data.Successful;
            if (data == null || !data.Successful) { failures.Add("The save did not deserialize."); yield break; }
            var savedCampaign = ((Game)data.Root).GameType as Campaign;
            var savedCrc = new Dictionary<string, object> { ["sceneXmlCrc"] = CrcField(savedCampaign, "_campaignMapSceneXmlCrc"), ["navMeshCrc"] = CrcField(savedCampaign, "_campaignMapSceneNavigationMeshCrc"),
                ["uniqueGameId"] = savedCampaign.UniqueGameId };
            row["savedCampaign"] = savedCrc;
            trace("save_deserialized");
            // 4. Load through the generated loader. First-chance exceptions are recorded for the whole load (informational:
            //    the engine raises and handles some by design).
            var firstChance = new List<string>();
            EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> onFirstChance = (sender, e) =>
            { lock (firstChance) if (firstChance.Count < 50) firstChance.Add(e.Exception.GetType().FullName + ": " + e.Exception.Message); };
            AppDomain.CurrentDomain.FirstChanceException += onFirstChance;
            row["firstChanceExceptions"] = firstChance;
            var registration = GeneratedCampaignRegistration.FromInstalledPackage(package);
            var censuses = new List<object>(); var support = new Dictionary<string, object>(); row["censuses"] = censuses; row["mapSupport"] = support;
            var identity = header.Where(k => k.Key != "UniqueGameId").ToDictionary(k => k.Key, k => (string)k.Value);
            var boot = DiagnosticSandboxBootstrap.StartGeneratedLoad(data, worldId, package.SceneName, () => registration.ReapplyForSavedGame(trace),
                census => { censuses.Add(census); context.Checkpoint(); }, support, trace, registration.Periodic, registration.Climate, identity);
            double start = context.Seconds;
            while (!boot.Completed)
            {
                boot.Tick();
                if (boot.Failed) throw new InvalidOperationException(boot.Error);
                if (context.Seconds - start > 600) throw new TimeoutException("The generated save did not reach MapState.");
                yield return null;
            }
            row["savedGameDocuments"] = registration.Report["savedGameDocuments"];
            var map = Campaign.Current.MapSceneWrapper as DiagnosticMapScene;
            if (map == null || map.Scene.GetName() != package.SceneName) { failures.Add("The loaded map is not the generated scene."); yield break; }
            start = context.Seconds;
            while (!map.Scene.IsLoadingFinished() || Utilities.GetNumberOfShaderCompilationsInProgress() > 0 || context.Seconds - start < 5)
            {
                boot.Tick();
                if (context.Seconds - start > 180) throw new TimeoutException("Loaded map rendering readiness timed out.");
                yield return null;
            }
            InformationManager.HideInquiry();
            string screenshot = Path.Combine(context.Directory, "generated-save-load-" + context.ProcessId + "-" + index + ".bmp");
            Utilities.TakeScreenshot(screenshot); context.Outputs["world" + index + "LoadedScreenshot"] = screenshot;
            // 5. Compare with the snapshot taken at save time.
            var live = Snapshot(worldId); row["snapshot"] = live;
            var mismatches = Compare((Dictionary<string, object>)entry["snapshot"], live);
            row["mismatches"] = mismatches;
            if (mismatches.Count != 0) failures.Add(mismatches.Count + " values differ from the saved snapshot (first: " + mismatches[0] + ").");
            if (Convert.ToString(savedCrc["sceneXmlCrc"], CultureInfo.InvariantCulture) != Convert.ToString(live["sceneXmlCrc"], CultureInfo.InvariantCulture) ||
                Convert.ToString(savedCrc["navMeshCrc"], CultureInfo.InvariantCulture) != Convert.ToString(live["navMeshCrc"], CultureInfo.InvariantCulture))
                failures.Add("The live scene CRCs differ from the saved ones (Campaign.CheckMapUpdate would relocate objects).");
            row["identityBehaviorEntries"] = boot.Identity == null ? 0 : boot.Identity.Entries.Count;
            if (GeneratedCampaignDataset.BuildCount != 0) failures.Add("A dataset was built while loading.");
            // 6. Run campaign time briefly on the loaded world.
            var campaign = Campaign.Current; double hours = CampaignTime.Now.ToHours;
            boot.HoldPaused = false;
            DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppablePlay);
            start = context.Seconds;
            while (context.Seconds - start < 3)
            {
                boot.Tick();
                if (boot.Failed) throw new InvalidOperationException(boot.Error);
                if (campaign.TimeControlMode == CampaignTimeControlMode.Stop) DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppablePlay);
                yield return null;
            }
            DiagnosticSandboxBootstrap.Pause(campaign);
            boot.HoldPaused = true;
            row["campaignHoursRunAfterLoad"] = CampaignTime.Now.ToHours - hours;
            AppDomain.CurrentDomain.FirstChanceException -= onFirstChance;
            if (!(CampaignTime.Now.ToHours - hours > 0)) failures.Add("Campaign time did not advance on the loaded world.");
        }

        private static object CrcField(Campaign campaign, string name)
        {
            var field = typeof(Campaign).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? null : Convert.ToString(field.GetValue(campaign), CultureInfo.InvariantCulture);
        }

        // ---- snapshot and comparison ----
        internal static Dictionary<string, object> Snapshot(string worldId)
        {
            var campaign = Campaign.Current; var map = (IMapScene)campaign.MapSceneWrapper;
            var settlements = Settlement.All.OrderBy(s => s.StringId, StringComparer.Ordinal).Select(s => (object)new Dictionary<string, object> {
                ["id"] = s.StringId, ["position"] = Xy(s.Position), ["gate"] = Xy(s.GatePosition), ["port"] = s.HasPort ? Xy(s.PortPosition) : null,
                ["hasPort"] = s.HasPort, ["owner"] = s.OwnerClan == null ? null : s.OwnerClan.StringId, ["isReady"] = s.IsReady,
                ["name"] = s.Name == null ? null : s.Name.ToString(), ["culture"] = s.Culture == null ? null : s.Culture.StringId,
                ["gateFace"] = Face(s.GatePosition), ["portFace"] = s.HasPort ? Face(s.PortPosition) : null }).ToList();
            var parties = MobileParty.All.OrderBy(p => p.StringId, StringComparer.Ordinal).Select(p => (object)new Dictionary<string, object> {
                ["id"] = p.StringId, ["saved"] = SavedPositions.Select(n => (object)Xy((CampaignVec2)typeof(MobileParty).GetField(n, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(p))).ToList(),
                ["atSea"] = p.IsCurrentlyAtSea, ["ships"] = p.Ships.Count, ["settlement"] = p.CurrentSettlement == null ? null : p.CurrentSettlement.StringId,
                ["positionIsOnLand"] = p.Position.IsOnLand, ["nextTarget"] = Xy(p.NextTargetPosition) }).ToList();
            var main = MobileParty.MainParty;
            return new Dictionary<string, object> {
                ["worldId"] = worldId, ["scene"] = ((SandBox.MapScene)campaign.MapSceneWrapper).Scene.GetName(),
                ["sceneXmlCrc"] = map.GetSceneXmlCrc().ToString(CultureInfo.InvariantCulture), ["navMeshCrc"] = map.GetSceneNavigationMeshCrc().ToString(CultureInfo.InvariantCulture),
                ["uniqueGameId"] = campaign.UniqueGameId, ["campaignHours"] = CampaignTime.Now.ToHours.ToString("R", CultureInfo.InvariantCulture),
                ["bounds"] = new[] { Number(Campaign.MapMinimumPosition.X), Number(Campaign.MapMinimumPosition.Y), Number(Campaign.MapMaximumPosition.X), Number(Campaign.MapMaximumPosition.Y) },
                ["settlements"] = settlements, ["parties"] = parties,
                ["main"] = new Dictionary<string, object> { ["position"] = Xy(main.Position), ["face"] = Face(main.Position), ["atSea"] = main.IsCurrentlyAtSea, ["ships"] = main.Ships.Count },
                ["heroes"] = Hero.AllAliveHeroes.Select(h => h.StringId).OrderBy(x => x, StringComparer.Ordinal).Cast<object>().ToList(),
                ["clans"] = Clan.All.Select(c => c.StringId).OrderBy(x => x, StringComparer.Ordinal).Cast<object>().ToList(),
                ["kingdoms"] = Kingdom.All.Select(k => k.StringId).OrderBy(x => x, StringComparer.Ordinal).Cast<object>().ToList(),
                ["nonGeneratedSettlements"] = Settlement.All.Count(s => !s.StringId.StartsWith(worldId + "_", StringComparison.Ordinal))
            };
        }

        private static object Face(CampaignVec2 p)
        {
            var face = p.Face;
            return face.IsValid() ? new[] { face.FaceIndex, face.FaceGroupIndex, face.FaceIslandIndex } : null;
        }

        // NaN (the engine's unset sentinel) and infinities become distinct strings: JavaScriptSerializer would write invalid JSON.
        private static object[] Xy(CampaignVec2 p) { return new[] { Number(p.X), Number(p.Y) }; }
        private static object Number(float value)
        {
            if (Single.IsNaN(value)) return "NaN";
            if (Single.IsPositiveInfinity(value)) return "+Infinity";
            if (Single.IsNegativeInfinity(value)) return "-Infinity";
            return (double)value;
        }

        // Every value of the saved snapshot must reappear exactly (NaN equals NaN) in the loaded world.
        internal static List<string> Compare(Dictionary<string, object> saved, Dictionary<string, object> live)
        {
            var problems = new List<string>();
            var a = Normalize(saved); var b = Normalize(live);
            Walk("", a, b, problems);
            return problems;
        }

        private static object Normalize(object value)
        {
            // Round-trip through the serializer so the oracle (read from JSON) and the live snapshot compare like for like.
            var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            return serializer.DeserializeObject(serializer.Serialize(value));
        }

        private static void Walk(string path, object a, object b, List<string> problems)
        {
            if (problems.Count >= 40) return;
            var da = a as Dictionary<string, object>; var db = b as Dictionary<string, object>;
            if (da != null && db != null)
            {
                foreach (var key in da.Keys.Union(db.Keys).OrderBy(k => k, StringComparer.Ordinal))
                {
                    object va, vb;
                    bool ha = da.TryGetValue(key, out va), hb = db.TryGetValue(key, out vb);
                    if (!ha || !hb) { problems.Add(path + "." + key + (ha ? " missing after load" : " new after load")); continue; }
                    Walk(path + "." + key, va, vb, problems);
                }
                return;
            }
            var la = a as object[]; var lb = b as object[];
            if (la == null && a is ArrayList) la = ((ArrayList)a).ToArray();
            if (lb == null && b is ArrayList) lb = ((ArrayList)b).ToArray();
            if (la != null && lb != null)
            {
                // Lists of records are matched by "id" when present, otherwise by position.
                bool keyed = la.All(x => x is Dictionary<string, object> && ((Dictionary<string, object>)x).ContainsKey("id")) &&
                             lb.All(x => x is Dictionary<string, object> && ((Dictionary<string, object>)x).ContainsKey("id"));
                if (keyed)
                {
                    var ia = la.Cast<Dictionary<string, object>>().ToDictionary(x => Convert.ToString(x["id"], CultureInfo.InvariantCulture));
                    var ib = lb.Cast<Dictionary<string, object>>().ToDictionary(x => Convert.ToString(x["id"], CultureInfo.InvariantCulture));
                    Walk(path, ia.ToDictionary(k => k.Key, k => (object)k.Value), ib.ToDictionary(k => k.Key, k => (object)k.Value), problems);
                    return;
                }
                if (la.Length != lb.Length) { problems.Add(path + " length " + la.Length + " -> " + lb.Length); return; }
                for (int i = 0; i < la.Length; i++) Walk(path + "[" + i + "]", la[i], lb[i], problems);
                return;
            }
            string sa = Scalar(a), sb = Scalar(b);
            if (sa != sb) problems.Add(path + ": " + sa + " -> " + sb);
        }

        private static string Scalar(object value)
        {
            if (value == null) return "null";
            if (value is decimal || value is double || value is float || value is int || value is long)
            {
                double d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return Double.IsNaN(d) ? "NaN" : d.ToString("R", CultureInfo.InvariantCulture);
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private sealed class SaveObserver : ISaveManager
        {
            private readonly string expected;
            internal bool Completed, Success;
            internal SaveObserver(string expected) { this.expected = expected; }
            public int GetAutoSaveInterval() { return -1; }
            public bool IsAutoSaveDisabled() { return true; }
            public void OnSaveOver(bool success, string name) { Success = success && name == expected; Completed = true; }
        }
    }
}
