using System;

namespace MNBL.BattleScale.Core
{
    // Engine-independent receipt/exit gate. Successful allocation is insufficient:
    // teardown evidence must survive multiple application ticks before receipt,
    // and the caller must confirm that receipt was persisted before requesting exit.
    public sealed class NativePoolProbeCompletion
    {
        public enum Action { Wait, WriteReceipt, RequestExit }
        private bool started, receiptWritten;
        private double start, last, stableSince;
        private int stableTicks;
        public int StableTicks { get { return stableTicks; } }
        public bool Verified { get; private set; }

        public void Begin(double now, bool acceptanceStagesPassed)
        {
            if (started || !acceptanceStagesPassed) throw new InvalidOperationException("Probe acceptance is incomplete or teardown already started.");
            CheckTime(now);
            started = true; start = last = now;
        }

        public Action Observe(double now, bool allTeardownEvidencePresent)
        {
            CheckTime(now);
            if (!started || now < last) throw new InvalidOperationException("Invalid teardown observation order.");
            last = now;
            if (!allTeardownEvidencePresent)
            {
                if (Verified) throw new InvalidOperationException("Teardown evidence disappeared after verification.");
                stableTicks = 0;
            }
            if (now - start > 120) throw new TimeoutException("Mission teardown did not complete within120 seconds.");
            if (!allTeardownEvidencePresent) return Action.Wait;
            if (receiptWritten) return Action.RequestExit;
            if (stableTicks++ == 0) stableSince = now;
            if (stableTicks < 2 || now - stableSince < 1) return Action.Wait;
            Verified = true;
            return Action.WriteReceipt;
        }

        public void ConfirmReceiptWritten()
        {
            if (!Verified || receiptWritten) throw new InvalidOperationException("Receipt cannot precede verified teardown or be confirmed twice.");
            receiptWritten = true;
        }

        private static void CheckTime(double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now) || now < 0)
                throw new ArgumentOutOfRangeException("now");
        }
    }
}
