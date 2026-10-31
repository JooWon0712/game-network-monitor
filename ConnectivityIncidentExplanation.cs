using System;
using System.Globalization;

namespace PcInternetMonitor
{
    // Presentation only: explain one recorded check without inferring the state
    // of other checks, a route segment, or the cause of a connection problem.
    public sealed class ConnectivityIncidentExplanation
    {
        public string Title { get; private set; }
        public string WhatHappened { get; private set; }
        public string ConfirmedScope { get; private set; }
        public string NextStep { get; private set; }
        public string StateLabel { get; private set; }
        public string StateDetail { get; private set; }
        public string TargetLabel { get; private set; }
        public string ObservationText { get; private set; }
        public string LimitNote { get; private set; }
        public string EndReason { get; private set; }

        private ConnectivityIncidentExplanation() { }

        public static ConnectivityIncidentExplanation Describe(ConnectivityIncident incident)
        {
            if (incident == null) throw new ArgumentNullException("incident");

            string kind = CanonicalKind(incident.Kind);
            bool checkError = String.Equals(incident.Category, "check_error", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(incident.Category, "error", StringComparison.OrdinalIgnoreCase);
            ConnectivityIncidentExplanation explanation = ForKind(kind);
            if (checkError)
            {
                explanation.Title = CheckName(kind) + "에 오류가 있었어요";
                explanation.WhatHappened = "검사 도중 오류가 나서 정상적인 결과를 얻지 못했어요. 연결이 실제로 끊겼다고 확인한 기록은 아니에요.";
                explanation.NextStep = "같은 오류가 반복되면 기록 폴더에서 해당 시각의 오류 내용을 확인해 보세요. 이 이력만으로 연결 문제라고 판단하지 마세요.";
            }

            bool recovered = String.Equals(incident.State, "recovered", StringComparison.OrdinalIgnoreCase) && incident.RecoveredUtc.HasValue;
            if (recovered)
            {
                explanation.StateLabel = checkError ? "검사 다시 가능" : RecoveryLabel(kind);
                explanation.StateDetail = "이후 이 검사에서 성공 결과를 한 번 확인했어요.";
            }
            else if (String.Equals(incident.State, "open", StringComparison.OrdinalIgnoreCase))
            {
                explanation.StateLabel = checkError ? "검사 성공 아직 미확인" : "응답이 돌아왔는지 아직 미확인";
                explanation.StateDetail = "이 기록의 마지막 검사 결과까지 성공이 확인되지 않았어요.";
            }
            else
            {
                explanation.StateLabel = "다시 성공했는지 확인 못 함";
                explanation.StateDetail = "성공 결과를 확인하기 전에 이 기록의 관측이 끝났어요.";
            }

            string target = Clean(incident.Target, 512);
            explanation.TargetLabel = target.Length == 0 ? "검사 대상: 주소 기록 없음" : "검사 대상: " + target;
            string count = incident.Failures > 0 ? (checkError ? "오류가 난 검사 " : "실패한 검사 ") + incident.Failures.ToString("N0", CultureInfo.InvariantCulture) + "회" : "검사 횟수 기록 없음";
            string duration = incident.StartedUtc == DateTime.MinValue ? "관측 구간 기록 없음" : "결과 관측 구간 " + incident.DurationSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "초";
            explanation.ObservationText = count + " · " + duration;
            explanation.LimitNote = "실제 단절 시간·패킷 유실률은 확정할 수 없어요. 다른 검사 이력이 없다고 그 검사가 성공한 것은 아니에요.";
            explanation.EndReason = Clean(incident.EndReason, 600);
            return explanation;
        }

        private static ConnectivityIncidentExplanation ForKind(string kind)
        {
            switch (kind)
            {
                case "gateway": return new ConnectivityIncidentExplanation {
                    Title = "공유기 응답 검사가 실패했어요",
                    WhatHappened = "PC에서 기본 게이트웨이로 보낸 확인 요청이 성공하지 못했어요. 응답이 없거나 경로를 찾지 못한 경우를 포함해요.",
                    ConfirmedScope = "PC와 선택한 연결의 기본 게이트웨이 사이를 확인해요. 보통 공유기지만 VPN·가상 연결에서는 다를 수 있어요.",
                    NextStep = "그 시각에 랜선·Wi-Fi 연결이 끊겼는지, 공유기의 전원이나 연결 표시가 바뀌었는지 확인해 보세요."
                };
                case "reference": return new ConnectivityIncidentExplanation {
                    Title = "외부 서버 응답 검사가 실패했어요",
                    WhatHappened = "외부 검사 대상까지 보낸 확인 요청이 성공하지 못했어요. 응답이 없거나 경로를 찾지 못한 경우를 포함해요.",
                    ConfirmedScope = "내 PC와 외부 검사 대상 한 곳의 전체 왕복을 확인해요. 공유기 이후 구간만 따로 측정한 값은 아니에요.",
                    NextStep = "같은 시각에 웹사이트나 사용 중인 앱도 끊겼는지 확인해 보세요. 이 검사에만 답을 하지 않는 상황도 있어요."
                };
                case "dns": return new ConnectivityIncidentExplanation {
                    Title = "사이트 이름 조회가 실패했어요",
                    WhatHappened = "사이트 이름을 IP 주소로 바꾸는 조회가 성공하지 못했어요.",
                    ConfirmedScope = "Windows의 시스템 이름 조회를 확인해요. 저장된 결과(캐시)나 PC의 주소 목록(hosts)이 쓰일 수 있어요.",
                    NextStep = "그 시각에 사이트 이름으로 접속할 때 문제가 있었는지 확인해 보세요. 반복되면 기록 폴더에서 해당 시각의 DNS 오류를 확인해요."
                };
                case "https": return new ConnectivityIncidentExplanation {
                    Title = "웹 접속 검사가 실패했어요",
                    WhatHappened = "검사 주소에 접속했을 때 기대한 웹 응답을 확인하지 못했어요.",
                    ConfirmedScope = "이 주소 한 곳의 암호화된 웹 접속을 확인해요. 이름 조회·연결·인증서·서버 응답이 영향을 줄 수 있어요.",
                    NextStep = "다른 웹사이트와 사용 중인 앱도 그 시각에 영향을 받았는지 비교해 보세요. 반복되면 기록에 남은 웹 응답·오류를 확인해요."
                };
                case "adapter": return new ConnectivityIncidentExplanation {
                    Title = "PC 연결이 끊긴 상태가 확인됐어요",
                    WhatHappened = "선택한 PC 연결이 끊겼거나 해당 연결을 찾지 못한 상태가 기록됐어요.",
                    ConfirmedScope = "선택한 랜선·Wi-Fi 연결 상태를 확인해요. 외부 검사 대상의 응답 여부와는 별개예요.",
                    NextStep = "그 시각에 랜선이 빠졌는지, Wi-Fi 연결이나 선택한 연결이 바뀌었는지 확인해 보세요."
                };
                default: return new ConnectivityIncidentExplanation {
                    Title = "검사가 실패한 기록이에요",
                    WhatHappened = "기록된 검사에서 성공 결과를 확인하지 못했어요.",
                    ConfirmedScope = "기록된 검사 대상에 관한 결과예요. 다른 연결이나 서비스의 상태를 판단한 기록은 아니에요.",
                    NextStep = "실제로 사용하던 앱에 문제가 있었던 시각과 비교하고, 기록 폴더의 같은 시각 검사 결과를 확인해 보세요."
                };
            }
        }

        private static string CanonicalKind(string kind)
        {
            switch ((kind ?? "").Trim().ToLowerInvariant())
            {
                case "gateway": case "router": return "gateway";
                case "reference": case "external": case "internet": return "reference";
                case "dns": return "dns";
                case "https": return "https";
                case "adapter": case "pc": return "adapter";
                default: return "unknown";
            }
        }

        private static string CheckName(string kind)
        {
            return kind == "gateway" ? "공유기 응답 검사" : kind == "reference" ? "외부 서버 응답 검사" :
                kind == "dns" ? "사이트 이름 조회" : kind == "https" ? "웹 접속 검사" : kind == "adapter" ? "PC 연결 확인" : "검사";
        }

        private static string RecoveryLabel(string kind)
        {
            return kind == "dns" ? "이름 조회 다시 성공" : kind == "https" ? "웹 검사 다시 성공" :
                kind == "adapter" ? "PC 연결 다시 확인" : "검사 응답 돌아옴";
        }

        private static string Clean(string value, int limit)
        {
            string text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= limit ? text : text.Substring(0, limit);
        }
    }
}
