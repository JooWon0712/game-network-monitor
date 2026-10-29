using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace PcInternetMonitor
{
    // Synthetic observations only: no trace session, probe, adapter, or game is started.
    public static class FinalSnapshotTests
    {
        public static void SelfTest(string testRoot)
        {
            if (String.IsNullOrWhiteSpace(testRoot)) throw new ArgumentException("Test root is empty.", "testRoot");
            string root = Path.GetFullPath(testRoot);
            string fixtureRoot = Path.Combine(root, "final-snapshot-test-" + Guid.NewGuid().ToString("N"));
            RequireChild(root, fixtureRoot);
            Directory.CreateDirectory(fixtureRoot);
            string sessionDirectory = null;
            try
            {
                int pid;
                using (Process current = Process.GetCurrentProcess()) pid = current.Id;
                ProcessTrafficTracker tracker = new ProcessTrafficTracker();
                // Establish an empty public-API boundary before adding observations.
                // Constructor/JIT scheduling time must not decide this synthetic interval.
                DateTime startUtc = DateTime.UtcNow.AddTicks(TimeSpan.TicksPerSecond);
                ProcessTrafficSnapshot empty = tracker.GetSnapshot(startUtc, true);
                Check(empty.Available && empty.Rows.Count == 0 && empty.IntervalEndUtc == startUtc,
                    "empty anchor establishes the synthetic interval boundary");
                DateTime recordedUtc = startUtc.AddTicks(10 * TimeSpan.TicksPerMillisecond);
                DateTime firstEnd = startUtc.AddTicks(200 * TimeSpan.TicksPerMillisecond);
                tracker.Record(pid, 1024, true, false, recordedUtc);
                ProcessTrafficSnapshot first = tracker.GetSnapshot(firstEnd, true);
                CheckInterval(first, pid, firstEnd, 1024, 1024);
                Check(first.IntervalStartUtc == startUtc,
                    "first data interval retains the synthetic anchor boundary");
                // TotalSeconds is a double; exact 200ms ticks need not compare >= 0.2.
                Check(firstEnd.Ticks - startUtc.Ticks == 200 * TimeSpan.TicksPerMillisecond &&
                    Near(first.IntervalSeconds, 0.2, 0.000000001) && first.IntervalSeconds > 0 && first.IntervalSeconds < 1,
                    "forced first data interval is available after exactly 200 milliseconds");

                tracker.Record(pid, 512, true, false, firstEnd.AddMilliseconds(50));
                ProcessTrafficSnapshot cachedFirst = tracker.GetSnapshot(firstEnd.AddMilliseconds(100));
                Check(cachedFirst.Available && cachedFirst.TimestampUtc == first.TimestampUtc &&
                    cachedFirst.IntervalStartUtc == first.IntervalStartUtc &&
                    cachedFirst.IntervalEndUtc == first.IntervalEndUtc &&
                    cachedFirst.Rows.Count == 1 && cachedFirst.Rows[0].SentBytesTotal == 1024,
                    "normal subsecond call returns the cached interval without consuming new bytes");
                Check(!Object.ReferenceEquals(first, cachedFirst) &&
                    !Object.ReferenceEquals(first.Rows[0], cachedFirst.Rows[0]), "cached interval is a defensive copy");

                DateTime finalEnd = firstEnd.AddMilliseconds(200);
                ProcessTrafficSnapshot finalTraffic = tracker.GetSnapshot(finalEnd, true);
                CheckInterval(finalTraffic, pid, finalEnd, 512, 1536);
                Check(finalTraffic.IntervalStartUtc == first.IntervalEndUtc && Near(finalTraffic.IntervalSeconds, 0.2, 0.000000001),
                    "final interval covers exactly the remaining 200 milliseconds");
                ProcessTrafficSnapshot repeated = tracker.GetSnapshot(finalEnd, true);
                Check(!repeated.Available && repeated.Rows.Count == 0 && repeated.IntervalSeconds == 0,
                    "a repeated forced timestamp does not invent a second final interval");
                ProcessTrafficSnapshot cachedFinal = tracker.GetSnapshot(finalEnd.AddMilliseconds(100));
                Check(cachedFinal.Available && cachedFinal.TimestampUtc == finalEnd &&
                    cachedFinal.Rows.Count == 1 && cachedFinal.Rows[0].SentBytesTotal == 1536 &&
                    Near(cachedFinal.Rows[0].SentBytesPerSecond * cachedFinal.IntervalSeconds, 512, 0.000001),
                    "unavailable repeat does not replace the last cached interval");

                ProgramUsageTotals totals = new ProgramUsageTotals();
                totals.Observe(first);
                totals.Observe(cachedFirst);
                totals.Observe(finalTraffic);
                totals.Observe(finalTraffic);
                totals.Observe(repeated);
                List<ProgramUsageTotal> usage = totals.Snapshot();
                Check(usage.Count == 1 && Near(usage[0].SentBytes, 1536, 0.000001) && totals.IgnoredSnapshots == 2,
                    "disjoint partial intervals total 1536 bytes and cached/final duplicates add nothing");

                MonitorSnapshot initial = new MonitorSnapshot {
                    TimestampUtc = firstEnd, Running = true, ProcessId = pid, ProcessName = "synthetic, \"final\"",
                    GameStatus = "synthetic observation", GameCollectionRequested = true, ProcessCollectionRequested = true,
                    ProcessTraffic = first, Trace = new TraceHealth { Active = true, Status = "synthetic active" },
                    GameSentEvents = 1
                };
                DateTime stopRequested = finalEnd.AddMilliseconds(-20);
                MonitorSnapshot final = new MonitorSnapshot {
                    TimestampUtc = finalEnd, Running = false, IsFinal = true, StopRequestedUtc = stopRequested,
                    StopDrainComplete = true, StopBoundaryStatus = "synthetic drain complete, \"final\"",
                    ProcessId = pid, ProcessName = initial.ProcessName, GameStatus = "synthetic stopped",
                    GameCollectionRequested = true, ProcessCollectionRequested = true, ProcessTraffic = finalTraffic,
                    Trace = new TraceHealth { Active = false, Status = "synthetic stopped" }, GameSentEvents = 2
                };
                final.EndpointObservations.Add(new EndpointRow {
                    Protocol = "UDP", Local = "127.0.0.1:1200", Remote = "203.0.113.1:1300",
                    SentBytes = 1536, SentEvents = 2, LastSeen = finalEnd.AddMilliseconds(-10)
                });
                Check(!final.Running && !final.Trace.Active && final.ProcessTraffic.Available &&
                    final.StopRequestedUtc.Value < final.TimestampUtc && final.TimestampUtc == finalTraffic.IntervalEndUtc,
                    "stopped final sample retains the drain boundary and final interval evidence");

                using (AutomaticSessionLog log = new AutomaticSessionLog(fixtureRoot))
                {
                    sessionDirectory = log.SessionDirectory;
                    RequireChild(fixtureRoot, sessionDirectory);
                    Check(log.Append(initial) && log.Append(final), "normal and stopped final observations enqueue");
                    log.Complete("synthetic final snapshot test");
                    Check(log.IsHealthy && log.SampleCount == 2, "final observation drains to a healthy completed log");
                }
                CheckProcessCsv(sessionDirectory, first, finalTraffic, pid);
                CheckSamplesCsv(sessionDirectory, initial, final);
                CheckEndpointCsv(sessionDirectory, final);
                string metadata = File.ReadAllText(Path.Combine(sessionDirectory, "session.txt"), Encoding.UTF8);
                Check(metadata.Contains("state=completed") && metadata.Contains("endpoint_rows_written=1"),
                    "completed writer metadata includes the final endpoint observation");
            }
            finally
            {
                // Delete only this invocation's named fixture files and empty directories.
                // Existing logs/outputs and any unexpected files are never removed.
                if (sessionDirectory != null)
                {
                    RequireChild(fixtureRoot, sessionDirectory);
                    foreach (string name in new string[] { "samples.csv", "marks.csv", "probes.csv", "process-traffic.csv", "game-endpoints.csv", "tcp-rtt.csv", "internet-checks.csv", "connectivity-incidents.csv", "session.txt" })
                    {
                        string path = Path.Combine(sessionDirectory, name);
                        if (File.Exists(path)) File.Delete(path);
                    }
                    if (Directory.Exists(sessionDirectory) && Directory.GetFileSystemEntries(sessionDirectory).Length == 0)
                        Directory.Delete(sessionDirectory);
                }
                RequireChild(root, fixtureRoot);
                if (Directory.Exists(fixtureRoot) && Directory.GetFileSystemEntries(fixtureRoot).Length == 0)
                    Directory.Delete(fixtureRoot);
            }
        }

        private static void CheckInterval(ProcessTrafficSnapshot value, int pid, DateTime endUtc, long intervalBytes, long totalBytes)
        {
            Check(value.Available && value.Rows.Count == 1 && value.TimestampUtc == endUtc && value.IntervalEndUtc == endUtc,
                "partial interval is available at the requested boundary");
            Check(value.IntervalStartUtc.Kind == DateTimeKind.Utc && value.IntervalEndUtc.Kind == DateTimeKind.Utc &&
                value.IntervalEndUtc > value.IntervalStartUtc &&
                Near(value.IntervalSeconds, (value.IntervalEndUtc - value.IntervalStartUtc).TotalSeconds, 0.000000001),
                "interval duration uses the same UTC boundaries as the rate denominator");
            ProcessTrafficRow row = value.Rows[0];
            Check(row.ProcessId == pid && row.IdentityAvailable && row.SentBytesTotal == totalBytes && row.ReceivedBytesTotal == 0 &&
                Near(row.SentBytesPerSecond * value.IntervalSeconds, intervalBytes, 0.000001) && row.ReceivedBytesPerSecond == 0,
                "known current-process bytes survive partial interval normalization exactly");
        }

        private static void CheckProcessCsv(string directory, ProcessTrafficSnapshot first, ProcessTrafficSnapshot final, int pid)
        {
            string[] lines = File.ReadAllLines(Path.Combine(directory, "process-traffic.csv"), Encoding.UTF8);
            Check(lines.Length == 3, "process CSV contains both partial intervals exactly once");
            string[] header = ParseCsv(lines[0]);
            ProcessTrafficSnapshot[] intervals = new ProcessTrafficSnapshot[] { first, final };
            long[] deltas = new long[] { 1024, 512 };
            long[] cumulative = new long[] { 1024, 1536 };
            double integrated = 0;
            for (int i = 0; i < intervals.Length; i++)
            {
                string[] row = ParseCsv(lines[i + 1]);
                Check(row.Length == header.Length, "process CSV column counts agree");
                Check(Value(header, row, "pid") == pid.ToString(CultureInfo.InvariantCulture) &&
                    Utc(Value(header, row, "timestamp_utc")) == intervals[i].TimestampUtc &&
                    Utc(Value(header, row, "interval_start_utc")) == intervals[i].IntervalStartUtc &&
                    Utc(Value(header, row, "interval_end_utc")) == intervals[i].IntervalEndUtc &&
                    Value(header, row, "send_bytes_total") == cumulative[i].ToString(CultureInfo.InvariantCulture),
                    "process CSV retains timestamps and exact cumulative byte counters");
                double seconds = Number(Value(header, row, "interval_seconds"));
                double rate = Number(Value(header, row, "send_bytes_s"));
                // CSV intentionally rounds duration to six decimals and rates to three.
                double rounding = Math.Abs(rate) * 0.0000005 + seconds * 0.0005 + 0.000001;
                Check(Near(seconds, intervals[i].IntervalSeconds, 0.000000501) &&
                    Near(rate * seconds, deltas[i], rounding), "serialized rate and duration reconstruct the interval within CSV precision");
                integrated += rate * seconds;
            }
            Check(Near(integrated, 1536, 0.02), "CSV intervals preserve the same total as the program accumulator");
        }

        private static void CheckSamplesCsv(string directory, MonitorSnapshot initial, MonitorSnapshot final)
        {
            string[] lines = File.ReadAllLines(Path.Combine(directory, "samples.csv"), Encoding.UTF8);
            Check(lines.Length == 3 && lines[0] == SessionLog.CsvHeader, "samples CSV contains the normal and final samples");
            string[] header = ParseCsv(lines[0]), first = ParseCsv(lines[1]), last = ParseCsv(lines[2]);
            Check(first.Length == header.Length && last.Length == header.Length, "samples CSV column counts agree");
            Check(Value(header, first, "is_final") == "false" && Value(header, first, "stop_requested_utc") == "" &&
                Value(header, first, "stop_drain_complete") == "", "normal samples have no final drain claim");
            Check(Value(header, last, "is_final") == "true" && Value(header, last, "stop_drain_complete") == "true" &&
                Utc(Value(header, last, "stop_requested_utc")) == final.StopRequestedUtc.Value &&
                Utc(Value(header, last, "timestamp_utc")) == final.TimestampUtc &&
                Value(header, last, "stop_boundary_status") == final.StopBoundaryStatus &&
                Value(header, last, "trace_active") == "false" && Value(header, last, "process_traffic_available") == "true" &&
                Value(header, last, "game_send_events_total") == "2" && Value(header, last, "process") == final.ProcessName,
                "final sample persists boundary metadata and observation totals after trace deactivation");
            Check(lines[1] == SessionLog.ToCsvRow(initial) && lines[2] == SessionLog.ToCsvRow(final),
                "the writer preserves each serialized snapshot");
        }

        private static void CheckEndpointCsv(string directory, MonitorSnapshot final)
        {
            string[] lines = File.ReadAllLines(Path.Combine(directory, "game-endpoints.csv"), Encoding.UTF8);
            Check(lines.Length == 2, "final-only endpoint observation is saved once");
            string[] header = ParseCsv(lines[0]), row = ParseCsv(lines[1]);
            Check(row.Length == header.Length && Utc(Value(header, row, "timestamp_utc")) == final.TimestampUtc &&
                Value(header, row, "protocol") == "UDP" && Value(header, row, "local") == "127.0.0.1:1200" &&
                Value(header, row, "remote") == "203.0.113.1:1300" && Value(header, row, "send_bytes_total") == "1536" &&
                Value(header, row, "send_events_total") == "2" &&
                Utc(Value(header, row, "last_seen_utc")) == final.EndpointObservations[0].LastSeen,
                "final endpoint address, time, and cumulative counters survive completion");
        }

        private static string[] ParseCsv(string line)
        {
            List<string> fields = new List<string>();
            StringBuilder value = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; }
                    else quoted = !quoted;
                }
                else if (c == ',' && !quoted) { fields.Add(value.ToString()); value.Length = 0; }
                else value.Append(c);
            }
            Check(!quoted, "CSV fixture has balanced quotes");
            fields.Add(value.ToString());
            return fields.ToArray();
        }

        private static string Value(string[] header, string[] row, string name)
        {
            int index = Array.IndexOf(header, name);
            Check(index >= 0 && index < row.Length, "CSV column exists: " + name);
            return row[index];
        }

        private static DateTime Utc(string value) { return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind); }
        private static double Number(string value) { return Double.Parse(value, CultureInfo.InvariantCulture); }
        private static bool Near(double actual, double expected, double tolerance) { return Math.Abs(actual - expected) <= tolerance; }

        private static void RequireChild(string parent, string child)
        {
            string prefix = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Check(Path.GetFullPath(child).StartsWith(prefix, StringComparison.OrdinalIgnoreCase), "fixture path remains under its assigned test root");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Final snapshot test: " + message);
        }
    }
}
