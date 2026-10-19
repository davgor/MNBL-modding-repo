using System;
using System.Collections.Generic;
using System.Linq;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.Missions
{
    // MMR-42: the geographic mismatch guard. An observe-only postfix on DefaultSceneModel.GetBattleSceneForMapPatch (GeneratedPatches world set,
    // applied while a generated campaign runs) records every battle scene the model picks on a generated map against what the generated patch
    // names (GeneratedEncounterResolver.Check): a patch index 0 (no generated battle map: the scene model would silently use a random scene, as
    // with Calradia indexing), an index that does not resolve deterministically, or an opened scene other than the patch's is a mismatch, logged
    // and kept for the probes. It never changes the result. The terrain is the model's own tie-break input (IMapScene.GetTerrainTypeAtPosition
    // at the main party, the GetEnvironmentTerrainTypesCount out value). The last Capacity observations are kept (the model is also called on
    // menu evaluations in development builds).
    internal static class MissionScenePatches
    {
        public const int Capacity = 256;
        private static readonly LinkedList<Dictionary<string, object>> ring = new LinkedList<Dictionary<string, object>>();
        public static int Observed { get; private set; }
        public static int Mismatches { get; private set; }

        public static void Clear() { lock (ring) { ring.Clear(); Observed = 0; Mismatches = 0; } }
        public static List<Dictionary<string, object>> Snapshot() { lock (ring) return ring.ToList(); }
        public static Dictionary<string, object> LastOpening(string scene) { lock (ring) return ring.LastOrDefault(o => Equals(o["opened"], scene)); }

        public static void BattleScenePostfix(MapPatchData mapPatch, bool isNavalEncounter, string __result)
        {
            try
            {
                var map = Campaign.Current == null ? null : Campaign.Current.MapSceneWrapper as GeneratedMapScene;
                if (map == null || MobileParty.MainParty == null) return;
                var position = MobileParty.MainParty.Position;
                var terrain = Campaign.Current.MapSceneWrapper.GetTerrainTypeAtPosition(in position);
                var row = GeneratedBattleTerrain.Resolver().Check(mapPatch.sceneIndex, isNavalEncounter, (int)terrain, __result);
                row["position"] = new[] { position.X, position.Y };
                row["patchCoordinates"] = new[] { mapPatch.normalizedCoordinates.X, mapPatch.normalizedCoordinates.Y };
                row["campaignHours"] = CampaignTime.Now.ToHours;
                Record(row);
            }
            catch (Exception e)
            {
                Record(new Dictionary<string, object> { ["agrees"] = false, ["problem"] = "the mismatch check failed: " + e.GetType().Name + ": " + e.Message, ["opened"] = __result });
            }
        }

        private static void Record(Dictionary<string, object> row)
        {
            lock (ring)
            {
                Observed++;
                if (!Equals(row["agrees"], true)) { Mismatches++; Debug.Print("[MNBL] battle scene mismatch on the generated map: " + row["problem"]); }
                ring.AddLast(row);
                while (ring.Count > Capacity) ring.RemoveFirst();
            }
        }
    }
}
