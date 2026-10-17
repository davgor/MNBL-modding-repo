using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-36: the read-only validation of a whole staged campaign graph before it registers (docs/campaign-graph-validation.md). Pure
    // managed. It owns full reference closure and the unexpected-Calradia census; the MMR-30 registry keeps its own plan checks.
    // From the staged documents (every factory's output, or the fixture dataset's), the installed catalog, the verified live defaults, the
    // compiler manifest, the factories' slot resolutions and dispositions, the runtime plan and the world's spawn positions, it checks:
    //   * input: every document has a root, every record element an id, every disposition, id-map entry, exclusion and slot its keys;
    //   * identity: every record has a registered type, no key twice, and every record is a generated object, a world alias or a re-emitted
    //     installed template; an installed world instance (a Calradia settlement, clan, kingdom or hero, or a Calradia hero's character) is
    //     flagged;
    //   * closure: every reference of every staged record, top-level, component or nested, resolves to a staged record, an installed template
    //     (followed transitively; a failure inside a template is reported against the generated record that reached it), or a verified live
    //     default; a world alias is exempt only when staged; references the installed loaders tolerate missing are noted, not refused; and
    //     every reference names an object of the type its property holds;
    //   * semantics: kingdom ruler, ruling clan and capital; clan leaders; heroes, their characters, clans and occupations; one settlement
    //     component of a known kind, with the location complex every installed settlement of its kind has; villages bound to a town or
    //     castle of the world; ports with shipyards and shipyards with ports; troop upgrade trees of every character the world reaches,
    //     without cycles; every clan with members has a generated fortified home (where its lords' parties start); every runtime hero intent
    //     an installed template of its occupation and a home of its kind, and every runtime relation two heroes of the world;
    //   * census: generated objects, aliases, re-emitted templates, referenced templates and defaults, by type; settlement, start and spawn
    //     positions that copy an installed Calradia coordinate;
    //   * sources: every compiler record (by source UID) is staged as its kind, a runtime intent, or has an explicit disposition of a policy
    //     outcome with a reason, never both; staged records agree with their compiler records (kind, village binding, port); every pending
    //     compiler slot of a delivered owner is resolved and, where the staged graph shows it, resolved in the graph; slots of owners not yet
    //     delivered are listed as deferred, never passing silently;
    //   * the identity of the resolving policies and the runtime inventory (catalog receipt, live defaults), for the activation receipt.
    // Diagnostics are aggregated (code, source UID, generated id, property path, reason) so one run reports every failure. Runtime position
    // and route checks are reported as pending, never as passing.
    internal sealed class CampaignGraphValidator
    {
        public sealed class Diagnostic { public string Code, SourceUid, GeneratedId, Path, Reason; }
        public sealed class SlotResolution { public string Record, Slot, Value; }
        public sealed class SourceDisposition { public string Uid, Id, Outcome, Reason; }
        public sealed class SpawnPoint { public string Label; public double X, Y; }

        public sealed class Input
        {
            public string WorldPrefix { get; set; }
            public List<KeyValuePair<string, XmlDocument>> Documents = new List<KeyValuePair<string, XmlDocument>>();
            public HashSet<string> Generated = new HashSet<string>(StringComparer.Ordinal);   // Type.id of every generated object (aliases included)
            public HashSet<string> Aliases = new HashSet<string>(StringComparer.Ordinal);     // alias ids (unprefixed)
            public Dictionary<string, XmlDocument> Catalog = new Dictionary<string, XmlDocument>(StringComparer.Ordinal);
            public HashSet<string> Defaults = new HashSet<string>(StringComparer.Ordinal);
            public IDictionary<string, object> Campaign { get; set; }                             // null for the fixture world
            public IDictionary<string, object> IdMap { get; set; }
            public List<SlotResolution> Resolutions = new List<SlotResolution>();
            public List<SourceDisposition> Dispositions = new List<SourceDisposition>();
            public SortedDictionary<string, string> Policies = new SortedDictionary<string, string>(StringComparer.Ordinal);
            public GeneratedCampaignInitializer.StartupPlan Plan { get; set; }
            public List<SpawnPoint> SpawnPoints = new List<SpawnPoint>();                        // spawn anchors, pirate zones, start points
            // MMR-33's explicit village rebindings (village -> its new bound settlement, and why): the only staged bindings that may differ
            // from the compiler's.
            public SortedDictionary<string, string> Rebound = new SortedDictionary<string, string>(StringComparer.Ordinal);
            public SortedDictionary<string, string> ReboundReason = new SortedDictionary<string, string>(StringComparer.Ordinal);
            // MMR-42: the mission scene catalogue a claimed prototypeAndScenes slot is checked against (the staged locations' scenes).
            public MNBL.ProceduralWorld.Missions.MissionScenes MissionScenes { get; set; }
        }

        public sealed class Report
        {
            public bool Ok;
            public List<Diagnostic> Diagnostics = new List<Diagnostic>();
            public SortedDictionary<string, object> Census = new SortedDictionary<string, object>(StringComparer.Ordinal);
            public SortedDictionary<string, string> Outcomes = new SortedDictionary<string, string>(StringComparer.Ordinal);   // source UID -> outcome
            public List<SortedDictionary<string, object>> DeferredSlots = new List<SortedDictionary<string, object>>();
            public List<string> OptionalAbsent = new List<string>();
            public List<string> Reached = new List<string>();                                    // installed templates and defaults the closure reached
            public List<string> PendingRuntime = new List<string>();
            public SortedDictionary<string, object> PolicyIdentity = new SortedDictionary<string, object>(StringComparer.Ordinal);
            public SortedDictionary<string, object> Inputs = new SortedDictionary<string, object>(StringComparer.Ordinal);    // staged documents, defaults
            public string Sha256;
            public string Summary(int max)
            {
                return (Ok ? "valid" : Diagnostics.Count + " diagnostics: " + String.Join("; ", Diagnostics.Take(max).Select(d => d.Code + " " + d.GeneratedId + (d.Path == null ? "" : " " + d.Path) + ": " + d.Reason)));
            }
            // The report as a probe or receipt records it: everything but the per-UID outcomes and the reached set, with at most max diagnostics.
            public Dictionary<string, object> Record(int max)
            {
                return new Dictionary<string, object> {
                    ["ok"] = Ok, ["sha256"] = Sha256, ["census"] = Census, ["deferredSlots"] = DeferredSlots.Count, ["optionalAbsent"] = OptionalAbsent.Count,
                    ["pendingRuntime"] = PendingRuntime, ["policyIdentity"] = PolicyIdentity, ["inputs"] = Inputs, ["diagnosticCount"] = Diagnostics.Count,
                    ["codes"] = Diagnostics.Select(d => d.Code).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList(),
                    ["diagnostics"] = Diagnostics.Take(max).Select(d => new Dictionary<string, object> { ["code"] = d.Code, ["sourceUid"] = d.SourceUid, ["generatedId"] = d.GeneratedId, ["path"] = d.Path, ["reason"] = d.Reason }).ToList() };
            }
        }

        public sealed class GraphException : InvalidOperationException
        {
            public GraphException(Report report) : base("Campaign graph refused: " + report.Summary(20)) { Report = report; }
            public Report Report { get; private set; }
        }

        // The graph policy ships inside the module (an embedded copy of worldgen/policies/graph-validation.json), so every registration is
        // validated against a fixed, hashed policy.
        public const string PolicyPath = "worldgen/policies/graph-validation.json";
        public const string CatalogReceiptKey = "runtime:catalogReceipt", LiveDefaultsKey = "runtime:liveDefaults";
        private const string PolicyResource = "MNBL.ProceduralWorld.graph-validation.json";
        private static byte[] InstalledPolicyBytes()
        {
            using (var stream = typeof(CampaignGraphValidator).Assembly.GetManifestResourceStream(PolicyResource))
            {
                if (stream == null) throw new InvalidOperationException("The module has no embedded graph validation policy (" + PolicyResource + ").");
                using (var copy = new MemoryStream()) { stream.CopyTo(copy); return copy.ToArray(); }
            }
        }
        public static IDictionary<string, object> InstalledPolicy()
        {
            return (IDictionary<string, object>)new JavaScriptSerializer().DeserializeObject(new UTF8Encoding(false).GetString(InstalledPolicyBytes()).TrimStart('﻿'));
        }
        public static string InstalledPolicySha256() { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(InstalledPolicyBytes())).Replace("-", "").ToLowerInvariant(); }
        // The identity of the live default inventory: sha256 of the sorted, LF-separated typed ids.
        public static string InventorySha256(IEnumerable<string> defaults) { return Sha256(String.Join("\n", defaults.OrderBy(x => x, StringComparer.Ordinal))); }

        // The in-game fixture world's input (no compiler manifest): the documents about to load, the dataset's declared objects, runtime plan
        // and positions, the installed catalog it was built from, the live defaults it was checked against, and the policies it was built with.
        public static Input FixtureInput(string worldPrefix, IEnumerable<KeyValuePair<string, XmlDocument>> documents, IEnumerable<string> declared,
            GeneratedCampaignInitializer.StartupPlan plan, IDictionary<string, XmlDocument> catalog, IEnumerable<string> defaults, IDictionary<string, string> policies,
            IEnumerable<SpawnPoint> spawnPoints)
        {
            var input = new Input { WorldPrefix = worldPrefix, Plan = plan };
            input.Documents.AddRange(documents); input.Generated.UnionWith(declared); input.Aliases.UnionWith(CampaignIdentity.Aliases);
            foreach (var c in catalog) input.Catalog[c.Key] = c.Value;
            input.Defaults.UnionWith(defaults);
            foreach (var p in policies) input.Policies[p.Key] = p.Value;
            input.SpawnPoints.AddRange(spawnPoints);
            return input;
        }

        private static readonly Regex Typed = ReferenceSchema.Typed;
        private readonly IDictionary<string, object> policy;
        private readonly Input input;
        private readonly Report report = new Report();
        private readonly List<KeyValuePair<string, XmlDocument>> documents = new List<KeyValuePair<string, XmlDocument>>();   // the ones with a root
        private readonly Dictionary<string, XmlElement> staged = new Dictionary<string, XmlElement>(StringComparer.Ordinal);   // Type.id -> staged record or component
        private readonly Dictionary<string, XmlElement> installed = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
        private readonly HashSet<string> heroCharacters = new HashSet<string>(StringComparer.Ordinal);    // ids of installed Calradia heroes
        private readonly HashSet<string> worldTypes, reusable, registered;
        private readonly Dictionary<string, string> kindOf = new Dictionary<string, string>(StringComparer.Ordinal);   // staged key -> generated | alias | reemitted | vanilla | unknown
        private readonly Dictionary<string, string> origin = new Dictionary<string, string>(StringComparer.Ordinal);   // key -> the staged record that reached it
        private readonly HashSet<string> reached = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<SourceDisposition> dispositions = new List<SourceDisposition>();                         // the well-formed ones
        private Dictionary<string, string> uidById;

        private CampaignGraphValidator(IDictionary<string, object> policy, Input input)
        {
            this.policy = policy; this.input = input;
            worldTypes = new HashSet<string>(List(policy, "worldTypes").Cast<string>(), StringComparer.Ordinal);
            reusable = new HashSet<string>(List(policy, "reusableTypes").Cast<string>(), StringComparer.Ordinal);
            registered = new HashSet<string>(List(policy, "registeredTypes").Cast<string>(), StringComparer.Ordinal);
        }

        public static Report Validate(IDictionary<string, object> policy, Input input)
        {
            var v = new CampaignGraphValidator(policy, input);
            v.Run();
            return v.report;
        }

        private void Add(string code, string generatedId, string path, string reason, string uid = null)
        {
            report.Diagnostics.Add(new Diagnostic { Code = code, SourceUid = uid ?? UidOf(generatedId), GeneratedId = generatedId, Path = path, Reason = reason });
        }

        private string UidOf(string key)
        {
            if (key == null || uidById == null) return null;
            string id = key.Contains('.') ? key.Substring(key.IndexOf('.') + 1) : key, uid;
            return uidById.TryGetValue(id, out uid) ? uid : null;
        }

        private void Run()
        {
            // Every stage runs whatever an earlier one found; an input the stages cannot read is a diagnostic, never an exception.
            foreach (var stage in new Action[] { Inputs, IndexInstalled, IndexStaged, Closure, ReferenceTypes, Semantics, Positions, Sources, Identity })
            {
                try { stage(); }
                catch (Exception error) { Add("graph.malformed_input", null, stage.Method.Name, "the " + stage.Method.Name + " stage could not read its input: " + error.GetType().Name + ": " + error.Message); }
            }
            report.PendingRuntime.AddRange(List(policy, "pendingRuntime").Cast<string>());
            report.Ok = report.Diagnostics.Count == 0;
            report.Sha256 = Sha256(Canonical());
        }

        // Input shape: documents with a root; dispositions, id-map entries and exclusions with their keys; dispositions of a policy outcome
        // with a reason.
        private void Inputs()
        {
            if (Text(policy, "schema") != "mnbl.graph-validation" || Int(policy, "version") != 1) Add("graph.policy", null, null, "not mnbl.graph-validation version 1");
            foreach (var d in input.Documents)
                if (d.Value == null || d.Value.DocumentElement == null) Add("graph.malformed_input", null, d.Key, "the staged document " + d.Key + " has no root element");
                else documents.Add(d);
            var allowed = Obj(policy, "dispositions") ?? new Dictionary<string, object>();
            foreach (var d in input.Dispositions)
            {
                if (d == null || (d.Uid == null && d.Id == null)) { Add("graph.malformed_input", null, "dispositions", "a disposition names neither a source UID nor an id"); continue; }
                if (d.Outcome != "instantiated" && (String.IsNullOrEmpty(d.Outcome) || !allowed.ContainsKey(d.Outcome)))
                    Add("graph.disposition", d.Id, "outcome", "the disposition outcome '" + d.Outcome + "' is not a policy outcome (" + String.Join(", ", allowed.Keys) + ")", d.Uid);
                else if (String.IsNullOrEmpty(d.Reason)) Add("graph.disposition", d.Id, "reason", "the " + d.Outcome + " disposition gives no reason", d.Uid);
                else dispositions.Add(d);
            }
            if (input.IdMap != null)
            {
                foreach (var e in Records(input.IdMap, "entries").Where(e => Text(e, "uid") == null || Text(e, "id") == null || Text(e, "kind") == null))
                    Add("graph.malformed_input", Text(e, "id"), "id-map", "an id-map entry lacks its uid, id or kind", Text(e, "uid"));
                uidById = Records(input.IdMap, "entries").Where(e => Text(e, "id") != null && Text(e, "uid") != null).GroupBy(e => Text(e, "id")).ToDictionary(g => g.Key, g => Text(g.First(), "uid"), StringComparer.Ordinal);
            }
            if (input.Campaign != null)
                foreach (var g in Records(input.Campaign, "exclusions").GroupBy(e => Text(e, "id") ?? ""))
                {
                    if (g.Key.Length == 0) Add("graph.malformed_input", null, "exclusions", "an exclusion names no id");
                    else if (g.Count() > 1) Add("graph.malformed_input", g.Key, "exclusions", "the record is excluded " + g.Count() + " times");
                    if (g.Any(e => String.IsNullOrEmpty(Text(e, "reason")))) Add("graph.disposition", g.Key, "exclusions", "an exclusion gives no reason");
                }
        }

        private static IEnumerable<KeyValuePair<string, XmlElement>> Records(XmlDocument doc)
        {
            if (doc == null || doc.DocumentElement == null) yield break;
            foreach (var e in doc.DocumentElement.ChildNodes.OfType<XmlElement>().Where(e => e.HasAttribute("id")))
            {
                string type = ReferenceSchema.ElementType(e.Name);
                yield return new KeyValuePair<string, XmlElement>(type == null ? "?" + e.Name : type + "." + e.GetAttribute("id"), e);
                foreach (var c in e.SelectNodes("Components/*").OfType<XmlElement>().Where(c => c.HasAttribute("id")))
                    yield return new KeyValuePair<string, XmlElement>(ComponentType(c) + "." + c.GetAttribute("id"), c);
            }
        }

        private static string ComponentType(XmlElement c) { return c.Name == "CustomSettlementComponent" && c.HasAttribute("component_name") ? c.GetAttribute("component_name") : c.Name; }

        private void IndexInstalled()
        {
            foreach (var doc in input.Catalog.Values)
                foreach (var r in Records(doc))
                    if (!r.Key.StartsWith("?", StringComparison.Ordinal) && !installed.ContainsKey(r.Key)) installed[r.Key] = r.Value;
            heroCharacters.UnionWith(installed.Keys.Where(k => k.StartsWith("Hero.", StringComparison.Ordinal)).Select(k => k.Substring(5)));
        }

        // An installed Calradia world instance: a settlement, clan, kingdom or hero record, or the character of an installed hero.
        private bool WorldInstance(string type, string id) { return worldTypes.Contains(type) || (type == "NPCCharacter" && heroCharacters.Contains(id)); }

        // Identity: types, duplicates, and what each staged record is. A component shares its record's fate: the components of a refused,
        // vanilla or unknown record get no diagnostics of their own, and a vanilla record is not walked (its own references are Calradia's).
        private void IndexStaged()
        {
            foreach (var doc in documents)
            {
                foreach (var e in doc.Value.DocumentElement.ChildNodes.OfType<XmlElement>().Where(e => !e.HasAttribute("id")))
                    Add("graph.malformed_input", null, doc.Key + "/" + e.Name, "a " + e.Name + " record element without an id (the registry and the loaders skip it silently)");
                foreach (var c in doc.Value.DocumentElement.SelectNodes("*/Components/*").OfType<XmlElement>().Where(c => !c.HasAttribute("id")))
                    Add("graph.malformed_input", ((XmlElement)c.ParentNode.ParentNode).GetAttribute("id"), "Components/" + c.Name, "a settlement component without an id");
                string parentState = null;
                foreach (var r in Records(doc.Value))
                {
                    bool component = r.Value.ParentNode != null && r.Value.ParentNode.Name == "Components";
                    if (component && parentState != null)
                    {
                        if (parentState != "refused" && !staged.ContainsKey(r.Key)) { staged[r.Key] = r.Value; kindOf[r.Key] = parentState; }
                        continue;
                    }
                    if (!component) parentState = null;
                    if (r.Key.StartsWith("?", StringComparison.Ordinal)) { Add("graph.unknown_type", r.Key.Substring(1) + "." + r.Value.GetAttribute("id"), doc.Key, "element " + r.Key.Substring(1) + " has no supported object type"); parentState = "refused"; continue; }
                    string type = r.Key.Substring(0, r.Key.IndexOf('.')), id = r.Key.Substring(r.Key.IndexOf('.') + 1);
                    if (!registered.Contains(type)) { Add("graph.unknown_type", r.Key, doc.Key, type + " is not an object type the installed assemblies register"); if (!component) parentState = "refused"; continue; }
                    if (staged.ContainsKey(r.Key)) { Add("graph.duplicate_id", r.Key, doc.Key, "the same object is staged twice"); if (!component) parentState = "refused"; continue; }
                    staged[r.Key] = r.Value;
                    if (input.Aliases.Contains(id)) kindOf[r.Key] = "alias";
                    else if (input.Generated.Contains(r.Key) || id.StartsWith(input.WorldPrefix, StringComparison.Ordinal)) kindOf[r.Key] = "generated";
                    else if (installed.ContainsKey(r.Key) && WorldInstance(type, id))
                    {
                        kindOf[r.Key] = "vanilla"; if (!component) parentState = "vanilla";
                        Add("graph.unexpected_vanilla", r.Key, doc.Key, type == "NPCCharacter" && !worldTypes.Contains(type) ? "the character of installed Calradia hero " + id + " is staged in the generated world"
                                                                                                                          : "an installed Calradia " + type + " is staged in the generated world");
                    }
                    else if (installed.ContainsKey(r.Key))
                    {
                        kindOf[r.Key] = "reemitted";
                        if (!reusable.Contains(type)) Add("graph.unknown_object", r.Key, doc.Key, "installed " + type + " records are not a permitted reused template type");
                    }
                    else { kindOf[r.Key] = "unknown"; if (!component) parentState = "unknown"; Add("graph.unknown_object", r.Key, doc.Key, "neither a generated object, a world alias nor an installed record"); }
                }
            }
        }

        private bool Record(string key) { XmlElement e; return staged.TryGetValue(key, out e) && (e.ParentNode == null || e.ParentNode.Name != "Components"); }

        // Closure over staged records and, transitively, the installed templates they reference. A failure inside a template is reported
        // against the staged record that reached it, with the template in the path.
        private void Closure()
        {
            var templates = new Dictionary<string, int>(StringComparer.Ordinal); var defaults = new Dictionary<string, int>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            foreach (var key in staged.Keys.Where(k => Record(k) && kindOf[k] != "vanilla" && kindOf[k] != "unknown")) { origin[key] = key; queue.Enqueue(key); }
            while (queue.Count > 0)
            {
                string key = queue.Dequeue(), from = origin[key];
                bool template = !staged.ContainsKey(key);
                XmlElement element = template ? installed[key] : staged[key];
                Func<string, string> at = path => template ? key + ":" + path : path;
                Func<string, string> where = target => template ? " (inside installed template " + key + ")" : "";
                foreach (var reference in ReferenceSchema.References(element, m => Add("graph.malformed_reference", from, template ? key : null, m)).GroupBy(r => r.Key).Select(g => g.First()).ToList())
                {
                    string dep = reference.Key, contract = null;
                    if (dep.StartsWith("?", StringComparison.Ordinal)) { int bar = dep.IndexOf('|'); contract = dep.Substring(1, bar - 1); dep = dep.Substring(bar + 1); }
                    int dot = dep.IndexOf('.');
                    string depType = dep.Substring(0, dot), depId = dep.Substring(dot + 1);
                    if (staged.ContainsKey(dep)) continue;   // staged records are all walked; a staged vanilla record is already refused
                    XmlElement found;
                    if (installed.TryGetValue(dep, out found))
                    {
                        if (WorldInstance(depType, depId)) { Add("graph.official_reference", from, at(reference.Value), dep + ": an installed Calradia " + (worldTypes.Contains(depType) ? depType : "hero's character") + " the generated world does not stage" + where(dep)); continue; }
                        if (reached.Add(dep)) { int n; templates.TryGetValue(depType, out n); templates[depType] = n + 1; origin[dep] = from; queue.Enqueue(dep); }
                        continue;
                    }
                    if (input.Defaults.Contains(dep)) { if (reached.Add(dep)) { int n; defaults.TryGetValue(depType, out n); defaults[depType] = n + 1; } continue; }
                    if (contract != null) { report.OptionalAbsent.Add(dep + " from " + key + " (" + contract + ")"); continue; }
                    Add("graph.unresolved_reference", from, at(reference.Value), dep + " resolves to nothing staged, installed or defaulted" + where(dep));
                }
            }
            report.Census["templatesReferenced"] = new SortedDictionary<string, int>(templates, StringComparer.Ordinal);
            report.Census["defaultsReferenced"] = new SortedDictionary<string, int>(defaults, StringComparer.Ordinal);
            report.Census["generated"] = Count("generated"); report.Census["aliases"] = Count("alias"); report.Census["reemittedTemplates"] = Count("reemitted");
            report.Census["unexpectedVanilla"] = Count("vanilla"); report.Census["unknown"] = Count("unknown");
            report.OptionalAbsent = report.OptionalAbsent.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            report.Reached = reached.OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        private SortedDictionary<string, int> Count(string kind)
        {
            return new SortedDictionary<string, int>(kindOf.Where(k => k.Value == kind).GroupBy(k => k.Key.Substring(0, k.Key.IndexOf('.'))).ToDictionary(g => g.Key, g => g.Count()), StringComparer.Ordinal);
        }

        // The type of object each typed property holds (the installed Deserialize methods read them with GetObject<T>).
        private static string ExpectedType(string recordType, XmlElement e, string attribute)
        {
            if (attribute == "culture") return "Culture";
            switch (recordType + "/" + (e.ParentNode is XmlElement && e.ParentNode.ParentNode is XmlDocument ? "" : e.Name) + "@" + attribute)
            {
                case "Hero/@faction": return "Faction";
                case "Hero/@spouse": case "Hero/@father": case "Hero/@mother": return "Hero";
                case "Hero/@born_settlement": return "Settlement";
                case "Kingdom/@owner": return "Hero";
                case "Kingdom/@initial_home_settlement": case "Faction/@initial_home_settlement": return "Settlement";
                case "Faction/@owner": return "Hero";
                case "Faction/@super_faction": return "Kingdom";
                case "Settlement/@owner": return "Faction";
                case "Settlement/Village@bound": case "Settlement/Village@trade_bound": return "Settlement";
                case "Settlement/Village@village_type": return "VillageType";
                case "Settlement/Locations@complex_template": return "LocationComplexTemplate";
                case "NPCCharacter/@skill_template": return "SkillSet";
                case "NPCCharacter/upgrade_target@id": return "NPCCharacter";
            }
            return attribute == "troop" ? "NPCCharacter" : null;
        }

        // Every typed property of a generated or alias record names an object of the type the property holds.
        private void ReferenceTypes()
        {
            foreach (var s in staged.Where(x => Record(x.Key) && (kindOf[x.Key] == "generated" || kindOf[x.Key] == "alias")))
            {
                string recordType = s.Key.Substring(0, s.Key.IndexOf('.'));
                foreach (XmlElement e in s.Value.SelectNodes("descendant-or-self::*"))
                    foreach (XmlAttribute a in e.Attributes)
                    {
                        var m = Typed.Match(a.Value);
                        if (!m.Success) continue;
                        string expected = ExpectedType(recordType, e, a.Name);
                        if (expected != null && m.Groups[1].Value != expected)
                            Add("graph.reference_type", s.Key, (e == s.Value ? "" : e.Name + "/") + "@" + a.Name, a.Value + " is a " + m.Groups[1].Value + "; the property holds a " + expected);
                    }
            }
        }

        private IEnumerable<KeyValuePair<string, XmlElement>> Generated(string type)
        {
            return staged.Where(s => s.Key.StartsWith(type + ".", StringComparison.Ordinal) && (kindOf[s.Key] == "generated" || kindOf[s.Key] == "alias"));
        }

        private static string Ref(XmlElement e, string attribute) { var v = e.GetAttribute(attribute); return String.IsNullOrEmpty(v) ? null : v; }
        private static XmlElement Component(XmlElement settlement) { return settlement == null ? null : settlement.SelectNodes("Components/*").OfType<XmlElement>().FirstOrDefault(); }

        // town, castle, village, hideout, or the component type of another settlement.
        private static string SettlementKind(XmlElement settlement)
        {
            var c = Component(settlement);
            if (c == null) return null;
            string type = ComponentType(c);
            return type == "Town" ? (c.GetAttribute("is_castle") == "true" ? "castle" : "town") : type.ToLowerInvariant();
        }

        private XmlElement Get(string key) { XmlElement e; return key != null && staged.TryGetValue(key, out e) ? e : null; }
        private XmlElement Find(string key) { XmlElement e; return key == null ? null : staged.TryGetValue(key, out e) ? e : installed.TryGetValue(key, out e) ? e : null; }

        private void Semantics()
        {
            var clans = Generated("Faction").ToList(); var kingdoms = Generated("Kingdom").ToList(); var heroes = Generated("Hero").ToList();
            var settlements = Generated("Settlement").ToList();
            Func<string, XmlElement> character = heroKey => heroKey == null ? null : Get("NPCCharacter." + heroKey.Substring(heroKey.IndexOf('.') + 1));
            // Kingdoms: ruler, ruling clan and capital.
            foreach (var k in kingdoms)
            {
                string owner = Ref(k.Value, "owner");
                var members = clans.Where(c => Ref(c.Value, "super_faction") == "Kingdom." + k.Value.GetAttribute("id")).ToList();
                var ruling = members.Where(c => Ref(c.Value, "owner") == owner).ToList();
                if (owner == null || Get(owner) == null) Add("graph.ownership", k.Key, "@owner", "the kingdom has no staged ruler");
                else
                {
                    if (ruling.Count != 1) Add("graph.ownership", k.Key, "@owner", "its ruler leads " + ruling.Count + " of its clans, not exactly one (the ruling clan)");
                    if (kindOf[k.Key] == "generated" && character(owner) != null && character(owner).GetAttribute("occupation") != "Lord")
                        Add("graph.hero_role", k.Key, "@owner", "its ruler " + owner + " is a " + character(owner).GetAttribute("occupation") + ", not a Lord");
                }
                string capital = Ref(k.Value, "initial_home_settlement");
                var capitalRecord = Get(capital);
                if (capitalRecord == null) Add("graph.ownership", k.Key, "@initial_home_settlement", "the capital " + capital + " is not staged");
                else if (!members.Any(c => "Faction." + c.Value.GetAttribute("id") == Ref(capitalRecord, "owner"))) Add("graph.ownership", k.Key, "@initial_home_settlement", "the capital " + capital + " is not held by a clan of the kingdom");
            }
            // Clans: leader and home; a clan with members has a fortified generated home, where its lords' parties start.
            var heroFaction = heroes.ToDictionary(h => h.Value.GetAttribute("id"), h => Ref(h.Value, "faction"), StringComparer.Ordinal);
            foreach (var c in clans)
            {
                if (Ref(c.Value, "is_bandit") == "true") { if (Get(Ref(c.Value, "initial_home_settlement")) == null) Add("graph.party_intent", c.Key, "@initial_home_settlement", "the bandit clan has no staged home"); continue; }
                string owner = Ref(c.Value, "owner"), cid = "Faction." + c.Value.GetAttribute("id");
                if (owner == null || Get(owner) == null) Add("graph.ownership", c.Key, "@owner", "the clan has no staged leader");
                else
                {
                    string leaderFaction; heroFaction.TryGetValue(owner.Substring(owner.IndexOf('.') + 1), out leaderFaction);
                    if (leaderFaction != cid) Add("graph.ownership", c.Key, "@owner", "its leader " + owner + " belongs to " + (leaderFaction ?? "no clan"));
                    if (kindOf[c.Key] == "generated" && character(owner) != null && character(owner).GetAttribute("occupation") != "Lord")
                        Add("graph.hero_role", c.Key, "@owner", "its leader " + owner + " is a " + character(owner).GetAttribute("occupation") + ", not a Lord");
                }
                var home = Get(Ref(c.Value, "initial_home_settlement"));
                bool hasMembers = heroFaction.Values.Any(f => f == cid);
                if (home == null) Add("graph.party_intent", c.Key, "@initial_home_settlement", "the clan has no staged home");
                else if (hasMembers && SettlementKind(home) != "town" && SettlementKind(home) != "castle") Add("graph.party_intent", c.Key, "@initial_home_settlement", "its home " + Ref(c.Value, "initial_home_settlement") + " is a " + (SettlementKind(home) ?? "settlement without a component") + ", not a town or castle, where its lords' parties start");
            }
            // Heroes and their characters: a hero character of the hero's id, a clan, and a Lord when the clan is a noble clan of the world.
            foreach (var h in heroes)
            {
                string id = h.Value.GetAttribute("id");
                var npc = Get("NPCCharacter." + id);
                if (npc == null) Add("graph.hero_role", h.Key, "NPCCharacter", "the hero has no staged character of its id");
                else if (npc.GetAttribute("is_hero") != "true" || npc.GetAttribute("is_template") == "true") Add("graph.hero_role", h.Key, "NPCCharacter", "its character is not a hero (is_hero '" + npc.GetAttribute("is_hero") + "', is_template '" + npc.GetAttribute("is_template") + "')");
                var clan = Get(Ref(h.Value, "faction"));
                if (Ref(h.Value, "faction") == null) Add("graph.hero_role", h.Key, "@faction", "the hero belongs to no clan");
                else if (npc != null && clan != null && kindOf[h.Key] == "generated" && clan.GetAttribute("is_noble") == "true" && npc.GetAttribute("occupation") != "Lord")
                    Add("graph.hero_role", h.Key, "NPCCharacter/@occupation", "a hero of noble clan " + Ref(h.Value, "faction") + " is a " + npc.GetAttribute("occupation") + ", not a Lord");
            }
            // Settlements: one component of a known kind and the location complex every installed settlement of its kind has; bindings;
            // ports and shipyards.
            var installedKinds = installed.Where(x => x.Key.StartsWith("Settlement.", StringComparison.Ordinal) && Component(x.Value) != null).GroupBy(x => SettlementKind(x.Value)).ToList();
            var located = new HashSet<string>(installedKinds.Where(g => g.All(x => x.Value.SelectSingleNode("Locations/@complex_template") != null)).Select(g => g.Key), StringComparer.Ordinal);
            report.Census["locationComplexRequired"] = located.OrderBy(x => x, StringComparer.Ordinal).ToList();
            var fortified = new HashSet<string>(settlements.Where(s => SettlementKind(s.Value) == "town" || SettlementKind(s.Value) == "castle").Select(s => "Settlement." + s.Value.GetAttribute("id")), StringComparer.Ordinal);
            foreach (var s in settlements)
            {
                var comps = s.Value.SelectNodes("Components/*").OfType<XmlElement>().ToList();
                if (comps.Count != 1) { Add("graph.settlement_component", s.Key, "Components", comps.Count + " components, not one"); continue; }
                var c = comps[0];
                string kind = ComponentType(c), settlementKind = SettlementKind(s.Value);
                if (!List(policy, "settlementComponents").Cast<string>().Contains(kind)) { Add("graph.settlement_component", s.Key, "Components/" + c.Name, "component " + kind + " is not a supported settlement kind"); continue; }
                if (located.Contains(settlementKind) && s.Value.SelectSingleNode("Locations/@complex_template") == null) Add("graph.settlement_component", s.Key, "Locations/@complex_template", "the " + settlementKind + " has no location complex (every installed " + settlementKind + " has one)");
                bool port = s.Value.HasAttribute("port_posX");
                if (kind == "Town")
                {
                    bool castle = settlementKind == "castle";
                    bool shipyard = c.SelectSingleNode("Buildings/Building[@id='building_shipyard']") != null;
                    if (Ref(s.Value, "owner") == null) Add("graph.ownership", s.Key, "@owner", "the " + settlementKind + " has no owner clan");
                    if (!castle && port && !shipyard) Add("graph.port", s.Key, "Components/Town/Buildings", "a port town without building_shipyard (CanTownCreateShipFromHull dereferences it)");
                    if (shipyard && (!port || castle)) Add("graph.port", s.Key, "Components/Town/Buildings", "a shipyard without a town port");
                }
                else if (kind == "Village")
                {
                    string bound = Ref(c, "bound");
                    if (bound == null || !fortified.Contains(bound)) Add("graph.village_binding", s.Key, "Components/Village/@bound", "bound to " + (bound ?? "nothing") + ", not a town or castle of the world");
                    if (Ref(c, "village_type") == null) Add("graph.settlement_component", s.Key, "Components/Village/@village_type", "the village has no type");
                }
            }
            // Troop upgrade trees of every character the world stages or reaches: no cycle.
            var characters = staged.Where(x => x.Key.StartsWith("NPCCharacter.", StringComparison.Ordinal)).Concat(installed.Where(x => x.Key.StartsWith("NPCCharacter.", StringComparison.Ordinal) && !staged.ContainsKey(x.Key)))
                .ToDictionary(x => x.Key, x => x.Value.SelectNodes("upgrade_targets/upgrade_target/@id").OfType<XmlAttribute>().Select(a => a.Value).ToList(), StringComparer.Ordinal);
            var roots = characters.Keys.Where(k => (staged.ContainsKey(k) && kindOf[k] != "vanilla") || reached.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var state = new Dictionary<string, int>(StringComparer.Ordinal);
            int trees = 0;
            foreach (var start in roots)
            {
                if (characters[start].Count > 0) trees++;
                var stack = new Stack<KeyValuePair<string, int>>(); stack.Push(new KeyValuePair<string, int>(start, 0));
                while (stack.Count > 0)
                {
                    var top = stack.Pop(); int seen; state.TryGetValue(top.Key, out seen);
                    if (top.Value == 0)
                    {
                        if (seen == 2) continue;
                        if (seen == 1) { Add("graph.troop_upgrade", origin.ContainsKey(top.Key) ? origin[top.Key] : top.Key, top.Key + ":upgrade_targets", "the upgrade tree loops back to " + top.Key); continue; }
                        state[top.Key] = 1; stack.Push(new KeyValuePair<string, int>(top.Key, 1));
                        List<string> next; if (characters.TryGetValue(top.Key, out next)) foreach (var n in next) stack.Push(new KeyValuePair<string, int>(n, 0));
                    }
                    else state[top.Key] = 2;
                }
            }
            report.Census["upgradeTreesChecked"] = trees;
            // Runtime hero intents: an installed template of the intent's occupation and a home of its kind; runtime relations between heroes
            // of the world.
            if (input.Plan != null)
            {
                var notableOccupations = List(Obj(policy, "runtimeHeroes"), "notableOccupations").Cast<string>().ToList();
                var companionOccupations = List(Obj(policy, "runtimeHeroes"), "companionOccupations").Cast<string>().ToList();
                foreach (var intent in input.Plan.Notables.Select(n => new { Hero = n, Notable = true }).Concat(input.Plan.Companions.Select(c => new { Hero = c, Notable = false })))
                {
                    var h = intent.Hero;
                    var template = Find("NPCCharacter." + h.Template);
                    string occupation = template == null ? null : template.GetAttribute("occupation");
                    if (template == null || template.GetAttribute("is_template") != "true") Add("graph.party_intent", h.Id, "template", "the runtime hero's template " + h.Template + " is not an installed template", h.Uid);
                    else if (!(intent.Notable ? notableOccupations : companionOccupations).Contains(occupation) || (h.Occupation != null && h.Occupation != occupation))
                        Add("graph.party_intent", h.Id, "template", "the runtime " + (intent.Notable ? "notable" : "companion") + "'s template " + h.Template + " is a " + occupation + (h.Occupation != null ? " (the intent says " + h.Occupation + ")" : ""), h.Uid);
                    var home = Get("Settlement." + h.Settlement);
                    string kind = SettlementKind(home);
                    if (home == null || kindOf["Settlement." + h.Settlement] != "generated") Add("graph.party_intent", h.Id, "settlement", "the runtime hero's home " + h.Settlement + " is not a generated settlement", h.Uid);
                    else if (intent.Notable ? kind != "town" && kind != "village" : kind != "town")
                        Add("graph.party_intent", h.Id, "settlement", "a runtime " + (intent.Notable ? "notable" : "companion") + " homed at a " + kind + " (" + (intent.Notable ? "notables live in towns and villages" : "companions wait in town taverns") + ")", h.Uid);
                }
                var runtimeIds = new HashSet<string>(input.Plan.Notables.Concat(input.Plan.Companions).Select(h => h.Id), StringComparer.Ordinal);
                foreach (var r in input.Plan.Relations)
                    foreach (var hero in new[] { r.A, r.B })
                        if (hero == null || (!runtimeIds.Contains(hero) && Get("Hero." + hero) == null)) Add("graph.party_intent", hero, "relations", "a runtime relation names " + (hero ?? "nobody") + ", neither a staged hero nor a runtime intent");
            }
        }

        // Settlement positions, culture start points and spawn positions that copy an installed Calradia coordinate (an installed settlement,
        // gate or port position, or any installed culture's start point).
        private void Positions()
        {
            double tolerance = Real(policy, "positionTolerance");
            var points = new List<double[]>();
            foreach (var s in installed.Where(x => x.Key.StartsWith("Settlement.", StringComparison.Ordinal)).Select(x => x.Value))
                foreach (var a in new[] { "posX", "gate_posX", "port_posX" })
                {
                    double x, y;
                    if (Num(s, a, out x) && Num(s, a.Replace("X", "Y"), out y)) points.Add(new[] { x, y });
                }
            foreach (var c in installed.Where(x => x.Key.StartsWith("Culture.", StringComparison.Ordinal)).Select(x => x.Value))
            {
                double x, y;
                if (Num(c, "start_point_position_x", out x) && Num(c, "start_point_position_y", out y)) points.Add(new[] { x, y });
            }
            Func<double, double, bool> copied = (x, y) => points.Any(p => Math.Abs(p[0] - x) < tolerance && Math.Abs(p[1] - y) < tolerance);
            foreach (var s in Generated("Settlement"))
                foreach (var a in new[] { "posX", "gate_posX", "port_posX" })
                {
                    double x, y;
                    if (Num(s.Value, a, out x) && Num(s.Value, a.Replace("X", "Y"), out y) && copied(x, y)) Add("graph.fixed_position", s.Key, "@" + a, "(" + Num(x) + ", " + Num(y) + ") is an installed Calradia coordinate");
                }
            foreach (var c in staged.Where(x => x.Key.StartsWith("Culture.", StringComparison.Ordinal) && kindOf[x.Key] != "vanilla"))
            {
                double x, y;
                if (Num(c.Value, "start_point_position_x", out x) && Num(c.Value, "start_point_position_y", out y) && copied(x, y))
                    Add("graph.fixed_position", c.Key, "@start_point_position", "the start point (" + Num(x) + ", " + Num(y) + ") is an installed Calradia coordinate");
            }
            foreach (var p in input.SpawnPoints.Where(p => copied(p.X, p.Y)))
                Add("graph.fixed_position", p.Label, "spawn", "the spawn position (" + Num(p.X) + ", " + Num(p.Y) + ") is an installed Calradia coordinate");
            report.Census["positionsChecked"] = new SortedDictionary<string, int>(StringComparer.Ordinal) { ["settlements"] = Generated("Settlement").Count(), ["spawnPoints"] = input.SpawnPoints.Count };
        }

        // Source records and compiler slots.
        private void Sources()
        {
            if (input.Campaign == null) { report.Census["sources"] = "not applicable (no compiler manifest: a fixture world)"; return; }
            var byUid = dispositions.Where(d => d.Uid != null).GroupBy(d => d.Uid).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var byId = dispositions.Where(d => d.Id != null).GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var excluded = Records(input.Campaign, "exclusions").Where(e => Text(e, "id") != null).GroupBy(e => Text(e, "id")).ToDictionary(g => g.Key, g => Text(g.First(), "reason"), StringComparer.Ordinal);
            var typeOf = new Dictionary<string, string> { ["town"] = "Settlement", ["castle"] = "Settlement", ["village"] = "Settlement", ["clan"] = "Faction", ["kingdom"] = "Kingdom", ["hero"] = "Hero" };
            var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
            var outcomeById = new Dictionary<string, string>(StringComparer.Ordinal);
            var runtime = new HashSet<string>(input.Plan == null ? Enumerable.Empty<string>() : input.Plan.Notables.Concat(input.Plan.Companions).Select(h => h.Id), StringComparer.Ordinal);
            // Every source UID the compiler named: its id-map entries, and the source heroes it did not instantiate (no id).
            var sources = Records(input.IdMap, "entries").Where(e => Text(e, "uid") != null).Select(e => new { Uid = Text(e, "uid"), Id = Text(e, "id"), Kind = Text(e, "kind") })
                .Concat(List(input.Campaign, "notInstantiated").OfType<string>().Select(u => new { Uid = u, Id = (string)null, Kind = "hero" }))
                .GroupBy(e => e.Uid).Select(g => g.First()).OrderBy(e => e.Uid, StringComparer.Ordinal);
            foreach (var e in sources)
            {
                string uid = e.Uid, id = e.Id, kind = e.Kind, type, outcome = null;
                SourceDisposition d = null;
                if (!byUid.TryGetValue(uid, out d) && id != null) byId.TryGetValue(id, out d);
                string exclusion = null; bool isExcluded = id != null && excluded.TryGetValue(id, out exclusion);
                bool isStaged = typeOf.TryGetValue(kind ?? "", out type) && id != null && staged.ContainsKey(type + "." + id);
                bool isRuntime = kind == "hero" && id != null && runtime.Contains(id);
                if (isStaged || isRuntime)
                {
                    outcome = isStaged ? "staged" : "runtime intent";
                    if (isStaged && type == "Settlement" && SettlementKind(staged[type + "." + id]) != kind)
                        Add("graph.source_mismatch", type + "." + id, "Components", "the compiler " + kind + " is staged as a " + (SettlementKind(staged[type + "." + id]) ?? "settlement without a component"), uid);
                    if ((d != null && d.Outcome != "instantiated") || isExcluded)
                        Add("graph.disposition", id, kind, "the compiler " + kind + " is " + outcome + " although it is " + (isExcluded ? "excluded (" + exclusion + ")" : d.Outcome + " (" + d.Reason + ")"), uid);
                }
                else if (d != null)
                {
                    if (d.Outcome == "instantiated") Add("graph.silent_discard", id, kind, "its disposition says instantiated (" + d.Reason + ") but it is neither staged nor a runtime intent", uid);
                    else outcome = d.Outcome + ": " + d.Reason;
                }
                else if (isExcluded) outcome = "excluded: " + exclusion;
                if (outcome == null) { if (!report.Diagnostics.Any(x => x.SourceUid == uid && x.Code == "graph.silent_discard")) Add("graph.silent_discard", id, kind, "the compiler " + kind + " has neither a generated object nor an explicit disposition", uid); outcome = "MISSING"; }
                report.Outcomes[uid] = outcome;
                if (id != null) outcomeById[id] = outcome;
                string bucket = outcome.Split(':')[0]; int n; outcomes.TryGetValue(kind + " " + bucket, out n); outcomes[kind + " " + bucket] = n + 1;
            }
            report.Census["sources"] = new SortedDictionary<string, int>(outcomes, StringComparer.Ordinal);
            // Staged settlements agree with their compiler records: a village's binding (or MMR-33's explicit rebinding, with its reason), a
            // town's port.
            int rebound = 0;
            foreach (var v in Records(input.Campaign, "villages"))
            {
                string id = Text(v, "id"), to, why;
                var s = Get("Settlement." + id);
                if (s == null || Text(v, "bound") == null) continue;
                var c = Component(s);
                bool declared = input.Rebound.TryGetValue(id, out to) && input.ReboundReason.TryGetValue(id, out why) && !String.IsNullOrEmpty(why);
                string expected = declared ? to : Text(v, "bound");
                if (declared) rebound++;
                if (c != null && Ref(c, "bound") != "Settlement." + expected)
                    Add("graph.source_mismatch", "Settlement." + id, "Components/Village/@bound", "bound to " + Ref(c, "bound") + "; the compiler binds it to " + Text(v, "bound") + (declared ? " and MMR-33 rebinds it to " + to : " and no rebinding is declared"));
            }
            report.Census["villagesRebound"] = rebound;
            foreach (var t in Records(input.Campaign, "towns"))
            {
                var s = Get("Settlement." + Text(t, "id"));
                object port;
                if (s == null || !t.TryGetValue("port", out port) || !(port is bool)) continue;
                if ((bool)port != s.HasAttribute("port_posX")) Add("graph.source_mismatch", "Settlement." + Text(t, "id"), "@port_posX", (bool)port ? "the compiler port town is staged without a port" : "the compiler's landlocked town is staged with a port");
            }
            // Pending compiler slots: resolved by the compiler, or by a delivered owner (and then shown by the staged graph where it can be),
            // moot for a record with a disposition, or deferred to an owner not yet delivered.
            var delivered = new HashSet<string>(List(policy, "deliveredOwners").Cast<string>(), StringComparer.Ordinal);
            var deferred = Obj(policy, "deferredOwners") ?? new Dictionary<string, object>();
            var resolved = new HashSet<string>(input.Resolutions.Where(r => r != null).Select(r => r.Record + "|" + r.Slot), StringComparer.Ordinal);
            var slotCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var compilerClans = Records(input.Campaign, "clans").Where(c => Text(c, "id") != null).GroupBy(c => Text(c, "id")).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var list in new[] { "towns", "castles", "villages", "clans", "kingdoms", "heroes", "ports" })
                foreach (var r in Records(input.Campaign, list))
                {
                    string record = list == "ports" ? Text(r, "town") : Text(r, "id");
                    foreach (var slot in Records(r, "slots"))
                    {
                        string owner = Text(slot, "owner"), name = Text(slot, "name"), status = Text(slot, "status");
                        if (record == null || owner == null || name == null) { Add("graph.malformed_input", record, list + "/slots", "a compiler slot lacks its record, owner or name"); continue; }
                        string recordOutcome; outcomeById.TryGetValue(record, out recordOutcome);
                        string state;
                        if (status == "resolved") state = "resolved by the compiler";
                        else if (recordOutcome != null && recordOutcome != "staged" && recordOutcome != "runtime intent" && recordOutcome != "MISSING") state = "moot (" + recordOutcome.Split(':')[0] + ")";
                        else if (resolved.Contains(record + "|" + name))
                        {
                            state = "resolved by " + owner;
                            string problem = Unshown(list, record, name, compilerClans);
                            if (problem != null) Add("graph.pending_slot", record, name, "the " + owner + " slot " + name + " is claimed resolved but " + problem);
                        }
                        else if (deferred.ContainsKey(owner)) state = "deferred to " + owner;
                        else state = "PENDING";
                        if (state == "PENDING") Add("graph.pending_slot", record, name, "the " + owner + " slot " + name + " is still pending" + (delivered.Contains(owner) ? " although " + owner + " is delivered" : ""));
                        if (state.StartsWith("deferred", StringComparison.Ordinal))
                            report.DeferredSlots.Add(new SortedDictionary<string, object>(StringComparer.Ordinal) { ["record"] = record, ["slot"] = name, ["owner"] = owner, ["reason"] = Convert.ToString(deferred[owner], CultureInfo.InvariantCulture) });
                        string bucket = list + " " + name + " " + state.Split(' ')[0]; int n; slotCounts.TryGetValue(bucket, out n); slotCounts[bucket] = n + 1;
                    }
                }
            report.Census["slots"] = new SortedDictionary<string, int>(slotCounts, StringComparer.Ordinal);
        }

        // What the staged graph must show for a slot a factory claims to have resolved; null when it shows it (or holds nothing to show).
        private string Unshown(string list, string record, string slot, Dictionary<string, IDictionary<string, object>> compilerClans)
        {
            Func<string, string, bool> has = (key, attribute) => { var e = Get(key); return e != null && !String.IsNullOrEmpty(e.GetAttribute(attribute)); };
            Func<string, string, double> number = (key, attribute) => { var e = Get(key); double v; return e != null && Num(e, attribute, out v) ? v : double.NaN; };
            string settlement = "Settlement." + record, clan = "Faction." + record, kingdom = "Kingdom." + record;
            var component = Component(Get(settlement));
            switch (slot)
            {
                case "banner": return has(clan, "banner_key") ? null : "the staged clan has no banner_key";
                case "tier": return number(clan, "tier") >= 0 ? null : "the staged clan has no tier";
                case "vassalClans":
                {
                    IDictionary<string, object> source; string k = compilerClans.TryGetValue(record, out source) ? Text(source, "kingdom") : null;
                    string super = Get(clan) == null ? null : Ref(Get(clan), "super_faction");
                    if (k == null) return super == null ? null : "the independent compiler clan is staged as a vassal of " + super;
                    return super == "Kingdom." + k ? null : "the staged clan's kingdom is " + (super ?? "none") + ", not its compiler kingdom " + k;
                }
                case "bannerAndColors": return has(kingdom, "banner_key") && has(kingdom, "color") && has(kingdom, "color2") ? null : "the staged kingdom lacks its banner_key, color or color2";
                case "policies": return Get(kingdom) != null && Get(kingdom).SelectNodes("policies/policy").Count > 0 ? null : "the staged kingdom has no policy";
                case "prosperityAndEconomy": { double v; return component != null && Num(component, "prosperity", out v) && v > 0 ? null : "the staged town has no prosperity"; }
                case "villageType": return component != null && Ref(component, "village_type") != null ? null : "the staged village has no village_type";
                case "hearth": { double v; return component != null && Num(component, "hearth", out v) && v > 0 ? null : "the staged village has no hearth"; }
                case "prototypeAndScenes":
                {
                    if (input.MissionScenes == null) return "no mission scene catalogue was given to check its staged locations against";
                    var staged = Get(settlement);
                    string kind = component == null ? null : component.Name == "Town" ? (component.GetAttribute("is_castle") == "true" ? "castle" : "town") : component.Name == "Village" ? "village" : null;
                    if (staged == null || kind == null) return "the staged settlement has no town, castle or village component";
                    var locations = MNBL.ProceduralWorld.Missions.MissionScenes.LocationsOf(staged);
                    var failed = input.MissionScenes.LocationProblems(kind, staged.HasAttribute("port_posX"), locations);
                    if (failed.Count > 0) return "its staged locations fail the mission scene contracts: " + failed[0] + (failed.Count > 1 ? " (and " + (failed.Count - 1) + " more)" : "");
                    // The claim is bound to what is staged: the slot's reference record is the staged locations' own.
                    var claim = input.Resolutions.FirstOrDefault(r => r != null && r.Record == record && r.Slot == slot);
                    var value = claim == null || claim.Value == null ? null : new JavaScriptSerializer().DeserializeObject(claim.Value) as IDictionary<string, object>;
                    object claimed; string staged256 = (string)input.MissionScenes.ReferenceRecord(locations)["referencesSha256"];
                    return value != null && value.TryGetValue("referencesSha256", out claimed) && Equals(claimed, staged256) ? null
                        : "the claimed location references (" + (value != null && value.ContainsKey("referencesSha256") ? value["referencesSha256"] : "none") + ") are not the staged locations' (" + staged256 + ")";
                }
                case "navalFacility": return Get(settlement) != null && Get(settlement).HasAttribute("port_posX") && Get(settlement).HasAttribute("port_posY") ? null : "the staged town has no port position";
                case "shipyardAndFleet": return component != null && component.SelectSingleNode("Buildings/Building[@id='building_shipyard']") != null ? null : "the staged town has no building_shipyard";
                case "garrison":
                {
                    var culture = Find(Get(settlement) == null ? null : Ref(Get(settlement), "culture"));
                    return culture != null && Ref(culture, "default_party_template") != null ? null : "the staged settlement's culture has no default_party_template to fill its garrison from";
                }
                case "troopTreeAndRecruitTemplates":
                {
                    var culture = Find(Get(kingdom) == null ? null : Ref(Get(kingdom), "culture"));
                    return Get(kingdom) == null || (culture != null && Ref(culture, "basic_troop") != null && Ref(culture, "elite_basic_troop") != null) ? null : "the staged kingdom's culture has no basic and elite troop";
                }
                case "characterTemplate": case "ageTraitsAndFamily":
                {
                    if (Get("Hero." + record) != null)
                    {
                        var npc = Get("NPCCharacter." + record);
                        return npc == null ? "the staged hero has no character" : slot == "ageTraitsAndFamily" && !npc.HasAttribute("age") ? "the staged hero's character has no age" : null;
                    }
                    var intent = input.Plan == null ? null : input.Plan.Notables.Concat(input.Plan.Companions).FirstOrDefault(h => h.Id == record);
                    return intent == null || !String.IsNullOrEmpty(intent.Template) ? null : "the runtime intent has no template";
                }
            }
            return null;
        }

        private void Identity()
        {
            var lines = input.Policies.Select(p => p.Key + " " + p.Value).ToList();
            report.PolicyIdentity["policies"] = new SortedDictionary<string, string>(input.Policies, StringComparer.Ordinal);
            report.PolicyIdentity["sha256"] = Sha256(String.Join("\n", lines));
            // Every world names the graph policy, the catalog receipt and the live defaults; a compiler world also every resolving policy and template.
            var required = List(policy, "requiredInventory").Cast<string>().Concat(input.Campaign != null ? List(policy, "policies").Cast<string>() : Enumerable.Empty<string>());
            foreach (var r in required.Distinct())
                if (!input.Policies.ContainsKey(r)) Add("graph.policy_identity", r, null, "the resolving policy or inventory " + r + " is not in the snapshot");
        }

        private string Canonical()
        {
            report.Inputs["documents"] = documents.Select(d => d.Key + " " + Sha256(d.Value.OuterXml)).ToList();
            report.Inputs["defaultsSha256"] = InventorySha256(input.Defaults);
            var o = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["schema"] = "mnbl.graph-report", ["version"] = 2, ["worldPrefix"] = input.WorldPrefix, ["census"] = report.Census, ["outcomes"] = report.Outcomes,
                ["deferredSlots"] = report.DeferredSlots.Count, ["optionalAbsent"] = report.OptionalAbsent, ["reached"] = report.Reached, ["policyIdentity"] = report.PolicyIdentity["sha256"],
                ["inputs"] = report.Inputs,
                ["diagnostics"] = report.Diagnostics.Select(d => d.Code + "|" + d.SourceUid + "|" + d.GeneratedId + "|" + d.Path + "|" + d.Reason).ToList() };
            return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(o);
        }

        // Slot resolutions from the factories' results (MMR-31 from the faction intents, MMR-32/33/35 from their slot lists).
        public static List<SlotResolution> Resolutions(FactionFactory.Result factions, HeroFactory.Result heroes, SettlementFactory.Result settlements, NavalContentFactory.Result naval)
        {
            var list = new List<SlotResolution>();
            foreach (var c in factions.Clans)
            {
                if (!String.IsNullOrEmpty(c.BannerKey)) list.Add(new SlotResolution { Record = c.Id, Slot = "banner", Value = c.BannerKey });
                list.Add(new SlotResolution { Record = c.Id, Slot = "tier", Value = c.Tier.ToString(CultureInfo.InvariantCulture) });
                list.Add(new SlotResolution { Record = c.Id, Slot = "vassalClans", Value = c.KingdomId == null ? "independent" : "vassal of " + c.KingdomId + (c.Ruling ? " (ruling clan)" : "") });
            }
            foreach (var k in factions.Kingdoms)
            {
                if (!String.IsNullOrEmpty(k.BannerKey) && !String.IsNullOrEmpty(k.Color)) list.Add(new SlotResolution { Record = k.Id, Slot = "bannerAndColors", Value = k.BannerKey + " " + k.Color + "/" + k.Color2 });
                if (k.Policies.Count > 0) list.Add(new SlotResolution { Record = k.Id, Slot = "policies", Value = String.Join(",", k.Policies) });
            }
            if (heroes != null) list.AddRange(heroes.Slots.Select(s => new SlotResolution { Record = s.Owner, Slot = s.Name, Value = new JavaScriptSerializer().Serialize(s.Value) }));
            if (settlements != null) list.AddRange(settlements.Slots.Select(s => new SlotResolution { Record = s.Owner, Slot = s.Name, Value = new JavaScriptSerializer().Serialize(s.Value) }));
            if (naval != null) list.AddRange(naval.Slots.Select(s => new SlotResolution { Record = s.Town, Slot = s.Name, Value = s.Value }));
            return list;
        }

        // Dispositions from the factories: every compiler hero (MMR-32) and every dissolved realm and its clans (MMR-31). The slots of a record
        // with a disposition are moot.
        public static List<SourceDisposition> Dispositions(FactionFactory.Result factions, HeroFactory.Result heroes)
        {
            var list = new List<SourceDisposition>();
            if (heroes != null) list.AddRange(heroes.Dispositions.Select(d => new SourceDisposition { Uid = d.Uid, Id = d.Id, Outcome = d.Outcome, Reason = d.Reason }));
            foreach (var d in factions.Dissolved)
            {
                list.Add(new SourceDisposition { Uid = d.Uid, Id = d.Kingdom, Outcome = "dissolved", Reason = d.Reason });
                foreach (var clan in d.Clans) list.Add(new SourceDisposition { Id = clan, Outcome = "dissolved", Reason = "a clan of dissolved realm " + d.Kingdom });
            }
            return list;
        }

        private static bool Num(XmlElement e, string attribute, out double value)
        {
            value = 0; return e.HasAttribute(attribute) && Double.TryParse(e.GetAttribute(attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
        private static string Num(double x) { return x.ToString("0.###", CultureInfo.InvariantCulture); }
        private static string Sha256(string text) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false).GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
        private static IDictionary<string, object> Obj(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v as IDictionary<string, object> : null; }
        private static string Text(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v as string : null; }
        private static int Int(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 0; }
        private static double Real(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : double.NaN; }
        private static IEnumerable<object> List(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v is IEnumerable && !(v is string) ? ((IEnumerable)v).Cast<object>() : Enumerable.Empty<object>(); }
        private static IEnumerable<IDictionary<string, object>> Records(IDictionary<string, object> o, string key) { return List(o, key).OfType<IDictionary<string, object>>(); }
    }
}
