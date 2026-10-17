using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.Persistence;

namespace MNBL.ProceduralWorld.Generation
{
    // MMR-45 (U03): one compiled world becomes one campaign instance, all or nothing (docs/world-activation.md). Pure managed: the in-game commit
    // stage (ActivationCommitStage) and the offline failure harness (tests/Generation/ActivationFailureTests.cs) run this same transaction.
    //
    // Boundaries, in order; each is journaled durably (<store>/activations/<instance>.json) once it has completed:
    //   Admit           the store's writer lock, no other activation in progress, the generation job ready for review
    //   VerifyData      the DATA_READY bundle: manifest, file hashes and host compatibility, before any native resource opens
    //   VerifyRuntime   the derived runtime reports belong to this world and scene and still pass their gates: navigation (W02), settlement
    //                   layout (W04), playability and route reachability (W05)
    //   BuildWorld      the campaign graph from the bundle (factories, resolved positions, re-emitted templates) closed and validated (C07)
    //   PromotePackage  the world package (manifest last, moved into place whole): from here a stopped process leaves a promoted world
    //   StartCampaign   the new game: registration inside RegisterSubModuleObjects (R07), the generated map scene, navigation caches, battle map
    //   PostBuild       the post-build checks on the live campaign (baked entities, positions, mission scenes, cache key, climate: W03)
    //   BindIdentity    the save header's world binding (WorldIdentity) with this instance
    //   PublishSave     the initial save, read back bound to this instance and whole (MMR-62's journal)
    //   WriteReceipt    the immutable activation receipt and the runtime-derived record it names (written by the transaction itself)
    // A failure at a boundary undoes, in reverse ownership order through the host's Undo, first what the failing boundary itself left (its
    // partial work: Undo is safe on a boundary that did not finish or never started its work), then every completed boundary (end the
    // campaign, roll back registration, delete a package this activation created); the journal says failed with every undo's outcome:
    // diagnostic evidence, never a playable half-world. Failures that leave the journal active for recovery instead: any failure once the
    // initial save was published (the receipt write, or a journal write after PublishSave: the world is published whole and bound, so nothing
    // is undone and recovery writes the receipt), and a publication whose save journal stays unresolved (UnresolvedPublication: the live
    // campaign is undone, the package kept, and recovery commits or abandons once MMR-62's recovery has decided the save). No save is
    // published before every check has passed, so no partial world is ever selectable as a save.
    internal enum ActivationBoundary { Admit, VerifyData, VerifyRuntime, BuildWorld, PromotePackage, StartCampaign, PostBuild, BindIdentity, PublishSave, WriteReceipt }

    // What a boundary produced: a note, the evidence hashes the receipt binds (keys such as W02, C07), and detail for the runtime-derived record.
    internal sealed class ActivationStep
    {
        public ActivationBoundary Boundary;
        public string Note;
        public SortedDictionary<string, string> Evidence = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public SortedDictionary<string, object> Detail = new SortedDictionary<string, object>(StringComparer.Ordinal);
    }

    // The instance the transaction activates and the state its boundaries share.
    internal sealed class ActivationRequest
    {
        public string Instance;                 // 32 lowercase hex (the save binding's instance)
        public string WorldContentId, ManifestSha256;
        public readonly Dictionary<string, object> State = new Dictionary<string, object>(StringComparer.Ordinal);
        public string CampaignInstanceId { get { return Uuid(Instance); } }   // the same id as a dashed RFC 4122 UUID (the receipt's form)

        // The save binding's instance (WorldBinding.NewInstance: Guid.NewGuid in "N" form, a version-4 UUID) written with dashes. An instance
        // whose version and variant nibbles are not a UUID's is refused, so the receipt's pattern always holds.
        public static string Uuid(string instance)
        {
            if (!ValidInstance(instance)) throw new ArgumentException("Not a campaign instance: " + instance);
            return instance.Substring(0, 8) + "-" + instance.Substring(8, 4) + "-" + instance.Substring(12, 4) + "-" + instance.Substring(16, 4) + "-" + instance.Substring(20, 12);
        }
        public static bool ValidInstance(string instance)
        {
            return instance != null && instance.Length == 32 && instance.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
                && instance[12] >= '1' && instance[12] <= '8' && "89ab".IndexOf(instance[16]) >= 0;
        }
    }

    internal interface IActivationHost
    {
        // Performs a boundary's work (never WriteReceipt, which the transaction writes); throws to fail it.
        ActivationStep Run(ActivationBoundary boundary, ActivationRequest context);
        // Reverses a boundary; called in reverse order after a failure: for the failing boundary itself (whatever part of its work it did, or
        // none) and for every completed one. Must be safe whatever part of the work happened. Throws to report that the undo did not complete.
        void Undo(ActivationBoundary boundary, ActivationRequest context);
    }

    internal sealed class ActivationException : Exception
    {
        public readonly ActivationBoundary Boundary;
        public readonly bool Recoverable;
        public readonly List<Dictionary<string, object>> Rollback;
        public ActivationException(ActivationBoundary boundary, Exception cause, bool recoverable, List<Dictionary<string, object>> rollback)
            : base("Activation failed at " + boundary + ": " + cause.Message, cause) { Boundary = boundary; Recoverable = recoverable; Rollback = rollback; }
    }

    // A failure the harness or a probe injects at a boundary (before its work), and a simulated process stop after one (no undo, no journal).
    internal sealed class InjectedActivationFault : Exception { public InjectedActivationFault(ActivationBoundary b) : base("injected fault at " + b) { } }
    internal sealed class SimulatedProcessStop : Exception { public SimulatedProcessStop(string where) : base("simulated process stop " + where) { } }
    // PublishSave's failure when the initial save's journal could not be resolved (MMR-62 leaves the intent): the save may exist, bound and
    // whole, so the activation is neither committed nor failed until the save store's recovery decides.
    internal sealed class UnresolvedPublication : Exception { public UnresolvedPublication(string message, Exception cause) : base(message, cause) { } }

    internal sealed class ActivationJournal
    {
        public string Instance, WorldContentId, ManifestSha256, State, FailedAt, Error;
        public readonly List<Dictionary<string, object>> Phases = new List<Dictionary<string, object>>();
        public readonly List<Dictionary<string, object>> History = new List<Dictionary<string, object>>();
        public List<Dictionary<string, object>> Rollback = new List<Dictionary<string, object>>();
        public IEnumerable<ActivationBoundary> Completed
        {
            get { return Phases.Select(p => (ActivationBoundary)Enum.Parse(typeof(ActivationBoundary), (string)p["boundary"])); }
        }
        public bool Has(ActivationBoundary b) { return Completed.Contains(b); }
        public SortedDictionary<string, string> Evidence
        {
            get
            {
                var all = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var p in Phases) foreach (var kv in (IDictionary<string, object>)p["evidence"]) all[kv.Key] = (string)kv.Value;
                return all;
            }
        }
    }

    // The activation journals: one durable JSON per instance, written at every transition (DurableFile).
    internal sealed class ActivationJournalStore
    {
        public const string Schema = "mnbl.activation-journal.v1";
        public const string Active = "active", Committed = "committed", Failed = "failed", Abandoned = "abandoned", Quarantined = "quarantined";
        public const string QuarantineSuffix = ".quarantined";
        // The component harness only: runs before every journal write with its note (a throw is a write that failed).
        internal Action<string> BeforeWrite { get; set; }
        private readonly string root;
        private readonly Func<DateTime> clock;

        public ActivationJournalStore(string storeRoot, Func<DateTime> clock = null)
        {
            root = Path.Combine(Path.GetFullPath(storeRoot), "activations"); this.clock = clock ?? (() => DateTime.UtcNow);
        }
        public string Root { get { return root; } }
        public string JournalPath(string instance) { if (!ActivationRequest.ValidInstance(instance)) throw new ArgumentException("Not a campaign instance: " + instance); return Path.Combine(root, instance + ".json"); }
        public string InstanceDirectory(string instance) { if (!ActivationRequest.ValidInstance(instance)) throw new ArgumentException("Not a campaign instance: " + instance); return Path.Combine(root, instance); }

        // Only one activation may be in progress (active) at a time.
        public ActivationJournal Begin(ActivationRequest context)
        {
            if (File.Exists(JournalPath(context.Instance))) throw new InvalidOperationException("Campaign instance " + context.Instance + " already has an activation journal.");
            List<string> unreadable;
            var running = Journals(out unreadable).Where(j => j.State == Active).Select(j => j.Instance).ToList();
            if (running.Count > 0) throw new InvalidOperationException("Another activation is in progress or unrecovered (" + String.Join(", ", running) + "); recover it first.");
            if (unreadable.Count > 0) throw new InvalidDataException("An activation journal cannot be read (" + unreadable[0] + "); refusing to start another.");
            var journal = new ActivationJournal { Instance = context.Instance, WorldContentId = context.WorldContentId, ManifestSha256 = context.ManifestSha256, State = Active };
            Transition(journal, Active, "begun");
            return journal;
        }

        public void Record(ActivationJournal journal, ActivationStep step)
        {
            journal.Phases.Add(new Dictionary<string, object> { ["boundary"] = step.Boundary.ToString(), ["utc"] = Now(), ["note"] = step.Note ?? "",
                ["evidence"] = new SortedDictionary<string, object>(step.Evidence.ToDictionary(kv => kv.Key, kv => (object)kv.Value), StringComparer.Ordinal), ["detail"] = step.Detail });
            Transition(journal, journal.State, "completed " + step.Boundary);
        }
        public void Fail(ActivationJournal journal, ActivationBoundary at, Exception error, List<Dictionary<string, object>> rollback)
        {
            journal.FailedAt = at.ToString(); journal.Error = error.GetType().Name + ": " + error.Message; journal.Rollback = rollback;
            Transition(journal, Failed, "failed at " + at + "; " + rollback.Count + " boundaries undone");
        }
        public void Note(ActivationJournal journal, string note) { Transition(journal, journal.State, note); }
        public void Commit(ActivationJournal journal, string note) { Transition(journal, Committed, note); }
        public void Abandon(ActivationJournal journal, string note) { Transition(journal, Abandoned, note); }

        public ActivationJournal Journal(string instance) { string p = JournalPath(instance); return File.Exists(p) ? Read(p) : null; }
        public List<ActivationJournal> Journals(out List<string> unreadable)
        {
            var list = new List<ActivationJournal>(); unreadable = new List<string>();
            if (!Directory.Exists(root)) return list;
            foreach (var path in JournalFiles())
                try { list.Add(Read(path)); } catch (Exception e) { unreadable.Add(Path.GetFileName(path) + ": " + e.GetType().Name + ": " + e.Message); }
            return list;
        }
        // Every journal file (<instance>.json; temporaries and quarantined files excluded).
        public IEnumerable<string> JournalFiles()
        {
            if (!Directory.Exists(root)) return new string[0];
            return Directory.GetFiles(root, "*.json").Where(p => Path.GetExtension(p) == ".json" && !DurableFile.IsTemporary(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();
        }
        // An unreadable journal moved aside (kept as evidence) so it no longer stops every later activation.
        public string QuarantineFile(string path)
        {
            string target = path + QuarantineSuffix;
            if (File.Exists(target)) target = path + "." + clock().Ticks.ToString(CultureInfo.InvariantCulture) + QuarantineSuffix;
            File.Move(path, target);
            return Path.GetFileName(target);
        }
        public void Quarantine(ActivationJournal journal, string note) { Transition(journal, Quarantined, note); }

        private string Now() { return clock().ToString("o", CultureInfo.InvariantCulture); }
        private void Transition(ActivationJournal journal, string state, string note)
        {
            if (BeforeWrite != null) BeforeWrite(note);
            journal.State = state;
            journal.History.Add(new Dictionary<string, object> { ["state"] = state, ["utc"] = Now(), ["note"] = note });
            var document = new Dictionary<string, object> {
                ["schema"] = Schema, ["instance"] = journal.Instance, ["campaignInstanceId"] = ActivationRequest.Uuid(journal.Instance), ["worldContentId"] = journal.WorldContentId,
                ["manifestSha256"] = journal.ManifestSha256, ["state"] = journal.State, ["failedAt"] = journal.FailedAt, ["error"] = journal.Error,
                ["phases"] = journal.Phases, ["rollback"] = journal.Rollback, ["history"] = journal.History };
            Directory.CreateDirectory(root);
            DurableFile.WriteAllText(JournalPath(journal.Instance), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(document));
        }

        private static ActivationJournal Read(string path)
        {
            var d = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
            if (d == null || !Equals(d.ContainsKey("schema") ? d["schema"] : null, Schema)) throw new InvalidDataException("not an activation journal");
            Func<string, string> text = k => { object v; return d.TryGetValue(k, out v) ? v as string : null; };
            var j = new ActivationJournal { Instance = text("instance"), WorldContentId = text("worldContentId"), ManifestSha256 = text("manifestSha256"), State = text("state"),
                FailedAt = text("failedAt"), Error = text("error") };
            if (j.Instance + ".json" != Path.GetFileName(path)) throw new InvalidDataException("the journal's instance is not its file name");
            if (!new[] { Active, Committed, Failed, Abandoned, Quarantined }.Contains(j.State)) throw new InvalidDataException("unknown state " + j.State);
            foreach (var p in Rows(d, "phases"))
            {
                object b; if (!p.TryGetValue("boundary", out b) || !Enum.IsDefined(typeof(ActivationBoundary), b as string ?? "")) throw new InvalidDataException("a phase has no known boundary");
                if (!(p.ContainsKey("evidence") && p["evidence"] is IDictionary<string, object>)) throw new InvalidDataException("a phase has no evidence");
                j.Phases.Add(p);
            }
            j.History.AddRange(Rows(d, "history"));
            j.Rollback = Rows(d, "rollback").ToList();
            return j;
        }
        private static IEnumerable<Dictionary<string, object>> Rows(Dictionary<string, object> d, string key)
        {
            object v; if (!d.TryGetValue(key, out v) || v == null) yield break;
            foreach (var row in (IEnumerable)v) { var r = row as Dictionary<string, object>; if (r == null) throw new InvalidDataException(key + " has a malformed row"); yield return r; }
        }
    }

    // The activation receipt (worldgen/contracts/activation-receipt.schema.json, mnbl.activation-receipt v1) and the runtime-derived record its
    // runtimeDerivedBundleSha256 names: everything derived at activation (generator, request and policy hashes, backend, engine and DLC hashes,
    // validated resource and cache hashes), built only from the journal so a recovery writes the same bytes. Both are write-once.
    internal static class ActivationReceipt
    {
        public static readonly string[] Validations = { "W02", "W03", "W04", "W05", "C07" };

        // The boundaries the receipt and the runtime-derived record are built from: every check up to and including BindIdentity. The publication
        // and the receipt's own phase are left out, so a recovery that journals them differently still rebuilds the same bytes.
        private static IEnumerable<Dictionary<string, object>> Validated(ActivationJournal journal)
        {
            return journal.Phases.Where(p => (ActivationBoundary)Enum.Parse(typeof(ActivationBoundary), (string)p["boundary"]) < ActivationBoundary.PublishSave);
        }
        private static SortedDictionary<string, string> EvidenceOf(ActivationJournal journal)
        {
            var all = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in Validated(journal)) foreach (var kv in (IDictionary<string, object>)p["evidence"]) all[kv.Key] = (string)kv.Value;
            return all;
        }

        public static string DerivedText(ActivationJournal journal)
        {
            var record = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["schema"] = "mnbl.runtime-derived-bundle", ["version"] = 1, ["worldContentId"] = journal.WorldContentId, ["manifestSha256"] = journal.ManifestSha256,
                ["campaignInstanceId"] = ActivationRequest.Uuid(journal.Instance), ["evidence"] = EvidenceOf(journal),
                ["boundaries"] = Validated(journal).Select(p => new SortedDictionary<string, object>(StringComparer.Ordinal) { ["boundary"] = p["boundary"], ["evidence"] = Canonical(p["evidence"]),
                    ["detail"] = Canonical(p.ContainsKey("detail") ? p["detail"] : null) }).ToList() };
            return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(record);
        }

        public static string ReceiptText(ActivationJournal journal, string derivedSha256)
        {
            var evidence = EvidenceOf(journal);
            var missing = Validations.Where(v => !evidence.ContainsKey(v) || !IsSha(evidence[v])).ToList();
            if (missing.Count > 0) throw new InvalidDataException("The journal holds no passed evidence for " + String.Join(", ", missing) + ".");
            if (!IsSha(journal.WorldContentId) || !IsSha(journal.ManifestSha256) || !IsSha(derivedSha256)) throw new InvalidDataException("The journal's world identity is not complete.");
            var receipt = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["schema"] = "mnbl.activation-receipt", ["version"] = 1, ["worldContentId"] = journal.WorldContentId, ["manifestSha256"] = journal.ManifestSha256,
                ["campaignInstanceId"] = ActivationRequest.Uuid(journal.Instance), ["runtimeDerivedBundleSha256"] = derivedSha256,
                ["validations"] = new SortedDictionary<string, object>(Validations.ToDictionary(v => v, v => (object)new SortedDictionary<string, object>(StringComparer.Ordinal) { ["status"] = "passed", ["evidenceSha256"] = evidence[v] }), StringComparer.Ordinal),
                ["activatedBy"] = "U03" };
            return new JavaScriptSerializer().Serialize(receipt);
        }

        // Writes runtime-derived.json and receipt.json under <store>/activations/<instance>/ once: an existing file with the same bytes is the
        // same receipt (a recovery after a stop), anything else refuses. Callers pass the journal as read back from its durable file, so the
        // transaction and a later recovery build from the same values. Returns the receipt's SHA-256.
        public static string Write(ActivationJournalStore store, ActivationJournal journal)
        {
            string directory = store.InstanceDirectory(journal.Instance);
            Directory.CreateDirectory(directory);
            byte[] derived = new UTF8Encoding(false).GetBytes(DerivedText(journal));
            WriteOnce(Path.Combine(directory, "runtime-derived.json"), derived);
            byte[] receipt = new UTF8Encoding(false).GetBytes(ReceiptText(journal, Sha(derived)));
            WriteOnce(Path.Combine(directory, "receipt.json"), receipt);
            return Sha(receipt);
        }

        // The receipt of an instance, verified against its runtime-derived record; null when there is none.
        public static Dictionary<string, object> Read(ActivationJournalStore store, string instance)
        {
            string directory = store.InstanceDirectory(instance), receiptPath = Path.Combine(directory, "receipt.json"), derivedPath = Path.Combine(directory, "runtime-derived.json");
            if (!File.Exists(receiptPath)) return null;
            var receipt = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(receiptPath)) as Dictionary<string, object>;
            Func<string, object> get = key => { object v; return receipt != null && receipt.TryGetValue(key, out v) ? v : null; };
            if (receipt == null || !Equals(get("schema"), "mnbl.activation-receipt") || !Equals(get("campaignInstanceId"), ActivationRequest.Uuid(instance)) || !File.Exists(derivedPath)
                || !Equals(get("runtimeDerivedBundleSha256"), Sha(File.ReadAllBytes(derivedPath))))
                throw new InvalidDataException("The activation receipt of " + instance + " does not verify against its instance and its runtime-derived record.");
            receipt["sha256"] = Sha(File.ReadAllBytes(receiptPath));
            return receipt;
        }

        private static void WriteOnce(string path, byte[] bytes)
        {
            if (File.Exists(path))
            {
                if (!File.ReadAllBytes(path).SequenceEqual(bytes)) throw new InvalidDataException(Path.GetFileName(path) + " exists with other content; an activation receipt is immutable.");
                return;
            }
            DurableFile.WriteAllBytes(path, bytes);
        }
        private static object Canonical(object value)
        {
            var d = value as IDictionary<string, object>;
            if (d != null) return new SortedDictionary<string, object>(d.ToDictionary(kv => kv.Key, kv => Canonical(kv.Value)), StringComparer.Ordinal);
            var list = value as IEnumerable;
            if (list != null && !(value is string)) return list.Cast<object>().Select(Canonical).ToList();
            return value;
        }
        public static bool IsSha(string s) { return s != null && s.Length == 64 && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')); }
        public static string Sha(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    }

    internal sealed class WorldActivationTransaction
    {
        public static readonly ActivationBoundary[] Order = (ActivationBoundary[])Enum.GetValues(typeof(ActivationBoundary));
        private readonly IActivationHost host;
        private readonly ActivationJournalStore journals;

        public WorldActivationTransaction(IActivationHost host, ActivationJournalStore journals) { this.host = host; this.journals = journals; }

        // fault: a boundary to fail before its work (harness and probes); stopAfter: a simulated process stop after a boundary completed (the
        // harness only: no undo, the journal is left as a stopped process leaves it). Returns the committed journal.
        public ActivationJournal Run(ActivationRequest context, ActivationBoundary? fault = null, ActivationBoundary? stopAfter = null)
        {
            if (!ActivationRequest.ValidInstance(context.Instance)) throw new ArgumentException("Not a campaign instance: " + context.Instance);
            var journal = journals.Begin(context);
            var completed = new List<ActivationBoundary>();
            ActivationBoundary current = ActivationBoundary.Admit;
            bool started = false;   // the current boundary's work began (an injected fault fails it before)
            try
            {
                foreach (var boundary in Order)
                {
                    current = boundary; started = false;
                    if (fault == boundary) throw new InjectedActivationFault(boundary);
                    started = true;
                    if (boundary == ActivationBoundary.WriteReceipt)
                    {
                        string receiptSha = ActivationReceipt.Write(journals, journals.Journal(context.Instance));
                        var step = new ActivationStep { Boundary = boundary, Note = "receipt " + receiptSha };
                        step.Detail["receiptSha256"] = receiptSha;
                        completed.Add(boundary);
                        journals.Record(journal, step);
                    }
                    else
                    {
                        var step = host.Run(boundary, context) ?? new ActivationStep();
                        step.Boundary = boundary;
                        completed.Add(boundary);   // done whether or not the journal write below succeeds: an undo must cover it
                        journals.Record(journal, step);
                    }
                    if (stopAfter == boundary) throw new SimulatedProcessStop("after " + boundary);
                }
                journals.Commit(journal, "committed: receipt written, initial save published");
                return journal;
            }
            catch (SimulatedProcessStop) { throw; }
            catch (Exception error)
            {
                // Once the initial save is published (its save is bound and flushed) the world is whole: nothing is undone and recovery writes
                // the receipt, whether the receipt or a later journal write failed.
                if (completed.Contains(ActivationBoundary.PublishSave) || current == ActivationBoundary.WriteReceipt)
                {
                    TryNote(journal, "the world was published; " + (current == ActivationBoundary.WriteReceipt ? "the receipt" : "the journal") + " could not be written (" + error.Message + "); recovery writes the receipt");
                    throw new ActivationException(current, error, true, new List<Dictionary<string, object>>());
                }
                // The initial save's outcome is unknown (it may exist, bound and whole): the live campaign is undone, the promoted package is
                // kept (the pending publication references it) and the journal stays active until the save store's recovery resolves it.
                bool pending = current == ActivationBoundary.PublishSave && error is UnresolvedPublication;
                var rollback = new List<Dictionary<string, object>>();
                Action<ActivationBoundary, bool> undo = (boundary, partial) =>
                {
                    var row = new Dictionary<string, object> { ["boundary"] = boundary.ToString() };
                    if (partial) row["partial"] = true;
                    if (pending && (boundary <= ActivationBoundary.PromotePackage || boundary == ActivationBoundary.PublishSave)) { row["undone"] = false; row["kept"] = "the pending initial save references it"; rollback.Add(row); return; }
                    try { host.Undo(boundary, context); row["undone"] = true; }
                    catch (Exception failure) { row["undone"] = false; row["error"] = failure.GetType().Name + ": " + failure.Message; }
                    rollback.Add(row);
                };
                if (started && !completed.Contains(current)) undo(current, true);   // what the failing boundary left
                foreach (var boundary in Enumerable.Reverse(completed)) undo(boundary, false);
                if (pending)
                {
                    journal.Rollback = rollback;
                    TryNote(journal, "the initial save's publication is unresolved (" + error.Message + "); the live campaign was undone and recovery decides");
                    throw new ActivationException(current, error, true, rollback);
                }
                try { journals.Fail(journal, current, error, rollback); }
                catch (Exception failure) { journal.Error = (journal.Error ?? "") + "; the failed state could not be journaled: " + failure.Message; }   // stays active: recovery abandons it
                throw new ActivationException(current, error, false, rollback);
            }
        }

        private void TryNote(ActivationJournal journal, string note)
        {
            try { journals.Note(journal, note); } catch (Exception) { }   // the journal stays as last written; recovery reads the save store
        }
    }

    // Resolves activations a stopped process left active (the startup pass, after MMR-62's save recovery; idempotent):
    //   stopped before the package was promoted, or after it but with no published initial save: abandoned (the package is kept, unbound;
    //   no save names it, so it is never selectable);
    //   the initial save published (journaled, or found published by MMR-62's journal) but no receipt: the receipt is written from the
    //   journal, committed;
    //   the receipt written but not committed: committed;
    //   MMR-62's journal still unresolved for the instance: left active (reported), retried at the next start.
    // A journal that cannot be read is moved aside (<file>.quarantined), and one whose recovery fails on its data (its evidence or receipt does
    // not hold) is marked quarantined: both are reported and kept as evidence, and neither stops later activations. An I/O failure is retried.
    internal static class ActivationRecovery
    {
        // saveState: the instance's initial-save journal state after MMR-62's recovery (null: no journal).
        public static List<Dictionary<string, object>> Recover(ActivationJournalStore journals, Func<string, string> saveState)
        {
            var rows = new List<Dictionary<string, object>>();
            var active = new List<ActivationJournal>();
            foreach (var path in journals.JournalFiles())
            {
                ActivationJournal read;
                try { read = journals.Journal(Path.GetFileNameWithoutExtension(path)); }
                catch (Exception e)
                {
                    var bad = new Dictionary<string, object> { ["file"] = Path.GetFileName(path), ["error"] = e.GetType().Name + ": " + e.Message };
                    try { bad["movedTo"] = journals.QuarantineFile(path); bad["outcome"] = "unreadable, quarantined"; }
                    catch (Exception move) { bad["outcome"] = "unreadable"; bad["moveError"] = move.GetType().Name + ": " + move.Message; }
                    rows.Add(bad);
                    continue;
                }
                if (read != null && read.State == ActivationJournalStore.Active) active.Add(read);
            }
            foreach (var journal in active)
            {
                var row = new Dictionary<string, object> { ["instance"] = journal.Instance, ["worldContentId"] = journal.WorldContentId };
                try
                {
                    string save = saveState(journal.Instance);
                    bool published = journal.Has(ActivationBoundary.PublishSave) || save == EarlyWorldBindingStore.Published;
                    if (journal.Has(ActivationBoundary.WriteReceipt) || published)
                    {
                        if (!journal.Has(ActivationBoundary.PublishSave))
                            journals.Record(journal, new ActivationStep { Boundary = ActivationBoundary.PublishSave, Note = "found published by the initial-save journal at recovery" });
                        string sha = ActivationReceipt.Write(journals, journals.Journal(journal.Instance));
                        if (!journal.Has(ActivationBoundary.WriteReceipt))
                        {
                            var step = new ActivationStep { Boundary = ActivationBoundary.WriteReceipt, Note = "receipt " + sha + " written at recovery" };
                            step.Detail["receiptSha256"] = sha;
                            journals.Record(journal, step);
                        }
                        journals.Commit(journal, "recovered: the initial save was published; receipt " + sha);
                        row["outcome"] = ActivationJournalStore.Committed; row["receiptSha256"] = sha;
                    }
                    else if (save == EarlyWorldBindingStore.Intent)
                    {
                        row["outcome"] = "unresolved"; row["note"] = "the initial save's publication is unresolved; retried at the next start";
                    }
                    else
                    {
                        string where = journal.Has(ActivationBoundary.PromotePackage) ? "after the world package was promoted, before its initial save was published (the package is kept, unbound)"
                                                                                    : "before the world package's promotion was journaled (a package written by then is kept, unbound)";
                        journals.Abandon(journal, "recovered: the process stopped " + where + (save == null ? "" : "; initial save " + save));
                        row["outcome"] = ActivationJournalStore.Abandoned; row["note"] = where;
                    }
                }
                catch (InvalidDataException e)
                {
                    row["error"] = e.GetType().Name + ": " + e.Message;
                    try { journals.Quarantine(journal, "quarantined at recovery: " + e.Message); row["outcome"] = ActivationJournalStore.Quarantined; }
                    catch (Exception mark) { row["outcome"] = "error"; row["markError"] = mark.GetType().Name + ": " + mark.Message; }
                }
                catch (Exception e) { row["outcome"] = "error"; row["error"] = e.GetType().Name + ": " + e.Message; }
                rows.Add(row);
            }
            return rows;
        }
    }
}
