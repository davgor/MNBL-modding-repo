using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-37: world lifetime primitives (pure managed, tested offline).
    //   * WorldGenerations: one generation token per activated world (a constructed world or a campaign map load); the caller begins it.
    //     Beginning a world retires every earlier one, and disposing a world retires its own, so a callback that finishes late (an encoder
    //     job, an install, a query through a stale reference) carries a token that is no longer current and is refused
    //     (StaleWorldException): it can never attach old resources to, or read through, a later world. Ordering rule: a new generation
    //     may begin only while no campaign map is live (its Destroy ends it), because a live map refuses every query once superseded;
    //     background preparation of a world does not begin a generation. Begin enforces the rule.
    //   * ResourceLedger: every resource a world holds, with who releases it; released in reverse order of acquisition, each once. A
    //     resource another owner releases (an installed engine object, the scene store) is handed to that owner and recorded as such, not
    //     as released.
    //   * SceneNames: the content-addressed scene name of a world encoding.
    internal sealed class StaleWorldException : InvalidOperationException
    {
        public StaleWorldException(string message) : base(message) { }
    }

    internal sealed class GenerationToken
    {
        internal GenerationToken(long id, string label) { Id = id; Label = label; }
        public long Id { get; private set; }
        public string Label { get; private set; }
        public bool Retired { get { return WorldGenerations.Current != Id; } }
        // Throws when a later world began or this one was disposed.
        public void Require(string operation)
        {
            if (Retired) throw new StaleWorldException(operation + " belongs to world generation " + Id + " (" + Label + "), which is no longer current (current " + WorldGenerations.Current + ").");
        }
        // A later world began: this one's resources and queries must never be reached again. (A retired but not superseded world is still
        // tearing down: its own engine teardown may query it until it is gone.)
        public bool Superseded { get { return WorldGenerations.Latest > Id; } }
        public void RequireNotSuperseded(string operation)
        {
            if (Superseded) throw new StaleWorldException(operation + " reached world generation " + Id + " (" + Label + ") after generation " + WorldGenerations.Latest + " began.");
        }
        public override string ToString() { return Id + ":" + Label; }
    }

    internal static class WorldGenerations
    {
        private const int LogLimit = 256;
        private static long next, current, liveCampaignMap;
        private static readonly List<string> log = new List<string>();
        public static long Current { get { return Interlocked.Read(ref current); } }
        public static long Latest { get { return Interlocked.Read(ref next); } }
        public static long LiveCampaignMap { get { return Interlocked.Read(ref liveCampaignMap); } }
        public static IList<string> Log { get { lock (log) return log.ToList().AsReadOnly(); } }
        private static void Write(string entry) { lock (log) { log.Add(entry); if (log.Count > LogLimit) log.RemoveAt(0); } }

        public static GenerationToken Begin(string label)
        {
            long live = LiveCampaignMap;
            if (live != 0) throw new InvalidOperationException("Campaign map generation " + live + " is live: a new world generation (" + label + ") begins only after it is destroyed.");
            long id = Interlocked.Increment(ref next);
            long previous = Interlocked.Exchange(ref current, id);
            Write("begin " + id + " " + label + (previous != 0 ? " (retires " + previous + ")" : ""));
            return new GenerationToken(id, label);
        }
        // Retires the token if it is still current; a later world's token is left alone.
        public static bool Retire(GenerationToken token)
        {
            if (token == null) return false;
            bool retired = Interlocked.CompareExchange(ref current, 0, token.Id) == token.Id;
            Write((retired ? "retire " : "already retired ") + token.Id + " " + token.Label);
            return retired;
        }
        // A campaign map is live from its Load to its Destroy.
        public static void CampaignMapLoaded(GenerationToken token) { Interlocked.Exchange(ref liveCampaignMap, token.Id); Write("campaign map live " + token.Id); }
        public static void CampaignMapDestroyed(GenerationToken token)
        {
            if (token != null && Interlocked.CompareExchange(ref liveCampaignMap, 0, token.Id) == token.Id) Write("campaign map ended " + token.Id);
        }
    }

    internal static class SceneNames
    {
        // One scene name per world encoding: the world's content id and the identity of what encodes it (encoder binary, template files,
        // extent) and of its campaign navmesh (MMR-38: the navmesh.bin hash). Re-encoding the same world with the same encoder and navigation
        // gives the same bytes, so an installed scene of that name is reused, never rewritten; a different encoding gets a different name.
        public static string ForWorld(string worldContentId, string encoderSha256, string templateSha256, double extent, string navigationSha256)
        {
            if (worldContentId == null || worldContentId.Length < 16) throw new ArgumentException("A world content id is required.");
            if (String.IsNullOrEmpty(navigationSha256)) throw new ArgumentException("The navmesh identity is required.");
            string identity = encoderSha256 + "\n" + templateSha256 + "\n" + extent.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "\nnavmesh " + navigationSha256;
            using (var sha = SHA256.Create())
            {
                string digest = BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false).GetBytes(identity))).Replace("-", "").ToLowerInvariant();
                return "mnbl_world_" + worldContentId.Substring(0, 16).ToLowerInvariant() + "_" + digest.Substring(0, 12);
            }
        }
        // Exactly the content-addressed form: other owned scenes (fixture slots such as mnbl_world_a, mnbl_generated_*) never match, so the
        // scene store's sweep never touches them.
        private static readonly System.Text.RegularExpressions.Regex Form = new System.Text.RegularExpressions.Regex("^mnbl_world_[0-9a-f]{16}_[0-9a-f]{12}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        public static bool IsWorldScene(string name) { return name != null && Form.IsMatch(name); }
    }

    internal sealed class ResourceLedger
    {
        public sealed class Entry { public int Order; public string Resource, Owner, ReleasedBy, HandedTo; public bool Released; }
        private readonly List<Entry> entries = new List<Entry>();
        private readonly Dictionary<string, Action> releases = new Dictionary<string, Action>(StringComparer.Ordinal);
        public IList<Entry> Entries { get { return entries.AsReadOnly(); } }
        // Held here and neither released nor handed to its owner.
        public int Outstanding { get { return entries.Count(e => !e.Released && e.HandedTo == null); } }

        // release: what releases it here, or null when another owner releases it (recorded in owner).
        public void Acquire(string resource, string owner, Action release)
        {
            if (entries.Any(e => e.Resource == resource && !e.Released && e.HandedTo == null)) throw new InvalidOperationException("Resource already held: " + resource);
            entries.Add(new Entry { Order = entries.Count, Resource = resource, Owner = owner });
            releases[resource] = release;
        }

        // Releases every held resource, latest first; a failing release is recorded and the rest still run. A resource another owner
        // releases is handed to that owner. Returns the failures.
        public List<string> ReleaseAll(string releasedBy)
        {
            var failures = new List<string>();
            foreach (var entry in entries.Where(e => !e.Released && e.HandedTo == null).OrderByDescending(e => e.Order).ToList())
            {
                Action release = null;
                releases.TryGetValue(entry.Resource, out release);
                if (release == null) { entry.HandedTo = entry.Owner; releases.Remove(entry.Resource); continue; }
                try { release(); }
                catch (Exception error) { failures.Add(entry.Resource + ": " + error.GetType().Name + ": " + error.Message); }
                entry.Released = true; entry.ReleasedBy = releasedBy;
                releases.Remove(entry.Resource);
            }
            return failures;
        }

        public List<Dictionary<string, object>> Record()
        {
            return entries.Select(e => new Dictionary<string, object> { ["order"] = e.Order, ["resource"] = e.Resource, ["owner"] = e.Owner, ["released"] = e.Released, ["releasedBy"] = e.ReleasedBy, ["handedTo"] = e.HandedTo }).ToList();
        }
    }
}
