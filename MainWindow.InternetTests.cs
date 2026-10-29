using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PcInternetMonitor
{
    public sealed partial class MainWindow
    {
        private static InternetCheckSample Fixture(string kind, string state, DateTime at)
        {
            return new InternetCheckSample { Kind = kind, State = state, Target = kind == "dns" ? DefaultDnsHost : DefaultHttpsUrl, StartedUtc = at.AddMilliseconds(-20), TimestampUtc = at, Success = state == "success", TimedOut = state == "timeout", DurationMs = 20, HttpStatusCode = kind == "https" ? (int?)(state == "success" ? 204 : 503) : null, Status = "합성 화면 검사 · 실제 측정 아님" };
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("Internet UI test: " + message); }
        public void SmokeTest(string screenshotPath)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(screenshotPath));
            Require(!running && startButton.IsEnabled && !stopButton.IsEnabled && cards.Children.Count == 4, "initial control state");
            RenderTestSurface(screenshotPath, 1040, 740);
            DateTime at = DateTime.UtcNow.AddMinutes(-1); beganUtc = at; running = true; history.Start(at); SetControls();
            latest = new MonitorSnapshot { TimestampUtc = DateTime.UtcNow, AdapterCountersAvailable = true, AdapterRatesAvailable = true, AdapterReceivedBytesPerSecond = 51200, AdapterSentBytesPerSecond = 2048, Gateway = new ProbeSummary { Target = "192.0.2.1", CurrentMs = 0, Sent = 50 }, Reference = new ProbeSummary { Target = "198.51.100.1", CurrentMs = 12.5, Sent = 50 } };
            latestInternet = new InternetCheckSnapshot { Running = true, Dns = Fixture("dns", "success", DateTime.UtcNow), Https = Fixture("https", "success", DateTime.UtcNow) };
            history.Observe(Fixture("dns", "timeout", at.AddSeconds(10))); history.Observe(Fixture("dns", "timeout", at.AddSeconds(15))); history.Observe(Fixture("dns", "success", at.AddSeconds(20)));
            history.Observe(Fixture("https", "failure", at.AddSeconds(30))); history.Observe(Fixture("https", "failure", at.AddSeconds(35)));
            historyDirty = true; RefreshPresentation();
            Require(values["dns"].Text == "조회 성공" && values["https"].Text == "접속 성공" && values["gateway"].Text == "1 ms 미만", "successful checks missing");
            Require(historyList.Items.Count >= 2 && history.GetRecent(10).Any(x => x.State == "recovered") && history.GetRecent(10).Any(x => x.State == "open"), "recovered and open histories missing");
            recordingDetail.Text = "합성 화면 예시 · 실제 인터넷 검사 결과가 아닙니다."; RenderTestSurface(Path.Combine(directory, "internet-dashboard-example.png"), 1040, 740); CheckBounds(1040, 740);
            for (int i = 0; i < 110; i++) archived.Add(new ConnectivityIncident { Id = Guid.NewGuid().ToString("N"), Version = 1, Kind = "dns", Target = DefaultDnsHost, Category = "failure", Scope = "DNS", Summary = "이전 기록 합성 예시", State = "ended", StartedUtc = at.AddMinutes(-i - 1), LastObservedUtc = at.AddMinutes(-i - 1).AddSeconds(10), ClosedUtc = at.AddMinutes(-i - 1).AddSeconds(10), Failures = 2 });
            historyDirty = true; RefreshHistory(); Require(historyList.Items.Count == 100, "history display cap missing"); historyList.ScrollIntoView(historyList.Items[99]); RenderTestSurface(Path.Combine(directory, "internet-history-scrolled.png"), 960, 660); CheckBounds(960, 660);
            archived.Clear(); historyDirty = true;
            latestInternet.Dns = Fixture("dns", "timeout", DateTime.UtcNow); latestInternet.Https = Fixture("https", "failure", DateTime.UtcNow); latest.Gateway.CurrentMs = null; latest.Gateway.CurrentError = true; RefreshPresentation();
            Require(values["dns"].Text == "시간 초과" && values["https"].Text == "접속 실패" && ((SolidColorBrush)values["dns"].Foreground).Color == AmberColor && values["gateway"].Text == "검사 오류", "failed checks retained success or zero RTT");
            notice = String.Concat(Enumerable.Repeat("작은 창의 긴 안내 배치 검사입니다. DNS나 HTTPS 검사 대상의 실패는 통신사 장애 확정이 아닙니다. ", 7)); RefreshPresentation(); RenderTestSurface(Path.Combine(directory, "internet-small-window.png"), 960, 660); CheckBounds(960, 660); Require(recordingDetail.ActualHeight <= 48 && ((string)recordingDetail.ToolTip).Contains(notice), "long notice overflowed or lost its full tooltip");
            latestInternet.Dns.State = "stale"; latestInternet.Dns.Success = true; RefreshPresentation(); Require(values["dns"].Text == "최근 값 없음" && ((SolidColorBrush)values["dns"].Foreground).Color == MutedColor, "stale check retained success");
            running = false; history.Stop(DateTime.UtcNow, "합성 검사 중지"); SetControls(); RefreshPresentation(); Require(values["dns"].Text == "—" && values["https"].Text == "—" && values["gateway"].Text == "—", "stopped checks retained current numbers");
            fault = true; stopReason = "합성 기록 오류"; RefreshPresentation(); Require(recordingTitle.Text.Contains("오류") && ((SolidColorBrush)recordingTitle.Foreground).Color == ErrorColor, "recording error missing");
            RenderTestSurface(Path.Combine(directory, "internet-error-stopped.png"), 960, 660);
        }
        private void CheckBounds(int width, int height)
        {
            FrameworkElement surface = (FrameworkElement)Content;
            foreach (FrameworkElement element in new FrameworkElement[] { adapterPicker, startButton, stopButton, cards, historyList, dashboardFooter })
            {
                Point p = element.TranslatePoint(new Point(), surface);
                Require(element.ActualWidth > 0 && element.ActualHeight > 0 && p.X >= 0 && p.Y >= 0 && p.X + element.ActualWidth <= width + 1 && p.Y + element.ActualHeight <= height, "fixed controls outside viewport");
            }
            Require(historyList.ActualHeight >= 90, "history viewport collapsed");
        }
        public void RecordingTest(string reportPath)
        {
            Require(isolatedTest, "recording test requires its own log root"); gatewayOverride = referenceOverride = "127.0.0.1";
            StartSession(); Require(running, "test session did not start"); Thread.Sleep(1200); Poll();
            Require(!latest.GameCollectionRequested && !latest.ProcessCollectionRequested && !latest.Trace.Active, "basic checks requested game or ETW");
            InternetCheckSample first = Fixture("dns", "timeout", DateTime.UtcNow); RecordInternetCheck(first); Thread.Sleep(5); RecordInternetCheck(Fixture("dns", "timeout", DateTime.UtcNow)); Thread.Sleep(5); RecordInternetCheck(Fixture("dns", "success", DateTime.UtcNow));
            string firstDirectory = lastDirectory; StopSession("사용자 중지", null); Require(!running && !fault && !stopButton.IsEnabled && startButton.IsEnabled, "stop state invalid");
            string csv = File.ReadAllText(Path.Combine(firstDirectory, "internet-checks.csv")); string incidentCsv = File.ReadAllText(Path.Combine(firstDirectory, "connectivity-incidents.csv"));
            Require(csv.Contains("dns") && incidentCsv.Contains("recovered") && File.ReadAllText(Path.Combine(firstDirectory, "summary.txt")).Contains("DNS"), "DNS/history/summary not flushed");
            Require(ConnectivityHistoryArchive.LoadRecent(logRoot, 100).Any(x => x.State == "recovered"), "history not restored from log");
            StartSession(); Require(running, "restart failed"); string errorDirectory = lastDirectory; nextPollErrorForTest = new InvalidOperationException("인터넷 기록 오류 주입"); Poll();
            Require(!running && fault && File.ReadAllText(Path.Combine(errorDirectory, "errors.txt")).Contains("InvalidOperationException"), "injected failure stack missing");
            StartSession(); Require(running && !fault, "error recovery failed"); string closeDirectory = lastDirectory; DisposeMonitor();
            Require(!running && File.ReadAllText(Path.Combine(closeDirectory, "session.txt")).Contains("state=completed"), "close did not complete recording");
            File.WriteAllText(reportPath, "PASS v" + AppVersion + ": actual PC WPF start/poll/stop, no game/ETW request, synthetic DNS failure/recovery through callbacks to CSV/history/summary/archive, restart, injected error stack, recovery and close. Only loopback ICMP; no external DNS/HTTPS or UAC.\r\n", new UTF8Encoding(true));
        }
        public void InternetRecordingTest(string reportPath)
        {
            Require(isolatedTest, "live recording requires isolated log directory"); liveInternetForTest = true; gatewayOverride = referenceOverride = "127.0.0.1";
            StartSession(); Require(running, "live recording did not start");
            for (int i = 0; i < 7 && running; i++) { Thread.Sleep(1000); Poll(); }
            string folder = lastDirectory; StopSession("실제 DNS/HTTPS 통합 검사 종료", null);
            Require(!fault, "live recording failed");
            string csv = File.ReadAllText(Path.Combine(folder, "internet-checks.csv")); string summary = File.ReadAllText(Path.Combine(folder, "summary.txt"));
            Require(csv.Contains(",\"dns\",") && csv.Contains(",\"https\",") && summary.Contains("DNS") && summary.Contains("HTTPS"), "actual check callbacks did not reach log and summary");
            File.WriteAllText(reportPath, "PASS v" + AppVersion + ": real DNS/HTTPS through normal WPF start/callback/poll/stop into isolated CSV and summary. Only ICMP is loopback. Network success/failure is evidence in the saved CSV, not implied by this flow test.\r\nfixture=" + folder + "\r\n", new UTF8Encoding(true));
        }
    }
}
