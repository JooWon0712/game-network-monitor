using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PcInternetMonitor
{
    // Synthetic serialization and summary checks. No TCP socket, EStats, probe or process lookup.
    public static class TcpRttLoggingTests
    {
        public static void SelfTest(string testRoot)
        {
            string root = Path.GetFullPath(testRoot);
            string fixtureRoot = Path.Combine(root, "tcp-rtt-log-test-" + Guid.NewGuid().ToString("N"));
            RequireChild(root, fixtureRoot);
            Directory.CreateDirectory(fixtureRoot);
            string directory = null;
            try
            {
                DateTime at = DateTime.UtcNow;
                MonitorSnapshot sample = Sample(at);
                TcpRttSnapshot first = Measured(at);
                first.Connections.Add(Connection("IPv4", "127.0.0.1:1234", "203.0.113.1:443", "first, \"connection\"", at, 2.5));
                first.Connections.Add(Connection("IPv6", "[::1]:1234", "[2001:db8::1]:443", "second", at, 10.5));
                sample.TcpRtt = first;
                string processName = sample.ProcessName;
                using (AutomaticSessionLog log = new AutomaticSessionLog(fixtureRoot))
                {
                    directory = log.SessionDirectory; RequireChild(fixtureRoot, directory);
                    Check(log.Append(sample), "first measured batch enqueues");
                    // These mutations must not alter already queued strings.
                    first.Connections[0].Status = "mutated after append";
                    sample.ProcessName = "changed name";
                    sample.TimestampUtc = at.AddSeconds(1);
                    Check(log.Append(sample), "cached RTT still allows the ordinary sample");
                    first.TimestampUtc = at.AddSeconds(-1);
                    sample.TimestampUtc = at.AddSeconds(2);
                    Check(log.Append(sample), "older cache still allows the ordinary sample");
                    TcpRttSnapshot unavailable = Measured(at.AddSeconds(1));
                    unavailable.Available = false; unavailable.PermissionDenied = true;
                    unavailable.State = "permission_denied"; unavailable.Status = "denied, \"fixture\"";
                    unavailable.NativeErrorCode = 5; unavailable.ErrorStage = "synthetic EStats";
                    unavailable.Connections.Add(Connection("IPv4", "127.0.0.1:1234", "203.0.113.1:443", "unavailable", at, 999));
                    sample.TimestampUtc = at.AddSeconds(3); sample.TcpRtt = unavailable;
                    Check(log.Append(sample), "unavailable batch enqueues without fabricated RTTs");
                    unavailable.Connections.Clear();
                    sample.TimestampUtc = at.AddSeconds(4);
                    sample.TcpRtt = new TcpRttSnapshot { TimestampUtc = at.AddSeconds(2), ProcessId = 42, Requested = true, State = "no_connections", Status = "TCP 연결 없음" };
                    Check(log.Append(sample), "empty connection state enqueues");
                    sample.TimestampUtc = at.AddSeconds(5);
                    sample.TcpRtt = Measured(at.AddSeconds(3)); sample.TcpRtt.ProcessId = 99;
                    sample.TcpRtt.Connections.Add(Connection("IPv4", "foreign local", "foreign remote", "foreign key", at, 777));
                    Check(log.Append(sample), "mismatched PID enqueues only a sanitized state");
                    log.Complete("synthetic TCP RTT serialization");
                    Check(log.IsHealthy && log.SampleCount == 6, "all ordinary samples flush successfully");
                }

                string[] lines = File.ReadAllLines(Path.Combine(directory, "tcp-rtt.csv"), Encoding.UTF8);
                Check(lines.Length == 6 && lines[0] == AutomaticSessionLog.TcpRttHeader,
                    "two measured rows, one unavailable row and two state rows are persisted once");
                string[] header = ParseCsv(lines[0]);
                string[] ipv4 = ParseCsv(lines[1]), ipv6 = ParseCsv(lines[2]), denied = ParseCsv(lines[3]), empty = ParseCsv(lines[4]), mismatch = ParseCsv(lines[5]);
                foreach (string[] row in new string[][] { ipv4, ipv6, denied, empty, mismatch })
                {
                    Check(row.Length == header.Length, "all RTT CSV rows match the header width");
                    Check(Value(header, row, "actual_game_ping_support") == "unsupported" && Value(header, row, "game_packet_loss_support") == "unsupported",
                        "actual game ping or packet loss support was fabricated");
                }
                Check(Value(header, ipv4, "process") == processName && Value(header, ipv4, "connection_key") == "first, \"connection\"" &&
                    Value(header, ipv4, "connection_status") == "synthetic ready" && Value(header, ipv4, "smoothed_rtt_ms") == "2.5" &&
                    Value(header, ipv4, "address_family") == "IPv4", "queued RTT values or quoted fields changed after append");
                Check(Value(header, ipv6, "local") == "[::1]:1234" && Value(header, ipv6, "remote") == "[2001:db8::1]:443" &&
                    Value(header, ipv6, "smoothed_rtt_ms") == "10.5", "IPv6 endpoints or RTT values were not preserved");
                Check(Value(header, denied, "available") == "false" && Value(header, denied, "connection_available") == "false" &&
                    Value(header, denied, "smoothed_rtt_ms") == "" && Value(header, denied, "sample_rtt_ms") == "" &&
                    Value(header, denied, "minimum_smoothed_rtt_ms") == "" && Value(header, denied, "maximum_smoothed_rtt_ms") == "" &&
                    Value(header, denied, "status") == "denied, \"fixture\"" && Value(header, denied, "native_error_code") == "5",
                    "unavailable RTT retained a number or lost its precise error state");
                Check(Value(header, empty, "state") == "no_connections" && Value(header, empty, "local") == "" &&
                    Value(header, empty, "smoothed_rtt_ms") == "", "no-connection state became zero RTT or disappeared");
                Check(Value(header, mismatch, "state") == "pid_mismatch" && Value(header, mismatch, "local") == "" &&
                    Value(header, mismatch, "remote") == "" && Value(header, mismatch, "connection_key") == "" &&
                    Value(header, mismatch, "smoothed_rtt_ms") == "", "another process's connection metadata leaked into the selected PID log");
                string metadata = File.ReadAllText(Path.Combine(directory, "session.txt"), Encoding.UTF8);
                Check(metadata.Contains("format_version=6") && metadata.Contains("tcp_rtt_rows_written=5") && metadata.Contains("state=completed"),
                    "RTT writer completion metadata is incomplete");
                Check(File.ReadAllLines(Path.Combine(directory, "samples.csv"), Encoding.UTF8).Length == 7,
                    "cached RTT deduplication removed ordinary monitor samples");
                CheckSummary(at);
            }
            finally
            {
                if (directory != null)
                {
                    RequireChild(fixtureRoot, directory);
                    foreach (string name in new string[] { "samples.csv", "marks.csv", "probes.csv", "process-traffic.csv", "game-endpoints.csv", "tcp-rtt.csv", "internet-checks.csv", "connectivity-incidents.csv", "session.txt" })
                    {
                        string path = Path.Combine(directory, name); if (File.Exists(path)) File.Delete(path);
                    }
                    if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0) Directory.Delete(directory);
                }
                RequireChild(root, fixtureRoot);
                if (Directory.Exists(fixtureRoot) && Directory.GetFileSystemEntries(fixtureRoot).Length == 0) Directory.Delete(fixtureRoot);
            }
        }

        private static void CheckSummary(DateTime at)
        {
            SessionAnalysis analysis = new SessionAnalysis();
            MonitorSnapshot first = Sample(at); first.TcpRtt = Measured(at); first.TcpRtt.Partial = true; analysis.Observe(first);
            MonitorSnapshot stale = Sample(at.AddSeconds(4)); stale.TcpRtt = Measured(at); stale.TcpRtt.Stale = true;
            stale.TcpRtt.MinimumMs = 777; stale.TcpRtt.MaximumMs = 999; analysis.Observe(stale);
            MonitorSnapshot denied = Sample(at.AddSeconds(5)); denied.TcpRtt = Measured(at.AddSeconds(5));
            denied.TcpRtt.Available = false; denied.TcpRtt.PermissionDenied = true; denied.TcpRtt.Status = "권한 부족"; analysis.Observe(denied);
            MonitorSnapshot final = Sample(at.AddSeconds(6)); final.Running = false; final.IsFinal = true; final.TcpRtt = Measured(at.AddSeconds(6));
            final.TcpRtt.MinimumMs = 1000; final.TcpRtt.MaximumMs = 2000; analysis.Observe(final);
            string text = analysis.SummaryText();
            Check(text.Contains("TCP 연결 RTT: 일반 요청 표본 중 가용 33.333% (1/3)") && text.Contains("미측정 2개 · 권한 부족 1개 · 일부 연결만 수집 1개"),
                "RTT availability counts included final or unavailable snapshots");
            Check(text.Contains("평활 RTT 관측 범위: 2.5 ~ 10.5 ms") && text.Contains("실제 게임 핑·게임 패킷 유실률: 미지원"),
                "summary used stale/final RTT values or called TCP RTT actual game ping");

            SessionAnalysis mismatch = new SessionAnalysis();
            MonitorSnapshot other = Sample(at); other.TcpRtt = Measured(at); other.TcpRtt.ProcessId = 99; mismatch.Observe(other);
            Check(mismatch.SummaryText().Contains("TCP 연결 RTT: 일반 요청 표본 중 가용 관측값 없음"),
                "another PID's RTT entered the selected process summary");
        }

        private static MonitorSnapshot Sample(DateTime at)
        {
            return new MonitorSnapshot { TimestampUtc = at, ProcessId = 42, ProcessName = "synthetic, \"RTT\"", Running = true,
                GameCollectionRequested = true, Trace = new TraceHealth { Active = true }, AdapterCountersAvailable = true };
        }

        private static TcpRttSnapshot Measured(DateTime at)
        {
            return new TcpRttSnapshot { TimestampUtc = at, ProcessId = 42, Requested = true, Available = true, State = "available", Status = "측정 중",
                ConnectionsFound = 2, ConnectionsTracked = 2, ConnectionsMeasured = 2, MinimumMs = 2.5, MaximumMs = 10.5 };
        }

        private static TcpRttConnection Connection(string family, string local, string remote, string key, DateTime at, double rtt)
        {
            return new TcpRttConnection { AddressFamily = family, Local = local, Remote = remote, Key = key, CreationTimestamp = at.Ticks,
                CollectionEnabled = true, CollectionOwned = true, Available = true, LastSampleUtc = at, SmoothedRttMs = rtt, SampleRttMs = rtt + 1,
                SamplesTotal = 10, SamplesDelta = 1, Status = "synthetic ready" };
        }

        private static string[] ParseCsv(string text)
        {
            List<string> fields = new List<string>(); StringBuilder value = new StringBuilder(); bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char current = text[i];
                if (current == '"') { if (quoted && i + 1 < text.Length && text[i + 1] == '"') { value.Append('"'); i++; } else quoted = !quoted; }
                else if (current == ',' && !quoted) { fields.Add(value.ToString()); value.Length = 0; }
                else value.Append(current);
            }
            Check(!quoted, "unbalanced CSV quotes"); fields.Add(value.ToString()); return fields.ToArray();
        }

        private static string Value(string[] header, string[] row, string key)
        {
            int index = Array.IndexOf(header, key); Check(index >= 0 && index < row.Length, "missing CSV field: " + key); return row[index];
        }

        private static void RequireChild(string parent, string child)
        {
            string prefix = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Check(Path.GetFullPath(child).StartsWith(prefix, StringComparison.OrdinalIgnoreCase), "fixture path escaped the test root");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("TCP RTT logging test: " + message);
        }
    }
}
