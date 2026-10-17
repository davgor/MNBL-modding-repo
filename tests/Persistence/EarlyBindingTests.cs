using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.Persistence;

// MMR-62 early save binding tests, without the game:
//   EarlyBindingTests OUT_JSON            (EarlyBindingTests hold DIR is the child process of the store lock test)
// The world binding read from a save header; the initial-save journal over a scripted save list: the refusals that write nothing, and
// publication interrupted at each boundary (before the intent is durable, before the engine save, a reported failure, a torn header, a
// whole header before torn game data, an unbound or malformed header, after the engine save, during the outcome's write), with the
// fail-closed cases (the save list unreadable, a deletion that does not take, an integrity check that could not run, a flush that fails, a
// corrupt or invalid journal, a bound save of another instance, a process without the store lock); the reference index and its retention
// decisions; abandoned staging cleanup with sentinels, the store lock, live jobs and reparse points (a candidate, a link inside, a parent);
// the durable file; the store lock across processes (held, and released when the holder is killed); the save folder listing that fails
// visibly; the save file integrity check on synthetic saves in the installed format, and on a file it cannot read.
internal static class EarlyBindingTests
{
    private static int checks, failures;
    private static readonly List<string> lines = new List<string>();
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; string line = (condition ? "ok    " : "FAIL  ") + label; lines.Add(line); Console.WriteLine(line); }

    private static readonly string Manifest = new string('a', 64), Manifest2 = new string('b', 64), Content = new string('c', 64);

    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "hold")
        {
            var held = StoreLock.TryAcquire(args[1]);
            Console.WriteLine(held != null ? "held" : "refused");
            Console.Out.Flush();
            Console.In.ReadLine();
            return 0;
        }
        if (args.Length != 1) { Console.Error.WriteLine("usage: EarlyBindingTests OUT_JSON"); return 2; }
        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.early-binding-tests", ["version"] = 1, ["ticket"] = "MMR-62" };
        string scratch = Path.Combine(Path.GetTempPath(), "mnbl-mmr62-" + Guid.NewGuid().ToString("N"));
        try { Run(scratch, evidence); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        finally { try { if (Directory.Exists(scratch)) ForceDelete(scratch); } catch (Exception) { } }
        evidence["checks"] = checks; evidence["failures"] = failures; evidence["lines"] = lines;
        File.WriteAllText(args[0], new JavaScriptSerializer().Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private sealed class FakeCatalog : ISaveCatalog
    {
        public readonly Dictionary<string, SaveHeader> Saves = new Dictionary<string, SaveHeader>(StringComparer.Ordinal);
        public bool ThrowOnList, ThrowOnRead, DeleteFails, FlushFails;
        public int Deletes;
        public readonly List<string> Flushed = new List<string>();
        public IList<SaveHeader> List() { if (ThrowOnList) throw new IOException("save folder unreadable"); return Saves.Values.Select(Strip).ToList(); }
        public SaveHeader Read(string name) { if (ThrowOnRead) throw new IOException("save folder unreadable"); SaveHeader h; return Saves.TryGetValue(name, out h) ? h : null; }
        public bool Delete(string name) { Deletes++; if (DeleteFails) return false; return Saves.Remove(name); }
        public void Flush(string name) { if (FlushFails) throw new IOException("flush failed"); Flushed.Add(name); }
        private static SaveHeader Strip(SaveHeader h) { return new SaveHeader { Name = h.Name, Metadata = h.Metadata }; }   // List carries no integrity check

        public void Bound(string name, WorldBinding b, bool? whole = true)
        {
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["UniqueGameId"] = "game-" + name, ["mnbl.world.id"] = b.WorldId, ["mnbl.world.manifestSha256"] = b.ManifestSha256,
                ["mnbl.world.scene"] = "scene_" + b.WorldId, ["mnbl.world.contentId"] = b.ContentId };
            if (b.Instance != null) metadata["mnbl.world.instance"] = b.Instance;
            Saves[name] = new SaveHeader { Name = name, Metadata = metadata, BodyWhole = whole, BodyProblem = whole == false ? "the game data ends early (a torn write)" : null };
        }
        public void Plain(string name) { Saves[name] = new SaveHeader { Name = name, Metadata = new Dictionary<string, string> { ["UniqueGameId"] = "vanilla" }, BodyWhole = true }; }
        public void Unreadable(string name) { Saves[name] = new SaveHeader { Name = name }; }
    }

    private static WorldBinding World(string id = "mnbl_00062001", string manifest = null, string content = null)
    {
        return new WorldBinding { WorldId = id, ManifestSha256 = manifest ?? Manifest, Instance = WorldBinding.NewInstance(), ContentId = content ?? WorldBinding.NoContent };
    }

    private static string Store(string scratch, string name) { string path = Path.Combine(scratch, name); Directory.CreateDirectory(path); return path; }

    // MMR-48: with every save kind bound the index also reads the later-save journals (none here); without them it records a gap.
    private static WorldReferenceIndex Index(ISaveCatalog saves, EarlyWorldBindingStore store, IEnumerable<WorldBinding> active, bool saveKindsBound = WorldReferenceIndex.SaveKindsBound)
    {
        return WorldReferenceIndex.Build(saves, store, active, saveKindsBound, saveKindsBound ? new SaveJournal(store.Root, saves, n => null) : null);
    }

    private static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } catch (Exception) { return false; } }

    private static void Run(string scratch, SortedDictionary<string, object> evidence)
    {
        // 1. The binding in a save header.
        var header = new Dictionary<string, string> { ["mnbl.world.id"] = "mnbl_00062001", ["mnbl.world.manifestSha256"] = Manifest, ["mnbl.world.instance"] = new string('1', 32), ["mnbl.world.contentId"] = Content };
        var parsed = WorldBinding.FromHeader(header);
        Check(parsed != null && parsed.PackageName == "mnbl_00062001-aaaaaaaaaaaa" && parsed.Instance == new string('1', 32) && parsed.ContentId == Content && !parsed.Legacy,
            "binding: a header's mnbl.world.* keys give the world id, the package <id>-<manifest12>, the campaign instance and the content id");
        Check(WorldBinding.FromHeader(new Dictionary<string, string> { ["UniqueGameId"] = "x" }) == null, "binding: a header without mnbl.world.id has no binding (a vanilla save)");
        var legacy = WorldBinding.FromHeader(new Dictionary<string, string> { ["mnbl.world.id"] = "mnbl_00059042", ["mnbl.world.manifestSha256"] = Manifest, ["mnbl.world.package"] = @"C:\Users\someone\AppData\Local\MNBL.ProceduralWorld\worlds\x" });
        Check(legacy != null && legacy.Legacy && legacy.ContentId == WorldBinding.NoContent && legacy.PackageName == "mnbl_00059042-aaaaaaaaaaaa",
            "binding: an MMR-19 header (no instance, an absolute package path) still names its package by id and manifest hash; the path is not used");
        Check(Throws<InvalidDataException>(() => WorldBinding.FromHeader(new Dictionary<string, string> { ["mnbl.world.id"] = "mnbl_1", ["mnbl.world.manifestSha256"] = "abc" }))
            && Throws<InvalidDataException>(() => WorldBinding.FromHeader(new Dictionary<string, string> { ["mnbl.world.id"] = "mnbl_1", ["mnbl.world.manifestSha256"] = Manifest, ["mnbl.world.instance"] = "XYZ" }))
            && Throws<InvalidDataException>(() => WorldBinding.FromHeader(new Dictionary<string, string> { ["mnbl.world.id"] = "mnbl_1", ["mnbl.world.manifestSha256"] = Manifest, ["mnbl.world.contentId"] = "short" }))
            && Throws<InvalidDataException>(() => WorldBinding.FromHeader(new Dictionary<string, string> { ["mnbl.world.id"] = "../escape", ["mnbl.world.manifestSha256"] = Manifest })),
            "binding: a malformed manifest hash, instance, content id or world id is refused (InvalidDataException)");
        var fresh = World(content: Content); var freshEntries = fresh.Entries();
        Check(freshEntries.Count == 2 && freshEntries["instance"] == fresh.Instance && freshEntries["contentId"] == Content && Throws<InvalidOperationException>(() => legacy.Entries()),
            "binding: a new binding contributes instance and contentId to the header; a legacy one cannot be written");

        // 2. Begin's refusals write nothing.
        {
            var saves = new FakeCatalog(); bool published = false;
            var store = new EarlyWorldBindingStore(Store(scratch, "refusals"), saves, x => published);
            var b = World();
            bool unpublished = Throws<InvalidDataException>(() => store.Begin(b, EarlyWorldBindingStore.InitialSaveName(b)));
            published = true;
            saves.Plain("taken");
            bool taken = Throws<IOException>(() => store.Begin(b, "taken"));
            bool legacyRefused = Throws<InvalidDataException>(() => store.Begin(legacy, "legacy_start"));
            bool badName = Throws<ArgumentException>(() => store.Begin(b, "a/b")) && Throws<ArgumentException>(() => store.Begin(b, " padded"));
            bool noJournal = !Directory.Exists(store.JournalDirectory) || Directory.GetFiles(store.JournalDirectory).Length == 0;
            Check(unpublished && taken && legacyRefused && badName && noJournal,
                "begin: an unpublished package, a taken save name, a binding without instance and an invalid name are refused and write no journal");
            var locked = new EarlyWorldBindingStore(Store(scratch, "refusals-lock"), saves, x => true, () => false);
            Check(Throws<InvalidOperationException>(() => locked.Begin(b, EarlyWorldBindingStore.InitialSaveName(b))) && locked.Journal(b.Instance) == null,
                "begin: a process without the store lock may not publish (no journal)");
            store.Begin(b, EarlyWorldBindingStore.InitialSaveName(b));
            Check(Throws<InvalidOperationException>(() => store.Begin(b, "second_name")), "begin: one initial save journal per campaign instance");
            Check(EarlyWorldBindingStore.InitialSaveName(b) == b.WorldId + "_start_" + b.Instance.Substring(0, 12), "begin: the reserved name is <worldId>_start_<instance12>");
        }

        // 3. Publication interrupted at each boundary, and the fail-closed cases.
        var matrix = new List<Dictionary<string, object>>(); evidence["boundaries"] = matrix;
        Func<string, Action<EarlyWorldBindingStore, FakeCatalog, WorldBinding, string>, Func<EarlyWorldBindingStore, FakeCatalog, WorldBinding, string, bool>, string, bool> boundary = (name, interrupt, verify, label) =>
        {
            var saves = new FakeCatalog();
            var store = new EarlyWorldBindingStore(Store(scratch, "b-" + matrix.Count), saves, x => true);
            var b = World(); string saveName = EarlyWorldBindingStore.InitialSaveName(b);
            interrupt(store, saves, b, saveName);
            bool ok;
            try { ok = verify(store, saves, b, saveName); } catch (Exception e) { ok = false; label += " (threw " + e.GetType().Name + ": " + e.Message + ")"; }
            InitialSave journal = null;
            try { journal = store.Journal(b.Instance); } catch (InvalidDataException) { }
            matrix.Add(new Dictionary<string, object> { ["boundary"] = name, ["state"] = journal == null ? null : journal.State, ["note"] = journal == null ? null : journal.LastNote, ["saveLeft"] = saves.Saves.ContainsKey(saveName), ["passed"] = ok });
            Check(ok, "boundary " + name + ": " + label);
            return ok;
        };
        boundary("before the intent is durable", (s, c, b, n) => { Directory.CreateDirectory(s.JournalDirectory); File.WriteAllText(Path.Combine(s.JournalDirectory, b.Instance + ".json" + DurableFile.TemporaryMarker + "x"), "{\"sche"); },
            (s, c, b, n) => { var r = s.Recover(); List<string> u; return r.Resolved.Count == 0 && r.Unresolved.Count == 0 && s.Journals(out u).Count == 0 && u.Count == 0 && c.Saves.Count == 0; },
            "a torn temporary journal is no journal: the engine was never asked to save and recovery finds nothing (staging cleanup removes temporaries)");
        boundary("after the intent, before the engine save", (s, c, b, n) => s.Begin(b, n),
            (s, c, b, n) => { var r = s.Recover(); return r.Resolved.Count == 1 && s.Journal(b.Instance).State == EarlyWorldBindingStore.Abandoned && c.Deletes == 0; },
            "no save under the reserved name: abandoned, nothing deleted (the package stays)");
        boundary("engine reported failure, no file", (s, c, b, n) => s.Begin(b, n),
            (s, c, b, n) => s.Complete(b.Instance, false).State == EarlyWorldBindingStore.Abandoned,
            "Complete after a failed engine save with no file: abandoned, never published");
        boundary("torn header", (s, c, b, n) => { s.Begin(b, n); c.Unreadable(n); },
            (s, c, b, n) => { s.Recover(); return s.Journal(b.Instance).State == EarlyWorldBindingStore.Removed && !c.Saves.ContainsKey(n); },
            "a save under the reserved name whose header cannot be read is this publication's torn write: deleted through the game, removed");
        boundary("whole header, torn game data", (s, c, b, n) => { s.Begin(b, n); c.Bound(n, b, false); },
            (s, c, b, n) => { s.Recover(); return s.Journal(b.Instance).State == EarlyWorldBindingStore.Removed && !c.Saves.ContainsKey(n) && s.Journal(b.Instance).LastNote.Contains("game data is torn"); },
            "a header bound to this instance before torn game data is not published: deleted, removed");
        boundary("engine reported success, torn game data", (s, c, b, n) => { s.Begin(b, n); c.Bound(n, b, false); },
            (s, c, b, n) => s.Complete(b.Instance, true).State == EarlyWorldBindingStore.Removed && !c.Saves.ContainsKey(n),
            "Complete trusts the read-back, not the engine's report: torn data is removed, not published");
        boundary("unbound header", (s, c, b, n) => { s.Begin(b, n); c.Plain(n); },
            (s, c, b, n) => { s.Recover(); return s.Journal(b.Instance).State == EarlyWorldBindingStore.Removed && !c.Saves.ContainsKey(n); },
            "a readable save under the reserved name with no world binding (an ambiguous save the vanilla loader would take) is deleted, removed");
        boundary("malformed binding", (s, c, b, n) => { s.Begin(b, n); c.Bound(n, b); c.Saves[n].Metadata["mnbl.world.manifestSha256"] = "zz"; },
            (s, c, b, n) => { s.Recover(); return s.Journal(b.Instance).State == EarlyWorldBindingStore.Removed && !c.Saves.ContainsKey(n); },
            "a save under the reserved name with a malformed binding is deleted, removed");
        boundary("after the engine save, before the outcome", (s, c, b, n) => { s.Begin(b, n); c.Bound(n, b); },
            (s, c, b, n) => { var r = s.Recover(); var j = s.Journal(b.Instance); return r.Resolved.Count == 1 && j.State == EarlyWorldBindingStore.Published && j.UniqueGameId == "game-" + n && c.Saves.ContainsKey(n) && c.Deletes == 0
                && c.Flushed.SequenceEqual(new[] { n }); },
            "a whole save bound to this instance and world: recovery flushes it to the disk, publishes it and records the game's UniqueGameId");
        boundary("normal publication", (s, c, b, n) => { s.Begin(b, n); c.Bound(n, b); },
            (s, c, b, n) => { var j = s.Complete(b.Instance, true); var again = s.Complete(b.Instance, true); return j.State == EarlyWorldBindingStore.Published && again.State == EarlyWorldBindingStore.Published
                && j.History.Select(h => (string)h["state"]).SequenceEqual(new[] { "intent", "published" }) && s.Recover().Resolved.Count == 0; },
            "Begin, the engine save, Complete: intent -> published; Complete and Recover afterwards change nothing");
        boundary("during the outcome's write", (s, c, b, n) => { s.Begin(b, n); c.Bound(n, b);
                string journal = Path.Combine(s.JournalDirectory, b.Instance + ".json");
                File.WriteAllText(journal + DurableFile.TemporaryMarker + "y", File.ReadAllText(journal).Replace("\"state\":\"intent\"", "\"state\":\"conflict\"")); },
            (s, c, b, n) => { s.Recover(); return s.Journal(b.Instance).State == EarlyWorldBindingStore.Published; },
            "a crash before the outcome's rename leaves the intent authoritative (a complete temporary journal is ignored): recovery resolves it");
        boundary("another instance's save under the name", (s, c, b, n) => { s.Begin(b, n); c.Bound(n, World()); },
            (s, c, b, n) => { s.Recover(); return s.Journal(b.Instance).State == EarlyWorldBindingStore.Conflict && c.Saves.ContainsKey(n) && c.Deletes == 0; },
            "a save under the reserved name bound to another instance is kept (a bound save is never deleted): conflict");
        boundary("another world's save under the name", (s, c, b, n) => { s.Begin(b, n); var w = World(manifest: Manifest2); w.Instance = b.Instance; c.Bound(n, w); },
            (s, c, b, n) => { s.Recover(); return s.Journal(b.Instance).State == EarlyWorldBindingStore.Conflict && c.Saves.ContainsKey(n); },
            "the same instance bound to another world's package is not this publication: kept, conflict");
        boundary("save list unreadable", (s, c, b, n) => { s.Begin(b, n); c.Bound(n, b); c.ThrowOnRead = true; },
            (s, c, b, n) => { var r = s.Recover(); return r.Unresolved.Count == 1 && s.Journal(b.Instance).State == EarlyWorldBindingStore.Intent && c.Saves.ContainsKey(n) && c.Deletes == 0; },
            "when the save list cannot be read the intent stays unresolved: nothing deleted, reported");
        boundary("deletion does not take", (s, c, b, n) => { s.Begin(b, n); c.Unreadable(n); c.DeleteFails = true; },
            (s, c, b, n) => { var r = s.Recover(); return r.Unresolved.Count == 1 && s.Journal(b.Instance).State == EarlyWorldBindingStore.Intent && c.Saves.ContainsKey(n); },
            "a torn save that cannot be deleted leaves the intent unresolved (retried at the next start)");
        boundary("flush fails", (s, c, b, n) => { s.Begin(b, n); c.Bound(n, b); c.FlushFails = true; },
            (s, c, b, n) => { var r = s.Recover(); return r.Unresolved.Count == 1 && s.Journal(b.Instance).State == EarlyWorldBindingStore.Intent && c.Saves.ContainsKey(n); },
            "a whole bound save that cannot be flushed to the disk is not published: unresolved");
        boundary("integrity not checked", (s, c, b, n) => { s.Begin(b, n); c.Bound(n, b, null); },
            (s, c, b, n) => { var r = s.Recover(); return r.Unresolved.Count == 1 && s.Journal(b.Instance).State == EarlyWorldBindingStore.Intent && c.Saves.ContainsKey(n); },
            "a bound save whose game data could not be checked is neither published nor deleted: unresolved");
        boundary("corrupt journal", (s, c, b, n) => { Directory.CreateDirectory(s.JournalDirectory); File.WriteAllText(Path.Combine(s.JournalDirectory, b.Instance + ".json"), "{not json"); },
            (s, c, b, n) => { var r = s.Recover(); return r.UnreadableJournals.Count == 1 && r.Resolved.Count == 0 && Index(c, s, null).Gaps.Any(g => g.Contains(b.Instance)); },
            "a journal that cannot be read is reported and is a gap in the reference index");
        boundary("journal history without a note", (s, c, b, n) => { s.Begin(b, n); c.Bound(n, b); string j = Path.Combine(s.JournalDirectory, b.Instance + ".json");
                File.WriteAllText(j, File.ReadAllText(j).Replace(",\"note\":\"intent written before the engine save\"", "")); },
            (s, c, b, n) => { var r = s.Recover(); return r.UnreadableJournals.Count == 1 && r.Resolved.Count == 0 && c.Saves.ContainsKey(n); },
            "a parseable journal with an incomplete history row is unreadable: reported, recovery goes on, nothing deleted");
        boundary("journal save name escapes", (s, c, b, n) => { s.Begin(b, n); string j = Path.Combine(s.JournalDirectory, b.Instance + ".json");
                File.WriteAllText(j, File.ReadAllText(j).Replace("\"saveName\":\"" + n + "\"", "\"saveName\":\"..\\\\outside\"")); },
            (s, c, b, n) => s.Recover().UnreadableJournals.Count == 1 && c.Deletes == 0,
            "a journal naming a save outside the save folder is unreadable and never acted on");
        boundary("journal renamed", (s, c, b, n) => { s.Begin(b, n); File.Move(Path.Combine(s.JournalDirectory, b.Instance + ".json"), Path.Combine(s.JournalDirectory, new string('e', 32) + ".json")); },
            (s, c, b, n) => s.Recover().UnreadableJournals.Count == 1,
            "a journal whose instance is not its file name is unreadable (never resolved against the wrong save)");

        // 4. The reference index.
        {
            var saves = new FakeCatalog();
            var store = new EarlyWorldBindingStore(Store(scratch, "index"), saves, x => true);
            var a = World("mnbl_00062010", content: Content); var p = World("mnbl_00062011"); var act = World("mnbl_00062012"); var other = World("mnbl_00062013", Manifest2, Content);
            saves.Bound("a_manual", a); saves.Bound("legacy_save", new WorldBinding { WorldId = "mnbl_00059042", ManifestSha256 = Manifest, ContentId = WorldBinding.NoContent });
            saves.Plain("vanilla_save");
            store.Begin(p, EarlyWorldBindingStore.InitialSaveName(p));
            var full = Index(saves, store, new[] { act });
            Check(full.DiscoveryComplete && full.SavesListed == 3 && full.SavesBound == 2 && full.References.Count == 4,
                "index: bound saves (one legacy), a pending initial save and the active campaign are references; a vanilla save is listed and names nothing");
            Check(full.Decide(a.PackageName).Retain && full.Decide("mnbl_00059042-aaaaaaaaaaaa").Retain && full.Decide(p.PackageName).Retain && full.Decide(act.PackageName).Retain,
                "index: a package named by a save, a legacy save, a pending initial save or the active campaign is retained");
            Check(full.Decide(other.PackageName, Content).Retain && full.Decide(other.PackageName, Content).References.Single().Name == "a_manual",
                "index: a package whose data bundle (content id) a save names is retained through the content id");
            var unref = Index(saves, store, new[] { act }, false).Decide("mnbl_00062099-aaaaaaaaaaaa");
            Check(unref.Retain && unref.Reason.Contains("not every save kind"), "index: while not every save kind is journaled (MMR-62's increment), complete discovery still retains an unreferenced complete world");
            var bound = Index(saves, store, new[] { act }, true);
            Check(!bound.Decide("mnbl_00062099-aaaaaaaaaaaa").Retain && bound.Decide(a.PackageName).Retain, "index: once every save kind is bound, an unreferenced package may go and a referenced one stays");
            saves.Unreadable("torn_foreign");
            var gap = Index(saves, store, null, true);
            Check(!gap.DiscoveryComplete && gap.Gaps.Single().Contains("torn_foreign") && gap.Decide("mnbl_00062099-aaaaaaaaaaaa").Retain && gap.Decide("mnbl_00062099-aaaaaaaaaaaa").Reason.StartsWith("reference discovery is incomplete"),
                "index: an unreadable save is a gap, and with a gap nothing is deletable (it might name any world)");
            saves.Saves.Remove("torn_foreign"); saves.Bound("malformed", a); saves.Saves["malformed"].Metadata["mnbl.world.instance"] = "nothex";
            Check(!Index(saves, store, null, true).DiscoveryComplete, "index: a malformed binding is a gap");
            saves.Saves.Remove("malformed"); saves.ThrowOnList = true;
            var noList = Index(saves, store, null, true);
            Check(!noList.DiscoveryComplete && noList.Gaps[0].StartsWith("the save list could not be read") && noList.Decide("mnbl_00062099-aaaaaaaaaaaa").Retain && noList.Decide(p.PackageName).Retain,
                "index: a save list that cannot be read is a gap; pending initial saves still count");
            evidence["index"] = full.Report();
        }

        // 5. Staging cleanup.
        {
            string store = Store(scratch, "staging"), outside = Store(scratch, "outside");
            File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep");
            Func<string, string> dir = rel => { string path = Path.Combine(store, rel); Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "f.bin"), rel); return path; };
            var staging = new[] { dir(@"bundles\.staging-abc"), dir(@"worlds\mnbl_00062001-aaaaaaaaaaaa" + DurableFile.TemporaryMarker + "123"), dir(@"world-surfaces\" + new string('0', 32)) };
            string notSurface = dir(@"world-surfaces\keep-not-a-guid");
            Directory.CreateDirectory(Path.Combine(store, @"saves\journal"));
            string journalTemp = Path.Combine(store, @"saves\journal\" + new string('1', 32) + ".json" + DurableFile.TemporaryMarker + "9"); File.WriteAllText(journalTemp, "{");
            string readOnly = Path.Combine(staging[0], "ro.bin"); File.WriteAllText(readOnly, "ro"); File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            var sentinels = new[] { dir(@"bundles\" + new string('d', 64)), dir(@"worlds\mnbl_00062001-aaaaaaaaaaaa"), dir(@"jobs\" + new string('f', 32)), dir(@"saves\journal\keep"), dir(@"world-data\other") };
            string journal = Path.Combine(store, @"saves\journal\" + new string('2', 32) + ".json"); File.WriteAllText(journal, "{}");
            bool junction = Junction(Path.Combine(staging[2], "link"), outside);
            string linkedCandidate = Path.Combine(store, @"world-surfaces\" + new string('9', 32));
            bool junction2 = Junction(linkedCandidate, outside);
            Check(junction && junction2, "staging: test junctions created (mklink /J)");
            var skipped = StagingCleanup.Run(store, false, null);
            Check(!skipped.Ran && skipped.Removed.Count == 0 && staging.All(Directory.Exists), "staging: without the store lock nothing is removed");
            string live = staging[1];
            var report = StagingCleanup.Run(store, true, path => path == live);
            Check(!Directory.Exists(staging[0]) && !Directory.Exists(staging[2]) && !File.Exists(journalTemp) && Directory.Exists(live),
                "staging: bundle staging, surface staging and journal temporaries are removed; staging a live job owns is kept");
            Check(sentinels.All(s => File.Exists(Path.Combine(s, "f.bin"))) && File.Exists(journal) && File.Exists(Path.Combine(notSurface, "f.bin")),
                "staging: a published bundle, a world package, a job record, journals, other store data and a surface folder not named like encoder staging are kept");
            Check(File.Exists(Path.Combine(outside, "precious.txt")), "staging: a junction inside a staging directory is removed as a link; its target is untouched");
            Check(Directory.Exists(linkedCandidate) && report.Retained.Any(r => ((string)r["path"]).EndsWith("world-surfaces/" + new string('9', 32)) && ((string)r["reason"]).Contains("reparse point")),
                "staging: a candidate that is itself a reparse point is kept and reported");
            var second = StagingCleanup.Run(store, true, null);
            Check(!Directory.Exists(live) && second.Removed.Count == 1, "staging: the next run removes staging no longer live");
            try { Directory.Delete(linkedCandidate, false); } catch (Exception) { }
            evidence["staging"] = report.ToDictionary();
            // A staging parent that is itself a link is not scanned.
            string linkedStore = Store(scratch, "staging-parent"), target = Store(scratch, "parent-target");
            string targetStaging = Path.Combine(target, new string('7', 32)); Directory.CreateDirectory(targetStaging); File.WriteAllText(Path.Combine(targetStaging, "precious.txt"), "keep");
            bool parentLink = Junction(Path.Combine(linkedStore, "world-surfaces"), target);
            var parentReport = StagingCleanup.Run(linkedStore, true, null);
            Check(parentLink && File.Exists(Path.Combine(targetStaging, "precious.txt")) && parentReport.Removed.Count == 0 && parentReport.Retained.Any(r => ((string)r["path"]) == "world-surfaces" && ((string)r["reason"]).Contains("not scanned")),
                "staging: a staging parent that is a reparse point is not scanned; its target's folders are untouched");
            try { Directory.Delete(Path.Combine(linkedStore, "world-surfaces"), false); } catch (Exception) { }
        }

        // 6. The durable file and the store lock.
        {
            string path = Path.Combine(Store(scratch, "durable"), "j.json");
            DurableFile.WriteAllText(path, "one");
            DurableFile.WriteAllText(path, "two");
            Check(File.ReadAllText(path) == "two" && Directory.GetFiles(Path.GetDirectoryName(path)).Length == 1 && DurableFile.IsTemporary(path + DurableFile.TemporaryMarker + "x") && !DurableFile.IsTemporary(path),
                "durable file: written and replaced whole, no temporary left; temporaries are recognised");
            string store = Store(scratch, "lock");
            var first = StoreLock.TryAcquire(store); var second = StoreLock.TryAcquire(store);
            bool exclusive = first != null && first.Held && second == null;
            first.Dispose();
            var third = StoreLock.TryAcquire(store);
            Check(exclusive && third != null, "store lock: one holder at a time in a process; released on dispose");
            third.Dispose();
            var child = Process.Start(new ProcessStartInfo(System.Reflection.Assembly.GetEntryAssembly().Location, "hold \"" + store + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true });
            string childSays = child.StandardOutput.ReadLine();
            var whileHeld = StoreLock.TryAcquire(store);
            child.Kill(); child.WaitForExit();
            var afterKill = StoreLock.TryAcquire(store);
            Check(childSays == "held" && whileHeld == null && afterKill != null, "store lock: held by another process it cannot be taken; when that process is killed the system releases it");
            if (whileHeld != null) whileHeld.Dispose();
            if (afterKill != null) afterKill.Dispose();
        }

        // 7. The save folder read so that a failure shows.
        {
            string folder = Store(scratch, "folder");
            File.WriteAllText(Path.Combine(folder, "a.sav"), "x"); File.WriteAllText(Path.Combine(folder, "b.sav"), "x"); File.WriteAllText(Path.Combine(folder, "notes.txt"), "x");
            string notFolder = Path.Combine(folder, "a.sav");
            Check(SaveFolder.Names(folder).OrderBy(n => n).SequenceEqual(new[] { "a", "b" }) && SaveFolder.Names(Path.Combine(folder, "missing")).Count == 0 && SaveFolder.Contains(folder, "A") && !SaveFolder.Contains(folder, "c"),
                "save folder: the *.sav names; a folder that does not exist (no save yet) is empty");
            Check(Throws<IOException>(() => SaveFolder.Names(notFolder)), "save folder: a folder that cannot be enumerated throws (the game's own listing would return nothing)");
            Check(Throws<IOException>(() => SaveFolder.RequireSame(new[] { "a" }, new[] { "a", "b" })) && Throws<IOException>(() => SaveFolder.RequireSame(new[] { "a", "z" }, new[] { "a" })) && !Throws<IOException>(() => SaveFolder.RequireSame(new[] { "A", "b" }, new[] { "a", "B" })),
                "save folder: the game's list must name the saves on disk, no more and no fewer");
        }

        // 8. The save file integrity check on synthetic saves in the installed format.
        {
            string dirPath = Store(scratch, "saves");
            byte[] whole = SyntheticSave(false);
            Func<byte[], string> check = bytes => { string p = Path.Combine(dirPath, Guid.NewGuid().ToString("N") + ".sav"); File.WriteAllBytes(p, bytes); string problem; return SaveFileCheck.Check(p, out problem) == false ? problem : (SaveFileCheck.Check(p, out problem) == true ? null : "unchecked"); };
            int metadata = BitConverter.ToInt32(whole, 0);
            Check(check(whole) == null, "save check: a whole save (header, deflated game data walked to its end) passes");
            Check(check(whole.Take(whole.Length - 40).ToArray()) != null && check(whole.Take(4 + metadata + 10).ToArray()) != null, "save check: game data cut short behind a whole header is torn");
            Check(check(whole.Take(4 + metadata / 2).ToArray()) != null && check(new byte[] { 1, 2 }) != null, "save check: a header cut short is torn");
            Check(check(SyntheticSave(true)) != null, "save check: data after the game data's end is refused");
            var garbage = (byte[])whole.Clone(); for (int i = 4 + metadata + 2; i < garbage.Length; i++) garbage[i] = 0xff;
            Check(check(garbage) != null, "save check: game data that does not inflate is refused");
            string heldPath = Path.Combine(dirPath, "held.sav"); File.WriteAllBytes(heldPath, whole);
            bool? heldOutcome; string heldProblem;
            using (new FileStream(heldPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) heldOutcome = SaveFileCheck.Check(heldPath, out heldProblem);
            Check(heldOutcome == null && heldProblem.StartsWith("could not read"), "save check: a file it cannot read (held without sharing) is unchecked, never torn");
        }
    }

    // [Int32 length][JSON][deflate: Int32 header length + header; Int32 object count, each Int32 length + bytes; Int32 container count, each
    // Int32 length + bytes; Int32 strings length + strings] (FileDriver.Save, GameData.Write, MetaData.Serialize in 1.4.8).
    private static byte[] SyntheticSave(bool trailing)
    {
        var random = new Random(62);
        Func<int, byte[]> bytes = n => { var b = new byte[n]; random.NextBytes(b); return b; };
        using (var file = new MemoryStream())
        {
            var json = Encoding.UTF8.GetBytes("{\"UniqueGameId\":\"x\",\"mnbl.world.id\":\"mnbl_00062001\"}");
            file.Write(BitConverter.GetBytes(json.Length), 0, 4); file.Write(json, 0, json.Length);
            using (var deflate = new DeflateStream(file, CompressionLevel.Fastest, true))
            using (var writer = new BinaryWriter(deflate))
            {
                var h = bytes(300); writer.Write(h.Length); writer.Write(h);
                writer.Write(40); for (int i = 0; i < 40; i++) { var o = bytes(random.Next(10, 4000)); writer.Write(o.Length); writer.Write(o); }
                writer.Write(5); for (int i = 0; i < 5; i++) { var c = bytes(random.Next(10, 2000)); writer.Write(c.Length); writer.Write(c); }
                var s = bytes(5000); writer.Write(s.Length); writer.Write(s);
                if (trailing) writer.Write(12345);
            }
            return file.ToArray();
        }
    }

    private static bool Junction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var process = Process.Start(start)) { process.WaitForExit(); return process.ExitCode == 0 && Directory.Exists(link); }
    }

    private static void ForceDelete(string directory)
    {
        foreach (var entry in Directory.GetFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) { if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(entry, false); else File.Delete(entry); }
            else if ((attributes & FileAttributes.Directory) != 0) ForceDelete(entry);
            else { File.SetAttributes(entry, FileAttributes.Normal); File.Delete(entry); }
        }
        Directory.Delete(directory, false);
    }
}
