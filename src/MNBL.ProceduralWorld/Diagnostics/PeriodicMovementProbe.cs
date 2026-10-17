using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MNBL.ProceduralWorld.Automation;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.ObjectSystem;

namespace MNBL.ProceduralWorld.Diagnostics
{
    // MMR-65: route execution and spatial queries across the periodic seam (docs/periodic-movement.md), on the MMR-63 fixture after
    // the wrap probe's own crossings. Each section is fail-soft (its error is recorded and the next one runs):
    //   patches       the audited spatial consumers are patched with their exact counts (PeriodicSpatialQueries);
    //   radiusQuery   the periodic radius query itself: parties either side of the seam found from canonical centres either side of it
    //                 and from a centre in a strip, each once, none from the middle; the direct settlement and party grid wrappers; a
    //                 transpiled consumer (HeroHelper.GetClosestSettlement);
    //   reinforcement the player's encounter joiners (StartBattle's join pass, DefaultEncounterModel over the locator search): a party of
    //                 the enemy's faction across the seam within the joining radius joins the battle, one outside does not;
    //   pursuit       an AI party engaging an AI target that moves across the seam (moving-target pursuit and interception), then the
    //                 direct track grid wrapper across the seam;
    //   army, fleet   an AI army on land and one at sea crossing the seam with attached members (they steer by leader target - own
    //                 position, now periodic), members staying beside the leader, no transition at the cut;
    //   cancellation  the player's route cancelled just before and just after the seam and reversed;
    //   ports         an AI ship entering a port across the seam from either side;
    //   circumnavigation an AI ship sailing four legs of a quarter turn east: two crossings, all in one direction, each leg's route cost
    //                 the short way, and the state after the crossings pricing a route as a fresh state does.
    // Every moving section checks face, domain and canonical state (Face), and no map-width step, periodic or planar (WorldWrapProbe.Travel).
    // Synthetic setup, all recorded: the fixture has one ruler per kingdom, so a section that needs more lords creates them in the ruler's
    // clan (Lords); every party a section uses carries grain (Provision); wars are declared and third parties held where a section needs it,
    // and every section restores what it changed in finally.
    internal static class PeriodicMovementProbe
    {
        private static GeneratedCampaignRegistration registration;
        private static readonly List<string> created = new List<string>();
        private static readonly Dictionary<string, object> provisioned = new Dictionary<string, object>();

        // Campaign time runs for days in a section (the circumnavigation about 16): every party a section uses carries grain, so none starves,
        // loses troops or disbands its army for food before the section ends (synthetic, recorded in the report).
        private static void Provision(MobileParty p)
        {
            if (p == null || !p.IsActive || p.Food >= 100) return;
            p.ItemRoster.AddToCounts(DefaultItems.Grain, 300);
            object before;
            provisioned[p.StringId] = (provisioned.TryGetValue(p.StringId, out before) ? (int)before : 0) + 300;
        }

        internal static IEnumerator Run(ProbeContext context, DiagnosticSandboxBootstrap boot, GeneratedCampaignRegistration world0, Dictionary<string, object> report)
        {
            registration = world0; created.Clear(); provisioned.Clear();
            string worldId = world0.WorldId;
            var failures = new List<string>(); report["failures"] = failures;
            // Every exception thrown while the sections run (any thread: party ticks run in parallel) goes to the progress log at once, so a crash
            // that ends the process still names its cause; the first ones are kept in the report.
            // A first-chance trace holds only the throwing frame, so the line carries the current stack with IL offsets (the build has no line
            // numbers; a frame the JIT inlined shows as its caller).
            var thrown = new List<string>(); report["exceptionsThrown"] = thrown;
            int logged = 0;
            EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> onThrown = (sender, e) =>
            {
                if (System.Threading.Interlocked.Increment(ref logged) > 200) return;
                string line;
                try
                {
                    line = e.Exception.GetType().Name + ": " + e.Exception.Message.Replace("\r", "").Replace("\n", " ") + " | thread " + System.Threading.Thread.CurrentThread.ManagedThreadId
                        + " | " + Frames(new System.Diagnostics.StackTrace(1, false));
                }
                catch (Exception) { return; }
                lock (thrown) { if (thrown.Count < 50) thrown.Add(line); }
                try { context.Stage("movement_exception:" + (line.Length > 3000 ? line.Substring(0, 3000) : line)); } catch (Exception) { }
            };
            UnhandledExceptionEventHandler onUnhandled = (sender, e) => { try { context.Stage("movement_unhandled:" + e.ExceptionObject); } catch (Exception) { } };
            AppDomain.CurrentDomain.FirstChanceException += onThrown;
            AppDomain.CurrentDomain.UnhandledException += onUnhandled;
            try
            {
                var inner = Sections(context, boot, worldId, report, failures);
                while (inner.MoveNext()) yield return inner.Current;
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= onThrown;
                AppDomain.CurrentDomain.UnhandledException -= onUnhandled;
            }
        }

        private static string Frames(System.Diagnostics.StackTrace trace)
        {
            var frames = trace.GetFrames() ?? new System.Diagnostics.StackFrame[0];
            return String.Join(" < ", frames.Take(40).Select(f =>
            {
                var m = f.GetMethod();
                return (m == null ? "?" : (m.DeclaringType == null ? "" : m.DeclaringType.FullName + "::") + m.Name) + "+IL_" + f.GetILOffset().ToString("x4");
            }));
        }

        private static IEnumerator Sections(ProbeContext context, DiagnosticSandboxBootstrap boot, string worldId, Dictionary<string, object> report, List<string> failures)
        {
            var world = PeriodicWorld.Current;
            var town1 = Settlement.Find(worldId + "_town_1"); var town2 = Settlement.Find(worldId + "_town_2");
            if (world == null || town1 == null || town2 == null) { failures.Add("No periodic world or seam towns."); yield break; }
            report["towns"] = new Dictionary<string, object> { ["east"] = Point(town1.GatePosition), ["west"] = Point(town2.GatePosition),
                ["eastPort"] = Point(town1.PortPosition), ["westPort"] = Point(town2.PortPosition) };
            Provision(MobileParty.MainParty);
            Section(report, failures, "patches", r => Patches(r));
            Section(report, failures, "radiusQuery", r => RadiusQuery(r, world, town1, town2));
            // movementSections (comma separated) runs a subset, for isolating a section; evidence runs use all.
            var only = context.Str("movementSections", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
            report["sectionsRun"] = only.Count == 0 ? "all" : String.Join(",", only);
            foreach (var name in new[] { "reinforcement", "pursuit", "army", "fleet", "cancellation", "ports", "circumnavigation" })
            {
                if (only.Count > 0 && !only.Contains(name)) continue;
                context.Stage("movement_section:" + name);
                var row = new Dictionary<string, object>(); report[name] = row;
                var rowFailures = new List<string>(); row["failures"] = rowFailures;
                IEnumerator steps = name == "pursuit" ? Pursuit(context, boot, world, town1, row)
                    : name == "reinforcement" ? Reinforcement(context, boot, world, town1, row)
                    : name == "army" ? ArmyCrossing(context, boot, world, town1, town2, false, row)
                    : name == "fleet" ? ArmyCrossing(context, boot, world, town1, town2, true, row)
                    : name == "cancellation" ? Cancellation(context, boot, world, town1, town2, row)
                    : name == "ports" ? Ports(context, boot, world, town1, town2, row)
                    : Circumnavigation(context, boot, world, town1, row);
                Exception error = null;
                while (true)
                {
                    try { if (!steps.MoveNext()) break; } catch (Exception e) { error = e; break; }
                    yield return steps.Current;
                }
                if (error != null) { row["error"] = error.ToString(); failures.Add(name + ": " + error.Message); }
                failures.AddRange(rowFailures.Select(f => name + ": " + f));
                try { RuntimeMissionProbe.Pause(); } catch (Exception) { }
                context.Stage("movement_section_end:" + name + ":" + rowFailures.Count + (error == null ? "" : ":error"));
                context.Checkpoint();
            }
            report["createdLords"] = created.ToList();
            report["provisionedGrain"] = new Dictionary<string, object>(provisioned);
            report["counters"] = new Dictionary<string, object> { ["gridImagePasses"] = PeriodicSpatialQueries.GridImagePasses, ["locatorImagePasses"] = PeriodicPatches.ImagePasses, ["rebases"] = PeriodicSeam.Rebases,
                ["canonicalPlacements"] = PeriodicPatches.CanonicalPlacements };
            report["nonCanonicalPartiesAtEnd"] = MobileParty.All.Where(p => p.IsActive && p.AttachedTo == null && !world.IsCanonical(p.Position.X)).Select(p => p.StringId).ToList();
            if (((List<string>)report["nonCanonicalPartiesAtEnd"]).Count > 0) failures.Add("Unattached parties left non-canonical at the end.");
        }

        private static void Section(Dictionary<string, object> report, List<string> failures, string name, Func<Dictionary<string, object>, IEnumerable<string>> section)
        {
            var row = new Dictionary<string, object>(); report[name] = row;
            try { failures.AddRange(section(row).Select(p => name + ": " + p)); }
            catch (Exception e) { row["error"] = e.ToString(); failures.Add(name + ": " + e.Message); }
        }

        private static float[] Point(CampaignVec2 p) { return new[] { p.X, p.Y }; }

        // No sample moved more than 5 units, periodically (a jump of about k·W passes that test) or planarly less the rebases' widths.
        private static void Steps(Dictionary<string, object> record, List<string> failures, string label)
        {
            if ((double)record["maxStepPeriodic"] > 5) failures.Add(label + ": a sample moved " + record["maxStepPeriodic"] + " units.");
            if ((float)record["maxPlanarResidual"] > 5) failures.Add(label + ": a sample jumped " + record["maxPlanarResidual"] + " units planarly beyond its rebases.");
        }

        // count free lord parties of the kingdom on land (out of settlements, not in an army or a map event; a party's at-sea state is its own,
        // not its position's, so a sailing lord placed on land would still count as at sea), the kingdom's existing lords first, then new lords of
        // its ruling clan from the world's lord template with parties spawned at the clan's town (as the startup audit does).
        private static List<MobileParty> Lords(Kingdom kingdom, int count)
        {
            var lords = MobileParty.All.Where(p => p.IsActive && p.IsLordParty && p.LeaderHero != null && p.MapFaction == kingdom && p.MapEvent == null && p.Army == null && p.AttachedTo == null
                && !p.IsCurrentlyAtSea && !p.IsTransitionInProgress && p.MemberRoster.TotalHealthyCount > 0).OrderBy(p => p.StringId, StringComparer.Ordinal).Take(count).ToList();
            var clan = kingdom.RulingClan ?? kingdom.Clans.First();
            var town = clan.Settlements.FirstOrDefault(s => s.IsTown) ?? Settlement.All.First(s => s.IsTown);
            var template = registration.Dataset.Prototypes.Select(x => x.Reference).Where(r => r.StartsWith("NPCCharacter.", StringComparison.Ordinal))
                .Select(r => MBObjectManager.Instance.GetObject<CharacterObject>(r.Substring("NPCCharacter.".Length))).FirstOrDefault(c => c != null);
            while (lords.Count < count && template != null)
            {
                var hero = HeroCreator.CreateSpecialHero(template, town, clan, null, 30);
                hero.ChangeState(Hero.CharacterStates.Active);
                var party = Helpers.MobilePartyHelper.SpawnLordParty(hero, town);
                if (party == null) break;
                party.MemberRoster.AddToCounts(hero.Culture.BasicTroop, 20, false, 0, 0, true, -1);
                created.Add(party.StringId);
                lords.Add(party);
            }
            foreach (var p in lords) { if (p.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(p); Provision(p); }
            return lords;
        }

        private static List<Kingdom> Kingdoms() { return Kingdom.All.Where(k => !k.IsEliminated && k.Clans.Count > 0).OrderBy(k => k.StringId, StringComparer.Ordinal).ToList(); }

        // A party's navigation state: a valid face whose domain is the party's (sea faces at sea, land faces on land), canonical unless
        // attached (an attached member follows its leader's frame).
        private static Dictionary<string, object> Face(PeriodicWorld world, MobileParty party, List<string> problems, string label)
        {
            var map = (GeneratedMapScene)Campaign.Current.MapSceneWrapper;
            var face = party.CurrentNavigationFace;
            bool valid = face.IsValid();
            var type = valid ? map.GetFaceTerrainType(face) : TerrainType.Water;
            bool seaFace = type == TerrainType.OpenSea || type == TerrainType.CoastalSea || type == TerrainType.Lake || type == TerrainType.River || type == TerrainType.Water;
            bool domain = valid && seaFace == party.IsCurrentlyAtSea;
            bool canonical = party.AttachedTo != null || world.IsCanonical(party.Position.X);
            if (!valid || !domain || !canonical) problems.Add(label + ": " + party.StringId + (valid ? "" : " has no navigation face") + (valid && !domain ? " is " + (party.IsCurrentlyAtSea ? "at sea" : "on land") + " on a " + type + " face" : "") + (canonical ? "" : " is not canonical"));
            return new Dictionary<string, object> { ["party"] = party.StringId, ["position"] = Point(party.Position), ["face"] = valid ? type.ToString() : null, ["atSea"] = party.IsCurrentlyAtSea,
                ["domainConsistent"] = domain, ["canonical"] = canonical };
        }

        // ---- the audited consumers are patched ----
        private static IEnumerable<string> Patches(Dictionary<string, object> r)
        {
            var problems = new List<string>();
            r["tableEntries"] = PeriodicSpatialQueries.Table.Length; r["patched"] = PeriodicSpatialQueries.Patched; r["recompiledCallers"] = PeriodicSpatialQueries.Recompiled;
            r["callsReplaced"] = PeriodicSpatialQueries.CallsReplaced;
            r["periodicPatchEntries"] = PeriodicPatches.Applied.Count; r["patchError"] = PeriodicPatches.LastError;
            if (!PeriodicPatches.IsApplied || PeriodicSpatialQueries.Patched + PeriodicSpatialQueries.Recompiled != PeriodicSpatialQueries.Table.Length) problems.Add("The audited spatial consumers are not all patched.");
            return problems;
        }

        // ---- the periodic radius query ----
        private static IEnumerable<string> RadiusQuery(Dictionary<string, object> r, PeriodicWorld world, Settlement town1, Settlement town2)
        {
            var problems = new List<string>();
            var main = MobileParty.MainParty;
            // Four still AI parties on a land row through the seam: two east of it, two west of it (synthetic placement).
            float y = WorldWrapProbe.LandRow(town1.Position.Y, world.MaxX - 8, world.MaxX - 3, world.OriginX + 3, world.OriginX + 8, world.MaxX - 1.5f);
            if (float.IsNaN(y)) { problems.Add("No land row through the seam."); return problems; }
            var parties = Kingdoms().SelectMany(k => Lords(k, 2)).Take(4).ToList();
            if (parties.Count < 4) { problems.Add("Fewer than four free lord parties."); return problems; }
            var xs = new[] { world.MaxX - 8, world.MaxX - 3, world.OriginX + 3, world.OriginX + 8 };
            try { RadiusQueries(r, world, town1, town2, parties, xs, y, problems); }
            finally { foreach (var p in parties.Where(p => p.IsActive)) p.Ai.SetDoNotMakeNewDecisions(false); }
            return problems;
        }

        private static void RadiusQueries(Dictionary<string, object> r, PeriodicWorld world, Settlement town1, Settlement town2, List<MobileParty> parties, float[] xs, float y, List<string> problems)
        {
            for (int i = 0; i < 4; i++)
            {
                parties[i].Ai.SetDoNotMakeNewDecisions(true);
                parties[i].Position = new CampaignVec2(new Vec2(xs[i], y), true); parties[i].SetMoveModeHold();
            }
            var placed = parties.Select(p => p.StringId).ToList();
            r["placed"] = parties.Select((p, i) => (object)new Dictionary<string, object> { ["party"] = p.StringId, ["position"] = Point(p.Position) }).ToList();
            var queries = new List<object>(); r["queries"] = queries;
            foreach (var q in new[] { Tuple.Create("canonical centre east of the seam", world.MaxX - 1.5f, 10f, 4),
                                      Tuple.Create("centre in the east strip", world.MaxX + 1.5f, 10f, 4),
                                      Tuple.Create("canonical centre west of the seam", world.OriginX + 1.5f, 10f, 4),
                                      Tuple.Create("centre in the middle", world.OriginX + world.Width / 2, 10f, 0) })
            {
                var centre = new Vec2(q.Item2, y);
                var found = new List<string>();
                var data = MobileParty.StartFindingLocatablesAroundPosition(centre, q.Item3);
                for (var p = MobileParty.FindNextLocatable(ref data); p != null; p = MobileParty.FindNextLocatable(ref data)) found.Add(p.StringId);
                var ours = found.Where(placed.Contains).ToList();
                bool once = ours.Count == ours.Distinct().Count();
                var withinRadius = parties.Where(p => world.Distance(centre, p.Position.ToVec2()) <= q.Item3).Select(p => p.StringId).ToList();
                bool exact = withinRadius.All(ours.Contains) && ours.All(withinRadius.Contains);
                queries.Add(new Dictionary<string, object> { ["query"] = q.Item1, ["centre"] = new[] { centre.X, centre.Y }, ["radius"] = q.Item3, ["found"] = ours, ["foundOnce"] = once,
                    ["withinPeriodicRadius"] = withinRadius, ["matchesPeriodicRadius"] = exact, ["othersFound"] = found.Count - ours.Count });
                if (!once || !exact || ours.Count != q.Item4) problems.Add(q.Item1 + ": found " + ours.Count + " of the placed parties (" + q.Item4 + " expected" + (once ? "" : ", one twice") + ").");
            }
            // The direct LocatorGrid wrapper (PeriodicSpatialQueries.GridNextSettlement) from the east seam finds the west town across it.
            var gridProperty = typeof(Campaign).GetProperty("SettlementLocator", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var grid = gridProperty == null ? null : gridProperty.GetValue(Campaign.Current);
            var start = grid == null ? null : grid.GetType().GetMethod("StartFindingLocatablesAroundPosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (grid == null || start == null) problems.Add("No settlement locator.");
            else
            {
                float radius = world.Distance(new Vec2(world.MaxX - 1.5f, y), town2.Position.ToVec2()) + 2;
                var data = (LocatableSearchData<Settlement>)start.Invoke(grid, new object[] { new Vec2(world.MaxX - 1.5f, y), radius });
                var foundSettlements = new List<string>();
                for (var s = PeriodicSpatialQueries.GridNextSettlement(grid, ref data); s != null; s = PeriodicSpatialQueries.GridNextSettlement(grid, ref data)) foundSettlements.Add(s.StringId);
                r["directGrid"] = new Dictionary<string, object> { ["centre"] = new[] { world.MaxX - 1.5f, y }, ["radius"] = radius, ["found"] = foundSettlements };
                if (!foundSettlements.Contains(town2.StringId)) problems.Add("The direct settlement grid search from the east seam misses the west town across it.");
            }
            // The direct party grid wrapper (PeaceBarterable.Apply searches this way) from the west seam finds the two parties east of it.
            var partyGrid = typeof(Campaign).GetProperty("MobilePartyLocator", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var parties2 = partyGrid == null ? null : partyGrid.GetValue(Campaign.Current);
            if (parties2 == null) problems.Add("No party locator.");
            else
            {
                var centre = new Vec2(world.OriginX + 1.5f, y);
                var data = PeriodicSpatialQueries.GridStart<MobileParty>(parties2, centre, 10f);
                var found = new List<string>();
                for (var p = PeriodicSpatialQueries.GridNextParty(parties2, ref data); p != null; p = PeriodicSpatialQueries.GridNextParty(parties2, ref data)) found.Add(p.StringId);
                var ours = found.Where(placed.Contains).ToList();
                r["directPartyGrid"] = new Dictionary<string, object> { ["centre"] = new[] { centre.X, centre.Y }, ["radius"] = 10f, ["found"] = ours, ["othersFound"] = found.Count - ours.Count };
                if (ours.Count != 4 || ours.Distinct().Count() != 4) problems.Add("The direct party grid search from the west seam found " + ours.Count + " of the four placed parties.");
            }
            // Two transpiled consumers: the closest settlement to a hero, and the AI's current settlement for a party, at the east seam.
            var hero = parties[1].LeaderHero;
            var closest = hero == null ? null : Helpers.HeroHelper.GetClosestSettlement(hero);
            var nearest = Settlement.All.Where(s => s.IsTown || s.IsCastle || s.IsVillage).OrderBy(s => world.Distance(parties[1].Position.ToVec2(), s.GatePosition.ToVec2())).FirstOrDefault();
            r["closestSettlementToHero"] = new Dictionary<string, object> { ["hero"] = hero == null ? null : hero.StringId, ["at"] = Point(parties[1].Position),
                ["answer"] = closest == null ? null : closest.StringId, ["periodicNearest"] = nearest == null ? null : nearest.StringId };
            if (hero != null && (closest == null || nearest == null || Math.Abs(world.Distance(parties[1].Position.ToVec2(), closest.GatePosition.ToVec2()) - world.Distance(parties[1].Position.ToVec2(), nearest.GatePosition.ToVec2())) > 1f))
                problems.Add("HeroHelper.GetClosestSettlement does not answer the periodic nearest settlement at the seam.");
        }

        // ---- moving-target pursuit ----
        private static IEnumerator Pursuit(ProbeContext context, DiagnosticSandboxBootstrap boot, PeriodicWorld world, Settlement town1, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"];
            float y = WorldWrapProbe.LandRow(town1.Position.Y, world.MaxX - 25, world.MaxX - 3, world.MaxX - 1, world.OriginX + 1, world.OriginX + 30, world.OriginX + 60);
            if (float.IsNaN(y)) { failures.Add("No land row through the seam."); yield break; }
            // Two lords of kingdoms at war (war declared when needed; synthetic).
            var kingdoms = Kingdoms();
            var target = kingdoms.Count < 2 ? null : Lords(kingdoms[1], 1).FirstOrDefault();
            var pursuer = kingdoms.Count < 2 ? null : Lords(kingdoms[0], 1).FirstOrDefault();
            if (target == null || pursuer == null) { failures.Add("No two lords of different kingdoms."); yield break; }
            // The factions are kept: the loser of the interception may leave its party without a leader (and a faction) afterwards.
            var pursuerFaction = pursuer.MapFaction; var targetFaction = target.MapFaction;
            bool declared = false;
            if (!pursuerFaction.IsAtWarWith(targetFaction)) { DeclareWarAction.ApplyByDefault(pursuerFaction, targetFaction); declared = true; }
            // Every other lord of the two kingdoms is held far from the seam, so only the pursuer can intercept the target (synthetic).
            var held = MobileParty.All.Where(p => p.IsActive && p.IsLordParty && p != pursuer && p != target && p.MapEvent == null && p.Army == null && p.AttachedTo == null
                && (p.MapFaction == pursuerFaction || p.MapFaction == targetFaction)).ToList();
            var heldAt = held.ToDictionary(p => p, p => p.Position);
            float farY = WorldWrapProbe.LandRow(y, world.OriginX + world.Width / 2);
            try
            {
            foreach (var p in held)
            {
                if (p.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(p);
                p.Ai.SetDoNotMakeNewDecisions(true);
                if (!float.IsNaN(farY) && !p.IsCurrentlyAtSea) p.Position = new CampaignVec2(new Vec2(world.OriginX + world.Width / 2, farY), true);   // a ship cannot engage a land party
                p.SetMoveModeHold();
            }
            // The target is slowed (extra troops) so the faster pursuer catches it across the seam.
            int extra = Math.Max(0, 150 - target.MemberRoster.TotalManCount);
            if (extra > 0) target.MemberRoster.AddToCounts(target.LeaderHero.Culture.BasicTroop, extra, false, 0, 0, true, -1);
            target.Ai.SetDoNotMakeNewDecisions(true); pursuer.Ai.SetDoNotMakeNewDecisions(true);
            target.Position = new CampaignVec2(new Vec2(world.MaxX - 3, y), true);
            pursuer.Position = new CampaignVec2(new Vec2(world.MaxX - 25, y), true);
            var goal = new CampaignVec2(new Vec2(world.OriginX + 60, y), true);
            target.SetMoveGoToPoint(goal, MobileParty.NavigationType.Default);
            pursuer.SetMoveEngageParty(target, MobileParty.NavigationType.Default);
            row["setup"] = new Dictionary<string, object> { ["target"] = target.StringId, ["pursuer"] = pursuer.StringId, ["targetStart"] = Point(target.Position), ["pursuerStart"] = Point(pursuer.Position),
                ["targetGoal"] = Point(goal), ["warDeclared"] = declared, ["targetExtraTroops"] = extra, ["targetSpeed"] = target.Speed, ["pursuerSpeed"] = pursuer.Speed };
            var record = new Dictionary<string, object> { ["leg"] = "AI pursuit across the seam", ["party"] = pursuer.StringId }; row["pursuerTravel"] = record;
            int targetRebasesBefore = PeriodicSeam.Events.Count(e => e.Party == target.StringId);
            var distances = new List<float>();
            Dictionary<string, object> route = null;
            var steps = WorldWrapProbe.Travel(context, boot, world, pursuer, () => { distances.Add(world.Distance(pursuer.Position.ToVec2(), target.Position.ToVec2())); route = route ?? SeamRoute(world, pursuer); return target.MapEvent != null || pursuer.MapEvent != null; }, 120, record);
            while (steps.MoveNext()) yield return steps.Current;
            var battle = pursuer.MapEvent != null && pursuer.MapEvent == target.MapEvent ? pursuer.MapEvent : null;
            row["intercepted"] = battle != null; row["routeAtSeam"] = route; row["heldElsewhere"] = held.Select(p => p.StringId).ToList();
            if (battle == null && (target.MapEvent != null || pursuer.MapEvent != null))
                row["otherBattle"] = (target.MapEvent ?? pursuer.MapEvent).InvolvedParties.Select(p => p.MobileParty == null ? "settlement" : p.MobileParty.StringId).ToList();
            row["targetCrossed"] = PeriodicSeam.Events.Count(e => e.Party == target.StringId) > targetRebasesBefore;
            row["interception"] = battle == null ? null : new Dictionary<string, object> { ["position"] = Point(battle.Position), ["canonical"] = world.IsCanonical(battle.Position.X),
                ["parties"] = battle.InvolvedParties.Select(p => p.MobileParty == null ? "settlement" : p.MobileParty.StringId).ToList(),
                ["logicalX"] = world.LogicalX(battle.Position.X) };
            row["distanceSamples"] = distances.Where((d, i) => i % 10 == 0).Take(80).ToList();
            row["finalDistance"] = world.Distance(pursuer.Position.ToVec2(), target.Position.ToVec2());
            if (!(bool)record["arrived"] || battle == null) failures.Add("The pursuer did not intercept the target within 120 s (periodic distance " + row["finalDistance"] + ").");
            if (!(bool)row["targetCrossed"] && (int)record["rebases"] < 1) failures.Add("Neither party crossed the seam during the pursuit.");
            Steps(record, failures, "the pursuit");
            if ((float)record["traveledPeriodic"] > world.Width / 2) failures.Add("The pursuer travelled " + record["traveledPeriodic"] + " units (the long way).");
            if (battle != null && !world.IsCanonical(battle.Position.X)) failures.Add("The interception battle is not at a canonical position.");
            // Let the battle resolve (simulated), then restore.
            double begin = context.Seconds;
            boot.HoldPaused = false; DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppableFastForward);
            while (context.Seconds - begin < 30 && ((pursuer.IsActive && pursuer.MapEvent != null) || (target.IsActive && target.MapEvent != null))) { boot.Tick(); yield return null; }
            DiagnosticSandboxBootstrap.Pause(Campaign.Current); boot.HoldPaused = true;
            var state = new List<object>(); row["after"] = state;
            foreach (var p in new[] { pursuer, target }.Where(p => p.IsActive && p.MapEvent == null)) state.Add(Face(world, p, failures, "after the pursuit"));
            // The direct track grid wrapper (MapTracksCampaignBehavior.QuarterHourlyTick searches this way) from the east side of the seam,
            // against the periodic radius over every track; one track is laid on each side of the seam first (tracks the parties drop are
            // random), with the target's identity.
            row["trackGrid"] = TrackGrid(world, new Vec2(world.MaxX - 1, y), 15f, new[] { target, pursuer }.FirstOrDefault(p => p.IsActive) ?? MobileParty.MainParty, failures);
            }
            finally
            {
                foreach (var p in new[] { pursuer, target }.Concat(held).Where(p => p.IsActive)) p.Ai.SetDoNotMakeNewDecisions(false);
                foreach (var p in held.Where(p => p.IsActive && p.MapEvent == null && p.CurrentSettlement == null && !p.IsCurrentlyAtSea)) p.Position = heldAt[p];
                if (declared && pursuerFaction.IsAtWarWith(targetFaction)) MakePeaceAction.Apply(pursuerFaction, targetFaction);
            }
        }

        private static Dictionary<string, object> TrackGrid(PeriodicWorld world, Vec2 centre, float radius, MobileParty owner, List<string> failures)
        {
            var type = typeof(MapTracksCampaignBehavior);
            var behavior = Campaign.Current.GetCampaignBehavior<MapTracksCampaignBehavior>();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var gridField = type.GetField("_trackLocator", flags); var allField = type.GetField("_allTracks", flags);
            var grid = behavior == null || gridField == null ? null : gridField.GetValue(behavior);
            var all = behavior == null || allField == null ? null : allField.GetValue(behavior) as List<Track>;
            if (grid == null || all == null) { failures.Add("No track locator."); return null; }
            var laid = new[] { new Vec2(world.MaxX - 4, centre.Y), new Vec2(world.OriginX + 4, centre.Y) };
            foreach (var at in laid) behavior.AddTrack(owner, new CampaignVec2(at, true), new Vec2(-1, 0));
            var data = PeriodicSpatialQueries.GridStart<Track>(grid, centre, radius);
            var found = new List<Track>();
            for (var t = PeriodicSpatialQueries.GridNextTrack(grid, ref data); t != null; t = PeriodicSpatialQueries.GridNextTrack(grid, ref data)) found.Add(t);
            // Tracks within 0.01 of the radius may fall either way (the grid compares planar squares in single precision).
            var within = all.Where(t => world.Distance(centre, t.GetPosition2D) <= radius - 0.01f).ToList();
            var edge = all.Where(t => Math.Abs(world.Distance(centre, t.GetPosition2D) - radius) <= 0.01f).ToList();
            int missed = within.Count(t => !found.Contains(t)), extra = found.Count(t => !within.Contains(t) && !edge.Contains(t));
            bool once = found.Count == found.Distinct().Count();
            int across = found.Count(t => world.IsCanonical(t.GetPosition2D.X) && t.GetPosition2D.X < world.OriginX + world.Width / 2);
            if (!once || missed > 0 || extra > 0)
                failures.Add("The direct track grid search at the seam differs from the periodic radius (" + found.Count + " found, " + within.Count + " within it, " + missed + " missed, " + extra + " extra" + (once ? "" : ", one twice") + ").");
            if (across == 0) failures.Add("No track across the seam was found by the direct track grid search (" + within.Count + " within the periodic radius).");
            return new Dictionary<string, object> { ["centre"] = new[] { centre.X, centre.Y }, ["radius"] = radius, ["laid"] = laid.Select(v => new[] { v.X, v.Y }).ToList(), ["tracksInWorld"] = all.Count, ["found"] = found.Count,
                ["withinPeriodicRadius"] = within.Count, ["foundAcrossSeam"] = across, ["foundOnce"] = once, ["missed"] = missed, ["extra"] = extra };
        }

        // ---- battle reinforcements (the player's encounter joiners) ----
        private static IEnumerator Reinforcement(ProbeContext context, DiagnosticSandboxBootstrap boot, PeriodicWorld world, Settlement town1, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"];
            var main = MobileParty.MainParty;
            if (main.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(main);
            // The join pass takes only candidates whose at-sea state is the player's: the player is brought fully ashore first.
            var ashore = Ashore(context, boot, main, town1.GatePosition);
            while (ashore.MoveNext()) yield return ashore.Current;
            if (main.IsCurrentlyAtSea || main.IsTransitionInProgress) { failures.Add("The player could not be brought ashore."); yield break; }
            Hero.MainHero.HitPoints = Hero.MainHero.MaxHitPoints;
            float radius = Campaign.Current.Models.EncounterModel.GetEncounterJoiningRadius;
            float y = WorldWrapProbe.LandRow(town1.Position.Y, world.MaxX - 0.6f, world.MaxX - 1.2f, world.OriginX + 0.4f, world.MaxX - (radius + 6));
            if (float.IsNaN(y)) { failures.Add("No land row through the seam."); yield break; }
            var kingdom = Kingdoms().FirstOrDefault();
            var members = kingdom == null ? new List<MobileParty>() : Lords(kingdom, 3);
            if (members.Count < 3) { failures.Add("No kingdom with three free lord parties."); yield break; }
            bool declared = false;
            if (!kingdom.IsAtWarWith(main.MapFaction)) { DeclareWarAction.ApplyByDefault(Clan.PlayerClan, kingdom); declared = true; }
            var enemy = members[0]; var across = members[1]; var outside = members[2];
            try
            {
            foreach (var p in members) p.Ai.SetDoNotMakeNewDecisions(true);
            main.Position = new CampaignVec2(new Vec2(world.MaxX - 0.6f, y), true); main.SetMoveModeHold();
            enemy.Position = new CampaignVec2(new Vec2(world.MaxX - 1.2f, y), true); enemy.SetMoveModeHold();
            across.Position = new CampaignVec2(new Vec2(world.OriginX + 0.4f, y), true); across.SetMoveModeHold();
            outside.Position = new CampaignVec2(new Vec2(world.MaxX - (radius + 6), y), true); outside.SetMoveModeHold();
            PlayerEncounter.RestartPlayerEncounter(enemy.Party, main.Party, true, false);
            // StartBattle runs the player's join pass itself (PlayerEncounter.StartBattleInternal -> CheckNearbyPartiesToJoinPlayerMapEvent; the
            // DefaultEncounterModel's candidates come from the locator search around the battle): the joiners are in the map event when it returns.
            var battle = PlayerEncounter.StartBattle();
            if (battle == null) { failures.Add("StartBattle returned no map event."); yield break; }
            row["setup"] = new Dictionary<string, object> { ["joiningRadius"] = radius, ["battle"] = Point(battle.Position), ["enemy"] = enemy.StringId,
                ["acrossSeam"] = new Dictionary<string, object> { ["party"] = across.StringId, ["position"] = Point(across.Position), ["periodicDistance"] = world.Distance(battle.Position.ToVec2(), across.Position.ToVec2()),
                    ["planarDistance"] = battle.Position.ToVec2().Distance(across.Position.ToVec2()) },
                ["outside"] = new Dictionary<string, object> { ["party"] = outside.StringId, ["periodicDistance"] = world.Distance(battle.Position.ToVec2(), outside.Position.ToVec2()) },
                ["warDeclared"] = declared, ["synthetic"] = "war declared on the kingdom; three of its lord parties placed: the enemy beside the player, one across the seam within the joining radius, one outside it" };
            row["involved"] = battle.InvolvedParties.Select(p => (object)new Dictionary<string, object> { ["party"] = p.MobileParty == null ? "settlement" : p.MobileParty.StringId, ["side"] = p.Side.ToString() }).ToList();
            // Diagnostics: what the locator search around the battle returns (the join pass's candidates).
            var near = new List<string>();
            var search = MobileParty.StartFindingLocatablesAroundPosition(battle.Position.ToVec2(), radius);
            for (var p = MobileParty.FindNextLocatable(ref search); p != null; p = MobileParty.FindNextLocatable(ref search)) near.Add(p.StringId);
            row["locatorAroundBattle"] = near;
            bool acrossJoined = across.MapEvent == battle && across.Party.Side == enemy.Party.Side;
            row["acrossJoinedEnemySide"] = acrossJoined; row["outsideInABattle"] = outside.MapEvent != null;
            if (!acrossJoined)
            {
                failures.Add("The party across the seam within the joining radius did not join the battle on the enemy's side.");
                row["acrossState"] = new Dictionary<string, object> { ["atSea"] = across.IsCurrentlyAtSea, ["playerAtSea"] = main.IsCurrentlyAtSea, ["shouldJoinPlayerBattles"] = across.ShouldJoinPlayerBattles,
                    ["disorganized"] = across.IsDisorganized, ["inSettlement"] = across.CurrentSettlement != null, ["attachedTo"] = across.AttachedTo == null ? null : across.AttachedTo.StringId,
                    ["mapEvent"] = across.MapEvent != null, ["atWarWithPlayer"] = across.MapFaction.IsAtWarWith(main.MapFaction), ["canJoinEnemySide"] = battle.CanPartyJoinBattle(across.Party, enemy.Party.Side) };
            }
            if (outside.MapEvent != null) failures.Add("The party outside the joining radius joined a battle.");
            }
            finally { Clean(main, members, kingdom, declared); }
            double begin = context.Seconds;
            while ((PlayerEncounter.Current != null || main.MapEvent != null) && context.Seconds - begin < 10) yield return null;
            row["afterCleanup"] = new Dictionary<string, object> { ["encounter"] = PlayerEncounter.Current != null, ["playerMapEvent"] = main.MapEvent != null };
        }

        // Disembarks a party at a point and runs the campaign until the transition has finished (at most 15 s).
        private static IEnumerator Ashore(ProbeContext context, DiagnosticSandboxBootstrap boot, MobileParty party, CampaignVec2 at)
        {
            if (!party.IsCurrentlyAtSea && !party.IsTransitionInProgress) yield break;
            if (!party.IsTransitionInProgress) party.DisembarkToPosition(at);
            double begin = context.Seconds;
            boot.HoldPaused = false; DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppablePlay);
            while (context.Seconds - begin < 15 && (party.IsCurrentlyAtSea || party.IsTransitionInProgress)) { boot.Tick(); yield return null; }
            DiagnosticSandboxBootstrap.Pause(Campaign.Current); boot.HoldPaused = true;
        }

        private static void Clean(MobileParty main, List<MobileParty> members, Kingdom kingdom, bool declared)
        {
            try { if (PlayerEncounter.Current != null) { if (PlayerEncounter.Battle != null) PlayerEncounter.LeaveBattle(); PlayerEncounter.Finish(true); } }
            catch (Exception) { }
            foreach (var p in members.Where(p => p.IsActive)) p.Ai.SetDoNotMakeNewDecisions(false);
            if (declared && kingdom.IsAtWarWith(main.MapFaction)) MakePeaceAction.Apply(Clan.PlayerClan.MapFaction, kingdom);
        }

        // ---- an army (land) or a fleet (sea) crossing the seam with attached members ----
        private static IEnumerator ArmyCrossing(ProbeContext context, DiagnosticSandboxBootstrap boot, PeriodicWorld world, Settlement town1, Settlement town2, bool sea, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"];
            var kingdom = Kingdoms().FirstOrDefault(k => k.Armies.Count == 0);
            var lords = kingdom == null ? new List<MobileParty>() : Lords(kingdom, 3);
            if (lords.Count < 3) { failures.Add("No kingdom with three free lords."); yield break; }
            var leader = lords[0];
            // The land army forms 20 units before the seam and marches to 20 past it on a land row (a short crossing; the fleet sails port to
            // port); without such a row it forms at the east town.
            float landY = sea ? float.NaN : WorldWrapProbe.LandRow(town1.Position.Y, world.MaxX - 20, world.MaxX - 10, world.MaxX - 1, world.OriginX + 1, world.OriginX + 10, world.OriginX + 20);
            var start = sea ? town1.PortPosition : float.IsNaN(landY) ? town1.GatePosition : new CampaignVec2(new Vec2(world.MaxX - 20, landY), true);
            foreach (var p in lords)
            {
                if (p.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(p);
                p.Ai.SetDoNotMakeNewDecisions(true);
                if (sea && p.Ships.Count == 0)
                {
                    var hull = p.LeaderHero.Culture.AvailableShipHulls.FirstOrDefault() ?? Kingdom.All.SelectMany(k => k.Culture.AvailableShipHulls).First();
                    int crew = Math.Max(0, new Ship(hull).SkeletalCrewCapacity - p.MemberRoster.TotalHealthyCount);
                    if (crew > 0) p.MemberRoster.AddToCounts(p.LeaderHero.Culture.BasicTroop, crew, false, 0, 0, true, -1);
                    ChangeShipOwnerAction.ApplyByLooting(p.Party, new Ship(hull));
                }
                if (sea) p.SetSailAtPosition(start); else p.Position = start;
                p.SetMoveModeHold();
            }
            // Army.CheckArmyDispersion disbands an army whose kingdom has no active war: one is declared for the crossing (synthetic).
            var rival = Kingdoms().FirstOrDefault(k => k != kingdom);
            bool declaredWar = false;
            if (rival != null && !kingdom.IsAtWarWith(rival)) { DeclareWarAction.ApplyByDefault(kingdom, rival); declaredWar = true; }
            // The rival's lords are held in the middle of the world, so none intercepts the army at the seam (run 30656: its leader was killed).
            var heldAway = rival == null ? new List<KeyValuePair<MobileParty, CampaignVec2>>() : HoldAway(world, town1.Position.Y, p => p.MapFaction == rival);
            row["rivalLordsHeld"] = heldAway.Select(kv => kv.Key.StringId).ToList();
            var types = Enum.GetValues(typeof(Army.ArmyTypes)).Cast<Army.ArmyTypes>().ToList();
            var type = types.FirstOrDefault(t => t.ToString() == "Patrolling");
            Army army = null;
            var dispersal = new List<string>(); var listener = new object();
            CampaignEvents.ArmyDispersed.AddNonSerializedListener(listener, (dispersed, reason, isPlayers) => { if (dispersed == army) dispersal.Add(reason.ToString()); });
            try
            {
            // The army's objective is the destination. Army.IsWaitingForArmyMembers holds a new army in its gathering phase until the gathering
            // has started (Army.HourlyTick then sends the leader back to the objective's gate): the gathering is marked started now, with
            // every member attached (synthetic).
            kingdom.CreateArmy(leader.LeaderHero, town2, type, new MBReadOnlyList<MobileParty>());
            army = leader.Army;
            if (army == null) { failures.Add("CreateArmy made no army."); yield break; }
            var gathering = typeof(Army).GetField("_armyGatheringStartTime", BindingFlags.Instance | BindingFlags.NonPublic);
            if (gathering == null) { failures.Add("Army._armyGatheringStartTime is missing."); yield break; }
            foreach (var m in lords.Skip(1)) { m.Army = army; army.AddPartyToMergedParties(m); }
            leader.Ai.SetDoNotMakeNewDecisions(true);
            row["setup"] = new Dictionary<string, object> { ["kingdom"] = kingdom.StringId, ["leader"] = leader.StringId, ["members"] = lords.Skip(1).Select(m => m.StringId).ToList(),
                ["attached"] = lords.Skip(1).Select(m => m.AttachedTo == leader).ToList(), ["armyType"] = type.ToString(), ["atSea"] = lords.Select(p => p.IsCurrentlyAtSea).ToList(),
                ["start"] = Point(start), ["synthetic"] = (sea ? "a culture ship and a skeletal crew granted to each lord; the fleet formed at the east seam port" : "the army formed 20 units before the seam on a land row")
                    + "; objective the destination town, gathering marked started with every member attached; war declared on the rival kingdom when needed (an army without an active war disbands)" };
            if (lords.Skip(1).Any(m => m.AttachedTo != leader)) failures.Add("The members are not attached to the leader.");
            gathering.SetValue(army, Campaign.CurrentTime);
            row["waitingForMembers"] = army.IsWaitingForArmyMembers();
            var goal = sea ? town2.PortPosition : float.IsNaN(landY) ? town2.GatePosition : new CampaignVec2(new Vec2(world.OriginX + 20, landY), true);
            leader.SetMoveGoToPoint(goal, sea ? MobileParty.NavigationType.Naval : MobileParty.NavigationType.Default);
            var record = new Dictionary<string, object> { ["leg"] = (sea ? "fleet" : "army") + " east->west across the seam", ["party"] = leader.StringId }; row["leaderTravel"] = record;
            float maxSpread = 0; int detached = 0, transitions = 0; List<string> battle = null; var memberRebases = lords.Skip(1).ToDictionary(m => m.StringId, m => PeriodicSeam.Events.Count(e => e.Party == m.StringId));
            Dictionary<string, object> route = null;
            var steps = WorldWrapProbe.Travel(context, boot, world, leader, () =>
            {
                route = route ?? SeamRoute(world, leader);
                // Crossing the cut is not a domain transition: no member embarks or disembarks on the way.
                if (lords.Any(m => m.IsTransitionInProgress || m.IsCurrentlyAtSea != sea)) transitions++;
                if (battle == null && leader.MapEvent != null) battle = leader.MapEvent.InvolvedParties.Select(p => p.MobileParty == null ? "settlement" : p.MobileParty.StringId).ToList();
                foreach (var m in lords.Skip(1)) { if (m.AttachedTo != leader) detached++; else maxSpread = Math.Max(maxSpread, world.Distance(m.Position.ToVec2(), leader.Position.ToVec2())); }
                return world.Distance(leader.Position.ToVec2(), goal.ToVec2()) < (sea ? 2.5f : 1.5f);
            }, 180, record);
            while (steps.MoveNext()) yield return steps.Current;
            WorldWrapProbe.Judge(record, failures, world, start.ToVec2(), goal.ToVec2());
            row["maxMemberSpread"] = maxSpread; row["detachedSamples"] = detached; row["transitionSamples"] = transitions; row["routeAtSeam"] = route; row["leaderBattle"] = battle;
            if (battle != null) failures.Add("The army was attacked during the crossing (" + String.Join(", ", battle) + ").");
            if (transitions > 0) failures.Add("A member embarked or disembarked during the crossing (" + transitions + " samples).");
            if ((float)record["maxPlanarResidual"] > 5) failures.Add("A leader sample jumped " + record["maxPlanarResidual"] + " units planarly beyond its rebases.");
            row["memberRebases"] = lords.Skip(1).ToDictionary(m => m.StringId, m => (object)(PeriodicSeam.Events.Count(e => e.Party == m.StringId) - memberRebases[m.StringId]));
            if (maxSpread > 6) failures.Add("A member strayed " + maxSpread + " units from the leader (the long way round).");
            if (detached > 0) failures.Add("Members detached during the crossing.");
            var state = new List<object>(); row["after"] = state;
            foreach (var p in lords) state.Add(Face(world, p, failures, "after the crossing"));
            if (sea && lords.Any(p => !p.IsCurrentlyAtSea)) failures.Add("A fleet party left the sea.");
            row["armyStillActive"] = leader.Army == army; row["dispersalReasons"] = dispersal;
            if (leader.Army != army) failures.Add("The army disbanded during the crossing (" + String.Join(", ", dispersal) + ").");
            }
            finally
            {
                CampaignEvents.ArmyDispersed.ClearListeners(listener);
                if (army != null && leader.Army == army) DisbandArmyAction.ApplyByObjectiveFinished(army);
                Release(lords);
                Restore(heldAway);
                if (declaredWar && kingdom.IsAtWarWith(rival)) MakePeaceAction.Apply(kingdom, rival);
            }
        }

        // The party's route diagnostics (PeriodicRouteExecutor) the first time its path holds a seam segment, else null.
        private static Dictionary<string, object> SeamRoute(PeriodicWorld world, MobileParty party)
        {
            var d = PeriodicRouteExecutor.Describe(world, party);
            return ((List<object>)d["seamCrossings"]).Count > 0 ? d : null;
        }

        private static void Release(List<MobileParty> lords) { foreach (var p in lords.Where(p => p.IsActive)) p.Ai.SetDoNotMakeNewDecisions(false); }

        // Free lord parties that match are held, the land ones moved to a land row in the middle of the world (a ship stays where it is: it
        // cannot engage a land party), so no third party intercepts a section's parties at the seam (synthetic). Restore puts them back.
        private static List<KeyValuePair<MobileParty, CampaignVec2>> HoldAway(PeriodicWorld world, float y0, Func<MobileParty, bool> which)
        {
            var held = new List<KeyValuePair<MobileParty, CampaignVec2>>();
            float farY = WorldWrapProbe.LandRow(y0, world.OriginX + world.Width / 2);
            foreach (var p in MobileParty.All.Where(p => p.IsActive && p.IsLordParty && p.MapEvent == null && p.Army == null && p.AttachedTo == null && which(p)).ToList())
            {
                held.Add(new KeyValuePair<MobileParty, CampaignVec2>(p, p.Position));
                if (p.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(p);
                p.Ai.SetDoNotMakeNewDecisions(true);
                if (!float.IsNaN(farY) && !p.IsCurrentlyAtSea) p.Position = new CampaignVec2(new Vec2(world.OriginX + world.Width / 2, farY), true);
                p.SetMoveModeHold();
            }
            return held;
        }

        private static void Restore(List<KeyValuePair<MobileParty, CampaignVec2>> held)
        {
            foreach (var kv in held.Where(kv => kv.Key.IsActive))
            {
                kv.Key.Ai.SetDoNotMakeNewDecisions(false);
                if (kv.Key.MapEvent == null && kv.Key.CurrentSettlement == null && !kv.Key.IsCurrentlyAtSea) kv.Key.Position = kv.Value;
            }
        }

        // ---- route cancellation just before and just after the seam ----
        private static IEnumerator Cancellation(ProbeContext context, DiagnosticSandboxBootstrap boot, PeriodicWorld world, Settlement town1, Settlement town2, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"];
            var main = MobileParty.MainParty;
            if (main.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(main);
            if (main.IsCurrentlyAtSea) main.DisembarkToPosition(town1.GatePosition);
            main.Position = town1.GatePosition; main.SetMoveModeHold();
            var legs = new List<object>(); row["legs"] = legs;
            foreach (var cut in new[] { Tuple.Create("cancelled before the seam", -3f), Tuple.Create("cancelled after the seam", 3f) })
            {
                main.SetMoveGoToPoint(town2.GatePosition, MobileParty.NavigationType.Default);
                var go = new Dictionary<string, object> { ["leg"] = "player toward the west town, " + cut.Item1, ["party"] = main.StringId }; legs.Add(go);
                // Logical x measured from the seam at MaxX: -3 is three units before it, +3 three units past it (canonical x OriginX + 3).
                Func<bool> reached = () => cut.Item2 < 0 ? main.Position.X >= world.MaxX + cut.Item2 && main.Position.X < world.MaxX : main.Position.X > world.OriginX && main.Position.X <= world.OriginX + cut.Item2 + 2 && main.Position.X >= world.OriginX + cut.Item2 - 2;
                var steps = WorldWrapProbe.Travel(context, boot, world, main, reached, 90, go);
                while (steps.MoveNext()) yield return steps.Current;
                if (!(bool)go["arrived"]) { failures.Add(cut.Item1 + ": the player never reached the cut point."); continue; }
                main.SetMoveModeHold();
                var held = main.Position.ToVec2();
                double begin = context.Seconds;
                boot.HoldPaused = false; DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppablePlay);
                while (context.Seconds - begin < 1.5) { boot.Tick(); yield return null; }
                DiagnosticSandboxBootstrap.Pause(Campaign.Current); boot.HoldPaused = true;
                var drift = world.Distance(held, main.Position.ToVec2());
                go["afterHold"] = Face(world, main, failures, cut.Item1 + " (held)"); go["driftWhileHeld"] = drift;
                if (drift > 0.5f) failures.Add(cut.Item1 + ": the held party moved " + drift + " units.");
                var back = new Dictionary<string, object> { ["leg"] = "player back to the east town after " + cut.Item1, ["party"] = main.StringId }; legs.Add(back);
                main.SetMoveGoToPoint(town1.GatePosition, MobileParty.NavigationType.Default);
                var target = town1.GatePosition.ToVec2();
                steps = WorldWrapProbe.Travel(context, boot, world, main, () => world.Distance(main.Position.ToVec2(), target) < 1.5f, 90, back);
                while (steps.MoveNext()) yield return steps.Current;
                if (!(bool)back["arrived"]) failures.Add(cut.Item1 + ": the reversed route did not arrive.");
                Steps(go, failures, cut.Item1 + " (on the way)"); Steps(back, failures, cut.Item1 + " (on the way back)");
                if ((float)back["traveledPeriodic"] > world.Width / 2) failures.Add(cut.Item1 + ": the way back took " + back["traveledPeriodic"] + " units (the long way).");
                back["after"] = Face(world, main, failures, cut.Item1 + " (back)");
            }
        }

        // ---- an AI ship entering a port across the seam, from either side ----
        private static IEnumerator Ports(ProbeContext context, DiagnosticSandboxBootstrap boot, PeriodicWorld world, Settlement town1, Settlement town2, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"];
            var ship = Ships().FirstOrDefault();
            if (ship == null) { failures.Add("No AI lord party with ships."); yield break; }
            Provision(ship);
            // The ship enters both towns: their kingdoms are at peace for the section (synthetic, recorded; restored after).
            var f1 = town1.MapFaction; var f2 = town2.MapFaction;
            bool peaceMade = f1 != f2 && f1.IsAtWarWith(f2);
            if (peaceMade) MakePeaceAction.Apply(f1, f2);
            row["setup"] = new Dictionary<string, object> { ["ship"] = ship.StringId, ["peaceMadeForTheSection"] = peaceMade };
            try
            {
            ship.Ai.SetDoNotMakeNewDecisions(true);
            if (ship.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(ship);
            ship.SetSailAtPosition(town1.PortPosition); ship.SetMoveModeHold();
            var legs = new List<object>(); row["legs"] = legs;
            foreach (var leg in new[] { Tuple.Create("east port -> west port", town1, town2), Tuple.Create("west port -> east port", town2, town1) })
            {
                var record = new Dictionary<string, object> { ["leg"] = "AI ship " + leg.Item1 + " (entering)", ["party"] = ship.StringId }; legs.Add(record);
                ship.SetMoveGoToSettlement(leg.Item3, MobileParty.NavigationType.Naval, true);
                Dictionary<string, object> route = null;
                var last = ship.Position.ToVec2(); float maxBeforeEntry = 0;
                var steps = WorldWrapProbe.Travel(context, boot, world, ship, () =>
                {
                    route = route ?? SeamRoute(world, ship);
                    if (ship.CurrentSettlement == leg.Item3) return true;
                    var here = ship.Position.ToVec2(); maxBeforeEntry = Math.Max(maxBeforeEntry, world.Distance(last, here)); last = here;
                    return false;
                }, 180, record);
                while (steps.MoveNext()) yield return steps.Current;
                record["entered"] = ship.CurrentSettlement == leg.Item3; record["routeAtSeam"] = route;
                record["maxStepBeforeEntry"] = maxBeforeEntry; record["entrySnap"] = "entering moves the party from the port to the settlement (" + leg.Item3.PortPosition.ToVec2().Distance(leg.Item3.GatePosition.ToVec2()) + " units)";
                if (!(bool)record["arrived"]) failures.Add(leg.Item1 + ": the ship did not enter the port.");
                if ((int)record["rebases"] < 1) failures.Add(leg.Item1 + ": never crossed the seam.");
                if (maxBeforeEntry > 5) failures.Add(leg.Item1 + ": a sample moved " + maxBeforeEntry + " units before the entry.");
                if ((float)record["maxPlanarResidual"] > 5 && !(bool)record["entered"]) failures.Add(leg.Item1 + ": a sample jumped " + record["maxPlanarResidual"] + " units planarly beyond its rebases.");
                if ((float)record["traveledPeriodic"] > world.Width / 2) failures.Add(leg.Item1 + ": travelled " + record["traveledPeriodic"] + " units (the long way).");
                if (ship.CurrentSettlement == leg.Item3) { LeaveSettlementAction.ApplyForParty(ship); ship.SetSailAtPosition(leg.Item3.PortPosition); ship.SetMoveModeHold(); }
                record["after"] = Face(world, ship, failures, leg.Item1 + " (after leaving)");
            }
            }
            finally
            {
                if (ship.IsActive) ship.Ai.SetDoNotMakeNewDecisions(false);
                if (peaceMade && !f1.IsAtWarWith(f2)) DeclareWarAction.ApplyByDefault(f1, f2);
            }
        }

        // The rulers' own ships (lords of port towns get them at creation), never a lord the probe made: those carry probe-granted ships.
        private static List<MobileParty> Ships()
        {
            return MobileParty.All.Where(p => p != MobileParty.MainParty && p.IsActive && !p.IsCaravan && p.Ships.Count > 0 && p.MapEvent == null && p.Army == null && p.AttachedTo == null
                && p.IsLordParty && !created.Contains(p.StringId)).OrderBy(p => p.StringId, StringComparer.Ordinal).ToList();
        }

        // ---- circumnavigation: four legs of +250 east (two crossings, one direction) ----
        private static IEnumerator Circumnavigation(ProbeContext context, DiagnosticSandboxBootstrap boot, PeriodicWorld world, Settlement town1, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"];
            var ships = Ships();
            var ship = ships.Skip(1).FirstOrDefault() ?? ships.FirstOrDefault();
            if (ship == null) { failures.Add("No AI lord party with ships."); yield break; }
            Provision(ship);
            // A sea row around the whole world (every 6.25 units of a canonical row is a sea face).
            float y = float.NaN;
            for (float dy = 0; dy < world.Coordinates.Height / 2 && float.IsNaN(y); dy += 3.125f)
                foreach (float cy in new[] { (float)(world.Coordinates.OriginY + world.Coordinates.Height / 2 + dy), (float)(world.Coordinates.OriginY + world.Coordinates.Height / 2 - dy) })
                {
                    bool sea = true;
                    for (float x = world.OriginX; x < world.MaxX && sea; x += 3.125f) { var p = new CampaignVec2(new Vec2(x, cy), false); sea = p.Face.IsValid(); }
                    if (sea) { y = cy; break; }
                }
            if (float.IsNaN(y)) { row["skipped"] = "no sea row around the world on this fixture"; failures.Add("No sea row around the world for the circumnavigation."); yield break; }
            var model = Campaign.Current.Models.MapDistanceModel;
            try
            {
            ship.Ai.SetDoNotMakeNewDecisions(true);
            if (ship.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(ship);
            float x0 = world.MaxX - 100;
            int transitions = 0;
            ship.SetSailAtPosition(new CampaignVec2(new Vec2(x0, y), false)); ship.SetMoveModeHold();
            var legs = new List<object>(); row["legs"] = legs;
            int rebasesBefore = PeriodicSeam.Events.Count(e => e.Party == ship.StringId);
            float total = 0; double hours = CampaignTime.Now.ToHours; float logicalAdvance = 0;
            for (int i = 1; i <= 4; i++)
            {
                float targetX = world.Canonical(x0 + 250 * i);
                var target = new CampaignVec2(new Vec2(targetX, y), false);
                var record = new Dictionary<string, object> { ["leg"] = "east leg " + i, ["party"] = ship.StringId, ["targetX"] = targetX }; legs.Add(record);
                float before = world.LogicalX(ship.Position.X);
                // The distance model's route cost to a point 250 east takes that way, never the 550 west (directed by the periodic placement).
                float landRatio;
                float cost = model.GetDistance(ship, in target, MobileParty.NavigationType.Naval, out landRatio);
                record["routeCost"] = cost;
                if (!(cost < world.Width / 2)) failures.Add("leg " + i + ": the route cost " + cost + " is not the short way east (250).");
                ship.SetMoveGoToPoint(target, MobileParty.NavigationType.Naval);
                var steps = WorldWrapProbe.Travel(context, boot, world, ship, () =>
                {
                    if (ship.IsTransitionInProgress || !ship.IsCurrentlyAtSea) transitions++;   // the cut is not a coast: no embark cost, no transition
                    return world.Distance(ship.Position.ToVec2(), target.ToVec2()) < 2.5f;
                }, 180, record);
                while (steps.MoveNext()) yield return steps.Current;
                float advance = (float)world.DeltaX(world.OriginX + before, world.OriginX + world.LogicalX(ship.Position.X));
                record["logicalAdvanceEast"] = advance; logicalAdvance += advance;
                total += (float)record["traveledPeriodic"];
                if (!(bool)record["arrived"]) failures.Add("leg " + i + ": did not arrive.");
                if (advance < 0) failures.Add("leg " + i + ": went west.");
                Steps(record, failures, "leg " + i);
            }
            row["transitionSamples"] = transitions;
            if (transitions > 0) failures.Add("The ship was in a transition or off the sea for " + transitions + " samples.");
            int crossings = PeriodicSeam.Events.Count(e => e.Party == ship.StringId) - rebasesBefore;
            row["seaRowY"] = y; row["crossings"] = crossings; row["traveled"] = total; row["eastwardLogicalAdvance"] = logicalAdvance; row["campaignHours"] = CampaignTime.Now.ToHours - hours;
            row["after"] = Face(world, ship, failures, "after the circumnavigation");
            if (crossings != 2) failures.Add("The ship crossed the seam " + crossings + " times (2 expected for 1000 units east from 100 before the seam).");
            if (logicalAdvance < 950 || total > 1300) failures.Add("The voyage advanced " + logicalAdvance + " east over " + total + " travelled (1000 expected).");
            // After two crossings the ship's state prices a route as a fresh state at the same point does: its navigation face re-derived from
            // its position (a stale face or frame from a rebase would change the cost). Both directions are recorded (directed costs).
            float ratioAfter, ratioFresh;
            float costAfter = model.GetDistance(ship, town1, true, MobileParty.NavigationType.Naval, out ratioAfter);
            int faceAfter = ship.CurrentNavigationFace.FaceIndex;
            ship.Position = new CampaignVec2(ship.Position.ToVec2(), false);
            float costFresh = model.GetDistance(ship, town1, true, MobileParty.NavigationType.Naval, out ratioFresh);
            var here = ship.Position;
            float costBack = model.GetDistance(town1, in here, true, MobileParty.NavigationType.Naval);
            row["routeCostAfterCrossings"] = new Dictionary<string, object> { ["to"] = town1.StringId + " (port)", ["afterCrossings"] = costAfter, ["freshState"] = costFresh,
                ["faceAfter"] = faceAfter, ["faceFresh"] = ship.CurrentNavigationFace.FaceIndex, ["fromPortBack"] = costBack };
            if (Math.Abs(costAfter - costFresh) > 0.01f * Math.Max(1f, costFresh)) failures.Add("After the crossings the ship's route cost " + costAfter + " differs from a fresh state's " + costFresh + ".");
            }
            finally { if (ship.IsActive) ship.Ai.SetDoNotMakeNewDecisions(false); }
        }
    }
}
