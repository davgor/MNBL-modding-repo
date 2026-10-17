using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Map.DistanceCache;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.ObjectSystem;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-41 in a campaign: the semantic route set (RuntimePlayabilityValidator.RouteSet) from the campaign's own objects, each directed route
    // through the campaign's engine queries, and every settlement-to-settlement route's entrance pair in the registered navigation caches:
    //   path    IMapScene.GetPathDistanceBetweenAIFaces from the start face to the target face, with the navigation type's invalid terrain
    //           types and War Sails' region switch costs (the directed native path query; it goes through the periodic map scene)
    //   cache   the registered cache of the route's navigation type holds the entrance pair, read from a snapshot taken before any query (the
    //           installed GetSettlementToSettlementDistanceWithLandRatio computes and stores a missing pair, so querying the model cannot tell a
    //           cached pair from a live one, and would change the cache). A cached distance is the mean of both directions by construction
    //           (AddClosestEntrancePairBase), so direction is tested by the path queries only.
    // The world: the generated towns, castles and villages at their campaign positions (only towns' ports: a harbour village's port is a
    // drop-off, not a port town), villages' bound settlements, kingdoms with their initial home (capital), fief owners, every clan's capability
    // from the game's fleet rule (GeneratedClanShipOwnershipModel's landlocked clans; without it, a port town in the clan home's land group),
    // the creation cultures' starting points with their nearest town, land groups and basins by the engine's own reachability
    // (MapDistanceModel.PathExistBetweenPoints). Nearest choices are by straight distance in the canonical frame.
    internal static class CampaignPlayability
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // startCultures: null for every creation culture (production); else only these cultures' starts, for a fixture world that cannot replace
        // the other cultures' installed Calradia start points (a world without startup fixtures); the rest are returned in excluded.
        public static PlayabilityWorld FromCampaign(string worldPrefix, ICollection<string> startCultures, List<PlayabilityWorld.Start> excluded)
        {
            var distance = Campaign.Current.Models.MapDistanceModel;
            var w = new PlayabilityWorld { StartCandidates = false, Distance = (a, b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1])) };
            var settlements = Settlement.All.Where(s => s.StringId.StartsWith(worldPrefix + "_", StringComparison.Ordinal) && (s.IsTown || s.IsCastle || s.IsVillage)).OrderBy(s => s.StringId, StringComparer.Ordinal).ToList();
            var landReps = new List<CampaignVec2>(); var seaReps = new List<CampaignVec2>();
            Func<List<CampaignVec2>, CampaignVec2, MobileParty.NavigationType, int> group = (reps, p, type) => {
                for (int i = 0; i < reps.Count; i++) { var r = reps[i]; if (distance.PathExistBetweenPoints(in r, in p, type)) return i; }
                reps.Add(p); return reps.Count - 1; };
            foreach (var s in settlements)
            {
                var owner = s.IsVillage ? (s.Village.Bound == null ? null : s.Village.Bound.OwnerClan) : s.OwnerClan;
                var gate = s.GatePosition;
                var site = new PlayabilityWorld.Site { Id = s.StringId, Kind = s.IsTown ? "town" : s.IsCastle ? "castle" : "village", Kingdom = owner == null || owner.Kingdom == null ? null : owner.Kingdom.StringId,
                    Owner = owner == null ? null : owner.StringId, Bound = s.IsVillage && s.Village.Bound != null ? s.Village.Bound.StringId : null,
                    HasPort = s.IsTown && s.HasPort, Gate = new double[] { gate.X, gate.Y }, Group = group(landReps, gate, MobileParty.NavigationType.Default) };
                if (site.HasPort) { var port = s.PortPosition; site.Port = new double[] { port.X, port.Y }; site.Basin = group(seaReps, port, MobileParty.NavigationType.Naval); }
                w.Sites[s.StringId] = site;
            }
            ICollection<string> landlocked = null;
            try { var model = NavalDLC.NavalDLCManager.Instance.GameModels.ClanShipOwnershipModel as MNBL.ProceduralWorld.CampaignRegistry.GeneratedClanShipOwnershipModel; if (model != null) landlocked = model.Landlocked; }
            catch (Exception) { landlocked = null; }
            foreach (var clan in Clan.All.Where(c => c.StringId.StartsWith(worldPrefix + "_", StringComparison.Ordinal)))
            {
                if (landlocked != null) { w.Clans[clan.StringId] = new PlayabilityWorld.Capability { Naval = !landlocked.Contains(clan.StringId), Reason = landlocked.Contains(clan.StringId) ? "clan " + clan.StringId + " is landlocked (the fleet rule)" : "clan " + clan.StringId + " may hold ships (the fleet rule)" }; continue; }
                var home = clan.HomeSettlement; PlayabilityWorld.Site homeSite;
                if (home == null || !w.Sites.TryGetValue(home.StringId, out homeSite)) { w.Clans[clan.StringId] = new PlayabilityWorld.Capability { Naval = false, Reason = "clan " + clan.StringId + " has no generated home" }; continue; }
                int ports = w.Sites.Values.Count(x => x.HasPort && x.Group == homeSite.Group);
                w.Clans[clan.StringId] = new PlayabilityWorld.Capability { Naval = ports > 0, Reason = ports + " port towns in clan " + clan.StringId + "'s home " + home.StringId + "'s land group (no fleet rule registered)" };
            }
            foreach (var k in Kingdom.All.Where(k => k.StringId.StartsWith(worldPrefix + "_", StringComparison.Ordinal)).OrderBy(k => k.StringId, StringComparer.Ordinal))
                w.Kingdoms.Add(new PlayabilityWorld.Faction { Id = k.StringId, Capital = k.InitialHomeSettlement == null ? null : k.InitialHomeSettlement.StringId, RulingClan = k.RulingClan == null ? null : k.RulingClan.StringId });
            foreach (var culture in MBObjectManager.Instance.GetObjectTypeList<CultureObject>().Where(c => c.IsMainCulture && c.StartingPoint.IsNonZero()).OrderBy(c => c.StringId, StringComparer.Ordinal))
            {
                var p = culture.StartingPoint;
                var town = w.Sites.Values.Where(x => x.Kind == "town").OrderBy(x => w.Distance(x.Gate, new double[] { p.X, p.Y })).ThenBy(x => x.Id, StringComparer.Ordinal).FirstOrDefault();
                if (town == null) continue;
                var start = new PlayabilityWorld.Start { Culture = culture.StringId, Town = town.Id, Point = new double[] { p.X, p.Y } };
                if (startCultures == null || startCultures.Contains(culture.StringId)) w.Starts.Add(start); else excluded.Add(start);
            }
            return w;
        }

        // The registered caches as their installed serialization, parsed (NavigationCacheFile): what the campaign holds now, untouched.
        public static Dictionary<MobileParty.NavigationType, NavigationCacheFile> Snapshot()
        {
            var distance = Campaign.Current.Models.MapDistanceModel;
            FieldInfo field = null;
            for (var t = distance.GetType(); t != null && field == null; t = t.BaseType) field = t.GetField("_navigationCaches", Members);
            var caches = field == null ? null : field.GetValue(distance) as IDictionary;
            var result = new Dictionary<MobileParty.NavigationType, NavigationCacheFile>();
            if (caches == null) return result;
            string scratch = Path.Combine(WorldNavigationCache.Root(), "verify-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                for (int i = 0; i < WorldNavigationCache.Types.Length; i++)
                {
                    var type = WorldNavigationCache.Types[i];
                    var cache = caches.Contains(type) ? caches[type] as SandBoxNavigationCache : null;
                    if (cache == null) continue;
                    string path = Path.Combine(scratch, type + ".bin");
                    cache.Serialize(path);
                    result[type] = NavigationCacheFile.Read(File.ReadAllBytes(path), NavigationCacheStore.TypeValues[i]);
                }
            }
            finally { try { Directory.Delete(scratch, true); } catch (IOException) { } }
            return result;
        }

        public static Dictionary<string, object> Run(string worldPrefix, Action<string> trace, ICollection<string> startCultures)
        {
            var snapshot = Snapshot();   // before any query
            var excluded = new List<PlayabilityWorld.Start>();
            var world = FromCampaign(worldPrefix, startCultures, excluded);
            var routes = RuntimePlayabilityValidator.RouteSet(world);
            var map = Campaign.Current.MapSceneWrapper;
            var distance = Campaign.Current.Models.MapDistanceModel;
            var navigation = Campaign.Current.Models.PartyNavigationModel;
            var rows = new List<Dictionary<string, object>>();
            foreach (var r in routes)
            {
                var row = new Dictionary<string, object> { ["id"] = r.Id, ["check"] = r.Category, ["subject"] = r.Subject, ["category"] = r.Category, ["mover"] = r.Mover, ["type"] = r.Type,
                    ["fromId"] = r.FromId, ["toId"] = r.ToId, ["from"] = r.From, ["to"] = r.To };
                var problems = new List<string>();
                if (r.Refused != null) problems.Add("refused: " + r.Refused);
                else
                {
                    var type = (MobileParty.NavigationType)Enum.Parse(typeof(MobileParty.NavigationType), r.Type);
                    var a = new CampaignVec2(new Vec2((float)r.From[0], (float)r.From[1]), r.FromLand); var b = new CampaignVec2(new Vec2((float)r.To[0], (float)r.To[1]), r.ToLand);
                    var invalid = navigation.GetInvalidTerrainTypesForNavigationType(type);
                    float radius = r.FromLand ? 0.3f : navigation.GetEmbarkDisembarkThresholdDistance(), pathCost = 0;
                    bool reachable = a.Face.IsValid() && b.Face.IsValid() && map.GetPathDistanceBetweenAIFaces(a.Face, b.Face, a.ToVec2(), b.ToVec2(), radius, Campaign.PathFindingMaxCostLimit, out pathCost,
                        invalid, distance.RegionSwitchCostFromLandToSea, distance.RegionSwitchCostFromSeaToLand);
                    row["pathReachable"] = reachable; row["pathCost"] = reachable ? (object)pathCost : null;
                    if (!reachable) problems.Add("no engine path (" + (a.Face.IsValid() ? "" : "no start face; ") + (b.Face.IsValid() ? "" : "no target face; ") + type + ")");
                    else if (pathCost <= 0 && (Math.Abs(r.From[0] - r.To[0]) > 1e-3 || Math.Abs(r.From[1] - r.To[1]) > 1e-3)) problems.Add("a zero path cost between distinct points");
                    Settlement from = Settlement.Find(r.FromId), to = Settlement.Find(r.ToId);
                    if (from != null && to != null && from != to)
                    {
                        NavigationCacheFile cache;
                        string x = r.FromId + ":" + (r.FromLand ? 0 : 1), y = r.ToId + ":" + (r.ToLand ? 0 : 1), pair = String.CompareOrdinal(x, y) <= 0 ? x + "|" + y : y + "|" + x;
                        float cached;
                        if (!snapshot.TryGetValue(type, out cache)) problems.Add("no registered " + type + " cache");
                        else if (!cache.Distances.TryGetValue(pair, out cached)) problems.Add("the registered " + type + " cache lacks the entrance pair " + pair);
                        else
                        {
                            row["cacheDistance"] = cached;
                            if (float.IsNaN(cached) || float.IsInfinity(cached) || cached < 0 || cached >= float.MaxValue / 2) problems.Add("the registered " + type + " cache holds no distance for " + pair + " (" + cached + ")");
                        }
                    }
                }
                row["agrees"] = problems.Count == 0; row["problems"] = problems;
                rows.Add(row);
            }
            trace("campaign_playability:" + rows.Count(x => Equals(x["agrees"], true)) + "/" + rows.Count);
            var report = RuntimePlayabilityValidator.Report(worldPrefix, map is GeneratedMapScene ? ((GeneratedMapScene)map).Scene.GetName() : null, null, null, null, routes, rows, new List<Dictionary<string, object>>(),
                "campaign (IMapScene paths; the registered navigation caches' entrance pairs)", null, null);
            report["world"] = new Dictionary<string, object> { ["sites"] = world.Sites.Count, ["portTowns"] = world.Sites.Values.Count(s => s.HasPort),
                ["kingdoms"] = world.Kingdoms.Select(k => new Dictionary<string, object> { ["id"] = k.Id, ["capital"] = k.Capital, ["rulingClan"] = k.RulingClan, ["naval"] = world.Of(k.RulingClan).Naval, ["reason"] = world.Of(k.RulingClan).Reason }).ToList(),
                ["starts"] = world.Starts.Select(s => new Dictionary<string, object> { ["culture"] = s.Culture, ["town"] = s.Town, ["point"] = s.Point }).ToList(),
                ["startCultures"] = startCultures == null ? null : startCultures.OrderBy(c => c, StringComparer.Ordinal).ToList(),
                ["excludedStarts"] = excluded.Select(s => new Dictionary<string, object> { ["culture"] = s.Culture, ["town"] = s.Town, ["point"] = s.Point }).ToList(),
                ["landGroups"] = world.Sites.Values.Select(s => s.Group).Distinct().Count(), ["basins"] = world.Sites.Values.Where(s => s.HasPort).Select(s => s.Basin).Distinct().Count(),
                ["cachePairs"] = snapshot.ToDictionary(kv => kv.Key.ToString(), kv => (object)kv.Value.Pairs) };
            return report;
        }
    }
}
