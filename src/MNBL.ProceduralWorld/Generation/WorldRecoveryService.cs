using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.Persistence;

namespace MNBL.ProceduralWorld.Generation
{
    // MMR-47: recovery after a crash and the store's cleanup, inside a verified data root. Pure managed (tested offline,
    // tests/Generation/RecoveryTests.cs).
    //   * The data root is the per-user %LOCALAPPDATA%\MNBL.ProceduralWorld (SubModule.Store): never the game installation, a Steam library,
    //     Program Files, or a path through a link (VerifyRoot). Nothing is cleaned or discarded under a root that does not verify.
    //   * Unfinished jobs: a generation job's directory under jobs/ that no live process owns and whose job did not end. Its phase is its last
    //     event's (events.jsonl is appended for every event, including the commit's), else its record's (timings.json). The setup offers each
    //     one: Resume (the same choice in the same directory: the worldbridge continues its persisted attempt state and an accepted world is
    //     reused, never regenerated) or Discard (its directory goes; bundles, packages, saves and activations stay). A job made under another
    //     generator (another producer lock) or other policies (its request binds other policy hashes) cannot be resumed: an old world is never
    //     regenerated under a new generator. A job a crash stopped while committing is left to the activation's recovery (MMR-45) and only
    //     discarded. A job whose records cannot be read is only discarded. One job's unreadable files never stop the others being listed.
    //   * Cleanup removes only staging nothing references (StagingCleanup, MMR-62: bundle staging, package and journal temporaries, encoder
    //     staging), finished job directories a stopped process left behind, and job directories with nothing to continue (an earlier version's
    //     or an emptied one); it never removes a bundle, a world package, an activation or a save, active or not.
    internal static class WorldRecoveryService
    {
        public const string StoreFolder = "MNBL.ProceduralWorld";
        // What a job found under jobs/ is: what the setup offers, and what the cleanup removes (Finished, Leftover).
        public const string Resumable = "resumable", OtherGenerator = "other generator", OtherPolicies = "other policies", Committing = "committing",
            Unrecorded = "unrecorded", Unreadable = "unreadable", Finished = "finished", Leftover = "leftover";
        private static readonly Regex JobId = new Regex("^[0-9a-f]{32}$");
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };

        // ---- The data root ----

        // Null when root is the expected per-user store (resolved, no link on its path, outside the game and Steam folders); else why not.
        // expected: the store this process uses (SubModule.Store); forbidden: the game installation and other managed folders.
        public static string VerifyRoot(string root, string expected, IEnumerable<string> forbidden)
        {
            if (String.IsNullOrWhiteSpace(root)) return "no data root";
            string full, want;
            try { full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar); want = Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar); }
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is PathTooLongException) { return "an invalid path: " + error.Message; }
            if (!String.Equals(full, want, StringComparison.OrdinalIgnoreCase)) return "not the per-user store " + want;
            foreach (var f in forbidden ?? Enumerable.Empty<string>())
            {
                if (String.IsNullOrEmpty(f)) continue;
                string other = Path.GetFullPath(f).TrimEnd(Path.DirectorySeparatorChar);
                if (Inside(full, other) || Inside(other, full)) return "inside or around a managed folder: " + other;
            }
            if (full.IndexOf(Path.DirectorySeparatorChar + "steamapps" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0 || full.EndsWith(Path.DirectorySeparatorChar + "steamapps", StringComparison.OrdinalIgnoreCase))
                return "inside a Steam library";
            // No link from the drive root down to the store (a junction could move it elsewhere).
            string path = Path.GetPathRoot(full);
            foreach (var part in full.Substring(path.Length).Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                path = Path.Combine(path, part);
                if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return "a link on its path: " + path;
            }
            return null;
        }

        private static bool Inside(string path, string parent) { return path.Equals(parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }

        // ---- Unfinished jobs ----

        public sealed class UnfinishedJob
        {
            public string Id, Root, Phase, Reason, Kind;
            public DateTime LastWriteUtc;
            // The choice the job was started with (choice.json, else its request.json).
            public uint Seed; public string SeedSource, CampaignInstanceId, ProducerLockSha256, Profile;
            public int RecipeVersion, MaxAttempts;
            public SortedDictionary<string, long> Overrides = new SortedDictionary<string, long>(StringComparer.Ordinal);
            public bool HasChoice;
            public Dictionary<string, string> RequestPolicies;   // the policy hashes its request bound (null without a request)
            public bool Resumable { get { return Kind == WorldRecoveryService.Resumable; } }
            public bool Finished { get { return Kind == WorldRecoveryService.Finished || Kind == WorldRecoveryService.Leftover; } }

            public Dictionary<string, object> Report()
            {
                return new Dictionary<string, object> { ["id"] = Id, ["phase"] = Phase, ["kind"] = Kind, ["finished"] = Finished, ["resumable"] = Resumable, ["reason"] = Reason,
                    ["seed"] = HasChoice ? (object)Seed : null, ["seedSource"] = SeedSource, ["campaignInstanceId"] = CampaignInstanceId, ["lastWriteUtc"] = LastWriteUtc.ToString("o", CultureInfo.InvariantCulture) };
            }
        }

        // Every job directory a stopped process left under jobsRoot (isLive: a job of this process), newest first. currentLock: the producer
        // lock sha256 this module generates with; policyMismatch: given a request's policy hashes, the first that differs from this module's
        // (null when all match); activeInstance: true when an activation journal for that campaign instance is still active.
        public static List<UnfinishedJob> Jobs(string jobsRoot, Func<string, bool> isLive, string currentLock, Func<string, bool> activeInstance = null,
                                               Func<IDictionary<string, string>, string> policyMismatch = null)
        {
            var result = new List<UnfinishedJob>();
            if (!Directory.Exists(jobsRoot) || (File.GetAttributes(jobsRoot) & FileAttributes.ReparsePoint) != 0) return result;
            foreach (var dir in Directory.GetDirectories(jobsRoot))
            {
                string id = Path.GetFileName(dir);
                if (!JobId.IsMatch(id)) continue;
                var job = new UnfinishedJob { Id = id, Root = dir };
                try
                {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                    if (isLive != null && isLive(dir)) continue;
                    job.LastWriteUtc = LastWrite(dir);
                    Classify(job, currentLock, activeInstance, policyMismatch);
                }
                catch (Exception error)
                {
                    // One job's unreadable files never stop the others: it is offered for Discard only.
                    job.Kind = Unreadable; job.Phase = job.Phase ?? "unknown"; job.Reason = "its records cannot be read: " + error.GetType().Name + ": " + error.Message;
                }
                result.Add(job);
            }
            return result.OrderByDescending(j => j.LastWriteUtc).ThenBy(j => j.Id, StringComparer.Ordinal).ToList();
        }

        private static void Classify(UnfinishedJob job, string currentLock, Func<string, bool> activeInstance, Func<IDictionary<string, string>, string> policyMismatch)
        {
            string dir = job.Root;
            ReadChoice(job);
            bool recorded = File.Exists(Path.Combine(dir, "timings.json")) || File.Exists(Path.Combine(dir, "events.jsonl"));
            // A directory with no choice, no generation request, no record and no events is not a generation job's (MMR-44): an earlier
            // version's job directory (the interim job's request/result/source) or one emptied by an interrupted release.
            if (!job.HasChoice && !recorded) { job.Phase = "Leftover"; job.Kind = Leftover; job.Reason = "left by an earlier version or an interrupted release: nothing to continue"; return; }
            job.Phase = PhaseOf(dir);
            JobPhase phase;
            bool known = Enum.TryParse(job.Phase, out phase);
            string mismatch;
            if (known && JobStateMachine.IsTerminal(phase)) { job.Kind = Finished; job.Reason = "the job ended (" + job.Phase + ") but its directory was not released"; }
            else if (known && phase == JobPhase.Committing)
            {
                job.Kind = Committing;
                job.Reason = activeInstance != null && job.CampaignInstanceId != null && activeInstance(Instance(job.CampaignInstanceId))
                    ? "stopped while starting its campaign: the activation's recovery decides it" : "stopped while starting its campaign";
            }
            else if (!job.HasChoice) { job.Kind = Unrecorded; job.Reason = "its choice was not recorded"; }
            else if (currentLock != null && job.ProducerLockSha256 != null && job.ProducerLockSha256 != currentLock) { job.Kind = OtherGenerator; job.Reason = "made by another version of the world generator"; }
            else if (policyMismatch != null && job.RequestPolicies != null && (mismatch = policyMismatch(job.RequestPolicies)) != null) { job.Kind = OtherPolicies; job.Reason = "made under other world policies (" + mismatch + ")"; }
            else { job.Kind = Resumable; job.Reason = "stopped while " + job.Phase; }
        }

        // The job's latest phase: its last event's (events are appended for every event, the commit's included; the record is written only at
        // review and at the end), else its record's, else its first.
        private static string PhaseOf(string dir)
        {
            string events = Path.Combine(dir, "events.jsonl");
            if (File.Exists(events))
                foreach (var line in File.ReadAllLines(events).Reverse())
                {
                    if (line.Trim().Length == 0) continue;
                    var e = Parse(line);
                    object p; if (e != null && e.TryGetValue("phase", out p) && p is string) return (string)p;
                    break;   // a torn last line: fall back to the record
                }
            string timings = Path.Combine(dir, "timings.json");
            if (File.Exists(timings)) { var r = Parse(File.ReadAllText(timings)); object p; if (r != null && r.TryGetValue("phase", out p) && p is string) return (string)p; }
            return JobPhase.ValidatingRequest.ToString();
        }

        // Null for an empty, torn or non-object document (JavaScriptSerializer returns null for an empty text).
        private static IDictionary<string, object> Parse(string text)
        {
            try { return Json.DeserializeObject(text) as IDictionary<string, object>; }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException) { return null; }
        }

        // The choice from choice.json; when it is missing, empty or torn, from request.json. The request's policy hashes when it has one.
        private static void ReadChoice(UnfinishedJob job)
        {
            string choice = Path.Combine(job.Root, "choice.json"), request = Path.Combine(job.Root, "request.json");
            var r = File.Exists(request) ? Parse(File.ReadAllText(request)) : null;
            if (r != null && r.ContainsKey("policies") && r["policies"] is IDictionary<string, object>)
                job.RequestPolicies = ((IDictionary<string, object>)r["policies"]).ToDictionary(kv => kv.Key, kv => Convert.ToString(kv.Value, CultureInfo.InvariantCulture), StringComparer.Ordinal);
            var c = File.Exists(choice) ? Parse(File.ReadAllText(choice)) : null;
            if (c != null && TryChoice(job, c, "seed")) return;
            if (r != null) TryChoice(job, r, "masterSeed");
        }

        private static bool TryChoice(UnfinishedJob job, IDictionary<string, object> d, string seedKey)
        {
            try
            {
                job.Seed = Convert.ToUInt32(d[seedKey], CultureInfo.InvariantCulture); job.SeedSource = d.ContainsKey("seedSource") ? d["seedSource"] as string : null;
                job.CampaignInstanceId = (string)d["campaignInstanceId"]; job.RecipeVersion = Convert.ToInt32(d.ContainsKey("recipeVersion") ? d["recipeVersion"] : 0, CultureInfo.InvariantCulture);
                job.MaxAttempts = Convert.ToInt32(d.ContainsKey("maxAttempts") ? d["maxAttempts"] : 0, CultureInfo.InvariantCulture);
                job.ProducerLockSha256 = d.ContainsKey("producerLockSha256") ? d["producerLockSha256"] as string : null; job.Profile = d.ContainsKey("profile") ? d["profile"] as string : null;
                job.Overrides.Clear();
                var overrides = d.ContainsKey("overrides") ? d["overrides"] as IDictionary<string, object> : null;
                if (overrides != null) foreach (var kv in overrides) job.Overrides[kv.Key] = Convert.ToInt64(kv.Value, CultureInfo.InvariantCulture);
                job.HasChoice = job.CampaignInstanceId != null;
                return job.HasChoice;
            }
            catch (Exception error) when (error is KeyNotFoundException || error is InvalidCastException || error is FormatException || error is OverflowException || error is NullReferenceException)
            { job.HasChoice = false; return false; }
        }

        private static string Instance(string campaignInstanceId) { Guid g; return Guid.TryParse(campaignInstanceId, out g) ? g.ToString("N") : campaignInstanceId; }

        private static DateTime LastWrite(string dir)
        {
            var times = new List<DateTime> { Directory.GetLastWriteTimeUtc(dir) };
            foreach (var f in Directory.GetFiles(dir)) times.Add(File.GetLastWriteTimeUtc(f));
            return times.Max();
        }

        // ---- Discard ----

        // Removes one unfinished (or finished, unreleased) job's directory: only under the verified root's jobs/, never a live job's, never
        // through a link. Returns null when removed, else why it was kept. Bundles, packages, activations and saves are not touched.
        public static string Discard(string store, string jobRoot, Func<string, bool> isLive, string rootProblem)
        {
            if (rootProblem != null) return "the data root did not verify: " + rootProblem;
            string jobs = Path.GetFullPath(Path.Combine(store, "jobs")).TrimEnd(Path.DirectorySeparatorChar), full;
            try { full = Path.GetFullPath(jobRoot).TrimEnd(Path.DirectorySeparatorChar); } catch (Exception error) when (error is ArgumentException || error is NotSupportedException) { return "an invalid path"; }
            if (!String.Equals(Path.GetDirectoryName(full), jobs, StringComparison.OrdinalIgnoreCase) || !JobId.IsMatch(Path.GetFileName(full))) return "not a job directory of this store";
            if (!Directory.Exists(full)) return null;
            if ((File.GetAttributes(jobs) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return "a link (never followed)";
            if (isLive != null && isLive(full)) return "a job of this process";
            try { StagingCleanup.DeleteTree(full); return null; }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return error.GetType().Name + ": " + error.Message; }
        }

        // ---- Cleanup ----

        // The startup cleanup under a verified root: abandoned staging (StagingCleanup) and finished job directories a stopped process left
        // (their job ended; nothing resumes them), and leftovers with nothing to continue. Unfinished jobs stay for the player to resume or
        // discard. Bundles, packages, activations and saves are never candidates.
        public static Dictionary<string, object> Cleanup(string store, string rootProblem, bool lockHeld, Func<string, bool> isLive, string currentLock)
        {
            var report = new Dictionary<string, object> { ["schema"] = "mnbl.world-cleanup.v1", ["root"] = rootProblem == null ? "verified" : rootProblem };
            if (rootProblem != null) { report["skipped"] = "the data root did not verify: nothing is removed"; return report; }
            report["staging"] = StagingCleanup.Run(store, lockHeld, isLive).ToDictionary();
            var removed = new List<string>(); var kept = new List<Dictionary<string, object>>();
            foreach (var job in Jobs(Path.Combine(store, "jobs"), isLive, currentLock))
            {
                if (job.Finished && lockHeld)
                {
                    string why = Discard(store, job.Root, isLive, null);
                    if (why == null) { removed.Add(job.Id); continue; }
                    var row = job.Report(); row["kept"] = why; kept.Add(row);
                }
                else kept.Add(job.Report());
            }
            report["finishedJobsRemoved"] = removed; report["jobs"] = kept;
            return report;
        }
    }
}
