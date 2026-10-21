using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.Generation;
using MNBL.ProceduralWorld.Persistence;
using HarmonyLib;

// MMR-284 (findings §17 RT-41) game and War Sails updates, without the game:
//   GameUpdateTests REPO_DIR GAME_ROOT HARMONY_DIR OUT_JSON
//   * the build rule (SupportedBuilds): a minimum per release and no maximum, ordered as TaleWorlds' ApplicationVersion orders builds;
//   * a save binding written for an older build at or above the minimum, and one newer than any known at release, load (LoadPolicy); one
//     below the minimum is refused with its localizable message, for the game and War Sails, recorded or running, and from a pre-MMR-48 header;
//   * the setup host: newer and older-at-or-above builds pass, a build below the minimum is refused naming the minimum; a changed patch is
//     refused naming the patch (GenerationErrors.PatchProblems), and so is a load (LoadPolicy.Patch);
//   * every recorded patch target (PatchSignatures) resolves by its signature in the installed assemblies; a changed signature, a missing
//     member, a missing type or a retyped field refuses that patch, naming it; an optional War Sails type that is absent is skipped;
//   * no exact-build string gates anything: the module's code holds no build literal outside SupportedBuilds, and no patch is located by
//     a build string.
internal static class GameUpdateTests
{
    private static int checks, failures;
    private static readonly Dictionary<string, object> Evidence = new Dictionary<string, object>();
    private static readonly List<string> Lines = new List<string>();
    private static string harmonyDir;
    private static string[] dirs;
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; string line = (condition ? "ok    " : "FAIL  ") + label; Lines.Add(line); Console.WriteLine(line); }

    private static int Main(string[] args)
    {
        if (args.Length != 4) { Console.Error.WriteLine("usage: GameUpdateTests REPO_DIR GAME_ROOT HARMONY_DIR OUT_JSON"); return 2; }
        harmonyDir = args[2];
        // The installed game and Bannerlord.Harmony resolve from their folders (registered before any method naming their types is compiled).
        dirs = new[] { "bin/Win64_Shipping_Client", "Modules/Native/bin/Win64_Shipping_Client", "Modules/SandBoxCore/bin/Win64_Shipping_Client", "Modules/SandBox/bin/Win64_Shipping_Client",
            "Modules/StoryMode/bin/Win64_Shipping_Client", "Modules/NavalDLC/bin/Win64_Shipping_Client" }.Select(d => Path.Combine(args[1], d)).Concat(new[] { harmonyDir }).Where(Directory.Exists).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (var d in dirs) { var path = Path.Combine(d, name); if (File.Exists(path)) return Assembly.LoadFrom(path); }
            return null;
        };
        Run("builds", Builds);
        Run("save bindings", Bindings);
        Run("setup host", Host);
        Run("patch signatures", () => Signatures(args[1]));
        Run("no build gate", () => NoBuildGate(args[0]));
        Evidence["schema"] = "mnbl.game-update-tests"; Evidence["version"] = 1; Evidence["ticket"] = "MMR-284";
        Evidence["minimum"] = new Dictionary<string, object> { ["game"] = SupportedBuilds.MinimumGame, ["naval"] = SupportedBuilds.MinimumNaval };
        Evidence["known"] = new Dictionary<string, object> { ["game"] = SupportedBuilds.KnownGame, ["naval"] = SupportedBuilds.KnownNaval };
        Evidence["checks"] = checks; Evidence["failures"] = failures; Evidence["lines"] = Lines;
        File.WriteAllText(args[3], new JavaScriptSerializer().Serialize(Evidence), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string what, Action body)
    {
        try { body(); }
        catch (Exception e) { Check(false, "unexpected exception (" + what + "): " + e); }
    }

    // ---- the build rule

    private static void Builds()
    {
        Check(SupportedBuilds.Game(SupportedBuilds.KnownGame) && SupportedBuilds.Naval(SupportedBuilds.KnownNaval), "the builds known at release are supported");
        Check(SupportedBuilds.Game("v1.4.8.100000") && SupportedBuilds.Game("v1.4.8") && SupportedBuilds.Game("v1.4.8.0"), "an older 1.4.8 change set, and the minimum itself, are supported");
        Check(SupportedBuilds.Game("v1.4.9.1") && SupportedBuilds.Game("v1.5.0.150000") && SupportedBuilds.Game("v2.0.0.0") && SupportedBuilds.Game("v1.10.0"),
            "every newer build is supported (no maximum; 1.10 is newer than 1.4, numerically)");
        Check(!SupportedBuilds.Game("v1.4.7.118000") && !SupportedBuilds.Game("v1.3.0.1") && !SupportedBuilds.Game("e1.8.1") && !SupportedBuilds.Game("b1.5.0"),
            "older releases, early access and beta lines are below the minimum (the type letter first, as ApplicationVersion orders it)");
        Check(SupportedBuilds.Game("d1.4.8.1") && SupportedBuilds.Compare("v1.4.8.119303", "v1.4.8") > 0 && SupportedBuilds.Compare("v1.4.8", "v1.4.8.0") == 0,
            "a development build orders after a release; an absent change set is 0");
        Check(!SupportedBuilds.Game(null) && !SupportedBuilds.Game("") && !SupportedBuilds.Game("1.4") && !SupportedBuilds.Game("v1.4.x.0") && !SupportedBuilds.Game("q1.4.8")
              && SupportedBuilds.Compare("v1.4.8", "junk") == null, "a value that is not a build is never supported");
        Check(SupportedBuilds.Naval("v1.2.8") && SupportedBuilds.Naval("v1.3.0.120000") && !SupportedBuilds.Naval("v1.2.7.0"), "War Sails: v1.2.8 and later are supported, v1.2.7 is not");
    }

    // ---- save bindings (the mnbl.save.* keys, LoadPolicy)

    private static Dictionary<string, string> Build(string game = SupportedBuilds.KnownGame, string naval = SupportedBuilds.KnownNaval)
    {
        return new Dictionary<string, string> { ["contract"] = "mnbl.save-binding.v1", ["module"] = "v0.0.1.0", ["moduleAssemblySha256"] = new string('a', 64), ["game"] = game, ["naval"] = naval,
            ["backend"] = "mnbl.runtime-world-backend/R07; producerLock 000000000000", ["navigationCache"] = "mnbl.navigation-cache-key/1; adapter mmr41/1", ["saveIds"] = "7910000/v1", ["worldIdentity"] = "mnbl.world-identity.v1" };
    }

    private static void Bindings()
    {
        var notes = new List<string>();
        Check(LoadPolicy.Compatibility(Build(), Build(), notes) == null && notes.Count == 0, "the build known at release loads with no note");
        notes.Clear();
        Check(LoadPolicy.Compatibility(Build("v1.4.8.100000"), Build(), notes) == null && notes.Count == 1 && notes[0].StartsWith("game differs", StringComparison.Ordinal) && notes[0].Contains("supported build"),
            "a binding written for an older build at or above the minimum (v1.4.8.100000) loads on v1.4.8.119303, noted");
        notes.Clear();
        Check(LoadPolicy.Compatibility(Build(), Build("v1.5.0.150000", "v1.3.0.120000"), notes) == null && notes.Count == 2,
            "a binding of the release build loads on a game and War Sails newer than any known at release (v1.5.0 / v1.3.0)");
        Check(LoadPolicy.Compatibility(Build("v1.6.2.200000", "v1.3.1.0"), Build("v1.6.2.200000", "v1.3.1.0"), null) == null,
            "a binding written for a build newer than any known at release loads on that build");
        Check(LoadPolicy.Compatibility(Build("v1.5.0.150000"), Build(), null) == null, "a binding of a newer build loads on an older supported one (no maximum either way; the catalog and patches decide)");
        var old = LoadPolicy.Compatibility(Build("v1.4.7.118000"), Build(), null);
        Check(old != null && old.Code == LoadPolicy.IncompatibleBuild && old.Text != null && old.Text.StartsWith("{=mnbl_load_build_saved_old}", StringComparison.Ordinal)
              && old.Variables["PRODUCT"] == "Bannerlord" && old.Variables["BUILD"] == "v1.4.7.118000" && old.Variables["MINIMUM"] == SupportedBuilds.MinimumGame
              && old.Detail.Contains("below the minimum") && old.Message.Contains("older than {MINIMUM}"),
            "a binding below the minimum (v1.4.7) is refused: incompatible-build, a localizable message naming the build and the minimum");
        var running = LoadPolicy.Compatibility(Build(), Build("v1.4.7.1"), null);
        Check(running != null && running.Text.StartsWith("{=mnbl_load_build_running_old}", StringComparison.Ordinal) && running.Variables["BUILD"] == "v1.4.7.1" && running.Message.Contains("Update the game"),
            "a game below the minimum is refused for every campaign, saying to update the game");
        var naval = LoadPolicy.Compatibility(Build(naval: "v1.2.7.0"), Build(), null);
        Check(naval != null && naval.Variables["PRODUCT"] == "War Sails" && naval.Variables["MINIMUM"] == SupportedBuilds.MinimumNaval, "a War Sails build below the minimum is refused, naming War Sails");
        var absent = LoadPolicy.Compatibility(Build(), Build(naval: "absent"), null);
        Check(absent != null && absent.Code == LoadPolicy.IncompatibleBuild && absent.Detail.Contains("which is not installed"), "a War Sails save on a game without War Sails is still refused");
        Check(LoadPolicy.Compatibility(Build(naval: "absent"), Build(naval: "absent"), null) == null, "a save made without War Sails loads without it");
        var other = Build(); other["contract"] = "mnbl.save-binding.v99";
        Check(LoadPolicy.Compatibility(other, Build(), null) != null, "the save contract is still strict");
        Check(LoadPolicy.LegacyCompatibility(new Dictionary<string, string> { ["game"] = "v1.4.8.100000", ["naval"] = "v1.2.8.0" }, Build("v1.5.0.0"), null) == null
              && LoadPolicy.LegacyCompatibility(new Dictionary<string, string> { ["game"] = "v1.4.7.1", ["naval"] = "v1.2.8.0" }, Build(), null).Detail.Contains("(from the header)"),
            "a pre-MMR-48 header follows the same rule: supported builds load, a build below the minimum is refused");
        var patch = LoadPolicy.Patch(PatchSignatures.SaveBinding, "TaleWorlds.Core.MBSaveLoad::OverwriteSaveAux", "detail");
        Check(patch.Code == LoadPolicy.IncompatibleBuild && patch.Text.StartsWith("{=mnbl_load_patch_signature}", StringComparison.Ordinal) && patch.Variables["PATCH"] == PatchSignatures.SaveBinding
              && patch.Variables["TARGET"].Contains("OverwriteSaveAux") && patch.Message.Contains("{PATCH}") && patch.Message.Contains("{TARGET}"),
            "a load refused by a changed patch names the patch and its member in a localizable message");
        Evidence["bindings"] = new Dictionary<string, object> { ["olderAtOrAboveMinimum"] = "v1.4.8.100000", ["newerThanKnown"] = "v1.6.2.200000", ["belowMinimum"] = "v1.4.7.118000", ["belowMinimumMessage"] = old == null ? null : old.Text };
    }

    // ---- the setup host

    private static void Host()
    {
        Func<string, string, List<GenerationError>> host = (game, naval) => GenerationErrors.Host(game, SupportedBuilds.MinimumGame, naval, SupportedBuilds.MinimumNaval, true, null);
        Check(host("v1.4.8.119303", "v1.2.8").Count == 0 && host("v1.4.8.100000", "v1.2.8").Count == 0 && host("v1.5.0.150000", "v1.3.0").Count == 0 && host("v2.0.0.0", "v2.0.0").Count == 0,
            "the setup host accepts the release build, an older change set at the minimum and newer builds (no maximum)");
        var oldGame = host("v1.4.7.118000", "v1.2.8");
        Check(oldGame.Count == 1 && oldGame[0].Code == "host.game_version" && oldGame[0].Category == GenerationErrorCategory.IncompatibleBuild && oldGame[0].Variables["MINIMUM"] == SupportedBuilds.MinimumGame
              && oldGame[0].Variables["ACTUAL"] == "v1.4.7.118000" && oldGame[0].Message.StartsWith("{=mnbl_err_game_version}", StringComparison.Ordinal) && oldGame[0].Message.Contains("or later"),
            "a game below the minimum is refused at setup, naming the minimum");
        var oldNaval = host("v1.4.8.119303", "v1.2.7");
        Check(oldNaval.Count == 1 && oldNaval[0].Code == "host.war_sails_version" && oldNaval[0].Variables["MINIMUM"] == SupportedBuilds.MinimumNaval, "War Sails below the minimum is refused at setup");
        var patches = GenerationErrors.PatchProblems(new[] { Tuple.Create(PatchSignatures.Periodic, "Helpers.NavigationHelper::FindPointAroundPosition", "installed: other"),
            Tuple.Create(PatchSignatures.Periodic, "Helpers.MenuHelper::EncounterAttackConsequence", "no such method"), Tuple.Create(PatchSignatures.Startup, "NotablesCampaignBehavior::SpawnNotablesAtGameStart", "no such method") });
        Check(patches.Count == 2 && patches.All(p => p.Code == "host.patch_signature" && p.Category == GenerationErrorCategory.IncompatibleBuild && p.Message.StartsWith("{=mnbl_err_patch_signature}", StringComparison.Ordinal))
              && patches[0].Variables["PATCH"] == PatchSignatures.Periodic && patches[0].Variables["TARGET"].Contains("FindPointAroundPosition") && patches[1].Variables["PATCH"] == PatchSignatures.Startup
              && patches[0].Detail.Contains("installed: other"), "a changed patch is refused at setup, one error per patch, naming the patch and its member");
        Check(GenerationErrors.PatchProblems(new Tuple<string, string, string>[0]).Count == 0, "no changed patch: no setup error");
    }

    // ---- patch signatures against the installed assemblies

    private static void Signatures(string game)
    {
        var assemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        foreach (var name in PatchSignatures.All.Select(t => t.Assembly).Distinct())
            foreach (var d in dirs)
            {
                string path = Path.Combine(d, name + ".dll");
                if (File.Exists(path)) { assemblies[name] = Assembly.LoadFrom(path); break; }
            }
        Func<PatchSignatures.Target, Type> types = t => { Assembly a; return assemblies.TryGetValue(t.Assembly, out a) ? a.GetType(t.Type, false) : null; };
        Check(PatchSignatures.All.Select(t => t.Assembly).Distinct().All(assemblies.ContainsKey), "every assembly a patch target lives in is installed (" + assemblies.Count + ")");
        var installed = PatchSignatures.Verify(types);
        Check(installed.Count == 0, "every recorded patch target (" + PatchSignatures.All.Count + ") resolves by its signature in the installed game and War Sails" +
            (installed.Count == 0 ? "" : ": " + String.Join("; ", installed.Take(5).Select(f => f.Message))));
        var patches = new[] { PatchSignatures.ColourGrade, PatchSignatures.CharacterCreation, PatchSignatures.Startup, PatchSignatures.WandererIntroduction, PatchSignatures.BattleScene,
            PatchSignatures.SaveBlock, PatchSignatures.SaveBinding, PatchSignatures.LoadRouting, PatchSignatures.LoadGuard, PatchSignatures.Periodic, PatchSignatures.Presentation };
        Check(PatchSignatures.All.All(t => patches.Contains(t.Patch)) && patches.All(p => PatchSignatures.All.Any(t => t.Patch == p)), "every target names one of the module's patches, and every patch has its targets recorded");
        Check(PatchSignatures.All.GroupBy(t => t.Type + "::" + t.Member + " " + t.Signature).All(g => g.Count() == 1), "no target is recorded twice");
        bool ambiguous = false;
        try { PatchSignatures.Of(PatchSignatures.All.First(t => t.Member == "FindPointInsideArea").Type, "FindPointInsideArea"); } catch (InvalidOperationException) { ambiguous = true; }
        Check(ambiguous && PatchSignatures.Of("Helpers.NavigationHelper", "FindPointInsideArea", PatchSignatures.All.First(t => t.Member == "FindPointInsideArea").Signature) != null,
            "an overloaded target is taken only with its signature");

        // A game update, simulated by a changed record: each kind of change refuses that patch, naming it.
        var sort = PatchSignatures.Of("TaleWorlds.CampaignSystem.ViewModelCollection.CharacterCreation.CharacterCreationCultureStageVM", "SortCultureList");
        Func<PatchSignatures.Target, Action<PatchSignatures.Target>, PatchSignatures.Target> changed = (t, edit) =>
        {
            var c = new PatchSignatures.Target { Patch = t.Patch, Type = t.Type, Assembly = t.Assembly, Member = t.Member, Signature = t.Signature, IsField = t.IsField, Optional = t.Optional };
            edit(c); return c;
        };
        var parameter = PatchSignatures.Verify(types, new[] { changed(sort, c => c.Signature = "instance System.Void(System.Collections.Generic.List<System.Object>)") });
        Check(parameter.Count == 1 && parameter[0].Patch == PatchSignatures.CharacterCreation && parameter[0].Message.Contains(PatchSignatures.CharacterCreation) && parameter[0].Message.Contains("SortCultureList")
              && parameter[0].Installed.Contains("MBBindingList"), "a changed parameter type refuses the patch, naming the patch, the member and what is installed: " + (parameter.Count == 1 ? parameter[0].Message : "none"));
        var returns = PatchSignatures.Verify(types, new[] { changed(PatchSignatures.Of("TaleWorlds.CampaignSystem.GameComponents.DefaultSceneModel", "GetBattleSceneForMapPatch"),
            c => c.Signature = c.Signature.Replace("instance System.String", "instance System.Object")) });
        Check(returns.Count == 1 && returns[0].Patch == PatchSignatures.BattleScene, "a changed return type refuses the patch");
        var staticness = PatchSignatures.Verify(types, new[] { changed(PatchSignatures.Of("TaleWorlds.Core.MBSaveLoad", "OverwriteSaveAux"), c => c.Signature = c.Signature.Replace("static ", "instance ")) });
        Check(staticness.Count == 1 && staticness[0].Patch == PatchSignatures.SaveBinding, "a static method made instance refuses the patch");
        var removed = PatchSignatures.Verify(types, new[] { changed(PatchSignatures.Of("TaleWorlds.CampaignSystem.CampaignBehaviors.NotablesCampaignBehavior", "SpawnNotablesAtGameStart"), c => c.Member = "SpawnNotablesAtGameStartRenamed") });
        Check(removed.Count == 1 && removed[0].Patch == PatchSignatures.Startup && removed[0].Installed == "no such method", "a removed or renamed member refuses the patch");
        var field = PatchSignatures.Verify(types, new[] { changed(PatchSignatures.Of("SandBox.SandBoxSaveHelper", "_isInquiryActive"), c => c.Signature = "System.Int32") });
        Check(field.Count == 1 && field[0].Patch == PatchSignatures.LoadRouting && field[0].Installed.Contains("System.Boolean"), "a retyped field refuses the patch");
        var gone = changed(sort, c => c.Type = "TaleWorlds.CampaignSystem.ViewModelCollection.CharacterCreation.Gone");
        var missingType = PatchSignatures.Verify(types, new[] { gone });
        Check(missingType.Count == 1 && missingType[0].Installed.Contains("is not installed"), "a type that is gone refuses its patch");
        var optional = changed(gone, c => c.Optional = true);
        Check(PatchSignatures.Verify(types, new[] { optional }).Count == 0 && PatchSignatures.Locate(optional, types) == null, "an optional (War Sails) target whose type is absent is skipped, not refused");
        var optionalChanged = changed(PatchSignatures.All.First(t => t.Optional), c => c.Signature = "instance System.Void(System.Int32)");
        Check(PatchSignatures.Verify(types, new[] { optionalChanged }).Count == 1, "an optional target that is installed with another signature is refused");
        Introduction(types);
        Evidence["signatures"] = new Dictionary<string, object> { ["targets"] = PatchSignatures.All.Count, ["patches"] = patches, ["unresolved"] = installed.Select(f => f.Message).ToList(),
            ["simulatedRefusal"] = parameter.Count == 1 ? parameter[0].Message : null };
    }

    // The wanderer introduction's IL anchor, as GeneratedPatches.Verify reads it (Harmony, without patching): exactly one Settlement.FindFirst call.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Introduction(Func<PatchSignatures.Target, Type> types)
    {
        var introduction = (MethodInfo)PatchSignatures.Locate(PatchSignatures.Of("TaleWorlds.CampaignSystem.CampaignBehaviors.LordConversationsCampaignBehavior", "conversation_wanderer_introduction_on_condition"), types);
        var findFirst = (MethodInfo)PatchSignatures.Locate(PatchSignatures.Of("TaleWorlds.CampaignSystem.Settlements.Settlement", "FindFirst"), types);
        int calls = PatchProcessor.GetOriginalInstructions(introduction).Count(i => i.Calls(findFirst));
        Check(calls == 1, "the wanderer introduction's installed IL has exactly one Settlement.FindFirst call, read by Harmony without patching (" + calls + ")");
    }

    // ---- no exact-build gate in the module's code

    private static void NoBuildGate(string repo)
    {
        string src = Path.Combine(repo, "src", "MNBL.ProceduralWorld");
        var literal = new Regex("\"[a-z]?\\d+\\.\\d+\\.\\d+(\\.\\d+)?\"");
        var hits = new List<string>();
        foreach (var file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string relative = file.Substring(src.Length + 1).Replace('\\', '/');
            // Probes and diagnostics describe fixtures; obj holds generated files.
            if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("Automation/", StringComparison.Ordinal) || relative.StartsWith("Diagnostics/", StringComparison.Ordinal)) continue;
            int n = 0;
            foreach (var line in File.ReadAllLines(file))
            {
                n++;
                int comment = line.IndexOf("//", StringComparison.Ordinal);
                string code = comment < 0 ? line : line.Substring(0, comment);
                if (literal.IsMatch(code) && !(relative == "CampaignBootstrap/WorldDataBundle.cs" && (code.Contains("MinimumGame =") || code.Contains("KnownGame ="))))
                    hits.Add(relative + ":" + n);
            }
        }
        Check(hits.Count == 0, "no game build literal in the module's code outside SupportedBuilds" + (hits.Count == 0 ? "" : ": " + String.Join(", ", hits)));
        var all = String.Join("\n", Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)).Select(File.ReadAllText));
        Check(!all.Contains("RequireInstalledVersion") && !all.Contains("GeneratedPatches.GameVersion") && !all.Contains("GeneratedPatches.NavalVersion"), "no exact-build gate remains (RequireInstalledVersion and the pinned versions are gone)");
        Check(!all.Contains("SupportedBuilds.KnownGame") || !Regex.IsMatch(all, "(==|!=)\\s*SupportedBuilds\\.Known"), "the known build is never compared for equality");
    }
}
