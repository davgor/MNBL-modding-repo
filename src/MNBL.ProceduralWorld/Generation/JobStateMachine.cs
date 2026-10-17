using System;
using System.Collections.Generic;
using System.Linq;

namespace MNBL.ProceduralWorld.Generation
{
    // MMR-44: the generation job's versioned state machine (docs/generation-job.md), pure managed and tested offline.
    //   Idle -> ValidatingRequest -> GeneratingSource -> Compiling -> ValidatingData -> BuildingScene -> RegisteringCampaign -> ValidatingRuntime
    //        -> ReadyForReview -> Committing -> Playing
    // A job moves only to the next phase, or to Cancelled (any phase up to ReadyForReview: before commit begins) or Failed (any running
    // phase). Once Committing begins it finishes (Playing) or recovers (Failed after MMR-45's recovery); it is never cancelled. Version 2
    // (MMR-46): a commit the player withdraws (left character creation before the campaign started; the activation rolled everything back)
    // returns the job to ReadyForReview, so the same world can be started again without generating it twice. Cancelled, Failed and Playing
    // are final. Every phase records its own timing (the job's clock), kept apart from hashed artifacts.
    internal enum JobPhase { Idle, ValidatingRequest, GeneratingSource, Compiling, ValidatingData, BuildingScene, RegisteringCampaign, ValidatingRuntime, ReadyForReview, Committing, Playing, Cancelled, Failed }

    // The phase groups the cancellation tests cover: source generation, compilation (bundle and its verification), engine-side construction.
    internal enum PhaseGroup { None, Source, Compilation, Engine, Review, Commit, Terminal }

    internal sealed class PhaseTiming
    {
        public JobPhase Phase;
        public double Start, End = -1;
        public string Outcome;
        public double Seconds { get { return End < 0 ? -1 : End - Start; } }
        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object> { ["phase"] = Phase.ToString(), ["start"] = Math.Round(Start, 3), ["end"] = End < 0 ? (object)null : Math.Round(End, 3),
                ["seconds"] = End < 0 ? (object)null : Math.Round(Seconds, 3), ["outcome"] = Outcome };
        }
    }

    internal sealed class JobStateMachine
    {
        public const int Version = 2;
        public static readonly JobPhase[] Pipeline = { JobPhase.ValidatingRequest, JobPhase.GeneratingSource, JobPhase.Compiling, JobPhase.ValidatingData, JobPhase.BuildingScene,
            JobPhase.RegisteringCampaign, JobPhase.ValidatingRuntime, JobPhase.ReadyForReview, JobPhase.Committing, JobPhase.Playing };
        private readonly Func<double> clock;
        private readonly List<PhaseTiming> timings = new List<PhaseTiming>();

        public JobStateMachine(Func<double> clock) { this.clock = clock; Phase = JobPhase.Idle; }

        public JobPhase Phase { get; private set; }
        public IList<PhaseTiming> Timings { get { return timings.AsReadOnly(); } }

        public static PhaseGroup Group(JobPhase phase)
        {
            switch (phase)
            {
                case JobPhase.ValidatingRequest: case JobPhase.GeneratingSource: return PhaseGroup.Source;
                case JobPhase.Compiling: case JobPhase.ValidatingData: return PhaseGroup.Compilation;
                case JobPhase.BuildingScene: case JobPhase.RegisteringCampaign: case JobPhase.ValidatingRuntime: return PhaseGroup.Engine;
                case JobPhase.ReadyForReview: return PhaseGroup.Review;
                case JobPhase.Committing: return PhaseGroup.Commit;
                case JobPhase.Playing: case JobPhase.Cancelled: case JobPhase.Failed: return PhaseGroup.Terminal;
                default: return PhaseGroup.None;
            }
        }

        public static bool IsTerminal(JobPhase phase) { return phase == JobPhase.Playing || phase == JobPhase.Cancelled || phase == JobPhase.Failed; }
        // Before commit begins a job can be cancelled; from Committing it finishes or recovers.
        public static bool Cancellable(JobPhase phase) { return phase >= JobPhase.ValidatingRequest && phase <= JobPhase.ReadyForReview; }
        public static JobPhase Next(JobPhase phase)
        {
            if (phase == JobPhase.Idle) return JobPhase.ValidatingRequest;
            int at = Array.IndexOf(Pipeline, phase);
            if (at < 0 || at + 1 >= Pipeline.Length) throw new InvalidOperationException(phase + " has no next phase.");
            return Pipeline[at + 1];
        }

        public bool CanAdvance(JobPhase next, out string refusal)
        {
            refusal = null;
            var from = Phase;
            if (IsTerminal(from)) refusal = "The job is final (" + from + ").";
            else if (next == JobPhase.Cancelled) { if (!Cancellable(from)) refusal = from == JobPhase.Idle ? "Nothing has started." : "Commit has begun: the job finishes or recovers, it is not cancelled."; }
            else if (next == JobPhase.Failed) { if (from == JobPhase.Idle) refusal = "Nothing has started."; }
            else if (next == JobPhase.ReadyForReview && from == JobPhase.Committing) { }   // a withdrawn commit (version 2)
            else if (next != Next(from)) refusal = "A job moves from " + from + " only to " + Next(from) + ", Cancelled or Failed, not " + next + ".";
            return refusal == null;
        }

        private static string from(JobPhase open, JobPhase next) { return open == JobPhase.Committing && next == JobPhase.ReadyForReview ? "withdrawn" : "completed"; }

        public void Advance(JobPhase next, string outcome = null)
        {
            string refusal;
            if (!CanAdvance(next, out refusal)) throw new InvalidOperationException(refusal);
            double now = clock();
            var open = timings.LastOrDefault();
            if (open != null && open.End < 0) { open.End = now; open.Outcome = outcome ?? (next == JobPhase.Cancelled ? "cancelled" : next == JobPhase.Failed ? "failed" : from(open.Phase, next)); }
            Phase = next;
            if (!IsTerminal(next)) timings.Add(new PhaseTiming { Phase = next, Start = now });
        }
    }
}
