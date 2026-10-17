using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MNBL.ProceduralWorld.Persistence
{
    // A save file's size and last write time (UTC ticks); null stands for no file.
    internal sealed class SaveFileStat
    {
        public long Length, WrittenUtcTicks;
        public bool SameAs(SaveFileStat other) { return other != null && other.Length == Length && other.WrittenUtcTicks == WrittenUtcTicks; }
    }

    internal enum ReadbackAction { Abandon, Delete, Publish, Conflict, Unresolved }

    internal struct Readback
    {
        public ReadbackAction Action;
        public string Note;
        public static Readback Of(ReadbackAction action, string note) { return new Readback { Action = action, Note = note }; }
    }

    // MMR-48: the readback rules a save journal applies to the save found under its name, after the engine reported (Complete) or after a
    // crash (Recover). Shared by the initial save (EarlyWorldBindingStore, MMR-62) and the later saves (SaveJournal). The two differ in what the
    // journal knows about the name:
    //   - reserved (the initial save): no save had the name when the intent was written, so any save under it is this publication's own: a
    //     torn, unbound or malformed one is deleted;
    //   - not reserved (a later save: Save As, quick save, autosave and Ironman save overwrite whatever has the name): the intent records the
    //     file that had the name (size and write time). A file unchanged since then was never written by this save and is kept (abandoned);
    //     a changed file whose header cannot be read, or whose game data is torn behind this campaign's own header, is this save's torn write
    //     and is deleted; a changed file that is unbound, malformed or bound to another campaign is kept (conflict): a later save never deletes
    //     a save it cannot prove its own. When whether the file changed cannot be told, nothing is deleted (unresolved).
    // A whole save bound to the journal's instance and world is published (after a flush). A save whose header could not be opened (a sharing
    // violation, a permission error: SaveHeader.HeaderUnknown) or whose game data could not be read stays unresolved either way: only bytes
    // that are there and wrong count as a torn write (review finding: a sync client holding the file must never get a whole save deleted).
    internal static class SaveReadback
    {
        // changed: whether the file under the name was written after the intent (true, also for a file that did not exist then), was not
        // (false) or cannot be told (null). Ignored for a reserved name.
        public static Readback Decide(SaveHeader header, string saveName, WorldBinding expected, bool nameReserved, bool? changed)
        {
            if (header == null) return Readback.Of(ReadbackAction.Abandon, "no save named " + saveName);
            bool own = nameReserved || changed == true;
            if (!nameReserved && changed == false) return Readback.Of(ReadbackAction.Abandon, "the file named " + saveName + " is unchanged since the intent: the engine never wrote it, and it is kept");
            if (!header.Readable && header.HeaderUnknown)
                return Readback.Of(ReadbackAction.Unresolved, "its header could not be opened (" + (header.HeaderProblem ?? "unknown") + "), so whether it is torn cannot be told");
            if (!header.Readable)
                return own ? Readback.Of(ReadbackAction.Delete, "its header cannot be read: a torn or partial write")
                           : Readback.Of(ReadbackAction.Unresolved, "its header cannot be read and whether this save wrote it cannot be told");
            WorldBinding found;
            try { found = WorldBinding.FromHeader(header.Metadata); }
            catch (InvalidDataException error)
            {
                return nameReserved ? Readback.Of(ReadbackAction.Delete, "its world binding is malformed: " + error.Message)
                                    : Readback.Of(ReadbackAction.Conflict, "its world binding is malformed (" + error.Message + "); not provably this save's, it is kept");
            }
            if (found == null)
                return nameReserved ? Readback.Of(ReadbackAction.Delete, "its header carries no world binding")
                                    : Readback.Of(ReadbackAction.Conflict, "its header carries no world binding: another game's save under the name, it is kept");
            if (found.Instance == expected.Instance && found.SameWorld(expected))
            {
                // A write cut short can leave a whole header before torn game data.
                if (header.BodyWhole == false)
                    return own ? Readback.Of(ReadbackAction.Delete, "its game data is torn: " + header.BodyProblem)
                               : Readback.Of(ReadbackAction.Unresolved, "its game data is torn (" + header.BodyProblem + ") and whether this save wrote it cannot be told");
                if (header.BodyWhole != true) return Readback.Of(ReadbackAction.Unresolved, "could not check that " + saveName + " is whole" + (header.BodyProblem == null ? "" : ": " + header.BodyProblem));
                return Readback.Of(ReadbackAction.Publish, "read back bound to this instance");
            }
            return Readback.Of(ReadbackAction.Conflict, (nameReserved ? "the save under the reserved name" : "the save under the name") + " is bound to " + found.WorldId + "/" + (found.Instance ?? "legacy") + "; it is kept");
        }
    }

    // One later save's journal: its intent, written durably before the engine writes the file, and its outcome.
    internal sealed class SaveOperation
    {
        public string Id, Kind, Instance, WorldId, ManifestSha256, ContentId, SaveName, State, UniqueGameId, ReportedName;
        public bool? ReportedSuccess;
        public bool PriorExists;
        public long PriorLength, PriorWrittenUtcTicks;
        public List<Dictionary<string, object>> History = new List<Dictionary<string, object>>();
        public WorldBinding Binding { get { return new WorldBinding { WorldId = WorldId, ManifestSha256 = ManifestSha256, Instance = Instance, ContentId = ContentId }; } }
        public SaveFileStat Prior { get { return PriorExists ? new SaveFileStat { Length = PriorLength, WrittenUtcTicks = PriorWrittenUtcTicks } : null; } }
        public string LastNote { get { object note; return History.Count == 0 || !History[History.Count - 1].TryGetValue("note", out note) ? null : note as string; } }

        public Dictionary<string, object> Report()
        {
            return new Dictionary<string, object> { ["id"] = Id, ["kind"] = Kind, ["instance"] = Instance, ["worldId"] = WorldId, ["package"] = Binding.PackageName, ["contentId"] = ContentId,
                ["saveName"] = SaveName, ["state"] = State, ["uniqueGameId"] = UniqueGameId, ["priorExists"] = PriorExists, ["reportedSuccess"] = ReportedSuccess, ["reportedName"] = ReportedName,
                ["note"] = LastNote, ["transitions"] = History.Count };
        }
    }

    // MMR-48: the journal of a generated campaign's later saves (Save As, quick save, autosave, Ironman save), the MMR-62 initial-save journal
    // reused: the same states, durable writes (DurableFile), readback rules (SaveReadback), startup recovery pass and validation, one record
    // per save under <store>/saves/journal/later/<id>.json (the initial-save journals are <store>/saves/journal/<instance>.json).
    //   1. Begin, when the engine has chosen the save's name and before it writes the file (WorldSaveBinding's prefix on
    //      MBSaveLoad.OverwriteSaveAux, the one method every save mode goes through): the binding has an instance, this process holds the
    //      store lock, the name is a plain file name; the intent (kind, instance, world, content id, name, and the size and write time of the
    //      file that had the name) is written durably. Nothing is written when a check fails.
    //   2. The engine saves (the installed AsyncFileSaveDriver; OnSaveOver after the write, or before it with an empty name when the game
    //      refused the save).
    //   3. Complete: the save is read back from the game's save list, never trusted from the engine's report; the outcome follows SaveReadback.
    // Recover resolves every intent a crash left, at the main menu before the save list is shown. When the save list cannot be read, the game
    // data could not be checked, a deletion does not take or the flush fails, the intent stays (retried at the next start, and counted by the
    // reference index as a pending save). Resolved journals are pruned beyond the newest ResolvedKept; intents never are. Pure managed.
    internal sealed class SaveJournal
    {
        public const string Schema = "mnbl.save-journal.v1";
        public const string SaveAs = "saveAs", QuickSave = "quickSave", AutoSave = "autoSave", Ironman = "ironman";
        public static readonly string[] Kinds = { SaveAs, QuickSave, AutoSave, Ironman };
        public const int ResolvedKept = 64;
        // An intent is this save's only within this long of its write: a file under the name written later is another save's (review finding).
        public static readonly TimeSpan WriteWindow = TimeSpan.FromMinutes(10);
        private static readonly Regex IdPattern = new Regex("^[0-9]{8}T[0-9]{9}Z-[0-9a-f]{8}$", RegexOptions.CultureInvariant);
        private static readonly string[] States = { EarlyWorldBindingStore.Intent, EarlyWorldBindingStore.Published, EarlyWorldBindingStore.Abandoned, EarlyWorldBindingStore.Removed, EarlyWorldBindingStore.Conflict };

        private readonly string root;
        private readonly ISaveCatalog saves;
        private readonly Func<string, SaveFileStat> stat;
        private readonly Func<bool> writerAllowed;
        private readonly Func<DateTime> clock;

        public SaveJournal(string storeRoot, ISaveCatalog saves, Func<string, SaveFileStat> stat, Func<bool> writerAllowed = null, Func<DateTime> clock = null)
        {
            if (storeRoot == null || saves == null || stat == null) throw new ArgumentNullException();
            root = Path.GetFullPath(storeRoot); this.saves = saves; this.stat = stat; this.writerAllowed = writerAllowed; this.clock = clock ?? (() => DateTime.UtcNow);
        }

        public string Directory { get { return Path.Combine(root, "saves", "journal", "later"); } }

        // A save name the engine can write as "<name>.sav" in the save folder: a plain file name (no separator, no invalid character), so the
        // journal never addresses a file outside it. Wider than the initial save's reserved names (EarlyWorldBindingStore.ValidSaveName):
        // the player names a Save As, and leading or trailing spaces or a long name must not leave a save unjournaled (review finding).
        public static bool ValidName(string name)
        {
            return !String.IsNullOrEmpty(name) && name.Length <= 240 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name != "." && name != ".." && !name.EndsWith(".", StringComparison.Ordinal);
        }
        public ISaveCatalog Saves { get { return saves; } }

        public SaveOperation Begin(WorldBinding binding, string kind, string saveName)
        {
            if (binding == null) throw new ArgumentNullException("binding");
            binding.Validate();
            if (binding.Legacy) throw new InvalidDataException("A save journal needs a campaign instance in its binding.");
            if (!Kinds.Contains(kind)) throw new ArgumentException("Not a save kind: " + kind);
            if (!ValidName(saveName)) throw new ArgumentException("Not a valid save name: '" + saveName + "'.");
            if (writerAllowed != null && !writerAllowed()) throw new InvalidOperationException("Another game process uses the world store; this process may not journal a save.");
            var prior = stat(saveName);
            var now = clock();
            var operation = new SaveOperation { Id = now.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8), Kind = kind,
                Instance = binding.Instance, WorldId = binding.WorldId, ManifestSha256 = binding.ManifestSha256, ContentId = binding.ContentId, SaveName = saveName,
                PriorExists = prior != null, PriorLength = prior == null ? 0 : prior.Length, PriorWrittenUtcTicks = prior == null ? 0 : prior.WrittenUtcTicks };
            Transition(operation, EarlyWorldBindingStore.Intent, "intent written before the engine save (" + kind + ", " + (prior == null ? "a new file" : "replacing a file of " + prior.Length + " bytes") + ")");
            return operation;
        }

        // After the engine reported the save (OnSaveOver's result and name; the name is empty when the game refused the save before writing).
        // Returns the journal; throws when the outcome cannot be resolved (the intent then stays for Recover).
        public SaveOperation Complete(string id, bool engineSucceeded, string reportedName)
        {
            var operation = Read(PathOf(id));
            if (operation.State != EarlyWorldBindingStore.Intent) return operation;
            operation.ReportedSuccess = engineSucceeded; operation.ReportedName = reportedName;
            Resolve(operation, (engineSucceeded ? "the engine reported the save" : "the engine reported a failed save") + (reportedName == operation.SaveName ? "" : " under '" + reportedName + "'"));
            return operation;
        }

        // Resolves every intent a crash left, then prunes resolved journals. Never throws for one journal: failures are reported. An intent with
        // a newer journal under the same name is superseded: the newer save had the name after it, so the file there is not this save's.
        public RecoveryReport Recover()
        {
            var report = new RecoveryReport();
            List<string> unreadable;
            var journals = Journals(out unreadable);
            foreach (var operation in journals)
            {
                if (operation.State != EarlyWorldBindingStore.Intent) continue;
                var newer = journals.LastOrDefault(j => String.CompareOrdinal(j.Id, operation.Id) > 0 && String.Equals(j.SaveName, operation.SaveName, StringComparison.OrdinalIgnoreCase));
                try
                {
                    if (newer != null) Transition(operation, EarlyWorldBindingStore.Abandoned, "superseded by the later save journal " + newer.Id + " under the same name; the file there is not this save's (recovered at startup)");
                    else Resolve(operation, "recovered at startup");
                    report.Resolved.Add(operation.Report());
                }
                catch (Exception error)
                {
                    var row = operation.Report(); row["error"] = error.GetType().Name + ": " + error.Message;
                    report.Unresolved.Add(row);
                }
            }
            report.UnreadableJournals.AddRange(unreadable);
            try { Prune(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return report;
        }

        public SaveOperation Journal(string id) { string path = PathOf(id); return File.Exists(path) ? Read(path) : null; }

        // Every journal, oldest first; one that cannot be read is listed in unreadable (the reference index then cannot be complete).
        public List<SaveOperation> Journals(out List<string> unreadable)
        {
            var records = new List<SaveOperation>();
            unreadable = new List<string>();
            if (!System.IO.Directory.Exists(Directory)) return records;
            foreach (var path in System.IO.Directory.GetFiles(Directory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                if (DurableFile.IsTemporary(path)) continue;
                try { records.Add(Read(path)); }
                catch (Exception error) { unreadable.Add(Path.GetFileName(path) + ": " + error.GetType().Name + ": " + error.Message); }
            }
            return records;
        }

        // Removes resolved journals beyond the newest ResolvedKept (only the store lock holder); intents and unreadable journals are kept.
        public int Prune()
        {
            if (writerAllowed != null && !writerAllowed()) return 0;
            List<string> unreadable;
            int removed = 0;
            foreach (var old in Journals(out unreadable).Where(j => j.State != EarlyWorldBindingStore.Intent).OrderByDescending(j => j.Id, StringComparer.Ordinal).Skip(ResolvedKept))
            {
                File.Delete(PathOf(old.Id));
                removed++;
            }
            return removed;
        }

        private void Resolve(SaveOperation operation, string context)
        {
            var header = saves.Read(operation.SaveName);
            bool? changed = null;
            if (header != null)
            {
                var now = stat(operation.SaveName);
                DateTime intent;
                if (now != null && DateTime.TryParse(operation.History[0]["utc"] as string, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out intent)
                    && now.WrittenUtcTicks > intent.ToUniversalTime().Add(WriteWindow).Ticks)
                {
                    Transition(operation, EarlyWorldBindingStore.Abandoned, "the file named " + operation.SaveName + " was written more than " + WriteWindow.TotalMinutes + " minutes after the intent: another save's, and it is kept (" + context + ")");
                    return;
                }
                if (!operation.PriorExists) changed = true;
                else if (now != null) changed = !now.SameAs(operation.Prior);
            }
            var readback = SaveReadback.Decide(header, operation.SaveName, operation.Binding, false, changed);
            switch (readback.Action)
            {
                case ReadbackAction.Abandon:
                    Transition(operation, EarlyWorldBindingStore.Abandoned, readback.Note + " (" + context + ")");
                    return;
                case ReadbackAction.Delete:
                    bool deleted = saves.Delete(operation.SaveName);
                    if (!deleted || saves.Read(operation.SaveName) != null) throw new IOException("Could not remove " + operation.SaveName + " (" + readback.Note + ").");
                    Transition(operation, EarlyWorldBindingStore.Removed, "deleted: " + readback.Note + " (" + context + ")");
                    return;
                case ReadbackAction.Publish:
                    saves.Flush(operation.SaveName);
                    string unique; operation.UniqueGameId = header.Metadata.TryGetValue("UniqueGameId", out unique) ? unique : null;
                    Transition(operation, EarlyWorldBindingStore.Published, readback.Note + " (" + context + ")");
                    return;
                case ReadbackAction.Conflict:
                    Transition(operation, EarlyWorldBindingStore.Conflict, readback.Note + " (" + context + ")");
                    return;
                default:
                    throw new IOException(Char.ToUpperInvariant(readback.Note[0]) + readback.Note.Substring(1) + ".");
            }
        }

        private string PathOf(string id)
        {
            if (id == null || !IdPattern.IsMatch(id)) throw new ArgumentException("Not a save journal id: " + id);
            return Path.Combine(Directory, id + ".json");
        }

        private void Transition(SaveOperation operation, string state, string note)
        {
            operation.State = state;
            operation.History.Add(new Dictionary<string, object> { ["state"] = state, ["utc"] = clock().ToString("o", CultureInfo.InvariantCulture), ["note"] = note });
            var document = new Dictionary<string, object> {
                ["schema"] = Schema, ["id"] = operation.Id, ["kind"] = operation.Kind, ["instance"] = operation.Instance, ["worldId"] = operation.WorldId,
                ["manifestSha256"] = operation.ManifestSha256, ["contentId"] = operation.ContentId, ["package"] = operation.Binding.PackageName, ["saveName"] = operation.SaveName,
                ["prior"] = new Dictionary<string, object> { ["exists"] = operation.PriorExists, ["length"] = operation.PriorLength, ["writtenUtcTicks"] = operation.PriorWrittenUtcTicks.ToString(CultureInfo.InvariantCulture) },
                ["state"] = operation.State, ["uniqueGameId"] = operation.UniqueGameId, ["reportedSuccess"] = operation.ReportedSuccess, ["reportedName"] = operation.ReportedName,
                ["history"] = operation.History };
            DurableFile.WriteAllText(PathOf(operation.Id), new JavaScriptSerializer().Serialize(document));
        }

        private static SaveOperation Read(string path)
        {
            Dictionary<string, object> document;
            try { document = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>; }
            catch (ArgumentException error) { throw new InvalidDataException("the journal is not JSON: " + error.Message); }
            catch (InvalidOperationException error) { throw new InvalidDataException("the journal is not JSON: " + error.Message); }
            if (document == null || !(document.ContainsKey("schema") && (document["schema"] as string) == Schema)) throw new InvalidDataException("not a save journal");
            Func<string, string> text = key => { object value; return document.TryGetValue(key, out value) ? value as string : null; };
            var operation = new SaveOperation { Id = text("id"), Kind = text("kind"), Instance = text("instance"), WorldId = text("worldId"), ManifestSha256 = text("manifestSha256"),
                ContentId = text("contentId"), SaveName = text("saveName"), State = text("state"), UniqueGameId = text("uniqueGameId"), ReportedName = text("reportedName") };
            object reported; if (document.TryGetValue("reportedSuccess", out reported) && reported is bool) operation.ReportedSuccess = (bool)reported;
            if (operation.Id == null || operation.Id + ".json" != Path.GetFileName(path) || !IdPattern.IsMatch(operation.Id)) throw new InvalidDataException("the journal's id is not its file name");
            if (!Kinds.Contains(operation.Kind)) throw new InvalidDataException("the journal has no known save kind");
            if (!ValidName(operation.SaveName) || !States.Contains(operation.State)) throw new InvalidDataException("the journal has no valid save name or no known state");
            operation.Binding.Validate();
            if (operation.Instance == null) throw new InvalidDataException("the journal has no campaign instance");
            var prior = document.TryGetValue("prior", out reported) ? reported as Dictionary<string, object> : null;
            object exists, length, written; long ticks;
            if (prior == null || !prior.TryGetValue("exists", out exists) || !(exists is bool) || !prior.TryGetValue("length", out length) || !(length is int || length is long)
                || !prior.TryGetValue("writtenUtcTicks", out written) || !(written is string) || !Int64.TryParse((string)written, NumberStyles.None, CultureInfo.InvariantCulture, out ticks))
                throw new InvalidDataException("the journal does not record the file that had the name");
            operation.PriorExists = (bool)exists; operation.PriorLength = Convert.ToInt64(length, CultureInfo.InvariantCulture); operation.PriorWrittenUtcTicks = ticks;
            object history;
            if (!document.TryGetValue("history", out history) || !(history is IEnumerable)) throw new InvalidDataException("the journal has no history");
            foreach (var entry in (IEnumerable)history)
            {
                var row = entry as Dictionary<string, object>;
                object state, utc, note;
                if (row == null || !row.TryGetValue("state", out state) || !(state is string) || !row.TryGetValue("utc", out utc) || !(utc is string) || !row.TryGetValue("note", out note) || !(note is string))
                    throw new InvalidDataException("a journal history row lacks its state, time or note");
                operation.History.Add(row);
            }
            if (operation.History.Count == 0 || (string)operation.History[operation.History.Count - 1]["state"] != operation.State) throw new InvalidDataException("the journal's state is not its last history row's");
            return operation;
        }
    }
}
