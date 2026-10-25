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
    public sealed class MainWindow : Window
    {
        private const string DefaultReferenceTarget = "1.1.1.1";
        private const string AppVersion = "0.1.5";
        private static readonly Color BackColor = Color.FromRgb(16, 21, 29);
        private static readonly Color PanelColor = Color.FromRgb(25, 33, 44);
        private static readonly Color BorderColor = Color.FromRgb(48, 62, 79);
        private static readonly Color TextColor = Color.FromRgb(233, 241, 249);
        private static readonly Color MutedColor = Color.FromRgb(158, 177, 197);
        private static readonly Color AccentColor = Color.FromRgb(72, 218, 220);
        private static readonly Color AmberColor = Color.FromRgb(255, 195, 108);
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
        private readonly TextBlock statusLine = new TextBlock();
        private readonly TextBlock gameStatus = new TextBlock();
        private readonly TextBlock traceWarning = new TextBlock();
        private readonly TextBlock gatewayStatus = new TextBlock();
        private readonly TextBlock referenceStatus = new TextBlock();
        private readonly TextBlock adapterStatus = new TextBlock();
        private readonly TextBlock sessionStatus = new TextBlock();
        private readonly TextBlock endpointStatus = new TextBlock();
        private readonly TextBlock recordingStatus = new TextBlock();
        private readonly TextBox analysisSummary = new TextBox();
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
            };
            refreshButton.Click += delegate { RefreshSources(); };
            startButton.Click += delegate { StartSession(); };
            stopButton.Click += delegate { StopSession(); };
            exportButton.Click += delegate { Export(); };
            administratorButton.Click += delegate { OpenAdministrator(); };
            logFolderButton.Click += delegate { OpenLogFolder(); };
            controller.ProbeSampled += RecordProbe;
            Closing += delegate { DisposeMonitor(); };
            RefreshSources();
            RestoreSelection(selection);
            SetControls(false);
            statusLine.Text = "측정 시작 후 게임·전체 통신 프로그램·공유기·외부 ICMP를 자동 기록합니다.";
            gameStatus.Text = "선택한 게임의 송수신 이벤트를 기다립니다.";
            gatewayStatus.Text = "공유기에 확인 요청을 보낼 준비가 되었습니다.";
            referenceStatus.Text = "외부 ICMP 비교 검사 대기";
            adapterStatus.Text = "선택한 연결의 현재 사용량 · 측정 대기";
            traceWarning.Text = "게임 ETW 수집 권한이 없으면 안내를 표시하고 PC·공유기·외부 ICMP 측정을 계속합니다.";
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

        private UIElement BuildWindow()
        {
            Grid main = new Grid { Margin = new Thickness(24, 20, 24, 14), Background = Brush(BackColor) };
            main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid header = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel heading = new StackPanel();
            heading.Children.Add(Text("NETWORK / LOCAL DIAGNOSTICS", 11, AccentColor, true));
            TextBlock title = Text("게임 네트워크 모니터", 27, TextColor, true); title.Margin = new Thickness(0, 3, 0, 3); heading.Children.Add(title);
            heading.Children.Add(Text("선택한 게임 · 이 PC의 어댑터 · PC와 공유기 사이 연결", 13, MutedColor, false)); header.Children.Add(heading);
            Border state = new Border { Background = Brush(PanelColor), BorderBrush = Brush(BorderColor), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(20), Padding = new Thickness(15, 8, 15, 8), VerticalAlignment = VerticalAlignment.Center };
            stateText.Text = "●  대기"; stateText.Foreground = Brush(MutedColor); stateText.FontWeight = FontWeights.Bold; state.Child = stateText;
            Grid.SetColumn(state, 1); header.Children.Add(state); main.Children.Add(header);

            Border setup = new Border { Background = Brush(PanelColor), BorderBrush = Brush(BorderColor), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 16) };
            Grid form = new Grid();
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(.75, GridUnitType.Star) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(.75, GridUnitType.Star) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            AddField(form, "게임 / 프로세스", processPicker, 0, 0);
            AddField(form, "측정할 PC 연결 · 랜선 / Wi-Fi", adapterPicker, 0, 1);
            AddField(form, "공유기 주소 · 비우면 검사 끔", gatewayInput, 0, 2);
            AddField(form, "외부 ICMP 비교 대상", referenceInput, 0, 3);
            referenceInput.Text = DefaultReferenceTarget;
            refreshButton.Content = "목록 갱신"; refreshButton.Margin = new Thickness(6, 22, 0, 0); Grid.SetColumn(refreshButton, 4); form.Children.Add(refreshButton);
            Grid action = new Grid { Margin = new Thickness(0, 14, 0, 0) }; action.ColumnDefinitions.Add(new ColumnDefinition()); action.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusLine.TextWrapping = TextWrapping.Wrap; statusLine.Foreground = Brush(MutedColor); statusLine.VerticalAlignment = VerticalAlignment.Center; statusLine.Margin = new Thickness(0, 0, 15, 0); action.Children.Add(statusLine);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal };
            startButton.Content = "측정 시작"; startButton.Background = Brush(AccentColor); startButton.Foreground = Brush(BackColor); startButton.FontWeight = FontWeights.Bold;
            stopButton.Content = "중지"; stopButton.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(startButton); buttons.Children.Add(stopButton); Grid.SetColumn(buttons, 1); action.Children.Add(buttons);
            Grid.SetRow(action, 1); Grid.SetColumnSpan(action, 5); form.Children.Add(action); setup.Child = form; Grid.SetRow(setup, 1); main.Children.Add(setup);

            permissionBanner.Background = Brush(Color.FromRgb(52, 38, 25)); permissionBanner.BorderBrush = Brush(AmberColor);
            permissionBanner.BorderThickness = new Thickness(1); permissionBanner.CornerRadius = new CornerRadius(8);
            permissionBanner.Padding = new Thickness(14); permissionBanner.Margin = new Thickness(0, 0, 0, 14); permissionBanner.Visibility = Visibility.Collapsed;
            Grid permissionLayout = new Grid(); permissionLayout.ColumnDefinitions.Add(new ColumnDefinition()); permissionLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            permissionMessage.TextWrapping = TextWrapping.Wrap; permissionMessage.Foreground = Brush(AmberColor); permissionMessage.VerticalAlignment = VerticalAlignment.Center; permissionMessage.Margin = new Thickness(0, 0, 16, 0);
            permissionLayout.Children.Add(permissionMessage);
            administratorButton.Content = "관리자 모드 열기"; administratorButton.Foreground = Brush(AmberColor);
            administratorButton.ToolTip = "선택 값을 유지한 새 창을 엽니다. Windows UAC 승인이 필요하며 측정은 자동으로 시작하지 않습니다.";
            Grid.SetColumn(administratorButton, 1); permissionLayout.Children.Add(administratorButton); permissionBanner.Child = permissionLayout;
            Grid.SetRow(permissionBanner, 2); main.Children.Add(permissionBanner);

            tabs.Background = Brush(PanelColor); tabs.BorderThickness = new Thickness(0); tabs.Padding = new Thickness(0); tabs.Foreground = Brush(TextColor);
            tabs.Items.Add(new TabItem { Header = "대시보드", Content = BuildDashboard() });
            tabs.Items.Add(new TabItem { Header = "접속지", Content = BuildEndpoints() });
            tabs.Items.Add(new TabItem { Header = "통신 프로그램", Content = BuildProcessTraffic() });
            tabs.Items.Add(new TabItem { Header = "기록 · 분석", Content = BuildLog() });
            Grid.SetRow(tabs, 3); main.Children.Add(tabs);
            TextBlock footer = Text("LOCAL ONLY  ·  패킷 본문 수집 없음  ·  공유기 전체 트래픽 / 실제 게임 패킷 손실률은 포함하지 않음", 11, MutedColor, false);
            footer.Margin = new Thickness(0, 12, 0, 0); footer.TextWrapping = TextWrapping.Wrap; Grid.SetRow(footer, 4); main.Children.Add(footer);
            return main;
        }

        private UIElement BuildDashboard()
        {
            ScrollViewer scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            StackPanel body = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            Grid cards = new Grid(); cards.ColumnDefinitions.Add(new ColumnDefinition()); cards.ColumnDefinitions.Add(new ColumnDefinition()); cards.ColumnDefinitions.Add(new ColumnDefinition());
            StackPanel game = Card("선택한 게임", "최근 약 2초 · ETW 도착 기준 송수신", cards, 0);
            game.Children.Add(TwoMetrics("게임 수신", "gameRx", "게임 송신", "gameTx"));
            gameStatus.TextWrapping = TextWrapping.Wrap; gameStatus.Foreground = Brush(MutedColor); gameStatus.Margin = new Thickness(0, 9, 0, 8); game.Children.Add(gameStatus);
            game.Children.Add(SmallRow("수신 / 송신 이벤트", "gameEvents"));
            game.Children.Add(SmallRow("TCP 재전송 이벤트 · 누적", "retransmits"));
            game.Children.Add(SmallRow("마지막 수신 관측 이후", "receiveIdle"));
            game.Children.Add(SmallRow("기록 전달 지연 평균 / 최대", "deliveryLag"));
            values["deliveryLag"].ToolTip = "ETW 이벤트가 발생한 뒤 이 앱에 도착하기까지의 기록 지연입니다. 게임 서버 핑이 아닙니다.";

            StackPanel gateway = Card("공유기까지의 연결 상태", "내 PC → 공유기 → 내 PC · 최근 60초", cards, 1);
            gateway.Children.Add(TwoMetrics("공유기 응답 시간", "gatewayRtt", "답이 없던 비율", "gatewayMissing"));
            values["gatewayRtt"].ToolTip = "내 PC가 공유기에 작은 확인 요청을 보내고 답을 받기까지 걸린 시간입니다. 낮을수록 응답이 빠릅니다. Windows가 0ms로 보고한 값은 '1 ms 미만'으로 표시하며, 지연이 전혀 없다는 뜻은 아닙니다.";
            values["gatewayMissing"].ToolTip = "최근 60초 동안 답이 제한 시간 안에 오지 않은 검사 비율입니다. 요청은 약 1초마다 보냅니다. 게임 패킷 손실률은 별도이며, 주소 조회나 검사 오류는 이 비율에서 제외합니다.";
            gatewayStatus.Foreground = Brush(MutedColor); gatewayStatus.TextWrapping = TextWrapping.Wrap; gatewayStatus.Margin = new Thickness(0, 9, 0, 8); gateway.Children.Add(gatewayStatus);
            gateway.Children.Add(SmallRow("평균 응답 시간", "gatewayAverage"));
            gateway.Children.Add(SmallRow("응답 시간 흔들림", "gatewayVariation"));
            values["gatewayAverage"].ToolTip = "최근 60초 동안 답을 받은 검사의 평균 응답 시간입니다.";
            values["gatewayVariation"].ToolTip = "연속해서 답을 받은 두 검사의 응답 시간 차이의 크기를 평균한 값입니다. 작을수록 응답 시간이 일정합니다.";
            gateway.Children.Add(SmallRow("답 없음 / 확인 횟수", "gatewayCount"));
            gateway.Children.Add(SmallRow("검사 자체의 오류", "gatewayErrors"));
            values["gatewayCount"].ToolTip = "최근 60초의 시간 초과 횟수 / 정상 응답 또는 시간 초과로 결과가 나온 검사 수입니다. 검사 자체의 오류는 아래에 따로 표시합니다.";
            values["gatewayErrors"].ToolTip = "주소 조회 실패, 대상에 도달할 수 없음 등 검사 과정의 오류입니다. 응답을 기다리다 시간 초과된 검사와 구분합니다.";
            TextBlock gatewayNote = Text("공유기 검사 결과 · 게임 패킷 손실률과 별개", 11, AmberColor, false);
            gatewayNote.TextWrapping = TextWrapping.Wrap; gateway.Children.Add(gatewayNote);

            StackPanel adapter = Card("이 PC의 데이터 사용량", "초당 주고받는 양 · 모든 프로그램 합계", cards, 2);
            adapter.Children.Add(TwoMetrics("받는 데이터", "pcRx", "보내는 데이터", "pcTx"));
            values["pcRx"].ToolTip = "선택한 랜선·Wi-Fi 연결을 통해 이 PC로 들어오는 초당 데이터양입니다. 게임, 브라우저, 다운로드, 같은 집 안 파일 전송도 포함합니다. 인터넷 회선의 최대 속도를 검사하는 값은 아닙니다. KiB/s는 초당 1,024바이트 단위, MiB/s는 초당 1,048,576바이트 단위입니다.";
            values["pcTx"].ToolTip = "선택한 랜선·Wi-Fi 연결을 통해 이 PC에서 나가는 초당 데이터양입니다. 게임, 업로드, 클라우드 동기화 등 모든 프로그램의 사용량을 합칩니다. 다른 기기의 통신량은 포함하지 않습니다.";
            adapterStatus.Foreground = Brush(MutedColor); adapterStatus.TextWrapping = TextWrapping.Wrap; adapterStatus.Margin = new Thickness(0, 9, 0, 8); adapter.Children.Add(adapterStatus);
            adapter.Children.Add(SmallRow("초당 패킷 · 받기 / 보내기", "pcPackets"));
            adapter.Children.Add(SmallRow("처리 오류 · 받기 / 보내기", "pcErrors"));
            adapter.Children.Add(SmallRow("버린 패킷 · 받기 / 보내기", "pcDiscards"));
            values["pcPackets"].ToolTip = "패킷은 네트워크로 오가는 작은 데이터 묶음입니다. 왼쪽은 초당 받은 개수, 오른쪽은 초당 보낸 개수입니다.";
            values["pcErrors"].ToolTip = "측정 시작 이후 이 PC의 네트워크 장치가 보고한 수신 / 송신 오류 증가량입니다. 게임만의 오류 수나 회선 전체의 패킷 손실률은 아닙니다.";
            values["pcDiscards"].ToolTip = "측정 시작 이후 이 PC의 네트워크 장치가 오류 외의 이유로 버렸다고 보고한 수신 / 송신 패킷 증가량입니다. 게임 서버에서 사라진 패킷 수는 알 수 없습니다.";
            TextBlock adapterNote = Text("오류·버린 패킷은 측정 시작 이후 누적입니다. 게임·브라우저·파일 전송을 함께 포함합니다.", 11, MutedColor, false);
            adapterNote.TextWrapping = TextWrapping.Wrap; adapterNote.Margin = new Thickness(0, 7, 0, 0); adapter.Children.Add(adapterNote);
            body.Children.Add(cards);
            Grid charts = new Grid { Margin = new Thickness(0, 14, 0, 0) }; charts.ColumnDefinitions.Add(new ColumnDefinition()); charts.ColumnDefinitions.Add(new ColumnDefinition());
            AddChart(charts, "게임 송수신 추이", "최근 120초 · KiB/s     수신 ━ 청록 / 송신 ━ 보라", trafficChart, 0);
            AddChart(charts, "공유기 응답 시간 변화", "최근 120초 · ms     낮을수록 빠른 응답 · 답이 없는 구간은 선을 연결하지 않음", gatewayChart, 1); body.Children.Add(charts);
            Border diagnostics = new Border { Background = Brush(PanelColor), BorderBrush = Brush(BorderColor), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Margin = new Thickness(0, 14, 0, 0) };
            StackPanel diagnosticsBody = new StackPanel();
            diagnosticsBody.Children.Add(Text("수집 상태 / 외부 비교", 13, TextColor, true));
            traceWarning.TextWrapping = TextWrapping.Wrap; traceWarning.Foreground = Brush(MutedColor); traceWarning.Margin = new Thickness(0, 7, 0, 7); diagnosticsBody.Children.Add(traceWarning);
            referenceStatus.Foreground = Brush(MutedColor); referenceStatus.TextWrapping = TextWrapping.Wrap; diagnosticsBody.Children.Add(referenceStatus);
            TextBlock disclaimer = Text("이 화면은 PC가 읽을 수 있는 정보만 표시합니다. 다른 기기의 사용량과 공유기 WAN 전체 통계는 포함하지 않습니다.", 11, MutedColor, false); disclaimer.Margin = new Thickness(0, 9, 0, 0); disclaimer.TextWrapping = TextWrapping.Wrap; diagnosticsBody.Children.Add(disclaimer);
            diagnostics.Child = diagnosticsBody; body.Children.Add(diagnostics); scroller.Content = body; return scroller;
        }

        private UIElement BuildEndpoints()
        {
            Grid panel = new Grid { Margin = new Thickness(0, 14, 0, 0) }; panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); panel.RowDefinitions.Add(new RowDefinition());
            StackPanel top = new StackPanel { Margin = new Thickness(14, 10, 14, 12) };
            top.Children.Add(Text("관측된 접속지", 18, TextColor, true));
            TextBlock copy = Text("선택한 PID의 TCP·UDP 송수신에서 관측한 주소입니다. 게임 서버 여부를 판정하거나 이 주소로 자동 핑을 보내지 않습니다.", 12, MutedColor, false); copy.TextWrapping = TextWrapping.Wrap; copy.Margin = new Thickness(0, 7, 0, 5); top.Children.Add(copy);
            endpointStatus.Foreground = Brush(AccentColor); endpointStatus.Text = "측정 시작 후 접속지가 표시됩니다. 최근 관측 100행까지 표시합니다."; top.Children.Add(endpointStatus); panel.Children.Add(top);
            endpointGrid.AutoGenerateColumns = false; endpointGrid.IsReadOnly = true; endpointGrid.CanUserAddRows = false; endpointGrid.CanUserDeleteRows = false; endpointGrid.CanUserReorderColumns = true;
            endpointGrid.HeadersVisibility = DataGridHeadersVisibility.Column; endpointGrid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
            endpointGrid.HorizontalGridLinesBrush = Brush(BorderColor); endpointGrid.Background = Brush(PanelColor); endpointGrid.Foreground = Brush(TextColor); endpointGrid.BorderThickness = new Thickness(0);
            endpointGrid.RowBackground = Brush(PanelColor); endpointGrid.AlternatingRowBackground = Brush(Color.FromRgb(21, 29, 39)); endpointGrid.RowHeight = 34;
            endpointGrid.EnableRowVirtualization = true; endpointGrid.EnableColumnVirtualization = true;
            Style header = new Style(typeof(DataGridColumnHeader)); header.Setters.Add(new Setter(Control.BackgroundProperty, Brush(Color.FromRgb(35, 47, 62)))); header.Setters.Add(new Setter(Control.ForegroundProperty, Brush(MutedColor))); header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 10, 9, 10))); header.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(BorderColor))); endpointGrid.ColumnHeaderStyle = header;
            Style cell = new Style(typeof(DataGridCell)); cell.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4))); cell.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0))); cell.Setters.Add(new Setter(Control.ForegroundProperty, Brush(TextColor))); endpointGrid.CellStyle = cell;
            AddColumn("프로토콜", "Protocol", 75, null); AddColumn("내 주소 / 포트", "Local", 210, null); AddColumn("원격 주소 / 포트", "Remote", 220, null);
            AddColumn("송신 B", "SentBytes", 90, "N0"); AddColumn("수신 B", "ReceivedBytes", 90, "N0"); AddColumn("송신 이벤트", "SentEvents", 90, "N0"); AddColumn("수신 이벤트", "ReceivedEvents", 90, "N0"); AddColumn("재전송 이벤트", "RetransmitEvents", 100, "N0"); AddColumn("마지막 관측", "LastSeen", 110, "HH:mm:ss");
            Grid.SetRow(endpointGrid, 1); panel.Children.Add(endpointGrid); return panel;
        }

        private UIElement BuildProcessTraffic()
        {
            Grid panel = new Grid { Margin = new Thickness(14) };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); panel.RowDefinitions.Add(new RowDefinition());
            StackPanel intro = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            intro.Children.Add(Text("전체 통신 프로그램", 19, TextColor, true));
            TextBlock note = Text("통신이 관측된 모든 프로그램을 송신량 순으로 표시하고 자동 저장합니다. 모든 어댑터의 TCP/UDP 집계이며, ETW 전달 지연에 따라 한 구간에 몰릴 수 있습니다.", 12, MutedColor, false);
            note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 7, 0, 7); intro.Children.Add(note);
            processTrafficStatus.Text = "측정 시작 후 프로그램별 관측량을 표시합니다. 관리자 권한이 필요합니다.";
            processTrafficStatus.TextWrapping = TextWrapping.Wrap; processTrafficStatus.Foreground = Brush(AccentColor); intro.Children.Add(processTrafficStatus); panel.Children.Add(intro);
            processTrafficGrid.AutoGenerateColumns = false; processTrafficGrid.IsReadOnly = true;
            processTrafficGrid.CanUserAddRows = false; processTrafficGrid.CanUserDeleteRows = false;
            processTrafficGrid.HeadersVisibility = DataGridHeadersVisibility.Column; processTrafficGrid.Background = Brush(PanelColor);
            processTrafficGrid.Foreground = Brush(TextColor); processTrafficGrid.RowBackground = Brush(PanelColor);
            processTrafficGrid.AlternatingRowBackground = Brush(Color.FromRgb(21, 29, 39)); processTrafficGrid.RowHeight = 34;
            processTrafficGrid.BorderThickness = new Thickness(0); processTrafficGrid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
            processTrafficGrid.HorizontalGridLinesBrush = Brush(BorderColor); processTrafficGrid.EnableRowVirtualization = true;
            processTrafficGrid.ColumnHeaderStyle = endpointGrid.ColumnHeaderStyle; processTrafficGrid.CellStyle = endpointGrid.CellStyle;
            string[] labels = { "프로그램", "PID", "송신 KiB/s", "수신 KiB/s", "누적 송신 KiB", "누적 수신 KiB", "이름 식별" };
            string[] properties = { "Name", "Pid", "SendKiB", "ReceiveKiB", "SentTotalKiB", "ReceivedTotalKiB", "Identity" };
            double[] widths = { 285, 75, 125, 125, 135, 135, 160 };
            for (int i = 0; i < labels.Length; i++)
            {
                Binding binding = new Binding(properties[i]); if (i >= 2 && i <= 5) binding.StringFormat = "{0:N1}";
                processTrafficGrid.Columns.Add(new DataGridTextColumn { Header = labels[i], Binding = binding, Width = new DataGridLength(widths[i]) });
            }
            Grid.SetRow(processTrafficGrid, 1); panel.Children.Add(processTrafficGrid); return panel;
        }

        private UIElement BuildLog()
        {
            Grid panel = new Grid { Margin = new Thickness(14) };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid head = new Grid(); head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel intro = new StackPanel(); intro.Children.Add(Text("자동 기록 · 분석", 19, TextColor, true)); sessionStatus.Foreground = Brush(MutedColor); sessionStatus.Text = "기록 없음 · 측정 시작 시 새 폴더에 자동 저장합니다."; sessionStatus.Margin = new Thickness(0, 6, 0, 0); intro.Children.Add(sessionStatus); head.Children.Add(intro);
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal };
            exportButton.Content = "최근 CSV 내보내기"; exportButton.ToolTip = "별도 사본으로 저장합니다. 자동 기록은 전체 세션을 이미 보관합니다.";
            logFolderButton.Content = "기록 폴더 열기"; logFolderButton.Margin = new Thickness(8, 0, 0, 0); logFolderButton.IsEnabled = Directory.Exists(LogRoot);
            actions.Children.Add(exportButton); actions.Children.Add(logFolderButton); Grid.SetColumn(actions, 1); head.Children.Add(actions); panel.Children.Add(head);
            recordingStatus.Text = "측정 중 1초마다 자동 저장 · 전체 구간 자동 분석\n저장 위치: " + LogRoot;
            recordingStatus.Foreground = Brush(AccentColor); recordingStatus.TextWrapping = TextWrapping.Wrap; recordingStatus.Margin = new Thickness(0, 12, 0, 0); Grid.SetRow(recordingStatus, 1); panel.Children.Add(recordingStatus);
            Border help = new Border { Background = Brush(Color.FromRgb(20, 28, 39)), BorderBrush = Brush(BorderColor), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(14), Margin = new Thickness(0, 15, 0, 15) };
            StackPanel helpBody = new StackPanel(); helpBody.Children.Add(Text("수치를 읽는 방법", 14, TextColor, true));
            AddHelp(helpBody, "게임 송수신", "Windows ETW 이벤트 기준입니다. 이벤트 수는 실제 회선의 패킷 수와 같지 않을 수 있고 TCP 재전송 이벤트는 손실률이 아닙니다.");
            AddHelp(helpBody, "공유기까지의 연결 상태", "내 PC가 공유기에 약 1초마다 확인 요청을 보내 답이 오는 시간과 답이 없던 비율을 봅니다. 최근 60초 결과이며, 게임 서버의 지연·패킷 손실률은 별도입니다. 검사 요청 제한이나 차단도 영향을 줍니다.");
            AddHelp(helpBody, "이 PC의 데이터 사용량", "위에서 선택한 랜선·Wi-Fi 연결로 지금 주고받는 초당 데이터양입니다. 게임·브라우저·업로드·같은 집 안 파일 전송이 합산됩니다. 오류·버린 패킷은 이 PC의 장치가 보고한 측정 시작 이후 누적값입니다.");
            AddHelp(helpBody, "수집 누락", "ETW 이벤트/버퍼 누락은 이 프로그램의 관측 누락입니다. 실제 게임 패킷 손실로 해석하지 마세요.");
            AddHelp(helpBody, "자동 저장", "측정 중 전체 1초 표본과 개별 ICMP 응답·시간 초과·오류를 세션별 파일에 저장합니다. 창을 닫거나 새 측정을 시작해도 기존 파일은 남습니다. 기록을 외부로 업로드하지 않습니다.");
            AddHelp(helpBody, "자동 분석", "전체 기록에서 지연 급증·연속 무응답·게임 수신 공백을 규칙으로 찾으며 체감 끊김이나 장애 원인을 확정하지 않습니다. 종료 시 summary.txt를 저장합니다.");
            AddHelp(helpBody, "화면과 사본", "화면 그래프와 메모리 CSV 사본은 최근 구간만 보관합니다. 전체 기록은 자동 저장 폴더에 있습니다.");
            help.Child = helpBody;
            Expander explanation = new Expander { Header = "기록 범위와 지표 설명", Content = help, Foreground = Brush(MutedColor), Margin = new Thickness(0, 10, 0, 8), IsExpanded = false };
            Grid.SetRow(explanation, 2); panel.Children.Add(explanation);
            analysisSummary.Text = "측정 시작 후 전체 기록의 통계와 이상 구간 후보를 여기에 자동으로 표시합니다.";
            analysisSummary.IsReadOnly = true; analysisSummary.TextWrapping = TextWrapping.Wrap; analysisSummary.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            analysisSummary.VerticalContentAlignment = VerticalAlignment.Top;
            analysisSummary.Margin = new Thickness(0, 0, 0, 10); analysisSummary.Padding = new Thickness(12); Grid.SetRow(analysisSummary, 3); panel.Children.Add(analysisSummary);
            eventList.Background = Brush(Color.FromRgb(13, 20, 29)); eventList.Foreground = Brush(MutedColor); eventList.BorderBrush = Brush(BorderColor); eventList.FontFamily = new FontFamily("Malgun Gothic"); eventList.FontSize = 12;
            eventList.Items.Add("측정을 시작하면 수집 상태 변경이 여기에 기록됩니다."); Grid.SetRow(eventList, 4); panel.Children.Add(eventList); return panel;
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
                StopSession(); permissionBanner.Visibility = Visibility.Collapsed;
                statusLine.Text = "새 관리자 창을 열었습니다. 새 창에서 ‘측정 시작’을 누르세요. 이 창의 기존 기록은 CSV로 저장할 수 있습니다.";
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
            stateText.Text = failed ? requested ? "●  게임 수집 안 됨" : "●  프로그램 수집 안 됨" : "●  측정 중";
            stateText.Foreground = Brush(failed ? AmberColor : AccentColor);
            gameStatus.Text = s.GameStatus + (failed && requested ? " · 게임 수집 실패" : requested && !s.GameRatesAvailable ? " · 선택한 프로세스 통신 대기" : "");
            permissionBanner.Visibility = (requested || processRequested) && s.Trace.PermissionDenied ? Visibility.Visible : Visibility.Collapsed;
            administratorButton.Visibility = administrator ? Visibility.Collapsed : Visibility.Visible;
            permissionMessage.Text = administrator ? "관리자 모드에서도 Windows가 게임 수집 접근을 거부했습니다. PC·공유기·외부 ICMP 측정은 진행 중입니다. 아래 수집 상태의 오류 코드로 추가 확인이 필요합니다." :
                "게임·프로그램별 통신 수집이 권한 부족으로 시작되지 않았습니다. PC·공유기·외부 ICMP 측정은 진행 중입니다. ‘관리자 모드 열기’를 누르고 UAC를 승인한 뒤 새 창에서 측정을 시작하세요.";
        }

        private void StartSession(bool collectProcesses = true)
        {
            if (running) return;
            ProcessChoice p = processPicker.SelectedItem as ProcessChoice;
            AdapterInfo a = adapterPicker.SelectedItem as AdapterInfo;
            if (a == null) { MessageBox.Show(this, "측정할 PC 네트워크 어댑터를 선택하세요.", "어댑터 선택", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                if (String.IsNullOrWhiteSpace(referenceInput.Text)) referenceInput.Text = DefaultReferenceTarget;
                BeginRecording(p, a, collectProcesses);
                controller.Start(p == null ? 0 : p.Id, a.Id, gatewayInput.Text.Trim(), referenceInput.Text.Trim(), true, collectProcesses);
                session.Clear(); eventList.Items.Clear(); previousTraceStatus = null; endpointGrid.ItemsSource = null; trafficChart.Clear(); gatewayChart.Clear(); began = DateTime.Now;
                running = true; SetControls(true);
                AddEvent("측정 시작 · " + (p == null ? "PC / 공유기" : p.ToString()) + " · " + a.Name);
                statusLine.Text = "게임·전체 통신 프로그램·공유기·외부 ICMP를 자동 저장·분석 중입니다.";
                timer.Start(); Poll();
            }
            catch (Exception ex)
            {
                controller.Stop(); FinishRecording("측정 시작 실패"); running = false; SetControls(false); statusLine.Text = "시작하지 못했습니다: " + ex.Message;
                MessageBox.Show(this, ex.Message, "측정 시작 실패", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally { Mouse.OverrideCursor = null; }
        }

        private void StopSession(string reason = "측정 중지")
        {
            if (!running) return;
            permissionBanner.Visibility = Visibility.Collapsed;
            timer.Stop(); controller.Stop(); running = false; FinishRecording(reason); SetControls(false);
            AddEvent("측정 중지 · 표본 " + session.Count.ToString("N0") + "개");
            statusLine.Text = lastRecordingError.Length == 0 ? "측정을 중지했습니다. 전체 기록과 분석 요약을 자동 저장했습니다. ‘기록 · 분석’에서 확인하세요." : "자동 저장 중 오류가 있었습니다. ‘기록 · 분석’에서 저장 상태를 확인하세요.";
            trafficChart.Clear(); gatewayChart.Clear();
            foreach (string key in new string[] { "gameRx", "gameTx", "gameEvents", "receiveIdle", "gatewayRtt", "gatewayMissing", "gatewayAverage", "gatewayVariation", "gatewayCount", "gatewayErrors", "pcRx", "pcTx", "pcPackets" }) values[key].Text = "—";
            gameStatus.Text = "측정 중지 · 접속지와 누적 기록 유지"; gatewayStatus.Text = "공유기 응답 검사 중지"; referenceStatus.Text = "외부 비교 검사 중지"; adapterStatus.Text = "이 PC의 데이터 사용량 측정 중지"; traceWarning.Text = "수집 중지 · 게임 ETW 세션을 종료했습니다.";
            processTrafficStatus.Text = "수집 중지 · 마지막 프로그램 표본 유지 · 전체 기록은 자동 저장 폴더에 있습니다."; values["deliveryLag"].Text = "—";
            UpdateSessionText();
        }

        private void Poll()
        {
            if (!running) return;
            try
            {
                MonitorSnapshot s = controller.Snapshot(); session.Add(s);
                lock (recordingGate)
                {
                    if (automaticLog == null || !automaticLog.Append(s)) throw new IOException(automaticLog == null ? "자동 기록이 시작되지 않았습니다." : automaticLog.ErrorMessage);
                    analysis.Observe(s);
                    if ((s.TimestampUtc - summaryUpdatedUtc).TotalSeconds >= 10)
                    {
                        analysisSummary.Text = analysis.SummaryText(); summaryUpdatedUtc = s.TimestampUtc;
                    }
                }
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
                endpointStatus.Text = s.Endpoints.Count.ToString("N0") + "개 접속지 표시 · 자동 기록 " + s.EndpointObservations.Count.ToString("N0") + "개 대상 · 보관 한도 교체 " + s.GameEndpointEvictions.ToString("N0") + "회 · 화면 최근 100행";
                if (s.Trace.Status != previousTraceStatus) { AddEvent("게임 수집 · " + s.Trace.Status); previousTraceStatus = s.Trace.Status; }
                UpdateSessionText();
                // Detailed rows are already serialized to disk and copied into the UI.
                // Keep the two-hour in-memory chart history small during gameplay.
                s.EndpointObservations.Clear(); s.ProcessTraffic.Rows.Clear(); s.Endpoints = new List<EndpointRow>();
            }
            catch (Exception ex) { AddEvent("측정 오류 · " + ex.Message); StopSession(); statusLine.Text = "측정 중 오류가 발생해 중지했습니다: " + ex.Message; }
        }

        private void SetControls(bool active)
        {
            processPicker.IsEnabled = adapterPicker.IsEnabled = gatewayInput.IsEnabled = referenceInput.IsEnabled = refreshButton.IsEnabled = startButton.IsEnabled = !active;
            stopButton.IsEnabled = active; exportButton.IsEnabled = session.Count > 0;
            stateText.Text = active ? "●  측정 중" : "●  대기 / 중지"; stateText.Foreground = Brush(active ? AccentColor : MutedColor);
        }
        private void UpdateProcessTraffic(ProcessTrafficSnapshot traffic)
        {
            processTrafficStatus.Text = traffic.Status ?? "전체 프로그램별 통신 집계 대기";
            processTrafficStatus.Foreground = Brush(traffic.DroppedProcessEvents > 0 ? AmberColor : AccentColor);
            processTrafficGrid.ItemsSource = traffic.Available ? traffic.Rows.OrderByDescending(x => x.SentBytesPerSecond).Select(x => new ProcessTrafficView {
                Name = x.ProcessName, Pid = x.ProcessId, SendKiB = x.SentBytesPerSecond / 1024, ReceiveKiB = x.ReceivedBytesPerSecond / 1024,
                SentTotalKiB = x.SentBytesTotal / 1024.0, ReceivedTotalKiB = x.ReceivedBytesTotal / 1024.0,
                Identity = x.IdentityAvailable ? "확인" : "귀속 미확인"
            }).ToList() : null;
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
        }
        private void AddEvent(string text)
        {
            eventList.Items.Add(DateTime.Now.ToString("HH:mm:ss") + "   " + text); while (eventList.Items.Count > 250) eventList.Items.RemoveAt(0);
            if (eventList.Items.Count > 0) eventList.ScrollIntoView(eventList.Items[eventList.Items.Count - 1]);
        }
        private void DisposeMonitor()
        {
            if (disposed) return;
            disposed = true; timer.Stop(); controller.Stop(); running = false;
            FinishRecording("창 닫기"); controller.Dispose();
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
                    "\r\ngame_collection_requested=True\r\nprocess_traffic_requested=" + collectProcesses + "\r\nprocess_traffic_scope=all_observed_processes\r\nprocess_tracking_limit=2048\r\ngame_endpoint_tracking_limit=512\r\nadministrator=" + administrator + "\r\n";
                using (StreamWriter writer = new StreamWriter(new FileStream(Path.Combine(next.SessionDirectory, "settings.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(true))) writer.Write(settings);
            }
            catch { next.Complete("기록 설정 저장 실패"); next.Dispose(); throw; }
            lock (recordingGate)
            {
                automaticLog = next; analysis = new SessionAnalysis();
                lastLogDirectory = next.SessionDirectory; lastRecordingError = ""; summaryUpdatedUtc = DateTime.MinValue;
            }
            analysisSummary.Text = "측정 표본을 기다리는 중입니다. 전체 세션을 자동 분석합니다.";
            UpdateRecordingStatus();
        }

        private void RecordProbe(string kind, ProbeSample sample)
        {
            lock (recordingGate)
            {
                if (automaticLog == null) return;
                automaticLog.RecordProbe(kind, sample); analysis.ObserveProbe(kind, sample);
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
            lastRecordingError = completed.ErrorMessage;
            string summary = "종료 사유: " + reason + "\r\n저장 상태: " + (completed.IsHealthy ? "정상" : "오류 · " + lastRecordingError) +
                "\r\n전체 표본: " + completed.SampleCount.ToString("N0") + "개\r\n기록 폴더: " + completed.SessionDirectory + "\r\n\r\n" + completedAnalysis.SummaryText();
            analysisSummary.Text = summary;
            try
            {
                using (StreamWriter writer = new StreamWriter(new FileStream(Path.Combine(completed.SessionDirectory, "summary.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(true))) writer.Write(summary);
            }
            catch (Exception error) { lastRecordingError = "분석 요약을 저장하지 못했습니다: " + error.Message + (lastRecordingError.Length == 0 ? "" : " · " + lastRecordingError); }
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
                    recordingStatus.Text = (lastRecordingError.Length == 0 ? "자동 저장 완료 · summary.txt에서 전체 분석 확인" : "자동 저장 오류 · " + lastRecordingError) + "\n" + lastLogDirectory;
                    recordingStatus.Foreground = Brush(lastRecordingError.Length == 0 ? AccentColor : AmberColor);
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
            if (timer.IsEnabled || !referenceInput.IsEnabled || referenceInput.Text != DefaultReferenceTarget || tabs.Items.Count != 4 || values.Count < 12)
                throw new InvalidOperationException("UI initial monitoring/navigation state is invalid.");
            foreach (TabItem item in tabs.Items) { tabs.SelectedItem = item; UpdateLayout(); }
            tabs.SelectedIndex = 0; Width = 1240; Height = 920;
            RenderTestSurface(screenshotPath);
            tabs.SelectedIndex = 3;
            RenderTestSurface(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(screenshotPath)), "automatic-recording.png"));
            ProcessTrafficSnapshot example = new ProcessTrafficSnapshot { Available = true, Status = "UI 검사 예시 · 실제 측정 없음 · 모든 어댑터 ETW 도착 기준" };
            example.Rows.Add(new ProcessTrafficRow { ProcessId = 12345, ProcessName = "UI 검사 예시 · 실제 측정 없음", SentBytesPerSecond = 2048, ReceivedBytesPerSecond = 1024, IdentityAvailable = false });
            UpdateProcessTraffic(example); tabs.SelectedIndex = 2;
            if (processTrafficGrid.Items.Count != 1 || ((ProcessTrafficView)processTrafficGrid.Items[0]).SendKiB != 2) throw new InvalidOperationException("Process traffic UI binding failed.");
            RenderTestSurface(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(screenshotPath)), "process-traffic.png"));
            tabs.SelectedIndex = 0;
            // Exercise the permission UI without starting ETW, sending probes or invoking UAC.
            MonitorSnapshot denied = new MonitorSnapshot { ProcessId = 12345, GameCollectionRequested = true, ProcessCollectionRequested = true, GameStatus = "권한 상태 UI 검사 · 실제 측정 없음",
                Trace = new TraceHealth { PermissionDenied = true, NativeErrorCode = 5, FailureStage = "StartTrace", Status = "UI 검사: StartTrace · 오류 5" } };
            UpdateGameCollectionState(denied);
            if (permissionBanner.Visibility != Visibility.Visible || stateText.Text != "●  게임 수집 안 됨" ||
                administratorButton.Visibility != (administrator ? Visibility.Collapsed : Visibility.Visible) || values["gameRx"].Text != "—")
                throw new InvalidOperationException("Permission-denied UI did not expose unavailable game collection.");
            RenderTestSurface(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(screenshotPath)), "permission-needed.png"));
            denied.Trace.Active = true; denied.Trace.PermissionDenied = false;
            UpdateGameCollectionState(denied);
            if (permissionBanner.Visibility != Visibility.Collapsed || !gameStatus.Text.Contains("통신 대기"))
                throw new InvalidOperationException("Active collection waiting state is invalid.");
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
            if (!running) throw new InvalidOperationException("Local recording test failed to start.");
            System.Threading.Thread.Sleep(1300); Poll();
            string firstDirectory = lastLogDirectory;
            StopSession("로컬 기록 통합 검사");
            if (lastRecordingError.Length != 0) throw new IOException(lastRecordingError);
            string firstSamples = File.ReadAllText(Path.Combine(firstDirectory, "samples.csv"));
            if (File.ReadAllLines(Path.Combine(firstDirectory, "samples.csv")).Length < 3 ||
                File.ReadAllLines(Path.Combine(firstDirectory, "probes.csv")).Length < 3 ||
                !File.ReadAllText(Path.Combine(firstDirectory, "summary.txt")).Contains("전체 구간을 자동으로 분석합니다"))
                throw new InvalidOperationException("Automatic samples, raw probes, or summary were not persisted.");
            StartSession(false);
            if (!running || firstDirectory == lastLogDirectory) throw new InvalidOperationException("New session reused the previous log folder.");
            System.Threading.Thread.Sleep(250); Poll();
            string secondDirectory = lastLogDirectory;
            DisposeMonitor();
            if (!File.ReadAllText(Path.Combine(secondDirectory, "summary.txt")).Contains("종료 사유: 창 닫기") ||
                File.ReadAllText(Path.Combine(firstDirectory, "samples.csv")) != firstSamples || lastRecordingError.Length != 0)
                throw new InvalidOperationException("Window close did not preserve and finalize both sessions.");
            File.WriteAllText(reportPath, "PASS: Actual WPF start/poll/stop/restart/close persisted full samples, raw loopback probes and summaries without manual marks or exports. No game, ETW or UAC invoked.\r\nFirst fixture=" + firstDirectory + "\r\nSecond fixture=" + secondDirectory + "\r\n", new UTF8Encoding(true));
        }

        private void RenderTestSurface(string screenshotPath)
        {
            FrameworkElement surface = (FrameworkElement)Content;
            surface.Measure(new Size(1240, 920)); surface.Arrange(new Rect(0, 0, 1240, 920)); surface.UpdateLayout();
            RenderTargetBitmap bitmap = new RenderTargetBitmap(1240, 920, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
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
            StackPanel body = new StackPanel(); body.Children.Add(Text(title, 14, TextColor, true)); TextBlock sub = Text(subtitle, 10.5, MutedColor, false); sub.TextWrapping = TextWrapping.Wrap; sub.Margin = new Thickness(0, 4, 0, 6); body.Children.Add(sub); chart.Height = 155; body.Children.Add(chart); shell.Child = body; Grid.SetColumn(shell, column); parent.Children.Add(shell);
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
        private sealed class ProcessTrafficView
        {
            public string Name { get; set; }
            public int Pid { get; set; }
            public double SendKiB { get; set; }
            public double ReceiveKiB { get; set; }
            public double SentTotalKiB { get; set; }
            public double ReceivedTotalKiB { get; set; }
            public string Identity { get; set; }
        }

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
