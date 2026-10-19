using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.Automation;

// Only external API boundaries are doubled. AutorunHost and ProbeContext are compiled unchanged.
namespace TaleWorlds.ScreenSystem
{
    public static class ScreenManager { public static object TopScreen { get; set; } }
}
namespace TaleWorlds.MountAndBlade
{
    public class MBInitialScreenBase
    {
        public static void DoExitButtonAction() { CleanupDoubles.ExitButtonCalls++; }
    }
}
namespace TaleWorlds.Engine
{
    public static class Utilities
    {
        public static void QuitGame() { CleanupDoubles.QuitCalls++; }
        public static void DoDelayedexit(int code) { CleanupDoubles.DelayedCalls++; }
    }
}
namespace MNBL.ProceduralWorld
{
    public static class SubModule { public static string Store { get; set; } }
}
namespace MNBL.ProceduralWorld.Automation
{
    internal static class AutorunProbes
    {
        internal static Func<ProbeContext, IEnumerator> Factory;
        internal static IEnumerator Create(string id, ProbeContext context) { return Factory(context); }
    }
}
internal static class CleanupDoubles
{
    internal static int ExitButtonCalls, QuitCalls, DelayedCalls;
    internal static int Total { get { return ExitButtonCalls + QuitCalls + DelayedCalls; } }
    internal static void Reset() { ExitButtonCalls = QuitCalls = DelayedCalls = 0; }
}
internal static class AutorunCleanupTests
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static int assertions;
    private static readonly List<object> Cases = new List<object>();
    private sealed class Fixture
    {
        internal bool DoneA, DoneB, FinallyRan;
        internal int CancelA, CancelB, Moves;
        internal IEnumerator Run(ProbeContext context, bool throwOnResume)
        {
            context.TrackOwnedWork(() => CancelA++, () => DoneA);
            context.TrackOwnedWork(() => CancelB++, () => DoneB);
            try
            {
                Moves++;
                yield return null;
                Moves++;
                if (throwOnResume) throw new InvalidOperationException("fixture_move_next_failure");
            }
            finally { FinallyRan = true; }
        }
    }
    private static void Check(bool value, string reason)
    {
        assertions++;
        if (!value) throw new Exception(reason);
    }
    private static void Set(AutorunHost host, string field, object value)
    {
        var target = typeof(AutorunHost).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
        if (target == null) throw new MissingFieldException(typeof(AutorunHost).FullName, field);
        target.SetValue(host, value);
    }
    private static string Phase(AutorunHost host)
    {
        return typeof(AutorunHost).GetField("phase", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(host).ToString();
    }
    private static Dictionary<string, object> Result(string store)
    {
        string[] files = Directory.GetFiles(Path.Combine(store, "autorun"), "result-*.json");
        Check(files.Length == 1, "Expected exactly one host result");
        return Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(files[0]));
    }
    private static void AssertResult(string store, string status, bool final, bool cleaned)
    {
        var result = Result(store);
        Check((string)result["status"] == status, "Wrong status: " + result["status"] + ", expected " + status);
        Check((bool)result["final"] == final, "Wrong final flag");
        var outputs = (Dictionary<string, object>)result["outputs"];
        Check((bool)outputs["ownedWorkCleanupComplete"] == cleaned, "Wrong cleanup flag");
    }
    private static void RunCase(string root, string name, bool timeout, bool cleanupTimeout, bool throwOnResume, bool initiallyDone)
    {
        string store = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(store, "autorun"));
        MNBL.ProceduralWorld.SubModule.Store = store;
        CleanupDoubles.Reset();
        var fixture = new Fixture { DoneA = initiallyDone, DoneB = initiallyDone };
        AutorunProbes.Factory = context => fixture.Run(context, throwOnResume);
        TaleWorlds.ScreenSystem.ScreenManager.TopScreen = new TaleWorlds.MountAndBlade.MBInitialScreenBase();
        File.WriteAllText(Path.Combine(store, "autorun", "request.json"), Json.Serialize(new {
            requestId = name, probe = "fixture", quit = true, quitMethod = "exit_button", menuDelaySeconds = 0, probeTimeoutSeconds = 900
        }));
        AutorunHost.TryClaim(store);
        var host = AutorunHost.Current;
        Check(host != null, "Actual host failed to claim fixture request");
        host.Tick(0); // Menu detection.
        host.Tick(0); // Begin creates real ProbeContext and calls the injected iterator factory.
        Check(Phase(host) == "Running", "Host did not begin");
        host.Tick(0); // Suspend the real C# iterator inside try/finally with both work registrations active.
        Check(fixture.Moves == 1 && !fixture.FinallyRan, "Iterator was not suspended inside finally scope");
        if (timeout) Set(host, "probeStartAt", -10000.0);
        host.Tick(0); // Actual Step: timeout, completion, or MoveNext exception.
        Check(fixture.FinallyRan, "Finish failed to execute suspended iterator finally");
        Check(fixture.Moves == (timeout ? 1 : 2), "Timeout incorrectly resumed iterator");
        string expectedStatus = timeout ? "probe_timeout" : throwOnResume ? "error" : "ok";
        if (!initiallyDone)
        {
            Check(fixture.CancelA == 1 && fixture.CancelB == 1, "Owned work was not cancelled exactly once");
            Check(Phase(host) == "Cleaning", "Pending owned work did not enter Cleaning");
            AssertResult(store, expectedStatus, false, false);
            Check(CleanupDoubles.Total == 0, "Quit requested before cleanup");
            if (cleanupTimeout)
            {
                Set(host, "cleanupAt", -1000.0); // No real 30-second wait and no production clock changes.
                host.Tick(0);
                expectedStatus = "cleanup_timeout";
                AssertResult(store, expectedStatus, false, false);
                Check(Phase(host) == "Cleaning" && CleanupDoubles.Total == 0, "Cleanup deadline finalized or quit early");
                Check(((string)Result(store)["error"]).Contains("automatic quit is withheld"), "Missing cleanup timeout explanation");
                host.Tick(0);
                Check(CleanupDoubles.Total == 0, "Repeated overdue tick quit before cleanup");
            }
            fixture.DoneA = true;
            host.Tick(0);
            Check(Phase(host) == "Cleaning", "Host accepted only partial owned-work completion");
            AssertResult(store, expectedStatus, false, false);
            Check(CleanupDoubles.Total == 0, "Quit during partial cleanup");
            fixture.DoneB = true;
            host.Tick(0);
        }
        else Check(fixture.CancelA == 0 && fixture.CancelB == 0, "Already finished work was cancelled");
        AssertResult(store, expectedStatus, true, true);
        Check(Phase(host) == "Quitting", "Host did not enter quit phase after cleanup");
        Check(CleanupDoubles.ExitButtonCalls == 1 && CleanupDoubles.QuitCalls == 0 && CleanupDoubles.DelayedCalls == 0,
            "Unexpected quit method/count");
        if (throwOnResume && !cleanupTimeout)
            Check(((string)Result(store)["error"]).Contains("fixture_move_next_failure"), "Original iterator exception lost");
        host.Tick(0);
        Check(CleanupDoubles.Total == 1, "Immediate subsequent tick repeated quit");
        Check(fixture.CancelA == (initiallyDone ? 0 : 1) && fixture.CancelB == (initiallyDone ? 0 : 1), "Cleanup ticks repeated cancellation");
        Cases.Add(new { name, status = expectedStatus, final = true, ownedWorkCleanupComplete = true, quitCalls = CleanupDoubles.Total });
        Console.WriteLine("PASS " + name + " -> " + expectedStatus);
    }
    private static int Main(string[] args)
    {
        try
        {
            string root = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(root);
            RunCase(root, "probe-timeout-cleanup", true, false, false, false);
            RunCase(root, "cleanup-timeout-withholds-quit", true, true, false, false);
            RunCase(root, "natural-completion-pending-work", false, false, false, false);
            RunCase(root, "natural-completion-finished-work", false, false, false, true);
            RunCase(root, "iterator-exception-cleanup", false, false, true, false);
            File.WriteAllText(Path.Combine(root, "checks.json"), Json.Serialize(new {
                schema = "mnbl.autorun-cleanup-managed-tests", passed = true, assertions, cases = Cases,
                actualSources = new[] { "AutorunHost.cs", "ProbeContext.cs" }, gameApisDoubled = true,
                gameOrNativeRuntimeClaim = false
            }));
            Console.WriteLine("PASS " + assertions + " assertions; actual host/context sources, managed doubles only.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
