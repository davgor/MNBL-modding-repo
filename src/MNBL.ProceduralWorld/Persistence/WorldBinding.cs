using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace MNBL.ProceduralWorld.Persistence
{
    // MMR-62: the world binding a save carries, read from its header without deserializing anything (the R05 hook: the mnbl.world.* keys
    // WorldIdentityBehavior adds through CampaignEvents.CollectMetadataEntriesEvent, read with MBSaveLoad's save list at the main menu).
    // A binding names its immutable world package by world id and manifest hash (the package directory is <id>-<manifest12> under the
    // store's worlds folder; an absolute path in an older header is not used to resolve it), its campaign instance, and the data bundle the
    // world was built from ("none" for a fixture world). Pure managed: no game types, so the store and the index are tested offline.
    internal sealed class WorldBinding
    {
        public const string Prefix = "mnbl.world.";
        // MMR-50: the world identity schema a save's header records (WorldIdentity.Schema), here so tools without the game can compare it.
        public const string IdentitySchema = "mnbl.world-identity.v1";
        public const string NoContent = "none";
        private static readonly Regex Hex64 = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        private static readonly Regex Hex32 = new Regex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);
        private static readonly Regex WorldIdPattern = new Regex("^[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant);

        public string WorldId, ManifestSha256, Instance, ContentId, Scene;

        // The package directory name (under <store>/worlds).
        public string PackageName { get { return WorldId + "-" + ManifestSha256.Substring(0, 12); } }
        // A header written before MMR-62 carries no instance; it still names its world.
        public bool Legacy { get { return Instance == null; } }

        public static string NewInstance() { return Guid.NewGuid().ToString("N"); }

        // The binding in a save header (keys with the mnbl.world. prefix). Null when the header has no world binding (a save of the official
        // world or of another mod). Throws InvalidDataException when the header has a binding that is malformed: such a save names a
        // generated world, but which one cannot be told.
        public static WorldBinding FromHeader(IDictionary<string, string> metadata)
        {
            if (metadata == null) throw new ArgumentNullException("metadata");
            string id;
            if (!metadata.TryGetValue(Prefix + "id", out id)) return null;
            var binding = new WorldBinding { WorldId = id, ManifestSha256 = Value(metadata, "manifestSha256"), Instance = Value(metadata, "instance"),
                ContentId = Value(metadata, "contentId") ?? NoContent, Scene = Value(metadata, "scene") };
            binding.Validate();
            return binding;
        }

        // The header entries this binding contributes (without the prefix), merged into the world identity's entries.
        public Dictionary<string, string> Entries()
        {
            Validate();
            if (Legacy) throw new InvalidOperationException("A new binding needs a campaign instance.");
            return new Dictionary<string, string>(StringComparer.Ordinal) { ["instance"] = Instance, ["contentId"] = ContentId };
        }

        public void Validate()
        {
            if (WorldId == null || !WorldIdPattern.IsMatch(WorldId)) throw new InvalidDataException("The world binding has no valid world id.");
            if (ManifestSha256 == null || !Hex64.IsMatch(ManifestSha256)) throw new InvalidDataException("The world binding of " + WorldId + " has no valid manifest hash.");
            if (Instance != null && !Hex32.IsMatch(Instance)) throw new InvalidDataException("The world binding of " + WorldId + " has a malformed campaign instance.");
            if (ContentId == null || (ContentId != NoContent && !Hex64.IsMatch(ContentId))) throw new InvalidDataException("The world binding of " + WorldId + " has a malformed content id.");
        }

        public bool SameWorld(WorldBinding other) { return other != null && other.WorldId == WorldId && other.ManifestSha256 == ManifestSha256 && other.ContentId == ContentId; }

        private static string Value(IDictionary<string, string> metadata, string key) { string value; return metadata.TryGetValue(Prefix + key, out value) ? value : null; }
    }
}
