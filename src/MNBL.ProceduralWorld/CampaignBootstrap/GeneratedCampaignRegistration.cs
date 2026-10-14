using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Xml;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Settlements.Buildings;
using TaleWorlds.Core;
using TaleWorlds.ModuleManager;
using TaleWorlds.ObjectSystem;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // Explicit local bootstrap experiment. The captured merge is hash checked;
    // default-object references are checked against the live early registry.
    internal sealed class GeneratedCampaignRegistration
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

        internal static GeneratedCampaignRegistration Prepare(uint seed, string placementPath, string catalogReceipt,
            string culture, string heroTemplate, string townPrototype, string villagePrototype)
        {
            var placement = ReadJson(placementPath);
            if (Text(placement, "schema") != "mnbl.bootstrap-placement.v1") throw new InvalidDataException("Wrong generated placement schema.");
            string world = "mnbl_" + seed.ToString("x8", CultureInfo.InvariantCulture);
            string sceneName = "mnbl_generated_" + seed.ToString("x8", CultureInfo.InvariantCulture);
            if (Text(placement, "worldId") != world || Text(placement, "sceneName") != sceneName)
                throw new InvalidDataException("Placement identity differs from requested seed.");
            var settlements = ((IEnumerable)placement["settlements"]).Cast<Dictionary<string, object>>().ToArray();
            if (settlements.Length != 4 || settlements.Select(s => Text(s, "id")).Distinct().Count() != 4)
                throw new InvalidDataException("Expected exactly four distinct generated settlements.");
            var first = GetPlacement(settlements, world, 1); var second = GetPlacement(settlements, world, 2);
            double extent = Convert.ToDouble(placement["extent"], CultureInfo.InvariantCulture);
            var sceneFiles = (Dictionary<string, object>)placement["sceneFiles"];
            var folder = Path.Combine(ModuleHelper.GetModuleFullPath(DiagnosticMapSceneCreator.ModuleId), "SceneObj", sceneName);
            DiagnosticMapSceneCreator.ValidateFixture(sceneName);
            foreach (string file in new[] { "scene.xscene", "terrain.bin", "navmesh.bin", "flora.bin", "atmosphere.xml" })
                RequireHash(Path.Combine(folder, file), Text(sceneFiles, file));
            var receipt = ReadJson(catalogReceipt);
            if (Text(receipt, "probe") != "campaign_catalog" || Text(receipt, "status") != "ok" || !(bool)receipt["final"])
                throw new InvalidDataException("A completed campaign catalog receipt is required.");
            var outputs = (Dictionary<string, object>)receipt["outputs"];
            if (Text(outputs, "gameType") != "Campaign") throw new InvalidDataException("Wrong catalog game type.");
            var sources = new Dictionary<string, XmlDocument>(StringComparer.Ordinal);
            foreach (Dictionary<string, object> document in (IEnumerable)outputs["documents"])
            {
                string id = Text(document, "id"), path = Text(document, "path");
                RequireHash(path, Text(document, "sha256"));
                var xml = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) xml.Load(reader);
                sources.Add(id, xml);
            }
            return new GeneratedCampaignRegistration {
                seed = seed, WorldId = world, SceneName = sceneName, CultureId = culture, sources = sources,
                Periodic = PeriodicWorld.FromPlacement(placement),
                Climate = GeneratedClimate.FromPlacement(placement, placementPath),
                options = new GeneratedCampaignDataset.Options(culture, heroTemplate, townPrototype, villagePrototype, extent, first, second,
                    GeneratedCampaignDataset.InstalledEngineCharacterIds),
                Report = new Dictionary<string, object> {
                    ["worldId"] = world, ["sceneName"] = sceneName,
                    ["placementPath"] = Path.GetFullPath(placementPath), ["placementSha256"] = Hash(placementPath),
                    ["catalogReceipt"] = Path.GetFullPath(catalogReceipt), ["catalogReceiptSha256"] = Hash(catalogReceipt),
                    ["installedSceneHashes"] = sceneFiles, ["registrationCompleted"] = false,
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
            foreach (var document in Dataset.Documents)
            {
                trace("generated_xml_load:" + document.Id);
                MBObjectManager.Instance.LoadXml(document.CreateDocument(), Game.Current.GameType.IsDevelopment);
                documents.Add(new { id = document.Id, sha256 = document.Sha256 });
            }
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
                    ["instances"] = Dataset.Instances.Count, ["catalogReceiptSha256"] = Report["catalogReceiptSha256"], ["placementSha256"] = Report["placementSha256"] });
            Report["package"] = Package.Directory; Report["packageManifestSha256"] = Package.ManifestSha256;
            return Package;
        }

        // MMR-19: a registration for loading a saved generated world, built only from its verified package (no catalog,
        // no dataset build, no generator).
        internal static GeneratedCampaignRegistration FromInstalledPackage(GeneratedWorldPackage package)
        {
            return new GeneratedCampaignRegistration {
                seed = package.Seed, WorldId = package.WorldId, SceneName = package.SceneName, Package = package,
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
            foreach (var id in GeneratedWorldPackage.SavedGameDocuments)
            {
                trace("generated_saved_xml_load:" + id);
                MBObjectManager.Instance.LoadXml(Package.Document(id), Game.Current.GameType.IsDevelopment);
                rows.Add(new Dictionary<string, object> { ["id"] = id, ["sha256"] = Package.DocumentSha256(id) });
            }
            registered = true;
            trace("generated_saved_xml_reapplied");
        }

        private static void Defaults<T>(string kind, ICollection<string> result) where T : MBObjectBase
        {
            var values = MBObjectManager.Instance.GetObjectTypeList<T>();
            if (values == null) throw new InvalidOperationException("Default type registry unavailable: " + kind);
            int ready = 0;
            foreach (T value in values) if (value.IsReady) { result.Add(kind + "." + value.StringId); ready++; }
            if (ready == 0) throw new InvalidOperationException("No ready live defaults for kind: " + kind);
        }

        private static GeneratedCampaignDataset.Placement GetPlacement(Dictionary<string, object>[] settlements, string world, int ordinal)
        {
            string suffix = ordinal.ToString(CultureInfo.InvariantCulture);
            var town = settlements.Single(s => Text(s, "id") == world + "_town_" + suffix);
            var village = settlements.Single(s => Text(s, "id") == world + "_village_" + suffix);
            if (Text(town, "kind") != "town" || Text(village, "kind") != "village" || Text(village, "boundTo") != Text(town, "id"))
                throw new InvalidDataException("Generated village/town ownership contract differs.");
            var p = Point(town, "position"); var g = Point(town, "gatePosition");
            var water = Point(town, "portPosition"); var v = Point(village, "position");
            return new GeneratedCampaignDataset.Placement(p[0], p[1], g[0], g[1], water[0], water[1], v[0], v[1]);
        }

        private static double[] Point(Dictionary<string, object> record, string key)
        {
            var result = ((IEnumerable)record[key]).Cast<object>().Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray();
            if (result.Length != 3 || result.Any(v => Double.IsNaN(v) || Double.IsInfinity(v))) throw new InvalidDataException("Invalid placement point: " + key);
            return result;
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
