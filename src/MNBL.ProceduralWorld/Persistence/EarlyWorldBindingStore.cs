using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace MNBL.ProceduralWorld.Persistence
{
    // The game's save list as the binding store sees it. The Bannerlord implementation is BannerlordSaveCatalog (MBSaveLoad); tests use a
    // scripted one.
    internal interface ISaveCatalog
    {
        // Every save with its header. Throws when the saves cannot be listed.
        IList<SaveHeader> List();
        // Null when there is no such save; a header whose metadata cannot be read has Metadata null. A readable header also says whether
        // the whole file is intact (BodyWhole; null when that could not be checked).
        SaveHeader Read(string name);
        // True when the save was deleted.
        bool Delete(string name);
        // Writes the save file's cached data to the disk; throws when it cannot.
        void Flush(string name);
    }

    internal sealed class SaveHeader
    {
        public string Name;
        public IDictionary<string, string> Metadata;   // full keys (mnbl.world.* and the game's own); null when unreadable
        public bool? BodyWhole;                        // Read only: the game data after the header is intact (SaveFileCheck); null unchecked
        public string BodyProblem;
        // MMR-48: an unreadable header whose file could not be opened (sharing, permission): unknown, never torn (SaveReadback). HeaderProblem
        // says why a header is unreadable.
        public bool HeaderUnknown { get; set; }
        public string HeaderProblem { get; set; }
        public bool Readable { get { return Metadata != null; } }
    }

    // One initial save's journal: the publication's intent, written durably before the engine is asked to save, and its outcome.
    internal sealed class InitialSave
    {
        public string Instance, WorldId, ManifestSha256, ContentId, SaveName, State, UniqueGameId;
        public List<Dictionary<string, object>> History = new List<Dictionary<string, object>>();
        public WorldBinding Binding { get { return new WorldBinding { WorldId = WorldId, ManifestSha256 = ManifestSha256, Instance = Instance, ContentId = ContentId }; } }
        public string LastNote { get { object note; return History.Count == 0 || !History[History.Count - 1].TryGetValue("note", out note) ? null : note as string; } }

        public Dictionary<string, object> Report()
        {
            return new Dictionary<string, object> { ["instance"] = Instance, ["worldId"] = WorldId, ["package"] = Binding.PackageName, ["contentId"] = ContentId, ["saveName"] = SaveName,
                ["state"] = State, ["uniqueGameId"] = UniqueGameId, ["note"] = LastNote, ["transitions"] = History.Count };
        }
    }

    internal sealed class RecoveryReport
    {
        public readonly List<Dictionary<string, object>> Resolved = new List<Dictionary<string, object>>();
        public readonly List<Dictionary<string, object>> Unresolved = new List<Dictionary<string, object>>();
        public readonly List<string> UnreadableJournals = new List<string>();

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object> { ["resolved"] = Resolved, ["unresolved"] = Unresolved, ["unreadableJournals"] = UnreadableJournals };
        }
    }

    // MMR-62: binds a generated campaign's initial save to its world before activation reports success, and recovers a publication that
    // was interrupted. The binding itself is the save header's mnbl.world.* keys (R05: CollectMetadataEntriesEvent, readable at the main menu
    // without deserializing); this store adds the journal that makes the initial save's publication recoverable:
    //   1. Begin: the world package is published and verifies, the reserved save name is free, and the intent (instance, package, content id,
    //      save name) is written durably (DurableFile) before the engine is asked to save. Nothing is written when a check fails.
    //   2. The engine saves (SaveHandler.SaveAs; InitialSavePublisher waits for OnSaveOver).
    //   3. Complete: the save is read back from the game's own save list. Its header must carry this instance's binding; then the journal
    //      says published, and only then may activation report success. Any other outcome is resolved as below.
    // Resolution, the same after a crash (Recover, at the main menu before any save is listed to the player) as after a reported failure:
    //   - no save under the reserved name: abandoned (the world package stays; nothing references it but nothing deletes it either);
    //   - a save whose header cannot be read, has no binding or a malformed one: a torn or unbound write of this publication (the name was
    //     reserved: no save had it when the intent was written), so it is deleted through the game and the journal says removed;
    //   - a readable save bound to this instance and world whose game data is whole (SaveFileCheck): flushed to the disk, then published
    //     (the crash came after the engine wrote it); with torn game data behind a whole header: deleted, removed;
    //   - a readable save bound to another instance or world: kept, the journal says conflict (a bound save is never deleted).
    // When the save list cannot be read, a deletion does not take, the game data could not be read or the save could not be flushed, the
    // intent stays unresolved: nothing is deleted, and the reference index counts the pending publication as a reference. Only the process
    // holding the store lock may begin a publication (writerAllowed), so recovery in the lock holder never races another process's
    // publication. Pure managed: the game's save list is an ISaveCatalog. MMR-48 reuses this journal for the later saves (SaveJournal: the
    // same states, durable writes, readback rules and startup pass).
    internal sealed class EarlyWorldBindingStore
    {
        public const string JournalSchema = "mnbl.initial-save-journal.v1";
        public const string Intent = "intent", Published = "published", Abandoned = "abandoned", Removed = "removed", Conflict = "conflict";

        private readonly string root;
        private readonly ISaveCatalog saves;
        private readonly Func<WorldBinding, bool> packagePublished;
        private readonly Func<DateTime> clock;
        private readonly Func<bool> writerAllowed;

        public EarlyWorldBindingStore(string storeRoot, ISaveCatalog saves, Func<WorldBinding, bool> packagePublished, Func<bool> writerAllowed = null, Func<DateTime> clock = null)
        {
            if (storeRoot == null || saves == null || packagePublished == null) throw new ArgumentNullException();
            root = Path.GetFullPath(storeRoot); this.saves = saves; this.packagePublished = packagePublished; this.writerAllowed = writerAllowed; this.clock = clock ?? (() => DateTime.UtcNow);
        }

        public string Root { get { return root; } }
        public string JournalDirectory { get { return Path.Combine(root, "saves", "journal"); } }
        public ISaveCatalog Saves { get { return saves; } }

        // The reserved name of an instance's initial save.
        public static string InitialSaveName(WorldBinding binding) { return binding.WorldId + "_start_" + binding.Instance.Substring(0, 12); }

        // A save name the store reserves or deletes: a plain file name (MBSaveLoad has no path guard of its own).
        public static bool ValidSaveName(string name)
        {
            return !String.IsNullOrEmpty(name) && name.Length <= 200 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name.Trim() == name && name != "." && name != "..";
        }

        public InitialSave Begin(WorldBinding binding, string saveName)
        {
            if (binding == null) throw new ArgumentNullException("binding");
            binding.Validate();
            if (binding.Legacy) throw new InvalidDataException("An initial save needs a campaign instance in its binding.");
            if (!ValidSaveName(saveName)) throw new ArgumentException("Not a valid save name: '" + saveName + "'.");
            if (writerAllowed != null && !writerAllowed()) throw new InvalidOperationException("Another game process uses the world store; this process may not publish a save.");
            string path = JournalPath(binding.Instance);
            if (File.Exists(path)) throw new InvalidOperationException("Campaign instance " + binding.Instance + " already has an initial save journal.");
            if (!packagePublished(binding)) throw new InvalidDataException("World package " + binding.PackageName + " is not published or does not verify; the initial save would dangle.");
            if (saves.Read(saveName) != null) throw new IOException("A save named " + saveName + " exists; an initial save never replaces one.");
            var record = new InitialSave { Instance = binding.Instance, WorldId = binding.WorldId, ManifestSha256 = binding.ManifestSha256, ContentId = binding.ContentId, SaveName = saveName };
            Transition(record, Intent, "intent written before the engine save");
            return record;
        }

        // After the engine reported the save (engineSucceeded is OnSaveOver's result). Returns the journal: Published only when the save was
        // read back bound to this instance. Throws when the outcome cannot be resolved (the intent then stays for Recover).
        public InitialSave Complete(string instance, bool engineSucceeded)
        {
            var record = Read(JournalPath(instance));
            if (record.State != Intent) return record;
            Resolve(record, engineSucceeded ? "the engine reported the save" : "the engine reported a failed save");
            return record;
        }

        // Resolves every intent left by an interrupted publication. Never throws for one journal: failures are reported.
        public RecoveryReport Recover()
        {
            var report = new RecoveryReport();
            List<string> unreadable;
            foreach (var record in Journals(out unreadable))
            {
                if (record.State != Intent) continue;
                try { Resolve(record, "recovered at startup"); report.Resolved.Add(record.Report()); }
                catch (Exception error)
                {
                    var row = record.Report(); row["error"] = error.GetType().Name + ": " + error.Message;
                    report.Unresolved.Add(row);
                }
            }
            report.UnreadableJournals.AddRange(unreadable);
            return report;
        }

        public InitialSave Journal(string instance) { string path = JournalPath(instance); return File.Exists(path) ? Read(path) : null; }

        // Every journal; a journal that cannot be read is listed in unreadable (the index then cannot be complete).
        public List<InitialSave> Journals(out List<string> unreadable)
        {
            var records = new List<InitialSave>();
            unreadable = new List<string>();
            if (!Directory.Exists(JournalDirectory)) return records;
            foreach (var path in Directory.GetFiles(JournalDirectory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                if (DurableFile.IsTemporary(path)) continue;
                try { records.Add(Read(path)); }
                catch (Exception error) { unreadable.Add(Path.GetFileName(path) + ": " + error.GetType().Name + ": " + error.Message); }
            }
            return records;
        }

        // MMR-48: the rules are SaveReadback's, shared with the later saves' journal (SaveJournal); the initial save's name is reserved.
        private void Resolve(InitialSave record, string context)
        {
            var header = saves.Read(record.SaveName);
            var readback = SaveReadback.Decide(header, record.SaveName, record.Binding, true, true);
            switch (readback.Action)
            {
                case ReadbackAction.Abandon:
                    Transition(record, Abandoned, readback.Note + " (" + context + "); the world package is kept");
                    return;
                case ReadbackAction.Delete:
                    DeleteOwn(record, readback.Note + " (" + context + ")");
                    return;
                case ReadbackAction.Publish:
                    saves.Flush(record.SaveName);
                    string unique; record.UniqueGameId = header.Metadata.TryGetValue("UniqueGameId", out unique) ? unique : null;
                    Transition(record, Published, readback.Note + " (" + context + ")");
                    return;
                case ReadbackAction.Conflict:
                    Transition(record, Conflict, readback.Note + " (" + context + ")");
                    return;
                default:
                    throw new IOException(Char.ToUpperInvariant(readback.Note[0]) + readback.Note.Substring(1) + ".");
            }
        }

        // The reserved name was free when the intent was written, so a save under it is this publication's own.
        private void DeleteOwn(InitialSave record, string why)
        {
            bool deleted = saves.Delete(record.SaveName);
            if (!deleted || saves.Read(record.SaveName) != null) throw new IOException("Could not remove " + record.SaveName + " (" + why + ").");
            Transition(record, Removed, "deleted: " + why);
        }

        private string JournalPath(string instance)
        {
            if (instance == null || instance.Length != 32 || instance.Any(c => !Uri.IsHexDigit(c) || Char.IsUpper(c))) throw new ArgumentException("Not a campaign instance: " + instance);
            return Path.Combine(JournalDirectory, instance + ".json");
        }

        private void Transition(InitialSave record, string state, string note)
        {
            record.State = state;
            record.History.Add(new Dictionary<string, object> { ["state"] = state, ["utc"] = clock().ToString("o", CultureInfo.InvariantCulture), ["note"] = note });
            var document = new Dictionary<string, object> {
                ["schema"] = JournalSchema, ["instance"] = record.Instance, ["worldId"] = record.WorldId, ["manifestSha256"] = record.ManifestSha256,
                ["contentId"] = record.ContentId, ["package"] = record.Binding.PackageName, ["saveName"] = record.SaveName, ["state"] = record.State,
                ["uniqueGameId"] = record.UniqueGameId, ["history"] = record.History };
            DurableFile.WriteAllText(JournalPath(record.Instance), new JavaScriptSerializer().Serialize(document));
        }

        private static InitialSave Read(string path)
        {
            Dictionary<string, object> document;
            try { document = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>; }
            catch (ArgumentException error) { throw new InvalidDataException("the journal is not JSON: " + error.Message); }
            catch (InvalidOperationException error) { throw new InvalidDataException("the journal is not JSON: " + error.Message); }
            if (document == null || !(document.ContainsKey("schema") && (document["schema"] as string) == JournalSchema)) throw new InvalidDataException("not an initial save journal");
            Func<string, string> text = key => { object value; return document.TryGetValue(key, out value) ? value as string : null; };
            var record = new InitialSave { Instance = text("instance"), WorldId = text("worldId"), ManifestSha256 = text("manifestSha256"), ContentId = text("contentId"),
                SaveName = text("saveName"), State = text("state"), UniqueGameId = text("uniqueGameId") };
            if (record.Instance + ".json" != Path.GetFileName(path)) throw new InvalidDataException("the journal's instance is not its file name");
            if (!ValidSaveName(record.SaveName) || !new[] { Intent, Published, Abandoned, Removed, Conflict }.Contains(record.State)) throw new InvalidDataException("the journal has no valid save name or no known state");
            record.Binding.Validate();
            object history;
            if (!document.TryGetValue("history", out history) || !(history is IEnumerable)) throw new InvalidDataException("the journal has no history");
            foreach (var entry in (IEnumerable)history)
            {
                var row = entry as Dictionary<string, object>;
                object state, utc, note;
                if (row == null || !row.TryGetValue("state", out state) || !(state is string) || !row.TryGetValue("utc", out utc) || !(utc is string) || !row.TryGetValue("note", out note) || !(note is string))
                    throw new InvalidDataException("a journal history row lacks its state, time or note");
                record.History.Add(row);
            }
            if (record.History.Count == 0 || (string)record.History[record.History.Count - 1]["state"] != record.State) throw new InvalidDataException("the journal's state is not its last history row's");
            return record;
        }
    }
}
