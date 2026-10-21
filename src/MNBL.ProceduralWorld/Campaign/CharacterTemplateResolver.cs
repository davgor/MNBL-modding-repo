using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-32: the installed character templates generated heroes and troops are made from (docs/heroes.md). Pure managed, over the
    // merged campaign catalog (NPCCharacters, SPCultures, partyTemplates, EquipmentRosters, Items, SkillSets, BodyProperties,
    // ShipHulls, Factions) and the engine-default references a live run verified (Skill, Trait, ItemCategory, ...). It answers which
    // installed templates and lord equipment rosters a culture offers, and proves that every template and every troop a culture's
    // recruitment, garrison, militia, caravan, patrol, naval and bandit roots reach resolves: body (face key template or body key),
    // skill set, equipment sets, their items (existing, fitting their slot, usable by the wearer's sex), upgrade targets, upgrade item
    // categories and ship hulls. A broken troop, an upgrade cycle or a broken required root is a problem; a broken optional template
    // is excluded from its pool (and reported), so only an empty pool refuses. Nothing is repaired, created or registered.
    internal sealed class CharacterTemplateResolver
    {
        public sealed class CultureReport
        {
            public string Culture;
            public bool Bandit;
            public List<string> Roots = new List<string>(), Troops = new List<string>(), PartyTemplates = new List<string>(), Ships = new List<string>();
            public int MaxUpgradeDepth;
        }

        private readonly Dictionary<string, XmlElement> objects = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
        private readonly HashSet<string> defaults;
        private readonly IList<string> characterRoots, partyTemplateRoots, shipRoots;
        private readonly SortedDictionary<string, string> excluded = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public static readonly string[] Documents = { "NPCCharacters", "SPCultures", "partyTemplates", "EquipmentRosters", "Items", "SkillSets", "BodyProperties", "ShipHulls", "Factions" };

        // catalog: merged documents by id (Documents). defaults: typed engine-default references verified live. troops: the rosters
        // policy's troops section (characterRoots, partyTemplateRoots, ships).
        public CharacterTemplateResolver(IDictionary<string, XmlDocument> catalog, IEnumerable<string> defaults, IDictionary<string, object> troops)
        {
            foreach (var id in Documents)
            {
                XmlDocument doc;
                if (!catalog.TryGetValue(id, out doc) || doc.DocumentElement == null) throw new InvalidOperationException("catalog document " + id + " is missing");
                foreach (var e in doc.DocumentElement.ChildNodes.OfType<XmlElement>())
                {
                    string type = TypeOf(e.Name), key;
                    if (type == null || !e.HasAttribute("id")) continue;
                    key = type + "." + e.GetAttribute("id");
                    if (objects.ContainsKey(key)) throw new InvalidOperationException("duplicate catalog object " + key);
                    objects.Add(key, e);
                }
            }
            this.defaults = new HashSet<string>(defaults, StringComparer.Ordinal);
            characterRoots = Strings(troops, "characterRoots");
            partyTemplateRoots = Strings(troops, "partyTemplateRoots");
            shipRoots = Strings(troops, "ships");
        }

        // Templates left out of their pool, with the reason (a broken optional template never refuses the build on its own).
        public IDictionary<string, string> Excluded { get { return excluded; } }
        public bool Exists(string key) { return objects.ContainsKey(key) || defaults.Contains(key); }
        public XmlElement Element(string key) { XmlElement e; return objects.TryGetValue(key, out e) ? e : null; }
        public IEnumerable<string> BanditCultures() { return Cultures().Where(c => Culture(c).GetAttribute("is_bandit") == "true"); }
        public IEnumerable<string> Cultures() { return Keys("Culture."); }
        public bool IsFemale(string characterId) { var e = Element("NPCCharacter." + characterId); return e != null && e.GetAttribute("is_female") == "true"; }
        public string FaceKeyTemplate(string characterId)
        {
            var e = Element("NPCCharacter." + characterId);
            var t = e == null ? null : e.SelectSingleNode("face/face_key_template") as XmlElement;
            return t == null ? null : t.GetAttribute("value");
        }

        // Installed lord templates of a culture: its lord_templates list, each an is_template Lord whose references resolve.
        public List<string> LordTemplates(string culture, List<string> problems)
        {
            return Templates(culture, ListValues(Culture(culture), "lord_templates"), "Lord", problems);
        }

        public List<string> NotableTemplates(string culture, string occupation, List<string> problems)
        {
            return Templates(culture, ListValues(Culture(culture), "notable_templates").Where(k => Occupation(k) == occupation), occupation, problems);
        }

        // Companions: CompanionsCampaignBehavior.InitializeCompanionTemplateList takes every is_template Wanderer character (installed IL).
        public List<string> WandererTemplates(string culture, List<string> problems)
        {
            var keys = objects.Where(p => p.Key.StartsWith("NPCCharacter.", StringComparison.Ordinal) && p.Value.GetAttribute("is_template") == "true" &&
                                          p.Value.GetAttribute("occupation") == "Wanderer" && p.Value.GetAttribute("culture") == "Culture." + culture).Select(p => p.Key);
            return Templates(culture, keys, "Wanderer", problems);
        }

        // Lord equipment rosters of a culture for one sex, as the installed equipment selection flags them: IsLordTemplate, IsFemaleTemplate
        // for women only, never a child or teenager roster. civilian: rosters with a civilian set, else rosters with a battle set. Every item
        // must exist, fit its slot and be usable by that sex; a roster that fails is excluded.
        public List<string> LordEquipment(string culture, bool female, bool civilian)
        {
            var result = new List<string>();
            foreach (var key in Keys("EquipmentRoster.").Select(k => "EquipmentRoster." + k))
            {
                var e = Element(key);
                var flags = e.SelectSingleNode("Flags") as XmlElement;
                if (e.GetAttribute("culture") != "Culture." + culture || flags == null || flags.GetAttribute("IsLordTemplate") != "true" ||
                    (flags.GetAttribute("IsFemaleTemplate") == "true") != female || flags.GetAttribute("IsChildEquipmentTemplate") == "true" ||
                    flags.GetAttribute("IsTeenagerEquipmentTemplate") == "true") continue;
                var sets = e.ChildNodes.OfType<XmlElement>().Where(s => s.Name == "EquipmentSet" && Kind(s) == (civilian ? "civilian" : "battle")).ToList();
                if (sets.Count == 0) continue;
                var p = sets.SelectMany(s => ItemProblems(key, s, female)).ToList();
                if (p.Count > 0) { excluded[key] = String.Join("; ", p); continue; }
                result.Add(key.Substring("EquipmentRoster.".Length));
            }
            return result;
        }

        // A culture's noble child (teenager false) or teenager civilian rosters for one sex, as the installed equipment selection flags them
        // (IsLordTemplate with IsChildEquipmentTemplate or IsTeenagerEquipmentTemplate): the four rosters the engine's random child generator
        // dresses a culture's children from. clean: every item must exist, fit its slot and be usable by that sex, and a roster that fails is
        // excluded; otherwise every flagged roster (the engine's generator uses them as installed).
        public List<string> ChildEquipment(string culture, bool female, bool teenager, bool clean = true)
        {
            var result = new List<string>();
            foreach (var key in Keys("EquipmentRoster.").Select(k => "EquipmentRoster." + k))
            {
                var e = Element(key);
                var flags = e.SelectSingleNode("Flags") as XmlElement;
                if (e.GetAttribute("culture") != "Culture." + culture || flags == null || flags.GetAttribute("IsLordTemplate") != "true" ||
                    (flags.GetAttribute("IsFemaleTemplate") == "true") != female || flags.GetAttribute(teenager ? "IsTeenagerEquipmentTemplate" : "IsChildEquipmentTemplate") != "true") continue;
                var sets = e.ChildNodes.OfType<XmlElement>().Where(s => s.Name == "EquipmentSet" && Kind(s) == "civilian").ToList();
                if (sets.Count == 0) continue;
                var p = clean ? sets.SelectMany(s => ItemProblems(key, s, female)).ToList() : new List<string>();
                if (p.Count > 0) { excluded[key] = String.Join("; ", p); continue; }
                result.Add(key.Substring("EquipmentRoster.".Length));
            }
            return result;
        }

        // The culture's installed name strings ("{=id}Name"), for synthesized relatives and notables.
        public List<string> Names(string culture, bool female)
        {
            var list = Culture(culture) == null ? null : Culture(culture).SelectSingleNode(female ? "female_names" : "male_names");
            return list == null ? new List<string>() : list.ChildNodes.OfType<XmlElement>().Select(e => e.GetAttribute("name")).Where(n => n.Length > 0).ToList();
        }

        // Every reference a character needs: a body, its skill set, battle (and, for a hero, civilian) equipment whose items exist, fit
        // their slots and suit the wearer's sex (female: the character's own is_female unless given), its upgrade targets and upgrade item
        // category. equipmentOverride: equipment roster ids replacing the character's own (battle, civilian).
        public List<string> CharacterProblems(string key, bool hero, bool? female = null, string[] equipmentOverride = null)
        {
            var problems = new List<string>();
            var e = Element(key);
            if (e == null) { problems.Add(key + " does not exist"); return problems; }
            bool woman = female ?? e.GetAttribute("is_female") == "true";
            string culture = e.GetAttribute("culture");
            if (culture.Length > 0 && !Exists(culture)) problems.Add(key + " culture " + culture + " does not exist");
            var face = e.SelectSingleNode("face") as XmlElement;
            var faceTemplate = face == null ? null : face.SelectSingleNode("face_key_template") as XmlElement;
            if (faceTemplate != null) { if (!Exists(faceTemplate.GetAttribute("value"))) problems.Add(key + " face key template " + faceTemplate.GetAttribute("value") + " does not exist"); }
            else if (face == null || face.SelectSingleNode("BodyProperties[@key]") == null) problems.Add(key + " has no body (no face key template or body key)");
            string skills = e.GetAttribute("skill_template");
            if (skills.Length > 0 && !Exists(skills)) problems.Add(key + " skill template " + skills + " does not exist");
            string requires = e.GetAttribute("upgrade_requires");
            if (requires.Length > 0 && !Exists(requires)) problems.Add(key + " upgrade item category " + requires + " does not exist");
            foreach (var target in Targets(e)) if (!Exists(target)) problems.Add(key + " upgrade target " + target + " does not exist");
            bool battle = false, civilian = false;
            var references = new List<KeyValuePair<string, string>>();   // roster id, wanted kind
            // A null override keeps the character's own equipment of that kind (a boy's battle equipment under his child civilian roster).
            bool overrideBattle = equipmentOverride != null && equipmentOverride[0] != null, overrideCivilian = equipmentOverride != null && equipmentOverride[1] != null;
            if (overrideBattle) references.Add(new KeyValuePair<string, string>(equipmentOverride[0], "battle"));
            if (overrideCivilian) references.Add(new KeyValuePair<string, string>(equipmentOverride[1], "civilian"));
            var equipments = e.SelectSingleNode("Equipments");
            if (equipments != null)
                foreach (var child in equipments.ChildNodes.OfType<XmlElement>())
                {
                    string kind = Kind(child);
                    bool overridden = kind == "battle" ? overrideBattle : kind == "civilian" ? overrideCivilian : overrideBattle && overrideCivilian;
                    if (child.Name == "EquipmentSet") { if (!overridden) references.Add(new KeyValuePair<string, string>(child.GetAttribute("id"), kind)); }
                    else if (child.Name == "EquipmentRoster") { if (!overridden) { if (kind == "civilian") civilian = true; else if (kind == "battle") battle = true; problems.AddRange(ItemProblems(key, child, woman)); } }
                    else if (child.Name == "equipment") problems.AddRange(ItemProblems(key, child, woman));
                }
            // CharacterObject.AddEquipmentRoster takes the roster's sets of the referenced equipment type (installed IL).
            foreach (var reference in references)
            {
                string roster = "EquipmentRoster." + reference.Key;
                var r = Element(roster);
                if (r == null) { problems.Add(key + " equipment set " + roster + " does not exist"); continue; }
                foreach (var set in r.ChildNodes.OfType<XmlElement>().Where(s => s.Name == "EquipmentSet" && Kind(s) == reference.Value))
                {
                    if (reference.Value == "civilian") civilian = true; else if (reference.Value == "battle") battle = true;
                    problems.AddRange(ItemProblems(roster, set, woman));
                }
            }
            if (!battle) problems.Add(key + " has no battle equipment");
            if (hero && !civilian) problems.Add(key + " has no civilian equipment");
            return problems;
        }

        // The culture's troop roots and everything they reach through upgrade targets and party template stacks (and the ship hulls of
        // those templates), checked for cycles, missing targets and unresolvable equipment. required: roots a culture must have. For a
        // bandit culture, the default party templates of its installed bandit clans (BanditSpawnCampaignBehavior spawns from them) too.
        public CultureReport Troops(string culture, IEnumerable<string> required, List<string> problems)
        {
            var report = new CultureReport { Culture = culture };
            var c = Culture(culture);
            if (c == null) { problems.Add("culture " + culture + " does not exist"); return report; }
            report.Bandit = c.GetAttribute("is_bandit") == "true";
            var characters = new SortedSet<string>(StringComparer.Ordinal);
            var templates = new List<KeyValuePair<string, string>>();
            foreach (var root in characterRoots)
                foreach (var value in Values(c, root)) { report.Roots.Add(root + "=" + value); characters.Add(value); }
            foreach (var root in required) if (!Values(c, root).Any()) problems.Add("culture " + culture + " has no " + root);
            foreach (var root in partyTemplateRoots) foreach (var value in Values(c, root)) templates.Add(new KeyValuePair<string, string>(root, value));
            if (report.Bandit)
                foreach (var clan in Keys("Faction.").Select(k => Element("Faction." + k)).Where(f => f.GetAttribute("is_bandit") == "true" && f.GetAttribute("culture") == "Culture." + culture))
                    if (clan.GetAttribute("default_party_template").Length > 0) templates.Add(new KeyValuePair<string, string>("clan " + clan.GetAttribute("id") + " default_party_template", clan.GetAttribute("default_party_template")));
            foreach (var t in templates)
            {
                report.Roots.Add(t.Key + "=" + t.Value); report.PartyTemplates.Add(t.Value);
                var template = Element(t.Value);
                if (template == null) { problems.Add("culture " + culture + " " + t.Key + " " + t.Value + " does not exist"); continue; }
                foreach (var stack in template.SelectNodes("stacks/PartyTemplateStack").OfType<XmlElement>()) characters.Add(stack.GetAttribute("troop"));
                // PartyTemplateObject.Deserialize reads ship_hulls/ShipTemplateStack (installed IL).
                foreach (var stack in template.SelectNodes("ship_hulls/ShipTemplateStack").OfType<XmlElement>())
                {
                    string hull = Typed("ShipHull", stack.GetAttribute("id"));
                    report.Ships.Add(hull);
                    if (!Exists(hull)) problems.Add(t.Value + " ship hull " + hull + " does not exist");
                }
            }
            foreach (var root in shipRoots)
                foreach (var value in Values(c, root)) { report.Roots.Add(root + "=" + value); report.Ships.Add(value); if (!Exists(value)) problems.Add("culture " + culture + " ship hull " + value + " does not exist"); }
            // Upgrade graph: depth-first with colours; grey on the stack, black when done.
            var colour = new Dictionary<string, int>(StringComparer.Ordinal);
            var depth = new Dictionary<string, int>(StringComparer.Ordinal);
            Func<string, string, int> visit = null;
            visit = (key, from) =>
            {
                int state;
                if (colour.TryGetValue(key, out state)) { if (state == 1) problems.Add("upgrade cycle through " + key + " (from " + from + ")"); return state == 2 ? depth[key] : 0; }
                var e = Element(key);
                if (e == null) { problems.Add(key + " (from " + from + ") does not exist"); colour[key] = 2; depth[key] = 0; return 0; }
                colour[key] = 1;
                problems.AddRange(CharacterProblems(key, e.GetAttribute("is_hero") == "true"));
                int deepest = 0;
                foreach (var target in Targets(e)) if (Exists(target)) deepest = Math.Max(deepest, 1 + visit(target, key));
                colour[key] = 2; depth[key] = deepest;
                return deepest;
            };
            foreach (var key in characters.ToList()) report.MaxUpgradeDepth = Math.Max(report.MaxUpgradeDepth, visit(key, culture));
            report.Troops = colour.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            return report;
        }

        private List<string> Templates(string culture, IEnumerable<string> keys, string occupation, List<string> problems)
        {
            var result = new List<string>();
            foreach (var key in keys.Distinct().OrderBy(k => k, StringComparer.Ordinal))
            {
                var e = Element(key);
                if (e == null) { excluded[key] = "listed by culture " + culture + " but does not exist"; continue; }
                if (e.GetAttribute("is_template") != "true" || e.GetAttribute("occupation") != occupation) { excluded[key] = "not an is_template " + occupation; continue; }
                var p = CharacterProblems(key, true);
                if (p.Count > 0) { excluded[key] = String.Join("; ", p); continue; }
                result.Add(key.Substring("NPCCharacter.".Length));
            }
            return result;
        }

        // Items of one equipment set: each must exist (Equipment.DeserializeNode takes the part after '.' or the bare id), fit its slot
        // (Equipment.IsItemFitsToSlot) and be usable by the wearer's sex (ItemFlags NotUsableByFemale / NotUsableByMale).
        private IEnumerable<string> ItemProblems(string owner, XmlElement set, bool female)
        {
            foreach (var item in set.ChildNodes.OfType<XmlElement>().Where(x => x.Name == "Equipment" || x.Name == "equipment").Concat(set.Name == "equipment" ? new[] { set } : new XmlElement[0]))
            {
                string id = item.GetAttribute("id");
                if (id.Length == 0) continue;
                var e = Element(Typed("Item", id));
                if (e == null) { if (!Exists(Typed("Item", id))) yield return owner + " item " + id + " does not exist"; continue; }
                var flags = e.SelectSingleNode("Flags") as XmlElement;
                if (flags != null && flags.GetAttribute(female ? "NotUsableByFemale" : "NotUsableByMale") == "true") yield return owner + " item " + id + " is not usable by a " + (female ? "woman" : "man");
                string slot = item.GetAttribute("slot");
                if (!Fits(e, slot)) yield return owner + " item " + id + " (" + (e.Name == "CraftedItem" ? "crafted" : e.GetAttribute("Type")) + ") does not fit slot " + slot;
            }
        }

        // Equipment.IsItemFitsToSlot (installed IL): weapons, shields, ammunition and banners in Item0-3 (or the extra weapon slot), each
        // armour type, cape, horse and harness in its own slot. Slots the engine renames (GetEquipmentIndexFromOldEquipmentIndexName) and
        // item types it does not place are not judged here.
        private static bool Fits(XmlElement item, string slot)
        {
            int index;
            switch (slot)
            {
                case "Item0": case "Weapon0": index = 0; break;
                case "Item1": case "Weapon1": index = 1; break;
                case "Item2": case "Weapon2": index = 2; break;
                case "Item3": case "Weapon3": index = 3; break;
                case "ExtraWeaponSlot": index = 4; break;
                case "Head": index = 5; break;
                case "Body": index = 6; break;
                case "Leg": index = 7; break;
                case "Gloves": index = 8; break;
                case "Cape": index = 9; break;
                case "Horse": index = 10; break;
                case "HorseHarness": index = 11; break;
                default: return true;
            }
            if (item.Name == "CraftedItem") return index <= 4;
            switch (item.GetAttribute("Type"))
            {
                case "OneHandedWeapon": case "TwoHandedWeapon": case "Polearm": case "Arrows": case "Bolts": case "SlingStones": case "Shield": case "Bow":
                case "Crossbow": case "Sling": case "Thrown": case "Pistol": case "Musket": case "Bullets": case "Banner": return index <= 4;
                case "HeadArmor": return index == 5;
                case "BodyArmor": return index == 6;
                case "LegArmor": return index == 7;
                case "HandArmor": return index == 8;
                case "Cape": return index == 9;
                case "Horse": return index == 10;
                case "HorseHarness": return index == 11;
                default: return true;
            }
        }

        private IEnumerable<string> Keys(string prefix) { return objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Select(k => k.Substring(prefix.Length)).OrderBy(k => k, StringComparer.Ordinal); }
        private XmlElement Culture(string culture) { return Element("Culture." + culture); }
        private string Occupation(string key) { var e = Element(key); return e == null ? null : e.GetAttribute("occupation"); }
        // An equipment set's kind: civilian (civilian="true" or equipmentType="Civilian"), stealth (equipmentType="Stealth") or battle.
        private static string Kind(XmlElement e)
        {
            if (e.GetAttribute("civilian") == "true" || e.GetAttribute("equipmentType") == "Civilian") return "civilian";
            return e.GetAttribute("equipmentType") == "Stealth" ? "stealth" : "battle";
        }
        private static IEnumerable<string> Targets(XmlElement e) { return e.SelectNodes("upgrade_targets/upgrade_target").OfType<XmlElement>().Select(t => t.GetAttribute("id")); }
        private static string Typed(string type, string value) { return value.Contains(".") ? value : type + "." + value; }

        // A culture member's typed values: an attribute, or the entries of a child list (name or id attribute).
        private static IEnumerable<string> Values(XmlElement culture, string member)
        {
            string attribute = culture.GetAttribute(member);
            if (attribute.Length > 0) return new[] { attribute };
            return ListValues(culture, member);
        }

        private static IEnumerable<string> ListValues(XmlElement culture, string member)
        {
            if (culture == null) return Enumerable.Empty<string>();
            return culture.SelectNodes(member + "/*").OfType<XmlElement>().Select(x => x.HasAttribute("name") ? x.GetAttribute("name") : x.GetAttribute("id")).Where(v => v.Length > 0).ToList();
        }

        private static string TypeOf(string element)
        {
            switch (element)
            {
                case "NPCCharacter": return "NPCCharacter";
                case "Culture": return "Culture";
                case "MBPartyTemplate": return "PartyTemplate";
                case "EquipmentRoster": return "EquipmentRoster";
                case "Item": case "CraftedItem": return "Item";
                case "SkillSet": return "SkillSet";
                case "BodyProperty": return "BodyProperty";
                case "ShipHull": return "ShipHull";
                case "Faction": return "Faction";
                default: return null;
            }
        }

        private static IList<string> Strings(IDictionary<string, object> o, string key)
        {
            object v;
            return o != null && o.TryGetValue(key, out v) && v is System.Collections.IEnumerable && !(v is string)
                ? ((System.Collections.IEnumerable)v).Cast<object>().Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture)).ToList()
                : (IList<string>)new List<string>();
        }
    }
}
