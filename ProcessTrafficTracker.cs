using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace PcInternetMonitor
{
    public sealed class ProcessTrafficRow
    {
        public int ProcessId;
        public string ProcessName;
        public string ProcessInstance;
        public double SentBytesPerSecond, ReceivedBytesPerSecond;
        public long SentBytesTotal, ReceivedBytesTotal;
        public bool IdentityAvailable;
    }

    public sealed class ProcessTrafficSnapshot
    {
        public DateTime TimestampUtc;
        public DateTime IntervalStartUtc, IntervalEndUtc;
        public double IntervalSeconds;
        public bool Available;
        public string Status;
        public List<ProcessTrafficRow> Rows = new List<ProcessTrafficRow>();
        public long DroppedProcessEvents;
    }

    // Called from the existing ETW callback. Only PID, direction and byte count enter
    // this collector; names and start times are resolved on the snapshot caller.
    // Rates describe callback delivery intervals, not the packet's original time.
    public sealed class ProcessTrafficTracker
    {
        private const int MaximumProcesses = 2048;
        // Keep every tracked PID's identity without imposing a separate row limit.
        // The cache remains bounded even as inactive PIDs expire and new ones arrive.
        private const int MaximumIdentities = MaximumProcesses;
        private readonly object countersGate = new object();
        private readonly object snapshotGate = new object();
        private readonly Dictionary<int, Counters> processes = new Dictionary<int, Counters>();
        private readonly Dictionary<int, Identity> identities = new Dictionary<int, Identity>();
        private readonly Func<int, Identity> resolveIdentity;
        private DateTime previousSampleUtc;
        private ProcessTrafficSnapshot previousSnapshot;
        private long droppedProcessEvents;

        private sealed class Counters
        {
            internal int ProcessId;
            internal long Sent, Received, SampleSent, SampleReceived;
            internal long IdentitySentBaseline, IdentityReceivedBaseline;
            internal string KnownInstance;
            internal bool IdentityCheckedBefore;
            internal DateTime LastActiveUtc;
            internal DateTime FirstOccurredUtc, SampleFirstOccurredUtc;
            internal bool FirstTimestampMissing, SampleTimestampMissing;
        }

        private sealed class Candidate
        {
            internal Counters Source;
            internal int ProcessId;
            internal long Sent, Received, SentDelta, ReceivedDelta;
            internal DateTime FirstOccurredUtc, IntervalFirstOccurredUtc;
            internal bool FirstTimestampMissing, IntervalTimestampMissing;
            internal double Activity { get { return (double)SentDelta + ReceivedDelta; } }
        }

        private sealed class Identity
        {
            internal string Name, Instance;
            internal bool Available;
            internal DateTime CheckedUtc, StartedUtc;
        }

        public ProcessTrafficTracker() : this(DateTime.UtcNow, ResolveIdentity) { }

        private ProcessTrafficTracker(DateTime startUtc, Func<int, Identity> resolver)
        {
            previousSampleUtc = startUtc;
            resolveIdentity = resolver;
        }

        public void Record(int processId, uint bytes, bool send, bool retransmit)
        {
            // Compatibility callers without an original event time cannot prove
            // which incarnation of a reused PID produced the observation.
            Record(processId, bytes, send, retransmit, DateTime.MinValue);
        }

        public void Record(int processId, uint bytes, bool send, bool retransmit, DateTime occurredUtc)
        {
            if (retransmit || bytes == 0) return; // Retransmits must not count a second time.
            lock (countersGate)
            {
                if (processId <= 0)
                {
                    droppedProcessEvents++;
                    return;
                }
                Counters value;
                if (!processes.TryGetValue(processId, out value))
                {
                    if (processes.Count >= MaximumProcesses)
                    {
                        droppedProcessEvents++;
                        return;
                    }
                    value = new Counters { ProcessId = processId, LastActiveUtc = previousSampleUtc };
                    processes.Add(processId, value);
                }
                if (send) value.Sent += bytes;
                else value.Received += bytes;
                if (occurredUtc.Kind != DateTimeKind.Utc || occurredUtc == DateTime.MinValue)
                {
                    value.FirstTimestampMissing = true;
                    value.SampleTimestampMissing = true;
                }
                else
                {
                    if (value.FirstOccurredUtc == DateTime.MinValue || occurredUtc < value.FirstOccurredUtc)
                        value.FirstOccurredUtc = occurredUtc;
                    if (value.SampleFirstOccurredUtc == DateTime.MinValue || occurredUtc < value.SampleFirstOccurredUtc)
                        value.SampleFirstOccurredUtc = occurredUtc;
                }
            }
        }

        public ProcessTrafficSnapshot GetSnapshot(DateTime now)
        {
            return GetSnapshot(now, false);
        }

        public ProcessTrafficSnapshot GetSnapshot(DateTime now, bool forcePartial)
        {
            now = now.Kind == DateTimeKind.Utc ? now : now.ToUniversalTime();
            lock (snapshotGate)
            {
                DateTime intervalStart = previousSampleUtc;
                double seconds = (now - intervalStart).TotalSeconds;
                if (forcePartial && seconds <= 0)
                    return new ProcessTrafficSnapshot { TimestampUtc = now, Available = false,
                        Status = "종료 부분 구간 시각이 겹치거나 역행함 · 최종 구간 미확인", DroppedProcessEvents = DroppedEvents() };
                if (!forcePartial && seconds >= 0 && seconds < 1)
                {
                    if (previousSnapshot != null) return Clone(previousSnapshot);
                    return new ProcessTrafficSnapshot { TimestampUtc = now, Available = false,
                        Status = "전체 프로그램 통신 집계 첫 1초 대기", DroppedProcessEvents = DroppedEvents() };
                }
                if (seconds < 0)
                {
                    lock (countersGate)
                    {
                        foreach (Counters value in processes.Values)
                        {
                            value.SampleSent = value.Sent;
                            value.SampleReceived = value.Received;
                            value.LastActiveUtc = now;
                            value.SampleFirstOccurredUtc = DateTime.MinValue;
                            value.SampleTimestampMissing = false;
                        }
                        previousSampleUtc = now;
                    }
                    previousSnapshot = null;
                    return new ProcessTrafficSnapshot { TimestampUtc = now, Available = false,
                        Status = "시계 변경으로 전체 프로그램 통신 집계 구간 재시작", DroppedProcessEvents = DroppedEvents() };
                }

                List<Candidate> candidates = new List<Candidate>();
                long dropped;
                lock (countersGate)
                {
                    List<int> expired = null;
                    foreach (Counters value in processes.Values)
                    {
                        long sent = value.Sent - value.SampleSent, received = value.Received - value.SampleReceived;
                        value.SampleSent = value.Sent;
                        value.SampleReceived = value.Received;
                        if (sent != 0 || received != 0)
                        {
                            value.LastActiveUtc = now;
                            candidates.Add(new Candidate { Source = value, ProcessId = value.ProcessId,
                                Sent = value.Sent, Received = value.Received, SentDelta = sent, ReceivedDelta = received,
                                FirstOccurredUtc = value.FirstOccurredUtc, IntervalFirstOccurredUtc = value.SampleFirstOccurredUtc,
                                FirstTimestampMissing = value.FirstTimestampMissing, IntervalTimestampMissing = value.SampleTimestampMissing });
                        }
                        else if ((now - value.LastActiveUtc).TotalSeconds >= 120)
                        {
                            if (expired == null) expired = new List<int>();
                            expired.Add(value.ProcessId);
                        }
                        value.SampleFirstOccurredUtc = DateTime.MinValue;
                        value.SampleTimestampMissing = false;
                    }
                    if (expired != null) foreach (int pid in expired) processes.Remove(pid);
                    previousSampleUtc = now;
                    dropped = droppedProcessEvents;
                }
                candidates.Sort(delegate(Candidate left, Candidate right)
                {
                    int activity = right.Activity.CompareTo(left.Activity);
                    return activity == 0 ? left.ProcessId.CompareTo(right.ProcessId) : activity;
                });

                ProcessTrafficSnapshot result = new ProcessTrafficSnapshot { TimestampUtc = now, Available = true,
                    IntervalStartUtc = intervalStart, IntervalEndUtc = now, IntervalSeconds = seconds,
                    Status = "모든 어댑터 TCP/UDP · ETW 전달 간격 " + seconds.ToString("0.00", CultureInfo.InvariantCulture) +
                        "초 · 전체 활동 PID " + candidates.Count.ToString(CultureInfo.InvariantCulture) +
                        "개 · PID 집계 최대 " + MaximumProcesses.ToString(CultureInfo.InvariantCulture) + "개",
                    DroppedProcessEvents = dropped };
                int reused = 0;
                for (int i = 0; i < candidates.Count; i++)
                {
                    Candidate candidate = candidates[i];
                    Identity identity = GetIdentity(candidate.ProcessId, now);
                    bool changed = false;
                    lock (countersGate)
                    {
                        Counters value = candidate.Source;
                        if (identity.Available)
                        {
                            changed = value.KnownInstance != null && value.KnownInstance != identity.Instance;
                            bool uncertainEarlierIdentity = value.KnownInstance == null && value.IdentityCheckedBefore;
                            // PID reuse can happen before the first lookup. Compare
                            // original ETW occurrence times, not callback arrival times,
                            // with the replacement process's actual creation time.
                            bool priorTrafficPredates = value.KnownInstance == null &&
                                (candidate.FirstTimestampMissing || candidate.FirstOccurredUtc == DateTime.MinValue ||
                                    candidate.FirstOccurredUtc < identity.StartedUtc);
                            bool intervalPredates = candidate.IntervalTimestampMissing ||
                                candidate.IntervalFirstOccurredUtc == DateTime.MinValue ||
                                candidate.IntervalFirstOccurredUtc < identity.StartedUtc;
                            bool uncertain = uncertainEarlierIdentity || priorTrafficPredates || intervalPredates || identity.StartedUtc > now;
                            if (changed || uncertain)
                            {
                                // A PID was reused. Old observations cannot be assigned to the
                                // replacement process; discard its inherited lifetime total.
                                value.IdentitySentBaseline = candidate.Sent;
                                value.IdentityReceivedBaseline = candidate.Received;
                            }
                            changed = changed || uncertain;
                            value.KnownInstance = identity.Instance;
                        }
                        value.IdentityCheckedBefore = true;
                        result.Rows.Add(new ProcessTrafficRow { ProcessId = candidate.ProcessId,
                            ProcessName = changed ? identity.Name + " (프로세스 식별 변경 · 이 구간 귀속 불확실)" : identity.Name,
                            ProcessInstance = identity.Available ? identity.Instance : candidate.ProcessId.ToString(CultureInfo.InvariantCulture) + ":unknown",
                            IdentityAvailable = identity.Available && !changed,
                            SentBytesPerSecond = candidate.SentDelta / seconds,
                            ReceivedBytesPerSecond = candidate.ReceivedDelta / seconds,
                            SentBytesTotal = Math.Max(0, candidate.Sent - value.IdentitySentBaseline),
                            ReceivedBytesTotal = Math.Max(0, candidate.Received - value.IdentityReceivedBaseline) });
                    }
                    if (changed) reused++;
                }
                if (reused > 0) result.Status += " · PID 재사용 또는 식별 변경 " + reused + "개 (변경 구간 이름 귀속 불확실)";
                if (dropped > 0) result.Status += " · 유효하지 않은 PID 또는 집계 한도 초과로 제외된 이벤트 누적 " + dropped;
                TrimIdentityCache(now);
                previousSnapshot = result;
                return Clone(result);
            }
        }

        private long DroppedEvents()
        {
            lock (countersGate) return droppedProcessEvents;
        }

        private Identity GetIdentity(int processId, DateTime now)
        {
            Identity value;
            // Query identities only for PIDs active in this measurement interval.
            // Duplicate snapshot calls reuse the snapshot, while a fresh start-time
            // lookup once per interval detects PID reuse without trusting old names.
            if (identities.TryGetValue(processId, out value))
            {
                double age = (now - value.CheckedUtc).TotalSeconds;
                if (age >= 0 && age < 1) return value;
            }
            try { value = resolveIdentity(processId); }
            catch (Exception) { value = null; }
            if (value == null || !value.Available || String.IsNullOrEmpty(value.Name) || String.IsNullOrEmpty(value.Instance) ||
                value.StartedUtc == DateTime.MinValue || value.StartedUtc.Kind != DateTimeKind.Utc)
                value = new Identity { Name = "PID " + processId + " · 이름 미확인", Available = false };
            value.CheckedUtc = now;
            identities[processId] = value;
            return value;
        }

        private void TrimIdentityCache(DateTime now)
        {
            if (identities.Count <= MaximumIdentities) return;
            List<KeyValuePair<int, Identity>> sorted = new List<KeyValuePair<int, Identity>>(identities);
            sorted.Sort(delegate(KeyValuePair<int, Identity> left, KeyValuePair<int, Identity> right)
                { return left.Value.CheckedUtc.CompareTo(right.Value.CheckedUtc); });
            for (int i = 0; identities.Count > MaximumIdentities; i++) identities.Remove(sorted[i].Key);
        }

        private static Identity ResolveIdentity(int processId)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    // StartTime separates reused PIDs across snapshots; a process
                    // observed exiting during these queries is left unidentified.
                    DateTime started = process.StartTime.ToUniversalTime();
                    string name = process.ProcessName;
                    if (process.HasExited) return null;
                    return new Identity { Available = true, Name = name, StartedUtc = started,
                        Instance = processId.ToString(CultureInfo.InvariantCulture) + ":" + started.Ticks.ToString(CultureInfo.InvariantCulture) };
                }
            }
            catch (Exception) { return null; }
        }

        private static ProcessTrafficSnapshot Clone(ProcessTrafficSnapshot source)
        {
            ProcessTrafficSnapshot result = new ProcessTrafficSnapshot { TimestampUtc = source.TimestampUtc,
                IntervalStartUtc = source.IntervalStartUtc, IntervalEndUtc = source.IntervalEndUtc,
                IntervalSeconds = source.IntervalSeconds,
                Available = source.Available, Status = source.Status, DroppedProcessEvents = source.DroppedProcessEvents };
            foreach (ProcessTrafficRow row in source.Rows)
                result.Rows.Add(new ProcessTrafficRow { ProcessId = row.ProcessId, ProcessName = row.ProcessName,
                    ProcessInstance = row.ProcessInstance, IdentityAvailable = row.IdentityAvailable,
                    SentBytesPerSecond = row.SentBytesPerSecond, ReceivedBytesPerSecond = row.ReceivedBytesPerSecond,
                    SentBytesTotal = row.SentBytesTotal, ReceivedBytesTotal = row.ReceivedBytesTotal });
            return result;
        }

        public static void SelfTest()
        {
            DateTime start = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            int queries = 0;
            string instance = "111:original";
            ProcessTrafficTracker tracker = new ProcessTrafficTracker(start, delegate(int pid)
            {
                queries++;
                if (pid == 333) throw new InvalidOperationException("fixture access denied");
                return new Identity { Available = true, Name = "Fixture-" + pid,
                    Instance = pid == 111 ? instance : pid + ":fixture", StartedUtc = start.AddHours(-1) };
            });
            tracker.Record(111, 1000, true, false, start);
            tracker.Record(222, 2000, false, false, start);
            tracker.Record(111, 9000, true, true, start);
            tracker.Record(333, 50, true, false, start);
            Require(tracker.GetSnapshot(start.AddMilliseconds(500)).Rows.Count == 0, "first partial interval waits");
            ProcessTrafficSnapshot one = tracker.GetSnapshot(start.AddSeconds(2));
            Require(one.Available && one.Rows.Count == 3 && one.Rows[0].ProcessId == 222 &&
                one.Rows[0].ReceivedBytesPerSecond == 1000 && one.Rows[1].SentBytesPerSecond == 500 &&
                one.Rows[1].SentBytesTotal == 1000, "other PID rates sorted and retransmit excluded");
            Require(!one.Rows[2].IdentityAvailable && one.Rows[2].ProcessName.Contains("이름 미확인"), "identity failure explicit");
            one.Rows[0].ReceivedBytesTotal = -1;
            tracker.Record(111, 1000, true, false, start.AddSeconds(2));
            ProcessTrafficSnapshot repeat = tracker.GetSnapshot(start.AddMilliseconds(2200));
            Require(repeat.Rows[0].ReceivedBytesTotal == 2000 && queries == 3 && repeat.TimestampUtc == start.AddSeconds(2),
                "repeat snapshot retains interval and copies rows");
            ProcessTrafficSnapshot two = tracker.GetSnapshot(start.AddSeconds(3));
            Require(two.Rows.Count == 1 && two.Rows[0].SentBytesTotal == 2000 && two.Rows[0].SentBytesPerSecond == 1000,
                "snapshot does not reset session counters");
            instance = "111:replacement";
            tracker.Record(111, 500, true, false, start.AddSeconds(3));
            ProcessTrafficSnapshot reuse = tracker.GetSnapshot(start.AddSeconds(4));
            Require(!reuse.Rows[0].IdentityAvailable && reuse.Rows[0].ProcessInstance == instance &&
                reuse.Rows[0].SentBytesTotal == 0 && reuse.Status.Contains("PID 재사용"), "PID replacement separates old totals");
            tracker.Record(111, 250, true, false, start.AddSeconds(4));
            ProcessTrafficSnapshot replacement = tracker.GetSnapshot(start.AddSeconds(5));
            Require(replacement.Rows[0].IdentityAvailable && replacement.Rows[0].SentBytesTotal == 250, "replacement starts a new total");
            bool identityKnown = false;
            ProcessTrafficTracker uncertain = new ProcessTrafficTracker(start, delegate(int pid)
                { return identityKnown ? new Identity { Available = true, Name = "fixture", Instance = "444:new", StartedUtc = start.AddHours(-1) } : null; });
            uncertain.Record(444, 100, true, false);
            uncertain.GetSnapshot(start.AddSeconds(1));
            identityKnown = true;
            uncertain.Record(444, 200, true, false);
            ProcessTrafficSnapshot recovered = uncertain.GetSnapshot(start.AddSeconds(2));
            Require(!recovered.Rows[0].IdentityAvailable && recovered.Rows[0].SentBytesTotal == 0,
                "previous unidentified traffic is not assigned to a newly identified process");
            ProcessTrafficTracker firstLookupReuse = new ProcessTrafficTracker(start, delegate(int pid)
                { return new Identity { Available = true, Name = "replacement", Instance = "555:replacement", StartedUtc = start.AddMilliseconds(500) }; });
            firstLookupReuse.Record(555, 800, true, false, start.AddSeconds(-10)); // Buffered event from the old process.
            firstLookupReuse.Record(555, 200, true, false, start.AddMilliseconds(750));
            ProcessTrafficSnapshot mixedFirst = firstLookupReuse.GetSnapshot(start.AddSeconds(1));
            Require(!mixedFirst.Rows[0].IdentityAvailable && mixedFirst.Rows[0].ProcessName.Contains("귀속 불확실") &&
                mixedFirst.Rows[0].SentBytesTotal == 0 && mixedFirst.Rows[0].SentBytesPerSecond == 1000,
                "PID reused before first identity lookup never inherits old events");
            firstLookupReuse.Record(555, 250, true, false, start.AddMilliseconds(1500));
            ProcessTrafficSnapshot cleanReplacement = firstLookupReuse.GetSnapshot(start.AddSeconds(2));
            Require(cleanReplacement.Rows[0].IdentityAvailable && cleanReplacement.Rows[0].SentBytesTotal == 250,
                "clean replacement interval starts independently after mixed first lookup");
            firstLookupReuse.Record(555, 900, true, false, start.AddSeconds(-5)); // Arrives after identity was already known.
            firstLookupReuse.Record(555, 100, true, false, start.AddMilliseconds(2500));
            ProcessTrafficSnapshot lateOld = firstLookupReuse.GetSnapshot(start.AddSeconds(3));
            Require(!lateOld.Rows[0].IdentityAvailable && lateOld.Rows[0].SentBytesTotal == 0 &&
                lateOld.Rows[0].SentBytesPerSecond == 1000, "late buffered old-PID events cannot be assigned to current process");
            firstLookupReuse.Record(555, 50, true, false, start.AddMilliseconds(3500));
            ProcessTrafficSnapshot recoveredLateOld = firstLookupReuse.GetSnapshot(start.AddSeconds(4));
            Require(recoveredLateOld.Rows[0].IdentityAvailable && recoveredLateOld.Rows[0].SentBytesTotal == 50,
                "post-contamination total contains only later clean observations");
            int cacheQueries = 0;
            ProcessTrafficTracker clockCache = new ProcessTrafficTracker(start, delegate(int pid)
                { cacheQueries++; return new Identity { Available = true, Name = "fixture", Instance = "666:fixture", StartedUtc = start.AddHours(-1) }; });
            clockCache.GetIdentity(666, start.AddSeconds(10));
            clockCache.GetIdentity(666, start.AddSeconds(9));
            Require(cacheQueries == 2, "identity cache rejects negative age after clock rollback");
            ProcessTrafficTracker everyActive = new ProcessTrafficTracker(start, delegate(int pid)
                { return new Identity { Available = true, Name = "fixture-" + pid,
                    Instance = pid + ":fixture", StartedUtc = start.AddHours(-1) }; });
            for (int pid = 1; pid <= 40; pid++) everyActive.Record(pid, (uint)pid, true, false, start);
            ProcessTrafficSnapshot allFirst = everyActive.GetSnapshot(start.AddSeconds(1));
            Require(allFirst.Rows.Count == 40 && allFirst.Rows[0].ProcessId == 40 &&
                allFirst.Rows[39].ProcessId == 1 && allFirst.Rows[39].SentBytesPerSecond == 1 &&
                allFirst.Status.Contains("전체 활동 PID 40개"), "low traffic PIDs beyond twenty are retained");
            HashSet<string> firstInstances = new HashSet<string>();
            foreach (ProcessTrafficRow row in allFirst.Rows)
                Require(row.IdentityAvailable && firstInstances.Add(row.ProcessInstance), "all active process instances are distinct");
            for (int pid = 1; pid <= 40; pid++) everyActive.Record(pid, 2, true, false, start.AddSeconds(1));
            ProcessTrafficSnapshot allNext = everyActive.GetSnapshot(start.AddSeconds(2));
            Require(allNext.Rows.Count == 40, "all active PIDs are retained in the next interval");
            foreach (ProcessTrafficRow row in allNext.Rows)
                Require(row.IdentityAvailable && firstInstances.Contains(row.ProcessInstance) &&
                    row.SentBytesPerSecond == 2 && row.SentBytesTotal == row.ProcessId + 2,
                    "next interval preserves each process identity and lifetime totals");
            ProcessTrafficTracker bound = new ProcessTrafficTracker(start, delegate(int pid)
                { return new Identity { Available = true, Name = "fixture", Instance = pid + ":fixture", StartedUtc = start.AddHours(-1) }; });
            for (int pid = 1; pid <= MaximumProcesses + 50; pid++) bound.Record(pid, 10, true, false, start);
            ProcessTrafficSnapshot full = bound.GetSnapshot(start.AddSeconds(1));
            Require(bound.processes.Count == MaximumProcesses && full.Rows.Count == MaximumProcesses &&
                full.DroppedProcessEvents == 50 && full.Status.Contains("집계 한도 초과") &&
                bound.identities.Count <= MaximumIdentities, "all bounded processes returned and exclusions disclosed");
            bound.GetSnapshot(start.AddSeconds(122));
            bound.Record(9000, 99, true, false);
            Require(bound.processes.Count == 1 && bound.GetSnapshot(start.AddSeconds(123)).Rows[0].ProcessId == 9000,
                "idle PID entries expire and capacity recovers");
            Require(!bound.GetSnapshot(start).Available, "clock rollback is not a traffic measurement");
            ProcessTrafficTracker identityBound = new ProcessTrafficTracker(start, delegate(int pid)
                { return new Identity { Available = true, Name = "fixture", Instance = pid + ":fixture", StartedUtc = start.AddHours(-1) }; });
            for (int second = 1; second <= 30; second++)
            {
                for (int index = 1; index <= 100; index++)
                    identityBound.Record((second - 1) * 100 + index, 100, true, false, start.AddSeconds(second * 122 - 1));
                // Let inactive counter entries expire while exercising the
                // identity cache across more unique PIDs than its capacity.
                identityBound.GetSnapshot(start.AddSeconds(second * 122));
            }
            Require(identityBound.identities.Count == MaximumIdentities, "identity cache stays bounded across changing active processes");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Process traffic self-test failed: " + message);
        }
    }
}
