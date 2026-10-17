using System;
using System.Linq;
using MNBL.BattleScale.Core;

internal static class NativeWorkloadTests
{
    private static int checks;
    private static void Check(bool value) { checks++; if (!value) throw new Exception("Workload assertion " + checks); }
    private static void Reject(Action value) { checks++; try { value(); } catch (ArgumentException) { return; } throw new Exception("Missing input rejection " + checks); }
    public static int Main()
    {
        try
        {
            var cavalry = new NativeProbeWorkload("cavalry", 300, 3);
            Check(cavalry.EntityCost == 2 && cavalry.Targets.All(x => x % 2 == 0));
            Check(cavalry.CanSpawn(4078, 4080) && !cavalry.CanSpawn(4079, 4080) && !cavalry.CanSpawn(4080, 4080));
            Check(!cavalry.CanSpawn(511, 512));
            Check(new NativeProbeWorkload("ranged",120,1).Targets.Contains(2041));
            Check(new NativeProbeWorkload("infantry",120,1).CanSpawn(4079,4080));
            Reject(() => new NativeProbeWorkload("unknown",300,3));
            Reject(() => new NativeProbeWorkload("infantry",119,3));
            Reject(() => new NativeProbeWorkload("infantry",1801,3));
            Reject(() => new NativeProbeWorkload("infantry",300,0));
            Reject(() => cavalry.CanSpawn(4081,4080));
            var frames = new NativeFrameSamples();
            Check(frames.Snapshot().MedianMilliseconds == null);
            frames.Observe(0);
            for (int i=1;i<=99;i++) frames.Observe(i*.01);
            frames.Observe(1.99);
            var summary=frames.Snapshot();
            Check(summary.Samples==100 && Math.Abs(summary.MedianMilliseconds.Value-10)<1e-8);
            Check(Math.Abs(summary.P95Milliseconds.Value-10)<1e-8 && Math.Abs(summary.P99Milliseconds.Value-10)<1e-8);
            Check(Math.Abs(summary.MaximumMilliseconds.Value-1000)<1e-8);
            Check(Math.Abs(summary.MeanFramesPerSecond.Value-100/1.99)<1e-8);
            Reject(() => frames.Observe(1)); Reject(() => frames.Observe(double.NaN)); Reject(() => frames.Observe(double.PositiveInfinity));
            var bounded=new NativeFrameSamples(2); bounded.Observe(1);bounded.Observe(2);bounded.Observe(3);bounded.Observe(4);
            Check(bounded.Snapshot().Samples==2 && bounded.Snapshot().DroppedSamples==1);
            var resources = new NativeResourceCoverage();
            Check(!resources.IsSufficient(120));
            for (int i=0;i<=120;i++) resources.Observe(i, 1000, 2000, i == 0 ? (double?)null : 1.5);
            Check(resources.IsSufficient(120) && resources.CpuDeltas==120);
            var missingCpu = new NativeResourceCoverage();
            for (int i=0;i<=120;i++) missingCpu.Observe(i,1000,2000,null);
            Check(!missingCpu.IsSufficient(120));
            var sparse = new NativeResourceCoverage();
            sparse.Observe(0,1000,2000,null); sparse.Observe(60,1000,2000,1); sparse.Observe(120,1000,2000,1);
            Check(!sparse.IsSufficient(120));
            var missingStart = new NativeResourceCoverage();
            for (int i=20;i<=120;i++) missingStart.Observe(i,1000,2000,1);
            Check(!missingStart.IsSufficient(120));
            Check(!resources.IsSufficient(300));
            resources.Observe(121,null,2000,1);
            Check(resources.InvalidSample && !resources.IsSufficient(120));
            var invalidCpu = new NativeResourceCoverage(); invalidCpu.Observe(0,1000,2000,double.NaN);
            Check(invalidCpu.InvalidSample);
            Console.WriteLine("PASS: "+checks+" native workload assertions.");return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error);return 1; }
    }
}
