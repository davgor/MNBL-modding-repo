using System;
using System.Diagnostics;
using TaleWorlds.MountAndBlade;

namespace MNBL.AgentGrowthProbe
{
    public sealed class SubModule : MBSubModuleBase
    {
        private bool menuReady, attempted;
        private readonly Stopwatch menuClock = new Stopwatch();
        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            if (menuReady) return;
            menuReady = true;
            menuClock.Start();
        }
        protected override void OnApplicationTick(float dt)
        {
            if (attempted || !menuReady || menuClock.Elapsed.TotalSeconds < 3) return;
            attempted = true;
            string path = Environment.GetEnvironmentVariable(ProbeRequest.EnvironmentKey);
            if (String.IsNullOrEmpty(path)) return;
            // An ordinary/foreign launch never claims, loads native code or quits.
            ProbeRequest request;
            var process = Process.GetCurrentProcess();
            long start = process.StartTime.ToUniversalTime().Ticks;
            try
            {
                request = ProbeRequest.ReadAndValidate(path, typeof(SubModule).Assembly.Location,
                    process.Id, start, process.MainModule.FileName, DateTime.UtcNow);
                if (request == null) return;
                request.Claim(process.Id, start);
            }
            catch { return; } // Invalid arming is inert; runner diagnoses a missing result.
            Environment.SetEnvironmentVariable(ProbeRequest.EnvironmentKey, null, EnvironmentVariableTarget.Process);
            ProbeResult result = new ProbeResult(); int nativeCode = -1; string error = null;
            try
            {
                if (Mission.Current != null) throw new InvalidOperationException("Private probe requires main menu with no Mission.");
                result = ProbeInterop.Call(request.nativeDll, request.nativeSha256, out nativeCode);
                ProbeInterop.ValidateSuccess(nativeCode, result);
            }
            catch (Exception failure) { error = failure.ToString(); }
            try { request.Report(new {
                schemaVersion = 1, runId = request.runId, pid = process.Id, startTicks = start,
                status = error == null ? "passed-awaiting-normal-process-exit" : "failed-awaiting-normal-process-exit",
                nativeCode, native = result, error, nativeSha256 = request.nativeSha256, hostSha256 = request.hostSha256,
                menuWaitSeconds = menuClock.Elapsed.TotalSeconds, mainMenuReady = menuReady,
                missionAbsent = Mission.Current == null, quitRequested = true, finishedUtc = DateTime.UtcNow.ToString("O")
            }); }
            catch { /* The runner treats a missing report as failure. */ }
            finally { TaleWorlds.Engine.Utilities.QuitGame(); }
        }
    }
}
