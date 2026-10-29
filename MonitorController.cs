using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PcInternetMonitor
{
    public sealed class MonitorController : IDisposable
    {
        private readonly object gate = new object();
        private readonly KernelNetworkMonitor trace = new KernelNetworkMonitor();
        private readonly TcpRttMonitor tcpRtt = new TcpRttMonitor();
        private readonly ProbeRunner gatewayProbe = new ProbeRunner();
        private readonly ProbeRunner referenceProbe = new ProbeRunner();
        private readonly TrafficWindow traffic = new TrafficWindow();
        private readonly ProbeWindow gateway = new ProbeWindow();
        private readonly ProbeWindow reference = new ProbeWindow();
        private AdapterReader adapter;
        private AdapterSample baseline, previous;
        private readonly AdapterCounterTotals adapterTotals = new AdapterCounterTotals();
        private bool running, disposed;
        private int pid;
        private string processName;
        private DateTime processStarted, began;
        private bool processEnded;
        private bool gameRequested, processesRequested;
        private long captureGeneration;
        public event Action<string, ProbeSample> ProbeSampled;

        public MonitorController()
        {
            trace.EventReceived += AcceptTraffic;
            gatewayProbe.Sampled += delegate(ProbeSample s) { AcceptProbe("gateway", s, gateway); };
            referenceProbe.Sampled += delegate(ProbeSample s) { AcceptProbe("reference", s, reference); };
        }

        private void AcceptTraffic(TrafficEvent e)
        {
            e.ObservedUtc = DateTime.UtcNow;
            lock (gate) { if (running && e.CaptureGeneration == captureGeneration && !processEnded && e.ProcessId == pid && e.TimestampUtc >= began) traffic.Add(e); }
        }

        private void AcceptProbe(string kind, ProbeSample sample, ProbeWindow window)
        {
            // The subscriber only queues output and updates bounded analysis state.
            // Serializing acceptance with Stop prevents replies reaching a later session.
            lock (gate)
            {
                if (!running || sample.TimestampUtc < began) return;
                window.Add(sample);
                Action<string, ProbeSample> handler = ProbeSampled;
                if (handler != null) handler(kind, sample);
            }
        }

        public void Start(int processId, string adapterId, string gatewayTarget, string referenceTarget, bool collectGame, bool collectProcesses = false)
        {
            Stop();
            if (disposed) throw new ObjectDisposedException("MonitorController");
            if (!String.IsNullOrEmpty(gatewayTarget) && !ProbeRunner.IsValidTarget(gatewayTarget)) throw new ArgumentException("공유기 대상은 IP 주소 또는 호스트 이름으로 입력하세요.");
            if (!String.IsNullOrEmpty(referenceTarget) && !ProbeRunner.IsValidTarget(referenceTarget)) throw new ArgumentException("외부 비교 대상은 IP 주소 또는 호스트 이름으로 입력하세요.");
            if (!String.IsNullOrEmpty(gatewayTarget)) gatewayTarget = ProbeRunner.NormalizeTarget(gatewayTarget);
            if (!String.IsNullOrEmpty(referenceTarget)) referenceTarget = ProbeRunner.NormalizeTarget(referenceTarget);
            DateTime started = DateTime.MinValue;
            string name = "선택 안 함";
            if (processId > 0)
            {
                using (Process p = Process.GetProcessById(processId))
                {
                    if (p.HasExited) throw new ArgumentException("선택한 프로그램이 종료됐습니다. 목록을 새로고침하세요.");
                    name = p.ProcessName;
                    try { started = p.StartTime.ToUniversalTime(); } catch (Exception) { }
                }
                if (collectGame && started == DateTime.MinValue) throw new ArgumentException("게임 프로세스의 시작 시각을 확인할 수 없습니다. 관리자 권한을 확인하거나 게임을 ‘선택 안 함’으로 바꿔 PC·공유기·외부 ICMP를 측정하세요.");
            }
            lock (gate)
            {
                pid = processId; processName = name; processStarted = started; processEnded = false;
                captureGeneration++;
                gameRequested = collectGame && processId > 0; processesRequested = collectProcesses;
                traffic.Clear(); gateway.Clear(gatewayTarget); reference.Clear(referenceTarget);
                adapter = new AdapterReader(adapterId);
                baseline = previous = adapter.Read();
                adapterTotals.Clear(); adapterTotals.Update(baseline);
                began = DateTime.UtcNow; running = true;
            }
            if (!String.IsNullOrEmpty(gatewayTarget)) gatewayProbe.Start(gatewayTarget);
            if (!String.IsNullOrEmpty(referenceTarget)) referenceProbe.Start(referenceTarget);
            if (gameRequested) tcpRtt.Start(processId, started);
            if (gameRequested || collectProcesses) trace.Start(gameRequested ? processId : 0, collectProcesses, captureGeneration);
        }

        public MonitorSnapshot Snapshot()
        {
            return SnapshotCore(null);
        }

        private MonitorSnapshot SnapshotCore(TraceStopResult final)
        {
            bool stopTrace = false;
            MonitorSnapshot s;
            lock (gate)
            {
                DateTime now = DateTime.UtcNow;
                s = new MonitorSnapshot { TimestampUtc = now, Running = running, IsFinal = final != null, ProcessId = pid, ProcessName = processName, GameCollectionRequested = gameRequested, ProcessCollectionRequested = processesRequested };
                s.Trace = final == null ? trace.GetHealth() : final.Health;
                if (final != null) { s.StopDrainComplete = final.Drained; s.StopBoundaryStatus = final.Status; s.StopRequestedUtc = final.RequestedUtc; }
                if (running && pid > 0 && !processEnded)
                {
                    try
                    {
                        using (Process p = Process.GetProcessById(pid))
                        {
                            if (p.HasExited) processEnded = true;
                            else if (processStarted != DateTime.MinValue)
                                try { if (p.StartTime.ToUniversalTime() != processStarted) processEnded = true; } catch (System.ComponentModel.Win32Exception) { }
                        }
                    }
                    catch (ArgumentException) { processEnded = true; }
                    catch (InvalidOperationException) { processEnded = true; }
                    catch (System.ComponentModel.Win32Exception) { }
                    if (processEnded && !processesRequested) stopTrace = true;
                }
                s.GameStatus = !running ? "측정 중지" : pid <= 0 ? "게임 선택 안 함 · PC/공유기 측정 중" : processEnded ? "선택한 게임 종료됨 · 새 프로세스를 선택하세요" : processName + " · PID " + pid.ToString(CultureInfo.InvariantCulture);
                s.GameProcessEnded = processEnded;
                s.TcpRtt = tcpRtt.Snapshot(now);
                if (!running || processEnded)
                {
                    s.TcpRtt.Available = false; s.TcpRtt.MinimumMs = s.TcpRtt.MaximumMs = null;
                    s.TcpRtt.State = processEnded ? "process_ended" : "stopped";
                    s.TcpRtt.Status = processEnded ? "게임 종료" : "측정 중지";
                }
                traffic.Fill(s, now, began);
                s.GameRatesAvailable = gameRequested && (s.GameSentEvents + s.GameReceivedEvents + s.RetransmitEvents > 0) && (final != null || (running && !processEnded && s.Trace.Active));
                if (final != null) s.ProcessTraffic = final.ProcessTraffic;
                else if (running && processesRequested) s.ProcessTraffic = trace.GetProcessTraffic(now);
                s.Gateway = gateway.Summary(now);
                s.Reference = reference.Summary(now);
                if (adapter != null)
                {
                    s.AdapterExpected = true;
                    AdapterSample current = adapter.Read();
                    s.AdapterStatus = current.Status;
                    FillAdapter(s, current, previous, baseline);
                    AdapterCounterTotalsSnapshot counters = adapterTotals.Update(current);
                    s.AdapterCountersAvailable = counters.Available;
                    s.AdapterReceiveErrors = counters.ReceiveErrors; s.AdapterSendErrors = counters.SendErrors;
                    s.AdapterReceiveDiscards = counters.ReceiveDiscards; s.AdapterSendDiscards = counters.SendDiscards;
                    s.AdapterCounterResets = counters.ResetCount; s.AdapterCounterGaps = counters.GapCount;
                    if (counters.HasDiscontinuity) s.AdapterStatus += " · 카운터 초기화 " + counters.ResetCount + "회 / 관측 공백 " + counters.GapCount + "회 · 관측 증가량 보존, 경계 구간 미확인";
                    previous = current;
                    if (current.Available && (baseline == null || !baseline.Available || CounterReset(current, baseline))) baseline = current;
                }
                if (!running && final == null) { s.AdapterRatesAvailable = false; s.AdapterCountersAvailable = false; }
            }
            if (stopTrace) { trace.Stop(); s.Trace = trace.GetHealth(); }
            return s;
        }

        private static bool CounterReset(AdapterSample a, AdapterSample b)
        {
            return a.ReceivedBytes < b.ReceivedBytes || a.SentBytes < b.SentBytes || a.ReceiveErrors < b.ReceiveErrors || a.SendErrors < b.SendErrors || a.ReceiveDiscards < b.ReceiveDiscards || a.SendDiscards < b.SendDiscards;
        }

        private static void FillAdapter(MonitorSnapshot s, AdapterSample a, AdapterSample b, AdapterSample first)
        {
            if (!a.Available || b == null || !b.Available || first == null || !first.Available || CounterReset(a, b) || CounterReset(a, first)) return;
            s.AdapterCountersAvailable = true;
            double seconds = (a.TimestampUtc - b.TimestampUtc).TotalSeconds;
            if (seconds <= 0) return;
            s.AdapterRatesAvailable = true;
            s.AdapterReceivedBytesPerSecond = (a.ReceivedBytes - b.ReceivedBytes) / seconds;
            s.AdapterSentBytesPerSecond = (a.SentBytes - b.SentBytes) / seconds;
            s.AdapterReceivedPacketsPerSecond = Math.Max(0, a.ReceivedPackets - b.ReceivedPackets) / seconds;
            s.AdapterSentPacketsPerSecond = Math.Max(0, a.SentPackets - b.SentPackets) / seconds;
            s.AdapterReceiveErrors = a.ReceiveErrors - first.ReceiveErrors;
            s.AdapterSendErrors = a.SendErrors - first.SendErrors;
            s.AdapterReceiveDiscards = a.ReceiveDiscards - first.ReceiveDiscards;
            s.AdapterSendDiscards = a.SendDiscards - first.SendDiscards;
        }

        public void Stop()
        {
            lock (gate) running = false;
            gatewayProbe.Stop(); referenceProbe.Stop(); trace.Stop(); tcpRtt.Stop();
        }

        public MonitorSnapshot StopAndSnapshot(int drainTimeoutMilliseconds = 2500)
        {
            DateTime requested = DateTime.UtcNow;
            bool needsTrace;
            lock (gate) { if (!running) return null; needsTrace = gameRequested || processesRequested; }
            TraceStopResult final;
            TcpRttSnapshot lastTcp = null;
            try
            {
                gatewayProbe.Stop(); referenceProbe.Stop();
                // Keep acceptance enabled while the ETW consumer drains. Never wait under gate.
                if (needsTrace) final = trace.StopAndCapture(drainTimeoutMilliseconds);
                else { trace.Stop(); final = new TraceStopResult { Drained = true, Health = trace.GetHealth(), Status = "ETW 미요청 · 최종 PC/ICMP 표본 반영" }; }
            }
            finally
            {
                try { lastTcp = tcpRtt.Snapshot(DateTime.UtcNow); tcpRtt.Stop(); }
                finally { lock (gate) running = false; }
            }
            final.RequestedUtc = requested;
            lock (gate)
            {
                running = false;
                MonitorSnapshot snapshot = SnapshotCore(final);
                if (lastTcp != null && lastTcp.ProcessId == pid) snapshot.TcpRtt = lastTcp;
                return snapshot;
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            Stop(); disposed = true; gatewayProbe.Dispose(); referenceProbe.Dispose(); trace.Dispose(); tcpRtt.Dispose();
        }

        public static void SelfTest()
        {
            DateTime now = DateTime.UtcNow;
            using (MonitorController controller = new MonitorController())
            {
                controller.running = true; controller.captureGeneration = 2; controller.pid = 42; controller.began = now.AddSeconds(-1);
                controller.AcceptTraffic(new TrafficEvent { CaptureGeneration = 1, ProcessId = 42, TimestampUtc = now, Bytes = 1024 });
                controller.AcceptTraffic(new TrafficEvent { CaptureGeneration = 2, ProcessId = 42, TimestampUtc = now.AddSeconds(-2), Bytes = 1024 });
                controller.AcceptTraffic(new TrafficEvent { CaptureGeneration = 2, ProcessId = 41, TimestampUtc = now, Bytes = 1024 });
                controller.AcceptTraffic(new TrafficEvent { CaptureGeneration = 2, ProcessId = 42, TimestampUtc = now, Bytes = 512 });
                controller.running = false;
                controller.AcceptTraffic(new TrafficEvent { CaptureGeneration = 2, ProcessId = 42, TimestampUtc = now, Bytes = 1024 });
                MonitorSnapshot accepted = new MonitorSnapshot(); controller.traffic.Fill(accepted, now, controller.began);
                Check(accepted.GameReceivedEvents == 1, "old generation, pre-start, wrong PID or stopped callbacks contaminated the session");
            }
            ProbeWindow p = new ProbeWindow(); p.Clear("127.0.0.1");
            p.Add(new ProbeSample { TimestampUtc = now.AddSeconds(-3), Target = "127.0.0.1", RoundtripMs = 10, Countable = true, Status = "응답" });
            p.Add(new ProbeSample { TimestampUtc = now.AddSeconds(-2), Target = "127.0.0.1", RoundtripMs = 14, Countable = true, Status = "응답" });
            p.Add(new ProbeSample { TimestampUtc = now.AddSeconds(-1), Target = "127.0.0.1", TimedOut = true, Countable = true, Status = "시간 초과" });
            p.Add(new ProbeSample { TimestampUtc = now, Target = "127.0.0.1", Status = "DNS 오류" });
            ProbeSummary ps = p.Summary(now);
            Check(ps.Sent == 3 && ps.Missing == 1 && ps.Errors == 1 && Math.Abs(ps.MissingPercent.Value - 100.0 / 3) < .00001 && ps.VariationMs == 4 && !ps.CurrentMs.HasValue, "probe denominators");
            Check(p.Summary(now.AddSeconds(61)).Sent == 0, "probe rolling expiry");
            Check(p.Summary(now.AddSeconds(61)).Stale, "expired probes must remain stale, not return to initial waiting");
            ProbeWindow broken = new ProbeWindow(); broken.Clear("fixture");
            broken.Add(new ProbeSample { TimestampUtc = now, Target = "fixture", Countable = true, RoundtripMs = 1 });
            broken.Add(new ProbeSample { TimestampUtc = now.AddSeconds(1), Target = "fixture", Status = "도달 불가" });
            Check(broken.Summary(now.AddSeconds(1)).CurrentError && broken.Summary(now.AddSeconds(1)).MissingPercent == 0, "latest probe failure is independent of historical zero timeout ratio");
            TrafficWindow tw = new TrafficWindow();
            tw.Add(new TrafficEvent { TimestampUtc = now, ProcessId = 1, Protocol = "UDP", IsSend = false, Bytes = 100, SourceAddress = "1.2.3.4", SourcePort = 12, DestinationAddress = "127.0.0.1", DestinationPort = 34 });
            tw.Add(new TrafficEvent { TimestampUtc = now, ProcessId = 1, Protocol = "TCP", IsSend = true, IsRetransmit = true, Bytes = 80, SourceAddress = "127.0.0.1", SourcePort = 35, DestinationAddress = "1.2.3.4", DestinationPort = 13 });
            MonitorSnapshot ts = new MonitorSnapshot(); tw.Fill(ts, now, now.AddSeconds(-2));
            Check(ts.GameReceivedEvents == 1 && ts.GameSentEvents == 0 && ts.RetransmitEvents == 1, "retransmit not counted as normal send");
            Check(ts.Endpoints.Count == 2 && ts.Endpoints[0].Remote != null && ts.GameReceivedBytesPerSecond == 50, "endpoint and rolling rate");
            tw.Fill(ts = new MonitorSnapshot(), now.AddSeconds(4), now.AddSeconds(-2));
            Check(ts.GameReceivedBytesPerSecond == 0 && ts.GameReceiveIdleSeconds == 4, "idle rates and time since receive");
            TrafficWindow burst = new TrafficWindow();
            for (int i = 0; i < 50001; i++) burst.Add(new TrafficEvent { TimestampUtc = now, Protocol = "UDP", IsSend = true, Bytes = 2, SourceAddress = "127.0.0.1", DestinationAddress = "127.0.0.1" });
            MonitorSnapshot burstResult = new MonitorSnapshot(); burst.Fill(burstResult, now, now.AddSeconds(-2));
            Check(burstResult.GameSentEvents == 50001 && burstResult.GameSentBytesPerSecond == 50001, "bounded bucket preserves high-rate totals");
            MonitorSnapshot ns = new MonitorSnapshot();
            FillAdapter(ns, new AdapterSample { Available = true, TimestampUtc = now, ReceivedBytes = 200, SentBytes = 100 }, new AdapterSample { Available = true, TimestampUtc = now.AddSeconds(-1), ReceivedBytes = 50, SentBytes = 40 }, new AdapterSample { Available = true });
            Check(ns.AdapterReceivedBytesPerSecond == 150 && ns.AdapterSentBytesPerSecond == 60, "adapter rate");
            ns = new MonitorSnapshot(); FillAdapter(ns, new AdapterSample { Available = true, TimestampUtc = now, ReceivedBytes = 5 }, new AdapterSample { Available = true, TimestampUtc = now.AddSeconds(-1), ReceivedBytes = 50 }, new AdapterSample { Available = true });
            Check(!ns.AdapterRatesAvailable, "adapter reset not negative");
            Check(!ns.AdapterCountersAvailable, "adapter reset not reported as zero counters");
            TrafficWindow delayed = new TrafficWindow();
            delayed.Add(new TrafficEvent { TimestampUtc = now.AddSeconds(-5), ObservedUtc = now.AddSeconds(-.1), Protocol = "UDP", Bytes = 100 });
            MonitorSnapshot ds = new MonitorSnapshot(); delayed.Fill(ds, now, now.AddSeconds(-10));
            Check(ds.GameReceivedBytesPerSecond == 50 && ds.GameReceiveIdleSeconds == 5 && Math.Abs(ds.GameReceiveDeliveryIdleSeconds.Value - .1) < .001 && ds.GameEventDeliveryLagMs == 4900, "late events retained in delivery rate and idle separated");
            delayed.Add(new TrafficEvent { TimestampUtc = now.AddSeconds(-7), ObservedUtc = now, Protocol = "UDP", Bytes = 100 });
            ds = new MonitorSnapshot(); delayed.Fill(ds, now, now.AddSeconds(-10));
            Check(ds.GameReceiveIdleSeconds == 5 && ds.GameReceiveDeliveryIdleSeconds == 0 && ds.GameMaxEventDeliveryLagMs == 7000, "out-of-order event does not rewind latest occurrence");
            TrafficWindow manyEndpoints = new TrafficWindow();
            for (int i = 0; i < 600; i++)
                manyEndpoints.Add(new TrafficEvent { TimestampUtc = now.AddTicks(i), ObservedUtc = now, Protocol = "UDP", IsSend = true,
                    SourceAddress = "127.0.0.1", SourcePort = 4000, DestinationAddress = "203.0.113.1", DestinationPort = 5000 + i, Bytes = 1 });
            MonitorSnapshot bounded = new MonitorSnapshot(); manyEndpoints.Fill(bounded, now, now.AddSeconds(-2));
            Check(bounded.Endpoints.Count == 100 && bounded.EndpointObservations.Count == 512 && bounded.GameEndpointEvictions == 88,
                "endpoint logging has a separate full retained set and visible eviction count");
            MonitorSnapshot packetRates = new MonitorSnapshot { AdapterRatesAvailable = true, AdapterSentPacketsPerSecond = 72.5, AdapterReceivedPacketsPerSecond = 40, ProcessCollectionRequested = true, GameEndpointEvictions = 88 };
            string[] csvColumns = SessionLog.CsvHeader.Split(','), csvValues = SessionLog.ToCsvRow(packetRates).Split(',');
            Check(csvColumns.Length == csvValues.Length && csvValues[Array.IndexOf(csvColumns, "pc_adapter_send_packets_s")] == "72.5" &&
                csvValues[Array.IndexOf(csvColumns, "pc_adapter_receive_packets_s")] == "40" && csvValues[Array.IndexOf(csvColumns, "game_endpoint_evictions_total")] == "88",
                "new adapter packet rates and endpoint eviction CSV fields preserve values");
        }
        private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException("Monitor test: " + message); }
    }

    internal sealed class ProbeWindow
    {
        private readonly Queue<ProbeSample> samples = new Queue<ProbeSample>();
        private string target;
        private ProbeSample lastSample;
        public void Clear(string value) { samples.Clear(); lastSample = null; target = value ?? ""; }
        public void Add(ProbeSample sample) { if (sample.Target == target) { samples.Enqueue(sample); lastSample = sample; Trim(sample.TimestampUtc); } }
        private void Trim(DateTime now) { while (samples.Count > 0 && (now - samples.Peek().TimestampUtc).TotalSeconds > 60) samples.Dequeue(); }
        public ProbeSummary Summary(DateTime now)
        {
            Trim(now);
            ProbeSummary s = new ProbeSummary { Target = target, Status = String.IsNullOrEmpty(target) ? "검사 꺼짐" : "응답 대기" };
            double sum = 0, diffs = 0; int count = 0, pairs = 0; double? last = null;
            ProbeSample latest = lastSample;
            foreach (ProbeSample sample in samples)
            {
                latest = sample;
                if (sample.Countable) { s.Sent++; if (sample.TimedOut) s.Missing++; } else s.Errors++;
                if (sample.RoundtripMs.HasValue)
                {
                    sum += sample.RoundtripMs.Value; count++;
                    if (last.HasValue) { diffs += Math.Abs(sample.RoundtripMs.Value - last.Value); pairs++; }
                }
                last = sample.RoundtripMs;
            }
            if (s.Sent > 0) s.MissingPercent = 100.0 * s.Missing / s.Sent;
            if (count > 0) s.AverageMs = sum / count;
            if (pairs > 0) s.VariationMs = diffs / pairs;
            if (latest != null)
            {
                s.LastSampleUtc = latest.TimestampUtc;
                double age = (now - latest.TimestampUtc).TotalSeconds;
                s.Stale = age < 0 || age >= 3;
                s.CurrentError = !s.Stale && (!latest.Countable || (!latest.TimedOut && !latest.RoundtripMs.HasValue));
                s.CurrentTimedOut = !s.Stale && latest.Countable && latest.TimedOut;
                s.Status = latest.Status;
                if (!s.Stale) s.CurrentMs = latest.RoundtripMs;
            }
            return s;
        }
    }

    internal sealed class TrafficWindow
    {
        private sealed class Bucket { public long SendBytes, ReceiveBytes, SendEvents, ReceiveEvents, DelayEvents; public double DelayTotal, DelayMax; }
        private const long BucketTicks = TimeSpan.TicksPerMillisecond * 100;
        private readonly Dictionary<long, Bucket> recent = new Dictionary<long, Bucket>();
        private readonly Dictionary<string, EndpointRow> endpoints = new Dictionary<string, EndpointRow>();
        private long sends, receives, retransmits, invalidTimes, endpointEvictions;
        private DateTime lastReceive, lastReceiveObserved;
        public void Clear() { recent.Clear(); endpoints.Clear(); sends = receives = retransmits = invalidTimes = endpointEvictions = 0; lastReceive = lastReceiveObserved = DateTime.MinValue; }
        public void Add(TrafficEvent e)
        {
            DateTime observed = e.ObservedUtc == DateTime.MinValue ? e.TimestampUtc : e.ObservedUtc;
            if (e.IsRetransmit) retransmits++; else if (e.IsSend) sends++; else {
                receives++;
                if (e.TimestampUtc > lastReceive) lastReceive = e.TimestampUtc;
                if (observed > lastReceiveObserved) lastReceiveObserved = observed;
            }
            Trim(observed);
            long tick = observed.Ticks / BucketTicks;
            Bucket bucket;
            if (!recent.TryGetValue(tick, out bucket)) { bucket = new Bucket(); recent.Add(tick, bucket); }
            double lag = (observed - e.TimestampUtc).TotalMilliseconds;
            if (lag >= 0) { bucket.DelayEvents++; bucket.DelayTotal += lag; bucket.DelayMax = Math.Max(bucket.DelayMax, lag); }
            else invalidTimes++;
            if (!e.IsRetransmit)
            {
                if (e.IsSend) { bucket.SendEvents++; bucket.SendBytes += e.Bytes; }
                else { bucket.ReceiveEvents++; bucket.ReceiveBytes += e.Bytes; }
            }
            string local = Address(e.IsSend ? e.SourceAddress : e.DestinationAddress, e.IsSend ? e.SourcePort : e.DestinationPort);
            string remote = Address(e.IsSend ? e.DestinationAddress : e.SourceAddress, e.IsSend ? e.DestinationPort : e.SourcePort);
            string key = e.Protocol + "|" + local + "|" + remote;
            EndpointRow row;
            if (!endpoints.TryGetValue(key, out row))
            {
                if (endpoints.Count >= 512)
                {
                    string oldest = endpoints.OrderBy(x => x.Value.LastSeen).First().Key; endpoints.Remove(oldest);
                    endpointEvictions++;
                }
                row = new EndpointRow { Protocol = e.Protocol, Local = local, Remote = remote }; endpoints.Add(key, row);
            }
            row.LastSeen = e.TimestampUtc.ToLocalTime();
            if (e.IsRetransmit) row.RetransmitEvents++;
            else if (e.IsSend) { row.SentEvents++; row.SentBytes += e.Bytes; }
            else { row.ReceivedEvents++; row.ReceivedBytes += e.Bytes; }
        }
        private static string Address(string ip, int port) { return ((ip ?? "").IndexOf(':') >= 0 ? "[" + ip + "]" : ip) + ":" + port.ToString(CultureInfo.InvariantCulture); }
        private void Trim(DateTime now)
        {
            long oldest = (now.Ticks - TimeSpan.TicksPerSecond * 2) / BucketTicks;
            foreach (long key in recent.Keys.Where(x => x < oldest).ToArray()) recent.Remove(key);
        }
        public void Fill(MonitorSnapshot s, DateTime now, DateTime began)
        {
            Trim(now); double seconds = Math.Max(.1, Math.Min(2, (now - began).TotalSeconds));
            double delayTotal = 0, delayMax = 0; long delayEvents = 0;
            foreach (Bucket bucket in recent.Values)
            {
                s.GameSentBytesPerSecond += bucket.SendBytes / seconds; s.GameSentEventsPerSecond += bucket.SendEvents / seconds;
                s.GameReceivedBytesPerSecond += bucket.ReceiveBytes / seconds; s.GameReceivedEventsPerSecond += bucket.ReceiveEvents / seconds;
                delayTotal += bucket.DelayTotal; delayEvents += bucket.DelayEvents; delayMax = Math.Max(delayMax, bucket.DelayMax);
            }
            s.GameSentEvents = sends; s.GameReceivedEvents = receives; s.RetransmitEvents = retransmits;
            if (lastReceive != DateTime.MinValue) s.GameReceiveIdleSeconds = Math.Max(0, (now - lastReceive).TotalSeconds);
            if (lastReceiveObserved != DateTime.MinValue) s.GameReceiveDeliveryIdleSeconds = Math.Max(0, (now - lastReceiveObserved).TotalSeconds);
            if (delayEvents > 0) { s.GameEventDeliveryLagMs = delayTotal / delayEvents; s.GameMaxEventDeliveryLagMs = delayMax; }
            if (lastReceive != DateTime.MinValue) s.GameLastReceiveEventUtc = lastReceive;
            if (lastReceiveObserved != DateTime.MinValue) s.GameLastReceiveObservedUtc = lastReceiveObserved;
            s.GameInvalidEventTimeCount = invalidTimes;
            s.GameEndpointEvictions = endpointEvictions;
            s.EndpointObservations = endpoints.Values.OrderByDescending(x => x.LastSeen).Select(x => new EndpointRow {
                Protocol = x.Protocol, Local = x.Local, Remote = x.Remote, SentEvents = x.SentEvents, ReceivedEvents = x.ReceivedEvents,
                RetransmitEvents = x.RetransmitEvents, SentBytes = x.SentBytes, ReceivedBytes = x.ReceivedBytes, LastSeen = x.LastSeen
            }).ToList();
            s.Endpoints = s.EndpointObservations.Take(100).ToList();
        }
    }

    public sealed class SessionLog
    {
        public static readonly string CsvHeader = "timestamp_local,process,pid,game_collection_active,game_send_bytes_s,game_receive_bytes_s,game_send_events_s,game_receive_events_s,tcp_retransmit_events_total,trace_events_lost,trace_buffers_lost,trace_malformed_events,gateway_target,gateway_rtt_ms,gateway_missing_60s,gateway_sent_60s,gateway_errors_60s,reference_target,reference_rtt_ms,reference_missing_60s,reference_sent_60s,reference_errors_60s,pc_adapter_send_bytes_s,pc_adapter_receive_bytes_s,pc_adapter_receive_errors,pc_adapter_send_errors,pc_adapter_receive_discards,pc_adapter_send_discards,game_status,trace_status,timestamp_utc,trace_active,game_rates_available,game_receive_idle_seconds,game_send_events_total,game_receive_events_total,trace_permission_denied,trace_error_code,trace_failure_stage,trace_candidate_events,trace_selected_pid_events,trace_unsupported_events,adapter_rates_available,adapter_counters_available,adapter_status,gateway_status,reference_status";
        private readonly List<MonitorSnapshot> samples = new List<MonitorSnapshot>();
        static SessionLog()
        {
            CsvHeader += ",game_collection_requested,game_receive_delivery_idle_seconds,game_delivery_lag_mean_ms,game_delivery_lag_max_ms,game_last_receive_event_utc,game_last_receive_observed_utc,game_invalid_event_time_count,process_traffic_available,process_traffic_status,process_traffic_dropped_events";
            CsvHeader += ",process_collection_requested,pc_adapter_send_packets_s,pc_adapter_receive_packets_s,game_endpoint_evictions_total";
            CsvHeader += ",is_final,stop_requested_utc,stop_drain_complete,stop_boundary_status,game_process_ended,adapter_counter_resets,adapter_counter_gaps";
            CsvHeader += ",tcp_rtt_requested,tcp_rtt_available,tcp_rtt_state,tcp_rtt_status,tcp_rtt_observed_utc,tcp_rtt_min_ms,tcp_rtt_max_ms,tcp_rtt_connections_found,tcp_rtt_connections_tracked,tcp_rtt_connections_measured,tcp_rtt_connections_skipped,tcp_rtt_partial,tcp_rtt_stale,tcp_rtt_permission_denied,tcp_rtt_error_code,tcp_rtt_error_stage,game_ping_support,game_packet_loss_support";
        }
        private readonly List<KeyValuePair<DateTime, string>> marks = new List<KeyValuePair<DateTime, string>>();
        public int Count { get { return samples.Count; } }
        public void Clear() { samples.Clear(); marks.Clear(); }
        public void Add(MonitorSnapshot s) { samples.Add(s); if (samples.Count > 7200) samples.RemoveAt(0); }
        public void Mark(DateTime now, string label) { marks.Add(new KeyValuePair<DateTime, string>(now, label)); if (marks.Count > 1000) marks.RemoveAt(0); }
        public void Export(string path)
        {
            using (StreamWriter w = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                w.WriteLine(CsvHeader);
                foreach (MonitorSnapshot s in samples) w.WriteLine(ToCsvRow(s));
            }
            using (StreamWriter w = new StreamWriter(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFileNameWithoutExtension(path) + ".marks.csv"), false, new UTF8Encoding(true)))
            {
                w.WriteLine("timestamp_local,label"); foreach (KeyValuePair<DateTime, string> mark in marks) w.WriteLine(mark.Key.ToLocalTime().ToString("o") + "," + Csv(mark.Value));
            }
        }
        public static string ToCsvRow(MonitorSnapshot s)
        {
            TcpRttSnapshot tcp = s.TcpRtt ?? new TcpRttSnapshot();
            return String.Join(",", new string[] {
                s.TimestampUtc.ToLocalTime().ToString("o"), Csv(s.ProcessName), s.ProcessId.ToString(CultureInfo.InvariantCulture), s.GameRatesAvailable ? "true" : "false",
                Num(s.GameRatesAvailable ? (double?)s.GameSentBytesPerSecond : null), Num(s.GameRatesAvailable ? (double?)s.GameReceivedBytesPerSecond : null),
                Num(s.GameRatesAvailable ? (double?)s.GameSentEventsPerSecond : null), Num(s.GameRatesAvailable ? (double?)s.GameReceivedEventsPerSecond : null),
                s.RetransmitEvents.ToString(CultureInfo.InvariantCulture), s.Trace.EventsLost.ToString(CultureInfo.InvariantCulture), s.Trace.BuffersLost.ToString(CultureInfo.InvariantCulture), s.Trace.MalformedEvents.ToString(CultureInfo.InvariantCulture),
                Csv(s.Gateway.Target), Num(s.Gateway.CurrentMs), s.Gateway.Missing.ToString(CultureInfo.InvariantCulture), s.Gateway.Sent.ToString(CultureInfo.InvariantCulture), s.Gateway.Errors.ToString(CultureInfo.InvariantCulture),
                Csv(s.Reference.Target), Num(s.Reference.CurrentMs), s.Reference.Missing.ToString(CultureInfo.InvariantCulture), s.Reference.Sent.ToString(CultureInfo.InvariantCulture), s.Reference.Errors.ToString(CultureInfo.InvariantCulture),
                Num(s.AdapterRatesAvailable ? (double?)s.AdapterSentBytesPerSecond : null), Num(s.AdapterRatesAvailable ? (double?)s.AdapterReceivedBytesPerSecond : null),
                s.AdapterCountersAvailable ? s.AdapterReceiveErrors.ToString(CultureInfo.InvariantCulture) : "", s.AdapterCountersAvailable ? s.AdapterSendErrors.ToString(CultureInfo.InvariantCulture) : "",
                s.AdapterCountersAvailable ? s.AdapterReceiveDiscards.ToString(CultureInfo.InvariantCulture) : "", s.AdapterCountersAvailable ? s.AdapterSendDiscards.ToString(CultureInfo.InvariantCulture) : "", Csv(s.GameStatus), Csv(s.Trace.Status),
                s.TimestampUtc.ToUniversalTime().ToString("o"), s.Trace.Active ? "true" : "false", s.GameRatesAvailable ? "true" : "false", Num(s.GameRatesAvailable ? s.GameReceiveIdleSeconds : null),
                s.GameSentEvents.ToString(CultureInfo.InvariantCulture), s.GameReceivedEvents.ToString(CultureInfo.InvariantCulture), s.Trace.PermissionDenied ? "true" : "false", s.Trace.NativeErrorCode.ToString(CultureInfo.InvariantCulture), Csv(s.Trace.FailureStage),
                s.Trace.CandidateEvents.ToString(CultureInfo.InvariantCulture), s.Trace.SelectedPidEvents.ToString(CultureInfo.InvariantCulture), s.Trace.UnsupportedEvents.ToString(CultureInfo.InvariantCulture),
                s.AdapterRatesAvailable ? "true" : "false", s.AdapterCountersAvailable ? "true" : "false", Csv(s.AdapterStatus), Csv(s.Gateway.Status), Csv(s.Reference.Status),
                s.GameCollectionRequested ? "true" : "false", Num(s.GameRatesAvailable ? s.GameReceiveDeliveryIdleSeconds : null), Num(s.GameEventDeliveryLagMs), Num(s.GameMaxEventDeliveryLagMs),
                s.GameLastReceiveEventUtc.HasValue ? s.GameLastReceiveEventUtc.Value.ToUniversalTime().ToString("o") : "", s.GameLastReceiveObservedUtc.HasValue ? s.GameLastReceiveObservedUtc.Value.ToUniversalTime().ToString("o") : "",
                s.GameInvalidEventTimeCount.ToString(CultureInfo.InvariantCulture), s.ProcessTraffic.Available ? "true" : "false", Csv(s.ProcessTraffic.Status), s.ProcessTraffic.DroppedProcessEvents.ToString(CultureInfo.InvariantCulture),
                s.ProcessCollectionRequested ? "true" : "false", Num(s.AdapterRatesAvailable ? (double?)s.AdapterSentPacketsPerSecond : null), Num(s.AdapterRatesAvailable ? (double?)s.AdapterReceivedPacketsPerSecond : null), s.GameEndpointEvictions.ToString(CultureInfo.InvariantCulture),
                s.IsFinal ? "true" : "false", s.StopRequestedUtc.HasValue ? s.StopRequestedUtc.Value.ToUniversalTime().ToString("o") : "", s.IsFinal ? s.StopDrainComplete ? "true" : "false" : "", Csv(s.StopBoundaryStatus),
                s.GameProcessEnded ? "true" : "false", s.AdapterCounterResets.ToString(CultureInfo.InvariantCulture), s.AdapterCounterGaps.ToString(CultureInfo.InvariantCulture),
                tcp.Requested ? "true" : "false", tcp.Available ? "true" : "false", Csv(tcp.State), Csv(tcp.Status), tcp.TimestampUtc == DateTime.MinValue ? "" : tcp.TimestampUtc.ToUniversalTime().ToString("o"),
                Num(tcp.Available ? tcp.MinimumMs : null), Num(tcp.Available ? tcp.MaximumMs : null), tcp.ConnectionsFound.ToString(CultureInfo.InvariantCulture), tcp.ConnectionsTracked.ToString(CultureInfo.InvariantCulture), tcp.ConnectionsMeasured.ToString(CultureInfo.InvariantCulture), tcp.ConnectionsSkipped.ToString(CultureInfo.InvariantCulture),
                tcp.Partial ? "true" : "false", tcp.Stale ? "true" : "false", tcp.PermissionDenied ? "true" : "false", tcp.NativeErrorCode.ToString(CultureInfo.InvariantCulture), Csv(tcp.ErrorStage), "unsupported", "unsupported"
            });
        }
        internal static string Csv(string s)
        {
            s = s ?? "";
            if (s.Length > 0 && "=+-@\t\r".IndexOf(s[0]) >= 0) s = "'" + s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
        private static string Num(double? value) { return value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : ""; }
    }
}
