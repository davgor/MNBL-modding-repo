using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // The dataset options of an in-game fixture world from its placement (mnbl.bootstrap-placement.v1). Pure managed: shared by
    // GeneratedCampaignRegistration.Prepare and the offline graph tests (MMR-36), so the offline fixture datasets are the in-game ones.
    internal static class FixturePlacement
    {
        // economy/prototypes: the MMR-33 settlement fixture policies (null without settlement fixtures); startup and naval need them.
        public static GeneratedCampaignDataset.Options Options(IDictionary<string, object> placement, uint seed, string culture, string heroTemplate,
            string townPrototype, string villagePrototype, IDictionary<string, object> economy, IDictionary<string, object> prototypes, bool startupFixtures, bool navalFixtures,
            MNBL.ProceduralWorld.Missions.MissionScenes missionScenes = null)
        {
            if (Text(placement, "schema") != "mnbl.bootstrap-placement.v1") throw new InvalidDataException("Wrong generated placement schema.");
            string world = "mnbl_" + seed.ToString("x8", CultureInfo.InvariantCulture);
            string sceneName = "mnbl_generated_" + seed.ToString("x8", CultureInfo.InvariantCulture);
            if (Text(placement, "worldId") != world || Text(placement, "sceneName") != sceneName)
                throw new InvalidDataException("Placement identity differs from requested seed.");
            var settlements = ((IEnumerable)placement["settlements"]).Cast<IDictionary<string, object>>().ToArray();
            if (settlements.Length != 4 || settlements.Select(s => Text(s, "id")).Distinct().Count() != 4)
                throw new InvalidDataException("Expected exactly four distinct generated settlements.");
            var first = GetPlacement(settlements, world, 1); var second = GetPlacement(settlements, world, 2);
            double extent = Convert.ToDouble(placement["extent"], CultureInfo.InvariantCulture);
            GeneratedCampaignDataset.SettlementFixtures fixtures = null;
            if (economy != null)
            {
                // Land points the placement builder verified on this map: the obstruction detour's end, and the lake's two shores.
                var tests = (IDictionary<string, object>)placement["navigationTests"];
                var to = Coordinates(((IDictionary<string, object>)tests["obstruction"])["to"]);
                var lake = (IDictionary<string, object>)tests["lake"];
                var east = Coordinates(lake["eastShore"]); var west = Coordinates(lake["westShore"]);
                fixtures = new GeneratedCampaignDataset.SettlementFixtures(economy, prototypes, to[0], to[1], east[0], east[1], west[0], west[1], missionScenes);
            }
            GeneratedCampaignDataset.StartupFixtures startup = null;
            if (startupFixtures)
            {
                if (fixtures == null) throw new InvalidDataException("Startup fixtures need the settlement fixtures.");
                // The retreat and four hideouts at cells of the obstruction stamp: verified steppe land cells of the main landmass (cells
                // are [column, row]; the probe that disables the stamp's faces never runs with the startup fixtures): the retreat at the
                // first, the hideouts at the second, fourth, sixth and eighth.
                var cells = ((IEnumerable)((IDictionary<string, object>)((IDictionary<string, object>)placement["navigationTests"])["obstruction"])["cells"]).Cast<object>().Select(Coordinates).ToList();
                if (cells.Count < 8) throw new InvalidDataException("The placement's obstruction stamp has fewer than 8 cells.");
                double spacing = NavSpacing(placement);
                Func<double[], double[]> centre = c => new[] { (c[0] + 0.5) * spacing, (c[1] + 0.5) * spacing };
                startup = new GeneratedCampaignDataset.StartupFixtures(centre(cells[0])[0], centre(cells[0])[1], new[] { centre(cells[1]), centre(cells[3]), centre(cells[5]), centre(cells[7]) });
            }
            if (navalFixtures && fixtures == null) throw new InvalidDataException("Naval fixtures need the settlement fixtures.");
            return new GeneratedCampaignDataset.Options(culture, heroTemplate, townPrototype, villagePrototype, extent, first, second,
                GeneratedCampaignDataset.InstalledEngineCharacterIds) { Fixtures = fixtures, Startup = startup, Naval = navalFixtures };
        }

        // MMR-36: the graph validation input of a fixture world, shared by the registration gate and the offline graph tests: the documents
        // about to load, the dataset's declared objects, runtime plan and positions (creation start points, starting party gates), and the
        // identity of what it was built from (the graph policy, the catalog receipt, the live defaults and, with settlement fixtures, the
        // economy policy and settlement prototypes).
        public static MNBL.ProceduralWorld.CampaignRegistry.CampaignGraphValidator.Input GraphInput(uint seed, GeneratedCampaignDataset dataset,
            IEnumerable<KeyValuePair<string, System.Xml.XmlDocument>> documents, IDictionary<string, System.Xml.XmlDocument> catalog, IEnumerable<string> defaults,
            string graphPolicySha256, string catalogReceiptSha256, string economySha256, string prototypesSha256)
        {
            var policies = new SortedDictionary<string, string>(StringComparer.Ordinal) {
                [MNBL.ProceduralWorld.CampaignRegistry.CampaignGraphValidator.PolicyPath] = graphPolicySha256,
                [MNBL.ProceduralWorld.CampaignRegistry.CampaignGraphValidator.CatalogReceiptKey] = catalogReceiptSha256,
                [MNBL.ProceduralWorld.CampaignRegistry.CampaignGraphValidator.LiveDefaultsKey] = MNBL.ProceduralWorld.CampaignRegistry.CampaignGraphValidator.InventorySha256(defaults) };
            if (economySha256 != null) policies["worldgen/policies/economy.json"] = economySha256;
            if (prototypesSha256 != null) policies["worldgen/templates/settlements/prototypes.json"] = prototypesSha256;
            var spawns = dataset.StartPoints.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new MNBL.ProceduralWorld.CampaignRegistry.CampaignGraphValidator.SpawnPoint { Label = "start point of " + p.Key, X = p.Value[0], Y = p.Value[1] })
                .Concat(dataset.StartingParties.Select(p => new MNBL.ProceduralWorld.CampaignRegistry.CampaignGraphValidator.SpawnPoint { Label = "starting party " + p.Id, X = p.X, Y = p.Y }));
            return MNBL.ProceduralWorld.CampaignRegistry.CampaignGraphValidator.FixtureInput(MNBL.ProceduralWorld.CampaignRegistry.CampaignIdentity.WorldPrefixOfSeed(seed), documents,
                dataset.Instances.Concat(dataset.RequiredAliases), dataset.StartupPlan, catalog, defaults, policies, spawns);
        }

        public static double NavSpacing(IDictionary<string, object> placement)
        {
            return Convert.ToDouble(((IDictionary<string, object>)placement["nav"])["spacing"], CultureInfo.InvariantCulture);
        }

        private static GeneratedCampaignDataset.Placement GetPlacement(IDictionary<string, object>[] settlements, string world, int ordinal)
        {
            string suffix = ordinal.ToString(CultureInfo.InvariantCulture);
            var town = settlements.Single(s => Text(s, "id") == world + "_town_" + suffix);
            var village = settlements.Single(s => Text(s, "id") == world + "_village_" + suffix);
            if (Text(town, "kind") != "town" || Text(village, "kind") != "village" || Text(village, "boundTo") != Text(town, "id"))
                throw new InvalidDataException("Generated village/town ownership contract differs.");
            var p = Point(town, "position"); var g = Point(town, "gatePosition");
            var water = Point(town, "portPosition"); var v = Point(village, "position");
            // MMR-42: a harbour village (MMR-40's placements) keeps its port, so its prototype is a coastal, seaborne-raid capable one.
            object harbour, villagePort;
            bool isHarbour = village.TryGetValue("harbour", out harbour) && harbour is bool && (bool)harbour && village.TryGetValue("portPosition", out villagePort) && villagePort != null;
            var vp = isHarbour ? Point(village, "portPosition") : null;
            return new GeneratedCampaignDataset.Placement(p[0], p[1], g[0], g[1], water[0], water[1], v[0], v[1], vp == null ? (double?)null : vp[0], vp == null ? (double?)null : vp[1]);
        }

        private static double[] Point(IDictionary<string, object> record, string key)
        {
            var result = ((IEnumerable)record[key]).Cast<object>().Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray();
            if (result.Length != 3 || result.Any(v => Double.IsNaN(v) || Double.IsInfinity(v))) throw new InvalidDataException("Invalid placement point: " + key);
            return result;
        }

        private static double[] Coordinates(object value)
        {
            var result = ((IEnumerable)value).Cast<object>().Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray();
            if (result.Length < 2 || result.Any(v => Double.IsNaN(v) || Double.IsInfinity(v))) throw new InvalidDataException("Invalid fixture point.");
            return result;
        }

        private static string Text(IDictionary<string, object> record, string key) { return Convert.ToString(record[key], CultureInfo.InvariantCulture); }
    }
}
