using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.Generation;
using MNBL.ProceduralWorld.Persistence;

// MMR-45 world activation tests, without the game: ActivationFailureTests OUT_JSON
// The component harness of WorldActivationTransaction: a scripted host whose boundaries have the production side effects that matter to a
// failure (a world package promoted into the store, a running campaign with registered objects, the save header's binding, the initial save
// published through MMR-62's real journal over a scripted save list). An earlier world is activated first; then a failure is injected at each
// commit boundary (and inside the work of the boundaries with partial side effects, by a host that cleans its own partial work and by one
// that does not), a process stop is simulated after each boundary and inside the initial save's publication, a journal write fails after the
// save was published, and the save store and the activation journals are recovered as at the next start. After every case: the earlier world's package, receipt and save are byte for byte
// unchanged; no save is bound to an instance unless its activation committed with a receipt (and then exactly one); recovery is idempotent;
// the receipt is the v1 contract, written once, and verifies against its runtime-derived record.
internal static class ActivationFailureTests
{
    private static int checks, failures;
    private static readonly List<string> lines = new List<string>();
    private static readonly List<Dictionary<string, object>> matrix = new List<Dictionary<string, object>>();
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; string line = (condition ? "ok    " : "FAIL  ") + label; lines.Add(line); Console.WriteLine(line); }
    private static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } catch (Exception) { return false; } }

    private static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("usage: ActivationFailureTests OUT_JSON"); return 2; }
        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.activation-tests", ["version"] = 1, ["ticket"] = "MMR-45" };
        string scratch = Path.Combine(Path.GetTempPath(), "mnbl-mmr45-" + Guid.NewGuid().ToString("N"));
        try { Run(scratch); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        finally { try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch (Exception) { } }
        evidence["checks"] = checks; evidence["failures"] = failures; evidence["lines"] = lines; evidence["matrix"] = matrix;
        evidence["boundaries"] = WorldActivationTransaction.Order.Select(b => b.ToString()).ToList();
        File.WriteAllText(args[0], new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    // The game's save list.
    private sealed class FakeCatalog : ISaveCatalog
    {
        public readonly Dictionary<string, SaveHeader> Saves = new Dictionary<string, SaveHeader>(StringComparer.Ordinal);
        public bool ThrowOnRead;
        public IList<SaveHeader> List() { return Saves.Values.ToList(); }
        public SaveHeader Read(string name) { if (ThrowOnRead) throw new IOException("save folder unreadable"); SaveHeader h; return Saves.TryGetValue(name, out h) ? h : null; }
        public bool Delete(string name) { return Saves.Remove(name); }
        public void Flush(string name) { }
        public void Bound(string name, WorldBinding b, bool whole)
        {
            Saves[name] = new SaveHeader { Name = name, BodyWhole = whole, BodyProblem = whole ? null : "the game data ends early (a torn write)",
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["UniqueGameId"] = "game-" + name, ["mnbl.world.id"] = b.WorldId, ["mnbl.world.manifestSha256"] = b.ManifestSha256,
                    ["mnbl.world.instance"] = b.Instance, ["mnbl.world.contentId"] = b.ContentId, ["mnbl.world.scene"] = "scene_" + b.WorldId } };
        }
        public string Snapshot(string name)
        {
            SaveHeader h; if (!Saves.TryGetValue(name, out h)) return "missing";
            return String.Join(";", h.Metadata.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value)) + ";whole=" + h.BodyWhole;
        }
    }

    // One world to activate: its id, manifest and content id (what the compiled bundle would give).
    private sealed class World
    {
        public string Id, Manifest, Content;
        public WorldBinding Binding(string instance) { return new WorldBinding { WorldId = Id, ManifestSha256 = Manifest, Instance = instance, ContentId = Content }; }
    }

    // The engine side of one game process: the running campaign (if any) and what it registered. A process stop loses it.
    private sealed class Process
    {
        public string Campaign; public readonly HashSet<string> Registered = new HashSet<string>(StringComparer.Ordinal);
        public Dictionary<string, string> Identity;
    }

    private enum Publish { Normal, StopBeforeEngineSave, StopAfterEngineSave, Unresolved, TornSave, TakenName }

    private sealed class FakeHost : IActivationHost
    {
        public readonly string Store; public readonly FakeCatalog Catalog; public readonly EarlyWorldBindingStore Binding; public readonly Process Process; public readonly World World;
        public ActivationBoundary? FailInside;      // the work fails after a partial side effect
        public bool Dirty;                           // ... and the host leaves its partial work for the transaction's undo
        public ActivationBoundary? UndoFails;       // that boundary's undo does not complete
        public Publish PublishMode = Publish.Normal;
        public readonly List<string> Log = new List<string>();
        private bool createdPackage;

        public FakeHost(string store, FakeCatalog catalog, Process process, World world)
        {
            Store = store; Catalog = catalog; Process = process; World = world;
            Binding = new EarlyWorldBindingStore(store, catalog, b => File.Exists(Path.Combine(PackageDirectory(store, b.PackageName), "manifest.json")));
        }

        public ActivationStep Run(ActivationBoundary boundary, ActivationRequest context)
        {
            Log.Add("run " + boundary);
            var step = new ActivationStep { Note = boundary.ToString() };
            var binding = World.Binding(context.Instance);
            switch (boundary)
            {
                case ActivationBoundary.Admit: break;
                case ActivationBoundary.VerifyData: step.Detail["manifestSha256"] = World.Manifest; break;
                case ActivationBoundary.VerifyRuntime: step.Evidence["W02"] = Sha("navigation " + World.Id); break;
                case ActivationBoundary.BuildWorld: step.Evidence["C07"] = Sha("graph " + World.Id); step.Evidence["W04"] = Sha("layout " + World.Id); break;
                case ActivationBoundary.PromotePackage:
                {
                    string target = PackageDirectory(Store, binding.PackageName);
                    if (Directory.Exists(target)) { createdPackage = false; step.Detail["package"] = "reused"; break; }   // identical content is shared
                    string staging = Path.Combine(Store, "worlds", ".staging-" + context.Instance);
                    Directory.CreateDirectory(Path.Combine(staging, "documents"));
                    File.WriteAllText(Path.Combine(staging, "documents", "campaign.xml"), "<world id=\"" + World.Id + "\"/>");
                    if (FailInside == boundary && !Dirty) { Directory.Delete(staging, true); throw new IOException("the package could not be moved into place"); }
                    File.WriteAllText(Path.Combine(staging, "manifest.json"), "{\"worldId\":\"" + World.Id + "\",\"manifest\":\"" + World.Manifest + "\"}");   // manifest last
                    Directory.Move(staging, target);
                    createdPackage = true; step.Detail["package"] = "promoted";
                    if (FailInside == boundary) throw new IOException("the package's record could not be written after the move");
                    break;
                }
                case ActivationBoundary.StartCampaign:
                    Process.Campaign = World.Id + "/" + context.Instance;
                    foreach (var o in new[] { "kingdom_1", "clan_1", "town_1", "village_1" }) Process.Registered.Add(World.Id + "_" + o);
                    if (FailInside == boundary) { if (!Dirty) { Process.Registered.Clear(); Process.Campaign = null; } throw new InvalidOperationException("registration refused at closure"); }
                    break;
                case ActivationBoundary.PostBuild:
                    if (Process.Campaign == null) throw new InvalidOperationException("no campaign");
                    if (FailInside == boundary) throw new InvalidDataException("a post-build check failed: the navigation cache key differs");
                    step.Evidence["W03"] = Sha("climate " + World.Id); step.Evidence["W05"] = Sha("playability " + World.Id);
                    break;
                case ActivationBoundary.BindIdentity:
                    Process.Identity = binding.Entries();
                    break;
                case ActivationBoundary.PublishSave:
                    PublishInitialSave(binding);
                    break;
                default: throw new InvalidOperationException("the host does not run " + boundary);
            }
            return step;
        }

        // The production order (InitialSavePublisher): the intent is journaled, the engine saves, the save is read back bound to this instance.
        private void PublishInitialSave(WorldBinding binding)
        {
            string name = EarlyWorldBindingStore.InitialSaveName(binding);
            if (PublishMode == Publish.TakenName)
            {
                var other = World.Binding(WorldBinding.NewInstance()); External.Add(other.Instance);
                Catalog.Bound(name, other, true);   // another campaign's save holds the name
            }
            Binding.Begin(binding, name);
            if (PublishMode == Publish.StopBeforeEngineSave) throw new SimulatedProcessStop("inside PublishSave, before the engine save");
            Catalog.Bound(name, binding, PublishMode != Publish.TornSave);
            if (PublishMode == Publish.StopAfterEngineSave) throw new SimulatedProcessStop("inside PublishSave, after the engine save");
            if (PublishMode == Publish.Unresolved) Catalog.ThrowOnRead = true;
            InitialSave record;
            try { record = Binding.Complete(binding.Instance, true); }
            catch (Exception e) { throw new UnresolvedPublication("the initial save could not be read back: " + e.Message, e); }
            finally { Catalog.ThrowOnRead = false; }
            if (record.State != EarlyWorldBindingStore.Published) throw new IOException("the initial save was not published: " + record.State + " (" + record.LastNote + ")");
        }

        public void Undo(ActivationBoundary boundary, ActivationRequest context)
        {
            Log.Add("undo " + boundary);
            if (UndoFails == boundary) throw new IOException("the undo of " + boundary + " did not complete");
            var binding = World.Binding(context.Instance);
            switch (boundary)
            {
                case ActivationBoundary.PromotePackage:
                    // Only a package this activation created, and only while nothing else references it.
                    if (createdPackage) Directory.Delete(PackageDirectory(Store, binding.PackageName), true);
                    break;
                case ActivationBoundary.StartCampaign: Process.Registered.Clear(); Process.Campaign = null; break;
                case ActivationBoundary.BindIdentity: Process.Identity = null; break;
            }
        }
    }

    private static string PackageDirectory(string store, string package) { return Path.Combine(store, "worlds", package); }
    private static string Sha(string text) { return ActivationReceipt.Sha(Encoding.UTF8.GetBytes(text)); }
    private static string TreeHash(string directory)
    {
        if (!Directory.Exists(directory)) return "missing";
        var sb = new StringBuilder();
        foreach (var f in Directory.GetFiles(directory, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            sb.Append(f.Substring(directory.Length)).Append('=').Append(ActivationReceipt.Sha(File.ReadAllBytes(f))).Append('\n');
        return Sha(sb.ToString());
    }

    // The save store's recovery, then the activations' (the startup order).
    private static List<Dictionary<string, object>> Recover(string store, FakeCatalog catalog)
    {
        var binding = new EarlyWorldBindingStore(store, catalog, b => File.Exists(Path.Combine(PackageDirectory(store, b.PackageName), "manifest.json")));
        binding.Recover();
        return ActivationRecovery.Recover(new ActivationJournalStore(store), instance => { var j = binding.Journal(instance); return j == null ? null : j.State; });
    }

    private static readonly Regex UuidPattern = new Regex("^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$");
    private static readonly Regex ShaPattern = new Regex("^[0-9a-f]{64}$");

    // The receipt against worldgen/contracts/activation-receipt.schema.json (v1, no additional properties).
    private static List<string> ReceiptProblems(string path, World world, string instance)
    {
        var problems = new List<string>();
        var r = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
        if (r == null) { problems.Add("not an object"); return problems; }
        var keys = new[] { "schema", "version", "worldContentId", "manifestSha256", "campaignInstanceId", "runtimeDerivedBundleSha256", "validations", "activatedBy" };
        if (!new HashSet<string>(r.Keys).SetEquals(keys)) problems.Add("keys " + String.Join(",", r.Keys));
        if (!Equals(r["schema"], "mnbl.activation-receipt") || !Equals(r["version"], 1) || !Equals(r["activatedBy"], "U03")) problems.Add("constants");
        if (!Equals(r["worldContentId"], world.Content) || !Equals(r["manifestSha256"], world.Manifest)) problems.Add("world identity");
        if (!UuidPattern.IsMatch(r["campaignInstanceId"] as string ?? "") || ((string)r["campaignInstanceId"]).Replace("-", "") != instance) problems.Add("campaign instance");
        if (!ShaPattern.IsMatch(r["runtimeDerivedBundleSha256"] as string ?? "")) problems.Add("derived sha");
        var v = r["validations"] as Dictionary<string, object>;
        if (v == null || !new HashSet<string>(v.Keys).SetEquals(ActivationReceipt.Validations)) problems.Add("validations keys");
        else foreach (var kv in v)
        {
            var row = kv.Value as Dictionary<string, object>;
            if (row == null || row.Count != 2 || !Equals(row["status"], "passed") || !ShaPattern.IsMatch(row["evidenceSha256"] as string ?? "")) problems.Add("validation " + kv.Key);
        }
        return problems;
    }

    private sealed class Earlier { public string Instance, Package, Receipt, Save, SaveName; }
    // Saves of campaigns outside this store (a scripted other campaign holding a reserved name).
    private static readonly HashSet<string> External = new HashSet<string>(StringComparer.Ordinal);
    // Every committed activation so far, as captured when it committed: no later case may change any of them.
    private static readonly List<Tuple<World, Earlier>> Committed = new List<Tuple<World, Earlier>>();
    private static int worlds;
    // A world of its own for each case, so a case's package expectations never depend on an earlier case's.
    private static World NewWorld() { worlds++; return new World { Id = "mnbl_" + (0x45000000 + worlds).ToString("x8"), Manifest = Sha("manifest " + worlds), Content = Sha("content " + worlds) }; }
    private static bool PackageExists(string store, World w) { return File.Exists(Path.Combine(PackageDirectory(store, w.Binding(WorldBinding.NewInstance()).PackageName), "manifest.json")); }

    private static Earlier Capture(string store, FakeCatalog catalog, World world, string instance)
    {
        var binding = world.Binding(instance);
        string name = EarlyWorldBindingStore.InitialSaveName(binding);
        return new Earlier { Instance = instance, Package = TreeHash(PackageDirectory(store, binding.PackageName)), Receipt = TreeHash(new ActivationJournalStore(store).InstanceDirectory(instance)),
            Save = catalog.Snapshot(name), SaveName = name };
    }

    // The invariants after a case and its recovery. Returns the outcome for the matrix.
    private static Dictionary<string, object> Verify(string label, string store, FakeCatalog catalog, World world, string instance, string expected,
        bool packageExisted, Dictionary<string, object> row)
    {
        var journals = new ActivationJournalStore(store);
        var journal = journals.Journal(instance);
        string state = journal == null ? "none" : journal.State;
        Check(state == expected, label + ": the activation ends " + expected + " (" + state + (journal != null && journal.Error != null ? ": " + journal.Error : "") + ")");
        var damaged = Committed.Where(c => { var now = Capture(store, catalog, c.Item1, c.Item2.Instance);
            return now.Package != c.Item2.Package || now.Receipt != c.Item2.Receipt || now.Save != c.Item2.Save || journals.Journal(c.Item2.Instance).State != ActivationJournalStore.Committed; })
            .Select(c => c.Item1.Id + "/" + c.Item2.Instance.Substring(0, 8)).ToList();
        bool intact = damaged.Count == 0;
        Check(intact, label + ": every earlier committed world (" + Committed.Count + ") keeps its package, receipt, save and committed journal byte for byte" + (intact ? "" : "; damaged " + String.Join(", ", damaged)));
        // Bindings: every bound save's instance has one save, and its activation committed with a receipt.
        var bound = catalog.Saves.Values.Select(h => WorldBinding.FromHeader(h.Metadata)).Where(b => b != null && !External.Contains(b.Instance)).ToList();
        var ambiguous = bound.GroupBy(b => b.Instance).Where(g => g.Count() != 1 || journals.Journal(g.Key) == null || journals.Journal(g.Key).State != ActivationJournalStore.Committed
                                                                  || ActivationReceipt.Read(journals, g.Key) == null).Select(g => g.Key).ToList();
        var mine = bound.Count(b => b.Instance == instance);
        bool hasReceipt = File.Exists(Path.Combine(journals.InstanceDirectory(instance), "receipt.json"));
        bool bindingsOk = ambiguous.Count == 0 && (expected == ActivationJournalStore.Committed ? mine == 1 && hasReceipt : mine == 0 && !hasReceipt);
        Check(bindingsOk, label + ": no ambiguous save binding (" + (ambiguous.Count == 0 ? "every bound save has one committed, receipted instance" : "ambiguous " + String.Join(",", ambiguous)) +
              "; this instance: " + mine + " save(s), receipt " + hasReceipt + ")");
        string package = PackageDirectory(store, world.Binding(instance).PackageName);
        bool packageExists = File.Exists(Path.Combine(package, "manifest.json"));
        bool packageExpected = expected == ActivationJournalStore.Committed || packageExisted || (row.ContainsKey("packageKept") && (bool)row["packageKept"]);
        Check(packageExists == packageExpected, label + ": the world package is " + (packageExpected ? "present" : "removed") + (packageExisted ? " (it existed before: shared content is never deleted)" : ""));
        // Idempotence: a second recovery changes nothing and finds nothing to do.
        string before = TreeHash(Path.Combine(store, "activations")) + TreeHash(Path.Combine(store, "saves")) + String.Join("|", catalog.Saves.Keys.OrderBy(k => k));
        var again = Recover(store, catalog);
        string later = TreeHash(Path.Combine(store, "activations")) + TreeHash(Path.Combine(store, "saves")) + String.Join("|", catalog.Saves.Keys.OrderBy(k => k));
        Check(again.Count == 0 && before == later, label + ": recovery is idempotent (a second pass finds nothing and changes nothing)");
        if (expected == ActivationJournalStore.Committed)
        {
            var problems = ReceiptProblems(Path.Combine(journals.InstanceDirectory(instance), "receipt.json"), world, instance);
            var read = ActivationReceipt.Read(journals, instance);
            // The receipt is a function of the journal: rebuilt from the journal read back, it is the same bytes.
            string derived = ActivationReceipt.DerivedText(journal), rebuilt = ActivationReceipt.ReceiptText(journal, ActivationReceipt.Sha(new UTF8Encoding(false).GetBytes(derived)));
            bool same = rebuilt == File.ReadAllText(Path.Combine(journals.InstanceDirectory(instance), "receipt.json"));
            Check(problems.Count == 0 && read != null && same, label + ": the receipt is the v1 contract, verifies against its runtime-derived record and is rebuilt byte for byte from the journal" +
                  (problems.Count == 0 ? "" : " (" + String.Join("; ", problems) + ")"));
        }
        row["label"] = label; row["outcome"] = state; row["saveBound"] = mine; row["receipt"] = hasReceipt; row["packagePresent"] = packageExists; row["earlierWorldIntact"] = intact;
        row["failedAt"] = journal == null ? null : journal.FailedAt;
        if (state == ActivationJournalStore.Committed) Committed.Add(Tuple.Create(world, Capture(store, catalog, world, instance)));
        row["rollback"] = journal == null ? null : journal.Rollback.Select(r => r["boundary"] + (Equals(r["undone"], true) ? "" : r.ContainsKey("kept") ? " (kept)" : " (not undone)")).ToList();
        matrix.Add(row);
        return row;
    }

    private static void Run(string scratch)
    {
        string store = Path.Combine(scratch, "store"); Directory.CreateDirectory(store);
        var catalog = new FakeCatalog();
        var earlierWorld = new World { Id = "mnbl_1609fb72", Manifest = Sha("manifest A"), Content = Sha("content A") };
        Func<World, string, ActivationRequest> context = (w, i) => new ActivationRequest { Instance = i, WorldContentId = w.Content, ManifestSha256 = w.Manifest };

        // 1. A clean activation: the earlier world every later case must leave untouched.
        string i1 = WorldBinding.NewInstance();
        var p1 = new Process(); var h1 = new FakeHost(store, catalog, p1, earlierWorld);
        var j1 = new WorldActivationTransaction(h1, new ActivationJournalStore(store)).Run(context(earlierWorld, i1));
        var journals = new ActivationJournalStore(store);
        string receipt1 = Path.Combine(journals.InstanceDirectory(i1), "receipt.json");
        var problems1 = File.Exists(receipt1) ? ReceiptProblems(receipt1, earlierWorld, i1) : new List<string> { "no receipt" };
        Check(j1.State == ActivationJournalStore.Committed && j1.Phases.Count == WorldActivationTransaction.Order.Length && p1.Campaign != null && p1.Identity != null,
              "clean: every boundary completes in order (" + String.Join(" > ", j1.Completed) + "), the campaign runs with its identity bound, the journal is committed");
        Check(problems1.Count == 0, "clean: one receipt in the v1 contract (schema, version, content id, manifest, the instance as a dashed UUID, the runtime-derived record's hash, W02-W05 and C07 passed, activatedBy U03)" +
              (problems1.Count == 0 ? "" : ": " + String.Join("; ", problems1)));
        var saved1 = h1.Binding.Journal(i1);
        Check(saved1 != null && saved1.State == EarlyWorldBindingStore.Published && catalog.Saves.ContainsKey(EarlyWorldBindingStore.InitialSaveName(earlierWorld.Binding(i1))),
              "clean: the initial save is published through MMR-62's journal and is bound to the instance");
        var earlier = Capture(store, catalog, earlierWorld, i1);
        Committed.Add(Tuple.Create(earlierWorld, earlier));
        var forged = journals.Journal(i1);
        ((IDictionary<string, object>)forged.Phases[(int)ActivationBoundary.VerifyRuntime]["evidence"])["W02"] = Sha("forged");
        Check(Throws<InvalidDataException>(() => ActivationReceipt.Write(journals, forged)) && Capture(store, catalog, earlierWorld, i1).Receipt == earlier.Receipt
              && ActivationReceipt.Write(journals, journals.Journal(i1)) == ActivationReceipt.Read(journals, i1)["sha256"] as string,
              "receipt: a second receipt for the instance with other evidence is refused (immutable); the same journal rewrites the same bytes (idempotent)");
        Check(Throws<InvalidOperationException>(() => new WorldActivationTransaction(new FakeHost(store, catalog, new Process(), earlierWorld), journals).Run(context(earlierWorld, i1))),
              "admit: an instance that already has an activation journal is refused");
        var unused = NewWorld();
        Check(Throws<ArgumentException>(() => new WorldActivationTransaction(new FakeHost(store, catalog, new Process(), unused), journals).Run(context(unused, new string('a', 32)))),
              "admit: an instance that is not a UUID's 32 hex digits is refused before anything is written");

        // 2. A failure injected at each boundary (before its work), and inside the work for the boundaries with partial side effects.
        foreach (var boundary in WorldActivationTransaction.Order)
            foreach (var mode in new[] { "at", "inside", "dirty" })
            {
                bool inside = mode != "at";
                if (inside && boundary != ActivationBoundary.PromotePackage && boundary != ActivationBoundary.StartCampaign && boundary != ActivationBoundary.PostBuild) continue;
                if (mode == "dirty" && boundary == ActivationBoundary.PostBuild) continue;   // PostBuild has no side effect of its own
                string label = (mode == "at" ? "failure at " : mode == "inside" ? "failure inside " : "failure inside (host leaves its partial work) ") + boundary;
                string instance = WorldBinding.NewInstance(); var world = NewWorld();
                var process = new Process();
                var host = new FakeHost(store, catalog, process, world) { FailInside = inside ? boundary : (ActivationBoundary?)null, Dirty = mode == "dirty" };
                ActivationException caught = null;
                try { new WorldActivationTransaction(host, new ActivationJournalStore(store)).Run(context(world, instance), inside ? (ActivationBoundary?)null : boundary); }
                catch (ActivationException e) { caught = e; }
                bool receiptCase = boundary == ActivationBoundary.WriteReceipt;
                Check(caught != null && caught.Boundary == boundary && caught.Recoverable == receiptCase && process.Campaign == (receiptCase ? process.Campaign : null) && (receiptCase || process.Registered.Count == 0),
                      label + ": the transaction throws at the boundary" + (receiptCase ? " (recoverable: the world is published whole, nothing is undone)" : ", undoes what completed in reverse order and leaves no campaign or registered object"));
                var undone = host.Log.Where(l => l.StartsWith("undo ")).Select(l => l.Substring(5)).ToList();
                var ran = host.Log.Where(l => l.StartsWith("run ")).Select(l => l.Substring(4)).ToList();
                // The failing boundary's own leftovers first when its work began, then every completed boundary in reverse.
                var expectedUndo = receiptCase ? new List<string>() : (inside ? new[] { boundary.ToString() } : new string[0]).Concat(WorldActivationTransaction.Order.Where(b => b < boundary).Reverse().Select(b => b.ToString())).ToList();
                Check(undone.SequenceEqual(expectedUndo), label + ": undone in reverse ownership order: " + (undone.Count == 0 ? "nothing" : String.Join(" < ", undone)) + " (ran " + String.Join(", ", ran) + ")");
                var row = new Dictionary<string, object> { ["boundary"] = boundary.ToString(), ["mode"] = mode == "at" ? "injected failure" : mode == "inside" ? "failure inside the work" : "failure inside the work, partial work left to the undo",
                    ["recoverable"] = caught != null && caught.Recoverable };
                var recovered = Recover(store, catalog);
                row["recovery"] = recovered.Select(r => r["outcome"]).ToList();
                Verify(label, store, catalog, world, instance, receiptCase ? ActivationJournalStore.Committed : ActivationJournalStore.Failed, false, row);
            }

        // 3. A process stop after each boundary (no undo, no journal transition: the process is gone), then recovery at the next start.
        foreach (var boundary in WorldActivationTransaction.Order)
        {
            string label = "process stop after " + boundary;
            string instance = WorldBinding.NewInstance(); var world = NewWorld();
            var host = new FakeHost(store, catalog, new Process(), world);
            bool stopped = false;
            try { new WorldActivationTransaction(host, new ActivationJournalStore(store)).Run(context(world, instance), null, boundary); }
            catch (SimulatedProcessStop) { stopped = true; }
            var left = new ActivationJournalStore(store).Journal(instance);
            Check(stopped && left.State == ActivationJournalStore.Active && left.Has(boundary), label + ": the journal is left active with " + boundary + " journaled");
            var recovered = Recover(store, catalog);
            bool published = boundary >= ActivationBoundary.PublishSave;
            var row = new Dictionary<string, object> { ["boundary"] = boundary.ToString(), ["mode"] = "process stop after the boundary", ["packageKept"] = !published && boundary >= ActivationBoundary.PromotePackage,
                ["recovery"] = recovered.Select(r => r["outcome"]).ToList() };
            Verify(label, store, catalog, world, instance, published ? ActivationJournalStore.Committed : ActivationJournalStore.Abandoned, false, row);
            if (!published && boundary >= ActivationBoundary.PromotePackage)
                Directory.Delete(PackageDirectory(store, world.Binding(instance).PackageName), true);   // the abandoned, unbound package (MMR-47's sweep); the next case starts clean
        }

        // 4. The initial save's publication: stops inside it, a save list that cannot be read back, a torn save, a name another campaign holds.
        var publishCases = new[] {
            new { Mode = Publish.StopBeforeEngineSave, Label = "process stop inside PublishSave before the engine save", Expected = ActivationJournalStore.Abandoned, Kept = true },
            new { Mode = Publish.StopAfterEngineSave, Label = "process stop inside PublishSave after the engine save", Expected = ActivationJournalStore.Committed, Kept = false },
            new { Mode = Publish.Unresolved, Label = "the initial save cannot be read back (publication unresolved)", Expected = ActivationJournalStore.Committed, Kept = false },
            new { Mode = Publish.TornSave, Label = "the initial save is torn", Expected = ActivationJournalStore.Failed, Kept = false },
            new { Mode = Publish.TakenName, Label = "another campaign's save holds the reserved name", Expected = ActivationJournalStore.Failed, Kept = false } };
        foreach (var c in publishCases)
        {
            string instance = WorldBinding.NewInstance(); var world = NewWorld();
            var process = new Process();
            var host = new FakeHost(store, catalog, process, world) { PublishMode = c.Mode };
            string thrown = null; bool recoverable = false;
            try { new WorldActivationTransaction(host, new ActivationJournalStore(store)).Run(context(world, instance)); thrown = "none"; }
            catch (SimulatedProcessStop) { thrown = "stop"; }
            catch (ActivationException e) { thrown = e.Boundary.ToString(); recoverable = e.Recoverable; }
            if (c.Mode == Publish.Unresolved)
            {
                var pending = new ActivationJournalStore(store).Journal(instance);
                Check(thrown == "PublishSave" && recoverable && pending.State == ActivationJournalStore.Active && process.Campaign == null
                      && pending.Rollback.Any(r => Equals(r["boundary"], "PromotePackage") && r.ContainsKey("kept")) && h1.Binding.Journal(instance).State == EarlyWorldBindingStore.Intent,
                      c.Label + ": recoverable; the live campaign is undone, the package kept, the activation journal stays active beside the save's unresolved intent");
            }
            else if (c.Mode == Publish.TornSave || c.Mode == Publish.TakenName)
                Check(thrown == "PublishSave" && !recoverable && process.Campaign == null, c.Label + ": the activation fails at PublishSave and is undone");
            var recovered = Recover(store, catalog);
            var row = new Dictionary<string, object> { ["boundary"] = "PublishSave", ["mode"] = c.Label, ["packageKept"] = c.Kept, ["recovery"] = recovered.Select(r => r["outcome"]).ToList() };
            Verify(c.Label, store, catalog, world, instance, c.Expected, false, row);
            if (c.Mode == Publish.TakenName)
            {
                var other = catalog.Saves[EarlyWorldBindingStore.InitialSaveName(world.Binding(instance))];
                catalog.Saves.Remove(other.Name);   // the scripted other campaign's save (it has no activation of its own in this store)
            }
            if (c.Kept) Directory.Delete(PackageDirectory(store, world.Binding(instance).PackageName), true);
        }

        // 4b. The journal cannot be written right after the initial save was published: the world is published whole, so nothing is undone and
        //     recovery commits it with its receipt (never a bound save whose package was rolled back).
        {
            string instance = WorldBinding.NewInstance(); var world = NewWorld();
            var process = new Process();
            var faulty = new ActivationJournalStore(store) { BeforeWrite = note => { if (note == "completed PublishSave") throw new IOException("the disk is full"); } };
            ActivationException caught = null;
            try { new WorldActivationTransaction(new FakeHost(store, catalog, process, world), faulty).Run(context(world, instance)); } catch (ActivationException e) { caught = e; }
            var left = new ActivationJournalStore(store).Journal(instance);
            Check(caught != null && caught.Recoverable && caught.Boundary == ActivationBoundary.PublishSave && process.Campaign != null && left.State == ActivationJournalStore.Active,
                  "journal write fault after the save was published: recoverable, nothing undone (the campaign and the package stay), the journal left active (the note written after the fault records PublishSave: " + left.Has(ActivationBoundary.PublishSave) + ")");
            var row = new Dictionary<string, object> { ["boundary"] = "PublishSave", ["mode"] = "the journal write after the published save fails", ["recovery"] = Recover(store, catalog).Select(r => r["outcome"]).ToList() };
            Verify("journal write fault after publication", store, catalog, world, instance, ActivationJournalStore.Committed, false, row);
        }

        // 5. Undo that does not complete is recorded, never hidden.
        {
            string instance = WorldBinding.NewInstance(); var world = NewWorld();
            var process = new Process();
            var host = new FakeHost(store, catalog, process, world) { UndoFails = ActivationBoundary.BindIdentity };
            try { new WorldActivationTransaction(host, new ActivationJournalStore(store)).Run(context(world, instance), ActivationBoundary.PublishSave); } catch (ActivationException) { }
            var failed = new ActivationJournalStore(store).Journal(instance);
            var bindRow = failed.Rollback.FirstOrDefault(r => Equals(r["boundary"], "BindIdentity"));
            Check(failed.State == ActivationJournalStore.Failed && bindRow != null && Equals(bindRow["undone"], false) && ((string)bindRow["error"]).Contains("did not complete")
                  && failed.Rollback.Count == 8 && failed.Rollback.Count(r => Equals(r["undone"], true)) == 7, "undo failure: an undo that throws is journaled with its error and the remaining undos still run");
            var row = new Dictionary<string, object> { ["boundary"] = "PublishSave", ["mode"] = "injected failure, BindIdentity's undo fails", ["recovery"] = Recover(store, catalog).Select(r => r["outcome"]).ToList() };
            Verify("undo failure", store, catalog, world, instance, ActivationJournalStore.Failed, false, row);
        }

        // 6. The earlier world's content again (a second campaign of the same world): the package is shared, so a failure never deletes it,
        //    and a success binds a second instance to it with its own save and receipt.
        {
            string instance = WorldBinding.NewInstance();
            bool shared = PackageExists(store, earlierWorld);
            var host = new FakeHost(store, catalog, new Process(), earlierWorld);
            try { new WorldActivationTransaction(host, new ActivationJournalStore(store)).Run(context(earlierWorld, instance), ActivationBoundary.PostBuild); } catch (ActivationException) { }
            var row = new Dictionary<string, object> { ["boundary"] = "PostBuild", ["mode"] = "injected failure, same world content as the earlier world", ["recovery"] = Recover(store, catalog).Select(r => r["outcome"]).ToList() };
            Check(shared, "same world: the earlier world's package is in the store before the second activation");
            Verify("same world, failure at PostBuild", store, catalog, earlierWorld, instance, ActivationJournalStore.Failed, true, row);
            string second = WorldBinding.NewInstance();
            new WorldActivationTransaction(new FakeHost(store, catalog, new Process(), earlierWorld), new ActivationJournalStore(store)).Run(context(earlierWorld, second));
            var row2 = new Dictionary<string, object> { ["boundary"] = "none", ["mode"] = "second campaign of the same world", ["recovery"] = Recover(store, catalog).Select(r => r["outcome"]).ToList() };
            Verify("same world, second campaign", store, catalog, earlierWorld, second, ActivationJournalStore.Committed, true, row2);
            var r1 = ActivationReceipt.Read(journals, i1); var r2 = ActivationReceipt.Read(journals, second);
            Check(Equals(r1["worldContentId"], r2["worldContentId"]) && !Equals(r1["campaignInstanceId"], r2["campaignInstanceId"]),
                  "same world: identical world content backs two campaign instances, each with its own receipt (the content id is unchanged)");
        }

        // 7. Admission: one activation at a time; an unreadable journal stops the next one; recovery reports what it cannot read.
        {
            var world = NewWorld();
            string running = WorldBinding.NewInstance();
            try { new WorldActivationTransaction(new FakeHost(store, catalog, new Process(), world), new ActivationJournalStore(store)).Run(context(world, running), null, ActivationBoundary.VerifyData); }
            catch (SimulatedProcessStop) { }
            string next = WorldBinding.NewInstance();
            bool refused = Throws<InvalidOperationException>(() => new WorldActivationTransaction(new FakeHost(store, catalog, new Process(), world), new ActivationJournalStore(store)).Run(context(world, next)));
            Check(refused && new ActivationJournalStore(store).Journal(next) == null, "admit: a second activation while one is active (unrecovered) is refused and writes nothing");
            Recover(store, catalog);
            string bad = Path.Combine(store, "activations", WorldBinding.NewInstance() + ".json");
            File.WriteAllText(bad, "{ not json");
            string third = WorldBinding.NewInstance();
            bool unreadable = Throws<InvalidDataException>(() => new WorldActivationTransaction(new FakeHost(store, catalog, new Process(), world), new ActivationJournalStore(store)).Run(context(world, third)));
            var report = Recover(store, catalog);
            bool moved = !File.Exists(bad) && File.Exists(bad + ActivationJournalStore.QuarantineSuffix);
            Check(unreadable && report.Any(r => Equals(r["outcome"], "unreadable, quarantined")) && moved,
                  "admit: an unreadable activation journal refuses the next activation (fail closed); recovery reports it and moves it aside as evidence (" + Path.GetFileName(bad) + ActivationJournalStore.QuarantineSuffix + ")");
            // A journal recovery quarantined (its data does not hold) no longer stops activations either.
            string stuck = WorldBinding.NewInstance(); var stuckWorld = NewWorld();
            try { new WorldActivationTransaction(new FakeHost(store, catalog, new Process(), stuckWorld), new ActivationJournalStore(store)).Run(context(stuckWorld, stuck), null, ActivationBoundary.VerifyData); } catch (SimulatedProcessStop) { }
            var marking = new ActivationJournalStore(store); marking.Quarantine(marking.Journal(stuck), "test: its evidence does not hold");
            string fourth = WorldBinding.NewInstance(); var fourthWorld = NewWorld();
            var committed = new WorldActivationTransaction(new FakeHost(store, catalog, new Process(), fourthWorld), new ActivationJournalStore(store)).Run(context(fourthWorld, fourth));
            var quarantineRow = new Dictionary<string, object> { ["boundary"] = "none", ["mode"] = "an activation after an unreadable and a quarantined journal", ["recovery"] = Recover(store, catalog).Select(r => r["outcome"]).ToList() };
            Check(committed.State == ActivationJournalStore.Committed && new ActivationJournalStore(store).Journal(stuck).State == ActivationJournalStore.Quarantined,
                  "recovery: after the unreadable journal was moved aside and a stuck one quarantined, the next activation commits");
            Verify("activation after quarantine", store, catalog, fourthWorld, fourth, ActivationJournalStore.Committed, false, quarantineRow);
        }

        // 8. A receipt whose runtime-derived record was altered does not verify.
        {
            string derived = Path.Combine(journals.InstanceDirectory(i1), "runtime-derived.json");
            byte[] original = File.ReadAllBytes(derived);
            File.WriteAllText(derived, "{}");
            bool refused = Throws<InvalidDataException>(() => ActivationReceipt.Read(journals, i1));
            File.WriteAllBytes(derived, original);
            Check(refused && ActivationReceipt.Read(journals, i1) != null, "receipt: a receipt whose runtime-derived record was altered does not verify");
        }
    }
}
