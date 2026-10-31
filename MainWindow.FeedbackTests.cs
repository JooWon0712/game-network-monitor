using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;

namespace PcInternetMonitor
{
    public sealed partial class MainWindow
    {
        public static void RunFeedbackTests(string reportPath)
        {
            Require(Application.Current.ShutdownMode == ShutdownMode.OnExplicitShutdown, "feedback test shutdown mode");
            string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath)), "feedback-fixture-" + Guid.NewGuid().ToString("N"));
            MainWindow window = null;
            try
            {
                window = new MainWindow(null, root); window.gatewayOverride = window.referenceOverride = "127.0.0.1";
                window.StartSession(); Require(window.running, "feedback fixture start");
                window.controller.Stop(); Thread.Sleep(30);
                for (int i = 0; i < 2; i++) { window.RecordProbe("gateway", new ProbeSample { TimestampUtc = DateTime.UtcNow, Target = "127.0.0.1", NetworkFailure = true, Status = "synthetic unreachable" }); Thread.Sleep(20); }
                Require(window.history.GetRecent(100).Any(x => x.Kind == "gateway" && x.Category == "failure" && x.State == "open"), "ICMP network failure misclassified");
                window.ShowProbe("gateway", new ProbeSummary { Target = "127.0.0.1", CurrentNetworkFailure = true, NetworkFailures = 2 });
                Require(window.values["gateway"].Text == "응답/경로 실패" && window.values["gatewayDetail"].Text.Contains("경로 실패 2회"), "ICMP failure UI mapping");
                window.RecordGatewayTargetChanged("127.0.0.1", "");
                Require(window.history.GetRecent(100).Any(x => x.Kind == "gateway" && x.State == "ended" && !x.RecoveredUtc.HasValue && x.EndReason.Contains("대상 변경")), "removed target left old history open");
                for (int i = 0; i < 2; i++) { window.RecordProbe("gateway", new ProbeSample { TimestampUtc = DateTime.UtcNow, Target = "127.0.0.1", Status = "synthetic denied" }); Thread.Sleep(20); }
                Require(window.history.GetRecent(100).Any(x => x.Kind == "gateway" && x.Category == "check_error" && x.State == "open"), "local ICMP error no longer distinct");
                string session = window.lastDirectory; window.StopSession("feedback fixture stop", null);
                window.ShowProbe("gateway", new ProbeSummary { Sent = 20, Missing = 5, NetworkFailures = 2, Errors = 1 });
                Require(window.values["gateway"].Text == "—" && window.values["gatewayDetail"].Text == "검사 중지", "stopped ICMP kept recent numbers");
                string checks = File.ReadAllText(Path.Combine(session, "internet-checks.csv"));
                Require(checks.Contains(",\"failure\",") && checks.Contains(",\"error\","), "ICMP output lost failure/error distinction");
                string probes = File.ReadAllText(Path.Combine(session, "probes.csv"));
                Require(probes.Split('\n')[0].Contains("network_failure") && probes.Contains("false,false,true"), "raw ICMP failure flag missing");

                window.StartSession(); window.controller.Stop(); Thread.Sleep(30);
                for (int i = 0; i < 2; i++) { InternetCheckSample failed = Fixture("dns", "timeout", DateTime.UtcNow); failed.Generation = window.internet.Generation; window.RecordInternetCheck(failed); Thread.Sleep(20); }
                Require(window.history.GetRecent(100).Any(x => x.Kind == "dns" && x.State == "open"), "clock fixture open history missing");
                string clockSession = window.lastDirectory;
                DateTime completed = DateTime.UtcNow;
                window.RecordInternetCheck(new InternetCheckSample { Kind = "https", Target = DefaultHttpsUrl, State = "success", Success = true, Generation = window.internet.Generation, StartedUtc = completed.AddSeconds(1), TimestampUtc = completed });
                MainWindow owned = window;
                PumpWindowTestUntil(delegate { return !owned.running; }, 3000, "inverted clock did not stop measurement");
                Require(window.fault && window.stopReason.Contains("시스템 시각 변경") && window.clockStopUtc != DateTime.MinValue, "clock discontinuity stop was silent");
                Require(window.history.GetRecent(100).Where(x => x.Kind == "dns").All(x => x.State != "open" && !x.RecoveredUtc.HasValue), "clock change invented recovery");
                Require(File.ReadAllText(Path.Combine(clockSession, "session.txt")).Contains("state=completed") && File.ReadAllText(Path.Combine(clockSession, "summary.txt")).Contains("시스템 시각 변경"), "clock stop was not finalized and explained");
                Require(!File.ReadAllText(Path.Combine(clockSession, "internet-checks.csv")).Contains(",\"https\","), "inverted result entered normal observations");
                window.StartSession(); Require(window.running && !window.fault && !window.stopPending && window.clockStopUtc == DateTime.MinValue, "new clock epoch did not reset");
                window.controller.Stop();
                window.sessionClock.Reset(DateTime.UtcNow.AddMinutes(10), Stopwatch.GetTimestamp());
                window.Poll();
                Require(!window.running && window.fault && window.stopReason.Contains("시스템 시각 변경"), "backward clock poll did not end the epoch");
                window.StartSession(); Require(window.running && !window.fault, "explicit restart after rollback failed");
                window.StopSession("feedback complete", null);
                // An older epoch can have future UTC values after a wall-clock
                // rollback. Preserve its on-screen history, never its session ownership.
                DateTime future = DateTime.UtcNow.AddMinutes(10);
                string olderId = Guid.NewGuid().ToString("N");
                System.Reflection.FieldInfo historyField = typeof(ConnectivityHistory).GetField("incidents", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                System.Collections.Generic.List<ConnectivityIncident> retained = (System.Collections.Generic.List<ConnectivityIncident>)historyField.GetValue(window.history);
                retained.Add(new ConnectivityIncident { Id = olderId, Kind = "dns", Target = "older-epoch.example", Category = "failure", State = "ended", Scope = "DNS 검사", Summary = "synthetic previous epoch", Version = 1, Failures = 2, StartedUtc = future, LastObservedUtc = future, ClosedUtc = future });
                window.StartSession(); window.controller.Stop(); string nextSession = window.lastDirectory;
                window.StopSession("new epoch ownership check", null);
                Require(window.history.GetRecent(500).Any(x => x.Id == olderId), "old epoch history was deleted");
                Require(!File.ReadAllText(Path.Combine(nextSession, "summary.txt")).Contains("older-epoch.example"), "old epoch incident leaked into a new session summary");
                File.WriteAllText(reportPath, "PASS: synthetic ICMP failure/error/UI/CSV distinction, target-change recovery-unknown boundary, stopped card reset, in-flight time inversion and backward-clock safe stop, finalized history/summary and explicit restart. OS clock and network configuration were not changed.\r\n", new UTF8Encoding(true));
            }
            finally { if (window != null) { window.appExitRequested = true; try { window.Close(); } finally { window.DisposeMonitor(); } } }
        }
    }
}
