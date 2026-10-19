using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.ObjectSystem;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-49: the mutable campaign state a save must restore, captured right after the save (time stopped) and right after the load (before
    // time runs), and compared value for value: campaign time; every active party (position and land flag, order target, at sea, men,
    // prisoners, leader, besieged settlement, map event, ships with hull, hit points and owner, the flagship, the anchor's position, target,
    // arrival and transit);
    // every world settlement (owner clan, besieger); the watched heroes (prisoner and captor, relation with the player); the player (gold,
    // fiefs). Floats are written in round-trip form, so equal means bit-equal.
    internal static class WorldSnapshot
    {
        public static Dictionary<string, object> Capture(string worldPrefix, IEnumerable<string> watchedHeroes)
        {
            var snapshot = new Dictionary<string, object> { ["worldPrefix"] = worldPrefix, ["campaignHours"] = R(CampaignTime.Now.ToHours) };
            var parties = new Dictionary<string, object>();
            foreach (var p in MobileParty.All.Where(p => p != null && p.IsActive).OrderBy(p => p.StringId, StringComparer.Ordinal))
            {
                if (parties.ContainsKey(p.StringId)) { parties[p.StringId + "#duplicate" + parties.Count] = "duplicate"; continue; }
                var row = new Dictionary<string, object> {
                    ["position"] = V(p.Position), ["moveTarget"] = V(p.MoveTargetPoint), ["atSea"] = p.IsCurrentlyAtSea,
                    ["men"] = p.MemberRoster == null ? 0 : p.MemberRoster.TotalManCount, ["prisoners"] = p.PrisonRoster == null ? 0 : p.PrisonRoster.TotalManCount,
                    ["leader"] = p.LeaderHero == null ? null : p.LeaderHero.StringId, ["besieged"] = p.BesiegedSettlement == null ? null : p.BesiegedSettlement.StringId,
                    ["mapEvent"] = p.MapEvent != null, ["army"] = p.Army == null || p.Army.LeaderParty == null ? null : p.Army.LeaderParty.StringId };
                if (p.Ships != null && p.Ships.Count > 0)
                {
                    row["ships"] = p.Ships.Select(s => Ship(s) + ":" + (s.Owner == null ? "no owner" : s.Owner.Id)).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    row["flagship"] = p.Party == null || p.Party.FlagShip == null ? null : Ship(p.Party.FlagShip);
                }
                var anchor = p.Anchor;
                if (anchor != null && (anchor.IsValid || anchor.IsMovingToPoint))
                    row["anchor"] = new Dictionary<string, object> { ["position"] = V(anchor.Position), ["target"] = V(anchor.TargetPosition), ["arrivalHours"] = R(anchor.ArrivalTime.ToHours),
                        ["moving"] = anchor.IsMovingToPoint, ["disabled"] = anchor.IsDisabled };
                parties[p.StringId] = row;
            }
            snapshot["parties"] = parties;
            var settlements = new Dictionary<string, object>();
            foreach (var s in Settlement.All.Where(s => s.StringId.StartsWith(worldPrefix, StringComparison.Ordinal)).OrderBy(s => s.StringId, StringComparer.Ordinal))
                settlements[s.StringId] = new Dictionary<string, object> { ["owner"] = s.OwnerClan == null ? null : s.OwnerClan.StringId,
                    ["besieger"] = s.SiegeEvent == null || s.SiegeEvent.BesiegerCamp == null || s.SiegeEvent.BesiegerCamp.LeaderParty == null ? null : s.SiegeEvent.BesiegerCamp.LeaderParty.StringId };
            snapshot["settlements"] = settlements;
            var heroes = new Dictionary<string, object>();
            foreach (var id in (watchedHeroes ?? Enumerable.Empty<string>()).Distinct().OrderBy(x => x, StringComparer.Ordinal))
            {
                var h = Hero.FindFirst(x => x.StringId == id);
                heroes[id] = h == null ? (object)"missing" : new Dictionary<string, object> { ["prisoner"] = h.IsPrisoner, ["captor"] = h.PartyBelongedToAsPrisoner == null ? null : h.PartyBelongedToAsPrisoner.MobileParty != null ? h.PartyBelongedToAsPrisoner.MobileParty.StringId : h.PartyBelongedToAsPrisoner.Settlement != null ? h.PartyBelongedToAsPrisoner.Settlement.StringId : "?",
                    ["relationWithPlayer"] = Hero.MainHero == null ? 0 : Hero.MainHero.GetRelation(h), ["alive"] = h.IsAlive, ["party"] = h.PartyBelongedTo == null ? null : h.PartyBelongedTo.StringId };
            }
            snapshot["heroes"] = heroes;
            snapshot["player"] = new Dictionary<string, object> { ["gold"] = Hero.MainHero == null ? 0 : Hero.MainHero.Gold,
                ["fiefs"] = Clan.PlayerClan == null ? new List<object>() : Clan.PlayerClan.Fiefs.Select(f => (object)f.Settlement.StringId).OrderBy(x => (string)x, StringComparer.Ordinal).ToList() };
            return snapshot;
        }

        // Objects of a world prefix registered now (isolation: none of another world's), by the registry's census: every census type the
        // object manager holds (CampaignObjectRegistry.CensusTypes: cultures, characters, heroes, factions, settlements and their components,
        // parties, workshop types, location templates) and every campaign list.
        public static Dictionary<string, object> Objects(string worldPrefix)
        {
            var store = new CampaignRegistry.MBObjectStore(CampaignRegistry.StoreStage.SavedObjectsRegistered);
            var counts = new Dictionary<string, object>();
            foreach (var type in CampaignRegistry.CampaignObjectRegistry.CensusTypes)
                counts["store:" + type] = store.HasType(type) ? store.Objects(type).Count(o => o.Key != null && o.Key.StartsWith(worldPrefix, StringComparison.Ordinal)) : 0;
            foreach (var list in new CampaignRegistry.CampaignLists().Lists())
                counts["list:" + list.Key] = list.Value == null ? 0 : list.Value.Count(o => o.Key != null && o.Key.StartsWith(worldPrefix, StringComparison.Ordinal));
            return counts;
        }

        public static int Total(Dictionary<string, object> objects) { return objects.Values.Sum(v => Convert.ToInt32(v, CultureInfo.InvariantCulture)); }

        // Every value that differs (path: saved -> loaded), at most limit lines; empty when equal.
        public static List<string> Compare(object saved, object loaded, int limit = 40)
        {
            var differences = new List<string>();
            Walk("", saved, loaded, differences, limit);
            return differences;
        }

        private static void Walk(string path, object a, object b, List<string> differences, int limit)
        {
            if (differences.Count >= limit) return;
            var da = a as IDictionary; var db = b as IDictionary;
            if (da != null && db != null)
            {
                var keys = new HashSet<string>(da.Keys.Cast<object>().Select(k => k.ToString()).Concat(db.Keys.Cast<object>().Select(k => k.ToString())));
                foreach (var key in keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    bool inA = da.Contains(key), inB = db.Contains(key);
                    if (!inA) { differences.Add(path + "/" + key + ": only after the load"); continue; }
                    if (!inB) { differences.Add(path + "/" + key + ": missing after the load"); continue; }
                    Walk(path + "/" + key, da[key], db[key], differences, limit);
                    if (differences.Count >= limit) return;
                }
                return;
            }
            var la = a as IList; var lb = b as IList;
            if (la != null && lb != null && !(a is string))
            {
                if (la.Count != lb.Count) { differences.Add(path + ": " + la.Count + " items, " + lb.Count + " after the load"); return; }
                for (int i = 0; i < la.Count; i++) Walk(path + "[" + i + "]", la[i], lb[i], differences, limit);
                return;
            }
            string sa = a == null ? "null" : Convert.ToString(a, CultureInfo.InvariantCulture), sb = b == null ? "null" : Convert.ToString(b, CultureInfo.InvariantCulture);
            if (sa != sb) differences.Add(path + ": " + sa + " -> " + sb);
        }

        private static string R(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }
        private static string R(float v) { return v.ToString("R", CultureInfo.InvariantCulture); }
        private static string Ship(TaleWorlds.CampaignSystem.Naval.Ship s) { return (s.ShipHull == null ? "?" : s.ShipHull.StringId) + ":" + R(s.HitPoints) + ":" + R(s.SailHitPoints); }
        private static string V(CampaignVec2 v) { return v.IsValid() ? R(v.X) + "," + R(v.Y) + (v.IsOnLand ? ",land" : ",water") : "invalid"; }
    }
}
