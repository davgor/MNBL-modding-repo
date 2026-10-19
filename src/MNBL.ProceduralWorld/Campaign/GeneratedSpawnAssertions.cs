using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MNBL.ProceduralWorld.CampaignBootstrap;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-34: the diagnostic out-of-bounds and off-navmesh assertions on a generated world's parties (docs/campaign-behavior-compatibility.md).
    // The in-game counterpart of GeneratedSpawnModel: every party created, and every party on the map each campaign hour, is checked
    // against the active world's navigation: a valid navmesh face whose terrain the party's current navigation type may use
    // (PartyNavigationModel.IsTerrainTypeValidForNavigationType), inside the map's extents and, on a periodic world, at its canonical x.
    // Registered by diagnostic builds (the probes); a failure is recorded with the party, its kind, position and reason, never repaired.
    internal sealed class GeneratedSpawnAssertions : CampaignBehaviorBase
    {
        // A plain list: a party is still inside MobileParty.CreateParty when MobilePartyCreated fires, and hashing it (MBObjectBase
        // identity) or reading its id there can throw into the engine's dispatch. It is checked at the next campaign hour.
        private readonly List<MobileParty> created = new List<MobileParty>();
        public Dictionary<string, int> Checked { get; private set; }
        public Dictionary<string, int> CreatedChecked { get; private set; }
        public List<string> Violations { get; private set; }
        public int ViolationCount { get; private set; }
        public Dictionary<string, object> Samples { get; private set; }

        public GeneratedSpawnAssertions()
        {
            Checked = new Dictionary<string, int>(StringComparer.Ordinal); CreatedChecked = new Dictionary<string, int>(StringComparer.Ordinal);
            Violations = new List<string>(); Samples = new Dictionary<string, object>(StringComparer.Ordinal);
        }

        public override void RegisterEvents()
        {
            CampaignEvents.MobilePartyCreated.AddNonSerializedListener(this, p => { try { if (!ReferenceEquals(p, null)) created.Add(p); } catch { } });
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, Hourly);
        }

        public override void SyncData(IDataStore dataStore) { }

        public static string KindOf(MobileParty party)
        {
            if (party.IsMainParty) return "player";
            if (party.IsCaravan) return "caravan";
            if (party.IsVillager) return "villager";
            if (party.IsLordParty) return "lord";
            if (party.IsBandit) return "bandit";
            return "other";
        }

        // Why a party's position is not valid navigation of the active world, or null.
        public static string Problem(MobileParty party)
        {
            var position = party.Position;
            var navigation = party.IsCurrentlyAtSea ? MobileParty.NavigationType.Naval : MobileParty.NavigationType.Default;
            return Problem(position, navigation);
        }

        public static string Problem(CampaignVec2 position, MobileParty.NavigationType navigation)
        {
            var campaign = Campaign.Current;
            if (!position.IsValid()) return "not a valid campaign position";
            var v = position.ToVec2();
            if (float.IsNaN(v.x) || float.IsNaN(v.y)) return "not finite";
            var min = Campaign.MapMinimumPosition; var max = Campaign.MapMaximumPosition;
            if (v.x < min.x || v.y < min.y || v.x > max.x || v.y > max.y) return "outside the map extents (" + Format(min.x) + ", " + Format(min.y) + ")-(" + Format(max.x) + ", " + Format(max.y) + ")";
            var periodic = PeriodicWorld.Current;
            if (periodic != null && !periodic.IsCanonical(v.x)) return "not at the periodic world's canonical x";
            var face = position.Face;
            if (!face.IsValid()) return "on no navmesh face";
            var terrain = campaign.MapSceneWrapper.GetFaceTerrainType(face);
            if (!campaign.Models.PartyNavigationModel.IsTerrainTypeValidForNavigationType(terrain, navigation)) return "on " + terrain + " terrain, invalid for " + navigation + " navigation";
            return null;
        }

        public void Check(MobileParty party, string when)
        {
            if (party == null || !party.IsActive || party.CurrentSettlement != null) return;
            string kind = KindOf(party);
            string problem = Problem(party);
            var counter = when == "created" ? CreatedChecked : Checked;
            int n; counter.TryGetValue(kind, out n); counter[kind] = n + 1;
            var v = party.Position.ToVec2();
            if (!Samples.ContainsKey(kind)) Samples[kind] = new Dictionary<string, object> { ["party"] = party.StringId, ["position"] = new[] { v.x, v.y }, ["when"] = when, ["problem"] = problem };
            if (problem == null) return;
            ViolationCount++;
            if (Violations.Count < 50) Violations.Add(when + " " + kind + " " + party.StringId + " at (" + Format(v.x) + ", " + Format(v.y) + "): " + problem);
        }

        private void Hourly()
        {
            var pending = created.ToList(); created.Clear();
            foreach (var party in pending) { try { Check(party, "created"); } catch (Exception e) { Record("created ? : check threw " + e.GetType().Name + ": " + e.Message); } }
            foreach (var party in MobileParty.All.ToList()) { try { Check(party, "hourly"); } catch (Exception e) { Record("hourly ? : check threw " + e.GetType().Name + ": " + e.Message); } }
        }

        private void Record(string violation)
        {
            ViolationCount++;
            if (Violations.Count < 50) Violations.Add(violation);
        }

        private static string Format(float x) { return x.ToString("0.###", CultureInfo.InvariantCulture); }
    }
}
