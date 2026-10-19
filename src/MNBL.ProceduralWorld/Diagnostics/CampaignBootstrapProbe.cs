using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;

namespace MNBL.ProceduralWorld.Diagnostics
{
    // Read-only snapshots on the loading callback's thread. A census is evidence,
    // not an acceptance result; in particular an unavailable registry is not empty.
    internal static class CampaignBootstrapProbe
    {
        internal static Dictionary<string, object> Capture(string worldId, string phase)
        {
            var campaign = Campaign.Current;
            var objects = MBObjectManager.Instance;
            var report = new Dictionary<string, object>
            {
                ["worldId"] = worldId, ["phase"] = phase,
                ["utc"] = DateTime.UtcNow.ToString("O"),
                ["threadId"] = Thread.CurrentThread.ManagedThreadId,
                ["gameIdentityWitness"] = Witness(Game.Current),
                ["campaignIdentityWitness"] = Witness(campaign),
                ["objectManagerIdentityWitness"] = Witness(objects),
                ["campaignObjectManagerIdentityWitness"] = Witness(campaign == null ? null : campaign.CampaignObjectManager),
                ["identityWitnessLimit"] = "Runtime identity hashes are diagnostic witnesses, not collision-free identity proof.",
                ["acceptanceDecisionMade"] = false
            };
            var registries = new Dictionary<string, object>(); report["mbObjectManager"] = registries;
            Registry(() => Objects<CharacterObject>(objects), registries, "characters");
            Registry(() => Objects<CultureObject>(objects), registries, "cultures");
            Registry(() => Objects<Hero>(objects), registries, "heroes");
            Registry(() => Objects<Clan>(objects), registries, "clans");
            Registry(() => Objects<Kingdom>(objects), registries, "kingdoms");
            Registry(() => Objects<Settlement>(objects), registries, "settlements");
            Registry(() => Objects<MobileParty>(objects), registries, "parties");
            if (campaign == null || campaign.CampaignObjectManager == null)
                report["campaignObjectManager"] = new { available = false };
            else
            {
                // Settlements stays null until CampaignObjectManager.InitializeOnNewGame,
                // which runs after submodule registration.
                var manager = campaign.CampaignObjectManager;
                var lists = new Dictionary<string, object> { ["available"] = true };
                Registry(() => manager.Settlements, lists, "settlements");
                Registry(() => manager.Clans, lists, "clans");
                Registry(() => manager.Kingdoms, lists, "kingdoms");
                Registry(() => manager.AliveHeroes, lists, "aliveHeroes");
                Registry(() => manager.DeadOrDisabledHeroes, lists, "deadOrDisabledHeroes");
                Registry(() => manager.MobileParties, lists, "parties");
                report["campaignObjectManager"] = lists;
            }
            return report;
        }

        private static int? Witness(object value)
        {
            return value == null ? (int?)null : RuntimeHelpers.GetHashCode(value);
        }

        private static IEnumerable<T> Objects<T>(MBObjectManager manager) where T : MBObjectBase
        {
            if (manager == null) throw new InvalidOperationException("MBObjectManager is unavailable.");
            return manager.GetObjectTypeList<T>();
        }

        private static void Registry<T>(Func<IEnumerable<T>> list, IDictionary<string, object> report, string label)
            where T : MBObjectBase
        {
            try
            {
                report[label] = new { available = true, objects = Rows(list()) };
            }
            catch (Exception error)
            {
                report[label] = new { available = false, exception = error.ToString() };
            }
        }

        private static object[] Rows<T>(IEnumerable<T> values) where T : MBObjectBase
        {
            if (values == null) throw new InvalidOperationException("Registry list is unavailable.");
            return values.OrderBy(value => value.StringId, StringComparer.Ordinal).Select(value => (object)Row(value)).ToArray();
        }

        private static string Id(MBObjectBase value) { return value == null ? null : value.StringId; }

        private static Dictionary<string, object> Row(MBObjectBase value)
        {
            var row = new Dictionary<string, object>
            {
                ["id"] = value.StringId, ["type"] = value.GetType().FullName,
                ["ready"] = value.IsReady, ["identityWitness"] = Witness(value)
            };
            // A partially deserialized forward reference may not support derived
            // getters yet. Preserve the identity and record the failure explicitly.
            try
            {
                var character = value as CharacterObject;
                if (character != null)
                {
                    row["culture"] = Id(character.Culture);
                    row["hero"] = Id(character.HeroObject);
                    row["isHero"] = character.IsHero;
                    row["isTemplate"] = character.IsTemplate;
                }
                var hero = value as Hero;
                if (hero != null)
                {
                    row["character"] = Id(hero.CharacterObject); row["clan"] = Id(hero.Clan);
                    row["home"] = Id(hero.HomeSettlement); row["culture"] = Id(hero.Culture);
                }
                var clan = value as Clan;
                if (clan != null)
                {
                    row["leader"] = Id(clan.Leader); row["kingdom"] = Id(clan.Kingdom);
                    row["initialHome"] = Id(clan.InitialHomeSettlement); row["culture"] = Id(clan.Culture);
                }
                var kingdom = value as Kingdom;
                if (kingdom != null)
                {
                    row["leader"] = Id(kingdom.Leader); row["initialHome"] = Id(kingdom.InitialHomeSettlement);
                    row["culture"] = Id(kingdom.Culture); row["clans"] = kingdom.Clans.Select(Id).ToArray();
                }
                var settlement = value as Settlement;
                if (settlement != null)
                {
                    row["ownerClan"] = Id(settlement.OwnerClan); row["owner"] = Id(settlement.Owner);
                    row["culture"] = Id(settlement.Culture); row["isTown"] = settlement.IsTown;
                    row["isVillage"] = settlement.IsVillage; row["hasPort"] = settlement.HasPort;
                    row["position"] = new[] { settlement.Position.X, settlement.Position.Y };
                    row["gate"] = new[] { settlement.GatePosition.X, settlement.GatePosition.Y };
                    row["port"] = new[] { settlement.PortPosition.X, settlement.PortPosition.Y };
                    row["bound"] = settlement.IsVillage ? Id(settlement.Village.Bound) : null;
                    row["notables"] = settlement.Notables.Select(Id).ToArray();
                }
                var party = value as MobileParty;
                if (party != null)
                {
                    row["leader"] = Id(party.LeaderHero); row["owner"] = Id(party.Owner);
                    row["home"] = Id(party.HomeSettlement); row["component"] = party.PartyComponent == null ? null : party.PartyComponent.GetType().FullName;
                    row["position"] = new[] { party.Position.X, party.Position.Y };
                }
            }
            catch (Exception error) { row["referenceReadError"] = error.ToString(); }
            return row;
        }
    }
}
