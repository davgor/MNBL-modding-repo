using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using MNBL.BattleScale.Core;

internal static class CoreTests
{
    private static int assertions;
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--benchmark") return Benchmark();
            if (args.Length != 0) throw new ArgumentException("Supported argument: --benchmark");
            LargePopulationAndTransactions(); MountedBudget(); HysteresisAndConservation();
            VirtualCombat(); MixedCohortsDoNotDoubleFight(); DeterministicTicks();
            FractionalDamagePersists(); DeltaSpikesAndValidation(); FairAllocation(); LongPopulationCombat();
            NativePoolTeardownReceiptGate();
            SpawnAdmissionBoundaries();
            Console.WriteLine("PASS: " + assertions + " battle simulation assertions.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static BattleSimulation Create(int budget = 20)
    { return new BattleSimulation(new BattleSettings { EntityBudget = budget }); }

    private static void SpawnAdmissionBoundaries()
    {
        var live = new HashSet<int>();
        for (int i = 0; i < 4080; i++) live.Add(i);
        Check(SpawnAdmission.Check(4080, live.Count, 1, -1, live.Contains) == -1, "full public request rejected");
        Check(SpawnAdmission.Check(4080, live.Count, 2, -1, live.Contains) == -1, "full rider and horse request rejected");
        live.Remove(4079);
        Check(SpawnAdmission.Check(4080, live.Count, 1, -1, live.Contains) == 4079, "last infantry slot admitted");
        Check(SpawnAdmission.Check(4080, live.Count, 2, -1, live.Contains) == -1, "one slot cannot partially admit mounted pair");
        live.Remove(0);
        Check(SpawnAdmission.Check(4080, live.Count, 2, -1, live.Contains) == 0, "mounted pair admitted with two slots");
        Check(SpawnAdmission.Check(4080, live.Count, 2, 4079, live.Contains) == 4079, "valid explicit last slot admitted");
        Throws<ArgumentOutOfRangeException>(() => SpawnAdmission.Check(4080, 4078, 1, 4080, live.Contains));
        Throws<ArgumentOutOfRangeException>(() => SpawnAdmission.Check(4080, 4078, 1, -2, live.Contains));
        Throws<ArgumentException>(() => SpawnAdmission.Check(4080, 4078, 1, 1, live.Contains));
        Throws<ArgumentOutOfRangeException>(() => SpawnAdmission.Check(4080, 4081, 1, -1, live.Contains));
        Throws<InvalidOperationException>(() => SpawnAdmission.Check(4080, 0, 1, -1, i => true));
        Check(live.Count == 4078 && !live.Contains(0) && !live.Contains(4079), "admission checks never mutate allocator state");
    }

    private static void NativePoolTeardownReceiptGate()
    {
        var gate = new NativePoolProbeCompletion();
        Throws<InvalidOperationException>(() => gate.Begin(0, false));
        Throws<InvalidOperationException>(() => gate.ConfirmReceiptWritten());
        gate.Begin(10, true);
        Throws<InvalidOperationException>(() => gate.Begin(10, true));
        Check(gate.Observe(10, true) == NativePoolProbeCompletion.Action.Wait, "one teardown tick cannot pass");
        Check(gate.Observe(10.9, true) == NativePoolProbeCompletion.Action.Wait, "teardown must settle for one second");
        Check(gate.Observe(11, false) == NativePoolProbeCompletion.Action.Wait, "missing lifecycle evidence resets settling");
        Check(gate.Observe(11.5, true) == NativePoolProbeCompletion.Action.Wait, "restored evidence begins a new settling interval");
        Check(gate.Observe(12, true) == NativePoolProbeCompletion.Action.Wait, "old settling time cannot authorize a receipt");
        Check(gate.Observe(12.5, true) == NativePoolProbeCompletion.Action.WriteReceipt, "complete stable teardown authorizes receipt");
        Check(gate.Verified, "teardown verified before receipt");
        Check(gate.Observe(12.6, true) == NativePoolProbeCompletion.Action.WriteReceipt, "unconfirmed receipt cannot authorize exit");
        gate.ConfirmReceiptWritten();
        Throws<InvalidOperationException>(() => gate.ConfirmReceiptWritten());
        Check(gate.Observe(12.7, true) == NativePoolProbeCompletion.Action.RequestExit, "later application tick may exit after saved receipt");
        Throws<InvalidOperationException>(() => gate.Observe(12.8, false));
        var timeout = new NativePoolProbeCompletion(); timeout.Begin(0, true);
        Check(timeout.Observe(120, false) == NativePoolProbeCompletion.Action.Wait, "timeout bound is inclusive");
        Throws<TimeoutException>(() => timeout.Observe(120.001, false));
        var order = new NativePoolProbeCompletion(); order.Begin(2, true);
        Throws<InvalidOperationException>(() => order.Observe(1, true));
        Throws<ArgumentOutOfRangeException>(() => order.Observe(double.NaN, true));
        Throws<ArgumentOutOfRangeException>(() => order.Observe(double.PositiveInfinity, true));
    }
    private static FormationDefinition Definition(int id, long population, int team = 0, double x = 0)
    { return new FormationDefinition { Id = id, Population = population, Team = team, Position = new BattlePoint(x, 0) }; }
    private static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new Exception("Assertion failed: " + message); }
    private static void Conserved(FormationSnapshot f)
    { Check(f.Ready + f.Reserved + f.Active + f.Killed + f.Wounded == f.InitialPopulation, "population conservation " + f.Id); }
    private static void Throws<T>(Action action) where T : Exception
    {
        assertions++;
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void LargePopulationAndTransactions()
    {
        var sim = Create(); sim.AddFormation(Definition(1, 1000000));
        var plan = sim.Plan(new BattlePoint());
        Check(plan.SpawnTickets.Count == 20 && sim.UsedEntityBudget == 20, "population-independent reservation budget");
        Check(sim.Plan(new BattlePoint()).SpawnTickets.Count == 0, "repeated plan cannot duplicate reserved slots");
        var f = sim.GetFormation(1); Check(f.Ready == 999980 && f.Reserved == 20, "reserve debits ready population");
        long failed = plan.SpawnTickets[0].Id;
        Check(sim.CancelSpawn(failed), "failed engine spawn refunds reservation");
        Check(!sim.CancelSpawn(failed) && !sim.CommitSpawn(failed), "cancel is idempotent and invalidates ticket");
        Check(!sim.ReleaseAgent(plan.SpawnTickets[1].Id, AgentOutcome.Killed), "reserved ticket cannot be killed");
        for (int i = 1; i < plan.SpawnTickets.Count; i++) Check(sim.CommitSpawn(plan.SpawnTickets[i].Id), "commit spawn");
        long killed = plan.SpawnTickets[1].Id;
        Check(!sim.CommitSpawn(killed) && !sim.CancelSpawn(killed), "committed spawn is no longer a reservation");
        Check(sim.ReleaseAgent(killed, AgentOutcome.Killed), "release casualty");
        Check(!sim.ReleaseAgent(killed, AgentOutcome.Returned), "duplicate event cannot resurrect casualty");
        Check(sim.ReleaseAgent(plan.SpawnTickets[2].Id, AgentOutcome.Wounded), "release wounded");
        for (int i = 3; i < plan.SpawnTickets.Count; i++) Check(sim.ReleaseAgent(plan.SpawnTickets[i].Id, AgentOutcome.Returned), "demotion returns ready");
        f = sim.GetFormation(1);
        Check(f.Ready == 999998 && f.Killed == 1 && f.Wounded == 1 && sim.UsedEntityBudget == 0, "final outcome accounting"); Conserved(f);
        var huge = Create(2); huge.AddFormation(Definition(1, long.MaxValue));
        foreach (var t in huge.Plan(new BattlePoint()).SpawnTickets) { huge.CommitSpawn(t.Id); huge.ReleaseAgent(t.Id, AgentOutcome.Returned); }
        Check(huge.GetFormation(1).Ready == long.MaxValue, "long populations avoid overflow on reserve-return");
    }

    private static void MountedBudget()
    {
        var sim = Create(7); var mounted = Definition(1, 100000); mounted.Mounted = true; sim.AddFormation(mounted);
        var plan = sim.Plan(new BattlePoint());
        Check(plan.SpawnTickets.Count == 3 && sim.UsedEntityBudget == 6, "riders reserve horse entities");
        foreach (var t in plan.SpawnTickets) { Check(t.EntityCost == 2, "mounted cost"); sim.CancelSpawn(t.Id); }
        Check(sim.UsedEntityBudget == 0 && sim.GetFormation(1).Ready == 100000, "mounted cancellation refunds both slots");
        sim.AddFormation(Definition(2, 100000));
        plan = sim.Plan(new BattlePoint());
        Check(sim.UsedEntityBudget == 7, "infantry consumes odd budget remainder");
        foreach (var t in plan.SpawnTickets) { sim.CommitSpawn(t.Id); sim.ReleaseAgent(t.Id, AgentOutcome.Returned); }
        Conserved(sim.GetFormation(1)); Conserved(sim.GetFormation(2));
    }

    private static void HysteresisAndConservation()
    {
        var sim = Create(10); sim.AddFormation(Definition(1, 100000, 0, 125));
        Check(sim.Plan(new BattlePoint()).SpawnTickets.Count == 0, "outside promotion radius starts virtual");
        sim.SetPosition(1, new BattlePoint(100, 0));
        var plan = sim.Plan(new BattlePoint());
        foreach (var t in plan.SpawnTickets) sim.CommitSpawn(t.Id);
        sim.SetPosition(1, new BattlePoint(125, 0));
        Check(sim.Plan(new BattlePoint()).DemoteLeaseIds.Count == 0, "hysteresis retains active cohort");
        sim.SetPosition(1, new BattlePoint(151, 0));
        var far = sim.Plan(new BattlePoint());
        Check(far.DemoteLeaseIds.Count == 10 && sim.UsedEntityBudget == 10, "demotion request retains engine budget");
        Check(sim.GetFormation(1).Killed == 0, "demotion is not casualty");
        foreach (long id in far.DemoteLeaseIds) sim.ReleaseAgent(id, AgentOutcome.Returned);
        for (int i = 0; i < 100; i++)
        {
            sim.SetPosition(1, new BattlePoint());
            foreach (var t in sim.Plan(new BattlePoint()).SpawnTickets) sim.CommitSpawn(t.Id);
            sim.SetPosition(1, new BattlePoint(1000, 0));
            foreach (long id in sim.Plan(new BattlePoint()).DemoteLeaseIds) sim.ReleaseAgent(id, AgentOutcome.Returned);
            Conserved(sim.GetFormation(1));
        }
        Check(sim.GetFormation(1).Ready == 100000, "repeated promotion/demotion is lossless");
    }

    private static BattleSimulation CombatSimulation(double lethality = 1)
    {
        var sim = Create(10);
        var a = Definition(1, 100000, 0, 1000); a.CasualtiesPerFighterSecond = lethality; a.CombatFrontage = 100;
        var b = Definition(2, 100000, 1, 1010); b.CasualtiesPerFighterSecond = lethality; b.CombatFrontage = 100;
        sim.AddFormation(a); sim.AddFormation(b); sim.SetEngagement(1, 2); sim.SetEngagement(2, 1); return sim;
    }

    private static void VirtualCombat()
    {
        var sim = CombatSimulation(); Check(sim.Advance(1, new BattlePoint()) == 4, "fixed ticks");
        foreach (int id in new[] { 1, 2 })
        {
            var f = sim.GetFormation(id);
            Check(f.Ready == 99900 && f.Killed == 75 && f.Wounded == 25, "far combat resolves casualties and wounds"); Conserved(f);
        }
        sim.SetPosition(2, new BattlePoint(2000, 0)); sim.Advance(1, new BattlePoint());
        Check(sim.GetFormation(1).Ready == 99900, "out of range cohorts do not fight");
        sim.SetPosition(2, new BattlePoint(1010, 0)); sim.SetEngagement(2, null); sim.Advance(1, new BattlePoint());
        Check(sim.GetFormation(1).Ready == 99900 && sim.GetFormation(2).Ready == 99800, "explicit disengagement stops attacker only");
        var simultaneous = Create(0);
        var a = Definition(1, 1, 0, 1000); a.CasualtiesPerFighterSecond = 100; a.WoundedFraction = 0;
        var b = Definition(2, 1, 1, 1000); b.CasualtiesPerFighterSecond = 100; b.WoundedFraction = 0;
        simultaneous.AddFormation(a); simultaneous.AddFormation(b); simultaneous.SetEngagement(1, 2); simultaneous.SetEngagement(2, 1);
        simultaneous.Advance(.25, new BattlePoint());
        Check(simultaneous.GetFormation(1).Killed == 1 && simultaneous.GetFormation(2).Killed == 1, "simultaneous attacks do not favor insertion order");
    }

    private static void MixedCohortsDoNotDoubleFight()
    {
        var sim = CombatSimulation();
        var plan = sim.Plan(new BattlePoint(1000, 0));
        sim.Advance(1, new BattlePoint());
        Check(sim.GetFormation(1).Ready + sim.GetFormation(1).Reserved == 100000, "reserved cohort excluded from virtual combat");
        foreach (var t in plan.SpawnTickets) sim.CommitSpawn(t.Id);
        sim.Advance(1, new BattlePoint());
        Check(sim.GetFormation(1).Killed == 0 && sim.GetFormation(2).Killed == 0, "active cohorts excluded from both virtual attack and defense");
        foreach (var t in plan.SpawnTickets) sim.ReleaseAgent(t.Id, AgentOutcome.Returned);
        sim.Advance(1, new BattlePoint());
        Check(sim.GetFormation(1).Killed == 75, "fully virtual cohort resumes combat");
        var near = CombatSimulation(); near.Advance(1, new BattlePoint(1000, 0));
        Check(near.GetFormation(1).Killed == 0, "near budget overflow does not fight invisibly");
    }

    private static void DeterministicTicks()
    {
        var a = CombatSimulation(.123); var b = CombatSimulation(.123); var c = CombatSimulation(.123);
        for (int i = 0; i < 1000; i++) { a.Advance(.1, new BattlePoint()); b.Advance(.1, new BattlePoint()); }
        for (int i = 0; i < 400; i++) c.Advance(.25, new BattlePoint());
        foreach (int id in new[] { 1, 2 })
        {
            var fa = a.GetFormation(id); var fb = b.GetFormation(id); var fc = c.GetFormation(id);
            Check(fa.Ready == fb.Ready && fa.Killed == fb.Killed && fa.Wounded == fb.Wounded, "identical runs deterministic");
            Check(fa.Ready == fc.Ready && fa.Killed == fc.Killed && fa.Wounded == fc.Wounded, "frame partition does not affect fixed-tick outcomes"); Conserved(fa);
        }
        Check(a.TickCount == 400 && b.TickCount == 400 && c.TickCount == 400, "equivalent elapsed time yields equal ticks");
    }

    private static void FractionalDamagePersists()
    {
        var sim = CombatSimulation(.001);
        for (int i = 0; i < 39; i++) sim.Advance(.25, new BattlePoint());
        Check(sim.GetFormation(1).Ready == 100000, "subcasualty damage accumulates");
        sim.Advance(.25, new BattlePoint());
        Check(sim.GetFormation(1).Ready == 99999, "fractional damage eventually produces casualty");
    }

    private static void DeltaSpikesAndValidation()
    {
        var sim = CombatSimulation();
        Check(sim.Advance(1000, new BattlePoint()) == 8, "delta spike bounded to max catch-up");
        Check(sim.DroppedTimeSeconds == 998 && sim.GetFormation(1).Ready == 99800, "dropped time explicitly reported");
        Check(sim.Advance(0, new BattlePoint()) == 0, "discarded backlog cannot burst later");
        Throws<ArgumentOutOfRangeException>(() => sim.Advance(double.NaN, new BattlePoint()));
        Throws<ArgumentOutOfRangeException>(() => sim.Advance(double.PositiveInfinity, new BattlePoint()));
        Throws<ArgumentOutOfRangeException>(() => sim.Advance(-1, new BattlePoint()));
        Throws<ArgumentOutOfRangeException>(() => new BattlePoint(double.NaN, 0));
        Throws<ArgumentOutOfRangeException>(() => new BattleSimulation(new BattleSettings { EntityBudget = -1 }));
        Throws<ArgumentOutOfRangeException>(() => new BattleSimulation(new BattleSettings { DemotionDistance = 1 }));
        Throws<ArgumentOutOfRangeException>(() => new BattleSimulation(new BattleSettings { FixedStepSeconds = 0 }));
        Throws<ArgumentOutOfRangeException>(() => new BattleSimulation(new BattleSettings { MaxCatchUpSteps = 0 }));
        Throws<ArgumentOutOfRangeException>(() => sim.AddFormation(Definition(3, -1)));
        Throws<ArgumentException>(() => sim.AddFormation(Definition(1, 1)));
        Throws<ArgumentException>(() => sim.SetEngagement(1, 1));
        Throws<KeyNotFoundException>(() => sim.GetFormation(999));
        Throws<ArgumentOutOfRangeException>(() => sim.ReleaseAgent(1, (AgentOutcome)99));
        Check(!sim.CommitSpawn(999999) && !sim.CancelSpawn(999999) && !sim.ReleaseAgent(999999, AgentOutcome.Returned), "unknown event ids harmless");
        var zero = Create(0); zero.AddFormation(Definition(1, 0));
        Check(zero.Plan(new BattlePoint()).SpawnTickets.Count == 0, "empty population and zero budget");
    }

    private static void FairAllocation()
    {
        var sim = Create(20);
        for (int i = 0; i < 10; i++) sim.AddFormation(Definition(i, 100000));
        var plan = sim.Plan(new BattlePoint());
        for (int i = 0; i < 10; i++) Check(sim.GetFormation(i).Reserved == 2, "round-robin allocation avoids giant-cohort monopoly");
        foreach (var t in plan.SpawnTickets) sim.CancelSpawn(t.Id);
    }

    private static void LongPopulationCombat()
    {
        var sim = new BattleSimulation(new BattleSettings { EntityBudget = 2, FixedStepSeconds = 60 });
        var a = Definition(1, long.MaxValue, 0, 1000);
        var b = Definition(2, long.MaxValue, 1, 1010);
        a.CombatFrontage = b.CombatFrontage = 100000;
        a.CasualtiesPerFighterSecond = b.CasualtiesPerFighterSecond = 1000;
        a.WoundedFraction = 1; b.WoundedFraction = .5;
        sim.AddFormation(a); sim.AddFormation(b); sim.SetEngagement(1, 2); sim.SetEngagement(2, 1);
        sim.Advance(120, new BattlePoint());
        var fa = sim.GetFormation(1); var fb = sim.GetFormation(2);
        Check(fa.Ready == long.MaxValue - 12000000000L && fa.Wounded == 12000000000L && fa.Killed == 0,
            "long.MaxValue population and losses larger than Int32 preserve exact accounting");
        Check(fb.Ready == long.MaxValue - 12000000000L && fb.Wounded == 6000000000L && fb.Killed == 6000000000L,
            "large killed/wounded split remains exact");
        Conserved(fa); Conserved(fb);
        foreach (var t in sim.Plan(new BattlePoint(1000, 0)).SpawnTickets)
        { sim.CommitSpawn(t.Id); sim.ReleaseAgent(t.Id, AgentOutcome.Returned); }
        Check(sim.GetFormation(1).Ready == fa.Ready && sim.GetFormation(2).Ready == fb.Ready,
            "promotion and return preserve large survivor counts after combat");
    }

    // An opt-in managed-core workload, not Bannerlord frame rate or engine capacity.
    // Forced collection is outside the stopwatch. Memory is retained managed heap
    // change while this workload remains alive, not total allocation or peak RSS.
    private static int Benchmark()
    {
        RunBenchmark(100000, 400); // Excluded warm-up for every lifecycle branch and JIT initialization.
        string smaller = RunBenchmark(100000, 400);
        string larger = RunBenchmark(1000000, 400);
        Console.WriteLine("{\"benchmark\":\"battle-scale-managed-core\",\"scope\":\"headless simulation; no Bannerlord engine, rendering, physics, pathfinding or game FPS\","
            + "\"timing\":\"single sample; setup, 400 fixed ticks, lifecycle planning and conservation validation; excludes forced GC\","
            + "\"memoryMetric\":\"retained managed heap delta after full collection; not total allocation or peak memory\","
            + "\"warmupTicks\":400,\"cases\":[" + smaller + "," + larger + "]}");
        return 0;
    }

    private static string RunBenchmark(long population, int ticks)
    {
        const int formationCount = 200, budget = 512;
        long memoryBefore = GC.GetTotalMemory(true);
        var watch = Stopwatch.StartNew();
        var sim = new BattleSimulation(new BattleSettings { EntityBudget = budget });
        for (int i = 0; i < formationCount; i++)
        {
            var definition = Definition(i, population / formationCount, i % 2, (i / 2) * 100 + (i % 2) * 10);
            definition.Mounted = i % 5 == 0;
            sim.AddFormation(definition);
        }
        for (int i = 0; i < formationCount; i++) sim.SetEngagement(i, i ^ 1);
        var active = new List<long>();
        long offered = 0, committed = 0, cancelled = 0, returned = 0, activeCasualties = 0;
        int peakBudget = 0;
        for (int tick = 0; tick < ticks; tick++)
        {
            // Four observer locations exercise promotions, retained entities and
            // returns while most paired cohorts resolve abstract combat at range.
            var observer = new BattlePoint((tick / 100) * 2500, 0);
            sim.Advance(.25, observer);
            SimulationPlan plan = sim.Plan(observer);
            var demotions = new HashSet<long>(plan.DemoteLeaseIds);
            for (int i = active.Count - 1; i >= 0; i--)
            {
                long id = active[i];
                if (demotions.Contains(id))
                {
                    if (!sim.ReleaseAgent(id, AgentOutcome.Returned)) throw new Exception("Benchmark demotion failed.");
                    active.RemoveAt(i); returned++;
                }
                else if (tick > 0 && tick % 40 == 0 && id % 31 == 0)
                {
                    if (!sim.ReleaseAgent(id, id % 62 == 0 ? AgentOutcome.Wounded : AgentOutcome.Killed))
                        throw new Exception("Benchmark casualty release failed.");
                    active.RemoveAt(i); activeCasualties++;
                }
            }
            foreach (SpawnTicket ticket in plan.SpawnTickets)
            {
                offered++;
                // Deterministic failed engine-spawn stand-in: every eighth offer.
                if (offered % 8 == 0)
                {
                    if (!sim.CancelSpawn(ticket.Id)) throw new Exception("Benchmark cancellation failed.");
                    cancelled++;
                }
                else
                {
                    if (!sim.CommitSpawn(ticket.Id)) throw new Exception("Benchmark commit failed.");
                    active.Add(ticket.Id); committed++;
                }
            }
            peakBudget = Math.Max(peakBudget, sim.UsedEntityBudget);
            if (sim.UsedEntityBudget > budget) throw new Exception("Benchmark exceeded entity budget.");
        }
        foreach (long id in active)
        {
            if (!sim.ReleaseAgent(id, AgentOutcome.Returned)) throw new Exception("Benchmark final release failed.");
            returned++;
        }
        active.Clear();
        long ready = 0, killed = 0, wounded = 0;
        for (int i = 0; i < formationCount; i++)
        {
            FormationSnapshot f = sim.GetFormation(i);
            if (f.Ready + f.Active + f.Reserved + f.Killed + f.Wounded != f.InitialPopulation)
                throw new Exception("Benchmark lost population in formation " + i);
            ready += f.Ready; killed += f.Killed; wounded += f.Wounded;
        }
        bool conserved = ready + killed + wounded == population && sim.UsedEntityBudget == 0;
        if (!conserved || sim.TickCount != ticks) throw new Exception("Benchmark final state invalid.");
        watch.Stop();
        long memoryDelta = GC.GetTotalMemory(true) - memoryBefore;
        GC.KeepAlive(sim); GC.KeepAlive(active);
        return "{\"initialPopulation\":" + population + ",\"formations\":" + formationCount
            + ",\"entityBudget\":" + budget + ",\"mountedFormations\":40,\"fixedStepSeconds\":0.25,\"ticks\":" + sim.TickCount
            + ",\"elapsedMilliseconds\":" + watch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)
            + ",\"retainedManagedHeapDeltaBytes\":" + memoryDelta + ",\"peakUsedEntityBudget\":" + peakBudget
            + ",\"spawnOffers\":" + offered + ",\"committedSpawns\":" + committed + ",\"cancelledSpawns\":" + cancelled
            + ",\"returnedAgents\":" + returned + ",\"individualCasualties\":" + activeCasualties
            + ",\"ready\":" + ready + ",\"killed\":" + killed + ",\"wounded\":" + wounded
            + ",\"populationConserved\":true,\"finalUsedEntityBudget\":" + sim.UsedEntityBudget
            + ",\"droppedTimeSeconds\":" + sim.DroppedTimeSeconds.ToString("R", CultureInfo.InvariantCulture) + "}";
    }
}
