using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-261: the runtime landscape mutation in play (the model is World/WorldMutations.cs; docs/magic-ground.md "Live mutation").
    //   * WorldMutations.Report(MagicEvent) and the console (mnbl_magic.cast, mnbl_magic.list) queue events; the daily tick runs the residue
    //     batch; a safe point (the campaign tick on the map, no load running, the map's overlay up) starts fwg apply on a background thread
    //     (MutationRunner, GenerationProbe.RunProducerApply) and applies a finished delta to MMR-260's overlay (MagicGroundRuntime.Set).
    //   * Never during a load: the runner refuses while GeneratedWorldLoader.Busy, while a load's SyncData has been read and its load has not
    //     finished, or while no map overlay is up; a load beginning cancels a running job (CancelForLoad). The world's release
    //     (GeneratedGameManager's teardown) cancels it too (Deactivate).
    //   * SyncData: one string key, _mnblWorldMutations, the canonical MutationState document. Read in the load's first stage, before the map
    //     loads: verified cells and nodes go to MagicGroundRuntime.Restore (the map load replays them before PreAfterLoad); a document that does
    //     not verify goes to RestoreDisabled (live cells off, casts refused, generated ground drawn) and is saved back unchanged.
    //   * Header: mnbl.campaign.mutationProducer, the producer build the campaign casts with (readable at the main menu).
    internal sealed class WorldMutationBehavior : CampaignBehaviorBase
    {
        public const string SyncKey = "_mnblWorldMutations";
        public const string HeaderPrefix = "mnbl.campaign.";
        public static readonly string[] HeaderKeys = { "mutationProducer" };
        // The worlds' mutation contexts (WorldReconstruction at activation and load), by scene name, beside MagicGroundRuntime.Overlays.
        public static readonly Dictionary<string, MutationModel.Context> Contexts = new Dictionary<string, MutationModel.Context>(StringComparer.Ordinal);
        internal static WorldMutationBehavior Active { get; private set; }

        private readonly Campaign campaign;
        private readonly string sceneName;
        private readonly Action<string> trace;
        public MutationModel Model { get; private set; }
        public MutationRunner Runner { get; private set; }
        private bool loadPending;
        private string document;
        public string LastPump { get; private set; }
        public Dictionary<string, object> LastChange { get; private set; }

        public WorldMutationBehavior(MutationModel.Context context, string scene, Action<string> trace)
        {
            sceneName = scene; this.trace = trace ?? (s => { }); campaign = Campaign.Current;
            Model = new MutationModel(context);
            Runner = new MutationRunner(Loading, GenerationProbeLauncher);
            Active = this;
        }

        private static ApplyOutcome GenerationProbeLauncher(ApplyJob job, System.Threading.CancellationToken token) { return MNBL.ProceduralWorld.Diagnostics.GenerationProbe.RunProducerApply(job, token); }

        private bool Loading() { return MNBL.ProceduralWorld.Persistence.GeneratedWorldLoader.Busy || loadPending || MNBL.ProceduralWorld.World.MagicGroundRuntime.Snapshot == null; }

        internal static WorldMutationBehavior Current()
        {
            var a = Active;
            if (a == null || !ReferenceEquals(a.campaign, Campaign.Current)) return null;
            return a;
        }

        // The world's release: the running job is killed and its outcome dropped.
        public static void Deactivate()
        {
            var a = Active; Active = null;
            if (a != null) { a.Model.Cancel(a.Runner); a.trace("world_mutations_released"); }
        }
        // A load beginning (GeneratedWorldLoader): no producer keeps running into it.
        public static void CancelForLoad() { var a = Active; if (a != null) { a.Model.Cancel(a.Runner); a.trace("world_mutations_cancelled_for_load"); } }

        public override void RegisterEvents()
        {
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTick);
            CampaignEvents.TickEvent.AddNonSerializedListener(this, OnTick);
            CampaignEvents.OnGameLoadFinishedEvent.AddNonSerializedListener(this, () => loadPending = false);
            CampaignEvents.CollectMetadataEntriesEvent.AddNonSerializedListener(this, entries => entries.Add(new KeyValuePair<string, string>(HeaderPrefix + HeaderKeys[0], Model.State.Producer ?? "")));
        }

        public override void SyncData(IDataStore dataStore)
        {
            if (dataStore.IsSaving) document = Model.Save();
            dataStore.SyncData("_mnblWorldMutations", ref document);
            if (dataStore.IsSaving) return;
            loadPending = true;
            try
            {
                List<MutationCell> cells; List<MagicNode> nodes;
                if (Model.Load(document, out cells, out nodes))
                {
                    if (cells != null) MNBL.ProceduralWorld.World.MagicGroundRuntime.Restore(sceneName, cells, nodes);
                    trace("world_mutations_loaded:cells " + (cells == null ? 0 : cells.Count) + ":edits " + Model.State.Edits.Count + ":pending " + Model.State.Pending.Count);
                }
                else
                {
                    MNBL.ProceduralWorld.World.MagicGroundRuntime.RestoreDisabled(sceneName);
                    trace("world_mutations_disabled:" + Model.DisabledReason);
                }
            }
            catch (Exception error) { trace("world_mutations_load_failed:" + error); try { MNBL.ProceduralWorld.World.MagicGroundRuntime.RestoreDisabled(sceneName); } catch (Exception) { } }
        }

        private void OnDailyTick()
        {
            try
            {
                var emitted = Model.DailyBatch((int)Math.Floor(CampaignTime.Now.ToDays), OwnerKingdom, RulingHome);
                if (emitted.Count > 0) trace("world_mutations_edits:" + String.Join(",", emitted.Select(e => e.School + "@" + e.At)));
            }
            catch (Exception error) { trace("world_mutations_batch_failed:" + error); }
        }

        // The safe point: the campaign tick on the map (engine thread), outside any load.
        private void OnTick(float dt)
        {
            if (Loading()) return;
            // MMR-288: a live cast's veins grow outward over campaign time (one comparison when no stamp is due).
            MNBL.ProceduralWorld.World.MagicGroundRuntime.Advance(CampaignTime.Now.ToHours);
            try
            {
                var outcome = Runner.Take();
                if (outcome != null)
                {
                    var cells = Model.Applied(outcome, (c, n) => { var change = MNBL.ProceduralWorld.World.MagicGroundRuntime.Set(c, n); LastChange = change.Record(); });
                    trace("world_mutations_outcome:" + (cells == null ? "refused: " + Model.LastError : cells.Count + " cells") + ":" + outcome.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s");
                }
                if (Model.State.Pending.Count > 0 && !Runner.Busy)
                {
                    var why = Model.Pump(Runner);
                    if (why != LastPump) { LastPump = why; trace("world_mutations_pump:" + (why ?? "started")); }
                }
            }
            catch (Exception error) { trace("world_mutations_tick_failed:" + error); }
        }

        internal static string OwnerKingdom(string settlement)
        {
            var s = Settlement.Find(settlement);
            return s == null || s.OwnerClan == null || s.OwnerClan.Kingdom == null ? null : s.OwnerClan.Kingdom.StringId;
        }
        internal static string RulingHome(string kingdom)
        {
            var k = Kingdom.All.FirstOrDefault(x => x.StringId == kingdom);
            if (k == null || k.IsEliminated || k.RulingClan == null || k.RulingClan.HomeSettlement == null) return null;
            return k.RulingClan.HomeSettlement.StringId;
        }

        // The batch now (the console's cast), outside the daily tick.
        internal List<MutationEdit> BatchNow() { return Model.DailyBatch((int)Math.Floor(CampaignTime.Now.ToDays), OwnerKingdom, RulingHome); }
    }

    // MMR-261's API for a magic system (owner decision 7: none yet): report a magic event at a settlement.
    internal static class WorldMutations
    {
        // Null when queued for the next daily batch, else why it was refused.
        public static string Report(MagicEvent e)
        {
            var b = WorldMutationBehavior.Current();
            if (b == null) return "no generated campaign with live mutation is running";
            return b.Model.Report(e);
        }
    }

    // Debug console: mnbl_magic.cast <school> <settlement id or name> [strength]; mnbl_magic.list.
    internal static class MutationCommands
    {
        [CommandLineFunctionality.CommandLineArgumentFunction("cast", "mnbl_magic")]
        public static string Cast(List<string> args)
        {
            var b = WorldMutationBehavior.Current();
            if (b == null) return "No generated campaign with live mutation is running.";
            if (args == null || args.Count < 2) return "Usage: mnbl_magic.cast <school> <settlement id or name> [strength]. Schools: " + String.Join(", ", b.Model.World.Policy.Schools);
            string school = args[0];
            double strength = b.Model.World.Policy.CastStrength;
            var words = args.Skip(1).ToList();
            double parsed;
            if (words.Count > 1 && Double.TryParse(words[words.Count - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) { strength = parsed; words.RemoveAt(words.Count - 1); }
            string name = String.Join(" ", words);
            var settlement = Settlement.Find(name) ?? Settlement.All.FirstOrDefault(s => String.Equals(s.Name.ToString(), name, StringComparison.OrdinalIgnoreCase));
            if (settlement == null) return "No settlement " + name + ".";
            string refusal = b.Model.Report(new MagicEvent(school, settlement.StringId, strength));
            if (refusal != null) return "Refused: " + refusal;
            var edits = b.BatchNow();
            if (edits.Count == 0) return "Counted " + strength.ToString("0.##", CultureInfo.InvariantCulture) + " " + school + " at " + settlement.Name + "; no edit yet (" + (b.Model.LastError ?? "residue below " + b.Model.World.Policy.ResidueThreshold.ToString(CultureInfo.InvariantCulture)) + ").";
            return String.Join("\n", edits.Select(e => "Edit: " + e.School + " key point " + e.Node + " intensity " + e.Intensity + " at " + e.At + " (" + e.Rule + "); fwg apply runs in the background."));
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("list", "mnbl_magic")]
        public static string List(List<string> args)
        {
            var b = WorldMutationBehavior.Current();
            if (b == null) return "No generated campaign with live mutation is running.";
            var m = b.Model; var s = m.State;
            var lines = new List<string> {
                "producer " + s.Producer + (m.CastRefusal() == null ? " (casts allowed)" : " (casts refused: " + m.CastRefusal() + ")"),
                "edits " + s.Edits.Count + ", pending " + s.Pending.Count + ", live cells " + s.Cells.Count + ", nodes " + s.Nodes.Count + (b.Runner.Busy ? ", fwg apply running" : "") };
            lines.AddRange(s.Edits.Select(e => "  day " + e.Day + ": " + e.School + " at " + e.At + " (" + e.Rule + ", from " + e.Site + ") intensity " + e.Intensity));
            lines.AddRange(s.Pending.Select(e => "  pending: " + e.School + " at " + e.At + " intensity " + e.Intensity));
            lines.AddRange(s.Residue.Select(kv => "  residue " + kv.Key + " " + kv.Value.ToString("0.##", CultureInfo.InvariantCulture)));
            if (m.LastError != null) lines.Add("last error: " + m.LastError);
            if (m.DisabledReason != null) lines.Add("disabled: " + m.DisabledReason);
            return String.Join("\n", lines);
        }
    }
}
