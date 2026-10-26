using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace GameNetworkMonitor
{
    public sealed partial class MainWindow : Window
    {
        private const string DefaultReferenceTarget = "1.1.1.1";
        private const string AppVersion = App.Version;
        private static readonly Color BackColor = Color.FromRgb(16, 21, 29);
        private static readonly Color PanelColor = Color.FromRgb(25, 33, 44);
        private static readonly Color BorderColor = Color.FromRgb(48, 62, 79);
        private static readonly Color TextColor = Color.FromRgb(233, 241, 249);
        private static readonly Color MutedColor = Color.FromRgb(158, 177, 197);
        private static readonly Color AccentColor = Color.FromRgb(72, 218, 220);
        private static readonly Color AmberColor = Color.FromRgb(255, 195, 108);
        private static readonly Color ErrorColor = Color.FromRgb(255, 139, 151);
        private readonly MonitorController controller = new MonitorController();
        private readonly SessionLog session = new SessionLog();
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private readonly ComboBox processPicker = new ComboBox();
        private readonly ComboBox adapterPicker = new ComboBox();
        private readonly TextBox gatewayInput = new TextBox();
        private readonly TextBox referenceInput = new TextBox();
        private readonly Button startButton = new Button();
        private readonly Button stopButton = new Button();
        private readonly Button refreshButton = new Button();
        private readonly Button exportButton = new Button();
        private readonly Button administratorButton = new Button();
        private readonly Button logFolderButton = new Button();
        private readonly Border permissionBanner = new Border();
        private readonly TextBlock permissionMessage = new TextBlock();
        private readonly bool administrator = ElevationSupport.IsAdministrator();
        private readonly TextBlock stateText = new TextBlock();
        private readonly Border recordingBanner = new Border();
        private readonly TextBlock recordingStateTitle = new TextBlock();
        private readonly TextBlock recordingStateDetail = new TextBlock();
        private readonly TextBlock statusLine = new TextBlock();
        private readonly TextBlock gameStatus = new TextBlock();
        private readonly TextBlock traceWarning = new TextBlock();
        private readonly TextBlock gatewayStatus = new TextBlock();
        private readonly TextBlock referenceStatus = new TextBlock();
        private readonly TextBlock adapterStatus = new TextBlock();
        private readonly TextBlock sessionStatus = new TextBlock();
        private readonly TextBlock endpointStatus = new TextBlock();
        private readonly TextBlock recordingStatus = new TextBlock();
        private readonly object recordingGate = new object();
        private AutomaticSessionLog automaticLog;
        private SessionAnalysis analysis;
        private string lastLogDirectory, lastRecordingError = "";
        private readonly string logRoot;
        private DateTime summaryUpdatedUtc;
        private readonly Dictionary<string, TextBlock> values = new Dictionary<string, TextBlock>();
        private readonly SparkChart trafficChart = new SparkChart(false);
        private readonly SparkChart gatewayChart = new SparkChart(true);
        private readonly DataGrid endpointGrid = new DataGrid();
        private readonly DataGrid processTrafficGrid = new DataGrid();
        private readonly TextBlock processTrafficStatus = new TextBlock();
        private readonly ListBox eventList = new ListBox();
        private readonly TabControl tabs = new TabControl();
        private bool running;
        private bool disposed;
        private DateTime began;
        private string previousTraceStatus;
        private bool recordingFault, collectionLimited;
        private string recordingFaultMessage = "", lastStopReason = "아직 기록을 시작하지 않았습니다.";
        private DateTime measurementBeganUtc, lastSampleUtc;
        private long recordingSampleCount;
        private int probeFailureQueued;
        private Exception nextPollErrorForTest;

        public MainWindow() : this(null) { }

        public MainWindow(StartupSelection selection) : this(selection, null) { }

        public MainWindow(StartupSelection selection, string recordingRoot)
        {
            logRoot = recordingRoot ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Title = "게임 네트워크 모니터 " + AppVersion + " · " + (administrator ? "관리자 모드" : "일반 모드");
            MaxWidth = SystemParameters.WorkArea.Width; MaxHeight = SystemParameters.WorkArea.Height;
            Width = Math.Min(1240, MaxWidth); Height = Math.Min(920, MaxHeight);
            MinWidth = Math.Min(1000, MaxWidth); MinHeight = Math.Min(700, MaxHeight);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brush(BackColor); Foreground = Brush(TextColor);
            FontFamily = new FontFamily("Malgun Gothic"); FontSize = 13;
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            InstallStyles();
            Content = BuildWindow();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += delegate { Poll(); };
            processPicker.SelectionChanged += delegate { UpdateSelectionHint(); };
            adapterPicker.SelectionChanged += delegate {
                AdapterInfo selected = adapterPicker.SelectedItem as AdapterInfo;
                if (!running && selected != null) gatewayInput.Text = selected.Gateway ?? "";
                UpdateSelectionHint();
            };
            refreshButton.Click += delegate { RefreshSources(); };
            startButton.Click += delegate { StartSession(); };
            stopButton.Click += delegate { StopSession("사용자 중지"); };
            exportButton.Click += delegate { Export(); };
            administratorButton.Click += delegate { OpenAdministrator(); };
            logFolderButton.Click += delegate { OpenLogFolder(); };
            controller.ProbeSampled += RecordProbe;
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) {
                App.NoteExitRequest("창 닫기 요청");
                DiagnosticsLog.Write("window_closing", "recording=" + running + "; cancelled=" + e.Cancel + "; session=" + (lastLogDirectory ?? ""));
                DisposeMonitor();
            };
            Closed += delegate { DiagnosticsLog.Write("window_closed", "session=" + (lastLogDirectory ?? "")); };
            RefreshSources();
            RestoreSelection(selection);
            SetControls(false);
            statusLine.Text = "측정 시작 후 게임·전체 통신 프로그램·공유기·외부 ICMP를 자동 기록합니다.";
            gameStatus.Text = "선택한 게임의 송수신 이벤트를 기다립니다.";
            gatewayStatus.Text = "공유기에 확인 요청을 보낼 준비가 되었습니다.";
            referenceStatus.Text = "외부 ICMP 비교 검사 대기";
            adapterStatus.Text = "선택한 연결의 현재 사용량 · 측정 대기";
            traceWarning.Text = "게임 ETW 수집 권한이 없으면 안내를 표시하고 PC·공유기·외부 ICMP 측정을 계속합니다.";
            DiagnosticsLog.Write("window_ready", "administrator=" + administrator + "; log_root=" + LogRoot);
            RefreshRecordingIndicators();
        }

        private void InstallStyles()
        {
            Style button = new Style(typeof(Button));
            button.Setters.Add(new Setter(Control.BackgroundProperty, Brush(Color.FromRgb(38, 53, 68))));
            button.Setters.Add(new Setter(Control.ForegroundProperty, Brush(TextColor)));
            button.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(BorderColor)));
            button.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            button.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16, 9, 16, 9)));
            button.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory shell = new FrameworkElementFactory(typeof(Border));
            shell.Name = "shell";
            shell.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            shell.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            shell.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
            shell.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetBinding(FrameworkElement.MarginProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            shell.AppendChild(presenter); template.VisualTree = shell;
            Trigger hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, Brush(AccentColor), "shell")); template.Triggers.Add(hover);
            Trigger disabled = new Trigger { Property = IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .42)); template.Triggers.Add(disabled);
            button.Setters.Add(new Setter(Control.TemplateProperty, template)); Resources.Add(typeof(Button), button);

            Style text = new Style(typeof(TextBox));
            text.Setters.Add(new Setter(Control.BackgroundProperty, Brush(Color.FromRgb(13, 20, 29))));
            text.Setters.Add(new Setter(Control.ForegroundProperty, Brush(TextColor)));
            text.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(BorderColor)));
            text.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 7, 9, 7)));
            text.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            text.Setters.Add(new Setter(TextBoxBase.CaretBrushProperty, Brush(AccentColor))); Resources.Add(typeof(TextBox), text);
            Style combo = new Style(typeof(ComboBox));
            combo.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
            combo.Setters.Add(new Setter(Control.BackgroundProperty, Brush(Color.FromRgb(226, 236, 244))));
            combo.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 6, 7, 6)));
            combo.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            Resources.Add(typeof(ComboBox), combo);
            Style comboItem = new Style(typeof(ComboBoxItem));
            comboItem.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
            comboItem.Setters.Add(new Setter(Control.BackgroundProperty, Brush(Color.FromRgb(235, 242, 248))));
            Resources.Add(typeof(ComboBoxItem), comboItem);

            Style tab = new Style(typeof(TabItem));
            tab.Setters.Add(new Setter(Control.ForegroundProperty, Brush(MutedColor)));
            tab.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(18, 11, 18, 11)));
            tab.Setters.Add(new Setter(Control.BackgroundProperty, Brush(BackColor)));
            tab.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(BorderColor)));
            ControlTemplate tabTemplate = new ControlTemplate(typeof(TabItem));
            FrameworkElementFactory tabBorder = new FrameworkElementFactory(typeof(Border)); tabBorder.Name = "tabBorder";
            tabBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(7, 7, 0, 0));
            tabBorder.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            tabBorder.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            FrameworkElementFactory tabContent = new FrameworkElementFactory(typeof(ContentPresenter));
            tabContent.SetValue(ContentPresenter.ContentSourceProperty, "Header"); tabBorder.AppendChild(tabContent); tabTemplate.VisualTree = tabBorder;
            Trigger selected = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.ForegroundProperty, Brush(AccentColor)));
            selected.Setters.Add(new Setter(Border.BackgroundProperty, Brush(PanelColor), "tabBorder")); tabTemplate.Triggers.Add(selected);
            tab.Setters.Add(new Setter(Control.TemplateProperty, tabTemplate)); Resources.Add(typeof(TabItem), tab);
        }

        private void RefreshSources()
        {
            int previousPid = processPicker.SelectedItem is ProcessChoice ? ((ProcessChoice)processPicker.SelectedItem).Id : -1;
            string previousAdapter = adapterPicker.SelectedItem is AdapterInfo ? ((AdapterInfo)adapterPicker.SelectedItem).Id : null;
            List<ProcessChoice> choices = new List<ProcessChoice>(); choices.Add(new ProcessChoice(0, "선택 안 함 · PC / 공유기 / 외부 ICMP"));
            List<ProcessChoice> processes = new List<ProcessChoice>();
            foreach (Process p in Process.GetProcesses())
            {
                try { if (p.Id > 0) processes.Add(new ProcessChoice(p.Id, p.ProcessName)); }
                catch (Exception) { }
                finally { p.Dispose(); }
            }
            choices.AddRange(processes.OrderByDescending(x => x.Name.Equals("TheDivision2", StringComparison.OrdinalIgnoreCase)).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id));
            processPicker.ItemsSource = choices;
            ProcessChoice selected = previousPid >= 0 ? choices.FirstOrDefault(x => x.Id == previousPid) : choices.FirstOrDefault(x => x.Name.Equals("TheDivision2", StringComparison.OrdinalIgnoreCase));
            processPicker.SelectedItem = selected ?? choices[0];
            try
            {
                List<AdapterInfo> adapters = AdapterCatalog.GetAdapters(); adapterPicker.ItemsSource = adapters;
                string preferred = previousAdapter ?? AdapterCatalog.GuessDefaultAdapterId();
                adapterPicker.SelectedItem = adapters.FirstOrDefault(x => x.Id == preferred) ?? adapters.FirstOrDefault(x => x.IsUp) ?? adapters.FirstOrDefault();
            }
            catch (Exception ex) { statusLine.Text = "어댑터 목록을 읽지 못했습니다: " + ex.Message; }
            UpdateSelectionHint();
        }

        private void UpdateSelectionHint()
        {
            if (running) return;
            ProcessChoice p = processPicker.SelectedItem as ProcessChoice;
            AdapterInfo connection = adapterPicker.SelectedItem as AdapterInfo;
            if (analysis != null && recordedSelectionLabel != null)
            {
                selectionSummary.Text = (running ? "기록 대상 · " : "마지막 기록 · ") + recordedSelectionLabel;
                return;
            }
            selectionSummary.Text = (p == null ? "게임 선택 대기" : p.Id == 0 ? "게임 선택 안 함" : p.Name) + " · " + (connection == null ? "PC 연결 선택 대기" : connection.Name);
            processPicker.ToolTip = p == null ? "프로세스 선택" : p.ToString();
            if (p != null && p.Id > 0) gameStatus.Text = p.Name + " · PID " + p.Id.ToString(CultureInfo.InvariantCulture) + " · 측정 대기";
            else gameStatus.Text = "게임 선택 안 함 · 시작 시 PC·공유기·외부 ICMP 측정";
        }

        private void RestoreSelection(StartupSelection selection)
        {
            if (selection == null) return;
            processPicker.SelectedItem = processPicker.Items.Cast<ProcessChoice>().FirstOrDefault(x => x.Id == selection.ProcessId) ?? processPicker.Items.Cast<ProcessChoice>().FirstOrDefault(x => x.Id == 0);
            adapterPicker.SelectedItem = adapterPicker.Items.Cast<AdapterInfo>().FirstOrDefault(x => x.Id == selection.AdapterId) ?? adapterPicker.SelectedItem;
            gatewayInput.Text = selection.Gateway;
            referenceInput.Text = String.IsNullOrWhiteSpace(selection.Reference) ? DefaultReferenceTarget : selection.Reference;
            // Legacy restore flags no longer disable the default collectors.
        }

        private void OpenAdministrator()
        {
            if (administrator) return;
            ProcessChoice p = processPicker.SelectedItem as ProcessChoice;
            AdapterInfo a = adapterPicker.SelectedItem as AdapterInfo;
            StartupSelection selection = new StartupSelection { ProcessId = p == null ? 0 : p.Id, AdapterId = a == null ? "" : a.Id,
                Gateway = gatewayInput.Text.Trim(), Reference = String.IsNullOrWhiteSpace(referenceInput.Text) ? DefaultReferenceTarget : referenceInput.Text.Trim(), UseReference = true, CollectGame = true, CollectProcesses = true };
            try
            {
                administratorButton.IsEnabled = false;
                using (Process child = ElevationSupport.LaunchAdministrator(selection))
                    if (child == null) throw new InvalidOperationException("관리자 창의 실행을 확인하지 못했습니다.");
                // Keep this window and its unsaved records available for export.
                StopSession("관리자 창으로 전환"); permissionBanner.Visibility = Visibility.Collapsed;
                statusLine.Text = "새 관리자 창을 열었습니다. 새 창에서 ‘기록 시작’을 누르세요. 이 창의 기존 기록은 CSV로 저장할 수 있습니다.";
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                statusLine.Text = error.NativeErrorCode == 1223 ? "관리자 실행을 취소했습니다. 현재 PC·공유기 측정은 계속됩니다." : "관리자 창을 열지 못했습니다: " + error.Message;
            }
            catch (Exception error) { statusLine.Text = "관리자 창을 열지 못했습니다: " + error.Message; }
            finally { administratorButton.IsEnabled = true; }
        }

        private void UpdateGameCollectionState(MonitorSnapshot s)
        {
            bool requested = s.GameCollectionRequested;
            bool processRequested = s.ProcessCollectionRequested;
            bool failed = (requested || processRequested) && !s.Trace.Active;
            collectionLimited = failed;
            gameStatus.Text = s.GameStatus + (failed && requested ? " · 게임 수집 실패" : requested && !s.GameRatesAvailable ? " · 선택한 프로세스 통신 대기" : "");
            permissionBanner.Visibility = (requested || processRequested) && s.Trace.PermissionDenied ? Visibility.Visible : Visibility.Collapsed;
            administratorButton.Visibility = administrator ? Visibility.Collapsed : Visibility.Visible;
            permissionMessage.Text = administrator ? "관리자 모드에서도 Windows가 게임 수집 접근을 거부했습니다. PC·공유기·외부 ICMP 측정은 진행 중입니다. 아래 수집 상태의 오류 코드로 추가 확인이 필요합니다." :
                "게임·프로그램별 통신 수집이 권한 부족으로 시작되지 않았습니다. PC·공유기·외부 ICMP 측정은 진행 중입니다. ‘관리자 모드 열기’를 누르고 UAC를 승인한 뒤 새 창에서 측정을 시작하세요.";
            RefreshRecordingIndicators();
        }

        private void StartSession(bool collectProcesses = true)
        {
            if (running || disposed) return;
            ProcessChoice p = processPicker.SelectedItem as ProcessChoice;
            AdapterInfo a = adapterPicker.SelectedItem as AdapterInfo;
            if (a == null) { MessageBox.Show(this, "측정할 PC 네트워크 어댑터를 선택하세요.", "어댑터 선택", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                recordingFault = false; recordingFaultMessage = ""; collectionLimited = false; lastRecordingError = "";
                System.Threading.Interlocked.Exchange(ref probeFailureQueued, 0);
                measurementBeganUtc = DateTime.UtcNow; lastSampleUtc = DateTime.MinValue;
                recordingSampleCount = 0;
                DiagnosticsLog.Write("measurement_start_requested", "pid=" + (p == null ? 0 : p.Id) + "; adapter=" + a.Id + "; collect_processes=" + collectProcesses);
                if (String.IsNullOrWhiteSpace(referenceInput.Text)) referenceInput.Text = DefaultReferenceTarget;
                BeginRecording(p, a, collectProcesses);
                controller.Start(p == null ? 0 : p.Id, a.Id, gatewayInput.Text.Trim(), referenceInput.Text.Trim(), true, collectProcesses);
                running = true; SetControls(true);
                DiagnosticsLog.Write("measurement_started", "session=" + lastLogDirectory);
                AddEvent("측정 시작 · " + (p == null ? "PC / 공유기" : p.ToString()) + " · " + a.Name);
                statusLine.Text = "게임·전체 통신 프로그램·공유기·외부 ICMP를 자동 저장·분석 중입니다.";
                timer.Start(); Poll();
            }
            catch (Exception ex)
            {
                StopSession("측정 시작 실패", ex); statusLine.Text = "시작하지 못했습니다: " + ex.Message;
                MessageBox.Show(this, ex.Message, "측정 시작 실패", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally { Mouse.OverrideCursor = null; }
        }

        private void StopSession(string reason, Exception error = null)
        {
            if (!running && automaticLog == null && error == null) return;
            string sessionDirectory;
            lock (recordingGate) sessionDirectory = automaticLog == null ? null : automaticLog.SessionDirectory;
            DiagnosticsLog.Write("measurement_stop_requested", "reason=" + reason + "; session=" + (sessionDirectory ?? ""));
            timer.Stop(); running = false;
            if (error != null) SetRecordingFailure(reason, error, sessionDirectory);
            permissionBanner.Visibility = Visibility.Collapsed;
            try { controller.Stop(); }
            catch (Exception stopError) { SetRecordingFailure("수집 종료 오류", stopError, sessionDirectory); reason += " · 수집 종료 오류"; }
            lastStopReason = reason;
            try { FinishRecording(reason); }
            catch (Exception finishError) { SetRecordingFailure("기록 종료 오류", finishError, sessionDirectory); }
            SetControls(false);
            AddEvent("측정 중지 · 표본 " + session.Count.ToString("N0") + "개");
            statusLine.Text = recordingFault ? "오류로 기록이 멈췄습니다. 상단의 기록 폴더와 진단 기록을 확인하세요. " + recordingFaultMessage : "기록을 중지했습니다. 새 데이터는 기록하지 않습니다. 중지 사유: " + reason;
            foreach (string key in new string[] { "gameRx", "gameTx", "gameEvents", "receiveIdle", "gatewayRtt", "gatewayMissing", "gatewayAverage", "gatewayVariation", "gatewayCount", "gatewayErrors", "pcRx", "pcTx", "pcPackets" }) values[key].Text = "—";
            gameStatus.Text = "측정 중지 · 접속지와 누적 기록 유지"; gatewayStatus.Text = "공유기 응답 검사 중지"; referenceStatus.Text = "외부 비교 검사 중지"; adapterStatus.Text = "이 PC의 데이터 사용량 측정 중지"; traceWarning.Text = "수집 중지 · 게임 ETW 세션을 종료했습니다.";
            processTrafficStatus.Text = "수집 중지 · 마지막 프로그램 표본 유지 · 전체 기록은 자동 저장 폴더에 있습니다."; values["deliveryLag"].Text = "—";
            programHeadline.Text = "기록 중지 · 이번 측정 누적 보내기";
            programDetail.Text = "중지 시점까지 관측한 누적량입니다. 새 측정을 시작하면 0부터 다시 집계합니다.";
            UpdateSessionText();
            RefreshAnalysisPresentation();
            UpdateSelectionHint();
            DiagnosticsLog.Write("measurement_stopped", "reason=" + reason + "; session=" + (sessionDirectory ?? "") + "; recording_fault=" + recordingFault + "; storage_error=" + lastRecordingError);
            RefreshRecordingIndicators();
        }

        private void Poll()
        {
            if (!running) return;
            try
            {
                if (nextPollErrorForTest != null) { Exception injected = nextPollErrorForTest; nextPollErrorForTest = null; throw injected; }
                MonitorSnapshot s = controller.Snapshot(); session.Add(s);
                bool refreshOverview = false;
                lock (recordingGate)
                {
                    if (automaticLog == null || !automaticLog.Append(s)) throw new IOException(automaticLog == null ? "자동 기록이 시작되지 않았습니다." : automaticLog.ErrorMessage);
                    lastSampleUtc = s.TimestampUtc;
                    recordingSampleCount = automaticLog.SampleCount;
                    analysis.Observe(s);
                    if ((s.TimestampUtc - summaryUpdatedUtc).TotalSeconds >= 10)
                    {
                        summaryUpdatedUtc = s.TimestampUtc;
                        refreshOverview = true;
                    }
                }
                if (refreshOverview) RefreshAnalysisPresentation();
                UpdateGameCollectionState(s);
                values["gameRx"].Text = s.GameRatesAvailable ? Rate(s.GameReceivedBytesPerSecond) : "—";
                values["gameTx"].Text = s.GameRatesAvailable ? Rate(s.GameSentBytesPerSecond) : "—";
                values["gameEvents"].Text = s.GameRatesAvailable ? s.GameReceivedEventsPerSecond.ToString("N1") + " / " + s.GameSentEventsPerSecond.ToString("N1") + " 회/s" : "—";
                values["retransmits"].Text = s.GameRatesAvailable ? s.RetransmitEvents.ToString("N0") + " 회" : "—";
                values["receiveIdle"].Text = s.GameReceiveDeliveryIdleSeconds.HasValue && s.GameRatesAvailable ? s.GameReceiveDeliveryIdleSeconds.Value.ToString("N1") + " s" : "—";
                values["deliveryLag"].Text = Ms(s.GameEventDeliveryLagMs) + " / " + Ms(s.GameMaxEventDeliveryLagMs);
                UpdateProcessTraffic(s.ProcessTraffic);
                values["gatewayRtt"].Text = s.Gateway.CurrentMs.HasValue && s.Gateway.CurrentMs.Value == 0 ? "1 ms 미만" : Ms(s.Gateway.CurrentMs);
                values["gatewayMissing"].Text = s.Gateway.MissingPercent.HasValue ? s.Gateway.MissingPercent.Value.ToString("N3") + " %" : "—";
                values["gatewayAverage"].Text = Ms(s.Gateway.AverageMs);
                values["gatewayVariation"].Text = Ms(s.Gateway.VariationMs);
                values["gatewayCount"].Text = s.Gateway.Missing + " / " + s.Gateway.Sent + " 회";
                values["gatewayErrors"].Text = s.Gateway.Errors + " 회";
                gatewayStatus.Text = String.IsNullOrEmpty(s.Gateway.Target) ? "공유기 응답 검사 꺼짐" : "검사 대상 " + s.Gateway.Target + " · " + (s.Gateway.CurrentMs.HasValue ? "응답 받음" : s.Gateway.Status);
                values["pcRx"].Text = s.AdapterRatesAvailable ? Rate(s.AdapterReceivedBytesPerSecond) : "—";
                values["pcTx"].Text = s.AdapterRatesAvailable ? Rate(s.AdapterSentBytesPerSecond) : "—";
                values["pcPackets"].Text = s.AdapterRatesAvailable ? s.AdapterReceivedPacketsPerSecond.ToString("N0") + " / " + s.AdapterSentPacketsPerSecond.ToString("N0") + " 개" : "—";
                values["pcErrors"].Text = s.AdapterCountersAvailable ? s.AdapterReceiveErrors.ToString("N0") + " / " + s.AdapterSendErrors.ToString("N0") + " 개" : "—";
                values["pcDiscards"].Text = s.AdapterCountersAvailable ? s.AdapterReceiveDiscards.ToString("N0") + " / " + s.AdapterSendDiscards.ToString("N0") + " 개" : "—";
                AdapterInfo measuredAdapter = adapterPicker.SelectedItem as AdapterInfo;
                adapterStatus.Text = s.AdapterRatesAvailable ? "측정 연결 · " + (measuredAdapter == null ? "선택한 연결" : measuredAdapter.Name) : s.AdapterStatus;
                adapterStatus.ToolTip = s.AdapterStatus;
                traceWarning.Text = "게임 수집: " + s.Trace.Status + "  |  수집 누락 이벤트 " + s.Trace.EventsLost.ToString("N0") + " · 버퍼 " + s.Trace.BuffersLost.ToString("N0") + " · 해석 제외 " + s.Trace.MalformedEvents.ToString("N0");
                if (s.Trace.Active) traceWarning.Text += "  |  전체 후보 " + s.Trace.CandidateEvents.ToString("N0") + " · 선택 PID " + s.Trace.SelectedPidEvents.ToString("N0") + " · 미지원 형식 " + s.Trace.UnsupportedEvents.ToString("N0");
                traceWarning.Foreground = Brush(s.Trace.EventsLost > 0 || s.Trace.BuffersLost > 0 || s.GameEndpointEvictions > 0 || ((s.GameCollectionRequested || s.ProcessCollectionRequested) && !s.Trace.Active) ? AmberColor : MutedColor);
                referenceStatus.Text = String.IsNullOrEmpty(s.Reference.Target) ? "외부 ICMP 비교 검사 대기" : "외부 " + s.Reference.Target + " · " + s.Reference.Status + " · 왕복 " + Ms(s.Reference.CurrentMs) + " · 무응답 " + (s.Reference.MissingPercent.HasValue ? s.Reference.MissingPercent.Value.ToString("N3") + " %" : "—") + " (" + s.Reference.Missing + "/" + s.Reference.Sent + ") · 오류 " + s.Reference.Errors;
                trafficChart.Add(s.TimestampUtc, s.GameRatesAvailable ? (double?)(s.GameReceivedBytesPerSecond / 1024) : null, s.GameRatesAvailable ? (double?)(s.GameSentBytesPerSecond / 1024) : null);
                gatewayChart.Add(s.TimestampUtc, s.Gateway.CurrentMs, null);
                endpointGrid.ItemsSource = s.Endpoints;
                UpdateConnectionPresentation(s);
                endpointStatus.Text = s.Endpoints.Count.ToString("N0") + "개 접속지 표시 · 자동 기록 " + s.EndpointObservations.Count.ToString("N0") + "개 대상 · 보관 한도 교체 " + s.GameEndpointEvictions.ToString("N0") + "회 · 화면 최근 100행";
                if (s.Trace.Status != previousTraceStatus) { AddEvent("게임 수집 · " + s.Trace.Status); previousTraceStatus = s.Trace.Status; }
                UpdateSessionText();
                // Detailed rows are already serialized to disk and copied into the UI.
                // Keep the two-hour in-memory chart history small during gameplay.
                s.EndpointObservations.Clear(); s.ProcessTraffic.Rows.Clear(); s.Endpoints = new List<EndpointRow>();
            }
            catch (Exception ex) { StopSession("수집 오류", ex); AddEvent("수집 오류로 자동 중지 · " + ex.Message); }
        }

        private void SetControls(bool active)
        {
            processPicker.IsEnabled = adapterPicker.IsEnabled = gatewayInput.IsEnabled = referenceInput.IsEnabled = refreshButton.IsEnabled = startButton.IsEnabled = !active;
            stopButton.IsEnabled = active; exportButton.IsEnabled = session.Count > 0;
            startButton.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
            stopButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (active) setupExpander.IsExpanded = false;
            setupExpander.Header = active ? "측정 대상 설정 · 기록 중에는 변경할 수 없습니다" : analysis == null ? "측정 대상 설정 · 게임 / PC 연결 / 검사 주소" : "다음 기록 대상 설정 · 현재 결과의 대상은 유지됩니다";
            RefreshRecordingIndicators();
        }

        private void RefreshRecordingIndicators()
        {
            Color color = recordingFault ? ErrorColor : running ? collectionLimited ? AmberColor : AccentColor : MutedColor;
            string label = recordingFault ? "오류로 기록 중지" : running ? collectionLimited ? "기록 중 · 일부 수집 안 됨" : "기록 중" : "기록 중지";
            string symbol = running && !recordingFault ? "●  " : "■  ";
            recordingStateTitle.Text = symbol + label; recordingStateTitle.Foreground = Brush(color);
            recordingBanner.BorderBrush = Brush(color);
            recordingBanner.Background = Brush(recordingFault ? Color.FromRgb(58, 29, 38) : running ? Color.FromRgb(20, 45, 47) : Color.FromRgb(28, 35, 46));
            string last = lastSampleUtc == DateTime.MinValue ? "아직 표본 없음" : "마지막 표본 " + lastSampleUtc.ToLocalTime().ToString("HH:mm:ss");
            if (running)
            {
                TimeSpan elapsed = DateTime.UtcNow - measurementBeganUtc;
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                recordingStateDetail.Text = "기록 시간 " + ((int)elapsed.TotalHours).ToString("00") + ":" + elapsed.Minutes.ToString("00") + ":" + elapsed.Seconds.ToString("00") +
                    " · 누적 표본 " + recordingSampleCount.ToString("N0") + "개 · " + last + (collectionLimited ? "\nPC·공유기·외부 응답은 기록 중입니다. 게임·프로그램 수집 상태를 확인하세요." : " · 자동 저장 중");
            }
            else recordingStateDetail.Text = "새 데이터는 기록하지 않습니다. " + (recordingFault ? recordingFaultMessage : lastStopReason) + " · " + last;
            if (!String.IsNullOrEmpty(DiagnosticsLog.ErrorMessage)) recordingStateDetail.Text += "\n진단 기록 저장 실패: " + DiagnosticsLog.ErrorMessage;
            stateText.Text = symbol + label; stateText.Foreground = Brush(color);
            Title = label + " | 게임 네트워크 모니터 " + AppVersion + " · " + (administrator ? "관리자 모드" : "일반 모드");
        }

        private void SetRecordingFailure(string stage, Exception error, string sessionDirectory)
        {
            recordingFault = true;
            string message = error.Message ?? error.GetType().Name;
            if (message.Length > 300) message = message.Substring(0, 300);
            recordingFaultMessage = stage + ": " + message;
            DiagnosticsLog.Write("measurement_error", "stage=" + stage + "; session=" + (sessionDirectory ?? ""), error);
            if (String.IsNullOrEmpty(sessionDirectory)) return;
            try
            {
                using (StreamWriter writer = new StreamWriter(new FileStream(Path.Combine(sessionDirectory, "errors.txt"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(true)))
                    writer.WriteLine(DateTime.UtcNow.ToString("o") + "\r\n단계: " + stage + "\r\n" + error.ToString() + "\r\n");
            }
            catch (Exception writeError) { DiagnosticsLog.Write("session_error_write_failed", "session=" + sessionDirectory, writeError); }
        }
        private void UpdateProcessTraffic(ProcessTrafficSnapshot traffic)
        {
            processTrafficStatus.Text = traffic.Status ?? "전체 프로그램별 통신 집계 대기";
            processTrafficStatus.Foreground = Brush(traffic.DroppedProcessEvents > 0 ? AmberColor : AccentColor);
            UpdateProgramPresentation(traffic);
        }
        private void Export()
        {
            if (session.Count == 0) return;
            SaveFileDialog dialog = new SaveFileDialog { Filter = "CSV 기록 (*.csv)|*.csv", DefaultExt = ".csv", FileName = "network-" + began.ToString("yyyyMMdd-HHmmss") + ".csv", AddExtension = true, OverwritePrompt = true };
            if (dialog.ShowDialog(this) != true) return;
            try { session.Export(dialog.FileName); AddEvent("CSV 저장 · " + Path.GetFileName(dialog.FileName)); statusLine.Text = "최근 CSV 표본을 저장했습니다."; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "저장 실패", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        private void UpdateSessionText()
        {
            sessionStatus.Text = session.Count == 0 ? "기록 없음 · 시작 시 새 폴더에 자동 저장" : "시작 " + began.ToString("HH:mm:ss") + " · 화면 표본 " + session.Count.ToString("N0") + "개";
            exportButton.IsEnabled = session.Count > 0;
            UpdateRecordingStatus();
            RefreshRecordingIndicators();
        }
        private void AddEvent(string text)
        {
            try
            {
                eventList.Items.Add(DateTime.Now.ToString("HH:mm:ss") + "   " + text); while (eventList.Items.Count > 250) eventList.Items.RemoveAt(0);
                if (eventList.Items.Count > 0) eventList.ScrollIntoView(eventList.Items[eventList.Items.Count - 1]);
            }
            catch (Exception error) { DiagnosticsLog.Write("event_display_failed", "could not update optional event list", error); }
        }
        private void DisposeMonitor()
        {
            if (disposed) return;
            timer.Stop();
            try { if (running || automaticLog != null) StopSession("창 닫기"); }
            finally
            {
                disposed = true; running = false;
                try { controller.Dispose(); }
                catch (Exception error) { DiagnosticsLog.Write("monitor_dispose_failed", "session=" + (lastLogDirectory ?? ""), error); }
            }
        }

        private string LogRoot { get { return logRoot; } }

        private void BeginRecording(ProcessChoice process, AdapterInfo adapter, bool collectProcesses)
        {
            AutomaticSessionLog next = new AutomaticSessionLog(LogRoot);
            try
            {
                string settings = "app_version=" + AppVersion + "\r\nprocess=" + (process == null ? "선택 안 함" : process.ToString()) +
                    "\r\nadapter=" + adapter.Name + "\r\nadapter_id=" + adapter.Id + "\r\ngateway=" + gatewayInput.Text.Trim() +
                    "\r\nreference_enabled=True\r\nreference=" + referenceInput.Text.Trim() +
                    "\r\ngame_collection_requested=True\r\nprocess_traffic_requested=" + collectProcesses + "\r\nprocess_traffic_scope=all_observed_processes\r\nprocess_tracking_limit=2048\r\ngame_endpoint_tracking_limit=512\r\nadministrator=" + administrator + "\r\ndiagnostics_file=" + DiagnosticsLog.FilePath + "\r\n";
                using (StreamWriter writer = new StreamWriter(new FileStream(Path.Combine(next.SessionDirectory, "settings.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(true))) writer.Write(settings);
            }
            catch (Exception error) { SetRecordingFailure("기록 설정 저장 실패", error, next.SessionDirectory); next.Complete("기록 설정 저장 실패"); next.Dispose(); throw; }
            lock (recordingGate)
            {
                automaticLog = next; analysis = new SessionAnalysis();
                lastLogDirectory = next.SessionDirectory; lastRecordingError = ""; summaryUpdatedUtc = DateTime.MinValue;
                recordedSelectionLabel = (process == null || process.Id == 0 ? "게임 선택 안 함" : process.Name) + " · " + adapter.Name;
                selectionSummary.Text = "기록 대상 · " + recordedSelectionLabel;
            }
            ResetMeasurementPresentation();
            RefreshAnalysisPresentation();
            DiagnosticsLog.Write("measurement_session_created", "session=" + next.SessionDirectory);
            UpdateRecordingStatus();
        }

        private void RecordProbe(string kind, ProbeSample sample)
        {
            AutomaticSessionLog observedLog = null;
            try
            {
                lock (recordingGate)
                {
                    observedLog = automaticLog;
                    if (observedLog == null) return;
                    if (!observedLog.RecordProbe(kind, sample)) throw new IOException(observedLog.ErrorMessage);
                    analysis.ObserveProbe(kind, sample);
                }
            }
            catch (Exception error)
            {
                DiagnosticsLog.Write("probe_callback_error", "kind=" + kind + "; session=" + (observedLog == null ? "" : observedLog.SessionDirectory), error);
                lock (recordingGate)
                {
                    if (!Object.ReferenceEquals(automaticLog, observedLog) || probeFailureQueued != 0) return;
                    probeFailureQueued = 1;
                }
                try { Dispatcher.BeginInvoke(new Action(delegate {
                    bool sameSession; lock (recordingGate) sameSession = Object.ReferenceEquals(automaticLog, observedLog);
                    if (sameSession && !disposed) StopSession("프로브 처리 오류", error);
                })); }
                catch (Exception dispatchError) { DiagnosticsLog.Write("probe_error_dispatch_failed", "could not request UI stop", dispatchError); }
            }
        }

        private void FinishRecording(string reason)
        {
            AutomaticSessionLog completed;
            SessionAnalysis completedAnalysis;
            lock (recordingGate)
            {
                completed = automaticLog; completedAnalysis = analysis; automaticLog = null;
            }
            if (completed == null) return;
            completed.Complete(reason); completed.Dispose();
            recordingSampleCount = completed.SampleCount;
            lastRecordingError = completed.ErrorMessage;
            if (lastRecordingError.Length > 0) SetRecordingFailure("파일 저장 오류", new IOException(lastRecordingError), completed.SessionDirectory);
            string summary = "종료 사유: " + reason + "\r\n저장 상태: " + (completed.IsHealthy ? "정상" : "오류 · " + lastRecordingError) +
                "\r\n기록 상태: " + (recordingFault ? "오류로 중지 · " + recordingFaultMessage : "기록 중지") + "\r\n위 종료 사유는 기록 세션의 중지 사유입니다. 앱 종료 여부는 diagnostics의 별도 기록을 확인하세요." +
                "\r\n전체 표본: " + completed.SampleCount.ToString("N0") + "개\r\n기록 폴더: " + completed.SessionDirectory + "\r\n\r\n" + completedAnalysis.SummaryText();
            try
            {
                using (StreamWriter writer = new StreamWriter(new FileStream(Path.Combine(completed.SessionDirectory, "summary.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(true))) writer.Write(summary);
            }
            catch (Exception error) { lastRecordingError = "분석 요약을 저장하지 못했습니다: " + error.Message + (lastRecordingError.Length == 0 ? "" : " · " + lastRecordingError); SetRecordingFailure("분석 요약 저장 오류", error, completed.SessionDirectory); }
            DiagnosticsLog.Write("measurement_files_completed", "session=" + completed.SessionDirectory + "; reason=" + reason + "; samples=" + completed.SampleCount + "; storage_healthy=" + completed.IsHealthy + "; recording_fault=" + recordingFault);
            UpdateRecordingStatus();
        }

        private void UpdateRecordingStatus()
        {
            lock (recordingGate)
            {
                if (automaticLog != null)
                {
                    recordingStatus.Text = (automaticLog.IsHealthy ? "자동 저장 중 · 전체 표본 " + automaticLog.SampleCount.ToString("N0") + "개" : "자동 저장 오류 · " + automaticLog.ErrorMessage) + "\n" + automaticLog.SessionDirectory;
                    recordingStatus.Foreground = Brush(automaticLog.IsHealthy ? AccentColor : AmberColor);
                }
                else if (!String.IsNullOrEmpty(lastLogDirectory))
                {
                    recordingStatus.Text = (recordingFault ? "오류로 기록 중지 · " + recordingFaultMessage : "기록 중지 · summary.txt에서 전체 분석 확인") + "\n" + lastLogDirectory;
                    recordingStatus.Foreground = Brush(recordingFault ? ErrorColor : MutedColor);
                }
            }
            logFolderButton.IsEnabled = !String.IsNullOrEmpty(lastLogDirectory) || Directory.Exists(LogRoot);
        }

        private void OpenLogFolder()
        {
            string path = String.IsNullOrEmpty(lastLogDirectory) ? LogRoot : lastLogDirectory;
            try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
            catch (Exception error) { statusLine.Text = "기록 폴더를 열지 못했습니다: " + error.Message; }
        }

        public void SmokeTest(string screenshotPath)
        {
            if (running) throw new InvalidOperationException("Smoke test must not start monitoring.");
            if (timer.IsEnabled || !referenceInput.IsEnabled || referenceInput.Text != DefaultReferenceTarget || tabs.Items.Count != 2 || processTrafficGrid.Columns.Count != 2 || values.Count < 12 || !recordingStateTitle.Text.Contains("기록 중지") || overviewTitle.Text.Contains("이상"))
                throw new InvalidOperationException("UI initial monitoring/navigation state is invalid.");
            foreach (TabItem item in tabs.Items) { tabs.SelectedItem = item; UpdateLayout(); }
            tabs.SelectedIndex = 0; Width = 1240; Height = 920;
            RenderTestSurface(screenshotPath);
            SummaryPresentationTest(Path.GetDirectoryName(Path.GetFullPath(screenshotPath)));
            DateTime exampleAt = DateTime.UtcNow.AddMinutes(-1);
            ProcessTrafficSnapshot example = new ProcessTrafficSnapshot { TimestampUtc = exampleAt.AddSeconds(2), IntervalStartUtc = exampleAt, IntervalEndUtc = exampleAt.AddSeconds(2), IntervalSeconds = 2, Available = true, Status = "UI 검사 예시 · 실제 측정 없음 · 모든 어댑터 기준" };
            example.Rows.Add(new ProcessTrafficRow { ProcessId = 12345, ProcessName = "UI 검사 예시 · 실제 측정 없음", SentBytesPerSecond = 2048, SentBytesTotal = 99999999, IdentityAvailable = true });
            example.Rows.Add(new ProcessTrafficRow { ProcessId = 12346, ProcessName = "UI 검사 예시 · 실제 측정 없음", SentBytesPerSecond = 1024, IdentityAvailable = true });
            UpdateProcessTraffic(example); tabs.SelectedIndex = 1;
            UpdateProcessTraffic(example);
            if (processTrafficGrid.Items.Count != 1 || ((ProgramUsageView)processTrafficGrid.Items[0]).SentBytes != 6144 || !((ProgramUsageView)processTrafficGrid.Items[0]).SentDisplay.Contains("6.0")) throw new InvalidOperationException("Cumulative program traffic UI grouping or duplicate protection failed.");
            RenderTestSurface(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(screenshotPath)), "process-traffic.png"));
            values["pcErrors"].Text = "99 / 99"; values["retransmits"].Text = "99";
            ResetMeasurementPresentation();
            if (processTrafficGrid.Items.Count != 0 || values["pcErrors"].Text != "—" || values["retransmits"].Text != "—")
                throw new InvalidOperationException("New measurement retained a previous result before collector startup.");
            tabs.SelectedIndex = 0;
            // Exercise the permission UI without starting ETW, sending probes or invoking UAC.
            MonitorSnapshot denied = new MonitorSnapshot { ProcessId = 12345, GameCollectionRequested = true, ProcessCollectionRequested = true, GameStatus = "권한 상태 UI 검사 · 실제 측정 없음",
                Trace = new TraceHealth { PermissionDenied = true, NativeErrorCode = 5, FailureStage = "StartTrace", Status = "UI 검사: StartTrace · 오류 5" } };
            running = true; measurementBeganUtc = DateTime.UtcNow;
            SetControls(true); RefreshAnalysisPresentation();
            UpdateGameCollectionState(denied);
            if (permissionBanner.Visibility != Visibility.Visible || !stateText.Text.Contains("기록 중 · 일부 수집 안 됨") ||
                administratorButton.Visibility != (administrator ? Visibility.Collapsed : Visibility.Visible) || values["gameRx"].Text != "—")
                throw new InvalidOperationException("Permission-denied UI did not expose unavailable game collection.");
            RenderTestSurface(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(screenshotPath)), "permission-needed.png"));
            setupExpander.IsExpanded = true;
            permissionMessage.Text += "\n작은 창의 긴 안내문 배치 검사입니다. 위쪽 안내가 길어져도 바깥 스크롤로 측정 화면을 확인할 수 있어야 합니다.";
            RenderTestSurface(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(screenshotPath)), "small-window.png"), 1000, 700);
            if (tabs.ActualHeight < 200 || ((ScrollViewer)Content).ScrollableHeight <= 0)
                throw new InvalidOperationException("Small-window notices hid the monitoring tabs or lost outer scrolling.");
            setupExpander.IsExpanded = false;
            denied.Trace.Active = true; denied.Trace.PermissionDenied = false;
            UpdateGameCollectionState(denied);
            if (permissionBanner.Visibility != Visibility.Collapsed || !gameStatus.Text.Contains("통신 대기"))
                throw new InvalidOperationException("Active collection waiting state is invalid.");
            running = false; collectionLimited = false; SetControls(false);
            AdapterInfo a = adapterPicker.SelectedItem as AdapterInfo;
            StartupSelection restored = new StartupSelection { ProcessId = Process.GetCurrentProcess().Id, AdapterId = a == null ? "" : a.Id,
                Gateway = "127.0.0.1", Reference = "::1", UseReference = true, CollectGame = false };
            RestoreSelection(restored);
            if (((ProcessChoice)processPicker.SelectedItem).Id != restored.ProcessId || gatewayInput.Text != restored.Gateway ||
                referenceInput.Text != restored.Reference ||
                running || timer.IsEnabled || controller.Snapshot().Running)
                throw new InvalidOperationException("Restoring selections started collection or lost the settings.");
            DisposeMonitor();
        }

        public void RecordingTest(string reportPath)
        {
            if (running || timer.IsEnabled) throw new InvalidOperationException("Recording test must start idle.");
            AdapterInfo adapter = adapterPicker.SelectedItem as AdapterInfo;
            if (adapter == null) throw new InvalidOperationException("No adapter available for local recording test.");
            // No selected process and no process collection: this fixture never starts ETW.
            RestoreSelection(new StartupSelection { ProcessId = 0, AdapterId = adapter.Id,
                Gateway = "127.0.0.1", Reference = "127.0.0.1", UseReference = true, CollectGame = true, CollectProcesses = false });
            if (((ProcessChoice)processPicker.SelectedItem).Id != 0)
                throw new InvalidOperationException("Local recording fixture must select no game.");
            StartSession(false);
            if (!running || !recordingStateTitle.Text.Contains("기록 중") || !Title.StartsWith("기록 중")) throw new InvalidOperationException("Local recording state failed to become active.");
            System.Threading.Thread.Sleep(1300); Poll();
            if (!recordingStateDetail.Text.Contains("마지막 표본") || lastSampleUtc == DateTime.MinValue) throw new InvalidOperationException("Active recording heartbeat is missing.");
            string renderDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
            RenderTestSurface(Path.Combine(renderDirectory, "recording-active.png"));
            string firstDirectory = lastLogDirectory;
            stopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (lastRecordingError.Length != 0) throw new IOException(lastRecordingError);
            if (running || timer.IsEnabled || recordingFault || !recordingStateTitle.Text.Contains("기록 중지")) throw new InvalidOperationException("User stop state is invalid.");
            RenderTestSurface(Path.Combine(renderDirectory, "recording-stopped.png"));
            string firstSamples = File.ReadAllText(Path.Combine(firstDirectory, "samples.csv"));
            if (File.ReadAllLines(Path.Combine(firstDirectory, "samples.csv")).Length < 3 ||
                File.ReadAllLines(Path.Combine(firstDirectory, "probes.csv")).Length < 3 ||
                !File.ReadAllText(Path.Combine(firstDirectory, "summary.txt")).Contains("전체 구간을 자동으로 분석합니다"))
                throw new InvalidOperationException("Automatic samples, raw probes, or summary were not persisted.");
            if (!File.ReadAllText(Path.Combine(firstDirectory, "session.txt")).Contains("reason=사용자 중지")) throw new InvalidOperationException("User stop reason was not persisted.");
            StartSession(false);
            if (!running || firstDirectory == lastLogDirectory) throw new InvalidOperationException("New session reused the previous log folder.");
            System.Threading.Thread.Sleep(250); Poll();
            string secondDirectory = lastLogDirectory;
            nextPollErrorForTest = new InvalidOperationException("로컬 오류 주입 검사"); Poll();
            if (running || timer.IsEnabled || !recordingFault || !recordingStateTitle.Text.Contains("오류로 기록 중지") || ((SolidColorBrush)recordingBanner.BorderBrush).Color != ErrorColor)
                throw new InvalidOperationException("Injected error did not stop recording and expose the red error state.");
            string errorText = File.ReadAllText(Path.Combine(secondDirectory, "errors.txt"));
            if (!errorText.Contains("로컬 오류 주입 검사") || !errorText.Contains("MainWindow.Poll") || !File.ReadAllText(Path.Combine(secondDirectory, "session.txt")).Contains("reason=수집 오류"))
                throw new InvalidOperationException("Automatic stop lost its error stack or concrete reason.");
            RenderTestSurface(Path.Combine(renderDirectory, "recording-error.png"));
            StartSession(false);
            if (!running || recordingFault || !recordingStateTitle.Text.Contains("기록 중")) throw new InvalidOperationException("Restart did not clear the error state.");
            System.Threading.Thread.Sleep(250); Poll();
            string thirdDirectory = lastLogDirectory;
            DisposeMonitor();
            if (!File.ReadAllText(Path.Combine(thirdDirectory, "summary.txt")).Contains("종료 사유: 창 닫기") ||
                File.ReadAllText(Path.Combine(firstDirectory, "samples.csv")) != firstSamples || File.ReadAllText(Path.Combine(secondDirectory, "errors.txt")) != errorText || lastRecordingError.Length != 0)
                throw new InvalidOperationException("Window close did not preserve and finalize all sessions.");
            File.WriteAllText(reportPath, "PASS: Actual WPF active/stopped/error states, last-sample heartbeat, user stop, injected failure with persistent stack and reason, recovery, restart and close. Only loopback probes; no game, ETW, UAC, manual marks or exports.\r\nFirst fixture=" + firstDirectory + "\r\nError fixture=" + secondDirectory + "\r\nClose fixture=" + thirdDirectory + "\r\n", new UTF8Encoding(true));
        }

        private void RenderTestSurface(string screenshotPath, int width = 1240, int height = 920)
        {
            FrameworkElement surface = (FrameworkElement)Content;
            surface.Measure(new Size(width, height)); surface.Arrange(new Rect(0, 0, width, height)); surface.UpdateLayout();
            RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
            PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string directory = Path.GetDirectoryName(Path.GetFullPath(screenshotPath)); Directory.CreateDirectory(directory);
            using (FileStream stream = File.Create(screenshotPath)) encoder.Save(stream);
        }

        private static SolidColorBrush Brush(Color color) { SolidColorBrush b = new SolidColorBrush(color); b.Freeze(); return b; }
        private static TextBlock Text(string text, double size, Color color, bool bold) { return new TextBlock { Text = text, FontSize = size, Foreground = Brush(color), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal }; }
        private void AddField(Grid grid, string label, Control control, int row, int column)
        {
            StackPanel field = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; TextBlock caption = Text(label, 11, MutedColor, false); caption.Margin = new Thickness(0, 0, 0, 6); field.Children.Add(caption); control.Height = 35; field.Children.Add(control); Grid.SetRow(field, row); Grid.SetColumn(field, column); grid.Children.Add(field);
        }
        private StackPanel Card(string title, string subtitle, Grid grid, int column)
        {
            Border shell = new Border { Background = Brush(PanelColor), BorderBrush = Brush(BorderColor), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(15), Margin = new Thickness(column == 0 ? 0 : 7, 0, column == 2 ? 0 : 7, 0) };
            StackPanel body = new StackPanel(); TextBlock heading = Text(title, 15, TextColor, true); heading.TextWrapping = TextWrapping.Wrap; body.Children.Add(heading); TextBlock sub = Text(subtitle, 11, MutedColor, false); sub.TextWrapping = TextWrapping.Wrap; sub.Margin = new Thickness(0, 3, 0, 14); body.Children.Add(sub); shell.Child = body; Grid.SetColumn(shell, column); grid.Children.Add(shell); return body;
        }
        private UIElement TwoMetrics(string firstLabel, string firstKey, string secondLabel, string secondKey)
        {
            Grid pair = new Grid(); pair.ColumnDefinitions.Add(new ColumnDefinition()); pair.ColumnDefinitions.Add(new ColumnDefinition());
            pair.Children.Add(BigMetric(firstLabel, firstKey, AccentColor)); UIElement second = BigMetric(secondLabel, secondKey, TextColor); Grid.SetColumn(second, 1); pair.Children.Add(second); return pair;
        }
        private UIElement BigMetric(string label, string key, Color color)
        {
            StackPanel stack = new StackPanel(); stack.Children.Add(Text(label, 11, MutedColor, false)); TextBlock value = Text("—", 24, color, true); value.Margin = new Thickness(0, 3, 0, 0); values.Add(key, value); stack.Children.Add(value); return stack;
        }
        private UIElement SmallRow(string label, string key)
        {
            Grid row = new Grid { Margin = new Thickness(0, 0, 0, 7) }; row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock caption = Text(label, 11, MutedColor, false); caption.TextWrapping = TextWrapping.Wrap; row.Children.Add(caption); TextBlock value = Text("—", 12, TextColor, false); values.Add(key, value); value.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(value, 1); row.Children.Add(value); return row;
        }
        private void AddChart(Grid parent, string title, string subtitle, SparkChart chart, int column)
        {
            Border shell = new Border { Background = Brush(PanelColor), BorderBrush = Brush(BorderColor), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Margin = new Thickness(column == 0 ? 0 : 7, 0, column == 1 ? 0 : 7, 0) };
            StackPanel body = new StackPanel(); body.Children.Add(Text(title, 14, TextColor, true)); TextBlock sub = Text(subtitle, 11, MutedColor, false); sub.TextWrapping = TextWrapping.Wrap; sub.Margin = new Thickness(0, 4, 0, 6); body.Children.Add(sub); chart.Height = 155; body.Children.Add(chart); shell.Child = body; Grid.SetColumn(shell, column); parent.Children.Add(shell);
        }
        private void AddColumn(string title, string property, double width, string format)
        {
            Binding binding = new Binding(property); if (format != null) binding.StringFormat = "{0:" + format + "}";
            endpointGrid.Columns.Add(new DataGridTextColumn { Header = title, Binding = binding, Width = new DataGridLength(width) });
        }
        private static void AddHelp(StackPanel stack, string title, string copy)
        {
            TextBlock text = Text(title + "  ·  " + copy, 12, MutedColor, false); text.TextWrapping = TextWrapping.Wrap; text.Margin = new Thickness(0, 8, 0, 0); stack.Children.Add(text);
        }
        private static string Ms(double? value) { return value.HasValue ? value.Value.ToString("N1") + " ms" : "—"; }
        private static string Rate(double value) { return value >= 1048576 ? (value / 1048576).ToString("N2") + " MiB/s" : (value / 1024).ToString("N1") + " KiB/s"; }
        private sealed class ProcessChoice
        {
            public readonly int Id; public readonly string Name;
            public ProcessChoice(int id, string name) { Id = id; Name = name; }
            public override string ToString() { return Id == 0 ? Name : Name + "  (" + Id.ToString(CultureInfo.InvariantCulture) + ")"; }
        }
    }

    internal sealed class SparkChart : FrameworkElement
    {
        private sealed class PointSample { public DateTime At; public double? First, Second; }
        private readonly List<PointSample> samples = new List<PointSample>();
        private readonly List<DateTime> marks = new List<DateTime>();
        private readonly bool latency;
        public SparkChart(bool isLatency) { latency = isLatency; SnapsToDevicePixels = true; }
        public void Add(DateTime at, double? first, double? second)
        {
            samples.Add(new PointSample { At = at, First = first, Second = second }); while (samples.Count > 120) samples.RemoveAt(0);
            while (marks.Count > 0 && (at - marks[0]).TotalSeconds > 120) marks.RemoveAt(0); InvalidateVisual();
        }
        public void Mark(DateTime at) { marks.Add(at); InvalidateVisual(); }
        public void Clear() { samples.Clear(); marks.Clear(); InvalidateVisual(); }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc); double width = ActualWidth, height = ActualHeight;
            if (width < 80 || height < 60) return;
            Rect plot = new Rect(42, 8, Math.Max(1, width - 54), Math.Max(1, height - 34));
            double max = 0;
            foreach (PointSample sample in samples) { if (sample.First.HasValue) max = Math.Max(max, sample.First.Value); if (sample.Second.HasValue) max = Math.Max(max, sample.Second.Value); }
            max = Math.Max(latency ? 5 : 1, max * 1.15);
            Pen grid = new Pen(new SolidColorBrush(Color.FromRgb(45, 59, 76)), 1);
            for (int i = 0; i < 4; i++)
            {
                double y = plot.Top + i * plot.Height / 3; dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
                DrawLabel(dc, (max * (3 - i) / 3).ToString(max >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture), new Point(0, y - 7), Color.FromRgb(139, 160, 181));
            }
            DrawLabel(dc, "−120s", new Point(plot.Left, plot.Bottom + 5), Color.FromRgb(139, 160, 181)); DrawLabel(dc, "현재", new Point(plot.Right - 24, plot.Bottom + 5), Color.FromRgb(139, 160, 181));
            bool available = samples.Any(x => x.First.HasValue || x.Second.HasValue);
            if (samples.Count == 0 || !available)
            {
                DrawLabel(dc, samples.Count == 0 ? "측정 시작 후 실제 표본을 표시합니다" : "표시할 수 있는 측정값이 없습니다", new Point(plot.Left + 12, plot.Top + plot.Height / 2 - 8), Color.FromRgb(158, 177, 197)); return;
            }
            DateTime now = samples[samples.Count - 1].At;
            Pen markPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 195, 108)), 1); markPen.DashStyle = DashStyles.Dash;
            foreach (DateTime mark in marks) { double x = plot.Right - (now - mark).TotalSeconds / 120 * plot.Width; if (x >= plot.Left && x <= plot.Right) dc.DrawLine(markPen, new Point(x, plot.Top), new Point(x, plot.Bottom)); }
            DrawSeries(dc, plot, max, now, false, Color.FromRgb(72, 218, 220));
            if (!latency) DrawSeries(dc, plot, max, now, true, Color.FromRgb(177, 151, 255));
        }
        private void DrawSeries(DrawingContext dc, Rect plot, double max, DateTime now, bool second, Color color)
        {
            Pen pen = new Pen(new SolidColorBrush(color), 2); Point? previous = null; DateTime previousTime = DateTime.MinValue;
            foreach (PointSample sample in samples)
            {
                double? value = second ? sample.Second : sample.First;
                if (!value.HasValue) { previous = null; continue; }
                Point current = new Point(plot.Right - (now - sample.At).TotalSeconds / 120 * plot.Width, plot.Bottom - Math.Min(max, Math.Max(0, value.Value)) / max * plot.Height);
                if (current.X < plot.Left) { previous = null; continue; }
                if (previous.HasValue && (sample.At - previousTime).TotalSeconds <= 3) dc.DrawLine(pen, previous.Value, current);
                else dc.DrawEllipse(pen.Brush, null, current, 1.5, 1.5);
                previous = current; previousTime = sample.At;
            }
        }
        private static void DrawLabel(DrawingContext dc, string text, Point point, Color color)
        {
            FormattedText formatted = new FormattedText(text, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), 10, new SolidColorBrush(color), 1.0); dc.DrawText(formatted, point);
        }
    }
}
