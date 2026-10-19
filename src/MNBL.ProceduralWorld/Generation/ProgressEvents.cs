using System;
using System.Collections.Generic;

namespace MNBL.ProceduralWorld.Generation
{
    // MMR-44: the generation job's progress events (worldgen/contracts/progress-event.schema.json): protocol version, job id, a sequence
    // that increases by one per event of that job, the phase, completed/total only when the work has a known size, a message code
    // (lower_snake, localised by the UI), and a structured error (category, code, retryable; the detail stays in the job's diagnostics).
    internal sealed class ProgressEvent
    {
        public const int ProtocolVersion = 1;
        public string JobId { get; set; }
        public long Sequence { get; set; }
        public JobPhase Phase { get; set; }
        public int? Completed { get; set; }
        public int? Total { get; set; }
        public string MessageCode { get; set; }
        public GenerationError Error { get; set; }
        public double Seconds { get; set; }   // since the job started

        public Dictionary<string, object> ToDictionary()
        {
            var d = new Dictionary<string, object> { ["protocolVersion"] = ProtocolVersion, ["stateMachineVersion"] = JobStateMachine.Version, ["jobId"] = JobId, ["sequence"] = Sequence,
                ["phase"] = Phase.ToString(), ["messageCode"] = MessageCode, ["seconds"] = Math.Round(Seconds, 3) };
            if (Completed.HasValue) d["completed"] = Completed.Value;
            if (Total.HasValue) d["total"] = Total.Value;
            if (Error != null) d["error"] = new Dictionary<string, object> { ["category"] = Error.Category.ToString(), ["code"] = Error.Code, ["retryable"] = Error.Retryable };
            return d;
        }
    }

    // A consumer's view of one job's events: only the followed job, and only events newer than the last accepted one. A late event from a
    // cancelled job, or an event delivered out of order, is dropped and counted. One consumer thread (the UI's engine-thread tick).
    internal sealed class ProgressSink
    {
        private string jobId;
        private long last = -1;
        public int Accepted { get; private set; }
        public int DroppedStale { get; private set; }
        public int DroppedOutOfOrder { get; private set; }
        public ProgressEvent Latest { get; private set; }
        public string JobId { get { return jobId; } }

        public void Follow(string id) { jobId = id; last = -1; Latest = null; }
        public bool Accept(ProgressEvent e)
        {
            if (e == null || jobId == null || e.JobId != jobId) { DroppedStale++; return false; }
            if (e.Sequence <= last) { DroppedOutOfOrder++; return false; }
            last = e.Sequence; Latest = e; Accepted++;
            return true;
        }
    }
}
