using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;

namespace MNBL.ProceduralWorld.Persistence
{
    // MMR-62: publishes a generated campaign's initial save. The journal's intent is written first (EarlyWorldBindingStore.Begin), then the
    // engine saves (SaveHandler.SaveAs, observed through a temporary ISaveManager that forwards OnSaveOver to the sandbox's own and turns
    // autosave off while it is installed), then the save is read back (Complete). Published is reported only when the header read back from
    // the game's save list carries this instance's binding and the file is whole; MMR-45's activation returns success only on Published.
    // Ironman: SaveHandler.SaveTick saves an Ironman campaign under IronmanModSaveName ("Ironman" + UniqueGameId) whatever name SaveAs was
    // given (installed 1.4.8 IL_005f), so that is the name reserved and journaled. Tick once per frame until Done; Tick(true) after a timeout
    // gives up only when the engine has no save queued (SaveHandler.IsSaving), so a queued save is never abandoned while it can still land.
    internal sealed class InitialSavePublisher
    {
        public InitialSave Record { get; private set; }
        public bool Done { get; private set; }
        public string Error { get; private set; }
        public bool Published { get { return Record != null && Record.State == EarlyWorldBindingStore.Published; } }
        public string SaveName { get; private set; }
        public bool Ironman { get; private set; }

        private readonly EarlyWorldBindingStore store;
        private readonly Campaign campaign;
        private ISaveManager previous;
        private Observer observer;

        private InitialSavePublisher(EarlyWorldBindingStore store, Campaign campaign) { this.store = store; this.campaign = campaign; }

        public static InitialSavePublisher Start(EarlyWorldBindingStore store, Campaign campaign, WorldBinding binding)
        {
            var publisher = new InitialSavePublisher(store, campaign) { Ironman = CampaignOptions.IsIronmanMode };
            publisher.SaveName = publisher.Ironman ? campaign.SaveHandler.IronmanModSaveName : EarlyWorldBindingStore.InitialSaveName(binding);
            try
            {
                publisher.Record = store.Begin(binding, publisher.SaveName);
                publisher.previous = campaign.SandBoxManager.SandBoxSaveManager;
                publisher.observer = new Observer(publisher.SaveName, publisher.previous);
                campaign.SandBoxManager.SandBoxSaveManager = publisher.observer;
                campaign.SaveHandler.SaveAs(publisher.SaveName);
            }
            catch (Exception error) { publisher.Finish(error); }
            return publisher;
        }

        public void Tick(bool timedOut = false)
        {
            if (Done || observer == null) return;
            if (!observer.Completed && (!timedOut || campaign.SaveHandler.IsSaving)) return;
            try { Record = store.Complete(Record.Instance, observer.Completed && observer.Success); Finish(null); }
            catch (Exception error) { Finish(error); }
        }

        private void Finish(Exception error)
        {
            if (observer != null && campaign.SandBoxManager.SandBoxSaveManager == observer) campaign.SandBoxManager.SandBoxSaveManager = previous;
            if (error != null) Error = error.GetType().Name + ": " + error.Message;
            Done = true;
        }

        private sealed class Observer : ISaveManager
        {
            private readonly string expected;
            private readonly ISaveManager inner;
            internal bool Completed, Success;
            internal Observer(string expected, ISaveManager inner) { this.expected = expected; this.inner = inner; }
            public int GetAutoSaveInterval() { return -1; }
            public bool IsAutoSaveDisabled() { return true; }
            public void OnSaveOver(bool success, string name)
            {
                if (name == expected) { Success = success; Completed = true; }
                if (inner != null) inner.OnSaveOver(success, name);
            }
        }
    }
}
