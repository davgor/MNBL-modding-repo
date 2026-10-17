using System;
using System.Collections.Generic;
using System.Linq;

namespace MNBL.ProceduralWorld.Persistence
{
    // MMR-49: what a generated save's load decides before anything native opens, as pure functions (tested offline). The loader
    // (GeneratedWorldLoader) gathers the facts; these say whether the load may go on, and if not, which refusal the player sees.
    //   Compatibility: the save's recorded build (mnbl.save.*, SaveBindingContract) against the running one. A different binding contract,
    //     save id base, world identity schema, game or War Sails version refuses: the patches and the save definitions are pinned to them.
    //     A different module build, backend or navigation cache key does not: the load rebuilds the world from its immutable bundle and
    //     requires every rebuilt artifact (documents, scene, coordinate frame, climate) to be the saved world's, so a module update that
    //     changes none of them loads, and one that changes any refuses there. A save made before MMR-48 records none of the keys; its
    //     reconstruction is checked the same way.
    //   Rebuilt documents: the package's documents (as validated at activation) and the rebuilt ones must be the same set with the same hashes.
    //   Scene files: installed (every file with the package's hash), missing (no folder or a file absent: re-installed from the bundle) or
    //     different (refused: an installed scene is never rewritten).
    //   Routing (LoadRouting): which saves the world loader takes, and the repeated calls it ignores (the preload screen behind Continue
    //     calls the installed load on every frame until a game starts).
    //   Busy: a load waits for nothing; it refuses while another load, a generation job or an activation runs, or while the running campaign
    //     is not on its map (ending a game that is not on its map stops the engine).
    //   Stall: whether a load the engine stopped answering is a busy engine (the first load of a world in a module build builds its
    //     navigation caches on the engine thread for minutes) or a stopped one (an exception escaped one of its callbacks).
    internal static class LoadPolicy
    {
        public const string MissingWorld = "missing-world", DamagedWorld = "damaged-world", DamagedSave = "damaged-save", IncompatibleBuild = "incompatible-build",
            NotRebuildable = "not-rebuildable", NotGenerated = "not-generated", Busy = "busy", Unavailable = "save-unavailable", Failed = "load-failed";
        public static readonly string[] StrictKeys = { "contract", "saveIds", "worldIdentity", "game", "naval" };
        public static readonly string[] RebuiltKeys = { "module", "moduleAssemblySha256", "backend", "navigationCache" };

        internal sealed class Refusal
        {
            public string Code, Detail;
            public string Title { get { return Titles.ContainsKey(Code) ? Titles[Code] : "The campaign could not be loaded"; } }
            public string Message { get { return Messages.ContainsKey(Code) ? Messages[Code] : Messages[Failed]; } }
            public override string ToString() { return Code + ": " + Detail; }
        }

        private static readonly Dictionary<string, string> Titles = new Dictionary<string, string> {
            [MissingWorld] = "This campaign's world is missing", [DamagedWorld] = "This campaign's world is damaged", [DamagedSave] = "This save is damaged", [IncompatibleBuild] = "This campaign needs another version",
            [NotRebuildable] = "This campaign's map cannot be rebuilt", [NotGenerated] = "Not a generated campaign", [Busy] = "Not now", [Unavailable] = "This save is in use",
            [Failed] = "The campaign could not be loaded" };
        private static readonly Dictionary<string, string> Messages = new Dictionary<string, string> {
            [MissingWorld] = "The world this campaign was played in is not on this computer. Copy the save together with its world (tools/Transfer-WorldSave.ps1), then load it again. Nothing was changed.",
            [DamagedWorld] = "The world this campaign was played in is damaged on this computer. Copy it again together with the save (tools/Transfer-WorldSave.ps1), then load it again. Nothing was changed.",
            [DamagedSave] = "The save file cannot be read whole. Load an earlier save of this campaign. Nothing was changed.",
            [IncompatibleBuild] = "This campaign was saved with another version of the game, War Sails or this mod's save format. Load it with the version it was made with. Nothing was changed.",
            [NotRebuildable] = "This version of the mod cannot rebuild this campaign's world exactly as it was saved. Load it with the version it was made with. Nothing was changed.",
            [NotGenerated] = "This save does not belong to a generated world.",
            [Busy] = "A campaign cannot be loaded now: another load, a world being generated or activated, or a campaign that is not on its map. Wait for it to finish, or return to the campaign map, then load again. Nothing was changed.",
            [Unavailable] = "Another program is using the save file, so it cannot be checked. Close it, then load again. Nothing was changed.",
            [Failed] = "The campaign could not be loaded. Your saves and worlds were not changed." };

        public static Refusal Refuse(string code, string detail) { return new Refusal { Code = code, Detail = detail }; }

        // ---------------------------------------------------------------- routing

        public enum Route { Installed, Ignore, Load }

        // What the routing prefix does with one call of the installed load. generated: the save's header carries a world binding; inquiryOpen:
        // the installed load's own prompt is up (module mismatch); repeatedFromPreload: the active game state is the preload state this
        // routing already took a save from (the preload screen calls again every frame until a game starts, also behind a refusal's message).
        public static Route Routing(bool generated, bool inquiryOpen, bool repeatedFromPreload)
        {
            if (!generated) return Route.Installed;
            return inquiryOpen || repeatedFromPreload ? Route.Ignore : Route.Load;
        }

        // Why a load may not start now (null when it may).
        public static string BusyReason(bool loading, string generationJobPhase, bool activationRunning, bool gameRunning, bool onMap)
        {
            if (loading) return "another campaign is loading";
            if (generationJobPhase != null) return "a world is being generated (" + generationJobPhase + ")";
            if (activationRunning) return "a world is being activated";
            if (gameRunning && !onMap) return "the running campaign is not on its map";
            return null;
        }

        // ---------------------------------------------------------------- stall

        // Samples of an unanswered engine poll: (seconds since the poll, the engine thread's processor seconds). The engine counts as stopped
        // when its thread used less than IdleCpu in every window of WindowSeconds for StoppedAfter seconds in a row, or when the poll stays
        // unanswered past Deadline whatever the processor does (a busy loop that never returns). The first load of a world after a module
        // build keeps the engine thread busy for about 6.5 minutes (the navigation caches): busy windows reset the idle run.
        internal sealed class Stall
        {
            public const double WindowSeconds = 5, IdleCpu = 0.05, StoppedAfter = 120, Deadline = 1800;
            private double windowStart = 0, windowCpu = Double.NaN, idleSince = 0;
            public string Reason { get; private set; }

            // True when the engine is taken for stopped (Reason says why).
            public bool Sample(double seconds, double engineCpu)
            {
                if (Double.IsNaN(windowCpu)) { windowCpu = engineCpu; windowStart = seconds; idleSince = seconds; }
                if (seconds - windowStart >= WindowSeconds)
                {
                    bool idle = engineCpu - windowCpu < IdleCpu;
                    if (!idle) idleSince = seconds;
                    windowStart = seconds; windowCpu = engineCpu;
                }
                if (seconds - idleSince >= StoppedAfter) { Reason = "its thread used no processor time for " + StoppedAfter + " s"; return true; }
                if (seconds >= Deadline) { Reason = "it answered nothing for " + Deadline + " s"; return true; }
                return false;
            }
        }

        // A refusal, or null, with notes on the differences that do not refuse.
        public static Refusal Compatibility(IDictionary<string, string> saved, IDictionary<string, string> running, List<string> notes)
        {
            saved = saved ?? new Dictionary<string, string>();
            if (saved.Count == 0) { if (notes != null) notes.Add("saved before MMR-48: no build keys recorded; the world's reconstruction is verified instead"); return null; }
            var strict = new List<string>();
            foreach (var key in StrictKeys)
            {
                string a, b; saved.TryGetValue(key, out a); running.TryGetValue(key, out b);
                if (a != b) strict.Add(key + ": saved " + (a ?? "(none)") + ", running " + (b ?? "(none)"));
            }
            if (strict.Count > 0) return Refuse(IncompatibleBuild, String.Join("; ", strict));
            foreach (var key in RebuiltKeys)
            {
                string a, b; saved.TryGetValue(key, out a); running.TryGetValue(key, out b);
                if (a != b && notes != null) notes.Add(key + " differs (saved " + (a ?? "(none)") + ", running " + (b ?? "(none)") + "): the rebuilt world is verified instead");
            }
            return null;
        }

        // The documents rebuilt from the bundle against the package's (id -> SHA-256): a refusal when they differ.
        public static Refusal RebuiltDocuments(IDictionary<string, string> package, IDictionary<string, string> rebuilt)
        {
            var problems = new List<string>();
            foreach (var id in package.Keys.Union(rebuilt.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                string a, b; package.TryGetValue(id, out a); rebuilt.TryGetValue(id, out b);
                if (a == null) problems.Add(id + " is rebuilt but not in the package");
                else if (b == null) problems.Add(id + " is in the package but not rebuilt");
                else if (a != b) problems.Add(id + " rebuilds differently");
            }
            return problems.Count == 0 ? null : Refuse(NotRebuildable, "documents: " + String.Join("; ", problems));
        }

        public enum SceneState { Installed, Missing, Different }

        // expected: the package's scene file hashes; installed: the scene folder's (a file absent maps to null; no folder: null or empty).
        // Only a scene with no file at all is missing (a sweep removes the whole folder); a partial folder is different, never patched.
        public static SceneState Scene(IDictionary<string, string> expected, IDictionary<string, string> installed)
        {
            if (installed == null || installed.Count == 0 || expected.Keys.All(k => !installed.ContainsKey(k) || installed[k] == null)) return SceneState.Missing;
            foreach (var kv in expected)
            {
                string h; installed.TryGetValue(kv.Key, out h);
                if (h != kv.Value) return SceneState.Different;
            }
            return SceneState.Installed;
        }

        // A value rebuilt from the bundle against the one the save recorded (the coordinate frame, the climate pattern).
        public static Refusal Rebuilt(string what, string saved, string rebuilt)
        {
            if (saved == null) return null;   // a save that did not record it
            return saved == rebuilt ? null : Refuse(NotRebuildable, what + ": saved " + saved + ", rebuilt " + (rebuilt ?? "(none)"));
        }
    }
}
