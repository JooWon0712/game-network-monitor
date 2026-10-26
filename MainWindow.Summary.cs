using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace GameNetworkMonitor
{
    public sealed partial class MainWindow
    {
        private readonly TextBlock selectionSummary = new TextBlock();
        private readonly TextBlock overviewTitle = new TextBlock();
        private readonly TextBlock overviewDetail = new TextBlock();
        private readonly TextBlock overviewScope = new TextBlock();
        private readonly TextBlock connectionAddress = new TextBlock();
        private readonly TextBlock programHeadline = new TextBlock();
        private readonly TextBlock programDetail = new TextBlock();
        private readonly ProgramUsageTotals programTotals = new ProgramUsageTotals();
        private readonly Expander setupExpander = new Expander();
        private List<ProgramUsageView> displayedPrograms = new List<ProgramUsageView>();
        private string recordedSelectionLabel;

        private UIElement BuildWindow()
        {
            Grid main = new Grid { Margin = new Thickness(24, 18, 24, 12), Background = Brush(BackColor) };
            for (int i = 0; i < 6; i++) main.RowDefinitions.Add(new RowDefinition { Height = i == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel heading = new StackPanel();
            heading.Children.Add(Text("GAME NETWORK MONITOR", 11, AccentColor, true));
            TextBlock title = Text("게임 네트워크 모니터", 25, TextColor, true); title.Margin = new Thickness(0, 3, 0, 3); heading.Children.Add(title);
            PrepareText(selectionSummary, 12, MutedColor); heading.Children.Add(selectionSummary); header.Children.Add(heading);
            TextBlock version = Text("v" + AppVersion + " · " + (administrator ? "관리자 모드" : "일반 모드"), 12, MutedColor, false);
            version.VerticalAlignment = VerticalAlignment.Center;
            StackPanel headerActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            headerActions.Children.Add(version); logFolderButton.Content = "기록 폴더"; logFolderButton.Margin = new Thickness(14, 0, 0, 0);
            logFolderButton.ToolTip = "전체 측정 로그와 자동 분석 요약을 저장한 폴더를 엽니다."; headerActions.Children.Add(logFolderButton);
            Grid.SetColumn(headerActions, 1); header.Children.Add(headerActions); main.Children.Add(header);

            Grid form = new Grid { Margin = new Thickness(0, 10, 0, 12) };
            for (int i = 0; i < 4; i++) form.ColumnDefinitions.Add(new ColumnDefinition());
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddField(form, "게임 / 프로세스", processPicker, 0, 0); AddField(form, "이 PC의 연결 · 랜선 / Wi-Fi", adapterPicker, 0, 1);
            AddField(form, "공유기 주소 · 비우면 검사 끔", gatewayInput, 0, 2); AddField(form, "인터넷 비교 검사 대상", referenceInput, 0, 3);
            referenceInput.Text = DefaultReferenceTarget;
            refreshButton.Content = "목록 갱신"; refreshButton.Margin = new Thickness(0, 22, 0, 0); Grid.SetColumn(refreshButton, 4); form.Children.Add(refreshButton);
            PrepareText(statusLine, 12, MutedColor); statusLine.Margin = new Thickness(0, 10, 0, 0); Grid.SetRow(statusLine, 1); Grid.SetColumnSpan(statusLine, 5); form.Children.Add(statusLine);
            setupExpander.Header = "측정 대상 설정 · 게임 / PC 연결 / 검사 주소"; setupExpander.Content = form;
            setupExpander.Foreground = Brush(MutedColor); setupExpander.Margin = new Thickness(0, 0, 0, 12);
            Grid.SetRow(setupExpander, 1); main.Children.Add(setupExpander);

            recordingBanner.CornerRadius = new CornerRadius(7); recordingBanner.BorderThickness = new Thickness(4, 1, 1, 1);
            recordingBanner.Padding = new Thickness(15, 10, 15, 10); recordingBanner.Margin = new Thickness(0, 0, 0, 10);
            Grid recordingLayout = new Grid(); recordingLayout.ColumnDefinitions.Add(new ColumnDefinition()); recordingLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel recordingCopy = new StackPanel(); PrepareText(recordingStateTitle, 19, TextColor); recordingStateTitle.FontWeight = FontWeights.SemiBold;
            PrepareText(recordingStateDetail, 12, MutedColor); recordingStateDetail.Margin = new Thickness(0, 4, 12, 0);
            recordingCopy.Children.Add(recordingStateTitle); recordingCopy.Children.Add(recordingStateDetail); recordingLayout.Children.Add(recordingCopy);
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            startButton.Content = "기록 시작"; startButton.Background = Brush(AccentColor); startButton.Foreground = Brush(BackColor);
            stopButton.Content = "기록 중지"; actions.Children.Add(startButton); actions.Children.Add(stopButton);
            Grid.SetColumn(actions, 1); recordingLayout.Children.Add(actions); recordingBanner.Child = recordingLayout;
            permissionBanner.Background = Brush(Color.FromRgb(52, 38, 25)); permissionBanner.BorderBrush = Brush(AmberColor);
            permissionBanner.BorderThickness = new Thickness(1); permissionBanner.CornerRadius = new CornerRadius(7); permissionBanner.Padding = new Thickness(12);
            permissionBanner.Margin = new Thickness(0, 0, 0, 10); permissionBanner.Visibility = Visibility.Collapsed;
            Grid permission = new Grid(); permission.ColumnDefinitions.Add(new ColumnDefinition()); permission.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            PrepareText(permissionMessage, 12, AmberColor); permissionMessage.Margin = new Thickness(0, 0, 12, 0); permission.Children.Add(permissionMessage);
            administratorButton.Content = "관리자 모드 열기"; administratorButton.Foreground = Brush(AmberColor);
            administratorButton.ToolTip = "선택 값을 유지한 새 창을 엽니다. UAC 승인 후 기록 시작을 직접 누르세요.";
            Grid.SetColumn(administratorButton, 1); permission.Children.Add(administratorButton); permissionBanner.Child = permission;
            StackPanel notices = new StackPanel(); notices.Children.Add(recordingBanner); notices.Children.Add(permissionBanner); Grid.SetRow(notices, 2); main.Children.Add(notices);

            UIElement overview = BuildDashboard(); Grid.SetRow(overview, 3); main.Children.Add(overview);
            tabs.Background = Brush(BackColor); tabs.BorderThickness = new Thickness(0); tabs.Padding = new Thickness(0); tabs.Foreground = Brush(TextColor);
            tabs.Items.Add(new TabItem { Header = "게임 연결", Content = BuildEndpoints() });
            tabs.Items.Add(new TabItem { Header = "프로그램 사용량", Content = BuildProcessTraffic() });
            Grid.SetRow(tabs, 4); main.Children.Add(tabs);
            TextBlock footer = Text("이 PC에서만 기록 · 패킷 내용 수집 없음 · 실제 게임 패킷 손실률과 FPS는 측정하지 않음", 11, MutedColor, false);
            footer.TextWrapping = TextWrapping.Wrap; footer.Margin = new Thickness(0, 9, 0, 0); Grid.SetRow(footer, 5); main.Children.Add(footer);
            ScrollViewer windowScroll = new ScrollViewer { Content = main, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            main.LayoutUpdated += delegate {
                double automaticHeight = 0;
                for (int i = 0; i < main.RowDefinitions.Count; i++) if (i != 4) automaticHeight += main.RowDefinitions[i].ActualHeight;
                double required = Math.Max(700, automaticHeight + 220);
                double height = Math.Max(required, windowScroll.ActualHeight - main.Margin.Top - main.Margin.Bottom);
                if (Double.IsNaN(main.Height) || Math.Abs(main.Height - height) > 1) main.Height = height;
            };
            RefreshAnalysisPresentation(); return windowScroll;
        }

        private UIElement BuildDashboard()
        {
            StackPanel body = new StackPanel { Margin = new Thickness(0, 1, 0, 12) };
            PrepareText(overviewTitle, 17, TextColor); overviewTitle.FontWeight = FontWeights.SemiBold; body.Children.Add(overviewTitle);
            PrepareText(overviewDetail, 12, MutedColor); overviewDetail.Margin = new Thickness(0, 4, 0, 4); body.Children.Add(overviewDetail);
            PrepareText(overviewScope, 11, MutedColor); overviewScope.Margin = new Thickness(0, 0, 0, 10); body.Children.Add(overviewScope);
            Grid metrics = new Grid(); for (int i = 0; i < 3; i++) metrics.ColumnDefinitions.Add(new ColumnDefinition());
            SummaryMetric(metrics, 0, "내 PC ↔ 공유기", "summaryGateway", "summaryGatewayTiming");
            SummaryMetric(metrics, 1, "인터넷 비교 검사", "summaryReference", "summaryReferenceTiming");
            SummaryMetric(metrics, 2, "기록 반영 지연 · 수집 상태", "summaryLag", "summaryLagDetail");
            body.Children.Add(metrics); return body;
        }

        private void SummaryMetric(Grid grid, int column, string label, string key, string detailKey)
        {
            StackPanel body = new StackPanel(); body.Children.Add(Text(label, 12, MutedColor, false));
            TextBlock metric = Text("측정값 없음", 22, TextColor, true); metric.Margin = new Thickness(0, 4, 0, 4); values.Add(key, metric); body.Children.Add(metric);
            TextBlock detail = Text("기록 시작 후 표시", 11, MutedColor, false); detail.TextWrapping = TextWrapping.Wrap; values.Add(detailKey, detail); body.Children.Add(detail);
            Border shell = Surface(body); shell.Padding = new Thickness(14, 11, 14, 11); shell.Margin = new Thickness(column == 0 ? 0 : 6, 0, column == 2 ? 0 : 6, 0);
            Grid.SetColumn(shell, column); grid.Children.Add(shell);
        }

        private UIElement BuildEndpoints()
        {
            StackPanel body = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            body.Children.Add(Heading("게임은 어디와 통신하나요?", "연결 확인용 검사와 게임 통신량을 구분해서 볼 수 있어요."));
            Grid pair = new Grid(); pair.ColumnDefinitions.Add(new ColumnDefinition()); pair.ColumnDefinitions.Add(new ColumnDefinition());
            StackPanel game = DetailCard(pair, 0, "선택한 게임의 통신", "최근 약 2초 · 수집기에 도착한 이벤트 기준");
            game.Children.Add(TwoMetrics("받는 데이터", "gameRx", "보내는 데이터", "gameTx"));
            PrepareText(gameStatus, 12, MutedColor); gameStatus.Margin = new Thickness(0, 10, 0, 7); game.Children.Add(gameStatus);
            game.Children.Add(SmallRow("마지막 통신이 도착한 뒤", "receiveIdle"));
            game.Children.Add(SmallRow("TCP 재전송 · 현재 측정 누적", "retransmits"));
            PrepareText(connectionAddress, 12, TextColor); connectionAddress.Margin = new Thickness(0, 8, 0, 0); game.Children.Add(connectionAddress);
            game.Children.Add(Paragraph("관측한 주소만으로 게임 서버의 역할·위치나 실제 패킷 손실률은 알 수 없습니다.", 11));
            StackPanel pc = DetailCard(pair, 1, "이 PC의 데이터 사용량", "선택한 랜선·Wi-Fi 연결 · 모든 프로그램 합계");
            pc.Children.Add(TwoMetrics("받는 데이터", "pcRx", "보내는 데이터", "pcTx"));
            PrepareText(adapterStatus, 12, MutedColor); adapterStatus.Margin = new Thickness(0, 10, 0, 7); pc.Children.Add(adapterStatus);
            pc.Children.Add(SmallRow("처리 오류 · 받기 / 보내기", "pcErrors")); pc.Children.Add(SmallRow("버린 패킷 · 받기 / 보내기", "pcDiscards"));
            pc.Children.Add(Paragraph("오류·버린 패킷은 측정 시작 이후 누적입니다. 공유기의 다른 기기 사용량은 포함하지 않습니다.", 11)); body.Children.Add(pair);
            values["receiveIdle"].ToolTip = "게임 수신 이벤트가 수집기에 마지막으로 도착한 뒤 경과한 시간입니다. 메뉴·대기·수집 지연도 영향을 주며 게임 끊김을 확정하지 않습니다.";
            values["pcErrors"].ToolTip = values["pcDiscards"].ToolTip = "선택한 PC 어댑터의 시작 시점 대비 증가량이며 게임별 손실 수치가 아닙니다.";

            StackPanel technical = new StackPanel();
            Grid inspections = new Grid(); inspections.ColumnDefinitions.Add(new ColumnDefinition()); inspections.ColumnDefinitions.Add(new ColumnDefinition());
            StackPanel gateway = DetailCard(inspections, 0, "공유기까지 확인 요청", "최근 60초 · 내 PC ↔ 공유기 검사");
            gateway.Children.Add(TwoMetrics("현재 응답 시간", "gatewayRtt", "답이 없던 비율", "gatewayMissing"));
            PrepareText(gatewayStatus, 12, MutedColor); gatewayStatus.Margin = new Thickness(0, 8, 0, 8); gateway.Children.Add(gatewayStatus);
            gateway.Children.Add(SmallRow("평균 응답 시간", "gatewayAverage"));
            gateway.Children.Add(SmallRow("연속 응답 간 시간 차이 평균", "gatewayVariation"));
            gateway.Children.Add(SmallRow("답 없음 / 검사 횟수", "gatewayCount")); gateway.Children.Add(SmallRow("검사 자체 오류", "gatewayErrors"));
            StackPanel capture = DetailCard(inspections, 1, "통신을 기록하는 과정", "수집 상태 · 게임 서버 핑과 다름");
            capture.Children.Add(SmallRow("게임 받기 / 보내기 이벤트", "gameEvents")); capture.Children.Add(SmallRow("기록 반영 지연 · 평균 / 최대", "deliveryLag")); capture.Children.Add(SmallRow("PC 초당 패킷 · 받기 / 보내기", "pcPackets"));
            PrepareText(traceWarning, 11, MutedColor); traceWarning.Margin = new Thickness(0, 7, 0, 7); capture.Children.Add(traceWarning);
            PrepareText(referenceStatus, 11, MutedColor); capture.Children.Add(referenceStatus); technical.Children.Add(inspections);
            PrepareText(endpointStatus, 11, MutedColor); endpointStatus.Margin = new Thickness(0, 14, 0, 7); endpointStatus.Text = "측정을 시작하면 관측 주소가 표시됩니다."; technical.Children.Add(endpointStatus);
            ConfigureGrid(endpointGrid); endpointGrid.Height = 225;
            AddColumn("방식", "Protocol", 65, null); AddColumn("관측 주소 A", "Local", 210, null); AddColumn("관측 주소 B", "Remote", 210, null);
            AddColumn("보내기 · 바이트", "SentBytes", 125, "N0"); AddColumn("받기 · 바이트", "ReceivedBytes", 125, "N0"); AddColumn("마지막 관측", "LastSeen", 110, "HH:mm:ss");
            technical.Children.Add(endpointGrid); technical.Children.Add(Paragraph("주소는 역할을 확정하지 않은 관측값입니다. 일부 TCP 기록은 방향 확인이 필요합니다. 누적량은 해당 연결을 추적한 구간 기준이며 동시에 연결된 서버 수와 다릅니다.", 11));
            body.Children.Add(Disclosure("기술 정보 · 관측 주소 / 최근 60초 검사 / 수집 상태", technical));
            Grid charts = new Grid(); charts.ColumnDefinitions.Add(new ColumnDefinition()); charts.ColumnDefinitions.Add(new ColumnDefinition());
            AddChart(charts, "게임 데이터 사용량", "최근 120개 표본 · KiB/s · 받기 청록 / 보내기 보라", trafficChart, 0);
            AddChart(charts, "공유기 응답 시간", "최근 120개 표본 · ms · 무응답 구간은 연결하지 않음", gatewayChart, 1);
            body.Children.Add(Disclosure("실시간 그래프 · 중지하면 마지막 기록 유지", charts));
            StackPanel recordInfo = new StackPanel(); PrepareText(sessionStatus, 12, MutedColor); recordInfo.Children.Add(sessionStatus);
            PrepareText(recordingStatus, 12, MutedColor); recordingStatus.Margin = new Thickness(0, 6, 0, 0); recordInfo.Children.Add(recordingStatus);
            recordInfo.Children.Add(Paragraph("측정 중 모든 표본과 자동 분석 요약을 파일로 남깁니다. 시간별 진단 탭을 열거나 끊김 시점을 직접 표시할 필요는 없습니다.", 12));
            exportButton.Content = "최근 표본 CSV 사본 저장"; exportButton.Margin = new Thickness(0, 10, 0, 0); exportButton.HorizontalAlignment = HorizontalAlignment.Left;
            exportButton.ToolTip = "최근 표본의 별도 사본입니다. 전체 로그는 상단의 기록 폴더에 이미 자동 저장됩니다."; recordInfo.Children.Add(exportButton);
            body.Children.Add(Disclosure("기록 정보 · 전체 로그와 자동 요약은 계속 저장", recordInfo)); return Scroll(body);
        }

        private UIElement BuildProcessTraffic()
        {
            Grid panel = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); panel.RowDefinitions.Add(new RowDefinition());
            StackPanel intro = new StackPanel();
            intro.Children.Add(Heading("어떤 프로그램이 많이 보냈나요?", "이번 측정을 시작한 뒤의 누적 보내기입니다. 같은 이름의 프로그램은 합쳐서 표시해요."));
            PrepareText(programHeadline, 16, TextColor); PrepareText(programDetail, 12, MutedColor);
            programHeadline.Text = "프로그램별 통신을 기다리고 있어요"; programDetail.Text = "기록 시작 후 보낸 데이터가 있는 프로그램을 표시합니다.";
            StackPanel explanation = new StackPanel(); explanation.Children.Add(programHeadline); programDetail.Margin = new Thickness(0, 6, 0, 0); explanation.Children.Add(programDetail);
            Border highlight = Surface(explanation); highlight.Margin = new Thickness(0, 0, 0, 12); intro.Children.Add(highlight);
            PrepareText(processTrafficStatus, 11, MutedColor); intro.Children.Add(processTrafficStatus);
            TextBlock scope = Paragraph("이 PC의 모든 어댑터에서 관측한 양 · 누적 보내기가 큰 순서 · 실제 인터넷 업로드량이나 게임 지연의 원인을 뜻하지 않습니다.", 11);
            scope.Margin = new Thickness(0, 8, 0, 12); intro.Children.Add(scope); panel.Children.Add(intro);
            ConfigureGrid(processTrafficGrid);
            processTrafficGrid.Columns.Add(new DataGridTextColumn { Header = "프로그램", Binding = new Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 250 });
            processTrafficGrid.Columns.Add(new DataGridTextColumn { Header = "누적 보내기", Binding = new Binding("SentDisplay"), SortMemberPath = "SentBytes", Width = new DataGridLength(190) });
            Style row = new Style(typeof(DataGridRow)); row.Setters.Add(new Setter(Control.ToolTipProperty, new Binding("ScopeNote"))); processTrafficGrid.RowStyle = row;
            Grid.SetRow(processTrafficGrid, 1); panel.Children.Add(processTrafficGrid); return panel;
        }

        private void RefreshAnalysisPresentation()
        {
            if (!values.ContainsKey("summaryGateway")) return;
            AnalysisOverview data = analysis == null ? null : analysis.GetOverview();
            if (data == null || (data.Samples == 0 && data.Gateway.Total == 0 && data.Reference.Total == 0))
            {
                overviewTitle.Text = running ? "검사 결과를 모으고 있어요" : "기록을 시작하면 연결 상태를 요약해요";
                overviewTitle.Foreground = Brush(TextColor);
                overviewDetail.Text = "공유기와 인터넷 비교 검사는 게임 서버의 핑·패킷 손실률과 다릅니다.";
                overviewScope.Text = "현재 측정 전체 기준 · 아직 검사 결과 없음";
                foreach (string key in new[] { "summaryGateway", "summaryReference", "summaryLag" }) values[key].Text = "측정값 없음";
                values["summaryGatewayTiming"].Text = values["summaryReferenceTiming"].Text = "기록 시작 후 표시"; values["summaryLagDetail"].Text = "게임 서버 핑과 다름";
                return;
            }
            SetProbeMetric(data.Gateway, "summaryGateway", "summaryGatewayTiming"); SetProbeMetric(data.Reference, "summaryReference", "summaryReferenceTiming");
            long misses = data.Gateway.Timeouts + data.Reference.Timeouts, high = data.Gateway.HighResponses + data.Reference.HighResponses, errors = data.Gateway.Errors + data.Reference.Errors;
            bool bothMeasured = data.Gateway.Successes > 0 && data.Reference.Successes > 0;
            overviewTitle.Text = misses > 0 || high > 0 ? "응답 누락이나 지연이 관측됐어요" : errors > 0 ? "연결 검사 일부를 확인하지 못했어요" : data.NetworkCandidateCount > 0 ? "PC 네트워크에서 확인할 항목이 있어요" : bothMeasured ? "연결 응답 검사에서 이상을 찾지 못했어요" : "검사 결과를 더 확인하고 있어요";
            overviewTitle.Foreground = Brush(misses > 0 || high > 0 || errors > 0 ? AmberColor : TextColor);
            overviewDetail.Text = "공유기 + 인터넷 비교 · 응답 누락 " + misses.ToString("N0") + "회 · 높은 응답 시간 " + high.ToString("N0") + "회 · 검사 자체 오류 " + errors.ToString("N0") + "회";
            overviewDetail.ToolTip = "높은 응답 시간의 후보 기준은 공유기 20ms 이상, 인터넷 비교 대상 150ms 이상입니다. 게임 서버 핑이나 장애 확정 기준이 아닙니다.";
            overviewScope.Text = "현재 측정 전체 기준 · " + (running ? "기록 중 · 약 10초마다 요약 갱신" : "기록 중지 · 마지막 결과 유지") + " · 실제 게임 끊김 여부는 별도 확인 필요";
            values["summaryLag"].Text = data.MaxDeliveryLagMs.HasValue ? "최대 " + (data.MaxDeliveryLagMs.Value / 1000).ToString("N2") + "초" : "측정값 없음";
            values["summaryLagDetail"].Text = "기록이 앱에 도착하는 시간 · 게임 핑과 다름";
        }

        private void SetProbeMetric(AnalysisProbeOverview probe, string key, string detailKey)
        {
            long checks = probe.Successes + probe.Timeouts;
            values[key].Text = checks > 0 ? "응답 누락 " + probe.Timeouts.ToString("N0") + "회" : probe.Errors > 0 ? "검사 오류 " + probe.Errors.ToString("N0") + "회" : "측정값 없음";
            values[detailKey].Text = probe.AverageMs.HasValue ? "평균 " + probe.AverageMs.Value.ToString("N2") + "ms · 최대 " + probe.MaximumMs.Value.ToString("N0") + "ms · " + checks.ToString("N0") + "회 검사" : "응답 없음 · " + (String.IsNullOrEmpty(probe.Target) ? "검사 대기 또는 꺼짐" : probe.Target);
            values[key].ToolTip = "현재 측정의 전체 원시 ICMP 검사 기준입니다. 정상 응답과 시간 초과가 검사 횟수에 포함되고, 검사 자체 오류는 별도로 표시합니다.";
        }

        private void UpdateConnectionPresentation(MonitorSnapshot snapshot)
        {
            EndpointRow endpoint = snapshot.Endpoints.FirstOrDefault(x => String.Equals(x.Protocol, "UDP", StringComparison.OrdinalIgnoreCase));
            connectionAddress.Text = endpoint == null ? "UDP 관측 주소를 기다리는 중 · 서버 역할 미확인" : "관측 UDP 주소 · " + endpoint.Remote + "\n이 연결 보내기 " + Bytes(endpoint.SentBytes) + " / 받기 " + Bytes(endpoint.ReceivedBytes);
        }

        private void ResetMeasurementPresentation()
        {
            // Clear the previous result before starting collectors, including startup-failure paths.
            session.Clear(); eventList.Items.Clear(); previousTraceStatus = null; endpointGrid.ItemsSource = null;
            trafficChart.Clear(); gatewayChart.Clear(); began = DateTime.Now;
            foreach (KeyValuePair<string, TextBlock> value in values) if (!value.Key.StartsWith("summary", StringComparison.Ordinal)) value.Value.Text = "—";
            programTotals.Clear(); displayedPrograms = new List<ProgramUsageView>(); processTrafficGrid.ItemsSource = displayedPrograms;
            connectionAddress.Text = "UDP 관측 주소를 기다리는 중 · 서버 역할 미확인";
            endpointStatus.Text = "새 측정의 통신 주소를 기다립니다.";
            programHeadline.Text = "프로그램별 통신을 기다리고 있어요";
            programDetail.Text = "이번 측정에서 통신이 관측된 프로그램만 표시합니다.";
            processTrafficStatus.Text = "새 측정의 프로그램 수집 대기";
            gameStatus.Text = "새 측정의 게임 통신 대기"; adapterStatus.Text = "선택한 PC 연결의 첫 표본 대기";
            gatewayStatus.Text = "공유기 응답 대기"; referenceStatus.Text = "인터넷 비교 검사 대기"; traceWarning.Text = "이번 측정의 수집 상태를 기다립니다.";
        }

        // Deterministic presentation fixtures: no probes, trace, log writer or UAC.
        private void SummaryPresentationTest(string directory)
        {
            SessionAnalysis original = analysis; analysis = new SessionAnalysis();
            DateTime at = DateTime.UtcNow.AddMinutes(-1);
            for (int i = 0; i < 4; i++)
            {
                analysis.ObserveProbe("gateway", new ProbeSample { TimestampUtc = at.AddSeconds(i), Target = "192.0.2.1", Countable = true, RoundtripMs = i });
                analysis.ObserveProbe("reference", new ProbeSample { TimestampUtc = at.AddSeconds(i), Target = "198.51.100.1", Countable = true, RoundtripMs = 3 + i });
            }
            analysis.Observe(new MonitorSnapshot { TimestampUtc = at.AddSeconds(4), Running = true, ProcessId = 12345, ProcessName = "UI 검사 예시", GameCollectionRequested = true,
                Trace = new TraceHealth { Active = true }, GameRatesAvailable = true, AdapterCountersAvailable = true, AdapterRatesAvailable = true,
                GameReceivedEvents = 1, GameReceiveDeliveryIdleSeconds = 0.5, GameReceiveIdleSeconds = 2.5, GameMaxEventDeliveryLagMs = 3010 });
            RefreshAnalysisPresentation();
            if (!overviewTitle.Text.Contains("이상을 찾지 못") || !values["summaryGateway"].Text.Contains("0회") || !values["summaryLag"].Text.Contains("3.01") ||
                analysis.GetOverview().NetworkCandidateCount != 0 || analysis.GetOverview().CollectionCandidateCount == 0)
                throw new InvalidOperationException("Summary misclassified collector delay as a connection problem.");
            recordingStateTitle.Text = "UI 검사 예시 · 실제 측정 아님"; tabs.SelectedIndex = 0;
            RenderTestSurface(System.IO.Path.Combine(directory, "summary-example.png"));
            analysis.ObserveProbe("reference", new ProbeSample { TimestampUtc = at.AddSeconds(5), Target = "198.51.100.1", Status = "UI 검사 오류", Countable = false });
            RefreshAnalysisPresentation();
            if (!overviewTitle.Text.Contains("확인하지 못")) throw new InvalidOperationException("Probe error incorrectly displayed as healthy.");
            analysis.ObserveProbe("gateway", new ProbeSample { TimestampUtc = at.AddSeconds(5), Target = "192.0.2.1", Countable = true, TimedOut = true });
            RefreshAnalysisPresentation();
            if (!overviewTitle.Text.Contains("누락이나 지연")) throw new InvalidOperationException("Single probe timeout missing from whole-session verdict.");
            analysis = original; RefreshAnalysisPresentation(); RefreshRecordingIndicators();
        }

        private void UpdateProgramPresentation(ProcessTrafficSnapshot traffic)
        {
            programTotals.Observe(traffic);
            displayedPrograms = programTotals.Snapshot().Select(x => new ProgramUsageView { Name = x.Name, SentBytes = x.SentBytes, ScopeNote = x.ScopeNote }).ToList();
            processTrafficGrid.ItemsSource = displayedPrograms; ProgramUsageView top = displayedPrograms.FirstOrDefault();
            programHeadline.Text = top == null ? "프로그램별 통신을 기다리고 있어요" : "누적 보내기가 가장 큰 프로그램 · " + top.Name;
            programDetail.Text = top == null ? "이번 측정에서 보낸 데이터가 관측되면 표시합니다. 관리자 권한과 수집 상태를 확인하세요." : "이번 측정 누적 " + top.SentDisplay + " · 모든 어댑터 관측량\n통신량만으로 게임 지연의 원인이라고 판단하지 않습니다.";
        }

        private static void PrepareText(TextBlock text, double size, Color color) { text.FontSize = size; text.Foreground = Brush(color); text.TextWrapping = TextWrapping.Wrap; }
        private static TextBlock Paragraph(string content, double size) { TextBlock text = Text(content, size, MutedColor, false); text.TextWrapping = TextWrapping.Wrap; text.Margin = new Thickness(0, 6, 0, 0); return text; }
        private static StackPanel Heading(string title, string detail) { StackPanel panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) }; TextBlock text = Text(title, 17, TextColor, true); text.TextWrapping = TextWrapping.Wrap; panel.Children.Add(text); panel.Children.Add(Paragraph(detail, 12)); return panel; }
        private static Border Surface(UIElement content) { return new Border { Background = Brush(PanelColor), BorderBrush = Brush(BorderColor), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(14), Child = content }; }
        private static ScrollViewer Scroll(UIElement content) { return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; }
        private static Expander Disclosure(string title, UIElement content) { return new Expander { Header = title, Content = content, IsExpanded = false, Foreground = Brush(MutedColor), Margin = new Thickness(0, 14, 0, 0) }; }
        private static StackPanel DetailCard(Grid grid, int column, string title, string subtitle)
        {
            StackPanel panel = Heading(title, subtitle); Border surface = Surface(panel); surface.Margin = new Thickness(column == 0 ? 0 : 7, 0, column == 0 ? 7 : 0, 0); Grid.SetColumn(surface, column); grid.Children.Add(surface); return panel;
        }
        private static void ConfigureGrid(DataGrid grid)
        {
            grid.AutoGenerateColumns = false; grid.IsReadOnly = true; grid.CanUserAddRows = false; grid.CanUserDeleteRows = false;
            grid.HeadersVisibility = DataGridHeadersVisibility.Column; grid.Background = Brush(PanelColor); grid.Foreground = Brush(TextColor); grid.RowBackground = Brush(PanelColor);
            grid.AlternatingRowBackground = Brush(Color.FromRgb(21, 29, 39)); grid.RowHeight = 34; grid.BorderThickness = new Thickness(0); grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
            grid.HorizontalGridLinesBrush = Brush(BorderColor); grid.EnableRowVirtualization = true; grid.EnableColumnVirtualization = true;
            Style header = new Style(typeof(DataGridColumnHeader)); header.Setters.Add(new Setter(Control.BackgroundProperty, Brush(PanelColor))); header.Setters.Add(new Setter(Control.ForegroundProperty, Brush(MutedColor))); header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 10, 9, 10))); grid.ColumnHeaderStyle = header;
            Style cell = new Style(typeof(DataGridCell)); cell.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4))); cell.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0))); cell.Setters.Add(new Setter(Control.ForegroundProperty, Brush(TextColor))); grid.CellStyle = cell;
        }
        private static string Bytes(long value) { return value >= 1048576 ? (value / 1048576.0).ToString("N2") + " MiB" : (value / 1024.0).ToString("N1") + " KiB"; }
        private sealed class ProgramUsageView
        {
            public string Name { get; set; }
            public double SentBytes { get; set; }
            public string ScopeNote { get; set; }
            public string SentDisplay { get { return SentBytes >= 1073741824 ? (SentBytes / 1073741824).ToString("N2") + " GiB" : SentBytes >= 1048576 ? (SentBytes / 1048576).ToString("N2") + " MiB" : (SentBytes / 1024).ToString("N1") + " KiB"; } }
        }
    }
}
