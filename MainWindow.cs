using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PcInternetMonitor
{
    public sealed partial class MainWindow : Window
    {
        private const string DefaultReferenceTarget = "1.1.1.1", DefaultDnsHost = "www.microsoft.com", DefaultHttpsUrl = "https://www.gstatic.com/generate_204", AppVersion = App.Version;
        private static readonly Color BackColor = Color.FromRgb(13, 15, 18), PanelColor = Color.FromRgb(19, 22, 26), BorderColor = Color.FromRgb(39, 44, 50), TextColor = Color.FromRgb(231, 234, 238), MutedColor = Color.FromRgb(147, 154, 164), LabelColor = Color.FromRgb(185, 192, 202), AccentColor = Color.FromRgb(87, 214, 206), AmberColor = Color.FromRgb(232, 186, 117), ErrorColor = Color.FromRgb(241, 124, 131);
        internal static readonly FontFamily TerminalFont = new FontFamily("Consolas, Malgun Gothic");
        private readonly MonitorController controller = new MonitorController();
        private readonly InternetCheckRunner internet = new InternetCheckRunner();
        private readonly ConnectivityHistory history = new ConnectivityHistory();
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private readonly object recordingGate = new object();
        private readonly ComboBox adapterPicker = new ComboBox();
        private readonly Button startButton = new Button(), stopButton = new Button(), refreshButton = new Button(), logFolderButton = new Button(), exitButton = new Button();
        private readonly string logRoot;
        private readonly bool isolatedTest;
        private AutomaticSessionLog automaticLog;
        private InternetSessionSummary internetSummary = new InternetSessionSummary();
        private List<ConnectivityIncident> archived = new List<ConnectivityIncident>();
        private bool running, disposed, fault, stopPending, historyDirty = true, liveInternetForTest, sessionEnding;
        private DateTime beganUtc, lastSampleUtc;
        private string lastDirectory = "", notice = "", stopReason = "검사를 시작하면 연결 상태를 확인하고 기록합니다.", gatewayOverride, referenceOverride;
        private long sampleCount;
        private Exception nextPollErrorForTest;
        private MonitorSnapshot latest;
        private InternetCheckSnapshot latestInternet;

        public MainWindow() : this(null, null) { }
        public MainWindow(StartupSelection selection) : this(selection, null) { }
        public MainWindow(StartupSelection selection, string recordingRoot)
        {
            logRoot = recordingRoot ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs"); isolatedTest = recordingRoot != null;
            InitializeWindowFrame();
            Title = "PC 인터넷 모니터 " + AppVersion; Background = Brush(BackColor); Foreground = Brush(TextColor); FontFamily = TerminalFont;
            UseLayoutRounding = true; SnapsToDevicePixels = true; TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display); TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            MaxWidth = SystemParameters.WorkArea.Width; MaxHeight = SystemParameters.WorkArea.Height;
            Width = Math.Min(1040, MaxWidth); Height = Math.Min(740, MaxHeight); MinWidth = Math.Min(960, MaxWidth); MinHeight = Math.Min(660, MaxHeight);
            try { using (Stream icon = typeof(MainWindow).Assembly.GetManifestResourceStream("PcInternetMonitor.AppIcon.ico")) if (icon != null) Icon = BitmapFrame.Create(icon, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); }
            catch (Exception error) { DiagnosticsLog.Write("icon_load_failed", "embedded icon", error); }
            InstallTerminalStyles(); InstallPickerStyles(); Content = BuildWindow(); RefreshSources(selection == null ? null : selection.AdapterId);
            if (selection != null) { gatewayOverride = selection.Gateway; referenceOverride = selection.UseReference ? selection.Reference : null; }
            refreshButton.Click += delegate { RefreshSources(null); }; startButton.Click += delegate { StartSession(); }; stopButton.Click += delegate { StopSession("사용자 중지", null); }; logFolderButton.Click += delegate { OpenLogFolder(); }; exitButton.Click += delegate { RequestAppExit(); };
            controller.GatewayTargetChanged += RecordGatewayTargetChanged; controller.ProbeSampled += RecordProbe; internet.Sampled += RecordInternetCheck; history.Changed += RecordIncident;
            timer.Interval = TimeSpan.FromSeconds(1); timer.Tick += delegate { Poll(); };
            Closing += HandleWindowClosing;
            Closed += delegate { DisposeMonitor(); DiagnosticsLog.Write("window_closed", "session=" + lastDirectory); };
            SetControls(); RefreshPresentation(); LoadHistory();
        }
        private void RefreshSources(string preferred)
        {
            string id = preferred ?? (adapterPicker.SelectedItem is AdapterInfo ? ((AdapterInfo)adapterPicker.SelectedItem).Id : AdapterCatalog.GuessDefaultAdapterId());
            try { List<AdapterInfo> adapters = AdapterCatalog.GetAdapters(); adapterPicker.ItemsSource = adapters; adapterPicker.SelectedItem = adapters.FirstOrDefault(x => x.Id == id) ?? adapters.FirstOrDefault(x => x.IsUp) ?? adapters.FirstOrDefault(); }
            catch (Exception error) { notice = "PC 연결 목록 확인 실패: " + error.Message; }
        }
        private void LoadHistory()
        {
            if (isolatedTest) return;
            Task.Run(delegate {
                try { IList<ConnectivityIncident> saved = ConnectivityHistoryArchive.LoadRecent(logRoot, 500); Dispatcher.BeginInvoke(new Action(delegate { if (!disposed) { archived = saved.ToList(); historyDirty = true; RefreshHistory(); } })); }
                catch (Exception error) { DiagnosticsLog.Write("history_load_failed", "previous session history", error); }
            });
        }
        private void StartSession()
        {
            if (running || disposed) return;
            AdapterInfo selected = adapterPicker.SelectedItem as AdapterInfo;
            if (selected == null) { notice = "확인할 PC 연결을 선택하세요."; RefreshPresentation(); return; }
            try
            {
                AdapterInfo current = AdapterCatalog.GetAdapters().FirstOrDefault(x => x.Id == selected.Id) ?? selected;
                string gateway = !String.IsNullOrEmpty(gatewayOverride) ? gatewayOverride : current.Gateway;
                string reference = !String.IsNullOrEmpty(referenceOverride) ? referenceOverride : DefaultReferenceTarget;
                lock (recordingGate) { beganUtc = DateTime.UtcNow; sessionClock.Reset(beganUtc, Stopwatch.GetTimestamp()); sessionElapsed.Restart(); clockStopUtc = DateTime.MinValue; sessionIncidentIds.Clear(); lastSampleUtc = DateTime.MinValue; sampleCount = 0; fault = false; notice = ""; latest = null; latestInternet = null; internetSummary = new InternetSessionSummary(); stopPending = false; }
                AutomaticSessionLog created = new AutomaticSessionLog(logRoot); lock (recordingGate) { automaticLog = created; lastDirectory = created.SessionDirectory; }
                File.WriteAllText(Path.Combine(lastDirectory, "settings.txt"), "app_version=" + AppVersion + "\r\npurpose=pc_internet_status\r\nadapter=" + current.Name + "\r\nadapter_id=" + current.Id + "\r\ngateway=" + gateway + "\r\nreference=" + reference + "\r\ndns=" + DefaultDnsHost + "\r\nhttps=" + DefaultHttpsUrl + "\r\nhttps_expected_status=204\r\ngame_collection_requested=False\r\nprocess_traffic_requested=False\r\ntcp_rtt_requested=False\r\nrouting=Windows routing and proxy; not bound to selected adapter\r\ndiagnostics_file=" + DiagnosticsLog.FilePath + "\r\n", new UTF8Encoding(true));
                lock (recordingGate) { history.Start(beganUtc); running = true; }
                controller.Start(0, current.Id, gateway, reference, false, false, String.IsNullOrEmpty(gatewayOverride));
                if (!isolatedTest || liveInternetForTest) internet.Start(DefaultDnsHost, DefaultHttpsUrl);
                DiagnosticsLog.Write("measurement_start", "purpose=pc_internet_status; session=" + lastDirectory);
                historyDirty = true; SetControls(); timer.Start(); Poll();
            }
            catch (Exception error) { StopSession("검사 시작 실패", error); }
        }
        private void RecordProbe(string kind, ProbeSample probe)
        {
            AutomaticSessionLog observed = null;
            try
            {
                lock (recordingGate)
                {
                    if (!running || automaticLog == null) return;
                    observed = automaticLog;
                    CheckObservationClockLocked();
                    if (probe.TimestampUtc < beganUtc) return;
                    if (!automaticLog.RecordProbe(kind, probe)) throw new IOException(automaticLog.ErrorMessage);
                    RecordCheckLocked(new InternetCheckSample { Kind = kind, Target = probe.Target, StartedUtc = probe.TimestampUtc, TimestampUtc = probe.TimestampUtc, Success = probe.Countable && !probe.TimedOut && probe.RoundtripMs.HasValue, TimedOut = probe.TimedOut, DurationMs = probe.RoundtripMs, State = probe.NetworkFailure ? "failure" : !probe.Countable ? "error" : probe.TimedOut ? "timeout" : "success", Status = probe.Status });
                }
            }
            catch (Exception error) { QueueFailure(error is SessionClockChangedException ? ClockStopReason : "ICMP 기록 오류", error, observed); }
        }
        private void RecordInternetCheck(InternetCheckSample sample)
        {
            AutomaticSessionLog observed = null;
            try { lock (recordingGate) { if (!running || automaticLog == null || sample.Generation != internet.Generation) return; observed = automaticLog; CheckObservationClockLocked(sample); if (sample.StartedUtc < beganUtc) return; RecordCheckLocked(sample); } }
            catch (Exception error) { QueueFailure(error is SessionClockChangedException ? ClockStopReason : "DNS/HTTPS 기록 오류", error, observed); }
        }
        private void RecordCheckLocked(InternetCheckSample sample)
        {
            if (!automaticLog.AppendInternetCheck(sample)) throw new IOException(automaticLog.ErrorMessage);
            internetSummary.ObserveCheck(sample); history.Observe(sample);
        }
        private void RecordIncident(ConnectivityIncident incident)
        {
            lock (recordingGate)
            {
                historyDirty = true;
                if (!running || automaticLog == null) return;
                if (incident.State == "open") sessionIncidentIds.Add(incident.Id);
                if (!sessionIncidentIds.Contains(incident.Id)) return;
                if (sessionIncidentIds.Count > 512) sessionIncidentIds.IntersectWith(history.GetRecent(500).Select(x => x.Id));
                if (!automaticLog.AppendConnectivityIncident(incident)) throw new IOException(automaticLog.ErrorMessage);
            }
        }
        private void QueueFailure(string stage, Exception error, AutomaticSessionLog expected)
        {
            DiagnosticsLog.Write("check_callback_failed", stage, error); AutomaticSessionLog observed;
            lock (recordingGate) { if (stopPending || !running || expected == null || !Object.ReferenceEquals(expected, automaticLog)) return; stopPending = true; observed = automaticLog; }
            try { Dispatcher.BeginInvoke(new Action(delegate { if (!disposed && Object.ReferenceEquals(observed, automaticLog)) StopSession(stage, error); })); }
            catch (Exception dispatchError) { DiagnosticsLog.Write("check_failure_dispatch_failed", stage, dispatchError); }
        }
        private void Poll()
        {
            if (!running) return;
            try
            {
                lock (recordingGate) { if (stopPending) return; CheckObservationClockLocked(); }
                if (nextPollErrorForTest != null) { Exception error = nextPollErrorForTest; nextPollErrorForTest = null; throw error; }
                MonitorSnapshot snapshot = controller.Snapshot(); InternetCheckSnapshot checks = isolatedTest && !liveInternetForTest ? latestInternet : internet.Snapshot(DateTime.UtcNow);
                lock (recordingGate)
                {
                    if (stopPending) return;
                    CheckObservationClockLocked();
                    if (automaticLog == null || !automaticLog.Append(snapshot)) throw new IOException(automaticLog == null ? "기록기가 없습니다." : automaticLog.ErrorMessage);
                    internetSummary.ObserveSnapshot(snapshot); history.ObserveAdapter(snapshot.TimestampUtc, snapshot.AdapterCountersAvailable, snapshot.AdapterStatus); history.CheckGaps(snapshot.TimestampUtc);
                    sampleCount = automaticLog.SampleCount; lastSampleUtc = snapshot.TimestampUtc; latest = snapshot; latestInternet = checks;
                }
                snapshot.EndpointObservations.Clear(); snapshot.Endpoints.Clear(); snapshot.ProcessTraffic.Rows.Clear(); snapshot.TcpRtt.Connections.Clear(); RefreshPresentation();
            }
            catch (Exception error) { StopSession(error is SessionClockChangedException ? ClockStopReason : "검사 기록 오류", error); }
        }
        private void StopSession(string reason, Exception failure)
        {
            timer.Stop(); sessionElapsed.Stop();
            if (!running && automaticLog == null) { if (failure != null) { fault = true; stopReason = reason + ": " + failure.Message; } RefreshPresentation(); return; }
            lock (recordingGate) { try { CheckObservationClockLocked(); } catch (SessionClockChangedException error) { reason = ClockStopReason; failure = error; } }
            DiagnosticsLog.Write("measurement_stop_requested", "reason=" + reason, failure);
            try { internet.Stop(); MonitorSnapshot final = controller.StopAndSnapshot(); lock (recordingGate) { if (final != null && automaticLog != null) { try { CheckObservationClockLocked(); } catch (SessionClockChangedException error) { reason = ClockStopReason; failure = error; } if (clockStopUtc == DateTime.MinValue && !automaticLog.Append(final)) throw new IOException(automaticLog.ErrorMessage); internetSummary.ObserveSnapshot(final); latest = final; sampleCount = automaticLog.SampleCount; if (clockStopUtc == DateTime.MinValue) lastSampleUtc = final.TimestampUtc; } } }
            catch (Exception error) { failure = failure ?? error; DiagnosticsLog.Write("measurement_final_failed", reason, error); }
            finally { try { internet.Stop(); } catch (Exception error) { failure = failure ?? error; } try { controller.Stop(); } catch (Exception error) { failure = failure ?? error; } }
            AutomaticSessionLog completed;
            DateTime endUtc = clockStopUtc != DateTime.MinValue ? clockStopUtc : sessionClock.LastStableUtc;
            lock (recordingGate)
            {
                try { history.Stop(endUtc, reason); } catch (Exception error) { failure = failure ?? error; }
                running = false; completed = automaticLog; automaticLog = null; fault = failure != null; stopReason = reason + (failure == null ? "" : ": " + failure.Message); historyDirty = true;
            }
            if (completed != null)
            {
                try
                {
                    if (failure != null) File.AppendAllText(Path.Combine(completed.SessionDirectory, "errors.txt"), DateTime.UtcNow.ToString("o") + "\r\n" + reason + "\r\n" + failure + "\r\n", new UTF8Encoding(true));
                    completed.Complete(reason); if (!completed.IsHealthy) { fault = true; stopReason = "기록 저장 오류: " + completed.ErrorMessage; }
                    string text = "PC 인터넷 모니터 " + AppVersion + "\r\n저장 상태: " + (completed.IsHealthy ? "파일 기록 완료" : "오류") + "\r\n" + internetSummary.BuildText(history.GetRecent(500).Where(x => sessionIncidentIds.Contains(x.Id)).ToList(), history.DroppedCount, endUtc, reason);
                    File.WriteAllText(Path.Combine(completed.SessionDirectory, "summary.txt"), text, new UTF8Encoding(true));
                }
                catch (Exception error) { fault = true; stopReason = "기록 마무리 오류: " + error.Message; DiagnosticsLog.Write("recording_complete_failed", reason, error); }
                finally { completed.Dispose(); }
            }
            SetControls(); RefreshPresentation(); DiagnosticsLog.Write("measurement_stopped", stopReason);
        }
        internal void AllowSystemShutdown() { sessionEnding = true; }
        private void HandleWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!appExitRequested && !sessionEnding && !disposed && HideToBackground()) { e.Cancel = true; return; }
            NotifyBackgroundClosing();
            App.NoteExitRequest(sessionEnding ? "Windows 종료 요청" : appExitRequested ? "앱 종료 요청" : "창 닫기 요청");
            DiagnosticsLog.Write("window_closing", "recording=" + running + "; session_ending=" + sessionEnding + "; explicit_exit=" + appExitRequested);
            DisposeMonitor();
        }
        private void SetControls() { startButton.IsEnabled = !running && !disposed; stopButton.IsEnabled = running && !disposed; adapterPicker.IsEnabled = refreshButton.IsEnabled = !running; exitButton.IsEnabled = !disposed && !appExitRequested; }
        private void OpenLogFolder() { try { string path = lastDirectory.Length == 0 ? logRoot : lastDirectory; Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception error) { notice = "기록 폴더 열기 실패: " + error.Message; RefreshPresentation(); } }
        private void DisposeMonitor() { if (disposed) return; try { StopSession(sessionEnding ? "Windows 종료" : appExitRequested ? "앱 종료" : "창 닫기", null); } finally { disposed = true; backgroundReady = false; timer.Stop(); internet.Dispose(); controller.Dispose(); } }
        private static SolidColorBrush Brush(Color color) { SolidColorBrush b = new SolidColorBrush(color); b.Freeze(); return b; }
        private static TextBlock Text(string value, double size, Color color, bool bold) { return new TextBlock { Text = value, FontSize = size, Foreground = Brush(color), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap }; }
        private static string Ms(double value) { return value == 0 ? "1 ms 미만" : value.ToString("N1", CultureInfo.CurrentCulture) + " ms"; }
        private static string Rate(double bytes) { return bytes >= 1048576 ? (bytes / 1048576).ToString("N1") + " MiB/s" : bytes >= 1024 ? (bytes / 1024).ToString("N1") + " KiB/s" : bytes.ToString("N0") + " B/s"; }
        private void RenderTestSurface(string path, int width, int height) { FrameworkElement surface = (FrameworkElement)Content; surface.Measure(new Size(width, height)); surface.Arrange(new Rect(0, 0, width, height)); surface.UpdateLayout(); RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface); PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))); using (FileStream output = File.Create(path)) encoder.Save(output); }
    }
}
