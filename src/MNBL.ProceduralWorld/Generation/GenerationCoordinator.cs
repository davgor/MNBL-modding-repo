using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.UI;

namespace MNBL.ProceduralWorld.Generation
{
    // MMR-44: the generation job (docs/generation-job.md), pure managed and tested offline with scripted stages; the production stages are
    // GenerationStages (the proven worldbridge request/generate/bundle steps, the MMR-29 verifier, the MMR-37/38 scene and navigation path).
    //   * One job at a time. The host owns the job: its id, directory (SubModule.Store/jobs/<id>), cancellation and job ticket. Each phase's
    //     stage runs on the job's worker thread; engine mutations go through the EngineWorkScheduler as bounded engine-thread items carrying
    //     the job ticket, and the worker waits for them (never the engine thread).
    //   * Progress: a ProgressEvent at every phase start, the stages' own progress, and the final state; also written to the job's
    //     events.jsonl. Phase timings go to the job's timings.json, apart from any hashed artifact.
    //   * Ownership: a stage records what it creates (staging, a published bundle, an engine world, a world generation) with its release.
    //     Cancellation and failure release them in reverse order, each once, and nothing else; the job directory itself stays (logs,
    //     events, timings) until the job is released.
    //   * Cancellation: allowed before commit begins. A queued engine item is withdrawn or dropped (its ticket is no longer live); a running
    //     one finishes and its result is torn down on the engine thread. A job awaiting review is cancelled at once (its resources released).
    //     Committing (MMR-45) finishes or recovers and is never cancelled.
    internal sealed class GenerationFailure : Exception
    {
        public GenerationFailure(GenerationError error, Exception inner = null) : base(error.ToString(), inner) { Error = error; }
        public GenerationError Error { get; private set; }
    }

    // Start refused: another job is unfinished (running, awaiting review, or still being released).
    internal sealed class GenerationBusyException : InvalidOperationException
    {
        public GenerationBusyException(string message) : base(message) { }
    }

    internal interface IGenerationStage
    {
        JobPhase Phase { get; }
        // The job's worker thread. Engine work only through job.Engine / job.Teardown.
        void Run(GenerationJob job);
    }

    // MMR-45's activation: Commit runs from Committing; on failure Recover restores a consistent state before the job fails.
    internal interface ICommitStage
    {
        void Commit(GenerationJob job);
        void Recover(GenerationJob job, Exception error);
    }

    // MMR-46: thrown by a commit stage the player withdrew before anything of the commit remained (the activation rolled back completely):
    // the job returns to ReadyForReview with its world, rather than failing.
    internal sealed class CommitWithdrawn : Exception { public CommitWithdrawn(string message, Exception inner = null) : base(message, inner) { } }

    internal sealed class GenerationJob
    {
        private readonly GenerationCoordinator owner;
        private readonly List<KeyValuePair<string, Action>> owned = new List<KeyValuePair<string, Action>>();
        private long sequence;
        internal readonly CancellationTokenSource CancelSource = new CancellationTokenSource();
        internal readonly object Sync = new object();
        internal readonly object PublishSync = new object();
        internal readonly Action<ProgressEvent> Listener;
        internal int offEngineViolations, engineViolations;
        internal bool cancelRequested;   // under Sync: set by Cancel before the token's callbacks run

        internal GenerationJob(GenerationCoordinator owner, string id, string root, WorldChoice choice, JobTicket ticket, JobStateMachine machine, double started, Action<ProgressEvent> listener)
        {
            this.owner = owner; Id = id; Root = root; Choice = choice; Ticket = ticket; Machine = machine; Started = started; Listener = listener;
        }

        public string Id { get; private set; }
        public string Root { get; private set; }
        public WorldChoice Choice { get; private set; }
        public JobTicket Ticket { get; private set; }
        internal JobStateMachine Machine { get; private set; }
        public double Started { get; private set; }
        public CancellationToken Token { get { return CancelSource.Token; } }
        public JobPhase Phase { get { lock (Sync) return Machine.Phase; } }
        public IList<PhaseTiming> Timings { get { lock (Sync) return Machine.Timings.ToList(); } }
        private readonly Dictionary<string, object> artifacts = new Dictionary<string, object>(StringComparer.Ordinal);
        // What the stages produced (paths, ids, receipts), for the next stages, the UI and the record. Any thread.
        public void Set(string key, object value) { lock (artifacts) artifacts[key] = value; }
        public T Get<T>(string key) { lock (artifacts) { object value; return artifacts.TryGetValue(key, out value) && value is T ? (T)value : default(T); } }
        public Dictionary<string, object> Artifacts { get { lock (artifacts) return new Dictionary<string, object>(artifacts, StringComparer.Ordinal); } }
        public GenerationError Error { get; internal set; }
        public string FailureDetail { get; internal set; }
        public Task Completion { get; internal set; }
        // Set by GenerationCoordinator.Release: done once the job is final and its directory deleted.
        public Task Released { get; internal set; }
        public bool CancelRequested { get { lock (Sync) return cancelRequested; } }
        public List<string> Cleanup { get; } = new List<string>();
        public List<ProgressEvent> Events { get; } = new List<ProgressEvent>();
        public int OffEngineViolations { get { return Volatile.Read(ref offEngineViolations); } }
        public int EngineViolations { get { return Volatile.Read(ref engineViolations); } }
        public int OwnedCount { get { lock (Sync) return owned.Count; } }
        public IList<string> OwnedResources { get { lock (Sync) return owned.Select(o => o.Key).ToList(); } }
        internal long NextSequence() { return Interlocked.Increment(ref sequence) - 1; }

        public void Report(int? completed, int? total, string messageCode) { owner.Publish(this, Phase, messageCode, completed, total, null); }
        public void Own(string resource, Action release) { lock (Sync) owned.Add(new KeyValuePair<string, Action>(resource, release)); }
        // A resource handed to its next owner (a committed world takes its bundle and scene): no longer released by this job.
        public bool Disown(string resource) { lock (Sync) return owned.RemoveAll(o => o.Key == resource) > 0; }
        internal List<KeyValuePair<string, Action>> TakeOwned() { lock (Sync) { var all = owned.AsEnumerable().Reverse().ToList(); owned.Clear(); return all; } }
        public T Engine<T>(string label, Func<T> work, Action<T> discard = null) { return owner.RunEngine(this, label, work, discard); }
        public void Teardown(string label, Action work) { owner.RunTeardown(label, work); }
        public string File(string name) { return System.IO.Path.Combine(Root, name); }
        // MMR-47: the job continues an unfinished job's directory (its request, the worldbridge's attempt state and work).
        public bool Resumed { get; internal set; }
    }

    internal sealed class GenerationCoordinator
    {
        private readonly Dictionary<JobPhase, IGenerationStage> stages;
        private readonly EngineWorkScheduler scheduler;
        private readonly Func<double> clock;
        private readonly string jobsRoot;
        private readonly Func<Exception, GenerationJob, GenerationError> classify;
        private readonly object gate = new object();
        private GenerationJob current;

        public GenerationCoordinator(IEnumerable<IGenerationStage> stages, EngineWorkScheduler scheduler, Func<double> clock, string jobsRoot, Func<Exception, GenerationJob, GenerationError> classify)
        {
            this.stages = stages.ToDictionary(s => s.Phase);
            foreach (var phase in JobStateMachine.Pipeline.TakeWhile(p => p != JobPhase.ReadyForReview))
                if (!this.stages.ContainsKey(phase)) throw new ArgumentException("No stage runs " + phase + ".");
            this.scheduler = scheduler; this.clock = clock; this.jobsRoot = jobsRoot; this.classify = classify;
        }

        public EngineWorkScheduler Scheduler { get { return scheduler; } }
        public double TeardownTimeoutSeconds { get; set; } = 60;
        // MMR-62: a refusal message when this process may not write to the store (another game process holds its lock); null to admit.
        public Func<string> Admission { get; set; }
        public GenerationJob Current { get { lock (gate) return current; } }

        // Starts a job for the choice. Refused while another job is unfinished (running, awaiting review, or not yet released): cancel or
        // release it first (AwaitIdle waits for a release in progress). created runs before the job's first event.
        // MMR-47: with choice.ResumeJobRoot, the job continues that unfinished job's directory under jobsRoot (WorldRecoveryService found it):
        // same id, its request reused, the worldbridge resuming its persisted attempt state. The choice is persisted (choice.json) so a job a
        // crash stops can be offered again.
        public GenerationJob Start(WorldChoice choice, Action<ProgressEvent> listener, Action<GenerationJob> created = null)
        {
            string refusal = Admission == null ? null : Admission();
            if (refusal != null) throw new GenerationBusyException(refusal);
            lock (gate)
            {
                if (current != null && (!JobStateMachine.IsTerminal(current.Phase) || (current.Released != null && !current.Released.IsCompleted)))
                    throw new GenerationBusyException("Generation job " + current.Id + " is " + current.Phase + "; cancel or release it first.");
                string id, root;
                bool resumed = choice.ResumeJobRoot != null;
                if (resumed)
                {
                    root = Path.GetFullPath(choice.ResumeJobRoot).TrimEnd(Path.DirectorySeparatorChar); id = Path.GetFileName(root);
                    if (!String.Equals(Path.GetDirectoryName(root), Path.GetFullPath(jobsRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root)
                        || (System.IO.File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Not an unfinished job of this store: " + root);
                    // What a stopped job left mid-write: a pending request and a cancel request are not the job's state.
                    foreach (var stale in new[] { "request.json.pending", "cancel" }) { string p = Path.Combine(root, stale); if (System.IO.File.Exists(p)) System.IO.File.Delete(p); }
                }
                else
                {
                    id = Guid.NewGuid().ToString("N"); root = Path.Combine(jobsRoot, id);
                    if (Directory.Exists(root) || System.IO.File.Exists(root)) throw new IOException("Job path already exists: " + root);
                    Directory.CreateDirectory(root);
                }
                WriteChoice(Path.Combine(root, "choice.json"), choice);
                var machine = new JobStateMachine(clock);
                var job = new GenerationJob(this, id, root, choice, JobTicket.Begin(id), machine, clock(), listener) { Resumed = resumed };
                machine.Advance(JobPhase.ValidatingRequest);
                current = job;
                if (created != null) created(job);
                job.Completion = Task.Run(() => Execute(job));
                return job;
            }
        }

        private static void WriteChoice(string path, WorldChoice choice)
        {
            var record = new Dictionary<string, object> { ["schema"] = "mnbl.generation-choice.v1", ["seed"] = choice.Seed, ["seedSource"] = choice.SeedSource, ["campaignInstanceId"] = choice.CampaignInstanceId,
                ["recipeVersion"] = choice.RecipeVersion, ["maxAttempts"] = choice.MaxAttempts, ["producerLockSha256"] = choice.ProducerLockSha256, ["profile"] = choice.Profile,
                ["overrides"] = choice.Overrides == null ? new Dictionary<string, object>() : choice.Overrides.ToDictionary(kv => kv.Key, kv => (object)kv.Value) };
            // Atomically (review finding: a torn choice must never make a job unreadable).
            MNBL.ProceduralWorld.Persistence.DurableFile.WriteAllText(path, new JavaScriptSerializer().Serialize(record));
        }

        // Waits (up to timeout) for the current job's release in progress, if any; true when a new job may start.
        public bool AwaitIdle(TimeSpan timeout)
        {
            var job = Current;
            if (job == null) return true;
            Task released; lock (job.Sync) released = job.Released;
            if (released != null) released.Wait(timeout);
            job = Current;
            return job == null || (JobStateMachine.IsTerminal(job.Phase) && (job.Released == null || job.Released.IsCompleted));
        }

        private void Execute(GenerationJob job)
        {
            try
            {
                foreach (var phase in JobStateMachine.Pipeline)
                {
                    if (phase == JobPhase.ReadyForReview) break;
                    if (phase == JobPhase.ValidatingRequest) Publish(job, phase, "phase_started", null, null, null);
                    else Transition(job, phase, "phase_started");
                    if (scheduler.OnEngineThread) Interlocked.Increment(ref job.offEngineViolations);
                    job.Token.ThrowIfCancellationRequested();
                    stages[phase].Run(job);
                }
                Transition(job, JobPhase.ReadyForReview, "ready_for_review");
                WriteRecord(job);
            }
            catch (Exception error) { Fail(job, error); }
        }

        private void Fail(GenerationJob job, Exception error)
        {
            var e = error is AggregateException ? error.GetBaseException() : error;
            // Whatever a stage throws once the job was cancelled (the bridge's cancelled exit, a killed child) is the cancellation.
            if (job.CancelRequested || job.Token.IsCancellationRequested) Finish(job, JobPhase.Cancelled, null, e);
            else if (e is GenerationFailure) Finish(job, JobPhase.Failed, ((GenerationFailure)e).Error, e);
            else Finish(job, JobPhase.Failed, classify(e, job), e);
        }

        private void Transition(GenerationJob job, JobPhase phase, string code)
        {
            lock (job.Sync)
            {
                if (job.cancelRequested) throw new OperationCanceledException(job.Token);
                job.Machine.Advance(phase);
            }
            Publish(job, phase, code, null, null, null);
        }

        private void Finish(GenerationJob job, JobPhase terminal, GenerationError error, Exception cause)
        {
            job.Error = error;
            if (cause != null && !(cause is OperationCanceledException)) job.FailureDetail = cause.GetType().Name + ": " + cause.Message;
            ReleaseOwned(job);
            lock (job.Sync) job.Machine.Advance(terminal);
            job.Ticket.End();
            WriteRecord(job);
            Publish(job, terminal, terminal == JobPhase.Cancelled ? "cancelled" : terminal == JobPhase.Failed ? "failed" : "playing", null, null, error);
        }

        private void ReleaseOwned(GenerationJob job)
        {
            foreach (var resource in job.TakeOwned())
            {
                try { if (resource.Value != null) resource.Value(); lock (job.Cleanup) job.Cleanup.Add("released " + resource.Key); }
                catch (Exception error) { lock (job.Cleanup) job.Cleanup.Add("failed to release " + resource.Key + ": " + error.GetType().Name + ": " + error.Message); }
            }
        }

        // Before commit begins: true when the job will stop (now cancelled, or its worker stopping); false when it cannot be cancelled.
        public bool Cancel(GenerationJob job)
        {
            lock (job.Sync)
            {
                var phase = job.Machine.Phase;
                if (!JobStateMachine.Cancellable(phase) || job.cancelRequested) return false;
                job.cancelRequested = true;
                job.Ticket.Cancel();
                // A job awaiting review has no worker: it is finished here. The swap is under the lock, so Release and Commit see it.
                if (phase == JobPhase.ReadyForReview) job.Completion = job.Completion.ContinueWith(_ => Finish(job, JobPhase.Cancelled, null, null), TaskScheduler.Default);
            }
            job.CancelSource.Cancel();   // its callbacks (the owned process job's termination) run outside the lock
            return true;
        }

        // The UI no longer shows the job: an unfinished job is cancelled first; once it is final (its completion no longer changing) its
        // directory is deleted.
        public Task Release(GenerationJob job)
        {
            Cancel(job);
            lock (job.Sync)
            {
                if (job.Released != null) return job.Released;
                job.Released = Task.Run(() =>
                {
                    while (true)
                    {
                        Task completion; lock (job.Sync) completion = job.Completion;
                        try { completion.Wait(); } catch (AggregateException) { }
                        lock (job.Sync) if (completion == job.Completion) break;
                    }
                    string failure = DeleteDirectory(job.Root);
                    lock (job.Cleanup) job.Cleanup.Add(failure == null ? "released job directory" : "failed to release job directory: " + failure);
                    lock (gate) if (current == job && JobStateMachine.IsTerminal(job.Phase)) current = null;
                });
                return job.Released;
            }
        }

        // Deletes a directory, retrying briefly while an exiting child process still holds a file. Null when done, else the last error.
        private static string DeleteDirectory(string path)
        {
            string last = null;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try { if (Directory.Exists(path)) Directory.Delete(path, true); return null; }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { last = error.Message; Thread.Sleep(200); }
            }
            return last;
        }

        // MMR-45 supplies the commit stage; from ReadyForReview only. Never cancelled once begun.
        public Task Commit(GenerationJob job, ICommitStage stage)
        {
            Task committing;
            lock (job.Sync)
            {
                if (job.Machine.Phase != JobPhase.ReadyForReview || job.cancelRequested) throw new InvalidOperationException("Only a job awaiting review commits (" + job.Machine.Phase + ").");
                job.Machine.Advance(JobPhase.Committing);
                committing = job.Completion = job.Completion.ContinueWith(_ =>
                {
                    try
                    {
                        stage.Commit(job);
                        // What the commit did not take over is staging: released before the job is final, as Finish does.
                        ReleaseOwned(job);
                        job.Ticket.End();
                        lock (job.Sync) job.Machine.Advance(JobPhase.Playing);
                        WriteRecord(job);
                        Publish(job, JobPhase.Playing, "playing", null, null, null);
                    }
                    catch (CommitWithdrawn withdrawn)
                    {
                        // The player withdrew and nothing of the commit remains: the world is ready for review again (the job's own staging
                        // and its ticket stay as they were).
                        try { stage.Recover(job, withdrawn); } catch (Exception recovery) { lock (job.Cleanup) job.Cleanup.Add("recovery failed: " + recovery.Message); }
                        lock (job.Sync) job.Machine.Advance(JobPhase.ReadyForReview);
                        lock (job.Cleanup) job.Cleanup.Add("commit withdrawn: " + withdrawn.Message);
                        Publish(job, JobPhase.ReadyForReview, "commit_withdrawn", null, null, null);
                    }
                    catch (Exception error)
                    {
                        try { stage.Recover(job, error); } catch (Exception recovery) { lock (job.Cleanup) job.Cleanup.Add("recovery failed: " + recovery.Message); }
                        Finish(job, JobPhase.Failed, error is GenerationFailure ? ((GenerationFailure)error).Error : classify(error, job), error);
                    }
                }, TaskScheduler.Default);
            }
            Publish(job, JobPhase.Committing, "phase_started", null, null, null);
            return committing;
        }

        internal T RunEngine<T>(GenerationJob job, string label, Func<T> work, Action<T> discard)
        {
            if (scheduler.OnEngineThread) throw new InvalidOperationException("Engine work is awaited from the job's worker, never on the engine thread: " + label);
            var handle = scheduler.Post(job.Ticket, label, () => { if (!scheduler.OnEngineThread) Interlocked.Increment(ref job.engineViolations); return work(); });
            var wait = ((IAsyncResult)handle.Task).AsyncWaitHandle;
            while (!handle.Task.IsCompleted)
            {
                if (job.Token.IsCancellationRequested && handle.TryWithdraw()) throw new OperationCanceledException(job.Token);
                wait.WaitOne(50);
            }
            if (handle.Task.IsCanceled) throw new OperationCanceledException(job.Token);
            if (handle.Task.IsFaulted) ExceptionDispatchInfo.Capture(handle.Task.Exception.GetBaseException()).Throw();
            T result = handle.Task.Result;
            if (job.Token.IsCancellationRequested)
            {
                // It ran while the job was being cancelled: what it made is released on the engine thread, then the job stops.
                if (discard != null) RunTeardown(label + " (discarded)", () => discard(result));
                throw new OperationCanceledException(job.Token);
            }
            return result;
        }

        internal void RunTeardown(string label, Action work)
        {
            if (scheduler.OnEngineThread) { work(); return; }
            var handle = scheduler.Post<bool>(null, label, () => { work(); return true; });
            if (!((IAsyncResult)handle.Task).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(TeardownTimeoutSeconds))) throw new TimeoutException("Engine teardown did not run within " + TeardownTimeoutSeconds + " s: " + label);
            if (handle.Task.IsFaulted) ExceptionDispatchInfo.Capture(handle.Task.Exception.GetBaseException()).Throw();
        }

        internal void Publish(GenerationJob job, JobPhase phase, string code, int? completed, int? total, GenerationError error)
        {
            ProgressEvent e;
            lock (job.PublishSync)
            {
                e = new ProgressEvent { JobId = job.Id, Sequence = job.NextSequence(), Phase = phase, Completed = completed, Total = total, MessageCode = code, Error = error, Seconds = clock() - job.Started };
                job.Events.Add(e);
                try { System.IO.File.AppendAllText(job.File("events.jsonl"), new JavaScriptSerializer().Serialize(e.ToDictionary()) + "\n", new UTF8Encoding(false)); }
                catch (Exception failure) when (failure is IOException || failure is UnauthorizedAccessException) { }
            }
            if (job.Listener != null) job.Listener(e);
        }

        // The job's record, apart from any hashed artifact: its phases with their timings, outcome, cleanup and thread-affinity counters.
        public Dictionary<string, object> Record(GenerationJob job)
        {
            List<PhaseTiming> timings; JobPhase phase;
            lock (job.Sync) { timings = job.Machine.Timings.ToList(); phase = job.Machine.Phase; }
            List<string> cleanup; lock (job.Cleanup) cleanup = job.Cleanup.ToList();
            return new Dictionary<string, object> { ["schema"] = "mnbl.generation-job", ["stateMachineVersion"] = JobStateMachine.Version, ["protocolVersion"] = ProgressEvent.ProtocolVersion,
                ["jobId"] = job.Id, ["phase"] = phase.ToString(), ["timings"] = timings.Select(t => t.ToDictionary()).ToList(),
                ["error"] = job.Error == null ? null : new Dictionary<string, object> { ["category"] = job.Error.Category.ToString(), ["code"] = job.Error.Code, ["detail"] = job.Error.Detail },
                ["failureDetail"] = job.FailureDetail, ["cleanup"] = cleanup, ["owned"] = job.OwnedResources, ["events"] = job.Events.Count,
                ["affinity"] = new Dictionary<string, object> { ["stageOnEngineThread"] = job.OffEngineViolations, ["engineItemOffEngineThread"] = job.EngineViolations },
                ["scheduler"] = new Dictionary<string, object> { ["ran"] = scheduler.Ran, ["droppedStale"] = scheduler.DroppedStale, ["withdrawn"] = scheduler.Withdrawn,
                    ["affinityViolations"] = scheduler.AffinityViolations, ["longestItemSeconds"] = Math.Round(scheduler.LongestItemSeconds, 4), ["longestItem"] = scheduler.LongestItemLabel },
                ["artifacts"] = (Dictionary<string, object>)Plain(job.Artifacts, 0) };
        }

        // A JSON-ready copy: strings, numbers, booleans, dictionaries (keys as text) and lists; any other object is recorded by its type.
        private static object Plain(object value, int depth)
        {
            if (value == null || value is string || value is bool || value is int || value is long || value is uint || value is double || value is float || value is decimal) return value;
            if (depth > 8) return value.GetType().Name;
            var dictionary = value as System.Collections.IDictionary;
            if (dictionary != null)
            {
                var plain = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (System.Collections.DictionaryEntry entry in dictionary) plain[Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture)] = Plain(entry.Value, depth + 1);
                return plain;
            }
            var list = value as System.Collections.IEnumerable;
            if (list != null) { var plain = new List<object>(); foreach (var item in list) plain.Add(Plain(item, depth + 1)); return plain; }
            return value.GetType().Name;
        }

        private void WriteRecord(GenerationJob job)
        {
            // The record is diagnostics: a problem writing it never fails the job.
            try { MNBL.ProceduralWorld.Persistence.DurableFile.WriteAllText(job.File("timings.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(Record(job))); }
            catch (Exception failure) { lock (job.Cleanup) job.Cleanup.Add("record not written: " + failure.GetType().Name + ": " + failure.Message); }
        }
    }
}
