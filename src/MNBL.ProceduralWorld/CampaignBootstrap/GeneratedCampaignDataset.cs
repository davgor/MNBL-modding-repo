using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // Pure managed preflight. No singleton access, registry writes, scene loading or save support.
    // mergedSources must be stock GetMergedXmlForManaged(id,false,false,gameType) results,
    // not concatenated module files: active-module, game-type and XSLT filtering matter.
    public sealed class GeneratedCampaignDataset
    {
        public sealed class Placement
        {
            public double TownX { get; private set; } public double TownY { get; private set; }
            public double GateX { get; private set; } public double GateY { get; private set; }
            public double PortX { get; private set; } public double PortY { get; private set; }
            public double VillageX { get; private set; } public double VillageY { get; private set; }
            // MMR-42: a harbour village's port (the placement's harbour village; its scene carries a drop-off entity, so the campaign gives
            // it a port): the fixture village is built with it, so it takes a coastal, seaborne-raid capable prototype. Null: inland.
            public double? VillagePortX { get; private set; } public double? VillagePortY { get; private set; }
            public Placement(double townX, double townY, double gateX, double gateY,
                double portX, double portY, double villageX, double villageY, double? villagePortX = null, double? villagePortY = null)
            {
                TownX = townX; TownY = townY; GateX = gateX; GateY = gateY;
                PortX = portX; PortY = portY; VillageX = villageX; VillageY = villageY; VillagePortX = villagePortX; VillagePortY = villagePortY;
            }
        }
        public sealed class Options
        {
            public string CultureId { get; private set; } public string HeroTemplateId { get; private set; }
            public string TownPrototypeId { get; private set; } public string VillagePrototypeId { get; private set; }
            public double Extent { get; private set; }
            public Placement First { get; private set; } public Placement Second { get; private set; }
            // Reusable NPCCharacter IDs that installed code looks up by string literal. The reference
            // closure cannot discover them; each still passes the template/world-hero classification.
            public ReadOnlyCollection<string> EngineCharacterIds { get; private set; }
            // MMR-33: when set, the first world's settlements are built by SettlementFactory (the world's own builder) and gain a castle,
            // a castle-bound village and a hideout: the in-game settlement fixtures.
            public SettlementFixtures Fixtures { get; set; }
            // MMR-34: when set (requires Fixtures), the first world also supplies the startup: the installed land bandit clans as world
            // aliases homed at hideouts, the retirement retreat, every creation culture's start at the fixture town's gate, and a runtime
            // plan of notables, companions and a relation (records built by GeneratedCampaignInitializer's shared builders).
            public StartupFixtures Startup { get; set; }
            // MMR-35: when set (requires Fixtures), the first world also carries the naval fixtures: the installed pirate clans as world aliases
            // homed at the fixture hideout (records from NavalContentFactory's builder), and the fleet rule (FixtureNavalPlan).
            public bool Naval { get; set; }
            public Options(string cultureId, string heroTemplateId, string townPrototypeId,
                string villagePrototypeId, double extent, Placement first, Placement second,
                IEnumerable<string> engineCharacterIds = null)
            {
                CultureId = cultureId; HeroTemplateId = heroTemplateId;
                TownPrototypeId = townPrototypeId; VillagePrototypeId = villagePrototypeId;
                Extent = extent; First = first; Second = second;
                EngineCharacterIds = (engineCharacterIds ?? new string[0]).ToList().AsReadOnly();
            }
        }
        // MMR-33 fixtures: the economy policy and prototypes, and three land points of the generated map (from the placement's own
        // navigation tests) for the castle, its village and the hideout.
        public sealed class SettlementFixtures
        {
            public IDictionary<string, object> Economy { get; private set; }
            public IDictionary<string, object> Prototypes { get; private set; }
            public double CastleX { get; private set; } public double CastleY { get; private set; }
            public double VillageX { get; private set; } public double VillageY { get; private set; }
            public double HideoutX { get; private set; } public double HideoutY { get; private set; }
            // MMR-42: the mission scene catalogue the fixture factory checks its picks against (null offline: no contract check).
            internal MNBL.ProceduralWorld.Missions.MissionScenes MissionScenes { get; private set; }
            internal SettlementFixtures(IDictionary<string, object> economy, IDictionary<string, object> prototypes, double castleX, double castleY,
                double villageX, double villageY, double hideoutX, double hideoutY, MNBL.ProceduralWorld.Missions.MissionScenes missionScenes = null)
            {
                Economy = economy; Prototypes = prototypes; CastleX = castleX; CastleY = castleY; VillageX = villageX; VillageY = villageY; HideoutX = hideoutX; HideoutY = hideoutY;
                MissionScenes = missionScenes;
            }
        }
        // MMR-34 startup fixtures: verified land points of the generated map for the retirement retreat and for one hideout of each
        // bandit culture the fixture hideout (forest) does not cover (desert, mountain, sea raiders, steppe).
        public sealed class StartupFixtures
        {
            public double RetreatX { get; private set; } public double RetreatY { get; private set; }
            public ReadOnlyCollection<double[]> HideoutPoints { get; private set; }
            public StartupFixtures(double retreatX, double retreatY, IEnumerable<double[]> hideoutPoints)
            {
                RetreatX = retreatX; RetreatY = retreatY; HideoutPoints = hideoutPoints.ToList().AsReadOnly();
            }
        }
        public sealed class Document
        {
            public string Id { get; private set; } public string Xml { get; private set; }
            public string Sha256 { get; private set; }
            internal Document(string id, XmlDocument document) { Id = id; Xml = document.OuterXml; Sha256 = Hash(Xml); }
            public XmlDocument CreateDocument() { return Parse(Xml); }
        }
        public sealed class Provenance
        {
            public string Reference { get; private set; } public string Source { get; private set; }
            public string SourceSha256 { get; private set; } public string RecordSha256 { get; private set; }
            public string Reason { get; private set; }
            internal Provenance(string reference, string source, string sourceHash, string recordHash, string reason)
            { Reference = reference; Source = source; SourceSha256 = sourceHash; RecordSha256 = recordHash; Reason = reason; }
        }
        public sealed class PartySpecification
        {
            public string Id { get; private set; } public string HeroId { get; private set; }
            public string HomeId { get; private set; } public string ClanId { get; private set; }
            public double X { get; private set; } public double Y { get; private set; }
            internal PartySpecification(string id, string hero, string home, string clan, Placement p)
            { Id = id; HeroId = hero; HomeId = home; ClanId = clan; X = p.GateX; Y = p.GateY; }
        }
        public string WorldId { get; private set; } public string CultureId { get; private set; }
        public string SceneName { get; private set; }
        public double StartX { get; private set; } public double StartY { get; private set; }
        public double Extent { get; private set; }
        public ReadOnlyCollection<Document> Documents { get; private set; }
        public ReadOnlyCollection<Provenance> Templates { get; private set; }
        public ReadOnlyCollection<Provenance> Prototypes { get; private set; }
        public ReadOnlyCollection<string> RequiredAliases { get; private set; }
        public ReadOnlyCollection<string> Instances { get; private set; }
        public ReadOnlyCollection<PartySpecification> StartingParties { get; private set; }
        public ReadOnlyCollection<string> Limitations { get; private set; }
        public ReadOnlyCollection<string> OptionalReferencesAbsent { get; private set; }
        // Hash of sorted, LF-separated typed default IDs, not proof of live registry availability.
        public string DefaultReferencesSha256 { get; private set; }
        public ReadOnlyCollection<string> DeclaredDefaultReferences { get; private set; }
        // MMR-33 fixtures: settlement id -> the vanilla map prefab its entity is made from (the generated scene has none for them).
        public ReadOnlyDictionary<string, string> FixtureEntityPrefabs { get; private set; }
        // MMR-34: the runtime plan GeneratedStartupBehavior instantiates (null without startup fixtures).
        internal MNBL.ProceduralWorld.CampaignRegistry.GeneratedCampaignInitializer.StartupPlan StartupPlan { get; private set; }
        // MMR-34: every creation culture's planned start point (culture -> x, y) and town, and the installed start points none may keep
        // (empty without startup fixtures; without them only the selected culture is re-emitted, at StartX/StartY).
        public ReadOnlyDictionary<string, double[]> StartPoints { get; private set; }
        public string StartTown { get; private set; }
        public ReadOnlyDictionary<string, double[]> InstalledStartPoints { get; private set; }
        // MMR-35: the fleet rule of a world with naval fixtures (null without them).
        internal MNBL.ProceduralWorld.CampaignRegistry.NavalContentFactory.NavalPlan NavalPlan { get; private set; }
        // The fixture world's fleet rule. Both towns are port towns on the fixture's one sea, so by NavalContentFactory.Roles' rule no clan is
        // landlocked; the fixture declares clan 2 landlocked anyway, so the in-game audit sees the rule act on a real clan through the new
        // game's ship production and trade (clan 1 and the player stay coastal).
        internal static MNBL.ProceduralWorld.CampaignRegistry.NavalContentFactory.NavalPlan FixtureNavalPlan(string world)
        {
            var plan = new MNBL.ProceduralWorld.CampaignRegistry.NavalContentFactory.NavalPlan();
            plan.Landlocked.Add(world + "_clan_2");
            return plan;
        }
        private GeneratedCampaignDataset() { }

        private sealed class Record
        {
            internal string Key, Source, SourceHash; internal XmlElement Element;
        }
        // Installed v1.4.8.119303 / NavalDLC v1.2.8.119303 IL loads these reusable characters by literal ID
        // (e.g. BodyGeneratorView..ctor dereferences facgen_template_test_char_0..9 without a null check).
        // Scan, consumers and assembly hashes: build/research-bootstrap18/engine-character-literals.json.
        // Official hero instances found by the same scan are deliberately excluded.
        public static readonly ReadOnlyCollection<string> InstalledEngineCharacterIds = Array.AsReadOnly(new[] {
            "betting_fraud_thug_female", "betting_fraud_thug_male", "borrowed_troop", "captivity_troops",
            "company_of_trouble_character", "cutscene_midwife", "cutscene_monk", "disguise_contractor_character",
            "disguise_default_character", "disguise_officer_character", "disguise_shadow_target",
            "facgen_template_test_char_0", "facgen_template_test_char_1", "facgen_template_test_char_2",
            "facgen_template_test_char_3", "facgen_template_test_char_4", "facgen_template_test_char_5",
            "facgen_template_test_char_6", "facgen_template_test_char_7", "facgen_template_test_char_8",
            "facgen_template_test_char_9", "gangster_1", "gangster_2", "gangster_3",
            "gear_practice_dummy_empire", "mercenary_8", "naval_storyline_alley_fight_enemy",
            "npc_merchant_equipment_empire", "poacher", "sea_hound_captivity", "sp_hermit", "stealth_character",
            "veteran_borrowed_troop" });
        private static readonly Regex Identifier = new Regex(@"^[a-zA-Z0-9_-]+$", RegexOptions.CultureInvariant);
        private static readonly Regex Typed = new Regex(@"^([A-Za-z][A-Za-z0-9_]*)\.([A-Za-z0-9_-]+)$", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> WorldTypes = new HashSet<string>(new[] {
            "Hero", "Faction", "Clan", "Kingdom", "Settlement", "Town", "Village", "MobileParty" }, StringComparer.Ordinal);
        // Only these default object kinds may be asserted by the caller's live read-only census.
        private static readonly HashSet<string> DefaultTypes = new HashSet<string>(new[] {
            "Skill", "Trait", "Policy", "BuildingType", "VillageType", "ItemCategory", "Feat", "CharacterAttribute", "SiegeEngineType", "SkeletonScale" }, StringComparer.Ordinal);

        // MMR-19: counts dataset builds in this process, so a cold load can prove it never regenerated a world.
        public static int BuildCount { get; private set; }

        public static GeneratedCampaignDataset Build(uint seed, Options options,
            IDictionary<string, XmlDocument> mergedSources, IEnumerable<string> verifiedDefaultReferences)
        {
            BuildCount++;
            if (options == null || mergedSources == null || verifiedDefaultReferences == null)
                throw new ArgumentNullException("Dataset options, merged sources and verified defaults are required.");
            foreach (var id in new[] { options.CultureId, options.HeroTemplateId, options.TownPrototypeId, options.VillagePrototypeId }.Concat(options.EngineCharacterIds))
                CheckId(id);
            if (!Finite(options.Extent) || options.Extent <= 0) Fail("Extent must be finite and positive.");
            ValidatePlacement(options.First, options.Extent); ValidatePlacement(options.Second, options.Extent);
            if (options.First.TownX == options.Second.TownX && options.First.TownY == options.Second.TownY)
                Fail("The two towns must have distinct positions.");
            if (options.First.PortX == options.Second.PortX && options.First.PortY == options.Second.PortY)
                Fail("The two ports must have distinct positions.");
            var defaults = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in verifiedDefaultReferences)
            {
                var match = Typed.Match(reference ?? "");
                if (!match.Success || !DefaultTypes.Contains(match.Groups[1].Value))
                    Fail("Not an allowed engine-default reference: " + reference);
                defaults.Add(reference);
            }
            Dictionary<string, XmlDocument> sourceDocs;
            var catalog = Catalog(mergedSources, out sourceDocs);
            var culture = Require(catalog, "Culture." + options.CultureId);
            var heroTemplate = Require(catalog, "NPCCharacter." + options.HeroTemplateId);
            if (!True(heroTemplate.Element, "is_template") || heroTemplate.Element.GetAttribute("occupation") != "Lord" ||
                heroTemplate.Element.GetAttribute("culture") != culture.Key || catalog.ContainsKey("Hero." + options.HeroTemplateId))
                Fail("Hero prototype must be an explicit reusable Lord template of the selected culture, not an official hero: " + heroTemplate.Key);
            var town = Require(catalog, "Settlement." + options.TownPrototypeId);
            var village = Require(catalog, "Settlement." + options.VillagePrototypeId);
            ValidatePrototype(town, "Town", culture.Key); ValidatePrototype(village, "Village", culture.Key);
            if (town.Element.SelectSingleNode("Locations/Location[@id='port']") == null)
                Fail("Town prototype lacks a port location: " + town.Key);

            string world = "mnbl_" + seed.ToString("x8", CultureInfo.InvariantCulture);
            var docs = new Dictionary<string, XmlDocument>(StringComparer.Ordinal);
            foreach (string root in new[] { "SPCultures", "NPCCharacters", "Heroes", "Kingdoms", "Factions", "Settlements" }) docs.Add(root, NewDocument(root));
            var selectedCulture = (XmlElement)docs["SPCultures"].ImportNode(culture.Element, true);
            selectedCulture.SetAttribute("start_point_position_x", Number(options.First.GateX));
            selectedCulture.SetAttribute("start_point_position_y", Number(options.First.GateY));
            docs["SPCultures"].DocumentElement.AppendChild(selectedCulture);
            var generated = new HashSet<string>(StringComparer.Ordinal);
            var parties = new List<PartySpecification>();
            for (int i = 1; i <= 2; i++)
            {
                string suffix = i.ToString(CultureInfo.InvariantCulture), hero = world + "_ruler_" + suffix,
                    clan = world + "_clan_" + suffix, kingdom = world + "_kingdom_" + suffix,
                    townId = world + "_town_" + suffix, villageId = world + "_village_" + suffix;
                Placement p = i == 1 ? options.First : options.Second;
                AddCharacter(docs["NPCCharacters"], heroTemplate.Element, hero, "Ruler " + suffix + " " + world);
                Add(docs["Heroes"], "Hero", "id", hero, "faction", "Faction." + clan);
                Add(docs["Kingdoms"], "Kingdom", "id", kingdom, "name", "Realm " + suffix + " " + world,
                    "short_name", "Realm " + suffix, "culture", culture.Key, "owner", "Hero." + hero,
                    "initial_home_settlement", "Settlement." + townId, "color", "FF39223F", "color2", "FFDE9953",
                    "banner_key", Banner, "primary_banner_color", "0xff793191", "secondary_banner_color", "0xffFCDE90");
                AddClan(docs["Factions"], clan, hero, townId, culture.Key, kingdom, "House " + suffix + " " + world, "3");
                AddSettlement(docs["Settlements"], town.Element, townId, "Harbor " + suffix + " " + world, culture.Key, clan, null, p);
                parties.Add(new PartySpecification(world + "_party_" + suffix, hero, townId, clan, p));
                foreach (string key in new[] { "NPCCharacter." + hero, "Hero." + hero, "Faction." + clan,
                    "Kingdom." + kingdom, "Settlement." + townId, "Town." + townId + "_component" }) generated.Add(key);
                // Villages are appended below, after both towns have fully deserialized.
                generated.Add("Settlement." + villageId); generated.Add("Village." + villageId + "_component");
            }
            for (int i = 1; i <= 2; i++)
            {
                string suffix = i.ToString(CultureInfo.InvariantCulture);
                AddSettlement(docs["Settlements"], village.Element, world + "_village_" + suffix,
                    "Village " + suffix + " " + world, culture.Key, null, world + "_town_" + suffix,
                    i == 1 ? options.First : options.Second);
            }
            var fixturePrefabs = new Dictionary<string, string>(StringComparer.Ordinal);
            double[] townGate = null;
            if (options.Fixtures != null) townGate = AddFixtures(docs["Settlements"], options, world, sourceDocs["Settlements"], defaults, catalog, generated, fixturePrefabs);
            MNBL.ProceduralWorld.CampaignRegistry.GeneratedCampaignInitializer.StartupPlan startupPlan = null;
            var startPoints = new Dictionary<string, double[]>(StringComparer.Ordinal); var installedStarts = new Dictionary<string, double[]>(StringComparer.Ordinal);
            double startX = options.First.GateX, startY = options.First.GateY;
            if (options.Startup != null)
            {
                if (options.Fixtures == null) Fail("Startup fixtures need the settlement fixtures (the bandit clans' home is the fixture hideout).");
                startupPlan = AddStartupFixtures(docs, options, world, sourceDocs, catalog, generated, defaults, fixturePrefabs, townGate, startPoints, installedStarts);
                startX = townGate[0]; startY = townGate[1];
            }
            MNBL.ProceduralWorld.CampaignRegistry.NavalContentFactory.NavalPlan navalPlan = null;
            if (options.Naval)
            {
                if (options.Fixtures == null) Fail("Naval fixtures need the settlement fixtures (the pirate clans' home is the fixture hideout).");
                foreach (var clan in new[] { "northern_pirates", "southern_pirates" })
                {
                    var record = sourceDocs["Factions"].DocumentElement.SelectSingleNode("Faction[@id='" + clan + "']") as XmlElement;
                    if (record == null) Fail("Installed pirate clan " + clan + " is missing.");
                    docs["Factions"].DocumentElement.AppendChild(MNBL.ProceduralWorld.CampaignRegistry.GeneratedCampaignInitializer.BanditClanRecord(docs["Factions"], record, world + "_hideout_1"));
                    generated.Add("Faction." + clan);
                }
                navalPlan = FixtureNavalPlan(world);
            }
            AddCharacter(docs["NPCCharacters"], heroTemplate.Element, "main_hero", "Traveler " + world);
            Add(docs["Heroes"], "Hero", "id", "main_hero", "faction", "Faction.player_faction");
            AddClan(docs["Factions"], "player_faction", "main_hero", world + "_town_1", culture.Key, null, "Traveler " + world, "0");
            generated.UnionWith(new[] { "NPCCharacter.main_hero", "Hero.main_hero", "Faction.player_faction" });
            // An alias (the player, and with startup fixtures the bandit clans and the retreat) replaces the official definition.
            foreach (string key in generated)
                if (catalog.ContainsKey(key) && !MNBL.ProceduralWorld.CampaignRegistry.CampaignIdentity.IsAlias(key.Substring(key.IndexOf('.') + 1)))
                    Fail("Generated identity collides with source: " + key);

            var roots = StockRoots(catalog).Concat(new[] { new KeyValuePair<string, string>(heroTemplate.Key, "generated ruler/player prototype") })
                .Concat(options.EngineCharacterIds.OrderBy(x => x, StringComparer.Ordinal).Select(id => new KeyValuePair<string, string>("NPCCharacter." + id, "installed code string-literal lookup")))
                .Concat(startupPlan == null ? new KeyValuePair<string, string>[0] :
                    startupPlan.Notables.Concat(startupPlan.Companions).Select(h => h.Template).Distinct().OrderBy(x => x, StringComparer.Ordinal).Select(t => new KeyValuePair<string, string>("NPCCharacter." + t, "MMR-34 startup plan template")));
            var closure = Close(catalog, generated, defaults, roots, docs.Values);
            var provenance = closure.Provenance; var optionalAbsent = closure.OptionalAbsent;
            EmitTemplates(docs["NPCCharacters"], catalog, closure);
            var ordered = new List<Document>();
            foreach (string id in new[] { "SPCultures", "NPCCharacters", "Heroes", "Kingdoms", "Factions", "WorkshopTypes", "LocationComplexTemplates", "Settlements" })
                ordered.Add(new Document(id, docs.ContainsKey(id) ? docs[id] : sourceDocs[id]));
            var aliases = new[] { "NPCCharacter.main_hero", "Hero.main_hero", "Faction.player_faction", "MobileParty.player_party" }
                .Concat(generated.Where(k => MNBL.ProceduralWorld.CampaignRegistry.CampaignIdentity.IsAlias(k.Substring(k.IndexOf('.') + 1)) && !k.EndsWith(".main_hero", StringComparison.Ordinal) && k != "Faction.player_faction"))
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            return new GeneratedCampaignDataset {
                WorldId = world, CultureId = options.CultureId, SceneName = "mnbl_generated_" + seed.ToString("x8", CultureInfo.InvariantCulture),
                Extent = options.Extent, StartX = startX, StartY = startY,
                Documents = ordered.AsReadOnly(), Templates = provenance.OrderBy(p => p.Reference, StringComparer.Ordinal).ToList().AsReadOnly(),
                Prototypes = new[] { town, village, heroTemplate }.Select(r => new Provenance(r.Key, r.Source, r.SourceHash, Hash(r.Element.OuterXml),
                    "structure/assets only; new identity and geography; source world record is not registered")).ToList().AsReadOnly(),
                RequiredAliases = Array.AsReadOnly(aliases), Instances = generated.OrderBy(x => x, StringComparer.Ordinal).ToList().AsReadOnly(),
                StartingParties = parties.AsReadOnly(), Limitations = Array.AsReadOnly(new[] {
                    "New-game only: no save-load/pre-deserialization safety claim.",
                    "Coordinates are bounds-checked, not navigability-verified; no scene markers or nav caches are copied.",
                    "StartingParties are creation specifications, not instantiated parties; reconcile with stock creation to avoid duplicates.",
                    "Native mesh/scene/prefab/material existence and scene location compatibility require separate runtime validation.",
                    "Stock world-specific behaviors/start positions require root integration; this builder never removes behaviors.",
                    "Preflight validates modeled XML object-reference fields, not every possible future/modded XML schema; unrecognized source categories fail closed."
                }), OptionalReferencesAbsent = optionalAbsent.OrderBy(x => x, StringComparer.Ordinal).ToList().AsReadOnly(),
                DefaultReferencesSha256 = Hash(String.Join("\n", defaults.OrderBy(x => x, StringComparer.Ordinal))),
                DeclaredDefaultReferences = defaults.OrderBy(x => x, StringComparer.Ordinal).ToList().AsReadOnly(),
                FixtureEntityPrefabs = new ReadOnlyDictionary<string, string>(fixturePrefabs),
                StartupPlan = startupPlan, NavalPlan = navalPlan, StartPoints = new ReadOnlyDictionary<string, double[]>(startPoints), StartTown = startupPlan == null ? null : world + "_town_1",
                InstalledStartPoints = new ReadOnlyDictionary<string, double[]>(installedStarts)
            };
        }
        // MMR-33: the first world's town and village rebuilt by SettlementFactory.Element (same ids, owner and positions), plus a castle of
        // the same clan, a village bound to the castle and a forest-bandit hideout, each from an installed prototype of its culture and kind.
        // Returns the fixture town's gate.
        private static double[] AddFixtures(XmlDocument settlements, Options options, string world, XmlDocument installed, HashSet<string> defaults,
            Dictionary<string, Record> catalog, HashSet<string> generated, Dictionary<string, string> prefabs)
        {
            var f = options.Fixtures;
            var factory = new MNBL.ProceduralWorld.CampaignRegistry.SettlementFactory(f.Economy, f.Prototypes, installed, key => defaults.Contains(key) || catalog.ContainsKey(key), f.MissionScenes);
            var p = options.First;
            string town = world + "_town_1", village = world + "_village_1", castle = world + "_castle_1", castleVillage = world + "_village_3", hideout = world + "_hideout_1", clan = world + "_clan_1";
            // Gates distinct from the centres (the world's settlements stage the gate at the centre until MMR-40 places gates), so the
            // in-game check can tell a gate from a centre: 0.3 of a cell (1.875) inside the centre's own verified land cell, the town's
            // toward its port, the castle's east.
            const double gateOffset = 1.875;
            double toPort = Math.Sqrt((p.PortX - p.TownX) * (p.PortX - p.TownX) + (p.PortY - p.TownY) * (p.PortY - p.TownY));
            if (!(toPort > gateOffset)) Fail("Fixture town port is not away from the town centre.");
            double townGateX = p.TownX + gateOffset * (p.PortX - p.TownX) / toPort, townGateY = p.TownY + gateOffset * (p.PortY - p.TownY) / toPort;
            // MMR-40: a placement whose gates the settlement layout resolved (tools/Build-FixtureSettlements.ps1) gives the town its resolved gate.
            if (p.GateX != p.TownX || p.GateY != p.TownY) { townGateX = p.GateX; townGateY = p.GateY; }
            var intents = new[] {
                factory.Fixture(town, "town", "Harbor 1 " + world, options.CultureId, clan, null, p.TownX, p.TownY, townGateX, townGateY, p.PortX, p.PortY, 3600, 0, null),
                factory.Fixture(castle, "castle", "Castle 1 " + world, options.CultureId, clan, null, f.CastleX, f.CastleY, f.CastleX + gateOffset, f.CastleY, null, null, 700, 0, null),
                factory.Fixture(village, "village", "Village 1 " + world, options.CultureId, null, town, p.VillageX, p.VillageY, p.VillageX, p.VillageY, p.VillagePortX, p.VillagePortY, 0, 300, "wheat_farm"),
                factory.Fixture(castleVillage, "village", "Village 3 " + world, options.CultureId, null, castle, f.VillageX, f.VillageY, f.VillageX, f.VillageY, null, null, 0, 250, "cattle_farm"),
                factory.Hideout(hideout, "Hideout 1 " + world, "forest_bandits", f.HideoutX, f.HideoutY) };
            foreach (var id in new[] { town, village })
            {
                var old = settlements.DocumentElement.SelectSingleNode("Settlement[@id='" + id + "']");
                if (old == null) Fail("Fixture settlement " + id + " was not built by the dataset.");
                settlements.DocumentElement.RemoveChild(old);
            }
            // Towns and castles first, then the villages bound to them, then the hideout (the MMR-30 ReadyAt rule).
            var first = settlements.DocumentElement.FirstChild;
            foreach (var s in intents.Where(x => x.Kind == "town" || x.Kind == "castle"))
                settlements.DocumentElement.InsertBefore(MNBL.ProceduralWorld.CampaignRegistry.SettlementFactory.Element(settlements, s, factory.Prototype(s.Prototype)), first);
            foreach (var s in intents.Where(x => x.Kind == "village" || x.Kind == "hideout"))
                settlements.DocumentElement.AppendChild(MNBL.ProceduralWorld.CampaignRegistry.SettlementFactory.Element(settlements, s, factory.Prototype(s.Prototype)));
            generated.UnionWith(new[] { "Settlement." + castle, "Town." + castle + "_component", "Settlement." + castleVillage, "Village." + castleVillage + "_component",
                                        "Settlement." + hideout, "Hideout." + hideout + "_component" });
            foreach (var s in intents.Where(x => x.Id == castle || x.Id == castleVillage || x.Id == hideout))
            {
                if (s.MapPrefab == null) Fail("Fixture settlement " + s.Id + " prototype " + s.Prototype + " has no map prefab.");
                prefabs[s.Id] = s.MapPrefab;
            }
            return new[] { townGateX, townGateY };
        }
        // MMR-34: what the startup supplies, on the fixture world, through GeneratedCampaignInitializer's shared record builders and rules.
        // One hideout of every bandit culture BanditSpawnCampaignBehavior spawns for (the fixture forest hideout and four more; it reads its
        // hideouts-by-culture map unchecked, run 37036); the installed land bandit clans (world aliases) homed by the initializer's rule
        // (the first hideout of their culture, else the first: looters and deserters at the forest one); the installed retirement retreat
        // (alias) at a verified land point with the nearest town's culture; every creation culture re-emitted with its start at the fixture
        // town's gate (so no installed start point survives); and a runtime plan built as the MMR-32 rules build the full world's: notables
        // meeting the installed targets (town: 2 merchants, 2 gang leaders, an artisan; village: a headman and 2 rural notables) from the
        // culture's installed notable templates, the companions the installed loop creates (0.6 x towns) from its installed wanderer
        // templates, and one ruler relation.
        private static MNBL.ProceduralWorld.CampaignRegistry.GeneratedCampaignInitializer.StartupPlan AddStartupFixtures(Dictionary<string, XmlDocument> docs, Options options,
            string world, Dictionary<string, XmlDocument> sourceDocs, Dictionary<string, Record> catalog, HashSet<string> generated, HashSet<string> defaults,
            Dictionary<string, string> prefabs, double[] townGate, Dictionary<string, double[]> startPoints, Dictionary<string, double[]> installedStarts)
        {
            var factory = new MNBL.ProceduralWorld.CampaignRegistry.SettlementFactory(options.Fixtures.Economy, options.Fixtures.Prototypes, sourceDocs["Settlements"],
                key => defaults.Contains(key) || catalog.ContainsKey(key), options.Fixtures.MissionScenes);
            var hideouts = new List<MNBL.ProceduralWorld.CampaignRegistry.SettlementFactory.SettlementIntent> {
                factory.Hideout(world + "_hideout_1", "Hideout 1 " + world, "forest_bandits", options.Fixtures.HideoutX, options.Fixtures.HideoutY) };   // emitted by AddFixtures
            var extra = new[] { "desert_bandits", "mountain_bandits", "sea_raiders", "steppe_bandits" };
            if (options.Startup.HideoutPoints.Count != extra.Length) Fail("Startup fixtures need " + extra.Length + " hideout points.");
            for (int i = 0; i < extra.Length; i++)
            {
                string id = world + "_hideout_" + (i + 2).ToString(CultureInfo.InvariantCulture);
                var p = options.Startup.HideoutPoints[i];
                var h = factory.Hideout(id, "Hideout " + (i + 2) + " " + world, extra[i], p[0], p[1]);
                docs["Settlements"].DocumentElement.AppendChild(MNBL.ProceduralWorld.CampaignRegistry.SettlementFactory.Element(docs["Settlements"], h, factory.Prototype(h.Prototype)));
                generated.UnionWith(new[] { "Settlement." + id, "Hideout." + id + "_component" });
                if (h.MapPrefab == null) Fail("Fixture hideout " + id + " prototype " + h.Prototype + " has no map prefab.");
                prefabs[id] = h.MapPrefab;
                hideouts.Add(h);
            }
            foreach (var clan in new[] { "looters", "deserters", "sea_raiders", "mountain_bandits", "forest_bandits", "desert_bandits", "steppe_bandits" })
            {
                var record = sourceDocs["Factions"].DocumentElement.SelectSingleNode("Faction[@id='" + clan + "']") as XmlElement;
                if (record == null) Fail("Installed bandit clan " + clan + " is missing.");
                var home = MNBL.ProceduralWorld.CampaignRegistry.GeneratedCampaignInitializer.BanditHome(record.GetAttribute("culture").Split('.').Last(), hideouts);
                docs["Factions"].DocumentElement.AppendChild(MNBL.ProceduralWorld.CampaignRegistry.GeneratedCampaignInitializer.BanditClanRecord(docs["Factions"], record, home));
                generated.Add("Faction." + clan);
            }
            var retreat = sourceDocs["Settlements"].DocumentElement.SelectSingleNode("Settlement[@id='retirement_retreat']") as XmlElement;
            if (retreat == null) Fail("The installed retirement retreat is missing.");
            var towns = new[] { 1, 2 }.Select(i => { var p = i == 1 ? options.First : options.Second;
                return new MNBL.ProceduralWorld.CampaignRegistry.SettlementFactory.SettlementIntent { Id = world + "_town_" + i, Culture = options.CultureId, X = p.TownX, Y = p.TownY }; }).ToList();
            string retreatCulture = MNBL.ProceduralWorld.CampaignRegistry.GeneratedCampaignInitializer.NearestTownCulture(options.Startup.RetreatX, options.Startup.RetreatY, towns,
                (x1, y1, x2, y2) => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2)));
            docs["Settlements"].DocumentElement.AppendChild(MNBL.ProceduralWorld.CampaignRegistry.GeneratedCampaignInitializer.RetreatRecord(docs["Settlements"], retreat,
                options.Startup.RetreatX, options.Startup.RetreatY, retreatCulture));
            generated.Add("Settlement.retirement_retreat"); generated.Add("RetirementSettlementComponent.retirement_component");
            // Every creation culture starts at the fixture town's gate (the selected culture's own record is replaced).
            var creation = MNBL.ProceduralWorld.CampaignRegistry.GeneratedCampaignInitializer.CreationCultures(sourceDocs["SPCultures"]);
            if (!creation.Contains(options.CultureId)) Fail("The selected culture " + options.CultureId + " is not a creation culture.");
            foreach (var old in docs["SPCultures"].DocumentElement.ChildNodes.OfType<XmlElement>().ToList()) docs["SPCultures"].DocumentElement.RemoveChild(old);
            foreach (var id in creation)
            {
                var record = (XmlElement)sourceDocs["SPCultures"].DocumentElement.SelectSingleNode("Culture[@id='" + id + "']");
                installedStarts[id] = new[] { Double.Parse(record.GetAttribute("start_point_position_x"), CultureInfo.InvariantCulture), Double.Parse(record.GetAttribute("start_point_position_y"), CultureInfo.InvariantCulture) };
                docs["SPCultures"].DocumentElement.AppendChild(MNBL.ProceduralWorld.CampaignRegistry.GeneratedCampaignInitializer.StartCultureRecord(docs["SPCultures"], record, townGate[0], townGate[1]));
                startPoints[id] = new[] { townGate[0], townGate[1] };
            }

            var culture = sourceDocs["SPCultures"].DocumentElement.SelectSingleNode("Culture[@id='" + options.CultureId + "']") as XmlElement;
            var notableTemplates = culture == null ? new List<string>() : culture.SelectNodes("notable_templates/template").OfType<XmlElement>().Select(t => t.GetAttribute("name").Split('.').Last()).ToList();
            var characters = sourceDocs["NPCCharacters"].DocumentElement.ChildNodes.OfType<XmlElement>().ToDictionary(e => e.GetAttribute("id"), StringComparer.Ordinal);
            Func<string, string> templateOf = occupation => {
                var id = notableTemplates.FirstOrDefault(t => characters.ContainsKey(t) && characters[t].GetAttribute("occupation") == occupation);
                if (id == null) Fail("Culture " + options.CultureId + " has no installed " + occupation + " notable template.");
                return id; };
            var plan = new MNBL.ProceduralWorld.CampaignRegistry.GeneratedCampaignInitializer.StartupPlan();
            var targets = new Dictionary<string, string[]> {
                ["town"] = new[] { "Merchant", "Merchant", "GangLeader", "GangLeader", "Artisan" },
                ["village"] = new[] { "Headman", "RuralNotable", "RuralNotable" } };
            var homes = new[] { "town_1", "town_2", "village_1", "village_2", "village_3" };
            foreach (var home in homes)
            {
                var occupations = targets[home.StartsWith("town_", StringComparison.Ordinal) ? "town" : "village"];
                for (int i = 0; i < occupations.Length; i++)
                    plan.Notables.Add(new MNBL.ProceduralWorld.CampaignRegistry.GeneratedCampaignInitializer.HeroPlan {
                        Id = "fixture-notable:" + home + ":" + i, Uid = "notable:" + world + "_" + home + ":" + occupations[i] + ":" + i, Template = templateOf(occupations[i]),
                        Settlement = world + "_" + home, Name = occupations[i] + " " + (i + 1) + " of " + home.Replace('_', ' '), Occupation = occupations[i], Age = 35 + 3 * i });
            }
            var wanderers = characters.Values.Where(e => e.GetAttribute("occupation") == "Wanderer" && e.GetAttribute("is_template") == "true" && e.GetAttribute("culture") == "Culture." + options.CultureId)
                .Select(e => e.GetAttribute("id")).OrderBy(x => x, StringComparer.Ordinal).ToList();
            int companions = 0;
            for (int i = 0; (float)i < 0.6f * 2; i++) companions++;   // the installed loop over 2 towns
            if (wanderers.Count < companions) Fail("Culture " + options.CultureId + " has fewer installed wanderer templates than companions.");
            for (int i = 0; i < companions; i++)
                plan.Companions.Add(new MNBL.ProceduralWorld.CampaignRegistry.GeneratedCampaignInitializer.HeroPlan {
                    Id = "fixture-companion:" + i, Uid = "companion:" + i, Template = wanderers[i], Settlement = world + "_town_" + (i + 1),
                    Name = "Wanderer " + (i + 1) + " " + world, Occupation = "Wanderer", Age = 28 + i });
            plan.Relations.Add(new MNBL.ProceduralWorld.CampaignRegistry.GeneratedCampaignInitializer.RelationPlan { A = world + "_ruler_1", B = world + "_ruler_2", Value = -23 });
            return plan;
        }
        private const string Banner = "11.4.4.4345.4345.764.764.1.0.0.163.5.5.512.512.764.764.1.0.0";
        private static void AddClan(XmlDocument doc, string id, string hero, string home, string culture, string kingdom, string name, string tier)
        {
            var e = Add(doc, "Faction", "id", id, "owner", "Hero." + hero, "name", name, "short_name", name,
                "culture", culture, "initial_home_settlement", "Settlement." + home, "tier", tier,
                "is_noble", "true", "color", "FF39223F", "color2", "FFDE9953", "banner_key", Banner);
            if (kingdom != null) e.SetAttribute("super_faction", "Kingdom." + kingdom);
        }
        private static void AddCharacter(XmlDocument doc, XmlElement prototype, string id, string name)
        {
            var e = (XmlElement)doc.ImportNode(prototype, true);
            e.SetAttribute("id", id); e.SetAttribute("name", name); e.SetAttribute("is_hero", "true");
            e.SetAttribute("is_template", "false"); e.SetAttribute("age", "30");
            foreach (string attr in new[] { "faction", "clan", "father", "mother", "spouse" }) e.RemoveAttribute(attr);
            doc.DocumentElement.AppendChild(e);
        }
        private static void AddSettlement(XmlDocument doc, XmlElement prototype, string id, string name, string culture,
            string owner, string bound, Placement p)
        {
            bool isTown = bound == null;
            var e = Add(doc, "Settlement", "id", id, "name", name, "culture", culture,
                "posX", Number(isTown ? p.TownX : p.VillageX), "posY", Number(isTown ? p.TownY : p.VillageY));
            if (isTown) {
                e.SetAttribute("owner", "Faction." + owner); e.SetAttribute("gate_posX", Number(p.GateX)); e.SetAttribute("gate_posY", Number(p.GateY));
                e.SetAttribute("port_posX", Number(p.PortX)); e.SetAttribute("port_posY", Number(p.PortY));
            }
            var components = doc.CreateElement("Components"); e.AppendChild(components);
            var component = (XmlElement)doc.ImportNode(prototype.SelectSingleNode(isTown ? "Components/Town" : "Components/Village"), true);
            component.SetAttribute("id", id + "_component");
            if (!isTown) component.SetAttribute("bound", "Settlement." + bound);
            components.AppendChild(component);
            foreach (string child in new[] { "Locations", "CommonAreas" }) {
                var node = prototype.SelectSingleNode(child); if (node != null) e.AppendChild(doc.ImportNode(node, true));
            }
        }
        // The merged installed documents as records by typed key (Type.id), and their parsed copies.
        private static Dictionary<string, Record> Catalog(IDictionary<string, XmlDocument> mergedSources, out Dictionary<string, XmlDocument> sourceDocs)
        {
            var catalog = new Dictionary<string, Record>(StringComparer.Ordinal);
            sourceDocs = new Dictionary<string, XmlDocument>(StringComparer.Ordinal);
            foreach (var source in mergedSources.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (source.Value == null || source.Value.DocumentElement == null || source.Value.DocumentType != null)
                    Fail("Missing root or forbidden DTD in merged source " + source.Key);
                var doc = Parse(source.Value.OuterXml); sourceDocs.Add(source.Key, doc);
                string expectedRoot = source.Key == "SiegeEngines" ? "SiegeEngineTypes" : source.Key;
                if (doc.DocumentElement.Name != expectedRoot && !(source.Key == "partyTemplates" && doc.DocumentElement.Name == "MBPartyTemplates"))
                    Fail("Merged source/root mismatch: " + source.Key + " / " + doc.DocumentElement.Name);
                string sourceHash = Hash(doc.OuterXml);
                foreach (var element in Children(doc.DocumentElement))
                {
                    if (!element.HasAttribute("id")) continue;
                    string type = ElementType(element.Name);
                    string id = element.GetAttribute("id"); CheckId(id);
                    string key = type + "." + id;
                    if (catalog.ContainsKey(key)) Fail("Duplicate merged object " + key + "; supply stock merged XML, not concatenated records.");
                    catalog.Add(key, new Record { Key = key, Source = source.Key, SourceHash = sourceHash, Element = element });
                }
            }
            foreach (string required in new[] { "NPCCharacters", "Heroes", "SPCultures", "partyTemplates", "EquipmentRosters", "Settlements", "WorkshopTypes", "LocationComplexTemplates" })
                if (!sourceDocs.ContainsKey(required)) Fail("Required merged source missing: " + required);
            return catalog;
        }

        // The installed records a world's documents need re-emitted (MMR-18): every reference reachable from the roots (in order) and from the
        // generated documents' records, through the installed loaders' reference schema, minus the world's own objects and the verified engine
        // defaults. Official world instances (lords, kingdoms, settlements) are refused, as is an official hero kept as a template.
        private sealed class Closure
        {
            internal readonly HashSet<string> Used = new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<Provenance> Provenance = new List<Provenance>();
            internal readonly HashSet<string> OptionalAbsent = new HashSet<string>(StringComparer.Ordinal);
        }
        private static Closure Close(Dictionary<string, Record> catalog, HashSet<string> generated, HashSet<string> defaults, IEnumerable<KeyValuePair<string, string>> roots, IEnumerable<XmlDocument> generatedDocs)
        {
            var result = new Closure();
            var used = result.Used; var provenance = result.Provenance; var optionalAbsent = result.OptionalAbsent;
            var cultureRoles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in catalog.Values.Where(r => r.Source == "SPCultures"))
                foreach (XmlAttribute attribute in record.Element.Attributes)
                    if (attribute.Value.StartsWith("NPCCharacter.", StringComparison.Ordinal)) cultureRoles.Add(attribute.Value);
            Action<string, string> visit = null;
            visit = (reference, reason) =>
            {
                if (reference.StartsWith("?", StringComparison.Ordinal)) {
                    int separator = reference.IndexOf('|');
                    string optionalContract = reference.Substring(1, separator - 1);
                    reference = reference.Substring(separator + 1);
                    if (!catalog.ContainsKey(reference) && !defaults.Contains(reference)) {
                        optionalAbsent.Add(reference + " from " + reason + "; installed " + optionalContract + " permits a missing target");
                        return;
                    }
                }
                if (generated.Contains(reference)) return;
                string type = reference.Split('.')[0];
                if (WorldTypes.Contains(type)) Fail("Official world-instance dependency forbidden: " + reference + " from " + reason);
                if (!used.Add(reference)) return;
                Record record;
                if (!catalog.TryGetValue(reference, out record))
                {
                    if (!defaults.Contains(reference)) Fail("Unresolved dependency " + reference + " from " + reason);
                    provenance.Add(new Provenance(reference, "caller-verified-default-declarations; live readiness not asserted",
                        Hash(String.Join("\n", defaults.OrderBy(x => x, StringComparer.Ordinal))), "", reason)); return;
                }
                if (type == "NPCCharacter" && (catalog.ContainsKey("Hero." + record.Element.GetAttribute("id")) ||
                    (True(record.Element, "is_hero") && !True(record.Element, "is_template") && !cultureRoles.Contains(reference))))
                    Fail("Named/world hero cannot be retained as a reusable NPC template: " + reference + " from " + reason);
                provenance.Add(new Provenance(reference, record.Source, record.SourceHash, Hash(record.Element.OuterXml), reason +
                    (type == "NPCCharacter" && True(record.Element, "is_hero") && !True(record.Element, "is_template")
                    ? "; explicit culture NPC role, no matching Hero instance (is_hero alone is not a live-world classification)" : "")));
                foreach (string dependency in Dependencies(record.Element)) visit(dependency, reference);
            };
            foreach (var root in roots) visit(root.Key, root.Value);
            foreach (var document in generatedDocs)
                foreach (var element in Children(document.DocumentElement))
                    foreach (string dependency in Dependencies(element)) visit(dependency, "generated " + element.GetAttribute("id"));
            return result;
        }

        // These stock categories have already loaded before the registration hook. Include their references even if a world's instances use
        // only some cultures; do not leave presumed NPCs.
        private static IEnumerable<KeyValuePair<string, string>> StockRoots(Dictionary<string, Record> catalog)
        {
            foreach (string source in new[] { "SPCultures", "partyTemplates", "EquipmentRosters", "WorkshopTypes", "LocationComplexTemplates" })
                foreach (var record in catalog.Values.Where(r => r.Source == source).OrderBy(r => r.Key, StringComparer.Ordinal))
                    yield return new KeyValuePair<string, string>(record.Key, "stock category " + source);
        }

        // Re-emits every used installed NPC template at the head of the generated NPCCharacters document (a generated alias replaces the
        // official definition, never both).
        private static void EmitTemplates(XmlDocument characters, Dictionary<string, Record> catalog, Closure closure)
        {
            foreach (string reference in closure.Used.Where(k => k.StartsWith("NPCCharacter.", StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal))
            {
                var source = Require(catalog, reference).Element;
                characters.DocumentElement.InsertBefore(characters.ImportNode(source, true), characters.DocumentElement.FirstChild);
            }
        }

        // MMR-45: a compiled world's documents for the new game, in RegistrationPlan.NewGameOrder: its own documents (SPCultures with the
        // creation cultures' generated start points, NPCCharacters, Heroes, Kingdoms, Factions, Settlements: the factories' records, merged)
        // with the installed records they need re-emitted by the same closure as the fixture world (stock categories, the engine's
        // literal NPC lookups, the startup plan's templates, every generated record's references), and the installed WorkshopTypes and
        // LocationComplexTemplates whole. generated: the world's declared objects (Type.id); an official key among them must be an alias.
        internal static List<KeyValuePair<string, XmlDocument>> CompiledWorldDocuments(IDictionary<string, XmlDocument> mergedSources, IEnumerable<string> verifiedDefaultReferences,
            IDictionary<string, XmlDocument> worldDocuments, IEnumerable<string> generatedKeys, IEnumerable<string> planTemplates, out List<Provenance> templates, out List<string> optionalAbsent)
        {
            var defaults = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in verifiedDefaultReferences)
            {
                var match = Typed.Match(reference ?? "");
                if (!match.Success || !DefaultTypes.Contains(match.Groups[1].Value)) Fail("Not an allowed engine-default reference: " + reference);
                defaults.Add(reference);
            }
            Dictionary<string, XmlDocument> sourceDocs;
            var catalog = Catalog(mergedSources, out sourceDocs);
            var generated = new HashSet<string>(generatedKeys, StringComparer.Ordinal);
            foreach (string key in generated)
                if (catalog.ContainsKey(key) && !MNBL.ProceduralWorld.CampaignRegistry.CampaignIdentity.IsAlias(key.Substring(key.IndexOf('.') + 1)))
                    Fail("Generated identity collides with source: " + key);
            var docs = new Dictionary<string, XmlDocument>(StringComparer.Ordinal);
            foreach (string root in new[] { "SPCultures", "NPCCharacters", "Heroes", "Kingdoms", "Factions", "Settlements" })
            {
                XmlDocument d;
                if (!worldDocuments.TryGetValue(root, out d) || d == null || d.DocumentElement == null || d.DocumentElement.Name != root) Fail("The compiled world has no " + root + " document.");
                docs[root] = Parse(d.OuterXml);
            }
            var roots = StockRoots(catalog)
                .Concat(InstalledEngineCharacterIds.OrderBy(x => x, StringComparer.Ordinal).Select(id => new KeyValuePair<string, string>("NPCCharacter." + id, "installed code string-literal lookup")))
                .Concat(planTemplates.Distinct().OrderBy(x => x, StringComparer.Ordinal).Select(t => new KeyValuePair<string, string>("NPCCharacter." + t, "MMR-34 startup plan template")));
            var closure = Close(catalog, generated, defaults, roots, docs.Values);
            EmitTemplates(docs["NPCCharacters"], catalog, closure);
            templates = closure.Provenance.OrderBy(x => x.Reference, StringComparer.Ordinal).ToList();
            optionalAbsent = closure.OptionalAbsent.OrderBy(x => x, StringComparer.Ordinal).ToList();
            return MNBL.ProceduralWorld.CampaignRegistry.RegistrationPlan.NewGameOrder
                .Select(id => new KeyValuePair<string, XmlDocument>(id, docs.ContainsKey(id) ? docs[id] : Parse(sourceDocs[id].OuterXml))).ToList();
        }

        private static void ValidatePrototype(Record r, string kind, string culture)
        {
            if (r.Element.GetAttribute("culture") != culture || r.Element.SelectSingleNode("Components/" + kind) == null ||
                r.Element.SelectSingleNode("Locations") == null) Fail("Incompatible " + kind + " prototype " + r.Key);
            var c = (XmlElement)r.Element.SelectSingleNode("Components/" + kind);
            if (kind == "Town" && True(c, "is_castle")) Fail("Town prototype is a castle: " + r.Key);
            if (Children((XmlElement)r.Element.SelectSingleNode("Components")).Count() != 1) Fail("Multiple prototype components unsupported: " + r.Key);
        }
        // The installed loaders' references (shared with CampaignGraphValidator, MMR-36); a malformed value fails the preflight.
        private static IEnumerable<string> Dependencies(XmlElement root)
        {
            return MNBL.ProceduralWorld.CampaignRegistry.ReferenceSchema.Dependencies(root, Fail).ToList();
        }
        private static string ElementType(string element)
        {
            var type = MNBL.ProceduralWorld.CampaignRegistry.ReferenceSchema.ElementType(element);
            if (type == null) Fail("Unsupported source object element " + element + "; supply its explicit schema before accepting it.");
            return type;
        }
        private static Record Require(Dictionary<string, Record> catalog, string key)
        { Record r; if (!catalog.TryGetValue(key, out r)) Fail("Required source object missing: " + key); return r; }
        private static void ValidatePlacement(Placement p, double extent)
        {
            if (p == null) Fail("Both placements are required.");
            foreach (double x in new[] { p.TownX, p.TownY, p.GateX, p.GateY, p.PortX, p.PortY, p.VillageX, p.VillageY })
                if (!Finite(x) || x <= 0 || x >= extent) Fail("Every placement coordinate must be strictly inside (0,extent).");
            if ((p.TownX == p.PortX && p.TownY == p.PortY) || (p.GateX == p.PortX && p.GateY == p.PortY))
                Fail("Port must be distinct from the land town/gate.");
        }
        private static XmlDocument NewDocument(string root) { var d = new XmlDocument { XmlResolver = null }; d.AppendChild(d.CreateElement(root)); return d; }
        private static XmlElement Add(XmlDocument d, string tag, params string[] attributes)
        { var e = d.CreateElement(tag); for (int i = 0; i < attributes.Length; i += 2) e.SetAttribute(attributes[i], attributes[i + 1]); d.DocumentElement.AppendChild(e); return e; }
        private static IEnumerable<XmlElement> Children(XmlElement e) { return e.ChildNodes.OfType<XmlElement>(); }
        private static XmlDocument Parse(string xml)
        {
            var d = new XmlDocument { XmlResolver = null };
            using (var r = XmlReader.Create(new System.IO.StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) d.Load(r);
            return d;
        }
        private static bool True(XmlElement e, string name) { return String.Equals(e.GetAttribute(name), "true", StringComparison.OrdinalIgnoreCase); }
        private static bool Finite(double x) { return !Double.IsNaN(x) && !Double.IsInfinity(x); }
        private static string Number(double x) { return x.ToString("R", CultureInfo.InvariantCulture); }
        private static void CheckId(string id) { if (String.IsNullOrEmpty(id) || !Identifier.IsMatch(id)) Fail("Invalid object ID: " + id); }
        private static string Hash(string text) { using (var sha = SHA256.Create()) return String.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2", CultureInfo.InvariantCulture))); }
        private static void Fail(string message) { throw new InvalidOperationException("Generated campaign preflight: " + message); }
    }
}
