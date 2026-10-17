using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-36 fault injection for the in-game refusal probe (graphFault=<class>[,<class>]): each class alters a world's staged documents
    // before the graph gate the way a factory defect would, and names the diagnostic code the gate must refuse it with. Pure managed: the
    // offline graph tests apply the same classes to the same fixture datasets.
    internal static class GraphFaults
    {
        public static readonly IList<string> Classes = Array.AsReadOnly(new[] { "unresolved_reference", "unexpected_vanilla", "duplicate_id" });

        // Alters the documents and returns the code the gate must report.
        public static string Apply(string fault, string worldPrefix, List<KeyValuePair<string, XmlDocument>> documents, IDictionary<string, XmlDocument> catalog)
        {
            var settlements = documents.First(d => d.Key == "Settlements").Value;
            switch (fault)
            {
                case "unresolved_reference":
                {
                    // A generated character whose culture names nothing.
                    var characters = documents.First(d => d.Key == "NPCCharacters").Value;
                    var hero = characters.DocumentElement.ChildNodes.OfType<XmlElement>().First(e => e.GetAttribute("id").StartsWith(worldPrefix, StringComparison.Ordinal));
                    hero.SetAttribute("culture", "Culture." + worldPrefix + "missing_culture");
                    return "graph.unresolved_reference";
                }
                case "unexpected_vanilla":
                {
                    // An installed Calradia town staged unchanged.
                    var town = catalog["Settlements"].DocumentElement.ChildNodes.OfType<XmlElement>().First(e => e.SelectSingleNode("Components/Town[not(@is_castle='true')]") != null);
                    settlements.DocumentElement.AppendChild(settlements.ImportNode(town, true));
                    return "graph.unexpected_vanilla";
                }
                case "duplicate_id":
                {
                    // A generated settlement staged twice.
                    var first = settlements.DocumentElement.ChildNodes.OfType<XmlElement>().First(e => e.GetAttribute("id").StartsWith(worldPrefix, StringComparison.Ordinal));
                    settlements.DocumentElement.AppendChild(first.CloneNode(true));
                    return "graph.duplicate_id";
                }
                default: throw new ArgumentException("Unknown graph fault " + fault + "; known: " + String.Join(", ", Classes));
            }
        }
    }
}
