using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace MNBL.ProceduralWorld.Generation
{
    // MMR-47: every failure the player is shown gets a correlation id, and its diagnostics are retained under it:
    // <store>/diagnostics/<id>/error.json (category, code, the player message and action ids, the detail, the job, the time, the host), with
    // copies of the job's own records (timings.json, the last events, the bridge and producer logs, truncated). A job's directory is deleted
    // when the setup releases it, so the copies keep what a report needs. The player sees the message, the action and "Reference <id>";
    // the detail and the folder are behind the setup's Details. Ids look like MNBL-20261017-104812-3fa9c1 (UTC time and 24 random bits) and
    // are unique within a store; the newest Keep records are kept. Pure managed; any thread.
    internal sealed class GenerationDiagnostics
    {
        public const int Keep = 100;
        public const int MaxLogBytes = 64 * 1024;
        public const int EventLines = 60;
        private static readonly string[] JobFiles = { "timings.json", "choice.json", "request.json", "state.json" };
        private readonly string root;
        private readonly Func<DateTime> clock;
        private readonly object gate = new object();
        private readonly Func<string> rootProblem;

        // rootProblem: why the store is not the verified data root (null when it is); records are not written under an unverified root.
        public GenerationDiagnostics(string store, Func<DateTime> clock = null, Func<string> rootProblem = null)
        {
            root = Path.Combine(Path.GetFullPath(store), "diagnostics");
            this.clock = clock ?? (() => DateTime.UtcNow);
            this.rootProblem = rootProblem;
        }

        public string Root { get { return root; } }
        public string Folder(string correlationId) { return Path.Combine(root, correlationId); }

        // Assigns the error its correlation id (once) and writes its record; returns the id. A failure to write is recorded on the error
        // (RetainProblem) and never thrown: the player still sees the message and the id.
        public string Retain(GenerationError error, string jobId = null, string jobRoot = null, IDictionary<string, object> context = null)
        {
            if (error == null) return null;
            lock (gate)
            {
                if (error.CorrelationId != null && Directory.Exists(Folder(error.CorrelationId))) return error.CorrelationId;
                if (error.CorrelationId == null) error.CorrelationId = NewId();
                try
                {
                    string problem = rootProblem == null ? null : rootProblem();
                    if (problem != null) { error.RetainProblem = "the data root did not verify: " + problem; return error.CorrelationId; }
                    if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) { error.RetainProblem = "the diagnostics folder is a link"; return error.CorrelationId; }
                    string folder = Folder(error.CorrelationId);
                    Directory.CreateDirectory(folder);
                    var copied = new List<string>();
                    if (jobRoot != null && Directory.Exists(jobRoot) && (File.GetAttributes(jobRoot) & FileAttributes.ReparsePoint) == 0)
                    {
                        foreach (var name in JobFiles) if (Copy(Path.Combine(jobRoot, name), Path.Combine(folder, name), MaxLogBytes)) copied.Add(name);
                        string events = Path.Combine(jobRoot, "events.jsonl");
                        if (File.Exists(events))
                        {
                            var tail = File.ReadAllLines(events).Reverse().Take(EventLines).Reverse().ToArray();
                            File.WriteAllLines(Path.Combine(folder, "events.jsonl"), tail, new UTF8Encoding(false)); copied.Add("events.jsonl");
                        }
                        foreach (var log in Directory.GetFiles(jobRoot, "*.log").OrderBy(p => p, StringComparer.Ordinal))
                            if (Copy(log, Path.Combine(folder, Path.GetFileName(log)), MaxLogBytes)) copied.Add(Path.GetFileName(log));
                    }
                    var record = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                        ["schema"] = "mnbl.generation-diagnostics.v1", ["correlationId"] = error.CorrelationId, ["utc"] = clock().ToString("o", CultureInfo.InvariantCulture),
                        ["category"] = error.Category.ToString(), ["code"] = error.Code, ["message"] = error.Message, ["title"] = error.Title, ["action"] = error.Action,
                        ["variables"] = error.Variables, ["detail"] = error.Detail, ["jobId"] = jobId, ["jobFiles"] = copied };
                    if (context != null) foreach (var kv in context) record["context." + kv.Key] = kv.Value;
                    File.WriteAllText(Path.Combine(folder, "error.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(record), new UTF8Encoding(false));
                    error.RetainProblem = null;
                    Prune();
                }
                catch (Exception failure) when (failure is IOException || failure is UnauthorizedAccessException || failure is ArgumentException)
                {
                    error.RetainProblem = failure.GetType().Name + ": " + failure.Message;
                }
                return error.CorrelationId;
            }
        }

        // The retained record, or null.
        public IDictionary<string, object> Read(string correlationId)
        {
            string path = Path.Combine(Folder(correlationId), "error.json");
            return File.Exists(path) ? (IDictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(File.ReadAllText(path)) : null;
        }

        public IList<string> Ids()
        {
            return Directory.Exists(root) ? Directory.GetDirectories(root, "MNBL-*").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList() : new List<string>();
        }

        private string NewId()
        {
            var bytes = new byte[3];
            using (var rng = RandomNumberGenerator.Create())
                while (true)
                {
                    rng.GetBytes(bytes);
                    string id = "MNBL-" + clock().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
                    if (!Directory.Exists(Folder(id))) return id;
                }
        }

        // The newest Keep records stay (ids sort by time); older folders go, never following links. A folder that cannot be removed is left
        // (and retried next time); it never fails the record just written.
        public int PruneFailures { get; private set; }
        private void Prune()
        {
            var ids = Ids();
            foreach (var old in ids.Take(Math.Max(0, ids.Count - Keep)))
            {
                string folder = Folder(old);
                try
                {
                    if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(folder, false); continue; }   // the link, not its target
                    MNBL.ProceduralWorld.Persistence.StagingCleanup.DeleteTree(folder);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { PruneFailures++; }
            }
        }

        // A file's first max bytes (a note marks a cut); false when there is none.
        private static bool Copy(string from, string to, int max)
        {
            if (!File.Exists(from) || (File.GetAttributes(from) & FileAttributes.ReparsePoint) != 0) return false;
            using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(to, FileMode.Create, FileAccess.Write))
            {
                var buffer = new byte[Math.Min(max, (int)Math.Max(1, input.Length))];
                int read = input.Read(buffer, 0, buffer.Length);
                output.Write(buffer, 0, read);
                if (input.Length > max) { var note = Encoding.UTF8.GetBytes("\n[cut at " + max + " of " + input.Length + " bytes]\n"); output.Write(note, 0, note.Length); }
            }
            return true;
        }
    }
}
