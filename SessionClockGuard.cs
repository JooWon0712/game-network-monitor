using System;
using System.Diagnostics;

namespace PcInternetMonitor
{
    // Callers serialize clock observations and read utc/ticks together at that
    // boundary. A callback's earlier result timestamp is not a clock reading.
    public sealed class SessionClockGuard
    {
        private const double MaximumBackwardStepSeconds = 0.250;
        private bool initialized, discontinuous;
        private DateTime lastStableUtc;
        private long lastTicks;

        public DateTime LastStableUtc { get { return lastStableUtc; } }
        public bool Discontinuous { get { return discontinuous; } }

        public void Reset(DateTime utc, long ticks)
        {
            utc = Utc(utc);
            if (utc == DateTime.MinValue) throw new ArgumentException("A clock observation is required.", "utc");
            lastStableUtc = utc; lastTicks = ticks;
            initialized = true; discontinuous = false;
        }

        public bool Observe(DateTime utc, long ticks) { return Observe(utc, ticks, ticks); }

        public bool Observe(DateTime utc, long beforeTicks, long afterTicks)
        {
            if (discontinuous) return false;
            utc = Utc(utc);
            if (!initialized || utc == DateTime.MinValue || beforeTicks < lastTicks || afterTicks < beforeTicks || utc < lastStableUtc)
            { discontinuous = true; return false; }
            // Decimal subtraction avoids signed long overflow without losing
            // low counter bits before converting the elapsed interval.
            double monotonicSeconds = (double)((decimal)beforeTicks - (decimal)lastTicks) / Stopwatch.Frequency;
            double utcSeconds = (utc - lastStableUtc).TotalSeconds;
            if (monotonicSeconds - utcSeconds >= MaximumBackwardStepSeconds)
            { discontinuous = true; return false; }
            lastStableUtc = utc; lastTicks = afterTicks;
            return true;
        }

        private static DateTime Utc(DateTime utc)
        { return utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : DateTime.SpecifyKind(utc, DateTimeKind.Utc); }

        public static void SelfTest()
        {
            DateTime at = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            long second = Stopwatch.Frequency;
            SessionClockGuard guard = new SessionClockGuard();
            guard.Reset(at, 0);
            Check(guard.Observe(at, 0) && guard.Observe(at.AddSeconds(1), second), "normal and equal clock observations");
            Check(guard.LastStableUtc == at.AddSeconds(1) && !guard.Discontinuous, "normal boundary advances");

            guard.Reset(at, 0);
            Check(guard.Observe(at.AddMilliseconds(900), second) &&
                guard.Observe(at.AddMilliseconds(1900), second * 2), "small forward jitter is accepted");
            Check(guard.Observe(at.AddSeconds(10), second * 3), "forward wall clock correction is accepted");
            Check(!guard.Observe(at.AddMilliseconds(10700), second * 4) &&
                guard.LastStableUtc == at.AddSeconds(10), "backstep after forward correction is detected");
            Check(!guard.Observe(at.AddSeconds(20), second * 5) && guard.Discontinuous &&
                guard.LastStableUtc == at.AddSeconds(10), "discontinuity stays latched");

            guard.Reset(at, 0);
            Check(!guard.Discontinuous && guard.Observe(at.AddSeconds(1), second), "reset starts a fresh clock epoch");
            Check(!guard.Observe(at.AddMinutes(-10), second * 2) && guard.LastStableUtc == at.AddSeconds(1), "ten minute rollback keeps last good boundary");
            guard.Reset(at, 0);
            Check(!guard.Observe(at.AddTicks(-1), 0) && guard.LastStableUtc == at, "even a small raw UTC reversal is rejected");
            guard.Reset(at, 0);
            Check(!guard.Observe(at.AddMilliseconds(750), second), "250 ms monotonic backstep boundary");
            guard.Reset(at, 0);
            Check(guard.Observe(at.AddMilliseconds(751), second), "sub-threshold forward jitter remains valid");

            guard.Reset(at, 0);
            Check(guard.Observe(at.AddMilliseconds(100), second / 10, second) &&
                guard.Observe(at.AddMilliseconds(1100), second + second / 10, second + second / 10),
                "scheduler pause while pairing clocks is not a wall-clock rollback");
            guard.Reset(at, second);
            Check(!guard.Observe(at.AddSeconds(1), second - 1) && guard.LastStableUtc == at, "reversed monotonic counter is rejected");
            guard.Reset(at, 0);
            Check(guard.Observe(at.AddSeconds(1).ToLocalTime(), second) && guard.LastStableUtc.Kind == DateTimeKind.Utc,
                "local timezone representation of the same instant does not look like a clock correction");
            guard.Reset(DateTime.SpecifyKind(at, DateTimeKind.Unspecified), 0);
            Check(guard.Observe(at.AddSeconds(1), second), "an unspecified injected UTC value is interpreted as UTC");
            SessionClockGuard uninitialized = new SessionClockGuard();
            Check(!uninitialized.Observe(at, 0) && uninitialized.Discontinuous && uninitialized.LastStableUtc == DateTime.MinValue,
                "observations cannot invent an uninitialized session boundary");
        }

        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Session clock guard test: " + message); }
    }
}