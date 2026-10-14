using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Diagnostics;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Automation
{
    internal static class GeneratedCampaignBootProbe
    {
        internal static IEnumerator Run(ProbeContext context)
        {
            context.Outputs["productionReady"] = false;
            context.Outputs["scope"] = "Generated new-game initialization and registry census; recruitment/trade/port interactions and save restoration require separate observed evidence.";
            var worlds = new List<object>(); context.Outputs["worlds"] = worlds;
            int count = context.Int("worldCount", 1);
            if (count < 1 || count > 2) throw new ArgumentOutOfRangeException("worldCount");
            var priorIds = new HashSet<string>(StringComparer.Ordinal);
            // Environment probe failures are reported after both worlds ran, so one run covers the world switch too.
            var deferred = new List<string>();
            for (int worldIndex = 0; worldIndex < count; worldIndex++)
            {
                uint seed = checked((uint)context.Int(worldIndex == 0 ? "seed" : "secondSeed", worldIndex == 0 ? 42 : 43));
                string placement = context.Str(worldIndex == 0 ? "placement" : "secondPlacement", "");
                var registration = GeneratedCampaignRegistration.Prepare(seed, placement, context.Str("catalogReceipt", ""),
                    context.Str("culture", "empire"), context.Str("heroTemplate", ""), context.Str("townPrototype", ""), context.Str("villagePrototype", ""));
                var censuses = new List<object>(); var support = new Dictionary<string, object>();
                var world = new Dictionary<string, object> {
                    ["worldId"] = registration.WorldId, ["registration"] = registration.Report,
                    ["censuses"] = censuses, ["mapSupport"] = support, ["completed"] = false
                };
                worlds.Add(world); context.Checkpoint();
                string worldLabel = registration.WorldId;
                Action<string> trace = stage => { context.Stage(worldLabel + ":" + stage); context.Checkpoint(); };
                var boot = DiagnosticSandboxBootstrap.StartGenerated(registration.WorldId, registration.SceneName, registration.CultureId,
                    () => registration.Register(trace), census => { censuses.Add(census); context.Checkpoint(); }, support, trace, registration.Periodic, registration.Climate);
                double start = context.Seconds;
                while (!boot.Completed)
                {
                    boot.Tick();
                    if (boot.Failed) throw new InvalidOperationException(boot.Error);
                    if (context.Seconds - start > 600) throw new TimeoutException("Generated campaign did not reach MapState.");
                    yield return null;
                }
                var map = Campaign.Current.MapSceneWrapper as DiagnosticMapScene;
                if (map == null || map.Scene.GetName() != registration.SceneName)
                    throw new InvalidOperationException("Generated scene identity was displaced.");
                start = context.Seconds;
                while (!map.Scene.IsLoadingFinished() || Utilities.GetNumberOfShaderCompilationsInProgress() > 0 || context.Seconds - start < 8)
                {
                    boot.Tick();
                    if (context.Seconds - start > 180) throw new TimeoutException("Generated rendering readiness timed out.");
                    yield return null;
                }
                InformationManager.HideInquiry();
                for (int frame = 0; frame < 5; frame++) { boot.Tick(); yield return null; }
                var failures = Validate(registration, priorIds);
                world["validationFailures"] = failures;
                world["finalCensus"] = CampaignBootstrapProbe.Capture(registration.WorldId, "visible_paused_map");
                world["topScreen"] = ScreenManager.TopScreen.GetType().FullName;
                world["sceneName"] = map.Scene.GetName(); world["navFaces"] = map.Scene.GetNavMeshFaceCount();
                world["timeControl"] = Campaign.Current.TimeControlMode.ToString();
                string screenshot = Path.Combine(context.Directory, "generated-campaign-" + context.ProcessId + "-" + worldIndex + ".bmp");
                Utilities.TakeScreenshot(screenshot); world["screenshot"] = screenshot;
                context.Outputs["world" + worldIndex + "Screenshot"] = screenshot;
                trace("visible_map_captured");
                // MMR-19: pin the world's outputs and put its identity in every save from now on (autosaves included).
                try
                {
                    var package = registration.WritePackage(GeneratedWorldPackage.Sha256(File.ReadAllBytes(typeof(GeneratedCampaignBootProbe).Assembly.Location)));
                    foreach (var entry in WorldIdentity.Build(registration, package, (TaleWorlds.CampaignSystem.Map.IMapScene)map)) boot.Identity.Entries[entry.Key] = entry.Value;
                    world["package"] = package.Directory; world["identity"] = new Dictionary<string, string>(boot.Identity.Entries);
                    trace("world_package_written");
                }
                catch (Exception error) { world["packageError"] = error.ToString(); failures.Add("world package/identity: " + error.Message); }
                start = context.Seconds;
                while (context.Seconds - start < context.Num("holdSeconds", 25)) { boot.Tick(); yield return null; }
                double gameplayDays = context.Num("gameplayDays", 0);
                if (failures.Count == 0 && gameplayDays > 0)
                {
                    var gameplay = new Dictionary<string, object>(); world["gameplay"] = gameplay;
                    var steps = GeneratedCampaignGameplay.Run(context, boot, registration.WorldId, gameplay, gameplayDays,
                        context.Str("gameplaySpeed", "fast") != "play", context.Int("gameplayEntryProbe", 0) != 0);
                    while (steps.MoveNext()) yield return steps.Current;
                    string after = Path.Combine(context.Directory, "generated-campaign-" + context.ProcessId + "-" + worldIndex + "-gameplay.bmp");
                    Utilities.TakeScreenshot(after);
                    context.Outputs["world" + worldIndex + "GameplayScreenshot"] = after;
                    failures.AddRange(((List<string>)gameplay["failures"]).Select(f => "gameplay: " + f));
                    world["postGameplayCensus"] = CampaignBootstrapProbe.Capture(registration.WorldId, "after_gameplay");
                    var afterGameplay = Validate(registration, priorIds);
                    world["postGameplayValidationFailures"] = afterGameplay;
                    failures.AddRange(afterGameplay.Select(f => "after gameplay: " + f));
                }
                if (failures.Count == 0 && context.Int("navigationProbe", 0) != 0)
                {
                    var navigation = new Dictionary<string, object>(); world["navigation"] = navigation;
                    var steps = GeneratedNavigationProbe.Run(context, boot, registration.WorldId, (string)registration.Report["placementPath"], navigation);
                    while (steps.MoveNext()) yield return steps.Current;
                    string capture = Path.Combine(context.Directory, "generated-campaign-" + context.ProcessId + "-" + worldIndex + "-navigation.bmp");
                    Utilities.TakeScreenshot(capture);
                    context.Outputs["world" + worldIndex + "NavigationScreenshot"] = capture;
                    failures.AddRange(((List<string>)navigation["failures"]).Select(f => "navigation: " + f));
                }
                if (failures.Count == 0 && context.Int("missionProbe", 0) != 0)
                {
                    var missions = new Dictionary<string, object>(); world["missions"] = missions;
                    var identity = new Dictionary<string, object>();
                    foreach (string key in new[] { "worldId", "sceneName", "placementSha256", "catalogReceiptSha256", "installedSceneHashes" })
                        identity[key] = registration.Report[key];
                    var steps = RuntimeMissionProbe.Run(context, boot, registration.WorldId, worldIndex, identity, missions);
                    while (steps.MoveNext()) yield return steps.Current;
                    failures.AddRange(((List<string>)missions["failures"]).Select(f => "missions: " + f));
                }
                if (failures.Count == 0 && context.Int("environmentProbe", 0) != 0)
                {
                    var environment = new Dictionary<string, object>(); world["environment"] = environment;
                    var steps = RuntimeEnvironmentProbe.Run(context, boot, registration.WorldId, worldIndex, registration.Climate, environment);
                    while (steps.MoveNext()) yield return steps.Current;
                    deferred.AddRange(((List<string>)environment["failures"]).Select(f => registration.WorldId + " environment: " + f));
                }
                if (context.Int("seaDatumProbe", 0) != 0)
                {
                    var sea = SeaDatumProbe.Capture(context, map); world["seaDatum"] = sea;
                    deferred.AddRange(((List<string>)sea["failures"]).Select(f => registration.WorldId + " sea datum: " + f));
                    trace("sea_datum_captured");
                }
                if (failures.Count == 0 && context.Int("saveProbe", 0) != 0)
                {
                    var save = new Dictionary<string, object>(); world["save"] = save;
                    var steps = WorldSaveProbe.Save(context, boot, registration, worldIndex, save);
                    while (steps.MoveNext()) yield return steps.Current;
                    deferred.AddRange(((List<string>)save["failures"]).Select(f => registration.WorldId + " save: " + f));
                }
                if (failures.Count == 0 && context.Int("wrapProbe", 0) != 0)
                {
                    var wrap = new Dictionary<string, object>(); world["wrap"] = wrap;
                    var steps = WorldWrapProbe.Run(context, boot, registration.WorldId, wrap);
                    while (steps.MoveNext()) yield return steps.Current;
                    failures.AddRange(((List<string>)wrap["failures"]).Select(f => "wrap: " + f));
                }
                if (failures.Count != 0) throw new InvalidOperationException("Generated campaign census failed: " + String.Join("; ", failures));
                world["completed"] = true;
                foreach (string id in registration.Dataset.Instances) priorIds.Add(id);
                // Use the normal installed menu-exit lifecycle, without registry
                // clearing or keeping strong references to the old campaign.
                if (worldIndex + 1 < count)
                {
                    trace("normal_game_end_requested");
                    MBGameManager.EndGame();
                    boot = null; map = null; registration = null;
                    start = context.Seconds;
                    while (Game.Current != null || MBGameManager.Current != null)
                    {
                        if (context.Seconds - start > 90) throw new TimeoutException("Normal game-end lifecycle did not return to menu.");
                        yield return null;
                    }
                    world["normalGameEndCompleted"] = true; context.Checkpoint();
                    start = context.Seconds;
                    while (context.Seconds - start < 2) yield return null;
                }
            }
            context.Outputs["sequenceCompleted"] = true;
            if (deferred.Count != 0) throw new InvalidOperationException("Generated campaign census failed: " + String.Join("; ", deferred));
        }

        private static List<string> Validate(GeneratedCampaignRegistration registration, HashSet<string> priorIds)
        {
            var failures = new List<string>(); var dataset = registration.Dataset;
            if (dataset == null) { failures.Add("Dataset missing after campaign startup."); return failures; }
            var settlements = Settlement.All.ToArray();
            if (settlements.Length != 4 || settlements.Count(s => s.IsTown) != 2 || settlements.Count(s => s.IsVillage) != 2)
                failures.Add("Expected exactly two generated towns and two villages.");
            foreach (var settlement in settlements)
            {
                if (!settlement.StringId.StartsWith(dataset.WorldId + "_", StringComparison.Ordinal)) failures.Add("Unexpected settlement: " + settlement.StringId);
                if (priorIds.Contains("Settlement." + settlement.StringId)) failures.Add("Prior-world settlement survived: " + settlement.StringId);
                if (settlement.OwnerClan == null || !settlement.OwnerClan.StringId.StartsWith(dataset.WorldId + "_", StringComparison.Ordinal)) failures.Add("Settlement owner is outside this world: " + settlement.StringId);
                if (settlement.IsTown && !settlement.HasPort) failures.Add("Generated town has no port: " + settlement.StringId);
                if (settlement.IsVillage && (settlement.Village.Bound == null || !settlement.Village.Bound.StringId.StartsWith(dataset.WorldId + "_", StringComparison.Ordinal))) failures.Add("Invalid village binding: " + settlement.StringId);
            }
            foreach (var clan in Clan.All)
            {
                if (clan.StringId != "player_faction" && !clan.StringId.StartsWith(dataset.WorldId + "_", StringComparison.Ordinal)) failures.Add("Unexpected clan: " + clan.StringId);
                if (priorIds.Contains("Faction." + clan.StringId) && clan.StringId != "player_faction") failures.Add("Prior-world clan survived: " + clan.StringId);
            }
            if (Clan.All.Count != 3 || Kingdom.All.Count != 2) failures.Add("Expected two generated noble clans/kingdoms plus the player clan.");
            // Required aliases (main_hero, player_faction) are recreated by every world and are not prior-world leftovers.
            var aliases = new HashSet<string>(dataset.RequiredAliases, StringComparer.Ordinal);
            foreach (var kingdom in Kingdom.All)
                if (priorIds.Contains("Kingdom." + kingdom.StringId)) failures.Add("Prior-world kingdom survived: " + kingdom.StringId);
            foreach (var hero in Hero.AllAliveHeroes.Concat(Hero.DeadOrDisabledHeroes))
                if (priorIds.Contains("Hero." + hero.StringId) && !aliases.Contains("Hero." + hero.StringId)) failures.Add("Prior-world hero survived: " + hero.StringId);
            foreach (var character in TaleWorlds.ObjectSystem.MBObjectManager.Instance.GetObjectTypeList<CharacterObject>())
                if (priorIds.Contains("NPCCharacter." + character.StringId) && !aliases.Contains("NPCCharacter." + character.StringId)) failures.Add("Prior-world character survived: " + character.StringId);
            foreach (var kingdom in Kingdom.All)
                if (!kingdom.StringId.StartsWith(dataset.WorldId + "_", StringComparison.Ordinal) || kingdom.Leader == null)
                    failures.Add("Unexpected kingdom or missing ruler: " + kingdom.StringId);
            foreach (var party in dataset.StartingParties)
            {
                var ruler = Hero.Find(party.HeroId);
                if (ruler == null || ruler.Clan == null || ruler.Clan.StringId != party.ClanId) failures.Add("Ruler/clan binding missing: " + party.HeroId);
                else if (ruler.PartyBelongedTo == null || ruler.PartyBelongedTo.LeaderHero != ruler)
                    failures.Add("Generated ruler has no leading party: " + party.HeroId);
            }
            if (MobileParty.MainParty == null || Campaign.Current.TimeControlMode != CampaignTimeControlMode.Stop)
                failures.Add("Generated main party is missing or campaign is not paused.");
            else if (Math.Abs(MobileParty.MainParty.Position.X - dataset.StartX) > .05 || Math.Abs(MobileParty.MainParty.Position.Y - dataset.StartY) > .05)
                failures.Add("Main party did not start at the generated gate.");
            return failures;
        }
    }
}
