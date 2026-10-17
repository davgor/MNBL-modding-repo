using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.Persistence;
using MNBL.ProceduralWorld.Surface;

// MMR-49 loader decisions, without the game:
//   LoaderTests OUT_JSON
// LoadPolicy: the recorded build against the running one (refusing contract, save id, identity schema, game and War Sails differences;
// noting module, backend and cache differences; a save without keys verified by reconstruction), the rebuilt documents against the
// package's, the installed scene's state, rebuilt values against saved ones, every refusal's title and message, the routing of the installed
// load's calls (one load per Continue), when a load may start, and how a stalled engine is told from a busy one; WorldLifetimeScope: tracked
// statics against the baseline.
internal static class LoaderTests
{
    private static int checks, failures;
    private static readonly List<string> lines = new List<string>();
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; string line = (condition ? "ok    " : "FAIL  ") + label; lines.Add(line); Console.WriteLine(line); }

    private static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("usage: LoaderTests OUT_JSON"); return 2; }
        try { Run(); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.loader-tests", ["version"] = 1, ["ticket"] = "MMR-49", ["checks"] = checks, ["failures"] = failures, ["lines"] = lines };
        File.WriteAllText(args[0], new JavaScriptSerializer().Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static Dictionary<string, string> Build(string game = "v1.4.8.119303", string module = "v0.0.1.0")
    {
        return new Dictionary<string, string> { ["contract"] = "mnbl.save-binding.v1", ["module"] = module, ["moduleAssemblySha256"] = new string('a', 64), ["game"] = game, ["naval"] = "v1.2.8.119303",
            ["backend"] = "mnbl.runtime-world-backend/R07; producerLock 000000000000", ["navigationCache"] = "mnbl.navigation-cache-key/1; adapter mmr41/1", ["saveIds"] = "7910000/v1", ["worldIdentity"] = "mnbl.world-identity.v1" };
    }

    private static void Run()
    {
        // Compatibility.
        var running = Build();
        var notes = new List<string>();
        Check(LoadPolicy.Compatibility(Build(), running, notes) == null && notes.Count == 0, "compatibility: the same build loads with no note");
        notes.Clear();
        Check(LoadPolicy.Compatibility(new Dictionary<string, string>(), running, notes) == null && notes.Single().Contains("before MMR-48"), "compatibility: a save without build keys (before MMR-48) loads, its reconstruction verified instead");
        foreach (var key in LoadPolicy.StrictKeys)
        {
            var saved = Build(); saved[key] = "other";
            var refusal = LoadPolicy.Compatibility(saved, running, null);
            Check(refusal != null && refusal.Code == LoadPolicy.IncompatibleBuild && refusal.Detail.StartsWith(key + ": saved other", StringComparison.Ordinal), "compatibility: a different " + key + " refuses (incompatible build)");
        }
        var missingStrict = Build(); missingStrict.Remove("game");
        Check(LoadPolicy.Compatibility(missingStrict, running, null) != null, "compatibility: a save whose keys omit the game version refuses");
        notes.Clear();
        var updated = Build(module: "v0.0.2.0"); updated["moduleAssemblySha256"] = new string('b', 64); updated["backend"] = "other backend"; updated["navigationCache"] = "other cache";
        Check(LoadPolicy.Compatibility(updated, running, notes) == null && notes.Count == 4 && notes.All(n => n.Contains("verified instead")), "compatibility: a different module build, backend or cache key loads with a note each (the rebuilt world is verified)");
        // Rebuilt documents.
        var package = new Dictionary<string, string> { ["Settlements"] = "1", ["NPCCharacters"] = "2" };
        Check(LoadPolicy.RebuiltDocuments(package, new Dictionary<string, string>(package)) == null, "documents: the rebuilt documents equal to the package's load");
        var refused = LoadPolicy.RebuiltDocuments(package, new Dictionary<string, string> { ["Settlements"] = "1", ["NPCCharacters"] = "3", ["Kingdoms"] = "4" });
        Check(refused != null && refused.Code == LoadPolicy.NotRebuildable && refused.Detail.Contains("NPCCharacters rebuilds differently") && refused.Detail.Contains("Kingdoms is rebuilt but not in the package"), "documents: a document rebuilt differently, or one too many, refuses (not rebuildable)");
        Check(LoadPolicy.RebuiltDocuments(package, new Dictionary<string, string> { ["Settlements"] = "1" }).Detail.Contains("NPCCharacters is in the package but not rebuilt"), "documents: a document missing from the rebuild refuses");
        // The installed scene.
        var expected = new Dictionary<string, string> { ["scene.xscene"] = "a", ["terrain.bin"] = "b", ["navmesh.bin"] = "c" };
        Check(LoadPolicy.Scene(expected, new Dictionary<string, string>(expected)) == LoadPolicy.SceneState.Installed, "scene: every file with the package's hash is installed");
        Check(LoadPolicy.Scene(expected, null) == LoadPolicy.SceneState.Missing && LoadPolicy.Scene(expected, new Dictionary<string, string>()) == LoadPolicy.SceneState.Missing
              && LoadPolicy.Scene(expected, expected.ToDictionary(k => k.Key, k => (string)null)) == LoadPolicy.SceneState.Missing, "scene: no folder, or no file of it, is missing (re-installed from the bundle)");
        Check(LoadPolicy.Scene(expected, new Dictionary<string, string> { ["scene.xscene"] = "a", ["terrain.bin"] = "b", ["navmesh.bin"] = null }) == LoadPolicy.SceneState.Different
              && LoadPolicy.Scene(expected, new Dictionary<string, string> { ["scene.xscene"] = "a", ["terrain.bin"] = "x", ["navmesh.bin"] = "c" }) == LoadPolicy.SceneState.Different,
            "scene: a partial folder or a changed file is different (refused: an installed scene is never patched or rewritten)");
        // Rebuilt values.
        Check(LoadPolicy.Rebuilt("climate", null, "x") == null && LoadPolicy.Rebuilt("climate", "x", "x") == null, "rebuilt values: an equal value, or one the save did not record, loads");
        var frame = LoadPolicy.Rebuilt("coordinate frame", "a", "b");
        Check(frame != null && frame.Code == LoadPolicy.NotRebuildable && frame.Detail == "coordinate frame: saved a, rebuilt b", "rebuilt values: a different coordinate frame or climate refuses");
        // Routing of the installed load's calls.
        Check(LoadPolicy.Routing(false, false, false) == LoadPolicy.Route.Installed && LoadPolicy.Routing(false, true, true) == LoadPolicy.Route.Installed,
            "routing: a save without a world binding always loads as installed");
        Check(LoadPolicy.Routing(true, false, false) == LoadPolicy.Route.Load, "routing: a generated save's call goes on to the world loader (after the installed checks and prompts)");
        Check(LoadPolicy.Routing(true, false, true) == LoadPolicy.Route.Ignore && LoadPolicy.Routing(true, true, false) == LoadPolicy.Route.Ignore && LoadPolicy.Routing(true, true, true) == LoadPolicy.Route.Ignore,
            "routing: the preload screen's repeated calls for the save it already started, and calls while the installed prompt is up, are ignored (one load per Continue)");
        // When a load may start.
        Check(LoadPolicy.BusyReason(false, null, false, false, false) == null && LoadPolicy.BusyReason(false, null, false, true, true) == null,
            "busy: from the main menu, or from a campaign on its map, a load may start");
        Check(LoadPolicy.BusyReason(true, null, false, false, false).Contains("loading") && LoadPolicy.BusyReason(false, "Compiling", false, false, false).Contains("Compiling")
              && LoadPolicy.BusyReason(false, null, true, true, true).Contains("activated") && LoadPolicy.BusyReason(false, null, false, true, false).Contains("not on its map"),
            "busy: another load, a generation job (named by its phase), an activation, or a running campaign off its map refuses the load");
        // A stalled engine against a busy one (samples every window, as the loader takes them).
        Func<Func<double, double>, double, double> stopsAt = (cpu, until) =>
        {
            var stall = new LoadPolicy.Stall();
            for (double t = LoadPolicy.Stall.WindowSeconds; t <= until; t += LoadPolicy.Stall.WindowSeconds) if (stall.Sample(t, cpu(t))) return t;
            return -1;
        };
        Check(stopsAt(t => t * 0.9, 900) == -1, "stall: an engine thread busy for 15 minutes (the first load's navigation caches take about 6.5) is never taken for stopped");
        double idle = stopsAt(t => 3.0, 600);
        Check(idle >= LoadPolicy.Stall.StoppedAfter && idle <= LoadPolicy.Stall.StoppedAfter + 2 * LoadPolicy.Stall.WindowSeconds, "stall: an engine thread with no processor time is taken for stopped after " + LoadPolicy.Stall.StoppedAfter + " s (at " + idle + " s)");
        Check(stopsAt(t => t < 100 ? 0 : t < 110 ? (t - 100) * 0.5 : 5, 205) == -1, "stall: a busy window resets the idle run (100 s idle, busy, 95 s idle is not stopped)");
        double trickle = stopsAt(t => t * 0.002, 600);
        Check(trickle > 0 && trickle <= LoadPolicy.Stall.StoppedAfter + 2 * LoadPolicy.Stall.WindowSeconds, "stall: a trickle below the per-window threshold is idle (measured per window, not cumulatively; stopped at " + trickle + " s)");
        Check(stopsAt(t => t, LoadPolicy.Stall.Deadline + 10) == LoadPolicy.Stall.Deadline, "stall: a poll unanswered past the deadline is stopped whatever the processor does (a loop that never returns)");
        // Every refusal speaks to the player.
        foreach (var code in new[] { LoadPolicy.MissingWorld, LoadPolicy.DamagedWorld, LoadPolicy.DamagedSave, LoadPolicy.IncompatibleBuild, LoadPolicy.NotRebuildable, LoadPolicy.NotGenerated, LoadPolicy.Busy, LoadPolicy.Unavailable, LoadPolicy.Failed })
        {
            var r = LoadPolicy.Refuse(code, "detail");
            Check(!String.IsNullOrEmpty(r.Title) && !String.IsNullOrEmpty(r.Message) && (code == LoadPolicy.NotGenerated || r.Message.Contains("changed")) && !r.Message.Contains("\\"),
                "refusal " + code + ": a title and a message saying nothing was changed");
        }
        Check(LoadPolicy.Refuse(LoadPolicy.MissingWorld, "x").Message.Contains("Transfer-WorldSave") && LoadPolicy.Refuse(LoadPolicy.IncompatibleBuild, "x").Message.Contains("version"),
            "refusals say what to do: copy the world with the save, or use the version the campaign was made with");
        // WorldLifetimeScope.
        int prefabs = 0, wrapper = 0;
        WorldLifetimeScope.Track("test: prefabs", () => prefabs);
        WorldLifetimeScope.Track("test: wrapper", () => wrapper);
        Check(WorldLifetimeScope.AtBaseline && WorldLifetimeScope.Snapshot().Count == 2, "lifetime scope: every tracked static at zero is the baseline");
        prefabs = 3; wrapper = 1;
        var held = WorldLifetimeScope.Held();
        Check(!WorldLifetimeScope.AtBaseline && held["test: prefabs"] == 3 && held["test: wrapper"] == 1, "lifetime scope: what a world holds is named with its count");
        prefabs = 0; wrapper = 0;
        WorldLifetimeScope.Track("test: broken", () => { throw new InvalidOperationException(); });
        Check(WorldLifetimeScope.Held().Single().Key == "test: broken" && WorldLifetimeScope.Held()["test: broken"] == -1, "lifetime scope: a count that throws is held (-1), never taken for the baseline");
        WorldLifetimeScope.Track("test: broken", () => 0);
        Check(WorldLifetimeScope.AtBaseline && WorldLifetimeScope.Names.Count == 3, "lifetime scope: tracking a name again replaces it");
        Check(Throws(() => WorldLifetimeScope.Track("", () => 0)) && Throws(() => WorldLifetimeScope.Track("x", null)), "lifetime scope: a tracked static needs a name and a count");
    }

    private static bool Throws(Action action) { try { action(); return false; } catch (Exception) { return true; } }
}
