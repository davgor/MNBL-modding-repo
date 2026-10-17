using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // The typed references of a campaign XML record, as the installed loaders read them (v1.4.8.119303 / War Sails v1.2.8.119303). Pure
    // managed; shared by GeneratedCampaignDataset (the fixture's template closure) and CampaignGraphValidator (MMR-36, the whole world's).
    //   * an attribute value "Type.id" is a reference (top-level, component or nested);
    //   * some untyped attributes name an object of a known type (an EquipmentSet's id, a Building's id, mission_ship, ...);
    //   * a reference the installed loader tolerates when missing is written "?<installed contract>|Type.id": optional.
    // A malformed value (a typed-looking reference that does not parse, or an untyped value where a typed one is required) is reported to
    // the caller, who decides whether it fails.
    internal static class ReferenceSchema
    {
        public static readonly Regex Typed = new Regex(@"^([A-Za-z][A-Za-z0-9_]*)\.([A-Za-z0-9_-]+)$", RegexOptions.CultureInvariant);
        public static readonly Regex Identifier = new Regex(@"^[a-zA-Z0-9_-]+$", RegexOptions.CultureInvariant);
        private static readonly string[] TypedAttributes = { "culture", "faction", "owner", "super_faction", "bound", "village_type", "complex_template", "skill_template", "troop" };
        private static readonly string[] Supported = { "NPCCharacter", "Hero", "Culture", "Kingdom", "Faction", "Settlement", "Item", "EquipmentRoster",
            "WorkshopType", "LocationComplexTemplate", "BodyProperty", "SkillSet", "ShipHull", "ShipSlot", "ShipUpgradePiece",
            "ShipPhysicsReference", "MissionShip", "CraftingPiece", "CraftingTemplate", "WeaponDescription", "Monster",
            "ItemCategory", "Skeleton", "SkeletonScale", "BannerIcon", "MusicInstrument", "MusicTrack", "ItemModifier", "ItemModifierGroup", "SiegeEngineType" };

        // The object type of a record element, or null for an element without an explicit schema.
        public static string ElementType(string element)
        {
            if (element == "MBPartyTemplate") return "PartyTemplate";
            if (element == "CraftedItem") return "Item";
            return Supported.Contains(element) ? element : null;
        }

        public static IEnumerable<string> Dependencies(XmlElement root, Action<string> malformed)
        {
            return References(root, malformed).Select(r => r.Key);
        }

        // Each reference with the property path it is read from, relative to the record ("Components/Village/@bound").
        public static IEnumerable<KeyValuePair<string, string>> References(XmlElement root, Action<string> malformed)
        {
            foreach (XmlElement e in root.SelectNodes("descendant-or-self::*"))
                foreach (XmlAttribute a in e.Attributes)
                {
                    string path = PathOf(root, e) + "@" + a.Name;
                    if (e == root && a.Name == "id") continue;
                    string value = a.Value;
                    var match = Typed.Match(value);
                    if (match.Success) { yield return new KeyValuePair<string, string>(value, path); continue; }
                    if (Regex.IsMatch(value, @"^[A-Z][A-Za-z0-9_]*\.", RegexOptions.CultureInvariant))
                        malformed("Malformed typed reference " + value + " at " + root.Name + "/" + e.Name + "/@" + a.Name);
                    else if (TypedAttributes.Contains(a.Name) && value.Length != 0)
                        malformed("Expected a typed reference at " + root.Name + "/" + e.Name + "/@" + a.Name + ": " + value);
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
                        if (e.Attributes[0] != a) malformed("Workshop input_item must be first: installed loader uses the first attribute value.");
                        prefix = "ItemCategory";
                    }
                    if (a.Name == "default_character_creation_body_property") prefix = "BodyProperty";
                    if (prefix != null && value.Length > 0)
                    {
                        if (!Identifier.IsMatch(value)) malformed("Invalid object ID: " + value);
                        yield return new KeyValuePair<string, string>(prefix + "." + value, path);
                    }
                }
        }

        private static string PathOf(XmlElement root, XmlElement e)
        {
            var parts = new List<string>();
            for (XmlNode n = e; n != null && n != root; n = n.ParentNode) parts.Add(n.Name);
            parts.Reverse();
            return parts.Count == 0 ? "" : String.Join("/", parts) + "/";
        }
    }
}
