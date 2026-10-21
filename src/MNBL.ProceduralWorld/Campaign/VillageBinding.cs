using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // The village binding (economy.json binding; docs/settlements.md), pure managed: which town or castle each village is bound to. Since
    // map epic 2 (MMR-252) the travel compiler's village quota decides it (campaign.json v2 villageQuota; docs/travel-graph.md "Village
    // quota"): exactly townVillages per town and castleVillages per castle, castle villages included, and this passes the v2 binding through.
    // SettlementFactory binds the campaign's villages with it, and MMR-46's world preview with the same function: a village belongs to its
    // bound settlement's kingdom (the game's MapFaction). The spacing and the two distance limits stay: a village farther from its bound
    // settlement than the preferred limit is reported remote and must stay within the engine's trade limit.
    internal static class VillageBinding
    {
        // A town or castle as the binding sees it.
        public sealed class Fortification { public string Id, Kind, Kingdom; public double X, Y; public int LandGroup; }

        public sealed class Result
        {
            public Dictionary<string, string> BoundOf = new Dictionary<string, string>(StringComparer.Ordinal);   // village -> its bound town or castle
            public SortedDictionary<string, string> Rebound = new SortedDictionary<string, string>(StringComparer.Ordinal);   // always empty: the quota binds
            public SortedDictionary<string, string> ReboundReason = new SortedDictionary<string, string>(StringComparer.Ordinal);
            public HashSet<string> Remote = new HashSet<string>(StringComparer.Ordinal);
            public double Spacing, PreferredLimit, EngineLimit;
        }

        // The binding over records and positions alone (the preview's path): the towns and castles placed at their travel points, with the
        // records' kingdoms, the world width from the travel grid.
        public static Result FromRecords(IDictionary<string, object> economy, IDictionary<string, object> campaign, IDictionary<string, object> travel, List<string> problems)
        {
            var points = Records(travel, "settlements").ToDictionary(s => Text(s, "id"), StringComparer.Ordinal);
            var grid = Obj(travel, "grid");
            double width = Int(grid, "cols") * Double(grid, "spacing");
            var towns = Records(campaign, "towns").OrderBy(t => Text(t, "id"), StringComparer.Ordinal).ToList();
            var castles = Records(campaign, "castles").OrderBy(c => Text(c, "id"), StringComparer.Ordinal).ToList();
            var villages = Records(campaign, "villages").OrderBy(v => Text(v, "id"), StringComparer.Ordinal).ToList();
            var fortifications = new List<Fortification>();
            foreach (var kind in new[] { "town", "castle" })
                foreach (var r in kind == "town" ? towns : castles)
                {
                    IDictionary<string, object> point;
                    if (!points.TryGetValue(Text(r, "id"), out point)) { problems.Add(kind + " " + Text(r, "id") + " has no MMR-28 position"); continue; }
                    var p = Obj(point, "point");
                    fortifications.Add(new Fortification { Id = Text(r, "id"), Kind = kind, Kingdom = Text(r, "kingdom"), X = Double(p, "x"), Y = Double(p, "y"), LandGroup = Int(point, "group") });
                }
            return Bind(economy, towns, castles, villages, points, fortifications, width, problems);
        }

        // towns, castles and villages ordered by id; fortifications in the towns' then the castles' order (as SettlementFactory adds them).
        // Each village keeps its campaign v2 binding (bound, boundKind); a binding to no placed fortification of that kind is a problem.
        public static Result Bind(IDictionary<string, object> economy, IList<IDictionary<string, object>> towns, IList<IDictionary<string, object>> castles, IList<IDictionary<string, object>> villages,
                                  Dictionary<string, IDictionary<string, object>> points, IList<Fortification> fortificationList, double width, List<string> problems)
        {
            var result = new Result();
            Func<double, double, double, double, double> dist = (x1, y1, x2, y2) => { double dx = Math.Abs(x1 - x2), dy = y1 - y2; if (width > 0) dx = Math.Min(dx, width - dx); return Math.Sqrt(dx * dx + dy * dy); };
            var binding = Obj(economy, "binding"); var distance = Obj(binding, "distance");
            var byId = fortificationList.ToDictionary(x => x.Id, StringComparer.Ordinal);
            var townIntents = fortificationList.Where(x => x.Kind == "town").ToList();
            if (townIntents.Count < 2) problems.Add("fewer than two towns: the world has no town spacing");
            result.Spacing = townIntents.Count < 2 ? 0 : townIntents.Average(t => townIntents.Where(u => u != t).Min(u => dist(t.X, t.Y, u.X, u.Y)));
            result.PreferredLimit = result.Spacing * Double(distance, "preferredRatio");
            result.EngineLimit = result.Spacing * Double(distance, "engineTradeRatio");
            if (!(result.PreferredLimit > 0) || !(result.EngineLimit >= result.PreferredLimit)) problems.Add("the binding distance limits are not positive and ordered");
            foreach (var v in villages)
            {
                string id = Text(v, "id"), bound = Text(v, "bound"), kind = Text(v, "boundKind");
                Fortification f;
                if (bound == null || !byId.TryGetValue(bound, out f) || f.Kind != kind) { problems.Add("village " + id + " is bound to " + (kind ?? "?") + " " + (bound ?? "nothing") + ", which is not a placed fortification of that kind (campaign v2 binding)"); continue; }
                result.BoundOf[id] = bound;
                IDictionary<string, object> point;
                if (points.TryGetValue(id, out point))
                {
                    var p = Obj(point, "point");
                    if (dist(Double(p, "x"), Double(p, "y"), f.X, f.Y) > result.PreferredLimit) result.Remote.Add(id);
                }
            }
            return result;
        }

        private static IDictionary<string, object> Obj(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v as IDictionary<string, object> : null; }
        private static string Text(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v as string : null; }
        private static int Int(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 0; }
        private static double Double(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : double.NaN; }
        private static IEnumerable<IDictionary<string, object>> Records(IDictionary<string, object> o, string key)
        {
            object v; return o != null && o.TryGetValue(key, out v) && v is IEnumerable && !(v is string) ? ((IEnumerable)v).Cast<object>().OfType<IDictionary<string, object>>() : Enumerable.Empty<IDictionary<string, object>>();
        }
    }
}
