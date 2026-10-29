using System;
using System.Diagnostics;
using System.Collections.Generic;

namespace PcInternetMonitor
{
    public sealed partial class MainWindow
    {
        private readonly HashSet<string> sessionIncidentIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly SessionClockGuard sessionClock = new SessionClockGuard();
        private readonly Stopwatch sessionElapsed = new Stopwatch();
        private DateTime clockStopUtc;
        private const string ClockStopReason = "시스템 시각 변경으로 검사 중지 · 검사 시작으로 새 기록을 시작하세요";
        private sealed class SessionClockChangedException : InvalidOperationException
        { internal SessionClockChangedException() : base("UTC 시각이 역행하거나 검사 시작/완료 시각이 뒤집혀 현재 관측을 마무리합니다.") { } }

        // All callers hold recordingGate. Observe the current clock, never a
        // callback's possibly earlier timestamp, to distinguish late callbacks.
        private void CheckObservationClockLocked(InternetCheckSample completed = null)
        {
            if (clockStopUtc != DateTime.MinValue) throw new SessionClockChangedException();
            bool inverted = completed != null && completed.StartedUtc != DateTime.MinValue && completed.TimestampUtc < completed.StartedUtc;
            long beforeTicks = Stopwatch.GetTimestamp();
            DateTime utc = DateTime.UtcNow;
            long afterTicks = Stopwatch.GetTimestamp();
            if (inverted || !sessionClock.Observe(utc, beforeTicks, afterTicks))
            {
                clockStopUtc = sessionClock.LastStableUtc;
                if (clockStopUtc == DateTime.MinValue) clockStopUtc = beganUtc;
                throw new SessionClockChangedException();
            }
        }

        private void RecordGatewayTargetChanged(string oldTarget, string newTarget)
        {
            AutomaticSessionLog observed = null;
            try
            {
                lock (recordingGate)
                {
                    if (!running || automaticLog == null) return;
                    observed = automaticLog;
                    CheckObservationClockLocked();
                    history.ChangeTarget("gateway", newTarget ?? "", DateTime.UtcNow);
                    historyDirty = true;
                }
                DiagnosticsLog.Write("gateway_target_changed", "old=" + oldTarget + "; new=" + newTarget);
            }
            catch (Exception error) { QueueFailure(error is SessionClockChangedException ? ClockStopReason : "공유기 검사 대상 변경 오류", error, observed); }
        }
    }
}