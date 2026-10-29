using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PcInternetMonitor
{
    // These are observed failures of individual checks, not proof that an ISP or
    // the entire Internet failed. Times are the first/last completed observations.
    public sealed class ConnectivityIncident
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string Target { get; set; }
        public string Category { get; set; }
        public string Scope { get; set; }
        public string State { get; set; }
        public string Summary { get; set; }
        public string EndReason { get; set; }
        public long Version { get; set; }
        public long Failures { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime LastObservedUtc { get; set; }
        // EndedUtc is intentionally reserved for an observed recovery. A stop,
        // gap or target change has ClosedUtc and State=ended instead.
        public DateTime? EndedUtc { get; set; }
        public DateTime? RecoveredUtc { get; set; }
        public DateTime? ClosedUtc { get; set; }
        public double DurationSeconds
        {
            get { return Math.Max(0, ((RecoveredUtc ?? ClosedUtc ?? LastObservedUtc) - StartedUtc).TotalSeconds); }
        }
    }

    public sealed class ConnectivityHistory
    {
        public const int RetainedHistoryLimit = 500;
        private readonly object gate = new object();
        private readonly List<ConnectivityIncident> incidents = new List<ConnectivityIncident>();
        private readonly Dictionary<string, CheckStream> streams = new Dictionary<string, CheckStream>(StringComparer.Ordinal);
        private DateTime startedUtc;
        private bool running;
        private long droppedCount;

        private sealed class CheckStream
        {
            internal string Kind, Target, PendingCategory, PendingStatus;
            internal DateTime LastUtc, FirstFailureUtc;
            internal long PendingFailures;
            internal ConnectivityIncident Active;
        }

        public event Action<ConnectivityIncident> Changed;
        public bool Running { get { lock (gate) return running; } }
        public long DroppedCount { get { lock (gate) return droppedCount; } }
        public int Count { get { lock (gate) return incidents.Count; } }

        public void Start(DateTime utc)
        {
            utc = Utc(utc);
            if (utc == DateTime.MinValue) throw new ArgumentException("측정 시작 시각이 필요합니다.", "utc");
            List<ConnectivityIncident> changes = new List<ConnectivityIncident>();
            lock (gate)
            {
                Expire(utc, changes);
                foreach (CheckStream stream in streams.Values)
                    Close(stream, utc, "새 측정 시작 · 이전 이상 회복 미확인", changes);
                streams.Clear(); startedUtc = utc; running = true;
            }
            Publish(changes);
        }

        public void Observe(InternetCheckSample sample)
        {
            if (sample == null || sample.Cancelled) return;
            string kind = CanonicalKind(sample.Kind);
            if (kind == null) return;
            string state = (sample.State ?? "").Trim().ToLowerInvariant();
            bool successful = sample.Success && state == "success";
            string category = state == "error" || state == "check_error" ? "check_error" :
                state == "failure" || state == "timeout" ? "failure" : null;
            if (!successful && category == null) return;
            DateTime at = Utc(sample.TimestampUtc), began = Utc(sample.StartedUtc);
            if (at == DateTime.MinValue || at > DateTime.UtcNow.AddSeconds(1) || (began != DateTime.MinValue && began > at)) return;
            ObserveCore(kind, Clean(sample.Target, 512), at, began, successful, category,
                Clean(sample.Status, 600), kind == "adapter" && category == "failure");
        }

        public void ObserveAdapter(DateTime utc, bool available, string status)
        {
            string value = status ?? "";
            bool disconnected = value.IndexOf("연결 끊김", StringComparison.Ordinal) >= 0 ||
                value.IndexOf("연결 끊어짐", StringComparison.Ordinal) >= 0 ||
                value.IndexOf("어댑터를 찾을 수 없음", StringComparison.Ordinal) >= 0 ||
                value.IndexOf("어댑터 없음", StringComparison.Ordinal) >= 0 ||
                value.IndexOf("disconnected", StringComparison.OrdinalIgnoreCase) >= 0;
            ObserveAdapter(utc, available, status, disconnected);
        }

        public void ObserveAdapter(DateTime utc, bool available, string status, bool disconnected)
        {
            utc = Utc(utc);
            if (utc == DateTime.MinValue || utc > DateTime.UtcNow.AddSeconds(1)) return;
            ObserveCore("adapter", "선택한 PC 연결", utc, DateTime.MinValue, available,
                available ? null : disconnected ? "failure" : "check_error", Clean(status, 600), !available && disconnected);
        }

        private void ObserveCore(string kind, string target, DateTime at, DateTime began, bool success, string category, string status, bool immediate)
        {
            List<ConnectivityIncident> changes = new List<ConnectivityIncident>();
            lock (gate)
            {
                // StartedUtc also rejects a previous generation's late result
                // that completed after a new recording began.
                if (!running || at < startedUtc || (began != DateTime.MinValue && began < startedUtc)) return;
                CheckStream stream;
                if (streams.TryGetValue(kind, out stream) && at <= stream.LastUtc) return;
                Expire(at, changes);
                if (stream == null)
                { stream = new CheckStream { Kind = kind, Target = target }; streams.Add(kind, stream); }
                else if (!String.Equals(stream.Target, target, StringComparison.Ordinal))
                {
                    Close(stream, at, "검사 대상 변경 · 이전 이상 회복 미확인", changes);
                    ResetPending(stream); stream.Target = target;
                }
                stream.LastUtc = at;
                if (success)
                {
                    if (stream.Active != null)
                    {
                        ConnectivityIncident incident = stream.Active;
                        incident.LastObservedUtc = at; incident.EndedUtc = incident.RecoveredUtc = at;
                        incident.State = "recovered"; incident.EndReason = "성공 결과 관측";
                        incident.Summary = incident.Scope + " 회복 관측"; incident.Version++;
                        changes.Add(Copy(incident)); stream.Active = null;
                    }
                    ResetPending(stream);
                }
                else
                {
                    if (stream.Active != null && stream.Active.Category != category)
                        Close(stream, at, "검사 결과 종류 변경 · 이전 이상 회복 미확인", changes);
                    if (stream.PendingCategory != category)
                    { ResetPending(stream); stream.PendingCategory = category; stream.FirstFailureUtc = at; }
                    if (stream.PendingFailures == 0) stream.FirstFailureUtc = at;
                    stream.PendingFailures++; stream.PendingStatus = status;
                    if (stream.Active == null && (immediate || stream.PendingFailures >= 2))
                    {
                        ConnectivityIncident incident = new ConnectivityIncident {
                            Id = Guid.NewGuid().ToString("N"), Kind = kind, Target = target, Category = category,
                            Scope = Scope(kind), State = "open", Version = 1, StartedUtc = stream.FirstFailureUtc,
                            LastObservedUtc = at, Failures = stream.PendingFailures, EndReason = ""
                        };
                        incident.Summary = FailureSummary(incident, status); stream.Active = incident;
                        Retain(incident); changes.Add(Copy(incident));
                    }
                    else if (stream.Active != null)
                    {
                        stream.Active.LastObservedUtc = at; stream.Active.Failures++;
                        stream.Active.Summary = FailureSummary(stream.Active, status); stream.Active.Version++;
                        changes.Add(Copy(stream.Active));
                    }
                }
            }
            Publish(changes);
        }

        // Poll may call this even when a worker has produced no result. A gap is
        // never filled with invented failures, duration or a recovered outcome.
        public void CheckGaps(DateTime utc)
        {
            utc = Utc(utc);
            if (utc == DateTime.MinValue || utc > DateTime.UtcNow.AddSeconds(1)) return;
            List<ConnectivityIncident> changes = new List<ConnectivityIncident>();
            lock (gate) { if (!running || utc < startedUtc) return; Expire(utc, changes); }
            Publish(changes);
        }

        private void Expire(DateTime now, List<ConnectivityIncident> changes)
        {
            foreach (CheckStream stream in streams.Values)
            {
                double limit = stream.Kind == "dns" || stream.Kind == "https" ? 20 : 5;
                if (stream.LastUtc != DateTime.MinValue && (now - stream.LastUtc).TotalSeconds > limit)
                {
                    Close(stream, stream.LastUtc, "검사 갱신 공백 · 이후 상태 미확인", changes);
                    ResetPending(stream);
                }
            }
        }

        public void Stop(DateTime utc, string reason)
        {
            utc = Utc(utc);
            List<ConnectivityIncident> changes = new List<ConnectivityIncident>();
            lock (gate)
            {
                if (!running) return;
                running = false;
                Expire(utc, changes);
                foreach (CheckStream stream in streams.Values)
                { Close(stream, utc, "측정 종료 · 회복 미확인" + (String.IsNullOrWhiteSpace(reason) ? "" : " · " + Clean(reason, 160)), changes); ResetPending(stream); }
                streams.Clear();
            }
            Publish(changes);
        }

        public IList<ConnectivityIncident> GetRecent(int count)
        {
            lock (gate)
            {
                if (count <= 0) return new List<ConnectivityIncident>();
                return incidents.AsEnumerable().Reverse().Take(Math.Min(count, RetainedHistoryLimit)).Select(Copy).ToList();
            }
        }

        private void Retain(ConnectivityIncident incident)
        {
            if (incidents.Count >= RetainedHistoryLimit)
            {
                // With at most five streams, an old closed incident is always
                // available before the cap. Never discard an active record.
                int at = incidents.FindIndex(x => x.State != "open");
                if (at < 0) throw new InvalidOperationException("Connectivity history has no removable closed record.");
                incidents.RemoveAt(at); droppedCount++;
            }
            incidents.Add(incident);
        }

        private static void Close(CheckStream stream, DateTime at, string reason, List<ConnectivityIncident> changes)
        {
            if (stream.Active == null) return;
            ConnectivityIncident incident = stream.Active;
            incident.State = "ended"; incident.ClosedUtc = at < incident.LastObservedUtc ? incident.LastObservedUtc : at;
            incident.EndReason = reason; incident.Summary = incident.Scope + " 관측 종료 · 회복 미확인"; incident.Version++;
            changes.Add(Copy(incident)); stream.Active = null; ResetPending(stream);
        }

        private static void ResetPending(CheckStream stream)
        { stream.PendingFailures = 0; stream.FirstFailureUtc = DateTime.MinValue; stream.PendingCategory = stream.PendingStatus = null; }

        private static string FailureSummary(ConnectivityIncident incident, string status)
        {
            string summary = incident.Scope + (incident.Category == "check_error" ? " 검사 자체 오류" : incident.Kind == "adapter" ? " 끊김 관측" : " 검사 실패 관측");
            return summary + " · " + incident.Failures.ToString(CultureInfo.InvariantCulture) + "회" + (String.IsNullOrEmpty(status) ? "" : " · " + status);
        }

        private static string CanonicalKind(string kind)
        {
            switch ((kind ?? "").Trim().ToLowerInvariant())
            {
                case "dns": return "dns";
                case "https": return "https";
                case "gateway": case "router": return "gateway";
                case "reference": case "internet": case "external": return "reference";
                case "adapter": case "pc": return "adapter";
                default: return null;
            }
        }
        private static string Scope(string kind)
        { return kind == "dns" ? "DNS 이름 조회" : kind == "https" ? "HTTPS 접속" : kind == "gateway" ? "공유기 응답" : kind == "reference" ? "외부 응답" : "PC 연결"; }
        private static string Clean(string text, int limit)
        { text = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim(); return text.Length <= limit ? text : text.Substring(0, limit); }
        private static DateTime Utc(DateTime value)
        { return value == DateTime.MinValue ? DateTime.MinValue : value.Kind == DateTimeKind.Utc ? value : value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc); }
        private void Publish(List<ConnectivityIncident> changes)
        { foreach (ConnectivityIncident incident in changes) { Action<ConnectivityIncident> handler = Changed; if (handler != null) handler(incident); } }
        private static ConnectivityIncident Copy(ConnectivityIncident value)
        {
            return new ConnectivityIncident { Id = value.Id, Kind = value.Kind, Target = value.Target, Category = value.Category,
                Scope = value.Scope, State = value.State, Summary = value.Summary, EndReason = value.EndReason,
                Version = value.Version, Failures = value.Failures, StartedUtc = value.StartedUtc, LastObservedUtc = value.LastObservedUtc,
                EndedUtc = value.EndedUtc, RecoveredUtc = value.RecoveredUtc, ClosedUtc = value.ClosedUtc };
        }

        public static void SelfTest()
        {
            DateTime at = DateTime.UtcNow.AddHours(-2);
            ConnectivityHistory history = new ConnectivityHistory(); List<ConnectivityIncident> changes = new List<ConnectivityIncident>();
            history.Changed += delegate(ConnectivityIncident row) { changes.Add(row); row.Summary = "mutated subscriber"; };
            history.Start(at);
            history.Observe(Sample("dns", at.AddSeconds(1), "failure")); Check(history.Count == 0, "single failure became an incident");
            history.Observe(Sample("dns", at.AddSeconds(6), "timeout"));
            ConnectivityIncident incident = history.GetRecent(1)[0];
            Check(incident.State == "open" && incident.StartedUtc == at.AddSeconds(1) && incident.Failures == 2 && incident.Version == 1, "consecutive failures were not grouped");
            Check(!incident.Summary.Contains("mutated subscriber"), "event mutation changed retained history");
            incident.State = "foreign mutation"; Check(history.GetRecent(1)[0].State == "open", "reader mutation changed retained history");
            history.Observe(Sample("dns", at.AddSeconds(6), "failure")); history.Observe(Sample("dns", at.AddSeconds(5), "failure"));
            Check(history.GetRecent(1)[0].Failures == 2, "duplicate/older callback was counted");
            history.Observe(Sample("dns", at.AddSeconds(11), "success")); incident = history.GetRecent(1)[0];
            Check(incident.State == "recovered" && incident.RecoveredUtc == at.AddSeconds(11) && incident.EndedUtc == incident.RecoveredUtc && incident.ClosedUtc == null && incident.DurationSeconds == 10, "recovery boundary was not observed");
            Check(changes.Count == 2 && changes[0].State == "open" && changes[1].State == "recovered", "immutable lifecycle events are missing");

            history.Observe(Sample("https", at.AddSeconds(12), "error")); history.Observe(Sample("https", at.AddSeconds(17), "error"));
            incident = history.GetRecent(1)[0]; Check(incident.Category == "check_error" && !incident.Summary.Contains("인터넷 장애"), "local check errors became Internet failures");
            history.Stop(at.AddSeconds(18), "user stop"); incident = history.GetRecent(1)[0];
            Check(incident.State == "ended" && incident.ClosedUtc == at.AddSeconds(18) && !incident.RecoveredUtc.HasValue && !incident.EndedUtc.HasValue, "stop was called recovery");
            history.Observe(Sample("https", at.AddSeconds(19), "success")); Check(history.GetRecent(1)[0].State == "ended", "late stopped callback modified history");
            history.Start(at.AddSeconds(20)); InternetCheckSample oldGeneration = Sample("https", at.AddSeconds(21), "failure"); oldGeneration.StartedUtc = at.AddSeconds(19); history.Observe(oldGeneration);
            history.Observe(Sample("https", at.AddSeconds(25), "failure")); Check(history.Count == 2, "late previous generation counted toward a new incident");
            history.Observe(Sample("https", at.AddSeconds(30), "failure")); Check(history.Count == 3, "fresh session could not create a new incident");
            history.CheckGaps(at.AddSeconds(51)); incident = history.GetRecent(1)[0];
            Check(incident.State == "ended" && incident.ClosedUtc == at.AddSeconds(30) && !incident.RecoveredUtc.HasValue, "gap invented continued failure or recovery");
            history.Observe(Sample("https", at.AddSeconds(52), "failure")); Check(history.Count == 3, "failure continuity crossed a monitoring gap");
            history.Observe(Sample("https", at.AddSeconds(57), "success")); Check(history.Count == 3, "success created an incident from one failure");

            history.ObserveAdapter(at.AddSeconds(58), false, "이 PC 어댑터 통계 접근 권한 오류"); Check(history.Count == 3, "adapter permission failure became an immediate disconnect");
            history.ObserveAdapter(at.AddSeconds(59), false, "이 PC 어댑터 통계 미지원"); Check(history.GetRecent(1)[0].Category == "check_error", "adapter read error classification was lost");
            history.ObserveAdapter(at.AddSeconds(60), true, "connected");
            history.ObserveAdapter(at.AddSeconds(61), false, "이 PC 어댑터 연결 끊김"); Check(history.GetRecent(1)[0].Category == "failure" && history.GetRecent(1)[0].Failures == 1, "confirmed link-down was not immediate");
            history.ObserveAdapter(at.AddSeconds(62), true, "connected");

            int before = history.Count; InternetCheckSample future = Sample("dns", DateTime.UtcNow.AddHours(1), "failure"); history.Observe(future);
            InternetCheckSample staleTimeout = Sample("dns", at.AddSeconds(63), "stale"); staleTimeout.TimedOut = true; history.Observe(staleTimeout);
            InternetCheckSample stoppedTimeout = Sample("dns", at.AddSeconds(64), "stopped"); stoppedTimeout.TimedOut = true; history.Observe(stoppedTimeout);
            history.Observe(Sample("dns", at.AddSeconds(64), "cancelled"));
            Check(history.Count == before, "future/stale/cancelled result became a failure");
            history.Observe(Sample("dns", at.AddSeconds(65), "failure")); InternetCheckSample target = Sample("dns", at.AddSeconds(70), "failure"); target.Target = "another.example"; history.Observe(target);
            Check(history.Count == before, "failures crossed a target change");
            history.Observe(Sample("dns", at.AddSeconds(71), "error")); history.Observe(Sample("dns", at.AddSeconds(76), "success"));
            Check(history.Count == before, "mixed result categories became consecutive failures");

            ConnectivityHistory bounded = new ConnectivityHistory(); bounded.Start(at);
            for (int i = 0; i < RetainedHistoryLimit + 3; i++)
            { DateTime time = at.AddSeconds(i * 3 + 1); bounded.ObserveAdapter(time, false, "연결 끊김", true); bounded.ObserveAdapter(time.AddSeconds(1), true, "connected"); }
            Check(bounded.Count == RetainedHistoryLimit && bounded.DroppedCount == 3 && bounded.GetRecent(Int32.MaxValue).Count == RetainedHistoryLimit, "history cap or omission count is inaccurate");
            bounded.Stop(at.AddHours(1), "test");
        }
        private static InternetCheckSample Sample(string kind, DateTime timestamp, string state)
        { return new InternetCheckSample { Kind = kind, Target = "test.example", State = state, Status = "synthetic " + state, TimestampUtc = timestamp, StartedUtc = timestamp.AddMilliseconds(-1), Success = state == "success", TimedOut = state == "timeout", Cancelled = state == "cancelled" }; }
        private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException("Connectivity history test: " + reason); }
    }
}
