using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using MNBL.ProceduralWorld.Automation;

// MMR-247: the probe's engine-thread hang capture (Automation/HangWatchdog.cs) without the game: HangWatchdogTests OUT_DIRECTORY
//   * the stall rule on a test clock: nothing before the threshold, one capture per stall, "resumed" with the stall's length when the beats
//     come back, at most MaxReports;
//   * real captures of a thread standing in for the engine thread: blocked in a managed wait and blocked in native code (WaitForSingleObject),
//     each with the text written (the stall, the probe state, the MNBL section mark, the stack or why it was not walked) and the dump a
//     minidump; the blocked thread runs on when released (the walk's suspension is always undone) and its next beats mark the stall resumed.
internal static class HangWatchdogTests
{
    private static int checks, failures;
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; Console.WriteLine((condition ? "ok    " : "FAIL  ") + label); }

    private static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("usage: HangWatchdogTests OUT_DIRECTORY"); return 2; }
        string dir = args[0];
        Directory.CreateDirectory(dir);
        try
        {
            StallRule(Path.Combine(dir, "rule"));
            Blocked(Path.Combine(dir, "managed"), false);
            Blocked(Path.Combine(dir, "native"), true);
        }
        catch (Exception e) { Check(false, "unexpected " + e); }
        Console.WriteLine(checks - failures + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static void StallRule(string dir)
    {
        Directory.CreateDirectory(dir);
        double now = 0;
        var w = new HangWatchdog(dir, 4242, 60, 2, () => "probe test, last stage x", () => now) { WriteDumps = false };
        w.Poll();
        Check(w.Reports == 0, "no capture before the first beat");
        w.Beat(); now = 59; w.Poll();
        Check(w.Reports == 0, "no capture 59 s after the last beat (threshold 60 s)");
        now = 61; w.Poll(); now = 90; w.Poll();
        Check(w.Reports == 1 && File.Exists(Path.Combine(dir, "hang-4242-1.txt")), "one capture for a stall past the threshold, however long it lasts");
        string text = File.ReadAllText(Path.Combine(dir, "hang-4242-1.txt"));
        Check(text.Contains("no engine tick for 61.0 s") && text.Contains("probe test, last stage x") && text.Contains("MNBL section on the engine thread: "), "the capture names the stall, the probe state and the engine thread's MNBL section");
        Check(text.Contains("(not walked: the check runs on the engine thread itself)"), "the stack walk never suspends the thread that runs the check");
        now = 100; w.Beat(); now = 101; w.Poll();
        text = File.ReadAllText(Path.Combine(dir, "hang-4242-1.txt"));
        Check(text.Contains("resumed ") && text.Contains("after 100.0 s"), "beats after a stall append its length (resumed after 100.0 s)");
        now = 200; w.Poll(); now = 210; w.Beat(); now = 211; w.Poll(); now = 300; w.Poll();
        Check(w.Reports == 2 && File.Exists(Path.Combine(dir, "hang-4242-2.txt")), "a second stall gets its own capture");
        now = 400; w.Poll();
        Check(w.Reports == 2 && !File.Exists(Path.Combine(dir, "hang-4242-3.txt")), "no capture past MaxReports");
    }

    // A thread standing in for the engine thread beats, marks a section, then blocks (a managed wait, or native code) until released.
    private static void Blocked(string dir, bool native)
    {
        Directory.CreateDirectory(dir);
        string kind = native ? "native" : "managed";
        var release = new ManualResetEvent(false);
        IntPtr nativeEvent = CreateEvent(IntPtr.Zero, true, false, null);
        bool ranOn = false;
        HangWatchdog w = null;
        var ready = new ManualResetEvent(false);
        var engine = new Thread(() =>
        {
            for (int i = 0; i < 5; i++) { w.Beat(); Thread.Sleep(20); }
            EngineMarks.Enter("the test's blocked section");
            ready.Set();
            if (native) BlockedInNativeWait(nativeEvent); else BlockedInManagedWait(release);
            EngineMarks.Leave();
            ranOn = true;
            for (int i = 0; i < 20; i++) { w.Beat(); Thread.Sleep(50); }
        }) { IsBackground = true, Name = "test engine thread (" + kind + ")" };
        w = new HangWatchdog(dir, 5151, 0.5, 2, () => "probe test " + kind);
        engine.Start();
        ready.WaitOne(5000);
        w.Start(100);
        string text = Path.Combine(dir, "hang-5151-1.txt"), dump = Path.Combine(dir, "hang-5151-1.dmp");
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until && !(File.Exists(text) && File.ReadAllText(text).Contains("dump "))) Thread.Sleep(100);
        string body = File.Exists(text) ? File.ReadAllText(text) : "";
        Check(body.Contains("no engine tick for") && body.Contains("probe test " + kind), kind + ": the capture is written while the thread is blocked");
        Check(body.Contains("MNBL section on the engine thread: inside the test's blocked section"), kind + ": it names the MNBL section the thread blocked in");
        string stack = Between(body, "engine thread managed stack:\n", "\ndump ");
        string method = native ? "BlockedInNativeWait" : "BlockedInManagedWait";
        Check(stack.Contains("HangWatchdogTests." + method), kind + ": the blocked thread's managed stack is walked down to the frame that blocked (" + method + ")");
        Console.WriteLine("      " + kind + " stack: " + String.Join(" | ", stack.Trim().Split('\n'), 0, Math.Min(4, stack.Trim().Split('\n').Length)).Replace("\r", ""));
        bool minidump = false; long size = 0;
        if (File.Exists(dump)) { var head = new byte[4]; using (var f = File.OpenRead(dump)) { size = f.Length; f.Read(head, 0, 4); } minidump = Encoding.ASCII.GetString(head) == "MDMP"; }
        Check(minidump && size > 0 && body.Contains(size + " bytes"), kind + ": the dump is a minidump (MDMP, " + size + " bytes) and its size is recorded");
        if (native) SetEvent(nativeEvent); else release.Set();
        bool joined = engine.Join(10000);
        Check(joined && ranOn, kind + ": the released thread runs on (the walk's suspension was undone)");
        until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until && !File.ReadAllText(text).Contains("resumed ")) Thread.Sleep(100);
        Check(File.ReadAllText(text).Contains("resumed "), kind + ": its beats after the release mark the stall resumed");
        w.Dispose();
        CloseHandle(nativeEvent);
    }

    private static void BlockedInManagedWait(ManualResetEvent release) { release.WaitOne(); }
    private static void BlockedInNativeWait(IntPtr handle) { WaitForSingleObject(handle, 0xFFFFFFFF); }

    private static string Between(string s, string from, string to)
    {
        int a = s.IndexOf(from, StringComparison.Ordinal); if (a < 0) return "";
        a += from.Length; int b = s.IndexOf(to, a, StringComparison.Ordinal);
        return b < 0 ? s.Substring(a) : s.Substring(a, b - a);
    }
    private static string FirstLine(string s) { s = s.Trim(); int n = s.IndexOf('\n'); return n < 0 ? s : s.Substring(0, n).Trim(); }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string name);
    [DllImport("kernel32.dll")] private static extern bool SetEvent(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
