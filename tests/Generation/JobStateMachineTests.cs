using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.Generation;
using MNBL.ProceduralWorld.UI;

// MMR-44 generation job tests, without the game:
//   JobStateMachineTests REPO OUT_JSON
// The versioned state machine (legal and refused transitions, cancellation only before commit, timings), the progress sink (only the
// followed job, only in order), the progress events against worldgen/contracts/progress-event.schema.json, the engine-work scheduler (engine
// thread only, per-tick budget, capacity, withdrawal, stale tickets dropped unrun), and the coordinator over scripted stages on a stand-in
// engine thread: the whole pipeline to review; cancellation in each phase group (source, compilation, engine: queued and running engine
// work) with owned resources released in reverse order; stale callbacks of a cancelled job against a later job; failures with their
// categories; review cancellation; commit to Playing and a failed commit's recovery.
internal static class JobStateMachineTests
{
    private static int checks, failures;
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; Console.WriteLine((condition ? "ok    " : "FAIL  ") + label); }
    private static readonly Stopwatch clock = Stopwatch.StartNew();
    private static double Now() { return clock.Elapsed.TotalSeconds; }

    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("usage: JobStateMachineTests REPO OUT_JSON"); return 2; }
        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.generation-job-tests", ["version"] = 1, ["ticket"] = "MMR-44" };
        try { Run(args[0], evidence); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        evidence["checks"] = checks; evidence["failures"] = failures;
        File.WriteAllText(args[1], new JavaScriptSerializer().Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string repo, SortedDictionary<string, object> evidence)
    {
        // 1. The state machine.
        var m = new JobStateMachine(Now);
        var path = new List<string>();
        foreach (var p in JobStateMachine.Pipeline) { m.Advance(p); path.Add(p.ToString()); }
        string refusal;
        var skip = new JobStateMachine(Now); skip.Advance(JobPhase.ValidatingRequest);
        bool skipRefused = !skip.CanAdvance(JobPhase.Compiling, out refusal) && !skip.CanAdvance(JobPhase.ValidatingRequest, out refusal);
        bool cancelEach = JobStateMachine.Pipeline.TakeWhile(p => p != JobPhase.Committing).All(p => { var x = To(p); return x.CanAdvance(JobPhase.Cancelled, out refusal); });
        bool noCancelInCommit = !To(JobStateMachine.Pipeline.First(p => p == JobPhase.Committing)).CanAdvance(JobPhase.Cancelled, out refusal) && To(JobPhase.Committing).CanAdvance(JobPhase.Failed, out refusal);
        bool idle = !new JobStateMachine(Now).CanAdvance(JobPhase.Cancelled, out refusal) && !new JobStateMachine(Now).CanAdvance(JobPhase.Failed, out refusal);
        var done = To(JobPhase.ValidatingRuntime); done.Advance(JobPhase.Failed);
        bool final = !done.CanAdvance(JobPhase.Cancelled, out refusal) && !done.CanAdvance(JobPhase.Failed, out refusal) && !m.CanAdvance(JobPhase.Failed, out refusal);
        // Version 2 (MMR-46): a withdrawn commit returns Committing to ReadyForReview, timed as "withdrawn"; nothing else goes back.
        var withdraw = To(JobPhase.Committing); withdraw.Advance(JobPhase.ReadyForReview);
        bool withdrawnCommit = withdraw.Phase == JobPhase.ReadyForReview && withdraw.Timings.Last(t => t.Phase == JobPhase.Committing).Outcome == "withdrawn" && withdraw.CanAdvance(JobPhase.Committing, out refusal)
            && !To(JobPhase.ValidatingRuntime).CanAdvance(JobPhase.BuildingScene, out refusal) && !To(JobPhase.ReadyForReview).CanAdvance(JobPhase.ValidatingRuntime, out refusal);
        Check(m.Phase == JobPhase.Playing && skipRefused && cancelEach && noCancelInCommit && idle && final && withdrawnCommit && m.Timings.Count == 9 && m.Timings.All(t => t.End >= t.Start && t.Outcome == "completed")
              && JobStateMachine.Version == 2,
              "state machine v2: Idle -> " + String.Join(" -> ", path) + "; only the next phase, Cancelled before commit (each phase up to ReadyForReview), Failed from any running phase; Committing never cancels; a withdrawn commit returns to ReadyForReview (and nothing else goes back); final states stay final; each phase timed");
        Check(JobStateMachine.Group(JobPhase.GeneratingSource) == PhaseGroup.Source && JobStateMachine.Group(JobPhase.ValidatingData) == PhaseGroup.Compilation
              && JobStateMachine.Group(JobPhase.BuildingScene) == PhaseGroup.Engine && JobStateMachine.Group(JobPhase.ValidatingRuntime) == PhaseGroup.Engine && JobStateMachine.Group(JobPhase.Committing) == PhaseGroup.Commit,
              "phase groups: source (request, generation), compilation (bundle, verification), engine (scene, registration, runtime validation), review, commit");

        // 2. The progress sink.
        var sink = new ProgressSink(); sink.Follow("b");
        var accepted = new[] { Ev("a", 5), Ev("b", 0), Ev("b", 2), Ev("b", 1), Ev("b", 2), Ev("b", 3), Ev("a", 9) }.Count(sink.Accept);
        Check(accepted == 3 && sink.DroppedStale == 2 && sink.DroppedOutOfOrder == 2 && sink.Latest.Sequence == 3,
              "the progress sink takes only the followed job's events and only newer ones: another job's are stale, a repeated or older sequence is out of order");

        // 3. The scheduler.
        using (var engine = new FakeEngine())
        {
            var s = engine.Scheduler;
            var ticket = JobTicket.Begin("schedulerjob");
            var h = s.Post(ticket, "where", () => Thread.CurrentThread.ManagedThreadId);
            h.Task.Wait(2000);
            int off = s.Pump();   // this thread is not the engine thread
            // The guard: off the engine thread Pump runs nothing (counted), the item waits for the engine thread.
            engine.Paused = true; Thread.Sleep(20);
            var guarded = s.Post(ticket, "guarded", () => Thread.CurrentThread.ManagedThreadId);
            long violationsBefore = s.AffinityViolations;
            int offWithWork = s.Pump();
            bool affinityGuard = offWithWork == 0 && s.AffinityViolations == violationsBefore + 1 && !guarded.Task.IsCompleted;
            engine.Paused = false; guarded.Task.Wait(2000);
            affinityGuard &= guarded.Task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && guarded.Task.Result == engine.ThreadId;
            engine.Paused = true; Thread.Sleep(20);
            var slow = Enumerable.Range(0, 6).Select(i => s.Post(ticket, "slow " + i, () => { Thread.Sleep(5); return i; })).ToList();
            int firstPump = engine.PumpOnce();
            var withdrawn = s.Post(ticket, "withdrawn", () => -1);
            bool withdrew = withdrawn.TryWithdraw();
            engine.Paused = false;
            Wait(() => slow.All(x => x.Task.IsCompleted), 3000);
            var later = JobTicket.Begin("laterjob");
            var stale = s.Post(ticket, "stale", () => -2);
            Wait(() => stale.Task.IsCompleted, 3000);
            bool capacityRefused = false;
            var small = new EngineWorkScheduler(() => false, Now, 0.008, 2);
            small.Post<int>(null, "1", () => 1); small.Post<int>(null, "2", () => 2);
            try { small.Post<int>(null, "3", () => 3); } catch (InvalidOperationException) { capacityRefused = true; }
            Check(h.Task.Result == engine.ThreadId && off == 0 && affinityGuard && firstPump >= 1 && firstPump <= 2 && slow.All(x => x.Task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion),
                  "engine work runs only on the engine thread (a pump from another thread runs nothing and is counted), within the per-tick budget (" + firstPump + " of 6 five-millisecond items in one 8 ms pump)");
            Check(withdrew && withdrawn.Task.IsCanceled && stale.Task.IsFaulted && stale.Task.Exception.GetBaseException() is StaleWorkException && s.DroppedStale == 1 && capacityRefused && !ticket.Live && later.Live,
                  "a queued item can be withdrawn unrun; an item whose job ticket is no longer live (a later job began) is dropped unrun; a full queue refuses work");
            later.End();
            // The probes' hold: job items stay queued, teardown items (no ticket) still run.
            var holdTicket = JobTicket.Begin("holdjob");
            s.Held = true;
            var kept = s.Post(holdTicket, "kept", () => 1);
            var teardown = s.Post<int>(null, "teardown", () => 2);
            Wait(() => teardown.Task.IsCompleted, 2000); Thread.Sleep(30);
            bool heldOk = teardown.Task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && !kept.Task.IsCompleted;
            s.Held = false; Wait(() => kept.Task.IsCompleted, 2000);
            heldOk &= kept.Task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion;
            holdTicket.End();
            Check(heldOk, "the probes' hold keeps job items queued while teardown items run, and releases them when lifted");
        }

        // 4. The coordinator over scripted stages.
        string root = Path.Combine(Path.GetTempPath(), "mnbl-job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var schema = (IDictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen/contracts/progress-event.schema.json")));
        var schemaProblems = new List<string>();
        using (var engine = new FakeEngine())
        {
            var log = new List<string>();
            Func<JobPhase, Action<GenerationJob>, IGenerationStage> stage = (phase, body) => new ScriptedStage { Phase = phase, Body = body };
            Func<Dictionary<JobPhase, Action<GenerationJob>>, GenerationCoordinator> make = overrides =>
            {
                var stages = JobStateMachine.Pipeline.TakeWhile(p => p != JobPhase.ReadyForReview).Select(p =>
                {
                    Action<GenerationJob> body;
                    if (overrides != null && overrides.TryGetValue(p, out body)) return stage(p, body);
                    return stage(p, sj =>
                    {
                        sj.Own("res-" + p, () => { lock (log) log.Add("release " + p); });
                        if (p == JobPhase.GeneratingSource) { sj.Report(0, 3, "attempt_running"); sj.Report(1, 3, "attempt_rejected"); }
                        if (p == JobPhase.BuildingScene) sj.Set("engineThread", sj.Engine("install", () => Thread.CurrentThread.ManagedThreadId));
                    });
                }).ToList();
                return new GenerationCoordinator(stages, engine.Scheduler, Now, root, (e, j) => GenerationErrors.FromGeneration(null, e, null, e.Message)) { TeardownTimeoutSeconds = 10 };
            };
            var choice = new WorldChoice { CampaignInstanceId = Guid.NewGuid().ToString("D"), Seed = 42, SeedSource = "explicit", RecipeVersion = 3, MaxAttempts = 3,
                Overrides = new SortedDictionary<string, long> { ["phase"] = 16, ["size"] = 129 }, ProducerLockSha256 = new string('0', 64), Profile = "one-shot" };

            // 4a. The whole pipeline to review.
            var events = new List<ProgressEvent>();
            var c = make(null);
            var job = c.Start(choice, e => { lock (events) events.Add(e); });
            Settle(job, JobPhase.ReadyForReview);
            var starts = events.Where(e => e.MessageCode == "phase_started").Select(e => e.Phase).ToList();
            foreach (var e in events) schemaProblems.AddRange(Validate(schema, e.ToDictionary()));
            var record = c.Record(job);
            Check(job.Phase == JobPhase.ReadyForReview && starts.SequenceEqual(JobStateMachine.Pipeline.TakeWhile(p => p != JobPhase.ReadyForReview)) && events.Last().MessageCode == "ready_for_review"
                  && events.Select(e => e.Sequence).SequenceEqual(Enumerable.Range(0, events.Count).Select(i => (long)i)) && events.Any(e => e.Completed == 1 && e.Total == 3)
                  && job.Get<int>("engineThread") == engine.ThreadId && job.OffEngineViolations == 0 && job.EngineViolations == 0 && job.Timings.Count == 8
                  && File.Exists(job.File("timings.json")) && File.ReadAllLines(job.File("events.jsonl")).Length == events.Count && job.OwnedCount == 7,
                  "a job runs every phase in order to review: a start event per phase, the stages' progress (attempt 1 of 3), sequence 0.." + (events.Count - 1) + " without gaps, engine work on the engine thread, timings and events written to the job, its resources still held for review");
            Check(schemaProblems.Count == 0, "every progress event validates against progress-event.schema.json" + (schemaProblems.Count > 0 ? ": " + String.Join("; ", schemaProblems.Distinct().Take(5)) : ""));
            evidence["pipelineEvents"] = events.Select(e => e.ToDictionary()).ToList();
            evidence["pipelineRecord"] = record;
            bool refusedSecond = false; try { c.Start(choice, null); } catch (InvalidOperationException) { refusedSecond = true; }
            lock (log) log.Clear();
            c.Cancel(job); Settle(job, JobPhase.Cancelled);
            List<string> released; lock (log) released = log.ToList();
            c.Release(job).Wait(5000);
            Check(refusedSecond && job.Phase == JobPhase.Cancelled && released.SequenceEqual(JobStateMachine.Pipeline.TakeWhile(p => p != JobPhase.ReadyForReview).Reverse().Select(p => "release " + p)) && !Directory.Exists(job.Root),
                  "a second job is refused while one awaits review; cancelling at review releases every resource in reverse order of acquisition; releasing the job deletes its directory");

            // 4b. Cancellation in each phase group.
            var groups = new List<object>(); evidence["cancellations"] = groups;
            foreach (var target in new[] { JobPhase.GeneratingSource, JobPhase.Compiling, JobPhase.BuildingScene })
            {
                lock (log) log.Clear();
                var reached = new ManualResetEventSlim(false);
                int runs = 0;
                // The first job blocks in the target phase until cancelled; the next job passes through.
                var cc = make(new Dictionary<JobPhase, Action<GenerationJob>> { [target] = j => { if (Interlocked.Increment(ref runs) > 1) return; j.Own("res-" + target, () => { lock (log) log.Add("release " + target); }); reached.Set(); while (true) { j.Token.ThrowIfCancellationRequested(); Thread.Sleep(5); } } });
                var evs = new List<ProgressEvent>();
                var jj = cc.Start(choice, e => { lock (evs) evs.Add(e); });
                reached.Wait(5000);
                bool cancelled = cc.Cancel(jj);
                Settle(jj, JobPhase.Cancelled);
                List<string> rel; lock (log) rel = log.ToList();
                var expected = JobStateMachine.Pipeline.TakeWhile(p => p <= target).Reverse().Select(p => "release " + p).ToList();
                bool ok = cancelled && jj.Phase == JobPhase.Cancelled && rel.SequenceEqual(expected) && jj.OwnedCount == 0 && !jj.Ticket.Live && evs.Last().Phase == JobPhase.Cancelled && evs.Last().MessageCode == "cancelled"
                          && Directory.Exists(jj.Root);
                var next = cc.Start(choice, null); Settle(next, JobPhase.ReadyForReview);
                ok &= next.Phase == JobPhase.ReadyForReview;
                cc.Release(next).Wait(5000); cc.Release(jj).Wait(5000);
                groups.Add(new Dictionary<string, object> { ["phase"] = target.ToString(), ["group"] = JobStateMachine.Group(target).ToString(), ["released"] = rel, ["ok"] = ok });
                Check(ok, "cancelled during " + target + " (" + JobStateMachine.Group(target) + " group): Cancelled, exactly its own resources released in reverse order (" + String.Join(", ", rel) + "), its ticket ended, the next job runs");
            }

            // Engine work queued (withdrawn) and running (finished, its result torn down on the engine thread) when the job is cancelled.
            engine.Paused = true;
            var queuedPosted = new ManualResetEventSlim(false); bool queuedRan = false;
            var cq = make(new Dictionary<JobPhase, Action<GenerationJob>> { [JobPhase.BuildingScene] = j => { queuedPosted.Set(); j.Engine("queued", () => { queuedRan = true; return 1; }); } });
            var jq = cq.Start(choice, null);
            queuedPosted.Wait(5000); Wait(() => engine.Scheduler.Pending > 0, 3000);
            long withdrawnBefore = engine.Scheduler.Withdrawn;
            cq.Cancel(jq); Settle(jq, JobPhase.Cancelled);
            engine.Paused = false; Wait(() => engine.Scheduler.Pending == 0, 3000); Thread.Sleep(50);
            var running = new ManualResetEventSlim(false); int discardedOn = 0; bool resultSeen = false;
            var cr = make(new Dictionary<JobPhase, Action<GenerationJob>> { [JobPhase.ValidatingRuntime] = j => { j.Engine("slow construct", () => { running.Set(); Thread.Sleep(300); return "world"; }, w => { discardedOn = Thread.CurrentThread.ManagedThreadId; }); resultSeen = true; } });
            var jr = cr.Start(choice, null);
            running.Wait(5000); cr.Cancel(jr); Settle(jr, JobPhase.Cancelled);
            Check(jq.Phase == JobPhase.Cancelled && !queuedRan && engine.Scheduler.Withdrawn > withdrawnBefore && jr.Phase == JobPhase.Cancelled && !resultSeen && discardedOn == engine.ThreadId,
                  "engine-side cancellation: queued engine work is withdrawn unrun; running engine work finishes and what it made is torn down on the engine thread before the job stops");
            cq.Release(jq).Wait(5000); cr.Release(jr).Wait(5000);

            // 4c. Stale callbacks of a cancelled job against a later job.
            var hold = new ManualResetEventSlim(false);
            var cs = make(new Dictionary<JobPhase, Action<GenerationJob>> { [JobPhase.BuildingScene] = j => { hold.Set(); while (true) { j.Token.ThrowIfCancellationRequested(); Thread.Sleep(5); } } });
            var a = cs.Start(choice, null);
            hold.Wait(5000); cs.Cancel(a); Settle(a, JobPhase.Cancelled);
            var setupSink = new ProgressSink();
            var bEvents = new List<ProgressEvent>();
            var b = cs.Start(choice, e => { lock (bEvents) bEvents.Add(e); });
            setupSink.Follow(b.Id);
            bool lateRan = false;
            var late = engine.Scheduler.Post(a.Ticket, "late callback of the cancelled job", () => { lateRan = true; return 0; });
            a.Report(null, null, "late_progress");
            var lateEvent = a.Events.Last();
            Wait(() => late.Task.IsCompleted, 3000);
            Wait(() => b.Phase == JobPhase.ReadyForReview || b.Phase == JobPhase.BuildingScene, 5000);
            bool lateDropped = !setupSink.Accept(lateEvent);
            Check(a.Phase == JobPhase.Cancelled && !lateRan && late.Task.IsFaulted && late.Task.Exception.GetBaseException() is StaleWorkException && lateDropped && a.Ticket.Serial < b.Ticket.Serial,
                  "stale callbacks: engine work a cancelled job posts after a later job began is dropped unrun, and its late progress event is dropped by the later job's consumer");
            cs.Cancel(b); SettleFinal(b); cs.Release(b).Wait(5000); cs.Release(a).Wait(5000);

            // 4d. Failures.
            var cf = make(new Dictionary<JobPhase, Action<GenerationJob>> { [JobPhase.Compiling] = j => { j.Own("bundle", () => { lock (log) log.Add("release bundle"); }); throw new GenerationFailure(GenerationErrors.Job(GenerationErrorCategory.BadSource, "compile.failed", "exit 2")); } });
            lock (log) log.Clear();
            var jf = cf.Start(choice, null); SettleFinal(jf);
            List<string> relf; lock (log) relf = log.ToList();
            var ci = make(new Dictionary<JobPhase, Action<GenerationJob>> { [JobPhase.ValidatingData] = j => { throw new IOException("full", unchecked((int)0x80070070)); } });
            var ji = ci.Start(choice, null); SettleFinal(ji);
            Check(jf.Phase == JobPhase.Failed && jf.Error.Category == GenerationErrorCategory.BadSource && jf.Error.Code == "compile.failed" && relf.First() == "release bundle" && relf.Last() == "release ValidatingRequest"
                  && ji.Phase == JobPhase.Failed && ji.Error.Category == GenerationErrorCategory.DiskFull && jf.Events.Last().Error != null,
                  "a failing stage fails the job in its category (a stage's own error, or the classified exception: disk full), releasing what it held; the final event carries the structured error");
            cf.Release(jf).Wait(5000); ci.Release(ji).Wait(5000);

            // 4e. Commit (MMR-45's stage): to Playing, never cancelled; a failed commit recovers, then fails.
            var cm = make(null);
            var jc = cm.Start(choice, null); Settle(jc, JobPhase.ReadyForReview);
            var commit = new ScriptedCommit { Body = j => { Thread.Sleep(100); j.Disown("res-BuildingScene"); } };
            lock (log) log.Clear();
            var committing = cm.Commit(jc, commit);
            bool noCancel = !cm.Cancel(jc);
            committing.Wait(5000);
            List<string> relc; lock (log) relc = log.ToList();
            var jx = cm.Start(choice, null); Settle(jx, JobPhase.ReadyForReview);
            var failing = new ScriptedCommit { Body = j => { throw new InvalidOperationException("activation failed"); } };
            cm.Commit(jx, failing).Wait(5000);
            Check(jc.Phase == JobPhase.Playing && noCancel && !relc.Contains("release BuildingScene") && relc.Contains("release ValidatingRequest") && jx.Phase == JobPhase.Failed && failing.Recovered,
                  "commit: from review only, never cancelled; Playing keeps what the commit took over and releases the remaining staging; a failed commit recovers before the job fails");
            // MMR-46: a withdrawn commit (the player left before the campaign started) returns the job to review with everything it held; it can
            // be committed again.
            var jw = cm.Start(choice, null); Settle(jw, JobPhase.ReadyForReview);
            int ownedBefore = jw.OwnedCount;
            var withdrawing = new ScriptedCommit { Body = j => { throw new CommitWithdrawn("the player left character creation"); } };
            lock (log) log.Clear();
            cm.Commit(jw, withdrawing).Wait(5000);
            List<string> relw; lock (log) relw = log.ToList();
            bool backToReview = jw.Phase == JobPhase.ReadyForReview && withdrawing.Recovered && jw.OwnedCount == ownedBefore && relw.Count == 0 && jw.Events.Last().MessageCode == "commit_withdrawn";
            var again = new ScriptedCommit { Body = j => { } };
            cm.Commit(jw, again).Wait(5000);
            Check(backToReview && jw.Phase == JobPhase.Playing,
                  "commit withdrawn: the job returns to review keeping all " + ownedBefore + " owned resources (none released), the event says commit_withdrawn, and the same world commits on the next try");
            cm.Release(jc).Wait(5000); cm.Release(jx).Wait(5000); cm.Release(jw).Wait(5000);

            // 4f. One job at a time across releases: while a job's release runs, a new job is refused as busy; AwaitIdle waits it out. A
            // release during a commit waits for the commit to finish.
            var cb = make(new Dictionary<JobPhase, Action<GenerationJob>> { [JobPhase.ValidatingRequest] = j => j.Own("slow resource", () => Thread.Sleep(400)) });
            var jb = cb.Start(choice, null); Settle(jb, JobPhase.ReadyForReview);
            var releasingB = cb.Release(jb);
            bool busy = false; try { cb.Start(choice, null); } catch (GenerationBusyException) { busy = true; }
            bool awaitedIdle = cb.AwaitIdle(TimeSpan.FromSeconds(5));
            var jn = cb.Start(choice, null); Settle(jn, JobPhase.ReadyForReview);
            bool sameRelease = cb.Release(jn) == cb.Release(jn);
            cb.Release(jn).Wait(5000);
            // MMR-62: a process that may not write to the store (another game process holds its lock) is refused before any job exists.
            var ca = make(null); ca.Admission = () => "another game process is using the world store";
            int jobsBefore = Directory.GetDirectories(root).Length;
            string admissionRefusal = null; try { ca.Start(choice, null); } catch (GenerationBusyException e) { admissionRefusal = e.Message; }
            Check(admissionRefusal == "another game process is using the world store" && ca.Current == null && Directory.GetDirectories(root).Length == jobsBefore,
                "coordinator admission (MMR-62): refused with the admission's message, no job and no job directory");
            var cd = make(null);
            var jd = cd.Start(choice, null); Settle(jd, JobPhase.ReadyForReview);
            var committingD = cd.Commit(jd, new ScriptedCommit { Body = j => Thread.Sleep(300) });
            var releasingD = cd.Release(jd);
            Thread.Sleep(50);
            bool heldDuringCommit = Directory.Exists(jd.Root) && !releasingD.IsCompleted && jd.Phase == JobPhase.Committing;
            releasingD.Wait(5000);
            Check(busy && awaitedIdle && releasingB.IsCompleted && jn.Phase == JobPhase.Cancelled && sameRelease && heldDuringCommit && jd.Phase == JobPhase.Playing && !Directory.Exists(jd.Root),
                  "one job at a time: a new job is refused as busy while the previous job's release runs and starts once AwaitIdle returns; releasing twice is one release; a release during a commit waits for Playing before deleting the job");
            evidence["scheduler"] = new Dictionary<string, object> { ["ran"] = engine.Scheduler.Ran, ["droppedStale"] = engine.Scheduler.DroppedStale, ["withdrawn"] = engine.Scheduler.Withdrawn,
                ["affinityViolations"] = engine.Scheduler.AffinityViolations, ["longestItemSeconds"] = engine.Scheduler.LongestItemSeconds };
        }
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private static JobStateMachine To(JobPhase phase)
    {
        var m = new JobStateMachine(Now);
        foreach (var p in JobStateMachine.Pipeline) { m.Advance(p); if (p == phase) break; }
        return m;
    }
    // A job has settled once it reached the phase and its completion (worker, review cancellation or commit) has run to its end: its events
    // and timings.json are written by then.
    private static void Settle(GenerationJob job, JobPhase phase) { Wait(() => job.Phase == phase && job.Completion.IsCompleted, 5000); }
    private static void SettleFinal(GenerationJob job) { Wait(() => JobStateMachine.IsTerminal(job.Phase) && job.Completion.IsCompleted, 5000); }
    private static ProgressEvent Ev(string job, long sequence) { return new ProgressEvent { JobId = job, Sequence = sequence, Phase = JobPhase.Compiling, MessageCode = "x" }; }
    private static void Wait(Func<bool> condition, int milliseconds) { var w = Stopwatch.StartNew(); while (!condition() && w.ElapsedMilliseconds < milliseconds) Thread.Sleep(5); }

    // A small JSON Schema subset (type, const, enum, pattern, minimum, minLength, required, additionalProperties false, nested objects).
    private static IEnumerable<string> Validate(IDictionary<string, object> schema, IDictionary<string, object> value, string at = "$")
    {
        var properties = (IDictionary<string, object>)schema["properties"];
        foreach (string r in ((object[])schema["required"]).Cast<string>()) if (!value.ContainsKey(r)) yield return at + "." + r + " missing";
        foreach (var kv in value)
        {
            if (!properties.ContainsKey(kv.Key)) { yield return at + "." + kv.Key + " not allowed"; continue; }
            var p = (IDictionary<string, object>)properties[kv.Key];
            if (p.ContainsKey("const") && !Equals(Convert.ToString(p["const"]), Convert.ToString(kv.Value))) yield return at + "." + kv.Key + " differs from const";
            if (p.ContainsKey("enum") && !((object[])p["enum"]).Contains(kv.Value)) yield return at + "." + kv.Key + " not in enum: " + kv.Value;
            if (p.ContainsKey("pattern") && !(kv.Value is string && Regex.IsMatch((string)kv.Value, (string)p["pattern"]))) yield return at + "." + kv.Key + " fails pattern: " + kv.Value;
            if (p.ContainsKey("minimum") && Convert.ToDouble(kv.Value) < Convert.ToDouble(p["minimum"])) yield return at + "." + kv.Key + " below minimum";
            if (p.ContainsKey("minLength") && ((string)kv.Value).Length < Convert.ToInt32(p["minLength"])) yield return at + "." + kv.Key + " too short";
            if (p.ContainsKey("type"))
            {
                string type = (string)p["type"]; object v = kv.Value;
                bool okType = type == "string" ? v is string : type == "integer" ? (v is int || v is long) : type == "number" ? (v is int || v is long || v is double || v is decimal) : type == "boolean" ? v is bool : type == "object" ? v is IDictionary<string, object> : true;
                if (!okType) yield return at + "." + kv.Key + " is not " + type;
                if (type == "object" && v is IDictionary<string, object>) foreach (var nested in Validate(p, (IDictionary<string, object>)v, at + "." + kv.Key)) yield return nested;
            }
        }
    }

    private sealed class ScriptedStage : IGenerationStage
    {
        public JobPhase Phase { get; set; }
        public Action<GenerationJob> Body;
        public void Run(GenerationJob job) { Body(job); }
    }

    private sealed class ScriptedCommit : ICommitStage
    {
        public Action<GenerationJob> Body; public bool Recovered;
        public void Commit(GenerationJob job) { Body(job); }
        public void Recover(GenerationJob job, Exception error) { Recovered = true; }
    }

    // A stand-in engine thread pumping the scheduler, as SubModule.OnApplicationTick does.
    private sealed class FakeEngine : IDisposable
    {
        private readonly Thread thread;
        private volatile bool stop;
        public volatile bool Paused;
        private volatile int threadId;
        public int ThreadId { get { return threadId; } }
        public EngineWorkScheduler Scheduler { get; private set; }
        private readonly ManualResetEventSlim pumpRequest = new ManualResetEventSlim(false), pumped = new ManualResetEventSlim(false);
        private int lastPump;
        public FakeEngine()
        {
            Scheduler = new EngineWorkScheduler(() => Thread.CurrentThread.ManagedThreadId == threadId, Now, 0.008, 64);
            thread = new Thread(() =>
            {
                threadId = Thread.CurrentThread.ManagedThreadId;
                while (!stop)
                {
                    if (pumpRequest.IsSet) { lastPump = Scheduler.Pump(); pumpRequest.Reset(); pumped.Set(); }
                    else if (!Paused) Scheduler.Pump();
                    Thread.Sleep(2);
                }
            }) { IsBackground = true };
            thread.Start();
            while (threadId == 0) Thread.Sleep(1);
        }
        // One pump on the engine thread while paused; returns how many items it ran.
        public int PumpOnce() { pumped.Reset(); pumpRequest.Set(); pumped.Wait(2000); return lastPump; }
        public void Dispose() { stop = true; thread.Join(2000); }
    }
}
