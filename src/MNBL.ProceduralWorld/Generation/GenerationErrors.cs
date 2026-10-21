using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MNBL.ProceduralWorld.Generation
{
    // The player-facing error categories MMR-47's ticket names: missing tool or DLC, incompatible build or schema, refused recipe, exhausted
    // quality attempts, disk full, bad source, runtime capability failure, interrupted generation. MMR-43 needs them first (the New Campaign
    // setup screen's War Sails and version refusals, and its generation outcomes), so the vocabulary is defined here; MMR-47 owns recovery,
    // logs, correlation ids and cleanup.
    // Messages are localisation-ready: "{=id}default text", with {NAME} placeholders the caller fills (TextObject.SetTextVariable). A
    // GenerationError's Detail is for the diagnostics view only (paths, hashes, raw statuses, producer codes, executable names); the
    // ordinary flow shows Title, Message and Action, which name only what a player can act on (the game, War Sails and their versions).
    // MMR-283: ContentConflict, another active module's content that generated worlds cannot use (named with its object).
    internal enum GenerationErrorCategory { MissingToolOrDlc, IncompatibleBuild, RefusedRecipe, ExhaustedQualityAttempts, DiskFull, BadSource, RuntimeCapabilityFailure, InterruptedGeneration, ContentConflict }

    internal sealed class GenerationError
    {
        public GenerationError(GenerationErrorCategory category, string code, string message, string detail, params string[] variables)
        {
            Category = category; Code = code; Message = message; Detail = detail ?? "";
            Variables = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < variables.Length; i += 2) Variables[variables[i]] = variables[i + 1];
        }
        public GenerationErrorCategory Category { get; private set; }
        public string Code { get; private set; }
        public string Message { get; private set; }
        public Dictionary<string, string> Variables { get; private set; }
        public string Detail { get; private set; }
        public string Title { get { return GenerationErrors.Title(Category); } }
        public string Action { get { return GenerationErrors.Action(Category); } }
        // MMR-47: the id the player sees ("Reference ...") and the diagnostics are retained under (GenerationDiagnostics); null until retained.
        public string CorrelationId { get; set; }
        public string RetainProblem { get; set; }
        public bool Retryable { get { return Category == GenerationErrorCategory.InterruptedGeneration || Category == GenerationErrorCategory.ExhaustedQualityAttempts || Category == GenerationErrorCategory.RefusedRecipe || Category == GenerationErrorCategory.BadSource; } }
        public override string ToString() { return Category + " " + Code + (CorrelationId != null ? " [" + CorrelationId + "]" : "") + (Detail.Length > 0 ? " (" + Detail + ")" : ""); }
    }

    internal static class GenerationErrors
    {
        public static string Title(GenerationErrorCategory category)
        {
            switch (category)
            {
                case GenerationErrorCategory.MissingToolOrDlc: return "{=mnbl_err_missing_title}Something required is missing";
                case GenerationErrorCategory.IncompatibleBuild: return "{=mnbl_err_build_title}This version does not match";
                case GenerationErrorCategory.RefusedRecipe: return "{=mnbl_err_recipe_title}The generator refused these settings";
                case GenerationErrorCategory.ExhaustedQualityAttempts: return "{=mnbl_err_quality_title}No playable world for this seed";
                case GenerationErrorCategory.DiskFull: return "{=mnbl_err_disk_title}Not enough disk space";
                case GenerationErrorCategory.BadSource: return "{=mnbl_err_source_title}The generated world could not be read";
                case GenerationErrorCategory.RuntimeCapabilityFailure: return "{=mnbl_err_runtime_title}The game could not build this world";
                case GenerationErrorCategory.ContentConflict: return "{=mnbl_err_content_title}Another mod conflicts with generated worlds";
                default: return "{=mnbl_err_interrupted_title}Generation was interrupted";
            }
        }

        public static string Action(GenerationErrorCategory category)
        {
            switch (category)
            {
                case GenerationErrorCategory.MissingToolOrDlc: return "{=mnbl_err_missing_action}Install or enable what is named above, then restart the game.";
                case GenerationErrorCategory.IncompatibleBuild: return "{=mnbl_err_build_action}Install a game and War Sails version this mod supports (a minimum is named above), or a version of this mod made for your game.";
                case GenerationErrorCategory.RefusedRecipe: return "{=mnbl_err_recipe_action}Try another seed. If it happens again, reinstall the mod.";
                case GenerationErrorCategory.ExhaustedQualityAttempts: return "{=mnbl_err_quality_action}Try another seed (Random World picks a new one).";
                case GenerationErrorCategory.DiskFull: return "{=mnbl_err_disk_action}Free some disk space, then generate again.";
                case GenerationErrorCategory.BadSource: return "{=mnbl_err_source_action}Generate again. If it happens again, reinstall the mod.";
                case GenerationErrorCategory.RuntimeCapabilityFailure: return "{=mnbl_err_runtime_action}Generate again. If it happens again, check that the game and War Sails are up to date.";
                case GenerationErrorCategory.ContentConflict: return "{=mnbl_err_content_action}Disable or update the module named above, then restart the game. Mods that only add items, troops, cultures or battle scenes, or change stats, work with generated worlds.";
                default: return "{=mnbl_err_interrupted_action}Generate again when you are ready; the same seed makes the same world.";
            }
        }

        // The host the setup screen runs on: War Sails installed and enabled, game and War Sails builds at or above this release's minimum
        // (MMR-284: no maximum; a newer build is checked by the patch signatures and the catalog instead, PatchProblems and MMR-283), and the
        // pinned generator binaries. Empty when compatible.
        public static List<GenerationError> Host(string installedGame, string minimumGame, string installedNaval, string minimumNaval, bool navalEnabled, IEnumerable<KeyValuePair<string, string>> toolProblems)
        {
            var problems = new List<GenerationError>();
            if (installedNaval == null || !navalEnabled)
                problems.Add(new GenerationError(GenerationErrorCategory.MissingToolOrDlc, "host.war_sails_missing",
                    "{=mnbl_err_war_sails_missing}War Sails is required: procedural worlds use its seas, ports and ships.", installedNaval == null ? "NavalDLC not installed" : "NavalDLC not enabled"));
            if (!CampaignBootstrap.SupportedBuilds.AtLeast(installedGame, minimumGame))
                problems.Add(new GenerationError(GenerationErrorCategory.IncompatibleBuild, "host.game_version",
                    "{=mnbl_err_game_version}This mod needs Bannerlord {MINIMUM} or later; this game is {ACTUAL}.", (installedGame ?? "?") + " is below " + minimumGame, "MINIMUM", minimumGame, "ACTUAL", installedGame ?? "?"));
            if (installedNaval != null && !CampaignBootstrap.SupportedBuilds.AtLeast(installedNaval, minimumNaval))
                problems.Add(new GenerationError(GenerationErrorCategory.IncompatibleBuild, "host.war_sails_version",
                    "{=mnbl_err_war_sails_version}This mod needs War Sails {MINIMUM} or later; installed is {ACTUAL}.", installedNaval + " is below " + minimumNaval, "MINIMUM", minimumNaval, "ACTUAL", installedNaval));
            foreach (var tool in toolProblems ?? Enumerable.Empty<KeyValuePair<string, string>>())
                problems.Add(new GenerationError(GenerationErrorCategory.MissingToolOrDlc, "host.tool", "{=mnbl_err_tool}The world generator files of this mod are missing or changed.", tool.Key + ": " + tool.Value));
            return problems;
        }

        // MMR-284: the patches this game build no longer accepts (PatchSignatures.Verify: patch, target, what is installed), one error per patch
        // (its first target), named by the patch and the installed member. Code host.patch_signature; the detail says what is installed.
        public static List<GenerationError> PatchProblems(IEnumerable<Tuple<string, string, string>> failures)
        {
            var problems = new List<GenerationError>();
            foreach (var failure in failures ?? Enumerable.Empty<Tuple<string, string, string>>())
                if (problems.All(p => p.Variables["PATCH"] != failure.Item1))
                    problems.Add(new GenerationError(GenerationErrorCategory.IncompatibleBuild, "host.patch_signature",
                        "{=mnbl_err_patch_signature}This game version changes {TARGET}, which this mod patches ({PATCH}). Generated worlds need a version of this mod made for this game.",
                        failure.Item2 + ": " + failure.Item3, "PATCH", failure.Item1, "TARGET", failure.Item2));
            return problems;
        }

        // The outcome of a generation that did not produce a world: the interim job's (the R01 generation, GenerationProbe) recorded status and
        // exception. A status this mapping does not know is a bad source, never silently a success.
        public static GenerationError FromGeneration(string status, Exception error, string failureCode, string detail)
        {
            if (error is OperationCanceledException || status == "cancelled")
                return new GenerationError(GenerationErrorCategory.InterruptedGeneration, "generation.cancelled", "{=mnbl_err_cancelled}Generation was cancelled before the world was finished.", detail);
            if (IsDiskFull(error))
                return new GenerationError(GenerationErrorCategory.DiskFull, "generation.disk_full", "{=mnbl_err_disk}The disk is full; the world could not be written.", detail);
            if (error is FileNotFoundException || (error is InvalidDataException && error.Message.StartsWith("Binary differs from pinned build", StringComparison.Ordinal)))
                return new GenerationError(GenerationErrorCategory.MissingToolOrDlc, "generation.tool", "{=mnbl_err_tool}The world generator files of this mod are missing or changed.", ToolName(error) + ": " + detail);
            switch (status)
            {
                case "producer_refused":
                    return new GenerationError(GenerationErrorCategory.RefusedRecipe, "generation.refused", "{=mnbl_err_refused}The generator refused the request.", "producer code " + (failureCode ?? "?") + "; " + detail);
                case "producer_protocol_error":
                case "producer_exit_failure":
                case "reader_failed":
                    return new GenerationError(GenerationErrorCategory.BadSource, "generation." + status, "{=mnbl_err_source}The generator stopped before the world was complete.", detail);
                case "quality_exhausted":
                    return new GenerationError(GenerationErrorCategory.ExhaustedQualityAttempts, "generation.quality_exhausted", "{=mnbl_err_quality}Every attempt for this seed failed the playability checks.", detail);
                default:
                    return new GenerationError(GenerationErrorCategory.BadSource, "generation.failed", "{=mnbl_err_failed}The world could not be generated.", detail);
            }
        }

        // MMR-44: a generation job's failure in its category, with the category's player message; code and detail are for diagnostics.
        public static GenerationError Job(GenerationErrorCategory category, string code, string detail)
        {
            switch (category)
            {
                case GenerationErrorCategory.InterruptedGeneration: return new GenerationError(category, code, "{=mnbl_err_cancelled}Generation was cancelled before the world was finished.", detail);
                case GenerationErrorCategory.DiskFull: return new GenerationError(category, code, "{=mnbl_err_disk}The disk is full; the world could not be written.", detail);
                case GenerationErrorCategory.MissingToolOrDlc: return new GenerationError(category, code, "{=mnbl_err_tool}The world generator files of this mod are missing or changed.", detail);
                case GenerationErrorCategory.RefusedRecipe: return new GenerationError(category, code, "{=mnbl_err_refused}The generator refused the request.", detail);
                case GenerationErrorCategory.ExhaustedQualityAttempts: return new GenerationError(category, code, "{=mnbl_err_quality}Every attempt for this seed failed the playability checks.", detail);
                case GenerationErrorCategory.IncompatibleBuild: return new GenerationError(category, code, "{=mnbl_err_incompatible}The world data does not match this game or War Sails version.", detail);
                case GenerationErrorCategory.RuntimeCapabilityFailure: return new GenerationError(category, code, "{=mnbl_err_runtime_world}The game could not build the map or travel for this world.", detail);
                default: return new GenerationError(GenerationErrorCategory.BadSource, code, "{=mnbl_err_source}The generator stopped before the world was complete.", detail);
            }
        }

        // MMR-283: a conflict of the installed content (a CatalogPin.Conflict's kind, object key, module and target; ContentCompatibility.Errors),
        // named by its object and, when one provides it, its module (official: the game's own module, an update). The detail is for diagnostics.
        public static GenerationError Content(string kind, string obj, string module, bool official, string target, string detail)
        {
            string who = module == null ? "unknown" : official ? "game" : "mod";
            Func<string, string, GenerationError> error = (code, message) => new GenerationError(GenerationErrorCategory.ContentConflict, "content." + code, message, detail, "MODULE", module ?? "", "OBJECT", obj ?? "?", "TARGET", target ?? "");
            switch (kind)
            {
                case "missing-document": return error("document", "{=mnbl_err_content_document}The game data {OBJECT} cannot be read, and generated worlds are built from it.");
                case "missing":
                    return who == "mod" ? error("missing", "{=mnbl_err_content_missing_mod}{OBJECT}, which generated worlds use, is missing: the module {MODULE} removes or replaces it.")
                                        : error("missing", "{=mnbl_err_content_missing}{OBJECT}, which generated worlds use, is missing from the installed game.");
                case "retyped":
                case "changed":
                    return who == "mod" ? error("changed", "{=mnbl_err_content_changed_mod}The module {MODULE} changes {OBJECT} in what generated worlds read from it.")
                         : who == "game" ? error("changed", "{=mnbl_err_content_changed_game}This game version changes {OBJECT} (in {MODULE}) in what generated worlds read from it.")
                                         : error("changed", "{=mnbl_err_content_changed}{OBJECT} differs in what generated worlds read from it.");
                case "unresolved":
                    return who == "mod" ? error("unresolved", "{=mnbl_err_content_unresolved_mod}The module {MODULE} adds or changes {OBJECT}, which names {TARGET}, and that is not installed.")
                                        : error("unresolved", "{=mnbl_err_content_unresolved}{OBJECT} names {TARGET}, which is not installed.");
                case "battle":
                    return who == "unknown" ? error("battle", "{=mnbl_err_content_battle}The installed battle scenes ({OBJECT}) leave some ground of a generated world without a battle scene.")
                                            : error("battle", "{=mnbl_err_content_battle_mod}The battle scenes of the module {MODULE} ({OBJECT}) leave some ground of a generated world without a battle scene.");
                case "scene": return error("scene", "{=mnbl_err_content_scene}The scene {OBJECT}, which generated worlds use, is not installed.");
                default: return error("other", "{=mnbl_err_content_other}{OBJECT} cannot be used by generated worlds.");
            }
        }

        // MMR-283: the content check itself could not run (the setup screen never generates unchecked).
        public static GenerationError ContentUnchecked(string detail)
        {
            return new GenerationError(GenerationErrorCategory.ContentConflict, "content.unchecked", "{=mnbl_err_content_unchecked}The installed mods could not be checked against generated worlds.", detail);
        }

        // MMR-46: starting the campaign in a reviewed world failed and was undone (MMR-45's rollback); and the case after the initial save, which
        // MMR-45 leaves for recovery at the next start.
        public static GenerationError Activation(string detail)
        {
            return new GenerationError(GenerationErrorCategory.RuntimeCapabilityFailure, "activation.failed", "{=mnbl_err_activation}The campaign could not be started in this world. The attempt was undone; generate the world again to retry.", detail);
        }
        public static GenerationError ActivationUnresolved(string detail)
        {
            return new GenerationError(GenerationErrorCategory.RuntimeCapabilityFailure, "activation.unresolved", "{=mnbl_err_activation_unresolved}The campaign started, but its first save could not be confirmed. The game checks it the next time it starts.", detail);
        }

        // The generation job is still finishing or releasing an earlier world (a quick reopen and Generate).
        public static GenerationError Busy(string detail)
        {
            return new GenerationError(GenerationErrorCategory.InterruptedGeneration, "generation.busy", "{=mnbl_err_busy}An earlier world is still being finished. Try again in a moment.", detail);
        }

        public static bool IsDiskFullError(Exception error) { return IsDiskFull(error); }

        // MMR-47: a native tool's output reporting a full disk (Win32 ERROR_DISK_FULL 112 / ERROR_HANDLE_DISK_FULL 39 as std::filesystem
        // or ofstream report them, or the C runtime's ENOSPC).
        public static bool DiskFullText(string text)
        {
            if (String.IsNullOrEmpty(text)) return false;
            foreach (var marker in new[] { "not enough space", "disk full", "disk is full", "no space left", "no_space_on_device", "enospc", "error 112", "error code 112", "(112)" })
                if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // MMR-47: worldbridge --generate's failure (SourceStage), in one pure place the offline tests reach. exitCode 4 cancelled; 3 exhausted or
        // failed (the state's status and the last attempt's failure kind and producer outcome); 2 refused before any attempt (stderr names
        // why). A full disk is a full disk whatever the exit; changed policies or attempt state are an incompatible build (an update), never a
        // refused recipe.
        public static GenerationError FromBridge(int exitCode, string stderr, string detail, string stateStatus, string lastFailure, string lastOutcome)
        {
            if (exitCode == 4) return Job(GenerationErrorCategory.InterruptedGeneration, "generate.cancelled", detail);
            if (DiskFullText(stderr) || DiskFullText(detail)) return Job(GenerationErrorCategory.DiskFull, "generate.disk_full", detail);
            if (exitCode == 3 && stateStatus != null)
            {
                if (stateStatus == "exhausted") return Job(GenerationErrorCategory.ExhaustedQualityAttempts, "generate.exhausted", detail);
                if (lastFailure == "input") return Job(GenerationErrorCategory.RefusedRecipe, "generate.refused", detail);
                if (lastFailure == "engine") return Job(GenerationErrorCategory.RuntimeCapabilityFailure, "generate.engine", detail);
                return Job(GenerationErrorCategory.BadSource, "generate." + (lastOutcome ?? lastFailure ?? "failed"), detail);
            }
            if (exitCode == 2)
            {
                string e = stderr ?? "";
                if (e.Contains("seed_width_unverified") || e.Contains("producer_changed") || e.Contains("pin_mismatch")) return Job(GenerationErrorCategory.MissingToolOrDlc, "generate.producer_changed", detail);
                if (e.Contains("policy_changed") || e.Contains("state_request_mismatch") || e.Contains("frozen_world_mismatch")) return Job(GenerationErrorCategory.IncompatibleBuild, "generate.changed_inputs", detail);
                return Job(GenerationErrorCategory.RefusedRecipe, "generate.request_refused", detail);
            }
            return Job(GenerationErrorCategory.BadSource, "generate.exit_" + exitCode, detail);
        }

        // MMR-47: a bundle the verifier refused while the job validated it: other versions, capabilities or schema are an incompatible build;
        // anything else is a bad source.
        public static GenerationError FromBundleProblems(IList<string> problems)
        {
            bool incompatible = problems.Count > 0 && problems.All(p => p.StartsWith("built for", StringComparison.Ordinal) || p.StartsWith("requires runtime capability", StringComparison.Ordinal)
                || p.StartsWith("manifest is not ", StringComparison.Ordinal) || p.StartsWith("DATA_READY is not ", StringComparison.Ordinal));
            return Job(incompatible ? GenerationErrorCategory.IncompatibleBuild : GenerationErrorCategory.BadSource, "data.rejected", String.Join("; ", problems.Take(8)));
        }

        private static bool IsDiskFull(Exception error)
        {
            for (var e = error; e != null; e = e.InnerException)
            {
                var io = e as IOException;
                int code = io == null ? 0 : io.HResult & 0xFFFF;
                if (code == 0x70 || code == 0x27) return true;   // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
            }
            return false;
        }

        private static string ToolName(Exception error)
        {
            var missing = error as FileNotFoundException;
            string text = missing != null && missing.FileName != null ? missing.FileName : error.Message;
            return text.IndexOf("worldbridge", StringComparison.OrdinalIgnoreCase) >= 0 ? "worldbridge" : text.IndexOf("fwg", StringComparison.OrdinalIgnoreCase) >= 0 ? "fwg" : "?";
        }
    }
}
