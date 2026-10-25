using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GameNetworkMonitor
{
    // The observer accepts metadata only. It neither runs probes nor opens a trace.
    public sealed class SessionAnalysis
    {
        private const int MaximumCandidates = 100;
        private const double CandidateMergeSeconds = 2.5;
        private const double GatewayHighMilliseconds = 20;
        private const double ReferenceHighMilliseconds = 150;
        private const double ReceiveIdleSeconds = 3;
        private const double HighSendBytesPerSecond = 10000000;
        private const int MaximumProcessPeaks = 32;
        private readonly object gate = new object();
        private readonly ProbeStatistics gateway = new ProbeStatistics("공유기", GatewayHighMilliseconds);
        private readonly ProbeStatistics reference = new ProbeStatistics("외부 비교", ReferenceHighMilliseconds);
        private readonly List<Candidate> candidates = new List<Candidate>();
        private readonly Dictionary<string, Candidate> latestCandidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        private readonly Dictionary<string, ProcessPeak> processPeaks = new Dictionary<string, ProcessPeak>(StringComparer.Ordinal);
        private readonly CounterDifference retransmits = new CounterDifference();
        private readonly CounterDifference receiveErrors = new CounterDifference();
        private readonly CounterDifference sendErrors = new CounterDifference();
        private readonly CounterDifference receiveDiscards = new CounterDifference();
        private readonly CounterDifference sendDiscards = new CounterDifference();
        private readonly CounterDifference traceEventsLost = new CounterDifference();
        private readonly CounterDifference traceBuffersLost = new CounterDifference();
        private DateTime firstAt = DateTime.MinValue;
        private DateTime lastAt = DateTime.MinValue;
        private DateTime lastSnapshotAt = DateTime.MinValue;
        private long snapshots, runningSnapshots, selectedSnapshots, activeSnapshots, gameAvailableSnapshots;
        private long waitingSnapshots, inactiveSnapshots, adapterAvailableSnapshots, snapshotGaps, ignoredSnapshots;
        private long requestedSnapshots, disabledSnapshots, deliveryMissingSnapshots, deliveryDelaySnapshots;
        private long processTrafficAvailableSnapshots, adapterRateSnapshots, highSendSnapshots, highSendAndRttSnapshots, highSendAndRttProbes;
        private DateTime lastMatchedGatewayProbeAt = DateTime.MinValue, lastHighSendAt = DateTime.MinValue;
        private double maximumAdapterSend, maximumDeliveryLag, maximumDeliveryMean;
        private bool deliveryLagObserved, deliveryMeanObserved;
        private long candidateCount;
        private double maximumSnapshotGap;
        private string gameName;
        private int gamePid;

        public void Observe(MonitorSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            lock (gate)
            {
                DateTime now = snapshot.TimestampUtc;
                if (now == DateTime.MinValue || (lastSnapshotAt != DateTime.MinValue && now <= lastSnapshotAt))
                { ignoredSnapshots++; return; }
                Touch(now);
                if (lastSnapshotAt != DateTime.MinValue)
                {
                    double gap = (now - lastSnapshotAt).TotalSeconds;
                    maximumSnapshotGap = Math.Max(maximumSnapshotGap, gap);
                    if (gap >= 3)
                    {
                        snapshotGaps++;
                        AddCandidate("snapshot-gap", lastSnapshotAt, now, "기록 표본 공백", "표본 간격 " + Number(gap) + "초 · 이 사이 상태는 알 수 없음");
                    }
                }
                lastSnapshotAt = now;
                snapshots++;
                if (!snapshot.Running) return;
                runningSnapshots++;
                if (snapshot.AdapterCountersAvailable)
                {
                    adapterAvailableSnapshots++;
                    long beforeReceiveErrors = receiveErrors.Increase, beforeSendErrors = sendErrors.Increase;
                    long beforeReceiveDiscards = receiveDiscards.Increase, beforeSendDiscards = sendDiscards.Increase;
                    receiveErrors.Observe(snapshot.AdapterReceiveErrors);
                    sendErrors.Observe(snapshot.AdapterSendErrors);
                    receiveDiscards.Observe(snapshot.AdapterReceiveDiscards);
                    sendDiscards.Observe(snapshot.AdapterSendDiscards);
                    if (receiveErrors.Increase > beforeReceiveErrors || sendErrors.Increase > beforeSendErrors ||
                        receiveDiscards.Increase > beforeReceiveDiscards || sendDiscards.Increase > beforeSendDiscards)
                        AddCandidate("adapter-counters", now, now, "PC 어댑터 오류·폐기 증가", "선택한 어댑터 전체 프로그램의 통계 · 게임 손실로 단정할 수 없음");
                }
                else AddCandidate("adapter-unavailable", now, now, "PC 어댑터 수집 불가", Clean(snapshot.AdapterStatus));

                ObserveProcessTraffic(snapshot);
                if (snapshot.AdapterRatesAvailable && ValidNumber(snapshot.AdapterSentBytesPerSecond))
                {
                    adapterRateSnapshots++;
                    maximumAdapterSend = Math.Max(maximumAdapterSend, snapshot.AdapterSentBytesPerSecond);
                    if (snapshot.AdapterSentBytesPerSecond >= HighSendBytesPerSecond)
                    {
                        highSendSnapshots++;
                        lastHighSendAt = now;
                        // Raw probes are counted once even when their latest result appears in several UI samples.
                        double age = (now - gateway.LastAt).TotalSeconds;
                        if (gateway.LastResponse.HasValue && age >= 0 && age <= CandidateMergeSeconds && gateway.LastResponse.Value >= GatewayHighMilliseconds)
                        {
                            highSendAndRttSnapshots++;
                            if (lastMatchedGatewayProbeAt != gateway.LastAt)
                            {
                                highSendAndRttProbes++;
                                lastMatchedGatewayProbeAt = gateway.LastAt;
                            }
                            AddCandidate("adapter-send-rtt", now, now, "PC 대량 송신·공유기 지연 동시 관측", "NIC 송신 " + Rate(snapshot.AdapterSentBytesPerSecond) + " · 최근 원시 ICMP " + Number(gateway.LastResponse.Value) + " ms · 원인 확정 아님");
                        }
                    }
                }

                if (snapshot.ProcessId <= 0) return;
                selectedSnapshots++;
                gameName = Clean(snapshot.ProcessName);
                gamePid = snapshot.ProcessId;
                if (!snapshot.GameCollectionRequested)
                {
                    disabledSnapshots++;
                    return;
                }
                requestedSnapshots++;
                TraceHealth health = snapshot.Trace;
                if (health == null || !health.Active)
                {
                    inactiveSnapshots++;
                    string detail = health == null ? "수집 상태 없음" : Clean(health.Status);
                    AddCandidate("trace-inactive", now, now, "요청한 게임 ETW 수집 비활성", detail + " · 권한·프로세스 상태 확인, 통신 끊김 판정 불가");
                    return;
                }
                activeSnapshots++;
                long previousEventsLost = traceEventsLost.Increase, previousBuffersLost = traceBuffersLost.Increase;
                traceEventsLost.Observe(health.EventsLost);
                traceBuffersLost.Observe(health.BuffersLost);
                retransmits.Observe(snapshot.RetransmitEvents);
                if (traceEventsLost.Increase > previousEventsLost || traceBuffersLost.Increase > previousBuffersLost)
                    AddCandidate("trace-loss", now, now, "ETW 수집 누락 증가", "수집 이벤트·버퍼 누락이며 네트워크 패킷 손실 수치가 아님");
                if (!snapshot.GameRatesAvailable)
                {
                    waitingSnapshots++;
                    return;
                }
                gameAvailableSnapshots++;
                bool lagged = false;
                if (snapshot.GameMaxEventDeliveryLagMs.HasValue && ValidNumber(snapshot.GameMaxEventDeliveryLagMs.Value))
                {
                    deliveryLagObserved = true;
                    maximumDeliveryLag = Math.Max(maximumDeliveryLag, snapshot.GameMaxEventDeliveryLagMs.Value);
                    lagged = snapshot.GameMaxEventDeliveryLagMs.Value >= 2000;
                }
                if (snapshot.GameEventDeliveryLagMs.HasValue && ValidNumber(snapshot.GameEventDeliveryLagMs.Value))
                {
                    deliveryMeanObserved = true;
                    maximumDeliveryMean = Math.Max(maximumDeliveryMean, snapshot.GameEventDeliveryLagMs.Value);
                }
                double? idle = snapshot.GameReceiveIdleSeconds, deliveryIdle = snapshot.GameReceiveDeliveryIdleSeconds;
                if (!deliveryIdle.HasValue || !ValidNumber(deliveryIdle.Value)) deliveryMissingSnapshots++;
                // Event occurrence and callback delivery are separate clocks. Recently delivered old
                // events establish collector delay, not an observed interruption of game packets.
                if (snapshot.GameReceivedEvents > 0 && deliveryIdle.HasValue && ValidNumber(deliveryIdle.Value))
                {
                    double recordedSeconds = Math.Max(0, (now - firstAt).TotalSeconds);
                    if (deliveryIdle.Value >= ReceiveIdleSeconds)
                    {
                        DateTime begin = now.AddSeconds(-Math.Min(deliveryIdle.Value, recordedSeconds));
                        AddCandidate("game-receive-idle", begin, now, "게임 수신 관측 공백 후보", "마지막 수신 이벤트 전달 후 " + Number(deliveryIdle.Value) + "초 · ETW 전달 대기·메뉴·대기도 가능, 실제 망 단절 확정 아님");
                    }
                    else if (idle.HasValue && ValidNumber(idle.Value) && idle.Value >= ReceiveIdleSeconds) lagged = true;
                }
                if (lagged)
                {
                    deliveryDelaySnapshots++;
                    string detail = "발생 시각과 수집기 도착 시각 차이 · 수신 공백으로 해석하지 않음";
                    if (snapshot.GameMaxEventDeliveryLagMs.HasValue && ValidNumber(snapshot.GameMaxEventDeliveryLagMs.Value))
                        detail += " · 최근 2초 전달 지연 최대 " + Number(snapshot.GameMaxEventDeliveryLagMs.Value) + " ms";
                    AddCandidate("trace-delivery-delay", now, now, "ETW 이벤트 전달 지연 후보", detail);
                }
            }
        }

        // Call exactly once for each raw ProbeRunner sample, never with a rolling ProbeSummary.
        public void ObserveProbe(string kind, ProbeSample sample)
        {
            if (sample == null) throw new ArgumentNullException("sample");
            ProbeStatistics statistics;
            if (String.Equals(kind, "gateway", StringComparison.OrdinalIgnoreCase)) statistics = gateway;
            else if (String.Equals(kind, "reference", StringComparison.OrdinalIgnoreCase)) statistics = reference;
            else throw new ArgumentException("Probe kind must be gateway or reference.", "kind");
            lock (gate)
            {
                DateTime now = sample.TimestampUtc;
                if (now == DateTime.MinValue || (statistics.LastAt != DateTime.MinValue && now <= statistics.LastAt))
                { statistics.Ignored++; return; }
                Touch(now);
                string target = Clean(sample.Target);
                bool continuous = statistics.LastAt != DateTime.MinValue && (now - statistics.LastAt).TotalSeconds <= CandidateMergeSeconds;
                if (statistics.Total > 0 && !String.Equals(statistics.Target, target, StringComparison.OrdinalIgnoreCase))
                { statistics.TargetChanges++; continuous = false; }
                if (!continuous) { statistics.TimeoutStreak = 0; statistics.ErrorStreak = 0; }
                statistics.Target = target;
                statistics.LastAt = now;
                statistics.LastResponse = null;
                statistics.Total++;
                string key = kind.ToLowerInvariant() + "|" + target + "|";
                if (sample.Countable && sample.TimedOut && !sample.RoundtripMs.HasValue)
                {
                    statistics.Timeouts++;
                    statistics.ErrorStreak = 0;
                    if (statistics.TimeoutStreak == 0) statistics.TimeoutBegin = now;
                    statistics.TimeoutStreak++;
                    statistics.MaximumTimeoutStreak = Math.Max(statistics.MaximumTimeoutStreak, statistics.TimeoutStreak);
                    if (statistics.TimeoutStreak >= 2)
                        AddCandidate(key + "timeout", statistics.TimeoutBegin, now, statistics.Name + " ICMP 연속 시간 초과", target + " · " + statistics.TimeoutStreak.ToString(CultureInfo.InvariantCulture) + "회 연속 · 게임 패킷 손실과 다름");
                }
                else if (sample.Countable && !sample.TimedOut && sample.RoundtripMs.HasValue && ValidNumber(sample.RoundtripMs.Value))
                {
                    statistics.TimeoutStreak = 0;
                    statistics.ErrorStreak = 0;
                    double value = sample.RoundtripMs.Value;
                    statistics.LastResponse = value;
                    statistics.Successes++;
                    statistics.Sum += value;
                    statistics.Maximum = Math.Max(statistics.Maximum, value);
                    long count;
                    statistics.Responses.TryGetValue(value, out count);
                    statistics.Responses[value] = count + 1;
                    if (value >= statistics.HighThreshold)
                    {
                        statistics.HighResponses++;
                        AddCandidate(key + "high-rtt", now, now, statistics.Name + " 높은 RTT 후보", target + " · " + Number(value) + " ms (기준 " + Number(statistics.HighThreshold) + " ms 이상)");
                    }
                }
                else
                {
                    statistics.Errors++;
                    statistics.TimeoutStreak = 0;
                    if (sample.Countable) statistics.InvalidSamples++;
                    if (statistics.ErrorStreak == 0) statistics.ErrorBegin = now;
                    statistics.ErrorStreak++;
                    statistics.LastError = Clean(sample.Status);
                    if (statistics.ErrorStreak >= 2)
                        AddCandidate(key + "probe-error", statistics.ErrorBegin, now, statistics.Name + " ICMP 측정 오류", target + " · " + statistics.LastError + " · 시간 초과 비율 분모에서 제외");
                }
            }
        }

        public string SummaryText()
        {
            lock (gate)
            {
                StringBuilder text = new StringBuilder();
                text.AppendLine("게임 네트워크 세션 분석 · 규칙 기반 자동 요약");
                if (firstAt == DateTime.MinValue)
                {
                    text.AppendLine("기록된 표본이 없습니다. 측정 데이터가 없어 상태를 판단할 수 없습니다.");
                    return text.ToString();
                }
                text.AppendLine("기록 범위: " + Time(firstAt) + " ~ " + Time(lastAt) + " (" + Number(Math.Max(0, (lastAt - firstAt).TotalSeconds)) + "초, 표본 공백 포함)");
                text.AppendLine("상태 표본 " + snapshots.ToString(CultureInfo.InvariantCulture) + "개 · 측정 중 " + runningSnapshots.ToString(CultureInfo.InvariantCulture) + "개 · 중복·역순·시각 누락 제외 " + ignoredSnapshots.ToString(CultureInfo.InvariantCulture) + "개");
                if (snapshotGaps > 0)
                    text.AppendLine("3초 이상 상태 표본 공백 " + snapshotGaps.ToString(CultureInfo.InvariantCulture) + "회 · 최대 " + Number(maximumSnapshotGap) + "초. 기록 공백을 정상 구간으로 간주하지 않습니다.");
                if (selectedSnapshots > 0)
                {
                    text.AppendLine("게임: " + gameName + " · PID " + gamePid.ToString(CultureInfo.InvariantCulture));
                    text.AppendLine("게임 선택 표본 " + selectedSnapshots.ToString(CultureInfo.InvariantCulture) + "개 · 수집 요청 " + requestedSnapshots.ToString(CultureInfo.InvariantCulture) + "개 · 수집 끔 " + disabledSnapshots.ToString(CultureInfo.InvariantCulture) + "개 (수집 끔은 장애 후보에서 제외)");
                    text.AppendLine("수집 요청 표본 중 ETW 활성 " + Ratio(activeSnapshots, requestedSnapshots) + ", 통신 수치 가용 " + Ratio(gameAvailableSnapshots, requestedSnapshots));
                    text.AppendLine("ETW 활성·첫 통신 대기 " + waitingSnapshots.ToString(CultureInfo.InvariantCulture) + "개 · 요청했으나 ETW 비활성 " + inactiveSnapshots.ToString(CultureInfo.InvariantCulture) + "개 (권한·게임 종료 등 확인)");
                    if (deliveryLagObserved)
                        text.AppendLine("ETW 발생→도착 지연: 관측 최대 " + Number(maximumDeliveryLag) + " ms · 최근 2초 구간평균 중 최대 " + (deliveryMeanObserved ? Number(maximumDeliveryMean) + " ms" : "관측값 없음") + " (게임 RTT 아님)");
                    text.AppendLine("ETW 전달 지연 후보 표본 " + deliveryDelaySnapshots.ToString(CultureInfo.InvariantCulture) + "개 · 전달 신선도 미제공 표본 " + deliveryMissingSnapshots.ToString(CultureInfo.InvariantCulture) + "개. 발생 시각만 오래된 이벤트를 수신 중단으로 판정하지 않습니다.");
                }
                else text.AppendLine("게임 선택 표본 없음 · 게임 통신 상태 판단 불가");
                text.AppendLine("PC 어댑터 누적 통계 가용: " + Ratio(adapterAvailableSnapshots, runningSnapshots));
                text.AppendLine("PC 선택 NIC 송신 관측 최대 " + (adapterRateSnapshots == 0 ? "관측값 없음" : Rate(maximumAdapterSend)) + " · 10.0 MB/s 이상 표본 " + highSendSnapshots.ToString(CultureInfo.InvariantCulture) + "개" + (lastHighSendAt == DateTime.MinValue ? "" : " · 최근 " + Time(lastHighSendAt)));
                text.AppendLine("대량 송신 중 최근 2.5초 이내 공유기 RTT 20 ms 이상 동시 관측: 상태 표본 " + highSendAndRttSnapshots.ToString(CultureInfo.InvariantCulture) + "개 / 서로 다른 원시 ICMP " + highSendAndRttProbes.ToString(CultureInfo.InvariantCulture) + "개. 시간적 동시 관측이며 원인·게임 손실의 증거로 확정하지 않습니다.");
                AppendProcessPeaks(text);
                text.AppendLine();
                AppendProbe(text, gateway);
                AppendProbe(text, reference);
                text.AppendLine();
                text.AppendLine("첫 유효 상태 표본 이후 누적 카운터 증가량 (같은 누적값을 반복 합산하지 않음)");
                AppendCounter(text, "게임 TCP 재전송 이벤트", retransmits);
                AppendCounter(text, "PC 수신 오류", receiveErrors);
                AppendCounter(text, "PC 송신 오류", sendErrors);
                AppendCounter(text, "PC 수신 폐기", receiveDiscards);
                AppendCounter(text, "PC 송신 폐기", sendDiscards);
                AppendCounter(text, "ETW 수집 이벤트 누락", traceEventsLost);
                AppendCounter(text, "ETW 수집 버퍼 누락", traceBuffersLost);
                text.AppendLine("카운터 감소는 리셋으로 분리합니다. 첫 표본 이전과 리셋 사이의 증가량은 알 수 없어 합산하지 않습니다.");
                text.AppendLine();
                text.AppendLine("자동 후보 규칙: ICMP 2회 이상 연속 시간 초과(표본 사이 2.5초 이하), 공유기 RTT 20 ms 이상, 외부 RTT 150 ms 이상, 활성 ETW·마지막 게임 수신 전달 후 3초 이상 관측 없음, 선택 NIC 송신 10 MB/s 이상·최근 공유기 고지연 동시 관측.");
                text.AppendLine("수집 품질 규칙: 최근 2초 이벤트 전달 지연 최대 2000 ms 이상 또는 발생 기준 수신 경과 3초 이상이지만 전달 기준 경과는 3초 미만이면 ETW 전달 지연 후보로 분리합니다.");
                text.AppendLine("같은 원인의 후보는 간격 2.5초 이내이면 합칩니다. 임계치는 진단용 고정 기준이며 평소 지연·메뉴·대기 상태에서도 후보가 생길 수 있습니다.");
                text.AppendLine("자동 후보 구간 " + candidateCount.ToString(CultureInfo.InvariantCulture) + "개 생성 · 최근 " + candidates.Count.ToString(CultureInfo.InvariantCulture) + "개 표시 (최대 100개 보관)");
                if (candidates.Count == 0) text.AppendLine("규칙에 해당하는 후보가 없습니다. 실제 끊김이 없었다는 보장은 아닙니다.");
                else
                {
                    List<Candidate> ordered = new List<Candidate>(candidates);
                    ordered.Sort(delegate(Candidate left, Candidate right) { return left.Start.CompareTo(right.Start); });
                    foreach (Candidate candidate in ordered)
                        text.AppendLine("- " + Time(candidate.Start) + " ~ " + Time(candidate.End) + " | " + candidate.Name + " | " + candidate.Detail);
                }
                text.AppendLine();
                text.AppendLine("이 기록으로 실제 게임 UDP 패킷 손실률이나 네트워크 전체 손실률은 계산할 수 없습니다. ICMP 시간 초과·PC 오류·TCP 재전송·ETW 누락은 서로 다른 지표입니다.");
                text.AppendLine("전체 구간을 자동으로 분석합니다. 후보는 원인을 좁히기 위한 증거이며, ISP·공유기·게임 서버의 장애 또는 사용자가 체감한 끊김을 확정하지 않습니다.");
                return text.ToString();
            }
        }

        private void Touch(DateTime now)
        {
            if (firstAt == DateTime.MinValue || now < firstAt) firstAt = now;
            if (lastAt == DateTime.MinValue || now > lastAt) lastAt = now;
        }

        private void ObserveProcessTraffic(MonitorSnapshot snapshot)
        {
            ProcessTrafficSnapshot traffic = snapshot.ProcessTraffic;
            if (traffic == null || !traffic.Available || traffic.Rows == null) return;
            processTrafficAvailableSnapshots++;
            foreach (ProcessTrafficRow row in traffic.Rows)
            {
                if (row == null || row.ProcessId <= 0 || !ValidNumber(row.SentBytesPerSecond)) continue;
                string instance = row.ProcessInstance ?? "";
                string key = row.ProcessId.ToString(CultureInfo.InvariantCulture) + "|" + instance;
                ProcessPeak peak;
                if (!processPeaks.TryGetValue(key, out peak))
                {
                    if (processPeaks.Count >= MaximumProcessPeaks)
                    {
                        ProcessPeak lowest = null;
                        foreach (ProcessPeak item in processPeaks.Values)
                            if (lowest == null || item.MaximumSend < lowest.MaximumSend) lowest = item;
                        if (lowest != null && row.SentBytesPerSecond <= lowest.MaximumSend) continue;
                        if (lowest != null) processPeaks.Remove(lowest.Key);
                    }
                    peak = new ProcessPeak { Key = key, ProcessId = row.ProcessId, ProcessName = Clean(row.ProcessName), IdentityAvailable = row.IdentityAvailable };
                    processPeaks.Add(key, peak);
                }
                if (row.SentBytesPerSecond > peak.MaximumSend || peak.PeakAt == DateTime.MinValue)
                {
                    peak.MaximumSend = row.SentBytesPerSecond;
                    peak.PeakAt = snapshot.TimestampUtc;
                    peak.ProcessName = Clean(row.ProcessName);
                    peak.IdentityAvailable = row.IdentityAvailable;
                }
            }
        }

        private void AppendProcessPeaks(StringBuilder text)
        {
            text.AppendLine("프로세스별 ETW 수집 가용 표본 " + processTrafficAvailableSnapshots.ToString(CultureInfo.InvariantCulture) + "개 · 모든 어댑터의 TCP/UDP 메타데이터이며 선택 NIC 통계와 직접 일치하지 않습니다.");
            text.AppendLine("  프로세스 속도는 이벤트가 수집기에 도착한 구간 기준입니다. ETW 전달 지연 시 한 구간에 몰릴 수 있어 실제 송신 발생 시각의 속도로 확정하지 않습니다.");
            if (processPeaks.Count == 0)
            {
                text.AppendLine("  프로세스별 송신 관측 없음 · 비활성·권한·첫 이벤트 대기 여부 확인, 송신 0이라는 의미는 아님");
                return;
            }
            List<ProcessPeak> ordered = new List<ProcessPeak>(processPeaks.Values);
            ordered.Sort(delegate(ProcessPeak left, ProcessPeak right) { int order = right.MaximumSend.CompareTo(left.MaximumSend); return order == 0 ? left.ProcessId.CompareTo(right.ProcessId) : order; });
            text.AppendLine("  세션 송신 관측 최대 상위 " + Math.Min(5, ordered.Count).ToString(CultureInfo.InvariantCulture) + "개 (프로세스 인스턴스별, 원인 순위가 아님)");
            for (int i = 0; i < Math.Min(5, ordered.Count); i++)
            {
                ProcessPeak peak = ordered[i];
                text.AppendLine("  - " + peak.ProcessName + " · PID " + peak.ProcessId.ToString(CultureInfo.InvariantCulture) + " · 최대 " + Rate(peak.MaximumSend) + " · " + Time(peak.PeakAt) + (peak.IdentityAvailable ? "" : " · 프로세스 신원 확인 불가"));
            }
        }

        private void AddCandidate(string key, DateTime begin, DateTime end, string name, string detail)
        {
            Candidate candidate;
            if (latestCandidates.TryGetValue(key, out candidate) && begin <= candidate.End.AddSeconds(CandidateMergeSeconds) && end >= candidate.Start.AddSeconds(-CandidateMergeSeconds))
            {
                if (begin < candidate.Start) candidate.Start = begin;
                if (end > candidate.End) candidate.End = end;
                candidate.Detail = detail;
                return;
            }
            candidate = new Candidate { Key = key, Start = begin, End = end, Name = name, Detail = detail };
            candidates.Add(candidate);
            latestCandidates[key] = candidate;
            candidateCount++;
            if (candidates.Count > MaximumCandidates)
            {
                Candidate removed = candidates[0];
                candidates.RemoveAt(0);
                Candidate latest;
                if (latestCandidates.TryGetValue(removed.Key, out latest) && Object.ReferenceEquals(latest, removed)) latestCandidates.Remove(removed.Key);
            }
        }

        private static void AppendProbe(StringBuilder text, ProbeStatistics statistics)
        {
            text.AppendLine(statistics.Name + " ICMP 원시 기록: " + (statistics.Total == 0 ? "없음 · 검사 꺼짐·대상 미설정 여부 확인" : statistics.Target));
            if (statistics.Total == 0) return;
            text.AppendLine("  검사 결과 " + statistics.Total.ToString(CultureInfo.InvariantCulture) + "개 · 응답 " + statistics.Successes.ToString(CultureInfo.InvariantCulture) + " · 시간 초과 " + statistics.Timeouts.ToString(CultureInfo.InvariantCulture) + " · 별도 오류 " + statistics.Errors.ToString(CultureInfo.InvariantCulture));
            long denominator = statistics.Successes + statistics.Timeouts;
            text.AppendLine("  ICMP 시간 초과 비율 " + (denominator == 0 ? "계산 불가 (유효 검사 없음)" : Ratio(statistics.Timeouts, denominator)) + " · 최대 연속 시간 초과 " + statistics.MaximumTimeoutStreak.ToString(CultureInfo.InvariantCulture) + "회");
            if (statistics.Successes == 0) text.AppendLine("  응답 RTT 없음 · 평균·최대·p95 계산 불가");
            else text.AppendLine("  응답 RTT 평균 " + Number(statistics.Sum / statistics.Successes) + " ms · 최대 " + Number(statistics.Maximum) + " ms · p95 " + Number(statistics.Percentile95()) + " ms (전체 원시 응답의 최근접 순위) · 고정 RTT 기준 이상 " + statistics.HighResponses.ToString(CultureInfo.InvariantCulture) + "회");
            if (statistics.Errors > 0) text.AppendLine("  최근 측정 오류: " + statistics.LastError + " · 시간 초과 비율 분모에서 제외");
            if (statistics.InvalidSamples > 0) text.AppendLine("  형식 불일치 표본 " + statistics.InvalidSamples.ToString(CultureInfo.InvariantCulture) + "개는 오류에 포함");
            if (statistics.TargetChanges > 0) text.AppendLine("  대상 변경 " + statistics.TargetChanges.ToString(CultureInfo.InvariantCulture) + "회 · 이 통계에는 변경 전후 대상이 함께 포함됨");
            if (statistics.Ignored > 0) text.AppendLine("  중복·역순·시각 누락 제외 " + statistics.Ignored.ToString(CultureInfo.InvariantCulture) + "개");
        }

        private static void AppendCounter(StringBuilder text, string name, CounterDifference counter)
        {
            string result = !counter.HasBaseline ? "관측값 없음" : counter.Observations < 2 ? "기준값만 관측 · 증가량 판단 표본 부족" : counter.Increase.ToString(CultureInfo.InvariantCulture) + " 증가 · 감소/리셋 " + counter.Resets.ToString(CultureInfo.InvariantCulture) + "회";
            text.AppendLine("  " + name + ": " + result);
        }

        private static bool ValidNumber(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= 0; }
        private static string Number(double value) { return value.ToString("0.0", CultureInfo.InvariantCulture); }
        private static string Rate(double value) { return value < 1000000 ? (value / 1024.0).ToString("0.###", CultureInfo.InvariantCulture) + " KiB/s" : Number(value / 1000000.0) + " MB/s (" + Number(value * 8.0 / 1000000.0) + " Mbps)"; }
        private static string Time(DateTime value) { return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }
        private static string Ratio(long numerator, long denominator)
        {
            return denominator <= 0 ? "관측값 없음" : (100.0 * numerator / denominator).ToString("0.000", CultureInfo.InvariantCulture) + "% (" + numerator.ToString(CultureInfo.InvariantCulture) + "/" + denominator.ToString(CultureInfo.InvariantCulture) + ")";
        }
        private static string Clean(string value)
        {
            if (String.IsNullOrEmpty(value)) return "정보 없음";
            StringBuilder clean = new StringBuilder();
            foreach (char character in value)
            {
                if (clean.Length >= 256) break;
                clean.Append(Char.IsControl(character) ? ' ' : character);
            }
            return clean.ToString();
        }

        private sealed class CounterDifference
        {
            public bool HasBaseline;
            public long Previous, Increase, Resets, Observations;
            public void Observe(long value)
            {
                if (value < 0) return;
                if (HasBaseline)
                {
                    if (value >= Previous) Increase = SaturatingAdd(Increase, value - Previous);
                    else Resets++;
                }
                HasBaseline = true;
                Previous = value;
                Observations++;
            }
            private static long SaturatingAdd(long value, long amount) { return value > Int64.MaxValue - amount ? Int64.MaxValue : value + amount; }
        }

        private sealed class Candidate
        {
            public string Key, Name, Detail;
            public DateTime Start, End;
        }

        private sealed class ProcessPeak
        {
            public string Key, ProcessName;
            public int ProcessId;
            public bool IdentityAvailable;
            public double MaximumSend;
            public DateTime PeakAt;
        }

        private sealed class ProbeStatistics
        {
            public readonly string Name;
            public readonly double HighThreshold;
            // Ping supplies integral milliseconds; a histogram retains exact session-wide quantiles
            // without storing one object for every response or summing overlapping rolling windows.
            public readonly SortedDictionary<double, long> Responses = new SortedDictionary<double, long>();
            public string Target, LastError;
            public DateTime LastAt = DateTime.MinValue, TimeoutBegin, ErrorBegin;
            public long Total, Successes, Timeouts, Errors, InvalidSamples, HighResponses, Ignored, TargetChanges;
            public long TimeoutStreak, MaximumTimeoutStreak, ErrorStreak;
            public double Sum, Maximum;
            public double? LastResponse;
            public ProbeStatistics(string name, double highThreshold) { Name = name; HighThreshold = highThreshold; }
            public double Percentile95()
            {
                long rank = (long)Math.Ceiling((decimal)Successes * 0.95m);
                long seen = 0;
                foreach (KeyValuePair<double, long> response in Responses)
                {
                    seen += response.Value;
                    if (seen >= rank) return response.Key;
                }
                return Double.NaN;
            }
        }

        public static void SelfTest()
        {
            DateTime began = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
            SessionAnalysis empty = new SessionAnalysis();
            Check(empty.SummaryText().IndexOf("표본이 없습니다", StringComparison.Ordinal) >= 0, "missing data should not be healthy zero");
            SessionAnalysis analysis = new SessionAnalysis();
            for (int i = 0; i < 20; i++)
            {
                analysis.ObserveProbe("gateway", new ProbeSample { TimestampUtc = began.AddSeconds(i), Target = "router", Countable = true, RoundtripMs = i });
                analysis.Observe(new MonitorSnapshot { TimestampUtc = began.AddSeconds(i), Running = true, ProcessId = 12, ProcessName = "fixture", GameCollectionRequested = true, Trace = new TraceHealth { Active = true }, Gateway = new ProbeSummary { Sent = 60, Missing = 30 } });
            }
            Check(analysis.gateway.Successes == 20 && analysis.gateway.Total == 20 && analysis.gateway.Timeouts == 0, "overlapping rolling summaries counted as raw probes");
            Check(analysis.gateway.Percentile95() == 18 && analysis.gateway.Sum / analysis.gateway.Successes == 9.5 && analysis.gateway.Maximum == 19, "exact whole-session RTT statistics");
            Check(analysis.gameAvailableSnapshots == 0 && analysis.waitingSnapshots == 20 && analysis.inactiveSnapshots == 0, "first-event waiting confused with inactive collection or zero game traffic");
            Check(analysis.candidates.Count == 1 && analysis.candidates[0].Key == "adapter-unavailable", "missing game samples created game idle or missing adapter treated as zero");
            analysis.ObserveProbe("gateway", new ProbeSample { TimestampUtc = began.AddSeconds(20), Target = "router", Countable = true, TimedOut = true });
            Check(analysis.candidates.Count == 1, "one timeout should not create a consecutive-timeout candidate");
            analysis.ObserveProbe("gateway", new ProbeSample { TimestampUtc = began.AddSeconds(21), Target = "router", Countable = true, TimedOut = true });
            analysis.ObserveProbe("gateway", new ProbeSample { TimestampUtc = began.AddSeconds(22), Target = "router", Countable = true, TimedOut = true });
            Check(analysis.candidates.Count == 2 && analysis.gateway.MaximumTimeoutStreak == 3, "continuous timeout candidates were not merged");
            analysis.ObserveProbe("gateway", new ProbeSample { TimestampUtc = began.AddSeconds(23), Target = "router", Countable = false, Status = "DNS fixture" });
            Check(analysis.gateway.Errors == 1 && analysis.gateway.Timeouts == 3 && analysis.gateway.Successes == 20, "probe errors included in timeout count or response RTT");
            analysis.ObserveProbe("gateway", new ProbeSample { TimestampUtc = began.AddSeconds(23), Target = "router", Countable = true, RoundtripMs = 999 });
            Check(analysis.gateway.Ignored == 1 && analysis.gateway.Successes == 20, "duplicate raw sample changed statistics");
            SessionAnalysis counters = new SessionAnalysis();
            long[] cumulative = new long[] { 100, 105, 2, 4 };
            for (int i = 0; i < cumulative.Length; i++)
                counters.Observe(new MonitorSnapshot { TimestampUtc = began.AddSeconds(i), Running = true, ProcessId = 12, ProcessName = "fixture", GameCollectionRequested = true, Trace = new TraceHealth { Active = true, EventsLost = cumulative[i], BuffersLost = cumulative[i] }, GameRatesAvailable = true, GameReceivedEvents = 1, GameReceiveIdleSeconds = i, GameReceiveDeliveryIdleSeconds = i, RetransmitEvents = cumulative[i], AdapterCountersAvailable = true, AdapterReceiveErrors = cumulative[i] });
            Check(counters.retransmits.Increase == 7 && counters.retransmits.Resets == 1, "cumulative retransmits counted repeatedly or reset amount added");
            Check(counters.receiveErrors.Increase == 7 && counters.receiveErrors.Resets == 1 && counters.traceEventsLost.Increase == 7 && counters.traceBuffersLost.Resets == 1, "adapter or ETW cumulative reset handling");
            bool idleFound = false;
            foreach (Candidate candidate in counters.candidates) if (candidate.Key == "game-receive-idle") idleFound = true;
            Check(idleFound, "active previously receiving game idle threshold missed");
            SessionAnalysis inactive = new SessionAnalysis();
            inactive.Observe(new MonitorSnapshot { TimestampUtc = began, Running = true, ProcessId = 12, GameCollectionRequested = true, GameRatesAvailable = false, GameReceivedEvents = 10, GameReceiveIdleSeconds = 99, Trace = new TraceHealth { Active = false, PermissionDenied = true, Status = "fixture denied" } });
            Check(inactive.inactiveSnapshots == 1 && inactive.gameAvailableSnapshots == 0 && !inactive.retransmits.HasBaseline, "inactive collection treated as usable game metrics");
            foreach (Candidate candidate in inactive.candidates) Check(candidate.Key != "game-receive-idle", "inactive trace created game network idle");
            SessionAnalysis disabled = new SessionAnalysis();
            disabled.Observe(new MonitorSnapshot { TimestampUtc = began, Running = true, ProcessId = 12, GameCollectionRequested = false, AdapterCountersAvailable = true, Trace = new TraceHealth { Active = false, Status = "user disabled" } });
            Check(disabled.inactiveSnapshots == 0 && disabled.disabledSnapshots == 1 && disabled.candidates.Count == 0, "intentional disabled capture reported as a collection fault");

            SessionAnalysis delivery = new SessionAnalysis();
            MonitorSnapshot delayed = GameFixture(began.AddSeconds(4), 4, 0.2);
            delayed.GameEventDeliveryLagMs = 3200;
            delayed.GameMaxEventDeliveryLagMs = 3500;
            delivery.Observe(delayed);
            Check(HasCandidate(delivery, "trace-delivery-delay") && !HasCandidate(delivery, "game-receive-idle"), "ETW delayed delivery misclassified as receive interruption");
            Check(delivery.deliveryDelaySnapshots == 1 && delivery.maximumDeliveryLag == 3500, "delivery quality metric missing");
            delivery.Observe(GameFixture(began.AddSeconds(8), 8, 4));
            Check(HasCandidate(delivery, "game-receive-idle"), "actual collector receive silence not represented as a candidate");
            SessionAnalysis oldFormat = new SessionAnalysis();
            oldFormat.Observe(GameFixture(began, 10, null));
            Check(!HasCandidate(oldFormat, "game-receive-idle") && oldFormat.deliveryMissingSnapshots == 1, "missing delivery freshness treated as a real receive gap");

            SessionAnalysis correlation = new SessionAnalysis();
            correlation.ObserveProbe("gateway", new ProbeSample { TimestampUtc = began, Target = "router", Countable = true, RoundtripMs = 25 });
            correlation.Observe(UploadFixture(began.AddSeconds(0.2), 12000000));
            correlation.Observe(UploadFixture(began.AddSeconds(1.2), 15000000));
            correlation.Observe(UploadFixture(began.AddSeconds(3), 20000000));
            Check(correlation.highSendSnapshots == 3 && correlation.highSendAndRttSnapshots == 2 && correlation.highSendAndRttProbes == 1, "rolling probe duplicate or stale RTT counted as an independent coincident probe");
            correlation.ObserveProbe("gateway", new ProbeSample { TimestampUtc = began.AddSeconds(4), Target = "router", Countable = true, TimedOut = true });
            correlation.Observe(UploadFixture(began.AddSeconds(4.2), 20000000));
            Check(correlation.highSendAndRttSnapshots == 2 && correlation.highSendAndRttProbes == 1, "previous response reused after a newer timeout");

            SessionAnalysis process = new SessionAnalysis();
            for (int i = 0; i < 100; i++)
            {
                MonitorSnapshot upload = UploadFixture(began.AddSeconds(i), 10000000 + i);
                upload.ProcessTraffic = new ProcessTrafficSnapshot { Available = true };
                upload.ProcessTraffic.Rows.Add(new ProcessTrafficRow { ProcessId = i + 1, ProcessName = "fixture " + i.ToString(CultureInfo.InvariantCulture), ProcessInstance = "start-" + i.ToString(CultureInfo.InvariantCulture), SentBytesPerSecond = i * 1000000, IdentityAvailable = true });
                process.Observe(upload);
            }
            Check(process.processPeaks.Count == MaximumProcessPeaks && process.processTrafficAvailableSnapshots == 100, "process peak storage is not bounded or observations not counted");
            Check(process.SummaryText().IndexOf("fixture 99", StringComparison.Ordinal) >= 0 && process.SummaryText().IndexOf("모든 어댑터", StringComparison.Ordinal) >= 0, "process peak evidence or adapter scope omitted");
            Check(Ratio(2, 12495).IndexOf("0.016%", StringComparison.Ordinal) == 0, "small timeout percentage rounded to zero");
            SessionAnalysis bounded = new SessionAnalysis();
            for (int i = 0; i < 150; i++) bounded.ObserveProbe("reference", new ProbeSample { TimestampUtc = began.AddSeconds(i * 10), Target = "reference", Countable = true, RoundtripMs = 200 });
            Check(bounded.candidates.Count == MaximumCandidates && bounded.latestCandidates.Count <= MaximumCandidates && bounded.candidateCount == 150, "candidate memory is not bounded");
            Check(bounded.SummaryText().IndexOf("고정 기준", StringComparison.Ordinal) >= 0 && bounded.SummaryText().IndexOf("확정하지 않습니다", StringComparison.Ordinal) >= 0, "rules or evidence boundary omitted");
        }

        private static MonitorSnapshot GameFixture(DateTime at, double eventIdle, double? deliveryIdle)
        {
            return new MonitorSnapshot { TimestampUtc = at, Running = true, ProcessId = 12, ProcessName = "fixture", GameCollectionRequested = true, Trace = new TraceHealth { Active = true }, GameRatesAvailable = true, GameReceivedEvents = 1, GameReceiveIdleSeconds = eventIdle, GameReceiveDeliveryIdleSeconds = deliveryIdle, AdapterCountersAvailable = true };
        }

        private static MonitorSnapshot UploadFixture(DateTime at, double bytesPerSecond)
        {
            return new MonitorSnapshot { TimestampUtc = at, Running = true, AdapterCountersAvailable = true, AdapterRatesAvailable = true, AdapterSentBytesPerSecond = bytesPerSecond };
        }

        private static bool HasCandidate(SessionAnalysis analysis, string key)
        {
            foreach (Candidate candidate in analysis.candidates) if (candidate.Key == key) return true;
            return false;
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("SessionAnalysis self-test: " + message);
        }
    }
}
