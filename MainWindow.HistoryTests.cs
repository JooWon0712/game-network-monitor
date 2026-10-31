using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PcInternetMonitor
{
    public sealed partial class MainWindow
    {
        private void HistoryGuidancePresentationTest(string directory, DateTime at)
        {
            List<ConnectivityIncident> saved = archived;
            try
            {
                archived = new List<ConnectivityIncident>();
                for (int i = 0; i < 7; i++)
                {
                    string kind = i == 0 ? "reference" : i == 1 ? "gateway" : i == 2 ? "dns" : i == 3 ? "https" : i == 4 ? "adapter" : "reference";
                    archived.Add(new ConnectivityIncident {
                        Id = "guidance-" + i, Version = 1, Kind = kind, Category = i == 5 ? "check_error" : "failure", State = i == 6 ? "ended" : "recovered",
                        Target = i == 3 ? "https://example.invalid/" + new String('a', 400) : "198.51.100.27", Failures = 4,
                        StartedUtc = at.AddSeconds(56 - i), LastObservedUtc = at.AddSeconds(60 - i),
                        RecoveredUtc = i == 6 ? (DateTime?)null : at.AddSeconds(60 - i), ClosedUtc = i == 6 ? (DateTime?)at.AddSeconds(60 - i) : null,
                        EndReason = i == 6 ? "합성 검사 중지" : "성공 결과 관측"
                    });
                }
                historyDirty = true; RefreshHistory();
                RenderTestSurface(Path.Combine(directory, "internet-history-guidance.png"), 1040, 740); CheckBounds(1040, 740);
                FrameworkElement firstCard = (FrameworkElement)historyList.Items[0];
                string firstText = HistoryTestText(firstCard);
                Require(firstText.Contains("무슨 일") && firstText.Contains("확인된 범위") && firstText.Contains("확인할 것") && firstText.Contains("외부 서버") && firstText.Contains("198.51.100.27"), "beginner guidance is missing from the visible card");
                Require(firstText.Contains("실패한 검사 4회") && firstText.Contains("검사 응답 돌아옴"), "failure meaning or recovery scope missing from history card");
                FrameworkElement errorCard = historyList.Items.Cast<FrameworkElement>().First(x => HistoryItemId(x) == "guidance-5");
                Require(HistoryTestText(errorCard).Contains("검사 도중 오류") && HistoryTestText(errorCard).Contains("검사 다시 가능") && HistoryTestText(errorCard).Contains("첫 검사 오류"), "check error presented as an internet outage");
                FrameworkElement endedCard = historyList.Items.Cast<FrameworkElement>().First(x => HistoryItemId(x) == "guidance-6");
                Require(HistoryTestText(endedCard).Contains("확인 못 함") && HistoryTestText(endedCard).Contains("합성 검사 중지"), "unconfirmed recovery or observation end reason hidden");
                historyDirty = true; RefreshHistory();
                Require(Object.ReferenceEquals(firstCard, historyList.Items[0]), "unchanged card rebuilt on refresh");

                RenderTestSurface(Path.Combine(directory, "internet-history-guidance-small.png"), 960, 660); CheckBounds(960, 660);
                ScrollViewer scroll = HistoryScrollViewer(historyList);
                Require(scroll != null && !scroll.CanContentScroll && scroll.ExtentWidth <= scroll.ViewportWidth + 1, "long guidance or target needs horizontal scrolling");
                scroll.ScrollToVerticalOffset(80); historyList.UpdateLayout();
                firstCard = (FrameworkElement)historyList.Items[0]; historyList.SelectedItem = firstCard;
                double top = firstCard.TranslatePoint(new Point(), scroll).Y;
                archived.Add(new ConnectivityIncident { Id = "guidance-new", Version = 1, Kind = "dns", Category = "failure", State = "open", Target = "example.invalid", Failures = 2, StartedUtc = at.AddSeconds(65), LastObservedUtc = at.AddSeconds(70) });
                historyDirty = true; RefreshHistory(); historyList.UpdateLayout();
                Require(HistoryItemId(historyList.SelectedItem) == "guidance-0" && Math.Abs(firstCard.TranslatePoint(new Point(), scroll).Y - top) <= 2, "new incident moved the card being read");
                scroll.ScrollToBottom(); historyList.UpdateLayout();
                Require(scroll.VerticalOffset > 0 && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) <= 1, "tall history card cannot be scrolled to its last guidance line");
            }
            finally { archived = saved; historyDirty = true; RefreshHistory(); }
        }

        private static string HistoryTestText(DependencyObject root)
        {
            TextBlock text = root as TextBlock;
            if (text != null) return text.Text;
            return String.Join("\n", LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().Select(HistoryTestText));
        }
    }
}
