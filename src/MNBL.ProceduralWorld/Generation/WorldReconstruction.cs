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
    // and at load (GeneratedWorldLoader): the campaign graph against the pinned installed catalog (CompiledWorldDataset.Build, managed and
    // deterministic: no generator process), the climate from the source in the world's coordinate frame, and the terrain context battles
    // read. A load then requires the rebuilt documents to be the package's byte for byte, so a world is never restored from anything but its
    // own immutable inputs.
    internal static class WorldReconstruction
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };

        internal sealed class World
        {
            public CompiledWorldDataset Dataset;
            public GeneratedClimateSystem.Result Synthesis;
            public GeneratedClimate Climate;
            public Func<double, double, GeneratedBattleTerrain.TerrainContext> TerrainContext;
            public string CatalogSha256;
            public int CatalogDocuments;
        }

        // The installed campaign catalog the world's policies were made from: every pinned document merged by the engine (engine runs a
        // function on the engine thread; its label names the step), each equal to its pinned hash. Throws when the installation differs.
        public static Dictionary<string, XmlDocument> Catalog(Func<string, Func<byte[]>, byte[]> engine, out byte[] pinBytes)
        {
            pinBytes = RuntimePlayabilityValidator.Embedded("campaign-catalog.json");
            if (pinBytes == null) throw new InvalidDataException("The module carries no campaign catalog pin.");
            var pin = (IDictionary<string, object>)Json.DeserializeObject(new UTF8Encoding(false).GetString(pinBytes));
            var pinned = (IDictionary<string, object>)pin["documents"];
            var catalog = new Dictionary<string, XmlDocument>(StringComparer.Ordinal);
            var differs = new List<string>();
            foreach (var id in pinned.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                string documentId = id, gameType = (string)pin["gameType"];
                var bytes = engine("catalog " + id, () =>
                {
                    var merged = MBObjectManager.GetMergedXmlForManaged(documentId, false, false, gameType);
                    if (merged == null || merged.DocumentElement == null || merged.DocumentType != null) throw new InvalidDataException("The merged campaign document " + documentId + " is unavailable.");
                    return new UTF8Encoding(false).GetBytes(merged.OuterXml);
                });
                if (CompiledWorldDataset.Sha256(bytes) != (string)pinned[id]) { differs.Add(id); continue; }
                var doc = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) doc.Load(reader);
                catalog[id] = doc;
            }
            if (differs.Count > 0) throw new InvalidDataException("The installed campaign catalog is not the one the world's policies were made from (another game version or mod changes " + String.Join(", ", differs) + ").");
            return catalog;
        }

        // The world's campaign, climate and terrain context from its bundle, its settlement layout report and its coordinate frame. Throws when
        // the graph does not validate or the settlement records differ from the scene's placement.
        public static World Build(WorldDataBundle bundle, IDictionary<string, object> layout, PeriodicWorldCoordinates coordinates, WorldSurface surface, Dictionary<string, XmlDocument> catalog, byte[] pinBytes)
        {
            var pin = (IDictionary<string, object>)Json.DeserializeObject(new UTF8Encoding(false).GetString(pinBytes));
            var defaults = ((IEnumerable)pin["defaults"]).Cast<object>().Select(x => (string)x).ToList();
            var materials = (IDictionary<string, object>)Json.DeserializeObject(new UTF8Encoding(false).GetString(RuntimePlayabilityValidator.Embedded("terrain-materials.json")));
            var sources = new CompiledWorldDataset.Sources {
                Bundle = bundle.ReadVerified, Policy = GameActivationHost.PolicyBytes, Catalog = catalog, CatalogSha256 = CompiledWorldDataset.Sha256(pinBytes), Defaults = defaults,
                MissionScenes = MNBL.ProceduralWorld.Missions.MissionScenes.Installed(), Layout = layout, OriginX = coordinates.OriginX, OriginY = coordinates.OriginY, Width = coordinates.Width };
            var dataset = CompiledWorldDataset.Build(sources);
            if (!dataset.Graph.Ok) throw new CampaignGraphValidator.GraphException(dataset.Graph);
            if (dataset.PositionMismatches.Count > 0) throw new InvalidDataException("The settlement records differ from the scene's placement: " + String.Join("; ", dataset.PositionMismatches.Take(5)));
            // MMR-39: the world's climate from its source, in its coordinate frame.
            byte[] worldJson = bundle.ReadVerified("source/world.json");
            GeneratedClimateSystem.Source source;
            using (var stream = new MemoryStream(worldJson)) source = GeneratedClimateSystem.ReadSource(stream, GeneratedClimateSystem.Sha(worldJson));
            var synthesis = GeneratedClimateSystem.Synthesize(source, coordinates);
            var climate = GeneratedClimate.FromSynthesis(synthesis, (float)surface.Extent);
            // MMR-42: the world's terrain context (biome, landform) at physical positions, for the battle map.
            var biomes = ((IEnumerable)materials["biomes"]).Cast<IDictionary<string, object>>().ToDictionary(b => Convert.ToInt32(b["id"], CultureInfo.InvariantCulture), b => (string)b["core"]);
            var landforms = ((IEnumerable)materials["landforms"]).Cast<IDictionary<string, object>>().ToDictionary(b => Convert.ToInt32(b["index"], CultureInfo.InvariantCulture), b => (string)b["name"]);
            Func<double, double, GeneratedBattleTerrain.TerrainContext> terrain = (x, y) =>
            {
                string b, l;
                return new GeneratedBattleTerrain.TerrainContext { Biome = biomes.TryGetValue(surface.BiomeAt(x, y), out b) ? b : null, Landform = landforms.TryGetValue(surface.LandformAt(x, y), out l) ? l : null };
            };
            return new World { Dataset = dataset, Synthesis = synthesis, Climate = climate, TerrainContext = terrain, CatalogSha256 = sources.CatalogSha256, CatalogDocuments = catalog.Count };
        }
    }
}
