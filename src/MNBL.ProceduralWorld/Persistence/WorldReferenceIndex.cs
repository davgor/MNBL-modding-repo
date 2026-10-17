using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MNBL.ProceduralWorld.Persistence
{
    internal sealed class WorldReference
    {
        public const string Save = "save", PendingInitialSave = "pending initial save", PendingSave = "pending save", Active = "active campaign";
        public string Kind, Name, Instance, WorldId, ManifestSha256, ContentId, Package;

        public Dictionary<string, object> Report()
        {
            return new Dictionary<string, object> { ["kind"] = Kind, ["name"] = Name, ["instance"] = Instance, ["worldId"] = WorldId, ["package"] = Package, ["contentId"] = ContentId };
        }
    }

    internal sealed class Retention
    {
        public bool Retain;
        public string Reason;
        public List<WorldReference> References;
    }

    // MMR-62: which saves, pending initial saves and active campaigns name which world package (and data bundle), from the save headers
    // (the game's save list, read without deserializing) and the initial-save journals. It fails closed: a save whose header cannot be read
    // might name any world, so does a malformed binding, an unreadable journal or a save list that cannot be read; each is a gap, and while
    // there is a gap no world is reported deletable.
    // MMR-48: every save kind is now bound and journaled (the initial save by EarlyWorldBindingStore; Save As, quick save, autosave and Ironman
    // saves by SaveJournal through WorldSaveBinding; a renamed or copied save carries its header), so SaveKindsBound is true: a complete
    // index decides retention by its references alone. A later save in flight (its journal's intent) is a reference, an unreadable later-save
    // journal a gap. Counts gives each package's and data bundle's references and campaign instances: identical immutable content is stored
    // once (bundles/<worldContentId>, worlds/<id>-<manifest12>) and referenced by every save and campaign that uses it. Nothing in this
    // increment deletes a world; Decide is what a deletion would have to ask. With every save kind bound the index needs both journals: a
    // caller that passes no initial-save store or no later-save journal gets a gap for each, so its decisions fail closed (review finding).
    internal sealed class WorldReferenceIndex
    {
        public const bool SaveKindsBound = true;

        public readonly List<WorldReference> References = new List<WorldReference>();
        public readonly List<string> Gaps = new List<string>();
        public int SavesListed { get; private set; }
        public int SavesBound { get; private set; }
        public bool DiscoveryComplete { get { return Gaps.Count == 0; } }
        private bool saveKindsBound;

        public static WorldReferenceIndex Build(ISaveCatalog saves, EarlyWorldBindingStore store, IEnumerable<WorldBinding> active, bool saveKindsBound = SaveKindsBound, SaveJournal later = null)
        {
            var index = new WorldReferenceIndex { saveKindsBound = saveKindsBound };
            IList<SaveHeader> listed = null;
            try { listed = saves.List(); }
            catch (Exception error) { index.Gaps.Add("the save list could not be read: " + error.GetType().Name + ": " + error.Message); }
            foreach (var header in listed ?? new List<SaveHeader>())
            {
                index.SavesListed++;
                if (!header.Readable) { index.Gaps.Add("save " + header.Name + ": its header cannot be read, so the world it names is unknown"); continue; }
                WorldBinding binding;
                try { binding = WorldBinding.FromHeader(header.Metadata); }
                catch (InvalidDataException error) { index.Gaps.Add("save " + header.Name + ": malformed world binding (" + error.Message + ")"); continue; }
                if (binding == null) continue;
                index.SavesBound++;
                index.Add(WorldReference.Save, header.Name, binding);
            }
            if (store != null)
            {
                List<string> unreadable;
                foreach (var journal in store.Journals(out unreadable))
                    if (journal.State == EarlyWorldBindingStore.Intent) index.Add(WorldReference.PendingInitialSave, journal.SaveName, journal.Binding);
                foreach (var entry in unreadable) index.Gaps.Add("initial save journal " + entry);
            }
            if (later != null)
            {
                List<string> unreadable;
                foreach (var journal in later.Journals(out unreadable))
                    if (journal.State == EarlyWorldBindingStore.Intent) index.Add(WorldReference.PendingSave, journal.SaveName, journal.Binding);
                foreach (var entry in unreadable) index.Gaps.Add("save journal " + entry);
            }
            if (saveKindsBound && store == null) index.Gaps.Add("the initial-save journals were not read, so a pending initial save may be missed");
            if (saveKindsBound && later == null) index.Gaps.Add("the later-save journals were not read, so a save in flight may be missed");
            foreach (var binding in active ?? Enumerable.Empty<WorldBinding>()) index.Add(WorldReference.Active, binding.Instance, binding);
            return index;
        }

        public List<WorldReference> For(string package, string contentId = null)
        {
            return References.Where(r => r.Package == package || (contentId != null && contentId != WorldBinding.NoContent && r.ContentId == contentId)).ToList();
        }

        // Whether a world package (and the bundle it was built from) must be kept.
        public Retention Decide(string package, string contentId = null)
        {
            var references = For(package, contentId);
            if (references.Count > 0) return new Retention { Retain = true, References = references, Reason = "referenced by " + references.Count + ": " + String.Join(", ", references.Select(r => r.Kind + " " + r.Name)) };
            if (!DiscoveryComplete) return new Retention { Retain = true, References = references, Reason = "reference discovery is incomplete (" + Gaps.Count + " gaps: " + Gaps[0] + (Gaps.Count > 1 ? "; ..." : "") + ")" };
            if (!saveKindsBound) return new Retention { Retain = true, References = references, Reason = "not every save kind is journaled; a complete world is never deleted then" };
            return new Retention { Retain = false, References = references, Reason = "no save, pending save or active campaign names it, and discovery is complete" };
        }

        // MMR-48: the reference counts of each package and each data bundle (content id): references by kind and the distinct campaign
        // instances (separate mutable campaigns of one immutable world).
        public Dictionary<string, object> Counts()
        {
            Func<IEnumerable<WorldReference>, Dictionary<string, object>> count = refs =>
            {
                var list = refs.ToList();
                return new Dictionary<string, object> { ["references"] = list.Count, ["saves"] = list.Count(r => r.Kind == WorldReference.Save),
                    ["pending"] = list.Count(r => r.Kind == WorldReference.PendingInitialSave || r.Kind == WorldReference.PendingSave),
                    ["active"] = list.Count(r => r.Kind == WorldReference.Active), ["instances"] = list.Where(r => r.Instance != null).Select(r => r.Instance).Distinct().Count() };
            };
            return new Dictionary<string, object> {
                ["packages"] = References.GroupBy(r => r.Package).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (object)count(g)),
                ["bundles"] = References.Where(r => r.ContentId != null && r.ContentId != WorldBinding.NoContent).GroupBy(r => r.ContentId).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => (object)count(g)) };
        }

        public Dictionary<string, object> Report()
        {
            return new Dictionary<string, object> { ["savesListed"] = SavesListed, ["savesBound"] = SavesBound, ["discoveryComplete"] = DiscoveryComplete, ["saveKindsBound"] = saveKindsBound,
                ["gaps"] = Gaps.ToList(), ["references"] = References.Select(r => r.Report()).ToList(), ["counts"] = Counts() };
        }

        private void Add(string kind, string name, WorldBinding binding)
        {
            References.Add(new WorldReference { Kind = kind, Name = name, Instance = binding.Instance, WorldId = binding.WorldId, ManifestSha256 = binding.ManifestSha256,
                ContentId = binding.ContentId, Package = binding.PackageName });
        }
    }
}
