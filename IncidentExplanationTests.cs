using System;

namespace PcInternetMonitor
{
    // Synthetic presentation regressions. No network requests, files, or windows.
    public static class IncidentExplanationTests
    {
        public static void Run()
        {
            DateTime start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            ConnectivityIncident incident = Fixture("reference", "failure", "open", start);
            ConnectivityIncidentExplanation open = ConnectivityIncidentExplanation.Describe(incident);
            string failedTitle = open.Title;
            Require(open.StateLabel.Contains("미확인"), "open history claimed a recovery");
            Require(open.TargetLabel.Contains("198.51.100.27") && !open.TargetLabel.Contains("1.1.1.1"), "explanation replaced the recorded target with a default");
            Require(open.ConfirmedScope.Contains("한 곳") && open.ConfirmedScope.Contains("전체 왕복"), "external check was narrowed to a route segment or generalized to other targets");
            Require(open.LimitNote.Contains("다른 검사 이력이 없다고") && open.LimitNote.Contains("유실률"), "missing history or failed check count was presented as service success or packet loss");

            incident.State = "recovered"; incident.RecoveredUtc = start.AddSeconds(4);
            incident.EndedUtc = incident.RecoveredUtc; incident.LastObservedUtc = incident.RecoveredUtc.Value;
            ConnectivityIncidentExplanation recovered = ConnectivityIncidentExplanation.Describe(incident);
            Require(recovered.Title == failedTitle && recovered.StateLabel == "검사 응답 돌아옴", "recovery erased the past failure from its title");
            Require(recovered.StateDetail.Contains("한 번") && recovered.ObservationText.Contains("실패한 검사 4회") && recovered.ObservationText.Contains("결과 관측 구간 4초"), "observed interval, check count, or recovery sample meaning was lost");
            Require(open.StateLabel != recovered.StateLabel && open.Title == failedTitle, "later descriptions mutated a previous explanation");

            incident.State = "ended"; incident.RecoveredUtc = null; incident.EndedUtc = null;
            incident.ClosedUtc = start.AddSeconds(6); incident.EndReason = "사용자 중지";
            ConnectivityIncidentExplanation ended = ConnectivityIncidentExplanation.Describe(incident);
            Require(ended.StateLabel.Contains("확인 못") && ended.EndReason == "사용자 중지", "stopping a check invented a recovery or lost its reason");
            incident.State = "recovered";
            Require(ConnectivityIncidentExplanation.Describe(incident).StateLabel == ended.StateLabel, "state label without a recovery observation invented a successful result");

            string[] kinds = { "gateway", "reference", "dns", "https", "adapter" };
            foreach (string kind in kinds)
            {
                ConnectivityIncident failure = Fixture(kind, "failure", "open", start);
                ConnectivityIncident error = Fixture(kind, "check_error", "open", start);
                // A restored summary intentionally no longer contains its original
                // failure status. Category, not text or target, identifies an error.
                error.Summary = failure.Summary = "회복 관측";
                ConnectivityIncidentExplanation failed = ConnectivityIncidentExplanation.Describe(failure);
                ConnectivityIncidentExplanation errored = ConnectivityIncidentExplanation.Describe(error);
                Require(failed.Title != errored.Title && errored.WhatHappened.Contains("검사 도중 오류"), "check error became a connection failure for " + kind);
                Require(!errored.NextStep.Contains("관리자") && !errored.NextStep.Contains("보안 설정"), "explanation proposed an unnecessary security change");
                error.State = "recovered"; error.RecoveredUtc = start.AddSeconds(8);
                Require(ConnectivityIncidentExplanation.Describe(error).StateLabel == "검사 다시 가능", "a resumed check claimed a recovered network for " + kind);
            }

            Require(ConnectivityIncidentExplanation.Describe(Fixture("gateway", "failure", "open", start)).ConfirmedScope.Contains("VPN·가상"), "gateway was assumed to be a physical home router");
            Require(ConnectivityIncidentExplanation.Describe(Fixture("dns", "failure", "open", start)).ConfirmedScope.Contains("캐시"), "system lookup was presented as a fresh remote DNS exchange");
            Require(ConnectivityIncidentExplanation.Describe(Fixture("https", "failure", "open", start)).ConfirmedScope.Contains("한 곳"), "one HTTPS target became a test of all websites");
            CheckRecovery("dns", "이름 조회 다시 성공", start);
            CheckRecovery("https", "웹 검사 다시 성공", start);
            CheckRecovery("adapter", "PC 연결 다시 확인", start);
            CheckAlias("external", "reference", start);
            CheckAlias("internet", "reference", start);
            CheckAlias("router", "gateway", start);
            CheckAlias("pc", "adapter", start);

            ConnectivityIncident unknown = Fixture("future-kind", "failure", "ended", start);
            unknown.Target = ""; unknown.Failures = 0; unknown.StartedUtc = DateTime.MinValue;
            ConnectivityIncidentExplanation fallback = ConnectivityIncidentExplanation.Describe(unknown);
            Require(!fallback.Title.Contains("공유기") && !fallback.Title.Contains("외부 서버") && fallback.TargetLabel.Contains("기록 없음") && fallback.ObservationText.Contains("기록 없음"), "unknown or missing data was converted into a specific target or a zero measurement");
            Require(unknown.Target == "" && unknown.StartedUtc == DateTime.MinValue && unknown.Failures == 0, "describing a record changed its source data");

            bool rejected = false;
            try { ConnectivityIncidentExplanation.Describe(null); }
            catch (ArgumentNullException) { rejected = true; }
            Require(rejected, "missing incident invented an explanation");
        }

        private static ConnectivityIncident Fixture(string kind, string category, string state, DateTime start)
        {
            return new ConnectivityIncident {
                Kind = kind, Category = category, State = state, Target = "198.51.100.27",
                StartedUtc = start, LastObservedUtc = start.AddSeconds(3), Failures = 4,
                Summary = "synthetic status", EndReason = ""
            };
        }

        private static void CheckRecovery(string kind, string expected, DateTime start)
        {
            ConnectivityIncident incident = Fixture(kind, "failure", "recovered", start);
            incident.RecoveredUtc = start.AddSeconds(4);
            Require(ConnectivityIncidentExplanation.Describe(incident).StateLabel == expected, "recovery label did not match the recorded check for " + kind);
        }

        private static void CheckAlias(string alias, string canonical, DateTime start)
        {
            ConnectivityIncidentExplanation alternative = ConnectivityIncidentExplanation.Describe(Fixture(alias, "failure", "open", start));
            ConnectivityIncidentExplanation expected = ConnectivityIncidentExplanation.Describe(Fixture(canonical, "failure", "open", start));
            Require(alternative.Title == expected.Title && alternative.ConfirmedScope == expected.ConfirmedScope, "legacy check kind changed explanation meaning for " + alias);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Incident explanation regression: " + message);
        }
    }
}
