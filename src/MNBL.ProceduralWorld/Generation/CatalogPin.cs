using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using MNBL.ProceduralWorld.CampaignRegistry;

namespace MNBL.ProceduralWorld.Generation
{
    // MMR-283: the installed campaign catalog a generated world is checked against (docs/world-activation.md, "Content mods"). Pure managed:
    // the setup screen, the activation, the loader and the offline tests (tests/Generation/ContentCompatibilityTests.cs) share it.
    //   * The pin (worldgen/policies/campaign-catalog.json v2, written by tools/Make-CatalogPin.ps1 from the campaign_catalog capture) lists
    //     only what a world can reference: the installed objects reached from the stock categories, the reusable NPC templates, the engine's
    //     literal NPC lookups, the player's records and every object the world policies name, through the installed loaders' reference schema
    //     (never into Calradia's lords, kingdoms, clans or settlements unless a policy names them as prototypes). Each object by its key
    //     (Type.id), element and a fingerprint of the fields the world reads (Fields), never by whole content: balance patches and mods that edit
    //     stats, equipment, names or texts pass, and mods may add items, troops and cultures.
    //   * Check: every pinned object present, of the same element, with the same read fields. Closure: the live stock categories, templates
    //     and policy roots resolve through the pinned engine defaults (a mod's addition naming something not installed is found here, before
    //     generation, instead of inside the activation's build).
    //   * A world's catalog is frozen with it: the activation keeps the merged documents it built from in the world package (catalog/*.xml), and
    //     a load rebuilds from those, so later stat changes never change a rebuilt world. A load checks only the installed objects that world
    //     references (CheckReferences), and the stock objects added since (Added).
    // Every conflict names its object; Attribute names the module that provides or patches it (ModuleSources).
    internal static class CatalogPin
    {
        public const string Schema = "mnbl.campaign-catalog-pin";
        public const int Version = 2;
        public const string PolicyPath = "worldgen/policies/campaign-catalog.json";
        public static readonly string[] StockDocuments = { "SPCultures", "partyTemplates", "EquipmentRosters", "WorkshopTypes", "LocationComplexTemplates" };
        public static readonly string[] PlayerRoots = { "NPCCharacter.main_hero", "Hero.main_hero", "Faction.player_faction" };
        private static readonly HashSet<string> WorldTypes = new HashSet<string>(new[] { "Hero", "Faction", "Clan", "Kingdom", "Settlement", "Town", "Village", "MobileParty" }, StringComparer.Ordinal);
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };

        public const string MissingDocument = "missing-document", Missing = "missing", Retyped = "retyped", Changed = "changed", Unresolved = "unresolved", Added = "added", Scene = "scene", Battle = "battle";

        public sealed class Conflict
        {
            public string Kind, Document, Key, Element, Target, Module, Detail;
            public bool Official;   // the module is one of the game's own (an update, not a mod)
            public string Id { get { return Key == null ? null : Key.Substring(Key.IndexOf('.') + 1); } }
            public string Signature { get { return Kind + " " + Key + (Target == null ? "" : " -> " + Target) + (Target == null && Detail != null ? ": " + Detail : ""); } }
            public override string ToString() { return Kind + " " + Key + (Target == null ? "" : " -> " + Target) + (Module == null ? "" : " [" + Module + "]") + (Detail == null ? "" : ": " + Detail); }
        }

        public sealed class Entry { public string Key, Document, Element, Read; }

        public sealed class Pin
        {
            public string GameType, Sha256;
            public List<string> Documents = new List<string>(), Roots = new List<string>(), Defaults = new List<string>(), Tolerated = new List<string>();
            public Dictionary<string, Entry> Objects = new Dictionary<string, Entry>(StringComparer.Ordinal);

            public static Pin FromBytes(byte[] bytes)
            {
                var o = (IDictionary<string, object>)Json.DeserializeObject(new UTF8Encoding(false).GetString(bytes).TrimStart('﻿'));
                if ((string)o["schema"] != Schema || Convert.ToInt32(o["version"], CultureInfo.InvariantCulture) != Version) throw new InvalidDataException("Not an " + Schema + " version " + Version + " pin.");
                var pin = new Pin { GameType = (string)o["gameType"], Sha256 = Sha(bytes) };
                pin.Documents.AddRange(((IEnumerable)o["documents"]).Cast<object>().Select(x => (string)x));
                pin.Roots.AddRange(((IEnumerable)o["policyRoots"]).Cast<object>().Select(x => (string)x));
                pin.Defaults.AddRange(((IEnumerable)o["defaults"]).Cast<object>().Select(x => (string)x));
                pin.Tolerated.AddRange(((IEnumerable)o["tolerated"]).Cast<object>().Select(x => (string)x));
                foreach (var kv in (IDictionary<string, object>)o["objects"])
                {
                    var row = ((IEnumerable)kv.Value).Cast<object>().Select(x => (string)x).ToArray();   // [document, element, read]
                    pin.Objects[kv.Key] = new Entry { Key = kv.Key, Document = row[0], Element = row[1], Read = row[2] };
                }
                return pin;
            }
        }

        // ---------------------------------------------------------------- the catalog by key

        public sealed class Index
        {
            private readonly Dictionary<string, KeyValuePair<string, XmlElement>> objects = new Dictionary<string, KeyValuePair<string, XmlElement>>(StringComparer.Ordinal);
            public readonly IDictionary<string, XmlDocument> Documents;
            public readonly List<string> Duplicates = new List<string>();

            // Every record with an id of an element the reference schema knows, by Type.id (the first of a duplicate is kept and the duplicate
            // reported: merged documents carry none).
            public Index(IDictionary<string, XmlDocument> documents)
            {
                Documents = documents;
                foreach (var d in documents.OrderBy(d => d.Key, StringComparer.Ordinal))
                {
                    if (d.Value == null || d.Value.DocumentElement == null) continue;
                    foreach (var e in d.Value.DocumentElement.ChildNodes.OfType<XmlElement>())
                    {
                        string type = ReferenceSchema.ElementType(e.Name);
                        if (type == null || !e.HasAttribute("id")) continue;
                        string key = type + "." + e.GetAttribute("id");
                        if (objects.ContainsKey(key)) { Duplicates.Add(key); continue; }
                        objects[key] = new KeyValuePair<string, XmlElement>(d.Key, e);
                    }
                }
            }
            public bool Contains(string key) { return objects.ContainsKey(key); }
            public XmlElement Element(string key) { KeyValuePair<string, XmlElement> v; return objects.TryGetValue(key, out v) ? v.Value : null; }
            public string DocumentOf(string key) { KeyValuePair<string, XmlElement> v; return objects.TryGetValue(key, out v) ? v.Key : null; }
            public IEnumerable<string> Keys { get { return objects.Keys; } }
            public IEnumerable<string> KeysIn(string document) { return objects.Where(o => o.Value.Key == document).Select(o => o.Key); }
        }

        // ---------------------------------------------------------------- the fields the world reads

        // The canonical text of what the compiler and the engine read from an installed object: its element, the selection and validation
        // attributes the factories read, and the structural references a world builds on. Never numbers, names, texts or equipment choices.
        //   NPCCharacter  culture, occupation, is_template, is_hero, is_female, skill_template, upgrade_requires; upgrade targets; face key template
        //                 (the world re-emits its own copy of every template it uses, so stats, skills and equipment are free)
        //   Item          its element (Item or CraftedItem), Type, item_category, crafting_template; usable by women and men
        //   EquipmentRoster  culture; its flags; how many battle, civilian and stealth sets it has
        //   Settlement    a prototype's assets: culture, its component kind and meshes, location complex template, location scenes, common areas
        //   Faction       culture, the clan kind flags, default_party_template
        //   Culture       every reference and every true/false flag (name lists are texts)
        //   PartyTemplate its stack troops and ship hulls (not the counts)
        //   any other     every reference, with where it is read from
        public static string Fields(XmlElement e)
        {
            var lines = new List<string>();
            Action<string> attr = name => { if (e.HasAttribute(name)) lines.Add("@" + name + "=" + e.GetAttribute(name)); };
            Action<string, string> each = (xpath, name) => { foreach (var x in e.SelectNodes(xpath).OfType<XmlElement>()) lines.Add(xpath + "@" + name + "=" + x.GetAttribute(name)); };
            switch (ReferenceSchema.ElementType(e.Name))
            {
                case "NPCCharacter":
                    foreach (var a in new[] { "culture", "occupation", "is_template", "is_hero", "is_female", "skill_template", "upgrade_requires" }) attr(a);
                    each("upgrade_targets/upgrade_target", "id"); each("face/face_key_template", "value");
                    break;
                case "Item":
                    foreach (var a in new[] { "Type", "item_category", "crafting_template" }) attr(a);
                    each("Flags", "NotUsableByFemale"); each("Flags", "NotUsableByMale");
                    break;
                case "EquipmentRoster":
                    attr("culture");
                    foreach (var f in e.SelectNodes("Flags").OfType<XmlElement>()) foreach (XmlAttribute a in f.Attributes) lines.Add("Flags@" + a.Name + "=" + a.Value);
                    foreach (var g in e.ChildNodes.OfType<XmlElement>().Where(s => s.Name == "EquipmentSet").GroupBy(SetKind)) lines.Add("sets:" + g.Key + "=" + g.Count().ToString(CultureInfo.InvariantCulture));
                    break;
                case "Settlement":
                    attr("culture");
                    foreach (var c in e.SelectNodes("Components/*").OfType<XmlElement>())
                    {
                        lines.Add("component=" + c.Name);
                        foreach (var a in new[] { "is_castle", "background_mesh", "wait_mesh", "castle_background_mesh" }) if (c.HasAttribute(a)) lines.Add("component@" + a + "=" + c.GetAttribute(a));
                    }
                    each("Locations", "complex_template");
                    foreach (var l in e.SelectNodes("Locations/Location").OfType<XmlElement>())
                        foreach (XmlAttribute a in l.Attributes) if (a.Name == "id" || a.Name.StartsWith("scene_name", StringComparison.Ordinal)) lines.Add("location " + l.GetAttribute("id") + "@" + a.Name + "=" + a.Value);
                    each("CommonAreas/Area", "type");
                    break;
                case "Faction":
                    foreach (var a in new[] { "culture", "is_bandit", "is_minor_faction", "is_outlaw", "is_mafia", "is_nomad", "is_sect", "is_clan_type_mercenary", "default_party_template" }) attr(a);
                    break;
                case "Culture":
                    foreach (var r in ReferenceSchema.References(e, s => { })) lines.Add(r.Value + "=" + r.Key);
                    foreach (XmlAttribute a in e.Attributes) if (a.Value == "true" || a.Value == "false") lines.Add("@" + a.Name + "=" + a.Value);
                    break;
                case "PartyTemplate":
                    each("stacks/PartyTemplateStack", "troop"); each("ship_hulls/ShipTemplateStack", "id");
                    break;
                case "Hero":
                case "Kingdom":
                    break;
                default:
                    foreach (var r in ReferenceSchema.References(e, s => { })) lines.Add(r.Value + "=" + r.Key);
                    break;
            }
            lines.Sort(StringComparer.Ordinal);
            return e.Name + "\n" + String.Join("\n", lines);
        }

        public static string Read(XmlElement e) { return Sha(new UTF8Encoding(false).GetBytes(Fields(e))); }

        private static string SetKind(XmlElement s)
        {
            if (s.GetAttribute("civilian") == "true" || s.GetAttribute("equipmentType") == "Civilian") return "civilian";
            return s.GetAttribute("equipmentType") == "Stealth" ? "stealth" : "battle";
        }

        // ---------------------------------------------------------------- what a world can reference

        // The roots: every record of the stock categories (the game loads them before the world registers, so the world must close over
        // them), every reusable NPC template, the engine's literal NPC lookups, the player's records and the objects the world policies name.
        public static List<string> Roots(Index index, IEnumerable<string> policyRoots, IEnumerable<string> engineCharacterIds)
        {
            var roots = new List<string>();
            foreach (var document in StockDocuments) roots.AddRange(index.KeysIn(document).OrderBy(k => k, StringComparer.Ordinal));
            roots.AddRange(index.KeysIn("NPCCharacters").Where(k => index.Element(k).GetAttribute("is_template") == "true").OrderBy(k => k, StringComparer.Ordinal));
            roots.AddRange(engineCharacterIds.Select(id => "NPCCharacter." + id));
            roots.AddRange(PlayerRoots);
            roots.AddRange(policyRoots);
            return roots.Distinct().ToList();
        }

        // Everything the roots reach through the reference schema: optional references that are absent are skipped, engine defaults are not
        // catalog objects, world instances (lords, clans, kingdoms, settlements) are pinned only as roots and never followed into. A reference
        // the catalog cannot resolve, a world instance named by a reusable object, and a named lord reused as a template are the conflicts the
        // activation's build would refuse on (GeneratedCampaignDataset's closure rule), found here first.
        public static SortedSet<string> Reachable(Index index, IEnumerable<string> roots, ICollection<string> defaults, List<Conflict> found)
        {
            var problems = new List<Conflict>();
            var reached = new SortedSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            var cultureRoles = new HashSet<string>(index.KeysIn("SPCultures").SelectMany(k => index.Element(k).Attributes.Cast<XmlAttribute>()).Select(a => a.Value)
                .Where(v => v.StartsWith("NPCCharacter.", StringComparison.Ordinal)), StringComparer.Ordinal);
            foreach (var root in roots) if (index.Contains(root) && reached.Add(root)) queue.Enqueue(root);
            var player = new HashSet<string>(PlayerRoots, StringComparer.Ordinal);
            while (queue.Count > 0)
            {
                string key = queue.Dequeue(), type = key.Substring(0, key.IndexOf('.'));
                var e = index.Element(key);
                if (type == "NPCCharacter" && !player.Contains(key) && (index.Contains("Hero." + e.GetAttribute("id")) ||
                    (e.GetAttribute("is_hero") == "true" && e.GetAttribute("is_template") != "true" && !cultureRoles.Contains(key))))
                    problems.Add(new Conflict { Kind = Unresolved, Key = key, Document = index.DocumentOf(key), Element = e.Name, Detail = "a named hero reused as a template" });
                bool world = WorldTypes.Contains(type);
                var malformed = new List<string>();
                foreach (var raw in ReferenceSchema.Dependencies(e, m => malformed.Add(m)).ToList())
                {
                    string dep = raw; bool optional = false;
                    if (dep.StartsWith("?", StringComparison.Ordinal)) { dep = dep.Substring(dep.IndexOf('|') + 1); optional = true; }
                    if (defaults.Contains(dep)) continue;
                    string depType = dep.Substring(0, dep.IndexOf('.'));
                    if (WorldTypes.Contains(depType))
                    {
                        if (!world && !optional) problems.Add(new Conflict { Kind = Unresolved, Key = key, Document = index.DocumentOf(key), Element = e.Name, Target = dep, Detail = "names a world instance" });
                        continue;
                    }
                    if (!index.Contains(dep))
                    {
                        if (!optional && !world) problems.Add(new Conflict { Kind = Unresolved, Key = key, Document = index.DocumentOf(key), Element = e.Name, Target = dep, Detail = "names an object that is not installed" });
                        continue;
                    }
                    if (reached.Add(dep)) queue.Enqueue(dep);
                }
                if (!world) foreach (var m in malformed) problems.Add(new Conflict { Kind = Unresolved, Key = key, Document = index.DocumentOf(key), Element = e.Name, Detail = m });
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            found.AddRange(problems.Where(p => seen.Add(p.Signature)));   // one per object and target (a set may name an item twice)
            return reached;
        }

        // ---------------------------------------------------------------- the checks

        // The setup screen's and the activation's check of the live catalog against the pin: every pinned document readable, every pinned
        // object present, of its element, with its read fields; and the live roots closed over the pinned defaults.
        public static List<Conflict> Check(Pin pin, IDictionary<string, XmlDocument> live, IEnumerable<string> engineCharacterIds)
        {
            var conflicts = new List<Conflict>();
            foreach (var id in pin.Documents) if (!live.ContainsKey(id) || live[id] == null || live[id].DocumentElement == null) conflicts.Add(new Conflict { Kind = MissingDocument, Document = id, Key = "Document." + id });
            var index = new Index(live);
            foreach (var o in pin.Objects.Values.OrderBy(o => o.Key, StringComparer.Ordinal))
            {
                var e = index.Element(o.Key);
                if (e == null) { conflicts.Add(new Conflict { Kind = Missing, Key = o.Key, Document = o.Document, Element = o.Element }); continue; }
                if (e.Name != o.Element) { conflicts.Add(new Conflict { Kind = Retyped, Key = o.Key, Document = o.Document, Element = e.Name, Detail = "was " + o.Element + ", is " + e.Name }); continue; }
                if (Read(e) != o.Read) conflicts.Add(new Conflict { Kind = Changed, Key = o.Key, Document = o.Document, Element = e.Name, Detail = Short(Fields(e)) });
            }
            var defaults = new HashSet<string>(pin.Defaults, StringComparer.Ordinal);
            var closure = new List<Conflict>();
            Reachable(index, Roots(index, pin.Roots, engineCharacterIds), defaults, closure);
            var tolerated = new HashSet<string>(pin.Tolerated, StringComparer.Ordinal);
            conflicts.AddRange(closure.Where(c => !tolerated.Contains(c.Signature) && !conflicts.Any(x => x.Key == c.Key)));
            return conflicts;
        }

        // A load's check: every installed object the world references (keys; its own NPC records are the world's documents', and engine
        // defaults are checked by the registration) still present in the live catalog, of its element, with the read fields of the copy the
        // world was built from.
        public static List<Conflict> CheckReferences(Index frozen, Index live, IEnumerable<string> keys)
        {
            var conflicts = new List<Conflict>();
            foreach (var key in keys.Distinct().OrderBy(k => k, StringComparer.Ordinal))
            {
                var f = frozen.Element(key);
                if (f == null || key.StartsWith("NPCCharacter.", StringComparison.Ordinal)) continue;
                var e = live.Element(key);
                if (e == null) { conflicts.Add(new Conflict { Kind = Missing, Key = key, Document = frozen.DocumentOf(key), Element = f.Name }); continue; }
                if (e.Name != f.Name) { conflicts.Add(new Conflict { Kind = Retyped, Key = key, Document = frozen.DocumentOf(key), Element = e.Name, Detail = "was " + f.Name + ", is " + e.Name }); continue; }
                if (Read(e) != Read(f)) conflicts.Add(new Conflict { Kind = Changed, Key = key, Document = frozen.DocumentOf(key), Element = e.Name, Detail = Short(Fields(e)) });
            }
            return conflicts;
        }

        // A load's check of what was added since the world was made: a stock culture or party template the game now loads that the frozen
        // catalog lacks, naming an NPC the world does not supply (the world's NPCCharacters replace the installed ones), would be left with a
        // presumed troop.
        public static List<Conflict> AddedStock(Index frozen, Index live, ISet<string> suppliedCharacters)
        {
            var conflicts = new List<Conflict>();
            foreach (var document in new[] { "SPCultures", "partyTemplates" })
                foreach (var key in live.KeysIn(document).Where(k => !frozen.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
                {
                    var target = ReferenceSchema.Dependencies(live.Element(key), m => { }).Select(d => d.StartsWith("?", StringComparison.Ordinal) ? d.Substring(d.IndexOf('|') + 1) : d)
                        .FirstOrDefault(d => d.StartsWith("NPCCharacter.", StringComparison.Ordinal) && !suppliedCharacters.Contains(d));
                    if (target != null) conflicts.Add(new Conflict { Kind = Added, Key = key, Document = document, Element = live.Element(key).Name, Target = target, Detail = "added after the campaign was created" });
                }
            return conflicts;
        }

        // Names the module of every conflict that has none: the last active module that defines or patches the object (its XML or XSLT for the
        // object's document), else nobody (Module null).
        public static void Attribute(IEnumerable<Conflict> conflicts, ModuleSources modules)
        {
            if (modules == null) return;
            foreach (var c in conflicts.Where(c => c.Module == null))
            {
                ModuleSources.Module m = null;
                if (c.Kind == Scene) m = modules.SceneProvider(c.Id);
                else if (c.Document != null && c.Key != null && c.Kind != MissingDocument) m = modules.Provider(c.Document, c.Id);
                if (m != null) { c.Module = m.Id; c.Official = m.Official; }
            }
        }

        // ---------------------------------------------------------------- the frozen catalog

        // A world's catalog identity: its documents' hashes, by id.
        public static string Identity(IDictionary<string, byte[]> documents)
        {
            return Sha(new UTF8Encoding(false).GetBytes(String.Join("\n", documents.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => d.Key + " " + Sha(d.Value)))));
        }

        public static Dictionary<string, XmlDocument> Parse(IDictionary<string, byte[]> documents)
        {
            var parsed = new Dictionary<string, XmlDocument>(StringComparer.Ordinal);
            foreach (var d in documents)
            {
                var doc = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(new MemoryStream(d.Value), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) doc.Load(reader);
                parsed[d.Key] = doc;
            }
            return parsed;
        }

        // The installed objects a built world references: its closure (CompiledWorldDataset.Templates), less the engine defaults.
        public static List<string> WorldReferences(IEnumerable<string> closure, ICollection<string> defaults)
        {
            return closure.Select(k => k.StartsWith("?", StringComparison.Ordinal) ? k.Substring(k.IndexOf('|') + 1) : k).Where(k => !defaults.Contains(k)).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
        }

        // ---------------------------------------------------------------- the pin, made

        // The pin's JSON (tools/Make-CatalogPin.ps1): the reachable objects of the captured catalog with their read fingerprints, and the
        // closure problems the captured catalog itself has (tolerated: templates the factories leave out of their pools, such as an installed
        // template naming an equipment roster that does not exist), which the live check does not report again.
        public static string Make(IDictionary<string, XmlDocument> captured, string gameType, IEnumerable<string> policyRoots, IEnumerable<string> engineCharacterIds, IList<string> defaults,
            IDictionary<string, object> provenance, List<Conflict> problems)
        {
            var index = new Index(captured);
            var roots = policyRoots.Where(index.Contains).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
            var reached = Reachable(index, Roots(index, roots, engineCharacterIds), new HashSet<string>(defaults, StringComparer.Ordinal), problems);
            var text = new StringBuilder();
            text.Append("{\n \"schema\": ").Append(Json.Serialize(Schema)).Append(",\n \"version\": ").Append(Version).Append(",\n \"ticket\": \"MMR-283\",\n \"gameType\": ").Append(Json.Serialize(gameType));
            text.Append(",\n \"rule\": ").Append(Json.Serialize("A world is built from the live merged documents and refused only when an object it can reference (below: [document, element, read fields]) is missing, of another element, or changed in the fields the world reads (CatalogPin.Fields); the live stock categories, templates and policy roots must resolve through the pinned engine defaults."));
            text.Append(",\n \"documents\": ").Append(Json.Serialize(captured.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList()));
            text.Append(",\n \"policyRoots\": ").Append(Json.Serialize(roots));
            text.Append(",\n \"defaults\": ").Append(Json.Serialize(defaults.OrderBy(k => k, StringComparer.Ordinal).ToList()));
            text.Append(",\n \"tolerated\": ").Append(Json.Serialize(problems.Select(p => p.Signature).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList()));
            text.Append(",\n \"provenance\": ").Append(Json.Serialize(provenance));
            text.Append(",\n \"objects\": {");
            bool first = true;
            foreach (var key in reached)
            {
                var e = index.Element(key);
                text.Append(first ? "\n  " : ",\n  ").Append(Json.Serialize(key)).Append(": ").Append(Json.Serialize(new[] { index.DocumentOf(key), e.Name, Read(e) }));
                first = false;
            }
            text.Append("\n }\n}\n");
            return text.ToString();
        }

        private static string Short(string text) { text = text.Replace("\n", "; "); return text.Length <= 300 ? text : text.Substring(0, 300) + "..."; }
        public static string Sha(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    }
}
