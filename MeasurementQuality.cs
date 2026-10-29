using System;
using System.Collections.Generic;
using System.Globalization;

namespace PcInternetMonitor
{
    // Describes limitations of this measurement, not the cause of a connection issue.
    // Empty means no limitation was reported by these flags; it does not establish
    // that actual game packets were all received or that every event was observable.
    public static class MeasurementQuality
    {
        private const int MaximumReasons = 4;

        public static string Describe(MonitorSnapshot s)
        {
            if (s == null) return "측정 상태 확인 안 됨";
            List<string> reasons = new List<string>();
            TraceHealth trace = s.Trace;

            // Show present unavailability before historical continuity limitations.
            if (s.GameCollectionRequested || s.ProcessCollectionRequested)
            {
                if (trace == null) reasons.Add("통신 수집 상태 확인 안 됨");
                else if (!trace.Active) reasons.Add(trace.PermissionDenied ? "통신 수집 권한 부족" : "통신 이벤트 수집 안 됨");
            }
            if (s.GameProcessEnded) reasons.Add("선택한 게임 종료");
            if (s.AdapterExpected && !s.AdapterCountersAvailable) reasons.Add("PC 연결 통계 확인 안 됨");
            AddProbeReason(reasons, "공유기", s.Gateway);
            AddProbeReason(reasons, "인터넷", s.Reference);

            if (trace != null && trace.Active)
            {
                if (trace.EventsLost > 0 || trace.BuffersLost > 0) reasons.Add("통신 이벤트 기록 누락");
                if (trace.MalformedEvents > 0) reasons.Add("통신 이벤트 해석 제외");
                if (trace.UnsupportedEvents > 0) reasons.Add("일부 통신 이벤트 형식 미지원");
            }
            if (s.ProcessTraffic != null && s.ProcessTraffic.DroppedProcessEvents > 0) reasons.Add("프로그램 기록 누락");
            if (s.AdapterCounterResets > 0) reasons.Add("PC 카운터 감소 관측");
            if (s.AdapterCounterGaps > 0) reasons.Add("PC 통계 수집 공백");

            int shown = Math.Min(MaximumReasons, reasons.Count);
            string description = String.Join(" · ", reasons.GetRange(0, shown).ToArray());
            return reasons.Count <= shown ? description : description + " · 외 " +
                (reasons.Count - shown).ToString(CultureInfo.InvariantCulture) + "개";
        }

        private static void AddProbeReason(List<string> reasons, string label, ProbeSummary probe)
        {
            if (probe == null) return;
            if (probe.CurrentError) reasons.Add(label + " 검사 오류");
            else if (!String.IsNullOrWhiteSpace(probe.Target) && probe.Stale) reasons.Add(label + " 검사값 갱신 지연");
            // CurrentTimedOut is an observed lack of a reply, not a collection failure.
            // Stale is independent: even a previously timed-out request can stop updating.
        }

        // Pure synthetic flags only: no collection, network, UI or file activity.
        public static void SelfTest()
        {
            Check(Describe(null) == "측정 상태 확인 안 됨", "null snapshot was treated as available");
            MonitorSnapshot s = Healthy();
            Check(Describe(s) == "", "active collection with no reported loss was limited");
            s.GameRatesAvailable = false; s.GameReceiveIdleSeconds = 120; s.GameReceiveDeliveryIdleSeconds = 120;
            s.GameMaxEventDeliveryLagMs = 10000;
            Check(Describe(s) == "", "initial silence, idle or delivery lag was treated as incomplete collection");

            s = Healthy(); s.Trace.Active = false; s.Trace.PermissionDenied = true;
            Contains(s, "권한 부족", "permission failure was omitted");
            s.Trace.PermissionDenied = false;
            Contains(s, "수집 안 됨", "requested inactive collection was omitted");
            s.GameCollectionRequested = s.ProcessCollectionRequested = false;
            Check(Describe(s) == "", "unrequested inactive collection was treated as failed");
            s = Healthy(); s.Trace = null;
            Contains(s, "수집 상태 확인 안 됨", "missing requested trace state was omitted");

            s = Healthy(); s.Trace.EventsLost = 1;
            Contains(s, "기록 누락", "lost event was omitted");
            s = Healthy(); s.Trace.BuffersLost = 1;
            Contains(s, "기록 누락", "lost buffer was omitted");
            s = Healthy(); s.Trace.MalformedEvents = 1;
            Contains(s, "해석 제외", "malformed event was omitted");
            s = Healthy(); s.Trace.UnsupportedEvents = 1;
            Contains(s, "미지원", "unsupported event was omitted");
            s = Healthy(); s.ProcessTraffic.DroppedProcessEvents = 1;
            Contains(s, "프로그램 기록 누락", "dropped program event was omitted");
            s = Healthy(); s.GameProcessEnded = true;
            Contains(s, "게임 종료", "game exit was omitted");
            s = Healthy(); s.AdapterCountersAvailable = false;
            Contains(s, "PC 연결 통계", "expected unavailable adapter statistics were omitted");
            s.AdapterExpected = false;
            Check(Describe(s) == "", "an unrequested adapter was treated as unavailable");

            s = Healthy(); s.Gateway.CurrentError = true;
            Contains(s, "공유기 검사 오류", "gateway probe error was omitted");
            s = Healthy(); s.Reference.CurrentError = true;
            Contains(s, "인터넷 검사 오류", "reference probe error was omitted");
            s = Healthy(); s.Gateway.Stale = true;
            Contains(s, "공유기 검사값 갱신 지연", "stale configured probe was omitted");
            s.Gateway.Target = "";
            Check(Describe(s) == "", "an unconfigured stale probe was treated as unavailable");
            s = Healthy(); s.Gateway.CurrentTimedOut = s.Reference.CurrentTimedOut = true;
            Check(Describe(s) == "", "timeout-only observations were treated as incomplete collection");
            s = Healthy(); s.AdapterCounterResets = 1;
            Contains(s, "카운터 감소", "counter reset history was omitted");
            s = Healthy(); s.AdapterCounterGaps = 1;
            Contains(s, "수집 공백", "adapter gap history was omitted");

            s = Healthy(); s.GameProcessEnded = true; s.AdapterCountersAvailable = false;
            s.Gateway.CurrentError = true; s.Reference.Stale = true;
            s.Trace.EventsLost = s.Trace.MalformedEvents = s.Trace.UnsupportedEvents = 1;
            s.ProcessTraffic.DroppedProcessEvents = s.AdapterCounterResets = s.AdapterCounterGaps = 1;
            string description = Describe(s);
            Check(description == "선택한 게임 종료 · PC 연결 통계 확인 안 됨 · 공유기 검사 오류 · 인터넷 검사값 갱신 지연 · 외 6개",
                "multiple reasons were not bounded or did not retain their priority and remaining count");
            Check(Describe(s) == description, "classification changed on a repeated call");
        }

        private static MonitorSnapshot Healthy()
        {
            return new MonitorSnapshot {
                Running = true, GameCollectionRequested = true, ProcessCollectionRequested = true,
                Trace = new TraceHealth { Active = true }, AdapterExpected = true, AdapterCountersAvailable = true,
                Gateway = new ProbeSummary { Target = "192.0.2.1" }, Reference = new ProbeSummary { Target = "198.51.100.1" },
                ProcessTraffic = new ProcessTrafficSnapshot { Available = true }
            };
        }
        private static void Contains(MonitorSnapshot s, string text, string message)
        {
            Check(Describe(s).Contains(text), message);
        }
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("MeasurementQuality self-test: " + message);
        }
    }
}
