using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PcInternetMonitor
{
    public sealed partial class MainWindow
    {
        private readonly TextBlock recordingTitle = new TextBlock(), recordingDetail = new TextBlock(), connectionOverview = new TextBlock(), historyCount = new TextBlock();
        private readonly Border recordingBanner = new Border();
        private readonly Grid cards = new Grid();
        private readonly ListBox historyList = new ListBox();
        private readonly Dictionary<string, TextBlock> values = new Dictionary<string, TextBlock>();
        private Border dashboardFooter;
        private const string RouteNote = "검사는 Windows의 라우팅·VPN·프록시 설정을 따르며 선택한 어댑터에 경로를 강제하지 않습니다. 외부 한 대상의 실패는 인터넷 전체나 ISP 장애의 확정이 아닙니다.";
        private UIElement BuildWindow()
        {
            Grid main = new Grid { Margin = new Thickness(22, 16, 22, 12), Background = Brush(BackColor) };
            for (int i = 0; i < 7; i++) main.RowDefinitions.Add(new RowDefinition { Height = i == 5 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid header = new Grid(); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel brand = new StackPanel(); brand.Children.Add(Text("[LOCAL] 내 PC 연결 · 이름 조회 · 웹 접속", 11, MutedColor, false)); brand.Children.Add(Text("> PC 인터넷 모니터", 25, TextColor, true)); header.Children.Add(brand);
            StackPanel headerActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            TextBlock version = Text("v" + AppVersion, 12, MutedColor, false); version.VerticalAlignment = VerticalAlignment.Center; headerActions.Children.Add(version);
            exitButton.Content = "앱 종료"; exitButton.Margin = new Thickness(16, 0, 0, 0); exitButton.ToolTip = "진행 중인 기록을 마무리하고 백그라운드 실행도 종료합니다."; headerActions.Children.Add(exitButton); Grid.SetColumn(headerActions, 1); header.Children.Add(headerActions); main.Children.Add(header);
            Grid form = new Grid { Margin = new Thickness(0, 14, 0, 12) }; form.ColumnDefinitions.Add(new ColumnDefinition()); form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel field = new StackPanel(); field.Children.Add(Text("PC 연결 · 랜선 / Wi-Fi", 11, LabelColor, true)); adapterPicker.Height = 38; adapterPicker.Margin = new Thickness(0, 5, 10, 0); field.Children.Add(adapterPicker); form.Children.Add(field);
            refreshButton.Content = "목록 갱신"; refreshButton.Margin = new Thickness(0, 19, 10, 0); Grid.SetColumn(refreshButton, 1); form.Children.Add(refreshButton);
            logFolderButton.Content = "기록 폴더"; logFolderButton.Margin = new Thickness(0, 19, 0, 0); Grid.SetColumn(logFolderButton, 2); form.Children.Add(logFolderButton); Grid.SetRow(form, 1); main.Children.Add(form);
            recordingBanner.Background = Brush(PanelColor); recordingBanner.BorderThickness = new Thickness(2, 0, 0, 0); recordingBanner.Padding = new Thickness(12, 10, 12, 10); recordingBanner.Margin = new Thickness(0, 0, 0, 10);
            Grid banner = new Grid(); banner.ColumnDefinitions.Add(new ColumnDefinition()); banner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel copy = new StackPanel(); recordingTitle.FontSize = 17; recordingTitle.FontWeight = FontWeights.SemiBold; recordingDetail.FontSize = 11; recordingDetail.Foreground = Brush(MutedColor); recordingDetail.TextWrapping = TextWrapping.Wrap; recordingDetail.TextTrimming = TextTrimming.CharacterEllipsis; recordingDetail.MaxHeight = 48; recordingDetail.Margin = new Thickness(0, 4, 12, 0); copy.Children.Add(recordingTitle); copy.Children.Add(recordingDetail); banner.Children.Add(copy);
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; startButton.Content = "> 검사 시작"; startButton.Foreground = Brush(AccentColor); stopButton.Content = "검사 중지"; stopButton.Margin = new Thickness(8, 0, 0, 0); actions.Children.Add(startButton); actions.Children.Add(stopButton); Grid.SetColumn(actions, 1); banner.Children.Add(actions); recordingBanner.Child = banner; Grid.SetRow(recordingBanner, 2); main.Children.Add(recordingBanner);
            StackPanel overview = new StackPanel(); connectionOverview.FontSize = 13; connectionOverview.Foreground = Brush(MutedColor); connectionOverview.TextWrapping = TextWrapping.Wrap; connectionOverview.Margin = new Thickness(0, 0, 0, 10); overview.Children.Add(connectionOverview);
            for (int i = 0; i < 4; i++) cards.ColumnDefinitions.Add(new ColumnDefinition());
            AddCheckCard(0, "gateway", "공유기 응답", "내 PC ↔ 공유기 검사 대상"); AddCheckCard(1, "reference", "외부 응답", "내 PC ↔ 외부 검사 서버"); AddCheckCard(2, "dns", "DNS 이름 조회", "사이트 이름을 IP 주소로 확인"); AddCheckCard(3, "https", "웹 접속 · HTTPS", "암호화된 웹 응답 확인"); overview.Children.Add(cards); Grid.SetRow(overview, 3); main.Children.Add(overview);
            Grid historyHeader = new Grid { Margin = new Thickness(0, 16, 0, 8) }; historyHeader.ColumnDefinitions.Add(new ColumnDefinition()); historyHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); historyHeader.Children.Add(Text("[ 끊김 · 검사 실패 이력 ]", 14, LabelColor, true)); historyCount.FontSize = 11; historyCount.Foreground = Brush(MutedColor); historyCount.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(historyCount, 1); historyHeader.Children.Add(historyCount); Grid.SetRow(historyHeader, 4); main.Children.Add(historyHeader);
            historyList.Background = Brush(PanelColor); historyList.Foreground = Brush(TextColor); historyList.BorderThickness = new Thickness(0); historyList.HorizontalContentAlignment = HorizontalAlignment.Stretch; historyList.Padding = new Thickness(7); historyList.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled); Grid.SetRow(historyList, 5); main.Children.Add(historyList);
            Style historyItemStyle = new Style(typeof(ListBoxItem)); historyItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brush(PanelColor))); historyItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brush(TextColor))); historyItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); historyItemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)); historyItemStyle.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
            Trigger selectedRow = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true }; selectedRow.Setters.Add(new Setter(Control.BackgroundProperty, Brush(Color.FromRgb(32, 38, 45)))); selectedRow.Setters.Add(new Setter(Control.ForegroundProperty, Brush(TextColor))); historyItemStyle.Triggers.Add(selectedRow); historyList.ItemContainerStyle = historyItemStyle;
            TextBlock foot = Text("X: 아이콘 없이 백그라운드 실행 · EXE 재실행: 창 열기 · 완전 종료: 앱 종료\nDNS·HTTPS 약 5초 간격 · 이력은 연속 2회 실패부터 기록 · 전체 인터넷의 상태를 보장하지 않음", 11, MutedColor, false); foot.ToolTip = RouteNote + "\nDNS는 시스템 캐시·hosts 결과를 포함할 수 있습니다. HTTPS 시간에는 DNS/TCP/TLS/서버 응답 시간이 포함되며 순수 회선 핑이 아닙니다.";
            dashboardFooter = new Border { BorderBrush = Brush(BorderColor), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 9, 0, 0), Margin = new Thickness(0, 10, 0, 0), Child = foot }; Grid.SetRow(dashboardFooter, 6); main.Children.Add(dashboardFooter); return main;
        }
        private void AddCheckCard(int column, string key, string title, string scope)
        {
            StackPanel body = new StackPanel(); body.Children.Add(Text(title, 13, LabelColor, true)); TextBlock range = Text(scope, 10, MutedColor, false); range.Margin = new Thickness(0, 5, 0, 0); body.Children.Add(range);
            TextBlock metric = Text("—", 22, MutedColor, true); metric.Margin = new Thickness(0, 14, 0, 0); body.Children.Add(metric); values.Add(key, metric);
            TextBlock detail = Text("검사 전", 11, MutedColor, false); detail.Margin = new Thickness(0, 6, 0, 0); detail.MinHeight = 32; body.Children.Add(detail); values.Add(key + "Detail", detail);
            TextBlock target = Text("", 10, MutedColor, false); target.TextWrapping = TextWrapping.NoWrap; target.TextTrimming = TextTrimming.CharacterEllipsis; body.Children.Add(target); values.Add(key + "Target", target);
            Border shell = new Border { Background = Brush(PanelColor), BorderBrush = Brush(BorderColor), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12), Margin = new Thickness(column == 0 ? 0 : 4, 0, column == 3 ? 0 : 4, 0), Child = body, ToolTip = RouteNote }; Grid.SetColumn(shell, column); cards.Children.Add(shell);
        }
        private void RefreshPresentation()
        {
            Color color = fault ? ErrorColor : running ? AccentColor : MutedColor;
            recordingTitle.Text = !running && clockStopUtc != DateTime.MinValue ? "[!] 시각 변경으로 검사 중지" : fault ? "[!] 오류로 검사 중지" : running ? "[*] 검사 · 기록 중" : "[-] 검사 중지"; recordingTitle.Foreground = Brush(color); recordingBanner.BorderBrush = Brush(color);
            recordingDetail.Text = running ? "진행 " + sessionElapsed.Elapsed.ToString(@"hh\:mm\:ss") + " · 기록 " + sampleCount.ToString("N0") + "개 · 마지막 관측 " + (lastSampleUtc == DateTime.MinValue ? "대기" : lastSampleUtc.ToLocalTime().ToString("HH:mm:ss")) : stopReason;
            if (notice.Length > 0) recordingDetail.Text += "\n" + notice;
            if (!String.IsNullOrEmpty(DiagnosticsLog.ErrorMessage)) recordingDetail.Text += "\n진단 저장 오류: " + DiagnosticsLog.ErrorMessage;
            recordingDetail.ToolTip = recordingDetail.Text;
            Title = (!running && clockStopUtc != DateTime.MinValue ? "시각 변경으로 중지" : fault ? "기록 오류" : running ? "검사 중" : "검사 중지") + " | PC 인터넷 모니터 " + AppVersion;
            ShowProbe("gateway", latest == null ? null : latest.Gateway); ShowProbe("reference", latest == null ? null : latest.Reference);
            ShowInternet("dns", latestInternet == null ? null : latestInternet.Dns); ShowInternet("https", latestInternet == null ? null : latestInternet.Https);
            bool link = latest != null && latest.AdapterCountersAvailable; string usage = latest != null && latest.AdapterRatesAvailable ? " · 받기 " + Rate(latest.AdapterReceivedBytesPerSecond) + " / 보내기 " + Rate(latest.AdapterSentBytesPerSecond) : "";
            connectionOverview.Text = !running ? "선택한 PC 연결과 Windows가 사용하는 인터넷 경로를 함께 확인합니다." : link ? "PC 연결 관측됨" + usage : latest == null ? "PC 연결 확인 중" : latest.AdapterStatus;
            connectionOverview.Foreground = Brush(!running || latest == null ? MutedColor : link ? TextColor : AmberColor); if (historyDirty) RefreshHistory();
        }
        private void ShowProbe(string key, ProbeSummary probe)
        {
            bool fresh = running && probe != null && !probe.Stale; bool success = fresh && probe.CurrentMs.HasValue && !probe.CurrentError && !probe.CurrentNetworkFailure && !probe.CurrentTimedOut;
            values[key].Text = success ? Ms(probe.CurrentMs.Value) : !running || probe == null ? "—" : probe.CurrentNetworkFailure ? "응답/경로 실패" : probe.CurrentError ? "검사 오류" : probe.CurrentTimedOut ? "응답 없음" : probe.Stale ? "최근 값 없음" : String.IsNullOrEmpty(probe.Target) ? "대상 없음" : "검사 대기";
            bool attention = running && probe != null && (probe.CurrentError || probe.CurrentNetworkFailure || probe.CurrentTimedOut || (success && probe.CurrentMs.Value >= (key == "gateway" ? 20 : 150)));
            values[key].Foreground = Brush(attention ? AmberColor : success ? AccentColor : MutedColor);
            values[key + "Detail"].Text = !running ? "검사 중지" : probe == null ? "ICMP · 검사 대기" : (probe.Sent == 0 ? "무응답률 표본 없음" : "최근 60초 무응답 " + (100.0 * probe.Missing / probe.Sent).ToString("N1") + "% · " + probe.Missing + "/" + probe.Sent + "회") + "\n경로 실패 " + probe.NetworkFailures + "회 · 검사 오류 " + probe.Errors + "회";
            values[key + "Target"].Text = probe == null ? key == "reference" ? DefaultReferenceTarget : "선택 연결의 기본 게이트웨이" : probe.Target;
            values[key].ToolTip = (probe == null ? "" : probe.Status + "\n") + "ICMP 응답 검사이며 전체 데이터 패킷 유실률이 아닙니다. 무응답률은 최근 60초의 시간 초과 ÷ (응답+시간 초과), 경로 실패·검사 오류는 분모에서 제외하고 따로 표시합니다.\n" + RouteNote;
        }
        private void ShowInternet(string key, InternetCheckSample sample)
        {
            bool measured = sample != null && running && sample.State == "success" && sample.Success; string state = sample == null ? "waiting" : sample.State;
            bool failed = running && (state == "failure" || state == "timeout" || state == "error");
            values[key].Text = !running ? "—" : measured ? key == "dns" ? "조회 성공" : "접속 성공" : state == "checking" ? "검사 중" : state == "stale" ? "최근 값 없음" : state == "timeout" ? "시간 초과" : state == "error" ? "검사 오류" : state == "failure" ? key == "dns" ? "조회 실패" : "접속 실패" : "검사 대기";
            values[key].Foreground = Brush(failed ? AmberColor : measured ? AccentColor : MutedColor);
            values[key + "Detail"].Text = !running ? "검사 중지" : sample == null ? "약 5초마다 자동 확인" : (sample.DurationMs.HasValue ? Ms(sample.DurationMs.Value) + " · " : "") + (key == "dns" ? "시스템 DNS · 캐시 포함 가능" : sample.HttpStatusCode.HasValue ? "HTTP " + sample.HttpStatusCode.Value : sample.Status);
            Uri target; values[key + "Target"].Text = sample == null ? key == "dns" ? DefaultDnsHost : new Uri(DefaultHttpsUrl).Host : key == "https" && Uri.TryCreate(sample.Target, UriKind.Absolute, out target) ? target.Host : sample.Target;
            values[key].ToolTip = (sample == null ? "" : sample.Target + "\n" + sample.Status + "\n" + sample.Error + "\n") + (key == "dns" ? "Windows 시스템 이름 조회이며 캐시·hosts 결과를 포함할 수 있습니다. 표시 시간은 DNS 서버만의 순수 왕복 시간이 아닙니다." : "해당 HTTPS 주소의 예상 HTTP 204 응답을 확인합니다. 시간에는 이름 조회·TCP·TLS·응답 처리가 포함됩니다. 프록시·인증서·대상 서버 제한으로도 실패할 수 있습니다.") + "\n" + RouteNote;
        }
        private void RefreshHistory()
        {
            historyDirty = false; IList<ConnectivityIncident> current = history.GetRecent(500);
            HashSet<string> currentIds = new HashSet<string>(current.Select(x => x.Id));
            List<ConnectivityIncident> shown = current.Concat(archived.Where(x => !currentIds.Contains(x.Id))).OrderByDescending(x => x.StartedUtc).Take(100).ToList();
            historyCount.Text = "최근 " + shown.Count + "건 · 이전 기록 포함" + (history.DroppedCount > 0 ? " · 한도 초과 " + history.DroppedCount : ""); historyCount.ToolTip = "화면은 최근 100건, 이번 실행의 메모리 이력은 최대 500건입니다. 이전 기록은 최근 50개 세션의 제한된 끝부분에서 읽습니다. 전체 원본은 기록 폴더에 보관합니다."; historyList.Items.Clear();
            if (shown.Count == 0) { historyList.Items.Add(Text("기록된 이상이 없습니다. 검사 실패와 회복 시각을 여기에 표시합니다.", 12, MutedColor, false)); return; }
            foreach (ConnectivityIncident item in shown)
            {
                string state = item.State == "recovered" ? item.Category == "check_error" ? "검사 가능 확인" : "회복 확인" : item.State == "open" ? "계속 관측 중" : "관측 종료 · 회복 미확인";
                Grid row = new Grid { Margin = new Thickness(3, 5, 3, 5), ToolTip = item.Summary + "\n대상: " + item.Target + "\n" + item.EndReason }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(138) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(185) });
                row.Children.Add(Text(item.StartedUtc.ToLocalTime().ToString("MM-dd HH:mm:ss"), 11, MutedColor, false)); TextBlock description = Text((item.Category == "check_error" ? "검사 자체 오류 · " : "") + item.Summary, 12, TextColor, false); description.Margin = new Thickness(0, 0, 10, 0); Grid.SetColumn(description, 1); row.Children.Add(description);
                string ended = item.RecoveredUtc.HasValue ? " · " + item.RecoveredUtc.Value.ToLocalTime().ToString("HH:mm:ss") : "";
                TextBlock outcome = Text(state + ended + "\n관측 " + item.DurationSeconds.ToString("N0") + "초 · 실패 " + item.Failures + "회", 11, item.State == "open" ? AmberColor : item.State == "recovered" ? AccentColor : MutedColor, false); Grid.SetColumn(outcome, 2); row.Children.Add(outcome); historyList.Items.Add(row);
            }
        }
    }
}
