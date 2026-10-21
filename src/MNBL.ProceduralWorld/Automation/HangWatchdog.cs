using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-247 (acc4 and acc5: four engine-thread blocks, CPU flat, killed by the orchestrator after 5 minutes, results/MMR-247-H.md): a hung
    // engine thread runs no probe code, so the progress log only names the last stage before the block, never the call that blocked. While a
    // harness probe runs, the engine thread beats this watchdog every application tick (AutorunHost.Tick); a background thread that sees no beat
    // for ThresholdSeconds writes, once per stall, hang-<pid>-<k>.txt into the autorun folder (the stall, the probe's last stage, the MNBL
    // section the engine thread was last in (EngineMarks), the engine thread's managed stack when the runtime can walk it, the process CPU
    // time), then hang-<pid>-<k>.dmp (MiniDumpWriteDump: every thread's native stack, modules, handles; no heap). When the engine ticks again
    // the stall's length is appended ("resumed": a long engine-thread task such as a navigation cache build, not a hang). At most MaxReports
    // per process. Pure .NET (Win32 calls only), so tools/Test-HangWatchdog.ps1 runs it offline on a thread it blocks.
    internal sealed class HangWatchdog : IDisposable
    {
        public readonly string Directory;
        public readonly int ProcessId;
        public readonly double ThresholdSeconds;
        public readonly int MaxReports;
        public bool WriteDumps = true;
        private readonly Func<string> describe;
        private readonly Func<double> clock;
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly object gate = new object();
        private Thread engine;
        private uint engineNativeId;
        private double lastBeat = -1;           // clock seconds of the last beat (-1: none yet)
        private DateTime lastBeatUtc;
        private bool stalled;
        private double stallFrom;
        private string lastReport;
        private Thread poller;
        private volatile bool stopping;
        public int Reports { get; private set; }
        public string LastReportPath { get { return lastReport; } }

        public HangWatchdog(string directory, int processId, double thresholdSeconds, int maxReports, Func<string> describe, Func<double> clock = null)
        {
            Directory = directory; ProcessId = processId; ThresholdSeconds = thresholdSeconds; MaxReports = maxReports;
            this.describe = describe; this.clock = clock ?? (() => watch.Elapsed.TotalSeconds);
        }

        // The engine thread, every application tick. The first beat names the thread watched.
        public void Beat()
        {
            if (engine == null) { engine = Thread.CurrentThread; engineNativeId = GetCurrentThreadId(); }
            Volatile.Write(ref lastBeat, clock());
            lastBeatUtc = DateTime.UtcNow;
        }

        public void Start(int pollMilliseconds = 2000)
        {
            if (poller != null) return;
            poller = new Thread(() => { while (!stopping) { try { Poll(); } catch (Exception) { } Thread.Sleep(pollMilliseconds); } }) { IsBackground = true, Name = "MNBL hang watchdog" };
            poller.Start();
        }

        public void Dispose() { stopping = true; }

        // One check (the poller's loop body; tests call it with their own clock).
        public void Poll()
        {
            lock (gate)
            {
                double beat = Volatile.Read(ref lastBeat);
                if (beat < 0) return;
                double stale = clock() - beat;
                if (!stalled && stale >= ThresholdSeconds)
                {
                    stalled = true; stallFrom = beat;
                    if (Reports < MaxReports) { Reports++; Report(Reports, stale); }
                }
                else if (stalled && stale < ThresholdSeconds)
                {
                    stalled = false;
                    if (lastReport != null) Append(lastReport, "resumed " + Utc() + ": the engine thread ticked again after " + (beat - stallFrom).ToString("0.0", CultureInfo.InvariantCulture) + " s (a long engine-thread task, not a hang)\n");
                }
            }
        }

        private void Report(int k, double stale)
        {
            string stem = Path.Combine(Directory, "hang-" + ProcessId + "-" + k);
            string text = stem + ".txt", dump = stem + ".dmp";
            lastReport = text;
            var b = new StringBuilder();
            b.Append("MNBL hang capture ").Append(k).Append(" pid ").Append(ProcessId).Append(" at ").Append(Utc()).Append('\n');
            b.Append("no engine tick for ").Append(stale.ToString("0.0", CultureInfo.InvariantCulture)).Append(" s (last tick ").Append(lastBeatUtc.ToString("O", CultureInfo.InvariantCulture)).Append(")\n");
            b.Append("engine thread: managed ").Append(engine == null ? "?" : engine.ManagedThreadId.ToString(CultureInfo.InvariantCulture)).Append(", native ").Append(engineNativeId).Append(" (0x").Append(engineNativeId.ToString("x", CultureInfo.InvariantCulture)).Append(")\n");
            try { var p = Process.GetCurrentProcess(); b.Append("process cpu ").Append(p.TotalProcessorTime.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(" s, threads ").Append(p.Threads.Count).Append('\n'); }
            catch (Exception e) { b.Append("process: ").Append(e.GetType().Name).Append('\n'); }
            string state = null;
            try { state = describe == null ? null : describe(); } catch (Exception e) { state = "describe failed: " + e.GetType().Name + ": " + e.Message; }
            if (state != null) b.Append(state).Append('\n');
            b.Append("MNBL section on the engine thread: ").Append(EngineMarks.Describe()).Append('\n');
            // Written before the stack walk and the dump, either of which could block on a lock the hung thread holds.
            Write(text, b.ToString());
            Append(text, "engine thread managed stack:\n" + EngineStack() + "\n");
            if (!WriteDumps) return;
            Append(text, "dump " + dump + ": " + Dump(dump) + "\n");
        }

        // The engine thread's managed frames, down to the managed call a native block sits under (Test-HangWatchdog: a WaitForSingleObject
        // P/Invoke and a WaitOne are both named). Suspend is requested for the walk and always undone; a thread in native code never stops for
        // it. A block entered from the engine's own frame loop has no managed frames (recorded so); the dump has every native stack.
        private string EngineStack()
        {
            var t = engine;
            if (t == null) return "(no engine thread)";
            if (t == Thread.CurrentThread) return "(not walked: the check runs on the engine thread itself)";
            bool suspended = false;
            try
            {
                // A thread blocked in native code or a wait stays SuspendRequested (preemptive), and the runtime walks it as it stands.
#pragma warning disable 618
                t.Suspend(); suspended = true;
                string state = SafeState(t);
                string frames = new StackTrace(t, false).ToString();
#pragma warning restore 618
                return "(state " + state + ")\n" + (frames.Trim().Length == 0 ? "(no managed frames: the thread is in native code entered without a managed caller)" : frames);
            }
            catch (Exception e) { return "(not walked: " + e.GetType().Name + ": " + e.Message + "; state " + SafeState(t) + ")"; }
            finally
            {
#pragma warning disable 618
                if (suspended) { try { t.Resume(); } catch (Exception) { } }
#pragma warning restore 618
            }
        }

        private static string SafeState(Thread t) { try { return t.ThreadState.ToString(); } catch (Exception) { return "?"; } }

        // MiniDumpNormal | HandleData | UnloadedModules | FullMemoryInfo | ThreadInfo: thread stacks and contexts, module list, handles,
        // the memory map; no heap (a campaign's working set is gigabytes).
        private const uint DumpType = 0x00000000 | 0x00000004 | 0x00000020 | 0x00000800 | 0x00001000;

        internal static string Dump(string path)
        {
            try
            {
                using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                using (var process = Process.GetCurrentProcess())
                {
                    if (!MiniDumpWriteDump(process.Handle, (uint)process.Id, file.SafeFileHandle, DumpType, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                        return "failed (win32 " + Marshal.GetLastWin32Error() + ")";
                    file.Flush(true);
                    return file.Length + " bytes";
                }
            }
            catch (Exception e) { return "failed (" + e.GetType().Name + ": " + e.Message + ")"; }
        }

        private static string Utc() { return DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture); }

        private static void Write(string path, string text)
        {
            try { File.WriteAllText(path, text, new UTF8Encoding(false)); } catch (Exception) { }
        }

        private static void Append(string path, string text)
        {
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            }
            catch (Exception) { }
        }

        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        [DllImport("dbghelp.dll", SetLastError = true)]
        private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, SafeFileHandle file, uint dumpType, IntPtr exception, IntPtr userStream, IntPtr callback);
    }

    // MMR-247: which MNBL section the engine thread is in (entered and left on the engine thread only; read by the watchdog's thread). A hang
    // capture reports it, so a block inside one of MNBL's engine callbacks (a probe step, the atmosphere tick, the camera's image refresh) is
    // told apart from one in the engine's own frame work.
    // No allocation per mark (sections are string literals); a torn read of name and time only blurs a diagnostic.
    internal static class EngineMarks
    {
        private static volatile string name = "none yet";
        private static volatile bool inside;
        private static long at = Stopwatch.GetTimestamp();
        public static void Enter(string section) { Interlocked.Exchange(ref at, Stopwatch.GetTimestamp()); name = section; inside = true; }
        public static void Leave() { Interlocked.Exchange(ref at, Stopwatch.GetTimestamp()); inside = false; }
        public static string Describe()
        {
            double seconds = (Stopwatch.GetTimestamp() - Interlocked.Read(ref at)) / (double)Stopwatch.Frequency;
            return (inside ? "inside " + name : "outside MNBL's marked sections (last left: " + name + ")") + " for " + seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        }
    }
}
