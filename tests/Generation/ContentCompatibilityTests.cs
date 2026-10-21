using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.Generation;
using MNBL.ProceduralWorld.Missions;
using MNBL.ProceduralWorld.Persistence;

// MMR-283 content-mod coexistence tests, without the game: ContentCompatibilityTests REPO_DIR BUNDLE_A GAME_ROOT OUT_JSON
//   * the reduced pin: the captured catalog passes it, it pins only what a world can reference, by key and element;
//   * a synthetic module adding items and troops (and a wanderer template using its item) passes the setup check, world A builds with it
//     (the activation's build), rebuilds byte for byte from the frozen catalog (the load's), and passes the load's live check, also after a
//     balance patch to objects the world references;
//   * refusals name the module and the object in localizable text: a module changing a read field, one removing a referenced item (XSLT),
//     one whose template names an item that is not installed, a culture added after the campaign naming a troop the world lacks;
//   * the battle-scene table: the installed table is the catalogue's; a scene pack adding a scene is absorbed and the battle map rebuilt
//     against it, with a stored mutation's patch on every strip image; a pack that takes ground away is refused before any cache build with
//     its module and scene named; a failing battle map install never throws; AfterLoad checks the table before the navigation caches.
internal static class ContentCompatibilityTests
{
    private static int checks, failures;
    private static readonly List<string> lines = new List<string>();
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; string line = (condition ? "ok    " : "FAIL  ") + label; lines.Add(line); Console.WriteLine(line); }
    private static IDictionary<string, object> Load(string path) { return (IDictionary<string, object>)Json.DeserializeObject(File.ReadAllText(path).TrimStart('﻿')); }
    private static IDictionary<string, object> O(IDictionary<string, object> o, string key) { return (IDictionary<string, object>)o[key]; }
    private static string S(IDictionary<string, object> o, string key) { object v; return o.TryGetValue(key, out v) ? v as string : null; }
    private static double D(object v) { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); }
    private static string Sha(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }

    private static int Main(string[] args)
    {
        if (args.Length != 4) { Console.Error.WriteLine("usage: ContentCompatibilityTests REPO_DIR BUNDLE_A GAME_ROOT OUT_JSON"); return 2; }
        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.content-compatibility-tests", ["version"] = 1, ["ticket"] = "MMR-283" };
        try { Run(args[0], args[1], args[2], Path.GetDirectoryName(Path.GetFullPath(args[3])), evidence); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        evidence["checks"] = checks; evidence["failures"] = failures; evidence["lines"] = lines;
        File.WriteAllText(args[3], Json.Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static Dictionary<string, byte[]> Bytes(IDictionary<string, XmlDocument> docs) { return docs.ToDictionary(d => d.Key, d => new UTF8Encoding(false).GetBytes(d.Value.OuterXml), StringComparer.Ordinal); }
    private static Dictionary<string, XmlDocument> Copy(IDictionary<string, XmlDocument> docs) { return docs.ToDictionary(d => d.Key, d => (XmlDocument)d.Value.CloneNode(true), StringComparer.Ordinal); }
    private static XmlElement Find(XmlDocument doc, string element, string id) { return doc.DocumentElement.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.Name == element && e.GetAttribute("id") == id); }

    // A module folder: SubModule.xml declaring its campaign XML, and those files.
    private static ModuleSources.Module WriteModule(string root, string id, IDictionary<string, KeyValuePair<string, string>> files)
    {
        string folder = Path.Combine(root, id);
        Directory.CreateDirectory(Path.Combine(folder, "ModuleData"));
        var xml = new StringBuilder("<Module><Id value=\"" + id + "\"/><Xmls>");
        foreach (var f in files) xml.Append("<XmlNode><XmlName id=\"" + f.Value.Key + "\" path=\"" + Path.GetFileNameWithoutExtension(f.Key) + "\"/></XmlNode>");
        xml.Append("</Xmls></Module>");
        File.WriteAllText(Path.Combine(folder, "SubModule.xml"), xml.ToString());
        foreach (var f in files) File.WriteAllText(Path.Combine(folder, "ModuleData", f.Key), f.Value.Value);
        return ModuleSources.Read(id, folder, false);
    }

    private static void Run(string repo, string bundleDir, string gameRoot, string scratch, SortedDictionary<string, object> evidence)
    {
        var pinBytes = File.ReadAllBytes(Path.Combine(repo, CatalogPin.PolicyPath));
        var pin = CatalogPin.Pin.FromBytes(pinBytes);
        var rosters = Load(Path.Combine(repo, "worldgen/policies/rosters.json"));
        var receipt = Load(Path.Combine(repo, S(O(rosters, "provenance"), "catalogReceipt")));
        var captured = new Dictionary<string, XmlDocument>(StringComparer.Ordinal);
        foreach (var d in ((IEnumerable)O(receipt, "outputs")["documents"]).Cast<IDictionary<string, object>>())
        {
            var bytes = File.ReadAllBytes(S(d, "path"));
            if (Sha(bytes) != S(d, "sha256")) throw new InvalidOperationException("catalog " + S(d, "id") + " differs from its capture");
            var doc = new XmlDocument { XmlResolver = null }; doc.LoadXml(new UTF8Encoding(false).GetString(bytes).TrimStart('﻿')); captured[S(d, "id")] = doc;
        }
        var engineIds = GeneratedCampaignDataset.InstalledEngineCharacterIds;

        // ---- the reduced pin ----
        var baseline = CatalogPin.Check(pin, captured, engineIds);
        var capturedIndex = new CatalogPin.Index(captured);
        var pinnedTypes = pin.Objects.Keys.GroupBy(k => k.Substring(0, k.IndexOf('.'))).ToDictionary(g => g.Key, g => g.Count());
        int lords = pin.Objects.Keys.Count(k => k.StartsWith("NPCCharacter.", StringComparison.Ordinal) && capturedIndex.Contains("Hero." + k.Substring(13)) && k != "NPCCharacter.main_hero");
        Check(baseline.Count == 0, "the captured catalog the policies were made from passes the reduced pin (" + pin.Objects.Count + " objects, " + pin.Tolerated.Count + " tolerated closure problems of its own)" +
              (baseline.Count == 0 ? "" : ": " + String.Join("; ", baseline.Take(5))));
        Check(pin.Objects.Count < capturedIndex.Keys.Count() && lords == 0 && !pinnedTypes.ContainsKey("Kingdom") && pinnedTypes.ContainsKey("Hero") && pinnedTypes["Hero"] == 1 &&
              pin.Objects.Values.All(o => o.Element != null && o.Read.Length == 64 && capturedIndex.Element(o.Key).Name == o.Element),
              "the pin covers only what a world can reference, by key, element and read fields: " + pin.Objects.Count + " of " + capturedIndex.Keys.Count() + " installed objects, no Calradian lord, kingdom or hero but main_hero (" +
              String.Join(", ", pinnedTypes.OrderBy(k => k.Key).Select(k => k.Key + " " + k.Value)) + ")");
        evidence["pin"] = new Dictionary<string, object> { ["sha256"] = pin.Sha256, ["objects"] = pin.Objects.Count, ["installed"] = capturedIndex.Keys.Count(), ["byType"] = pinnedTypes, ["policyRoots"] = pin.Roots.Count };

        // ---- a synthetic module adding items and troops ----
        var items = captured["Items"]; var characters = captured["NPCCharacters"];
        var sourceItem = items.DocumentElement.ChildNodes.OfType<XmlElement>().First(e => e.Name == "Item" && e.GetAttribute("Type") == "HeadArmor" && pin.Objects.ContainsKey("Item." + e.GetAttribute("id")));
        var sourceTroop = Find(characters, "NPCCharacter", "imperial_recruit");
        var sourceWanderer = characters.DocumentElement.ChildNodes.OfType<XmlElement>().First(e => e.GetAttribute("occupation") == "Wanderer" && e.GetAttribute("is_template") == "true" && e.GetAttribute("culture") == "Culture.empire");
        Func<XmlElement, string, XmlElement> clone = (e, id) => { var c = (XmlElement)e.CloneNode(true); c.SetAttribute("id", id); return c; };
        var modItem = clone(sourceItem, "mnbl_test_mod_helmet");
        var modTroop = clone(sourceTroop, "mnbl_test_mod_recruit"); modTroop.SetAttribute("name", "{=!}Mod Recruit");
        var modWanderer = clone(sourceWanderer, "mnbl_test_mod_wanderer");
        var carried = modWanderer.OwnerDocument.CreateElement("equipment"); carried.SetAttribute("slot", "Head"); carried.SetAttribute("id", "Item.mnbl_test_mod_helmet");
        modWanderer.SelectSingleNode("Equipments").AppendChild(carried);
        var modded = Copy(captured);
        modded["Items"].DocumentElement.AppendChild(modded["Items"].ImportNode(modItem, true));
        modded["NPCCharacters"].DocumentElement.AppendChild(modded["NPCCharacters"].ImportNode(modTroop, true));
        modded["NPCCharacters"].DocumentElement.AppendChild(modded["NPCCharacters"].ImportNode(modWanderer, true));
        var addMod = WriteModule(scratch, "MNBLTestAddsContent", new Dictionary<string, KeyValuePair<string, string>> {
            ["mod_items.xml"] = new KeyValuePair<string, string>("Items", "<Items>" + modItem.OuterXml + "</Items>"),
            ["mod_troops.xml"] = new KeyValuePair<string, string>("NPCCharacters", "<NPCCharacters>" + modTroop.OuterXml + modWanderer.OuterXml + "</NPCCharacters>") });
        var setup = CatalogPin.Check(pin, modded, engineIds);
        Check(setup.Count == 0, "setup: a module adding an item, a troop and a wanderer template that carries the item passes the content check" + (setup.Count == 0 ? "" : ": " + String.Join("; ", setup.Take(5))));

        // The activation's build with the module, then the load's rebuild from the frozen catalog and its live check.
        var bundle = WorldDataBundle.Open(bundleDir, new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", WorldDataBundle.HostCapabilities));
        var layoutReport = O(O(Load(Path.Combine(repo, "evidence/world/settlement-position-report.json")), "worlds"), "A");
        var grid = O((IDictionary<string, object>)Json.DeserializeObject(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/travel/travel-graph.json"))), "grid");
        double spacing = D(grid["spacing"]), originX = D(grid["columnOffset"]) * spacing, originY = D(grid["rowOffset"]) * spacing;
        var missionScenes = MissionScenes.FromBytes(File.ReadAllBytes(Path.Combine(repo, MissionScenes.PolicyPath)));
        Func<Dictionary<string, byte[]>, CompiledWorldDataset> build = frozen => CompiledWorldDataset.Build(new CompiledWorldDataset.Sources {
            Bundle = bundle.ReadVerified, Policy = p => File.ReadAllBytes(Path.Combine(repo, p)), Catalog = CatalogPin.Parse(frozen), CatalogSha256 = CatalogPin.Identity(frozen), Defaults = pin.Defaults,
            MissionScenes = missionScenes, Layout = layoutReport, OriginX = originX, OriginY = originY, Width = D(grid["cols"]) * spacing });
        var frozenBytes = Bytes(modded);
        var activated = build(frozenBytes);
        var reloaded = build(CatalogPin.Parse(frozenBytes).ToDictionary(d => d.Key, d => new UTF8Encoding(false).GetBytes(d.Value.OuterXml), StringComparer.Ordinal));
        Check(activated.Graph.Ok && reloaded.DocumentSha256.SequenceEqual(activated.DocumentSha256),
              "activation: world A builds and validates with the module (" + activated.Declared.Count + " declared objects, graph " + activated.Graph.Sha256.Substring(0, 12) + "); load: rebuilt from the frozen catalog, its " + activated.DocumentSha256.Count + " documents are byte for byte the activation's");
        var references = CatalogPin.WorldReferences(activated.Templates.Select(t => t.Reference), new HashSet<string>(pin.Defaults, StringComparer.Ordinal));
        var frozenIndex = new CatalogPin.Index(CatalogPin.Parse(frozenBytes));
        var supplied = new HashSet<string>(activated.Documents.Single(d => d.Key == "NPCCharacters").Value.DocumentElement.ChildNodes.OfType<XmlElement>().Select(e => "NPCCharacter." + e.GetAttribute("id")), StringComparer.Ordinal);
        var atLoad = CatalogPin.CheckReferences(frozenIndex, new CatalogPin.Index(modded), references).Concat(CatalogPin.AddedStock(frozenIndex, new CatalogPin.Index(modded), supplied)).ToList();
        int checkedAtLoad = references.Count(k => !k.StartsWith("NPCCharacter.", StringComparison.Ordinal));
        Check(atLoad.Count == 0 && checkedAtLoad > 0 && checkedAtLoad < pin.Objects.Count,
              "load: the live catalog with the module passes the load's check of the " + checkedAtLoad + " installed objects world A references (not the " + pin.Objects.Count + " pinned)" + (atLoad.Count == 0 ? "" : ": " + String.Join("; ", atLoad.Take(5))));
        evidence["worldA"] = new Dictionary<string, object> { ["documents"] = activated.DocumentSha256, ["references"] = references.Count, ["checkedAtLoad"] = checkedAtLoad, ["catalogIdentity"] = CatalogPin.Identity(frozenBytes) };

        // A balance patch after the campaign was made: stats, equipment, value and weight of objects the world references.
        var patched = Copy(modded);
        var refItem = references.First(k => k.StartsWith("Item.", StringComparison.Ordinal) && Find(patched["Items"], "Item", k.Substring(5)) != null);
        var item = Find(patched["Items"], "Item", refItem.Substring(5)); item.SetAttribute("value", "987654"); item.SetAttribute("weight", "9.5");
        var reemitted = activated.Templates.Select(t => t.Reference).First(k => k.StartsWith("NPCCharacter.", StringComparison.Ordinal) && Find(patched["NPCCharacters"], "NPCCharacter", k.Substring(13)) != null &&
            Find(patched["NPCCharacters"], "NPCCharacter", k.Substring(13)).SelectSingleNode("skills/skill") != null);
        var troop = Find(patched["NPCCharacters"], "NPCCharacter", reemitted.Substring(13));
        foreach (var skill in troop.SelectNodes("skills/skill").OfType<XmlElement>()) skill.SetAttribute("value", "300");
        troop.SetAttribute("level", "31");
        var balance = CatalogPin.Check(pin, patched, engineIds).Concat(CatalogPin.CheckReferences(frozenIndex, new CatalogPin.Index(patched), references)).ToList();
        Check(balance.Count == 0 && CatalogPin.Read(item) == CatalogPin.Read(frozenIndex.Element(refItem)),
              "a balance patch (" + refItem + "'s value and weight, " + reemitted + "'s skills and level) passes setup and the load's check, and the world still rebuilds from its frozen copies" +
              (balance.Count == 0 ? "" : ": " + String.Join("; ", balance.Take(5))));

        // ---- refusals name the module and the object ----
        var modules = new List<ModuleSources.Module>();
        foreach (var official in new[] { "Native", "SandBoxCore", "SandBox", "StoryMode", "NavalDLC" }) modules.Add(ModuleSources.Read(official, Path.Combine(gameRoot, "Modules", official), true));
        modules.Add(addMod);
        // A module changing what the world reads (a referenced item's type).
        var changedItem = Copy(modded); var retyped = Find(changedItem["Items"], "Item", refItem.Substring(5)); retyped.SetAttribute("Type", "TwoHandedWeapon");
        var badMod = WriteModule(scratch, "MNBLTestChangesItem", new Dictionary<string, KeyValuePair<string, string>> { ["changed_items.xml"] = new KeyValuePair<string, string>("Items", "<Items>" + retyped.OuterXml + "</Items>") });
        // A module removing a referenced item through XSLT (the load's check).
        var removedId = references.Last(k => k.StartsWith("Item.", StringComparison.Ordinal) && k != refItem && frozenIndex.Element(k).Name == "Item").Substring(5);
        var remover = WriteModule(scratch, "MNBLTestRemovesItem", new Dictionary<string, KeyValuePair<string, string>> { ["removed.xslt"] = new KeyValuePair<string, string>("Items",
            "<xsl:stylesheet version=\"1.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\"><xsl:template match=\"@*|node()\"><xsl:copy><xsl:apply-templates select=\"@*|node()\"/></xsl:copy></xsl:template><xsl:template match=\"Item[@id='" + removedId + "']\"/></xsl:stylesheet>") });
        var sources = new ModuleSources(modules.Concat(new[] { badMod, remover }));
        var changedConflicts = CatalogPin.Check(pin, changedItem, engineIds); CatalogPin.Attribute(changedConflicts, sources);
        var c1 = changedConflicts.FirstOrDefault(c => c.Key == refItem);
        var e1 = c1 == null ? null : GenerationErrors.Content(c1.Kind, c1.Key, c1.Module, c1.Official, c1.Target, c1.ToString());
        Check(c1 != null && c1.Kind == CatalogPin.Changed && c1.Module == "MNBLTestChangesItem" && !c1.Official && e1.Category == GenerationErrorCategory.ContentConflict &&
              e1.Message.StartsWith("{=mnbl_err_content_changed_mod}") && e1.Variables["MODULE"] == "MNBLTestChangesItem" && e1.Variables["OBJECT"] == refItem,
              "setup: a module changing " + refItem + "'s type is refused, naming the module and the object in a localizable message (" + (e1 == null ? "none" : e1.Message + " MODULE=" + e1.Variables["MODULE"]) + ")");
        var removed = Copy(modded); var gone = Find(removed["Items"], "Item", removedId); gone.ParentNode.RemoveChild(gone);
        var removedConflicts = CatalogPin.CheckReferences(frozenIndex, new CatalogPin.Index(removed), references); CatalogPin.Attribute(removedConflicts, sources);
        var c2 = removedConflicts.FirstOrDefault();
        var r2 = c2 == null ? null : LoadPolicy.Content(c2.Kind, c2.Key, c2.Module, c2.Official, c2.Target, c2.ToString());
        Check(removedConflicts.Count == 1 && c2.Kind == CatalogPin.Missing && c2.Key == "Item." + removedId && c2.Module == "MNBLTestRemovesItem" && r2.Code == LoadPolicy.ContentConflict &&
              r2.Message.StartsWith("{=mnbl_load_content_missing_mod}") && r2.Title.StartsWith("{=mnbl_load_content_title}") && r2.Variables["MODULE"] == "MNBLTestRemovesItem" && r2.Variables["OBJECT"] == "Item." + removedId,
              "load: an item the world references, removed by a module's XSLT, refuses the load naming the module and the object in a localizable message (" + (r2 == null ? "none" : r2.Message.Substring(0, 40) + "... " + r2.Variables["OBJECT"]) + ")");
        // A module whose template names an item that is not installed (found by the setup closure, not by the activation's build).
        var broken = Copy(modded); var brokenWanderer = Find(broken["NPCCharacters"], "NPCCharacter", "mnbl_test_mod_wanderer");
        foreach (var eq in brokenWanderer.SelectNodes("descendant::equipment[@slot='Head']").OfType<XmlElement>()) eq.SetAttribute("id", "Item.mnbl_test_not_installed");
        var brokenConflicts = CatalogPin.Check(pin, broken, engineIds); CatalogPin.Attribute(brokenConflicts, sources);
        var c3 = brokenConflicts.FirstOrDefault();
        var e3 = c3 == null ? null : GenerationErrors.Content(c3.Kind, c3.Key, c3.Module, c3.Official, c3.Target, c3.ToString());
        Check(brokenConflicts.Count == 1 && c3.Kind == CatalogPin.Unresolved && c3.Key == "NPCCharacter.mnbl_test_mod_wanderer" && c3.Target == "Item.mnbl_test_not_installed" && c3.Module == "MNBLTestAddsContent" &&
              e3.Message.StartsWith("{=mnbl_err_content_unresolved_mod}") && e3.Variables["TARGET"] == "Item.mnbl_test_not_installed",
              "setup: a module's template naming an item that is not installed is refused before generation, naming the module, the template and the item (" + (c3 == null ? "none" : c3.ToString()) + ")");
        // A culture added after the campaign was made, naming a troop the world does not supply.
        var later = Copy(modded); var culture = (XmlElement)Find(later["SPCultures"], "Culture", "empire").CloneNode(true);
        culture.SetAttribute("id", "mnbl_test_later_culture"); culture.SetAttribute("basic_troop", "NPCCharacter.mnbl_test_mod_recruit");
        later["SPCultures"].DocumentElement.AppendChild(culture);
        var cultureMod = WriteModule(scratch, "MNBLTestLaterCulture", new Dictionary<string, KeyValuePair<string, string>> { ["later_cultures.xml"] = new KeyValuePair<string, string>("SPCultures", "<SPCultures>" + culture.OuterXml + "</SPCultures>") });
        var laterConflicts = CatalogPin.AddedStock(frozenIndex, new CatalogPin.Index(later), supplied);
        CatalogPin.Attribute(laterConflicts, new ModuleSources(modules.Concat(new[] { cultureMod })));
        var c4 = laterConflicts.FirstOrDefault(); var r4 = c4 == null ? null : LoadPolicy.Content(c4.Kind, c4.Key, c4.Module, c4.Official, c4.Target, c4.ToString());
        Check(c4 != null && c4.Kind == CatalogPin.Added && c4.Key == "Culture.mnbl_test_later_culture" && c4.Module == "MNBLTestLaterCulture" && r4.Message.StartsWith("{=mnbl_load_content_added_mod}"),
              "load: a culture a module added after the campaign was made, naming a troop the world does not supply, refuses the load naming the module and the culture (" + (c4 == null ? "none" : c4.ToString()) + ")");
        var vanillaProvider = sources.Provider("NPCCharacters", "imperial_recruit");
        Check(vanillaProvider != null && vanillaProvider.Official, "attribution: an installed troop is the game's own module's (" + (vanillaProvider == null ? "none" : vanillaProvider.Id) + "), so a change there reads as a game update");

        // ---- the battle-scene table ----
        var m = missionScenes;
        var files = new[] { "SandBox", "NavalDLC" }.Select(id => new KeyValuePair<string, string>(id, Path.Combine(gameRoot, "Modules", id, "ModuleData", "sp_battle_scenes.xml"))).ToList();
        var fileProblems = new List<KeyValuePair<string, string>>();
        var installed = BattleTable.FromModuleFiles(files, m, fileProblems);
        var installedCheck = BattleTableCheck.Of(m, installed, fileProblems);
        Check(installedCheck.Ok && installed.Sha256 == m.TableSha256 && installed.InventorySha256 == m.InventorySha256 && installedCheck.Changed.Count == 0,
              "battle table: the active modules' sp_battle_scenes.xml, read as Campaign.InitializeScenes reads them, are the catalogue's table (" + installed.Rows.Count + " scenes, " + installed.Sha256.Substring(0, 12) + ")");
        var landIndices = installed.Indices.Where(i => installed.UniqueLand(i) != null).ToList();
        int unused = Enumerable.Range(1, 255).First(i => !installed.Indices.Contains(i));
        var sceneFolder = Path.Combine(scratch, "MNBLTestScenePack"); Directory.CreateDirectory(Path.Combine(sceneFolder, "ModuleData"));
        File.WriteAllText(Path.Combine(sceneFolder, "ModuleData", "sp_battle_scenes.xml"), "<SPBattleScenes><Scene id=\"mnbl_test_forest_glade\" terrain=\"Plain\" forest_density=\"High\" map_indices=\"" + unused + "\" is_naval=\"false\"/></SPBattleScenes>");
        var desert = landIndices.Where(i => installed.UniqueLand(i).Terrain == m.TerrainIds["Desert"]).ToList();
        var breakingFolder = Path.Combine(scratch, "MNBLTestBreakingPack"); Directory.CreateDirectory(Path.Combine(breakingFolder, "ModuleData"));
        File.WriteAllText(Path.Combine(breakingFolder, "ModuleData", "sp_battle_scenes.xml"), "<SPBattleScenes><Scene id=\"mnbl_test_everywhere\" terrain=\"Desert\" map_indices=\"" + String.Join(",", landIndices) + "\" is_naval=\"false\"/></SPBattleScenes>");
        var packProblems = new List<KeyValuePair<string, string>>();
        var absorbedTable = BattleTable.FromModuleFiles(files.Concat(new[] { new KeyValuePair<string, string>("MNBLTestScenePack", Path.Combine(sceneFolder, "ModuleData", "sp_battle_scenes.xml")) }), m, packProblems);
        var absorbed = BattleTableCheck.Of(m, absorbedTable, packProblems);
        Check(absorbed.Ok && !absorbed.Resolver.TableIsCatalogues && absorbed.Changed.Count == 1 && absorbed.Changed[0].Module == "MNBLTestScenePack" &&
              absorbed.Resolver.LandCandidates("woodland", true).Contains(unused),
              "battle table: a scene pack adding a high-forest Plain scene (index " + unused + ") is absorbed: no ground loses its scene, and woodland ground can now take the new scene");
        var breakingTable = BattleTable.FromModuleFiles(files.Concat(new[] { new KeyValuePair<string, string>("MNBLTestBreakingPack", Path.Combine(breakingFolder, "ModuleData", "sp_battle_scenes.xml")) }), m, packProblems);
        var refusedCheck = BattleTableCheck.Of(m, breakingTable, packProblems);
        var culprit = refusedCheck.Culprit();
        var e5 = GenerationErrors.Content(CatalogPin.Battle, "Scene." + culprit.Key, culprit.Value, false, null, String.Join("; ", refusedCheck.Lost.Take(3)));
        var r5 = LoadPolicy.Content(CatalogPin.Battle, "Scene." + culprit.Key, culprit.Value, false, null, "");
        Check(!refusedCheck.Ok && refusedCheck.Lost.Count > 0 && culprit.Key == "mnbl_test_everywhere" && culprit.Value == "MNBLTestBreakingPack" &&
              e5.Message.StartsWith("{=mnbl_err_content_battle_mod}") && r5.Message.StartsWith("{=mnbl_load_content_battle_mod}") && r5.Variables["MODULE"] == "MNBLTestBreakingPack",
              "battle table: a scene listing every land index makes " + refusedCheck.Lost.Count + " kinds of ground non-deterministic (first " + (refusedCheck.Lost.Count > 0 ? refusedCheck.Lost[0] : "-") +
              ") and is refused at setup and before a load, naming the module and the scene");
        // The battle map rebuilt against the absorbed table on a periodic world with a stored mutation: desert ground patched into a grassland
        // block reaches every strip image of its cells, and changing the patch rewrites exactly those images.
        var resolver = absorbed.Resolver;
        int plain = m.TerrainIds["Plain"];
        var mutated = new HashSet<long>();
        for (int y = 3; y <= 5; y++) for (int lx = 2; lx <= 4; lx++) mutated.Add(GeneratedEncounterResolver.MapRequest.Key(lx, y));
        string mutationClass = "desert";
        var request = new GeneratedEncounterResolver.MapRequest { Width = 40, Height = 12, OriginCell = 4, PeriodCells = 32, Periodic = true, WorldId = "mnbl_test_world",
            Sample = (cx, y) => { var s = new CellSamples { Biome = "grassland", CentreLand = plain }; s.Land.Add(plain); return s; } };
        request.Patches.Add((lx, y, s) => { if (mutated.Contains(GeneratedEncounterResolver.MapRequest.Key(lx, y))) s.ClassOverride = mutationClass; });
        var map = resolver.Rebuild(request);
        Func<GeneratedEncounterResolver.Map, int, int, int> at = (mp, x, y) => mp.Bytes[2 * (y * mp.Width + x)];
        Func<int, string> terrainOf = i => { var row = resolver.Table.UniqueLand(i); return row == null ? null : m.TerrainNames[row.Terrain]; };
        var images = new List<int>();
        for (int x = 0; x < map.Width; x++) if (GeneratedEncounterResolver.CanonicalColumn(x, 4, 32) == 3) images.Add(x);
        bool everyImage = images.Count == 2 && images.All(x => terrainOf(at(map, x, 4)) == "Desert") && terrainOf(at(map, 20, 8)) == "Plain";
        int expectedImages = mutated.Sum(k => Enumerable.Range(0, map.Width).Count(x => GeneratedEncounterResolver.CanonicalColumn(x, 4, 32) == (int)(k % 1000003)));
        mutationClass = "wetland";
        int written = resolver.RebuildCells(map, request, mutated);
        Check(everyImage && written == expectedImages && images.All(x => terrainOf(at(map, x, 4)) == "Swamp") && terrainOf(at(map, 20, 8)) == "Plain",
              "battle map: rebuilt against the absorbed table on a periodic strip with a stored mutation, the mutated cells take their class's scene in every image (columns " + String.Join(", ", images) +
              ": Desert), and re-patched cells are rewritten in all " + written + " images (Swamp)");
        // Never an exception from the battle map install: a resolver over a table with no land scene at all fails inside the install.
        var empty = new GeneratedEncounterResolver(m, new BattleTable(new BattleTable.Row[0], t => m.TerrainNames[t]));
        var traced = new List<string>();
        var failed = GeneratedEncounterResolver.Guarded(() => empty.Record(empty.Rebuild(request)), traced.Add);
        Check(Equals(failed["status"], "failed") && traced.Count == 1 && !BattleTableCheck.Of(m, empty.Table).Ok,
              "a battle map that cannot be built (a table with no scenes) is caught and recorded as failed, never thrown (" + failed["error"] + "); the table check refuses it first");
        // AfterLoad: the table is checked before the navigation caches, and nothing escapes it.
        var afterLoad = File.ReadAllText(Path.Combine(repo, "src/MNBL.ProceduralWorld/World/GeneratedMapScene.cs"));
        int start = afterLoad.IndexOf("public new void AfterLoad()", StringComparison.Ordinal), end = afterLoad.IndexOf("\n        }\n", start, StringComparison.Ordinal);
        string body = start < 0 || end < 0 ? "" : afterLoad.Substring(start, end - start);
        int preflight = body.IndexOf("GeneratedBattleTerrain.Preflight()", StringComparison.Ordinal), caches = body.IndexOf("RegisterNavigationCaches", StringComparison.Ordinal);
        Check(preflight > 0 && caches > preflight && body.Contains("catch (Exception error)") && !body.Contains("throw"),
              "GeneratedMapScene.AfterLoad checks the loaded battle table before it builds the navigation caches, records every failure and throws nothing");
        evidence["battle"] = new Dictionary<string, object> { ["installed"] = installedCheck.Record(), ["absorbed"] = absorbed.Record(), ["refused"] = refusedCheck.Record(), ["mapSha256"] = map.Sha256 };
    }
}
