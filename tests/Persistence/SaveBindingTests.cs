using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.Persistence;

// MMR-48 save binding tests, without the game:
//   SaveBindingTests REPO OUT_JSON
// Later saves (Save As, quick save, autosave, Ironman) journaled through the shared journal against a real save folder of synthetic saves in
// the installed format: each kind published by readback; a replaced file, a reported failure and a refused save; one interrupted-save
// recovery test over the crash points (before the write, a torn header, torn game data, after the write, another campaign's or an unbound
// file, the save list unreadable, a torn temporary journal), idempotent, with the in-flight save counted by the reference index; the
// refusals that write nothing; journal validation and pruning. The reference index with every save kind bound and its reference counts;
// the compatibility header keys; the save type ledger against its frozen fixture and the code; the contract schema against the documents
// the code writes; export and import of saves with their world (verification, tampering, dedup, conflicts, the store lock, crash
// leftovers) and immutable content unchanged by mutable saves.
internal static class SaveBindingTests
{
    private static int checks, failures;
    private static readonly List<string> lines = new List<string>();
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; string line = (condition ? "ok    " : "FAIL  ") + label; lines.Add(line); Console.WriteLine(line); }
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static string repo;

    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("usage: SaveBindingTests REPO OUT_JSON"); return 2; }
        repo = Path.GetFullPath(args[0]);
        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.save-binding-tests", ["version"] = 1, ["ticket"] = "MMR-48" };
        string scratch = Path.Combine(Path.GetTempPath(), "mnbl-mmr48-" + Guid.NewGuid().ToString("N"));
        try { Run(scratch, evidence); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        finally { try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch (Exception) { } }
        evidence["checks"] = checks; evidence["failures"] = failures; evidence["lines"] = lines;
        File.WriteAllText(args[1], Json.Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ fixtures

    private static readonly string Manifest = new string('a', 64);
    private static int clockTicks;
    private static DateTime Clock() { return new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc).AddMilliseconds(++clockTicks); }

    private static WorldBinding World(string id = "mnbl_00048001", string manifest = null, string content = WorldBinding.NoContent, string instance = null)
    {
        return new WorldBinding { WorldId = id, ManifestSha256 = manifest ?? Manifest, Instance = instance ?? WorldBinding.NewInstance(), ContentId = content };
    }

    private static Dictionary<string, string> Header(WorldBinding b, string unique)
    {
        var h = new Dictionary<string, string>(StringComparer.Ordinal) { ["UniqueGameId"] = unique, ["ApplicationVersion"] = "v1.4.8.119303" };
        if (b != null)
        {
            h["mnbl.world.schema"] = "mnbl.world-identity.v1"; h["mnbl.world.id"] = b.WorldId; h["mnbl.world.manifestSha256"] = b.ManifestSha256; h["mnbl.world.package"] = b.PackageName;
            h["mnbl.world.contentId"] = b.ContentId; if (b.Instance != null) h["mnbl.world.instance"] = b.Instance;
            foreach (var e in Compat()) h[SaveBindingContract.Prefix + e.Key] = e.Value;
        }
        return h;
    }

    private static Dictionary<string, string> Compat(string game = "v1.4.8.119303")
    {
        return SaveBindingContract.Compatibility("v0.0.1", new string('d', 64), game, "v1.2.8.119303", "mnbl.runtime-world-backend/R07; producerLock 000000000000",
            "mnbl.navigation-cache-key/1; adapter mmr41/1", SaveIds.Base + "/v" + SaveIds.Version, "mnbl.world-identity.v1");
    }

    // A save in the installed format: Int32 header length, JSON {"List": metadata}, then raw deflated game data.
    private static byte[] SaveBytes(IDictionary<string, string> metadata, int seed)
    {
        var random = new Random(seed);
        Func<int, byte[]> bytes = n => { var b = new byte[n]; random.NextBytes(b); return b; };
        using (var file = new MemoryStream())
        {
            var json = Encoding.UTF8.GetBytes(Json.Serialize(new Dictionary<string, object> { ["List"] = metadata.ToDictionary(k => k.Key, k => (object)k.Value) }));
            file.Write(BitConverter.GetBytes(json.Length), 0, 4); file.Write(json, 0, json.Length);
            using (var deflate = new DeflateStream(file, CompressionLevel.Fastest, true))
            using (var writer = new BinaryWriter(deflate))
            {
                var h = bytes(200); writer.Write(h.Length); writer.Write(h);
                writer.Write(12); for (int i = 0; i < 12; i++) { var o = bytes(random.Next(10, 3000)); writer.Write(o.Length); writer.Write(o); }
                writer.Write(3); for (int i = 0; i < 3; i++) { var c = bytes(random.Next(10, 900)); writer.Write(c.Length); writer.Write(c); }
                var s = bytes(1500); writer.Write(s.Length); writer.Write(s);
            }
            return file.ToArray();
        }
    }

    private static int writes;
    // Writes a save file as the engine does (one WriteAllBytes) and gives it a distinct write time.
    private static void Write(string folder, string name, byte[] bytes)
    {
        string path = Path.Combine(folder, name + ".sav");
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++writes));
    }
    private static void WriteBound(string folder, string name, WorldBinding b, int seed) { Write(folder, name, SaveBytes(Header(b, "game-" + name + "-" + seed), seed)); }
    private static byte[] TornData(byte[] whole) { return whole.Take(whole.Length - 600).ToArray(); }
    private static byte[] TornHeader(byte[] whole) { return whole.Take(10).ToArray(); }

    private sealed class Env
    {
        public string Store, Saves;
        public FolderSaveCatalog Catalog;
        public bool LockHeld = true;
        public SaveJournal Journal() { return new SaveJournal(Store, Catalog, Catalog.Stat, () => LockHeld, Clock); }
        public EarlyWorldBindingStore Initial() { return new EarlyWorldBindingStore(Store, Catalog, b => true, () => LockHeld, Clock); }
    }

    private static Env NewEnv(string scratch, string name)
    {
        var e = new Env { Store = Path.Combine(scratch, name, "store"), Saves = Path.Combine(scratch, name, "Game Saves") };
        Directory.CreateDirectory(e.Store); Directory.CreateDirectory(e.Saves);
        e.Catalog = new FolderSaveCatalog(e.Saves);
        return e;
    }

    // A catalog whose reads fail (the save folder unreadable).
    private sealed class FailingCatalog : ISaveCatalog
    {
        public IList<SaveHeader> List() { throw new IOException("save folder unreadable"); }
        public SaveHeader Read(string name) { throw new IOException("save folder unreadable"); }
        public bool Delete(string name) { throw new IOException("save folder unreadable"); }
        public void Flush(string name) { throw new IOException("save folder unreadable"); }
    }

    // ------------------------------------------------------------------ run

    private static void Run(string scratch, SortedDictionary<string, object> evidence)
    {
        Journals(scratch, evidence);
        Interrupted(scratch, evidence);
        Index(scratch, evidence);
        Contract(evidence);
        Ledger(evidence);
        Transfer(scratch, evidence);
    }

    // 1. Later saves of every kind, a replaced file, a reported failure, a refused save; refusals; validation; pruning.
    private static void Journals(string scratch, SortedDictionary<string, object> evidence)
    {
        var env = NewEnv(scratch, "kinds");
        var b = World();
        var rows = new List<object>();
        int seed = 1;
        foreach (var kind in SaveJournal.Kinds)
        {
            string name = kind == SaveJournal.AutoSave ? "saveauto1" : kind == SaveJournal.QuickSave ? "save001" : kind == SaveJournal.Ironman ? "Ironman" + b.Instance.Substring(0, 8) : "My campaign";
            var journal = env.Journal();
            var op = journal.Begin(b, kind, name);
            bool intentDurable = File.Exists(Path.Combine(journal.Directory, op.Id + ".json")) && journal.Journal(op.Id).State == EarlyWorldBindingStore.Intent && !op.PriorExists;
            WriteBound(env.Saves, name, b, seed++);
            var done = journal.Complete(op.Id, true, name);
            bool ok = intentDurable && done.State == EarlyWorldBindingStore.Published && done.UniqueGameId != null && done.UniqueGameId.StartsWith("game-" + name, StringComparison.Ordinal)
                && done.History.Count == 2 && journal.Journal(op.Id).State == EarlyWorldBindingStore.Published;
            rows.Add(new Dictionary<string, object> { ["kind"] = kind, ["name"] = name, ["state"] = done.State, ["passed"] = ok });
            Check(ok, "a " + kind + " save: intent durable before the write, published after its readback bound to this instance and whole, its UniqueGameId recorded");
            Check(SchemaValid("saveJournal", Path.Combine(journal.Directory, op.Id + ".json")), "the " + kind + " journal is a save-binding.schema.json saveJournal");
        }
        evidence["kinds"] = rows;
        // The initial save's journal (MMR-62's, shared) is described by the same contract; a bad journal fails it (the validator is not vacuous).
        {
            var store = new EarlyWorldBindingStore(env.Store, env.Catalog, x => true, () => true, Clock);
            var initial = World("mnbl_00048003");
            store.Begin(initial, EarlyWorldBindingStore.InitialSaveName(initial));
            var bad = (Dictionary<string, object>)Json.DeserializeObject(File.ReadAllText(Directory.GetFiles(env.Journal().Directory, "*.json").First()));
            bad["kind"] = "screenshot";
            Check(SchemaValid("initialSaveJournal", Path.Combine(store.JournalDirectory, initial.Instance + ".json")) && !SchemaValidObject("saveJournal", bad),
                "the initial-save journal is a save-binding.schema.json initialSaveJournal; a later-save journal of an unknown kind is not a saveJournal");
        }
        // A quick save over the campaign's own earlier save (the name the Save As set).
        {
            var journal = env.Journal();
            var before = env.Catalog.Stat("My campaign");
            var op = journal.Begin(b, SaveJournal.QuickSave, "My campaign");
            WriteBound(env.Saves, "My campaign", b, seed++);
            var done = journal.Complete(op.Id, true, "My campaign");
            Check(op.PriorExists && op.PriorLength == before.Length && done.State == EarlyWorldBindingStore.Published, "a quick save over the campaign's earlier save records the file it replaces and is published");
        }
        // The engine reports a failure and wrote nothing.
        {
            var journal = env.Journal();
            var op = journal.Begin(b, SaveJournal.SaveAs, "never written");
            var done = journal.Complete(op.Id, false, "never written");
            Check(done.State == EarlyWorldBindingStore.Abandoned && done.ReportedSuccess == false && !File.Exists(Path.Combine(env.Saves, "never written.sav")), "a reported failure with no file is abandoned");
        }
        // The game refuses the save before writing (OverwriteSaveAux's maximum: an empty name) over another campaign's file: kept, abandoned.
        {
            var other = World("mnbl_00048002");
            WriteBound(env.Saves, "theirs", other, 99);
            var bytes = File.ReadAllBytes(Path.Combine(env.Saves, "theirs.sav"));
            var journal = env.Journal();
            var op = journal.Begin(b, SaveJournal.SaveAs, "theirs");
            var done = journal.Complete(op.Id, false, "");
            Check(done.State == EarlyWorldBindingStore.Abandoned && done.LastNote.Contains("unchanged since the intent") && done.ReportedName == "" && File.ReadAllBytes(Path.Combine(env.Saves, "theirs.sav")).SequenceEqual(bytes),
                "a save the game refused before writing (an empty reported name) is abandoned; the file that had the name is unchanged and kept");
        }
        // Refusals write nothing.
        {
            var journal = env.Journal();
            int before = Directory.GetFiles(journal.Directory).Length;
            bool legacy = Throws(() => journal.Begin(new WorldBinding { WorldId = b.WorldId, ManifestSha256 = Manifest, ContentId = WorldBinding.NoContent }, SaveJournal.SaveAs, "x"));
            bool badName = Throws(() => journal.Begin(b, SaveJournal.SaveAs, "..\\outside"));
            bool badKind = Throws(() => journal.Begin(b, "screenshot", "x"));
            env.LockHeld = false;
            bool noLock = Throws(() => env.Journal().Begin(b, SaveJournal.SaveAs, "x"));
            env.LockHeld = true;
            Check(legacy && badName && badKind && noLock && Directory.GetFiles(journal.Directory).Length == before,
                "refusals write nothing: a binding without a campaign instance, a name that is not a plain file name, an unknown kind, a process without the store lock");
        }
        // Journal validation: unreadable journals are reported, never acted on.
        {
            var v = NewEnv(scratch, "validation");
            var journal = v.Journal();
            var op = journal.Begin(b, SaveJournal.AutoSave, "saveauto2");
            string path = Path.Combine(journal.Directory, op.Id + ".json");
            string renamed = Path.Combine(journal.Directory, "20261017T120000000Z-00000000.json");
            File.Copy(path, renamed);
            File.WriteAllText(Path.Combine(journal.Directory, "20261017T120000001Z-11111111.json"), "{not json");
            var text = File.ReadAllText(path);
            File.WriteAllText(Path.Combine(journal.Directory, "20261017T120000002Z-22222222.json"), text.Replace(op.Id, "20261017T120000002Z-22222222").Replace("\"autoSave\"", "\"screenshot\""));
            File.WriteAllText(Path.Combine(journal.Directory, "20261017T120000003Z-33333333.json"), Regex.Replace(text.Replace(op.Id, "20261017T120000003Z-33333333"), "\"prior\":\\{[^}]*\\},", ""));
            WriteBound(v.Saves, "saveauto2", World(), 5);   // another campaign's save under the name the valid intent names
            var report = journal.Recover();
            Check(report.UnreadableJournals.Count == 4 && report.Resolved.Count == 1 && File.Exists(Path.Combine(v.Saves, "saveauto2.sav")),
                "a renamed, corrupt, unknown-kind or prior-less journal is unreadable and never acted on; the valid one is resolved (a conflict: another campaign's save, kept)");
            var index = WorldReferenceIndex.Build(v.Catalog, v.Initial(), null, true, journal);
            Check(!index.DiscoveryComplete && index.Gaps.Count(g => g.StartsWith("save journal ", StringComparison.Ordinal)) == 4, "each unreadable save journal is a gap in the reference index");
        }
        // Pruning keeps the newest resolved journals and every intent.
        {
            var p = NewEnv(scratch, "prune");
            var journal = p.Journal();
            for (int i = 0; i < SaveJournal.ResolvedKept + 6; i++) { var op = journal.Begin(b, SaveJournal.AutoSave, "gone" + i); journal.Complete(op.Id, false, "gone" + i); }
            var open = journal.Begin(b, SaveJournal.SaveAs, "in flight");
            journal.Recover();   // resolves the intent (abandoned) and prunes
            List<string> unreadable;
            var left = journal.Journals(out unreadable);
            Check(left.Count == SaveJournal.ResolvedKept && left.Any(j => j.Id == open.Id) && unreadable.Count == 0, "pruning keeps the newest " + SaveJournal.ResolvedKept + " resolved journals");
            var intent = journal.Begin(b, SaveJournal.SaveAs, "still open");
            p.LockHeld = false;
            Check(journal.Prune() == 0 && journal.Journal(intent.Id).State == EarlyWorldBindingStore.Intent, "without the store lock nothing is pruned; an intent is never pruned");
        }
    }

    // 2. The one interrupted-save recovery test: the shared journal across the crash points of a later save (MMR-62 owns the initial-save
    //    boundary matrix). Each case: intent written, then the "engine" leaves the file as a crash would, then a new process recovers.
    private static void Interrupted(string scratch, SortedDictionary<string, object> evidence)
    {
        var env = NewEnv(scratch, "interrupted");
        var mine = World("mnbl_00048010", content: new string('c', 64));
        var theirs = World("mnbl_00048011");
        var whole = SaveBytes(Header(mine, "mine"), 7);
        var cases = new List<Tuple<string, Action<string>, Func<SaveOperation, bool>, string>> {
            Tuple.Create<string, Action<string>, Func<SaveOperation, bool>, string>("before the write, over another campaign's save",
                n => { }, op => op.State == EarlyWorldBindingStore.Abandoned && File.Exists(Path.Combine(env.Saves, op.SaveName + ".sav")), "abandoned; the earlier file kept"),
            Tuple.Create<string, Action<string>, Func<SaveOperation, bool>, string>("a torn header (a new file)",
                n => Write(env.Saves, n, TornHeader(whole)), op => op.State == EarlyWorldBindingStore.Removed && !File.Exists(Path.Combine(env.Saves, op.SaveName + ".sav")), "removed, deleted"),
            Tuple.Create<string, Action<string>, Func<SaveOperation, bool>, string>("a torn header over another campaign's save",
                n => Write(env.Saves, n, TornHeader(whole)), op => op.State == EarlyWorldBindingStore.Removed && !File.Exists(Path.Combine(env.Saves, op.SaveName + ".sav")), "removed, deleted"),
            Tuple.Create<string, Action<string>, Func<SaveOperation, bool>, string>("this campaign's whole header before torn game data",
                n => Write(env.Saves, n, TornData(whole)), op => op.State == EarlyWorldBindingStore.Removed && op.LastNote.Contains("game data is torn"), "removed, deleted"),
            Tuple.Create<string, Action<string>, Func<SaveOperation, bool>, string>("after the write",
                n => Write(env.Saves, n, whole), op => op.State == EarlyWorldBindingStore.Published && op.UniqueGameId == "mine", "published (flushed)"),
            Tuple.Create<string, Action<string>, Func<SaveOperation, bool>, string>("after the write over another campaign's save",
                n => Write(env.Saves, n, whole), op => op.State == EarlyWorldBindingStore.Published, "published"),
            Tuple.Create<string, Action<string>, Func<SaveOperation, bool>, string>("the file changed to another campaign's save",
                n => WriteBound(env.Saves, n, theirs, 31), op => op.State == EarlyWorldBindingStore.Conflict && File.Exists(Path.Combine(env.Saves, op.SaveName + ".sav")), "conflict, kept"),
            Tuple.Create<string, Action<string>, Func<SaveOperation, bool>, string>("the file changed to an unbound save",
                n => Write(env.Saves, n, SaveBytes(Header(null, "vanilla"), 32)), op => op.State == EarlyWorldBindingStore.Conflict && File.Exists(Path.Combine(env.Saves, op.SaveName + ".sav")), "conflict, kept"),
        };
        var matrix = new List<object>();
        var ids = new Dictionary<string, string>();
        int i = 0;
        foreach (var c in cases)
        {
            string name = "case" + (i++);
            if (c.Item1.Contains("over another campaign's save")) WriteBound(env.Saves, name, theirs, 40 + i);
            var op = env.Journal().Begin(mine, SaveJournal.QuickSave, name);
            ids[c.Item1] = op.Id;
            c.Item2(name);
        }
        // A crash before the intent was durable leaves only a torn temporary journal.
        string later = Path.Combine(env.Store, "saves", "journal", "later");
        File.WriteAllText(Path.Combine(later, "20261017T130000000Z-abcdef01.json" + DurableFile.TemporaryMarker + Guid.NewGuid().ToString("N")), "{\"schema\":\"mnbl.save-jou");
        // While the saves are in flight the reference index counts them.
        var inFlight = WorldReferenceIndex.Build(env.Catalog, env.Initial(), null, true, env.Journal());
        Check(inFlight.References.Count(r => r.Kind == WorldReference.PendingSave) == cases.Count && inFlight.Decide(mine.PackageName).Retain,
            "interrupted: every intent is a pending save in the reference index, so the world it names is retained");
        // The next process: the save list unreadable first (nothing resolved, nothing deleted), then readable.
        var blocked = new SaveJournal(env.Store, new FailingCatalog(), env.Catalog.Stat, () => true, Clock).Recover();
        int filesBefore = Directory.GetFiles(env.Saves).Length;
        Check(blocked.Unresolved.Count == cases.Count && blocked.Resolved.Count == 0 && Directory.GetFiles(env.Saves).Length == filesBefore,
            "interrupted: with the save list unreadable every intent stays unresolved and nothing is deleted");
        var report = env.Journal().Recover();
        foreach (var c in cases)
        {
            var op = env.Journal().Journal(ids[c.Item1]);
            bool ok = c.Item3(op);
            matrix.Add(new Dictionary<string, object> { ["crash"] = c.Item1, ["expected"] = c.Item4, ["state"] = op.State, ["note"] = op.LastNote, ["passed"] = ok });
            Check(ok, "interrupted " + c.Item1 + ": " + c.Item4);
        }
        Check(report.Resolved.Count == cases.Count && report.Unresolved.Count == 0 && report.UnreadableJournals.Count == 0, "interrupted: the torn temporary journal is ignored (staging), never read as a journal");
        var staging = StagingCleanup.Run(env.Store, true, p => false);
        Check(staging.Removed.Count == 1 && !Directory.GetFiles(later).Any(DurableFile.IsTemporary), "interrupted: the startup cleanup removes the torn temporary journal");
        var again = env.Journal().Recover();
        Check(again.Resolved.Count == 0 && again.Unresolved.Count == 0, "interrupted: a second recovery changes nothing");
        var after = WorldReferenceIndex.Build(env.Catalog, env.Initial(), null, true, env.Journal());
        Check(after.DiscoveryComplete && after.References.All(r => r.Kind == WorldReference.Save), "interrupted: after recovery the index is complete: no pending save, no unreadable save left");
        // Unknown stat: a changed-or-not file cannot be told, so an unreadable header is never deleted (SaveReadback, unit).
        var unknown = SaveReadback.Decide(new SaveHeader { Name = "x" }, "x", mine, false, null);
        var unknownTorn = SaveReadback.Decide(new SaveHeader { Name = "x", Metadata = Header(mine, "u"), BodyWhole = false, BodyProblem = "torn" }, "x", mine, false, null);
        var unchecked_ = SaveReadback.Decide(new SaveHeader { Name = "x", Metadata = Header(mine, "u"), BodyWhole = null }, "x", mine, false, true);
        Check(unknown.Action == ReadbackAction.Unresolved && unknownTorn.Action == ReadbackAction.Unresolved && unchecked_.Action == ReadbackAction.Unresolved,
            "interrupted: when whether the file changed cannot be told, or its game data could not be read, nothing is deleted (unresolved)");
        evidence["interrupted"] = matrix;

        // A whole save another process holds (review finding 1): never taken for a torn write.
        var held = NewEnv(scratch, "held");
        var heldOp = held.Journal().Begin(mine, SaveJournal.QuickSave, "held exclusively");
        Write(held.Saves, "held exclusively", whole);
        bool heldKept;
        using (new FileStream(Path.Combine(held.Saves, "held exclusively.sav"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var header = held.Catalog.Read("held exclusively");
            heldKept = header != null && !header.Readable && header.HeaderUnknown && Throws(() => held.Journal().Complete(heldOp.Id, true, "held exclusively"));
        }
        heldKept = heldKept && File.Exists(Path.Combine(held.Saves, "held exclusively.sav")) && held.Journal().Journal(heldOp.Id).State == EarlyWorldBindingStore.Intent;
        var syncOp = held.Journal().Begin(mine, SaveJournal.AutoSave, "held by a sync client");
        Write(held.Saves, "held by a sync client", whole);
        bool syncKept;
        using (new FileStream(Path.Combine(held.Saves, "held by a sync client.sav"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            syncKept = Throws(() => held.Journal().Complete(syncOp.Id, true, "held by a sync client"));
        syncKept = syncKept && File.Exists(Path.Combine(held.Saves, "held by a sync client.sav")) && held.Journal().Journal(syncOp.Id).State == EarlyWorldBindingStore.Intent;
        var released = held.Journal().Recover();
        Check(heldKept && syncKept && held.Journal().Journal(heldOp.Id).State == EarlyWorldBindingStore.Published && held.Journal().Journal(syncOp.Id).State == EarlyWorldBindingStore.Published,
            "a whole save another process holds open (exclusively, or writing with shared access as a sync client does) is never deleted: its intent stays unresolved, and is published once the file is released");
        var unknownHeader = SaveReadback.Decide(new SaveHeader { Name = "x", HeaderUnknown = true, HeaderProblem = "IOException: sharing violation" }, "x", mine, true, true);
        Check(unknownHeader.Action == ReadbackAction.Unresolved, "a header that could not be opened is unknown, never torn, even under a reserved name (the initial save)");

        // A stale intent (review finding 6): superseded by a newer journal of the same name, or by a write long after it.
        var stale = NewEnv(scratch, "stale");
        var older = stale.Journal().Begin(mine, SaveJournal.QuickSave, "slot");
        var newer = stale.Journal().Begin(mine, SaveJournal.QuickSave, "slot");
        Write(stale.Saves, "slot", TornHeader(whole));
        var late = stale.Journal().Begin(mine, SaveJournal.AutoSave, "late slot");
        Write(stale.Saves, "late slot", TornHeader(whole));
        File.SetLastWriteTimeUtc(Path.Combine(stale.Saves, "late slot.sav"), DateTime.Parse((string)late.History[0]["utc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime().AddHours(1));
        stale.Journal().Recover();
        var olderAfter = stale.Journal().Journal(older.Id); var newerAfter = stale.Journal().Journal(newer.Id); var lateAfter = stale.Journal().Journal(late.Id);
        Check(olderAfter.State == EarlyWorldBindingStore.Abandoned && olderAfter.LastNote.Contains("superseded") && newerAfter.State == EarlyWorldBindingStore.Removed
              && lateAfter.State == EarlyWorldBindingStore.Abandoned && File.Exists(Path.Combine(stale.Saves, "late slot.sav")),
            "a stale intent is superseded by a newer journal under the same name (only the newest resolves the file), and a file written long after an intent is never that save's (kept)");

        // Any name the engine can write is journaled (review finding 8).
        var named = NewEnv(scratch, "names");
        string spaced = "  My campaign, day 40 ", longName = new string('n', 150);
        var spacedOp = named.Journal().Begin(mine, SaveJournal.SaveAs, spaced); WriteBound(named.Saves, spaced, mine, 61);
        var longOp = named.Journal().Begin(mine, SaveJournal.SaveAs, longName); WriteBound(named.Saves, longName, mine, 62);
        Check(named.Journal().Complete(spacedOp.Id, true, spaced).State == EarlyWorldBindingStore.Published && named.Journal().Complete(longOp.Id, true, longName).State == EarlyWorldBindingStore.Published
              && Throws(() => named.Journal().Begin(mine, SaveJournal.SaveAs, "a\\b")),
            "a Save As name with leading or trailing spaces, or 150 characters long, is journaled and published; a name with a path separator is refused");

        // The index without its journal sources fails closed (review finding 5).
        var noSources = WorldReferenceIndex.Build(env.Catalog, null, null, true, null);
        Check(!noSources.DiscoveryComplete && noSources.Gaps.Count(g => g.Contains("journals were not read")) == 2 && noSources.Decide("mnbl_00048099-aaaaaaaaaaaa").Retain,
            "with every save kind bound, an index built without the initial-save or later-save journals has a gap for each and retains everything");
    }

    // 3. The reference index with every save kind bound, and reference counts.
    private static void Index(string scratch, SortedDictionary<string, object> evidence)
    {
        var env = NewEnv(scratch, "index");
        string content = new string('e', 64);
        var a = World("mnbl_00048020", content: content); var b = new WorldBinding { WorldId = a.WorldId, ManifestSha256 = a.ManifestSha256, ContentId = content, Instance = WorldBinding.NewInstance() };
        var lone = World("mnbl_00048021");
        WriteBound(env.Saves, "a start", a, 1); WriteBound(env.Saves, "a later", a, 2); WriteBound(env.Saves, "saveauto1", b, 3);
        Write(env.Saves, "vanilla", SaveBytes(Header(null, "v"), 4));
        var journal = env.Journal();
        journal.Begin(lone, SaveJournal.SaveAs, "lone in flight");
        var index = WorldReferenceIndex.Build(env.Catalog, env.Initial(), null, WorldReferenceIndex.SaveKindsBound, journal);
        var counts = index.Counts();
        var packages = (Dictionary<string, object>)counts["packages"]; var bundles = (Dictionary<string, object>)counts["bundles"];
        var pa = (Dictionary<string, object>)packages[a.PackageName]; var ba = (Dictionary<string, object>)bundles[content];
        Check(WorldReferenceIndex.SaveKindsBound && index.DiscoveryComplete && index.SavesListed == 4 && index.SavesBound == 3, "index: every save kind is bound (SaveKindsBound); bound saves found by header, whatever their names");
        Check((int)pa["references"] == 3 && (int)pa["saves"] == 3 && (int)pa["instances"] == 2 && (int)ba["references"] == 3 && (int)ba["instances"] == 2,
            "index: one immutable world referenced by two separate campaigns (three saves): the package and the bundle count 3 references and 2 campaign instances");
        Check(index.Decide(lone.PackageName).Retain && index.Decide(lone.PackageName).References.Single().Kind == WorldReference.PendingSave, "index: a later save in flight keeps its world");
        Check(!index.Decide("mnbl_00048099-aaaaaaaaaaaa").Retain && index.Decide(a.PackageName, content).Retain, "index: with discovery complete an unreferenced world may go; a referenced one stays");
        File.Move(Path.Combine(env.Saves, "a later.sav"), Path.Combine(env.Saves, "renamed by the player.sav"));
        var renamed = WorldReferenceIndex.Build(env.Catalog, env.Initial(), null, true, journal);
        Check(renamed.References.Any(r => r.Name == "renamed by the player" && r.Instance == a.Instance) && (int)((Dictionary<string, object>)((Dictionary<string, object>)renamed.Counts()["packages"])[a.PackageName])["saves"] == 3,
            "index: a save the player renamed is still found by its header (no sidecar to follow its name)");
        Write(env.Saves, "torn by a crash", TornHeader(SaveBytes(Header(a, "t"), 9)));
        var gap = WorldReferenceIndex.Build(env.Catalog, env.Initial(), null, true, journal);
        Check(!gap.DiscoveryComplete && gap.Decide("mnbl_00048099-aaaaaaaaaaaa").Retain, "index: an unreadable save is still a gap, and nothing is deletable while there is one");
        evidence["index"] = index.Report();
    }

    // 4. The compatibility header keys.
    private static void Contract(SortedDictionary<string, object> evidence)
    {
        var c = Compat();
        Check(c.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(SaveBindingContract.Keys.OrderBy(k => k, StringComparer.Ordinal)) && c["contract"] == SaveBindingContract.Contract && c["saveIds"] == "7910000/v2",
            "compatibility: every mnbl.save.* key, the contract version and the fixed save id base");
        Check(Throws(() => SaveBindingContract.Compatibility("", "x", "x", "x", "x", "x", "x", "x")) && Throws(() => SaveBindingContract.Compatibility("v\n1", "x", "x", "x", "x", "x", "x", "x")),
            "compatibility: an empty or multi-line value is refused");
        var header = Header(World(), "h");
        var read = SaveBindingContract.CompatibilityFrom(header);
        var differences = SaveBindingContract.Differences(read, Compat("v1.4.9.0"));
        Check(read.Count == SaveBindingContract.Keys.Length && differences.Count == 1 && differences[0].StartsWith("game: saved v1.4.8.119303, running v1.4.9.0", StringComparison.Ordinal)
              && SaveBindingContract.Differences(new Dictionary<string, string>(), c).Count == SaveBindingContract.Keys.Length,
            "compatibility: read back from a header; a difference from the running build is named; a pre-MMR-48 save lacks every key");
        Check(SchemaValidObject("header", header.ToDictionary(k => k.Key, k => (object)k.Value)), "the header entries are a save-binding.schema.json header");
        evidence["compatibility"] = c;
    }

    // 5. The save type ledger: append-only against its frozen fixture, and the code's fixed save definitions.
    private static void Ledger(SortedDictionary<string, object> evidence)
    {
        string ledgerPath = Path.Combine(repo, "worldgen", "contracts", "save-type-ledger.json");
        var ledger = (Dictionary<string, object>)Json.DeserializeObject(File.ReadAllText(ledgerPath));
        var entries = ((IList)ledger["entries"]).Cast<Dictionary<string, object>>().ToList();
        var fixture = Json.DeserializeObject(File.ReadAllText(Path.Combine(repo, "tests", "Persistence", "fixtures", "save-type-ledger.v1.json")));
        Check(SchemaValid("saveTypeLedger", ledgerPath), "the ledger is a save-binding.schema.json saveTypeLedger");
        Check(Canonical(entries[0]) == Canonical(fixture), "ledger: entry 1 is unchanged from its frozen fixture (entries are append-only across module versions)");
        Check(entries.Select(e => (int)e["entry"]).SequenceEqual(Enumerable.Range(1, entries.Count)), "ledger: entries are numbered 1..n in order");
        var last = entries[entries.Count - 1];
        var range = (Dictionary<string, object>)ledger["reservedRange"];
        Check((int)ledger["saveIdBase"] == SaveIds.Base && (int)last["saveIdsVersion"] == SaveIds.Version && SaveIds.Base >= (int)range["first"] && SaveIds.Base <= (int)range["last"],
            "ledger: the code's save id base and version (SaveIds) are the ledger's, inside the reserved range");
        // The code's fixed definitions, scanned from the module source.
        var sources = Directory.GetFiles(Path.Combine(repo, "src", "MNBL.ProceduralWorld"), "*.cs", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllText);
        var definers = sources.SelectMany(s => Regex.Matches(s.Value, @"class\s+(\w+)\s*:\s*(?:TaleWorlds\.SaveSystem\.)?SaveableTypeDefiner\b").Cast<Match>().Select(m => m.Groups[1].Value)).ToList();
        Check(definers.Count == ((IList)last["definers"]).Count && ((IList)last["typeIds"]).Count == 0 && definers.Count == 0, "ledger: the module defines no SaveableTypeDefiner (a world binding is header strings), as the ledger records");
        var calls = sources.SelectMany(s => Regex.Matches(s.Value, @"\.SyncData\(\s*([^,\)]*)\s*,").Cast<Match>().Select(m => m.Groups[1].Value.Trim())).ToList();
        var keys = calls.Where(c => Regex.IsMatch(c, "^\"[^\"]+\"$")).Select(c => c.Trim('"')).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var ledgerKeys = ((IList)last["syncData"]).Cast<Dictionary<string, object>>().Select(r => (string)r["key"]).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Check(calls.Count == keys.Count && keys.SequenceEqual(ledgerKeys), "ledger: every SyncData key in the module is a string literal (never derived from a world) and is listed: " + String.Join(", ", keys));
        var headerKeys = (Dictionary<string, object>)last["headerKeys"];
        var identitySource = sources.First(s => Path.GetFileName(s.Key) == "WorldIdentity.cs").Value;
        var identityKeys = Regex.Matches(Regex.Match(identitySource, @"Keys\s*=\s*\{([^}]*)\}").Groups[1].Value, "\"([^\"]+)\"").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
        // MMR-261: mnbl.campaign.* (WorldMutationBehavior.HeaderKeys).
        var mutationSource = sources.First(s => Path.GetFileName(s.Key) == "WorldMutationBehavior.cs").Value;
        var campaignKeys = Regex.Matches(Regex.Match(mutationSource, @"HeaderKeys\s*=\s*\{([^}]*)\}").Groups[1].Value, "\"([^\"]+)\"").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
        Check(identityKeys.SequenceEqual(((IList)headerKeys["mnbl.world."]).Cast<string>()) && SaveBindingContract.Keys.SequenceEqual(((IList)headerKeys["mnbl.save."]).Cast<string>())
              && headerKeys.ContainsKey("mnbl.campaign.") && campaignKeys.Count > 0 && campaignKeys.SequenceEqual(((IList)headerKeys["mnbl.campaign."]).Cast<string>()),
            "ledger: the header keys (WorldIdentity.Keys, SaveBindingContract.Keys, WorldMutationBehavior.HeaderKeys) are the ledger's");
        var worldId = new Regex("mnbl_[0-9a-f]{8}_", RegexOptions.CultureInvariant);
        Check(!worldId.IsMatch(File.ReadAllText(ledgerPath)), "ledger: no per-world generated object id appears among the fixed save definitions");
        evidence["ledger"] = new Dictionary<string, object> { ["entries"] = entries.Count, ["syncDataKeys"] = keys, ["definers"] = definers.Count };
    }

    // 6. Export and import of saves with their world, without regeneration; dedup and reference counts; immutable content unchanged.
    private static void Transfer(string scratch, SortedDictionary<string, object> evidence)
    {
        var source = NewEnv(scratch, "source");
        var world = MakeWorld(source.Store, "mnbl_00048030", 1);
        var a = new WorldBinding { WorldId = world.WorldId, ManifestSha256 = world.ManifestSha256, ContentId = world.ContentId, Instance = WorldBinding.NewInstance() };
        var b = new WorldBinding { WorldId = world.WorldId, ManifestSha256 = world.ManifestSha256, ContentId = world.ContentId, Instance = WorldBinding.NewInstance() };
        // MMR-49: each campaign's activation evidence, as its activation keeps it.
        foreach (var instance in new[] { a.Instance, b.Instance }) KeepEvidence(source.Store, instance);
        string immutableBefore = TreeHash(Path.Combine(source.Store, "worlds")) + TreeHash(Path.Combine(source.Store, "bundles"));
        // c2: mutable saves of both campaigns (each save a new game state), journaled as later saves.
        var journal = source.Journal();
        int seed = 100;
        foreach (var save in new[] { Tuple.Create("a1", a), Tuple.Create("a2", a), Tuple.Create("b1", b) })
        {
            var op = journal.Begin(save.Item2, SaveJournal.SaveAs, save.Item1);
            WriteBound(source.Saves, save.Item1, save.Item2, seed++);
            journal.Complete(op.Id, true, save.Item1);
        }
        Write(source.Saves, "vanilla", SaveBytes(Header(null, "v"), 7));
        string immutableAfterSaves = TreeHash(Path.Combine(source.Store, "worlds")) + TreeHash(Path.Combine(source.Store, "bundles"));
        Check(immutableBefore == immutableAfterSaves, "c2 (regression guard): journaling two campaigns' saves of changing game states writes nothing under worlds/ or bundles/ (the trees hash the same)");
        {
            string probe = Directory.GetFiles(Path.Combine(source.Store, "bundles"), "*.bin", SearchOption.AllDirectories).First();
            var original = File.ReadAllBytes(probe); var changed = (byte[])original.Clone(); changed[changed.Length / 2] ^= 1;
            File.WriteAllBytes(probe, changed);
            string changedHash = TreeHash(Path.Combine(source.Store, "worlds")) + TreeHash(Path.Combine(source.Store, "bundles"));
            File.WriteAllBytes(probe, original);
            Check(changedHash != immutableBefore && TreeHash(Path.Combine(source.Store, "worlds")) + TreeHash(Path.Combine(source.Store, "bundles")) == immutableBefore,
                "control: one changed byte in a bundle file changes the tree hash the c2 checks compare");
        }
        var opts = new WorldSaveTransfer.Options(source.Store, source.Saves, FakeBundleProblems, Clock);
        string exports = Path.Combine(scratch, "exports");
        {
            string kept = WorldSaveTransfer.EvidencePath(source.Store, b.Instance, "playability.json"), aside = kept + ".aside";
            File.Move(kept, aside);
            bool refusedWithout = Refuses(() => WorldSaveTransfer.Export(opts, new[] { "b1" }, exports, "without evidence"));
            File.Move(aside, kept);
            Check(refusedWithout && !Directory.Exists(Path.Combine(exports, "without evidence")), "export refuses a compiled campaign whose activation evidence is missing (it could not load elsewhere), and writes nothing");
        }
        var exported = WorldSaveTransfer.Export(opts, new[] { "a2", "b1" }, exports, "two campaigns");
        string exportDir = (string)exported["directory"];
        var manifest = WorldSaveTransfer.Verify(exportDir, FakeBundleProblems);
        Check(manifest.Saves.Count == 2 && manifest.Packages.Count == 1 && manifest.Bundles.Count == 1 && Directory.GetDirectories(Path.Combine(exportDir, "bundles")).Length == 1
              && !Directory.GetDirectories(exports).Any(d => Path.GetFileName(d).StartsWith(".mnbl-export-", StringComparison.Ordinal)),
            "export: two campaigns' saves with the one world they share, its package and bundle once, every file listed with its hash; no staging left");
        Check(manifest.Evidence.Count == 2 && manifest.Evidence.Contains(a.Instance) && manifest.Evidence.Contains(b.Instance)
              && WorldSaveTransfer.EvidenceFiles.All(f => File.ReadAllText(Path.Combine(exportDir, "evidence", a.Instance, f)) == File.ReadAllText(WorldSaveTransfer.EvidencePath(source.Store, a.Instance, f))),
            "export: each campaign's activation evidence (navigation, playability, settlement layout reports) is exported with its saves");
        Check(SchemaValid("saveExport", Path.Combine(exportDir, WorldSaveTransfer.ManifestFile)), "export.json is a save-binding.schema.json saveExport");
        Check(immutableBefore == TreeHash(Path.Combine(source.Store, "worlds")) + TreeHash(Path.Combine(source.Store, "bundles")), "export reads the store only");
        Check(Refuses(() => WorldSaveTransfer.Export(opts, new[] { "vanilla" }, exports, "x")) && Refuses(() => WorldSaveTransfer.Export(opts, new[] { "missing" }, exports, "y"))
              && Refuses(() => WorldSaveTransfer.Export(opts, new[] { "a2" }, exports, "two campaigns")) && !Directory.Exists(Path.Combine(exports, "x")),
            "export refuses a save of no generated world, a missing save and an existing destination, and writes nothing");
        string deep = Path.Combine(scratch, new string('d', 140));
        Check(Refuses(() => WorldSaveTransfer.Export(opts, new[] { "a2" }, deep, "too deep")) && !Directory.Exists(deep),
            "export refuses, before writing anything, a destination whose files would pass the Windows path limit");
        // Tampered exports are refused.
        Func<string, Action<string>, bool> tampered = (label, damage) =>
        {
            string copy = Path.Combine(scratch, "tampered", label);
            CopyDirectory(exportDir, copy); damage(copy);
            return Refuses(() => WorldSaveTransfer.Verify(copy, FakeBundleProblems));
        };
        string bundleFile = Directory.GetFiles(Path.Combine(exportDir, "bundles"), "*.bin", SearchOption.AllDirectories).First().Substring(exportDir.Length + 1);
        bool flipped = tampered("flipped", d => { var p = Path.Combine(d, bundleFile); var x = File.ReadAllBytes(p); x[0] ^= 1; File.WriteAllBytes(p, x); });
        bool extra = tampered("extra", d => File.WriteAllText(Path.Combine(d, "worlds", world.Package, "extra.txt"), "x"));
        bool missing = tampered("missing", d => File.Delete(Path.Combine(d, "saves", "b1.sav")));
        bool evidenceFlipped = tampered("evidence", d => File.AppendAllText(Path.Combine(d, "evidence", a.Instance, "navigation.json"), " "));
        bool evidenceMissing = tampered("evidence missing", d =>
        {
            File.Delete(Path.Combine(d, "evidence", b.Instance, "settlement-layout.json"));
            var p = Path.Combine(d, WorldSaveTransfer.ManifestFile);
            var m = (Dictionary<string, object>)Json.DeserializeObject(File.ReadAllText(p));
            m["files"] = ((IList)m["files"]).Cast<object>().Where(f => (string)((Dictionary<string, object>)f)["path"] != "evidence/" + b.Instance + "/settlement-layout.json").ToList();
            File.WriteAllText(p, Json.Serialize(m));
        });
        Check(evidenceFlipped && evidenceMissing, "verify refuses changed activation evidence, and a compiled campaign's save without its evidence (even with the manifest edited to match)");
        bool traversal = tampered("traversal", d => { var p = Path.Combine(d, WorldSaveTransfer.ManifestFile); File.WriteAllText(p, File.ReadAllText(p).Replace("\"saves/b1.sav\"", "\"saves/../../b1.sav\"")); });
        bool rebound = tampered("rebound", d => { var p = Path.Combine(d, WorldSaveTransfer.ManifestFile); File.WriteAllText(p, File.ReadAllText(p).Replace(b.Instance, WorldBinding.NewInstance())); });
        bool orphanSave = tampered("orphan", d =>
        {
            File.Copy(Path.Combine(d, "saves", "b1.sav"), Path.Combine(d, "saves", "extra.sav"));
            var p = Path.Combine(d, WorldSaveTransfer.ManifestFile);
            var m = (Dictionary<string, object>)Json.DeserializeObject(File.ReadAllText(p));
            var files = ((IList)m["files"]).Cast<object>().ToList();
            var row = (Dictionary<string, object>)files.First(f => (string)((Dictionary<string, object>)f)["path"] == "saves/b1.sav");
            files.Add(new Dictionary<string, object> { ["path"] = "saves/extra.sav", ["length"] = row["length"], ["sha256"] = row["sha256"] });
            m["files"] = files;
            File.WriteAllText(p, Json.Serialize(m));
        });
        Check(flipped && extra && missing && traversal && rebound && orphanSave,
            "verify refuses a changed byte, an unlisted file, a missing file, a path leaving the export, a save row naming another campaign than its header, and a listed save file no save row names");
        // An export that does not verify never stands under its name (review finding 4).
        Func<string, bool, IList<string>> refusingCopies = (d, n) => d.StartsWith(Path.Combine(scratch, "exports-refused"), StringComparison.OrdinalIgnoreCase) ? new List<string> { "refused for the test" } : FakeBundleProblems(d, n);
        bool refusedExport = Refuses(() => WorldSaveTransfer.Export(new WorldSaveTransfer.Options(source.Store, source.Saves, refusingCopies, Clock), new[] { "a2" }, Path.Combine(scratch, "exports-refused"), "x"));
        Check(refusedExport && !Directory.Exists(Path.Combine(scratch, "exports-refused", "x")) && Directory.GetDirectories(Path.Combine(scratch, "exports-refused")).Length == 0,
            "an export whose copy does not verify is removed before it takes its name (no folder left, a retry is not refused as existing)");
        // Import into an empty store.
        var target = NewEnv(scratch, "target");
        var importOptions = new WorldSaveTransfer.Options(target.Store, target.Saves, FakeBundleProblems, Clock);
        var first = WorldSaveTransfer.Import(importOptions, exportDir);
        var firstSaves = (List<Dictionary<string, object>>)first["saves"];
        var countsAfter = (Dictionary<string, object>)((Dictionary<string, object>)first["countsAfter"])["packages"];
        Check((string)((Dictionary<string, object>)first["packages"])[world.Package] == "copied" && (string)((Dictionary<string, object>)first["bundles"])[world.ContentId] == "copied"
              && firstSaves.Count == 2 && Equals(first["regenerated"], false) && Equals(first["indexComplete"], true)
              && (int)((Dictionary<string, object>)countsAfter[world.Package])["references"] == 2 && (int)((Dictionary<string, object>)countsAfter[world.Package])["instances"] == 2,
            "import: the package and bundle copied into the empty store, both saves imported; the world counts 2 references from 2 campaigns; nothing regenerated");
        var firstEvidence = (Dictionary<string, object>)first["evidence"];
        Check((string)firstEvidence[a.Instance] == "copied" && (string)firstEvidence[b.Instance] == "copied"
              && new[] { a.Instance, b.Instance }.All(i => WorldSaveTransfer.EvidenceFiles.All(f => File.ReadAllText(WorldSaveTransfer.EvidencePath(target.Store, i, f)) == File.ReadAllText(WorldSaveTransfer.EvidencePath(source.Store, i, f))))
              && !Directory.GetFiles(Path.Combine(target.Store, "activations"), "*", SearchOption.AllDirectories).Any(f => f.Contains(DurableFile.TemporaryMarker)),
            "import: each campaign's activation evidence is restored where a load reads it (activations/<instance>/evidence), byte for byte; no temporary file left");
        var readBack = target.Catalog.Read("a2");
        var bound = WorldBinding.FromHeader(readBack.Metadata);
        Check(readBack.BodyWhole == true && bound.Instance == a.Instance && bound.SameWorld(a) && WorldSaveTransfer.PackageProblems(Path.Combine(target.Store, "worlds", world.Package), world.ManifestSha256, world.WorldId).Count == 0
              && FakeBundleProblems(Path.Combine(target.Store, "bundles", world.ContentId), true).Count == 0 && !Directory.GetDirectories(Path.Combine(target.Store, "bundles")).Any(d => Path.GetFileName(d).StartsWith(".staging-", StringComparison.Ordinal)),
            "import: the imported save reads back whole and bound to its campaign and world; the package and bundle in the store verify; no staging left");
        // Again: the names exist: refused, nothing written.
        string targetBefore = TreeHash(target.Store) + TreeHash(target.Saves);
        Check(Refuses(() => WorldSaveTransfer.Import(importOptions, exportDir)) && TreeHash(target.Store) + TreeHash(target.Saves) == targetBefore, "import never replaces a save: the same names are refused and nothing is written");
        // Under new names: the immutable content is reused (stored once), the counts follow.
        var second = WorldSaveTransfer.Import(importOptions, exportDir, new Dictionary<string, string> { ["a2"] = "a2 copy", ["b1"] = "b1 copy" });
        var counts2 = (Dictionary<string, object>)((Dictionary<string, object>)((Dictionary<string, object>)second["countsAfter"])["packages"])[world.Package];
        Check((string)((Dictionary<string, object>)second["packages"])[world.Package] == "reused" && (string)((Dictionary<string, object>)second["bundles"])[world.ContentId] == "reused"
              && Directory.GetDirectories(Path.Combine(target.Store, "worlds")).Length == 1 && Directory.GetDirectories(Path.Combine(target.Store, "bundles")).Length == 1
              && (int)counts2["references"] == 4 && (int)counts2["instances"] == 2,
            "dedup: imported again under new names, the identical package and bundle are reused (stored once); 4 references from the same 2 campaigns");
        Check((string)((Dictionary<string, object>)second["evidence"])[a.Instance] == "reused", "dedup: the identical activation evidence is reused");
        // A store holding different activation evidence for the campaign: refused before anything is written.
        var otherEvidence = NewEnv(scratch, "other-evidence");
        KeepEvidence(otherEvidence.Store, a.Instance, "other");
        using (StoreLock.TryAcquire(otherEvidence.Store)) { }   // the lock file every import opens
        string otherBefore = TreeHash(otherEvidence.Store) + TreeHash(otherEvidence.Saves);
        Check(Refuses(() => WorldSaveTransfer.Import(new WorldSaveTransfer.Options(otherEvidence.Store, otherEvidence.Saves, FakeBundleProblems, Clock), exportDir))
              && TreeHash(otherEvidence.Store) + TreeHash(otherEvidence.Saves) == otherBefore && !Directory.Exists(Path.Combine(otherEvidence.Store, "worlds")),
            "import refuses a store holding different activation evidence for a campaign; nothing is written");
        // A different bundle under the content id, a held store lock: refused before anything is written.
        var conflict = NewEnv(scratch, "conflict");
        CopyDirectory(Path.Combine(target.Store, "bundles"), Path.Combine(conflict.Store, "bundles"));
        File.AppendAllText(Directory.GetFiles(Path.Combine(conflict.Store, "bundles"), "*.bin", SearchOption.AllDirectories).First(), "changed");
        using (StoreLock.TryAcquire(conflict.Store)) { }   // the lock file every import opens
        string conflictBefore = TreeHash(conflict.Store) + TreeHash(conflict.Saves);
        bool differs = Refuses(() => WorldSaveTransfer.Import(new WorldSaveTransfer.Options(conflict.Store, conflict.Saves, FakeBundleProblems, Clock), exportDir));
        bool lockRefused;
        using (var held = StoreLock.TryAcquire(conflict.Store))
            lockRefused = held != null && Refuses(() => WorldSaveTransfer.Import(new WorldSaveTransfer.Options(conflict.Store, conflict.Saves, FakeBundleProblems, Clock), exportDir, new Dictionary<string, string> { ["a2"] = "z1", ["b1"] = "z2" }));
        Check(differs && lockRefused && TreeHash(conflict.Store) + TreeHash(conflict.Saves) == conflictBefore && !Directory.Exists(Path.Combine(conflict.Store, "worlds")),
            "import refuses a store holding a different bundle under the content id, and a store whose lock a running game holds; nothing is written");
        // A crash mid-import: staging and a temporary save file are left; the next import and the startup cleanup remove them.
        var crashed = NewEnv(scratch, "crashed");
        Directory.CreateDirectory(Path.Combine(crashed.Store, "bundles", ".staging-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(crashed.Store, "worlds", world.Package + DurableFile.TemporaryMarker + Guid.NewGuid().ToString("N")));
        File.WriteAllText(Path.Combine(crashed.Saves, WorldSaveTransfer.ImportTemporaryPrefix + Guid.NewGuid().ToString("N") + ".tmp"), "partial");
        string partialEvidence = WorldSaveTransfer.EvidencePath(crashed.Store, a.Instance, "navigation.json") + DurableFile.TemporaryMarker + Guid.NewGuid().ToString("N").Substring(0, 12);
        Directory.CreateDirectory(Path.GetDirectoryName(partialEvidence)); File.WriteAllText(partialEvidence, "partial");
        var cleaned = StagingCleanup.Run(crashed.Store, true, p => false);
        var resumed = WorldSaveTransfer.Import(new WorldSaveTransfer.Options(crashed.Store, crashed.Saves, FakeBundleProblems, Clock), exportDir);
        Check(cleaned.Removed.Count == 3 && !File.Exists(partialEvidence) && ((List<string>)resumed["removedTemporaries"]).Count == 1 && ((List<Dictionary<string, object>>)resumed["saves"]).Count == 2
              && !Directory.GetFiles(crashed.Saves).Any(f => Path.GetFileName(f).StartsWith(WorldSaveTransfer.ImportTemporaryPrefix, StringComparison.Ordinal)),
            "a crash mid-import leaves staging the startup cleanup removes and a temporary save file the next import removes; the import then completes");
        Check(immutableBefore == TreeHash(Path.Combine(source.Store, "worlds")) + TreeHash(Path.Combine(source.Store, "bundles")) && TreeHash(Path.Combine(target.Store, "worlds")) == TreeHash(Path.Combine(source.Store, "worlds")),
            "c2/c3 (regression guard): the source's immutable content is unchanged throughout, and the target holds it byte for byte");
        evidence["transfer"] = new Dictionary<string, object> { ["export"] = exported.Where(k => k.Key != "directory").ToDictionary(k => k.Key, k => k.Value), ["firstImport"] = Strip(first), ["secondImport"] = Strip(second) };
    }

    private static Dictionary<string, object> Strip(Dictionary<string, object> report) { return report.Where(k => k.Key != "export").ToDictionary(k => k.Key, k => k.Value); }

    // A campaign's activation evidence as ActivationCommitStage keeps it (the reports' content does not matter to the transfer).
    private static void KeepEvidence(string store, string instance, string variant = "kept")
    {
        foreach (var f in WorldSaveTransfer.EvidenceFiles)
        {
            string path = WorldSaveTransfer.EvidencePath(store, instance, f);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "{\"report\":\"" + f + " of " + instance + " (" + variant + ")\"}");
        }
    }

    // A synthetic world: a package (world.json listing its files) and a bundle (manifest.json listing its files; its content id the hash of
    // its file lines), as the store holds them.
    private sealed class SyntheticWorld { public string WorldId, ManifestSha256, ContentId, Package; }

    private static SyntheticWorld MakeWorld(string store, string worldId, int seed)
    {
        var random = new Random(seed);
        string staging = Path.Combine(store, "bundles", "build");
        var bundleFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "terrain/heights.bin", "terrain/water.bin", "world.json", "DATA_READY" })
        {
            var bytes = new byte[name.EndsWith(".bin", StringComparison.Ordinal) ? 20000 : 200]; random.NextBytes(bytes);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(staging, name)));
            File.WriteAllBytes(Path.Combine(staging, name), bytes);
            bundleFiles[name] = Sha(bytes);
        }
        string content = Sha(Encoding.UTF8.GetBytes(String.Join("\n", bundleFiles.Select(f => f.Key + "=" + f.Value))));
        File.WriteAllText(Path.Combine(staging, "manifest.json"), Json.Serialize(new Dictionary<string, object> { ["worldContentId"] = content, ["files"] = bundleFiles.ToDictionary(k => k.Key, k => (object)k.Value) }));
        Directory.Move(staging, Path.Combine(store, "bundles", content));
        var packageFiles = new SortedDictionary<string, object>(StringComparer.Ordinal);
        var files = new Dictionary<string, byte[]> { ["placement.json"] = Encoding.UTF8.GetBytes("{\"worldId\":\"" + worldId + "\"}"), ["documents/Settlements.xml"] = Encoding.UTF8.GetBytes("<Settlements/>") };
        foreach (var f in files) packageFiles[f.Key] = Sha(f.Value);
        var manifestBytes = Encoding.UTF8.GetBytes(Json.Serialize(new Dictionary<string, object> { ["schema"] = WorldSaveTransfer.PackageSchema, ["worldId"] = worldId, ["files"] = packageFiles }));
        string manifestSha = Sha(manifestBytes);
        string package = worldId + "-" + manifestSha.Substring(0, 12);
        string dir = Path.Combine(store, "worlds", package);
        foreach (var f in files) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, f.Key))); File.WriteAllBytes(Path.Combine(dir, f.Key), f.Value); }
        File.WriteAllBytes(Path.Combine(dir, "world.json"), manifestBytes);
        return new SyntheticWorld { WorldId = worldId, ManifestSha256 = manifestSha, ContentId = content, Package = package };
    }

    // The tests' bundle verifier (in game and in the tool it is WorldDataBundle.Problems): every listed file present with its hash, nothing
    // unlisted, the content id the hash of the file lines, the directory named by it.
    private static IList<string> FakeBundleProblems(string directory, bool nameMustMatch)
    {
        var problems = new List<string>();
        if (!Directory.Exists(directory)) { problems.Add("not a bundle directory"); return problems; }
        var manifest = (Dictionary<string, object>)Json.DeserializeObject(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var files = (Dictionary<string, object>)manifest["files"];
        foreach (var f in files)
        {
            string p = Path.Combine(directory, f.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(p) || Sha(File.ReadAllBytes(p)) != (string)f.Value) problems.Add(f.Key + " differs");
        }
        var found = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Select(p => p.Substring(directory.Length + 1).Replace('\\', '/')).Where(p => p != "manifest.json");
        if (found.Any(p => !files.ContainsKey(p))) problems.Add("unlisted file");
        string content = Sha(Encoding.UTF8.GetBytes(String.Join("\n", files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => f.Key + "=" + f.Value))));
        if ((string)manifest["worldContentId"] != content) problems.Add("content id mismatch");
        if (nameMustMatch && Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)) != content) problems.Add("directory not named by its content id");
        return problems;
    }

    // ------------------------------------------------------------------ schema (the subset save-binding.schema.json uses)

    private static Dictionary<string, object> schema;
    private static bool SchemaValid(string def, string documentPath) { return SchemaValidObject(def, Json.DeserializeObject(File.ReadAllText(documentPath))); }
    private static bool SchemaValidObject(string def, object document)
    {
        if (schema == null) schema = (Dictionary<string, object>)Json.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen", "contracts", "save-binding.schema.json")));
        var errors = new List<string>();
        Validate(((Dictionary<string, object>)schema["$defs"])[def], document, "$", errors);
        foreach (var e in errors.Take(5)) Console.WriteLine("      schema " + def + ": " + e);
        return errors.Count == 0;
    }

    private static void Validate(object node, object value, string at, List<string> errors)
    {
        var s = (Dictionary<string, object>)node;
        object r;
        if (s.TryGetValue("$ref", out r)) { Validate(((Dictionary<string, object>)schema["$defs"])[((string)r).Substring("#/$defs/".Length)], value, at, errors); return; }
        if (s.TryGetValue("const", out r) && !Equals(r, value)) { errors.Add(at + ": not " + r); return; }
        if (s.TryGetValue("enum", out r) && !((IList)r).Cast<object>().Any(x => Equals(x, value))) { errors.Add(at + ": not one of the allowed values"); return; }
        if (s.TryGetValue("type", out r))
        {
            var types = r is string ? new[] { (string)r } : ((IList)r).Cast<string>().ToArray();
            if (!types.Any(t => Is(t, value))) { errors.Add(at + ": not " + String.Join("|", types)); return; }
        }
        if (s.TryGetValue("pattern", out r) && value is string && !Regex.IsMatch((string)value, (string)r)) errors.Add(at + ": does not match " + r);
        var map = value as Dictionary<string, object>;
        if (map != null)
        {
            if (s.TryGetValue("required", out r)) foreach (string k in (IList)r) if (!map.ContainsKey(k)) errors.Add(at + ": lacks " + k);
            var properties = s.TryGetValue("properties", out r) ? (Dictionary<string, object>)r : new Dictionary<string, object>();
            foreach (var kv in map)
            {
                if (properties.ContainsKey(kv.Key)) Validate(properties[kv.Key], kv.Value, at + "." + kv.Key, errors);
                else if (s.TryGetValue("additionalProperties", out r) && Equals(r, false)) errors.Add(at + ": unexpected " + kv.Key);
            }
        }
        var list = value as IList;
        if (list != null)
        {
            if (s.TryGetValue("minItems", out r) && list.Count < (int)r) errors.Add(at + ": fewer than " + r + " items");
            if (s.TryGetValue("items", out r)) for (int i = 0; i < list.Count; i++) Validate(r, list[i], at + "[" + i + "]", errors);
        }
    }

    private static bool Is(string type, object value)
    {
        switch (type)
        {
            case "object": return value is Dictionary<string, object>;
            case "array": return value is IList;
            case "string": return value is string;
            case "boolean": return value is bool;
            case "integer": return value is int || value is long;
            case "null": return value == null;
            default: return false;
        }
    }

    // ------------------------------------------------------------------ helpers

    private static bool Throws(Action action) { try { action(); return false; } catch (Exception) { return true; } }
    private static bool Refuses(Action action) { try { action(); return false; } catch (WorldSaveTransfer.Refused) { return true; } }

    private static string Canonical(object value)
    {
        var map = value as Dictionary<string, object>;
        if (map != null) return "{" + String.Join(",", map.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => Json.Serialize(k.Key) + ":" + Canonical(k.Value))) + "}";
        var list = value as IList;
        if (list != null) return "[" + String.Join(",", list.Cast<object>().Select(Canonical)) + "]";
        return Json.Serialize(value);
    }

    private static string Sha(byte[] bytes) { using (var sha = SHA256.Create()) return String.Concat(sha.ComputeHash(bytes).Select(x => x.ToString("x2", CultureInfo.InvariantCulture))); }

    private static string TreeHash(string root)
    {
        if (!Directory.Exists(root)) return "absent";
        var text = new StringBuilder();
        foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            text.Append(f.Substring(root.Length)).Append('=').Append(Sha(File.ReadAllBytes(f))).Append('\n');
        return Sha(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, f.Substring(from.Length + 1));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(f, target);
        }
    }
}
