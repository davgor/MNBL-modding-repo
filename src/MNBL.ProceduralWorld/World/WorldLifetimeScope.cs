using System;
using System.Collections.Generic;
using System.Linq;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-49: every static resource a generated campaign can leave in this process, by name, with what counts it (WorldLifetimeTrackers
    // registers the installed ones: patches, map-scene registries, battle terrain contexts, the periodic frame and its patches, the live
    // campaign map and the current world generation, the naval scene wrapper, the save binding and save block, the startup behaviour, the
    // colour grade, the module's campaign-event listeners, a load in progress). At the main menu with no campaign every count is the baseline,
    // zero: a campaign's teardown (GeneratedGameManager.OnGameEnd) and its map's destroy (GeneratedMapScene.Destroy) release what it held, and
    // Held names anything they did not, so a second world never starts beside the first's leftovers. Late callbacks are refused separately by
    // the world generation tokens (WorldGenerations): a callback carrying a retired token never reaches the next world. Pure managed.
    internal static class WorldLifetimeScope
    {
        private static readonly List<KeyValuePair<string, Func<int>>> tracked = new List<KeyValuePair<string, Func<int>>>();

        // Registers a resource once by name; held returns how many of it this process holds (0 at the baseline).
        public static void Track(string name, Func<int> held)
        {
            if (String.IsNullOrEmpty(name) || held == null) throw new ArgumentException("A tracked resource needs a name and a count.");
            lock (tracked)
            {
                tracked.RemoveAll(t => t.Key == name);
                tracked.Add(new KeyValuePair<string, Func<int>>(name, held));
            }
        }

        public static IList<string> Names { get { lock (tracked) return tracked.Select(t => t.Key).OrderBy(n => n, StringComparer.Ordinal).ToList(); } }

        // Every tracked resource's count now; a count that throws is recorded as -1 (and is held).
        public static SortedDictionary<string, int> Snapshot()
        {
            List<KeyValuePair<string, Func<int>>> copy;
            lock (tracked) copy = tracked.ToList();
            var result = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in copy)
            {
                int count;
                try { count = t.Value(); } catch (Exception) { count = -1; }
                result[t.Key] = count;
            }
            return result;
        }

        // What is held now: every resource whose count is not the baseline.
        public static SortedDictionary<string, int> Held()
        {
            var held = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var kv in Snapshot()) if (kv.Value != 0) held[kv.Key] = kv.Value;
            return held;
        }

        public static bool AtBaseline { get { return Held().Count == 0; } }
    }
}
