using System;
using System.Globalization;

namespace PcInternetMonitor
{
    public enum GameCheckTone { Muted, Normal, Attention }

    public sealed class GameCheckState
    {
        public string Status, Detail;
        public bool RatesAvailable;
        public GameCheckTone Tone;
    }

    // Presentation of observed game traffic, not a verdict about the game server,
    // actual game RTT, or end-to-end UDP packet loss. No collection is performed.
    public static class GameCheckPresentation
    {
        public static GameCheckState Describe(MonitorSnapshot s, bool recording)
        {
            if (s == null)
                return State("기록 전", "기록 시작 후 선택한 게임의 통신을 관측합니다.", false, GameCheckTone.Muted);
            if (!recording || !s.Running)
                return State("기록 중지", "마지막 통신 기록은 파일에 보관됩니다.", false, GameCheckTone.Muted);
            if (s.GameProcessEnded)
                return State("게임 종료", "선택한 게임이 종료됐습니다.", false, GameCheckTone.Attention);
            if (!s.GameCollectionRequested)
                return State("수집 안 함", "게임 통신 수집이 요청되지 않았습니다.", false, GameCheckTone.Muted);

            TraceHealth trace = s.Trace;
            if (trace == null || !trace.Active || trace.PermissionDenied)
                return State("수집 안 됨", trace != null && trace.PermissionDenied ?
                    "게임 통신 수집 권한이 없습니다." : "게임 통신 수집 상태를 확인하세요.", false, GameCheckTone.Attention);

            bool validRates = ValidRate(s.GameReceivedBytesPerSecond) && ValidRate(s.GameSentBytesPerSecond);
            bool available = s.GameRatesAvailable && validRates;
            if (trace.EventsLost > 0 || trace.BuffersLost > 0 || trace.MalformedEvents > 0 ||
                trace.UnsupportedEvents > 0 || s.GameInvalidEventTimeCount > 0)
                return State("일부 기록 누락", "일부 수집 기록 제외 · 실제 게임 유실률 아님", available, GameCheckTone.Attention);
            if (s.GameRatesAvailable && !validRates)
                return State("측정값 확인 필요", "통신량 값을 확인할 수 없습니다.", false, GameCheckTone.Attention);
            if (!available)
                return State("통신 대기", "선택한 게임의 통신 기록을 기다립니다.", false, GameCheckTone.Muted);

            // Use callback delivery freshness, never event occurrence age alone.
            // Old events can arrive late while the consumer remains active.
            double? idle = s.GameReceiveDeliveryIdleSeconds;
            if (idle.HasValue && ValidRate(idle.Value) && idle.Value >= 3)
                return State("수신 관측 공백", "최근 수신 관측 " + idle.Value.ToString("N1", CultureInfo.InvariantCulture) +
                    "초 전 · 메뉴/대기/수집 전달 대기 가능" + RetransmitNote(s.RetransmitEvents), true, GameCheckTone.Attention);
            if (s.RetransmitEvents > 0)
                return State("재전송 관측", "TCP 재전송 누적 " + s.RetransmitEvents.ToString("N0", CultureInfo.InvariantCulture) +
                    "회 · 게임 유실률 아님", true, GameCheckTone.Attention);

            if (s.GameSentEvents > 0 && s.GameReceivedEvents > 0)
                return State("송수신 관측", "게임 통신 관측값 · 연결 품질 판정 아님", true, GameCheckTone.Normal);
            if (s.GameSentEvents > 0 && s.GameReceivedEvents == 0)
                return State("보내기 관측", "받기는 아직 관측되지 않았습니다.", true, GameCheckTone.Normal);
            if (s.GameReceivedEvents > 0 && s.GameSentEvents == 0)
                return State("받기 관측", "보내기는 아직 관측되지 않았습니다.", true, GameCheckTone.Normal);
            return State("통신 관측", "게임 통신 관측값 · 연결 품질 판정 아님", true, GameCheckTone.Normal);
        }

        private static GameCheckState State(string status, string detail, bool rates, GameCheckTone tone)
        {
            return new GameCheckState { Status = status, Detail = detail, RatesAvailable = rates, Tone = tone };
        }

        private static bool ValidRate(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= 0;
        }

        private static string RetransmitNote(long count)
        {
            return count > 0 ? " · TCP 재전송 누적 " + count.ToString("N0", CultureInfo.InvariantCulture) + "회" : "";
        }

        // Pure synthetic cases; no process lookup, trace session, probe, or file I/O.
        public static void SelfTest()
        {
            Check(Describe(null, false).Status == "기록 전" && !Describe(null, true).RatesAvailable,
                "missing snapshots fabricated current traffic");
            MonitorSnapshot s = Active();
            GameCheckState state = Describe(s, true);
            Check(state.Status == "송수신 관측" && state.Tone == GameCheckTone.Normal && state.RatesAvailable &&
                state.Detail.Contains("판정 아님"), "observed traffic became an unavailable or healthy-connection verdict");
            state = Describe(s, false);
            Check(state.Status == "기록 중지" && !state.RatesAvailable && state.Tone == GameCheckTone.Muted,
                "stopped UI retained live rates");
            s.Running = false;
            Check(Describe(s, true).Status == "기록 중지" && !Describe(s, true).RatesAvailable,
                "stopped snapshot was treated as active traffic");

            s = Active(); s.GameProcessEnded = true;
            Check(Describe(s, true).Status == "게임 종료" && !Describe(s, true).RatesAvailable,
                "ended game retained rates");
            s = Active(); s.GameCollectionRequested = false;
            Check(Describe(s, true).Status == "수집 안 함" && !Describe(s, true).RatesAvailable,
                "unrequested game collection was shown as measured");
            s = Active(); s.Trace = null;
            Check(Describe(s, true).Status == "수집 안 됨" && !Describe(s, true).RatesAvailable,
                "missing trace status enabled rates");
            s = Active(); s.Trace.Active = false;
            Check(Describe(s, true).Status == "수집 안 됨" && !Describe(s, true).RatesAvailable,
                "inactive trace enabled rates");
            s = Active(); s.Trace.PermissionDenied = true;
            state = Describe(s, true);
            Check(state.Status == "수집 안 됨" && state.Detail.Contains("권한") && !state.RatesAvailable,
                "permission failure was hidden by a stale Active flag");

            Action<MonitorSnapshot>[] limited = {
                delegate(MonitorSnapshot value) { value.Trace.EventsLost = 1; },
                delegate(MonitorSnapshot value) { value.Trace.BuffersLost = 1; },
                delegate(MonitorSnapshot value) { value.Trace.MalformedEvents = 1; },
                delegate(MonitorSnapshot value) { value.Trace.UnsupportedEvents = 1; },
                delegate(MonitorSnapshot value) { value.GameInvalidEventTimeCount = 1; }
            };
            foreach (Action<MonitorSnapshot> set in limited)
            {
                s = Active(); set(s); state = Describe(s, true);
                Check(state.Status == "일부 기록 누락" && state.Tone == GameCheckTone.Attention && state.RatesAvailable &&
                    state.Detail.Contains("수집") && state.Detail.Contains("유실률 아님"),
                    "collection limitations were hidden or called network packet loss");
                s.GameRatesAvailable = false;
                Check(!Describe(s, true).RatesAvailable, "partial collection fabricated unavailable rates");
            }

            s = Active(); s.Trace.PermissionDenied = true;
            Describe(s, true);
            s.Trace.PermissionDenied = false; s.GameRatesAvailable = false;
            state = Describe(s, true);
            Check(state.Status == "통신 대기" && !state.RatesAvailable && state.Tone == GameCheckTone.Muted,
                "recovered collection retained an error or fabricated first traffic");
            s.GameRatesAvailable = true;
            Check(Describe(s, true).Status == "송수신 관측", "fresh traffic did not recover from waiting");

            s = Active(); s.GameReceiveIdleSeconds = 60; s.GameEventDeliveryLagMs = 6000; s.GameMaxEventDeliveryLagMs = 7000;
            s.GameReceiveDeliveryIdleSeconds = 0.2;
            Check(Describe(s, true).Tone == GameCheckTone.Normal, "collector lag or event age became a network-gap verdict");
            s.GameReceiveDeliveryIdleSeconds = 2.999;
            Check(Describe(s, true).Tone == GameCheckTone.Normal, "delivery-gap boundary changed below three seconds");
            s.GameReceiveDeliveryIdleSeconds = 3;
            state = Describe(s, true);
            Check(state.Status == "수신 관측 공백" && state.Tone == GameCheckTone.Attention && state.RatesAvailable &&
                state.Detail.Contains("메뉴") && state.Detail.Contains("대기") && state.Detail.Contains("전달"),
                "delivery-gap boundary or its evidence limit was omitted");
            s.RetransmitEvents = 7;
            Check(Describe(s, true).Detail.Contains("누적 7회"), "gap display lost the observed retransmit counter");
            s.GameReceiveDeliveryIdleSeconds = 0.2;
            state = Describe(s, true);
            Check(state.Status == "재전송 관측" && state.Detail.Contains("TCP") && state.Detail.Contains("누적 7회") &&
                state.Detail.Contains("유실률 아님") && state.Tone == GameCheckTone.Attention,
                "TCP retransmits were hidden or converted to a loss percentage");

            s = Active(); s.GameReceivedEvents = 0; s.GameReceiveDeliveryIdleSeconds = null;
            state = Describe(s, true);
            Check(state.Status == "보내기 관측" && state.Tone == GameCheckTone.Normal && state.Detail.Contains("받기"),
                "send-only observations claimed both directions");
            s = Active(); s.GameSentEvents = 0;
            Check(Describe(s, true).Status == "받기 관측", "receive-only observations claimed both directions");
            s = Active(); s.GameReceivedBytesPerSecond = Double.NaN;
            Check(!Describe(s, true).RatesAvailable && Describe(s, true).Tone == GameCheckTone.Attention,
                "invalid rate became a displayable measurement");
            s = Active(); s.GameSentBytesPerSecond = -1;
            Check(!Describe(s, true).RatesAvailable, "negative traffic rate was displayable");
        }

        private static MonitorSnapshot Active()
        {
            return new MonitorSnapshot { Running = true, ProcessId = 12345, GameCollectionRequested = true,
                Trace = new TraceHealth { Active = true }, GameRatesAvailable = true,
                GameSentEvents = 10, GameReceivedEvents = 12, GameSentBytesPerSecond = 512,
                GameReceivedBytesPerSecond = 1024, GameReceiveDeliveryIdleSeconds = 0.2 };
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Game check presentation test: " + message);
        }
    }
}
