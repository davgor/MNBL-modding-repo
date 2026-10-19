using System;
using System.Collections.Generic;
using System.Linq;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.Diagnostics;
using MNBL.ProceduralWorld.Surface;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-36: invalid staged campaigns are refused before they register (graphFault=<class>[,<class>], on the first world).
    // A refusal inside the engine's registration callback ends the process (GeneratedGameManager.Traced), so the gate is observed while the
    // first world is live: for each fault class, a fresh registration of the second world's placement gets the fault (GraphFaults) and the
    // real GeneratedCampaignRegistration.Register is called. It must throw CampaignGraphValidator.GraphException with the fault's code before
    // the fixture prefabs, the registry plan or any document load (the stages it reached show it). Corroborating: the object manager and the
    // campaign lists hold no object of that world and the same number of objects of every census type as before (a load would re-merge or
    // add records), the map scene's fixture prefabs are untouched and the refused registration has no registry.
    internal static class GraphGateProbe
    {
        public static Dictionary<string, object> Run(ProbeContext context, GeneratedCampaignRegistration live, List<string> failures)
        {
            var result = new Dictionary<string, object>();
            var rows = new List<object>(); result["faults"] = rows;
            result["liveWorld"] = live.GraphReport == null ? null : live.GraphReport.Record(10);
            if (live.GraphReport == null || !live.GraphReport.Ok) failures.Add("graph gate: the live world has no passing graph validation");
            uint seed = checked((uint)context.Int("secondSeed", 43));
            string prefix = CampaignIdentity.WorldPrefixOfSeed(seed);
            foreach (var fault in context.Str("graphFault", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var row = new Dictionary<string, object> { ["fault"] = fault, ["world"] = prefix }; rows.Add(row);
                try
                {
                    var registration = GeneratedCampaignRegistration.Prepare(seed, context.Str("secondPlacement", ""), context.Str("catalogReceipt", ""),
                        context.Str("culture", "empire"), context.Str("heroTemplate", ""), context.Str("townPrototype", ""), context.Str("villagePrototype", ""));
                    string expected = null;
                    registration.GraphFault = (documents, catalog) => expected = GraphFaults.Apply(fault, prefix, documents, catalog);
                    var before = Count(prefix); var totalsBefore = Totals(); var prefabs = new Dictionary<string, string>(GeneratedMapScene.FixturePrefabs);
                    var stages = new List<string>();
                    Exception refusal = null;
                    try { registration.Register(stage => stages.Add(stage)); }
                    catch (Exception error) { refusal = error; }
                    var after = Count(prefix); var totalsAfter = Totals();
                    var graph = refusal as CampaignGraphValidator.GraphException;
                    row["expectedCode"] = expected; row["stages"] = stages;
                    row["refusal"] = refusal == null ? null : refusal.GetType().Name + ": " + refusal.Message;
                    row["report"] = graph == null ? null : graph.Report.Record(10);
                    row["objectsBefore"] = before; row["objectsAfter"] = after; row["totalsBefore"] = totalsBefore; row["totalsAfter"] = totalsAfter;
                    row["totalsUnchanged"] = totalsBefore.Count == totalsAfter.Count && totalsBefore.All(t => totalsAfter.ContainsKey(t.Key) && totalsAfter[t.Key] == t.Value);
                    row["fixturePrefabsUnchanged"] = prefabs.Count == GeneratedMapScene.FixturePrefabs.Count && prefabs.All(p => GeneratedMapScene.FixturePrefabs.ContainsKey(p.Key) && GeneratedMapScene.FixturePrefabs[p.Key] == p.Value);
                    row["registryCreated"] = registration.Registry != null;
                    row["registrationCompleted"] = registration.Report["registrationCompleted"];
                    if (graph == null) failures.Add("graph gate: fault " + fault + " was not refused by the graph gate (" + (refusal == null ? "registered" : refusal.GetType().Name + ": " + refusal.Message) + ")");
                    else if (!graph.Report.Diagnostics.Any(d => d.Code == expected)) failures.Add("graph gate: fault " + fault + " refused without " + expected + ": " + graph.Report.Summary(5));
                    if (before.Values.Sum() != 0 || after.Values.Sum() != 0 || !(bool)row["totalsUnchanged"]) failures.Add("graph gate: objects of " + prefix + " are registered around the refused fault " + fault + ", or the object counts changed");
                    if (!(bool)row["fixturePrefabsUnchanged"] || registration.Registry != null || (bool)registration.Report["registrationCompleted"] || stages.Any(s => s.StartsWith("generated_xml_load", StringComparison.Ordinal)))
                        failures.Add("graph gate: the refused fault " + fault + " changed engine state (" + String.Join(", ", stages) + ")");
                }
                catch (Exception error) { row["error"] = error.ToString(); failures.Add("graph gate: fault " + fault + ": " + error.Message); }
            }
            return result;
        }

        // Objects of the world in the object manager (every census type) and the campaign lists.
        private static Dictionary<string, int> Count(string prefix)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var store = new MBObjectStore(StoreStage.NewGame);
            foreach (var type in CampaignObjectRegistry.CensusTypes)
                counts["manager " + type] = store.Objects(type).Count(o => o.Key.Contains(prefix));
            foreach (var list in new CampaignLists().Lists())
                counts["campaign " + list.Key] = list.Value == null ? 0 : list.Value.Count(o => o.Key != null && o.Key.Contains(prefix));
            return counts;
        }

        // Every object of every census type in the object manager and the campaign lists.
        private static Dictionary<string, int> Totals()
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var store = new MBObjectStore(StoreStage.NewGame);
            foreach (var type in CampaignObjectRegistry.CensusTypes) counts["manager " + type] = store.Objects(type).Count();
            foreach (var list in new CampaignLists().Lists()) counts["campaign " + list.Key] = list.Value == null ? 0 : list.Value.Count;
            return counts;
        }
    }
}
