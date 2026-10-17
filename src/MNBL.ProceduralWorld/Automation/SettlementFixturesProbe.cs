using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using MNBL.ProceduralWorld.CampaignBootstrap;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.ObjectSystem;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-33: the settlement fixtures (a coastal town, a castle, a town village, a castle village and a hideout, all built by
    // SettlementFactory.Element and registered through the generated world's XML path) checked on the live object graph after new game
    // against the Settlements XML the engine deserialized: components, LocationComplex locations and scenes (the town's port scene
    // override), exact centre/gate/port positions, building levels, ownership inverse lists (clan and kingdom), bound and trade-bound
    // villages, village types and hearths, the starting values the installed new-game behaviours derived (food stocks, militia, gold,
    // workshops, garrison), market prices against item base values, every registered village type's food production, and an ownership
    // transfer of the castle (and back). Every check that fails is a failure; nothing is repaired.
    internal static class SettlementFixturesProbe
    {
        private static readonly string[] PriceItems = { "grain", "butter", "cheese", "fish", "iron", "tools", "wine" };

        public static Dictionary<string, object> Run(GeneratedCampaignRegistration registration, List<string> failures)
        {
            var report = new Dictionary<string, object>();
            Action<bool, string> check = (ok, label) => { if (!ok) failures.Add("settlement fixtures: " + label); };
            string world = registration.WorldId;
            try
            {
                report["policies"] = registration.Report.ContainsKey("settlementFixturePolicies") ? registration.Report["settlementFixturePolicies"] : null;
                var xml = registration.Dataset.Documents.Single(d => d.Id == "Settlements").CreateDocument();
                Func<string, XmlElement> element = id => xml.DocumentElement.SelectSingleNode("Settlement[@id='" + id + "']") as XmlElement;
                var town = Settlement.Find(world + "_town_1"); var castle = Settlement.Find(world + "_castle_1");
                var village = Settlement.Find(world + "_village_1"); var castleVillage = Settlement.Find(world + "_village_3");
                var hideout = Settlement.Find(world + "_hideout_1"); var clan = Clan.FindFirst(x => x.StringId == world + "_clan_1");
                var otherClan = Clan.FindFirst(x => x.StringId == world + "_clan_2");
                check(town != null && castle != null && village != null && castleVillage != null && hideout != null && clan != null && otherClan != null, "every fixture settlement and both clans are registered");
                if (town == null || castle == null || village == null || castleVillage == null || hideout == null || clan == null || otherClan == null) { report["error"] = "missing fixtures"; return report; }
                var all = new[] { town, castle, village, castleVillage, hideout };
                // Components.
                check(town.IsTown && !town.IsCastle && town.Town != null, "the town has a town component");
                check(castle.IsCastle && castle.Town != null && castle.Town.IsCastle, "the castle has a castle component");
                check(village.IsVillage && castleVillage.IsVillage, "both villages have village components");
                check(hideout.IsHideout && hideout.Hideout != null, "the hideout has a hideout component");
                // LocationComplex: every fixture has locations with scenes; the coastal town's port location carries the port scene
                // override of its XML (the template creates a port location for every town; only the override is the prototype's).
                var locations = new Dictionary<string, object>();
                foreach (var s in all)
                {
                    var complex = s.LocationComplex;
                    var list = complex == null ? new List<TaleWorlds.CampaignSystem.Settlements.Locations.Location>() : complex.GetListOfLocations().ToList();
                    locations[s.StringId] = list.Select(l => l.StringId + ":" + (l.GetSceneName(s.IsTown || s.IsCastle ? s.Town.GetWallLevel() : 1) ?? "")).ToList();
                    check(complex != null && list.Count > 0, s.StringId + " has a location complex");
                    check(list.Any(l => !String.IsNullOrEmpty(l.GetSceneName(s.IsTown || s.IsCastle ? s.Town.GetWallLevel() : 1))), s.StringId + " has a location with a scene");
                }
                report["locations"] = locations;
                var portXml = element(town.StringId).SelectSingleNode("Locations/Location[@id='port']") as XmlElement;
                var portLive = town.LocationComplex.GetLocationWithId("port");
                string portScene = portLive == null ? null : portLive.GetSceneName(town.Town.GetWallLevel()), portExpected = portXml == null ? null : portXml.GetAttribute("scene_name");
                report["townPortScene"] = new Dictionary<string, object> { ["live"] = portScene, ["xml"] = portExpected };
                check(!String.IsNullOrEmpty(portExpected) && portScene == portExpected, "the coastal town's port location carries its XML port scene (" + portScene + " / " + portExpected + ")");
                check(town.LocationComplex.GetLocationWithId("center") != null && castle.LocationComplex.GetLocationWithId("center") != null &&
                      village.LocationComplex.GetLocationWithId("village_center") != null && hideout.LocationComplex.GetLocationWithId("hideout_center") != null, "the centre locations exist");
                // Positions: exactly the XML's centre, gate (towns and castles; others at the centre) and port.
                Func<XmlElement, string, double?> number = (e, a) => e != null && e.HasAttribute(a) ? double.Parse(e.GetAttribute(a), CultureInfo.InvariantCulture) : (double?)null;
                var positions = new Dictionary<string, object>();
                // MMR-40: a village whose scene entity carries a drop-off is a harbour village to War Sails, which sets its port from the drop-off.
                var dropoffs = new Dictionary<string, Vec2>(StringComparer.Ordinal);
                foreach (var d in ((MNBL.ProceduralWorld.Surface.GeneratedMapScene)Campaign.Current.MapSceneWrapper).Scene.FindEntitiesWithTag("main_map_village_dropoff"))
                    if (d.Parent != null) dropoffs[d.Parent.Name] = d.GlobalPosition.AsVec2;
                report["dropoffVillages"] = dropoffs.Keys.ToList();
                foreach (var s in all)
                {
                    var e = element(s.StringId);
                    double? x = number(e, "posX"), y = number(e, "posY"), gx = number(e, "gate_posX") ?? x, gy = number(e, "gate_posY") ?? y, px = number(e, "port_posX"), py = number(e, "port_posY");
                    positions[s.StringId] = new Dictionary<string, object> {
                        ["position"] = new[] { s.Position.X, s.Position.Y }, ["gate"] = new[] { s.GatePosition.X, s.GatePosition.Y }, ["port"] = s.HasPort ? new[] { s.PortPosition.X, s.PortPosition.Y } : null,
                        ["xml"] = new Dictionary<string, object> { ["position"] = new[] { x, y }, ["gate"] = new[] { gx, gy }, ["port"] = px.HasValue ? new[] { px, py } : null } };
                    Func<float, double?, bool> same = (live, expected) => expected.HasValue && Math.Abs(live - expected.Value) < 1e-3;
                    check(same(s.Position.X, x) && same(s.Position.Y, y), s.StringId + " centre is its XML centre");
                    check(same(s.GatePosition.X, gx) && same(s.GatePosition.Y, gy), s.StringId + " gate is its XML gate");
                    Vec2 dropoff;
                    if (s.IsVillage && dropoffs.TryGetValue(s.StringId, out dropoff)) check(s.HasPort && s.PortPosition.ToVec2().Distance(dropoff) < 1e-3, s.StringId + " port is its scene drop-off (War Sails)");
                    else check(px.HasValue ? s.HasPort && same(s.PortPosition.X, px) && same(s.PortPosition.Y, py) : !s.HasPort, s.StringId + " port is its XML port (or it has none)");
                }
                report["positions"] = positions;
                check(town.HasPort && !castle.HasPort && (!village.HasPort || dropoffs.ContainsKey(village.StringId)) && !castleVillage.HasPort, "only the coastal town (and a village with a scene drop-off) has a port");
                // Building levels: exactly the XML's.
                var buildings = new Dictionary<string, object>();
                foreach (var s in new[] { town, castle })
                {
                    var live = s.Town.Buildings.ToDictionary(b => b.BuildingType.StringId, b => b.CurrentLevel);
                    buildings[s.StringId] = live;
                    var expected = element(s.StringId).SelectNodes("Components/Town/Buildings/Building").OfType<XmlElement>().ToDictionary(b => b.GetAttribute("id"), b => int.Parse(b.GetAttribute("level"), CultureInfo.InvariantCulture));
                    var wrong = expected.Where(b => !live.ContainsKey(b.Key) || live[b.Key] != b.Value).Select(b => b.Key + " " + (live.ContainsKey(b.Key) ? live[b.Key].ToString(CultureInfo.InvariantCulture) : "missing") + " not " + b.Value).ToList();
                    check(expected.Count > 0 && wrong.Count == 0, s.StringId + " buildings are at their XML levels" + (wrong.Count == 0 ? "" : ": " + String.Join(", ", wrong)));
                }
                report["buildings"] = buildings;
                // Ownership and binding inverse lists.
                check(town.OwnerClan == clan && castle.OwnerClan == clan && clan.Settlements.Contains(town) && clan.Settlements.Contains(castle) && clan.Fiefs.Contains(town.Town) && clan.Fiefs.Contains(castle.Town),
                      "the clan owns the town and castle, and its settlement and fief lists hold both");
                check(clan.Villages.Contains(village.Village) && clan.Villages.Contains(castleVillage.Village) && village.OwnerClan == clan && castleVillage.OwnerClan == clan,
                      "the clan's village list holds both villages, which take their owner from their bound settlement");
                check(clan.Kingdom != null && clan.Kingdom.Fiefs.Contains(town.Town) && clan.Kingdom.Fiefs.Contains(castle.Town) && clan.Kingdom.Villages.Contains(village.Village) && clan.Kingdom.Villages.Contains(castleVillage.Village),
                      "the clan's kingdom lists the town, the castle and both villages");
                check(village.Village.Bound == town && castleVillage.Village.Bound == castle && town.BoundVillages.Contains(village.Village) && castle.BoundVillages.Contains(castleVillage.Village),
                      "each village is bound to its town or castle and listed in its bound villages");
                check(castleVillage.Village.TradeBound == town && village.Village.TradeBound == town, "both villages trade with the town (the castle village through the engine's trade-bound assignment)");
                check(village.Village.VillageType.StringId == "wheat_farm" && castleVillage.Village.VillageType.StringId == "cattle_farm" &&
                      Math.Abs(village.Village.Hearth - 300) < 50 && Math.Abs(castleVillage.Village.Hearth - 250) < 50, "village types and hearths are the specified ones");
                // Starting values derived at new game.
                var t = town.Town; var c = castle.Town;
                report["town"] = new Dictionary<string, object> { ["prosperity"] = t.Prosperity, ["foodStocks"] = t.FoodStocks, ["militia"] = town.Militia, ["gold"] = t.Gold,
                    ["workshops"] = t.Workshops.Where(w => w != null && w.WorkshopType != null).Select(w => w.WorkshopType.StringId).ToList(),
                    ["garrison"] = t.GarrisonParty == null ? 0 : t.GarrisonParty.MemberRoster.TotalManCount };
                report["castle"] = new Dictionary<string, object> { ["prosperity"] = c.Prosperity, ["foodStocks"] = c.FoodStocks, ["militia"] = castle.Militia,
                    ["garrison"] = c.GarrisonParty == null ? 0 : c.GarrisonParty.MemberRoster.TotalManCount };
                report["villages"] = new[] { village, castleVillage }.ToDictionary(s => s.StringId, s => (object)new Dictionary<string, object> {
                    ["type"] = s.Village.VillageType.StringId, ["hearth"] = s.Village.Hearth, ["gold"] = s.Village.Gold, ["militia"] = s.Militia, ["tradeBound"] = s.Village.TradeBound == null ? null : s.Village.TradeBound.StringId });
                check(Math.Abs(t.Prosperity - 3600) < 400 && Math.Abs(c.Prosperity - 700) < 200, "town and castle prosperity are the specified ones");
                check(t.FoodStocks > 0 && c.FoodStocks > 0 && t.Gold > 0 && town.Militia > 0, "the town and castle start with food, the town with gold and militia");
                check(t.Workshops.Count(w => w != null && w.WorkshopType != null) > 0, "the town starts with workshops");
                check(t.GarrisonParty != null && t.GarrisonParty.MemberRoster.TotalManCount > 0, "the town starts with a garrison");
                // Market prices against the item's base value (DefaultTradeItemPriceFactorModel clamps the factor to 0.1-10): grain, which
                // the fixture's wheat farm supplies, within 3 x; clamp hits are recorded.
                var prices = new Dictionary<string, object>();
                foreach (var item in PriceItems.Select(id => MBObjectManager.Instance.GetObject<ItemObject>(id)).Where(i => i != null))
                {
                    int price = t.GetItemPrice(item, null, false);
                    prices[item.StringId] = new Dictionary<string, object> { ["price"] = price, ["value"] = item.Value, ["multiple"] = item.Value == 0 ? 0.0 : Math.Round(price / (double)item.Value, 2) };
                }
                report["prices"] = prices;
                report["priceClampHits"] = prices.Where(p => (double)((Dictionary<string, object>)p.Value)["multiple"] >= 9.5).Select(p => p.Key).ToList();
                var grain = prices.ContainsKey("grain") ? (Dictionary<string, object>)prices["grain"] : null;
                check(grain != null && (int)grain["price"] > 0 && (double)grain["multiple"] <= 3.0, "grain, supplied by the fixture's wheat farm, sells within 3 x its base value");
                // Every registered village type's food production (products whose item is food), for the economy policy's food table.
                var food = new Dictionary<string, object>(); var foodItems = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var type in MBObjectManager.Instance.GetObjectTypeList<VillageType>())
                {
                    double amount = 0;
                    foreach (var p in type.Productions) if (p.Item1 != null && p.Item1.IsFood) { amount += p.Item2; foodItems.Add(p.Item1.StringId); }
                    food[type.StringId] = Math.Round(amount, 3);
                }
                report["villageTypeFood"] = food; report["foodItems"] = foodItems.ToList();
                // Ownership transfer and back: settlement, fief and village lists of both clans and their kingdoms, and the village's faction.
                Func<Clan, Clan, bool> holds = (owner, previous) => castle.OwnerClan == owner && owner.Settlements.Contains(castle) && owner.Fiefs.Contains(castle.Town) && !previous.Settlements.Contains(castle) &&
                    !previous.Fiefs.Contains(castle.Town) && castleVillage.OwnerClan == owner && owner.Villages.Contains(castleVillage.Village) && !previous.Villages.Contains(castleVillage.Village) &&
                    castleVillage.MapFaction == owner.MapFaction && (owner.Kingdom == null || (owner.Kingdom.Fiefs.Contains(castle.Town) && owner.Kingdom.Villages.Contains(castleVillage.Village))) &&
                    (previous.Kingdom == null || previous.Kingdom == owner.Kingdom || (!previous.Kingdom.Fiefs.Contains(castle.Town) && !previous.Kingdom.Villages.Contains(castleVillage.Village)));
                ChangeOwnerOfSettlementAction.ApplyByDefault(otherClan.Leader, castle);
                bool moved = holds(otherClan, clan);
                ChangeOwnerOfSettlementAction.ApplyByDefault(clan.Leader, castle);
                bool back = holds(clan, otherClan);
                report["ownershipTransfer"] = new Dictionary<string, object> { ["moved"] = moved, ["restored"] = back, ["kingdoms"] = new[] { clan.Kingdom == null ? null : clan.Kingdom.StringId, otherClan.Kingdom == null ? null : otherClan.Kingdom.StringId } };
                check(moved && back, "transferring the castle moves it and its village between the clans' (and kingdoms') settlement, fief and village lists, and back");
            }
            catch (Exception error) { report["error"] = error.ToString(); failures.Add("settlement fixtures: " + error.Message); }
            return report;
        }
    }
}
