using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MNBL.ProceduralWorld.Generation
{
    // MMR-44: work for the engine thread on behalf of a generation job, pure managed (the engine thread test is injected) and tested
    // offline. The game's instance is pumped every application tick (SubModule.OnApplicationTick), beside EngineWork.
    //   * Bounded: Pump runs queued items for at most BudgetSeconds of a tick (at least one); the queue holds at most Capacity items. An
    //     item itself is one native step (install, construct, validate, dispose) and runs to completion; the longest is recorded.
    //   * Job tickets: an item carries its job's ticket. A ticket stops being live when its job is cancelled or a later job begins, and the
    //     item is then dropped unrun (StaleWorkException), so a cancelled world's callbacks never mutate a later world. Teardown items carry
    //     no ticket and always run.
    //   * Thread affinity: Pump does nothing off the engine thread (counted); the job waits for its items from its worker, never on the
    //     engine thread.
    internal sealed class JobTicket
    {
        private static long current, next;
        private int cancelled;
        private JobTicket(long serial, string jobId) { Serial = serial; JobId = jobId; }
        public long Serial { get; private set; }
        public string JobId { get; private set; }
        public static long Current { get { return Interlocked.Read(ref current); } }
        // A new job's ticket; every earlier ticket stops being live.
        public static JobTicket Begin(string jobId) { long serial = Interlocked.Increment(ref next); Interlocked.Exchange(ref current, serial); return new JobTicket(serial, jobId); }
        public void Cancel() { Interlocked.Exchange(ref cancelled, 1); }
        public void End() { Cancel(); Interlocked.CompareExchange(ref current, 0, Serial); }
        public bool Live { get { return Volatile.Read(ref cancelled) == 0 && Interlocked.Read(ref current) == Serial; } }
        public override string ToString() { return Serial + ":" + JobId; }
    }

    internal sealed class StaleWorkException : OperationCanceledException
    {
        public StaleWorkException(string message) : base(message) { }
    }

    internal sealed class EngineWorkScheduler
    {
        internal const int Queued = 0, Running = 1, Done = 2, Dropped = 3;
        internal sealed class Item
        {
            public JobTicket Ticket; public string Label; public Action Body; public Action<Exception> Fail;
            public int State;
            public int ThreadId;
        }
        // A posted item: its task, and withdrawal while it is still queued.
        internal sealed class Handle<T>
        {
            internal Item Item;
            public Task<T> Task { get; internal set; }
            public int State { get { return Volatile.Read(ref Item.State); } }
            public bool TryWithdraw()
            {
                if (Interlocked.CompareExchange(ref Item.State, Dropped, Queued) != Queued) return false;
                Item.Fail(new OperationCanceledException("withdrawn before it ran: " + Item.Label));
                return true;
            }
        }

        private readonly ConcurrentQueue<Item> queue = new ConcurrentQueue<Item>();
        private readonly Func<bool> onEngineThread;
        private readonly Func<double> clock;
        private int pending;
        private long ran, droppedStale, withdrawn, affinityViolations, refused;
        private double longestSeconds;
        private string longestLabel;

        public EngineWorkScheduler(Func<bool> onEngineThread, Func<double> clock, double budgetSeconds = 0.008, int capacity = 256)
        {
            this.onEngineThread = onEngineThread; this.clock = clock; BudgetSeconds = budgetSeconds; Capacity = capacity;
        }

        public double BudgetSeconds { get; private set; }
        public int Capacity { get; private set; }
        public int Pending { get { return Volatile.Read(ref pending); } }
        public long Ran { get { return Interlocked.Read(ref ran); } }
        public long DroppedStale { get { return Interlocked.Read(ref droppedStale); } }
        public long Withdrawn { get { return Interlocked.Read(ref withdrawn); } }
        public long AffinityViolations { get { return Interlocked.Read(ref affinityViolations); } }
        public long Refused { get { return Interlocked.Read(ref refused); } }
        public double LongestItemSeconds { get { lock (queue) return longestSeconds; } }
        public string LongestItemLabel { get { lock (queue) return longestLabel; } }
        public bool OnEngineThread { get { return onEngineThread(); } }
        // Probes only: while held, Pump leaves job items queued (it still runs teardown items, which carry no ticket), so a probe can cancel
        // a job while its engine item is provably queued.
        private volatile bool held;
        public bool Held { get { return held; } set { held = value; } }

        public Handle<T> Post<T>(JobTicket ticket, string label, Func<T> work)
        {
            if (Interlocked.Increment(ref pending) > Capacity)
            {
                Interlocked.Decrement(ref pending); Interlocked.Increment(ref refused);
                throw new InvalidOperationException("The engine work queue is full (" + Capacity + "): " + label);
            }
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var item = new Item { Ticket = ticket, Label = label };
            item.Body = () => completion.TrySetResult(work());
            // A withdrawal cancels the task; a dropped stale item and a failing item fault it with their exception.
            item.Fail = e => { if (e is StaleWorkException || !(e is OperationCanceledException)) completion.TrySetException(e); else completion.TrySetCanceled(); };
            var handle = new Handle<T> { Item = item, Task = completion.Task };
            queue.Enqueue(item);
            return handle;
        }

        // Engine thread, every tick: queued items within the tick's budget; stale items are dropped unrun.
        public int Pump()
        {
            if (!onEngineThread()) { if (!queue.IsEmpty) Interlocked.Increment(ref affinityViolations); return 0; }
            double start = clock();
            int count = 0; Item item;
            List<Item> kept = null;
            while ((count == 0 || clock() - start < BudgetSeconds) && queue.TryDequeue(out item))
            {
                if (Held && item.Ticket != null && Volatile.Read(ref item.State) == Queued) { (kept ?? (kept = new List<Item>())).Add(item); continue; }
                Interlocked.Decrement(ref pending);
                if (Interlocked.CompareExchange(ref item.State, Running, Queued) != Queued) { Interlocked.Increment(ref withdrawn); continue; }
                if (item.Ticket != null && !item.Ticket.Live)
                {
                    Volatile.Write(ref item.State, Dropped); Interlocked.Increment(ref droppedStale);
                    item.Fail(new StaleWorkException("Engine work '" + item.Label + "' of job " + item.Ticket + " was dropped: its job was cancelled or a later job began."));
                    continue;
                }
                double began = clock();
                item.ThreadId = Thread.CurrentThread.ManagedThreadId;
                try { item.Body(); }
                catch (Exception error) { item.Fail(error); }
                Volatile.Write(ref item.State, Done);
                double took = clock() - began;
                lock (queue) if (took > longestSeconds) { longestSeconds = took; longestLabel = item.Label; }
                Interlocked.Increment(ref ran); count++;
            }
            if (kept != null) foreach (var held in kept) queue.Enqueue(held);
            return count;
        }
    }
}
