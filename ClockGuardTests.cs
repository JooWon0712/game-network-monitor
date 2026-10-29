using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PcInternetMonitor
{
    public static class ClockGuardTests
    {
        public static void SelfTest(string testRoot)
        {
            string root = Path.GetFullPath(testRoot);
            string fixture = Path.Combine(root, "clock-archive-fixture-" + Guid.NewGuid().ToString("N"));
            Child(root, fixture);
            Directory.CreateDirectory(fixture);
            try
            {
                string session = Path.Combine(fixture, "synthetic-clock-epoch");
                Child(fixture, session); Directory.CreateDirectory(session);
                DateTime at = DateTime.UtcNow.AddHours(1);
                string endedId = Guid.NewGuid().ToString("N"), recoveredId = Guid.NewGuid().ToString("N"), openId = Guid.NewGuid().ToString("N");
                StringBuilder text = new StringBuilder(AutomaticSessionLog.ConnectivityIncidentHeader + "\r\n");
                text.AppendLine(Row(endedId, "ended", at, at.AddSeconds(5), "", Iso(at.AddSeconds(5))));
                text.AppendLine(Row(recoveredId, "recovered", at.AddSeconds(10), at.AddSeconds(15), Iso(at.AddSeconds(15)), ""));
                text.AppendLine(Row(openId, "open", at.AddSeconds(20), at.AddSeconds(25), "", ""));
                // Removing the wall-clock upper bound must not weaken row-internal chronology.
                text.AppendLine(Row(Guid.NewGuid().ToString("N"), "open", at.AddSeconds(50), at.AddSeconds(45), "", ""));
                text.AppendLine(Row(Guid.NewGuid().ToString("N"), "ended", at, at.AddSeconds(5), "", Iso(at.AddSeconds(4))));
                text.AppendLine(Row(Guid.NewGuid().ToString("N"), "recovered", at, at.AddSeconds(5), Iso(at.AddSeconds(4)), ""));
                text.AppendLine(Row(Guid.NewGuid().ToString("N"), "open", at, at.AddSeconds(5), "", Iso(at.AddSeconds(5))));
                text.AppendLine(Row(Guid.NewGuid().ToString("N"), "ended", at, at.AddSeconds(5), "", "not-an-ISO-date"));
                text.AppendLine("invalid,not,a,record");
                string path = Path.Combine(session, "connectivity-incidents.csv"), original = text.ToString();
                File.WriteAllText(path, original, new UTF8Encoding(false));

                IList<ConnectivityIncident> loaded = ConnectivityHistoryArchive.LoadRecent(fixture, 500);
                Check(loaded.Count == 3, "future-clock rows are retained but malformed chronology is rejected");
                ConnectivityIncident ended = loaded.Single(x => x.Id == endedId);
                Check(ended.State == "ended" && ended.StartedUtc == at && ended.LastObservedUtc == at.AddSeconds(5) &&
                    ended.ClosedUtc == ended.LastObservedUtc && !ended.RecoveredUtc.HasValue && ended.DurationSeconds == 5,
                    "a previous clock epoch keeps its exact ended timestamps");
                ConnectivityIncident recovered = loaded.Single(x => x.Id == recoveredId);
                Check(recovered.State == "recovered" && recovered.RecoveredUtc == recovered.LastObservedUtc && !recovered.ClosedUtc.HasValue,
                    "observed recovery is not confused with an unknown stop");
                ConnectivityIncident open = loaded.Single(x => x.Id == openId);
                Check(open.State == "ended" && open.ClosedUtc == open.LastObservedUtc && !open.RecoveredUtc.HasValue && !open.EndedUtc.HasValue,
                    "an old open incident is closed with recovery unknown");
                Check(File.ReadAllText(path, Encoding.UTF8) == original, "archive loading remains read-only");
            }
            finally { Child(root, fixture); if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }
        }

        private static string Row(string id, string state, DateTime started, DateTime last, string recovered, string closed)
        {
            return String.Join(",", new string[] { id, "1", "dns", "fixture.invalid", "failure", "DNS", state,
                Iso(started), Iso(last), recovered, closed, "5", "2", "synthetic clock epoch", "clock discontinuity fixture" });
        }
        private static string Iso(DateTime at) { return at.ToString("o", CultureInfo.InvariantCulture); }
        private static void Child(string root, string child)
        {
            Check(Path.GetFullPath(child).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), "test fixture escaped its own root");
        }
        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Clock boundary archive test: " + message); }
    }
}