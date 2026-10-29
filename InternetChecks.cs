using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Threading;

namespace PcInternetMonitor
{
    // Exactly one outstanding OS operation per lane, including after timeouts or
    // a session change. DNS APM has no cancellation API: an overdue lookup is
    // retained until its callback drains, rather than starting overlapping work.
    public sealed class InternetCheckRunner : IDisposable
    {
        private const int PeriodMilliseconds = 5000, TimeoutMilliseconds = 3000;
        private const double StaleSeconds = 15;
        private readonly object gate = new object();
        private readonly Lane dns = new Lane("dns"), https = new Lane("https");
        private readonly bool workersStarted;
        private Context current;
        private bool disposed;
        private long generation;
        private InternetCheckSample lastDns = Waiting("dns", "", 0), lastHttps = Waiting("https", "", 0);

        private sealed class Context
        {
            internal string DnsHost;
            internal Uri HttpsUri;
            internal int ExpectedStatus;
            internal bool TestHttp;
            internal long Generation;
        }

        private sealed class Lane
        {
            internal readonly string Kind;
            private readonly object signalGate = new object();
            private readonly AutoResetEvent wake = new AutoResetEvent(false);
            private bool closed;
            internal Thread Worker;
            internal Operation Pending;
            internal bool InProgress;
            internal bool Failed;
            internal string Failure = "";
            internal long SeenGeneration;
            internal long NextDueTicks;
            internal Lane(string kind) { Kind = kind; }
            internal void Signal() { lock (signalGate) { if (!closed) wake.Set(); } }
            internal void Wait(int milliseconds) { wake.WaitOne(milliseconds); }
            internal void Close() { lock (signalGate) { if (!closed) { closed = true; wake.Close(); } } }
        }

        private sealed class Operation
        {
            private readonly object operationGate = new object();
            private readonly ManualResetEvent completed = new ManualResetEvent(false);
            private bool finished, released, timedOut, aborted;
            private double completedMilliseconds;
            private InternetCheckSample result;
            internal readonly string Kind, Target;
            internal readonly DateTime StartedUtc = DateTime.UtcNow;
            internal readonly Stopwatch Watch = Stopwatch.StartNew();
            private HttpWebRequest request;
            internal long Generation;
            internal Operation(string kind, string target) { Kind = kind; Target = target; }
            internal bool Finished { get { lock (operationGate) return finished; } }
            internal bool CanStart { get { lock (operationGate) return !finished && !timedOut && !aborted; } }
            internal double CompletedMilliseconds { get { lock (operationGate) return completedMilliseconds; } }
            internal bool Register(HttpWebRequest value)
            {
                lock (operationGate) { if (finished || timedOut || aborted) return false; request = value; return true; }
            }
            internal void Wait(int milliseconds) { completed.WaitOne(milliseconds); }
            internal InternetCheckSample Result { get { lock (operationGate) return result == null ? null : result.Copy(); } }
            internal InternetCheckSample Timeout()
            {
                lock (operationGate)
                {
                    timedOut = true;
                    return FinishSample(Kind, Target, StartedUtc, Watch.Elapsed.TotalMilliseconds, "timeout", Label(Kind) + " 응답 시간 초과", "3초 내 검사 완료 안 됨", null);
                }
            }
            internal void Complete(InternetCheckSample value)
            {
                lock (operationGate)
                {
                    if (finished) return;
                    result = timedOut ? FinishSample(Kind, Target, StartedUtc, Watch.Elapsed.TotalMilliseconds, "timeout", Label(Kind) + " 응답 시간 초과", "3초 내 검사 완료 안 됨", null) : value;
                    completedMilliseconds = Watch.Elapsed.TotalMilliseconds;
                    finished = true;
                    if (released) completed.Close(); else completed.Set();
                }
            }
            internal void Release()
            {
                lock (operationGate) { if (released) return; released = true; if (finished) completed.Close(); }
            }
            internal void Abort()
            {
                HttpWebRequest cancel;
                lock (operationGate) { aborted = true; cancel = request; }
                if (cancel == null) return;
                try { cancel.Abort(); } catch (ObjectDisposedException) { }
            }
        }

        public event Action<InternetCheckSample> Sampled;
        public long Generation { get { lock (gate) return current == null ? generation : current.Generation; } }

        public InternetCheckRunner() : this(true) { }
        private InternetCheckRunner(bool startWorkers)
        {
            workersStarted = startWorkers;
            if (!startWorkers) return;
            foreach (Lane lane in new Lane[] { dns, https })
            {
                Lane selected = lane;
                lane.Worker = new Thread(delegate() { Run(selected); }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Internet " + lane.Kind + " check" };
                lane.Worker.Start();
            }
        }

        // The default endpoint policy requires an empty HTTP 204 response. A
        // configured 200-only endpoint can be supplied explicitly, but redirects
        // are never followed or classified as a successful connectivity check.
        public void Start(string dnsHost, string httpsUrl) { Start(dnsHost, httpsUrl, 204); }
        public void Start(string dnsHost, string httpsUrl, int expectedHttpStatus)
        { StartCore(dnsHost, httpsUrl, expectedHttpStatus, false); }

        private void StartCore(string dnsHost, string url, int expectedStatus, bool testHttp)
        {
            string normalizedHost = ValidateHost(dnsHost);
            Uri uri = ValidateUri(url, testHttp);
            if (expectedStatus != 200 && expectedStatus != 204) throw new ArgumentException("HTTPS 예상 응답은 200 또는 204여야 합니다.", "expectedStatus");
            Operation cancel;
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("InternetCheckRunner");
                if (dns.Failed || https.Failed) throw new InvalidOperationException("DNS/HTTPS 검사 작업자가 중단되었습니다. 앱을 다시 실행한 뒤 검사를 시작하세요.");
                generation++;
                current = new Context { DnsHost = normalizedHost, HttpsUri = uri, ExpectedStatus = expectedStatus, TestHttp = testHttp, Generation = generation };
                lastDns = Initial(dns, normalizedHost, generation); lastHttps = Initial(https, uri.AbsoluteUri, generation);
                dns.InProgress = https.InProgress = false;
                cancel = https.Pending;
            }
            if (cancel != null) cancel.Abort();
            dns.Signal(); https.Signal();
        }

        public void Stop()
        {
            Operation cancel;
            lock (gate)
            {
                generation++;
                current = null; dns.InProgress = https.InProgress = false;
                lastDns = Stopped(lastDns, generation); lastHttps = Stopped(lastHttps, generation);
                cancel = https.Pending;
            }
            if (cancel != null) cancel.Abort();
            dns.Signal(); https.Signal();
        }

        public InternetCheckSnapshot Snapshot(DateTime now)
        {
            now = now.Kind == DateTimeKind.Utc ? now : now.ToUniversalTime();
            lock (gate)
            {
                InternetCheckSample dnsValue = Present(lastDns, dns.InProgress, now), httpsValue = Present(lastHttps, https.InProgress, now);
                PendingPrevious(dnsValue, dns, current, now); PendingPrevious(httpsValue, https, current, now);
                return new InternetCheckSnapshot { Running = current != null, Generation = current == null ? generation : current.Generation,
                    Dns = dnsValue, Https = httpsValue };
            }
        }

        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; }
            Stop();
            if (!workersStarted) { dns.Close(); https.Close(); return; }
            foreach (Lane lane in new Lane[] { dns, https })
                if (lane.Worker != Thread.CurrentThread && !lane.Worker.Join(300))
                    DiagnosticsLog.Write("internet_check_stop_pending", "kind=" + lane.Kind + "; OS operation cleanup pending; old session results excluded");
        }

        private void Run(Lane lane)
        {
            try
            {
                while (true)
                {
                    Context c;
                    lock (gate) { if (disposed) return; c = current; }
                    Operation pending = lane.Pending;
                    if (pending != null)
                    {
                        if (!pending.Finished) { pending.Wait(50); continue; }
                        pending.Release(); lock (gate) { if (Object.ReferenceEquals(lane.Pending, pending)) lane.Pending = null; }
                    }
                    if (c == null) { lane.Wait(1000); continue; }
                    if (lane.SeenGeneration != c.Generation) { lane.SeenGeneration = c.Generation; lane.NextDueTicks = 0; }
                    double delay = (lane.NextDueTicks - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
                    if (delay > 0) { lane.Wait((int)Math.Min(delay, 1000)); continue; }
                    if (!MarkChecking(c, lane)) continue;
                    Operation operation = new Operation(lane.Kind, lane.Kind == "dns" ? c.DnsHost : c.HttpsUri.AbsoluteUri) { Generation = c.Generation };
                    lock (gate) { lane.Pending = operation; }
                    if (!IsCurrent(c)) { operation.Complete(Cancelled(operation)); continue; }
                    // BeginGetResponse can synchronously evaluate a PAC script,
                    // and DNS Begin can involve OS setup. Keep those calls away
                    // from the UI/cache lock and from the logical timeout lane.
                    bool queued = ThreadPool.QueueUserWorkItem(delegate
                    {
                        if (lane.Kind == "dns") BeginDns(operation, c); else BeginHttps(operation, c);
                    });
                    if (!queued) operation.Complete(FinishSample(lane.Kind, operation.Target, operation.StartedUtc, 0, "error", Label(lane.Kind) + " 검사 시작 오류", "백그라운드 검사 작업을 큐에 넣지 못함", null));
                    InternetCheckSample result = WaitForResult(c, operation, TimeoutMilliseconds);
                    // Scheduling, like the operation deadline, must survive wall-clock corrections.
                    lane.NextDueTicks = Stopwatch.GetTimestamp() + (long)(Math.Max(0, PeriodMilliseconds - operation.Watch.Elapsed.TotalMilliseconds) * Stopwatch.Frequency / 1000.0);
                    if (result != null) Publish(c, lane, result);
                }
            }
            catch (Exception error)
            {
                DiagnosticsLog.Write("internet_check_worker_failed", "kind=" + lane.Kind, error);
                Context failedContext = null;
                InternetCheckSample failedSample = null;
                lock (gate)
                {
                    lane.Failed = true; lane.Failure = error.GetType().Name + ": " + error.Message;
                    if (current != null)
                    {
                        failedContext = current;
                        failedSample = FinishSample(lane.Kind, lane.Kind == "dns" ? current.DnsHost : current.HttpsUri.AbsoluteUri,
                            DateTime.UtcNow, 0, "error", Label(lane.Kind) + " 검사 중단", error.GetType().Name + ": " + error.Message, null);
                    }
                    lane.InProgress = false;
                }
                // Preserve a terminal collector error in the same raw-result
                // stream as normal checks, without invoking consumers in gate.
                if (failedSample != null) Publish(failedContext, lane, failedSample);
            }
            finally
            {
                Operation pending = lane.Pending;
                if (pending != null) { pending.Abort(); pending.Release(); }
                lane.Close();
            }
        }

        private bool MarkChecking(Context c, Lane lane)
        {
            lock (gate)
            {
                if (!IsCurrentLocked(c)) return false;
                lane.InProgress = true;
                InternetCheckSample value = lane.Kind == "dns" ? lastDns : lastHttps;
                if (value.State == "waiting") { value.State = "checking"; value.Status = Label(lane.Kind) + " 검사 중"; value.StartedUtc = DateTime.UtcNow; value.TimestampUtc = value.StartedUtc; }
                return true;
            }
        }

        private InternetCheckSample WaitForResult(Context c, Operation operation, int timeout)
        {
            while (!operation.Finished)
            {
                if (!IsCurrent(c)) { operation.Abort(); return null; }
                int remaining = timeout - (int)Math.Min(Int32.MaxValue, operation.Watch.ElapsedMilliseconds);
                if (remaining <= 0) { InternetCheckSample result = operation.Timeout(); operation.Abort(); return result; }
                operation.Wait(Math.Min(50, remaining));
            }
            if (!IsCurrent(c)) return null;
            if (operation.CompletedMilliseconds > timeout) { InternetCheckSample late = operation.Timeout(); operation.Abort(); return late; }
            return operation.Result;
        }

        private bool Publish(Context c, Lane lane, InternetCheckSample sample)
        {
            Action<InternetCheckSample> handler;
            lock (gate)
            {
                if (!IsCurrentLocked(c) || sample == null || sample.Cancelled) return false;
                sample.Generation = c.Generation; sample.InProgress = false;
                if (lane.Kind == "dns") lastDns = sample.Copy(); else lastHttps = sample.Copy();
                lane.InProgress = false; handler = Sampled;
            }
            // A consumer that queues work must also compare Generation when it
            // consumes the queue; an already-entered callback can straddle Stop.
            if (handler != null && IsCurrent(c))
            {
                try { handler(sample.Copy()); }
                catch (Exception error) { DiagnosticsLog.Write("internet_check_callback_failed", "kind=" + lane.Kind, error); }
            }
            return true;
        }

        private bool IsCurrent(Context c) { lock (gate) return IsCurrentLocked(c); }
        private bool IsCurrentLocked(Context c) { return !disposed && c != null && Object.ReferenceEquals(current, c); }

        private void BeginDns(Operation operation, Context c)
        {
            try
            {
                if (!IsCurrent(c) || !operation.CanStart) { operation.Complete(Cancelled(operation)); return; }
                Dns.BeginGetHostAddresses(operation.Target, delegate(IAsyncResult result)
                {
                    try
                    {
                        IPAddress[] addresses = Dns.EndGetHostAddresses(result);
                        bool found = addresses != null && addresses.Length > 0;
                        operation.Complete(FinishSample("dns", operation.Target, operation.StartedUtc, operation.Watch.Elapsed.TotalMilliseconds,
                            found ? "success" : "failure", found ? "DNS 조회 성공" : "DNS 주소 응답 없음", found ? "" : "반환된 IP 주소 없음", null));
                    }
                    catch (Exception error) { operation.Complete(Failure(operation, error)); }
                }, null);
            }
            catch (Exception error) { operation.Complete(Failure(operation, error)); }
        }

        private void BeginHttps(Operation operation, Context c)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(c.HttpsUri);
                request.Method = "GET"; request.AllowAutoRedirect = false; request.KeepAlive = false; request.Pipelined = false;
                request.Timeout = TimeoutMilliseconds; request.ReadWriteTimeout = TimeoutMilliseconds;
                request.MaximumResponseHeadersLength = 8;
                request.UseDefaultCredentials = false; request.Credentials = null; request.PreAuthenticate = false; request.CookieContainer = null;
                request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.BypassCache);
                request.UserAgent = "PCInternetMonitor/1.0";
                if (c.TestHttp) request.Proxy = null; // Exact own loopback fixture only.
                lock (gate)
                {
                    if (!IsCurrentLocked(c) || !operation.Register(request)) { operation.Complete(Cancelled(operation)); return; }
                }
                request.BeginGetResponse(delegate(IAsyncResult result)
                {
                    try
                    {
                        InternetCheckSample sample;
                        using (HttpWebResponse response = (HttpWebResponse)request.EndGetResponse(result))
                        {
                            int code = (int)response.StatusCode;
                            bool accepted = code == c.ExpectedStatus;
                            sample = FinishSample("https", operation.Target, operation.StartedUtc, operation.Watch.Elapsed.TotalMilliseconds,
                                accepted ? "success" : "failure", accepted ? "HTTPS 접속 성공" : "HTTPS 예상 응답 아님", accepted ? "" : "HTTP " + code.ToString(CultureInfo.InvariantCulture) + " (예상 " + c.ExpectedStatus.ToString(CultureInfo.InvariantCulture) + ")", code);
                        }
                        operation.Complete(sample);
                    }
                    catch (Exception error) { operation.Complete(Failure(operation, error)); }
                }, null);
            }
            catch (Exception error) { operation.Complete(Failure(operation, error)); }
        }

        private static InternetCheckSample Failure(Operation operation, Exception error)
        {
            string state = "error", status = Label(operation.Kind) + " 검사 오류";
            int? code = null;
            WebException web = error as WebException;
            SocketException socket = error as SocketException;
            if (web != null)
            {
                HttpWebResponse response = web.Response as HttpWebResponse;
                if (response != null) { code = (int)response.StatusCode; response.Close(); }
                if (web.Status == WebExceptionStatus.Timeout) { state = "timeout"; status = "HTTPS 응답 시간 초과"; }
                else if (web.Status == WebExceptionStatus.RequestCanceled) { state = "cancelled"; status = "HTTPS 검사 취소"; }
                else if (web.Status == WebExceptionStatus.TrustFailure || web.Status == WebExceptionStatus.SecureChannelFailure)
                { state = "failure"; status = "HTTPS 보안 연결 실패"; }
                else if (web.Status == WebExceptionStatus.NameResolutionFailure || web.Status == WebExceptionStatus.ProxyNameResolutionFailure)
                { state = "failure"; status = "HTTPS 이름 조회 실패"; }
                else if (web.Status == WebExceptionStatus.ConnectFailure || web.Status == WebExceptionStatus.ConnectionClosed ||
                    web.Status == WebExceptionStatus.ReceiveFailure || web.Status == WebExceptionStatus.SendFailure || web.Status == WebExceptionStatus.ProtocolError ||
                    web.Status == WebExceptionStatus.ServerProtocolViolation || web.Status == WebExceptionStatus.MessageLengthLimitExceeded)
                { state = "failure"; status = code.HasValue ? "HTTPS 예상 응답 아님" : "HTTPS 접속 실패"; }
            }
            else if (socket != null)
            {
                if (socket.SocketErrorCode == SocketError.TimedOut) { state = "timeout"; status = "DNS 응답 시간 초과"; }
                else if (socket.SocketErrorCode == SocketError.HostNotFound || socket.SocketErrorCode == SocketError.TryAgain || socket.SocketErrorCode == SocketError.NoData ||
                    socket.SocketErrorCode == SocketError.NoRecovery || socket.SocketErrorCode == SocketError.NetworkDown || socket.SocketErrorCode == SocketError.NetworkUnreachable ||
                    socket.SocketErrorCode == SocketError.HostUnreachable || socket.SocketErrorCode == SocketError.ConnectionRefused || socket.SocketErrorCode == SocketError.ConnectionReset ||
                    socket.SocketErrorCode == SocketError.ConnectionAborted)
                { state = "failure"; status = "DNS 조회 실패"; }
            }
            return FinishSample(operation.Kind, operation.Target, operation.StartedUtc, operation.Watch.Elapsed.TotalMilliseconds,
                state, status, error.GetType().Name + ": " + error.Message, code);
        }

        private static InternetCheckSample FinishSample(string kind, string target, DateTime started, double elapsed, string state, string status, string error, int? code)
        {
            return new InternetCheckSample { Kind = kind, Target = target, StartedUtc = started, TimestampUtc = DateTime.UtcNow,
                State = state, Status = status, Error = error ?? "", Success = state == "success", TimedOut = state == "timeout", Cancelled = state == "cancelled",
                DurationMs = state == "error" || state == "cancelled" || Double.IsNaN(elapsed) || Double.IsInfinity(elapsed) || elapsed < 0 ? (double?)null : elapsed, HttpStatusCode = code };
        }
        private static InternetCheckSample Cancelled(Operation operation) { return FinishSample(operation.Kind, operation.Target, operation.StartedUtc, operation.Watch.Elapsed.TotalMilliseconds, "cancelled", Label(operation.Kind) + " 검사 취소", "", null); }
        private static string Label(string kind) { return kind == "dns" ? "DNS" : "HTTPS"; }
        private static InternetCheckSample Waiting(string kind, string target, long token) { DateTime now = DateTime.UtcNow; return new InternetCheckSample { Kind = kind, Target = target, State = "waiting", Status = Label(kind) + " 검사 대기", StartedUtc = now, TimestampUtc = now, Generation = token }; }
        private static InternetCheckSample Initial(Lane lane, string target, long token)
        {
            InternetCheckSample value = Waiting(lane.Kind, target, token);
            if (lane.Failed) { value.State = "error"; value.Status = Label(lane.Kind) + " 검사 중단 · 앱 재실행 필요"; value.Error = lane.Failure; }
            return value;
        }
        private static void PendingPrevious(InternetCheckSample value, Lane lane, Context c, DateTime now)
        {
            Operation pending = lane.Pending;
            if (c == null || pending == null || pending.Finished || pending.Generation == c.Generation || value.State != "waiting") return;
            value.Status = "이전 " + Label(value.Kind) + " 조회 정리 대기";
            if ((now - value.TimestampUtc).TotalSeconds >= StaleSeconds)
            { value.State = "stale"; value.Stale = true; value.Status = Label(value.Kind) + " 검사 시작 지연 · 이전 조회 정리 대기"; }
        }
        private static InternetCheckSample Stopped(InternetCheckSample previous, long token) { return new InternetCheckSample { Kind = previous.Kind, Target = previous.Target, State = "stopped", Status = Label(previous.Kind) + " 검사 중지", TimestampUtc = DateTime.UtcNow, Generation = token }; }
        private static InternetCheckSample Present(InternetCheckSample value, bool inProgress, DateTime now)
        {
            InternetCheckSample copy = value.Copy(); copy.InProgress = inProgress;
            bool completed = copy.State == "success" || copy.State == "failure" || copy.State == "timeout" || copy.State == "error";
            double age = (now - copy.TimestampUtc).TotalSeconds;
            if (completed && (age < 0 || age >= StaleSeconds))
            { copy.State = "stale"; copy.Stale = true; copy.Success = false; copy.DurationMs = null; copy.Status = "최근 " + Label(copy.Kind) + " 검사 없음"; }
            return copy;
        }
        private static string ValidateHost(string host)
        {
            if (String.IsNullOrWhiteSpace(host)) throw new ArgumentException("DNS 검사 호스트 이름이 필요합니다.", "dnsHost");
            host = host.Trim();
            if (host.Length > 253 || Uri.CheckHostName(host) != UriHostNameType.Dns) throw new ArgumentException("DNS 검사는 IP 주소 대신 호스트 이름을 사용합니다.", "dnsHost");
            return host;
        }
        private static Uri ValidateUri(string url, bool testHttp)
        {
            Uri uri;
            if (String.IsNullOrWhiteSpace(url) || url.Length > 2048 || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
                throw new ArgumentException("올바른 HTTPS 검사 주소가 필요합니다.", "httpsUrl");
            IPAddress address;
            bool ownLoopback = testHttp && uri.Scheme == Uri.UriSchemeHttp && IPAddress.TryParse(uri.Host, out address) && IPAddress.IsLoopback(address);
            if (uri.Scheme != Uri.UriSchemeHttps && !ownLoopback) throw new ArgumentException("일반 인터넷 검사는 HTTPS 주소만 사용합니다.", "httpsUrl");
            return uri;
        }

        // Core synthetic checks use the actual wait/publication/freshness code;
        // no socket, DNS request, TLS override, probe, ETW or UAC is involved.
        public static void SelfTest()
        {
            using (InternetCheckRunner runner = new InternetCheckRunner(false))
            {
                runner.Start("example.test", "https://example.test/generate_204");
                Context old = runner.current; int delivered = 0; runner.Sampled += delegate { delivered++; };
                Operation op = new Operation("dns", "example.test");
                InternetCheckSample timed = runner.WaitForResult(old, op, 2);
                Check(timed.TimedOut && !timed.Success && timed.State == "timeout", "overdue check was not a timeout");
                Check(!op.CanStart, "an expired queued OS request could still start");
                Check(runner.Publish(old, runner.dns, timed) && delivered == 1, "current timeout not published once");
                op.Complete(FinishSample("dns", "example.test", op.StartedUtc, 1, "success", "fixture", "", null));
                Check(op.Result.TimedOut, "late OS result changed the completed timeout"); op.Release();
                InternetCheckSnapshot copied = runner.Snapshot(DateTime.UtcNow); copied.Dns.Status = "mutated";
                Check(runner.Snapshot(DateTime.UtcNow).Dns.Status != "mutated", "snapshot shared mutable cache");
                runner.Stop();
                InternetCheckSnapshot stopped = runner.Snapshot(DateTime.UtcNow);
                Check(!stopped.Running && stopped.Dns.State == "stopped" && !stopped.Dns.DurationMs.HasValue && !stopped.Dns.Success, "stopped check retained a current measurement");
                Operation cancelled = new Operation("dns", "example.test");
                Check(runner.WaitForResult(old, cancelled, 2) == null, "stopped context was not cancelled");
                cancelled.Complete(Cancelled(cancelled)); cancelled.Release();
                Check(!runner.Publish(old, runner.dns, timed) && delivered == 1, "old context published after Stop");
                runner.Start("new.example.test", "https://example.test/generate_204");
                Check(runner.current.Generation != old.Generation && !runner.Publish(old, runner.dns, timed), "prior session result entered new session");
                Operation previous = new Operation("dns", "example.test") { Generation = old.Generation };
                runner.dns.Pending = previous;
                InternetCheckSnapshot waiting = runner.Snapshot(DateTime.UtcNow.AddSeconds(16));
                Check(waiting.Dns.State == "stale" && waiting.Dns.Stale && !waiting.Dns.Success && !waiting.Dns.DurationMs.HasValue, "an undrained previous OS lookup was hidden as a fresh result");
                previous.Complete(Cancelled(previous)); previous.Release(); runner.dns.Pending = null;
                InternetCheckSample measured = FinishSample("https", "https://example.test", DateTime.UtcNow, 4, "success", "fixture", "", 204);
                measured.TimestampUtc = DateTime.UtcNow.AddSeconds(-15);
                InternetCheckSample stale = Present(measured, true, DateTime.UtcNow);
                Check(stale.Stale && stale.State == "stale" && !stale.Success && !stale.DurationMs.HasValue && stale.InProgress, "stale snapshot retained a measured number");
                Check(runner.Snapshot(DateTime.UtcNow).Dns.State == "waiting", "new session retained previous result");
                runner.Stop(); runner.dns.Failed = true;
                bool restartRejected = false;
                try { runner.Start("new.example.test", "https://example.test/generate_204"); } catch (InvalidOperationException) { restartRejected = true; }
                Check(restartRejected && !runner.Snapshot(DateTime.UtcNow).Running, "a dead worker silently entered a new recording session");
            }
            Operation failure = new Operation("https", "https://example.test");
            Check(Failure(failure, new WebException("fixture", WebExceptionStatus.TrustFailure)).State == "failure", "TLS failure was treated as success or local error");
            Check(Failure(failure, new WebException("fixture", WebExceptionStatus.Timeout)).TimedOut, "native HTTPS timeout lost classification");
            Check(Failure(failure, new SecurityException("fixture")).State == "error", "local security error classified as remote outage");
            Operation dnsFailure = new Operation("dns", "example.test");
            Check(Failure(dnsFailure, new SocketException((int)SocketError.HostNotFound)).State == "failure", "DNS lookup failure lost its classification");
            Check(Failure(dnsFailure, new SocketException((int)SocketError.NoBufferSpaceAvailable)).State == "error", "local resource exhaustion was called a remote DNS failure");
            dnsFailure.Complete(Cancelled(dnsFailure)); dnsFailure.Release();
            failure.Complete(Cancelled(failure)); failure.Release();
            bool rejected = false;
            try { ValidateUri("http://example.test/", false); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "normal checker accepted plain HTTP");
            rejected = false;
            try { ValidateHost("1.1.1.1"); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "DNS checker accepted an IP literal without a name lookup");
        }

        // Explicit fixture mode: localhost resolver plus sockets created here.
        // Plain HTTP is permitted only for these literal loopback addresses;
        // this exercises transport handling, not real HTTPS/TLS correctness.
        public static void LiveLoopbackTest(string reportPath)
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("Internet checks loopback fixture; no remote host, TLS override, ETW, ICMP or UAC.");
            RunLoopbackFixture(report, 204, true, "success", true);
            RunLoopbackFixture(report, 302, true, "failure", false);
            RunLoopbackFixture(report, 204, false, "timeout", false);
            string path = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, report.ToString(), new UTF8Encoding(true));
        }

        private static void RunLoopbackFixture(StringBuilder report, int status, bool respond, string expected, bool checkDns)
        {
            using (LoopbackResponder server = new LoopbackResponder(status, respond))
            using (InternetCheckRunner runner = new InternetCheckRunner())
            using (ManualResetEvent received = new ManualResetEvent(false))
            {
                object resultGate = new object(); InternetCheckSample dnsResult = null, httpsResult = null; bool accepting = true;
                runner.Sampled += delegate(InternetCheckSample value)
                {
                    lock (resultGate)
                    {
                        if (!accepting) return;
                        if (value.Kind == "dns") dnsResult = value.Copy(); else httpsResult = value.Copy();
                        if (httpsResult != null && (!checkDns || dnsResult != null)) received.Set();
                    }
                };
                runner.StartCore("localhost", server.Url, 204, true);
                bool completed = received.WaitOne(6500);
                runner.Stop();
                lock (resultGate)
                {
                    accepting = false;
                    bool pass = completed && httpsResult != null && httpsResult.State == expected;
                    report.AppendLine("Loopback HTTP " + (respond ? status.ToString(CultureInfo.InvariantCulture) : "no response") +
                        " " + (pass ? "PASS" : "NOT VALIDATED") + ": " + (httpsResult == null ? "no completed result" : httpsResult.State + " / " + httpsResult.Status + " / " + httpsResult.Error));
                    if (checkDns) report.AppendLine("localhost resolver " + (dnsResult != null && dnsResult.Success ? "PASS" : "NOT VALIDATED") +
                        ": " + (dnsResult == null ? "no result" : dnsResult.Status) + "; may use hosts/cache; not an external DNS freshness test.");
                }
                InternetCheckSnapshot stopped = runner.Snapshot(DateTime.UtcNow);
                report.AppendLine("Stopped current values " + (!stopped.Running && stopped.Dns.State == "stopped" && stopped.Https.State == "stopped" && !stopped.Https.DurationMs.HasValue ? "PASS" : "NOT VALIDATED"));
            }
        }

        private sealed class LoopbackResponder : IDisposable
        {
            private readonly TcpListener listener;
            private readonly Thread worker;
            private readonly ManualResetEvent stop = new ManualResetEvent(false);
            private readonly int status;
            private readonly bool respond;
            private TcpClient active;
            internal readonly string Url;
            internal LoopbackResponder(int status, bool respond)
            {
                this.status = status; this.respond = respond;
                listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture) + "/fixture";
                worker = new Thread(delegate() { Serve(); }) { IsBackground = true, Name = "Internet loopback fixture" }; worker.Start();
            }
            private void Serve()
            {
                try
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    {
                        active = client; client.ReceiveTimeout = client.SendTimeout = 1500;
                        NetworkStream stream = client.GetStream(); int matched = 0, bytes = 0;
                        while (matched < 4 && bytes++ < 8192)
                        {
                            int next = stream.ReadByte(); if (next < 0) return;
                            int expected = matched == 0 || matched == 2 ? 13 : 10;
                            matched = next == expected ? matched + 1 : next == 13 ? 1 : 0;
                        }
                        if (matched < 4) return;
                        if (!respond) { stop.WaitOne(6000); return; }
                        string reason = status == 204 ? "No Content" : "Found";
                        string response = "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + " " + reason + "\r\nContent-Length: 0\r\nConnection: close\r\n" +
                            (status == 302 ? "Location: " + Url + "-redirected\r\n" : "") + "\r\n";
                        byte[] body = Encoding.ASCII.GetBytes(response); stream.Write(body, 0, body.Length);
                    }
                }
                catch (SocketException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                finally { active = null; }
            }
            public void Dispose()
            {
                stop.Set(); listener.Stop(); TcpClient client = active; if (client != null) client.Close();
                if (worker.Join(2000)) stop.Close();
            }
        }
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Internet check test: " + message); }
    }
}
