using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace GameNetworkMonitor
{
    public static class AdapterCatalog
    {
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern uint GetBestInterface(uint destinationAddress, out uint bestInterfaceIndex);

        public static List<AdapterInfo> GetAdapters()
        {
            List<AdapterInfo> result = new List<AdapterInfo>();
            NetworkInterface[] interfaces;
            try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (NetworkInformationException) { return result; }
            catch (PlatformNotSupportedException) { return result; }

            foreach (NetworkInterface adapter in interfaces)
            {
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                try
                {
                    IPInterfaceProperties properties = adapter.GetIPProperties();
                    List<string> addresses = new List<string>();
                    foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
                    {
                        if (!IPAddress.IsLoopback(address.Address) && !address.Address.Equals(IPAddress.Any) &&
                            !address.Address.Equals(IPAddress.IPv6Any)) addresses.Add(address.Address.ToString());
                    }
                    string gateway = "";
                    foreach (GatewayIPAddressInformation item in properties.GatewayAddresses)
                    {
                        if (item.Address.Equals(IPAddress.Any) || item.Address.Equals(IPAddress.IPv6Any)) continue;
                        if (gateway.Length == 0 || item.Address.AddressFamily == AddressFamily.InterNetwork)
                            gateway = item.Address.ToString();
                        if (item.Address.AddressFamily == AddressFamily.InterNetwork) break;
                    }
                    result.Add(new AdapterInfo
                    {
                        Id = adapter.Id,
                        Name = adapter.Name,
                        Description = adapter.Description,
                        Gateway = gateway,
                        LocalAddresses = String.Join(", ", addresses.ToArray()),
                        IsUp = adapter.OperationalStatus == OperationalStatus.Up
                    });
                }
                catch (NetworkInformationException) { }
                catch (PlatformNotSupportedException) { }
            }
            result.Sort(delegate(AdapterInfo left, AdapterInfo right)
            {
                int up = right.IsUp.CompareTo(left.IsUp);
                if (up != 0) return up;
                int gateway = (!String.IsNullOrEmpty(right.Gateway)).CompareTo(!String.IsNullOrEmpty(left.Gateway));
                return gateway != 0 ? gateway : StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
            });
            return result;
        }

        public static string GuessDefaultAdapterId()
        {
            uint defaultIndex = 0;
            try
            {
                // This reads the local routing table; it sends no probe traffic.
                uint index;
                if (GetBestInterface(0, out index) == 0) defaultIndex = index;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (BadImageFormatException) { }

            string bestId = "";
            int bestScore = Int32.MinValue;
            NetworkInterface[] interfaces;
            try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (NetworkInformationException) { return bestId; }
            catch (PlatformNotSupportedException) { return bestId; }
            foreach (NetworkInterface adapter in interfaces)
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                try
                {
                    IPInterfaceProperties properties = adapter.GetIPProperties();
                    bool hasIpv4 = false;
                    bool hasGateway = false;
                    bool hasIpv4Gateway = false;
                    foreach (UnicastIPAddressInformation item in properties.UnicastAddresses)
                        if (item.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(item.Address) && !item.Address.Equals(IPAddress.Any)) hasIpv4 = true;
                    foreach (GatewayIPAddressInformation item in properties.GatewayAddresses)
                    {
                        if (item.Address.Equals(IPAddress.Any) || item.Address.Equals(IPAddress.IPv6Any)) continue;
                        hasGateway = true;
                        if (item.Address.AddressFamily == AddressFamily.InterNetwork) hasIpv4Gateway = true;
                    }
                    bool physicalType = adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                        adapter.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet ||
                        adapter.NetworkInterfaceType == NetworkInterfaceType.FastEthernetFx ||
                        adapter.NetworkInterfaceType == NetworkInterfaceType.FastEthernetT ||
                        adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                    int score = (hasGateway ? 100 : 0) + (hasIpv4Gateway ? 20 : 0) +
                        (hasIpv4 ? 10 : 0) + (physicalType ? 5 : 0);
                    IPv4InterfaceProperties ipv4 = hasIpv4 ? properties.GetIPv4Properties() : null;
                    if (defaultIndex != 0 && ipv4 != null && (uint)ipv4.Index == defaultIndex) score += 1000;
                    if (score > bestScore) { bestScore = score; bestId = adapter.Id; }
                }
                catch (NetworkInformationException) { }
                catch (PlatformNotSupportedException) { }
            }
            return bestId;
        }
    }

    public sealed class AdapterReader
    {
        private readonly string adapterId;

        public AdapterReader(string adapterId)
        {
            if (String.IsNullOrWhiteSpace(adapterId)) throw new ArgumentException("어댑터를 선택하세요.", "adapterId");
            this.adapterId = adapterId;
        }

        public AdapterSample Read()
        {
            AdapterSample sample = new AdapterSample { TimestampUtc = DateTime.UtcNow, Status = "이 PC 어댑터를 찾을 수 없음" };
            try
            {
                // Re-enumerating also refreshes link status after reconnects and adapter changes.
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!String.Equals(adapter.Id, adapterId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (adapter.OperationalStatus != OperationalStatus.Up)
                    {
                        sample.Status = "이 PC 어댑터 연결 끊김";
                        return sample;
                    }
                    IPInterfaceStatistics statistics = adapter.GetIPStatistics();
                    sample.ReceivedBytes = NonNegative(statistics.BytesReceived);
                    sample.SentBytes = NonNegative(statistics.BytesSent);
                    sample.ReceivedPackets = AddCounters(statistics.UnicastPacketsReceived, statistics.NonUnicastPacketsReceived);
                    sample.SentPackets = AddCounters(statistics.UnicastPacketsSent, statistics.NonUnicastPacketsSent);
                    sample.ReceiveErrors = NonNegative(statistics.IncomingPacketsWithErrors);
                    sample.SendErrors = NonNegative(statistics.OutgoingPacketsWithErrors);
                    sample.ReceiveDiscards = NonNegative(statistics.IncomingPacketsDiscarded);
                    sample.SendDiscards = NonNegative(statistics.OutgoingPacketsDiscarded);
                    sample.Status = "이 PC 어댑터 누적 통계 · " + adapter.Name;
                    sample.Available = true;
                    return sample;
                }
            }
            catch (NetworkInformationException) { sample.Status = "이 PC 어댑터 통계를 읽을 수 없음"; }
            catch (PlatformNotSupportedException) { sample.Status = "이 PC 어댑터 통계 미지원"; }
            catch (UnauthorizedAccessException) { sample.Status = "이 PC 어댑터 통계 접근 권한 오류"; }
            return sample;
        }

        private static long NonNegative(long value) { return value < 0 ? 0 : value; }

        private static long AddCounters(long first, long second)
        {
            first = NonNegative(first);
            second = NonNegative(second);
            return first > Int64.MaxValue - second ? Int64.MaxValue : first + second;
        }
    }

    public sealed class ProbeRunner : IDisposable
    {
        private const int PeriodMilliseconds = 1000;
        private const int TimeoutMilliseconds = 900;
        private readonly object gate = new object();
        private readonly Timer timer;
        private bool disposed;
        private bool running;
        private bool inFlight;
        private long generation;
        private string target;
        private Ping activePing;

        public event Action<ProbeSample> Sampled;

        public ProbeRunner()
        {
            timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start(string target)
        {
            string normalized = NormalizeTarget(target);
            Ping cancel;
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("ProbeRunner");
                generation++;
                running = true;
                this.target = normalized;
                cancel = activePing;
                timer.Change(0, PeriodMilliseconds);
            }
            Cancel(cancel);
        }

        public void Stop()
        {
            Ping cancel;
            lock (gate)
            {
                if (disposed) return;
                running = false;
                generation++;
                timer.Change(Timeout.Infinite, Timeout.Infinite);
                cancel = activePing;
            }
            Cancel(cancel);
        }

        public void Dispose()
        {
            Ping cancel;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                running = false;
                generation++;
                cancel = activePing;
                timer.Dispose();
            }
            Cancel(cancel);
        }

        public static bool IsValidTarget(string value)
        {
            string ignored;
            return TryNormalizeTarget(value, out ignored);
        }

        public static string NormalizeTarget(string value)
        {
            string normalized;
            if (!TryNormalizeTarget(value, out normalized))
                throw new ArgumentException("IP 주소 또는 호스트 이름만 입력하세요. URL·포트·경로는 사용할 수 없습니다.", "value");
            return normalized;
        }

        private async void OnTimer(object state)
        {
            long token;
            string destination;
            Ping ping = null;
            Task<PingReply> pending = null;
            ProbeSample sample = null;
            lock (gate)
            {
                if (!running || disposed || inFlight) return;
                inFlight = true;
                token = generation;
                destination = target;
                try
                {
                    ping = new Ping();
                    activePing = ping;
                    // Initiation is serialized with Stop; only this operation can be active.
                    pending = ping.SendPingAsync(destination, TimeoutMilliseconds, new byte[32], new PingOptions(64, false));
                }
                catch (Exception error) { sample = FromException(destination, error); }
            }
            try
            {
                if (pending != null)
                {
                    PingReply reply = await pending.ConfigureAwait(false);
                    sample = FromReply(destination, reply.Status, reply.RoundtripTime);
                }
            }
            catch (Exception error) { sample = FromException(destination, error); }
            finally
            {
                lock (gate)
                {
                    if (Object.ReferenceEquals(activePing, ping)) activePing = null;
                    inFlight = false;
                }
                if (ping != null) ping.Dispose();
            }
            if (sample != null) Publish(token, sample);
        }

        private void Publish(long token, ProbeSample sample)
        {
            Action<ProbeSample> handler;
            lock (gate)
            {
                if (!IsCurrentRun(token)) return;
                handler = Sampled;
            }
            if (handler == null) return;
            foreach (Action<ProbeSample> subscriber in handler.GetInvocationList())
            {
                lock (gate) { if (!IsCurrentRun(token)) return; }
                // UI consumers run outside our lock, including when they call Stop themselves.
                try { subscriber(sample); }
                catch (Exception) { }
            }
        }

        private bool IsCurrentRun(long token) { return !disposed && running && generation == token; }

        private static void Cancel(Ping ping)
        {
            if (ping == null) return;
            // Framework Ping cancellation can wait for outstanding DNS work; never block UI Stop.
            ThreadPool.QueueUserWorkItem(delegate(object ignored)
            {
                try { ping.SendAsyncCancel(); }
                catch (ObjectDisposedException) { }
                catch (PingException) { }
                catch (InvalidOperationException) { }
            });
        }

        private static ProbeSample FromReply(string destination, IPStatus status, long milliseconds)
        {
            ProbeSample sample = new ProbeSample { TimestampUtc = DateTime.UtcNow, Target = destination };
            if (status == IPStatus.Success)
            {
                sample.Status = "ICMP 응답";
                sample.RoundtripMs = Math.Max(0, milliseconds);
                sample.Countable = true;
            }
            else if (status == IPStatus.TimedOut)
            {
                sample.Status = "ICMP 응답 시간 초과";
                sample.TimedOut = true;
                sample.Countable = true;
            }
            else
            {
                sample.Status = "ICMP 오류 · " + status.ToString() + " (응답 누락률에서 제외)";
            }
            return sample;
        }

        private static ProbeSample FromException(string destination, Exception error)
        {
            string reason = "ICMP 측정 오류";
            for (Exception current = error; current != null; current = current.InnerException)
            {
                SocketException socket = current as SocketException;
                Win32Exception native = current as Win32Exception;
                if (current is UnauthorizedAccessException || current is SecurityException ||
                    (socket != null && socket.SocketErrorCode == SocketError.AccessDenied) ||
                    (native != null && (native.NativeErrorCode == 5 || native.NativeErrorCode == 1314 || native.NativeErrorCode == 10013)))
                { reason = "ICMP 접근 권한·정책 오류"; break; }
                if (socket != null && (socket.SocketErrorCode == SocketError.HostNotFound ||
                    socket.SocketErrorCode == SocketError.NoData || socket.SocketErrorCode == SocketError.TryAgain))
                    reason = "대상 이름 확인 실패 (DNS)";
                else if (socket != null && (socket.SocketErrorCode == SocketError.NetworkUnreachable || socket.SocketErrorCode == SocketError.HostUnreachable))
                    reason = "네트워크·대상 도달 불가";
                else if (current is OperationCanceledException) reason = "ICMP 측정 취소";
            }
            return new ProbeSample
            {
                TimestampUtc = DateTime.UtcNow,
                Target = destination,
                Status = reason + " (응답 누락률에서 제외)",
                Countable = false,
                TimedOut = false
            };
        }

        private static bool TryNormalizeTarget(string value, out string normalized)
        {
            normalized = null;
            if (String.IsNullOrWhiteSpace(value)) return false;
            string candidate = value.Trim();
            if (candidate.Length > 254) return false;
            foreach (char character in candidate)
                if (Char.IsWhiteSpace(character) || Char.IsControl(character) || character == '/' || character == '\\' || character == '@') return false;
            bool numericIpv4 = true;
            foreach (char character in candidate)
                if ((character < '0' || character > '9') && character != '.') numericIpv4 = false;
            IPAddress address = null;
            if (numericIpv4)
            {
                // IPAddress accepts legacy octal and shortened IPv4; accept explicit decimal octets.
                string[] octets = candidate.Split('.');
                if (octets.Length != 4) return false;
                byte[] bytes = new byte[4];
                for (int i = 0; i < 4; i++)
                    if (octets[i].Length == 0 || octets[i].Length > 3 ||
                        !Byte.TryParse(octets[i], NumberStyles.None, CultureInfo.InvariantCulture, out bytes[i])) return false;
                address = new IPAddress(bytes);
            }
            else IPAddress.TryParse(candidate, out address);
            if (address != null)
            {
                if (address.AddressFamily == AddressFamily.InterNetwork && !numericIpv4) return false;
                byte[] raw = address.GetAddressBytes();
                if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6Multicast ||
                    (address.AddressFamily == AddressFamily.InterNetwork && (raw[0] >= 224 || raw[0] == 0))) return false;
                normalized = address.ToString();
                return true;
            }
            if (candidate.IndexOf(':') >= 0 || candidate.IndexOf('[') >= 0 || candidate.IndexOf(']') >= 0 || candidate.IndexOf('%') >= 0) return false;
            string ascii;
            try { ascii = new IdnMapping().GetAscii(candidate); }
            catch (ArgumentException) { return false; }
            if (ascii.EndsWith(".", StringComparison.Ordinal)) ascii = ascii.Substring(0, ascii.Length - 1);
            if (ascii.Length == 0 || ascii.Length > 253) return false;
            foreach (string label in ascii.Split('.'))
            {
                if (label.Length == 0 || label.Length > 63 || label[0] == '-' || label[label.Length - 1] == '-') return false;
                foreach (char character in label)
                    if (!(character >= 'a' && character <= 'z') && !(character >= 'A' && character <= 'Z') &&
                        !(character >= '0' && character <= '9') && character != '-') return false;
            }
            normalized = ascii.ToLowerInvariant();
            return true;
        }

        public static void SelfTest()
        {
            foreach (string value in new string[] { "192.168.0.1", "127.0.0.1", "::1", "fe80::1%12", "example.com", "router", "example.com." })
                Assert(IsValidTarget(value), "유효 대상 거부: " + value);
            foreach (string value in new string[] { "", "https://example.com", "example.com/path", "example.com:443", "a b", "x\\y", "a..b", "-bad.test", "127.1", "999.999.999.999", "0x7f000001", "0.0.0.0", "255.255.255.255", "224.0.0.1", "ff02::1" })
                Assert(!IsValidTarget(value), "잘못된 대상 허용: " + value);
            string normalized;
            Assert(TryNormalizeTarget("192.168.001.010", out normalized) && normalized == "192.168.1.10", "IPv4 십진수 정규화 실패");
            Assert(NormalizeTarget(" EXAMPLE.COM. ") == "example.com", "호스트 대소문자·마침표 정규화 실패");
            Assert(NormalizeTarget("192.168.001.010") == "192.168.1.10", "공개 IPv4 정규화 실패");
            Assert(NormalizeTarget("[::1]") == "::1", "IPv6 정규화 실패");
            bool rejected = false;
            try { NormalizeTarget("https://example.com"); }
            catch (ArgumentException) { rejected = true; }
            Assert(rejected, "잘못된 공개 정규화 입력 허용");
            ProbeSample success = FromReply("target", IPStatus.Success, 7);
            ProbeSample timeout = FromReply("target", IPStatus.TimedOut, 0);
            ProbeSample unreachable = FromReply("target", IPStatus.DestinationHostUnreachable, 0);
            Assert(success.Countable && !success.TimedOut && success.RoundtripMs == 7, "성공 분류 실패");
            Assert(timeout.Countable && timeout.TimedOut && timeout.RoundtripMs == null, "시간 초과 분류 실패");
            Assert(!unreachable.Countable && !unreachable.TimedOut, "도달 불가를 손실로 분류");
            ProbeSample dns = FromException("target", new PingException("test", new SocketException((int)SocketError.HostNotFound)));
            ProbeSample denied = FromException("target", new PingException("test", new Win32Exception(5)));
            Assert(!dns.Countable && dns.Status.IndexOf("DNS", StringComparison.Ordinal) >= 0, "DNS 오류 분류 실패");
            Assert(!denied.Countable && denied.Status.IndexOf("권한", StringComparison.Ordinal) >= 0, "권한 오류 분류 실패");
            using (ProbeRunner runner = new ProbeRunner())
            {
                int delivered = 0;
                runner.Sampled += delegate(ProbeSample ignored) { delivered++; };
                lock (runner.gate) { runner.running = true; runner.generation = 3; }
                runner.Publish(2, success);
                runner.Publish(3, success);
                runner.Stop();
                runner.Publish(3, timeout);
                lock (runner.gate) { runner.running = true; runner.generation = 5; }
                runner.Publish(3, success);
                Assert(delivered == 1, "중지·재시작 이후 이전 응답 전달");
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("ProbeRunner self-test: " + message);
        }
    }
}
