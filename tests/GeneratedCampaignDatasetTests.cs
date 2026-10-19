using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Xml;
using MNBL.ProceduralWorld.CampaignBootstrap;
using Dataset = MNBL.ProceduralWorld.CampaignBootstrap.GeneratedCampaignDataset;

internal static class GeneratedCampaignDatasetTests
{
    private static int checks;
    private static void Check(bool condition, string label) { checks++; if (!condition) throw new Exception(label); }
    private static XmlDocument Xml(string value) { var d = new XmlDocument { XmlResolver = null }; d.LoadXml(value); return d; }
    private static Dataset.Options Options(double extent = 1040) {
        return new Dataset.Options("empire", "lord_template", "official_town", "official_village", extent,
            new Dataset.Placement(100, 100, 102, 100, 105, 100, 110, 110),
            new Dataset.Placement(200, 200, 202, 200, 205, 200, 210, 210));
    }
    private static Dictionary<string, XmlDocument> Sources() {
        return new Dictionary<string, XmlDocument> {
            { "NPCCharacters", Xml("<NPCCharacters><NPCCharacter id='lord_template' is_template='true' is_hero='false' occupation='Lord' culture='Culture.empire' skill_template='SkillSet.lord_skills'><face><face_key_template value='BodyProperty.face'/></face><Equipments><EquipmentSet id='lord_equipment'/></Equipments></NPCCharacter><NPCCharacter id='recruit' culture='Culture.empire'><upgrade_targets><upgrade_target id='NPCCharacter.soldier'/></upgrade_targets></NPCCharacter><NPCCharacter id='soldier' culture='Culture.empire'/><NPCCharacter id='official_lord' is_hero='true' culture='Culture.empire'/><NPCCharacter id='unreferenced' culture='Culture.empire'/></NPCCharacters>") },
            { "Heroes", Xml("<Heroes><Hero id='official_lord' faction='Faction.official_clan'/></Heroes>") },
            { "SPCultures", Xml("<SPCultures><Culture id='empire' basic_troop='NPCCharacter.recruit' default_party_template='PartyTemplate.standard' start_point_position_x='900' start_point_position_y='900'><lord_templates><template name='NPCCharacter.lord_template'/></lord_templates></Culture></SPCultures>") },
            { "partyTemplates", Xml("<MBPartyTemplates><MBPartyTemplate id='standard'><stacks><PartyTemplateStack troop='NPCCharacter.recruit' min_value='1' max_value='3'/></stacks></MBPartyTemplate></MBPartyTemplates>") },
            { "EquipmentRosters", Xml("<EquipmentRosters><EquipmentRoster id='lord_equipment'><EquipmentSet><Equipment id='Item.sword' slot='Item0'/></EquipmentSet></EquipmentRoster></EquipmentRosters>") },
            { "Items", Xml("<Items><Item id='sword'/></Items>") },
            { "BodyProperties", Xml("<BodyProperties><BodyProperty id='face'><BodyPropertiesMin age='25'/></BodyProperty></BodyProperties>") },
            { "SkillSets", Xml("<SkillSets><SkillSet id='lord_skills'><skill id='OneHanded' value='20'/></SkillSet></SkillSets>") },
            { "WorkshopTypes", Xml("<WorkshopTypes><WorkshopType id='smithy'/></WorkshopTypes>") },
            { "LocationComplexTemplates", Xml("<LocationComplexTemplates><LocationComplexTemplate id='town_complex'/><LocationComplexTemplate id='village_complex'/></LocationComplexTemplates>") },
            { "Settlements", Xml("<Settlements><Settlement id='official_town' name='Old town' culture='Culture.empire' owner='Faction.official_clan' posX='900' posY='901'><Components><Town id='official_component' is_castle='false' prosperity='1000' background_mesh='town_bg'><Buildings><Building id='market' level='1'/></Buildings></Town></Components><Locations complex_template='LocationComplexTemplate.town_complex'><Location id='port' scene_name='empire_shipyard'/></Locations></Settlement><Settlement id='official_village' name='Old village' culture='Culture.empire' posX='910' posY='911' port_posX='912' port_posY='913'><Components><Village id='official_village_component' bound='Settlement.official_town' village_type='VillageType.grain' hearth='200'/></Components><Locations complex_template='LocationComplexTemplate.village_complex'><Location id='village_center' scene_name='village_scene'/></Locations></Settlement></Settlements>") }
        };
    }
    private static readonly string[] Defaults = { "Skill.OneHanded", "BuildingType.market", "VillageType.grain" };
    private static Dataset Build(Dictionary<string, XmlDocument> s) { return Dataset.Build(42, Options(), s, Defaults); }
    private static void Reject(Action action, string expected) {
        try { action(); } catch (InvalidOperationException ex) { Check(ex.Message.Contains(expected), "wrong rejection: " + ex.Message); return; }
        throw new Exception("accepted invalid case " + expected);
    }
    private static void Main(string[] args) {
        if (args.Length > 0 && args[0] == "--catalog") { Catalog(args); return; }
        var sources = Sources(); var before = sources.ToDictionary(x => x.Key, x => x.Value.OuterXml);
        var a = Build(sources); var b = Dataset.Build(43, Options(), sources, Defaults);
        Check(a.WorldId == "mnbl_0000002a" && b.WorldId == "mnbl_0000002b", "seed identity");
        Check(a.SceneName == "mnbl_generated_0000002a", "scene identity");
        Check(a.StartX == 102 && a.StartY == 100, "spawn contract");
        Check(a.Documents.Select(x => x.Id).SequenceEqual(new[] { "SPCultures", "NPCCharacters", "Heroes", "Kingdoms", "Factions", "WorkshopTypes", "LocationComplexTemplates", "Settlements" }), "document order");
        foreach (var pair in sources) Check(pair.Value.OuterXml == before[pair.Key], "input mutated " + pair.Key);
        var culture = a.Documents[0].CreateDocument().DocumentElement.FirstChild as XmlElement;
        Check(culture.GetAttribute("start_point_position_x") == "102" && culture.GetAttribute("start_point_position_y") == "100", "generated culture start");
        var settlement = a.Documents.Last().CreateDocument();
        var nodes = settlement.DocumentElement.ChildNodes.OfType<XmlElement>().ToArray();
        Check(nodes.Length == 4 && nodes[0].SelectSingleNode("Components/Town") != null && nodes[1].SelectSingleNode("Components/Town") != null, "towns before villages");
        Check(nodes[2].SelectSingleNode("Components/Village/@bound").Value == "Settlement.mnbl_0000002a_town_1", "village bound");
        Check(!nodes[2].HasAttribute("port_posX"), "old village port not copied");
        Check(nodes[0].GetAttribute("posX") == "100" && nodes[0].GetAttribute("port_posX") == "105", "generated placement");
        Check(!a.Documents.Any(x => x.Xml.Contains("official_")), "official identity leaked");
        var npc = a.Documents.Single(x => x.Id == "NPCCharacters").CreateDocument();
        Check(npc.SelectSingleNode("/NPCCharacters/NPCCharacter[@id='soldier']") != null, "transitive upgrade closure");
        Check(npc.SelectSingleNode("/NPCCharacters/NPCCharacter[@id='unreferenced']") == null, "unused NPC excluded");
        Check(npc.SelectSingleNode("/NPCCharacters/NPCCharacter[@id='official_lord']") == null, "official hero excluded");
        Check(npc.SelectNodes("/NPCCharacters/NPCCharacter[@is_hero='true']").Count == 3, "three generated hero characters");
        Check(a.StartingParties.Count == 2 && a.StartingParties[0].HeroId == "mnbl_0000002a_ruler_1", "party specs");
        Check(a.Documents.Single(x => x.Id == "Kingdoms").CreateDocument().SelectSingleNode("/Kingdoms/Kingdom/@owner").Value.StartsWith("Hero.", StringComparison.Ordinal), "kingdom owner kind");
        Check(a.Templates.Any(p => p.Reference == "BodyProperty.face") && a.Templates.Any(p => p.Reference == "Item.sword"), "body/item closure");
        Check(a.Templates.All(p => !p.Reference.StartsWith("Settlement.", StringComparison.Ordinal)), "prototypes not templates");
        Check(a.Prototypes.Count == 3 && a.Templates.All(p => p.Source == "verified-engine-default" || p.SourceSha256.Length == 64), "provenance hashes");
        Check(a.Instances.Intersect(b.Instances).OrderBy(x => x).SequenceEqual(new[] { "Faction.player_faction", "Hero.main_hero", "NPCCharacter.main_hero" }.OrderBy(x => x)), "only required aliases shared");
        string retained = a.Documents.Last().Xml; settlement.DocumentElement.RemoveAll();
        Check(a.Documents.Last().Xml == retained && a.Documents.Last().CreateDocument().DocumentElement.ChildNodes.Count == 4, "document copy isolation");
        sources["NPCCharacters"].DocumentElement.RemoveAll(); Check(a.Documents.Single(x => x.Id == "NPCCharacters").Xml == npc.OuterXml, "source snapshot isolation");
        var oldCulture = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Check(Build(Sources()).Documents.Select(x => x.Sha256).SequenceEqual(a.Documents.Select(x => x.Sha256)), "locale/deterministic bytes"); }
        finally { CultureInfo.CurrentCulture = oldCulture; }
        Reject(() => Dataset.Build(42, Options(), Sources(), new string[0]), "Unresolved dependency");
        Reject(() => Dataset.Build(42, Options(), Sources(), new[] { "Settlement.official_town" }), "Not an allowed engine-default");
        Reject(() => Dataset.Build(42, Options(Double.NaN), Sources(), Defaults), "Extent");
        Reject(() => Dataset.Build(42, Options(150), Sources(), Defaults), "placement");
        var bad = Sources(); bad.Remove("Heroes"); Reject(() => Build(bad), "Required merged source missing");
        bad = Sources(); bad["Items"].DocumentElement.RemoveAll(); Reject(() => Build(bad), "Item.sword");
        bad = Sources(); ((XmlElement)bad["SPCultures"].DocumentElement.FirstChild).SetAttribute("basic_troop", "NPCCharacter.official_lord"); Reject(() => Build(bad), "Named/world hero");
        bad = Sources(); ((XmlElement)bad["SPCultures"].DocumentElement.FirstChild).SetAttribute("unexpected", "Faction.official_clan"); Reject(() => Build(bad), "world-instance dependency");
        bad = Sources(); ((XmlElement)bad["NPCCharacters"].DocumentElement.FirstChild).SetAttribute("is_template", "false"); Reject(() => Build(bad), "explicit reusable Lord template");
        bad = Sources(); bad["NPCCharacters"].DocumentElement.AppendChild(bad["NPCCharacters"].DocumentElement.FirstChild.CloneNode(true)); Reject(() => Build(bad), "Duplicate merged object");
        bad = Sources(); bad["Settlements"].SelectSingleNode("/Settlements/Settlement/Locations/Location").ParentNode.RemoveAll(); Reject(() => Build(bad), "port location");
        bad = Sources(); ((XmlElement)bad["SPCultures"].DocumentElement.FirstChild).SetAttribute("basic_troop", "NPCCharacter.recruit.bad"); Reject(() => Build(bad), "Malformed typed reference");
        bad = Sources(); ((XmlElement)bad["NPCCharacters"].DocumentElement.FirstChild).SetAttribute("culture", "empire"); Reject(() => Build(bad), "selected culture");
        bad = Sources(); bad["Items"] = Xml("<WrongRoot><Item id='sword'/></WrongRoot>"); Reject(() => Build(bad), "source/root mismatch");
        bad = Sources(); bad["NPCCharacters"].SelectSingleNode("//*[@id='recruit']").Attributes["id"].Value = "recruit-with-hyphen";
        ((XmlElement)bad["SPCultures"].DocumentElement.FirstChild).SetAttribute("basic_troop", "NPCCharacter.recruit-with-hyphen");
        bad["partyTemplates"].SelectSingleNode("//@troop").Value = "NPCCharacter.recruit-with-hyphen";
        Check(Build(bad).Templates.Any(x => x.Reference == "NPCCharacter.recruit-with-hyphen"), "installed hyphenated IDs");
        var crafted = Sources(); crafted["Items"] = Xml("<Items><CraftedItem id='sword' crafting_template='Blade'><Pieces><Piece id='blade'/></Pieces></CraftedItem></Items>");
        crafted.Add("CraftingTemplates", Xml("<CraftingTemplates><CraftingTemplate id='Blade' modifier_group='swords'><WeaponDescriptions><WeaponDescription id='Blade'/></WeaponDescriptions><UsablePieces><UsablePiece piece_id='optional_absent'/></UsablePieces></CraftingTemplate></CraftingTemplates>"));
        crafted.Add("CraftingPieces", Xml("<CraftingPieces><CraftingPiece id='blade'/></CraftingPieces>"));
        crafted.Add("WeaponDescriptions", Xml("<WeaponDescriptions><WeaponDescription id='Blade'><AvailablePieces><AvailablePiece id='optional_absent'/></AvailablePieces></WeaponDescription></WeaponDescriptions>"));
        crafted.Add("ItemModifierGroups", Xml("<ItemModifierGroups><ItemModifierGroup id='swords'/></ItemModifierGroups>"));
        Check(Build(crafted).Templates.Any(x => x.Reference == "Item.sword"), "CraftedItem namespace");
        Check(Build(crafted).OptionalReferencesAbsent.Count == 2, "two explicit nullable crafting links");
        crafted["Items"].SelectSingleNode("//Piece/@id").Value = "required_absent"; Reject(() => Build(crafted), "CraftingPiece.required_absent");
        bad = Sources(); bad["SkillSets"].DocumentElement.FirstChild.AppendChild(bad["SkillSets"].CreateElement("skill"));
        var obsolete = (XmlElement)bad["SkillSets"].DocumentElement.FirstChild.LastChild; obsolete.SetAttribute("id", "Polearms"); obsolete.SetAttribute("value", "50");
        Check(Build(bad).OptionalReferencesAbsent.Single().Contains("PropertyOwner.Deserialize IL_0063"), "obsolete skills explicitly nullable");
        bad = Sources(); var trader = bad["NPCCharacters"].CreateElement("NPCCharacter"); trader.SetAttribute("id", "generic_trader"); trader.SetAttribute("is_hero", "true"); trader.SetAttribute("occupation", "GoodsTrader"); trader.SetAttribute("culture", "Culture.empire"); bad["NPCCharacters"].DocumentElement.AppendChild(trader);
        ((XmlElement)bad["SPCultures"].DocumentElement.FirstChild).SetAttribute("merchant", "NPCCharacter.generic_trader");
        Check(Build(bad).Templates.Single(x => x.Reference == "NPCCharacter.generic_trader").Reason.Contains("explicit culture NPC role"), "generic hero-flag role provenance");
        var live = bad["Heroes"].CreateElement("Hero"); live.SetAttribute("id", "generic_trader"); bad["Heroes"].DocumentElement.AppendChild(live); Reject(() => Build(bad), "Named/world hero");
        Func<string[], Dataset> engine = ids => Dataset.Build(42, new Dataset.Options("empire", "lord_template", "official_town", "official_village", 1040,
            Options().First, Options().Second, ids), Sources(), Defaults);
        var engineNpc = engine(new[] { "unreferenced" }).Documents.Single(x => x.Id == "NPCCharacters").CreateDocument();
        Check(engineNpc.SelectSingleNode("/NPCCharacters/NPCCharacter[@id='unreferenced']") != null, "engine literal character retained");
        Check(engine(new[] { "unreferenced" }).Templates.Single(x => x.Reference == "NPCCharacter.unreferenced").Reason.Contains("string-literal"), "engine literal provenance");
        Reject(() => engine(new[] { "official_lord" }), "Named/world hero");
        Reject(() => engine(new[] { "absent_engine_character" }), "Unresolved dependency");
        Reject(() => engine(new[] { "bad id" }), "Invalid");
        Console.WriteLine("PASS " + checks + " managed dataset checks; no registry/native code executed.");
    }
    // Consumes root's real merged export. Defaults are explicit typed IDs, one per line.
    private static void Catalog(string[] args) {
        if (args.Length != 7 && args.Length != 10) throw new ArgumentException("--catalog directory defaults.txt culture heroTemplate townPrototype villagePrototype [placement42.json placement43.json outputDirectory]");
        var sources = new Dictionary<string, XmlDocument>();
        foreach (string path in Directory.GetFiles(args[1], "*.xml")) sources.Add(Path.GetFileNameWithoutExtension(path), Xml(File.ReadAllText(path)));
        var options = new Dataset.Options(args[3], args[4], args[5], args[6], 1040,
            new Dataset.Placement(100,100,102,100,105,100,110,110), new Dataset.Placement(200,200,202,200,205,200,210,210),
            Dataset.InstalledEngineCharacterIds);
        var data = Dataset.Build(42, options, sources, File.ReadAllLines(args[2]).Where(x => x.Length != 0));
        var catalogNpc = data.Documents.Single(x => x.Id == "NPCCharacters").CreateDocument();
        foreach (string id in Dataset.InstalledEngineCharacterIds)
            Check(catalogNpc.SelectSingleNode("/NPCCharacters/NPCCharacter[@id='" + id + "']") != null, "installed engine character retained: " + id);
        Console.WriteLine("PASS catalog preflight " + data.WorldId + ": " + data.Templates.Count + " templates; " + data.Instances.Count + " instance identities. Coordinates are test-only, not navigation evidence.");
        if (args.Length == 10) {
            foreach (uint seed in new uint[] { 42, 43 }) {
                var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
                var descriptor = json.DeserializeObject(File.ReadAllText(args[seed == 42 ? 7 : 8])) as Dictionary<string, object>;
                var settlements = ((object[])descriptor["settlements"]).Cast<Dictionary<string, object>>().ToArray();
                string world = "mnbl_" + seed.ToString("x8");
                Check((string)descriptor["worldId"] == world && (string)descriptor["sceneName"] == "mnbl_generated_" + seed.ToString("x8"), "placement identities");
                Func<int, Dataset.Placement> placement = i => {
                    var town = settlements.Single(x => (string)x["id"] == world + "_town_" + i);
                    var village = settlements.Single(x => (string)x["id"] == world + "_village_" + i);
                    Check((string)village["boundTo"] == (string)town["id"], "placement binding");
                    var p = (object[])town["position"]; var g = (object[])town["gatePosition"]; var w = (object[])town["portPosition"]; var v = (object[])village["position"];
                    return new Dataset.Placement(Convert.ToDouble(p[0]), Convert.ToDouble(p[1]), Convert.ToDouble(g[0]), Convert.ToDouble(g[1]), Convert.ToDouble(w[0]), Convert.ToDouble(w[1]), Convert.ToDouble(v[0]), Convert.ToDouble(v[1]));
                };
                var placed = Dataset.Build(seed, new Dataset.Options(args[3], args[4], args[5], args[6], Convert.ToDouble(descriptor["extent"]), placement(1), placement(2),
                    Dataset.InstalledEngineCharacterIds), sources, File.ReadAllLines(args[2]).Where(x => x.Length != 0));
                var generated = placed.Documents.Single(x => x.Id == "Settlements").CreateDocument();
                foreach (var entity in settlements) {
                    var e = (XmlElement)generated.SelectSingleNode("/Settlements/Settlement[@id='" + entity["id"] + "']"); var p = (object[])entity["position"];
                    Check(e != null && Double.Parse(e.GetAttribute("posX"), CultureInfo.InvariantCulture) == Convert.ToDouble(p[0]) && Double.Parse(e.GetAttribute("posY"), CultureInfo.InvariantCulture) == Convert.ToDouble(p[1]), "exact descriptor XY");
                    if ((string)entity["kind"] == "town") { var w = (object[])entity["portPosition"]; Check(Double.Parse(e.GetAttribute("port_posX"), CultureInfo.InvariantCulture) == Convert.ToDouble(w[0]) && Double.Parse(e.GetAttribute("port_posY"), CultureInfo.InvariantCulture) == Convert.ToDouble(w[1]), "exact port XY"); }
                }
                string output = Path.Combine(args[9], world); if (Directory.Exists(output)) throw new IOException("Output exists: " + output); Directory.CreateDirectory(output);
                foreach (var doc in placed.Documents) File.WriteAllText(Path.Combine(output, doc.Id + ".xml"), doc.Xml);
                File.WriteAllText(Path.Combine(output, "dataset.json"), json.Serialize(placed));
                Console.WriteLine("PASS descriptor " + placed.WorldId + " extent=" + placed.Extent + " optionalAbsences=" + placed.OptionalReferencesAbsent.Count + " -> " + output);
            }
        }
    }
}
