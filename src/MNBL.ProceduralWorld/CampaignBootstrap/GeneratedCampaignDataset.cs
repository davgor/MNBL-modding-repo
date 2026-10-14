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
            public Placement(double townX, double townY, double gateX, double gateY,
                double portX, double portY, double villageX, double villageY)
            {
                TownX = townX; TownY = townY; GateX = gateX; GateY = gateY;
                PortX = portX; PortY = portY; VillageX = villageX; VillageY = villageY;
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
            var catalog = new Dictionary<string, Record>(StringComparer.Ordinal);
            var sourceDocs = new Dictionary<string, XmlDocument>(StringComparer.Ordinal);
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
            AddCharacter(docs["NPCCharacters"], heroTemplate.Element, "main_hero", "Traveler " + world);
            Add(docs["Heroes"], "Hero", "id", "main_hero", "faction", "Faction.player_faction");
            AddClan(docs["Factions"], "player_faction", "main_hero", world + "_town_1", culture.Key, null, "Traveler " + world, "0");
            generated.UnionWith(new[] { "NPCCharacter.main_hero", "Hero.main_hero", "Faction.player_faction" });
            foreach (string key in generated)
                if (catalog.ContainsKey(key) && key != "NPCCharacter.main_hero" && key != "Hero.main_hero" && key != "Faction.player_faction")
                    Fail("Generated identity collides with source: " + key);

            var used = new HashSet<string>(StringComparer.Ordinal);
            var provenance = new List<Provenance>();
            var optionalAbsent = new HashSet<string>(StringComparer.Ordinal);
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
            // These stock categories have already loaded before the registration hook. Include their
            // references even if this world's instances use only one culture; do not leave presumed NPCs.
            foreach (string source in new[] { "SPCultures", "partyTemplates", "EquipmentRosters", "WorkshopTypes", "LocationComplexTemplates" })
                foreach (var record in catalog.Values.Where(r => r.Source == source).OrderBy(r => r.Key, StringComparer.Ordinal)) visit(record.Key, "stock category " + source);
            visit(heroTemplate.Key, "generated ruler/player prototype");
            foreach (string id in options.EngineCharacterIds.OrderBy(x => x, StringComparer.Ordinal))
                visit("NPCCharacter." + id, "installed code string-literal lookup");
            foreach (var document in docs.Values)
                foreach (var element in Children(document.DocumentElement))
                    foreach (string dependency in Dependencies(element)) visit(dependency, "generated " + element.GetAttribute("id"));
            foreach (string reference in used.Where(k => k.StartsWith("NPCCharacter.", StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal))
            {
                var source = Require(catalog, reference).Element;
                // Generated player replaces the official definition, never both.
                docs["NPCCharacters"].DocumentElement.InsertBefore(docs["NPCCharacters"].ImportNode(source, true), docs["NPCCharacters"].DocumentElement.FirstChild);
            }
            var ordered = new List<Document>();
            foreach (string id in new[] { "SPCultures", "NPCCharacters", "Heroes", "Kingdoms", "Factions", "WorkshopTypes", "LocationComplexTemplates", "Settlements" })
                ordered.Add(new Document(id, docs.ContainsKey(id) ? docs[id] : sourceDocs[id]));
            var aliases = new[] { "NPCCharacter.main_hero", "Hero.main_hero", "Faction.player_faction", "MobileParty.player_party" };
            return new GeneratedCampaignDataset {
                WorldId = world, CultureId = options.CultureId, SceneName = "mnbl_generated_" + seed.ToString("x8", CultureInfo.InvariantCulture),
                Extent = options.Extent, StartX = options.First.GateX, StartY = options.First.GateY,
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
                DeclaredDefaultReferences = defaults.OrderBy(x => x, StringComparer.Ordinal).ToList().AsReadOnly()
            };
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
        private static void ValidatePrototype(Record r, string kind, string culture)
        {
            if (r.Element.GetAttribute("culture") != culture || r.Element.SelectSingleNode("Components/" + kind) == null ||
                r.Element.SelectSingleNode("Locations") == null) Fail("Incompatible " + kind + " prototype " + r.Key);
            var c = (XmlElement)r.Element.SelectSingleNode("Components/" + kind);
            if (kind == "Town" && True(c, "is_castle")) Fail("Town prototype is a castle: " + r.Key);
            if (Children((XmlElement)r.Element.SelectSingleNode("Components")).Count() != 1) Fail("Multiple prototype components unsupported: " + r.Key);
        }
        private static IEnumerable<string> Dependencies(XmlElement root)
        {
            foreach (XmlElement e in root.SelectNodes("descendant-or-self::*"))
                foreach (XmlAttribute a in e.Attributes)
                {
                    if (e == root && a.Name == "id") continue;
                    string value = a.Value;
                    var match = Typed.Match(value);
                    if (match.Success) { yield return value; continue; }
                    if (Regex.IsMatch(value, @"^[A-Z][A-Za-z0-9_]*\.", RegexOptions.CultureInvariant))
                        Fail("Malformed typed reference " + value + " at " + root.Name + "/" + e.Name + "/@" + a.Name);
                    if (new[] { "culture", "faction", "owner", "super_faction", "bound", "village_type", "complex_template", "skill_template", "troop" }.Contains(a.Name)
                        && value.Length != 0)
                        Fail("Expected a typed reference at " + root.Name + "/" + e.Name + "/@" + a.Name + ": " + value);
                    string prefix = null;
                    if (a.Name == "id" && e != root) {
                        switch (e.Name) {
                            case "EquipmentSet": prefix = "EquipmentRoster"; break;
                            case "skill": prefix = "?PropertyOwner.Deserialize IL_0063|Skill"; break;
                            case "Trait": prefix = "?PropertyOwner.Deserialize IL_0063|Trait"; break;
                            case "policy": prefix = "Policy"; break;
                            case "Building": prefix = "BuildingType"; break;
                            case "ShipSlot": prefix = "ShipSlot"; break;
                            case "ShipUpgradePiece": prefix = "ShipUpgradePiece"; break;
                            case "WeaponDescription": prefix = "WeaponDescription"; break;
                            case "AvailablePiece": prefix = root.Name == "WeaponDescription" ? "?WeaponDescription.Deserialize IL_01af|CraftingPiece" : "CraftingPiece"; break;
                        }
                    }
                    if (a.Name == "mission_ship") prefix = "MissionShip";
                    if (a.Name == "ship_physics_reference_id") prefix = "ShipPhysicsReference";
                    if (a.Name == "modifier_group") prefix = "ItemModifierGroup";
                    if (a.Name == "modifier_group" && e.ParentNode != null && e.ParentNode.Name == "ItemComponent")
                        prefix = "?ItemComponent.Deserialize IL_0037 / Equipment.GetRandomizedEquipment IL_0031 null guard|ItemModifierGroup";
                    if (a.Name == "crafting_template") prefix = "CraftingTemplate";
                    if (a.Name == "item_category") prefix = "ItemCategory";
                    if (a.Name == "base_monster") prefix = "Monster";
                    if (a.Name == "skeleton_scale") prefix = "SkeletonScale";
                    if (a.Name == "piece_id" || (e.Name == "Piece" && a.Name == "id")) prefix = "CraftingPiece";
                    if (a.Name == "piece_id" && e.Name == "UsablePiece" && root.Name == "CraftingTemplate") prefix = "?CraftingTemplate.Deserialize IL_0398|CraftingPiece";
                    // Despite its name, installed WorkshopType.Deserialize IL_0201 resolves ItemCategory.
                    if (a.Name == "input_item") {
                        if (e.Attributes[0] != a) Fail("Workshop input_item must be first: installed loader uses the first attribute value.");
                        prefix = "ItemCategory";
                    }
                    if (a.Name == "default_character_creation_body_property") prefix = "BodyProperty";
                    if (prefix != null && value.Length > 0) { CheckId(value); yield return prefix + "." + value; }
                }
        }
        private static string ElementType(string element)
        {
            if (element == "MBPartyTemplate") return "PartyTemplate";
            if (element == "CraftedItem") return "Item";
            // Explicit supported object catalog, including read-only world sources used for exclusion/prototypes.
            if (new[] { "NPCCharacter", "Hero", "Culture", "Kingdom", "Faction", "Settlement", "Item", "EquipmentRoster",
                "WorkshopType", "LocationComplexTemplate", "BodyProperty", "SkillSet", "ShipHull", "ShipSlot", "ShipUpgradePiece",
                "ShipPhysicsReference", "MissionShip", "CraftingPiece", "CraftingTemplate", "WeaponDescription", "Monster",
                "ItemCategory", "Skeleton", "SkeletonScale", "BannerIcon", "MusicInstrument", "MusicTrack", "ItemModifier", "ItemModifierGroup", "SiegeEngineType" }.Contains(element)) return element;
            Fail("Unsupported source object element " + element + "; supply its explicit schema before accepting it."); return null;
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
