using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

// MMR-247 (K): the process tools/Test-HangDump.ps1 dumps and analyses (tools/hangdump/HangDump.cs). One scenario per run, then it stays
// blocked until the harness kills it. Prints "ready <scenario> <os thread id of the thread to look at>" once the block is in place. A heartbeat
// thread rewrites <heartbeat file> every 100 ms (it allocates), so the harness sees when the whole managed runtime has stopped (as the game's
// in-process watchdog stopped in acc5lab).
//   deadlock    two threads take LockAlpha and LockBeta in opposite orders (Monitor deadlock);
//   nativewait  a thread blocked in a WaitForSingleObject P/Invoke on an event nobody sets;
//   gcblock     a thread spinning in managed code (cooperative GC mode) is suspended with SuspendThread, then another thread calls
//               GC.Collect: the runtime suspension never completes (the GC waits for the spinner), and every other managed thread stops;
//   exception   a thrown InvalidOperationException whose exception filter blocks (an exception in flight, mid-dispatch).
internal static class HangDumpTarget
{
    private sealed class LockAlpha { }
    private sealed class LockBeta { }
    private static readonly LockAlpha alpha = new LockAlpha();
    private static readonly LockBeta beta = new LockBeta();
    private static volatile uint watched;
    private static long spins;

    private static int Main(string[] args)
    {
        string scenario = args[0];
        string heartbeat = args.Length > 1 ? args[1] : null;
        if (heartbeat != null) Start("heartbeat", () => { for (long n = 0; ; n++) { File.WriteAllText(heartbeat, n.ToString() + " " + DateTime.UtcNow.Ticks); Thread.Sleep(100); } });
        switch (scenario)
        {
            case "deadlock":
                var a = Start("alpha-then-beta", AlphaThenBeta);
                var b = Start("beta-then-alpha", BetaThenAlpha);
                while (!(Blocked(a) && Blocked(b))) Thread.Sleep(50);
                Thread.Sleep(500);
                Ready(scenario);
                break;
            case "nativewait":
                Start("native-waiter", NativeWait);
                while (watched == 0) Thread.Sleep(20);
                Thread.Sleep(300);
                Ready(scenario);
                break;
            case "gcblock":
                Start("spinner", Spin);
                while (watched == 0 || Interlocked.Read(ref spins) < 1000) Thread.Sleep(20);
                IntPtr h = OpenThread(0x0002 | 0x0008 | 0x0040, false, watched);       // SUSPEND_RESUME | GET_CONTEXT | QUERY_INFORMATION
                if (h == IntPtr.Zero || SuspendThread(h) == uint.MaxValue) { Console.WriteLine("error: could not suspend the spinner"); return 1; }
                Ready(scenario);
                Thread.Sleep(200);
                Start("collector", () => GC.Collect());
                break;
            case "exception":
                Start("thrower", Throw);
                while (watched == 0) Thread.Sleep(20);
                Thread.Sleep(300);
                Ready(scenario);
                break;
            default:
                Console.WriteLine("error: unknown scenario " + scenario);
                return 2;
        }
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    private static Thread Start(string name, ThreadStart body) { var t = new Thread(body) { Name = name, IsBackground = true }; t.Start(); return t; }
    private static bool Blocked(Thread t) { return (t.ThreadState & ThreadState.WaitSleepJoin) != 0; }
    private static void Ready(string scenario) { Console.WriteLine("ready " + scenario + " " + watched); Console.Out.Flush(); }

    private static void AlphaThenBeta() { lock (alpha) { watched = GetCurrentThreadId(); Thread.Sleep(300); lock (beta) { Console.WriteLine("unexpected: beta taken"); } } }
    private static void BetaThenAlpha() { lock (beta) { Thread.Sleep(300); lock (alpha) { Console.WriteLine("unexpected: alpha taken"); } } }

    private static void NativeWait()
    {
        IntPtr never = CreateEvent(IntPtr.Zero, true, false, null);
        watched = GetCurrentThreadId();
        uint woke = WaitForSingleObject(never, 0xffffffff);
        Console.WriteLine("unexpected: the wait ended " + woke);       // not a tail call, so this frame stays on the stack
    }

    private static void Spin() { watched = GetCurrentThreadId(); for (;;) Step(); }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Step() { Interlocked.Increment(ref spins); }

    private static void Throw()
    {
        try { throw new InvalidOperationException("MMR-247 analyzer test: an exception in flight"); }
        catch (InvalidOperationException) when (Hold()) { }
    }
    private static bool Hold() { watched = GetCurrentThreadId(); Thread.Sleep(Timeout.Infinite); return true; }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SuspendThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}
