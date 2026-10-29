using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PcInternetMonitor
{
    // Session-only aggregates supplied by the window. This class performs no probing.
    public sealed class InternetSessionSummary
    {
        private sealed class CheckTotal
        {
            internal string Kind, Target, LastStatus;
            internal DateTime LastUtc;
            internal long Succeeded, Failed, TimedOut, Errors;
            internal double DurationTotal, DurationMaximum;
            internal long DurationCount;
        }
        private readonly object gate = new object();
        private readonly Dictionary<string, CheckTotal> totals = new Dictionary<string, CheckTotal>(StringComparer.Ordinal);
        private DateTime firstUtc, lastSnapshotUtc;
        private long snapshots, availableAdapterSnapshots, adapterRateSnapshots;
        private bool confirmedAdapterCounters;
        private string lastAdapterStatus;
        private double maxReceive, maxSend;
        private long receiveErrors, sendErrors, receiveDiscards, sendDiscards;

        public void ObserveCheck(InternetCheckSample sample)
        {
            if (sample == null || sample.Cancelled || sample.TimestampUtc == DateTime.MinValue || sample.StartedUtc > sample.TimestampUtc) return;
            string state = sample.State ?? "";
            if (state != "success" && state != "failure" && state != "timeout" && state != "error") return;
            string key = (sample.Kind ?? "") + "\n" + (sample.Target ?? "");
            lock (gate)
            {
                CheckTotal total;
                if (!totals.TryGetValue(key, out total))
                {
                    if (totals.Count >= 64) return;
                    total = new CheckTotal { Kind = sample.Kind, Target = sample.Target }; totals.Add(key, total);
                }
                if (sample.TimestampUtc <= total.LastUtc) return;
                total.LastUtc = sample.TimestampUtc; total.LastStatus = sample.Status;
                if (firstUtc == DateTime.MinValue || sample.StartedUtc != DateTime.MinValue && sample.StartedUtc < firstUtc)
                    firstUtc = sample.StartedUtc == DateTime.MinValue ? sample.TimestampUtc : sample.StartedUtc;
                if (sample.Success) total.Succeeded++;
                else if (sample.TimedOut || state == "timeout") total.TimedOut++;
                else if (state == "error") total.Errors++;
                else total.Failed++;
                if (sample.Success && Valid(sample.DurationMs))
                { total.DurationTotal += sample.DurationMs.Value; total.DurationCount++; total.DurationMaximum = Math.Max(total.DurationMaximum, sample.DurationMs.Value); }
            }
        }

        public void ObserveSnapshot(MonitorSnapshot snapshot)
        {
            if (snapshot == null || snapshot.TimestampUtc == DateTime.MinValue) return;
            lock (gate)
            {
                if (snapshot.IsFinal)
                {
                    // Preserve confirmed final cumulative counters without treating
                    // a stop boundary as an ordinary availability/rate sample.
                    if (snapshot.AdapterCountersAvailable)
                    {
                        confirmedAdapterCounters = true;
                        receiveErrors = Math.Max(receiveErrors, snapshot.AdapterReceiveErrors); sendErrors = Math.Max(sendErrors, snapshot.AdapterSendErrors);
                        receiveDiscards = Math.Max(receiveDiscards, snapshot.AdapterReceiveDiscards); sendDiscards = Math.Max(sendDiscards, snapshot.AdapterSendDiscards);
                    }
                    return;
                }
                if (!snapshot.Running) return;
                if (snapshot.TimestampUtc <= lastSnapshotUtc) return;
                lastSnapshotUtc = snapshot.TimestampUtc; snapshots++;
                if (firstUtc == DateTime.MinValue || snapshot.TimestampUtc < firstUtc) firstUtc = snapshot.TimestampUtc;
                lastAdapterStatus = snapshot.AdapterStatus;
                if (snapshot.AdapterCountersAvailable)
                {
                    confirmedAdapterCounters = true;
                    availableAdapterSnapshots++;
                    receiveErrors = Math.Max(receiveErrors, snapshot.AdapterReceiveErrors); sendErrors = Math.Max(sendErrors, snapshot.AdapterSendErrors);
                    receiveDiscards = Math.Max(receiveDiscards, snapshot.AdapterReceiveDiscards); sendDiscards = Math.Max(sendDiscards, snapshot.AdapterSendDiscards);
                }
                if (snapshot.AdapterRatesAvailable)
                {
                    adapterRateSnapshots++;
                    if (Valid(snapshot.AdapterReceivedBytesPerSecond)) maxReceive = Math.Max(maxReceive, snapshot.AdapterReceivedBytesPerSecond);
                    if (Valid(snapshot.AdapterSentBytesPerSecond)) maxSend = Math.Max(maxSend, snapshot.AdapterSentBytesPerSecond);
                }
            }
        }

        public string BuildText(IList<ConnectivityIncident> incidents, long droppedCount, DateTime endUtc, string reason)
        {
            StringBuilder text = new StringBuilder();
            lock (gate)
            {
                text.AppendLine("PC 인터넷 상태 · 이번 측정 요약");
                text.AppendLine("관측 시작: " + Time(firstUtc) + " · 관측 종료: " + Time(endUtc));
                text.AppendLine("종료 사유: " + Clean(reason));
                text.AppendLine();
                text.AppendLine("검사별 결과");
                if (totals.Count == 0) text.AppendLine("완료된 검사 없음 · 접속 상태를 판단할 자료가 없습니다.");
                foreach (CheckTotal total in totals.Values.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Target, StringComparer.Ordinal))
                {
                    text.AppendLine(Name(total.Kind) + " · " + Clean(total.Target));
                    text.AppendLine("  성공 " + N(total.Succeeded) + "회 · 응답/접속 실패 " + N(total.Failed) + "회 · 시간 초과 " + N(total.TimedOut) + "회 · 검사 자체 오류 " + N(total.Errors) + "회");
                    text.AppendLine("  성공한 검사 소요 시간: " + (total.DurationCount == 0 ? "관측값 없음" : "평균 " + Number(total.DurationTotal / total.DurationCount) + " ms · 최대 " + Number(total.DurationMaximum) + " ms"));
                    text.AppendLine("  마지막 완료 " + Time(total.LastUtc) + " · " + Clean(total.LastStatus));
                }
                text.AppendLine();
                text.AppendLine("PC 연결 통계");
                text.AppendLine("어댑터 통계 가용 표본 " + N(availableAdapterSnapshots) + "/" + N(snapshots) + " · 마지막 상태 " + Clean(lastAdapterStatus));
                text.AppendLine("관측된 초당 통신량 최대: " + (adapterRateSnapshots == 0 ? "관측값 없음" : "받기 " + Rate(maxReceive) + " · 보내기 " + Rate(maxSend)) + " (회선 최대 속도 측정 아님)");
                text.AppendLine("확인된 누적 오류/폐기: " + (!confirmedAdapterCounters ? "관측값 없음" : "오류 받기 " + N(receiveErrors) + " · 보내기 " + N(sendErrors) + " / 폐기 받기 " + N(receiveDiscards) + " · 보내기 " + N(sendDiscards)));
            }
            text.AppendLine();
            text.AppendLine("끊김·검사 실패 이력 (각 검사 범위의 관측)");
            int count = 0;
            if (incidents != null)
                foreach (ConnectivityIncident incident in incidents.Where(x => x != null).OrderBy(x => x.StartedUtc))
                {
                    count++;
                    string state = incident.State == "recovered" && incident.RecoveredUtc.HasValue ? "회복 확인 " + Time(incident.RecoveredUtc.Value) : incident.State == "open" ? "관측 중" : "관측 종료 · 회복 미확인";
                    text.AppendLine("- " + Name(incident.Kind) + " · " + Clean(incident.Target) + " · " + (incident.Category == "check_error" ? "검사 자체 오류" : "검사 실패"));
                    text.AppendLine("  첫 실패 관측 " + Time(incident.StartedUtc) + " · " + state + " · 관측 구간 " + Number(incident.DurationSeconds) + "초 · 실패 검사 " + N(incident.Failures) + "회");
                    text.AppendLine("  " + Clean(incident.Summary) + (String.IsNullOrWhiteSpace(incident.EndReason) ? "" : " · " + Clean(incident.EndReason)));
                }
            if (count == 0) text.AppendLine("기록된 이력 없음. 미측정 구간이나 개별 검사 사이의 상태까지 정상임을 보장하지 않습니다.");
            text.AppendLine("화면/메모리 이력 한도 초과로 제외된 항목 " + N(Math.Max(0, droppedCount)) + "건. 원본 변경 이력은 connectivity-incidents.csv에서 확인합니다.");
            text.AppendLine();
            text.AppendLine("해석 범위");
            text.AppendLine("- 첫 실패와 회복은 검사 결과를 관측한 시각입니다. 실제 물리적 단절 시작/종료 시각과 같지 않을 수 있습니다. 중지·수집 공백은 회복으로 계산하지 않습니다.");
            text.AppendLine("- 공유기/외부 ICMP 무응답은 해당 대상의 응답 실패입니다. ICMP 제한·서버 상태·라우팅·VPN 등의 영향을 받아 인터넷 전체 장애나 ISP 고장을 확정하지 않습니다.");
            text.AppendLine("- DNS는 Windows 시스템 이름 조회이며 캐시·hosts·시스템 DNS 구성이 반영될 수 있습니다. 매번 외부 DNS 서버와 왕복했다는 뜻은 아닙니다.");
            text.AppendLine("- HTTPS는 설정된 특정 URL의 TLS/응답 확인입니다. DNS·프록시·인증서·서버 정책의 영향을 받으며 모든 웹사이트나 서비스의 접속을 보장하지 않습니다.");
            text.AppendLine("- 검사는 선택 NIC에 강제로 바인딩하지 않으며 Windows 라우팅과 프록시 구성을 사용합니다. 사용량은 속도 측정 결과가 아닙니다.");
            return text.ToString();
        }

        private static bool Valid(double? value) { return value.HasValue && Valid(value.Value); }
        private static bool Valid(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= 0; }
        private static string Clean(string value) { return String.IsNullOrWhiteSpace(value) ? "—" : value.Replace("\r", " ").Replace("\n", " "); }
        private static string Number(double value) { return Valid(value) ? value.ToString("0.###", CultureInfo.InvariantCulture) : "—"; }
        private static string N(long value) { return value.ToString(CultureInfo.InvariantCulture); }
        private static string Time(DateTime value) { return value == DateTime.MinValue ? "—" : value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }
        private static string Rate(double value) { return Number(value / 1024.0) + " KiB/s"; }
        private static string Name(string kind) { return kind == "dns" ? "DNS 이름 조회" : kind == "https" ? "HTTPS 접속" : kind == "gateway" ? "공유기 응답" : kind == "reference" ? "외부 응답" : kind == "adapter" ? "PC 연결" : Clean(kind); }
    }
}
