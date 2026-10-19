using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;
using NavalDLC.CampaignBehaviors;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-35: the naval definitions against the War Sails runtime (docs/naval-content.md), run by the generated_campaign probe with
    // navalAudit=<repo> on the world with the naval fixtures. At the visible paused map:
    //   * hulls: every creation culture's hulls (naval-content.json) are registered ShipHulls of the policy's type, their mission ships
    //     resolve and name the policy's prefab (whose nested prefab and deck navmesh make_naval_policy.py verified in the installed files;
    //     GameEntity.PrefabExists is recorded only: the campaign map has not loaded the mission ship prefabs, run 6636), and every upgrade
    //     piece's siege engine is registered;
    //   * shipyards: each port town has building_shipyard, and ShipProductionCampaignBehavior.CanTownCreateShipFromHull agrees with the
    //     policy's tiers for every hull of its culture at its level;
    //   * the new game's fleets: every ship the installed production and trade made is owned by a town or by a party of a clan that may hold
    //     ships, and is in its owner's list; the fixture's landlocked clan holds none;
    //   * the fleet rule: War Sails reads the generated ClanShipOwnershipModel and ShipDistributionModel; they give the installed answers for
    //     clans that may hold ships, and refuse ships to (and give an ideal fleet of 0 to) the landlocked clan;
    //   * the sanctioned path: a ship the installed ShipProductionCampaignBehavior.CreateShip builds at a port town (hull choice, upgrade
    //     pieces, OnShipCreated, ApplyByProduction) and sells to a lord's party by ApplyByTrade is owned by that party, in its ship list and
    //     no longer the town's, gives it naval navigation and a valid anchor (the trade anchors a buyer that already had ships), and marks
    //     its naval visual for refresh; a second produced ship goes to the player's party, whose visual the visual layer refreshes (it does
    //     so only for parties the player can see) by the end of the gameplay window;
    //   * pirates: the pirate clan aliases exist with their hideout home, PiratesCampaignBehavior counts them as pirate clans, their templates'
    //     hulls resolve, and their patrol zones are reported (the generated scene has no PirateSpawnPoint entities until MMR-40).
    // After the gameplay window: the fleets again, and the pirate parties.
    internal static class NavalContentProbe
    {
        public static Dictionary<string, object> AtStart(string repo, GeneratedCampaignRegistration registration, GeneratedGameManager boot, List<string> failures)
        {
            var report = new Dictionary<string, object>();
            Action<bool, string> check = (ok, label) => { if (!ok) failures.Add("naval audit: " + label); };
            try
            {
                var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
                var policy = (IDictionary<string, object>)json.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen/policies/naval-content.json")));
                var cultures = (IDictionary<string, object>)((IDictionary<string, object>)policy["hulls"])["cultures"];
                var tiers = (IDictionary<string, object>)((IDictionary<string, object>)policy["shipyards"])["hullTiers"];
                // Hulls.
                var missionType = typeof(MissionShipObject);
                var hullProblems = new List<string>(); var prefabs = new HashSet<string>(StringComparer.Ordinal); var loaded = new HashSet<string>(StringComparer.Ordinal);
                foreach (var c in cultures)
                    foreach (var h in (IDictionary<string, object>)c.Value)
                    {
                        var hull = MBObjectManager.Instance.GetObject<ShipHull>(h.Key);
                        var chain = (IDictionary<string, object>)h.Value;
                        string type = (string)chain["type"], prefab = (string)chain["prefab"];
                        var mission = hull == null ? null : MBObjectManager.Instance.GetObject<MissionShipObject>(hull.MissionShipObjectId);
                        bool prefabOk = mission != null && mission.Prefab == prefab;
                        if (mission != null) { prefabs.Add(mission.Prefab); if (TaleWorlds.Engine.GameEntity.PrefabExists(mission.Prefab)) loaded.Add(mission.Prefab); }
                        if (hull == null || !String.Equals(hull.Type.ToString(), type, StringComparison.OrdinalIgnoreCase) || mission == null || !prefabOk)
                            hullProblems.Add(c.Key + ":" + h.Key + (hull == null ? " not registered" : mission == null ? " mission ship " + hull.MissionShipObjectId + " missing" : !prefabOk ? " prefab " + mission.Prefab + " is not the policy's " + prefab : " is " + hull.Type));
                    }
                var engines = ((System.Collections.IEnumerable)((IDictionary<string, object>)policy["provenance"])["siegeEngines"]).Cast<object>().Select(x => (string)x).ToList();
                var enginesMissing = engines.Where(e => MBObjectManager.Instance.GetObject<SiegeEngineType>(e) == null).ToList();
                report["hulls"] = new Dictionary<string, object> { ["checked"] = cultures.Sum(c => ((IDictionary<string, object>)c.Value).Count), ["missionShipType"] = missionType.FullName, ["prefabs"] = prefabs.Count, ["prefabsLoadedOnTheMap"] = loaded.Count,
                    ["siegeEngines"] = engines.Count, ["siegeEnginesMissing"] = enginesMissing, ["problems"] = hullProblems };
                check(hullProblems.Count == 0 && enginesMissing.Count == 0, "creation-culture hull chains do not resolve as the policy says: " + String.Join(", ", hullProblems.Concat(enginesMissing)));
                // Shipyards and tier gating.
                var production = Campaign.Current.GetCampaignBehavior<ShipProductionCampaignBehavior>();
                var canCreate = typeof(ShipProductionCampaignBehavior).GetMethod("CanTownCreateShipFromHull", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var ports = Settlement.All.Where(s => s.IsTown && s.HasPort && s.StringId.StartsWith(registration.WorldId + "_", StringComparison.Ordinal)).OrderBy(s => s.StringId, StringComparer.Ordinal).ToList();
                var gating = new Dictionary<string, object>(); var gatingProblems = new List<string>();
                foreach (var town in ports)
                {
                    int level = town.Town.Buildings.Where(b => b.BuildingType.StringId == "building_shipyard").Select(b => b.CurrentLevel).FirstOrDefault();
                    var culture = town.Culture.StringId;
                    var rows = new Dictionary<string, object>();
                    object hulls;
                    if (cultures.TryGetValue(culture, out hulls))
                        foreach (var h in (IDictionary<string, object>)hulls)
                        {
                            var hull = MBObjectManager.Instance.GetObject<ShipHull>(h.Key);
                            int tier = Convert.ToInt32(tiers[(string)((IDictionary<string, object>)h.Value)["type"]]);
                            bool expected = level >= 1 && tier <= level;
                            bool actual = production != null && canCreate != null && hull != null && (bool)canCreate.Invoke(production, new object[] { town.Town, hull });
                            rows[h.Key] = actual;
                            if (actual != expected) gatingProblems.Add(town.StringId + " level " + level + " " + h.Key + ": engine " + actual + ", policy " + expected);
                        }
                    gating[town.StringId] = new Dictionary<string, object> { ["culture"] = culture, ["shipyardLevel"] = level, ["canCreate"] = rows };
                    if (level < 1) gatingProblems.Add(town.StringId + " has no shipyard");
                }
                report["shipyards"] = gating;
                check(ports.Count > 0 && production != null && canCreate != null && gatingProblems.Count == 0, "shipyard tiers disagree with the engine: " + String.Join("; ", gatingProblems));
                // The new game's fleets and the fleet rule, before this audit adds a ship of its own.
                report["fleets"] = Fleets(registration, boot, check, "after the new game");
                report["fleetRule"] = FleetRule(registration, boot, check);
                // The sanctioned path: the installed production at the best shipyard, then a trade to ruler 1's party (a coastal clan).
                var lord = Hero.Find(registration.WorldId + "_ruler_1"); var party = lord == null ? null : lord.PartyBelongedTo;
                var seller = ports.OrderByDescending(t => t.Town.Buildings.Where(b => b.BuildingType.StringId == "building_shipyard").Select(b => b.CurrentLevel).FirstOrDefault()).ThenBy(t => t.StringId, StringComparer.Ordinal).FirstOrDefault();
                var sanctioned = new Dictionary<string, object>();
                try
                {
                    var create = typeof(ShipProductionCampaignBehavior).GetMethod("CreateShip", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Town) }, null);
                    var before = seller.Party.Ships.ToList();
                    create.Invoke(production, new object[] { seller.Town });
                    var ship = seller.Party.Ships.FirstOrDefault(s => !before.Contains(s));
                    if (ship == null) throw new InvalidOperationException("ShipProductionCampaignBehavior.CreateShip built no ship at " + seller.StringId);
                    int shipsBefore = party.Ships.Count;
                    bool atTown = ship.Owner == seller.Party;
                    ChangeShipOwnerAction.ApplyByTrade(party.Party, ship);
                    sanctioned["hull"] = ((IShipOrigin)ship).Hull.StringId; sanctioned["seller"] = seller.StringId; sanctioned["party"] = party.StringId; sanctioned["ship"] = ship.Name.ToString();
                    sanctioned["producedAtTown"] = atTown; sanctioned["ownedByParty"] = ship.Owner == party.Party; sanctioned["inPartyList"] = party.Ships.Contains(ship);
                    sanctioned["leftTown"] = !seller.Party.Ships.Contains(ship); sanctioned["partyShips"] = shipsBefore + " -> " + party.Ships.Count;
                    sanctioned["navalNavigation"] = party.HasNavalNavigationCapability; sanctioned["inCampaignParties"] = MobileParty.All.Contains(party);
                    sanctioned["anchorValid"] = party.Anchor != null && party.Anchor.IsValid; sanctioned["visualMarkedDirty"] = party.IsNavalVisualDirty;
                    check(atTown && ship.Owner == party.Party && party.Ships.Contains(ship) && !seller.Party.Ships.Contains(ship) && party.HasNavalNavigationCapability && MobileParty.All.Contains(party) &&
                          party.Anchor != null && party.Anchor.IsValid && party.IsNavalVisualDirty,
                          "a ship the installed production built and a lord's party bought is not owned and listed by that party alone with naval navigation, a valid anchor and its visual marked for refresh");
                    // Rendering: NavalMobilePartyVisual.Tick refreshes a dirty naval visual only for a party the player can see, so a second
                    // produced ship goes to the player's party, by ApplyByTransferring (the sanctioned fleet move: no gold, so the gameplay
                    // window's recruiting and trading keep the player's purse); its visual must be refreshed by the end of the window.
                    before = seller.Party.Ships.ToList();
                    create.Invoke(production, new object[] { seller.Town });
                    var second = seller.Party.Ships.FirstOrDefault(x => !before.Contains(x));
                    if (second == null) throw new InvalidOperationException("ShipProductionCampaignBehavior.CreateShip built no second ship at " + seller.StringId);
                    ChangeShipOwnerAction.ApplyByTransferring(MobileParty.MainParty.Party, second);
                    sanctioned["playerShip"] = second.Name.ToString(); sanctioned["playerOwns"] = second.Owner == MobileParty.MainParty.Party && MobileParty.MainParty.Ships.Contains(second);
                    sanctioned["playerVisualMarkedDirty"] = MobileParty.MainParty.IsNavalVisualDirty;
                    check(second.Owner == MobileParty.MainParty.Party && MobileParty.MainParty.Ships.Contains(second) && MobileParty.MainParty.IsNavalVisualDirty,
                          "a produced ship traded to the player's party is not owned and listed by it with its visual marked for refresh");
                }
                catch (Exception e) { sanctioned["error"] = e.ToString(); check(false, "the sanctioned ship path failed: " + e.Message); }
                report["sanctionedPath"] = sanctioned;
                // Pirates.
                var pirates = new Dictionary<string, object>();
                var behaviour = Campaign.Current.GetCampaignBehavior<PiratesCampaignBehavior>();
                var isPirate = typeof(PiratesCampaignBehavior).GetMethod("IsPirateClan", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var zones = typeof(PiratesCampaignBehavior).GetMethod("GetPatrolZones", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Clan) }, null);
                var pirateProblems = new List<string>();
                foreach (var id in new[] { "northern_pirates", "southern_pirates" })
                {
                    var clan = Clan.All.FirstOrDefault(c => c.StringId == id);
                    if (clan == null) { pirateProblems.Add(id + " missing"); continue; }
                    var stacks = clan.DefaultPartyTemplate == null || clan.DefaultPartyTemplate.ShipHulls == null ? new List<ShipTemplateStack>() : clan.DefaultPartyTemplate.ShipHulls.ToList();
                    bool pirate = behaviour != null && isPirate != null && (bool)isPirate.Invoke(behaviour, new object[] { clan });
                    var zoneList = behaviour != null && zones != null ? zones.Invoke(behaviour, new object[] { clan }) as IList : null;
                    pirates[id] = new Dictionary<string, object> { ["isPirateClan"] = pirate, ["home"] = clan.HomeSettlement == null ? null : clan.HomeSettlement.StringId,
                        ["homeIsHideout"] = clan.HomeSettlement != null && clan.HomeSettlement.IsHideout, ["template"] = clan.DefaultPartyTemplate == null ? null : clan.DefaultPartyTemplate.StringId,
                        ["templateShipStacks"] = stacks.Count, ["patrolZones"] = zoneList == null ? -1 : zoneList.Count, ["parties"] = clan.WarPartyComponents.Count };
                    if (!pirate || clan.HomeSettlement == null || !clan.HomeSettlement.IsHideout || stacks.Count == 0) pirateProblems.Add(id + (pirate ? "" : " is not a pirate clan to PiratesCampaignBehavior") + (clan.HomeSettlement == null || !clan.HomeSettlement.IsHideout ? " has no hideout home" : "") + (stacks.Count == 0 ? " template has no ships" : ""));
                }
                report["pirates"] = pirates;
                check(pirateProblems.Count == 0, "pirate clans: " + String.Join("; ", pirateProblems));
            }
            catch (Exception error) { report["error"] = error.ToString(); failures.Add("naval audit: " + error.Message); }
            return report;
        }

        public static Dictionary<string, object> AfterGameplay(GeneratedCampaignRegistration registration, GeneratedGameManager boot, List<string> failures)
        {
            var report = new Dictionary<string, object>();
            Action<bool, string> check = (ok, label) => { if (!ok) failures.Add("naval audit after gameplay: " + label); };
            try
            {
                report["fleets"] = Fleets(registration, boot, check, "after the gameplay window");
                report["pirateParties"] = MobileParty.All.Count(p => p.ActualClan != null && (p.ActualClan.StringId == "northern_pirates" || p.ActualClan.StringId == "southern_pirates"));
                // Rendering: the visual layer has refreshed the player's party after its purchase (SetNavalVisualAsDirty -> NavalMobilePartyVisual.Tick
                // -> RefreshPartyIcon -> OnNavalVisualsUpdated; it does so for parties the player can see).
                var main = MobileParty.MainParty;
                report["buyerVisualDirty"] = main.IsNavalVisualDirty; report["buyerShips"] = main.Ships.Count;
                check(!main.IsNavalVisualDirty && main.Ships.Count > 0, "the player's naval visual was never refreshed after its purchase, or it lost its ships");
            }
            catch (Exception error) { report["error"] = error.ToString(); failures.Add("naval audit after gameplay: " + error.Message); }
            return report;
        }

        // Every ship in the world: owned by a town or by a party of a clan that may hold ships, and in its owner's list.
        private static Dictionary<string, object> Fleets(GeneratedCampaignRegistration registration, GeneratedGameManager boot, Action<bool, string> check, string when)
        {
            var landlocked = boot.ShipOwnership == null ? new List<string>() : boot.ShipOwnership.Landlocked.ToList();
            var atTowns = Settlement.All.Where(s => s.Party != null).SelectMany(s => s.Party.Ships.Select(ship => new { Owner = s.Party, Ship = ship, Clan = (string)null, Kind = "settlement " + s.StringId })).ToList();
            var atParties = MobileParty.All.SelectMany(p => p.Ships.Select(ship => new { Owner = p.Party, Ship = ship, Clan = p.ActualClan == null ? null : p.ActualClan.StringId, Kind = "party" })).ToList();
            var all = atTowns.Concat(atParties).ToList();
            var problems = all.Where(x => x.Ship.Owner != x.Owner).Select(x => x.Ship.Name + " listed by " + x.Kind + " but owned by " + (x.Ship.Owner == null ? "nobody" : x.Ship.Owner.Name.ToString())).ToList();
            problems.AddRange(atParties.Where(x => x.Clan != null && landlocked.Contains(x.Clan)).Select(x => x.Ship.Name + " held by landlocked clan " + x.Clan));
            var byClan = atParties.GroupBy(x => x.Clan ?? "none").ToDictionary(g => g.Key, g => (object)g.Count());
            var report = new Dictionary<string, object> { ["townShips"] = atTowns.Count, ["partyShips"] = atParties.Count, ["byClan"] = byClan,
                ["hulls"] = all.GroupBy(x => ((IShipOrigin)x.Ship).Hull.StringId).ToDictionary(g => g.Key, g => (object)g.Count()), ["problems"] = problems, ["landlocked"] = landlocked };
            check(problems.Count == 0, "ships " + when + " are not where their owners say, or held by a landlocked clan: " + String.Join("; ", problems.Take(5)));
            check(all.Count > 0, "no ship exists " + when + " (the installed production and trade made none)");
            check(landlocked.Count > 0 && landlocked.All(c => Clan.All.Any(x => x.StringId == c)), "the fixture's landlocked clan is missing, so the rule is not exercised");
            return report;
        }

        // War Sails reads the generated model, which delegates to the installed one and keeps a landlocked clan at zero.
        private static Dictionary<string, object> FleetRule(GeneratedCampaignRegistration registration, GeneratedGameManager boot, Action<bool, string> check)
        {
            var report = new Dictionary<string, object>();
            var models = NavalDLC.NavalDLCManager.Instance == null ? null : NavalDLC.NavalDLCManager.Instance.GameModels;
            var effective = models == null ? null : models.ClanShipOwnershipModel;
            var model = boot.ShipOwnership; var distribution = boot.ShipDistribution;
            report["effectiveModel"] = effective == null ? null : effective.GetType().FullName;
            report["installedModel"] = model == null || model.Installed == null ? null : model.Installed.GetType().FullName;
            report["effectiveDistributionModel"] = models == null || models.ShipDistributionModel == null ? null : models.ShipDistributionModel.GetType().FullName;
            report["installedDistributionModel"] = distribution == null || distribution.Installed == null ? null : distribution.Installed.GetType().FullName;
            check(model != null && ReferenceEquals(effective, model) && model.Installed != null && distribution != null && ReferenceEquals(models.ShipDistributionModel, distribution) && distribution.Installed != null,
                  "War Sails does not read the generated ClanShipOwnershipModel and ShipDistributionModel");
            if (model == null || model.Installed == null || distribution == null || distribution.Installed == null) return report;
            // The landlocked clan's parties are refused ships; a coastal clan's get the installed answer.
            var sampleHull = MBObjectManager.Instance.GetObjectTypeList<ShipHull>().FirstOrDefault();
            var sample = sampleHull == null ? null : new Ship(sampleHull);
            var answers = new Dictionary<string, object>(); bool refuses = true, delegates = true;
            foreach (var party in MobileParty.All.Where(p => p.IsLordParty && p.ActualClan != null).OrderBy(p => p.StringId, StringComparer.Ordinal))
            {
                bool landlocked = model.Landlocked.Contains(party.ActualClan.StringId);
                bool take = distribution.CanPartyTakeShip(party.Party, sample), installed = distribution.Installed.CanPartyTakeShip(party.Party, sample);
                bool send = distribution.CanSendShipToParty(sample, party), installedSend = distribution.Installed.CanSendShipToParty(sample, party);
                answers[party.StringId] = (landlocked ? "landlocked: " : "") + "take " + take + " (installed " + installed + "), send " + send + " (installed " + installedSend + ")";
                if (landlocked) refuses &= !take && !send; else delegates &= take == installed && send == installedSend;
            }
            report["distributionAnswers"] = answers;
            check(refuses && delegates && model.Landlocked.Count > 0, "the generated ShipDistributionModel does not refuse the landlocked clan's parties and delegate the rest");
            var clans = Clan.All.Where(c => !c.IsBanditFaction && c.StringId != "northern_pirates" && c.StringId != "southern_pirates").OrderBy(c => c.StringId, StringComparer.Ordinal).ToList();
            var values = clans.ToDictionary(c => c.StringId, c => (object)(model.GetIdealShipNumberForClan(c) + " (installed " + model.Installed.GetIdealShipNumberForClan(c) + ")"));
            bool same = clans.All(c => model.Landlocked.Contains(c.StringId) ? model.GetIdealShipNumberForClan(c) == 0 : model.GetIdealShipNumberForClan(c) == model.Installed.GetIdealShipNumberForClan(c));
            var probe = clans.FirstOrDefault(c => !model.Landlocked.Contains(c.StringId) && model.Installed.GetIdealShipNumberForClan(c) > 0);
            int whileLandlocked = -1, after = -1;
            if (probe != null)
            {
                model.Landlocked.Add(probe.StringId);
                try { whileLandlocked = NavalDLC.NavalDLCManager.Instance.GameModels.ClanShipOwnershipModel.GetIdealShipNumberForClan(probe); }
                finally { model.Landlocked.Remove(probe.StringId); }
                after = NavalDLC.NavalDLCManager.Instance.GameModels.ClanShipOwnershipModel.GetIdealShipNumberForClan(probe);
            }
            report["values"] = values; report["probeClan"] = probe == null ? null : probe.StringId; report["whileLandlocked"] = whileLandlocked; report["afterwards"] = after;
            check(same && probe != null && whileLandlocked == 0 && after == model.Installed.GetIdealShipNumberForClan(probe) && after > 0,
                  "the generated ClanShipOwnershipModel does not keep the installed values and zero for a landlocked clan");
            return report;
        }
    }
}
