using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // MMR-19: writes the generated world's identity into every save's metadata (CampaignEvents.CollectMetadataEntriesEvent is
    // dispatched by SaveHandler.GetSaveMetaData for every save mode, autosaves included). The header is readable at the main
    // menu with MBSaveLoad.GetSaveFileWithName(name).MetaData before anything deserializes. Keys are prefixed "mnbl.world."
    // because metadata values are written with set_Item and could otherwise overwrite built-in keys.
    // MMR-48: with a binding, the header also records the build that wrote it (SaveEntries, "mnbl.save.*": SaveBindingContract), and each
    // save's outcome reaches the save journal (OnSaveOverEvent -> WorldSaveBinding.OnSaveOver).
    public sealed class WorldIdentityBehavior : CampaignBehaviorBase
    {
        public const string Prefix = "mnbl.world.";
        public readonly Dictionary<string, string> Entries = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> SaveEntries = new Dictionary<string, string>(StringComparer.Ordinal);
        public int Collections { get; private set; }

        public override void RegisterEvents()
        {
            CampaignEvents.CollectMetadataEntriesEvent.AddNonSerializedListener(this, Collect);
            CampaignEvents.OnSaveOverEvent.AddNonSerializedListener(this, MNBL.ProceduralWorld.Persistence.WorldSaveBinding.OnSaveOver);
        }
        public override void SyncData(IDataStore dataStore) { }

        private void Collect(List<KeyValuePair<string, string>> entries)
        {
            foreach (var entry in Entries) entries.Add(new KeyValuePair<string, string>(Prefix + entry.Key, entry.Value));
            if (Entries.Count > 0) foreach (var entry in SaveEntries) entries.Add(new KeyValuePair<string, string>(MNBL.ProceduralWorld.Persistence.SaveBindingContract.Prefix + entry.Key, entry.Value));
            Collections++;
        }
    }
}
