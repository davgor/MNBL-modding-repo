using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.CampaignSystem;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-34: the non-serialized listeners of a campaign's events. CampaignEvents belongs to its Campaign (created in the Campaign
    // constructor; the Campaign.CampaignEvents property is internal), and each MbEvent keeps its non-serialized listeners as a chain of
    // EventHandlerRec (Owner, Next).
    internal static class CampaignListeners
    {
        public static CampaignEvents EventsOf(Campaign campaign)
        {
            if (campaign == null) return null;
            return typeof(Campaign).GetProperty("CampaignEvents", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(campaign) as CampaignEvents;
        }

        // (event field, owner) -> listener count.
        public static Dictionary<KeyValuePair<string, object>, int> Of(CampaignEvents events)
        {
            var counts = new Dictionary<KeyValuePair<string, object>, int>();
            if (events == null) return counts;
            foreach (var field in events.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                var e = field.GetValue(events);
                if (e == null || !(e.GetType().Name.StartsWith("MbEvent", StringComparison.Ordinal) || e.GetType().Name.StartsWith("ReferenceMBEvent", StringComparison.Ordinal))) continue;
                var head = e.GetType().GetField("_nonSerializedListenerList", BindingFlags.Instance | BindingFlags.NonPublic);
                var rec = head == null ? null : head.GetValue(e);
                for (int guard = 0; rec != null && guard < 100000; guard++)
                {
                    var ownerField = rec.GetType().GetField("<Owner>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                    var owner = ownerField == null ? null : ownerField.GetValue(rec);
                    if (owner != null)
                    {
                        var key = new KeyValuePair<string, object>(field.Name, owner);
                        int n; counts.TryGetValue(key, out n); counts[key] = n + 1;
                    }
                    var next = rec.GetType().GetField("Next", BindingFlags.Instance | BindingFlags.Public);
                    rec = next == null ? null : next.GetValue(rec);
                }
            }
            return counts;
        }
    }
}
