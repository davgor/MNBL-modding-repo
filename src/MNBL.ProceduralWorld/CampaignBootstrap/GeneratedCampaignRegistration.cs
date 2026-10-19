using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Xml;
using MNBL.ProceduralWorld.CampaignRegistry;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Settlements.Buildings;
using TaleWorlds.Core;
using TaleWorlds.ModuleManager;
using TaleWorlds.ObjectSystem;
using MNBL.ProceduralWorld.Surface;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // Explicit local bootstrap experiment. The captured merge is hash checked;
    // default-object references are checked against the live early registry. Registration and saved-game replay go
    // through the world's CampaignObjectRegistry (MMR-30): plan checks before the first document loads, phase-by-phase
    // loading, closure before the default lists publish, and a census of MBObjectManager and CampaignObjectManager.
    internal sealed class GeneratedCampaignRegistration : IGeneratedWorld
    {
        internal string WorldId { get; private set; }
        internal string SceneName { get; private set; }
        internal string CultureId { get; private set; }
        internal GeneratedCampaignDataset Dataset { get; private set; }
        internal Dictionary<string, object> Report { get; private set; }
        // MMR-63: set when the placement declares an x-periodic world (strips copying the other side of the seam).
        internal PeriodicWorld Periodic { get; private set; }
        // MMR-59: set when the placement declares generated wind and snow/rain fields.
        internal GeneratedClimate Climate { get; private set; }
        // MMR-19: the immutable package written after creation, or the package a saved game is loaded from.
        internal GeneratedWorldPackage Package { get; private set; }
        private uint seed;
        private GeneratedCampaignDataset.Options options;
        private Dictionary<string, XmlDocument> sources;
        private bool registered;
        // MMR-40 (generated_campaign factoryEntities=1): the fixture settlements without a scene entity get the factory's hierarchy at run time
        // (GeneratedMapScene.AddNewEntityToMapScene) instead of their vanilla prefabs.
        private bool factoryEntities;
        // MMR-36: the settlement fixture policies (path, sha256) the graph report binds with the graph policy; null without fixtures.
        private Dictionary<string, object> graphPolicies;
        // MMR-36: the whole-graph validation of the staged documents (CampaignGraphValidator), set by Register before anything registers.
        internal CampaignGraphValidator.Report GraphReport { get; private set; }
        // MMR-36, probes only: alters the staged documents before the gate (GraphFaults), so a refusal can be observed in game.
        internal Action<List<KeyValuePair<string, XmlDocument>>, IDictionary<string, XmlDocument>> GraphFault { get; set; }
        private CampaignObjectRegistry registry;
        internal CampaignObjectRegistry Registry { get { return registry; } }

        // MMR-33: settlement fixtures (the castle, castle-bound village and hideout built by SettlementFactory) when set.
        internal bool FixturesEnabled { get; private set; }
        // MMR-34: startup fixtures (bandit clans, retreat, runtime plan) when set; a saved world restores it from its package manifest
        // (adapter.dataset.suppliesStartup), so a load keeps RetirementCampaignBehavior and the startup behaviour's saved intent map.
        internal bool StartupEnabled { get; private set; }
        // MMR-35: naval fixtures (the pirate clans as world aliases, and the fleet rule) when set; a saved world restores the fleet rule from
        // its package manifest (adapter.dataset.navalLandlocked).
        internal bool NavalEnabled { get; private set; }
        private NavalContentFactory.NavalPlan savedNavalPlan;
        // The placement's navigation cell size (the MMR-17 port-gate envelope is maxPortGateCells cells); 0 for a saved world.
        internal double NavSpacing { get; private set; }
        // MMR-34: a saved fixture world's settlement -> map prefab map (adapter.dataset.fixturePrefabs). The generated scene has no entity
        // for the fixture settlements, so a load needs the map as much as the new game did (run 35880: SettlementVisual.OnStartup NRE).
        private Dictionary<string, string> savedFixturePrefabs = new Dictionary<string, string>(StringComparer.Ordinal);

        internal static GeneratedCampaignRegistration Prepare(uint seed, string placementPath, string catalogReceipt,
            string culture, string heroTemplate, string townPrototype, string villagePrototype, string settlementFixturesRepo = null, bool startupFixtures = false,
            bool navalFixtures = false, bool factoryEntities = false)
        {
            var placement = ReadJson(placementPath);
            if (Text(placement, "schema") != "mnbl.bootstrap-placement.v1") throw new InvalidDataException("Wrong generated placement schema.");
            string world = "mnbl_" + seed.ToString("x8", CultureInfo.InvariantCulture);
            string sceneName = "mnbl_generated_" + seed.ToString("x8", CultureInfo.InvariantCulture);
            if (Text(placement, "worldId") != world || Text(placement, "sceneName") != sceneName)
                throw new InvalidDataException("Placement identity differs from requested seed.");
            var sceneFiles = (Dictionary<string, object>)placement["sceneFiles"];
            var folder = Path.Combine(ModuleHelper.GetModuleFullPath(GeneratedMapSceneCreator.ModuleId), "SceneObj", sceneName);
            GeneratedMapSceneCreator.ValidateFixture(sceneName);
            foreach (string file in new[] { "scene.xscene", "terrain.bin", "navmesh.bin", "flora.bin", "atmosphere.xml" })
                RequireHash(Path.Combine(folder, file), Text(sceneFiles, file));
            var receipt = ReadJson(catalogReceipt);
            if (Text(receipt, "probe") != "campaign_catalog" || Text(receipt, "status") != "ok" || !(bool)receipt["final"])
                throw new InvalidDataException("A completed campaign catalog receipt is required.");
            var outputs = (Dictionary<string, object>)receipt["outputs"];
            if (Text(outputs, "gameType") != "Campaign") throw new InvalidDataException("Wrong catalog game type.");
            var sources = new Dictionary<string, XmlDocument>(StringComparer.Ordinal);
            string settlementsSha256 = null;
            foreach (Dictionary<string, object> document in (IEnumerable)outputs["documents"])
            {
                string id = Text(document, "id"), path = Text(document, "path");
                RequireHash(path, Text(document, "sha256"));
                if (id == "Settlements") settlementsSha256 = Text(document, "sha256");
                var xml = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) xml.Load(reader);
                sources.Add(id, xml);
            }
            Dictionary<string, object> fixturePolicies = null, economy = null, prototypes = null;
            if (!String.IsNullOrEmpty(settlementFixturesRepo))
            {
                // The economy policy and prototypes must be the ones made from this catalog's Settlements (as the offline harness
                // requires); their hashes go into the report.
                string economyPath = Path.Combine(settlementFixturesRepo, "worldgen/policies/economy.json");
                string prototypesPath = Path.Combine(settlementFixturesRepo, "worldgen/templates/settlements/prototypes.json");
                economy = ReadJson(economyPath); prototypes = ReadJson(prototypesPath);
                if (Text((Dictionary<string, object>)economy["provenance"], "settlementsSha256") != settlementsSha256 ||
                    Text((Dictionary<string, object>)prototypes["provenance"], "settlementsSha256") != settlementsSha256)
                    throw new InvalidDataException("The economy policy or the settlement prototypes were made from another Settlements catalog.");
                fixturePolicies = new Dictionary<string, object> {
                    ["economy"] = new Dictionary<string, object> { ["path"] = Path.GetFullPath(economyPath), ["sha256"] = Hash(economyPath) },
                    ["prototypes"] = new Dictionary<string, object> { ["path"] = Path.GetFullPath(prototypesPath), ["sha256"] = Hash(prototypesPath) },
                    ["settlementsSha256"] = settlementsSha256 };
            }
            // The placement's settlements, extent and verified land points become the dataset options (shared with the offline graph tests).
            // MMR-42: the fixture factory checks its picks against the mission scene catalogue in game.
            var options = FixturePlacement.Options(placement, seed, culture, heroTemplate, townPrototype, villagePrototype, economy, prototypes, startupFixtures, navalFixtures,
                economy == null ? null : MNBL.ProceduralWorld.Missions.MissionScenes.Installed());
            return new GeneratedCampaignRegistration {
                FixturesEnabled = options.Fixtures != null, StartupEnabled = options.Startup != null, NavalEnabled = navalFixtures, factoryEntities = factoryEntities,
                NavSpacing = FixturePlacement.NavSpacing(placement), graphPolicies = fixturePolicies,
                seed = seed, WorldId = world, SceneName = sceneName, CultureId = culture, sources = sources,
                Periodic = PeriodicWorld.FromPlacement(placement),
                Climate = GeneratedClimate.FromPlacement(placement, placementPath),
                placementData = placement,
                options = options,
                Report = new Dictionary<string, object> {
                    ["worldId"] = world, ["sceneName"] = sceneName,
                    ["placementPath"] = Path.GetFullPath(placementPath), ["placementSha256"] = Hash(placementPath),
                    ["catalogReceipt"] = Path.GetFullPath(catalogReceipt), ["catalogReceiptSha256"] = Hash(catalogReceipt),
                    ["installedSceneHashes"] = sceneFiles, ["registrationCompleted"] = false, ["settlementFixturePolicies"] = fixturePolicies,
                    ["scope"] = "Generated new-game registration experiment; no save restoration or final product activation claim."
                }
            };
        }

        internal void Register(Action<string> trace)
        {
            if (registered || Dataset != null) throw new InvalidOperationException("Generated registration may only run once.");
            if (Campaign.Current == null || Game.Current == null) throw new InvalidOperationException("Campaign defaults are unavailable.");
            var defaults = new List<string>();
            Defaults<SkillObject>("Skill", defaults); Defaults<TraitObject>("Trait", defaults);
            Defaults<PolicyObject>("Policy", defaults); Defaults<BuildingType>("BuildingType", defaults);
            Defaults<VillageType>("VillageType", defaults); Defaults<ItemCategory>("ItemCategory", defaults);
            Defaults<FeatObject>("Feat", defaults); Defaults<CharacterAttribute>("CharacterAttribute", defaults);
            // Game.InitializeDefaultGameObjects loads SiegeEngines (Native + NavalDLC) and
            // Game.LoadBasicFiles loads SkeletonScales, both before RegisterSubModuleObjects.
            Defaults<SiegeEngineType>("SiegeEngineType", defaults); Defaults<SkeletonScale>("SkeletonScale", defaults);
            Report["verifiedLiveDefaultReferences"] = defaults;
            trace("generated_dataset_preflight");
            Dataset = GeneratedCampaignDataset.Build(seed, options, sources, defaults);
            if (Dataset.WorldId != WorldId || Dataset.SceneName != SceneName) throw new InvalidDataException("Dataset/scene identities disagree.");
            Report["templates"] = Dataset.Templates; Report["prototypes"] = Dataset.Prototypes;
            Report["instances"] = Dataset.Instances; Report["requiredAliases"] = Dataset.RequiredAliases;
            Report["limitations"] = Dataset.Limitations;
            var documents = new List<object>(); Report["documents"] = documents;
            var loads = Dataset.Documents.Select(d => new KeyValuePair<string, XmlDocument>(d.Id, d.CreateDocument())).ToList();
            // MMR-36: the whole staged graph is validated before the fixture prefabs, the registry plan or any document load; a refused world
            // throws here with every diagnostic, so nothing of it is registered.
            if (GraphFault != null) GraphFault(loads, sources);
            Func<string, string> policySha = key => graphPolicies == null ? null : (string)((Dictionary<string, object>)graphPolicies[key])["sha256"];
            GraphReport = CampaignGraphValidator.Validate(CampaignGraphValidator.InstalledPolicy(), FixturePlacement.GraphInput(seed, Dataset, loads, sources, defaults,
                CampaignGraphValidator.InstalledPolicySha256(), (string)Report["catalogReceiptSha256"], policySha("economy"), policySha("prototypes")));
            Report["graphValidation"] = GraphReport.Record(50);
            trace("generated_graph_validation:" + (GraphReport.Ok ? "valid" : GraphReport.Diagnostics.Count + " diagnostics"));
            if (!GraphReport.Ok) throw new CampaignGraphValidator.GraphException(GraphReport);
            // MMR-33: the generated scene has no entities for the fixture settlements; the map scene makes them from these prefabs.
            GeneratedMapScene.FixturePrefabs.Clear();
            // MMR-40: with factoryEntities they get the factory's hierarchy at run time instead.
            if (!factoryEntities)
                foreach (var prefab in Dataset.FixtureEntityPrefabs) GeneratedMapScene.FixturePrefabs[prefab.Key] = prefab.Value;
            var store = new MBObjectStore(StoreStage.NewGame, phase => trace("generated_xml_load:" + phase));
            // The dataset's own list of the world objects it emitted, so every generated record is checked whatever its type.
            var plan = RegistrationPlan.FromDocuments(CampaignIdentity.WorldPrefixOfSeed(seed), loads, false, Dataset.Instances.Concat(Dataset.RequiredAliases));
            registry = new CampaignObjectRegistry(plan, store.IsSharedTemplate);
            trace("generated_registry_plan:" + plan.GeneratedObjects.Count());
            registry.Register(store, loads);   // refuses before loading, loads phase by phase, asserts closure
            foreach (var document in Dataset.Documents) documents.Add(new { id = document.Id, sha256 = document.Sha256 });
            Report["registryCensus"] = registry.Census(store, new CampaignLists(), "after_generated_registration", ClosureMode.Registration);
            registered = true;
            Report["registrationCompleted"] = true;
            trace("generated_xml_registration_complete");
        }

        // MMR-19: after the new game is built, pin its outputs (placement, climate files, the exact generated documents and the
        // installed scene hashes) as an immutable package that a later cold load reads instead of regenerating.
        internal GeneratedWorldPackage WritePackage(string adapterSha256)
        {
            if (!registered || Dataset == null) throw new InvalidOperationException("The world must be registered before it is packaged.");
            if (Package != null) return Package;
            Package = GeneratedWorldPackage.Write(WorldId, SceneName, seed, (string)Report["placementPath"], Dataset.Documents, adapterSha256,
                new Dictionary<string, object> { ["builder"] = "GeneratedCampaignDataset.Build (new game)", ["defaultReferencesSha256"] = Dataset.DefaultReferencesSha256,
                    ["instances"] = Dataset.Instances.Count, ["catalogReceiptSha256"] = Report["catalogReceiptSha256"], ["placementSha256"] = Report["placementSha256"],
                    ["suppliesStartup"] = StartupEnabled,
                    ["navalLandlocked"] = Dataset.NavalPlan == null ? null : Dataset.NavalPlan.Landlocked.Cast<object>().ToList(),
                    ["fixturePrefabs"] = Dataset.FixtureEntityPrefabs.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => (object)p.Value) });
            Report["package"] = Package.Directory; Report["packageManifestSha256"] = Package.ManifestSha256;
            return Package;
        }

        // MMR-19: a registration for loading a saved generated world, built only from its verified package (no catalog,
        // no dataset build, no generator).
        internal static GeneratedCampaignRegistration FromInstalledPackage(GeneratedWorldPackage package)
        {
            object adapter, dataset, supplies, prefabs, landlocked;
            var fingerprint = package.Manifest.TryGetValue("adapter", out adapter) && adapter is Dictionary<string, object> && ((Dictionary<string, object>)adapter).TryGetValue("dataset", out dataset)
                ? dataset as Dictionary<string, object> : null;
            bool startup = fingerprint != null && fingerprint.TryGetValue("suppliesStartup", out supplies) && supplies is bool && (bool)supplies;
            var saved = new Dictionary<string, string>(StringComparer.Ordinal);
            if (fingerprint != null && fingerprint.TryGetValue("fixturePrefabs", out prefabs) && prefabs is Dictionary<string, object>)
                foreach (var p in (Dictionary<string, object>)prefabs) saved[p.Key] = Convert.ToString(p.Value, CultureInfo.InvariantCulture);
            NavalContentFactory.NavalPlan naval = null;
            if (fingerprint != null && fingerprint.TryGetValue("navalLandlocked", out landlocked) && landlocked is System.Collections.IEnumerable && !(landlocked is string))
            {
                naval = new NavalContentFactory.NavalPlan();
                foreach (var clan in ((System.Collections.IEnumerable)landlocked).Cast<object>()) naval.Landlocked.Add(Convert.ToString(clan, CultureInfo.InvariantCulture));
                naval.AddFixturePirates(package.Placement);   // MMR-39: the same points the new game had
            }
            return new GeneratedCampaignRegistration {
                seed = package.Seed, WorldId = package.WorldId, SceneName = package.SceneName, Package = package, StartupEnabled = startup, savedFixturePrefabs = saved,
                NavalEnabled = naval != null, savedNavalPlan = naval,
                FixturesEnabled = saved.Count > 0,
                Periodic = PeriodicWorld.FromPlacement(package.Placement),
                Climate = GeneratedClimate.FromPlacement(package.Placement, package.PlacementPath),
                Report = new Dictionary<string, object> {
                    ["worldId"] = package.WorldId, ["sceneName"] = package.SceneName, ["package"] = package.Directory,
                    ["packageManifestSha256"] = package.ManifestSha256, ["savedGameDocuments"] = new List<object>(),
                    ["scope"] = "Saved generated world: generated XML re-applied from the immutable package in RegisterSubModuleObjects(true)"
                }
            };
        }

        // Re-merges the generated XML onto the deserialized objects by StringId, as vanilla InitializeSandboxXMLs(true) does
        // for official content: this marks them ready and restores what the save does not store (settlement names, positions,
        // gates, ports, cultures). Called from the saved-game RegisterSubModuleObjects(true), after MBObjectBase.BeforeLoad has
        // re-registered the saved objects and before Campaign.OnInitialize unregisters non-ready ones.
        internal void ReapplyForSavedGame(Action<string> trace)
        {
            if (Package == null || registered) throw new InvalidOperationException("Saved-game re-application needs a package registration, once.");
            var rows = (List<object>)Report["savedGameDocuments"];
            // Before the map scene loads: the fixture settlements' entities come from their prefabs, as at new game.
            GeneratedMapScene.FixturePrefabs.Clear();
            foreach (var prefab in savedFixturePrefabs) GeneratedMapScene.FixturePrefabs[prefab.Key] = prefab.Value;
            Report["fixturePrefabs"] = savedFixturePrefabs.Count;
            var loads = GeneratedWorldPackage.SavedGameDocuments.Select(id => new KeyValuePair<string, XmlDocument>(id, Package.Document(id))).ToList();
            // RegisterSubModuleObjects(true) runs after BeforeLoad re-registered the saved objects (R05, run 8256).
            var store = new MBObjectStore(StoreStage.SavedObjectsRegistered, phase => trace("generated_saved_xml_load:" + phase));
            var plan = RegistrationPlan.FromDocuments(CampaignIdentity.WorldPrefixOfSeed(seed), loads, true);
            registry = new CampaignObjectRegistry(plan, store.IsSharedTemplate);
            registry.Replay(store, loads, new CampaignLists());   // refuses before loading, re-merges, asserts closure
            foreach (var id in GeneratedWorldPackage.SavedGameDocuments)
                rows.Add(new Dictionary<string, object> { ["id"] = id, ["sha256"] = Package.DocumentSha256(id) });
            Report["registryCensus"] = registry.Census(store, new CampaignLists(), "after_saved_replay", ClosureMode.Replay);
            registered = true;
            trace("generated_saved_xml_reapplied");
        }

        string IGeneratedWorld.WorldId { get { return WorldId; } }
        bool IGeneratedWorld.SuppliesStartup { get { return StartupEnabled; } }
        CampaignRegistry.GeneratedCampaignInitializer.StartupPlan IGeneratedWorld.StartupPlan { get { return Dataset == null ? null : Dataset.StartupPlan; } }
        // OnGameStart reads the plan before the world registers: a new game takes the fixture rule its options select (the dataset records the
        // same rule for the package), a load the package's.
        NavalContentFactory.NavalPlan IGeneratedWorld.NavalPlan { get { return Package != null && Dataset == null ? savedNavalPlan : NavalEnabled ? (pendingNaval ?? (pendingNaval = FixturePlan())) : null; } }
        private Dictionary<string, object> placementData;
        // MMR-39: the fixture's fleet rule with its pirate spawn points (placement.naval.pirateSpawnPoints).
        private NavalContentFactory.NavalPlan FixturePlan()
        {
            var plan = GeneratedCampaignDataset.FixtureNavalPlan(WorldId);
            plan.AddFixturePirates(placementData);
            return plan;
        }
        private NavalContentFactory.NavalPlan pendingNaval;
        void IGeneratedWorld.Register(Action<string> trace) { Register(trace); }
        IList<string> IGeneratedWorld.RollBack()
        {
            if (registry == null) return new List<string>();
            var remaining = registry.RollBack(new MBObjectStore(StoreStage.NewGame), new CampaignLists());
            Report["rollback"] = remaining;
            return remaining;
        }
        void IGeneratedWorld.Replay(Action<string> trace) { ReapplyForSavedGame(trace); }

        // mode: ClosureMode.Settled once the new game reached the map or the load finished (the probes enforce it there).
        public IList<string> ClosureProblems(ClosureMode mode)
        {
            if (registry == null) return new List<string> { "the generated world was never registered" };
            return registry.ClosureProblems(new MBObjectStore(Package != null && Dataset == null ? StoreStage.SavedObjectsRegistered : StoreStage.NewGame), new CampaignLists(), mode);
        }

        public Dictionary<string, object> RegistryCensus(string phase, ClosureMode mode)
        {
            if (registry == null) return new Dictionary<string, object> { ["phase"] = phase, ["registered"] = false };
            return registry.Census(new MBObjectStore(Package != null && Dataset == null ? StoreStage.SavedObjectsRegistered : StoreStage.NewGame), new CampaignLists(), phase, mode);
        }

        private static void Defaults<T>(string kind, ICollection<string> result) where T : MBObjectBase
        {
            var values = MBObjectManager.Instance.GetObjectTypeList<T>();
            if (values == null) throw new InvalidOperationException("Default type registry unavailable: " + kind);
            int ready = 0;
            foreach (T value in values) if (value.IsReady) { result.Add(kind + "." + value.StringId); ready++; }
            if (ready == 0) throw new InvalidOperationException("No ready live defaults for kind: " + kind);
        }

        private static Dictionary<string, object> ReadJson(string path)
        {
            return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
        }
        private static string Text(Dictionary<string, object> record, string key) { return Convert.ToString(record[key], CultureInfo.InvariantCulture); }
        private static string Hash(string path)
        {
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        private static void RequireHash(string path, string expected)
        {
            if (expected == null || expected.Length != 64 || Hash(path) != expected) throw new InvalidDataException("Input hash mismatch: " + path);
        }
    }
}
