using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-33's village binding (economy.json binding; docs/settlements.md), pure managed: which town or castle each village is bound to, from the
    // MMR-27 records, the MMR-28 travel positions and land groups, and the economy policy. SettlementFactory binds the campaign's villages with
    // it, and MMR-46's world preview with the same function: a village belongs to its bound settlement's kingdom (the game's MapFaction), and
    // the binding moves some villages away from the compiler's town (run 43992 found 3 of world A's in another kingdom).
    internal static class VillageBinding
    {
        // A town or castle as the binding sees it.
        public sealed class Fortification { public string Id, Kind, Kingdom; public double X, Y; public int LandGroup; }

        public sealed class Result
        {
            public Dictionary<string, string> BoundOf = new Dictionary<string, string>(StringComparer.Ordinal);   // village -> its bound town or castle
            public SortedDictionary<string, string> Rebound = new SortedDictionary<string, string>(StringComparer.Ordinal);
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
        // Binding: (1) the compiler's town; (2) distance; (3) castles from their garrison town; (4) castles from over-full towns of their kingdom.
        public static Result Bind(IDictionary<string, object> economy, IList<IDictionary<string, object>> towns, IList<IDictionary<string, object>> castles, IList<IDictionary<string, object>> villages,
                                  Dictionary<string, IDictionary<string, object>> points, IList<Fortification> fortificationList, double width, List<string> problems)
        {
            var result = new Result();
            Func<double, double, double, double, double> dist = (x1, y1, x2, y2) => { double dx = Math.Abs(x1 - x2), dy = y1 - y2; if (width > 0) dx = Math.Min(dx, width - dx); return Math.Sqrt(dx * dx + dy * dy); };
            var binding = Obj(economy, "binding"); var distance = Obj(binding, "distance");
            int castleVillages = Int(binding, "castleVillages"), townMinimum = Int(binding, "townMinimum"), townMaximum = Int(binding, "townMaximum");
            var boundOf = villages.ToDictionary(v => Text(v, "id"), v => Text(v, "bound"), StringComparer.Ordinal);
            var byId = fortificationList.ToDictionary(x => x.Id, StringComparer.Ordinal);
            var townIntents = fortificationList.Where(x => x.Kind == "town").ToList();
            var fortifications = fortificationList.OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
            if (townIntents.Count < 2) problems.Add("fewer than two towns: the world has no town spacing");
            result.Spacing = townIntents.Count < 2 ? 0 : townIntents.Average(t => townIntents.Where(u => u != t).Min(u => dist(t.X, t.Y, u.X, u.Y)));
            result.PreferredLimit = result.Spacing * Double(distance, "preferredRatio");
            result.EngineLimit = result.Spacing * Double(distance, "engineTradeRatio");
            if (!(result.PreferredLimit > 0) || !(result.EngineLimit >= result.PreferredLimit)) problems.Add("the binding distance limits are not positive and ordered");
            Func<string, IDictionary<string, object>> pointOf = v => { IDictionary<string, object> p; return points.TryGetValue(v, out p) ? Obj(p, "point") : null; };
            Func<string, Fortification, double> villageDistance = (v, f) => { var p = pointOf(v); return p == null ? double.NaN : dist(Double(p, "x"), Double(p, "y"), f.X, f.Y); };
            Func<string, int> groupOf = v => points.ContainsKey(v) ? Int(points[v], "group") : -1;
            var remote = result.Remote;
            Action<string, string, string> rebind = (v, to, reason) => { boundOf[v] = to; result.Rebound[v] = to; result.ReboundReason[v] = reason; };
            Func<string, int> countOf = f => boundOf.Values.Count(b => b == f);
            Func<Fortification, bool> room = f => countOf(f.Id) < (f.Kind == "town" ? townMaximum : castleVillages);
            // (2) A village beyond the preferred limit: the nearest town or castle of its land group within it with room (its own
            // kingdom first), else without room; with none, the nearest of its land group with room within the engine limit, else the
            // nearest (remote; validated against the engine limit).
            foreach (var v in villages.Select(x => Text(x, "id")))
            {
                Fortification current;
                if (!byId.TryGetValue(boundOf[v] ?? "", out current) || !(villageDistance(v, current) > result.PreferredLimit)) continue;
                var group = fortifications.Where(f => f.LandGroup == groupOf(v) && f != current).OrderBy(f => villageDistance(v, f)).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
                var within = group.Where(f => villageDistance(v, f) <= result.PreferredLimit).ToList();
                var target = within.FirstOrDefault(f => f.Kingdom == current.Kingdom && room(f)) ?? within.FirstOrDefault(room) ??
                             within.FirstOrDefault(f => f.Kingdom == current.Kingdom) ?? within.FirstOrDefault();
                if (target == null)
                {
                    remote.Add(v);
                    var nearest = group.Concat(new[] { current }).OrderBy(f => villageDistance(v, f)).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
                    target = nearest.FirstOrDefault(f => (f == current || room(f)) && villageDistance(v, f) <= result.EngineLimit) ?? nearest.First();
                }
                if (target.Id != current.Id) rebind(v, target.Id, "distance");
            }
            Func<Fortification, IEnumerable<string>, string> nearestFor = (castle, from) => from.Where(v => groupOf(v) == castle.LandGroup && !remote.Contains(v) && villageDistance(v, castle) <= result.PreferredLimit)
                .OrderBy(v => villageDistance(v, castle)).ThenBy(v => v, StringComparer.Ordinal).FirstOrDefault();
            // (3) Each castle of a town, in id order, takes its nearest remaining village of that town in turn.
            foreach (var t in towns)
            {
                string tid = Text(t, "id");
                var takers = castles.Where(c => Text(c, "garrisonTown") == tid).Select(c => Text(c, "id")).Where(byId.ContainsKey).ToList();
                bool progress = true;
                while (progress && countOf(tid) > townMinimum)
                {
                    progress = false;
                    foreach (var castle in takers)
                    {
                        if (countOf(tid) <= townMinimum || countOf(castle) >= castleVillages) continue;
                        var nearest = nearestFor(byId[castle], boundOf.Where(x => x.Value == tid).Select(x => x.Key));
                        if (nearest == null) continue;
                        rebind(nearest, castle, "castle"); progress = true;
                    }
                }
            }
            // (4) A castle still short takes, from the towns of its kingdom with more than townMinimum, a village within the limit: from
            // the town with the most villages first, then the nearest.
            foreach (var castle in fortifications.Where(f => f.Kind == "castle"))
                while (countOf(castle.Id) < castleVillages)
                {
                    var donors = townIntents.Where(t => t.Kingdom == castle.Kingdom && countOf(t.Id) > townMinimum).ToDictionary(t => t.Id, t => countOf(t.Id), StringComparer.Ordinal);
                    var nearest = boundOf.Where(x => donors.ContainsKey(x.Value) && groupOf(x.Key) == castle.LandGroup && !remote.Contains(x.Key) && villageDistance(x.Key, castle) <= result.PreferredLimit)
                        .OrderByDescending(x => donors[x.Value]).ThenBy(x => villageDistance(x.Key, castle)).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key).FirstOrDefault();
                    if (nearest == null) break;
                    rebind(nearest, castle.Id, "balance");
                }
            // (5) Evening out, only where (1)-(4) leave a town or castle outside the policy's limits, so a world that needs none (world A)
            // keeps its binding byte for byte. Seed 73's world left towns with 6-8 villages and towns and castles with none.
            //   (a) A town above townMaximum or a castle above castleVillages (step (2) rebinds without counting) gives its villages, the
            //       nearest move first, to a town or castle of the village's land group that has room: a village that is not remote first,
            //       then within the preferred limit, then one without a village, then one of its own kingdom (seed 66: a castle with 4).
            //   (b) A town or castle below its floor (one village; townMinimum for a town the compiler gave at least that many) takes the
            //       nearest village from a town above townMinimum or a castle above one village: within the preferred limit first, its own
            //       kingdom first, then the fullest donor.
            //   A move beyond the preferred limit stays within the engine trade limit and makes the village remote, as (2) does.
            Func<Fortification, int> cap = f => f.Kind == "town" ? townMaximum : castleVillages;
            var source = villages.GroupBy(v => Text(v, "bound") ?? "").ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            Func<Fortification, int> floor = f => f.Kind == "town" && source.ContainsKey(f.Id) && source[f.Id] >= townMinimum ? townMinimum : 1;
            Func<Fortification, int> donorFloor = f => f.Kind == "town" ? townMinimum : 1;
            Action<string, Fortification> even = (v, to) => { if (villageDistance(v, to) > result.PreferredLimit) remote.Add(v); else remote.Remove(v); rebind(v, to.Id, "even"); };
            foreach (var over in fortifications)
                while (countOf(over.Id) > cap(over))
                {
                    var move = boundOf.Where(x => x.Value == over.Id).ToList()
                        .SelectMany(x => fortifications.Where(f => f != over && f.LandGroup == groupOf(x.Key) && countOf(f.Id) < cap(f) && villageDistance(x.Key, f) <= result.EngineLimit)
                            .Select(f => new { Village = x.Key, To = f, Distance = villageDistance(x.Key, f) }))
                        .OrderBy(m => remote.Contains(m.Village) ? 1 : 0).ThenBy(m => m.Distance <= result.PreferredLimit ? 0 : 1).ThenBy(m => countOf(m.To.Id) == 0 ? 0 : 1)
                        .ThenBy(m => m.To.Kingdom == over.Kingdom ? 0 : 1).ThenBy(m => m.Distance).ThenBy(m => m.Village, StringComparer.Ordinal).ThenBy(m => m.To.Id, StringComparer.Ordinal).FirstOrDefault();
                    if (move == null) break;
                    even(move.Village, move.To);
                }
            foreach (var short_ in fortifications)
                while (countOf(short_.Id) < floor(short_))
                {
                    var take = boundOf.Where(x => !remote.Contains(x.Key) && byId.ContainsKey(x.Value) && x.Value != short_.Id && countOf(x.Value) > donorFloor(byId[x.Value])
                                                  && groupOf(x.Key) == short_.LandGroup && villageDistance(x.Key, short_) <= result.EngineLimit).ToList()
                        .OrderBy(x => villageDistance(x.Key, short_) <= result.PreferredLimit ? 0 : 1).ThenBy(x => byId[x.Value].Kingdom == short_.Kingdom ? 0 : 1)
                        .ThenByDescending(x => countOf(x.Value)).ThenBy(x => villageDistance(x.Key, short_)).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key).FirstOrDefault();
                    if (take == null) break;
                    even(take, short_);
                }
            result.BoundOf = boundOf;
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
