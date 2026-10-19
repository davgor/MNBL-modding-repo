using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-30: what a generated world registers, in which order, and what every object references (docs/campaign-registry.md).
    // Pure managed: built from the generated XML documents the game loads (FromDocuments) or from the compiler's campaign
    // records (FromCompilerRecords, MMR-27 ids; no identity is derived from a display name), and checked before anything is
    // registered (Problems).
    //
    // Order (R04, runs 31308/23192/24736, re-run 24212/31624, and 416 through this registry): SPCultures, NPCCharacters,
    // Heroes, Kingdoms, Factions, WorkshopTypes, LocationComplexTemplates, Settlements, with every town and castle before any
    // village. A saved game replays the five permanent-type documents (R05, runs 8256 and 33092). MBObjectManager.LoadXml
    // deserializes records in document order; a reference to an object not loaded yet creates a presumed (not ready) object
    // that its own record makes ready later in the same registration: the two-pass pattern. The spike's registration relies
    // on it for three forward references, and a compiled world's hero families (MMR-45) for three more (TwoPass); every other
    // reference must point backwards. Installed IL:
    //   Village.Deserialize reads bound through ReadObjectReferenceFromXml (a presumed Settlement when not loaded yet) and
    //     links the village to its town only if Bound.IsTown; a presumed settlement has no Town component, so an early
    //     village silently loses its trade-bound link (no exception). Its bound settlement must be a town or castle
    //     deserialized before the village's own settlement (ReadyAt).
    //   Hero.Deserialize reads GetObject<CharacterObject>(StringId).Age without a null check, so every Hero needs its
    //     NPCCharacter of the same id registered earlier.
    internal enum ReferenceTiming { Presumed, Ready }

    internal sealed class Reference
    {
        public Reference(string type, string id, string attribute, ReferenceTiming timing) { Type = type; Id = id; Attribute = attribute; Timing = timing; }
        public string Type { get; private set; }
        public string Id { get; private set; }
        public string Attribute { get; private set; }
        public ReferenceTiming Timing { get; private set; }
        public string Key { get { return Type + "." + Id; } }
    }

    internal sealed class PlannedObject
    {
        public string Type { get; internal set; }
        public string Id { get; internal set; }
        public string Phase { get; internal set; }
        public int Ordinal { get; internal set; }         // load position across the whole registration
        public bool Generated { get; internal set; }       // a world object, not a shared template re-emitted with its official record
        public PlannedObject Parent { get; internal set; } // the settlement a Town or Village component deserializes with
        public List<Reference> References { get; private set; }
        public string Key { get { return Type + "." + Id; } }
        public PlannedObject() { References = new List<Reference>(); }
    }

    internal sealed class RegistrationPlan
    {
        public static readonly IList<string> NewGameOrder = Array.AsReadOnly(new[] {
            "SPCultures", "NPCCharacters", "Heroes", "Kingdoms", "Factions", "WorkshopTypes", "LocationComplexTemplates", "Settlements" });
        // Heroes, Kingdoms and Factions are temporary types whose state lives in the save; vanilla skips them on load too.
        public static readonly IList<string> SavedGameOrder = Array.AsReadOnly(new[] {
            "SPCultures", "NPCCharacters", "WorkshopTypes", "LocationComplexTemplates", "Settlements" });
        // (referring type, attribute, referenced type): forward references the proven registration resolves in two passes.
        // MMR-45: a hero's family (father, mother, spouse) may name a hero later in the Heroes document, as the installed Heroes.xml does.
        // Installed 1.4.8 IL: Hero.Deserialize reads each through ReadObjectReferenceFromXml (a presumed Hero when not loaded yet);
        // set_Father/set_Mother store it and add this hero to its _children list, set_Spouse links both sides; nothing else of the
        // referenced hero is read, and its own record later deserializes the same object (Deserialize never resets _children or _spouse).
        // A family member that never gets a record stays presumed, which the closure check refuses.
        public static readonly IList<string> TwoPass = Array.AsReadOnly(new[] {
            "Hero.faction>Faction", "Kingdom.initial_home_settlement>Settlement", "Faction.initial_home_settlement>Settlement",
            "Hero.father>Hero", "Hero.mother>Hero", "Hero.spouse>Hero" });
        // (referring type, attribute): references that must be fully deserialized when the referring object deserializes.
        public static readonly IList<string> ReadyAt = Array.AsReadOnly(new[] { "Village.bound" });
        // Engine object types that are world instances: never official in a generated world, always world-qualified.
        private static readonly HashSet<string> WorldTypes = new HashSet<string>(new[] {
            "Hero", "Faction", "Kingdom", "Settlement", "Town", "Village", "Hideout", "RetirementSettlementComponent", "MobileParty" }, StringComparer.Ordinal);
        // Types whose state lives in the save and, after initialization, in CampaignObjectManager (run 416: Settlement, Town
        // and Village stay in the object manager; Hero, Faction, Kingdom and MobileParty leave it).
        private static readonly HashSet<string> TemporaryTypes = new HashSet<string>(new[] { "Hero", "Kingdom", "Faction", "MobileParty" }, StringComparer.Ordinal);
        private static readonly Regex Typed = new Regex(@"^([A-Z][A-Za-z]*)\.([A-Za-z0-9_-]+)$", RegexOptions.CultureInvariant);

        public static bool IsWorldType(string type) { return WorldTypes.Contains(type); }
        public static bool IsTemporaryType(string type) { return TemporaryTypes.Contains(type); }

        public string WorldPrefix { get; private set; }
        public IList<string> Phases { get; private set; }
        public IList<PlannedObject> Objects { get; private set; }
        public bool SavedGame { get; private set; }
        private readonly List<string> buildProblems = new List<string>();
        private readonly HashSet<string> declared;   // the producer's generated keys (Type.id), or null to infer them

        private RegistrationPlan(string worldPrefix, IList<string> phases, bool saved, IEnumerable<string> generated)
        {
            WorldPrefix = worldPrefix; Phases = phases; SavedGame = saved; Objects = new List<PlannedObject>();
            declared = generated == null ? null : new HashSet<string>(generated, StringComparer.Ordinal);
        }

        public IEnumerable<PlannedObject> GeneratedObjects { get { return Objects.Where(o => o.Generated); } }

        // The objects in the XML documents the game will load, phase by phase (document id -> document), in the given order.
        // generated: the producer's list of the world objects it emitted (Type.id; GeneratedCampaignDataset.Instances plus
        // its aliases), so a generated record of any type is checked even without a world prefix. Without it (a replay from
        // a package), world-prefixed ids, aliases and world types are the generated objects.
        public static RegistrationPlan FromDocuments(string worldPrefix, IList<KeyValuePair<string, XmlDocument>> documents, bool savedGame, IEnumerable<string> generated = null)
        {
            var plan = new RegistrationPlan(worldPrefix, documents.Select(d => d.Key).ToList().AsReadOnly(), savedGame, generated);
            foreach (var document in documents)
            {
                if (document.Value == null || document.Value.DocumentElement == null) { plan.buildProblems.Add("document " + document.Key + " is empty"); continue; }
                foreach (var element in Children(document.Value.DocumentElement))
                {
                    if (!element.HasAttribute("id")) continue;
                    var record = plan.Add(document.Key, element.Name, element.GetAttribute("id"), null);
                    AddReferences(record, element);
                    // Settlement components (Components/Town, Components/Village) are objects of their own, deserialized with their settlement.
                    // A CustomSettlementComponent is created as the type its component_name names (Settlement.Deserialize, installed IL).
                    foreach (var components in Children(element).Where(c => c.Name == "Components"))
                        foreach (var component in Children(components).Where(c => c.HasAttribute("id")))
                        {
                            string type = component.Name == "CustomSettlementComponent" && component.HasAttribute("component_name") ? component.GetAttribute("component_name") : component.Name;
                            AddReferences(plan.Add(document.Key, type, component.GetAttribute("id"), record), component);
                        }
                }
            }
            if (plan.declared != null)
                foreach (var key in plan.declared.Where(k => !plan.Objects.Any(o => o.Key == k)).OrderBy(k => k, StringComparer.Ordinal))
                    if (!key.StartsWith("MobileParty.", StringComparison.Ordinal))   // parties are created by the campaign, not registered from XML
                        plan.buildProblems.Add("declared generated object " + key + " is in no document");
            return plan;
        }

        // The world objects of the compiler's campaign records (campaign.json, mnbl.campaign-records v1), with the references
        // the records name. Heroes become an NPCCharacter and a Hero; clans are Factions; towns and castles are Settlements
        // with a Town component, villages Settlements with a Village component, ordered after every town and castle. Every
        // object is a declared generated object.
        public static RegistrationPlan FromCompilerRecords(IDictionary<string, object> campaign)
        {
            var heroes = Records(campaign, "heroes"); var clans = Records(campaign, "clans"); var kingdoms = Records(campaign, "kingdoms");
            var towns = Records(campaign, "towns"); var castles = Records(campaign, "castles"); var villages = Records(campaign, "villages");
            string any = heroes.Concat(clans).Concat(towns).Select(r => Text(r, "id")).FirstOrDefault(id => CampaignIdentity.WorldPrefix(id) != null);
            var plan = new RegistrationPlan(CampaignIdentity.WorldPrefix(any), NewGameOrder, false, new string[0]);
            if (Text(campaign, "schema") != "mnbl.campaign-records") plan.buildProblems.Add("not mnbl.campaign-records");
            var clanOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var clan in clans)
                foreach (var member in List(clan, "members")) clanOf[Convert.ToString(member, CultureInfo.InvariantCulture)] = Text(clan, "id");
            foreach (var hero in heroes) plan.Declare("NPCCharacters", "NPCCharacter", Text(hero, "id"), null);
            foreach (var hero in heroes)
            {
                var h = plan.Declare("Heroes", "Hero", Text(hero, "id"), null);
                string clan;
                if (clanOf.TryGetValue(Text(hero, "id"), out clan)) h.References.Add(new Reference("Faction", clan, "faction", ReferenceTiming.Presumed));
            }
            foreach (var kingdom in kingdoms)
            {
                var k = plan.Declare("Kingdoms", "Kingdom", Text(kingdom, "id"), null);
                Link(k, "owner", "Hero", Text(kingdom, "ruler"));
                Link(k, "initial_home_settlement", "Settlement", Text(kingdom, "capital"));
            }
            foreach (var clan in clans)
            {
                var l = plan.Declare("Factions", "Faction", Text(clan, "id"), null);
                Link(l, "owner", "Hero", Text(clan, "leader"));
                Link(l, "super_faction", "Kingdom", Text(clan, "kingdom"));
                Link(l, "initial_home_settlement", "Settlement", Text(clan, "home"));
            }
            foreach (var settlement in towns.Concat(castles))
            {
                var s = plan.Declare("Settlements", "Settlement", Text(settlement, "id"), null);
                Link(s, "owner", "Faction", Text(settlement, "owner"));
                plan.Declare("Settlements", "Town", ComponentId(Text(settlement, "id")), s);
            }
            foreach (var village in villages)
            {
                var s = plan.Declare("Settlements", "Settlement", Text(village, "id"), null);
                var v = plan.Declare("Settlements", "Village", ComponentId(Text(village, "id")), s);
                Link(v, "bound", "Settlement", Text(village, "bound"));
            }
            return plan;
        }

        // The id of a settlement's Town or Village component.
        public static string ComponentId(string settlementId) { return settlementId + "_component"; }

        // Every reason this plan must not be registered, empty when it may. exists(type, id): the object is already in the
        // object manager or a published campaign list. shared(type, id): an approved shared template or engine default that
        // generated objects may reference.
        public List<string> Problems(Func<string, string, bool> exists, Func<string, string, bool> shared)
        {
            var problems = new List<string>(buildProblems);
            if (WorldPrefix == null) problems.Add("no world-qualified object: the world prefix is unknown");
            var expected = SavedGame ? SavedGameOrder : NewGameOrder;
            if (!Phases.SequenceEqual(expected)) problems.Add("phase order is " + String.Join(", ", Phases) + "; the proven order is " + String.Join(", ", expected));
            var byKey = new Dictionary<string, PlannedObject>(StringComparer.Ordinal);
            var typeOfGeneratedId = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var o in Objects)
            {
                if (byKey.ContainsKey(o.Key)) { problems.Add("duplicate identity " + o.Key + " (" + byKey[o.Key].Phase + " and " + o.Phase + ")"); continue; }
                byKey[o.Key] = o;
                if (!o.Generated) continue;   // a shared template re-emitted with its official id (merged onto the loaded record)
                string identity = CampaignIdentity.Problem(o.Id, WorldPrefix);
                if (identity != null) problems.Add("identity " + o.Key + ": " + identity);
                // A hero's character and its Hero share an id; any other id names one world object only.
                string other;
                if (typeOfGeneratedId.TryGetValue(o.Id, out other) && !(new[] { other, o.Type }.OrderBy(t => t, StringComparer.Ordinal).SequenceEqual(new[] { "Hero", "NPCCharacter" })))
                    problems.Add("duplicate identity " + o.Id + " used by " + other + " and " + o.Type);
                else typeOfGeneratedId[o.Id] = o.Type;
                if (!SavedGame && !CampaignIdentity.IsAlias(o.Id) && exists(o.Type, o.Id)) problems.Add("identity " + o.Key + " collides with an object already registered");
            }
            var towns = new HashSet<string>(Objects.Where(o => o.Type == "Town" && o.Parent != null).Select(o => o.Parent.Key), StringComparer.Ordinal);
            foreach (var hero in Objects.Where(o => o.Type == "Hero"))
            {
                PlannedObject character;
                if (!byKey.TryGetValue("NPCCharacter." + hero.Id, out character) || character.Ordinal > hero.Ordinal)
                    problems.Add("hero " + hero.Key + " has no NPCCharacter of the same id registered before it (Hero.Deserialize reads its character without a null check)");
            }
            // References of generated objects only: a shared template re-emitted with its official record keeps the engine's
            // own (sometimes optional) references, which the dataset builder already closed (GeneratedCampaignDataset).
            foreach (var o in Objects.Where(x => x.Generated))
                foreach (var r in o.References)
                {
                    string prefix = CampaignIdentity.WorldPrefix(r.Id);
                    if (prefix != null && WorldPrefix != null && prefix != WorldPrefix)
                    { problems.Add("reference to another world " + o.Key + "." + r.Attribute + " -> " + r.Key); continue; }
                    PlannedObject target;
                    if (byKey.TryGetValue(r.Key, out target))
                    {
                        if (r.Timing == ReferenceTiming.Ready)
                        {
                            int before = (o.Parent ?? o).Ordinal;
                            if (target.Ordinal >= before)
                                problems.Add("early reference " + o.Key + "." + r.Attribute + " -> " + r.Key + ": it must be deserialized before " + (o.Parent ?? o).Key + " (" + target.Phase + " position " + target.Ordinal + ", not before " + before + ")");
                            else if (r.Type == "Settlement" && !towns.Contains(target.Key))
                                problems.Add("reference " + o.Key + "." + r.Attribute + " -> " + r.Key + " is not a town or castle: the village would lose its trade-bound link");
                            continue;
                        }
                        if (target.Ordinal <= o.Ordinal) continue;
                        if (!TwoPass.Contains(o.Type + "." + r.Attribute + ">" + r.Type))
                            problems.Add("early reference " + o.Key + "." + r.Attribute + " -> " + r.Key + ": a forward reference outside the proven two-pass pattern (" + String.Join(", ", TwoPass) + ")");
                        continue;
                    }
                    // A replay re-merges only the permanent types; its references to clans, heroes and kingdoms are the
                    // saved objects (in the deserialized campaign lists; closure then checks them).
                    if (IsWorldType(r.Type) && SavedGame && CampaignIdentity.Problem(r.Id, WorldPrefix) == null && exists(r.Type, r.Id)) continue;
                    if (IsWorldType(r.Type))
                        problems.Add((CampaignIdentity.WorldPrefix(r.Id) == null && !CampaignIdentity.IsAlias(r.Id) ? "official world-instance dependency " : "unresolved world reference ") + o.Key + "." + r.Attribute + " -> " + r.Key);
                    else if (!shared(r.Type, r.Id))
                        problems.Add("unresolved reference " + o.Key + "." + r.Attribute + " -> " + r.Key + " (neither registered here nor an approved shared template)");
                }
            return problems;
        }

        private PlannedObject Add(string phase, string type, string id, PlannedObject parent)
        {
            string key = type + "." + id;
            bool generated = declared != null && declared.Contains(key) || CampaignIdentity.IsAlias(id) || CampaignIdentity.WorldPrefix(id) != null || IsWorldType(type);
            var o = new PlannedObject { Phase = phase, Type = type, Id = id, Ordinal = Objects.Count, Generated = generated, Parent = parent };
            Objects.Add(o);
            return o;
        }

        private PlannedObject Declare(string phase, string type, string id, PlannedObject parent)
        {
            declared.Add(type + "." + id);
            return Add(phase, type, id, parent);
        }

        private static void AddReferences(PlannedObject o, XmlElement element)
        {
            foreach (XmlAttribute attribute in element.Attributes)
            {
                if (attribute.Name == "id") continue;
                var m = Typed.Match(attribute.Value);
                if (!m.Success) continue;
                var timing = ReadyAt.Contains(o.Type + "." + attribute.Name) ? ReferenceTiming.Ready : ReferenceTiming.Presumed;
                o.References.Add(new Reference(m.Groups[1].Value, m.Groups[2].Value, attribute.Name, timing));
            }
        }

        private static void Link(PlannedObject o, string attribute, string type, string id)
        {
            if (String.IsNullOrEmpty(id)) return;
            var timing = ReadyAt.Contains(o.Type + "." + attribute) ? ReferenceTiming.Ready : ReferenceTiming.Presumed;
            o.References.Add(new Reference(type, id, attribute, timing));
        }

        private static IEnumerable<XmlElement> Children(XmlElement e) { return e.ChildNodes.OfType<XmlElement>(); }
        private static string Text(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v as string : null; }
        private static IEnumerable<object> List(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v is IEnumerable && !(v is string) ? ((IEnumerable)v).Cast<object>() : Enumerable.Empty<object>(); }
        private static List<IDictionary<string, object>> Records(IDictionary<string, object> campaign, string key) { return List(campaign, key).OfType<IDictionary<string, object>>().ToList(); }
    }
}
