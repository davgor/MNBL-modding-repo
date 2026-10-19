using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem.Settlements;

namespace MNBL.ProceduralWorld.Missions
{
    // MMR-42: every generated settlement's location scenes as the campaign holds them: the contract locations (center, lord's hall, a port
    // town's port, village_center, hideout_center) against the mission scene contracts, every named scene against the engine's own scene
    // lookup. Shared by the generated_campaign probe (missionScenesProbe) and MMR-52's land_missions probe.
    internal static class LocationSceneAudit
    {
        private static readonly HashSet<string> ContractLocations = new HashSet<string>(new[] { "center", "lordshall", "port", "village_center", "hideout_center" }, StringComparer.Ordinal);

        internal sealed class Result
        {
            public List<object> Settlements = new List<object>();
            public SortedSet<string> EngineMissing = new SortedSet<string>(StringComparer.Ordinal);
            public List<string> Problems = new List<string>();
            public int References;
            public string CatalogueSha256;
        }

        internal static Result Run(string worldId)
        {
            var catalogue = MissionScenes.Installed();
            var result = new Result { CatalogueSha256 = catalogue.Sha256 };
            foreach (var s in Settlement.All.Where(x => x.StringId.StartsWith(worldId + "_", StringComparison.Ordinal) && (x.IsTown || x.IsCastle || x.IsVillage || x.IsHideout)).OrderBy(x => x.StringId, StringComparer.Ordinal))
            {
                string kind = s.IsTown ? "town" : s.IsCastle ? "castle" : s.IsVillage ? "village" : "hideout";
                bool port = (s.IsTown || s.IsVillage) && s.HasPort;
                var locations = new Dictionary<string, string[]>(StringComparer.Ordinal);
                if (s.LocationComplex != null)
                    foreach (var l in s.LocationComplex.GetListOfLocations())
                    {
                        // The scene each wall level opens (GetSceneName: the level's own, else level 0's); GetSceneCount counts only the
                        // non-empty names, so it cannot bound the levels.
                        var names = new string[4];
                        for (int level = 0; level < 4; level++) { string n = null; try { n = l.GetSceneName(level); } catch (Exception) { n = null; } names[level] = String.IsNullOrEmpty(n) ? null : n; }
                        locations[l.StringId] = names;
                    }
                var contract = locations.Where(l => ContractLocations.Contains(l.Key) && (l.Key != "port" || (kind == "town" && port))).ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal);
                var problems = catalogue.LocationProblems(kind, port, contract);
                foreach (var r in MissionScenes.References(locations))
                {
                    string path; result.References++;
                    if (!TaleWorlds.Engine.Utilities.TryGetFullFilePathOfScene(r.Scene, out path) || String.IsNullOrEmpty(path)) result.EngineMissing.Add(r.Scene);
                }
                result.Settlements.Add(new Dictionary<string, object> { ["id"] = s.StringId, ["kind"] = kind, ["port"] = port, ["locations"] = locations.ToDictionary(l => l.Key, l => (object)l.Value),
                    ["contractProblems"] = problems, ["references"] = catalogue.ReferenceRecord(contract) });
                if (problems.Count > 0) result.Problems.Add(s.StringId + ": " + problems[0]);
            }
            return result;
        }
    }
}
