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
        private void RefreshHistory()
        {
            historyDirty = false;
            IList<ConnectivityIncident> current = history.GetRecent(500);
            HashSet<string> currentIds = new HashSet<string>(current.Select(x => x.Id));
            List<ConnectivityIncident> shown = current.Concat(archived.Where(x => !currentIds.Contains(x.Id))).OrderByDescending(x => x.StartedUtc).Take(100).ToList();
            historyCount.Text = "최근 " + shown.Count + "건 · 이전 기록 포함" + (history.DroppedCount > 0 ? " · 한도 초과 " + history.DroppedCount : "");
            historyCount.ToolTip = "화면은 최근 100건, 이번 실행의 메모리 이력은 최대 500건입니다. 이전 기록은 최근 50개 세션의 제한된 끝부분에서 읽습니다. 전체 원본은 기록 폴더에 보관합니다. 이력이 없는 검사도 정상이라고 단정할 수 없습니다.";

            ScrollViewer scroll = HistoryScrollViewer(historyList);
            double offset = scroll == null ? 0 : scroll.VerticalOffset;
            string selectedId = HistoryItemId(historyList.SelectedItem), anchorId = null;
            double anchorTop = 0;
            if (scroll != null && offset > 0)
            {
                foreach (FrameworkElement row in historyList.Items)
                {
                    double top = row.TranslatePoint(new Point(), scroll).Y;
                    if (top + row.ActualHeight > 0 && top < scroll.ViewportHeight)
                    { anchorId = HistoryItemId(row); anchorTop = top; break; }
                }
            }
            if (shown.Count == 0)
            {
                historyList.Items.Clear();
                StackPanel empty = new StackPanel { Margin = new Thickness(8) };
                empty.Children.Add(Text("기록된 검사 실패가 없어요.", 13, TextColor, true));
                empty.Children.Add(Text("실패가 기록되면 무슨 일이 있었는지, 확인된 범위와 확인할 항목을 여기에 안내해요. 기록이 없다는 것만으로 모든 접속이 정상이라는 뜻은 아니에요.", 12, MutedColor, false));
                historyList.Items.Add(empty); return;
            }

            // Keep unchanged cards and the reader's place when another result arrives.
            if (historyList.Items.Count > 0 && HistoryItemId(historyList.Items[0]) == null) historyList.Items.Clear();
            for (int i = 0; i < shown.Count; i++)
            {
                ConnectivityIncident incident = shown[i];
                if (i < historyList.Items.Count && HistoryItemId(historyList.Items[i]) == incident.Id)
                {
                    ConnectivityIncident previous = (ConnectivityIncident)((FrameworkElement)historyList.Items[i]).Tag;
                    if (!SameHistoryContent(previous, incident)) historyList.Items[i] = BuildHistoryCard(incident);
                    continue;
                }
                int existing = -1;
                for (int j = i + 1; j < historyList.Items.Count; j++)
                    if (HistoryItemId(historyList.Items[j]) == incident.Id) { existing = j; break; }
                Border card = existing < 0 ? null : historyList.Items[existing] as Border;
                if (existing >= 0) historyList.Items.RemoveAt(existing);
                if (card == null || !SameHistoryContent((ConnectivityIncident)card.Tag, incident)) card = BuildHistoryCard(incident);
                historyList.Items.Insert(i, card);
            }
            while (historyList.Items.Count > shown.Count) historyList.Items.RemoveAt(historyList.Items.Count - 1);
            if (selectedId != null)
                historyList.SelectedItem = historyList.Items.Cast<FrameworkElement>().FirstOrDefault(x => HistoryItemId(x) == selectedId);
            historyList.UpdateLayout();
            scroll = HistoryScrollViewer(historyList);
            if (scroll != null)
            {
                FrameworkElement anchor = anchorId == null ? null : historyList.Items.Cast<FrameworkElement>().FirstOrDefault(x => HistoryItemId(x) == anchorId);
                scroll.ScrollToVerticalOffset(anchor == null ? offset : scroll.VerticalOffset + anchor.TranslatePoint(new Point(), scroll).Y - anchorTop);
            }
        }

        private Border BuildHistoryCard(ConnectivityIncident incident)
        {
            ConnectivityIncidentExplanation explanation = ConnectivityIncidentExplanation.Describe(incident);
            Color stateColor = incident.State == "open" ? AmberColor : incident.State == "recovered" && incident.RecoveredUtc.HasValue ? AccentColor : MutedColor;
            StackPanel body = new StackPanel();
            Grid heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock title = Text(explanation.Title, 14, TextColor, true); title.Margin = new Thickness(0, 0, 12, 0); heading.Children.Add(title);
            TextBlock state = Text(explanation.StateLabel, 11, stateColor, true); state.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(state, 1); heading.Children.Add(state); body.Children.Add(heading);
            TextBlock timing = Text(HistoryObservationTime(incident) + " · " + explanation.ObservationText + "\n" + explanation.StateDetail, 11, MutedColor, false); timing.Margin = new Thickness(0, 5, 0, 6); body.Children.Add(timing);
            body.Children.Add(HistoryGuidanceLine("무슨 일", explanation.WhatHappened, TextColor));
            body.Children.Add(HistoryGuidanceLine("확인된 범위", explanation.ConfirmedScope, LabelColor));
            body.Children.Add(HistoryGuidanceLine("확인할 것", explanation.NextStep, AccentColor));
            TextBlock target = Text(explanation.TargetLabel, 10, MutedColor, false); target.TextWrapping = TextWrapping.NoWrap; target.TextTrimming = TextTrimming.CharacterEllipsis; target.ToolTip = explanation.TargetLabel; target.Margin = new Thickness(0, 5, 0, 0); body.Children.Add(target);
            TextBlock limit = Text(explanation.LimitNote, 10, MutedColor, false); body.Children.Add(limit);
            if (!String.IsNullOrEmpty(explanation.EndReason) && incident.State != "recovered") body.Children.Add(Text("관측 종료 이유: " + explanation.EndReason, 10, MutedColor, false));
            return new Border { Tag = incident, BorderBrush = Brush(stateColor), BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(12, 8, 10, 8), Margin = new Thickness(3, 5, 3, 9), Child = body, ToolTip = "검사 대상: " + incident.Target + "\n" + incident.Summary };
        }

        private static Grid HistoryGuidanceLine(string label, string value, Color color)
        {
            Grid line = new Grid { Margin = new Thickness(0, 3, 0, 0) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) }); line.ColumnDefinitions.Add(new ColumnDefinition());
            line.Children.Add(Text(label, 11, LabelColor, true));
            TextBlock copy = Text(value, 12, color, false); Grid.SetColumn(copy, 1); line.Children.Add(copy); return line;
        }

        private static string HistoryObservationTime(ConnectivityIncident incident)
        {
            DateTime? end = incident.State == "recovered" ? incident.RecoveredUtc : incident.ClosedUtc;
            string start = incident.Category == "check_error" || incident.Category == "error" ? "첫 검사 오류 " : "첫 실패 ";
            return start + incident.StartedUtc.ToLocalTime().ToString("MM-dd HH:mm:ss") + (end.HasValue ? " → " + end.Value.ToLocalTime().ToString(end.Value.ToLocalTime().Date == incident.StartedUtc.ToLocalTime().Date ? "HH:mm:ss" : "MM-dd HH:mm:ss") : "부터");
        }

        private static bool SameHistoryContent(ConnectivityIncident first, ConnectivityIncident second)
        {
            return first.Version == second.Version && first.State == second.State && first.Kind == second.Kind && first.Category == second.Category && first.Target == second.Target && first.Failures == second.Failures && first.StartedUtc == second.StartedUtc && first.LastObservedUtc == second.LastObservedUtc && first.RecoveredUtc == second.RecoveredUtc && first.ClosedUtc == second.ClosedUtc && first.EndReason == second.EndReason && first.Summary == second.Summary;
        }

        private static string HistoryItemId(object value)
        {
            FrameworkElement row = value as FrameworkElement;
            ConnectivityIncident incident = row == null ? null : row.Tag as ConnectivityIncident;
            return incident == null ? null : incident.Id;
        }

        private static ScrollViewer HistoryScrollViewer(DependencyObject root)
        {
            ScrollViewer result = root as ScrollViewer;
            if (result != null) return result;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            { result = HistoryScrollViewer(VisualTreeHelper.GetChild(root, i)); if (result != null) return result; }
            return null;
        }
    }
}
