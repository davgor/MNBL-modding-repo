using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MNBL.ProceduralWorld.CampaignBootstrap;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Settlements;
using MNBL.ProceduralWorld.Surface;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-34: every patch of installed code, in one place (docs/campaign-behavior-compatibility.md, "Patches"). Each one names its owner
    // ticket, its purpose and who applies and removes it, and is used only where the installed code offers no supported extension point.
    // MMR-284: no patch is pinned to a game build. Each locates its target by signature (PatchSignatures) or IL content, and is refused,
    // named, when the installed member no longer matches (PatchSignatureException); RequireSupportedBuild only refuses a game or War Sails
    // below this release's minimum (SupportedBuilds; no maximum). Verify checks every target without patching, for the setup screen and the
    // loader. Bannerlord.Harmony is a declared module dependency (docs/decisions/002), never copied.
    internal static class GeneratedPatches
    {
        public const string StartupId = "mnbl.proceduralworld.startup";
        public const string WorldId = "mnbl.proceduralworld.world";

        public sealed class Descriptor { public string Id, Target, Kind, Owner, Purpose, Lifetime; }

        private const string PeriodicLifetime = "applied when a periodic generated map scene loads (GeneratedMapScene.Load, PeriodicPatches.Apply), removed when it is destroyed (GeneratedMapScene.Destroy); inert without an active periodic world";

        public static readonly IList<Descriptor> Manifest = Array.AsReadOnly(new[] {
            new Descriptor { Id = GeneratedColorGrade.HarmonyId, Kind = "postfix", Owner = "MMR-39",
                Target = GeneratedColorGrade.ManagerType + ".Init",
                Purpose = "the map's colour-grade manager reads Calradia's region grid (the worldmap_colorgrade_grid texture, 512 x 512 grade bytes positioned " +
                          "by the terrain extent) into its private grid; no model or event supplies it, so the postfix copies the generated world's grid over it",
                Lifetime = "applied when a generated map scene with a generated colour grade loads (before the scene read; GeneratedMapScene.Load), removed when it is destroyed" },
            new Descriptor { Id = GeneratedCharacterCreation.HarmonyId, Kind = "prefix", Owner = "MMR-32",
                Target = "TaleWorlds.CampaignSystem.ViewModelCollection.CharacterCreation.CharacterCreationCultureStageVM.SortCultureList",
                Purpose = "the installed sort places the six vanilla cultures with Enumerable.Single and throws when the world presents through fewer",
                Lifetime = "applied when character creation initializes with a restricted culture list, removed when it finalizes (GeneratedCharacterCreation) or the game ends" },
            new Descriptor { Id = StartupId, Kind = "prefix", Owner = "MMR-34",
                Target = "TaleWorlds.CampaignSystem.CampaignBehaviors.NotablesCampaignBehavior.SpawnNotablesAtGameStart",
                Purpose = "the game-start spawn calls HeroCreator.CreateNotable once per target from random templates and ignores the world's MMR-32 notable " +
                          "intents; the prefix creates the intents instead (no model or event lets a world supply its own notables); daily replenishment is untouched",
                Lifetime = "applied when a new game with a startup plan starts (GeneratedStartupBehavior.Activate), removed after the new game's follow-ups or when the game ends" },
            new Descriptor { Id = StartupId, Kind = "prefix", Owner = "MMR-34",
                Target = "TaleWorlds.CampaignSystem.CampaignBehaviors.CompanionsCampaignBehavior.OnNewGameCreated",
                Purpose = "the game-start loop creates 0.6 x towns companions from random templates in random towns and ignores the world's MMR-32 companion " +
                          "intents; the prefix initializes the template list as the installed handler does and creates the intents instead",
                Lifetime = "as above" },
            new Descriptor { Id = MNBL.ProceduralWorld.Generation.ActivationSaveBlock.HarmonyId, Kind = "prefix", Owner = "MMR-46",
                Target = "TaleWorlds.CampaignSystem.SaveHandler.SaveAs, QuickSaveCurrentGame, SignalAutoSave, ForceAutoSave",
                Purpose = "no save of a campaign whose activation has not committed (MMR-45's rule that no partial world becomes selectable as a save): the sandbox's " +
                          "save manager can turn autosave off but not the escape menu's or quick save; the prefixes refuse every save but the activation's reserved initial save",
                Lifetime = "applied when the activated campaign's map runs (GameActivationHost.StartCampaign), removed at the commit, on rollback, or when the game ends (GeneratedGameManager.OnGameEnd)" },
            new Descriptor { Id = MNBL.ProceduralWorld.Persistence.WorldSaveBinding.HarmonyId, Kind = "prefix", Owner = "MMR-48",
                Target = "TaleWorlds.Core.MBSaveLoad.OverwriteSaveAux",
                Purpose = "observe only: every save mode (Save As, quick save, autosave, Ironman) passes here with its final name before the file is written, and no event carries " +
                          "the name of a quick save or an autosave before the write (OnBeforeSave and the metadata event run before MBSaveLoad picks it); the prefix writes the " +
                          "save's journal intent (SaveJournal.Begin) and the save runs unchanged",
                Lifetime = "applied when a generated campaign starts or loads (GeneratedGameManager.OnGameStart, with the world identity behaviour), removed when it ends (OnGameEnd)" },
            new Descriptor { Id = MNBL.ProceduralWorld.Persistence.LoadRouting.HarmonyId, Kind = "prefix", Owner = "MMR-49",
                Target = "SandBox.SandBoxSaveHelper.LoadGameAction, SandBox.SandBoxSaveHelper.TryLoadSave",
                Purpose = "the load screen, Continue and the preload screen all start a save through TryLoadSave, whose checks and prompts end in LoadGameAction, which deserializes " +
                          "the save and starts the vanilla SandBoxGameManager(LoadResult) that would read the official world's XML onto a generated save; the LoadGameAction prefix takes a " +
                          "save whose header carries a world binding to GeneratedWorldLoader (the world restored before deserialization), the TryLoadSave prefix ignores the preload " +
                          "screen's repeated calls for it, and every other save loads as installed",
                Lifetime = "applied once per process at the main menu (SubModule.OnBeforeInitialModuleScreenSetAsRoot), never removed" },
            new Descriptor { Id = MNBL.ProceduralWorld.Persistence.LoadRouting.GuardId, Kind = "prefix", Owner = "MMR-49",
                Target = "TaleWorlds.Core.MBSaveLoad.LoadSaveGameData",
                Purpose = "version-agnostic backstop: a generated save reaching the deserializer by any path but the world loader (the routing refused by a changed patch signature, " +
                          "another mod's load) is refused with a message instead of being read onto the official world; every other save is unchanged",
                Lifetime = "applied once per process at the main menu before the routing (SubModule.OnBeforeInitialModuleScreenSetAsRoot), never removed" },
            new Descriptor { Id = WorldId, Kind = "postfix", Owner = "MMR-42",
                Target = "TaleWorlds.CampaignSystem.GameComponents.DefaultSceneModel.GetBattleSceneForMapPatch",
                Purpose = "observe only: records the battle scene the model picked on a generated map against the generated patch (MissionScenePatches, " +
                          "GeneratedEncounterResolver.Check) so a missing battle map, a non-deterministic patch or another scene is never silent; the result is unchanged",
                Lifetime = "applied with the world patches (GeneratedGameManager.OnGameStart), removed when the generated campaign ends" },
            new Descriptor { Id = WorldId, Kind = "transpiler", Owner = "MMR-34",
                Target = "TaleWorlds.CampaignSystem.CampaignBehaviors.LordConversationsCampaignBehavior.conversation_wanderer_introduction_on_condition",
                Purpose = "sets IMPERIALCAPITAL from Settlement.FindFirst(s => s.StringId == \"town_ES4\").Name without a null check, so every wanderer " +
                          "introduction throws on a generated world; the one FindFirst call becomes FindFirstOrGenerated (the installed result, else the generated " +
                          "empire-culture town with the highest prosperity, else the highest-prosperity town)",
                Lifetime = "applied when a generated campaign starts or loads (GeneratedGameManager.OnGameStart), removed when it ends (OnGameEnd)" },
            new Descriptor { Id = WorldId, Kind = "transpiler", Owner = "MMR-287",
                Target = IncidentCulturePatch.BehaviourType + ": the trigger conditions of incident_vedmaks_treasure, incident_the_deer_departed, " +
                         "incident_kannic_splendors and incident_solider_is_wanted_criminal (IncidentCulturePatch: each condition is the first delegate after " +
                         "its incident's ldstr id, and its comparisons are IFaction.get_StringId directly followed by an ldstr kingdom literal and " +
                         "String.op_Equality; never a compiler-generated lambda name or a build string)",
                Purpose = "the four conditions compare the last-visited settlement's MapFaction.StringId with vanilla kingdom ids (sturgia, khuzait, aserai, " +
                          "empire / vlandia), which a generated kingdom never has, so the incidents never fire (RT-29); each comparison becomes one of the " +
                          "faction's culture id (IncidentCulturePatch.FactionCultureId). A condition whose comparisons are not exactly the expected literals is " +
                          "left as installed and logged: only that incident stays off",
                Lifetime = "applied with the world patches (GeneratedGameManager.OnGameStart), removed when the generated campaign ends (OnGameEnd)" },
            new Descriptor { Id = WorldId, Kind = "prefix", Owner = "MMR-281",
                Target = CompanionsTickPatch.BehaviourType + ": its daily tick (CompanionsTickPatch: the listener RegisterEvents passes to CampaignEvents.DailyTickEvent, an " +
                         "instance void() method whose body calls exactly a swap (LeaveSettlementAction and EnterSettlementAction.ApplyForCharacterOnly), a kill " +
                         "(KillCharacterAction.ApplyByRemove) and a spawn (HeroCreator.CreateSpecialHero through its own method); AdjustEquipments located as the " +
                         "instance void(Hero) method reading Hero.BattleEquipment and CivilianEquipment; never a build string; get__desiredTotalCompanionCount is never patched)",
                Purpose = "the installed tick kills an unhired wanderer at 10% a day (TryKillCompanion) and spawns from random templates (many naming Calradian places) " +
                          "against 0.6 x towns counted as templates; on a generated world with a population plan the prefix runs the swap as installed and then " +
                          "GeneratedPopulationBehavior's wanderer tick (WandererRules: rotation of unmet, unhired wanderers at the same daily chance without a death " +
                          "notice, a top-up below W heroes, FWGC++-style wanderers made in the installed four steps from the backstory-safe pool; RT-04, RT-33). " +
                          "Unresolved: left as installed and logged; a world without a population plan runs the installed tick",
                Lifetime = "applied with the world patches (GeneratedGameManager.OnGameStart), removed when the generated campaign ends (OnGameEnd); its handler is set " +
                           "by the campaign's GeneratedPopulationBehavior and cleared when that campaign ends" },
            new Descriptor { Id = PeriodicPatches.HarmonyId, Kind = "postfix", Owner = "MMR-63",
                Target = "TaleWorlds.CampaignSystem.Party.MobileParty.FindNextLocatable, TaleWorlds.CampaignSystem.Settlements.Settlement.FindNextLocatable",
                Purpose = "a proximity search whose circle crosses the x seam continues at the query position's image, where the engine's planar filter finds the canonical objects",
                Lifetime = PeriodicLifetime },
            new Descriptor { Id = PeriodicPatches.HarmonyId, Kind = "transpiler", Owner = "MMR-63",
                Target = "TaleWorlds.CampaignSystem.Party.PartyBase.CanPartyInteract, PartyBase.CalculateVisibilityRangeOfMapPoint, Helpers.MenuHelper.EncounterAttackConsequence, " +
                         "TaleWorlds.CampaignSystem.MapEvents.MapEvent.GetEventDirection",
                Purpose = "encounter range, visibility range, encounter direction and map-event direction each take one planar Vec2 difference; it becomes the periodic difference",
                Lifetime = PeriodicLifetime },
            new Descriptor { Id = PeriodicPatches.HarmonyId, Kind = "transpiler", Owner = "MMR-63",
                Target = "TaleWorlds.CampaignSystem.GameComponents.DefaultMapDistanceModel.GetDistance, NavalDLC.GameComponents.NavalDLCMapDistanceModel.GetDistance",
                Purpose = "the party legs (party to settlement, party to party, party to point) are planar CampaignVec2.Distance calls; they become periodic distances",
                Lifetime = PeriodicLifetime },
            new Descriptor { Id = PeriodicPatches.HarmonyId, Kind = "postfix", Owner = "MMR-63",
                Target = "Helpers.NavigationHelper.FindReachablePointAroundPosition",
                Purpose = "a random reachable point near the seam can fall in a duplicate strip; the postfix returns its canonical image",
                Lifetime = PeriodicLifetime },
            new Descriptor { Id = PeriodicPatches.HarmonyId, Kind = "prefix, transpiler", Owner = "MMR-63",
                Target = "TaleWorlds.CampaignSystem.GameState.MapState.ProcessTravel",
                Purpose = "a click on a duplicate strip carries display coordinates; the prefix translates them to the canonical point and the click direction is taken periodically",
                Lifetime = PeriodicLifetime },
            new Descriptor { Id = PeriodicPatches.HarmonyId, Kind = "transpiler", Owner = "MMR-64",
                Target = "TaleWorlds.CampaignSystem.Map.DistanceCache.SandBoxNavigationCache.GetClosestSettlementsToPositionInCache (its two ordering lambdas)",
                Purpose = "the closest settlement search (the per-face cache behind GetClosestEntranceToFace, and the neighbour check) takes candidates in planar " +
                          "squared-distance order, which sets its search limits and exact-tie winner; the order becomes the wrapped geometric distance " +
                          "(every candidate's path distance is still evaluated: both installed callers pass useEarlyOut = false)",
                Lifetime = PeriodicLifetime },
            new Descriptor { Id = PeriodicPatches.HarmonyId, Kind = "transpiler", Owner = "MMR-64",
                Target = "Helpers.DistanceHelper.FindClosestDistanceFromMobilePartyToMobileParty, Helpers.DistanceHelper.GetDistanceBetweenMobilePartyToMobileParty, " +
                         "Helpers.SettlementHelper.FindNearestSettlementToPoint",
                Purpose = "the installed distance helpers measure planar distances (the party-to-party bird-fly test and the straight legs of its entrance formula, the " +
                          "nearest settlement to a point); they become periodic (PeriodicDistanceModel.HelperCoverage lists every planar call in both helpers)",
                Lifetime = PeriodicLifetime },
            new Descriptor { Id = PeriodicPatches.HarmonyId, Kind = "postfix", Owner = "MMR-65",
                Target = "Helpers.NavigationHelper.FindPointAroundPosition, Helpers.NavigationHelper.FindPointInsideArea (both overloads)",
                Purpose = "random points for bandit, pirate and fishing spawns, naval patrol points and deserter positions can fall in a duplicate strip; the postfix returns their canonical image",
                Lifetime = PeriodicLifetime },
            new Descriptor { Id = PeriodicPatches.HarmonyId, Kind = "transpiler", Owner = "MMR-65",
                Target = "the campaign methods of PeriodicSpatialTable (generated from the audit of the installed assemblies; evidence/world/periodic-consumers.tsv)",
                Purpose = "AI, movement, armies, flee, tracks, spawns, quests and War Sails behaviours measure and difference positions in planar terms, and three methods search the " +
                          "LocatorGrid directly; between two campaign positions the measures become periodic, and the direct searches continue at the seam image. The calls found " +
                          "must equal the table's counts, else the periodic set is refused",
                Lifetime = PeriodicLifetime } });

        // The running game and War Sails versions (diagnostics, the navigation cache key and the startup audit).
        public static string InstalledVersions()
        {
            string game = TaleWorlds.Engine.Utilities.GetApplicationVersionWithBuildNumber().ToString();
            var naval = TaleWorlds.ModuleManager.ModuleHelper.GetModuleInfo("NavalDLC");
            return game + " / NavalDLC " + (naval == null ? "absent" : naval.Version.ToString());
        }

        // MMR-284: a game or War Sails below this release's minimum refuses the patch; any later build is accepted here (its targets are
        // checked by signature where they are located).
        public static void RequireSupportedBuild(string patch)
        {
            string game = TaleWorlds.Engine.Utilities.GetApplicationVersionWithBuildNumber().ToString();
            var naval = TaleWorlds.ModuleManager.ModuleHelper.GetModuleInfo("NavalDLC");
            if (!SupportedBuilds.Game(game) || (naval != null && !SupportedBuilds.Naval(naval.Version.ToString())))
                throw new InvalidOperationException("The " + patch + " patches need Bannerlord " + SupportedBuilds.MinimumGame + " and War Sails " + SupportedBuilds.MinimumNaval + " or later; the running game is " + InstalledVersions() + ".");
        }

        // MMR-284: every patch target of this module against the installed game, without patching: each recorded signature (PatchSignatures)
        // and the wanderer introduction's one Settlement.FindFirst call (its transpiler's IL content). Empty when every patch can be applied;
        // the MMR-281 and MMR-287 patches are not listed (located by IL content, an unresolved one is left as installed and logged).
        public static List<PatchSignatureException> Verify()
        {
            var failures = PatchSignatures.Verify();
            if (failures.All(f => f.Patch != PatchSignatures.WandererIntroduction))
            {
                try
                {
                    var target = WandererIntroduction; var findFirst = InstalledFindFirst;
                    int calls = PatchProcessor.GetOriginalInstructions(target).Count(i => i.Calls(findFirst));
                    if (calls != 1) failures.Add(new PatchSignatureException(PatchSignatures.WandererIntroduction, target.DeclaringType.FullName + "::" + target.Name, calls + " Settlement.FindFirst calls, not one"));
                }
                catch (Exception error) when (!(error is PatchSignatureException))
                {
                    failures.Add(new PatchSignatureException(PatchSignatures.WandererIntroduction, "LordConversationsCampaignBehavior::conversation_wanderer_introduction_on_condition", "its IL cannot be read: " + error.Message));
                }
            }
            return failures;
        }

        private static Harmony startup, world;
        private static int worldReplaced;
        public static bool StartupApplied { get { return startup != null; } }
        public static bool WorldApplied { get { return world != null; } }

        public static void ApplyStartup(Action<string> trace)
        {
            if (startup != null) return;
            RequireSupportedBuild("startup");
            var notables = PatchSignatures.Method(typeof(NotablesCampaignBehavior).FullName, "SpawnNotablesAtGameStart");
            var companions = PatchSignatures.Method(typeof(CompanionsCampaignBehavior).FullName, "OnNewGameCreated");
            startup = new Harmony(StartupId);
            startup.Patch(notables, prefix: new HarmonyMethod(typeof(GeneratedStartupBehavior), nameof(GeneratedStartupBehavior.NotablesPrefix)));
            startup.Patch(companions, prefix: new HarmonyMethod(typeof(GeneratedStartupBehavior), nameof(GeneratedStartupBehavior.CompanionsPrefix)));
            trace("startup_patches_applied");
        }

        public static void RemoveStartup(Action<string> trace)
        {
            if (startup == null) return;
            startup.UnpatchAll(StartupId); startup = null;
            trace("startup_patches_removed");
        }

        // Located by signature (PatchSignatures); each throws PatchSignatureException when the installed member differs.
        public static MethodInfo WandererIntroduction { get { return PatchSignatures.Method(typeof(LordConversationsCampaignBehavior).FullName, "conversation_wanderer_introduction_on_condition"); } }
        public static MethodInfo InstalledFindFirst { get { return PatchSignatures.Method(typeof(Settlement).FullName, nameof(Settlement.FindFirst)); } }
        public static MethodInfo FindFirstOrGeneratedMethod { get { return AccessTools.Method(typeof(GeneratedPatches), nameof(FindFirstOrGenerated)); } }
        public static MethodInfo BattleSceneForMapPatch { get { return PatchSignatures.Method(typeof(TaleWorlds.CampaignSystem.GameComponents.DefaultSceneModel).FullName, "GetBattleSceneForMapPatch"); } }

        // The world-lifetime patches of a generated campaign (new game or load).
        public static void ApplyWorld(Action<string> trace)
        {
            if (world != null) return;
            RequireSupportedBuild("world");
            var target = WandererIntroduction;
            var findFirst = InstalledFindFirst;   // the transpiler's operand, checked by signature before anything is patched
            int before = worldReplaced;
            world = new Harmony(WorldId);
            world.Patch(target, transpiler: new HarmonyMethod(typeof(GeneratedPatches), nameof(WorldTranspiler)));
            if (worldReplaced - before != 1)
            {
                world.UnpatchAll(WorldId); world = null;
                throw new PatchSignatureException(PatchSignatures.WandererIntroduction, target.DeclaringType.FullName + "::" + target.Name, (worldReplaced - before) + " Settlement.FindFirst calls, not one");
            }
            // MMR-42: the observe-only battle scene guard.
            // All or nothing: a failure here removes the transpiler above too.
            try
            {
                var scene = BattleSceneForMapPatch;
                MNBL.ProceduralWorld.Missions.MissionScenePatches.Clear();
                world.Patch(scene, postfix: new HarmonyMethod(typeof(MNBL.ProceduralWorld.Missions.MissionScenePatches), nameof(MNBL.ProceduralWorld.Missions.MissionScenePatches.BattleScenePostfix)));
            }
            catch (Exception) { world.UnpatchAll(WorldId); world = null; throw; }
            // MMR-287: the incident conditions, located by IL content; an unresolved one only leaves its incident off (never throws).
            IncidentCulturePatch.Apply(world, trace);
            // MMR-281: the wanderer tick, located by IL content; an unresolved one leaves the installed tick (never throws).
            CompanionsTickPatch.Apply(world, trace);
            trace("world_patches_applied");
        }

        public static void RemoveWorld(Action<string> trace)
        {
            if (world == null) return;
            world.UnpatchAll(WorldId); world = null;
            CompanionsTickPatch.Removed();
            trace("world_patches_removed");
        }

        private static IEnumerable<CodeInstruction> WorldTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var findFirst = InstalledFindFirst; var helper = FindFirstOrGeneratedMethod;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(findFirst)) { instruction.operand = helper; worldReplaced++; }
                yield return instruction;
            }
        }

        // The wanderer introduction's capital: the installed lookup's result when it has one; a generated world has no town_ES4, so the
        // generated town of the empire culture with the highest prosperity, else the town with the highest prosperity (ties by id).
        public static Settlement FindFirstOrGenerated(Func<Settlement, bool> predicate)
        {
            var found = Settlement.FindFirst(predicate);
            if (found != null) return found;
            var towns = Settlement.All.Where(s => s.IsTown && s.Town != null).ToList();
            var empire = towns.Where(s => s.Culture != null && s.Culture.StringId == "empire").ToList();
            return (empire.Count > 0 ? empire : towns).OrderByDescending(s => s.Town.Prosperity).ThenBy(s => s.StringId, StringComparer.Ordinal).FirstOrDefault();
        }

        // The installed methods currently patched by this module, with the patch owners: the startup audit reads it.
        public static List<string> Patched()
        {
            var owners = new HashSet<string>(Manifest.Select(d => d.Id), StringComparer.Ordinal);
            var list = new List<string>();
            foreach (var method in Harmony.GetAllPatchedMethods())
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                foreach (var owner in info.Owners.Where(owners.Contains)) list.Add(owner + ":" + method.DeclaringType.FullName + "." + method.Name);
            }
            return list.OrderBy(x => x, StringComparer.Ordinal).ToList();
        }
    }
}
