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

// MMR-45 compiled world dataset tests, without the game: CompiledWorldTests BUNDLE_DIR REPO_DIR OUT_JSON
// The real size-129 world A built by CompiledWorldDataset as the activation's BuildWorld builds it: the five accepted factories, the
// settlement records on the placement its scene entities were built from (evidence/world/settlement-position-report.json, world A), the
// merged new-game documents with the installed records they need re-emitted, the whole-graph validation (C07) in the logical frame, and the
// registration copies in the scene's physical frame. Checks: the graph validates; every record stands where its scene entity stands; the
// documents plan through the MMR-30 registry with no problem (the hero family forward references included, two-pass since MMR-45); the
// creation cultures and their start points; determinism; the physical frame moved positions only; a moved settlement and a layout of another
// world are refused.
internal static class CompiledWorldTests
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
        if (args.Length != 3) { Console.Error.WriteLine("usage: CompiledWorldTests BUNDLE_DIR REPO_DIR OUT_JSON"); return 2; }
        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.compiled-world-tests", ["version"] = 1, ["ticket"] = "MMR-45" };
        try { Run(args[0], args[1], evidence); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        evidence["checks"] = checks; evidence["failures"] = failures; evidence["lines"] = lines;
        File.WriteAllText(args[2], Json.Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string bundleDir, string repo, SortedDictionary<string, object> evidence)
    {
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", WorldDataBundle.HostCapabilities);
        var bundle = WorldDataBundle.Open(bundleDir, host);
        var pin = Load(Path.Combine(repo, "worldgen/policies/campaign-catalog.json"));
        // The catalog: the captured documents the pin names (MMR-283: v2 lists them by id), each verified against its capture receipt's hash
        // (in game: the live merged XML, checked against the reduced pin).
        var receipt = Load(Path.Combine(repo, S(O(pin, "provenance"), "catalogReceipt")));
        var catalog = new Dictionary<string, XmlDocument>(StringComparer.Ordinal);
        foreach (var d in ((IEnumerable)O(receipt, "outputs")["documents"]).Cast<IDictionary<string, object>>())
        {
            var bytes = File.ReadAllBytes(S(d, "path"));
            if (Sha(bytes) != S(d, "sha256") || !((IEnumerable)pin["documents"]).Cast<object>().Contains(S(d, "id"))) throw new InvalidOperationException("catalog " + S(d, "id") + " differs from its capture or the pin");
            var doc = new XmlDocument { XmlResolver = null }; doc.LoadXml(new UTF8Encoding(false).GetString(bytes).TrimStart('﻿')); catalog[S(d, "id")] = doc;
        }
        var defaults = ((IEnumerable)pin["defaults"]).Cast<object>().Select(x => (string)x).ToList();
        // MMR-262: the layout of this bundle's world (the report holds A' and B'), found by its world content id.
        var layoutReport = O(Load(Path.Combine(repo, "evidence/world/settlement-position-report.json")), "worlds").Values.Cast<IDictionary<string, object>>()
            .FirstOrDefault(w => S(w, "worldContentId") == bundle.WorldContentId);
        if (layoutReport == null) throw new InvalidDataException("evidence/world/settlement-position-report.json has no layout for world " + bundle.WorldContentId);
        var grid = O((IDictionary<string, object>)Json.DeserializeObject(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/travel/travel-graph.json"))), "grid");
        double spacing = D(grid["spacing"]), originX = D(grid["columnOffset"]) * spacing, originY = D(grid["rowOffset"]) * spacing;
        var missionScenes = MNBL.ProceduralWorld.Missions.MissionScenes.FromBytes(File.ReadAllBytes(Path.Combine(repo, MNBL.ProceduralWorld.Missions.MissionScenes.PolicyPath)));
        Func<IDictionary<string, object>, CompiledWorldDataset.Sources> sources = layout => new CompiledWorldDataset.Sources {
            Bundle = bundle.ReadVerified, Policy = p => File.ReadAllBytes(Path.Combine(repo, p)), Catalog = catalog,
            CatalogSha256 = Sha(File.ReadAllBytes(Path.Combine(repo, "worldgen/policies/campaign-catalog.json"))), Defaults = defaults, MissionScenes = missionScenes,
            Layout = layout, OriginX = originX, OriginY = originY, Width = D(grid["cols"]) * spacing };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var world = CompiledWorldDataset.Build(sources(layoutReport));
        double seconds = watch.Elapsed.TotalSeconds;

        // C07: the whole graph, as staged for the new game, validates.
        Check(world.Graph.Ok, "the compiled world " + world.WorldId + " (" + world.Documents.Count + " new-game documents, " + world.Declared.Count + " declared objects, " + world.Templates.Count +
              " installed records re-emitted) passes the whole-graph validation in the logical frame: " + world.Graph.Summary(30) + " (" + Math.Round(seconds, 1) + " s)");
        var census = world.Graph.Census;
        evidence["census"] = census; evidence["graphSha256"] = world.Graph.Sha256; evidence["record"] = world.Record(); evidence["buildSeconds"] = Math.Round(seconds, 2);
        // MMR-280: the staged population (houses, companies, outlaw clans, adult lords, minors, dead parents, members, wanderers) equals the
        // campaign v2 census the population pass wrote, counted by the graph validator from the staged documents and by HeroFactory from its intents.
        var campaignCensus = O((IDictionary<string, object>)Json.DeserializeObject(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/campaign/campaign.json"))), "census");
        var staged = census.ContainsKey("population") ? (SortedDictionary<string, int>)census["population"] : new SortedDictionary<string, int>();
        Check(staged.Count == 9 && staged.All(c => Convert.ToInt32(campaignCensus[c.Key]) == c.Value) && world.Heroes.Census.Where(c => campaignCensus.ContainsKey(c.Key)).All(c => Convert.ToInt32(campaignCensus[c.Key]) == c.Value) &&
              world.Factions.MinorClans.Count == staged["mercenaryCompanies"] + staged["outlawClans"] && world.Heroes.Census["wanderers"] == staged["wanderers"],
              "the staged population equals the campaign v2 census: " + String.Join(", ", staged.Select(c => c.Value + " " + c.Key)) + " (dead parents outside every other count)");

        // MMR-262 (F05): the new game registers every backstory-safe wanderer template the catalog resolves, by HeroFactory's own predicate, not
        // only the ones the day-0 wanderers drew: the runtime top-up and rotation pick only registered templates.
        var registeredIds = new HashSet<string>(world.Documents.Single(x => x.Key == "NPCCharacters").Value.DocumentElement.ChildNodes.OfType<XmlElement>().Select(e => e.GetAttribute("id")), StringComparer.Ordinal);
        var resolver = new CharacterTemplateResolver(catalog, defaults, O(Load(Path.Combine(repo, "worldgen/policies/rosters.json")), "troops"));
        var resolvable = new HashSet<string>(WandererPool.Cultures.SelectMany(c => resolver.WandererTemplates(c, new List<string>())), StringComparer.Ordinal);
        var safe = world.Population.Pool.Safe.ToList();
        var safeRegistered = safe.Where(t => registeredIds.Contains(t.Id)).ToList();
        var dayZero = world.Heroes.Wanderers.Select(w => w.Template).Distinct().ToList();
        var groups = safe.Where(t => resolvable.Contains(t.Id)).GroupBy(t => t.Culture + (t.Female ? " women" : " men")).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        Check(safeRegistered.Count == safe.Count(t => resolvable.Contains(t.Id)) && safeRegistered.Select(t => t.Id).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(world.WandererTemplates) &&
              dayZero.All(registeredIds.Contains) && safeRegistered.Count > dayZero.Count && groups.All(g => g.All(t => registeredIds.Contains(t.Id))),
              "the new game registers all " + safeRegistered.Count + " backstory-safe wanderer templates the catalog resolves (of " + safe.Count + " safe; the day-0 wanderers use " + dayZero.Count + "): " +
              String.Join(", ", groups.Select(g => g.Key + " " + g.Count(t => dayZero.Contains(t.Id)) + "->" + g.Count())) + " (day-0 -> registered)");
        evidence["wandererTemplates"] = new Dictionary<string, object> { ["safe"] = safe.Count, ["registered"] = safeRegistered.Count, ["dayZero"] = dayZero.Count };

        // The records on the scene's placement.
        var physical = world.Documents.Single(x => x.Key == "Settlements").Value;
        int compared = 0; var off = new List<string>();
        foreach (var row in ((IEnumerable)layoutReport["settlements"]).Cast<IDictionary<string, object>>())
        {
            var e = physical.DocumentElement.SelectSingleNode("Settlement[@id='" + S(row, "id") + "']") as XmlElement;
            if (e == null) { off.Add(S(row, "id") + " has no record"); continue; }
            foreach (var pair in new[] { new[] { "centre", "posX", "posY" }, new[] { "gate", "gate_posX", "gate_posY" }, new[] { "port", "port_posX", "port_posY" } })
            {
                object v; if (!row.TryGetValue(pair[0], out v) || v == null) continue;
                if (pair[0] == "gate" && !e.HasAttribute("gate_posX")) continue;   // a village's gate is its centre (no record attribute)
                var p = ((IEnumerable)v).Cast<object>().Select(D).ToArray();
                if (!e.HasAttribute(pair[1])) { off.Add(S(row, "id") + " " + pair[0] + " missing"); continue; }
                compared++;
                if (Math.Abs(D(e.GetAttribute(pair[1])) - p[0]) > 1e-6 || Math.Abs(D(e.GetAttribute(pair[2])) - p[1]) > 1e-6) off.Add(S(row, "id") + " " + pair[0]);
            }
        }
        var rows = ((IEnumerable)layoutReport["settlements"]).Cast<IDictionary<string, object>>().ToList();
        int expectedPositions = rows.Count + rows.Count(r => S(r, "kind") == "town" || S(r, "kind") == "castle") + rows.Count(r => r.ContainsKey("port") && r["port"] != null);
        Check(off.Count == 0 && compared == expectedPositions && world.PositionMismatches.Count == 0,
              "every settlement record stands where its scene entity stands: " + compared + " centres, gates and ports (of " + expectedPositions + ") equal the layout's physical positions (" + world.LayoutSha256.Substring(0, 12) + ") after the frame move (" +
              (off.Count == 0 ? "none off" : off.Count + " off, first " + String.Join("; ", off.Take(3))) + "); records against the layout in the logical frame: " + world.PositionMismatches.Count + " mismatches");

        // The frame move changes positions only.
        var logicalText = String.Join("\n", world.LogicalDocuments.Select(x => Strip(x.Value)));
        var physicalText = String.Join("\n", world.Documents.Select(x => Strip(x.Value)));
        var hideoutX = physical.DocumentElement.ChildNodes.OfType<XmlElement>().Where(e => e.SelectSingleNode("Components/Hideout") != null).Select(e => D(e.GetAttribute("posX"))).ToList();
        Check(logicalText == physicalText && world.PositionsMoved > 0 && hideoutX.Count > 0 && hideoutX.All(x => x >= originX && x < originX + D(grid["cols"]) * spacing),
              "the registration copies differ from the validated documents only in positions (" + world.PositionsMoved + " positions moved by the frame origin " + originX + ", " + originY + "); every hideout lands in the canonical physical range");

        // The new game's registration plan.
        var plan = RegistrationPlan.FromDocuments(world.WorldPrefix, world.Documents, false, world.Declared);
        var installed = new HashSet<string>(catalog.Values.SelectMany(doc => doc.DocumentElement.ChildNodes.OfType<XmlElement>()).Where(e => e.HasAttribute("id") && ReferenceSchema.ElementType(e.Name) != null)
            .Select(e => ReferenceSchema.ElementType(e.Name) + "." + e.GetAttribute("id")), StringComparer.Ordinal);
        var problems = plan.Problems((t, i) => false, (t, i) => defaults.Contains(t + "." + i) || installed.Contains(t + "." + i));
        int family = plan.Objects.Where(o => o.Type == "Hero").Sum(o => o.References.Count(r => r.Type == "Hero" && (r.Attribute == "father" || r.Attribute == "mother" || r.Attribute == "spouse") &&
                                                                                          plan.Objects.First(x => x.Key == r.Key).Ordinal > o.Ordinal));
        Check(problems.Count == 0 && family > 0, "the documents plan through the MMR-30 registry with no problem (" + problems.Count + (problems.Count == 0 ? "" : ": " + String.Join("; ", problems.Take(3))) +
              "), " + family + " hero family references to a later hero among them (two-pass since MMR-45)");

        // Creation cultures: every startup creation culture is re-emitted with a start point inside the world.
        var startup = Load(Path.Combine(repo, "worldgen/policies/startup.json"));
        var expected = ((IEnumerable)O(startup, "starts")["creationCultures"]).Cast<object>().Select(x => (string)x).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var cultures = world.Documents.Single(x => x.Key == "SPCultures").Value.DocumentElement.ChildNodes.OfType<XmlElement>().ToList();
        Check(world.CreationCultures.SequenceEqual(expected) && cultures.All(c => D(c.GetAttribute("start_point_position_x")) >= originX && D(c.GetAttribute("start_point_position_y")) >= originY),
              "the creation cultures (" + String.Join(", ", world.CreationCultures) + ") are the startup policy's, each with a start point in the physical frame");

        // Determinism.
        var again = CompiledWorldDataset.Build(sources(layoutReport));
        Check(again.DocumentSha256.SequenceEqual(world.DocumentSha256) && again.Graph.Sha256 == world.Graph.Sha256,
              "building twice gives the same documents (" + String.Join(", ", world.DocumentSha256.Select(x => x.Key + " " + x.Value.Substring(0, 8))) + ") and graph report " + world.Graph.Sha256.Substring(0, 12));

        // Refusals: a settlement missing from the layout; a layout that is not activatable.
        var cut = (IDictionary<string, object>)Json.DeserializeObject(Json.Serialize(layoutReport));
        cut["settlements"] = ((IEnumerable)cut["settlements"]).Cast<object>().Skip(1).ToArray();
        var refusedLayout = (IDictionary<string, object>)Json.DeserializeObject(Json.Serialize(layoutReport)); refusedLayout["activatable"] = false;
        string cutError = Refusal(() => CompiledWorldDataset.Build(sources(cut))), refusedError = Refusal(() => CompiledWorldDataset.Build(sources(refusedLayout)));
        Check(cutError != null && cutError.Contains("places no position") && refusedError != null && refusedError.Contains("not activatable"),
              "a layout without a settlement's placement, and a layout that is not activatable, are refused (" + cutError + " | " + refusedError + ")");
        // A moved record is found by the position check.
        var moved = world.World.Settlements.First(x => x.Kind == "castle"); moved.GateX += 0.5;
        var placed = CompiledWorldDataset.Layout(layoutReport, originX, originY, D(grid["cols"]) * spacing);
        var mismatch = SettlementFactory.CheckResolvedPositions(world.World.Settlements, id => placed.ContainsKey(id) ? placed[id] : null);
        moved.GateX -= 0.5;
        Check(mismatch.Count == 1 && mismatch[0].StartsWith(moved.Id + " gate"), "a record moved off the layout (a castle gate by half a unit) is found: " + String.Join("; ", mismatch));
        // MMR-258 (RT-14): each record takes the prototype its scene look was built from, so the map look matches the scene the player enters;
        // a layout whose village look shows another type than the record's is refused.
        var looks = CompiledWorldDataset.Looks(layoutReport);
        int looked = looks.Count(kv => kv.Value.Key != null);
        bool prototypes = looked > 0 && looks.Where(kv => kv.Value.Key != null).All(kv => world.World.Settlements.First(x => x.Id == kv.Key).Prototype == kv.Value.Key);
        var otherType = (IDictionary<string, object>)Json.DeserializeObject(Json.Serialize(layoutReport));
        var village = ((IEnumerable)otherType["settlements"]).Cast<IDictionary<string, object>>().First(x => (string)x["kind"] == "village");
        var villageLook = (IDictionary<string, object>)village["look"]; villageLook["villageType"] = (string)villageLook["villageType"] == "wheat_farm" ? "lumberjack" : "wheat_farm";
        string typeError = Refusal(() => CompiledWorldDataset.Build(sources(otherType)));
        Check(prototypes && typeError != null && typeError.Contains("village looks"), "every record whose layout look names a prototype takes it (" + looked + " of " + looks.Count + " settlements; the rest phase 0 or variants pick from their pool), and a village look of another type than the record's is refused (" + typeError + ")");
        evidence["documents"] = world.DocumentSha256; evidence["origin"] = new[] { originX, originY };
    }

    // A document's text without its position attributes.
    private static string Strip(XmlDocument d)
    {
        var copy = (XmlDocument)d.CloneNode(true);
        foreach (var e in copy.DocumentElement.ChildNodes.OfType<XmlElement>())
            foreach (var a in new[] { "posX", "posY", "gate_posX", "gate_posY", "port_posX", "port_posY", "start_point_position_x", "start_point_position_y" }) e.RemoveAttribute(a);
        return copy.OuterXml;
    }

    private static string Refusal(Action build) { try { build(); return null; } catch (Exception e) { return e.Message; } }
}
