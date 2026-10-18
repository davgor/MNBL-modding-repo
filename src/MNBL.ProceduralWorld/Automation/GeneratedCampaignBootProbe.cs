using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.Diagnostics;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;
using MNBL.ProceduralWorld.Surface;
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
            string settlementEntityWorld = null; List<string> settlementEntityIds = null;   // MMR-40: the first world, for the teardown check
            // MMR-37: the previous world's map scene, held on purpose (as a stale reference would be) so the next world can check that a later
            // generation's map refuses it; released after that check.
            GeneratedMapScene previousMap = null;
            // Environment probe failures are reported after both worlds ran, so one run covers the world switch too.
            var deferred = new List<string>();
            for (int worldIndex = 0; worldIndex < count; worldIndex++)
            {
                uint seed = checked((uint)context.Int(worldIndex == 0 ? "seed" : "secondSeed", worldIndex == 0 ? 42 : 43));
                string placement = context.Str(worldIndex == 0 ? "placement" : "secondPlacement", "");
                // MMR-33: settlementFixtures=<repo> builds the first world's settlements with SettlementFactory plus a castle, its village and a hideout.
                // MMR-34: startupAudit=<repo> audits every world's startup; the first world also gets the startup fixtures (bandit clans,
                // the retreat, a runtime plan) and every world the diagnostic spawn assertions.
                string startupAudit = context.Str("startupAudit", "");
                // MMR-35: navalAudit=<repo> gives the first world the naval fixtures (pirate clans, the fleet rule) and audits them.
                string navalAudit = context.Str("navalAudit", "");
                var registration = GeneratedCampaignRegistration.Prepare(seed, placement, context.Str("catalogReceipt", ""),
                    context.Str("culture", "empire"), context.Str("heroTemplate", ""), context.Str("townPrototype", ""), context.Str("villagePrototype", ""),
                    worldIndex == 0 ? context.Str("settlementFixtures", "") : "", worldIndex == 0 && startupAudit.Length > 0, worldIndex == 0 && navalAudit.Length > 0,
                    worldIndex == 0 && context.Int("factoryEntities", 0) != 0);   // MMR-40
                var censuses = new List<object>(); var support = new Dictionary<string, object>();
                // MMR-41: navigationCacheVerify=1 compares a reused cache with fresh ones; navigationCacheFault=corrupt damages the published copy
                // first (the first world).
                if (worldIndex == 0 && (context.Int("navigationCacheVerify", 0) != 0 || context.Str("navigationCacheFault", "").Length > 0))
                    support["navigationCacheOptions"] = new Dictionary<string, object> { ["verify"] = context.Int("navigationCacheVerify", 0) != 0 ? "1" : "0", ["fault"] = context.Str("navigationCacheFault", "") };
                var world = new Dictionary<string, object> {
                    ["worldId"] = registration.WorldId, ["registration"] = registration.Report,
                    ["censuses"] = censuses, ["mapSupport"] = support, ["completed"] = false
                };
                worlds.Add(world); context.Checkpoint();
                string worldLabel = registration.WorldId;
                Action<string> trace = stage => { context.Stage(worldLabel + ":" + stage); context.Checkpoint(); };
                // MMR-32: restrictCreationCultures limits character creation to the cultures a world presents through.
                var restrict = worldIndex == 0 ? context.Str("restrictCreationCultures", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList() : new List<string>();
                // First-chance exceptions while the generated new game is created, flushed per line so a crash still leaves the
                // throw site (handled ones included; at most 40).
                int firstChance = 0;
                EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> onFirstChance = (sender, e) =>
                {
                    string stack = Environment.StackTrace;
                    if (stack.Contains("CampaignBootstrapProbe")) return;   // the census's own handled probing
                    if (System.Threading.Interlocked.Increment(ref firstChance) > 40) return;
                    try { context.Stage(worldLabel + ":new_game_first_chance:" + e.Exception.GetType().FullName + ":" + e.Exception.Message + " | " + stack.Replace(Environment.NewLine, " | ")); } catch { }
                };
                AppDomain.CurrentDomain.FirstChanceException += onFirstChance;
                var boot = DiagnosticSandboxBootstrap.StartGenerated(registration, registration.SceneName, registration.CultureId,
                    census => { censuses.Add(census); context.Checkpoint(); }, support, trace, registration.Periodic, registration.Climate, restrict.Count > 0 ? restrict : null,
                    startupAudit.Length > 0);
                boot.AuditCreation = worldIndex == 0 && context.Int("creationAudit", 0) != 0;
                double start = context.Seconds;
                while (!boot.Completed)
                {
                    boot.Tick();
                    if (boot.Failed) throw new InvalidOperationException(boot.Error);
                    // The MMR-63 periodic fixture builds its All navigation cache in about 420 s and reaches the map at about 600-620 s.
                    if (context.Seconds - start > 900) throw new TimeoutException("Generated campaign did not reach MapState.");
                    yield return null;
                }
                var map = Campaign.Current.MapSceneWrapper as GeneratedMapScene;
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
                AppDomain.CurrentDomain.FirstChanceException -= onFirstChance;
                world["newGameFirstChanceExceptions"] = firstChance;
                var failures = Validate(registration, priorIds);
                world["validationFailures"] = failures;
                // MMR-32: the culture stage lists the cultures the generated world presents through (creationCultures, from the
                // compiled world), each with an origin in every narrative menu and resolvable creation assets.
                if (boot.CreationFilter != null)
                {
                    world["creationFilter"] = new Dictionary<string, object> { ["kept"] = boot.CreationFilter.Kept, ["removed"] = boot.CreationFilter.Removed, ["missing"] = boot.CreationFilter.Missing,
                                                                               ["installedSortSkipped"] = boot.CreationFilter.SortSkipped };
                    if (boot.CreationFilter.Kept == null) failures.Add("character creation: the culture filter never ran");
                    else if (boot.CreationFilter.Missing.Count > 0) failures.Add("character creation: world cultures missing from the installed list: " + String.Join(",", boot.CreationFilter.Missing));
                }
                if (boot.AuditCreation)
                {
                    var audit = boot.CreationAudit;
                    world["characterCreation"] = audit;
                    if (audit == null) failures.Add("character creation: the culture stage was never audited");
                    else
                    {
                        foreach (var problem in (List<string>)audit["problems"]) failures.Add("character creation: " + problem);
                        var listed = new HashSet<string>(((List<object>)audit["cultures"]).Cast<Dictionary<string, object>>().Select(c => (string)c["culture"]), StringComparer.Ordinal);
                        var expected = new HashSet<string>(context.Str("creationCultures", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
                        if (expected.Count == 0 || !listed.SetEquals(expected))
                            failures.Add("character creation lists " + String.Join(",", listed.OrderBy(x => x, StringComparer.Ordinal)) + "; the generated world presents through " + String.Join(",", expected.OrderBy(x => x, StringComparer.Ordinal)));
                    }
                }
                if (registration.FixturesEnabled)
                {
                    world["settlementFixtures"] = SettlementFixturesProbe.Run(registration, failures);
                    trace("settlement_fixtures_checked");
                }
                if (startupAudit.Length > 0)
                {
                    if (worldIndex > 0) { world["startupTeardown"] = StartupAuditProbe.Teardown(registration, failures); trace("startup_teardown_checked"); }
                    world["startupAudit"] = StartupAuditProbe.AtStart(startupAudit, registration, boot, failures);
                    trace("startup_audited");
                }
                if (registration.NavalEnabled)
                {
                    world["navalAudit"] = NavalContentProbe.AtStart(navalAudit, registration, boot, failures);
                    trace("naval_audited");
                }
                // MMR-36: graphFault=<class>[,<class>] observes the graph gate refusing faulted registrations of the second world's placement.
                if (worldIndex == 0 && context.Str("graphFault", "").Length > 0)
                {
                    world["graphGate"] = GraphGateProbe.Run(context, registration, failures);
                    trace("graph_gate_checked");
                }
                // MMR-32: the size-129 world's XML heroes get their explicit bodies from the engine's face generator.
                if (worldIndex == 0 && context.Str("heroBundle", "").Length > 0)
                {
                    world["heroBodies"] = HeroBodiesProbe.Run(context.Str("heroBundle", ""), context.Str("repo", ""), failures);
                    trace("hero_bodies_drawn");
                }
                // MMR-37: the map scene's generation and ledger, its queries against the native scene, and the previous world's map scene refused.
                world["mapSurface"] = CampaignMapSurface(map, previousMap, failures);
                previousMap = worldIndex + 1 < count ? map : null;
                world["finalCensus"] = CampaignBootstrapProbe.Capture(registration.WorldId, "visible_paused_map");
                // MMR-30: the registry census at every manager phase, and once more with the default lists published.
                var registryCensus = registration.RegistryCensus("visible_paused_map", ClosureMode.Settled);
                world["registryCensuses"] = boot.RegistryCensuses; world["finalRegistryCensus"] = registryCensus;
                foreach (var problem in (List<string>)registryCensus["closure"]) failures.Add("Registry closure: " + problem);
                foreach (var prefix in (List<string>)registryCensus["otherWorldPrefixes"]) failures.Add("Objects of another world survived: " + prefix);
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
                    // A fixture world: built from a placement, not a data bundle.
                    foreach (var entry in WorldIdentity.Build(registration, package, (TaleWorlds.CampaignSystem.Map.IMapScene)map, MNBL.ProceduralWorld.Persistence.WorldBinding.NoContent)) boot.Identity.Entries[entry.Key] = entry.Value;
                    world["package"] = package.Directory; world["identity"] = new Dictionary<string, string>(boot.Identity.Entries);
                    trace("world_package_written");
                }
                catch (Exception error) { world["packageError"] = error.ToString(); failures.Add("world package/identity: " + error.Message); }
                // MMR-40: the factory-built settlement entities bound in the campaign (the first world), and nothing of it left in the next.
                if (context.Int("settlementEntityProbe", 0) != 0)
                {
                    var entityFailures = new List<string>();
                    if (worldIndex == 0)
                    {
                        world["settlementEntities"] = MNBL.ProceduralWorld.Diagnostics.SettlementEntityProbe.Run(registration, placement, entityFailures);
                        settlementEntityWorld = registration.WorldId;
                        settlementEntityIds = TaleWorlds.CampaignSystem.Settlements.Settlement.All.Select(s => s.StringId).Where(id => id.StartsWith(registration.WorldId + "_", StringComparison.Ordinal)).ToList();
                    }
                    else if (settlementEntityIds != null) world["settlementEntityTeardown"] = MNBL.ProceduralWorld.Diagnostics.SettlementEntityProbe.Teardown(settlementEntityWorld, registration.WorldId, settlementEntityIds, entityFailures);
                    failures.AddRange(entityFailures.Select(f => "settlement entities: " + f));
                    trace("settlement_entities_checked:" + entityFailures.Count);
                }
                // MMR-42: every generated settlement's location scenes as the campaign holds them: the contract locations (center, lord's hall,
                // a port town's port, village_center, hideout_center) against the mission scene contracts, every named scene against the engine's
                // own scene lookup; the battle map's resolver record; the mismatch guard so far.
                if (context.Int("missionScenesProbe", 0) != 0)
                {
                    var audit = MNBL.ProceduralWorld.Missions.LocationSceneAudit.Run(registration.WorldId);
                    foreach (var problem in audit.Problems) failures.Add("mission scenes: " + problem);
                    if (audit.EngineMissing.Count > 0) failures.Add("mission scenes: the engine finds no scene file for " + String.Join(", ", audit.EngineMissing.Take(5)));
                    var guard = MNBL.ProceduralWorld.Missions.MissionScenePatches.Snapshot();
                    world["missionScenes"] = new Dictionary<string, object> { ["catalogueSha256"] = audit.CatalogueSha256, ["settlements"] = audit.Settlements, ["sceneReferences"] = audit.References,
                        ["engineMissing"] = audit.EngineMissing.ToList(), ["guardObservationsAtStart"] = guard.Count, ["guardMismatchesAtStart"] = MNBL.ProceduralWorld.Missions.MissionScenePatches.Mismatches };
                    trace("mission_scenes_checked:" + audit.Settlements.Count + ":" + audit.References);
                }
                // MMR-41: the semantic route set through the campaign's engine queries and navigation caches (the first world).
                if (context.Int("playabilityProbe", 0) != 0)
                {
                    // The cache key now (War Sails' wrapper has set the drop-off ports) must be the key the caches were acquired under.
                    var acquired = support.ContainsKey("generatedNavigationCaches") ? ((Dictionary<string, object>)support["generatedNavigationCaches"])["navigationCache"] as Dictionary<string, object> : null;
                    var cacheMap = TaleWorlds.CampaignSystem.Campaign.Current.MapSceneWrapper as MNBL.ProceduralWorld.Surface.GeneratedMapScene;
                    string keyNow = MNBL.ProceduralWorld.Surface.WorldNavigationCache.KeyNow(cacheMap, registration.SceneName, registration.WorldId);
                    world["navigationCacheKeyAtCampaignStart"] = new Dictionary<string, object> { ["acquired"] = acquired == null ? null : acquired["keySha256"], ["now"] = keyNow,
                        ["same"] = acquired != null && (string)acquired["keySha256"] == keyNow };
                    if (acquired == null || (string)acquired["keySha256"] != keyNow) failures.Add("playability: the navigation cache key changed after the caches were acquired");
                    if (worldIndex == 0)
                    {
                        // playabilityStartCultures (comma-separated): only these cultures' starts, for a fixture without startup fixtures, whose other
                        // cultures keep their installed Calradia start points; recorded in the report (startCultures, excludedStarts).
                        string startList = context.Str("playabilityStartCultures", "");
                        var startCultures = startList.Length == 0 ? null : new HashSet<string>(startList.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()), StringComparer.Ordinal);
                        var playability = MNBL.ProceduralWorld.Surface.CampaignPlayability.Run(registration.WorldId, trace, startCultures);
                        world["playability"] = playability;
                        if (!Equals(playability["activatable"], true)) failures.Add("playability: " + ((System.Collections.IList)playability["failures"]).Count + " semantic routes failed");
                    }
                }
                start = context.Seconds;
                while (context.Seconds - start < context.Num("holdSeconds", 25)) { boot.Tick(); yield return null; }
                double gameplayDays = context.Num("gameplayDays", 0);
                if (failures.Count == 0 && gameplayDays > 0)
                {
                    var gameplay = new Dictionary<string, object>(); world["gameplay"] = gameplay;
                    var aliasIds = new HashSet<string>(registration.Dataset.RequiredAliases.Select(k => k.Substring(k.IndexOf('.') + 1)), StringComparer.Ordinal);
                    var steps = GeneratedCampaignGameplay.Run(context, boot, registration.WorldId, aliasIds, gameplay, gameplayDays,
                        context.Str("gameplaySpeed", "fast") != "play", context.Int("gameplayEntryProbe", 0) != 0, startupAudit.Length > 0);
                    while (steps.MoveNext()) yield return steps.Current;
                    string after = Path.Combine(context.Directory, "generated-campaign-" + context.ProcessId + "-" + worldIndex + "-gameplay.bmp");
                    Utilities.TakeScreenshot(after);
                    context.Outputs["world" + worldIndex + "GameplayScreenshot"] = after;
                    failures.AddRange(((List<string>)gameplay["failures"]).Select(f => "gameplay: " + f));
                    if (startupAudit.Length > 0) { world["startupAuditAfterGameplay"] = StartupAuditProbe.AfterGameplay(registration, boot, failures); trace("startup_spawns_checked"); }
                    if (registration.NavalEnabled) { world["navalAuditAfterGameplay"] = NavalContentProbe.AfterGameplay(registration, boot, failures); trace("naval_fleets_checked"); }
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
                // MMR-62: the initial save's journaled publication; with initialSaveCrash (default) it leaves every interruption boundary for
                // the save_recovery probe and kills the process.
                if (failures.Count == 0 && context.Int("initialSaveProbe", 0) != 0)
                {
                    var initial = new Dictionary<string, object>(); world["initialSave"] = initial;
                    var steps = MNBL.ProceduralWorld.Diagnostics.InitialSaveProbe.Publish(context, boot, registration, initial);
                    while (steps.MoveNext()) yield return steps.Current;
                    failures.AddRange(((List<string>)initial["failures"]).Select(f => "initialSave: " + f));
                }
                if (failures.Count == 0 && context.Int("wrapProbe", 0) != 0)
                {
                    var wrap = new Dictionary<string, object>(); world["wrap"] = wrap;
                    var steps = WorldWrapProbe.Run(context, boot, registration.WorldId, wrap);
                    while (steps.MoveNext()) yield return steps.Current;
                    failures.AddRange(((List<string>)wrap["failures"]).Select(f => "wrap: " + f));
                }
                // MMR-65: route execution and spatial queries across the seam; runs after the wrap probe even when that reported failures.
                if (context.Int("movementProbe", 0) != 0 && failures.All(f => f.StartsWith("wrap: ", StringComparison.Ordinal)))
                {
                    var movement = new Dictionary<string, object>(); world["movement"] = movement;
                    var steps = MNBL.ProceduralWorld.Diagnostics.PeriodicMovementProbe.Run(context, boot, registration, movement);
                    while (steps.MoveNext()) yield return steps.Current;
                    failures.AddRange(((List<string>)movement["failures"]).Select(f => "movement: " + f));
                }
                // MMR-66: the map presentation across the seam (camera, display copies, picking, labels); after the wrap and movement probes,
                // even when those reported failures.
                if (context.Int("presentationProbe", 0) != 0 && failures.All(f => f.StartsWith("wrap: ", StringComparison.Ordinal) || f.StartsWith("movement: ", StringComparison.Ordinal)))
                {
                    var presentation = new Dictionary<string, object>(); world["presentation"] = presentation;
                    var steps = MNBL.ProceduralWorld.Diagnostics.PeriodicPresentationProbe.Run(context, boot, registration.WorldId,
                        world.ContainsKey("wrap") ? world["wrap"] as Dictionary<string, object> : null, presentation);
                    while (steps.MoveNext()) yield return steps.Current;
                    failures.AddRange(((List<string>)presentation["failures"]).Select(f => "presentation: " + f));
                }
                if (failures.Count == 0 && context.Int("waterClimateProbe", 0) != 0)
                {
                    var water = new Dictionary<string, object>(); world["waterClimate"] = water;   // MMR-39
                    var steps = MNBL.ProceduralWorld.Diagnostics.WaterClimateProbe.Run(context, boot, registration, water);
                    while (steps.MoveNext()) yield return steps.Current;
                    failures.AddRange(((List<string>)water["failures"]).Select(f => "waterClimate: " + f));
                }
                // MMR-42: every battle scene choice the guard observed in this world's campaign (after every mission-producing probe), and its mismatches.
                if (context.Int("missionScenesProbe", 0) != 0)
                {
                    var observed = MNBL.ProceduralWorld.Missions.MissionScenePatches.Snapshot();
                    world["missionSceneGuard"] = new Dictionary<string, object> { ["observations"] = observed, ["observed"] = MNBL.ProceduralWorld.Missions.MissionScenePatches.Observed,
                        ["mismatches"] = MNBL.ProceduralWorld.Missions.MissionScenePatches.Mismatches };
                    if (MNBL.ProceduralWorld.Missions.MissionScenePatches.Mismatches > 0) failures.Add("mission scenes: the battle scene guard flagged " + MNBL.ProceduralWorld.Missions.MissionScenePatches.Mismatches + " choices");
                }
                if (failures.Count != 0) throw new InvalidOperationException("Generated campaign census failed: " + String.Join("; ", failures));
                world["completed"] = true;
                foreach (string id in registration.Dataset.Instances) priorIds.Add(id);
                // Use the normal installed menu-exit lifecycle, without registry clearing or keeping strong references to the old campaign
                // (MMR-37's map surface check holds the old map scene object on purpose until the next world's check, as a stale reference
                // would be, and drops it right after).
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
            // MMR-34 startup fixtures add world aliases (the land bandit clans and the retirement retreat), recreated by every world.
            var aliasIds = new HashSet<string>(dataset.RequiredAliases.Select(k => k.Substring(k.IndexOf('.') + 1)), StringComparer.Ordinal);
            if (registration.StartupEnabled && (Settlement.Find("retirement_retreat") == null || !(Settlement.Find("retirement_retreat").SettlementComponent is RetirementSettlementComponent)))
                failures.Add("The startup's retirement retreat is missing or has no RetirementSettlementComponent.");
            var settlements = Settlement.All.Where(s => !aliasIds.Contains(s.StringId)).ToArray();
            // MMR-33 fixtures add a castle, its village and a hideout to the first world.
            int extra = registration.FixturesEnabled ? 1 : 0, hideouts = extra + (registration.StartupEnabled ? 4 : 0);
            if (settlements.Length != 4 + 2 * extra + hideouts || settlements.Count(s => s.IsTown) != 2 || settlements.Count(s => s.IsVillage) != 2 + extra ||
                settlements.Count(s => s.IsCastle) != extra || settlements.Count(s => s.IsHideout) != hideouts)
                failures.Add("Expected exactly two generated towns and two villages (plus the fixtures' castle, village and hideout).");
            foreach (var settlement in settlements)
            {
                if (!settlement.StringId.StartsWith(dataset.WorldId + "_", StringComparison.Ordinal)) failures.Add("Unexpected settlement: " + settlement.StringId);
                if (priorIds.Contains("Settlement." + settlement.StringId)) failures.Add("Prior-world settlement survived: " + settlement.StringId);
                if (!settlement.IsHideout && (settlement.OwnerClan == null || !settlement.OwnerClan.StringId.StartsWith(dataset.WorldId + "_", StringComparison.Ordinal))) failures.Add("Settlement owner is outside this world: " + settlement.StringId);
                if (settlement.IsTown && !settlement.HasPort) failures.Add("Generated town has no port: " + settlement.StringId);
                if (settlement.IsVillage && (settlement.Village.Bound == null || !settlement.Village.Bound.StringId.StartsWith(dataset.WorldId + "_", StringComparison.Ordinal))) failures.Add("Invalid village binding: " + settlement.StringId);
            }
            foreach (var clan in Clan.All)
            {
                if (!aliasIds.Contains(clan.StringId) && !clan.StringId.StartsWith(dataset.WorldId + "_", StringComparison.Ordinal)) failures.Add("Unexpected clan: " + clan.StringId);
                if (priorIds.Contains("Faction." + clan.StringId) && !aliasIds.Contains(clan.StringId)) failures.Add("Prior-world clan survived: " + clan.StringId);
            }
            int banditAliases = dataset.RequiredAliases.Count(k => k.StartsWith("Faction.", StringComparison.Ordinal) && k != "Faction.player_faction");
            if (Clan.All.Count != 3 + banditAliases || Kingdom.All.Count != 2) failures.Add("Expected two generated noble clans/kingdoms plus the player clan (and the startup's bandit clans).");
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
    
        // MMR-37: the campaign map scene is the runtime backend's: its generation is current, its ledger names who releases the scene and
        // the agent renderer, its guarded IMapScene ground (land and sea), height-and-normal and map patch answers agree with the native scene's, and the
        // previous world's map scene (if still reachable) refuses queries once this world's generation began.
        private static Dictionary<string, object> CampaignMapSurface(GeneratedMapScene map, GeneratedMapScene previous, List<string> failures)
        {
            var result = new Dictionary<string, object> { ["generation"] = map.Token.Id, ["currentGeneration"] = WorldGenerations.Current, ["world"] = map.World.Record() };
            if (map.Token.Id != WorldGenerations.Current) failures.Add("map surface: the map scene's generation " + map.Token.Id + " is not current (" + WorldGenerations.Current + ")");
            TaleWorlds.CampaignSystem.Map.IMapScene wrapper = map;
            var scene = map.Scene;
            Vec2 size = wrapper.GetTerrainSize();
            var rows = new List<object>(); result["queries"] = rows;
            for (int j = 1; j <= 3; j++) for (int i = 1; i <= 3; i++)
            {
                var p = new Vec2(size.x * i / 4 + .37f, size.y * j / 4 + .29f);
                var land = new CampaignVec2(p, true); var sea = new CampaignVec2(p, false);
                float height = float.NaN, seaHeight = float.NaN, combined, nativeLand = float.NaN, nativeSea = float.NaN; Vec3 normal;
                bool hit = wrapper.GetHeightAtPoint(in land, ref height), seaHit = wrapper.GetHeightAtPoint(in sea, ref seaHeight);
                wrapper.GetTerrainHeightAndNormal(p, out combined, out normal);
                float terrain = scene.GetTerrainHeight(p, true);
                // The installed IMapScene ground query is Scene.GetHeightAtPoint with body flags 16 | 128 (land) or 16 | 544321929 (sea): on
                // a campaign map it meets the water surface above underwater terrain, so it is compared with the same native query.
                bool nativeLandHit = scene.GetHeightAtPoint(p, (BodyFlags)(16 | 128), ref nativeLand), nativeSeaHit = scene.GetHeightAtPoint(p, (BodyFlags)(16 | 544321929), ref nativeSea);
                var patch = wrapper.GetMapPatchAtPosition(in land);
                rows.Add(new { position = new[] { p.x, p.y }, mapSceneLand = hit ? (object)height : null, nativeLand = nativeLandHit ? (object)nativeLand : null, mapSceneSea = seaHit ? (object)seaHeight : null,
                    nativeSea = nativeSeaHit ? (object)nativeSea : null, combined, terrain, patchScene = patch.sceneIndex, patchCoordinates = new[] { patch.normalizedCoordinates.x, patch.normalizedCoordinates.y } });
                // Ground on the campaign map is the terrain or, above underwater terrain, the water surface at the sea datum (the fixture
                // placements' seaLevel 0): max(terrain, 0). The height-and-normal answer is the bare terrain (a different native call).
                float groundRule = Math.Max(terrain, 0f);
                if (!hit || !seaHit || Math.Abs(height - groundRule) > .05f || Math.Abs(seaHeight - groundRule) > .05f || Math.Abs(combined - terrain) > .05f)
                    failures.Add("map surface: the map scene's answers at " + p + " disagree with the native terrain (ground " + height + "/" + seaHeight + ", terrain " + terrain + ", combined " + combined + ")");
                if (patch.sceneIndex < 0) failures.Add("map surface: no map patch at " + p);
            }
            if (previous == null) result["previousMap"] = "none (first world)";
            else
            {
                result["previousMapGeneration"] = previous.Token.Id; result["previousMapWorld"] = previous.World.Record();
                try { ((TaleWorlds.CampaignSystem.Map.IMapScene)previous).GetTerrainSize(); result["previousMap"] = "ANSWERED"; failures.Add("map surface: the previous world's map scene answered after this world's generation began"); }
                catch (StaleWorldException error) { result["previousMap"] = "refused: " + error.Message; }
                try { float h = 0; var p = new CampaignVec2(new Vec2(10, 10), true); ((TaleWorlds.CampaignSystem.Map.IMapScene)previous).GetHeightAtPoint(in p, ref h); result["previousMapHeight"] = "ANSWERED"; failures.Add("map surface: the previous world's map scene answered a height query after this world's generation began"); }
                catch (StaleWorldException) { result["previousMapHeight"] = "refused"; }
                try { var direct = previous.Scene; result["previousMapScene"] = "ANSWERED"; failures.Add("map surface: the previous world's map scene handed out its native scene after this world's generation began"); }
                catch (StaleWorldException) { result["previousMapScene"] = "refused"; }
            }
            return result;
        }
    }
}
