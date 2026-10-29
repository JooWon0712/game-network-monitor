using System;
using System.Globalization;

namespace PcInternetMonitor
{
    public sealed class TcpRttDisplay
    {
        public string Text, Detail, ToolTip;
        public bool Available, Attention;
    }

    public static class TcpRttPresentation
    {
        public static TcpRttDisplay Describe(TcpRttSnapshot value, int selectedPid, bool recording, bool gameEnded, DateTime now)
        {
            const string scope = "선택한 프로세스의 TCP 연결별 Windows 평활 RTT 범위입니다. 로그인·다운로드 등도 포함할 수 있으며 실제 플레이 서버 핑이나 게임 패킷 유실률이 아닙니다.";
            if (!recording || gameEnded) return Make("—", gameEnded ? "게임 종료" : "측정 중지", false, false, scope);
            if (value == null || !value.Requested || value.ProcessId != selectedPid) return Make("표본 대기", "선택한 프로세스의 TCP 연결 확인 중", false, false, scope);
            double age = (now.ToUniversalTime() - value.TimestampUtc.ToUniversalTime()).TotalSeconds;
            bool stale = value.Stale || value.TimestampUtc == DateTime.MinValue || age < 0 || age >= 3;
            bool valid = value.MinimumMs.HasValue && value.MaximumMs.HasValue && Finite(value.MinimumMs.Value) && Finite(value.MaximumMs.Value) && value.MinimumMs.Value <= value.MaximumMs.Value && value.ConnectionsMeasured > 0;
            string tooltip = scope + "\n" + (value.Status ?? "") + "\n추적 " + value.ConnectionsTracked + " / 발견 " + value.ConnectionsFound + " · 한도 제외 " + value.ConnectionsSkipped;
            if (value.NativeErrorCode != 0) tooltip += "\n" + value.ErrorStage + " · 오류 " + value.NativeErrorCode;
            if (value.Available && valid && !stale)
            {
                double low = value.MinimumMs.Value, high = value.MaximumMs.Value;
                string number = low == high ? low == 0 ? "1 ms 미만" : low.ToString("0.#", CultureInfo.CurrentCulture) + " ms" :
                    (low == 0 ? "<1" : low.ToString("0.#", CultureInfo.CurrentCulture)) + " ~ " + high.ToString("0.#", CultureInfo.CurrentCulture) + " ms";
                return Make(number, "측정 " + value.ConnectionsMeasured + "/" + value.ConnectionsFound + " 연결" + (value.Partial ? " · 일부 미측정" : ""), true, value.Partial, tooltip);
            }
            if (value.PermissionDenied) return Make("권한 필요", "TCP RTT 통계 수집 권한 부족", false, true, tooltip);
            if (value.State == "unsupported") return Make("미지원", "이 환경에서 TCP RTT 통계를 지원하지 않습니다.", false, false, tooltip);
            if (value.State == "no_connections") return Make("TCP 연결 없음", "연결이 없거나 다른 통신 방식을 사용 중입니다.", false, false, tooltip);
            if (value.State == "process_ended") return Make("—", "선택한 프로세스 종료", false, false, tooltip);
            if (stale && value.TimestampUtc != DateTime.MinValue) return Make("최근 표본 없음", "갱신된 TCP RTT 표본을 기다립니다.", false, false, tooltip);
            return Make("미측정", value.Status ?? "새 TCP RTT 표본을 기다립니다.", false, value.NativeErrorCode != 0, tooltip);
        }

        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= 0; }
        private static TcpRttDisplay Make(string text, string detail, bool available, bool attention, string tooltip)
        {
            return new TcpRttDisplay { Text = text, Detail = detail, Available = available, Attention = attention, ToolTip = tooltip };
        }

        public static void SelfTest()
        {
            DateTime now = DateTime.UtcNow;
            TcpRttSnapshot s = new TcpRttSnapshot { Requested = true, ProcessId = 42, TimestampUtc = now, Available = true, MinimumMs = 0, MaximumMs = 0, ConnectionsFound = 1, ConnectionsTracked = 1, ConnectionsMeasured = 1 };
            Check(Describe(s, 42, true, false, now).Text == "1 ms 미만", "fresh integer-ms zero was not distinguished from missing RTT");
            s.MinimumMs = 12; s.MaximumMs = 25; s.ConnectionsFound = 3; s.ConnectionsMeasured = 2; s.Partial = true;
            TcpRttDisplay d = Describe(s, 42, true, false, now);
            Check(d.Available && d.Text.Contains("~") && d.Detail.Contains("2/3") && d.Attention && d.ToolTip.Contains("실제 플레이 서버 핑"), "multiple connections became one assumed game-server ping");
            Check(!Describe(s, 42, false, false, now).Available && Describe(s, 42, false, false, now).Text == "—", "stopped RTT remained current");
            Check(!Describe(s, 43, true, false, now).Available && !Describe(s, 42, true, true, now).Available, "old process RTT leaked into another selection or ended game");
            Check(!Describe(s, 42, true, false, now.AddSeconds(3)).Available, "stale cached TCP RTT remained measured");
            s.Available = false; s.PermissionDenied = true;
            Check(Describe(s, 42, true, false, now).Text == "권한 필요", "failed collection displayed numeric RTT");
            s.PermissionDenied = false; s.State = "unsupported";
            Check(Describe(s, 42, true, false, now).Text == "미지원", "unsupported RTT was labeled zero");
            s.State = "no_connections";
            Check(Describe(s, 42, true, false, now).Text == "TCP 연결 없음", "no TCP connection was treated as network loss");
            s.State = "available"; s.Available = true; s.MinimumMs = Double.NaN;
            Check(!Describe(s, 42, true, false, now).Available, "invalid OS measurement was displayed");
        }

        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("TCP RTT presentation test: " + message); }
    }
}
