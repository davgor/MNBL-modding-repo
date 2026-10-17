using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using MNBL.ProceduralWorld.CampaignRegistry;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Settlements.Buildings;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // MMR-45: a compiled world as the new game registers it (docs/world-activation.md). The activation built and validated its documents at
    // the main menu (CompiledWorldDataset, BuildWorld); inside the new game's RegisterSubModuleObjects (R07) this registers exactly those
    // documents through the world's CampaignObjectRegistry (MMR-30: plan checks before the first document loads, phase by phase, closure),
    // after checking that the live engine defaults are the pinned inventory the documents were built against. Every XML hero's explicit body
    // is drawn here, with the engine's face generator (HeroBodies, MMR-32), from the hero's seed: the body ranges live in the game's object
    // manager, which the main menu does not have. The same seed gives the same body, so the package keeps the documents as validated and a
    // load draws the same bodies again.
    // MMR-49: a load (ForLoad) replays instead: inside the saved game's RegisterSubModuleObjects(true), after MBObjectBase.BeforeLoad
    // re-registered the deserialized objects, the same documents (rebuilt from the bundle and equal to the package's, GeneratedWorldLoader)
    // with the same drawn bodies are re-merged onto them by StringId through the registry's replay (as the installed InitializeSandboxXMLs(true)
    // does for official content): plan checks before anything merges, phase by phase, closure in replay mode. A new-game registration never
    // replays and a load registration never registers.
    internal sealed class CompiledWorldRegistration : IGeneratedWorld
    {
        private readonly CompiledWorldDataset dataset;
        private readonly string campaignInstanceId;
        private readonly bool forLoad;
        private CampaignObjectRegistry registry;
        private bool registered;
        // Probes only: alters the documents just before they register (an in-game refusal at closure).
        internal Action<List<KeyValuePair<string, XmlDocument>>> RegistrationFault { get; set; }

        public CompiledWorldRegistration(CompiledWorldDataset dataset, string sceneName, string campaignInstanceId) : this(dataset, sceneName, campaignInstanceId, false) { }

        // MMR-49: the registration a load replays.
        public static CompiledWorldRegistration ForLoad(CompiledWorldDataset dataset, string sceneName, string campaignInstanceId) { return new CompiledWorldRegistration(dataset, sceneName, campaignInstanceId, true); }

        private CompiledWorldRegistration(CompiledWorldDataset dataset, string sceneName, string campaignInstanceId, bool forLoad)
        {
            if (dataset == null || dataset.Graph == null || !dataset.Graph.Ok) throw new ArgumentException("Only a compiled world whose graph validated registers.");
            this.dataset = dataset; SceneName = sceneName; this.campaignInstanceId = campaignInstanceId; this.forLoad = forLoad;
            Report = new Dictionary<string, object> { ["worldId"] = dataset.WorldId, ["sceneName"] = sceneName, ["registrationCompleted"] = false, ["documents"] = dataset.DocumentSha256,
                ["graphSha256"] = dataset.Graph.Sha256 };
        }

        public string WorldId { get { return dataset.WorldId; } }
        public string SceneName { get; private set; }
        public CompiledWorldDataset Dataset { get { return dataset; } }
        public Dictionary<string, object> Report { get; private set; }
        public CampaignObjectRegistry Registry { get { return registry; } }

        public bool ForLoading { get { return forLoad; } }
        private StoreStage Stage { get { return forLoad ? StoreStage.SavedObjectsRegistered : StoreStage.NewGame; } }

        public void Register(Action<string> trace)
        {
            if (forLoad) throw new InvalidOperationException("A load's registration replays; it never registers anew.");
            if (registered || registry != null) throw new InvalidOperationException("A compiled world registers once.");
            if (Campaign.Current == null || Game.Current == null) throw new InvalidOperationException("Campaign defaults are unavailable.");
            // The documents were built against the pinned default inventory; the live one must be the same.
            var live = LiveDefaults();
            var pinned = new HashSet<string>(dataset.Defaults, StringComparer.Ordinal);
            var missing = pinned.Where(x => !live.Contains(x)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var extra = live.Where(x => !pinned.Contains(x)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Report["liveDefaults"] = live.Count; Report["liveDefaultsSha256"] = CampaignGraphValidator.InventorySha256(live);
            if (missing.Count > 0 || extra.Count > 0)
                throw new RegistrationException("Refused before loading", new[] { "the live engine defaults are not the pinned inventory: missing " + String.Join(", ", missing.Take(10)) + "; extra " + String.Join(", ", extra.Take(10)) });
            trace("compiled_defaults_verified:" + live.Count);
            var loads = dataset.Documents.Select(d => new KeyValuePair<string, XmlDocument>(d.Key, (XmlDocument)d.Value.CloneNode(true))).ToList();
            var bodyProblems = new List<string>();
            var bodies = HeroBodies.Draw(dataset.Heroes.XmlHeroes, bodyProblems);
            HeroBodies.Apply(loads.Single(d => d.Key == "NPCCharacters").Value, bodies, bodyProblems);
            if (bodyProblems.Count > 0) throw new RegistrationException("Refused before loading", bodyProblems);
            Report["bodiesDrawn"] = bodies.Count;
            Report["bodiesSha256"] = CompiledWorldDataset.Sha256(System.Text.Encoding.UTF8.GetBytes(String.Join("\n", bodies.Select(b => b.Hero + "=" + b.Xml))));
            trace("compiled_hero_bodies:" + bodies.Count);
            if (RegistrationFault != null) RegistrationFault(loads);
            var store = new MBObjectStore(StoreStage.NewGame, phase => trace("compiled_xml_load:" + phase));
            var plan = RegistrationPlan.FromDocuments(dataset.WorldPrefix, loads, false, dataset.Declared);
            registry = new CampaignObjectRegistry(plan, store.IsSharedTemplate, campaignInstanceId);
            trace("compiled_registry_plan:" + plan.GeneratedObjects.Count());
            registry.Register(store, loads);   // refuses before loading, loads phase by phase, asserts closure
            Report["registryCensus"] = registry.Census(store, new CampaignLists(), "after_compiled_registration", ClosureMode.Registration);
            registered = true;
            Report["registrationCompleted"] = true;
            trace("compiled_xml_registration_complete");
        }

        public void Replay(Action<string> trace)
        {
            if (!forLoad) throw new InvalidOperationException("A new game's registration registers; only a load's replays.");
            if (registered || registry != null) throw new InvalidOperationException("A compiled world replays once.");
            if (Campaign.Current == null || Game.Current == null) throw new InvalidOperationException("Campaign defaults are unavailable.");
            // The live defaults are recorded, not required here: during a load they are not all ready yet (the loader checked the installed
            // catalog against the pin before the game started, and the registry's replay refuses any reference it cannot resolve).
            try
            {
                var live = LiveDefaults();
                var pinned = new HashSet<string>(dataset.Defaults, StringComparer.Ordinal);
                Report["liveDefaults"] = live.Count; Report["liveDefaultsMissing"] = pinned.Count(x => !live.Contains(x)); Report["liveDefaultsExtra"] = live.Count(x => !pinned.Contains(x));
            }
            catch (Exception error) { Report["liveDefaultsError"] = error.GetType().Name + ": " + error.Message; }
            var loads = ReplayDocuments();
            var bodyProblems = new List<string>();
            var bodies = HeroBodies.Draw(dataset.Heroes.XmlHeroes, bodyProblems);
            HeroBodies.Apply(loads.Single(d => d.Key == "NPCCharacters").Value, bodies, bodyProblems);
            if (bodyProblems.Count > 0) throw new RegistrationException("Refused before loading", bodyProblems);
            Report["bodiesDrawn"] = bodies.Count;
            Report["bodiesSha256"] = CompiledWorldDataset.Sha256(System.Text.Encoding.UTF8.GetBytes(String.Join("\n", bodies.Select(b => b.Hero + "=" + b.Xml))));
            trace("compiled_replay_hero_bodies:" + bodies.Count);
            var store = new MBObjectStore(StoreStage.SavedObjectsRegistered, phase => trace("compiled_saved_xml_load:" + phase));
            // A replay from a package: world-prefixed ids, aliases and world types are the generated objects (no declared list: the temporary
            // types the save holds are not replayed, RegistrationPlan.SavedGameOrder).
            var plan = RegistrationPlan.FromDocuments(dataset.WorldPrefix, loads, true);
            registry = new CampaignObjectRegistry(plan, store.IsSharedTemplate, campaignInstanceId);
            registry.Replay(store, loads, new CampaignLists());   // refuses before loading, re-merges, asserts closure
            Report["registryCensus"] = registry.Census(store, new CampaignLists(), "after_compiled_replay", ClosureMode.Replay);
            registered = true;
            Report["replayCompleted"] = true;
            trace("compiled_xml_replay_complete");
        }

        // MMR-49: the documents a load replays, in the saved-game phases (Heroes, Kingdoms and Factions are temporary types whose state the
        // save holds; vanilla skips them on load too).
        internal List<KeyValuePair<string, XmlDocument>> ReplayDocuments()
        {
            return RegistrationPlan.SavedGameOrder.Select(id => dataset.Documents.First(d => d.Key == id)).Select(d => new KeyValuePair<string, XmlDocument>(d.Key, (XmlDocument)d.Value.CloneNode(true))).ToList();
        }

        // MMR-49: the replay plan's structural problems (phase order, duplicates, identities), checked while the menu is up: a refusal inside the
        // load's RegisterSubModuleObjects stops the engine (run 3428, and this ticket's first load), so nothing the plan can know may wait for it.
        // References to saved objects are resolved inside the load.
        public IList<string> ReplayPreflight()
        {
            var plan = RegistrationPlan.FromDocuments(dataset.WorldPrefix, ReplayDocuments(), true);
            return plan.Problems((type, id) => true, (type, id) => true);
        }

        public IList<string> ClosureProblems(ClosureMode mode)
        {
            if (registry == null) return new List<string> { "the compiled world was never registered" };
            return registry.ClosureProblems(new MBObjectStore(Stage), new CampaignLists(), mode);
        }

        public IList<string> RollBack()
        {
            if (registry == null) return new List<string>();
            var remaining = registry.RollBack(new MBObjectStore(Stage), new CampaignLists());
            Report["rollback"] = remaining;
            return remaining;
        }

        public Dictionary<string, object> RegistryCensus(string phase, ClosureMode mode)
        {
            if (registry == null) return new Dictionary<string, object> { ["phase"] = phase, ["registered"] = false };
            return registry.Census(new MBObjectStore(Stage), new CampaignLists(), phase, mode);
        }

        public bool SuppliesStartup { get { return true; } }
        public GeneratedCampaignInitializer.StartupPlan StartupPlan { get { return dataset.Start.Plan; } }
        public NavalContentFactory.NavalPlan NavalPlan { get { return dataset.NavalPlan; } }

        // The ready engine defaults of the kinds a world may reference (the fixture registration's inventory).
        internal static HashSet<string> LiveDefaults()
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            Add<SkillObject>("Skill", result); Add<TraitObject>("Trait", result); Add<PolicyObject>("Policy", result); Add<BuildingType>("BuildingType", result);
            Add<VillageType>("VillageType", result); Add<ItemCategory>("ItemCategory", result); Add<FeatObject>("Feat", result); Add<CharacterAttribute>("CharacterAttribute", result);
            Add<SiegeEngineType>("SiegeEngineType", result); Add<SkeletonScale>("SkeletonScale", result);
            return result;
        }
        private static void Add<T>(string kind, HashSet<string> result) where T : MBObjectBase
        {
            var values = MBObjectManager.Instance.GetObjectTypeList<T>();
            if (values == null) throw new InvalidOperationException("Default type registry unavailable: " + kind);
            foreach (T value in values) if (value.IsReady) result.Add(kind + "." + value.StringId);
        }
    }
}
