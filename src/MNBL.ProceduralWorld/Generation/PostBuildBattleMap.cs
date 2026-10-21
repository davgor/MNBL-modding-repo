using System;
using System.Collections.Generic;
using System.Globalization;

namespace MNBL.ProceduralWorld.Generation
{
    // MMR-262: the activation's PostBuild checks of the battle map's support record (supportReport["battleSceneSelection"], written by
    // GeneratedMapScene.AfterLoad), over the record alone so the offline activation harness runs them. Three shapes reach it:
    //   * Install's report (GeneratedBattleTerrain.Install): status "installed", the catalogue match, the terrain context source, the hashes
    //     and the per-party counts;
    //   * Preflight's refusal (GeneratedBattleTerrain.Preflight): BattleTableCheck.Record plus status "failed" and error;
    //   * Guarded's failure (GeneratedEncounterResolver.Guarded): status "failed" and error only.
    // Every key is read null-safely: a failed record gives one problem naming its error (an indexed read of a key it lacks threw
    // KeyNotFoundException, which hid the error), and Refused says when the loaded battle table is the cause, so the activation names the
    // module as a content conflict, as the loader does.
    internal static class PostBuildBattleMap
    {
        // The problems of the record, and the record PostBuild keeps (null without one). afterLoad: the map's AfterLoad failure
        // (supportReport["afterLoadFailure"]), named when AfterLoad wrote no record.
        public static SortedDictionary<string, object> Check(IDictionary<string, object> battle, object afterLoad, List<string> problems)
        {
            Func<string, object> b = key => { object v; return battle != null && battle.TryGetValue(key, out v) ? v : null; };
            object error = b("error") ?? (battle == null ? afterLoad : null);
            bool installed = Equals(b("status"), "installed");
            if (!installed || !Equals(b("navigationSetsMatchCatalogue"), true))
                problems.Add("the battle map is not installed against the catalogue" + (error == null ? "" : " (" + error + ")"));
            if (installed)
            {
                if (!Convert.ToString(b("terrainContextSource") ?? "", CultureInfo.InvariantCulture).Contains("masks")) problems.Add("the battle map has no terrain context from the world's masks");
                // GeneratedEncounterResolver's record counts the cells where every land party finds one scene (landDeterministic of landCells)
                // and the sea terrain checks that find one naval scene (seaDeterministic of seaTerrainChecks).
                Func<string, long> n = key => b(key) == null ? -1 : Convert.ToInt64(b(key), CultureInfo.InvariantCulture);
                if (n("landDeterministic") != n("landCells") || n("seaDeterministic") != n("seaTerrainChecks") || n("landCells") <= 0)
                    problems.Add("the battle map does not give every party one scene (land " + n("landDeterministic") + " of " + n("landCells") + ", sea " + n("seaDeterministic") + " of " + n("seaTerrainChecks") + ")");
            }
            return battle == null ? null : new SortedDictionary<string, object>(StringComparer.Ordinal) { ["status"] = b("status"), ["error"] = b("error"),
                ["terrainContextSource"] = b("terrainContextSource"), ["sceneTableSha256"] = b("sceneTableSha256"), ["inventorySha256"] = b("inventorySha256"),
                ["catalogueSha256"] = b("catalogueSha256"), ["mapSha256"] = b("sha256"), ["landCells"] = b("landCells"), ["landDeterministic"] = b("landDeterministic"),
                ["seaTerrainChecks"] = b("seaTerrainChecks"), ["seaDeterministic"] = b("seaDeterministic") };
        }

        // A failed record carrying the battle table's coverage loss ("lost", from Preflight's BattleTableCheck.Record): the loaded battle
        // table, not the install, refused the map.
        public static bool Refused(IDictionary<string, object> battle)
        {
            object status;
            return battle != null && battle.TryGetValue("status", out status) && Equals(status, "failed") && battle.ContainsKey("lost");
        }
    }
}
