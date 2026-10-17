using System;
using System.Collections.Generic;
using System.Linq;

namespace MNBL.BattleScale.Core
{
    public sealed class NativeProbeWorkload
    {
        public string Profile { get; private set; }
        public int SustainedSeconds { get; private set; }
        public int ReuseCycles { get; private set; }
        public int EntityCost { get { return Profile == "cavalry" ? 2 : 1; } }
        public string TroopId { get { return Profile == "cavalry" ? "imperial_cataphract" : Profile == "ranged" ? "imperial_archer" : "imperial_legionary"; } }
        public int[] Targets { get { return new[] { 512, 2040, EntityCost == 2 ? 2042 : 2041, 3072, 4080 }; } }
        public int TimeoutSeconds { get { return SustainedSeconds + 1200 + ReuseCycles * 120; } }

        public NativeProbeWorkload(string profile, int sustainedSeconds, int reuseCycles)
        {
            if (profile != "infantry" && profile != "cavalry" && profile != "ranged") throw new ArgumentException("Unknown native workload profile.", "profile");
            if (sustainedSeconds < 120 || sustainedSeconds > 1800) throw new ArgumentOutOfRangeException("sustainedSeconds", "Use120..1800 real seconds.");
            if (reuseCycles < 1 || reuseCycles > 20) throw new ArgumentOutOfRangeException("reuseCycles", "Use1..20 controlled64-slot cycles.");
            Profile = profile; SustainedSeconds = sustainedSeconds; ReuseCycles = reuseCycles;
        }

        public bool CanSpawn(int liveEntities, int target)
        {
            if (liveEntities < 0 || liveEntities > 4080 || target < 0 || target > 4080) throw new ArgumentOutOfRangeException("liveEntities");
            return liveEntities <= target - EntityCost && liveEntities <= 4080 - EntityCost;
        }
    }

    public sealed class NativeResourceCoverage
    {
        public int ValidSamples { get; private set; }
        public int CpuDeltas { get; private set; }
        public double? FirstSeconds { get; private set; }
        public double? LastSeconds { get; private set; }
        public bool InvalidSample { get; private set; }
        public void Observe(double combatSeconds, long? workingSet, long? privateBytes, double? cpu)
        {
            if (double.IsNaN(combatSeconds) || double.IsInfinity(combatSeconds) || combatSeconds < 0
                || (LastSeconds.HasValue && combatSeconds < LastSeconds.Value)
                || !workingSet.HasValue || workingSet.Value <= 0 || !privateBytes.HasValue || privateBytes.Value <= 0
                || (cpu.HasValue && (double.IsNaN(cpu.Value) || double.IsInfinity(cpu.Value) || cpu.Value < 0)))
            { InvalidSample = true; return; }
            ValidSamples++;
            if (cpu.HasValue) CpuDeltas++;
            if (!FirstSeconds.HasValue) FirstSeconds = combatSeconds;
            LastSeconds = combatSeconds;
        }
        public bool IsSufficient(int requestedSeconds)
        {
            return !InvalidSample && ValidSamples >= Math.Max(3, (int)Math.Ceiling(requestedSeconds / 5.0))
                && CpuDeltas >= 2 && FirstSeconds.HasValue && FirstSeconds.Value <= 2
                && LastSeconds.HasValue && LastSeconds.Value >= requestedSeconds - 2;
        }
    }

    public sealed class NativeFrameSummary
    {
        public int Samples { get; set; }
        public long DroppedSamples { get; set; }
        public double MeasuredIntervalSeconds { get; set; }
        public double? MedianMilliseconds { get; set; }
        public double? P95Milliseconds { get; set; }
        public double? P99Milliseconds { get; set; }
        public double? MaximumMilliseconds { get; set; }
        public double? MeanFramesPerSecond { get; set; }
    }

    // Exact nearest-rank percentiles of observed mission-screen tick intervals.
    // These include CPU scheduling/render-loop delays; they are not GPU timings.
    public sealed class NativeFrameSamples
    {
        private readonly List<double> milliseconds = new List<double>();
        private readonly int limit;
        private double previous = -1, sum;
        private long dropped;
        public NativeFrameSamples(int maximumSamples = 1000000)
        {
            if (maximumSamples < 1) throw new ArgumentOutOfRangeException("maximumSamples");
            limit = maximumSamples;
        }
        public void Observe(double realSeconds)
        {
            if (double.IsNaN(realSeconds) || double.IsInfinity(realSeconds) || realSeconds < 0 || realSeconds < previous)
                throw new ArgumentOutOfRangeException("realSeconds");
            if (previous >= 0)
            {
                double interval = (realSeconds - previous) * 1000;
                if (milliseconds.Count < limit) { milliseconds.Add(interval); sum += interval; }
                else dropped++;
            }
            previous = realSeconds;
        }
        public NativeFrameSummary Snapshot()
        {
            var sorted = milliseconds.OrderBy(x => x).ToArray();
            Func<double, double?> percentile = q => sorted.Length == 0 ? (double?)null : sorted[Math.Max(0, (int)Math.Ceiling(q * sorted.Length) - 1)];
            return new NativeFrameSummary { Samples = sorted.Length, DroppedSamples = dropped,
                MeasuredIntervalSeconds = sum / 1000, MedianMilliseconds = percentile(.5), P95Milliseconds = percentile(.95),
                P99Milliseconds = percentile(.99), MaximumMilliseconds = percentile(1),
                MeanFramesPerSecond = sum > 0 ? sorted.Length * 1000 / sum : (double?)null };
        }
    }
}
