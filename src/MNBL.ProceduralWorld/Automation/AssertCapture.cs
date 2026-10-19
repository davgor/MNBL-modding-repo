using System;
using System.Collections.Generic;
using System.IO;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.Automation
{
    // The engine's asserts (TaleWorlds.Library.Debug.Assert, FailedAssert, SilentAssert all call Debug.DebugManager) while installed:
    // a wrapper records each failed one and forwards everything to the game's own manager. A canary FailedAssert proves it records;
    // the canary is not forwarded.
    internal sealed class AssertCapture : IDebugManager
    {
        private const string Canary = "mnbl probe assert canary";
        private IDebugManager inner;
        public readonly List<object> Asserts = new List<object>();
        public int Count; public bool CanarySeen;

        public void Install()
        {
            inner = Debug.DebugManager;
            Debug.DebugManager = this;
            Debug.FailedAssert(Canary, "AssertCapture.cs", "Install", 0);
        }

        public void Remove() { if (ReferenceEquals(Debug.DebugManager, this)) Debug.DebugManager = inner; }

        private void Record(string message, string file, string method, int line, bool silent)
        {
            if (message == Canary) { CanarySeen = true; return; }
            Count++;
            if (Asserts.Count < 25) Asserts.Add(new Dictionary<string, object> { ["message"] = message, ["file"] = file == null ? null : Path.GetFileName(file), ["method"] = method, ["line"] = line, ["silent"] = silent,
                ["campaignHours"] = Campaign.Current == null ? (object)null : Math.Round(CampaignTime.Now.ToHours, 2) });
        }

        public void Assert(bool condition, string message, string callerFile, string callerMethod, int callerLine)
        {
            if (!condition) Record(message, callerFile, callerMethod, callerLine, false);
            if (message != Canary && inner != null) inner.Assert(condition, message, callerFile, callerMethod, callerLine);
        }

        public void SilentAssert(bool condition, string message, bool getDump, string callerFile, string callerMethod, int callerLine)
        {
            if (!condition) Record(message, callerFile, callerMethod, callerLine, true);
            if (inner != null) inner.SilentAssert(condition, message, getDump, callerFile, callerMethod, callerLine);
        }

        public void ShowWarning(string message) { if (inner != null) inner.ShowWarning(message); }
        public void Print(string message, int logLevel, Debug.DebugColor color, ulong debugFilter) { if (inner != null) inner.Print(message, logLevel, color, debugFilter); }
        public void PrintError(string error, string stackTrace, ulong debugFilter) { if (inner != null) inner.PrintError(error, stackTrace, debugFilter); }
        public void PrintWarning(string warning, ulong debugFilter) { if (inner != null) inner.PrintWarning(warning, debugFilter); }
        public void ShowError(string message) { if (inner != null) inner.ShowError(message); }
        public void ShowMessageBox(string lpText, string lpCaption, uint uType) { if (inner != null) inner.ShowMessageBox(lpText, lpCaption, uType); }
        public void DisplayDebugMessage(string message) { if (inner != null) inner.DisplayDebugMessage(message); }
        public void WatchVariable(string name, object value) { if (inner != null) inner.WatchVariable(name, value); }
        public void WriteDebugLineOnScreen(string message) { if (inner != null) inner.WriteDebugLineOnScreen(message); }
        public void RenderDebugLine(Vec3 position, Vec3 direction, uint color, bool depthCheck, float time) { if (inner != null) inner.RenderDebugLine(position, direction, color, depthCheck, time); }
        public void RenderDebugSphere(Vec3 position, float radius, uint color, bool depthCheck, float time) { if (inner != null) inner.RenderDebugSphere(position, radius, color, depthCheck, time); }
        public void RenderDebugText3D(Vec3 position, string text, uint color, int screenPosOffsetX, int screenPosOffsetY, float time) { if (inner != null) inner.RenderDebugText3D(position, text, color, screenPosOffsetX, screenPosOffsetY, time); }
        public void RenderDebugFrame(MatrixFrame frame, float lineLength, float time) { if (inner != null) inner.RenderDebugFrame(frame, lineLength, time); }
        public void RenderDebugText(float screenX, float screenY, string text, uint color, float time) { if (inner != null) inner.RenderDebugText(screenX, screenY, text, color, time); }
        public void RenderDebugRectWithColor(float left, float bottom, float right, float top, uint color) { if (inner != null) inner.RenderDebugRectWithColor(left, bottom, right, top, color); }
        public Vec3 GetDebugVector() { return inner != null ? inner.GetDebugVector() : Vec3.Zero; }
        public void SetDebugVector(Vec3 value) { if (inner != null) inner.SetDebugVector(value); }
        public void SetCrashReportCustomString(string customString) { if (inner != null) inner.SetCrashReportCustomString(customString); }
        public void SetCrashReportCustomStack(string customStack) { if (inner != null) inner.SetCrashReportCustomStack(customStack); }
        public void SetTestModeEnabled(bool testModeEnabled) { if (inner != null) inner.SetTestModeEnabled(testModeEnabled); }
        public void AbortGame() { if (inner != null) inner.AbortGame(); }
        public void DoDelayedexit(int returnCode) { if (inner != null) inner.DoDelayedexit(returnCode); }
        public void ReportMemoryBookmark(string message) { if (inner != null) inner.ReportMemoryBookmark(message); }
    }
}
