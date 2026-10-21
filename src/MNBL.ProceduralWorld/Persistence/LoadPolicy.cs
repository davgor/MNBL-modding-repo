using System;
using System.Collections.Generic;
using System.Linq;

namespace MNBL.ProceduralWorld.Persistence
{
    // MMR-49: what a generated save's load decides before anything native opens, as pure functions (tested offline). The loader
    // (GeneratedWorldLoader) gathers the facts; these say whether the load may go on, and if not, which refusal the player sees.
    //   Compatibility: the save's recorded build (mnbl.save.*, SaveBindingContract) against the running one. A different binding contract,
    //     save id base or world identity schema refuses: the save definitions are pinned to them. MMR-284: the game and War Sails builds
    //     refuse only below this release's minimum (SupportedBuilds; the saved build or the running one) or when a War Sails save meets a game
    //     without War Sails; any other difference, a newer build than any known included, is noted and the load goes on to the checks that
    //     keep it safe: the world's catalog (MMR-283) and every patch's signature (PatchSignatures, refused naming the patch: Patch).
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
            NotRebuildable = "not-rebuildable", NotGenerated = "not-generated", Busy = "busy", Unavailable = "save-unavailable", Failed = "load-failed", ContentConflict = "content-conflict";
        public static readonly string[] StrictKeys = { "contract", "saveIds", "worldIdentity" };
        public static readonly string[] BuildKeys = { "game", "naval" };   // MMR-284: refused below the minimum only (BuildCheck)
        public static readonly string[] RebuiltKeys = { "module", "moduleAssemblySha256", "backend", "navigationCache" };

        internal sealed class Refusal
        {
            public string Code, Detail;
            // MMR-283: a refusal with its own localizable text ("{=id}text" with {NAME} variables, rendered through TextObject).
            public string Text;
            public Dictionary<string, string> Variables = new Dictionary<string, string>(StringComparer.Ordinal);
            public string Title { get { return Titles.ContainsKey(Code) ? Titles[Code] : Titles[Failed]; } }
            public string Message { get { return Text ?? (Messages.ContainsKey(Code) ? Messages[Code] : Messages[Failed]); } }
            public override string ToString() { return Code + ": " + Detail; }
        }

        // MMR-286: every title and message is a localizable "{=id}text" (GeneratedWorldLoader renders it through TextObject; the strings are in
        // ModuleData/Languages/std_mnbl_procedural_world.xml).
        private static readonly Dictionary<string, string> Titles = new Dictionary<string, string> {
            [MissingWorld] = "{=mnbl_load_missing_world_title}This campaign's world is missing", [DamagedWorld] = "{=mnbl_load_damaged_world_title}This campaign's world is damaged",
            [DamagedSave] = "{=mnbl_load_damaged_save_title}This save is damaged", [IncompatibleBuild] = "{=mnbl_load_build_title}This campaign needs another version",
            [NotRebuildable] = "{=mnbl_load_not_rebuildable_title}This campaign's map cannot be rebuilt", [NotGenerated] = "{=mnbl_load_not_generated_title}Not a generated campaign",
            [Busy] = "{=mnbl_load_busy_title}Not now", [Unavailable] = "{=mnbl_load_unavailable_title}This save is in use",
            [Failed] = "{=mnbl_load_failed_title}The campaign could not be loaded", [ContentConflict] = "{=mnbl_load_content_title}A mod conflicts with this campaign" };
        private static readonly Dictionary<string, string> Messages = new Dictionary<string, string> {
            [MissingWorld] = "{=mnbl_load_missing_world}The world this campaign was played in is not on this computer. Copy the save together with its world (tools/Transfer-WorldSave.ps1), then load it again. Nothing was changed.",
            [DamagedWorld] = "{=mnbl_load_damaged_world}The world this campaign was played in is damaged on this computer. Copy it again together with the save (tools/Transfer-WorldSave.ps1), then load it again. Nothing was changed.",
            [DamagedSave] = "{=mnbl_load_damaged_save}The save file cannot be read whole. Load an earlier save of this campaign. Nothing was changed.",
            [IncompatibleBuild] = "{=mnbl_load_build}This campaign needs another version of the game, War Sails or this mod's save format. Load it with a version that supports it. Nothing was changed.",
            [NotRebuildable] = "{=mnbl_load_not_rebuildable}This version of the mod cannot rebuild this campaign's world exactly as it was saved. Load it with the version it was made with. Nothing was changed.",
            [NotGenerated] = "{=mnbl_load_not_generated}This save does not belong to a generated world.",
            [Busy] = "{=mnbl_load_busy}A campaign cannot be loaded now: another load, a world being generated or activated, or a campaign that is not on its map. Wait for it to finish, or return to the campaign map, then load again. Nothing was changed.",
            [Unavailable] = "{=mnbl_load_unavailable}Another program is using the save file, so it cannot be checked. Close it, then load again. Nothing was changed.",
            [Failed] = "{=mnbl_load_failed}The campaign could not be loaded. Your saves and worlds were not changed." };

        // MMR-286: the loader's other player messages, localizable ("{=id}text", {NAME} variables filled through TextObject).
        internal static class Texts
        {
            public const string AlreadyLoading = "{=mnbl_load_already}A campaign is already loading.";
            public const string Restored = "{=mnbl_load_restored}The campaign is restored.";
            public const string EndedRunning = "{=mnbl_load_ended_running}The campaign that was running had been closed for this load; load one of its saves to return to it.";
            public const string BackAtMenu = "{=mnbl_load_back_at_menu}You are back at the main menu.";
            public const string Ok = "{=mnbl_load_ok}OK";
            // LoadRouting: a generated save the installed load was asked to open (SAVE; REASON: why the routing is not installed).
            public const string Routed = "{=mnbl_load_routed}The save {SAVE} belongs to a generated world; only the world loader of MNBL Procedural World opens it. Nothing was loaded.";
            public const string RoutedUnapplied = "{=mnbl_load_routed_unapplied}The save {SAVE} belongs to a generated world; only the world loader of MNBL Procedural World opens it, and its load routing is not installed ({REASON}). Nothing was loaded.";
            // PeriodicSeam: a loaded periodic world whose patches did not apply is frozen (REASON: the patch error's first line).
            public const string PeriodicFrozen = "{=mnbl_load_periodic_frozen}This periodic world cannot run: its periodic patches did not apply ({REASON}).";
            public static IEnumerable<string> All() { return typeof(Texts).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()); }
        }
        // Every localizable title and message of a load (the module strings harness checks each has an id and is in the language file).
        public static IEnumerable<string> AllTexts() { return Titles.Values.Concat(Messages.Values).Concat(Texts.All()); }

        public static Refusal Refuse(string code, string detail) { return new Refusal { Code = code, Detail = detail }; }

        // MMR-283: the installed content refuses the load (the conflict's kind as CatalogPin names it, its object and module; official: the
        // module is the game's own). Localizable, naming the module and the object.
        public static Refusal Content(string kind, string obj, string module, bool official, string target, string detail)
        {
            string who = module == null ? "unknown" : official ? "game" : "mod", text;
            switch (kind)
            {
                case "missing-document": text = "{=mnbl_load_content_document}The game data {OBJECT} cannot be read, and this campaign's world is checked against it. Nothing was changed."; break;
                case "missing":
                    text = who == "mod" ? "{=mnbl_load_content_missing_mod}This campaign uses {OBJECT}, which is missing: the module {MODULE} provided it and is not active, or removes or replaces it. Enable the mods this campaign was played with, then load it again. Nothing was changed."
                                        : "{=mnbl_load_content_missing}This campaign uses {OBJECT}, which is missing from the installed game and mods. Enable the mods this campaign was played with, then load it again. Nothing was changed.";
                    break;
                case "retyped":
                case "changed":
                    text = who == "mod" ? "{=mnbl_load_content_changed_mod}The module {MODULE} changes {OBJECT}, which this campaign uses, in what its world reads. Disable it, or load the campaign with the mods it was played with. Nothing was changed."
                         : who == "game" ? "{=mnbl_load_content_changed_game}This game version changes {OBJECT} (in {MODULE}), which this campaign uses, in what its world reads. Nothing was changed."
                                         : "{=mnbl_load_content_changed}{OBJECT}, which this campaign uses, differs in what its world reads. Load the campaign with the mods it was played with. Nothing was changed.";
                    break;
                case "added":
                    text = who == "mod" ? "{=mnbl_load_content_added_mod}The module {MODULE} adds {OBJECT}, which names {TARGET}; this campaign's world was made without it and cannot supply that. Disable the module to load this campaign. Nothing was changed."
                                        : "{=mnbl_load_content_added}{OBJECT} was added after this campaign was made and names {TARGET}, which this campaign's world cannot supply. Nothing was changed.";
                    break;
                case "battle":
                    text = who == "unknown" ? "{=mnbl_load_content_battle}The installed battle scenes ({OBJECT}) leave some ground of this campaign's world without a battle scene. Nothing was changed."
                                            : "{=mnbl_load_content_battle_mod}The battle scenes of the module {MODULE} ({OBJECT}) leave some ground of this campaign's world without a battle scene. Disable it, then load again. Nothing was changed.";
                    break;
                default: text = "{=mnbl_load_content_other}{OBJECT} cannot be used by this campaign's world. Nothing was changed."; break;
            }
            var refusal = new Refusal { Code = ContentConflict, Detail = detail, Text = text };
            refusal.Variables["OBJECT"] = obj ?? "?"; refusal.Variables["MODULE"] = module ?? ""; refusal.Variables["TARGET"] = target ?? "";
            return refusal;
        }

        // MMR-284: a game or War Sails build below this release's minimum (the saved one: made with an older game; the running one: the game is
        // older than this mod supports). Localizable, naming the product, the builds and the minimum.
        public static Refusal BelowMinimum(string key, bool saved, string build, string minimum, string detail)
        {
            var refusal = new Refusal { Code = IncompatibleBuild, Detail = detail,
                Text = saved ? "{=mnbl_load_build_saved_old}This campaign was saved with {PRODUCT} {BUILD}, older than {MINIMUM}, the oldest this version of the mod supports. Load it with the version of the mod it was made with. Nothing was changed."
                             : "{=mnbl_load_build_running_old}This version of the mod needs {PRODUCT} {MINIMUM} or later; this game has {BUILD}. Update the game, then load again. Nothing was changed." };
            refusal.Variables["PRODUCT"] = key == "naval" ? "War Sails" : "Bannerlord"; refusal.Variables["BUILD"] = build ?? "?"; refusal.Variables["MINIMUM"] = minimum;
            return refusal;
        }

        // MMR-284: a patch this game build no longer accepts (PatchSignatureException: the patch, its target, what is installed). Localizable,
        // naming the patch and the member.
        public static Refusal Patch(string patch, string target, string detail)
        {
            var refusal = new Refusal { Code = IncompatibleBuild, Detail = detail,
                Text = "{=mnbl_load_patch_signature}This game version changes {TARGET}, which this mod patches ({PATCH}), so this campaign cannot be loaded with this version of the mod. Nothing was changed." };
            refusal.Variables["PATCH"] = patch ?? "?"; refusal.Variables["TARGET"] = target ?? "?";
            return refusal;
        }

        // MMR-284: one build key (game, naval), saved against running: a refusal below the minimum or for a War Sails save without War Sails,
        // else null, with a note when the builds differ. source: where the saved value came from (" (from the header)" for a pre-MMR-48 save).
        public static Refusal BuildCheck(string key, string saved, string running, string source, out string note)
        {
            note = null;
            bool naval = key == "naval";
            string minimum = naval ? CampaignBootstrap.SupportedBuilds.MinimumNaval : CampaignBootstrap.SupportedBuilds.MinimumGame;
            bool savedAbsent = saved == null || saved == "absent", runningAbsent = running == null || running == "absent";
            if (naval && !savedAbsent && runningAbsent) return Refuse(IncompatibleBuild, "this campaign was saved with War Sails " + saved + ", which is not installed");
            if (naval && savedAbsent && runningAbsent) return null;
            if (!naval || !savedAbsent)
            {
                if (!CampaignBootstrap.SupportedBuilds.AtLeast(saved, minimum))
                    return BelowMinimum(key, true, saved, minimum, key + ": saved " + (saved ?? "(none)") + source + ", below the minimum " + minimum);
            }
            if (!CampaignBootstrap.SupportedBuilds.AtLeast(running, minimum))
                return BelowMinimum(key, false, running, minimum, key + ": running " + (running ?? "(none)") + ", below the minimum " + minimum);
            if (saved != running) note = key + " differs (saved " + (saved ?? "(none)") + source + ", running " + running + "): a supported build (minimum " + minimum + ", no maximum); the catalog and the patch signatures are verified instead";
            return null;
        }

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
                if (a == b) continue;
                // MMR-50: a War Sails save on a game without War Sails, said as such (WorldCompatibilityPolicy words it the same).
                if (key == "naval" && (b == "absent" || b == null) && a != null && a != "absent") strict.Add("this campaign was saved with War Sails " + a + ", which is not installed");
                else strict.Add(key + ": saved " + (a ?? "(none)") + ", running " + (b ?? "(none)"));
            }
            if (strict.Count > 0) return Refuse(IncompatibleBuild, String.Join("; ", strict));
            foreach (var key in BuildKeys)
            {
                string a, b, note; saved.TryGetValue(key, out a); running.TryGetValue(key, out b);
                var refusal = BuildCheck(key, a, b, "", out note);
                if (refusal != null) return refusal;
                if (note != null && notes != null) notes.Add(note);
            }
            foreach (var key in RebuiltKeys)
            {
                string a, b; saved.TryGetValue(key, out a); running.TryGetValue(key, out b);
                if (a != b && notes != null) notes.Add(key + " differs (saved " + (a ?? "(none)") + ", running " + (b ?? "(none)") + "): the rebuilt world is verified instead");
            }
            return null;
        }

        // MMR-50: a save made before MMR-48 recorded no build keys; its header still records the game version (ApplicationVersion) and War
        // Sails' (Module_NavalDLC; absent when the save was made without it). Null when the header records neither.
        public static Dictionary<string, string> LegacyBuild(IDictionary<string, string> header)
        {
            string game, naval;
            if (header == null || !header.TryGetValue("ApplicationVersion", out game)) return null;
            if (!header.TryGetValue("Module_NavalDLC", out naval)) naval = "absent";
            return new Dictionary<string, string>(StringComparer.Ordinal) { ["game"] = game, ["naval"] = naval };
        }

        // MMR-50: the refusal, or null, for a save without build keys: its header's game and War Sails versions against the running ones
        // (MMR-284: by the same minimum rule, BuildCheck).
        public static Refusal LegacyCompatibility(IDictionary<string, string> legacy, IDictionary<string, string> running, List<string> notes)
        {
            if (notes != null) notes.Add("saved before MMR-48: no build keys recorded; the header's game and War Sails versions are compared and the world's reconstruction is verified");
            if (legacy == null) return null;
            foreach (var key in BuildKeys)
            {
                string a, b, note; legacy.TryGetValue(key, out a); running.TryGetValue(key, out b);
                var refusal = BuildCheck(key, a, b, " (from the header)", out note);
                if (refusal != null) return refusal;
                if (note != null && notes != null) notes.Add(note);
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
