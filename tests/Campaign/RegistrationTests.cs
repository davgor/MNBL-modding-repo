using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;

// MMR-30 registry tests, without the game: RegistrationTests BUNDLE_DIR SAVE_DEFINER_SCAN
// The object manager is modelled independently of the registry (EngineModel below), from installed IL and the in-game runs:
//   LoadXml deserializes records in document order; a reference to an object not loaded yet creates a presumed, not-ready
//   object (two-pass). Village.Deserialize links the village to its town only if the bound settlement is already a town
//   (no exception: an early village silently loses the link). Hero.Deserialize reads its CharacterObject without a null
//   check (an exception). After initialization Hero, Faction, Kingdom and MobileParty leave the object manager for the
//   campaign lists; settlements stay (run 416). During a load, BeforeLoad re-registers the saved permanent objects not ready,
//   the saved clans, heroes and kingdoms are in the campaign lists (some heroes and kingdoms also in the object manager), all
//   not ready until the load finishes (runs 3428, 33092); UnregisterNonReadyObjects drops what is not ready.
// BUNDLE_DIR is the real MMR-29 bundle, read through WorldDataBundle, whose compiler records are planned too.
internal static class RegistrationTests
{
    private static int checks, failures;
    private const string A = "mnbl_0000002a_", B = "mnbl_0000002b_";
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; Console.WriteLine((condition ? "ok    " : "FAIL  ") + label); }
    private static bool Has(IEnumerable<string> problems, string part) { return problems.Any(p => p.Contains(part)); }
    private static string Refusal(Action action)
    {
        try { action(); } catch (RegistrationException e) { return e.Message; }
        return null;
    }
    private static List<KeyValuePair<string, bool>> Entries(IEnumerable<string> ids, bool ready) { return ids.Select(i => new KeyValuePair<string, bool>(i, ready)).ToList(); }

    // ---- an independent model of MBObjectManager and the campaign lists ----
    private sealed class EngineModel : IObjectStore
    {
        private static readonly Regex Typed = new Regex(@"^([A-Z][A-Za-z]*)\.([A-Za-z0-9_-]+)$");
        private static readonly string[] Temporary = { "Hero", "Kingdom", "Faction", "MobileParty" };
        private readonly Dictionary<string, Dictionary<string, bool>> objects = new Dictionary<string, Dictionary<string, bool>>(StringComparer.Ordinal);
        private readonly HashSet<string> removedTypes = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> towns = new HashSet<string>(StringComparer.Ordinal);                         // settlements with a Town component, not a castle
        private readonly HashSet<string> castles = new HashSet<string>(StringComparer.Ordinal);                       // settlements with a castle Town component
        public readonly Dictionary<string, string> TradeBoundTo = new Dictionary<string, string>(StringComparer.Ordinal);  // village settlement -> town
        public StoreStage Stage { get; set; }
        public List<string> Loaded = new List<string>();
        public bool HasType(string type) { return !removedTypes.Contains(type); }
        public void Add(string type, string id, bool ready = true)
        {
            Dictionary<string, bool> map;
            if (!objects.TryGetValue(type, out map)) objects[type] = map = new Dictionary<string, bool>(StringComparer.Ordinal);
            map[id] = ready || (map.ContainsKey(id) && map[id]);
        }
        public bool Contains(string type, string id) { Dictionary<string, bool> m; return HasType(type) && objects.TryGetValue(type, out m) && m.ContainsKey(id); }
        public bool IsReady(string type, string id) { return Contains(type, id) && objects[type][id]; }
        public IEnumerable<KeyValuePair<string, bool>> Objects(string type) { Dictionary<string, bool> m; return HasType(type) && objects.TryGetValue(type, out m) ? m.ToList() : new List<KeyValuePair<string, bool>>(); }
        public bool Unregister(string type, string id) { Dictionary<string, bool> m; return objects.TryGetValue(type, out m) && m.Remove(id); }
        // Settlement.IsTown excludes castles: a castle's village is not trade-bound at deserialization (null, not applicable).
        public bool? TradeBound(string village, string bound) { string t; return !Contains("Settlement", village) || castles.Contains(bound) ? (bool?)null : TradeBoundTo.TryGetValue(village, out t) && t == bound; }
        public string ThrowOnPhase;   // MMR-36: a load failure in the middle of registration
        public void LoadDocument(string phase, XmlDocument document)
        {
            if (phase == ThrowOnPhase) throw new InvalidOperationException("model load failure at " + phase);
            Loaded.Add(phase);
            foreach (var e in document.DocumentElement.ChildNodes.OfType<XmlElement>().Where(e => e.HasAttribute("id"))) Deserialize(e, null);
        }
        private void Deserialize(XmlElement e, string settlement)
        {
            // Settlement.Deserialize creates a CustomSettlementComponent as the type its component_name names (installed IL, MMR-34).
            string type = e.Name == "CustomSettlementComponent" && e.HasAttribute("component_name") ? e.GetAttribute("component_name") : e.Name, id = e.GetAttribute("id");
            if (!Contains(type, id)) Add(type, id, false);   // GetPresumedObject
            if (type == "Hero" && !IsReady("NPCCharacter", id)) throw new NullReferenceException("Hero.Deserialize read the CharacterObject " + id + ", which is not registered");
            foreach (XmlAttribute a in e.Attributes)
            {
                var m = Typed.Match(a.Value);
                if (a.Name == "id" || !m.Success) continue;
                if (!Contains(m.Groups[1].Value, m.Groups[2].Value)) Add(m.Groups[1].Value, m.Groups[2].Value, false);
                // Village.Deserialize: only a bound settlement that already is a town gets the village as trade-bound.
                if (type == "Village" && a.Name == "bound" && towns.Contains(m.Groups[2].Value)) TradeBoundTo[settlement] = m.Groups[2].Value;
            }
            foreach (var components in e.ChildNodes.OfType<XmlElement>().Where(c => c.Name == "Components"))
                foreach (var c in components.ChildNodes.OfType<XmlElement>().Where(c => c.HasAttribute("id")))
                {
                    Deserialize(c, id);
                    if (c.Name == "Town") { if (c.GetAttribute("is_castle") == "true") castles.Add(id); else towns.Add(id); }
                }
            Add(type, id, true);
        }
        // After initialization (new game) or a finished load: the temporary types move to the campaign lists.
        public Dictionary<string, IList<KeyValuePair<string, bool>>> RemoveTemporaryTypes()
        {
            var lists = new Dictionary<string, IList<KeyValuePair<string, bool>>> {
                ["Settlements"] = Objects("Settlement").ToList(), ["Clans"] = Objects("Faction").ToList(), ["Kingdoms"] = Objects("Kingdom").ToList(),
                ["AliveHeroes"] = Objects("Hero").ToList(), ["DeadOrDisabledHeroes"] = new List<KeyValuePair<string, bool>>(), ["MobileParties"] = Objects("MobileParty").ToList() };
            foreach (var type in Temporary) { objects.Remove(type); removedTypes.Add(type); }
            return lists;
        }
        public Dictionary<string, IList<KeyValuePair<string, bool>>> BeforeLoad(IEnumerable<KeyValuePair<string, string>> saved)
        {
            var list = new Dictionary<string, string> { ["Faction"] = "Clans", ["Hero"] = "AliveHeroes", ["Kingdom"] = "Kingdoms", ["MobileParty"] = "MobileParties", ["Settlement"] = "Settlements" };
            var lists = list.Values.Distinct().Concat(new[] { "DeadOrDisabledHeroes" }).ToDictionary(n => n, n => (IList<KeyValuePair<string, bool>>)new List<KeyValuePair<string, bool>>());
            foreach (var s in saved)
            {
                if (list.ContainsKey(s.Key)) lists[list[s.Key]].Add(new KeyValuePair<string, bool>(s.Value, false));
                if (!Temporary.Contains(s.Key)) Add(s.Key, s.Value, false);
            }
            // Run 33092: the object manager also held a saved hero and a saved kingdom of the world, not ready.
            foreach (var type in new[] { "Hero", "Kingdom" })
            {
                var instance = saved.LastOrDefault(s => s.Key == type);
                if (instance.Value != null) Add(type, instance.Value, false);
            }
            Stage = StoreStage.SavedObjectsRegistered;
            return lists;
        }
        public void UnregisterNonReadyObjects()
        {
            foreach (var m in objects.Values) foreach (var id in m.Where(kv => !kv.Value).Select(kv => kv.Key).ToList()) m.Remove(id);
            Stage = StoreStage.NonReadyUnregistered;
        }
        // OnLoadFinished: the saved temporary objects are ready, in their lists.
        public static void FinishLoad(Dictionary<string, IList<KeyValuePair<string, bool>>> lists)
        {
            foreach (var name in lists.Keys.ToList()) lists[name] = lists[name].Select(e => new KeyValuePair<string, bool>(e.Key, true)).ToList();
        }
    }

    private sealed class FakeLists : ICampaignLists
    {
        public Dictionary<string, IList<KeyValuePair<string, bool>>> Values = new Dictionary<string, IList<KeyValuePair<string, bool>>>();
        public IEnumerable<KeyValuePair<string, IList<KeyValuePair<string, bool>>>> Lists() { return Values; }
    }

    // Shared templates an earlier module loaded, in the given order.
    private static EngineModel Engine(bool reversed = false)
    {
        var engine = new EngineModel();
        var officials = new[] { "Culture.empire", "Culture.sturgia", "NPCCharacter.lord_template", "NPCCharacter.main_hero", "WorkshopType.smithy",
                                "LocationComplexTemplate.town_complex", "LocationComplexTemplate.village_complex", "Item.sword", "Skill.OneHanded" };
        foreach (var o in reversed ? officials.Reverse() : officials) engine.Add(o.Split('.')[0], o.Split('.')[1]);
        return engine;
    }
    private static Func<string, string, bool> SharedOf(EngineModel engine) { return (t, i) => !RegistrationPlan.IsWorldType(t) && engine.IsReady(t, i); }

    private static XmlDocument Xml(string text) { var d = new XmlDocument { XmlResolver = null }; d.LoadXml(text); return d; }

    // The documents of a two-town world shaped like GeneratedCampaignDataset's (MMR-18 fixture).
    private static List<KeyValuePair<string, XmlDocument>> World(string p, Func<string, string> edit = null)
    {
        Func<string, string> e = edit ?? (s => s);
        var docs = new Dictionary<string, string> {
            ["SPCultures"] = "<SPCultures><Culture id='empire' start_point_position_x='10'/></SPCultures>",
            ["NPCCharacters"] = "<NPCCharacters><NPCCharacter id='lord_template' is_template='true' culture='Culture.empire'/>" +
                "<NPCCharacter id='" + p + "ruler_1' is_hero='true' culture='Culture.empire'/><NPCCharacter id='" + p + "ruler_2' is_hero='true' culture='Culture.empire'/>" +
                "<NPCCharacter id='main_hero' is_hero='true' culture='Culture.empire'/></NPCCharacters>",
            ["Heroes"] = "<Heroes><Hero id='" + p + "ruler_1' faction='Faction." + p + "clan_1'/><Hero id='" + p + "ruler_2' faction='Faction." + p + "clan_2'/>" +
                "<Hero id='main_hero' faction='Faction.player_faction'/></Heroes>",
            ["Kingdoms"] = "<Kingdoms><Kingdom id='" + p + "kingdom_1' owner='Hero." + p + "ruler_1' initial_home_settlement='Settlement." + p + "town_1' culture='Culture.empire'/>" +
                "<Kingdom id='" + p + "kingdom_2' owner='Hero." + p + "ruler_2' initial_home_settlement='Settlement." + p + "town_2' culture='Culture.empire'/></Kingdoms>",
            ["Factions"] = "<Factions><Faction id='" + p + "clan_1' owner='Hero." + p + "ruler_1' super_faction='Kingdom." + p + "kingdom_1' initial_home_settlement='Settlement." + p + "town_1' culture='Culture.empire'/>" +
                "<Faction id='" + p + "clan_2' owner='Hero." + p + "ruler_2' super_faction='Kingdom." + p + "kingdom_2' initial_home_settlement='Settlement." + p + "town_2' culture='Culture.empire'/>" +
                "<Faction id='player_faction' owner='Hero.main_hero' initial_home_settlement='Settlement." + p + "town_1' culture='Culture.empire'/></Factions>",
            ["WorkshopTypes"] = "<WorkshopTypes><WorkshopType id='smithy'/></WorkshopTypes>",
            ["LocationComplexTemplates"] = "<LocationComplexTemplates><LocationComplexTemplate id='town_complex'/><LocationComplexTemplate id='village_complex'/></LocationComplexTemplates>",
            ["Settlements"] = "<Settlements>" + Town(p, 1) + Town(p, 2) + Village(p, 1) + Village(p, 2) + "</Settlements>"
        };
        return RegistrationPlan.NewGameOrder.Select(id => new KeyValuePair<string, XmlDocument>(id, Xml(e(docs[id])))).ToList();
    }
    private static string Town(string p, int i) { return "<Settlement id='" + p + "town_" + i + "' owner='Faction." + p + "clan_" + i + "' culture='Culture.empire'><Components><Town id='" + p + "town_" + i + "_component'/></Components><Locations complex_template='LocationComplexTemplate.town_complex'/></Settlement>"; }
    private static string Village(string p, int i, string bound = null) { return "<Settlement id='" + p + "village_" + i + "' culture='Culture.empire'><Components><Village id='" + p + "village_" + i + "_component' bound='Settlement." + (bound ?? p + "town_" + i) + "'/></Components></Settlement>"; }
    // The dataset's declared generated objects (GeneratedCampaignDataset.Instances plus the aliases).
    private static List<string> Declared(string p)
    {
        var keys = new List<string> { "NPCCharacter.main_hero", "Hero.main_hero", "Faction.player_faction", "MobileParty.player_party" };
        foreach (var i in new[] { 1, 2 })
            keys.AddRange(new[] { "NPCCharacter." + p + "ruler_" + i, "Hero." + p + "ruler_" + i, "Faction." + p + "clan_" + i, "Kingdom." + p + "kingdom_" + i,
                                  "Settlement." + p + "town_" + i, "Town." + p + "town_" + i + "_component", "Settlement." + p + "village_" + i, "Village." + p + "village_" + i + "_component" });
        return keys;
    }
    private static List<KeyValuePair<string, XmlDocument>> Saved(List<KeyValuePair<string, XmlDocument>> docs) { return docs.Where(d => RegistrationPlan.SavedGameOrder.Contains(d.Key)).ToList(); }
    private static CampaignObjectRegistry Registry(string p, List<KeyValuePair<string, XmlDocument>> docs, EngineModel engine, bool saved = false, IEnumerable<string> declared = null, string instance = null)
    {
        return new CampaignObjectRegistry(RegistrationPlan.FromDocuments(p, docs, saved, saved ? null : (declared ?? Declared(p))), SharedOf(engine), instance);
    }
    private static string Json(object o) { return new JavaScriptSerializer().Serialize(o); }
    private static IDictionary Row(IDictionary<string, object> census, string section, string key) { return (IDictionary)((IDictionary)census[section])[key]; }
    private static List<string> Closure(IDictionary<string, object> census) { return (List<string>)census["closure"]; }

    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("usage: RegistrationTests BUNDLE_DIR SAVE_DEFINER_SCAN"); return 2; }
        try { Ordering(); Duplicates(); CensusAndClosure(); TwoWorlds(); Replay(); Identity(args[1]); CompilerRecords(args[0]); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    // c1: early references and order.
    private static void Ordering()
    {
        var engine = Engine();
        var registry = Registry(A, World(A), engine);
        registry.Register(engine, World(A));
        Check(registry.Registered && engine.Loaded.SequenceEqual(RegistrationPlan.NewGameOrder) && registry.ClosureProblems(engine, null, ClosureMode.Registration).Count == 0 &&
              engine.TradeBound(A + "village_1", A + "town_1") == true, "a world in the proven order registers phase by phase, closes, and every village is trade-bound to its town");
        Check(engine.IsReady("Faction", A + "clan_1") && engine.IsReady("Settlement", A + "town_1"),
              "the proven two-pass forward references (Hero.faction, Kingdom/Faction.initial_home_settlement) become ready in the same registration");
        // MMR-33: a castle and a village bound to it. Village.Deserialize trade-binds only to a town (Settlement.IsTown excludes castles),
        // so registration closure must not ask a castle's village for a trade bound (run 35960 was refused for it).
        Func<string, string> withCastle = s => s.StartsWith("<Settlements>") ? s.Replace("</Settlements>",
            "<Settlement id='" + A + "castle_1' owner='Faction." + A + "clan_1' culture='Culture.empire'><Components><Town id='" + A + "castle_1_component' is_castle='true'/></Components><Locations complex_template='LocationComplexTemplate.town_complex'/></Settlement>" +
            Village(A, 3, A + "castle_1") + "</Settlements>") : s;
        var castled = Engine();
        var castleRegistry = Registry(A, World(A, withCastle), castled, false, Declared(A).Concat(new[] { "Settlement." + A + "castle_1", "Town." + A + "castle_1_component", "Settlement." + A + "village_3", "Village." + A + "village_3_component" }));
        string castleRefusal = Refusal(() => castleRegistry.Register(castled, World(A, withCastle)));
        Check(castleRefusal == null && castleRegistry.ClosureProblems(castled, null, ClosureMode.Registration).Count == 0 && castled.TradeBound(A + "village_3", A + "castle_1") == null &&
              castled.TradeBound(A + "village_1", A + "town_1") == true,
              "a village bound to a castle registers and closes without a trade bound (the engine trade-binds only to towns), while town villages stay trade-bound");

        // A village before its bound town: the engine raises nothing and silently drops the trade-bound link.
        Func<string, string> villageFirst = s => s.StartsWith("<Settlements>") ? "<Settlements>" + Village(A, 1) + Town(A, 1) + Town(A, 2) + Village(A, 2) + "</Settlements>" : s;
        var raw = Engine();
        foreach (var d in World(A, villageFirst)) raw.LoadDocument(d.Key, d.Value);
        var lostLink = raw.TradeBound(A + "village_1", A + "town_1") == false;
        var rawClosure = Registry(A, World(A, villageFirst), Engine()).ClosureProblems(raw, null, ClosureMode.Registration);
        var guarded = Engine();
        var refusal = Refusal(() => Registry(A, World(A, villageFirst), guarded).Register(guarded, World(A, villageFirst)));
        Check(lostLink && Has(rawClosure, "village Settlement." + A + "village_1 is not trade-bound to Settlement." + A + "town_1") &&
              refusal != null && refusal.Contains("early reference Village." + A + "village_1_component.bound -> Settlement." + A + "town_1") && guarded.Loaded.Count == 0,
              "a village before its bound town (silently not trade-bound in the engine, which closure also detects) is refused before any document loads");
        Func<string, string> selfEdit = s => s.Replace(Village(A, 2), Village(A, 2, A + "village_2"));
        Func<string, string> toVillageEdit = s => s.Replace(Village(A, 2), Village(A, 2, A + "village_1"));
        var self = Engine();
        var selfBound = Refusal(() => Registry(A, World(A, selfEdit), self).Register(self, World(A, selfEdit)));
        var toVillage = Engine();
        var villageBound = Refusal(() => Registry(A, World(A, toVillageEdit), toVillage).Register(toVillage, World(A, toVillageEdit)));
        Check(selfBound != null && selfBound.Contains("it must be deserialized before Settlement." + A + "village_2") &&
              villageBound != null && villageBound.Contains("is not a town or castle"), "a village bound to its own settlement or to another village is refused");

        // A hero without its character: the engine throws inside LoadXml(Heroes); the registry refuses first.
        Func<string, string> noCharacter = s => s.Replace("<NPCCharacter id='" + A + "ruler_2' is_hero='true' culture='Culture.empire'/>", "");
        var heroless = Engine();
        bool engineThrows = false;
        try { foreach (var d in World(A, noCharacter)) heroless.LoadDocument(d.Key, d.Value); } catch (NullReferenceException) { engineThrows = true; }
        var hc = Engine();
        var heroRefusal = Refusal(() => Registry(A, World(A, noCharacter), hc, false, Declared(A).Where(key => key != "NPCCharacter." + A + "ruler_2")).Register(hc, World(A, noCharacter)));
        Check(engineThrows && heroRefusal != null && heroRefusal.Contains("hero Hero." + A + "ruler_2 has no NPCCharacter") && hc.Loaded.Count == 0,
              "a hero without its NPCCharacter (Hero.Deserialize throws) is refused before any document loads");

        // A forward reference outside the proven two-pass pattern.
        Func<string, string> capital = s => s.Replace("<Kingdom id='" + A + "kingdom_1'", "<Kingdom capital='Settlement." + A + "town_1' id='" + A + "kingdom_1'");
        var c = Engine();
        var forward = Refusal(() => Registry(A, World(A, capital), c).Register(c, World(A, capital)));
        Check(forward != null && forward.Contains("early reference Kingdom." + A + "kingdom_1.capital -> Settlement." + A + "town_1: a forward reference outside the proven two-pass pattern"),
              "a forward reference outside the two-pass pattern is an early reference");
        // Documents out of the proven phase order.
        var order = World(A); var k = order[3]; order[3] = order[2]; order[2] = k;   // Kingdoms before Heroes
        var o = Engine();
        var phases = Refusal(() => Registry(A, order, o).Register(o, order));
        Check(phases != null && phases.Contains("phase order is") && phases.Contains("early reference Kingdom." + A + "kingdom_1.owner -> Hero." + A + "ruler_1") && o.Loaded.Count == 0,
              "documents out of the proven phase order are refused, naming the early references they cause");
        Func<string, string> missing = s => s.Replace("faction='Faction." + A + "clan_2'", "faction='Faction." + A + "clan_9'");
        var mi = Engine();
        var unresolved = Refusal(() => Registry(A, World(A, missing), mi).Register(mi, World(A, missing)));
        Check(unresolved != null && unresolved.Contains("unresolved world reference Hero." + A + "ruler_2.faction -> Faction." + A + "clan_9"), "a reference to a world object nothing registers is refused");
    }

    // c1: duplicate identities and identity rules.
    private static void Duplicates()
    {
        Func<Func<string, string>, EngineModel, IEnumerable<string>, string> refuse = (edit, engine, declared) =>
            Refusal(() => Registry(A, World(A, edit), engine, false, declared).Register(engine, World(A, edit)));
        var twice = refuse(s => s.Replace(Village(A, 2), Village(A, 2) + Village(A, 2)), Engine(), null);
        Check(twice != null && twice.Contains("duplicate identity Settlement." + A + "village_2"), "the same object twice is a duplicate identity");
        var crossType = refuse(s => s.Replace("<Faction id='" + A + "clan_2'", "<Faction id='" + A + "kingdom_2'").Replace("Faction." + A + "clan_2", "Faction." + A + "kingdom_2"), Engine(),
                               Declared(A).Select(x => x.Replace("Faction." + A + "clan_2", "Faction." + A + "kingdom_2")));
        Check(crossType != null && crossType.Contains("duplicate identity " + A + "kingdom_2 used by Kingdom and Faction"), "one id used by two world types is a duplicate identity");
        var registered = Engine(); registered.Add("Settlement", A + "town_2");
        var collision = refuse(null, registered, null);
        Check(collision != null && collision.Contains("identity Settlement." + A + "town_2 collides with an object already registered"), "a generated id already registered collides");
        var unqualified = refuse(s => s.Replace(A + "village_2", "village_2"), Engine(), Declared(A).Select(x => x.Replace(A + "village_2", "village_2")));
        Check(unqualified != null && unqualified.Contains("identity Settlement.village_2: not world-qualified"), "a generated id without the world prefix is refused");
        var foreign = refuse(s => s.Replace(A + "village_2", B + "village_2"), Engine(), Declared(A).Select(x => x.Replace(A + "village_2", B + "village_2")));
        Check(foreign != null && foreign.Contains("belongs to world mnbl_0000002b, not mnbl_0000002a"), "an id of another world is refused");
        var longId = refuse(s => s.Replace(A + "village_2", A + new string('v', 40)), Engine(), Declared(A).Select(x => x.Replace(A + "village_2", A + new string('v', 40))));
        Check(longId != null && longId.Contains("longer than " + CampaignIdentity.MaxIdLength), "an id longer than the documented limit is refused");
        // Generated records of non-world types, known from the producer's declared list.
        Func<string, string> culture = s => s.StartsWith("<SPCultures>") ? "<SPCultures><Culture id='empire' start_point_position_x='10'/><Culture id='GeneratedCulture'/></SPCultures>" : s;
        var unprefixedCulture = refuse(culture, Engine(), Declared(A).Concat(new[] { "Culture.GeneratedCulture" }));
        Check(unprefixedCulture != null && unprefixedCulture.Contains("identity Culture.GeneratedCulture: not lowercase"), "a declared generated culture without the world prefix is refused");
        Func<string, string> upper = s => s.Replace("<NPCCharacter id='lord_template'", "<NPCCharacter id='" + A + "Ruler_X' culture='Culture.empire'/><NPCCharacter id='lord_template'");
        var uppercase = refuse(upper, Engine(), Declared(A).Concat(new[] { "NPCCharacter." + A + "Ruler_X" }));
        Check(uppercase != null && uppercase.Contains("identity NPCCharacter." + A + "Ruler_X: not lowercase"), "a declared generated character with an uppercase id is refused");
        Func<string, string> reuse = s => s.StartsWith("<SPCultures>") ? "<SPCultures><Culture id='empire' start_point_position_x='10'/><Culture id='sturgia'/></SPCultures>" : s;
        var reused = refuse(reuse, Engine(), Declared(A).Concat(new[] { "Culture.sturgia" }));
        Check(reused != null && reused.Contains("identity Culture.sturgia"), "a declared generated culture reusing an official id is refused, not merged");
        var undeclared = refuse(null, Engine(), Declared(A).Concat(new[] { "Settlement." + A + "town_9" }));
        Check(undeclared != null && undeclared.Contains("declared generated object Settlement." + A + "town_9 is in no document"), "a declared generated object missing from the documents is refused");
        var otherTemplate = Engine(); otherTemplate.Add("Culture", B + "culture");
        var crossWorld = refuse(s => s.Replace("<NPCCharacter id='" + A + "ruler_2' is_hero='true' culture='Culture.empire'/>", "<NPCCharacter id='" + A + "ruler_2' is_hero='true' culture='Culture." + B + "culture'/>"), otherTemplate, null);
        Check(crossWorld != null && crossWorld.Contains("reference to another world NPCCharacter." + A + "ruler_2.culture -> Culture." + B + "culture"), "a reference to another world's object is refused even when it is registered");
        var official = Engine(); official.Add("Faction", "official_clan");
        var dependency = refuse(s => s.Replace("owner='Faction." + A + "clan_2'", "owner='Faction.official_clan'"), official, null);
        Check(dependency != null && dependency.Contains("official world-instance dependency Settlement." + A + "town_2.owner -> Faction.official_clan"), "a dependency on an official world object is refused");
        var aliases = Engine();
        Check(Refusal(() => Registry(A, World(A), aliases).Register(aliases, World(A))) == null && aliases.IsReady("NPCCharacter", "main_hero"),
              "the unprefixed player aliases (main_hero, player_faction) are allowed, replacing the official player definition");
        // MMR-34: the startup's aliases, a land bandit clan homed at a generated hideout and the retirement retreat, register as well; the
        // retreat's CustomSettlementComponent is typed by its component_name.
        var startupEngine = Engine(); startupEngine.Add("Culture", "looters");
        Func<string, string> startupEdit = s => s
            .Replace("</Factions>", "<Faction id='looters' is_bandit='true' culture='Culture.looters' initial_home_settlement='Settlement." + A + "hideout_1'/></Factions>")
            .Replace("</Settlements>", "<Settlement id='" + A + "hideout_1' culture='Culture.empire'><Components><Hideout id='" + A + "hideout_1_component'/></Components></Settlement>" +
                     "<Settlement id='retirement_retreat' culture='Culture.empire'><Components><CustomSettlementComponent id='retirement_component' component_name='RetirementSettlementComponent'/></Components></Settlement></Settlements>");
        var startupDeclared = Declared(A).Concat(new[] { "Faction.looters", "Settlement." + A + "hideout_1", "Hideout." + A + "hideout_1_component", "Settlement.retirement_retreat",
                                                         "RetirementSettlementComponent.retirement_component" }).ToList();
        var startupPlan = RegistrationPlan.FromDocuments(A, World(A, startupEdit), false, startupDeclared);
        var startupRefusal = Refusal(() => Registry(A, World(A, startupEdit), startupEngine, false, startupDeclared).Register(startupEngine, World(A, startupEdit)));
        Check(startupRefusal == null && startupEngine.IsReady("Faction", "looters") && startupEngine.IsReady("Settlement", "retirement_retreat") &&
              startupPlan.Objects.Any(o => o.Type == "RetirementSettlementComponent" && o.Id == "retirement_component") && !startupPlan.Objects.Any(o => o.Type == "CustomSettlementComponent"),
              "the startup's unprefixed aliases register too: a land bandit clan (looters) homed at a generated hideout, and the retirement retreat, whose CustomSettlementComponent is typed RetirementSettlementComponent by its component_name" +
              (startupRefusal == null ? "" : " (refused: " + startupRefusal + ")"));
        var notAlias = Refusal(() => { var e = Engine(); var edit = (Func<string, string>)(s => s.Replace("</Factions>", "<Faction id='corsair_band' is_bandit='true' culture='Culture.empire' initial_home_settlement='Settlement." + A + "town_1'/></Factions>"));
                                       Registry(A, World(A, edit), e, false, Declared(A).Concat(new[] { "Faction.corsair_band" })).Register(e, World(A, edit)); });
        Check(notAlias != null && notAlias.Contains("corsair_band"), "an unprefixed generated id that is not an alias (corsair_band) is refused");
    }

    // c2: deterministic census over both managers; closure asserted before the default lists publish and after.
    private static void CensusAndClosure()
    {
        var first = Engine(); var second = Engine(true);
        var r1 = Registry(A, World(A), first); r1.Register(first, World(A));
        var r2 = Registry(A, World(A), second); r2.Register(second, World(A));
        var unpublished = new FakeLists { Values = { ["Settlements"] = null, ["Clans"] = new List<KeyValuePair<string, bool>>() } };
        Check(Json(r1.Census(first, unpublished, "registration", ClosureMode.Registration)) == Json(r2.Census(second, unpublished, "registration", ClosureMode.Registration)),
              "the census is identical whatever the registration order of the shared templates");
        // Determinism also when closure has problems (the same objects left presumed in a different order).
        first.Add("Settlement", A + "ghost_2", false); first.Add("Settlement", A + "ghost_1", false);
        second.Add("Settlement", A + "ghost_1", false); second.Add("Settlement", A + "ghost_2", false);
        string bad1 = Json(r1.Census(first, null, "x", ClosureMode.Registration)), bad2 = Json(r2.Census(second, null, "x", ClosureMode.Registration));
        Check(bad1 == bad2 && Closure(r1.Census(first, null, "x", ClosureMode.Registration)).Count == 2, "the census is identical also when closure reports problems (sorted)");
        first.Unregister("Settlement", A + "ghost_1"); first.Unregister("Settlement", A + "ghost_2");

        var census = r1.Census(first, unpublished, "registration", ClosureMode.Registration);
        var settlements = Row(census, "objectManager", "Settlement");
        Check((int)settlements["thisWorld"] == 4 && (int)Row(census, "objectManager", "Culture")["official"] == 2 && Closure(census).Count == 0 &&
              (bool)Row(census, "campaignLists", "Settlements")["published"] == false, "the census counts this world's and official objects per type, reports closure and lists not yet published");
        // Origins: objects the campaign derives from ours, runtime-created ones, and ones derived from another world.
        first.Add("MobileParty", "garrison_party_" + A + "town_1_" + A + "clan_1_1"); first.Add("Hero", "CharacterObject_5");
        var origins = r1.Census(first, null, "runtime", ClosureMode.Registration);
        Check((int)Row(origins, "objectManager", "MobileParty")["thisWorldDerived"] == 1 && (int)Row(origins, "objectManager", "Hero")["runtimeCreated"] == 1 &&
              (int)Row(origins, "objectManager", "Hero")["official"] == 0, "campaign-created objects are counted as derived from this world or runtime-created, not official");
        // An installed record the registration itself loads first (generated games replace SandBox's XML) is official too.
        Func<string, string> castle = s => s.Replace("<LocationComplexTemplate id='village_complex'/>", "<LocationComplexTemplate id='village_complex'/><LocationComplexTemplate id='castle_complex'/>");
        var fresh = Engine(); var rf = Registry(A, World(A, castle), fresh); rf.Register(fresh, World(A, castle));
        var templates = Row(rf.Census(fresh, null, "templates", ClosureMode.Registration), "objectManager", "LocationComplexTemplate");
        Check((int)templates["official"] == 3 && (int)templates["runtimeCreated"] == 0, "an installed record first loaded by the registration counts as official, not runtime-created");
        first.Add("MobileParty", "militias_of_" + B + "village_1");
        var leak = r1.Census(first, null, "leak", ClosureMode.Registration);
        Check(((List<string>)leak["otherWorldPrefixes"]).SequenceEqual(new[] { "mnbl_0000002b" }) && Has(Closure(leak), "object of another world: MobileParty.militias_of_" + B + "village_1"),
              "an object derived from another world (its prefix embedded in the id) is another world's object");
        first.Unregister("MobileParty", "militias_of_" + B + "village_1"); first.Unregister("MobileParty", "garrison_party_" + A + "town_1_" + A + "clan_1_1"); first.Unregister("Hero", "CharacterObject_5");

        // After initialization: the temporary types move to the campaign lists; settlements stay.
        var moved = Engine(); var rm = Registry(A, World(A), moved); rm.Register(moved, World(A));
        var lists = new FakeLists { Values = moved.RemoveTemporaryTypes() };
        var afterInit = rm.Census(moved, lists, "settled", ClosureMode.Settled);
        Check(Closure(afterInit).Count == 0 && (bool)Row(afterInit, "objectManager", "Hero")["held"] == false && (bool)Row(afterInit, "objectManager", "Settlement")["held"] == true &&
              (int)Row(afterInit, "campaignLists", "Settlements")["thisWorld"] == 4, "after initialization, closure follows the moved types to the campaign lists; settlements stay held");
        var clans = lists.Values["Clans"]; lists.Values["Clans"] = clans.Where(e => e.Key != A + "clan_2").ToList();
        Check(Has(rm.ClosureProblems(moved, lists, ClosureMode.Settled), "not in the campaign's Faction list: Faction." + A + "clan_2"), "a generated clan missing from the published Clans list fails closure");
        lists.Values["Clans"] = clans.Select(e => e.Key == A + "clan_2" ? new KeyValuePair<string, bool>(e.Key, false) : e).ToList();
        Check(Has(rm.ClosureProblems(moved, lists, ClosureMode.Settled), "not ready (campaign list): Faction." + A + "clan_2"), "a listed generated clan that is not ready fails settled closure");
        lists.Values["Clans"] = clans;
        var sett = lists.Values["Settlements"]; lists.Values["Settlements"] = sett.Where(e => e.Key != A + "village_2").ToList();
        var missingSettlement = rm.ClosureProblems(moved, lists, ClosureMode.Settled);
        lists.Values["Settlements"] = sett.Concat(Entries(new[] { A + "village_9" }, true)).ToList();
        var extraSettlement = rm.ClosureProblems(moved, lists, ClosureMode.Settled);
        lists.Values["Settlements"] = sett;
        Check(Has(missingSettlement, "registered but not in the campaign's Settlements list: Settlement." + A + "village_2") && Has(extraSettlement, "in the campaign's Settlements list but not registered: Settlement." + A + "village_9"),
              "the published Settlements list and the object manager must agree on this world's settlements, both ways");
        lists.Values["Kingdoms"] = lists.Values["Kingdoms"].Concat(Entries(new[] { B + "kingdom_1" }, true)).ToList();
        Check(Has(rm.ClosureProblems(moved, lists, ClosureMode.Settled), "object of another world: Kingdom." + B + "kingdom_1"), "a campaign list holding another world's object fails closure");
        first.TradeBoundTo.Remove(A + "village_2"); moved.TradeBoundTo.Remove(A + "village_2");
        Check(Has(r1.ClosureProblems(first, null, ClosureMode.Registration), "village Settlement." + A + "village_2 is not trade-bound to Settlement." + A + "town_2") && !Has(rm.ClosureProblems(moved, null, ClosureMode.Settled), "trade-bound"), "a village that lost its trade-bound link fails registration closure (settled closure leaves trade bounds to the campaign)");

        // An approved template that is not actually loaded stays presumed: closure fails inside Register.
        Func<string, string> item = s => s.Replace("<NPCCharacter id='" + A + "ruler_2' ", "<NPCCharacter id='" + A + "ruler_2' banner_item='Item.missing_item' ");
        var engine = Engine();
        var lying = new CampaignObjectRegistry(RegistrationPlan.FromDocuments(A, World(A, item), false, Declared(A)), (t, i) => true);
        string message = null;
        try { lying.Register(engine, World(A, item)); } catch (RegistrationException e) { message = e.Message; }
        Check(message != null && message.StartsWith("Closure failed before the default lists publish") && message.Contains("reference not ready: NPCCharacter." + A + "ruler_2.banner_item -> Item.missing_item") && !lying.Registered,
              "a reference left presumed fails closure inside Register, before the caller can publish default lists");
        // MMR-36: a failed new-world registration is rolled back through RemoveWorldObjects, at closure or in the middle of loading.
        Func<EngineModel, int> worldObjects = e => CampaignObjectRegistry.CensusTypes.Sum(t => e.Objects(t).Count(o => CampaignIdentity.WorldPrefix(o.Key) == A));
        Check(worldObjects(engine) == 0 && lying.Trace.Any(t => t.StartsWith("rolled_back:", StringComparison.Ordinal)) && lying.RollbackRemaining != null && lying.RollbackRemaining.Count == 0 &&
              engine.IsReady("NPCCharacter", "main_hero") && engine.IsReady("Culture", "empire"),
              "the closure failure rolled the world back: no object of it remains, templates and the pre-existing main_hero stay (" + lying.Trace.Last(t => t.StartsWith("rolled_back:", StringComparison.Ordinal)) + ")");
        var halfway = Engine(); halfway.ThrowOnPhase = World(A).Last().Key;
        var interrupted = Registry(A, World(A), halfway);
        Exception loadFailure = null;
        try { interrupted.Register(halfway, World(A)); } catch (Exception e) { loadFailure = e; }
        Check(loadFailure is InvalidOperationException && halfway.Loaded.Count == World(A).Count() - 1 && worldObjects(halfway) == 0 && interrupted.RollbackRemaining != null && interrupted.RollbackRemaining.Count == 0 && !interrupted.Registered,
              "a load that fails at the last phase (" + halfway.ThrowOnPhase + ", after " + halfway.Loaded.Count + " documents loaded) is rolled back before the failure propagates: no object of the world remains");
    }

    // c3: world A, then world B, keep the shared templates and nothing of A.
    private static void TwoWorlds()
    {
        // As the engine does it: B gets a fresh object manager with the templates reloaded.
        var storeA = Engine(); var ra = Registry(A, World(A), storeA); ra.Register(storeA, World(A));
        var storeB = Engine(true); var rb = Registry(B, World(B), storeB); rb.Register(storeB, World(B));
        var censusA = ra.Census(storeA, null, "A", ClosureMode.Registration); var censusB = rb.Census(storeB, null, "B", ClosureMode.Registration);
        Check(((List<string>)censusB["otherWorldPrefixes"]).Count == 0 && Closure(censusB).Count == 0 &&
              new[] { "Culture", "NPCCharacter", "WorkshopType", "LocationComplexTemplate" }.All(t => (string)Row(censusA, "objectManager", t)["officialIdsSha256"] == (string)Row(censusB, "objectManager", t)["officialIdsSha256"]),
              "B in a fresh object manager closes with the same official template ids as A");
        // The census detects a leak: an A object left in B's object manager, or an A-derived party in B's lists.
        var leaky = Engine(); leaky.Add("Settlement", A + "town_1");
        var rl = Registry(B, World(B), leaky);
        var leakRefusal = Refusal(() => rl.Register(leaky, World(B)));
        var derived = new FakeLists { Values = { ["MobileParties"] = Entries(new[] { "garrison_party_" + A + "town_1_" + A + "clan_1_1" }, true) } };
        var derivedLeak = rb.Census(storeB, derived, "B", ClosureMode.Settled);
        Check(leakRefusal != null && leakRefusal.Contains("object of another world: Settlement." + A + "town_1") &&
              (int)Row(derivedLeak, "campaignLists", "MobileParties")["otherWorlds"] == 1, "an object of A in B's object manager fails B's closure, and an A-derived party in B's lists is counted as A's");
        // The same object manager reused: removing A takes only A's objects, never a template or the pre-existing alias.
        var store = Engine();
        var r = Registry(A, World(A), store); r.Register(store, World(A));
        List<string> remaining;
        int removed = r.RemoveWorldObjects(store, null, out remaining);
        var kept = new[] { "Culture.empire", "NPCCharacter.lord_template", "NPCCharacter.main_hero", "WorkshopType.smithy", "LocationComplexTemplate.town_complex", "Item.sword" }
            .All(o => store.IsReady(o.Split('.')[0], o.Split('.')[1]));
        var r2 = Registry(B, World(B), store); r2.Register(store, World(B));
        var census = r2.Census(store, null, "B after A", ClosureMode.Registration);
        Check(removed > 0 && kept && remaining.Count == 0 && ((List<string>)census["otherWorldPrefixes"]).Count == 0 && Closure(census).Count == 0 && !r.Registered,
              "in a reused object manager, removing A unregisters only A's objects; templates and the pre-existing main_hero stay; B registers with nothing of A");
        // After initialization the campaign lists hold A's clans, heroes and kingdoms: removal reports them.
        var late = Engine(); var rlate = Registry(A, World(A), late); rlate.Register(late, World(A));
        var lateLists = new FakeLists { Values = late.RemoveTemporaryTypes() };
        List<string> left;
        rlate.RemoveWorldObjects(late, lateLists, out left);
        Check(left.Any(x => x.StartsWith("Faction." + A + "clan_1")) && left.All(x => x.EndsWith("(campaign list)")), "after initialization, removal reports the world objects it cannot unregister");
        // No mutable static state: every static field is read-only and no public static collection can be changed.
        var statics = new[] { typeof(CampaignObjectRegistry), typeof(RegistrationPlan), typeof(CampaignIdentity), typeof(SaveIds) }
            .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)).ToList();
        var mutable = statics.Where(f => !f.IsInitOnly && !f.IsLiteral).Select(f => f.Name)
            .Concat(statics.Where(f => f.IsPublic && f.GetValue(null) is ICollection && !(f.GetValue(null) is IList && ((IList)f.GetValue(null)).IsReadOnly)).Select(f => f.Name)).ToList();
        Check(mutable.Count == 0, "the registry types hold no mutable static state" + (mutable.Count == 0 ? "" : ": " + String.Join(", ", mutable)));
        bool second = false;
        try { r2.Register(store, World(B)); } catch (InvalidOperationException) { second = true; }
        Check(second, "a registry registers one world once; a second campaign needs its own registry");
        // The campaign instance is not part of any world id: two campaigns of the world plan the same ids.
        string i1 = "3f2b8c1e-9d4a-4b6f-8e2a-1c5d7f9a0b3e", i2 = "7a1d2c3b-4e5f-4a6b-9c8d-0e1f2a3b4c5d";
        var c1 = Engine(); var ri1 = Registry(A, World(A), c1, false, null, i1); ri1.Register(c1, World(A));
        var c2 = Engine(); var ri2 = Registry(A, World(A), c2, false, null, i2); ri2.Register(c2, World(A));
        string bare = i1.Replace("-", "").Substring(0, 30);
        Func<string, string> embed = s => s.Replace(A + "village_2", A + bare);
        var embedded = Engine();
        var embeds = Refusal(() => Registry(A, World(A, embed), embedded, false, Declared(A).Select(x => x.Replace(A + "village_2", A + bare)), bare).Register(embedded, World(A, embed)));
        Check(ri1.Plan.GeneratedObjects.Select(x => x.Key).SequenceEqual(ri2.Plan.GeneratedObjects.Select(x => x.Key)) &&
              (string)ri1.Census(c1, null, "i", ClosureMode.Registration)["campaignInstanceId"] == i1 && embeds != null && embeds.Contains("embeds the campaign instance id"),
              "two campaigns of one world register the same immutable ids; the instance id is recorded in the census and refused inside an id");
    }

    // The R05 timing contract: replay after BeforeLoad, before UnregisterNonReadyObjects; settled closure after the load.
    private static void Replay()
    {
        var plan = RegistrationPlan.FromDocuments(A, World(A), false, Declared(A));
        var saved = plan.GeneratedObjects.Select(o => new KeyValuePair<string, string>(o.Type, o.Id)).ToList();
        var early = Engine();
        var before = Refusal(() => Registry(A, Saved(World(A)), early, true).Replay(early, Saved(World(A)), new FakeLists()));
        Check(before != null && before.Contains("would orphan them"), "a replay before BeforeLoad re-registers the saved objects is refused");
        var late = Engine(); var lateLists = new FakeLists { Values = late.BeforeLoad(saved) }; late.UnregisterNonReadyObjects();
        var after = Refusal(() => Registry(A, Saved(World(A)), late, true).Replay(late, Saved(World(A)), lateLists));
        Check(after != null && after.Contains("after UnregisterNonReadyObjects"), "a replay after UnregisterNonReadyObjects is refused");
        var claimed = Engine(); claimed.Stage = StoreStage.SavedObjectsRegistered;
        var wrongClaim = Refusal(() => Registry(A, Saved(World(A)), claimed, true).Replay(claimed, Saved(World(A)), new FakeLists()));
        Check(wrongClaim != null && wrongClaim.Contains("not between BeforeLoad and UnregisterNonReadyObjects"), "the replay timing is checked on the engine state, not only on the caller's stage");
        var load = Engine(); var loadLists = load.BeforeLoad(saved); var lists = new FakeLists { Values = loadLists };
        var replay = Registry(A, Saved(World(A)), load, true);
        replay.Replay(load, Saved(World(A)), lists);
        var duringLoad = replay.ClosureProblems(load, lists, ClosureMode.Settled);
        load.UnregisterNonReadyObjects();
        EngineModel.FinishLoad(loadLists);
        var settled = replay.Census(load, lists, "saved_game_loaded", ClosureMode.Settled);
        Check(replay.Registered && load.Loaded.SequenceEqual(RegistrationPlan.SavedGameOrder) && saved.Where(s => !RegistrationPlan.IsTemporaryType(s.Key)).All(s => load.IsReady(s.Key, s.Value)) &&
              !load.Objects("Hero").Any() && Closure(settled).Count == 0,
              "a replay at the contracted time re-merges the five permanent documents against the saved clans in the lists and closes once the load finished" +
              (Closure(settled).Count == 0 ? "" : ": " + String.Join("; ", Closure(settled).Take(3))));
        Check(Has(duringLoad, "not ready (campaign list): Faction." + A + "clan_1"), "before the load finishes, settled closure reports the saved clans not ready (replay closure only requires them listed)");
        var lostClan = Engine(); var lostLists = new FakeLists { Values = lostClan.BeforeLoad(saved) };
        lostLists.Values["Clans"] = lostLists.Values["Clans"].Where(e => e.Key != A + "clan_2").ToList();
        var noClan = Refusal(() => Registry(A, Saved(World(A)), lostClan, true).Replay(lostClan, Saved(World(A)), lostLists));
        Check(noClan != null && noClan.Contains("unresolved world reference Settlement." + A + "town_2.owner -> Faction." + A + "clan_2"),
              "a replayed settlement whose owner clan is not in the save is refused before loading");
        var partial = Engine(); var partialLists = new FakeLists { Values = partial.BeforeLoad(saved) };
        Func<string, string> dropVillage = s => s.Replace(Village(A, 2), "");
        var lost = Refusal(() => Registry(A, Saved(World(A, dropVillage)), partial, true).Replay(partial, Saved(World(A, dropVillage)), partialLists));
        Check(lost != null && lost.Contains("presumed or not deserialized: Settlement." + A + "village_2"), "a replay that misses a saved object fails closure instead of letting the engine drop it");
    }

    private static void Identity(string scanPath)
    {
        Check(CampaignIdentity.Problem("mnbl_1609fb72_t_a06de08e5b", "mnbl_1609fb72_") == null && CampaignIdentity.IsCompilerId("mnbl_1609fb72_t_a06de08e5b") &&
              CampaignIdentity.WorldPrefixOfSeed(42) == A && CampaignIdentity.Problem("Mnbl_0000002a_x", A) != null && CampaignIdentity.Problem("player_party", A) == null &&
              CampaignIdentity.WorldOrigin("militias_of_militias_of_" + A + "village_1") == A,
              "identity rules: compiler and seed ids are world-qualified; aliases are exempt; uppercase is refused; derived ids carry their world");
        var scan = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(scanPath).TrimStart('﻿'));
        var rows = ((object[])scan["definers"]).Cast<Dictionary<string, object>>().ToList();
        var bases = rows.Select(d => Convert.ToInt64(d["baseId"])).ToList();
        var installed = rows.Where(d => (string)d["source"] == "installed").Select(d => Convert.ToInt64(d["baseId"])).ToList();
        Check(installed.Count >= 70 && SaveIds.Base > installed.Max() + 1000000 && bases.All(b => Math.Abs(b - SaveIds.Base) > 1000000),
              "the module save-id base " + SaveIds.Base + " is clear of all " + bases.Count + " scanned SaveableTypeDefiner bases (" + installed.Count + " installed, highest " + installed.Max() + "; " +
              (bases.Count - installed.Count) + " from Workshop modules)");
    }

    // The compiler's records (MMR-27 ids, read from the verified MMR-29 bundle) plan without a problem.
    private static void CompilerRecords(string bundleDir)
    {
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", WorldDataBundle.HostCapabilities);
        var bundle = WorldDataBundle.Open(bundleDir, host);
        var text = new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/campaign/campaign.json"));
        var campaign = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(text);
        var counts = (Dictionary<string, object>)campaign["counts"];
        var plan = RegistrationPlan.FromCompilerRecords(campaign);
        var problems = plan.Problems((t, i) => false, (t, i) => true);
        // campaign.json v2: the clans are the houses, the mercenary companies and the outlaw clans (MMR-280).
        int minorClans = ((object[])campaign["mercenaryCompanies"]).Length + ((object[])campaign["outlawClans"]).Length;
        int expected = 2 * (int)counts["heroes"] + (int)counts["kingdoms"] + (int)counts["houses"] + minorClans + 2 * ((int)counts["towns"] + (int)counts["castles"]) + 2 * (int)counts["villages"];
        Check(problems.Count == 0 && plan.Objects.Count == expected && plan.Objects.All(o => o.Generated && (CampaignIdentity.IsCompilerId(o.Id) || o.Id.EndsWith("_component"))) &&
              plan.WorldPrefix == CampaignIdentity.WorldPrefix((string)((Dictionary<string, object>)((object[])campaign["towns"])[0])["id"]),
              "the real world's compiler records (" + plan.Objects.Count + " objects) plan in the proven order with compiler ids, no early reference and no duplicate" +
              (problems.Count == 0 ? "" : ": " + String.Join("; ", problems.Take(3))));
        Check(plan.Objects.Where(o => o.Type == "Village").All(v => plan.Objects.First(t => t.Key == "Settlement." + v.References[0].Id).Ordinal < v.Parent.Ordinal),
              "every village of the real world is planned after its bound town");
        var heroes = ((object[])campaign["heroes"]).ToList();
        heroes.Add(heroes[0]);
        var doubled = new Dictionary<string, object>(campaign) { ["heroes"] = heroes.ToArray() };
        Check(Has(RegistrationPlan.FromCompilerRecords(doubled).Problems((t, i) => false, (t, i) => true), "duplicate identity NPCCharacter." + (string)((Dictionary<string, object>)heroes[0])["id"]),
              "a repeated compiler record is a duplicate identity");
        var towns = ((object[])campaign["towns"]).Skip(1).ToArray();
        var lostTown = new Dictionary<string, object>(campaign) { ["towns"] = towns };
        Check(Has(RegistrationPlan.FromCompilerRecords(lostTown).Problems((t, i) => false, (t, i) => true), "unresolved world reference"),
              "a record whose town is missing has an unresolved world reference");
    }
}
