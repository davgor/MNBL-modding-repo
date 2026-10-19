using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using HarmonyLib;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;
using MNBL.ProceduralWorld.Surface;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-34: the startup audit of a generated campaign (docs/campaign-behavior-compatibility.md), run by the generated_campaign probe with
    // startupAudit=<repo>. At the visible paused map:
    //   * roster: every enabled campaign behaviour and every registered game model (Campaign.GetCampaignBehaviors, GameModelsManager.
    //     GetGameModels, every assembly) against the compatibility inventory: each must be listed, excluded ones absent; the effective model
    //     of every GameModels contract must be listed and not excluded;
    //   * listeners: this module's behaviours each subscribe once per event (CampaignEvents' non-serialized listener lists);
    //   * literals: every world id the IL scan found in an enabled type resolves only when it is a world alias (bandit clans, retreat),
    //     and every literal template lookup resolves;
    //   * the wanderer introduction (LordConversationsCampaignBehavior): the world transpiler is in place, the installed lookup finds
    //     nothing, and the installed condition, run with a generated wanderer as the one-to-one conversation hero, sets IMPERIALCAPITAL
    //     to the generated town;
    //   * start: with a startup, every creation culture's StartingPoint is its planned start (not its installed Calradia point) and the
    //     main party stands at its culture's; the start town's gate is valid land, its port valid water within the MMR-17 port-gate
    //     envelope, the engine's port-to-gate route exists, a naval route reaches the sea destination, and the shipyard is the ship source;
    //   * startup: the bandit aliases with their hideout home, the retreat (RetirementSettlementComponent) with its behaviour kept, the
    //     plan's notables, companions and relation, startup patches removed after the new game, and the War Sails storyline inactive.
    // After the gameplay window: the spawn assertions over every party (natural counts kept apart from the forced spawns), plus a
    // caravan, a villager party, a fresh lord party (a new lord of the ruler's clan) and a bandit party spawned through the installed
    // factories at installed spawn points, each on valid navigation. In a later world: the earlier campaign's teardown record
    // (GeneratedGameManager.LastTeardown: patches removed, this module's listeners removed from the ending campaign's events), a new
    // CampaignEvents instance, and no fixture or periodic static of the earlier world.
    internal static class StartupAuditProbe
    {
        public static Dictionary<string, object> AtStart(string repo, GeneratedCampaignRegistration registration, GeneratedGameManager boot, List<string> failures)
        {
            var report = new Dictionary<string, object>();
            Action<bool, string> check = (ok, label) => { if (!ok) failures.Add("startup audit: " + label); };
            try
            {
                var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
                var inventory = (IDictionary<string, object>)((IDictionary<string, object>)json.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen/policies/campaign-compatibility.json"))))["types"];
                var scan = (IDictionary<string, object>)json.DeserializeObject(File.ReadAllText(Path.Combine(repo, "evidence/fwg/behavior-scan.json")));
                var playability = (IDictionary<string, object>)json.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen/policies/playability.json")));
                Func<string, string> status = type => { object v; return inventory.TryGetValue(type, out v) ? (string)((IDictionary<string, object>)v)["status"] : null; };
                report["installedVersions"] = GeneratedPatches.InstalledVersions();
                // Roster: every behaviour and every registered model, whatever its assembly.
                var behaviors = Campaign.Current.GetCampaignBehaviors<CampaignBehaviorBase>().ToList();
                var own = behaviors.Where(b => b.GetType().Assembly == typeof(StartupAuditProbe).Assembly).ToList();
                var installed = behaviors.Where(b => b.GetType().Assembly != typeof(StartupAuditProbe).Assembly).ToList();
                var models = Campaign.Current.Models.GetGameModels().ToList();
                var installedModels = models.Where(m => m.GetType().Assembly != typeof(StartupAuditProbe).Assembly).ToList();
                var types = installed.Select(b => b.GetType().FullName).Concat(installedModels.Select(m => m.GetType().FullName)).Distinct().ToList();
                var unlisted = types.Where(t => status(t) == null).OrderBy(t => t, StringComparer.Ordinal).ToList();
                var excludedPresent = types.Where(t => status(t) == "excluded").ToList();
                report["roster"] = new Dictionary<string, object> {
                    ["behaviors"] = installed.Select(b => b.GetType().FullName).OrderBy(t => t, StringComparer.Ordinal).ToList(),
                    ["ownBehaviors"] = own.Select(b => b.GetType().FullName).OrderBy(t => t, StringComparer.Ordinal).ToList(),
                    ["registeredModels"] = installedModels.Select(m => m.GetType().FullName).OrderBy(t => t, StringComparer.Ordinal).ToList(),
                    ["registeredModelAssemblies"] = installedModels.GroupBy(m => m.GetType().Assembly.GetName().Name).ToDictionary(g => g.Key, g => g.Count()),
                    ["ownModels"] = models.Except(installedModels).Select(m => m.GetType().FullName).ToList(),
                    ["statusCounts"] = types.GroupBy(t => status(t) ?? "unlisted").ToDictionary(g => g.Key, g => g.Count()),
                    ["unlisted"] = unlisted, ["excludedPresent"] = excludedPresent };
                check(unlisted.Count == 0, "enabled behaviours or registered models missing from the compatibility inventory: " + String.Join(", ", unlisted));
                check(excludedPresent.Count == 0, "excluded behaviours or models are enabled: " + String.Join(", ", excludedPresent));
                bool startup = registration.StartupEnabled;
                bool retirement = installed.Any(b => b.GetType().FullName == "SandBox.CampaignBehaviors.RetirementCampaignBehavior");
                check(retirement == startup, "RetirementCampaignBehavior is " + (retirement ? "kept" : "removed") + " but the world " + (startup ? "supplies" : "does not supply") + " the retreat");
                check(!installed.Any(b => b.GetType().FullName.EndsWith(".BackstoryCampaignBehavior", StringComparison.Ordinal)), "BackstoryCampaignBehavior is enabled");
                // The effective model of each contract (one instance per GameModels property): listed in the inventory and not excluded.
                var properties = Campaign.Current.Models.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => typeof(GameModel).IsAssignableFrom(p.PropertyType)).ToList();
                var effective = properties.Select(p => new KeyValuePair<string, object>(p.Name, p.GetValue(Campaign.Current.Models))).Where(x => x.Value != null)
                    .ToDictionary(x => x.Key, x => x.Value.GetType().FullName, StringComparer.Ordinal);
                var effectiveUnlisted = effective.Values.Where(t => t.StartsWith("MNBL.", StringComparison.Ordinal) ? false : status(t) == null).Distinct().ToList();
                report["effectiveModels"] = effective; report["effectiveContracts"] = effective.Count; report["registeredModelCount"] = models.Count;
                check(effectiveUnlisted.Count == 0, "effective models missing from the compatibility inventory: " + String.Join(", ", effectiveUnlisted));
                check(effective.Values.All(t => status(t) != "excluded"), "an excluded model won selection");
                check(effective.ContainsKey("NotableSpawnModel") && effective.ContainsKey("MapDistanceModel"), "effective models were not read");
                // Listeners.
                var counts = CampaignListeners.Of(CampaignListeners.EventsOf(Campaign.Current));
                var ownCounts = own.ToDictionary(b => b.GetType().Name, b => counts.Where(c => c.Key.Value == b).ToDictionary(c => c.Key.Key, c => c.Value));
                report["listeners"] = ownCounts;
                check(own.All(b => counts.Where(c => c.Key.Value == b).All(c => c.Value == 1)), "a module behaviour subscribed to one event more than once");
                check(own.All(b => b is GeneratedCharacterCreation || counts.Any(c => c.Key.Value == b)), "a module behaviour listens to nothing");
                // Literal world ids and template lookups of enabled types.
                var scanned = ((IEnumerable)scan["types"]).Cast<IDictionary<string, object>>().Where(t => status((string)t["type"]) == "retained" || status((string)t["type"]) == "adapted").ToList();
                var worldIds = scanned.SelectMany(t => ((IDictionary<string, object>)t["worldIds"]).Keys).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
                var aliases = new HashSet<string>(registration.Dataset.RequiredAliases.Select(k => k.Substring(k.IndexOf('.') + 1)), StringComparer.Ordinal);
                var present = worldIds.Where(k => Resolves(k)).ToList();
                var aliasesMissing = worldIds.Where(k => aliases.Contains(k.Split(':')[1]) && !Resolves(k)).ToList();
                report["worldLiterals"] = new Dictionary<string, object> { ["checked"] = worldIds, ["present"] = present };
                check(present.All(k => aliases.Contains(k.Split(':')[1])), "a Calradia world object an installed behaviour names survived in the generated world (or an alias this world does not supply): " + String.Join(", ", present.Where(k => !aliases.Contains(k.Split(':')[1]))));
                check(aliasesMissing.Count == 0, "world aliases an installed behaviour names are missing: " + String.Join(", ", aliasesMissing));
                var literal = new Regex("^MBObjectManager\\.GetObject<(\\w+)>\\(\"([^\"]+)\"\\) in ");
                var lookups = scanned.SelectMany(t => ((IEnumerable)t["lookups"]).Cast<string>()).Select(l => literal.Match(l)).Where(m => m.Success)
                    .Select(m => m.Groups[1].Value + ":" + m.Groups[2].Value).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                var unresolved = lookups.Where(l => !TemplateResolves(l)).ToList();
                report["templateLookups"] = new Dictionary<string, object> { ["checked"] = lookups.Count, ["unresolved"] = unresolved };
                check(unresolved.Count == 0, "literal template lookups do not resolve: " + String.Join(", ", unresolved));
                // The wanderer introduction.
                report["wandererIntroduction"] = WandererIntroduction(registration, boot, startup, check);
                // Start.
                report["start"] = Start(registration, startup, playability, check);
                // Startup supplied by the world.
                if (startup)
                {
                    var clans = new[] { "looters", "deserters", "sea_raiders", "mountain_bandits", "forest_bandits", "desert_bandits", "steppe_bandits" };
                    var hideouts = Settlement.All.Where(x => x.IsHideout).ToList();
                    var bandits = clans.Select(id => Clan.All.FirstOrDefault(c => c.StringId == id)).ToList();
                    report["banditClans"] = clans.Zip(bandits, (id, c) => new KeyValuePair<string, object>(id, c == null ? null : (object)new Dictionary<string, object> {
                        ["isBandit"] = c.IsBanditFaction, ["home"] = c.HomeSettlement == null ? null : c.HomeSettlement.StringId, ["culture"] = c.Culture.StringId,
                        ["homeCulture"] = c.HomeSettlement == null ? null : c.HomeSettlement.Culture.StringId })).ToDictionary(x => x.Key, x => x.Value);
                    report["hideoutCultures"] = hideouts.Select(x => x.StringId + ":" + x.Culture.StringId).ToList();
                    check(bandits.All(c => c != null && c.IsBanditFaction && c.HomeSettlement != null && c.HomeSettlement.IsHideout &&
                                           (c.HomeSettlement.Culture == c.Culture || !hideouts.Any(x => x.Culture == c.Culture))),
                          "the bandit clan aliases are missing, not bandits, or not homed at a hideout of their culture");
                    check(bandits.Where(c => c != null && c.Culture.CanHaveSettlement).All(c => hideouts.Any(x => x.Culture == c.Culture)),
                          "a bandit culture BanditSpawnCampaignBehavior spawns for has no hideout");
                    var retreat = Settlement.Find("retirement_retreat");
                    report["retreat"] = retreat == null ? null : new Dictionary<string, object> { ["component"] = retreat.SettlementComponent.GetType().FullName, ["culture"] = retreat.Culture.StringId,
                        ["position"] = new[] { retreat.Position.ToVec2().x, retreat.Position.ToVec2().y }, ["navigation"] = GeneratedSpawnAssertions.Problem(retreat.Position, MobileParty.NavigationType.Default) };
                    check(retreat != null && retreat.SettlementComponent is RetirementSettlementComponent && GeneratedSpawnAssertions.Problem(retreat.Position, MobileParty.NavigationType.Default) == null,
                          "the retirement retreat is missing, not a RetirementSettlementComponent, or not on valid land navigation");
                    var s = boot.Startup; var plan = registration.Dataset.StartupPlan;
                    var map = s == null ? new Dictionary<string, Hero>() : s.Map;
                    var notablesBySettlement = plan.Notables.GroupBy(h => h.Settlement).ToDictionary(g => g.Key, g => g.ToList());
                    Func<GeneratedCampaignInitializer.HeroPlan, Hero, bool> named = (i, h) => h.Name.ToString() == new TextObject(i.Name).ToString() && h.FirstName != null && h.FirstName.ToString() == i.Name.Split(' ')[0];
                    var notableMismatch = notablesBySettlement.Where(g => { var st = Settlement.Find(g.Key); return st == null || st.Notables.Count != g.Value.Count ||
                        !g.Value.All(i => map.ContainsKey(i.Id) && map[i.Id].CurrentSettlement == st && named(i, map[i.Id])); }).Select(g => g.Key).ToList();
                    var companionMismatch = plan.Companions.Where(i => !map.ContainsKey(i.Id) || map[i.Id].Occupation != Occupation.Wanderer || map[i.Id].CurrentSettlement == null ||
                                                                       map[i.Id].CurrentSettlement.StringId != i.Settlement || !named(i, map[i.Id])).Select(i => i.Id).ToList();
                    var rulerA = Hero.Find(registration.WorldId + "_ruler_1"); var rulerB = Hero.Find(registration.WorldId + "_ruler_2");
                    int relation = rulerA != null && rulerB != null ? CharacterRelationManager.GetHeroRelation(rulerA, rulerB) : int.MinValue;
                    report["notableDetail"] = notableMismatch.ToDictionary(id => id, id => (object)new Dictionary<string, object> {
                        ["planned"] = notablesBySettlement[id].Select(i => i.Id + "=" + (map.ContainsKey(i.Id) ? map[i.Id].StringId + "/" + map[i.Id].Name + "/" + map[i.Id].FirstName + "/" + (map[i.Id].CurrentSettlement == null ? "nowhere" : map[i.Id].CurrentSettlement.StringId) : "unmapped")).ToList(),
                        ["actual"] = Settlement.Find(id) == null ? new List<string>() : Settlement.Find(id).Notables.Select(h => h.StringId + "/" + h.Name + "/" + h.Occupation + "/" + (h.Template == null ? "?" : h.Template.StringId)).ToList() });
                    report["plan"] = new Dictionary<string, object> { ["notables"] = s == null ? -1 : s.NotablesCreated, ["companions"] = s == null ? -1 : s.CompanionsCreated, ["relations"] = s == null ? -1 : s.RelationsApplied, ["namesRestored"] = s == null ? -1 : s.NamesRestored,
                        ["mapped"] = map.Count, ["problems"] = s == null ? new List<string> { "no startup behaviour" } : s.Problems, ["notableMismatch"] = notableMismatch, ["companionMismatch"] = companionMismatch,
                        ["rulerRelation"] = relation, ["patchesAfterNewGame"] = GeneratedPatches.Patched(), ["activeAfterNewGame"] = GeneratedStartupBehavior.Active != null,
                        ["sampleNames"] = map.Take(3).Select(h => h.Key + "=" + h.Value.Name + " (first name " + h.Value.FirstName + ")").ToList() };
                    check(s != null && s.Problems.Count == 0 && s.NotablesCreated == plan.Notables.Count && s.CompanionsCreated == plan.Companions.Count && s.RelationsApplied == plan.Relations.Count && map.Count == plan.Notables.Count + plan.Companions.Count,
                          "the startup plan was not instantiated exactly: " + (s == null ? "no startup behaviour" : String.Join("; ", s.Problems)));
                    check(notableMismatch.Count == 0, "settlements whose notables are not exactly the plan's (named with the intent's full and first name, placed, none extra): " + String.Join(", ", notableMismatch));
                    check(companionMismatch.Count == 0, "companions not created as planned: " + String.Join(", ", companionMismatch));
                    check(relation == plan.Relations[0].Value, "the planned ruler relation is " + relation + ", not " + plan.Relations[0].Value);
                    check(!GeneratedPatches.StartupApplied && !GeneratedPatches.Patched().Any(p => p.StartsWith(GeneratedPatches.StartupId, StringComparison.Ordinal)) && GeneratedStartupBehavior.Active == null,
                          "the startup patches outlived the new game");
                }
                // The patches applied now are manifest-listed, with their owners.
                report["patches"] = GeneratedPatches.Patched();
                report["manifest"] = GeneratedPatches.Manifest.Select(d => d.Id + " " + d.Kind + " " + d.Owner + ": " + d.Target).ToList();
                check(GeneratedPatches.WorldApplied && GeneratedPatches.Patched().Any(p => p.StartsWith(GeneratedPatches.WorldId + ":", StringComparison.Ordinal)), "the world patches are not applied");
                check((registration.Periodic != null) == GeneratedPatches.Patched().Any(p => p.StartsWith(PeriodicPatches.HarmonyId + ":", StringComparison.Ordinal)),
                      "the periodic patches are " + (registration.Periodic != null ? "missing on a periodic world" : "applied on a non-periodic world"));
                check(GeneratedColorGrade.Applied && GeneratedPatches.Patched().Any(p => p.StartsWith(GeneratedColorGrade.HarmonyId + ":", StringComparison.Ordinal)),
                      "the colour-grade patch (MMR-39) is not applied on a generated map");
                // The storyline is inactive, or not even added (null: no NavalStorylineCampaignBehavior in the roster).
                var storyline = StorylineActive();
                report["storylineActive"] = storyline; report["storylineBehavior"] = storyline == null ? "absent" : "present";
                check(storyline != true, "the War Sails storyline is active");
            }
            catch (Exception error) { report["error"] = error.ToString(); failures.Add("startup audit: " + error.Message); }
            return report;
        }

        // B1: the installed wanderer-introduction condition on a generated world.
        private static Dictionary<string, object> WandererIntroduction(GeneratedCampaignRegistration registration, GeneratedGameManager boot, bool startup, Action<bool, string> check)
        {
            var report = new Dictionary<string, object>();
            var target = GeneratedPatches.WandererIntroduction;
            var info = target == null ? null : Harmony.GetPatchInfo(target);
            var transpilers = info == null ? new List<string>() : info.Transpilers.Select(p => p.owner).ToList();
            var current = target == null ? new List<CodeInstruction>() : PatchProcessor.GetCurrentInstructions(target);
            bool callsHelper = current.Any(i => i.Calls(GeneratedPatches.FindFirstOrGeneratedMethod)), callsInstalled = current.Any(i => i.Calls(GeneratedPatches.InstalledFindFirst));
            var installedResult = Settlement.FindFirst(s => s.StringId == "town_ES4");
            var expected = GeneratedPatches.FindFirstOrGenerated(s => s.StringId == "town_ES4");
            report["transpilers"] = transpilers; report["currentCallsHelper"] = callsHelper; report["currentCallsInstalledFindFirst"] = callsInstalled;
            report["installedLookupFinds"] = installedResult == null ? null : installedResult.StringId;
            report["fallback"] = expected == null ? null : expected.StringId + " (" + expected.Name + ", " + expected.Culture.StringId + ", prosperity " + expected.Town.Prosperity + ")";
            check(transpilers.Contains(GeneratedPatches.WorldId) && callsHelper && !callsInstalled, "the wanderer introduction does not route its town_ES4 lookup through FindFirstOrGenerated");
            check(installedResult == null && expected != null && expected.IsTown && expected.StringId.StartsWith(registration.WorldId + "_", StringComparison.Ordinal),
                  "the installed town_ES4 lookup is not empty, or the fallback is not a generated town");
            // Run the installed condition with a generated wanderer as the one-to-one conversation hero (ConversationManager's agent list).
            var behavior = Campaign.Current.GetCampaignBehavior<LordConversationsCampaignBehavior>();
            Hero wanderer = boot.Startup != null ? boot.Startup.Map.Values.FirstOrDefault(h => h.IsWanderer && h.IsAlive) : null;
            if (wanderer == null) wanderer = Hero.AllAliveHeroes.Where(h => h.IsWanderer && h.Template != null).OrderBy(h => h.StringId, StringComparer.Ordinal).FirstOrDefault();
            var agentsField = typeof(ConversationManager).GetField("_conversationAgents", BindingFlags.Instance | BindingFlags.NonPublic);
            var agents = agentsField == null ? null : agentsField.GetValue(Campaign.Current.ConversationManager) as List<IAgent>;
            var metField = typeof(LordConversationsCampaignBehavior).GetField("_previouslyMetWandererTemplates", BindingFlags.Instance | BindingFlags.NonPublic);
            var met = behavior == null || metField == null ? null : metField.GetValue(behavior) as Dictionary<CharacterObject, CharacterObject>;
            report["wanderer"] = wanderer == null ? null : wanderer.StringId + " (" + wanderer.Name + ", template " + (wanderer.Template == null ? "?" : wanderer.Template.StringId) + ")";
            if (behavior == null || wanderer == null || agents == null || met == null || agents.Count != 0 || target == null)
            {
                report["condition"] = "not run: " + (behavior == null ? "no LordConversationsCampaignBehavior" : wanderer == null ? "no wanderer" : agents == null || met == null ? "installed fields not found" : target == null ? "no target" : "conversation agents present");
                check(!startup, "the wanderer introduction condition could not be run on the startup world (" + report["condition"] + ")");
                return report;
            }
            bool hadTemplate = met.ContainsKey(wanderer.Template);
            MBTextManager.SetTextVariable("IMPERIALCAPITAL", new TextObject("mnbl_audit_sentinel"), false);
            var agent = new ProbeAgent(wanderer.CharacterObject);
            agents.Add(agent);
            object result = null; string error = null;
            try { result = target.Invoke(behavior, null); }
            catch (TargetInvocationException e) { error = e.InnerException.GetType().FullName + ": " + e.InnerException.Message; }
            finally { agents.Remove(agent); if (!hadTemplate) met.Remove(wanderer.Template); }
            var capital = TextVariable("IMPERIALCAPITAL");
            report["conditionResult"] = result; report["conditionError"] = error; report["campaignMissionPresent"] = CampaignMission.Current != null;
            report["imperialCapital"] = capital == null ? null : capital.ToString(); report["expectedCapital"] = expected == null ? null : expected.Name.ToString();
            // On the campaign map the condition goes on past IMPERIALCAPITAL to CampaignMission.Current.Location, which needs the tavern
            // mission a real conversation runs in: with no mission that later read throws, after the capital was set.
            check(capital != null && expected != null && capital.ToString() == expected.Name.ToString(),
                  "the installed wanderer introduction did not set IMPERIALCAPITAL to the generated town (" + (capital == null ? "unset" : capital.ToString()) + "; " + (error ?? "no exception") + ")");
            check(error == null || (CampaignMission.Current == null && error.StartsWith("System.NullReferenceException", StringComparison.Ordinal)),
                  "the wanderer introduction threw something other than the map-side CampaignMission read: " + error);
            return report;
        }

        private static TextObject TextVariable(string name)
        {
            var contextField = typeof(MBTextManager).GetField("TextContext", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var context = contextField == null ? null : contextField.GetValue(null);
            var variablesField = context == null ? null : context.GetType().GetField("_variables", BindingFlags.Instance | BindingFlags.NonPublic);
            var variables = variablesField == null ? null : variablesField.GetValue(context) as Dictionary<string, TextObject>;
            if (variables == null) return null;
            TextObject value;
            if (variables.TryGetValue(name, out value)) return value;
            return variables.Where(v => String.Equals(v.Key, name, StringComparison.OrdinalIgnoreCase)).Select(v => v.Value).FirstOrDefault();
        }

        // M6: the planned starts, the main party at its culture's, and the start town's embarkation path.
        private static Dictionary<string, object> Start(GeneratedCampaignRegistration registration, bool startup, IDictionary<string, object> playability, Action<bool, string> check)
        {
            var dataset = registration.Dataset;
            var main = MobileParty.MainParty;
            var culture = Hero.MainHero.Culture;
            var at = main.Position.ToVec2();
            var report = new Dictionary<string, object> { ["culture"] = culture.StringId, ["mainParty"] = new[] { at.x, at.y }, ["navigation"] = GeneratedSpawnAssertions.Problem(main) };
            Func<double, double, double, double, double> distance = (x1, y1, x2, y2) => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));
            if (startup)
            {
                var starts = new Dictionary<string, object>();
                var wrong = new List<string>();
                foreach (var planned in dataset.StartPoints)
                {
                    var c = MBObjectManager.Instance.GetObject<CultureObject>(planned.Key);
                    var point = c == null ? new TaleWorlds.Library.Vec2(float.NaN, float.NaN) : c.StartingPoint.ToVec2();
                    var installedPoint = dataset.InstalledStartPoints[planned.Key];
                    starts[planned.Key] = new Dictionary<string, object> { ["startingPoint"] = new[] { point.x, point.y }, ["planned"] = planned.Value, ["installed"] = installedPoint };
                    if (!(distance(point.x, point.y, planned.Value[0], planned.Value[1]) < 0.05) || !(distance(point.x, point.y, installedPoint[0], installedPoint[1]) > 1)) wrong.Add(planned.Key);
                }
                report["creationCultureStarts"] = starts; report["startTown"] = dataset.StartTown;
                check(dataset.StartPoints.Count > 0 && wrong.Count == 0, "creation cultures whose StartingPoint is not their planned start or is still the installed point: " + String.Join(", ", wrong));
                double[] own;
                check(dataset.StartPoints.TryGetValue(culture.StringId, out own) && distance(at.x, at.y, own[0], own[1]) < 0.05 && GeneratedSpawnAssertions.Problem(main) == null,
                      "the main party is not at its culture's planned start on valid navigation");
            }
            else
            {
                var point = culture.StartingPoint.ToVec2();
                report["startingPoint"] = new[] { point.x, point.y };
                check(distance(at.x, at.y, point.x, point.y) < 0.05 && distance(at.x, at.y, dataset.StartX, dataset.StartY) < 0.05 && GeneratedSpawnAssertions.Problem(main) == null,
                      "the main party is not at its culture's StartingPoint (the generated gate) on valid navigation");
            }
            // The start town (the startup's planned town; the fixture world's other town is the sea destination) and its embarkation path.
            var town = Settlement.Find(dataset.StartTown ?? registration.WorldId + "_town_1"); var destination = Settlement.Find(registration.WorldId + "_town_2");
            double envelope = Convert.ToDouble(((IDictionary<string, object>)playability["ports"])["maxPortGateCells"]) * registration.NavSpacing;
            float seaDistance = town != null && destination != null ? Campaign.Current.Models.MapDistanceModel.GetDistance(town, destination, true, true, MobileParty.NavigationType.Naval) : -1;
            float portToGate = town != null && town.HasPort ? Campaign.Current.Models.MapDistanceModel.GetPortToGateDistanceForSettlement(town) : -1;
            double gatePort = town != null && town.HasPort ? distance(town.GatePosition.ToVec2().x, town.GatePosition.ToVec2().y, town.PortPosition.ToVec2().x, town.PortPosition.ToVec2().y) : -1;
            int shipyard = town == null ? 0 : town.Town.Buildings.Where(b => b.BuildingType.StringId == "building_shipyard").Select(b => b.CurrentLevel).FirstOrDefault();
            string gateProblem = town == null ? "no town" : GeneratedSpawnAssertions.Problem(town.GatePosition, MobileParty.NavigationType.Default);
            string portProblem = town != null && town.HasPort ? GeneratedSpawnAssertions.Problem(town.PortPosition, MobileParty.NavigationType.Naval) : "no port";
            report["town"] = town == null ? null : town.StringId;
            report["embarkation"] = new Dictionary<string, object> {
                ["gate"] = town == null ? null : new[] { town.GatePosition.ToVec2().x, town.GatePosition.ToVec2().y }, ["gateNavigation"] = gateProblem,
                ["port"] = town != null && town.HasPort ? new[] { town.PortPosition.ToVec2().x, town.PortPosition.ToVec2().y } : null, ["portNavigation"] = portProblem,
                ["gateToPort"] = gatePort, ["envelope"] = envelope, ["engineEnvelopeSource"] = "playability.json ports.maxPortGateCells x the placement's navigation cell (MMR-17/28)",
                ["enginePortToGate"] = portToGate, ["seaDestination"] = destination == null ? null : destination.StringId, ["seaDistance"] = seaDistance,
                ["shipSource"] = "building_shipyard level " + shipyard, ["rule"] = "the player embarks only at a town port (MMR-17)" };
            check(gateProblem == null && portProblem == null && gatePort > 0 && gatePort <= envelope + 1e-6 && portToGate > 0 && !float.IsInfinity(portToGate) && portToGate < float.MaxValue / 2,
                  "the start town's embarkation path is not legal (gate " + (gateProblem ?? "ok") + ", port " + (portProblem ?? "ok") + ", gate-port " + gatePort + " of " + envelope + ", engine port-to-gate " + portToGate + ")");
            check(destination != null && destination.HasPort && seaDistance > 0 && seaDistance < float.MaxValue / 2 && shipyard > 0, "the start town has no naval route to its sea destination or no shipyard");
            return report;
        }

        public static Dictionary<string, object> AfterGameplay(GeneratedCampaignRegistration registration, GeneratedGameManager boot, List<string> failures)
        {
            var report = new Dictionary<string, object>();
            Action<bool, string> check = (ok, label) => { if (!ok) failures.Add("startup audit after gameplay: " + label); };
            try
            {
                var checks = boot.SpawnChecks;
                check(checks != null, "no spawn assertions were registered");
                if (checks == null) return report;
                // What the game spawned itself, before the forced spawns below.
                var natural = new Dictionary<string, object>(checks.Checked.ToDictionary(k => k.Key, k => (object)k.Value));
                var naturalCreated = new Dictionary<string, object>(checks.CreatedChecked.ToDictionary(k => k.Key, k => (object)k.Value));
                report["naturalChecked"] = natural; report["naturalCreatedChecked"] = naturalCreated;
                // Parties spawned through the installed factories at installed spawn points, then checked.
                string world = registration.WorldId;
                var town = Settlement.Find(world + "_town_1"); var village = Settlement.Find(world + "_village_1");
                var forced = new Dictionary<string, object>();
                bool fixtures = registration.StartupEnabled;   // the forced spawns use the startup fixtures (bandit clans, hideout, plan)
                Action<string, Func<MobileParty>> spawn = (kind, create) => {
                    try { var p = create(); if (p == null) { forced[kind] = "not created"; return; } checks.Check(p, "created"); forced[kind] = new Dictionary<string, object> { ["party"] = p.StringId, ["kind"] = GeneratedSpawnAssertions.KindOf(p), ["position"] = new[] { p.Position.ToVec2().x, p.Position.ToVec2().y }, ["problem"] = GeneratedSpawnAssertions.Problem(p) }; }
                    catch (Exception e) { forced[kind] = "failed: " + e.Message; } };
                if (fixtures)
                {
                    var merchant = town.Notables.FirstOrDefault(h => h.IsMerchant) ?? town.Notables.FirstOrDefault();
                    spawn("caravan", () => CaravanPartyComponent.CreateCaravanParty(merchant, town, merchant.Culture.CaravanPartyTemplates.First()));
                    spawn("villager", () => VillagerPartyComponent.CreateVillagerParty("mnbl_audit_villagers_" + world, village.Village));
                    // A fresh lord: a new hero of the first ruler's clan from the world's lord template, with a party spawned at its town.
                    var ruler = Hero.Find(world + "_ruler_1");
                    var lordTemplate = registration.Dataset.Prototypes.Select(p => p.Reference).Where(r => r.StartsWith("NPCCharacter.", StringComparison.Ordinal))
                        .Select(r => MBObjectManager.Instance.GetObject<CharacterObject>(r.Substring("NPCCharacter.".Length))).FirstOrDefault(c => c != null);
                    spawn("lord", () => {
                        var lord = HeroCreator.CreateSpecialHero(lordTemplate, town, ruler.Clan, null, 30);
                        lord.ChangeState(Hero.CharacterStates.Active);
                        var party = Helpers.MobilePartyHelper.SpawnLordParty(lord, town);
                        if (party != null && party.LeaderHero != lord) throw new InvalidOperationException("the spawned lord party is not led by the new lord");
                        return party; });
                    var clan = Clan.All.FirstOrDefault(c => c.StringId == "forest_bandits"); var hideout = Settlement.Find(world + "_hideout_1");
                    spawn("bandit", () => {
                        var point = Helpers.NavigationHelper.FindReachablePointAroundPosition(hideout.GatePosition, MobileParty.NavigationType.Default, 4f, 1f, false);
                        return BanditPartyComponent.CreateBanditParty("mnbl_audit_bandits_" + world, clan, hideout.Hideout, false, clan.DefaultPartyTemplate, point); });
                }
                report["forcedSpawns"] = forced;
                report["checked"] = new Dictionary<string, object>(checks.Checked.ToDictionary(k => k.Key, k => (object)k.Value));
                report["createdChecked"] = new Dictionary<string, object>(checks.CreatedChecked.ToDictionary(k => k.Key, k => (object)k.Value));
                report["violations"] = checks.Violations; report["violationCount"] = checks.ViolationCount; report["samples"] = checks.Samples;
                check(checks.ViolationCount == 0, "parties off valid navigation: " + String.Join("; ", checks.Violations.Take(5)));
                // Each kind is checked naturally or through its forced spawn; the summary keeps the two apart.
                foreach (var kind in fixtures ? new[] { "player", "caravan", "villager", "lord", "bandit" } : new[] { "player", "lord" })
                {
                    object n, c;
                    bool seen = (natural.TryGetValue(kind, out n) && (int)n > 0) || (naturalCreated.TryGetValue(kind, out c) && (int)c > 0);
                    bool made = forced.ContainsKey(kind) && forced[kind] is Dictionary<string, object> && ((Dictionary<string, object>)forced[kind])["problem"] == null && (string)((Dictionary<string, object>)forced[kind])["kind"] == kind;
                    check(seen || made, "no " + kind + " party was checked, natural or forced");
                }
                check(forced.Values.All(v => v is Dictionary<string, object> && ((Dictionary<string, object>)v)["problem"] == null), "a party spawned through an installed factory is off valid navigation or failed: " + String.Join("; ", forced.Select(f => f.Key + " " + (f.Value is string ? f.Value : ((Dictionary<string, object>)f.Value)["problem"]))));
                check(forced.All(f => !(f.Value is Dictionary<string, object>) || (string)((Dictionary<string, object>)f.Value)["kind"] == f.Key), "a forced spawn is not the kind it was spawned as");
                var storyline = StorylineActive();
                report["storylineActive"] = storyline;
                check(storyline != true, "the War Sails storyline became active");
                if (boot.Startup != null)
                {
                    var alive = boot.Startup.Map.Count(h => h.Value.IsAlive);
                    report["planHeroesAlive"] = alive;
                    check(alive > 0, "no planned hero is alive after the gameplay window");
                }
            }
            catch (Exception error) { report["error"] = error.ToString(); failures.Add("startup audit after gameplay: " + error.Message); }
            return report;
        }

        // In a later world: the earlier campaign's teardown record, a new event instance, and no static of the earlier world.
        public static Dictionary<string, object> Teardown(GeneratedCampaignRegistration registration, List<string> failures)
        {
            var report = new Dictionary<string, object>();
            Action<bool, string> check = (ok, label) => { if (!ok) failures.Add("startup audit teardown: " + label); };
            try
            {
                var record = GeneratedGameManager.LastTeardown;
                check(record != null, "the earlier campaign recorded no teardown");
                if (record == null) return report;
                var events = CampaignListeners.EventsOf(Campaign.Current);
                var earlierEvents = record.Events == null ? null : record.Events.Target;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var earlier = record.Behaviors.Select(w => w.Target).Where(o => o != null).ToList();
                var stale = CampaignListeners.Of(events).Keys.Select(k => k.Value).Where(o => earlier.Contains(o)).Select(o => o.GetType().Name).Distinct().ToList();
                report["earlierWorld"] = record.WorldId; report["error"] = record.Error;
                report["ownBehaviors"] = record.OwnBehaviors; report["ownListenersBefore"] = record.OwnListenersBefore; report["ownListenersLeft"] = record.OwnListenersLeft;
                report["patchesLeftAtEnd"] = record.PatchesLeft; report["startupActiveLeft"] = record.StartupActiveLeft;
                report["startupAppliedLeft"] = record.StartupAppliedLeft; report["worldAppliedLeft"] = record.WorldAppliedLeft;
                report["earlierBehaviors"] = record.Behaviors.Count; report["earlierBehaviorsAliveAfterGC"] = earlier.Count;
                report["sameCampaignEvents"] = earlierEvents != null && ReferenceEquals(earlierEvents, events);
                report["staleListeners"] = stale;
                report["periodicWorld"] = PeriodicWorld.Current == null ? null : PeriodicWorld.Current.Width + "@" + PeriodicWorld.Current.OriginX;
                report["fixturePrefabs"] = GeneratedMapScene.FixturePrefabs.Count;
                check(record.Error == null && record.WorldId != registration.WorldId, "the earlier campaign's teardown failed or is not the earlier world's: " + record.Error);
                check(record.OwnBehaviors > 0 && record.OwnListenersBefore > 0 && record.OwnListenersLeft == 0, "this module's behaviours still listened to the ending campaign's events (" + record.OwnListenersLeft + " of " + record.OwnListenersBefore + ")");
                check(record.PatchesLeft.Count == 0 && !record.StartupActiveLeft && !record.StartupAppliedLeft && !record.WorldAppliedLeft, "a patch or the active startup outlived the earlier campaign: " + String.Join(", ", record.PatchesLeft));
                check(events != null && !ReferenceEquals(earlierEvents, events), "the new campaign reuses the earlier campaign's events");
                check(stale.Count == 0, "behaviours of the earlier campaign listen to the new campaign: " + String.Join(", ", stale));
                check(ReferenceEquals(PeriodicWorld.Current, registration.Periodic), "the periodic world is not this world's");
                check(registration.FixturesEnabled || GeneratedMapScene.FixturePrefabs.Count == 0, "the earlier world's fixture prefabs survived");
            }
            catch (Exception error) { report["error"] = error.ToString(); failures.Add("startup audit teardown: " + error.Message); }
            return report;
        }

        private static bool Resolves(string key)
        {
            string kind = key.Split(':')[0], id = key.Substring(key.IndexOf(':') + 1);
            switch (kind)
            {
                case "settlement": return Settlement.Find(id) != null;
                case "hero": return Hero.FindFirst(h => h.StringId == id) != null || MBObjectManager.Instance.GetObject<CharacterObject>(id) != null && MBObjectManager.Instance.GetObject<CharacterObject>(id).IsHero;
                case "clan": return Clan.All.Any(c => c.StringId == id);
                case "kingdom": return Kingdom.All.Any(k => k.StringId == id);
                default: return false;
            }
        }

        private static bool TemplateResolves(string typed)
        {
            string type = typed.Split(':')[0], id = typed.Substring(typed.IndexOf(':') + 1);
            Type t = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name.StartsWith("TaleWorlds", StringComparison.Ordinal) || a.GetName().Name == "NavalDLC"))
            {
                try { t = assembly.GetTypes().FirstOrDefault(x => x.Name == type && typeof(MBObjectBase).IsAssignableFrom(x)); } catch { }
                if (t != null) break;
            }
            if (t == null) return false;
            var get = typeof(MBObjectManager).GetMethods().First(m => m.Name == "GetObject" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
            return get.MakeGenericMethod(t).Invoke(MBObjectManager.Instance, new object[] { id }) != null;
        }

        private static bool? StorylineActive()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "NavalDLC").Select(a => a.GetType("NavalDLC.Storyline.CampaignBehaviors.NavalStorylineCampaignBehavior")).FirstOrDefault(t => t != null);
            if (type == null) return null;
            var behavior = typeof(Campaign).GetMethod("GetCampaignBehavior").MakeGenericMethod(type).Invoke(Campaign.Current, null);
            if (behavior == null) return null;
            return (bool)type.GetMethod("IsNavalStorylineActive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(behavior, null);
        }

        // The one-to-one conversation partner the wanderer-introduction check stands in for (ConversationManager reads only Character).
        private sealed class ProbeAgent : IAgent
        {
            private readonly BasicCharacterObject character;
            public ProbeAgent(BasicCharacterObject character) { this.character = character; }
            public BasicCharacterObject Character { get { return character; } }
            public bool IsEnemyOf(IAgent agent) { return false; }
            public bool IsFriendOf(IAgent agent) { return true; }
            public AgentState State { get { return AgentState.Active; } }
            public IMissionTeam Team { get { return null; } }
            public IAgentOriginBase Origin { get { return null; } }
            public float Age { get { return 30f; } }
            public bool IsActive() { return true; }
            public void SetAsConversationAgent(bool set) { }
            public void OnConversationStarted() { }
        }
    }
}
