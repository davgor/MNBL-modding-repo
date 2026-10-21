using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.ObjectSystem;

namespace MNBL.ProceduralWorld.Generation
{
    // MMR-49: a compiled world's campaign rebuilt from its immutable data bundle, the same way at activation (GameActivationHost.BuildWorld)
    // and at load (GeneratedWorldLoader): the campaign graph against its catalog (CompiledWorldDataset.Build, managed and deterministic: no
    // generator process), the climate from the source in the world's coordinate frame, and the terrain context battles read. A load then
    // requires the rebuilt documents to be the package's byte for byte, so a world is never restored from anything but its own immutable
    // inputs.
    // MMR-283: the catalog is the live merged documents at activation, checked against the reduced pin (CatalogPin: only what a world can
    // reference, by key, element and read fields), and frozen with the world: the package keeps those documents and the engine defaults they
    // were built with (catalog/), and a load rebuilds from them, checking only the installed objects the world references (LoadConflicts).
    internal static class WorldReconstruction
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        public const string CatalogManifest = "catalog/catalog.json";

        internal sealed class World
        {
            public CompiledWorldDataset Dataset;
            public GeneratedClimateSystem.Result Synthesis;
            public GeneratedClimate Climate;
            public Func<double, double, GeneratedBattleTerrain.TerrainContext> TerrainContext;
            public MNBL.ProceduralWorld.World.RoadSpeed.Cells Roads;   // MMR-254: the road cells the party speed model reads
            public string CatalogSha256;
            public int CatalogDocuments;
            public Dictionary<string, byte[]> CatalogBytes;   // the documents the world was built from (the frozen catalog)
            public List<string> Defaults;
            public MapDressingPlan Dressing;   // MMR-257: the road decals, bridges and ruin markers the map draws
            public WorldSurfaceOverlay Overlay;   // MMR-260: the magic ground overlay (generated variants; MMR-261 feeds live cells)
            public MutationModel.Context Mutation;   // MMR-261: the live mutation's inputs (request, capital rule, producer build); null for a magic-off world
        }

        // A world's catalog: its merged documents' bytes and the engine defaults it is built with.
        internal sealed class Frozen
        {
            public Dictionary<string, byte[]> Documents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            public List<string> Defaults = new List<string>();
            public List<Dictionary<string, object>> Modules = new List<Dictionary<string, object>>();   // the active modules at activation (id, official)
        }

        public static CatalogPin.Pin InstalledPin()
        {
            var bytes = RuntimePlayabilityValidator.Embedded("campaign-catalog.json");
            if (bytes == null) throw new InvalidDataException("The module carries no campaign catalog pin.");
            return CatalogPin.Pin.FromBytes(bytes);
        }

        // The live merged documents (engine runs a function on the engine thread; its label names the step); a document the game cannot merge
        // is left out (CatalogPin.Check reports it).
        public static Dictionary<string, byte[]> LiveDocuments(Func<string, Func<byte[]>, byte[]> engine, IEnumerable<string> ids, string gameType)
        {
            var documents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var id in ids.OrderBy(k => k, StringComparer.Ordinal))
            {
                string documentId = id;
                var bytes = engine("catalog " + id, () =>
                {
                    var merged = MBObjectManager.GetMergedXmlForManaged(documentId, false, false, gameType);
                    if (merged == null || merged.DocumentElement == null || merged.DocumentType != null) return null;
                    return new UTF8Encoding(false).GetBytes(merged.OuterXml);
                });
                if (bytes != null) documents[id] = bytes;
            }
            return documents;
        }

        // Activation: the live catalog, checked against the pin before anything is built from it. Throws ContentConflictException naming the
        // module and object of each conflict.
        public static Frozen LiveCatalog(Func<string, Func<byte[]>, byte[]> engine)
        {
            var pin = InstalledPin();
            var frozen = new Frozen { Defaults = pin.Defaults.ToList() };
            frozen.Documents = LiveDocuments(engine, pin.Documents, pin.GameType);
            var conflicts = CatalogPin.Check(pin, CatalogPin.Parse(frozen.Documents), GeneratedCampaignDataset.InstalledEngineCharacterIds);
            var modules = ContentCompatibility.Modules();
            if (conflicts.Count > 0) { CatalogPin.Attribute(conflicts, modules); throw new ContentConflictException(conflicts); }
            frozen.Modules = modules.Modules.Select(m => new Dictionary<string, object> { ["id"] = m.Id, ["official"] = m.Official }).ToList();
            return frozen;
        }

        // The package's files for a world's frozen catalog (catalog/<id>.xml and catalog/catalog.json).
        public static Dictionary<string, byte[]> PackageFiles(Frozen frozen)
        {
            var files = frozen.Documents.ToDictionary(d => "catalog/" + d.Key + ".xml", d => d.Value, StringComparer.Ordinal);
            var manifest = new Dictionary<string, object> { ["schema"] = "mnbl.world-catalog", ["version"] = 1, ["identity"] = CatalogPin.Identity(frozen.Documents),
                ["documents"] = frozen.Documents.OrderBy(d => d.Key, StringComparer.Ordinal).ToDictionary(d => d.Key, d => (object)CatalogPin.Sha(d.Value)),
                ["defaults"] = frozen.Defaults.OrderBy(x => x, StringComparer.Ordinal).ToList(), ["modules"] = frozen.Modules };
            files[CatalogManifest] = new UTF8Encoding(false).GetBytes(Json.Serialize(manifest));
            return files;
        }

        // A load: the frozen catalog read back from the package directory, each document against the catalog manifest's hash.
        public static Frozen PackageCatalog(string packageDirectory)
        {
            string path = Path.Combine(packageDirectory, CatalogManifest.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) throw new InvalidDataException("the world package keeps no catalog (made before MMR-283)");
            var manifest = (IDictionary<string, object>)Json.DeserializeObject(new UTF8Encoding(false).GetString(File.ReadAllBytes(path)));
            if ((string)manifest["schema"] != "mnbl.world-catalog" || Convert.ToInt32(manifest["version"], CultureInfo.InvariantCulture) != 1) throw new InvalidDataException("the world package's catalog is not an mnbl.world-catalog v1");
            var frozen = new Frozen { Defaults = ((IEnumerable)manifest["defaults"]).Cast<object>().Select(x => (string)x).ToList(),
                Modules = ((IEnumerable)manifest["modules"]).Cast<IDictionary<string, object>>().Select(m => m.ToDictionary(kv => kv.Key, kv => kv.Value)).ToList() };
            foreach (var kv in (IDictionary<string, object>)manifest["documents"])
            {
                var bytes = File.ReadAllBytes(Path.Combine(packageDirectory, "catalog", kv.Key + ".xml"));
                if (CatalogPin.Sha(bytes) != (string)kv.Value) throw new InvalidDataException("the world package's catalog document " + kv.Key + " changed");
                frozen.Documents[kv.Key] = bytes;
            }
            if (CatalogPin.Identity(frozen.Documents) != (string)manifest["identity"]) throw new InvalidDataException("the world package's catalog identity differs");
            return frozen;
        }

        // A load's check of the live catalog against the world: the installed objects it references still present, of their element, with
        // the read fields of its frozen copies; nothing added since that names an NPC the world does not supply. A missing object no active
        // module provides is put on a module that was active at activation and is not now, when there is one.
        public static List<CatalogPin.Conflict> LoadConflicts(Func<string, Func<byte[]>, byte[]> engine, World world, Frozen frozen)
        {
            var pin = InstalledPin();
            var live = CatalogPin.Parse(LiveDocuments(engine, frozen.Documents.Keys, pin.GameType));
            var conflicts = frozen.Documents.Keys.Where(id => !live.ContainsKey(id)).Select(id => new CatalogPin.Conflict { Kind = CatalogPin.MissingDocument, Document = id, Key = "Document." + id }).ToList();
            var frozenIndex = new CatalogPin.Index(CatalogPin.Parse(frozen.Documents)); var liveIndex = new CatalogPin.Index(live);
            conflicts.AddRange(CatalogPin.CheckReferences(frozenIndex, liveIndex, CatalogPin.WorldReferences(world.Dataset.Templates.Select(t => t.Reference), new HashSet<string>(frozen.Defaults, StringComparer.Ordinal))));
            var supplied = new HashSet<string>(world.Dataset.Documents.Single(d => d.Key == "NPCCharacters").Value.DocumentElement.ChildNodes.OfType<XmlElement>()
                .Where(e => e.HasAttribute("id")).Select(e => "NPCCharacter." + e.GetAttribute("id")), StringComparer.Ordinal);
            conflicts.AddRange(CatalogPin.AddedStock(frozenIndex, liveIndex, supplied));
            if (conflicts.Count == 0) return conflicts;
            var modules = ContentCompatibility.Modules();
            CatalogPin.Attribute(conflicts, modules);
            var gone = frozen.Modules.Where(m => !Equals(m["official"], true) && modules.Modules.All(a => a.Id != (string)m["id"])).Select(m => (string)m["id"]).FirstOrDefault();
            foreach (var c in conflicts.Where(c => c.Module == null && c.Kind == CatalogPin.Missing && gone != null)) { c.Module = gone; c.Detail = "provided by " + gone + ", active when the campaign was created and not now"; }
            return conflicts;
        }

        // The world's campaign, climate and terrain context from its bundle, its settlement layout report, its coordinate frame and its
        // catalog. Throws when the graph does not validate or the settlement records differ from the scene's placement.
        // MMR-257: navigationReport is the world's kept navigation report (its build.bridges are the decks the map dressing bridges); MMR-247:
        // the report NavigationGate.ReadAt returns from the persisted record, not the record.
        public static World Build(WorldDataBundle bundle, IDictionary<string, object> layout, PeriodicWorldCoordinates coordinates, WorldSurface surface, Frozen frozen,
            IDictionary<string, object> navigationReport)
        {
            var defaults = frozen.Defaults.ToList();
            var catalog = CatalogPin.Parse(frozen.Documents);
            var materials = (IDictionary<string, object>)Json.DeserializeObject(new UTF8Encoding(false).GetString(RuntimePlayabilityValidator.Embedded("terrain-materials.json")));
            var sources = new CompiledWorldDataset.Sources {
                Bundle = bundle.ReadVerified, Policy = GameActivationHost.PolicyBytes, Catalog = catalog, CatalogSha256 = CatalogPin.Identity(frozen.Documents), Defaults = defaults,
                MissionScenes = MNBL.ProceduralWorld.Missions.MissionScenes.Installed(), Layout = layout, OriginX = coordinates.OriginX, OriginY = coordinates.OriginY, Width = coordinates.Width };
            var dataset = CompiledWorldDataset.Build(sources);
            if (!dataset.Graph.Ok) throw new CampaignGraphValidator.GraphException(dataset.Graph);
            if (dataset.PositionMismatches.Count > 0) throw new InvalidDataException("The settlement records differ from the scene's placement: " + String.Join("; ", dataset.PositionMismatches.Take(5)));
            // MMR-39: the world's climate from its source, in its coordinate frame.
            byte[] worldJson = bundle.ReadVerified("source/world.json");
            GeneratedClimateSystem.Source source;
            using (var stream = new MemoryStream(worldJson)) source = GeneratedClimateSystem.ReadSource(stream, GeneratedClimateSystem.Sha(worldJson));
            var synthesis = GeneratedClimateSystem.Synthesize(source, coordinates, GeneratedClimateSystem.Policy.Installed());
            var climate = GeneratedClimate.FromSynthesis(synthesis, (float)surface.Extent);
            // MMR-257: the map dressing from the bundle, the kept navigation and the source's ruins (read in the same pass as the climate).
            var dressing = MapDressing.Build(bundle, surface, navigationReport, source, MNBL.ProceduralWorld.World.RoadsPolicy.Installed(), MNBL.ProceduralWorld.World.SettlementLooksPolicy.Installed());
            // MMR-42: the world's terrain context (biome, landform) at physical positions, for the battle map.
            var biomes = ((IEnumerable)materials["biomes"]).Cast<IDictionary<string, object>>().ToDictionary(b => Convert.ToInt32(b["id"], CultureInfo.InvariantCulture), b => (string)b["core"]);
            var landforms = ((IEnumerable)materials["landforms"]).Cast<IDictionary<string, object>>().ToDictionary(b => Convert.ToInt32(b["index"], CultureInfo.InvariantCulture), b => (string)b["name"]);
            Func<double, double, GeneratedBattleTerrain.TerrainContext> terrain = (x, y) =>
            {
                string b, l;
                return new GeneratedBattleTerrain.TerrainContext { Biome = biomes.TryGetValue(surface.BiomeAt(x, y), out b) ? b : null, Landform = landforms.TryGetValue(surface.LandformAt(x, y), out l) ? l : null };
            };
            // MMR-254: the road bit of the world's travel cells (cells.bin v2), by physical position.
            var graph = (IDictionary<string, object>)Json.DeserializeObject(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/travel/travel-graph.json")));
            var roads = MNBL.ProceduralWorld.World.RoadSpeed.Cells.FromGrid((IDictionary<string, object>)graph["grid"], bundle.ReadVerified("compiled/travel/cells.bin"));
            // MMR-260: the magic ground overlay from the source's magic layers (optional) over the world's travel cells.
            var grid = (IDictionary<string, object>)graph["grid"];
            Func<string, int> integer = key => Convert.ToInt32(grid[key], CultureInfo.InvariantCulture);
            var cells = TravelCells.Read(bundle.ReadVerified("compiled/travel/cells.bin"), integer("cols"), integer("rows"), integer("columnOffset"), integer("rowOffset"), Convert.ToDouble(grid["spacing"], CultureInfo.InvariantCulture));
            // MMR-247: with the dressing's lake planes (the scene's), so no land stamp centres on a shown plane.
            var overlay = new WorldSurfaceOverlay(MagicGroundSource.From(source, surface, cells, MNBL.ProceduralWorld.World.BiomeTable.Installed(), dressing == null ? null : dressing.Lakes));
            // MMR-261: the live mutation's inputs, all from the bundle: the accepted request (fwg apply regenerates the world from it), the capital
            // rule's tables and the producer build that generated the world (the build the campaign casts with, RT-37).
            MutationModel.Context mutation = null;
            if (overlay.Source.Enabled)
            {
                byte[] request = bundle.ReadVerified("source/request.json");
                var campaignDoc = (IDictionary<string, object>)Json.DeserializeObject(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/campaign/campaign.json")));
                var producer = (IDictionary<string, object>)((IDictionary<string, object>)bundle.Manifest["source"])["producer"];
                string bin = Path.GetDirectoryName(typeof(WorldReconstruction).Assembly.Location);
                mutation = new MutationModel.Context { Source = overlay.Source, Sites = MutationSites.From(campaignDoc, graph, source.Realms, surface.Strip, surface.PoleMargin),
                    Policy = MutationPolicy.Installed(), Archive = ProducerArchive.Installed(), Request = request, RequestSha256 = MNBL.ProceduralWorld.Surface.Json.Sha(request), WorldSha256 = source.Sha256,
                    ProducerSha256 = (string)producer["binarySha256"], Seed = source.Seed, ModuleRoot = Path.GetFullPath(Path.Combine(bin, "..", "..")),
                    WorkRoot = Path.Combine(SubModule.Store, "mutations"), Hash = MNBL.ProceduralWorld.Diagnostics.GenerationProbe.Hash };
                // MMR-247 (acc5 c30): the settlements the campaign start places beside the travel graph's (MMR-34's hideouts and the retirement
                // retreat; logical, as the dataset's intents, registered at logical + origin), which MMR-289's features keep clear of too.
                foreach (var h in dataset.Start.Hideouts) mutation.Sites.Placed.Add(new[] { h.X + coordinates.OriginX, h.Y + coordinates.OriginY });
                mutation.Sites.Placed.Add(new[] { dataset.Start.RetreatX + coordinates.OriginX, dataset.Start.RetreatY + coordinates.OriginY });
            }
            return new World { Dataset = dataset, Synthesis = synthesis, Climate = climate, TerrainContext = terrain, Roads = roads, CatalogSha256 = sources.CatalogSha256, CatalogDocuments = catalog.Count,
                CatalogBytes = frozen.Documents, Defaults = defaults, Dressing = dressing, Overlay = overlay, Mutation = mutation };
        }
    }
}
