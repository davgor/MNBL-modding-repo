using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // Where a load stands in the object manager's lifecycle (R05 timing contract, run 8256).
    internal enum StoreStage
    {
        NewGame,                // a fresh object manager: official templates loaded, no world objects
        SavedObjectsRegistered, // a save deserialized and MBObjectBase.BeforeLoad re-registered its objects by StringId
        NonReadyUnregistered    // UnregisterNonReadyObjects ran: anything not ready is gone
    }

    // What a closure check may expect.
    internal enum ClosureMode
    {
        Registration, // a new game, from the last document to the default-list publication: everything registered and ready
        Replay,       // a load before OnLoadFinished: the permanent types ready; saved clans, heroes, kingdoms and parties
                      // only listed (run 33092: they are ready only after the load finishes)
        Settled       // after initialization or a finished load: everything ready, wherever it lives
    }

    // The object manager as the registry sees it (MBObjectStore in game, a model of it in tests).
    internal interface IObjectStore
    {
        StoreStage Stage { get; }
        // False for a type the object manager does not hold now: after initialization the campaign moves Hero, Faction,
        // Kingdom and MobileParty into CampaignObjectManager (run 416).
        bool HasType(string type);
        bool Contains(string type, string id);
        bool IsReady(string type, string id);
        IEnumerable<KeyValuePair<string, bool>> Objects(string type);   // (StringId, ready) of every object of a held element type
        void LoadDocument(string phase, XmlDocument document);
        bool Unregister(string type, string id);
        // Whether the village settlement is in its bound town's trade-bound villages; null when that cannot be read.
        bool? TradeBound(string villageSettlementId, string boundSettlementId);
    }

    // CampaignObjectManager's lists by name (Settlements, Clans, Kingdoms, AliveHeroes, DeadOrDisabledHeroes, MobileParties):
    // (StringId, ready) of each entry, or null for a list not published yet (Settlements stays null until
    // InitializeOnNewGame; the others are empty until then).
    internal interface ICampaignLists
    {
        IEnumerable<KeyValuePair<string, IList<KeyValuePair<string, bool>>>> Lists();
    }

    internal sealed class RegistrationException : InvalidOperationException
    {
        public RegistrationException(string message, IList<string> problems) : base(message + ": " + String.Join("; ", problems.Take(20)) + (problems.Count > 20 ? "; ..." : "")) { Problems = problems; }
        public IList<string> Problems { get; private set; }
    }

    // MMR-30: the one place generated campaign objects are registered, replayed and removed (docs/campaign-registry.md).
    // Register checks the plan (identities, duplicates, collisions, order, the proven two-pass references, unresolved and
    // official-world dependencies) before the first document loads, loads the documents phase by phase, then asserts
    // closure before returning, which is before CampaignObjectManager publishes its default lists. Census is a deterministic
    // report over both object managers that tells this world's objects, objects the campaign derived from them, other
    // worlds' objects (also when only embedded in a derived id), aliases, official objects (present before registration)
    // and other runtime-created objects apart (consumed by MMR-36). RemoveWorldObjects removes this world's objects only,
    // never a shared template or an alias that existed before. No static state: one registry per campaign.
    internal sealed class CampaignObjectRegistry
    {
        // Element types the census covers.
        public static readonly IList<string> CensusTypes = Array.AsReadOnly(new[] {
            "Culture", "NPCCharacter", "Hero", "Kingdom", "Faction", "Settlement", "Town", "Village", "Hideout", "RetirementSettlementComponent", "MobileParty", "WorkshopType", "LocationComplexTemplate" });
        // The object type each campaign list holds.
        private static readonly Dictionary<string, string> ListTypes = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["Settlements"] = "Settlement", ["Clans"] = "Faction", ["Kingdoms"] = "Kingdom", ["AliveHeroes"] = "Hero",
            ["DeadOrDisabledHeroes"] = "Hero", ["MobileParties"] = "MobileParty" };

        private readonly Func<string, string, bool> shared;
        private readonly List<string> trace = new List<string>();
        private readonly HashSet<string> before = new HashSet<string>(StringComparer.Ordinal);   // census objects present before registration
        private HashSet<string> sharedKeys;                                                         // installed records the plan re-emits
        public RegistrationPlan Plan { get; private set; }
        public bool Registered { get; private set; }
        public string CampaignInstanceId { get; private set; }
        public IList<string> Trace { get { return trace.AsReadOnly(); } }

        // shared(type, id): approved shared templates and engine defaults that generated objects may reference.
        // campaignInstanceId: the mutable campaign identity (MMR-24), recorded in the census and kept out of every StringId.
        public CampaignObjectRegistry(RegistrationPlan plan, Func<string, string, bool> shared, string campaignInstanceId = null)
        {
            if (plan == null || shared == null) throw new ArgumentNullException("plan/shared");
            Plan = plan; this.shared = shared; CampaignInstanceId = campaignInstanceId;
        }

        // New game: RegisterSubModuleObjects(false). documents: phase -> document, exactly the plan's phases in order.
        public void Register(IObjectStore store, IList<KeyValuePair<string, XmlDocument>> documents)
        {
            if (Registered) throw new InvalidOperationException("This registry already registered its world; a second campaign needs its own registry.");
            if (Plan.SavedGame) throw new InvalidOperationException("A saved-game plan is replayed, not registered.");
            if (store.Stage != StoreStage.NewGame) throw new RegistrationException("Refused before loading", new[] { "a new world registers into a fresh object manager, not one at stage " + store.Stage });
            Load(store, documents, "registered", null, ClosureMode.Registration);
        }

        // Saved game: RegisterSubModuleObjects(true), after BeforeLoad re-registered the saved objects and before
        // UnregisterNonReadyObjects; re-merges the permanent-type documents so saved objects become ready. The saved clans,
        // heroes and kingdoms the replayed records reference are in the deserialized CampaignObjectManager lists, not in the
        // object manager (run 3428), so lists is required. The timing is checked on the engine state, not only on the
        // caller's stage: BeforeLoad leaves the saved permanent objects registered and not ready; before it they are absent,
        // after UnregisterNonReadyObjects they are gone.
        public void Replay(IObjectStore store, IList<KeyValuePair<string, XmlDocument>> documents, ICampaignLists lists)
        {
            if (Registered) throw new InvalidOperationException("This registry already replayed its world.");
            if (!Plan.SavedGame) throw new InvalidOperationException("A new-game plan is registered, not replayed.");
            if (lists == null) throw new ArgumentNullException("lists", "A replay needs the saved campaign's lists.");
            if (store.Stage == StoreStage.NewGame)
                throw new RegistrationException("Refused before loading", new[] { "replay before MBObjectBase.BeforeLoad re-registers the saved objects would orphan them" });
            if (store.Stage == StoreStage.NonReadyUnregistered)
                throw new RegistrationException("Refused before loading", new[] { "replay after UnregisterNonReadyObjects: the saved objects it would make ready are already gone" });
            if (!Plan.GeneratedObjects.Any(o => !RegistrationPlan.IsTemporaryType(o.Type) && store.Contains(o.Type, o.Id) && !store.IsReady(o.Type, o.Id)))
                throw new RegistrationException("Refused before loading", new[] { "no saved object of this world is registered and waiting for its XML: the replay is not between BeforeLoad and UnregisterNonReadyObjects" });
            foreach (var o in Plan.GeneratedObjects.Where(o => !store.Contains(o.Type, o.Id))) trace.Add("not_in_save:" + o.Key);
            Load(store, documents, "replayed", lists, ClosureMode.Replay);
        }

        private void Load(IObjectStore store, IList<KeyValuePair<string, XmlDocument>> documents, string outcome, ICampaignLists lists, ClosureMode mode)
        {
            before.Clear();
            foreach (var type in CensusTypes.Where(store.HasType))
                foreach (var entry in store.Objects(type)) before.Add(type + "." + entry.Key);
            var listed = Published(lists);
            var problems = Plan.Problems((type, id) => store.Contains(type, id) || InList(listed, type, id), shared);
            if (!documents.Select(d => d.Key).SequenceEqual(Plan.Phases)) problems.Add("documents " + String.Join(", ", documents.Select(d => d.Key)) + " are not the planned phases");
            if (CampaignInstanceId != null)
            {
                string bare = CampaignInstanceId.Replace("-", "");
                foreach (var o in Plan.GeneratedObjects.Where(o => o.Id.Contains(CampaignInstanceId) || o.Id.Contains(bare)))
                    problems.Add("identity " + o.Key + " embeds the campaign instance id: world ids are immutable and shared by every campaign of the world");
            }
            if (problems.Count > 0) throw new RegistrationException("Refused before loading", problems);
            try
            {
                foreach (var document in documents)
                {
                    trace.Add("load:" + document.Key);
                    store.LoadDocument(document.Key, document.Value);
                }
                var closure = ClosureProblems(store, lists, mode);
                if (closure.Count > 0) throw new RegistrationException("Closure failed before the default lists publish", closure);
            }
            catch (Exception) when (!Plan.SavedGame)
            {
                // MMR-36: a new world that fails while loading or at closure is rolled back before the failure propagates (a saved world's
                // objects belong to the save and are left to the load's own failure handling).
                RollBack(store, lists);
                throw;
            }
            Registered = true;
            trace.Add(outcome + ":" + Plan.GeneratedObjects.Count());
        }

        // Every planned object and every reference of a generated object present and ready; nothing presumed and not ready of
        // this world; nothing of another world (by prefix or embedded in a derived id); a published Settlements list and the
        // object manager agreeing on this world's settlements; every village trade-bound to its town. An object the object
        // manager does not hold is found in its campaign list. In Replay mode the saved clans, heroes, kingdoms and parties
        // need only be listed. Sorted; empty when closed.
        public List<string> ClosureProblems(IObjectStore store, ICampaignLists lists, ClosureMode mode)
        {
            var problems = new List<string>();
            var listed = Published(lists);
            // An object both registered and listed is one instance: the object manager's readiness decides. A listed object the
            // object manager does not hold is decided by its list entry. During a replay a saved clan, hero, kingdom or party
            // need only be listed (a replayed record's reference may leave a presumed copy that UnregisterNonReadyObjects drops).
            Func<string, string, string> state = (type, id) => {
                Dictionary<string, bool> entries; bool listedReady = false;
                bool inList = listed.TryGetValue(type, out entries) && entries.TryGetValue(id, out listedReady);
                if (inList && mode == ClosureMode.Replay && RegistrationPlan.IsTemporaryType(type)) return null;
                if (store.Contains(type, id)) return store.IsReady(type, id) ? null : "not ready";
                if (inList) return listedReady ? null : "not ready (campaign list)";
                return store.HasType(type) || !listed.ContainsKey(type) ? "not registered" : "not in the campaign's " + type + " list";
            };
            foreach (var o in Plan.Objects)
            {
                string problem = state(o.Type, o.Id);
                if (problem != null) problems.Add(problem + ": " + o.Key);
                if (!o.Generated) continue;
                foreach (var r in o.References)
                    if (state(r.Type, r.Id) != null) problems.Add("reference not ready: " + o.Key + "." + r.Attribute + " -> " + r.Key);
                // Only right after a new game deserialized its settlements: later the campaign re-derives trade bounds (after a load
                // the towns did not list their villages, run 2244), so the link is a registration invariant, not a settled one.
                if (o.Type == "Village" && o.Parent != null && mode == ClosureMode.Registration)
                    foreach (var r in o.References.Where(r => r.Attribute == "bound"))
                        if (store.TradeBound(o.Parent.Id, r.Id) == false) problems.Add("village " + o.Parent.Key + " is not trade-bound to " + r.Key);
            }
            foreach (var type in CensusTypes.Where(store.HasType))
                foreach (var entry in store.Objects(type))
                {
                    string origin = CampaignIdentity.WorldOrigin(entry.Key);
                    if (origin == null) continue;
                    if (origin != Plan.WorldPrefix) problems.Add("object of another world: " + type + "." + entry.Key);
                    // During a load the object manager holds some saved heroes and kingdoms not ready until the load finishes
                    // (run 33092); their state is the save's. A replay answers for the permanent types it re-merges.
                    else if (!entry.Value && !(mode == ClosureMode.Replay && RegistrationPlan.IsTemporaryType(type))) problems.Add("presumed or not deserialized: " + type + "." + entry.Key);
                }
            foreach (var type in listed)
                foreach (var entry in type.Value)
                {
                    string origin = CampaignIdentity.WorldOrigin(entry.Key);
                    if (origin != null && origin != Plan.WorldPrefix) problems.Add("object of another world: " + type.Key + "." + entry.Key);
                    else if (origin == Plan.WorldPrefix && !entry.Value && mode == ClosureMode.Settled) problems.Add("not ready (campaign list): " + type.Key + "." + entry.Key);
                }
            // Settlements stay in the object manager after initialization (run 416); a published list must name exactly this
            // world's registered settlements.
            Dictionary<string, bool> settlements;
            if (listed.TryGetValue("Settlement", out settlements) && store.HasType("Settlement"))
            {
                var registered = new HashSet<string>(store.Objects("Settlement").Where(e => CampaignIdentity.WorldPrefix(e.Key) == Plan.WorldPrefix).Select(e => e.Key), StringComparer.Ordinal);
                foreach (var id in registered.Where(i => !settlements.ContainsKey(i))) problems.Add("registered but not in the campaign's Settlements list: Settlement." + id);
                foreach (var id in settlements.Keys.Where(i => CampaignIdentity.WorldPrefix(i) == Plan.WorldPrefix && !registered.Contains(i))) problems.Add("in the campaign's Settlements list but not registered: Settlement." + id);
            }
            return problems.Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        }

        // Where an object comes from: this world, derived from this world (an id the campaign built from one of ours), another
        // world, an alias, official (present before this world registered, or an installed record the plan re-emits), or created at runtime.
        private string Origin(string type, string id)
        {
            string origin = CampaignIdentity.WorldOrigin(id);
            if (origin != null && origin != Plan.WorldPrefix) return "otherWorlds";
            if (CampaignIdentity.WorldPrefix(id) == Plan.WorldPrefix) return "thisWorld";
            if (origin == Plan.WorldPrefix) return "thisWorldDerived";
            if (CampaignIdentity.IsAlias(id)) return "aliases";
            // Official: present before this world registered, or an installed record the registration itself loaded (in a
            // generated game SandBox's own XML is replaced, so the stock workshop types and location templates first arrive here).
            if (sharedKeys == null) sharedKeys = new HashSet<string>(Plan.Objects.Where(o => !o.Generated).Select(o => o.Key), StringComparer.Ordinal);
            return before.Contains(type + "." + id) || sharedKeys.Contains(type + "." + id) ? "official" : "runtimeCreated";
        }

        private static readonly string[] Origins = { "thisWorld", "thisWorldDerived", "otherWorlds", "aliases", "official", "runtimeCreated" };

        // A deterministic report: the same objects give the same census whatever their registration order. Per element type
        // (held or not): totals, ready and not ready, counts by origin, and hashes of this world's and the official ids. Per
        // campaign list: publication, counts by origin, entries not ready, and for the Settlements list the disagreement with
        // the object manager. closure: ClosureProblems in the given mode.
        public Dictionary<string, object> Census(IObjectStore store, ICampaignLists lists, string phase, ClosureMode mode)
        {
            var types = new SortedDictionary<string, object>(StringComparer.Ordinal);
            var foreign = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var type in CensusTypes)
            {
                if (!store.HasType(type)) { types[type] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["held"] = false }; continue; }
                var entries = store.Objects(type).OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
                var row = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["held"] = true, ["total"] = entries.Count, ["ready"] = entries.Count(e => e.Value), ["notReady"] = entries.Count(e => !e.Value) };
                foreach (var origin in Origins) row[origin] = entries.Count(e => Origin(type, e.Key) == origin);
                row["thisWorldIdsSha256"] = Sha256(String.Join("\n", entries.Where(e => Origin(type, e.Key) == "thisWorld").Select(e => e.Key)));
                row["officialIdsSha256"] = Sha256(String.Join("\n", entries.Where(e => Origin(type, e.Key) == "official").Select(e => e.Key)));
                foreach (var e in entries.Where(e => Origin(type, e.Key) == "otherWorlds")) foreign.Add(CampaignIdentity.WorldOrigin(e.Key).TrimEnd('_'));
                types[type] = row;
            }
            var listReport = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var list in (lists == null ? Enumerable.Empty<KeyValuePair<string, IList<KeyValuePair<string, bool>>>>() : lists.Lists()).OrderBy(l => l.Key, StringComparer.Ordinal))
            {
                if (list.Value == null) { listReport[list.Key] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["published"] = false }; continue; }
                string type;
                if (!ListTypes.TryGetValue(list.Key, out type)) type = list.Key;
                var entries = list.Value.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
                var row = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["published"] = true, ["count"] = entries.Count,
                    ["notReady"] = entries.Where(e => !e.Value).Select(e => e.Key).ToList() };
                foreach (var origin in Origins) row[origin] = entries.Count(e => Origin(type, e.Key) == origin);
                foreach (var e in entries.Where(e => Origin(type, e.Key) == "otherWorlds")) foreign.Add(CampaignIdentity.WorldOrigin(e.Key).TrimEnd('_'));
                if (type == "Settlement" && store.HasType(type))
                {
                    var registered = new HashSet<string>(store.Objects(type).Select(e => e.Key), StringComparer.Ordinal);
                    row["notInObjectManager"] = entries.Where(e => !registered.Contains(e.Key)).Select(e => e.Key).ToList();
                }
                listReport[list.Key] = row;
            }
            return new Dictionary<string, object> {
                ["schema"] = "mnbl.registry-census", ["version"] = 2, ["phase"] = phase, ["closureMode"] = mode.ToString(),
                ["worldPrefix"] = Plan.WorldPrefix, ["campaignInstanceId"] = CampaignInstanceId, ["planned"] = Plan.GeneratedObjects.Count(),
                ["objectManager"] = types, ["campaignLists"] = listReport,
                ["otherWorldPrefixes"] = foreign.ToList(), ["closure"] = ClosureProblems(store, lists, mode)
            };
        }

        // What a rollback could not remove (empty after a complete one); null when no rollback ran.
        public List<string> RollbackRemaining { get; private set; }

        // MMR-36: rolls a failed new-world registration back through RemoveWorldObjects, recording the outcome in the trace; a failure of
        // the rollback itself is recorded and never masks the failure being rolled back.
        public List<string> RollBack(IObjectStore store, ICampaignLists lists)
        {
            try
            {
                List<string> remaining;
                int removed = RemoveWorldObjects(store, lists, out remaining);
                RollbackRemaining = remaining;
                trace.Add("rolled_back:" + removed + ":" + remaining.Count);
            }
            catch (Exception error) { RollbackRemaining = new List<string> { "rollback failed: " + error.Message }; trace.Add("rollback_failed:" + error.GetType().Name); }
            return RollbackRemaining;
        }

        // Unregisters this world's generated objects and any presumed leftovers under its prefix, never an official or shared
        // template and never an alias that existed before this world registered (installed content may reference it). For a
        // failed or abandoned registration (MMR-36 rolls staged registration back through this). remaining: this world's
        // objects still registered or listed afterwards (after initialization the campaign lists hold heroes, clans and
        // kingdoms the object manager cannot unregister); a complete removal leaves it empty.
        public int RemoveWorldObjects(IObjectStore store, ICampaignLists lists, out List<string> remaining)
        {
            int removed = 0;
            foreach (var o in Plan.GeneratedObjects.Reverse())
                if (!before.Contains(o.Key) && store.HasType(o.Type) && store.Contains(o.Type, o.Id) && store.Unregister(o.Type, o.Id)) removed++;
            foreach (var type in CensusTypes.Where(store.HasType))
                foreach (var entry in store.Objects(type).ToList())
                    if (CampaignIdentity.WorldPrefix(entry.Key) == Plan.WorldPrefix && store.Unregister(type, entry.Key)) removed++;
            remaining = new List<string>();
            foreach (var type in CensusTypes.Where(store.HasType))
                remaining.AddRange(store.Objects(type).Where(e => CampaignIdentity.WorldPrefix(e.Key) == Plan.WorldPrefix).Select(e => type + "." + e.Key));
            foreach (var type in Published(lists))
                remaining.AddRange(type.Value.Keys.Where(i => CampaignIdentity.WorldPrefix(i) == Plan.WorldPrefix).Select(i => type.Key + "." + i + " (campaign list)"));
            remaining.Sort(StringComparer.Ordinal);
            trace.Add("removed:" + removed + " remaining:" + remaining.Count);
            Registered = false;
            return removed;
        }

        private static bool InList(Dictionary<string, Dictionary<string, bool>> listed, string type, string id)
        {
            Dictionary<string, bool> ids;
            return listed.TryGetValue(type, out ids) && ids.ContainsKey(id);
        }

        // Published campaign lists by the object type they hold: id -> ready.
        private static Dictionary<string, Dictionary<string, bool>> Published(ICampaignLists lists)
        {
            var result = new Dictionary<string, Dictionary<string, bool>>(StringComparer.Ordinal);
            if (lists == null) return result;
            foreach (var list in lists.Lists())
            {
                string type;
                if (list.Value == null || !ListTypes.TryGetValue(list.Key, out type)) continue;
                Dictionary<string, bool> ids;
                if (!result.TryGetValue(type, out ids)) result[type] = ids = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var entry in list.Value) ids[entry.Key] = entry.Value;
            }
            return result;
        }

        private static string Sha256(string text)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false).GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
    }
}
