using System;
using System.Collections.Generic;
using System.Linq;

namespace MNBL.ProceduralWorld.Persistence
{
    // MMR-48: what every save of a generated campaign records about the build that wrote it, beside its world binding (the mnbl.world.* keys
    // of WorldIdentity: world id, package and manifest hash, scene and file hashes, campaign instance, content id). These mnbl.save.* keys
    // are written by the same metadata hook (WorldIdentityBehavior, CampaignEvents.CollectMetadataEntriesEvent, every save mode) and read
    // from the header at the main menu before anything deserializes, so a loader can tell which contract, module build, game, War Sails and
    // runtime backend made a save without opening it (MMR-49 decides what a difference means; this increment records and reports them):
    //   contract              this binding contract (worldgen/contracts/save-binding.schema.json)
    //   module                the module's version; moduleAssemblySha256 the module assembly's hash (the world package records it too)
    //   game, naval           the game version with its build number, and War Sails' version or "absent"
    //   backend               the runtime world backend and the producer lock it runs (worldbridge and encoder hashes, MMR-21)
    //   navigationCache       the navigation cache key's schema and adapter (derived caches are versioned apart from the world, MMR-41)
    //   saveIds               the module's fixed save definition id base and version (SaveIds; worldgen/contracts/save-type-ledger.json),
    //                         a namespace never mixed with per-world object ids
    //   worldIdentity         the schema of the mnbl.world.* keys
    // Pure managed.
    internal static class SaveBindingContract
    {
        public const string Contract = "mnbl.save-binding.v1";
        public const string Prefix = "mnbl.save.";
        public static readonly string[] Keys = { "contract", "module", "moduleAssemblySha256", "game", "naval", "backend", "navigationCache", "saveIds", "worldIdentity" };

        public static Dictionary<string, string> Compatibility(string module, string moduleAssemblySha256, string game, string naval, string backend, string navigationCache, string saveIds, string worldIdentity)
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal) { ["contract"] = Contract, ["module"] = module, ["moduleAssemblySha256"] = moduleAssemblySha256, ["game"] = game,
                ["naval"] = naval, ["backend"] = backend, ["navigationCache"] = navigationCache, ["saveIds"] = saveIds, ["worldIdentity"] = worldIdentity };
            foreach (var entry in entries)
                if (String.IsNullOrEmpty(entry.Value) || entry.Value.Length > 256 || entry.Value.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new ArgumentException("No usable value for " + Prefix + entry.Key + ".");
            return entries;
        }

        // The mnbl.save.* keys of a save header, without the prefix (empty for a save made before MMR-48).
        public static Dictionary<string, string> CompatibilityFrom(IDictionary<string, string> metadata)
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            if (metadata == null) return entries;
            foreach (var entry in metadata)
                if (entry.Key.StartsWith(Prefix, StringComparison.Ordinal)) entries[entry.Key.Substring(Prefix.Length)] = entry.Value;
            return entries;
        }

        // How a save's recorded build differs from the running one: one line per key that differs or is missing (none for the same build).
        public static List<string> Differences(IDictionary<string, string> saved, IDictionary<string, string> running)
        {
            var lines = new List<string>();
            foreach (var key in Keys)
            {
                string a = null, b = null;
                if (saved != null) saved.TryGetValue(key, out a);
                if (running != null) running.TryGetValue(key, out b);
                if (a != b) lines.Add(key + ": saved " + (a ?? "(none)") + ", running " + (b ?? "(none)"));
            }
            return lines;
        }
    }
}
