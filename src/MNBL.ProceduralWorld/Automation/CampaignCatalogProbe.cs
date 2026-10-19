using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;

namespace MNBL.ProceduralWorld.Automation
{
    // Read the same active-module/XSLT merge as Sandbox before trying a custom
    // bootstrap. Does not load any definition into a live object registry.
    internal static class CampaignCatalogProbe
    {
        internal static readonly string[] Sources = {
            "NPCCharacters", "Heroes", "SPCultures", "partyTemplates", "EquipmentRosters",
            "Settlements", "WorkshopTypes", "LocationComplexTemplates", "Items", "SkillSets", "BodyProperties",
            "Monsters", "ItemModifiers", "ItemModifierGroups", "CraftingPieces", "CraftingTemplates", "WeaponDescriptions",
            "Kingdoms", "Factions", "ShipUpgradePieces", "ShipSlots", "ShipHulls",
            "ShipPhysicsReferences", "MissionShips"
        };

        internal static IEnumerator Run(ProbeContext context)
        {
            if (Game.Current != null) throw new InvalidOperationException("Catalog capture requires the initial menu.");
            var records = new List<object>(); context.Outputs["documents"] = records;
            context.Outputs["scope"] = "Installed active-module merged XML inspection only; no registry writes or campaign startup.";
            const string gameType = "Campaign";
            context.Outputs["gameType"] = gameType;
            var folder = Path.Combine(context.Directory, "campaign-catalog-" + context.ProcessId);
            if (Directory.Exists(folder)) throw new IOException("Catalog output already exists.");
            Directory.CreateDirectory(folder);
            context.Outputs["folder"] = folder;
            foreach (string id in Sources)
            {
                context.Stage("campaign_catalog_merge:" + id);
                var doc = MBObjectManager.GetMergedXmlForManaged(id, false, false, gameType);
                if (doc == null || doc.DocumentElement == null || doc.DocumentType != null)
                    throw new InvalidDataException("Merged campaign document unavailable or contains DTD: " + id);
                byte[] bytes = new UTF8Encoding(false).GetBytes(doc.OuterXml);
                string path = Path.Combine(folder, id + ".xml");
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) file.Write(bytes, 0, bytes.Length);
                string hash;
                using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                records.Add(new { id, path, sha256 = hash, root = doc.DocumentElement.Name, childCount = doc.DocumentElement.ChildNodes.Count });
                context.Checkpoint();
                yield return null;
            }
        }
    }
}
