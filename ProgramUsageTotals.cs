using System;
using System.Collections.Generic;
using System.Globalization;

namespace GameNetworkMonitor
{
    public sealed class ProgramUsageTotal
    {
        public string Name { get; internal set; }
        public double SentBytes { get; internal set; }
        public bool IdentityAvailable { get; internal set; }
        public string ScopeNote { get; internal set; }
    }

    // UI-only integration of the same interval rates already written to process-traffic.csv.
    // Lifetime counters are intentionally ignored: they reset when tracking expires or a PID
    // is reused. This accumulator performs no process lookups or extra network collection.
    public sealed class ProgramUsageTotals
    {
        private const int MaximumNamedGroups = 512;
        private const int MaximumProcessNameLength = 260;
        private const string OtherName = "기타 · 표시한도";
        private readonly object gate = new object();
        private readonly Dictionary<string, ProgramUsageTotal> groups = new Dictionary<string, ProgramUsageTotal>(StringComparer.OrdinalIgnoreCase);
        private ProgramUsageTotal other;
        private DateTime lastAcceptedEndUtc = DateTime.MinValue;
        private long ignoredSnapshots, ignoredRows;

        public long IgnoredSnapshots { get { lock (gate) return ignoredSnapshots; } }
        public long IgnoredRows { get { lock (gate) return ignoredRows; } }

        public void Clear()
        {
            lock (gate)
            {
                groups.Clear(); other = null; lastAcceptedEndUtc = DateTime.MinValue;
                ignoredSnapshots = ignoredRows = 0;
            }
        }

        public void Observe(ProcessTrafficSnapshot snapshot)
        {
            // An unavailable snapshot has no interval evidence; it must not move the watermark.
            if (snapshot != null && !snapshot.Available) return;
            lock (gate)
            {
                if (!ValidInterval(snapshot) || (lastAcceptedEndUtc != DateTime.MinValue && snapshot.IntervalStartUtc < lastAcceptedEndUtc))
                {
                    ignoredSnapshots++;
                    return;
                }

                // Rates do not locate the bytes within an interval. Reject an entire overlapping
                // interval rather than guess how much of it was new. The normal tracker emits
                // consecutive, disjoint intervals; repeated cached snapshots are thus harmless.
                lastAcceptedEndUtc = snapshot.IntervalEndUtc;
                HashSet<int> seenPids = new HashSet<int>();
                foreach (ProcessTrafficRow row in snapshot.Rows)
                {
                    if (row == null || row.ProcessId <= 0 || !seenPids.Add(row.ProcessId) ||
                        !Finite(row.SentBytesPerSecond) || row.SentBytesPerSecond < 0)
                    {
                        ignoredRows++;
                        continue;
                    }
                    double bytes = row.SentBytesPerSecond * snapshot.IntervalSeconds;
                    if (!Finite(bytes) || bytes < 0 || bytes > Int64.MaxValue)
                    {
                        ignoredRows++;
                        continue;
                    }
                    if (bytes == 0) continue;

                    string name = row.ProcessName == null ? "" : row.ProcessName.Trim();
                    bool identified = row.IdentityAvailable && name.Length > 0 && name.Length <= MaximumProcessNameLength;
                    string key;
                    if (identified) key = "name|" + name;
                    else
                    {
                        name = "알 수 없는 프로그램 · PID " + row.ProcessId.ToString(CultureInfo.InvariantCulture);
                        key = "unknown-pid|" + row.ProcessId.ToString(CultureInfo.InvariantCulture);
                    }
                    ProgramUsageTotal total;
                    if (!groups.TryGetValue(key, out total))
                    {
                        if (groups.Count < MaximumNamedGroups)
                        {
                            total = new ProgramUsageTotal {
                                Name = name, IdentityAvailable = identified,
                                ScopeNote = identified ? "이번 측정 전체 · 같은 이름의 프로세스 합계 · 모든 어댑터 관측량" :
                                    "이번 측정 전체 · 프로그램 귀속 미확인 · 같은 PID가 재사용됐을 수 있음"
                            };
                            groups.Add(key, total);
                        }
                        else
                        {
                            if (other == null) other = new ProgramUsageTotal {
                                Name = OtherName, IdentityAvailable = false,
                                ScopeNote = "이름별 표시 한도 512개를 넘은 프로그램의 보내기 합계 · 원본 CSV는 별도 보존"
                            };
                            total = other;
                        }
                    }
                    double sum = total.SentBytes + bytes;
                    if (!Finite(sum)) { ignoredRows++; continue; }
                    total.SentBytes = sum;
                }
            }
        }

        public List<ProgramUsageTotal> Snapshot()
        {
            lock (gate)
            {
                List<ProgramUsageTotal> result = new List<ProgramUsageTotal>(groups.Count + (other == null ? 0 : 1));
                foreach (ProgramUsageTotal total in groups.Values) result.Add(Copy(total));
                if (other != null) result.Add(Copy(other));
                result.Sort(delegate(ProgramUsageTotal a, ProgramUsageTotal b) {
                    int order = b.SentBytes.CompareTo(a.SentBytes);
                    return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
                });
                return result;
            }
        }

        private static ProgramUsageTotal Copy(ProgramUsageTotal source)
        {
            return new ProgramUsageTotal { Name = source.Name, SentBytes = source.SentBytes, IdentityAvailable = source.IdentityAvailable, ScopeNote = source.ScopeNote };
        }

        private static bool ValidInterval(ProcessTrafficSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Rows == null || snapshot.IntervalStartUtc == DateTime.MinValue ||
                snapshot.IntervalStartUtc.Kind != DateTimeKind.Utc || snapshot.IntervalEndUtc.Kind != DateTimeKind.Utc ||
                snapshot.TimestampUtc.Kind != DateTimeKind.Utc || snapshot.TimestampUtc != snapshot.IntervalEndUtc ||
                snapshot.IntervalEndUtc <= snapshot.IntervalStartUtc || !Finite(snapshot.IntervalSeconds) || snapshot.IntervalSeconds <= 0)
                return false;
            double actualSeconds = (snapshot.IntervalEndUtc - snapshot.IntervalStartUtc).TotalSeconds;
            return Math.Abs(actualSeconds - snapshot.IntervalSeconds) <= Math.Max(0.000001, actualSeconds * 0.000000001);
        }

        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }

        public static void SelfTest()
        {
            DateTime start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            ProgramUsageTotals totals = new ProgramUsageTotals();
            ProcessTrafficSnapshot first = Fixture(start, 2,
                Row(10, "Game", 50, true, "instance-a"), Row(11, "game", 25, true, "instance-b"));
            first.Rows[0].SentBytesTotal = first.Rows[1].SentBytesTotal = Int64.MaxValue;
            totals.Observe(first);
            Check(totals.Snapshot().Count == 1 && Near(totals.Snapshot()[0].SentBytes, 150), "interval integration or name aggregation failed");
            totals.Observe(first);
            totals.Observe(Fixture(start.AddSeconds(1), 2, Row(10, "Game", 999, true, "instance-a")));
            Check(Near(totals.Snapshot()[0].SentBytes, 150) && totals.IgnoredSnapshots == 2, "duplicate or overlapping interval counted twice");
            totals.Observe(new ProcessTrafficSnapshot { Available = false, IntervalEndUtc = start.AddYears(1) });
            totals.Observe(Fixture(start.AddSeconds(2), 3, Row(77, "Game", 10, true, "new-instance")));
            totals.Observe(Fixture(start.AddSeconds(200), 1, Row(10, "Game", 20, true, "expired-and-restarted")));
            Check(Near(totals.Snapshot()[0].SentBytes, 200), "expiry, instance change or an unavailable snapshot erased totals");
            List<ProgramUsageTotal> copy = totals.Snapshot(); copy[0].SentBytes = 0; copy.Clear();
            Check(Near(totals.Snapshot()[0].SentBytes, 200), "returned snapshot can mutate internal totals");

            ProgramUsageTotals unknown = new ProgramUsageTotals();
            unknown.Observe(Fixture(start, 1, Row(10, "Game", 10, false, null), Row(11, "Game", 20, false, null)));
            unknown.Observe(Fixture(start.AddSeconds(1), 1, Row(10, "Game", 30, true, "resolved")));
            Check(unknown.Snapshot().Count == 3 && unknown.Snapshot()[0].IdentityAvailable && Near(unknown.Snapshot()[0].SentBytes, 30), "unidentified bytes were silently assigned to an identified program");

            ProgramUsageTotals invalid = new ProgramUsageTotals();
            ProcessTrafficSnapshot invalidInterval = Fixture(start, 1, Row(1, "valid", 10, true, null));
            invalidInterval.IntervalSeconds = Double.NaN; invalid.Observe(invalidInterval);
            invalidInterval = Fixture(start, 1, Row(1, "valid", 10, true, null)); invalidInterval.IntervalSeconds = 2; invalid.Observe(invalidInterval);
            invalidInterval = Fixture(start, 1, Row(1, "valid", 10, true, null)); invalidInterval.TimestampUtc = start; invalid.Observe(invalidInterval);
            invalid.Observe(null);
            invalid.Observe(Fixture(start, 1, Row(1, "nan", Double.NaN, true, null), Row(2, "negative", -1, true, null),
                Row(3, "infinite", Double.PositiveInfinity, true, null), Row(4, "too-large", Double.MaxValue, true, null), Row(0, "bad-pid", 1, true, null),
                Row(5, "zero", 0, true, null), Row(6, "valid", 7, true, null), Row(6, "duplicate-row", 100, true, null)));
            Check(invalid.IgnoredSnapshots == 4 && invalid.IgnoredRows == 6 && invalid.Snapshot().Count == 1 && Near(invalid.Snapshot()[0].SentBytes, 7), "invalid values, zero-send or duplicate PID handling failed");

            ProgramUsageTotals bounded = new ProgramUsageTotals();
            List<ProcessTrafficRow> many = new List<ProcessTrafficRow>();
            for (int i = 0; i < 520; i++) many.Add(Row(i + 1, "fixture-" + i.ToString(CultureInfo.InvariantCulture), 1, true, null));
            bounded.Observe(Fixture(start, 1, many.ToArray()));
            bounded.Observe(Fixture(start.AddSeconds(1), 1, Row(999, "fixture-0", 4, true, "restarted"), Row(1000, "another-over-limit", 2, true, null)));
            double all = 0, overflow = 0; bool firstFound = false;
            foreach (ProgramUsageTotal value in bounded.Snapshot()) { all += value.SentBytes; if (value.Name == OtherName) overflow = value.SentBytes; if (value.Name == "fixture-0") firstFound = Near(value.SentBytes, 5); }
            Check(bounded.Snapshot().Count == MaximumNamedGroups + 1 && Near(all, 526) && Near(overflow, 10) && firstFound, "display bound lost bytes or failed to update an existing name");
            bounded.Clear();
            bounded.Observe(Fixture(start, 1, Row(1, "new-session", 3, true, null)));
            Check(bounded.Snapshot().Count == 1 && Near(bounded.Snapshot()[0].SentBytes, 3) && bounded.IgnoredSnapshots == 0, "new session did not reset totals and watermark");
        }

        private static ProcessTrafficSnapshot Fixture(DateTime start, double seconds, params ProcessTrafficRow[] rows)
        {
            DateTime end = start.AddSeconds(seconds);
            return new ProcessTrafficSnapshot { TimestampUtc = end, IntervalStartUtc = start, IntervalEndUtc = end, IntervalSeconds = seconds, Available = true, Rows = new List<ProcessTrafficRow>(rows) };
        }
        private static ProcessTrafficRow Row(int pid, string name, double rate, bool identified, string instance)
        {
            return new ProcessTrafficRow { ProcessId = pid, ProcessName = name, SentBytesPerSecond = rate, IdentityAvailable = identified, ProcessInstance = instance };
        }
        private static bool Near(double left, double right) { return Math.Abs(left - right) < 0.000001; }
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("ProgramUsageTotals self-test: " + message); }
    }
}
