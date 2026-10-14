using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;

namespace MNBL.ProceduralWorld.Automation
{
    // Single-shot, file-requested probe execution for tools/Invoke-InGameProbe.ps1.
    // The request is claimed (renamed to request.<pid>.taken.json) before any probe code runs,
    // so a crash can never re-trigger the same request on the next start.
    internal sealed class AutorunHost
    {
        private enum Phase { WaitingForMenu, Delay, Running, Cleaning, Quitting, Done }

        // Quit paths, all official: the Exit Game menu action, the engine quit call, and the delayed exit with an
        // explicit process return code used by TaleWorlds' own test automation (Debug.DoDelayedexit -> Utilities.DoDelayedexit).
        private const string ExitButton = "exit_button", QuitGame = "quit_game", DelayedExit = "delayed_exit";
        public const string FallbackQuitMethod = "TaleWorlds.Engine.Utilities.QuitGame";
        private const double FallbackQuitSeconds = 30;

        private readonly string directory;
        private readonly int processId;
        private readonly string progressPath;
        private readonly string resultPath;
        private readonly string takenPath;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<object> stages = new List<object>();
        private readonly object gate = new object();
        private Phase phase = Phase.WaitingForMenu;
        private string requestId;
        private string probeId;
        private Dictionary<string, object> args = new Dictionary<string, object>();
        private bool quit = true;
        private string quitMethod = ExitButton;
        private readonly int exitCode = 0;
        private double menuDelaySeconds = 3;
        private double menuTimeoutSeconds = 600;
        private double probeTimeoutSeconds = 900;
        private double menuAt = -1, probeStartAt = -1, probeEndAt = -1, quitAt = -1;
        private string menuScreen;
        private IEnumerator probe;
        private ProbeContext context;
        private string status = "pending";
        private string error;
        private bool fallbackQuitUsed;
        private int checkpointFailures;
        private double cleanupAt;
        private bool cleanupTimedOut;

        public static AutorunHost Current { get; private set; }

        // True from request claim until the probe finished (or the request expired); menu diagnostics are disabled meanwhile.
        public static bool Busy { get { var host = Current; return host != null && host.phase != Phase.Done; } }

        private string lastTopScreen;

        private AutorunHost(string directory, int processId, string takenPath)
        {
            this.directory = directory;
            this.processId = processId;
            this.takenPath = takenPath;
            progressPath = Path.Combine(directory, "progress-" + processId + ".log");
            resultPath = Path.Combine(directory, "result-" + processId + ".json");
        }

        private double Now { get { return clock.Elapsed.TotalSeconds; } }

        public static void TryClaim(string store)
        {
            var directory = Path.Combine(store, "autorun");
            var request = Path.Combine(directory, "request.json");
            if (!File.Exists(request)) return;
            int pid = Process.GetCurrentProcess().Id;
            var taken = Path.Combine(directory, "request." + pid + ".taken.json");
            try { File.Move(request, taken); }
            catch (Exception failure)
            {
                File.AppendAllText(Path.Combine(store, "module.log"), "autorun claim failed: " + failure + "\n");
                return;
            }
            var host = new AutorunHost(directory, pid, taken);
            Current = host;
            host.Trace("request_taken file=" + Path.GetFileName(taken));
            try
            {
                var parsed = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(taken)) as Dictionary<string, object>;
                if (parsed == null) throw new InvalidDataException("Request is not a JSON object.");
                host.requestId = Text(parsed, "requestId");
                host.probeId = Text(parsed, "probe");
                if (string.IsNullOrEmpty(host.probeId)) throw new InvalidDataException("Request names no probe.");
                object value;
                if (parsed.TryGetValue("args", out value) && value != null)
                {
                    host.args = value as Dictionary<string, object>;
                    if (host.args == null) throw new InvalidDataException("Request args must be a JSON object.");
                }
                if (parsed.TryGetValue("quit", out value) && value is bool) host.quit = (bool)value;
                host.quitMethod = Text(parsed, "quitMethod") ?? ExitButton;
                if (host.quitMethod != ExitButton && host.quitMethod != QuitGame && host.quitMethod != DelayedExit)
                    throw new InvalidDataException("quitMethod must be exit_button, quit_game or delayed_exit.");
                if (parsed.TryGetValue("menuDelaySeconds", out value) && value != null) host.menuDelaySeconds = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (parsed.TryGetValue("probeTimeoutSeconds", out value) && value != null) host.probeTimeoutSeconds = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                var expires = Text(parsed, "expiresUtc");
                DateTime expiry;
                if (expires != null && DateTime.TryParse(expires, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out expiry)
                    && DateTime.UtcNow > expiry)
                {
                    // A stale request from an abandoned harness run must not hijack a manual game session.
                    host.status = "expired";
                    host.quit = false;
                    host.phase = Phase.Done;
                    host.Trace("request_expired expiresUtc=" + expires);
                    host.WriteResult(true);
                    return;
                }
                host.Trace("request_parsed probe=" + host.probeId + " requestId=" + host.requestId + " quit=" + host.quit);
            }
            catch (Exception failure)
            {
                host.status = "invalid_request";
                host.error = failure.ToString();
                host.Trace("request_invalid " + failure.GetType().Name + ": " + failure.Message);
                // Still quit once the menu is up so the harness does not wait for its timeout.
                host.probe = Empty();
            }
            host.WriteResult(false);
        }

        private static IEnumerator Empty() { yield break; }

        private static string Text(Dictionary<string, object> map, string key)
        {
            object value;
            return map.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        public void Tick(float dt)
        {
            // Any screen change after the menu appeared is traced, so foreign UI activity is attributable in the log.
            var current = ScreenManager.TopScreen;
            var currentName = current == null ? "none" : current.GetType().FullName;
            if (menuAt >= 0 && currentName != lastTopScreen) Trace("top_screen_changed to=" + currentName);
            lastTopScreen = currentName;
            switch (phase)
            {
                case Phase.WaitingForMenu:
                    var top = ScreenManager.TopScreen;
                    if (top is MBInitialScreenBase)
                    {
                        menuAt = Now;
                        menuScreen = top.GetType().FullName;
                        Trace("menu_visible screen=" + menuScreen);
                        phase = Phase.Delay;
                    }
                    else if (Now > menuTimeoutSeconds)
                    {
                        status = "menu_timeout";
                        error = "Initial menu screen was not observed within " + menuTimeoutSeconds + " s.";
                        Finish();
                    }
                    break;
                case Phase.Delay:
                    if (Now - menuAt >= menuDelaySeconds) Begin();
                    break;
                case Phase.Running:
                    Step();
                    break;
                case Phase.Quitting:
                    if (!fallbackQuitUsed && Now - quitAt > FallbackQuitSeconds)
                    {
                        fallbackQuitUsed = true;
                        Trace("quit_fallback method=" + FallbackQuitMethod);
                        TaleWorlds.Engine.Utilities.QuitGame();
                    }
                    break;
                case Phase.Cleaning:
                    if (context.OwnedWorkFinished)
                    {
                        context.Outputs["ownedWorkCleanupComplete"] = true;
                        Trace("owned_work_cleanup_complete");
                        CompleteFinish();
                    }
                    else if (!cleanupTimedOut && Now - cleanupAt > 30)
                    {
                        cleanupTimedOut = true;
                        status = "cleanup_timeout";
                        error = "Owned work has not completed cleanup; result remains non-final and automatic quit is withheld.";
                        Trace("owned_work_cleanup_timeout");
                        WriteResult(false);
                    }
                    break;
            }
        }

        private void Begin()
        {
            probeStartAt = Now;
            context = new ProbeContext(args, directory, processId, Trace, () => WriteResult(false));
            phase = Phase.Running;
            if (probe == null)
            {
                status = "running";
                Trace("probe_start id=" + probeId);
                try { probe = AutorunProbes.Create(probeId, context); }
                catch (Exception failure) { Fail(failure); Finish(); return; }
            }
            WriteResult(false);
        }

        private void Step()
        {
            if (Now - probeStartAt > probeTimeoutSeconds)
            {
                status = "probe_timeout";
                error = "Probe exceeded " + probeTimeoutSeconds + " s.";
                Trace("probe_timeout");
                Finish();
                return;
            }
            bool more;
            try { more = probe.MoveNext(); }
            catch (Exception failure) { Fail(failure); Finish(); return; }
            if (!more)
            {
                if (status == "running") status = "ok";
                Finish();
            }
        }

        private void Fail(Exception failure)
        {
            status = "error";
            error = failure.ToString();
            Trace("probe_exception " + failure.GetType().Name + ": " + failure.Message);
        }

        private void Finish()
        {
            var disposable = probe as IDisposable;
            probe = null;
            try { disposable?.Dispose(); }
            catch (Exception failure) { Fail(failure); }
            try { context?.CancelOwnedWork(); }
            catch (Exception failure) { Fail(failure); }
            if (context != null && !context.OwnedWorkFinished)
            {
                context.Outputs["ownedWorkCleanupComplete"] = false;
                phase = Phase.Cleaning;
                cleanupAt = Now;
                Trace("owned_work_cleanup_wait");
                WriteResult(false);
                return;
            }
            if (context != null) context.Outputs["ownedWorkCleanupComplete"] = true;
            CompleteFinish();
        }

        private void CompleteFinish()
        {
            probeEndAt = Now;
            Trace("probe_end status=" + status);
            WriteResult(true);
            if (!quit) { phase = Phase.Done; return; }
            phase = Phase.Quitting;
            quitAt = Now;
            Trace("quit_requested method=" + QuitDescription());
            try
            {
                // Probe failure is reported through the result file; the process return code reflects the quit path.
                if (quitMethod == ExitButton) MBInitialScreenBase.DoExitButtonAction();
                else if (quitMethod == QuitGame) TaleWorlds.Engine.Utilities.QuitGame();
                else TaleWorlds.Engine.Utilities.DoDelayedexit(exitCode);
                Trace("quit_call_returned");
            }
            catch (Exception failure)
            {
                Trace("quit_call_failed " + failure.GetType().Name + ": " + failure.Message + "; method=" + FallbackQuitMethod);
                fallbackQuitUsed = true;
                TaleWorlds.Engine.Utilities.QuitGame();
            }
        }

        private string QuitDescription()
        {
            if (quitMethod == ExitButton) return "TaleWorlds.MountAndBlade.MBInitialScreenBase.DoExitButtonAction";
            if (quitMethod == QuitGame) return "TaleWorlds.Engine.Utilities.QuitGame";
            return "TaleWorlds.Engine.Utilities.DoDelayedexit(" + exitCode + ")";
        }

        public void OnUnloaded() { Trace("submodule_unloaded"); }
        public void OnProcessExit() { Trace("process_exit"); }

        private void Trace(string stage)
        {
            lock (gate)
            {
                double ms = clock.Elapsed.TotalMilliseconds;
                string utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                stages.Add(new Dictionary<string, object> { { "name", stage }, { "utc", utc }, { "ms", Math.Round(ms, 1) } });
                var bytes = Encoding.UTF8.GetBytes(utc + " +" + ms.ToString("F0", CultureInfo.InvariantCulture) + "ms " + stage + "\n");
                try
                {
                    using (var stream = new FileStream(progressPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                }
                catch (IOException) { }
            }
        }

        private void WriteResult(bool final)
        {
            lock (gate)
            {
                var timings = new Dictionary<string, object>
                {
                    { "moduleLoadToMenuMs", menuAt < 0 ? null : (object)Math.Round(menuAt * 1000) },
                    { "menuDelayMs", menuAt < 0 || probeStartAt < 0 ? null : (object)Math.Round((probeStartAt - menuAt) * 1000) },
                    { "probeMs", probeStartAt < 0 ? null : (object)Math.Round(((probeEndAt < 0 ? Now : probeEndAt) - probeStartAt) * 1000) },
                    { "moduleLoadToResultMs", Math.Round(Now * 1000) }
                };
                var assembly = typeof(AutorunHost).Assembly.Location;
                var result = new Dictionary<string, object>
                {
                    { "schema", "mnbl.autorun-result" }, { "version", 1 },
                    { "requestId", requestId }, { "probe", probeId }, { "args", args },
                    { "processId", processId }, { "store", SubModule.Store }, { "requestFile", Path.GetFileName(takenPath) },
                    { "status", status }, { "final", final }, { "error", error }, { "checkpointWriteFailures", checkpointFailures },
                    { "menuScreen", menuScreen },
                    { "quitPlanned", quit ? QuitDescription() + " (fallback " + FallbackQuitMethod + " after " + FallbackQuitSeconds + " s)" : null },
                    { "timings", timings }, { "stages", stages.ToArray() },
                    { "outputs", context == null ? null : context.Outputs },
                    { "moduleAssembly", assembly }, { "moduleSha256", Hash(assembly) },
                    { "utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) }
                };
                var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 }.Serialize(result);
                var temporary = resultPath + ".tmp";
                // The harness may be reading the previous version; retry briefly instead of failing the probe.
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        File.WriteAllText(temporary, json, new UTF8Encoding(false));
                        if (File.Exists(resultPath)) File.Replace(temporary, resultPath, null);
                        else File.Move(temporary, resultPath);
                        return;
                    }
                    catch (IOException) when (attempt < (final ? 100 : 10))
                    {
                        System.Threading.Thread.Sleep(20);
                    }
                    catch (IOException) when (!final)
                    {
                        checkpointFailures++;
                        return;
                    }
                }
            }
        }

        private static string Hash(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            catch (IOException) { return null; }
        }
    }
}
