using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-34: instantiates a generated world's runtime plan at new game (docs/campaign-behavior-compatibility.md, docs/heroes.md):
    //   * the MMR-32 notable intents, in place of NotablesCampaignBehavior's game-start spawn: HeroCreator.CreateSpecialHero(template,
    //     home, null, null, age) and SetName; NotablesCampaignBehavior.OnHeroCreated then places each notable in its home, as for its own;
    //   * the MMR-32 companion intents, in place of CompanionsCampaignBehavior's game-start loop: the steps of the installed
    //     CreateCompanionAndAddToSettlement (CreateSpecialHero, AdjustEquipments, ChangeState Active, EnterSettlementAction) with the
    //     intent's template, home and age, and SetName;
    //   * the MMR-31 ruler relations (CharacterRelationManager.SetHeroRelation) once every new-game follow-up has run;
    //   * the intents' names, restored at that point: InitializeWorkshopAction.ApplyByNewGame regenerates every new-game workshop
    //     owner's whole name (NameGenerator.GenerateHeroNameAndHeroFullName), which would discard the world's source names (run 37940).
    //     Later workshop changes in play retitle notables as the installed game does.
    // The engine mints each runtime hero's StringId, so the intent id is the hero's stable identity: this behaviour keeps the map from
    // intent id to hero and saves it with the campaign. A template or home the world does not have is a recorded problem naming the
    // intent (the startup audit fails on it, and it is traced), never a silent fallback. Construction randomness is the plan's; the engine
    // still draws what the intents leave to HeroCreator (bodies within the template, birthdays) from the campaign RNG. The active startup
    // belongs to the campaign it was activated in: a prefix reached from any other campaign (a new game that ended without OnGameEnd)
    // removes the patches and lets the installed code run.
    internal sealed class GeneratedStartupBehavior : CampaignBehaviorBase
    {
        internal static GeneratedStartupBehavior Active { get; private set; }
        private Campaign campaign;
        private readonly Func<GeneratedCampaignInitializer.StartupPlan> planSource;
        private readonly Action<string> trace;
        private List<string> intentIds = new List<string>();
        private List<Hero> intentHeroes = new List<Hero>();
        public List<string> Problems { get; private set; }
        public int NotablesCreated { get; private set; }
        public int CompanionsCreated { get; private set; }
        public int RelationsApplied { get; private set; }
        public int NamesRestored { get; private set; }
        public bool NotablesReplaced { get; private set; }
        public bool CompanionsReplaced { get; private set; }

        // planSource: the world's runtime plan, read when the new game creates its heroes (the world registers after OnGameStart).
        public GeneratedStartupBehavior(Func<GeneratedCampaignInitializer.StartupPlan> planSource, Action<string> trace)
        {
            this.planSource = planSource; this.trace = trace ?? (s => { }); Problems = new List<string>();
        }

        public IDictionary<string, Hero> Map
        {
            get { var map = new Dictionary<string, Hero>(StringComparer.Ordinal); for (int i = 0; i < Math.Min(intentIds.Count, intentHeroes.Count); i++) map[intentIds[i]] = intentHeroes[i]; return map; }
        }

        // A new game with a plan: the startup patches replace the installed game-start spawns until the new game's follow-ups end.
        public void Activate()
        {
            campaign = Campaign.Current;
            Active = this;
            GeneratedPatches.ApplyStartup(trace);
        }

        // The active startup of the current campaign; a stale one (another campaign) is deactivated.
        private static GeneratedStartupBehavior Current()
        {
            var active = Active;
            if (active == null) return null;
            if (active.campaign != null && ReferenceEquals(active.campaign, Campaign.Current)) return active;
            active.trace("startup_stale_active_deactivated");
            Deactivate(active.trace);
            return null;
        }

        public static void Deactivate(Action<string> trace)
        {
            Active = null;
            GeneratedPatches.RemoveStartup(trace ?? (s => { }));
        }

        public override void RegisterEvents()
        {
            CampaignEvents.OnNewGameCreatedPartialFollowUpEndEvent.AddNonSerializedListener(this, OnNewGameFollowUpEnd);
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("_mnblStartupIntentIds", ref intentIds);
            dataStore.SyncData("_mnblStartupIntentHeroes", ref intentHeroes);
        }

        // The prefixes and the follow-up handler run inside the engine's new-game dispatch: an exception is recorded as a problem (the
        // startup audit fails on it), never thrown into the dispatch.
        internal static bool NotablesPrefix()
        {
            var active = Current();
            if (active == null) return true;
            try { active.CreateHeroes(false, null); }
            catch (Exception error) { active.Problems.Add("notable intents: " + error); }
            return false;
        }

        internal static bool CompanionsPrefix(object __instance)
        {
            var active = Current();
            if (active == null) return true;
            var initialize = AccessTools.Method(__instance.GetType(), "InitializeCompanionTemplateList");
            if (initialize == null) { active.Problems.Add("CompanionsCampaignBehavior.InitializeCompanionTemplateList is not the installed method"); return true; }
            try { initialize.Invoke(__instance, null); active.CreateHeroes(true, __instance); }
            catch (Exception error) { active.Problems.Add("companion intents: " + error); }
            return false;
        }

        private void CreateHeroes(bool companions, object companionsBehavior)
        {
            var plan = planSource();
            if (plan == null) { Problems.Add("the world supplied no startup plan"); return; }
            var adjust = companions ? AccessTools.Method(companionsBehavior.GetType(), "AdjustEquipments") : null;
            if (companions && adjust == null) Problems.Add("CompanionsCampaignBehavior.AdjustEquipments is not the installed method");
            foreach (var intent in companions ? plan.Companions : plan.Notables)
            {
                var template = MBObjectManager.Instance.GetObject<CharacterObject>(intent.Template);
                var home = Settlement.Find(intent.Settlement);
                if (template == null || home == null) { Problems.Add((companions ? "companion" : "notable") + " intent " + intent.Id + ": " + (template == null ? "template " + intent.Template + " is not registered" : "home " + intent.Settlement + " is not registered")); continue; }
                var hero = HeroCreator.CreateSpecialHero(template, home, null, null, intent.Age);
                if (companions)
                {
                    if (adjust != null) adjust.Invoke(companionsBehavior, new object[] { hero });
                    hero.ChangeState(Hero.CharacterStates.Active);
                    EnterSettlementAction.ApplyForCharacterOnly(hero, home);
                }
                hero.SetName(new TextObject(intent.Name), FirstName(intent.Name));
                intentIds.Add(intent.Id); intentHeroes.Add(hero);
                if (companions) CompanionsCreated++; else NotablesCreated++;
            }
            if (companions) CompanionsReplaced = true; else NotablesReplaced = true;
            trace("startup_" + (companions ? "companions" : "notables") + "_created:" + (companions ? CompanionsCreated : NotablesCreated));
        }

        private void OnNewGameFollowUpEnd(CampaignGameStarter starter)
        {
            try { ApplyRelations(); }
            catch (Exception error) { Problems.Add("relations: " + error); }
            try { RestoreNames(); }
            catch (Exception error) { Problems.Add("names: " + error); }
            if (!NotablesReplaced) Problems.Add("NotablesCampaignBehavior.SpawnNotablesAtGameStart did not reach the startup patch");
            if (!CompanionsReplaced) Problems.Add("CompanionsCampaignBehavior.OnNewGameCreated did not reach the startup patch");
            trace("startup_problems:" + Problems.Count);
            foreach (var problem in Problems.Take(10)) trace("startup_problem:" + problem.Replace("\r", "").Replace("\n", " | "));
            Deactivate(trace);
        }

        // The intent's name is the hero's full name; its first name is the name's first word (as the installed name generator sets both).
        private static TextObject FirstName(string name)
        {
            var words = (name ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return new TextObject(words.Length == 0 ? name : words[0]);
        }

        private void RestoreNames()
        {
            var plan = planSource();
            if (plan == null) return;
            var map = Map;
            foreach (var intent in plan.Notables.Concat(plan.Companions))
            {
                Hero hero;
                if (!map.TryGetValue(intent.Id, out hero)) continue;
                var name = new TextObject(intent.Name); var first = FirstName(intent.Name);
                if (hero.Name != null && hero.Name.ToString() == name.ToString() && hero.FirstName != null && hero.FirstName.ToString() == first.ToString()) continue;
                hero.SetName(name, first);
                NamesRestored++;
            }
            trace("startup_names_restored:" + NamesRestored);
        }

        private void ApplyRelations()
        {
            var plan = planSource();
            if (plan != null)
            {
                var heroes = Hero.AllAliveHeroes.ToDictionary(h => h.StringId, StringComparer.Ordinal);
                foreach (var relation in plan.Relations)
                {
                    Hero a, b;
                    if (!heroes.TryGetValue(relation.A, out a) || !heroes.TryGetValue(relation.B, out b)) { Problems.Add("relation " + relation.A + "/" + relation.B + ": a hero is not alive in the world"); continue; }
                    CharacterRelationManager.SetHeroRelation(a, b, relation.Value);
                    RelationsApplied++;
                }
                trace("startup_relations_applied:" + RelationsApplied);
            }
        }
    }
}
