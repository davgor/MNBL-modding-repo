using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

// MMR-247 (K): reads a full memory dump of a hung .NET Framework process (the game, Bannerlord on the desktop CLR) without an installed
// debugger. It hosts the debugger engine Windows ships (C:\Windows\System32\dbgeng.dll) and loads the SOS extension the .NET Framework ships
// next to its clr.dll, then runs the debugger's own commands and reads their output:
//   ~                    every thread, its OS id and suspend count;
//   kn / !ip2md          every thread's native stack, its JIT frames named by SOS;
//   !threads [-special]  the managed threads, their GC mode (cooperative = the GC must wait for them), lock counts, exceptions, and the
//                        runtime's special threads (GC, SuspendEE: a garbage collection or a runtime suspension in progress);
//   !syncblk, !dso       the Monitor locks held (owner, waiters) and, per waiting thread, which held locks are on its stack;
//   !clrstack            every managed stack.
// Then it names what it sees: Monitor deadlock cycles, a GC or runtime suspension in progress (and the cooperative threads it waits for),
// exceptions in flight, threads suspended beyond the rest, and the engine thread's native top frames. Usage:
//   HangDump.exe <dump> [--out <report.txt>] [--json <summary.json>] [--engine <os thread id, hex 0x.. or decimal>] [--symbols <sympath>]
//                [--sos <sos.dll>] [--dbgeng <dbgeng.dll>] [--frames <n>] [--engine-frame <module+0xRVA>]
// Symbols: none by default (no network: exports only, offsets into each module, which REA reads). --symbols takes a debugger symbol path,
// e.g. srv*<cache>*https://msdl.microsoft.com/download/symbols, to name ntdll/clr frames (the GC's own functions among them).
// tools/Analyze-HangDump.ps1 builds and runs it; tools/Test-HangDump.ps1 tests it on dumps of a small process it deadlocks.
namespace MNBL.Tools.HangDump
{
    [ComImport, Guid("4bf58045-d654-4c40-b0af-683090f356dc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDebugOutputCallbacks
    {
        [PreserveSig] int Output(uint mask, [MarshalAs(UnmanagedType.LPStr)] string text);
    }

    // Slot order from the Windows SDK's DbgEng.h; the _NN placeholders keep the vtable positions of methods this tool never calls.
    [ComImport, Guid("27fe5639-8407-4f47-8364-ee118fb08ac8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDebugClient
    {
        void _03(); void _04(); void _05(); void _06(); void _07(); void _08(); void _09(); void _10(); void _11(); void _12(); void _13();
        void _14(); void _15(); void _16(); void _17(); void _18();
        [PreserveSig] int OpenDumpFile([MarshalAs(UnmanagedType.LPStr)] string dumpFile);                               // 19
        void _20(); void _21(); void _22(); void _23(); void _24(); void _25();
        [PreserveSig] int EndSession(uint flags);                                                                         // 26
        void _27(); void _28(); void _29(); void _30(); void _31(); void _32(); void _33();
        [PreserveSig] int SetOutputCallbacks([MarshalAs(UnmanagedType.Interface)] IDebugOutputCallbacks callbacks);       // 34
        void _35();
        [PreserveSig] int SetOutputMask(uint mask);                                                                       // 36
    }

    [ComImport, Guid("5182e668-105e-416e-ad92-24ef800424ba"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDebugControl
    {
        void _03(); void _04(); void _05(); void _06(); void _07(); void _08(); void _09(); void _10(); void _11(); void _12(); void _13();
        void _14(); void _15(); void _16(); void _17(); void _18(); void _19(); void _20(); void _21(); void _22(); void _23(); void _24();
        void _25(); void _26(); void _27(); void _28(); void _29(); void _30(); void _31(); void _32(); void _33(); void _34(); void _35();
        void _36(); void _37(); void _38(); void _39(); void _40(); void _41(); void _42(); void _43(); void _44(); void _45(); void _46();
        void _47(); void _48(); void _49(); void _50(); void _51(); void _52(); void _53();
        [PreserveSig] int AddEngineOptions(uint options);                                                                 // 54
        void _55();
        [PreserveSig] int SetEngineOptions(uint options);                                                                 // 56
        void _57(); void _58(); void _59(); void _60(); void _61(); void _62(); void _63(); void _64(); void _65();
        [PreserveSig] int Execute(uint outputControl, [MarshalAs(UnmanagedType.LPStr)] string command, uint flags);      // 66
        void _67(); void _68(); void _69(); void _70(); void _71(); void _72(); void _73(); void _74(); void _75(); void _76(); void _77();
        void _78(); void _79(); void _80(); void _81(); void _82(); void _83(); void _84(); void _85(); void _86(); void _87(); void _88();
        void _89(); void _90(); void _91(); void _92();
        [PreserveSig] int WaitForEvent(uint flags, uint timeout);                                                        // 93
    }

    public sealed class Sink : IDebugOutputCallbacks
    {
        public readonly StringBuilder Text = new StringBuilder();
        public int Output(uint mask, string text) { Text.Append(text); return 0; }
    }

    internal sealed class Frame
    {
        public int Index; public string Sp, Ret, Site; public string Managed;     // Managed: SOS's name for a JIT frame (!ip2md)
        public ulong Ip; public string Rva;                                        // the frame's instruction pointer as module+0xRVA
        public bool Trusted;                                                       // Site names the function (PDB symbols, or an export within 0x1000)
        public string Name { get { return Managed != null ? "[managed] " + Managed : Trusted ? Site + (Rva != null ? "  [" + Rva + "]" : "") : Rva != null ? Rva + "  (" + Site + ")" : Site; } }
    }

    internal sealed class Module { public ulong Start, End; public string Name, Symbols, File; }

    internal sealed class ThreadRow
    {
        public int Index; public uint Osid; public int Suspend; public string Teb, Name;
        public int? ManagedId; public string ThreadObj, State, GcMode, Domain, Apt, Exception; public int LockCount;
        public readonly List<string> Special = new List<string>();
        public readonly List<Frame> Native = new List<Frame>();
        public readonly List<string> Managed = new List<string>();
        public string ManagedNote;                                           // why no managed frames (SOS's own words)
        public readonly List<Tuple<string, string>> StackObjects = new List<Tuple<string, string>>(); // !dso: address, type
        public string NativeTop { get { var f = Native.FirstOrDefault(); return f == null ? "(no frames)" : f.Name; } }
        public string ManagedTop { get { return Managed.FirstOrDefault(); } }
        public string Label { get { return "thread " + Index + " (os 0x" + Osid.ToString("x", CultureInfo.InvariantCulture) + (ManagedId.HasValue ? ", managed " + ManagedId : "") + ")"; } }
    }

    internal sealed class SyncBlock
    {
        public int Index; public string Address; public int MonitorHeld, Recursion; public string OwnerThreadObj; public uint OwnerOsid; public int? OwnerIndex;
        public string Object, Type;
        public int Waiters { get { return Math.Max(0, (MonitorHeld - (OwnerOsid != 0 ? 1 : 0)) / 2); } }
    }

    internal static class Program
    {
        private static IDebugClient client;
        private static IDebugControl control;
        private static readonly Sink sink = new Sink();
        private static readonly StringBuilder raw = new StringBuilder();
        private static readonly Regex Noise = new Regex(@"^\*\*\* (WARNING|ERROR): |^\*{10,}|^\*\s+\*?$|^\*\s+Symbols can not be loaded|^\*\s+Either you specified|^\*\s+Use \.symfix|^\*\s+\.sympath|^\*\s+_NT_SYMBOL|^\*\s+is not set|^\*\s+environment|^\*\s+Symbol", RegexOptions.Compiled);

        private static int Main(string[] args)
        {
            if (args.Length < 1 || args[0].StartsWith("--", StringComparison.Ordinal)) { Console.Error.WriteLine("usage: HangDump.exe <dump> [--out report.txt] [--json summary.json] [--engine <osid>] [--symbols <sympath>] [--sos <sos.dll>] [--dbgeng <dbgeng.dll>] [--frames <n>]"); return 2; }
            string dump = Path.GetFullPath(args[0]);
            var opt = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i + 1 < args.Length; i += 2) opt[args[i]] = args[i + 1];
            string Opt(string k, string d) { string v; return opt.TryGetValue(k, out v) ? v : d; }
            int frames = int.Parse(Opt("--frames", "40"), CultureInfo.InvariantCulture);
            if (!File.Exists(dump)) { Console.Error.WriteLine("no dump at " + dump); return 2; }
            string dbgeng = Opt("--dbgeng", Path.Combine(Environment.SystemDirectory, "dbgeng.dll"));
            if (LoadLibrary(dbgeng) == IntPtr.Zero) { Console.Error.WriteLine("dbgeng.dll did not load from " + dbgeng + " (win32 " + Marshal.GetLastWin32Error() + ")"); return 3; }
            string sympath = Opt("--symbols", null);
            var stopwatch = Stopwatch.StartNew();

            object o; Guid iid = typeof(IDebugClient).GUID;
            int hr = DebugCreate(ref iid, out o);
            if (hr != 0) { Console.Error.WriteLine("DebugCreate failed 0x" + hr.ToString("x8")); return 3; }
            client = (IDebugClient)o; control = (IDebugControl)o;
            client.SetOutputCallbacks(sink);
            client.SetOutputMask(0x1 | 0x2 | 0x4);                                         // normal, error, warning
            // No network unless a symbol path is given: exports only, every frame still names its module and offset.
            if (sympath == null) control.AddEngineOptions(0x8);                             // DEBUG_ENGOPT_DISALLOW_NETWORK_PATHS
            Run(".sympath " + (sympath ?? Path.Combine(Path.GetDirectoryName(dump), "no-symbols")));
            hr = client.OpenDumpFile(dump);
            if (hr != 0) { Console.Error.WriteLine("OpenDumpFile failed 0x" + hr.ToString("x8")); return 4; }
            hr = control.WaitForEvent(0, 0xffffffff);
            if (hr != 0) { Console.Error.WriteLine("WaitForEvent failed 0x" + hr.ToString("x8")); return 4; }

            string target = Raw("||", "||");
            string version = Raw("vertarget", "vertarget");
            string modules = Run("lm");
            string threadList = Raw("~", "~");
            var threads = ParseThreads(threadList);

            // SOS: from the dumped process's own clr.dll folder (same machine: the DAC beside it matches), else the given one.
            string sos = Opt("--sos", null);
            bool desktop = Regex.IsMatch(modules, @"^\S+\s+\S+\s+clr\s", RegexOptions.Multiline);
            string sosLoad = sos != null ? Run(".load " + sos) : desktop ? Run(".loadby sos clr") : "no clr.dll in the dump (not a desktop CLR process)";
            raw.Append("== sos load\n").Append(Clean(sosLoad)).Append('\n');
            bool sosOk = desktop || sos != null;
            string eeversion = sosOk ? Raw("!eeversion", "!eeversion") : "";
            string mthreads = sosOk ? Raw("!threads", "!threads") : "";
            string special = sosOk ? Raw("!threads -special", "!threads -special") : "";
            string syncblk = sosOk ? Raw("!syncblk", "!syncblk") : "";
            ParseManagedThreads(mthreads, threads);
            ParseSpecial(special, threads);
            var blocks = ParseSyncBlocks(syncblk, threads);
            string runaway = Run("!runaway 7");
            bool times = runaway.Contains("User Mode Time");
            if (times) raw.Append("== !runaway 7\n").Append(Clean(runaway)).Append('\n');

            var stacks = new StringBuilder();
            foreach (var t in threads)
            {
                Run("~" + t.Index + "s");
                string k = Run("kn " + frames);
                t.Native.AddRange(ParseFrames(k));
                var rip = Regex.Match(Run("r rip"), @"rip=([0-9a-f`]+)", RegexOptions.IgnoreCase);
                for (int i = 0; i < t.Native.Count; i++)
                    t.Native[i].Ip = i == 0 ? (rip.Success ? Hex(rip.Groups[1].Value) : 0) : Hex(t.Native[i - 1].Ret);
                if (sosOk)
                {
                    foreach (var f in t.Native.Where(f => f.Site.StartsWith("0x", StringComparison.Ordinal) || Regex.IsMatch(f.Site, @"^\w+_ni(\+|!)")))
                    {
                        var m = Regex.Match(Run("!ip2md " + (f.Site.StartsWith("0x", StringComparison.Ordinal) ? f.Site : "0x" + f.Ip.ToString("x", CultureInfo.InvariantCulture))), @"Method Name:\s*(.+)");
                        if (m.Success) f.Managed = m.Groups[1].Value.Trim();
                    }
                    string cs = Run("!clrstack");
                    ParseClrStack(cs, t);
                    if (WaitsOnMonitor(t) || t.LockCount > 0) ParseDso(Run("!dso"), t);
                }
            }

            // Frames as module+RVA (what REA reads), named only where the name can be trusted: a module with PDB symbols, or an export within
            // 0x1000 of the address (with exports only, a far offset is just the nearest export, e.g. TaleWorlds_Native!WotsMainNativeCoreCLR+0x3d81ce).
            string lmAfter = Run("lmo");
            raw.Append("== lmo (after the stacks: each module's symbol state)\n").Append(Clean(lmAfter)).Append('\n');
            var mods = ParseModules(lmAfter);
            foreach (var f in threads.SelectMany(t => t.Native))
            {
                var mod = mods.FirstOrDefault(m => f.Ip >= m.Start && f.Ip < m.End);
                if (mod != null) f.Rva = mod.Name + "+0x" + (f.Ip - mod.Start).ToString("x", CultureInfo.InvariantCulture);
                var named = Regex.Match(f.Site, @"^(\w+)!.+?(?:\+0x([0-9a-f]+))?$", RegexOptions.IgnoreCase);
                bool pdb = mod != null && mod.Symbols != null && mod.Symbols.Contains("pdb");
                f.Trusted = named.Success && (pdb || !named.Groups[2].Success || ulong.Parse(named.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) < 0x1000);
            }
            foreach (var m in mods.Where(m => m.Name.StartsWith("TaleWorlds_Native", StringComparison.Ordinal) || m.Name == "clr")) Raw("lmvm " + m.Name, "lmvm " + m.Name);
            foreach (var t in threads)
            {
                stacks.Append("---- ").Append(t.Label).Append(" suspend ").Append(t.Suspend).Append(t.GcMode != null ? ", " + t.GcMode + " GC" : "")
                      .Append(t.Special.Count > 0 ? ", " + string.Join(" ", t.Special) : "").Append(t.Name != null ? ", \"" + t.Name + "\"" : "").Append('\n');
                stacks.Append("native:\n").Append(string.Join("\n", t.Native.Select(f => "  " + f.Index.ToString("x2") + " " + f.Name))).Append('\n');
                if (sosOk) stacks.Append("managed:\n").Append(t.Managed.Count > 0 ? string.Join("\n", t.Managed.Select(s => "  " + s)) : "  " + (t.ManagedNote ?? "(none)")).Append('\n');
                var held = t.StackObjects.Where(so => blocks.Any(b => b.Object == so.Item1)).ToList();
                if (held.Count > 0) stacks.Append("locked objects on its stack:\n").Append(string.Join("\n", held.Select(so => "  " + so.Item1 + " " + so.Item2))).Append('\n');
            }

            // ---------------------------------------------------------------- findings
            var verdict = new List<string>();
            uint engineOsid = ParseOsid(Opt("--engine", null));
            // The engine thread: given; else the one in the managed application tick; else the one whose native stack runs through the engine's
            // frame loop (--engine-frame; default the 1.4.8 TaleWorlds.Native.dll's (timestamp 6A732505) WotsMainNativeSDLL+0x3c, RVA 0x457bc, read
            // off the acc5lab navigation-cache capture, where thread 0 only waits in WotsMain); else thread 0.
            string engineFrame = Opt("--engine-frame", "TaleWorlds_Native+0x457bc");
            ThreadRow engine; string engineHow;
            if ((engine = threads.FirstOrDefault(t => engineOsid != 0 && t.Osid == engineOsid)) != null) engineHow = "given (--engine)";
            else if ((engine = threads.FirstOrDefault(t => t.Managed.Any(s => s.Contains("TaleWorlds.DotNet.Managed.ApplicationTick")))) != null) engineHow = "the thread in TaleWorlds.DotNet.Managed.ApplicationTick";
            else if ((engine = threads.FirstOrDefault(t => t.Native.Any(f => f.Rva == engineFrame))) != null) engineHow = "the thread whose stack runs through the engine frame loop " + engineFrame;
            else if ((engine = threads.FirstOrDefault(t => t.Index == 0)) != null) engineHow = "thread 0 (no thread in ApplicationTick or through " + engineFrame + ")";
            else engineHow = "none";

            // A garbage collection or a runtime suspension: the runtime's own special-thread roles, and (with clr symbols) its functions.
            // SuspendEE is the thread suspending the runtime (for a GC, a debugger or a thread abort); a "GC" thread alone can be the idle background
            // GC thread, so it counts only with clr symbols showing GC work on a stack.
            var gcThreads = threads.Where(t => t.Special.Any(s => s == "GC" || s == "SuspendEE") || t.Native.Any(f => f.Trusted && GcFrame.IsMatch(f.Site))).ToList();
            var gcWaiters = threads.Where(t => t.Native.Any(f => f.Trusted && GcWaitFrame.IsMatch(f.Site))).ToList();
            var cooperative = threads.Where(t => t.GcMode == "Cooperative").ToList();
            bool gcInProgress = gcThreads.Any(t => t.Special.Contains("SuspendEE") || t.Native.Any(f => f.Trusted && GcFrame.IsMatch(f.Site)));
            if (special.Contains("Failed to get CLR Tls data")) verdict.Add("note: !threads -special could not read every thread's CLR TLS (a dump without full memory?), so a SuspendEE thread may be missed");
            if (gcInProgress)
            {
                verdict.Add("GC / runtime suspension IN PROGRESS: " + string.Join("; ", gcThreads.Select(t => t.Label + " [" + string.Join(" ", t.Special) + "]")) +
                            ". Every managed thread that leaves native code or allocates waits for it to end.");
                var stuck = cooperative.Where(t => !gcThreads.Contains(t)).ToList();
                verdict.Add(stuck.Count == 0 ? "  no other thread is in cooperative GC mode (the suspension is not waiting on a managed thread)"
                    : "  it waits for the threads still in cooperative mode (running managed code, not at a safe point): " + string.Join("; ", stuck.Select(t => t.Label + " suspend " + t.Suspend + ", top " + t.NativeTop + (t.ManagedTop != null ? " / " + t.ManagedTop : ""))));
            }
            else verdict.Add(sosOk ? "no GC or runtime suspension in progress (no SuspendEE thread in !threads -special" + (gcWaiters.Count > 0 ? "; but " + gcWaiters.Count + " threads show GC-wait frames" : "") + ")" : "GC state unknown (SOS not loaded)");

            // Monitor locks: owners from !syncblk; a waiter is a thread in Monitor.Enter/Wait whose stack holds a locked object it does not own.
            var edges = new List<Tuple<ThreadRow, SyncBlock>>();
            foreach (var t in threads.Where(WaitsOnMonitor))
                foreach (var b in blocks.Where(b => b.OwnerOsid != 0 && b.OwnerOsid != t.Osid && t.StackObjects.Any(so => so.Item1 == b.Object)))
                    edges.Add(Tuple.Create(t, b));
            foreach (var b in blocks.Where(b => b.OwnerOsid != 0))
            {
                var owner = threads.FirstOrDefault(t => t.Osid == b.OwnerOsid);
                var waiters = edges.Where(e => e.Item2 == b).Select(e => e.Item1.Label).ToList();
                verdict.Add("Monitor " + b.Object + " " + b.Type + " held by " + (owner != null ? owner.Label : "os 0x" + b.OwnerOsid.ToString("x")) + " (MonitorHeld " + b.MonitorHeld + ": " + b.Waiters + " waiting" + (waiters.Count > 0 ? ": " + string.Join(", ", waiters) : "") + ")");
            }
            foreach (var cycle in Cycles(threads, edges))
                verdict.Add("DEADLOCK (Monitor): " + string.Join("; ", cycle.Select(e => e.Item1.Label + " waits for " + e.Item2.Type + " " + e.Item2.Object + " held by " + Owner(threads, e.Item2))));
            foreach (var t in threads.Where(WaitsOnMonitor).Where(t => !edges.Any(e => e.Item1 == t)))
                verdict.Add("Monitor wait with no held lock found on its stack: " + t.Label + " (" + t.ManagedTop + ")");

            // Exceptions in flight: a managed exception on a thread, or the native exception dispatch / unhandled-exception path on a stack.
            foreach (var t in threads.Where(t => !string.IsNullOrEmpty(t.Exception) && !Regex.IsMatch(t.Exception, @"^\((Finalizer|Threadpool [^)]*|GC|Gate)\)$")))
                verdict.Add("managed exception on " + t.Label + ": " + t.Exception);
            foreach (var t in threads.Where(t => t.Native.Any(f => f.Trusted && ExceptionFrame.IsMatch(f.Site))))
                verdict.Add("exception dispatch on the native stack of " + t.Label + ": " + string.Join(" <- ", t.Native.Where(f => f.Trusted && ExceptionFrame.IsMatch(f.Site)).Take(4).Select(f => f.Site)));

            // Suspended threads beyond the usual count (a dumper may suspend every thread once while it writes). The comsvcs dumps of
            // Test-HangDump showed 0 for a thread the target had suspended itself, so a 0 here proves nothing.
            int usual = threads.GroupBy(t => t.Suspend).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            foreach (var t in threads.Where(t => t.Suspend > usual)) verdict.Add("suspended beyond the rest (" + t.Suspend + " vs " + usual + "): " + t.Label + ", top " + t.NativeTop);

            // A thread whose top frame is not a system call was running user code when the dump froze it. In a process whose CPU time was flat,
            // such a thread was not running: it was stopped there (by a debugger event, a crash handler's SuspendThread, a runtime suspension).
            var outside = threads.Where(t => t.Native.Count > 0 && !(t.Native[0].Trusted && Regex.IsMatch(t.Native[0].Site, @"!(Nt|Zw)\w+\+0x[0-9a-f]+$", RegexOptions.IgnoreCase))).ToList();
            if (outside.Count > 0) verdict.Add("stopped outside a system call (" + outside.Count + "; with CPU flat they were held there, not running): " + string.Join("; ", outside.Take(10).Select(t => t.Label + " at " + t.NativeTop)) + (outside.Count > 10 ? "; ..." : ""));
            // A debugger attached to the dumped process (PEB.BeingDebugged): its debug events freeze every thread until it continues.
            var peb = Regex.Match(Run("db @$peb+2 L1"), @"^[0-9a-f`]+\s+([0-9a-f]{2})\b", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            string debugged = peb.Success ? (peb.Groups[1].Value == "00" ? "no" : "YES (PEB.BeingDebugged " + peb.Groups[1].Value + ")") : "unknown (PEB not read)";
            verdict.Add("debugger attached to the dumped process: " + debugged);

            // What every thread waits in (top native frame family), and the engine thread.
            var waits = threads.GroupBy(t => WaitKind(t)).OrderByDescending(g => g.Count()).Select(g => g.Key + " " + g.Count());
            verdict.Add("threads " + threads.Count + " (managed " + threads.Count(t => t.ManagedId.HasValue) + ", cooperative GC mode " + cooperative.Count + "); top native frames: " + string.Join(", ", waits));
            if (engine != null)
            {
                verdict.Add("engine thread: " + engine.Label + " (" + engineHow + "), suspend " + engine.Suspend + (engine.GcMode != null ? ", " + engine.GcMode + " GC" : "") + ", waits in " + WaitKind(engine));
                foreach (var f in engine.Native.Take(12)) verdict.Add("  " + f.Index.ToString("x2") + " " + f.Name);
                if (engine.Managed.Count > 0) verdict.Add("  managed top: " + string.Join(" <- ", engine.Managed.Take(4)));
            }

            // ---------------------------------------------------------------- report
            var report = new StringBuilder();
            report.Append("MNBL hang dump analysis\n");
            report.Append("dump ").Append(dump).Append(" (").Append(new FileInfo(dump).Length).Append(" bytes, written ").Append(File.GetLastWriteTimeUtc(dump).ToString("O", CultureInfo.InvariantCulture)).Append(")\n");
            report.Append("dbgeng ").Append(dbgeng).Append(' ').Append(FileVersionInfo.GetVersionInfo(dbgeng).FileVersion).Append("; sos ").Append(sosOk ? (sos ?? "loadby clr") : "not loaded").Append("; symbols ").Append(sympath ?? "none (exports only)").Append('\n');
            report.Append(Clean(eeversion).Trim().Split('\n').FirstOrDefault() ?? "").Append('\n');
            report.Append("analysis took ").Append(stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(" s\n\n");
            report.Append("== findings\n").Append(string.Join("\n", verdict)).Append("\n\n");
            report.Append("== threads (index os-id suspend managed-id gc-mode locks special | top native | top managed)\n");
            foreach (var t in threads)
                report.Append(string.Format(CultureInfo.InvariantCulture, "{0,4} {1,6:x} {2,2} {3,5} {4,-11} {5,2} {6,-16} | {7} | {8}\n", t.Index, t.Osid, t.Suspend, t.ManagedId.HasValue ? t.ManagedId.ToString() : "-",
                    t.GcMode ?? "-", t.LockCount, string.Join(" ", t.Special), t.NativeTop, t.ManagedTop ?? (t.ManagedId.HasValue ? "(no managed frames)" : "-")));
            report.Append("\n== stacks\n").Append(stacks).Append('\n');
            report.Append(raw);
            string text = report.ToString();
            string outPath = Opt("--out", null);
            if (outPath != null) File.WriteAllText(outPath, text, new UTF8Encoding(false)); else Console.Write(text);
            string jsonPath = Opt("--json", null);
            if (jsonPath != null) File.WriteAllText(jsonPath, Json(dump, verdict, threads, blocks, edges, gcInProgress, gcThreads, cooperative, engine, engineHow), new UTF8Encoding(false));
            if (outPath != null) { Console.WriteLine("hang dump analysis: " + outPath); foreach (var v in verdict) Console.WriteLine(v); }
            client.EndSession(0x1);                                                          // DEBUG_END_ACTIVE_TERMINATE (a dump: just closes it)
            return 0;
        }

        // ---------------------------------------------------------------- patterns

        private static readonly Regex GcFrame = new Regex(@"clr!(WKS|SVR)::GCHeap::GarbageCollect|clr!(WKS|SVR)::gc_heap::(garbage_collect|gc1)|clr!ThreadSuspend::SuspendRuntime|clr!ThreadSuspend::SuspendEE", RegexOptions.Compiled);
        private static readonly Regex GcWaitFrame = new Regex(@"clr!(WKS|SVR)::GCHeap::WaitUntilGCComplete|clr!Thread::RareDisablePreemptiveGC|clr!(WKS|SVR)::gc_heap::wait_for_gc_done|clr!Thread::RareEnablePreemptiveGC", RegexOptions.Compiled);
        private static readonly Regex ExceptionFrame = new Regex(@"KiUserExceptionDispatch|RtlDispatchException|!RaiseException|UnhandledExceptionFilter|WerpReportFault|CxxThrowException|RaiseFailFastException|RtlReportException|!IL_Throw|COMPlusThrow|ProcessCLRException|kernel32!WerpReport", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MonitorFrame = new Regex(@"System\.Threading\.Monitor\.(ReliableEnter|Enter|TryEnter|ObjWait|Wait|ReliableEnterTimeout)|JIT_MonEnter|JITutil_MonContention|AwareLock::Enter|AwareLock::Contention|SyncBlock::Wait", RegexOptions.Compiled);

        private static bool WaitsOnMonitor(ThreadRow t)
        {
            return t.Managed.Take(4).Any(s => MonitorFrame.IsMatch(s)) || t.Native.Take(12).Any(f => MonitorFrame.IsMatch(f.Managed ?? (f.Trusted ? f.Site : "")));
        }

        private static string WaitKind(ThreadRow t)
        {
            var f = t.Native.FirstOrDefault();
            if (f == null) return "no-frames";
            string s = f.Site;
            var m = Regex.Match(s, @"!(Nt|Zw)(\w+?)(\+0x[0-9a-f]+)?$");
            if (m.Success && f.Trusted) return m.Groups[2].Value;
            if (f.Managed != null) return "managed code";
            m = Regex.Match(s, @"^(\w+)!(\w+)");
            return m.Success ? (f.Trusted ? m.Groups[1].Value + "!" + m.Groups[2].Value : m.Groups[1].Value + " code") : "unknown code";
        }

        // ---------------------------------------------------------------- parsing

        private static List<ThreadRow> ParseThreads(string text)
        {
            var rows = new List<ThreadRow>();
            foreach (var line in Lines(text))
            {
                var m = Regex.Match(line, @"^[\s.#]*(\d+)\s+Id:\s*([0-9a-f]+)\.([0-9a-f]+)\s+Suspend:\s*(-?\d+)\s+Teb:\s*([0-9a-f`]+)\s+\w+(?:\s+""(.*)"")?", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                rows.Add(new ThreadRow { Index = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), Osid = uint.Parse(m.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    Suspend = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture), Teb = m.Groups[5].Value, Name = m.Groups[6].Success ? m.Groups[6].Value : null });
            }
            return rows;
        }

        // !threads:  DBG ID OSID ThreadOBJ State GC-Mode GC-Alloc-Context Domain LockCount Apt Exception
        private static void ParseManagedThreads(string text, List<ThreadRow> threads)
        {
            foreach (var line in Lines(text))
            {
                var m = Regex.Match(line, @"^\s*(\d+|XXXX)\s+(\d+)\s+([0-9a-f]+)\s+([0-9a-f]+)\s+([0-9a-f]+)\s+(Preemptive|Cooperative)\s+(\S+)\s+([0-9a-f]+)\s+(-?\d+)\s+(\w+)\s*(.*)$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                uint osid = uint.Parse(m.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var t = threads.FirstOrDefault(r => r.Osid == osid && osid != 0);
                if (t == null) continue;                                                          // dead or unstarted (XXXX)
                t.ManagedId = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture); t.ThreadObj = m.Groups[4].Value; t.State = m.Groups[5].Value; t.GcMode = m.Groups[6].Value;
                t.Domain = m.Groups[8].Value; t.LockCount = int.Parse(m.Groups[9].Value, CultureInfo.InvariantCulture); t.Apt = m.Groups[10].Value; t.Exception = m.Groups[11].Value.Trim();
            }
        }

        private static void ParseSpecial(string text, List<ThreadRow> threads)
        {
            bool table = false;
            foreach (var line in Lines(text))
            {
                if (line.Contains("Special thread type")) { table = true; continue; }
                if (!table) continue;
                var m = Regex.Match(line, @"^\s*(\d+)\s+([0-9a-f]+)\s+(.+?)\s*$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                uint osid = uint.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var t = threads.FirstOrDefault(r => r.Osid == osid);
                if (t != null) t.Special.AddRange(m.Groups[3].Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            }
        }

        // !syncblk: Index SyncBlock MonitorHeld Recursion OwningThreadInfo(ThreadOBJ OSID DbgId) SyncBlockOwner(object type)
        private static List<SyncBlock> ParseSyncBlocks(string text, List<ThreadRow> threads)
        {
            var rows = new List<SyncBlock>();
            foreach (var line in Lines(text))
            {
                var m = Regex.Match(line, @"^\s*(\d+)\s+([0-9a-f]{8,16})\s+(\d+)\s+(\d+)\s+([0-9a-f]{8,16})\s+(?:([0-9a-f]+)\s+(\d+|XXX)|none)\s+([0-9a-f]{8,16})\s+(\S+)", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var b = new SyncBlock { Index = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), Address = m.Groups[2].Value, MonitorHeld = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                    Recursion = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture), OwnerThreadObj = m.Groups[5].Value, Object = Norm(m.Groups[8].Value), Type = m.Groups[9].Value };
                if (m.Groups[6].Success) b.OwnerOsid = uint.Parse(m.Groups[6].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var owner = threads.FirstOrDefault(t => t.Osid == b.OwnerOsid && b.OwnerOsid != 0);
                if (owner != null) b.OwnerIndex = owner.Index;
                rows.Add(b);
            }
            return rows;
        }

        // kn:  ## Child-SP RetAddr Call-Site
        private static List<Frame> ParseFrames(string text)
        {
            var rows = new List<Frame>();
            foreach (var line in Lines(text))
            {
                var m = Regex.Match(line, @"^\s*([0-9a-f]{2,})\s+([0-9a-f`]{8,17})\s+([0-9a-f`]{8,17})\s+(.+?)\s*$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                rows.Add(new Frame { Index = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), Sp = m.Groups[2].Value, Ret = m.Groups[3].Value, Site = m.Groups[4].Value });
            }
            return rows;
        }

        private static void ParseClrStack(string text, ThreadRow t)
        {
            bool table = false;
            foreach (var line in Lines(text))
            {
                if (Regex.IsMatch(line, @"Child SP\s+IP\s+Call Site")) { table = true; continue; }
                if (!table)
                {
                    if (line.Contains("Unable to walk") || line.Contains("Failed to start stack walk") || line.Contains("not a managed thread")) t.ManagedNote = line.Trim();
                    continue;
                }
                var m = Regex.Match(line, @"^\s*([0-9a-f]{8,16})\s+([0-9a-f]{8,16})\s+(.+?)\s*$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                // A transition frame ([InlinedCallFrame: ...], [GCFrame: ...]) names the method it stands for after it, if any.
                string site = Regex.Replace(m.Groups[3].Value, @"^\[[A-Za-z_0-9]+Frame[A-Za-z_0-9]*: [0-9a-f]+\]\s*", "");
                if (site.Length == 0 || (t.Managed.Count > 0 && t.Managed[t.Managed.Count - 1] == site)) continue;
                t.Managed.Add(site);
            }
            if (t.Managed.Count == 0 && t.ManagedNote == null && t.ManagedId.HasValue) t.ManagedNote = "(no managed frames)";
        }

        private static void ParseDso(string text, ThreadRow t)
        {
            foreach (var line in Lines(text))
            {
                var m = Regex.Match(line, @"^\s*(\S+)\s+([0-9a-f]{8,16})\s+(\S.*?)\s*$", RegexOptions.IgnoreCase);
                if (!m.Success || m.Groups[1].Value == "RSP/REG" || m.Groups[1].Value == "OS") continue;
                string addr = Norm(m.Groups[2].Value);
                if (!t.StackObjects.Any(so => so.Item1 == addr)) t.StackObjects.Add(Tuple.Create(addr, m.Groups[3].Value));
            }
        }

        private static string Owner(List<ThreadRow> threads, SyncBlock b)
        {
            var o = threads.FirstOrDefault(t => t.Osid == b.OwnerOsid);
            return o != null ? o.Label : "os 0x" + b.OwnerOsid.ToString("x", CultureInfo.InvariantCulture);
        }

        // lm / lmo:  start end module (symbol state) [file]
        private static List<Module> ParseModules(string text)
        {
            var rows = new List<Module>();
            foreach (var line in Lines(text))
            {
                var m = Regex.Match(line, @"^([0-9a-f`]{8,17})\s+([0-9a-f`]{8,17})\s+(\S+)\s+(?:\S+\s+)?\(([^)]*)\)\s*(.*)$", RegexOptions.IgnoreCase);
                if (m.Success) rows.Add(new Module { Start = Hex(m.Groups[1].Value), End = Hex(m.Groups[2].Value), Name = m.Groups[3].Value, Symbols = m.Groups[4].Value.Trim(), File = m.Groups[5].Value.Trim() });
            }
            return rows;
        }

        private static ulong Hex(string s) { return ulong.Parse(s.Replace("`", "").Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture); }

        // Each wait-for cycle once (thread -> lock -> owner thread -> ...).
        private static List<List<Tuple<ThreadRow, SyncBlock>>> Cycles(List<ThreadRow> threads, List<Tuple<ThreadRow, SyncBlock>> edges)
        {
            var found = new List<List<Tuple<ThreadRow, SyncBlock>>>(); var seen = new HashSet<string>();
            foreach (var start in edges.Select(e => e.Item1).Distinct())
            {
                var path = new List<Tuple<ThreadRow, SyncBlock>>(); var at = start; var visited = new HashSet<ThreadRow>();
                while (at != null && visited.Add(at))
                {
                    var e = edges.FirstOrDefault(x => x.Item1 == at);
                    if (e == null) break;
                    path.Add(e);
                    at = threads.FirstOrDefault(t => t.Osid == e.Item2.OwnerOsid);
                    if (at == start)
                    {
                        string key = string.Join(",", path.Select(p => p.Item1.Index).OrderBy(i => i));
                        if (seen.Add(key)) found.Add(path);
                        break;
                    }
                }
            }
            return found;
        }

        // ---------------------------------------------------------------- engine

        private static string Run(string command)
        {
            sink.Text.Clear();
            int hr = control.Execute(0, command, 0x2 | 0x4);                                // this client; not logged, no repeat
            string text = sink.Text.ToString();
            return hr == 0 ? text : text + "\n(command failed: 0x" + hr.ToString("x8") + ")";
        }

        private static string Raw(string title, string command)
        {
            string text = Run(command);
            raw.Append("== ").Append(title).Append('\n').Append(Clean(text)).Append('\n');
            return text;
        }

        private static string Clean(string text) { return string.Join("\n", Lines(text).Where(l => !Noise.IsMatch(l))); }
        private static IEnumerable<string> Lines(string text) { return (text ?? "").Replace("\r", "").Split('\n'); }
        private static string Norm(string address) { return address.Replace("`", "").TrimStart('0').ToLowerInvariant(); }

        private static uint ParseOsid(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            s = s.Trim();
            return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? uint.Parse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : uint.Parse(s, CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- json

        private static string Json(string dump, List<string> verdict, List<ThreadRow> threads, List<SyncBlock> blocks, List<Tuple<ThreadRow, SyncBlock>> edges, bool gc, List<ThreadRow> gcThreads,
            List<ThreadRow> cooperative, ThreadRow engine, string engineHow)
        {
            var b = new StringBuilder();
            b.Append("{\n  \"schema\": \"mnbl.hang-dump-analysis\", \"version\": 1,\n  \"dump\": ").Append(Q(dump)).Append(",\n");
            b.Append("  \"findings\": [").Append(string.Join(", ", verdict.Select(Q))).Append("],\n");
            b.Append("  \"gcInProgress\": ").Append(gc ? "true" : "false").Append(", \"gcThreads\": [").Append(string.Join(", ", gcThreads.Select(t => t.Index.ToString(CultureInfo.InvariantCulture)))).Append("], \"cooperative\": [")
             .Append(string.Join(", ", cooperative.Select(t => t.Index.ToString(CultureInfo.InvariantCulture)))).Append("],\n");
            b.Append("  \"engineThread\": ").Append(engine == null ? "null" : engine.Index.ToString(CultureInfo.InvariantCulture)).Append(", \"engineThreadHow\": ").Append(Q(engineHow)).Append(",\n");
            b.Append("  \"locks\": [").Append(string.Join(", ", blocks.Select(k => "{\"object\": " + Q(k.Object) + ", \"type\": " + Q(k.Type) + ", \"ownerOsid\": " + k.OwnerOsid + ", \"ownerIndex\": " + (k.OwnerIndex.HasValue ? k.OwnerIndex.ToString() : "null") + ", \"monitorHeld\": " + k.MonitorHeld + ", \"waiters\": " + k.Waiters + "}"))).Append("],\n");
            b.Append("  \"waits\": [").Append(string.Join(", ", edges.Select(e => "{\"thread\": " + e.Item1.Index + ", \"object\": " + Q(e.Item2.Object) + ", \"ownerIndex\": " + (e.Item2.OwnerIndex.HasValue ? e.Item2.OwnerIndex.ToString() : "null") + "}"))).Append("],\n");
            b.Append("  \"threads\": [\n");
            b.Append(string.Join(",\n", threads.Select(t => "    {\"index\": " + t.Index + ", \"osid\": " + t.Osid + ", \"suspend\": " + t.Suspend + ", \"managedId\": " + (t.ManagedId.HasValue ? t.ManagedId.ToString() : "null") +
                ", \"gcMode\": " + Q(t.GcMode) + ", \"lockCount\": " + t.LockCount + ", \"special\": [" + string.Join(", ", t.Special.Select(Q)) + "], \"exception\": " + Q(t.Exception) +
                ", \"native\": [" + string.Join(", ", t.Native.Select(f => Q(f.Name))) + "], \"managed\": [" + string.Join(", ", t.Managed.Select(Q)) + "]}")));
            b.Append("\n  ]\n}\n");
            return b.ToString();
        }

        private static string Q(string s)
        {
            if (s == null) return "null";
            var b = new StringBuilder("\"");
            foreach (char c in s) { if (c == '"' || c == '\\') b.Append('\\').Append(c); else if (c < ' ') b.Append("\\u").Append(((int)c).ToString("x4")); else b.Append(c); }
            return b.Append('"').ToString();
        }

        [DllImport("dbgeng.dll")] private static extern int DebugCreate(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object client);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibrary(string path);
    }
}
