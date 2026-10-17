using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using HarmonyLib;
using Helpers;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Diagnostics;
using MNBL.ProceduralWorld.Generation;
using MNBL.ProceduralWorld.Persistence;
using MNBL.ProceduralWorld.Surface;
using MNBL.ProceduralWorld.UI;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-49: the two-world load matrix in the running game (two_world_matrix; phase=create|cold|load, seed=, fixtureSeed=, culture=).
    //   create (one process): world A (seed 42) is generated, started (activation, character creation) and given mutable land and naval state:
    //     an AI lord's party embarked at sea, an AI lord's fleet after a simulated naval battle with pirates, a town's owner changed, a newly
    //     spawned lord party, a lord taken prisoner by the player, a changed relation, a siege, one fleet called across the seam (saved while
    //     its anchor moves) and one anchored by the seam, the player's party saved just after crossing the seam with a bandit pursuing it from
    //     across the seam; non-canonical values planted in the saved fields the per-tick pass does not cover (a BehaviorTarget, an anchor's last
    //     disembark position, a map track, a map event when one exists), which the save must write canonical; saved; the game ended (every
    //     tracked static back at its baseline). World B: MMR-19's fixture world (the production generator yields no second activatable world
    //     today), with the instant changes. Then A is loaded through the main menu's Continue (the preload state, whose screen calls the
    //     installed load on every frame) and compared value for value with its saved state, then B through the load screen's entry
    //     (SandBoxSaveHelper.TryLoadSave) from inside A (the loader checks B, then ends A) and compared. After each load: no object of the other
    //     world, the loaded world's frame and terrain context only, no generator started; the pursuit ends in one encounter and the party does
    //     not cross again, and the fleet's anchor arrives where it was going (A); B's changes hold while its time runs. Then refused loads from
    //     inside the running B: a torn save, a save naming a missing bundle, a save recorded by another game version: refused before anything
    //     changed, B still running; and a refused Continue at the main menu, which returns to the main menu. Saves A and B are kept for the
    //     cold phase.
    //   cold (a new process): A's installed scene and its derived reports are removed first (a sweep), then A is loaded (the scene re-encoded
    //     from the bundle by the surface encoder, the reports restored from the campaign's activation evidence), then B from inside A, each
    //     compared with the saved state from the create phase (previous=<the create run's result file>); the probe's saves are deleted at the end.
    // Steps that set up state are fail-soft (a failing step is recorded and the matrix goes on); every expectation is recomputed by
    // tools/summarize_two_world_matrix.py from the outputs.
    internal static class TwoWorldMatrixProbe
    {
        internal static IEnumerator Run(ProbeContext context)
        {
            var stack = new Stack<IEnumerator>(); stack.Push(Body(context));
            try
            {
                while (stack.Count > 0)
                {
                    if (!stack.Peek().MoveNext()) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
                    var nested = stack.Peek().Current as IEnumerator;
                    if (nested != null) stack.Push(nested); else yield return stack.Peek().Current;
                }
            }
            finally { while (stack.Count > 0) { try { (stack.Pop() as IDisposable)?.Dispose(); } catch (Exception) { } } }
        }

        private sealed class State
        {
            public ProbeContext Context;
            public List<string> Failures = new List<string>();
            public Dictionary<string, object> Worlds = new Dictionary<string, object>();
            public void Expect(bool ok, string what) { if (!ok) Failures.Add(what); Context.Stage((ok ? "ok: " : "FAIL: ") + what); }
        }

        private static IEnumerator Body(ProbeContext context)
        {
            string phase = context.Str("phase", "create");
            var st = new State { Context = context };
            context.Outputs["phase"] = phase;
            context.Outputs["failures"] = st.Failures;
            context.Outputs["worlds"] = st.Worlds;
            context.Outputs["baselines"] = new List<object>();
            Baseline(context, st, "start");
            if (phase == "create")
            {
                context.Outputs["scope"] = "MMR-49 create phase: worlds A and B generated, played into mutable land and naval state, saved; A then B loaded through the normal load entry in the same process and compared with their saves; refused loads; tracked statics back at baseline after each game.";
                yield return CreateWorld(context, st, "A", new[] { (uint)context.Int("seed", 42) });
                Baseline(context, st, "after world A ended");
                // World B: MMR-19's fixture world (seed 364610), a second distinct world: the production generator yields no second activatable
                // world today (seeds 43-72 refused by its quality gate, MMR-37; 73 refused by the campaign's village binding at activation).
                yield return CreateFixtureWorld(context, st, "B");
                Baseline(context, st, "after world B ended");
                yield return LoadWorld(context, st, "A", "same process, through Continue", true);
                yield return LoadWorld(context, st, "B", "same process, from inside A", false);
                yield return Refusals(context, st);
                yield return EndGame(context);
                Baseline(context, st, "after the same-process loads and the refused loads");
                yield return ContinueRefusal(context, st);
                Baseline(context, st, "after the refused Continue");
            }
            else if (phase == "load")
            {
                // A quick check of the load path on an existing save (save=<name>): routed, restored, ended, baseline.
                context.Outputs["scope"] = "MMR-49 load path check: one existing save of a generated world loaded through the normal load entry.";
                string save = context.Str("save", null);
                var info = MBSaveLoad.GetSaveFileWithName(save);
                bool vanilla = false;
                if (info == null) throw new InvalidOperationException("No save " + save);
                SandBox.SandBoxSaveHelper.TryLoadSave(info, r => vanilla = true, () => { });
                var loader = GeneratedWorldLoader.Current;
                double start = context.Seconds;
                while (loader != null && !loader.Done && context.Seconds - start < 3600) yield return null;
                context.Outputs["loader"] = loader == null ? null : loader.Report;
                context.Outputs["vanillaStarted"] = vanilla;
                st.Expect(loader != null && loader.State == GeneratedWorldLoader.Phase.Loaded && !vanilla, "the save loaded through the world loader (" + (loader == null ? "no loader" : loader.State + " " + loader.Refusal + " " + loader.Error) + ")");
                if (loader != null && loader.State == GeneratedWorldLoader.Phase.Loaded)
                {
                    Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
                    yield return Frames(30);
                    var binding = WorldSaveBinding.BindingOf(Campaign.Current.GetCampaignBehavior<WorldIdentityBehavior>());
                    context.Outputs["loadedBinding"] = binding == null ? null : binding.Instance;
                    context.Outputs["objects"] = binding == null ? null : WorldSnapshot.Objects(binding.WorldId + "_");
                    Campaign.Current.TimeControlMode = CampaignTimeControlMode.UnstoppablePlay;
                    double hours = CampaignTime.Now.ToHours;
                    start = context.Seconds;
                    while (context.Seconds - start < 10) { if (Campaign.Current.TimeControlMode == CampaignTimeControlMode.Stop) Campaign.Current.TimeControlMode = CampaignTimeControlMode.UnstoppablePlay; yield return null; }
                    Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
                    context.Outputs["hoursRun"] = CampaignTime.Now.ToHours - hours;
                    st.Expect(CampaignTime.Now.ToHours > hours, "campaign time runs after the load");
                }
                yield return EndGame(context);
                Baseline(context, st, "after the load check");
            }
            else
            {
                context.Outputs["scope"] = "MMR-49 cold phase: a new process; A's scene and reports swept, A loaded (scene re-encoded, reports restored), then B from inside A, each compared with the create phase's saved state; the probe's saves deleted.";
                var previous = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(File.ReadAllText(context.Str("previous", null)));
                var worlds = (Dictionary<string, object>)((Dictionary<string, object>)previous["outputs"])["worlds"];
                foreach (var kv in worlds) st.Worlds[kv.Key] = new Dictionary<string, object> { ["fromCreate"] = kv.Value };
                if (context.Bool("sweepSceneA", true)) SweepScene(context, st, "A");
                yield return LoadWorld(context, st, "A", "cold restart", false);
                yield return LoadWorld(context, st, "B", "cold restart, from inside A", false);
                yield return EndGame(context);
                Baseline(context, st, "after the cold loads");
                if (context.Bool("cleanup", true)) CleanUp(context, st);
            }
            context.Outputs["completed"] = true;
            context.Checkpoint();
            if (st.Failures.Count > 0) throw new InvalidOperationException("Two-world matrix expectations failed: " + String.Join("; ", st.Failures.Take(12)));
        }

        // ---------------------------------------------------------------- baseline

        private static void Baseline(ProbeContext context, State st, string label)
        {
            var held = WorldLifetimeScope.Held();
            ((List<object>)context.Outputs["baselines"]).Add(new Dictionary<string, object> { ["at"] = label, ["game"] = Game.Current != null, ["held"] = held.ToDictionary(k => k.Key, k => (object)k.Value), ["tracked"] = WorldLifetimeScope.Names.Count });
            st.Expect(Game.Current == null && held.Count == 0, "baseline " + label + ": no game and every tracked static at its baseline" + (held.Count == 0 ? "" : " (held: " + String.Join(", ", held.Keys) + ")"));
        }

        // ---------------------------------------------------------------- create

        private static IEnumerator CreateWorld(ProbeContext context, State st, string key, uint[] seeds)
        {
            var world = new Dictionary<string, object>();
            st.Worlds[key] = world;
            var refused = new List<object>(); world["seedsRefused"] = refused;
            var steps = new Dictionary<string, object>(); world["steps"] = steps;
            Module.CurrentModule.ExecuteInitialStateOptionWithId(WorldSetupScreen.MenuId);
            double start = context.Seconds;
            while ((WorldSetupScreen.Current == null || !WorldSetupScreen.Current.MovieLoaded) && context.Seconds - start < 20) yield return null;
            if (WorldSetupScreen.Current == null) throw new InvalidOperationException("The setup screen did not open.");
            var screen = WorldSetupScreen.Current; var vm = screen.ViewModel; var model = vm.Model;
            foreach (var seed in seeds)
            {
                vm.SeedText = seed.ToString(CultureInfo.InvariantCulture); yield return null;
                screen.HandleInput(k => k == SetupInput.Confirm, false);
                context.Stage("two_world_matrix:" + key + ":generate:" + seed);
                start = context.Seconds;
                yield return null;
                while (model.State == SetupState.Generating && context.Seconds - start < 1200) yield return null;
                if (model.State == SetupState.Generated) { world["seed"] = seed; break; }
                refused.Add(new Dictionary<string, object> { ["seed"] = seed, ["state"] = model.State.ToString(), ["error"] = model.Error == null ? null : model.Error.ToString() });
            }
            if (model.State != SetupState.Generated) throw new InvalidOperationException("World " + key + " was not generated from any candidate seed: " + model.State + " " + model.Error);
            vm.ExecuteStartCampaign();
            var creation = new NewCampaignFlowProbe.CreationDriver(context.Str("culture", "empire"), "MNBL " + key);
            start = context.Seconds;
            while (model.State == SetupState.Starting && context.Seconds - start < 3300)
            {
                if (Game.Current != null)
                {
                    creation.StepToMap();
                    if (Campaign.Current != null && Game.Current.GameStateManager.ActiveState is MapState) Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
                }
                yield return null;
            }
            world["startSeconds"] = context.Seconds - start;
            if (model.State != SetupState.Started || Campaign.Current == null) throw new InvalidOperationException("World " + key + " did not start: " + model.State + (model.Error == null ? "" : " (" + model.Error + ")"));
            while (!(Game.Current.GameStateManager.ActiveState is MapState) && context.Seconds - start < 3400) yield return null;
            Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
            yield return Frames(30);
            var binding = WorldSaveBinding.BindingOf(Campaign.Current.GetCampaignBehavior<WorldIdentityBehavior>());
            string prefix = binding.WorldId + "_";
            world["worldId"] = binding.WorldId; world["prefix"] = prefix; world["instance"] = binding.Instance; world["package"] = binding.PackageName; world["contentId"] = binding.ContentId;
            world["frame"] = PeriodicWorld.Current == null ? null : PeriodicWorld.Current.Coordinates.FrameIdentity;
            var initial = SaveBindingRuntime.Store.Journal(binding.Instance);
            world["initialSave"] = initial == null ? null : initial.SaveName;
            var watched = new List<string>(); world["watchedHeroes"] = watched;
            // Mutable state, then the save.
            yield return ScenarioA(context, st, world, steps, watched, prefix);
            yield return SaveAndEnd(context, st, key, world, prefix, watched);
        }

        // MMR-19's fixture world through the diagnostic bootstrap (as generated_campaign starts it): its package and identity are written once
        // the map is up, so every save names it.
        private static IEnumerator CreateFixtureWorld(ProbeContext context, State st, string key)
        {
            var world = new Dictionary<string, object> { ["fixture"] = true };
            st.Worlds[key] = world;
            var steps = new Dictionary<string, object>(); world["steps"] = steps;
            uint seed = (uint)context.Int("fixtureSeed", 364610);
            world["seed"] = seed;
            var registration = GeneratedCampaignRegistration.Prepare(seed, context.Str("fixturePlacement", ""), context.Str("catalogReceipt", ""), context.Str("culture", "empire"),
                context.Str("heroTemplate", "spc_legion_of_the_betrayed_leader_0"), context.Str("townPrototype", "town_EN2"), context.Str("villagePrototype", "village_EN1_3"));
            var support = new Dictionary<string, object>();
            var boot = DiagnosticSandboxBootstrap.StartGenerated(registration, registration.SceneName, registration.CultureId, census => { }, support,
                stage => context.Stage("two_world_matrix:" + key + ":" + stage), registration.Periodic, registration.Climate);
            double start = context.Seconds;
            while (!boot.Completed)
            {
                boot.Tick();
                if (boot.Failed) throw new InvalidOperationException("World " + key + " (fixture) failed: " + boot.Error);
                if (context.Seconds - start > 1200) throw new TimeoutException("World " + key + " (fixture) did not reach its map.");
                yield return null;
            }
            world["startSeconds"] = context.Seconds - start;
            var map = Campaign.Current.MapSceneWrapper as GeneratedMapScene;
            start = context.Seconds;
            while (map != null && (!map.Scene.IsLoadingFinished() || context.Seconds - start < 5) && context.Seconds - start < 180) { boot.Tick(); yield return null; }
            InformationManager.HideInquiry();
            var package = registration.WritePackage(GeneratedWorldPackage.Sha256(File.ReadAllBytes(typeof(TwoWorldMatrixProbe).Assembly.Location)));
            foreach (var entry in WorldIdentity.Build(registration, package, (TaleWorlds.CampaignSystem.Map.IMapScene)map, WorldBinding.NoContent)) boot.Identity.Entries[entry.Key] = entry.Value;
            var binding = WorldSaveBinding.BindingOf(boot.Identity);
            string prefix = binding.WorldId + "_";
            world["worldId"] = binding.WorldId; world["prefix"] = prefix; world["instance"] = binding.Instance; world["package"] = binding.PackageName; world["contentId"] = binding.ContentId;
            world["frame"] = PeriodicWorld.Current == null ? null : PeriodicWorld.Current.Coordinates.FrameIdentity;
            world["initialSave"] = null;
            boot.HoldPaused = false;
            Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
            var watched = new List<string>(); world["watchedHeroes"] = watched;
            Instant(st, world, steps, watched, prefix, key);
            yield return SaveAndEnd(context, st, key, world, prefix, watched);
        }

        private static IEnumerator SaveAndEnd(ProbeContext context, State st, string key, Dictionary<string, object> world, string prefix, List<string> watched)
        {
            double start;
            string save = "mnbl_probe49_" + key + "_" + context.ProcessId;
            world["save"] = save;
            var before = WorldSaveBinding.Last;
            var planted = Plant();
            world["periodic"] = PeriodicWorld.Current != null;
            world["planted"] = planted.Select(p => p.Row).ToList();
            int fieldsBefore = PeriodicSeam.SaveFieldsCanonicalized;
            Campaign.Current.SaveHandler.SaveAs(save);
            start = context.Seconds;
            yield return null;
            while (context.Seconds - start < 120 && (WorldSaveBinding.Last == before || WorldSaveBinding.Last.State == EarlyWorldBindingStore.Intent || Campaign.Current.SaveHandler.IsSaving)) yield return null;
            var journal = WorldSaveBinding.Last;
            world["saveJournal"] = journal == null || journal == before ? null : journal.Report();
            var nonCanonical = PeriodicSeam.NonCanonicalSaved();
            world["nonCanonicalAtSave"] = nonCanonical.Take(20).ToList(); world["savePassFields"] = PeriodicSeam.SaveFieldsCanonicalized - fieldsBefore;
            foreach (var p in planted) p.Check();
            st.Expect(journal != null && journal != before && journal.SaveName == save && journal.State == EarlyWorldBindingStore.Published && nonCanonical.Count == 0,
                "world " + key + " saved as " + save + ": published, every saved position canonical");
            if (PeriodicWorld.Current != null)
                st.Expect(planted.Count >= (key == "A" ? 3 : 1) && planted.All(p => Equals(p.Row["canonicalAfterSave"], true) && Equals(p.Row["nonCanonicalPlanted"], true)),
                    "world " + key + ": the non-canonical values planted before the save (" + String.Join(", ", planted.Select(p => (string)p.Row["field"])) + ") were saved canonical, each at its canonical image");
            var saved = WorldSnapshot.Capture(prefix, watched);
            world["saved"] = saved;
            world["savedObjects"] = WorldSnapshot.Objects(prefix);
            // The control for the comparison after a load: the same frames, time stopped, without a load (AI parties may choose a new
            // movement target without time passing).
            yield return Frames(20);
            world["controlDrift"] = WorldSnapshot.Compare(Normalize(saved), Normalize(WorldSnapshot.Capture(prefix, watched)), 400);
            context.Checkpoint();
            yield return EndGame(context);
        }

        private static IEnumerator ScenarioA(ProbeContext context, State st, Dictionary<string, object> world, Dictionary<string, object> steps, List<string> watched, string prefix)
        {
            var main = MobileParty.MainParty;
            var landlocked = LandlockedClans();
            var ports = Settlement.All.Where(s => s.StringId.StartsWith(prefix, StringComparison.Ordinal) && s.HasPort && s.PortPosition.IsValid()).OrderBy(s => s.StringId, StringComparer.Ordinal).ToList();
            world["ports"] = ports.Count;
            MobileParty battleLord = null, pirate = null;
            main.IgnoreByOtherPartiesTill(CampaignTime.HoursFromNow(24 * 30));
            MobileParty embarked = null;
            // An AI fleet after a naval battle with pirates.
            Step(steps, "navalBattle", () =>
            {
                pirate = MobileParty.All.Where(p => p.IsActive && p.IsBandit && p.MapEvent == null && p.Ships.Count > 0).OrderBy(p => p.StringId, StringComparer.Ordinal).FirstOrDefault();
                if (pirate == null) throw new InvalidOperationException("no pirate party with ships");
                if (!pirate.IsCurrentlyAtSea) pirate.SetSailAtPosition(NavalPoint(ports.First().PortPosition));
                battleLord = Lords(landlocked).FirstOrDefault(p => p.MemberRoster.TotalManCount >= 20);
                if (battleLord == null) throw new InvalidOperationException("no lord party of a seafaring clan");
                if (battleLord.Ships.Count == 0) Grant(battleLord);
                battleLord.SetSailAtPosition(NavalPoint(pirate.Position));
                StartBattleAction.ApplyStartBattle(battleLord, pirate);
                return new Dictionary<string, object> { ["lord"] = battleLord.StringId, ["pirate"] = pirate.StringId, ["naval"] = battleLord.MapEvent != null && battleLord.MapEvent.IsNavalMapEvent,
                    ["lordMen"] = battleLord.MemberRoster.TotalManCount, ["pirateMen"] = pirate.MemberRoster.TotalManCount, ["lordShips"] = ShipsOf(battleLord) };
            });
            if (battleLord != null && battleLord.MapEvent != null)
            {
                Campaign.Current.TimeControlMode = CampaignTimeControlMode.UnstoppablePlay;
                double start = context.Seconds;
                while (context.Seconds - start < 120 && battleLord.IsActive && battleLord.MapEvent != null)
                {
                    if (Campaign.Current.TimeControlMode == CampaignTimeControlMode.Stop) Campaign.Current.TimeControlMode = CampaignTimeControlMode.UnstoppablePlay;
                    yield return null;
                }
                Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
                yield return Frames(5);
                steps["navalBattleAfter"] = new Dictionary<string, object> { ["battleSeconds"] = context.Seconds - start, ["lordActive"] = battleLord.IsActive, ["lordInEvent"] = battleLord.MapEvent != null,
                    ["lordMen"] = battleLord.IsActive ? battleLord.MemberRoster.TotalManCount : 0, ["lordShips"] = battleLord.IsActive ? ShipsOf(battleLord) : null,
                    ["pirateActive"] = pirate != null && pirate.IsActive, ["lordAtSea"] = battleLord.IsActive && battleLord.IsCurrentlyAtSea };
                world["fleetAfterBattle"] = battleLord.IsActive ? battleLord.StringId : null;
                st.Expect(battleLord.IsActive && battleLord.MapEvent == null && battleLord.Ships.Count > 0, "world A: an AI lord's fleet came out of a naval battle with pirates (ships and crew as the battle left them)");
            }
            else st.Expect(false, "world A: a naval battle started (" + (steps.ContainsKey("navalBattle") ? Convert.ToString(steps["navalBattle"], CultureInfo.InvariantCulture) : "") + ")");
            // An AI lord's party embarked at sea (with its own ship, not the battle's fleet).
            Step(steps, "embark", () =>
            {
                if (ports.Count == 0) throw new InvalidOperationException("the world has no port");
                embarked = Lords(landlocked).First(p => p != battleLord);
                if (embarked.Ships.Count == 0) Grant(embarked);
                var port = ports.OrderBy(p => p.PortPosition.ToVec2().DistanceSquared(embarked.Position.ToVec2())).First();
                embarked.SetSailAtPosition(NavalPoint(port.PortPosition));
                embarked.Ai.SetDoNotMakeNewDecisions(true);
                world["embarkedParty"] = embarked.StringId;
                return new Dictionary<string, object> { ["party"] = embarked.StringId, ["port"] = port.StringId, ["ships"] = embarked.Ships.Count };
            });
            yield return Frames(10);
            steps["embarkAtSea"] = embarked != null && embarked.IsCurrentlyAtSea;
            st.Expect(embarked != null && embarked.IsCurrentlyAtSea, "world A: an AI lord's party is embarked at sea");
            // Instant changes, time stopped.
            Instant(st, world, steps, watched, prefix, "A");
            // A siege.
            Step(steps, "siege", () =>
            {
                var town = Settlement.All.Where(s => s.IsTown && s.StringId.StartsWith(prefix, StringComparison.Ordinal) && s.SiegeEvent == null && s.OwnerClan != Clan.PlayerClan && s.MapFaction is Kingdom)
                    .OrderBy(s => s.StringId, StringComparer.Ordinal).First();
                var enemy = Kingdom.All.Where(k => k != town.MapFaction && !k.IsEliminated && k.Leader != null && k != Clan.PlayerClan.MapFaction).OrderBy(k => k.StringId, StringComparer.Ordinal).First();
                if (!enemy.IsAtWarWith(town.MapFaction)) DeclareWarAction.ApplyByDefault(enemy, town.MapFaction);
                // A lord party of the enemy, else one spawned for an idle enemy lord (a campaign can have every lord in a settlement or an army).
                var besieger = MobileParty.All.Where(p => p.IsActive && p.IsLordParty && p.MapFaction == enemy && p.MapEvent == null && p.Army == null && p != battleLord && p != embarked && !p.IsCurrentlyAtSea && p.LeaderHero != null)
                    .OrderBy(p => p.CurrentSettlement == null ? 0 : 1).ThenBy(p => p.StringId, StringComparer.Ordinal).FirstOrDefault();
                if (besieger == null)
                {
                    var lord = Hero.AllAliveHeroes.Where(h => h.IsLord && h.MapFaction == enemy && h.PartyBelongedTo == null && !h.IsPrisoner && h.CurrentSettlement != null).OrderBy(h => h.StringId, StringComparer.Ordinal).First();
                    besieger = MobilePartyHelper.SpawnLordParty(lord, lord.CurrentSettlement);
                }
                if (besieger.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(besieger);
                besieger.Position = town.GatePosition;
                besieger.SetMoveBesiegeSettlement(town, MobileParty.NavigationType.Default);
                if (town.SiegeEvent == null) Campaign.Current.SiegeEventManager.StartSiegeEvent(town, besieger);
                world["siegedTown"] = town.StringId; world["besieger"] = besieger.StringId;
                return new Dictionary<string, object> { ["town"] = town.StringId, ["besieger"] = besieger.StringId, ["siege"] = town.SiegeEvent != null };
            });
            st.Expect(world.ContainsKey("siegedTown") && Settlement.Find((string)world["siegedTown"]).SiegeEvent != null, "world A: a town is under siege");
            // Anchors: one fleet called across the seam (moving at the save), one anchored by the seam.
            Step(steps, "anchors", () =>
            {
                var frame = PeriodicWorld.Current;
                var east = ports.OrderByDescending(s => s.PortPosition.X).First(); var west = ports.OrderBy(s => s.PortPosition.X).First();
                var fleets = Lords(landlocked).Where(p => p != battleLord && p.BesiegedSettlement == null && p.Anchor != null).Take(2).ToList();
                if (fleets.Count < 2) throw new InvalidOperationException("fewer than two lord parties with anchors");
                foreach (var f in fleets) if (f.Ships.Count == 0) Grant(f);
                fleets[0].Anchor.SetSettlement(east);
                fleets[0].Anchor.CallFleet(west);
                fleets[1].Anchor.SetSettlement(west);
                world["movingFleet"] = fleets[0].StringId; world["anchoredFleet"] = fleets[1].StringId;
                return new Dictionary<string, object> { ["east"] = east.StringId, ["eastX"] = east.PortPosition.X, ["west"] = west.StringId, ["westX"] = west.PortPosition.X,
                    ["seamX"] = frame == null ? (object)null : frame.OriginX, ["width"] = frame == null ? (object)null : frame.Width,
                    ["movingFleet"] = fleets[0].StringId, ["moving"] = fleets[0].Anchor.IsMovingToPoint, ["arrivalHours"] = fleets[0].Anchor.ArrivalTime.ToHours - CampaignTime.Now.ToHours,
                    ["anchoredFleet"] = fleets[1].StringId, ["anchoredAt"] = fleets[1].Anchor.Position.ToVec2().ToString() };
            });
            // The player's party crosses the seam just before the save; a bandit pursues it from across the seam.
            var periodic = PeriodicWorld.Current;
            float y = float.NaN;
            Step(steps, "seamCrossing", () =>
            {
                if (periodic == null) throw new InvalidOperationException("world A is not periodic");
                var xs = new[] { periodic.OriginX + 2f, periodic.MaxX - 2f, periodic.OriginX - 1f, periodic.OriginX + 1.5f };
                y = WorldWrapProbe.LandRow(main.Position.Y, xs);
                // Near the player first, then anywhere on the map (a start far from land at the seam, run 11568).
                for (float center = 20f; float.IsNaN(y) && center < periodic.Extent; center += 40f) y = WorldWrapProbe.LandRow(center, xs);
                if (float.IsNaN(y)) throw new InvalidOperationException("no land row across the seam anywhere on the map");
                main.Position = new CampaignVec2(new Vec2(periodic.OriginX + 2f, y), true);
                main.SetMoveGoToPoint(new CampaignVec2(new Vec2(periodic.MaxX - 2f, y), true), MobileParty.NavigationType.Default);
                return new Dictionary<string, object> { ["y"] = y, ["from"] = periodic.OriginX + 2f, ["to"] = periodic.MaxX - 2f };
            });
            if (!float.IsNaN(y))
            {
                int before = PeriodicSeam.Events.Count;
                Campaign.Current.TimeControlMode = CampaignTimeControlMode.UnstoppablePlay;
                double start = context.Seconds;
                while (!PeriodicSeam.Events.Skip(before).Any(e => e.Party == main.StringId) && context.Seconds - start < 30)
                {
                    if (Campaign.Current.TimeControlMode == CampaignTimeControlMode.Stop) Campaign.Current.TimeControlMode = CampaignTimeControlMode.UnstoppablePlay;
                    yield return null;
                }
                Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
                var crossing = PeriodicSeam.Events.Skip(before).FirstOrDefault(e => e.Party == main.StringId);
                world["crossed"] = crossing != null;
                world["crossing"] = crossing == null ? null : new Dictionary<string, object> { ["fromX"] = crossing.FromX, ["toX"] = crossing.ToX, ["y"] = crossing.Y };
                st.Expect(crossing != null, "world A: the player's party crossed the seam just before the save");
                Step(steps, "pursuit", () =>
                {
                    var bandit = MobileParty.All.Where(p => p.IsActive && p.IsBandit && p.MapEvent == null && !p.IsCurrentlyAtSea && p.Ships.Count == 0 && p.CurrentSettlement == null)
                        .OrderBy(p => p.StringId, StringComparer.Ordinal).First();
                    bandit.Position = new CampaignVec2(new Vec2(periodic.OriginX + 1.5f, y), true);
                    main.IgnoreByOtherPartiesTill(CampaignTime.Now);
                    bandit.SetMoveEngageParty(main, MobileParty.NavigationType.Default);
                    world["pursuer"] = bandit.StringId;
                    return new Dictionary<string, object> { ["bandit"] = bandit.StringId, ["at"] = bandit.Position.ToVec2().ToString(), ["player"] = main.Position.ToVec2().ToString() };
                });
            }
            if (float.IsNaN(y)) st.Expect(false, "world A: the player's party crosses the seam just before the save (" + Convert.ToString(steps.ContainsKey("seamCrossing") ? steps["seamCrossing"] : null, CultureInfo.InvariantCulture) + ")");
            var moving = world.ContainsKey("movingFleet") ? MobileParty.All.FirstOrDefault(p => p.StringId == (string)world["movingFleet"]) : null;
            st.Expect(moving != null && moving.Anchor.IsMovingToPoint, "world A: a fleet is called across the seam and its anchor is moving at the save");
        }

        // Ownership, a newly spawned lord party, a lord taken prisoner, a relation.
        private static void Instant(State st, Dictionary<string, object> world, Dictionary<string, object> steps, List<string> watched, string prefix, string key)
        {
            Step(steps, "ownership", () =>
            {
                var town = Settlement.All.Where(s => s.IsTown && s.StringId.StartsWith(prefix, StringComparison.Ordinal) && s.OwnerClan != null && s.OwnerClan != Clan.PlayerClan && s.SiegeEvent == null)
                    .OrderBy(s => s.StringId, StringComparer.Ordinal).Last();
                string before = town.OwnerClan.StringId;
                ChangeOwnerOfSettlementAction.ApplyByDefault(Hero.MainHero, town);
                world["ownedTown"] = town.StringId;
                return new Dictionary<string, object> { ["town"] = town.StringId, ["before"] = before, ["after"] = town.OwnerClan.StringId };
            });
            Func<Hero, Settlement> home = h => h.CurrentSettlement ?? h.HomeSettlement ?? h.Clan.Settlements.FirstOrDefault()
                ?? Settlement.All.Where(s => s.IsTown && s.MapFaction == h.MapFaction && s.StringId.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(s => s.StringId, StringComparer.Ordinal).FirstOrDefault();
            var idle = Hero.AllAliveHeroes.Where(h => h.IsLord && h.Clan != null && h.Clan != Clan.PlayerClan && h.Clan.Kingdom != null && h.PartyBelongedTo == null && !h.IsPrisoner
                && h.StringId.StartsWith(prefix, StringComparison.Ordinal) && home(h) != null).OrderBy(h => h.CurrentSettlement == null ? 1 : 0).ThenBy(h => h.StringId, StringComparer.Ordinal).ToList();
            world["idleLords"] = idle.Count;
            Step(steps, "spawnedParty", () =>
            {
                if (idle.Count == 0) throw new InvalidOperationException("no idle lord");
                var hero = idle[0];
                var party = MobilePartyHelper.SpawnLordParty(hero, home(hero));
                world["spawnedParty"] = party.StringId; watched.Add(hero.StringId);
                return new Dictionary<string, object> { ["hero"] = hero.StringId, ["party"] = party.StringId, ["men"] = party.MemberRoster.TotalManCount, ["at"] = home(hero).StringId };
            });
            Step(steps, "prisoner", () =>
            {
                // A second idle lord, else a lord party's leader (a fixture world's lords all lead parties).
                var hero = idle.Skip(1).FirstOrDefault(h => h.PartyBelongedTo == null && !h.IsPrisoner)
                    ?? MobileParty.All.Where(p => p.IsActive && p.IsLordParty && p.MapEvent == null && p.LeaderHero != null && p.LeaderHero.Clan != null && p.LeaderHero.Clan != Clan.PlayerClan
                        && p.LeaderHero.StringId.StartsWith(prefix, StringComparison.Ordinal) && !watched.Contains(p.LeaderHero.StringId) && !Equals(world.ContainsKey("spawnedParty") ? world["spawnedParty"] : null, p.StringId))
                        .OrderBy(p => p.StringId, StringComparer.Ordinal).Select(p => p.LeaderHero).FirstOrDefault();
                if (hero == null) throw new InvalidOperationException("no lord to take prisoner");
                TakePrisonerAction.Apply(MobileParty.MainParty.Party, hero);
                world["prisoner"] = hero.StringId; watched.Add(hero.StringId);
                return new Dictionary<string, object> { ["hero"] = hero.StringId, ["prisoner"] = hero.IsPrisoner, ["captor"] = hero.PartyBelongedToAsPrisoner == null ? null : hero.PartyBelongedToAsPrisoner.Name.ToString() };
            });
            Step(steps, "relation", () =>
            {
                var hero = Hero.AllAliveHeroes.Where(h => h.IsLord && h.StringId.StartsWith(prefix, StringComparison.Ordinal) && h != Hero.MainHero && !watched.Contains(h.StringId))
                    .OrderBy(h => h.StringId, StringComparer.Ordinal).First();
                int before = Hero.MainHero.GetRelation(hero);
                ChangeRelationAction.ApplyRelationChangeBetweenHeroes(Hero.MainHero, hero, 37, false);
                watched.Add(hero.StringId);
                return new Dictionary<string, object> { ["hero"] = hero.StringId, ["before"] = before, ["after"] = Hero.MainHero.GetRelation(hero) };
            });
            // A fixture world may have no idle lord to spawn a party for (its lords all lead parties); its spawned party is then not required.
            bool spawnRequired = !world.ContainsKey("fixture") || idle.Count > 0;
            st.Expect(world.ContainsKey("ownedTown") && (world.ContainsKey("spawnedParty") || !spawnRequired) && world.ContainsKey("prisoner"),
                "world " + key + ": a town changed owner, " + (spawnRequired ? "a lord party was spawned" : "no idle lord to spawn a party for (fixture world)") + " and a lord was taken prisoner");
        }

        // ---------------------------------------------------------------- load

        private static IEnumerator LoadWorld(ProbeContext context, State st, string key, string how, bool viaContinue)
        {
            var world = (Dictionary<string, object>)st.Worlds[key];
            var created = world.ContainsKey("fromCreate") ? (Dictionary<string, object>)world["fromCreate"] : world;
            string other = key == "A" ? "B" : "A";
            var otherWorld = (Dictionary<string, object>)st.Worlds[other];
            var otherCreated = otherWorld.ContainsKey("fromCreate") ? (Dictionary<string, object>)otherWorld["fromCreate"] : otherWorld;
            string save = (string)created["save"], prefix = (string)created["prefix"], otherPrefix = (string)otherCreated["prefix"];
            var load = new Dictionary<string, object> { ["how"] = how, ["fromRunningGame"] = Game.Current != null };
            world["load"] = load;
            var info = MBSaveLoad.GetSaveFileWithName(save);
            if (info == null) { st.Expect(false, "world " + key + "'s save " + save + " is listed"); yield break; }
            bool vanilla = false; int routed = LoadRouting.Routed, ignored = LoadRouting.Ignored;
            var previousLoader = GeneratedWorldLoader.Current;
            double start = context.Seconds;
            if (viaContinue)
            {
                // As the main menu's Continue does (SandBoxViewSubModule): the preload state for the save, whose screen starts it.
                load["entry"] = "Continue (preload state)";
                var preload = GameStateManager.Current.CreateState<Sandbox.View.GameStates.PreloadState>(new object[] { save });
                GameStateManager.Current.PushState(preload, 0);
                while (GeneratedWorldLoader.Current == previousLoader && context.Seconds - start < 60) yield return null;
            }
            else
            {
                load["entry"] = "load screen (SandBoxSaveHelper.TryLoadSave)";
                SandBox.SandBoxSaveHelper.TryLoadSave(info, r => vanilla = true, () => { });
            }
            var loader = GeneratedWorldLoader.Current == previousLoader ? null : GeneratedWorldLoader.Current;
            while (loader != null && !loader.Done && context.Seconds - start < 3600) yield return null;
            load["routed"] = LoadRouting.Routed == routed + 1 && loader != null;
            load["ignoredRepeats"] = LoadRouting.Ignored - ignored;
            load["seconds"] = context.Seconds - start;
            load["loader"] = loader == null ? null : loader.Report;
            load["vanillaStarted"] = vanilla;
            if (viaContinue) st.Expect(LoadRouting.Routed == routed + 1 && LoadRouting.Ignored > ignored,
                "world " + key + " (" + how + "): the preload screen's per-frame calls started one load (" + (LoadRouting.Routed - routed) + " routed, " + (LoadRouting.Ignored - ignored) + " repeats ignored)");
            st.Expect(loader != null && loader.State == GeneratedWorldLoader.Phase.Loaded && !vanilla && (bool)load["routed"],
                "world " + key + " (" + how + "): the normal load entry routed the save to the world loader and the campaign is restored" + (loader == null ? "" : " (" + loader.State + (loader.Refusal != null ? ": " + loader.Refusal : loader.Error != null ? ": " + loader.Error : "") + ")"));
            if (loader == null || loader.State != GeneratedWorldLoader.Phase.Loaded) yield break;
            Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
            yield return Frames(20);
            var watched = ((IEnumerable)created["watchedHeroes"]).Cast<object>().Select(o => (string)o).ToList();
            var loaded = WorldSnapshot.Capture(prefix, watched);
            var all = WorldSnapshot.Compare(created["saved"], Normalize(loaded), 400);
            // An AI party that chose a new movement target in the frames between the load and this snapshot (its position unchanged, its AI
            // free to decide) is reported apart; anything else is a difference.
            var retargets = all.Where(d => AiRetarget(d, all)).ToList();
            var differences = all.Except(retargets).ToList();
            int compared = ((IDictionary)loaded["parties"]).Count;
            load["differences"] = differences; load["aiRetargets"] = retargets; load["comparedParties"] = compared;
            load["controlDrift"] = created.ContainsKey("controlDrift") ? created["controlDrift"] : null;
            st.Expect(differences.Count == 0 && retargets.Count <= Math.Max(2, compared / 50), "world " + key + " (" + how + "): the loaded campaign equals its save (time, parties and fleets, anchors, settlements, prisoners, relations, player)"
                + (differences.Count == 0 ? "" : ": " + String.Join("; ", differences.Take(5))) + " (" + retargets.Count + " AI retargets of " + compared + " parties)");
            var mine = WorldSnapshot.Objects(prefix); var theirs = WorldSnapshot.Objects(otherPrefix);
            load["objects"] = mine; load["otherWorldObjects"] = theirs;
            var scene = loader.Report.ContainsKey("scene") ? (string)loader.Report["scene"] : null;
            load["frame"] = PeriodicWorld.Current == null ? null : PeriodicWorld.Current.Coordinates.FrameIdentity;
            load["terrainContexts"] = GeneratedBattleTerrain.ContextSources.Keys.ToList();
            load["nonCanonical"] = PeriodicSeam.NonCanonicalSaved().Take(10).ToList();
            // A compiled world has its terrain context; a fixture world none.
            var contexts = Equals(created["contentId"], WorldBinding.NoContent) ? new string[0] : new[] { scene };
            load["expectedTerrainContexts"] = contexts.ToList();
            st.Expect(WorldSnapshot.Total(theirs) == 0 && WorldSnapshot.Total(mine) > 0 && Equals(load["frame"], created["frame"]) && GeneratedBattleTerrain.ContextSources.Keys.SequenceEqual(contexts),
                "world " + key + " (" + how + "): nothing of world " + other + " is registered, and the frame and terrain context are " + key + "'s only");
            st.Expect(Equals(loader.Report["generatorStarted"], false), "world " + key + " (" + how + "): no generator process started during the load");
            // After the load: the world runs on.
            if (key == "A") yield return AfterLoadA(context, st, created, load);
            else yield return AfterLoadB(context, st, created, load);
            context.Checkpoint();
        }

        // What decides whether campaign time runs now: the mode and its lock, the game state, an encounter, a menu, an inquiry, the periodic
        // patches, the camera's animation, the hostile parties beside the player.
        private static Dictionary<string, object> TimeState(double seconds)
        {
            var campaign = Campaign.Current; var main = MobileParty.MainParty;
            var map = Game.Current.GameStateManager.ActiveState as MapState;
            object animating = null;
            try { var view = PeriodicMapCameraView.Current; animating = view == null ? null : view.GetType().GetProperty("CameraAnimationInProgress", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).GetValue(view); }
            catch (Exception error) { animating = error.GetType().Name; }
            return new Dictionary<string, object> { ["t"] = Math.Round(seconds, 1), ["hours"] = CampaignTime.Now.ToHours, ["mode"] = campaign.TimeControlMode.ToString(), ["locked"] = campaign.TimeControlModeLock,
                ["state"] = Game.Current.GameStateManager.ActiveState == null ? null : Game.Current.GameStateManager.ActiveState.GetType().Name,
                ["menu"] = map == null || map.MenuContext == null || map.MenuContext.GameMenu == null ? null : map.MenuContext.GameMenu.StringId, ["encounter"] = PlayerEncounter.Current != null,
                ["mainMapEvent"] = main.MapEvent != null, ["inquiry"] = InformationManager.IsAnyInquiryActive(), ["periodicPatches"] = PeriodicPatches.IsApplied, ["periodicWorld"] = PeriodicWorld.Current != null,
                ["cameraAnimating"] = animating, ["main"] = main.Position.ToVec2().ToString(), ["mainMoving"] = main.DefaultBehavior.ToString(),
                ["hostileNear"] = MobileParty.All.Count(p => p.IsActive && p != main && p.MapFaction != null && p.MapFaction.IsAtWarWith(main.MapFaction) && p.Position.ToVec2().Distance(main.Position.ToVec2()) < 3f) };
        }

        // Leaves a game menu (an encounter's) for the map, where campaign time runs; the menus left are recorded.
        private static void ExitMenu(Dictionary<string, object> row)
        {
            var map = Game.Current == null ? null : Game.Current.GameStateManager.ActiveState as MapState;
            if (map == null || map.MenuContext == null || map.MenuContext.GameMenu == null) return;
            string id = map.MenuContext.GameMenu.StringId;
            try { TaleWorlds.CampaignSystem.GameMenus.GameMenu.ExitToLast(); }
            catch (Exception error) { id += " (" + error.GetType().Name + ")"; }
            var left = row.ContainsKey("menusLeft") ? (List<string>)row["menusLeft"] : (List<string>)(row["menusLeft"] = new List<string>());
            if (left.Count < 10) left.Add(id);
        }

        // "/parties/<id>/moveTarget: a -> b" of an active AI party free to decide whose position did not change.
        private static bool AiRetarget(string difference, List<string> all)
        {
            const string head = "/parties/";
            int end = difference.IndexOf("/moveTarget: ", StringComparison.Ordinal);
            if (!difference.StartsWith(head, StringComparison.Ordinal) || end < 0) return false;
            string id = difference.Substring(head.Length, end - head.Length);
            var party = MobileParty.All.FirstOrDefault(p => p.StringId == id);
            return party != null && party.IsActive && party != MobileParty.MainParty && party.Ai != null && !party.Ai.DoNotMakeNewDecisions
                && !all.Any(d => d.StartsWith(head + id + "/position", StringComparison.Ordinal));
        }

        // The JSON round trip of the create phase's snapshot (cold phase) turns lists into object arrays; the live one is compared the same way.
        private static object Normalize(Dictionary<string, object> snapshot)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            return json.DeserializeObject(json.Serialize(snapshot));
        }

        // World A after its load: first the pursuit from across the seam (one encounter, the one pursuer, no second crossing of the player's
        // party), then, with the encounter finished and the pursuer gone, the fleet called across the seam runs on to its port.
        private static IEnumerator AfterLoadA(ProbeContext context, State st, Dictionary<string, object> created, Dictionary<string, object> load)
        {
            var after = new Dictionary<string, object>(); load["after"] = after;
            var main = MobileParty.MainParty;
            // The fleet called across the seam, as the load left it (before any time runs).
            var moving = created.ContainsKey("movingFleet") ? MobileParty.All.FirstOrDefault(p => p.StringId == (string)created["movingFleet"]) : null;
            var target = moving == null || moving.Anchor == null ? CampaignVec2.Invalid : moving.Anchor.TargetPosition;
            after["target"] = target.IsValid() ? target.ToVec2().ToString() : "invalid"; after["movingAtLoad"] = moving != null && moving.Anchor != null && moving.Anchor.IsMovingToPoint;
            after["arrivalHoursAtLoad"] = moving == null || moving.Anchor == null ? (object)null : moving.Anchor.ArrivalTime.ToHours - CampaignTime.Now.ToHours;
            string pursuer = created.ContainsKey("pursuer") ? (string)created["pursuer"] : null;
            var embarked = created.ContainsKey("embarkedParty") ? MobileParty.All.FirstOrDefault(p => p.StringId == (string)created["embarkedParty"]) : null;
            after["embarkedAtSea"] = embarked != null && embarked.IsCurrentlyAtSea;
            int rebasesBefore = PeriodicSeam.Events.Count(e => e.Party == main.StringId);
            Campaign.Current.TimeControlMode = CampaignTimeControlMode.UnstoppablePlay;
            double start = context.Seconds;
            while (context.Seconds - start < 30 && PlayerEncounter.Current == null && main.MapEvent == null)
            {
                if (Campaign.Current.TimeControlMode == CampaignTimeControlMode.Stop && PlayerEncounter.Current == null) Campaign.Current.TimeControlMode = CampaignTimeControlMode.UnstoppablePlay;
                yield return null;
            }
            Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
            yield return Frames(5);
            after["encounter"] = PlayerEncounter.Current != null || main.MapEvent != null;
            after["pursuerCount"] = pursuer == null ? 0 : MobileParty.All.Count(p => p.StringId == pursuer);
            after["mapEventsWithPlayer"] = Campaign.Current.MapEventManager.MapEvents.Count(e => e.InvolvedParties.Any(p => p.MobileParty == main));
            after["playerRecrossings"] = PeriodicSeam.Events.Count(e => e.Party == main.StringId) - rebasesBefore;
            after["pursuitSeconds"] = context.Seconds - start;
            st.Expect(pursuer != null && (int)after["pursuerCount"] == 1 && (bool)after["encounter"] && (int)after["mapEventsWithPlayer"] <= 1 && (int)after["playerRecrossings"] == 0,
                "world A after load: the pursuit from across the seam ends in one encounter with the one pursuer, and the player's party does not cross the seam again");
            // Clear the encounter, remove the pursuer, and let the fleet run on.
            try { if (PlayerEncounter.Current != null) PlayerEncounter.Finish(true); } catch (Exception error) { after["finishError"] = error.Message; }
            var bandit = pursuer == null ? null : MobileParty.All.FirstOrDefault(p => p.StringId == pursuer);
            try { if (bandit != null && bandit.IsActive && bandit.MapEvent == null) DestroyPartyAction.Apply(null, bandit); } catch (Exception error) { after["destroyError"] = error.Message; }
            main.IgnoreByOtherPartiesTill(CampaignTime.HoursFromNow(24 * 30));
            // The encounter's menu keeps campaign time stopped (run 44940): back to the map.
            ExitMenu(after);
            yield return Frames(10);
            if (moving == null || moving.Anchor == null) { st.Expect(false, "world A after load: the moving fleet exists"); yield break; }
            Campaign.Current.TimeControlMode = CampaignTimeControlMode.UnstoppablePlay;
            Campaign.Current.SpeedUpMultiplier = 4f;
            start = context.Seconds;
            double fleetHours = CampaignTime.Now.ToHours, sampled = -10;
            var samples = new List<object>(); after["fleetSamples"] = samples;
            while (moving.Anchor.IsMovingToPoint && context.Seconds - start < 90)
            {
                try { if (PlayerEncounter.Current != null) PlayerEncounter.Finish(true); } catch (Exception error) { after["finishErrorInFleet"] = error.Message; }
                ExitMenu(after);
                if (context.Seconds - sampled >= 10) { sampled = context.Seconds; samples.Add(TimeState(context.Seconds - start)); }
                if (Campaign.Current.TimeControlMode == CampaignTimeControlMode.Stop) Campaign.Current.TimeControlMode = CampaignTimeControlMode.UnstoppablePlay;
                yield return null;
            }
            after["fleetHoursRun"] = CampaignTime.Now.ToHours - fleetHours;
            samples.Add(TimeState(context.Seconds - start));
            Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
            yield return Frames(5);
            after["arrived"] = !moving.Anchor.IsMovingToPoint; after["positionAfter"] = moving.Anchor.Position.IsValid() ? moving.Anchor.Position.ToVec2().ToString() : "invalid";
            after["fleetSeconds"] = context.Seconds - start;
            bool atTarget = target.IsValid() && moving.Anchor.Position.IsValid() && moving.Anchor.Position.ToVec2().Distance(target.ToVec2()) < 0.01f;
            st.Expect((bool)after["movingAtLoad"] && !moving.Anchor.IsMovingToPoint && atTarget,
                "world A after load: the fleet called across the seam kept its target and transit, and arrived at the port it was sent to (not snapped to the player)");
        }

        // World B (the fixture) after its load: campaign time runs, parties move, and its changes hold (the town the player took, the lord the
        // player holds); every position stays canonical.
        private static IEnumerator AfterLoadB(ProbeContext context, State st, Dictionary<string, object> created, Dictionary<string, object> load)
        {
            var after = new Dictionary<string, object>(); load["after"] = after;
            var positions = MobileParty.All.Where(p => p.IsActive && p != MobileParty.MainParty && p.CurrentSettlement == null && p.MapEvent == null)
                .OrderBy(p => p.StringId, StringComparer.Ordinal).Take(80).ToDictionary(p => p.StringId, p => p.Position.ToVec2());
            double hours = CampaignTime.Now.ToHours;
            Campaign.Current.TimeControlMode = CampaignTimeControlMode.UnstoppablePlay;
            double start = context.Seconds;
            while (context.Seconds - start < 8)
            {
                try { if (PlayerEncounter.Current != null) PlayerEncounter.Finish(true); } catch (Exception) { }
                if (Campaign.Current.TimeControlMode == CampaignTimeControlMode.Stop) Campaign.Current.TimeControlMode = CampaignTimeControlMode.UnstoppablePlay;
                yield return null;
            }
            Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
            after["hoursRun"] = CampaignTime.Now.ToHours - hours;
            after["partiesMoved"] = positions.Count(kv => { var p = MobileParty.All.FirstOrDefault(x => x.StringId == kv.Key); return p != null && p.IsActive && p.Position.ToVec2().Distance(kv.Value) > 0.01f; });
            var owned = created.ContainsKey("ownedTown") ? Settlement.Find((string)created["ownedTown"]) : null;
            after["ownedByPlayer"] = owned != null && owned.OwnerClan == Clan.PlayerClan;
            var prisoner = created.ContainsKey("prisoner") ? Hero.FindFirst(h => h.StringId == (string)created["prisoner"]) : null;
            after["prisonerHeld"] = prisoner != null && prisoner.IsPrisoner && prisoner.PartyBelongedToAsPrisoner == MobileParty.MainParty.Party;
            // While time runs, the per-tick pass keeps party positions and order targets canonical; the other saved fields (BehaviorTarget,
            // anchors, tracks, map events) are made canonical at the save (CanonicalizeForSave) and may sit in a strip between saves.
            after["nonCanonicalPartiesAfterRun"] = PeriodicSeam.NonCanonicalSaved().Where(f => !f.EndsWith(".BehaviorTarget", StringComparison.Ordinal) && !f.Contains(".Anchor.")
                && !f.StartsWith("Track[", StringComparison.Ordinal) && !f.StartsWith("MapEvent[", StringComparison.Ordinal)).Take(10).ToList();
            st.Expect((double)after["hoursRun"] > 0 && (int)after["partiesMoved"] > 0 && (bool)after["ownedByPlayer"] && (bool)after["prisonerHeld"] && ((List<string>)after["nonCanonicalPartiesAfterRun"]).Count == 0,
                "world B after load: campaign time runs and parties move; the town the player took is still the player's and the lord the player took is still held; party positions and targets canonical");
        }

        // ---------------------------------------------------------------- refusals

        // From inside the running world B: each refused before anything changed, B still running on its map.
        private static IEnumerator Refusals(ProbeContext context, State st)
        {
            var refusals = new Dictionary<string, object>(); context.Outputs["refusals"] = refusals;
            var running = Game.Current;
            refusals["fromRunningGame"] = running != null;
            var a = (Dictionary<string, object>)st.Worlds["A"];
            string source = BannerlordSaveCatalog.SavePath((string)a["save"]);
            var bytes = File.ReadAllBytes(source);
            string contentId = (string)a["contentId"];
            var fixtures = new List<KeyValuePair<string, Func<byte[]>>> {
                new KeyValuePair<string, Func<byte[]>>("torn", () => bytes.Take(bytes.Length - bytes.Length / 3).ToArray()),
                new KeyValuePair<string, Func<byte[]>>("missing-bundle", () => RewriteHeader(bytes, contentId, new string('e', 64))),
                new KeyValuePair<string, Func<byte[]>>("other-game-version", () => RewriteHeader(bytes, "\"mnbl.save.game\":\"v1.4.8.119303\"", "\"mnbl.save.game\":\"v9.9.9.999999\"")) };
            var expected = new Dictionary<string, string> { ["torn"] = LoadPolicy.DamagedSave, ["missing-bundle"] = LoadPolicy.MissingWorld, ["other-game-version"] = LoadPolicy.IncompatibleBuild };
            string packageDir = Path.Combine(GeneratedWorldPackage.Root, (string)a["package"]), bundleDir = Path.Combine(CompileStage.BundleRoot(), contentId);
            string worldBefore = TreeHash(packageDir) + TreeHash(bundleDir);
            foreach (var fixture in fixtures)
            {
                var row = new Dictionary<string, object>(); refusals[fixture.Key] = row;
                string name = "mnbl_probe49_refuse_" + fixture.Key + "_" + context.ProcessId;
                try { File.WriteAllBytes(BannerlordSaveCatalog.SavePath(name), fixture.Value()); }
                catch (Exception error) { row["error"] = error.Message; st.Expect(false, "refusal fixture " + fixture.Key + " written"); continue; }
                var info = MBSaveLoad.GetSaveFileWithName(name);
                bool vanilla = false, cancelled = false, gameChanged = false;
                if (info == null || info.MetaData == null) { row["listed"] = info != null; row["corrupted"] = info == null || info.IsCorrupted; }
                if (info != null && info.MetaData != null)
                {
                    var previousLoader = GeneratedWorldLoader.Current;
                    SandBox.SandBoxSaveHelper.TryLoadSave(info, r => vanilla = true, () => cancelled = true);
                    var loader = GeneratedWorldLoader.Current == previousLoader ? null : GeneratedWorldLoader.Current;
                    double start = context.Seconds;
                    while (loader != null && !loader.Done && context.Seconds - start < 120) { if (Game.Current != running) gameChanged = true; yield return null; }
                    yield return Frames(5);
                    if (loader != null) loader.Dismiss();
                    yield return Frames(5);
                    bool kept = Game.Current == running && running != null && Game.Current.GameStateManager.ActiveState is MapState;
                    row["state"] = loader == null ? null : loader.State.ToString(); row["code"] = loader == null || loader.Refusal == null ? null : loader.Refusal.Code;
                    row["detail"] = loader == null || loader.Refusal == null ? null : loader.Refusal.Detail; row["vanillaStarted"] = vanilla;
                    row["gameStarted"] = gameChanged; row["runningGameKept"] = kept && !gameChanged; row["cancelCalled"] = cancelled;
                    row["endedRunningGame"] = loader != null && loader.Report.ContainsKey("endedRunningGame");
                    row["phases"] = loader == null ? null : loader.Report["phases"];
                }
                else row["state"] = "not offered (the game lists the file as corrupted)";
                bool listedCorrupt = info == null || info.MetaData == null;
                bool ok = listedCorrupt ? fixture.Key == "torn" : (string)row["code"] == expected[fixture.Key] && !vanilla && Equals(row["runningGameKept"], true) && cancelled && Equals(row["endedRunningGame"], false);
                st.Expect(ok, "refused load " + fixture.Key + ": " + (listedCorrupt ? "the game itself lists the file as corrupted, so it is never offered"
                    : "refused (" + expected[fixture.Key] + ") from inside the running world before anything changed: it still runs on its map, the installed load never ran, the caller's cancel ran"));
                try { SaveBindingRuntime.Store.Saves.Delete(name); } catch (Exception) { }
            }
            string worldAfter = TreeHash(packageDir) + TreeHash(bundleDir);
            refusals["worldUnchanged"] = worldBefore == worldAfter;
            st.Expect(worldBefore == worldAfter, "the refused loads changed nothing in world A's package and bundle");
        }

        // Replaces a text in a save's header JSON, keeping the game data; the header length follows.
        private static byte[] RewriteHeader(byte[] save, string from, string to)
        {
            int length = BitConverter.ToInt32(save, 0);
            string json = new UTF8Encoding(false).GetString(save, 4, length);
            if (json.IndexOf(from, StringComparison.Ordinal) < 0) throw new InvalidOperationException("the header has no " + from);
            var header = new UTF8Encoding(false).GetBytes(json.Replace(from, to));
            var result = new byte[4 + header.Length + save.Length - 4 - length];
            BitConverter.GetBytes(header.Length).CopyTo(result, 0);
            header.CopyTo(result, 4);
            Array.Copy(save, 4 + length, result, 4 + header.Length, save.Length - 4 - length);
            return result;
        }

        // At the main menu, Continue on a save whose world's bundle is missing: refused once (the preload screen's repeated calls ignored), and
        // the refusal's OK returns from the preload state to the main menu.
        private static IEnumerator ContinueRefusal(ProbeContext context, State st)
        {
            var row = new Dictionary<string, object>(); context.Outputs["continueRefusal"] = row;
            var a = (Dictionary<string, object>)st.Worlds["A"];
            string name = "mnbl_probe49_refuse_continue_" + context.ProcessId;
            var bytes = File.ReadAllBytes(BannerlordSaveCatalog.SavePath((string)a["save"]));
            try { File.WriteAllBytes(BannerlordSaveCatalog.SavePath(name), RewriteHeader(bytes, (string)a["contentId"], new string('e', 64))); }
            catch (Exception error) { row["error"] = error.Message; st.Expect(false, "the Continue refusal's fixture is written"); yield break; }
            int routed = LoadRouting.Routed, ignored = LoadRouting.Ignored;
            var previousLoader = GeneratedWorldLoader.Current;
            var preload = GameStateManager.Current.CreateState<Sandbox.View.GameStates.PreloadState>(new object[] { name });
            GameStateManager.Current.PushState(preload, 0);
            double start = context.Seconds;
            while (GeneratedWorldLoader.Current == previousLoader && context.Seconds - start < 60) yield return null;
            var loader = GeneratedWorldLoader.Current == previousLoader ? null : GeneratedWorldLoader.Current;
            while (loader != null && !loader.Done && context.Seconds - start < 300) yield return null;
            yield return Frames(30);
            row["state"] = loader == null ? null : loader.State.ToString(); row["code"] = loader == null || loader.Refusal == null ? null : loader.Refusal.Code;
            row["routed"] = LoadRouting.Routed - routed; row["ignoredRepeats"] = LoadRouting.Ignored - ignored;
            row["preloadActiveBeforeOk"] = GameStateManager.Current.ActiveState == preload;
            if (loader != null) loader.Dismiss();
            yield return Frames(10);
            row["menuAfterOk"] = GameStateManager.Current.ActiveState is InitialState; row["gameStarted"] = Game.Current != null;
            st.Expect(loader != null && Equals(row["code"], LoadPolicy.MissingWorld) && (int)row["routed"] == 1 && (int)row["ignoredRepeats"] > 0 && Equals(row["preloadActiveBeforeOk"], true)
                && Equals(row["menuAfterOk"], true) && Game.Current == null,
                "refused Continue: one refusal (missing-world) for the preload screen's repeated calls; its OK returns to the main menu, no game started");
            try { SaveBindingRuntime.Store.Saves.Delete(name); } catch (Exception) { }
        }

        // Non-canonical values in the saved fields the per-tick pass does not cover, each at its canonical image plus the world's width; Check
        // reads them back after the save.
        private sealed class Planted
        {
            public readonly Dictionary<string, object> Row = new Dictionary<string, object>();
            public Func<CampaignVec2> Read;
            public float Expected;
            public void Check()
            {
                var v = Read(); var world = PeriodicWorld.Current;
                Row["after"] = v.IsValid() ? v.X.ToString("R", CultureInfo.InvariantCulture) : "invalid";
                Row["canonicalAfterSave"] = world != null && v.IsValid() && world.IsCanonical(v.X) && Math.Abs(v.X - Expected) < 0.01f;
            }
        }

        private static List<Planted> Plant()
        {
            var planted = new List<Planted>();
            var world = PeriodicWorld.Current;
            if (world == null) return planted;
            float width = world.Width;
            Action<string, string, CampaignVec2, Action<CampaignVec2>, Func<CampaignVec2>> plant = (field, owner, canonical, write, read) =>
            {
                // The image in the duplicate strip, on the map (a navmesh face), as a party crossing the seam leaves it; off the map only
                // when neither image is on it.
                var shifted = new[] { width, -width }.Select(dx => new CampaignVec2(new Vec2(canonical.X + dx, canonical.Y), canonical.IsOnLand)).FirstOrDefault(v => v.IsValid());
                if (!shifted.IsValid()) shifted = new CampaignVec2(new Vec2(canonical.X + width, canonical.Y), canonical.IsOnLand);
                write(shifted);
                var p = new Planted { Read = read, Expected = canonical.X };
                p.Row["field"] = field; p.Row["owner"] = owner; p.Row["canonical"] = canonical.X.ToString("R", CultureInfo.InvariantCulture); p.Row["planted"] = shifted.X.ToString("R", CultureInfo.InvariantCulture);
                p.Row["nonCanonicalPlanted"] = !world.IsCanonical(shifted.X); p.Row["plantedOnMap"] = shifted.IsValid();
                planted.Add(p);
            };
            var ai = MobileParty.All.Where(p => p.IsActive && p != MobileParty.MainParty && p.Ai != null && p.Position.IsValid() && world.IsCanonical(p.Position.X)).OrderBy(p => p.StringId, StringComparer.Ordinal).FirstOrDefault();
            var behaviorTarget = AccessTools.Field(typeof(MobilePartyAi), "BehaviorTarget");
            if (ai != null) plant("MobilePartyAi.BehaviorTarget", ai.StringId, ai.Position, v => behaviorTarget.SetValue(ai.Ai, v), () => (CampaignVec2)behaviorTarget.GetValue(ai.Ai));
            var anchored = MobileParty.All.Where(p => p.IsActive && p.Anchor != null && p.Position.IsValid() && world.IsCanonical(p.Position.X)).OrderBy(p => p.StringId, StringComparer.Ordinal).FirstOrDefault();
            var disembark = AccessTools.Field(typeof(AnchorPoint), "_lastUsedDisembarkPosition");
            if (anchored != null) plant("AnchorPoint._lastUsedDisembarkPosition", anchored.StringId, anchored.Position, v => disembark.SetValue(anchored.Anchor, v), () => (CampaignVec2)disembark.GetValue(anchored.Anchor));
            var tracks = Campaign.Current.GetCampaignBehavior<TaleWorlds.CampaignSystem.CampaignBehaviors.MapTracksCampaignBehavior>();
            var all = tracks == null ? null : AccessTools.Field(typeof(TaleWorlds.CampaignSystem.CampaignBehaviors.MapTracksCampaignBehavior), "_allTracks").GetValue(tracks) as IEnumerable<Track>;
            // A party's track, not the player's movement arrow (a pointer track the game releases when the order changes; run 11568).
            var track = all == null ? null : all.FirstOrDefault(t => t != null && !t.IsPointer && !t.IsExpired && t.Position.IsValid() && world.IsCanonical(t.Position.X));
            if (track != null) plant("Track.Position", "track", track.Position, v => track.Position = v, () => track.Position);
            var mapEvent = Campaign.Current.MapEventManager.MapEvents.FirstOrDefault(e => e != null && e.Position.IsValid() && world.IsCanonical(e.Position.X));
            var eventPosition = AccessTools.Field(typeof(TaleWorlds.CampaignSystem.MapEvents.MapEvent), "<Position>k__BackingField");
            if (mapEvent != null) plant("MapEvent.Position", "map event", mapEvent.Position, v => eventPosition.SetValue(mapEvent, v), () => mapEvent.Position);
            return planted;
        }

        // ---------------------------------------------------------------- cold-phase helpers

        // As a later generation job's sweep does: the world's installed scene folder and its derived reports go.
        private static void SweepScene(ProbeContext context, State st, string key)
        {
            var created = (Dictionary<string, object>)((Dictionary<string, object>)st.Worlds[key])["fromCreate"];
            var package = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(File.ReadAllText(Path.Combine(GeneratedWorldPackage.Root, (string)created["package"], "world.json")));
            string scene = (string)package["sceneName"];
            var removed = new List<string>();
            string folder = RuntimeWorldBackend.SceneFolder(scene);
            if (SceneNames.IsWorldScene(scene) && Directory.Exists(folder)) { Directory.Delete(folder, true); removed.Add("scene"); }
            foreach (var report in new[] { NavigationGate.ReportPath(SubModule.Store, scene), RuntimeWorldBackend.SettlementReportPath(scene), RuntimePlayabilityValidator.ReportPath(SubModule.Store, scene) })
                if (File.Exists(report)) { File.Delete(report); removed.Add(Path.GetFileName(Path.GetDirectoryName(report))); }
            context.Outputs["sweptA"] = new Dictionary<string, object> { ["scene"] = scene, ["removed"] = removed };
            st.Expect(!Directory.Exists(folder) && removed.Contains("scene"), "cold phase: world A's installed scene and derived reports were swept before its load");
        }

        private static void CleanUp(ProbeContext context, State st)
        {
            var deleted = new Dictionary<string, object>();
            // Saves earlier attempts of this probe left (deleteSaves=a,b,...).
            foreach (var name in context.Str("deleteSaves", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()))
            {
                try { deleted[name] = SaveBindingRuntime.Store.Saves.Read(name) == null || (SaveBindingRuntime.Store.Saves.Delete(name) && SaveBindingRuntime.Store.Saves.Read(name) == null); }
                catch (Exception error) { deleted[name] = error.Message; }
            }
            foreach (var kv in st.Worlds)
            {
                var created = (Dictionary<string, object>)((Dictionary<string, object>)kv.Value)["fromCreate"];
                foreach (var name in new[] { created["save"] as string, created["initialSave"] as string }.Where(n => n != null))
                {
                    try { deleted[name] = SaveBindingRuntime.Store.Saves.Delete(name) && SaveBindingRuntime.Store.Saves.Read(name) == null; }
                    catch (Exception error) { deleted[name] = error.Message; }
                }
            }
            context.Outputs["cleanup"] = deleted;
        }

        // ---------------------------------------------------------------- helpers

        // Ends a game that reached its map (ending one mid-load stops the engine); a game still loading is waited for first, up to an hour.
        private static IEnumerator EndGame(ProbeContext context)
        {
            if (Game.Current == null) yield break;
            double wait = context.Seconds;
            while (Game.Current != null && !(Game.Current.GameStateManager.ActiveState is MapState) && context.Seconds - wait < 3600) yield return null;
            if (Game.Current == null) yield break;
            try { InformationManager.HideInquiry(); } catch (Exception) { }
            MBGameManager.EndGame();
            double start = context.Seconds;
            while ((Game.Current != null || MBGameManager.Current != null) && context.Seconds - start < 120) yield return null;
            yield return Frames(30);
        }

        private static void Step(Dictionary<string, object> steps, string name, Func<object> step)
        {
            try { steps[name] = step(); }
            catch (Exception error) { steps[name] = new Dictionary<string, object> { ["error"] = error.GetType().Name + ": " + error.Message }; }
        }

        private static HashSet<string> LandlockedClans()
        {
            var manager = Game.Current.GameManager as CampaignRegistry.GeneratedGameManager;
            var plan = manager == null || manager.World == null ? null : manager.World.NavalPlan;
            return new HashSet<string>(plan == null ? Enumerable.Empty<string>() : plan.Landlocked, StringComparer.Ordinal);
        }

        // Lord parties of seafaring clans, idle on land.
        private static IEnumerable<MobileParty> Lords(HashSet<string> landlocked)
        {
            return MobileParty.All.Where(p => p.IsActive && p.IsLordParty && p.LeaderHero != null && p.LeaderHero.Clan != null && p.LeaderHero.Clan.Kingdom != null && p.LeaderHero.Clan != Clan.PlayerClan
                && !landlocked.Contains(p.LeaderHero.Clan.StringId) && p.MapEvent == null && p.Army == null && !p.IsCurrentlyAtSea && p.CurrentSettlement == null && p.BesiegedSettlement == null)
                .OrderBy(p => p.StringId, StringComparer.Ordinal);
        }

        private static void Grant(MobileParty party)
        {
            var hull = MBObjectManager.Instance.GetObject<ShipHull>("northern_trade_ship");
            if (hull == null) throw new InvalidOperationException("the installed hull northern_trade_ship is missing");
            ChangeShipOwnerAction.ApplyByTransferring(party.Party, new Ship(hull));
        }

        private static List<string> ShipsOf(MobileParty party) { return party.Ships.Select(s => s.ShipHull.StringId + ":" + s.HitPoints.ToString("R", CultureInfo.InvariantCulture) + "/" + s.MaxHitPoints.ToString("R", CultureInfo.InvariantCulture)).ToList(); }

        private static CampaignVec2 NavalPoint(CampaignVec2 near)
        {
            var point = NavigationHelper.FindReachablePointAroundPosition(new CampaignVec2(near.ToVec2(), false), MobileParty.NavigationType.Naval, 3f, 0.5f, false);
            return point.IsValid() ? point : new CampaignVec2(near.ToVec2(), false);
        }

        private static string TreeHash(string root)
        {
            if (!Directory.Exists(root)) return "absent";
            var text = new StringBuilder();
            foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)) text.Append(f.Substring(root.Length)).Append('=').Append(WorldSaveTransfer.Sha256File(f)).Append('\n');
            using (var sha = System.Security.Cryptography.SHA256.Create()) return String.Concat(sha.ComputeHash(new UTF8Encoding(false).GetBytes(text.ToString())).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static IEnumerator Frames(int count) { for (int i = 0; i < count; i++) yield return null; }
    }
}
