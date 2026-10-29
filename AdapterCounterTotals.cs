using System;

namespace PcInternetMonitor
{
    public sealed class AdapterCounterTotalsSnapshot
    {
        // Totals remain available for later inspection even when the current read fails.
        // Use Available to decide whether the current UI/CSV sample may display them.
        public bool Available { get; internal set; }
        public bool HasBaseline { get; internal set; }
        public bool BaselineOnly { get; internal set; }
        public long ReceiveErrors { get; internal set; }
        public long SendErrors { get; internal set; }
        public long ReceiveDiscards { get; internal set; }
        public long SendDiscards { get; internal set; }
        public long ResetCount { get; internal set; }
        public long GapCount { get; internal set; }
        public bool ResetObserved { get; internal set; }
        public bool GapActive { get; internal set; }
        public bool GapRecovered { get; internal set; }
        public bool Saturated { get; internal set; }
        public bool HasDiscontinuity { get { return ResetCount > 0 || GapCount > 0 || Saturated; } }
    }

    // Accumulates only increases observed within a continuous counter segment.
    // A decreasing counter starts a new segment independently of the other counters.
    // Its first raw value is NOT added: a driver/device reset boundary is uncertain.
    // A failed/invalid read similarly ends continuity for all four counters. Recovery
    // establishes fresh baselines without losing the totals already observed.
    // These conservative totals do not guarantee recovery of events during resets
    // or gaps, and cannot detect a reset which is hidden between successful reads.
    public sealed class AdapterCounterTotals
    {
        private readonly object gate = new object();
        private readonly Counter receiveErrors = new Counter();
        private readonly Counter sendErrors = new Counter();
        private readonly Counter receiveDiscards = new Counter();
        private readonly Counter sendDiscards = new Counter();
        private bool available, hasBaseline, baselineOnly, resetObserved, gapActive, gapRecovered, saturated;
        private long resetCount, gapCount;

        public void Clear()
        {
            lock (gate)
            {
                receiveErrors.Clear(); sendErrors.Clear(); receiveDiscards.Clear(); sendDiscards.Clear();
                available = hasBaseline = baselineOnly = resetObserved = gapActive = gapRecovered = saturated = false;
                resetCount = gapCount = 0;
            }
        }

        // Pass every read, including Available=false (or null), so a read failure
        // is not silently bridged as a complete measurement interval.
        public AdapterCounterTotalsSnapshot Update(AdapterSample sample)
        {
            lock (gate)
            {
                resetObserved = gapRecovered = baselineOnly = false;
                if (!Valid(sample))
                {
                    available = false;
                    if (!gapActive && gapCount < Int64.MaxValue) gapCount++;
                    gapActive = true;
                    return Copy();
                }

                baselineOnly = !hasBaseline || gapActive;
                gapRecovered = gapActive;
                bool changed = receiveErrors.Update(sample.ReceiveErrors, baselineOnly, ref saturated);
                changed = sendErrors.Update(sample.SendErrors, baselineOnly, ref saturated) || changed;
                changed = receiveDiscards.Update(sample.ReceiveDiscards, baselineOnly, ref saturated) || changed;
                changed = sendDiscards.Update(sample.SendDiscards, baselineOnly, ref saturated) || changed;
                resetObserved = changed;
                // Count samples containing a decrease, not the number of fields decreased.
                if (resetObserved && resetCount < Int64.MaxValue) resetCount++;
                available = hasBaseline = true; gapActive = false;
                return Copy();
            }
        }

        public AdapterCounterTotalsSnapshot Snapshot()
        {
            lock (gate) return Copy();
        }

        private AdapterCounterTotalsSnapshot Copy()
        {
            return new AdapterCounterTotalsSnapshot {
                Available = available, HasBaseline = hasBaseline, BaselineOnly = baselineOnly,
                ReceiveErrors = receiveErrors.Total, SendErrors = sendErrors.Total,
                ReceiveDiscards = receiveDiscards.Total, SendDiscards = sendDiscards.Total,
                ResetCount = resetCount, GapCount = gapCount, ResetObserved = resetObserved,
                GapActive = gapActive, GapRecovered = gapRecovered, Saturated = saturated
            };
        }

        private static bool Valid(AdapterSample sample)
        {
            return sample != null && sample.Available && sample.ReceiveErrors >= 0 && sample.SendErrors >= 0 &&
                sample.ReceiveDiscards >= 0 && sample.SendDiscards >= 0;
        }

        private sealed class Counter
        {
            private long previous;
            public long Total;

            public void Clear() { previous = Total = 0; }

            public bool Update(long current, bool baseline, ref bool saturated)
            {
                bool reset = !baseline && current < previous;
                if (!baseline && !reset)
                {
                    long increase = current - previous;
                    if (increase > Int64.MaxValue - Total) { Total = Int64.MaxValue; saturated = true; }
                    else Total += increase;
                }
                previous = current;
                return reset;
            }
        }

        // Pure synthetic counter checks; no adapter reads, probes or file writes.
        public static void SelfTest()
        {
            AdapterCounterTotals totals = new AdapterCounterTotals();
            AdapterCounterTotalsSnapshot s = totals.Snapshot();
            Check(!s.Available && !s.HasBaseline && !s.HasDiscontinuity, "fresh state was reported as a measured zero");
            s = totals.Update(Sample(100, 200, 300, 400));
            Check(s.Available && s.BaselineOnly && s.ReceiveErrors == 0 && !s.HasDiscontinuity, "first sample was counted instead of baselined");
            s = totals.Update(Sample(105, 202, 303, 404));
            Check(s.ReceiveErrors == 5 && s.SendErrors == 2 && s.ReceiveDiscards == 3 && s.SendDiscards == 4, "independent counter increases were lost");
            s = totals.Update(Sample(0, 204, 307, 405));
            Check(s.ReceiveErrors == 5 && s.SendErrors == 4 && s.ReceiveDiscards == 7 && s.SendDiscards == 5 &&
                s.ResetObserved && s.ResetCount == 1 && s.HasDiscontinuity, "one counter reset erased totals or rebased unrelated counters");
            s = totals.Update(Sample(2, 205, 308, 406));
            Check(s.ReceiveErrors == 7 && s.SendErrors == 5 && !s.ResetObserved && s.ResetCount == 1, "five before reset plus two after reset did not remain seven");
            s = totals.Update(Sample(1, 205, 308, 406));
            Check(s.ReceiveErrors == 7 && s.ResetObserved, "uncertain nonzero reset boundary was added to the total");
            s = totals.Update(Sample(4, 205, 308, 406));
            Check(s.ReceiveErrors == 10, "observed increases after a nonzero reset were lost");

            s = totals.Update(new AdapterSample { Available = false });
            Check(!s.Available && s.GapActive && s.GapCount == 1 && s.ReceiveErrors == 10 && s.HasDiscontinuity,
                "an unavailable interval was shown as a valid zero or erased retained totals");
            s = totals.Update(null);
            Check(!s.Available && s.GapCount == 1, "one unavailable streak was counted as multiple gaps");
            s = totals.Update(Sample(1000, 2000, 3000, 4000));
            Check(s.Available && s.BaselineOnly && s.GapRecovered && !s.GapActive && s.ReceiveErrors == 10 && s.SendErrors == 5,
                "gap recovery guessed unobserved increases or lost previous totals");
            s = totals.Update(Sample(1003, 2002, 3001, 4004));
            Check(s.ReceiveErrors == 13 && s.SendErrors == 7 && s.ReceiveDiscards == 9 && s.SendDiscards == 10 &&
                !s.BaselineOnly && !s.GapRecovered, "continuous observations did not resume after gap baselining");
            s.ReceiveErrors = 0;
            Check(totals.Snapshot().ReceiveErrors == 13, "a returned snapshot could mutate internal totals");
            s = totals.Update(Sample(-1, 0, 0, 0));
            Check(!s.Available && s.GapCount == 2 && s.ReceiveErrors == 13, "invalid negative raw data was treated as measured counters");

            totals.Clear();
            s = totals.Snapshot();
            Check(!s.Available && !s.HasBaseline && s.ReceiveErrors == 0 && s.SendErrors == 0 && s.ReceiveDiscards == 0 &&
                s.SendDiscards == 0 && s.ResetCount == 0 && s.GapCount == 0 && !s.HasDiscontinuity, "new session retained old totals or discontinuity flags");
            totals.Update(Sample(500, 600, 700, 800));
            s = totals.Update(Sample(501, 602, 703, 804));
            Check(s.ReceiveErrors == 1 && s.SendErrors == 2 && s.ReceiveDiscards == 3 && s.SendDiscards == 4 &&
                !s.HasDiscontinuity, "fresh-session baseline reused an earlier counter segment");

            totals.Clear(); totals.Update(Sample(0, 0, 0, 0));
            totals.Update(Sample(Int64.MaxValue, 0, 0, 0));
            totals.Update(Sample(0, 0, 0, 0));
            s = totals.Update(Sample(1, 0, 0, 0));
            Check(s.ReceiveErrors == Int64.MaxValue && s.Saturated && s.HasDiscontinuity, "counter total overflowed into a false small or negative value");
        }

        private static AdapterSample Sample(long receiveError, long sendError, long receiveDiscard, long sendDiscard)
        {
            return new AdapterSample { Available = true, ReceiveErrors = receiveError, SendErrors = sendError,
                ReceiveDiscards = receiveDiscard, SendDiscards = sendDiscard };
        }
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("AdapterCounterTotals self-test: " + message);
        }
    }
}
