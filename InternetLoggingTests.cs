using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PcInternetMonitor
{
    // Synthetic file/queue/archive checks; no DNS, HTTPS, sockets or ETW.
    public static class InternetLoggingTests
    {
        public static void SelfTest(string testDirectory)
        {
            string root = Path.GetFullPath(testDirectory), fixture = Path.Combine(root, "internet-log-test-" + Guid.NewGuid().ToString("N"));
            Child(root, fixture); Directory.CreateDirectory(fixture);
            string session = null; List<string> archiveSessions = new List<string>();
            try
            {
                DateTime at = DateTime.UtcNow.AddMinutes(-1);
                InternetCheckSample dns = new InternetCheckSample { Kind = "dns", Target = "synthetic,\"name\"", StartedUtc = at, TimestampUtc = at.AddMilliseconds(20), State = "success", Status = "fixture,\"resolved\"", Success = true, DurationMs = 20 };
                InternetCheckSample https = new InternetCheckSample { Kind = "https", Target = "https://example.invalid/check", StartedUtc = at.AddSeconds(5), TimestampUtc = at.AddSeconds(8), State = "timeout", Status = "fixture timeout", Error = "line1\nline2", TimedOut = true, DurationMs = 3000 };
                ConnectivityIncident incident = new ConnectivityIncident { Id = Guid.NewGuid().ToString("N"), Version = 1, Kind = "https", Target = https.Target, Category = "failure", Scope = "HTTPS 검사 대상", State = "open", StartedUtc = at.AddSeconds(8), LastObservedUtc = at.AddSeconds(13), Failures = 2, Summary = "접속 실패, \"fixture\"" };
                string expectedDnsTarget = dns.Target, expectedSummary = incident.Summary;
                using (AutomaticSessionLog log = new AutomaticSessionLog(fixture))
                {
                    session = log.SessionDirectory; Child(fixture, session);
                    Check(log.AppendInternetCheck(dns) && log.AppendInternetCheck(dns), "DNS row and duplicate accepted");
                    dns.Target = "mutated"; dns.Status = "mutated";
                    Check(log.AppendInternetCheck(https), "HTTPS timeout enqueued");
                    Check(log.AppendConnectivityIncident(incident) && log.AppendConnectivityIncident(incident), "incident duplicate accepted");
                    incident.Summary = "mutated";
                    incident.Version = 2; incident.State = "recovered"; incident.RecoveredUtc = incident.EndedUtc = at.AddSeconds(18); incident.LastObservedUtc = at.AddSeconds(18); incident.Summary = "회복 확인";
                    Check(log.AppendConnectivityIncident(incident), "recovered version enqueued");
                    incident.Version = 1; incident.State = "open";
                    Check(log.AppendConnectivityIncident(incident), "older incident version ignored");
                    ConnectivityIncident stopped = new ConnectivityIncident { Id = Guid.NewGuid().ToString("N"), Version = 1, Kind = "dns", Target = "example.invalid", Category = "check_error", Scope = "DNS 검사", State = "ended", StartedUtc = at.AddSeconds(20), LastObservedUtc = at.AddSeconds(25), ClosedUtc = at.AddSeconds(30), Failures = 2, Summary = "검사 오류", EndReason = "사용자 중지 · 회복 미확인" };
                    Check(log.AppendConnectivityIncident(stopped), "stopped incident enqueued");
                    log.Complete("synthetic internet logging");
                    Check(log.IsHealthy && !log.AppendInternetCheck(https) && !log.AppendConnectivityIncident(stopped), "completed writer rejects more rows");
                }
                IList<string[]> checks = ConnectivityHistoryArchive.ParseCsv(File.ReadAllText(Path.Combine(session, "internet-checks.csv"), Encoding.UTF8).TrimStart('\uFEFF'), 100);
                IList<string[]> versions = ConnectivityHistoryArchive.ParseCsv(File.ReadAllText(Path.Combine(session, "connectivity-incidents.csv"), Encoding.UTF8).TrimStart('\uFEFF'), 100);
                Check(checks.Count == 3 && versions.Count == 4, "duplicates or older versions were written");
                Check(checks[0].Length == 13 && checks[1].Length == 13 && checks[2].Length == 13 && checks[1][4] == expectedDnsTarget && checks[1][6] == "fixture,\"resolved\"" && checks[2][12] == "line1\nline2", "CSV quoting or immutable check row changed");
                Check(versions[0].Length == 15 && versions[1][13] == expectedSummary && versions[2][6] == "recovered" && versions[2][9].Length > 0 && versions[2][10] == "", "incident versions or recovery columns changed");
                Check(versions[3][6] == "ended" && versions[3][9] == "" && versions[3][10].Length > 0, "recording stop became a recovery");
                string metadata = File.ReadAllText(Path.Combine(session, "session.txt"), Encoding.UTF8);
                Check(metadata.Contains("format_version=5") && metadata.Contains("internet_checks_written=2") && metadata.Contains("connectivity_incident_versions_written=3") && metadata.Contains("state=completed"), "new writers did not finish and report counts");
                foreach (string name in new string[] { "samples.csv", "marks.csv", "probes.csv", "process-traffic.csv", "game-endpoints.csv", "tcp-rtt.csv" })
                    Check(File.ReadAllText(Path.Combine(session, name), Encoding.UTF8).Length > 0, "existing writer missing or empty: " + name);
                IList<ConnectivityIncident> loaded = ConnectivityHistoryArchive.LoadRecent(fixture, 500);
                Check(loaded.Count == 2 && loaded.Any(x => x.Id == incident.Id && x.Version == 2 && x.State == "recovered") && loaded.Any(x => x.State == "ended" && !x.RecoveredUtc.HasValue), "archive latest version or stop classification changed");
                Check(ConnectivityHistoryArchive.LoadRecent(fixture, 1).Count == 1 && ConnectivityHistoryArchive.LoadRecent(fixture, 0).Count == 0, "archive output limit ignored");
                ArchiveOpenAndMalformed(session, fixture, at);
                ArchiveTailTests(fixture, archiveSessions, at);
                SummaryTest(at);
            }
            finally
            {
                if (session != null)
                {
                    Child(fixture, session);
                    foreach (string name in new string[] { "samples.csv", "marks.csv", "probes.csv", "process-traffic.csv", "game-endpoints.csv", "tcp-rtt.csv", "internet-checks.csv", "connectivity-incidents.csv", "session.txt" })
                    { string path = Path.Combine(session, name); if (File.Exists(path)) File.Delete(path); }
                    if (Directory.Exists(session) && Directory.GetFileSystemEntries(session).Length == 0) Directory.Delete(session);
                }
                foreach (string directory in archiveSessions)
                {
                    Child(fixture, directory);
                    string path = Path.Combine(directory, "connectivity-incidents.csv"); if (File.Exists(path)) File.Delete(path);
                    if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0) Directory.Delete(directory);
                }
                Child(root, fixture); if (Directory.Exists(fixture) && Directory.GetFileSystemEntries(fixture).Length == 0) Directory.Delete(fixture);
            }
        }

        private static void ArchiveTailTests(string fixture, List<string> created, DateTime at)
        {
            foreach (bool large in new bool[] { false, true })
            {
                string directory = Path.Combine(fixture, large ? "archive-large-tail" : "archive-late-recovery");
                Child(fixture, directory); Directory.CreateDirectory(directory); created.Add(directory);
                string path = Path.Combine(directory, "connectivity-incidents.csv"), id = Guid.NewGuid().ToString("N");
                int versions = large ? 5300 : 4500;
                string padding = large ? new String('한', 180) + ", \"quoted\"\ninside summary" : "fixture";
                using (StreamWriter writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(true)))
                {
                    writer.WriteLine(AutomaticSessionLog.ConnectivityIncidentHeader);
                    for (int version = 1; version <= versions; version++) writer.WriteLine(ArchiveRow(id, version, at, false, padding));
                    writer.WriteLine(ArchiveRow(id, versions + 1, at, true, "tail recovery,\n인용 \"완료\""));
                    // An unfinished final write cannot erase the preceding recovery.
                    writer.Write("\"" + Guid.NewGuid().ToString("N") + "\",999999,\"dns\",\"partial");
                }
                long length = new FileInfo(path).Length;
                Check(large ? length > 2 * 1024 * 1024 : length < 2 * 1024 * 1024, "archive tail fixture did not exercise its byte bound");
                if (large)
                {
                    // Force the bounded tail to begin in a multi-byte character
                    // inside the quoted summary of an older record.
                    int shift = -1;
                    using (FileStream stream = File.OpenRead(path))
                    {
                        stream.Position = stream.Length - 2 * 1024 * 1024;
                        for (int i = 0; i < 2048; i++) { int value = stream.ReadByte(); if (value < 0) break; if ((value & 0xc0) == 0x80) { shift = i; break; } }
                    }
                    Check(shift >= 0, "large archive fixture did not contain a UTF-8 tail boundary");
                    if (shift > 0) File.AppendAllText(path, new String('\n', shift), new UTF8Encoding(false));
                }
                IList<ConnectivityIncident> loaded = ConnectivityHistoryArchive.LoadRecent(fixture, 500);
                ConnectivityIncident recovery = loaded.FirstOrDefault(x => x.Id == id);
                Check(recovery != null && recovery.Version == versions + 1 && recovery.State == "recovered" && recovery.RecoveredUtc.HasValue && recovery.Summary == "tail recovery,\n인용 \"완료\"", "latest recovery after 4096 records or in a large UTF-8 CSV tail was lost");
            }
        }

        private static string ArchiveRow(string id, int version, DateTime at, bool recovered, string summary)
        {
            DateTime last = at.AddSeconds(5).AddTicks(version);
            return String.Join(",", new string[] {
                SessionLog.Csv(id), version.ToString(CultureInfo.InvariantCulture), "dns", "fixture.invalid", "failure", "DNS", recovered ? "recovered" : "open",
                at.ToString("o", CultureInfo.InvariantCulture), last.ToString("o", CultureInfo.InvariantCulture), recovered ? last.ToString("o", CultureInfo.InvariantCulture) : "", "", "5", version.ToString(CultureInfo.InvariantCulture), SessionLog.Csv(summary), recovered ? "observed success" : ""
            });
        }

        private static void ArchiveOpenAndMalformed(string session, string fixture, DateTime at)
        {
            string file = Path.Combine(session, "connectivity-incidents.csv"), id = Guid.NewGuid().ToString("N");
            string row = String.Join(",", new string[] { id, "1", "dns", "fixture", "failure", "DNS", "open", at.ToString("o", CultureInfo.InvariantCulture), at.AddSeconds(5).ToString("o", CultureInfo.InvariantCulture), "", "", "5", "2", "\"first line\nsecond, \"\"quoted\"\"\"", "" });
            string future = row.Replace(id, Guid.NewGuid().ToString("N")).Replace(at.ToString("o", CultureInfo.InvariantCulture), DateTime.SpecifyKind(new DateTime(2100, 1, 1), DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture));
            File.AppendAllText(file, row + "\r\ninvalid,not,a,row\r\n" + future + "\r\n", Encoding.UTF8);
            IList<ConnectivityIncident> loaded = ConnectivityHistoryArchive.LoadRecent(fixture, 500);
            ConnectivityIncident unknown = loaded.FirstOrDefault(x => x.Id == id);
            Check(loaded.Count == 3 && unknown != null && unknown.State == "ended" && !unknown.RecoveredUtc.HasValue && !unknown.EndedUtc.HasValue && unknown.ClosedUtc.HasValue && unknown.EndReason.Contains("회복 미확인") && unknown.Summary.Contains("second, \"quoted\""), "archive inferred recovery, accepted malformed/future dates or lost a multiline quoted record");
        }

        private static void SummaryTest(DateTime at)
        {
            InternetSessionSummary summary = new InternetSessionSummary();
            InternetCheckSample sample = new InternetCheckSample { Kind = "dns", Target = "fixture", State = "success", Success = true, StartedUtc = at, TimestampUtc = at.AddMilliseconds(5), DurationMs = 5 };
            summary.ObserveCheck(sample); summary.ObserveCheck(sample);
            sample = new InternetCheckSample { Kind = "https", Target = "fixture", State = "timeout", TimedOut = true, StartedUtc = at.AddSeconds(1), TimestampUtc = at.AddSeconds(4), DurationMs = 3000 }; summary.ObserveCheck(sample);
            summary.ObserveCheck(new InternetCheckSample { Kind = "dns", Target = "fixture", State = "cancelled", Cancelled = true, TimestampUtc = at.AddSeconds(5) });
            summary.ObserveSnapshot(new MonitorSnapshot { TimestampUtc = at, Running = true, AdapterExpected = true, AdapterStatus = "미측정" });
            ConnectivityIncident ended = new ConnectivityIncident { Id = Guid.NewGuid().ToString("N"), Version = 1, Kind = "https", Target = "fixture", Category = "failure", State = "ended", StartedUtc = at, LastObservedUtc = at.AddSeconds(5), ClosedUtc = at.AddSeconds(10), Failures = 2, EndReason = "사용자 중지" };
            string text = summary.BuildText(new List<ConnectivityIncident> { ended }, 0, at.AddSeconds(10), "사용자 중지");
            Check(text.Contains("DNS 이름 조회") && text.Contains("성공 1회") && text.Contains("시간 초과 1회") && text.Contains("회복 미확인") && text.Contains("관측값 없음") && text.Contains("캐시") && text.Contains("프록시"), "PC summary counts, unmeasured values or scope guidance changed");
            summary.ObserveSnapshot(new MonitorSnapshot { TimestampUtc = at.AddSeconds(11), IsFinal = true, AdapterCountersAvailable = true, AdapterReceiveErrors = 7, AdapterRatesAvailable = true, AdapterReceivedBytesPerSecond = 999999 });
            text = summary.BuildText(new List<ConnectivityIncident> { ended }, 0, at.AddSeconds(11), "사용자 중지");
            Check(text.Contains("가용 표본 0/1") && text.Contains("오류 받기 7") && text.Contains("초당 통신량 최대: 관측값 없음"), "final counters were lost or final rates/availability entered ordinary totals");
        }
        private static void Child(string root, string child) { Check(Path.GetFullPath(child).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "fixture escaped test root"); }
        private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Internet logging test: " + message); }
    }
}
