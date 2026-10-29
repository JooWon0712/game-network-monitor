using System;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;

namespace PcInternetMonitor
{
    // Kernel network events contain metadata, not the application's transmitted content.
    // These are ETW events/bytes, not counts of packets observed on the wire.
    // Schema: learn.microsoft.com/windows/win32/etw/{tcpip,udpip}-typegroup{1,2,3}
    // v1/v2 share the prefix below; pointer-width-sensitive fields after it are ignored.
    public sealed class KernelNetworkMonitor : IDisposable
    {
        private static readonly Guid TcpProvider = new Guid("9a280ac0-c8e0-11d1-84e2-00c04fb998a2");
        private static readonly Guid UdpProvider = new Guid("bf3a50c5-a9c9-4988-a005-2df0b7c80f80");
        private const ulong InvalidTrace = ulong.MaxValue;
        private readonly object lifecycle = new object();
        private Context current;
        private TraceHealth last = new TraceHealth();
        private bool disposed;

        public event Action<TrafficEvent> EventReceived;

        private sealed class Context
        {
            internal readonly object NativeGate = new object();
            internal readonly object HealthGate = new object();
            internal readonly TraceHealth Health = new TraceHealth();
            internal int ProcessId;
            internal long CaptureGeneration;
            internal bool Started, ConsumerStarted;
            internal string Name;
            internal IntPtr NameBuffer;
            internal IntPtr Properties;
            internal ulong Session;
            internal ulong Consumer = InvalidTrace;
            internal Thread Worker;
            internal EventRecordCallback RecordCallback;
            internal BufferCallback BufferCallback;
            internal volatile bool Stopping;
            internal volatile bool DrainRequested;
            internal uint ConsumerExitCode;
            internal bool Finished;
            internal bool StopFailed;
            internal DateTime LastQueryUtc;
            internal long CandidateEvents, SelectedPidEvents, UnsupportedEvents;
            internal bool IncludeOtherProcesses;
            internal readonly ProcessTrafficTracker ProcessTraffic = new ProcessTrafficTracker();
        }

        public void Start(int processId)
        {
            Start(processId, false);
        }

        public void Start(int processId, bool includeOtherProcesses, long captureGeneration = 0)
        {
            if (processId < 0 || (processId == 0 && !includeOtherProcesses)) throw new ArgumentOutOfRangeException("processId");
            lock (lifecycle)
            {
                if (disposed) throw new ObjectDisposedException("KernelNetworkMonitor");
                StopLocked();
                if (current != null) return; // A previous own session could not be stopped; retain it for retry.
                Context c = new Context();
                c.ProcessId = processId;
                c.CaptureGeneration = captureGeneration;
                c.IncludeOtherProcesses = includeOtherProcesses;
                c.Name = "PcInternetMonitor-" + Guid.NewGuid().ToString("N");
                current = c;
                string failureStage = "ETW 초기화";
                try
                {
                    ValidateNativeLayouts();
                    c.NameBuffer = Marshal.StringToHGlobalUni(c.Name);
                    int size = Marshal.SizeOf(typeof(EventTraceProperties));
                    int total = checked(size + (c.Name.Length + 1) * 2);
                    c.Properties = Marshal.AllocHGlobal(total);
                    Marshal.Copy(new byte[total], 0, c.Properties, total);
                    EventTraceProperties p = new EventTraceProperties();
                    p.Wnode.BufferSize = (uint)total;
                    p.Wnode.Guid = Guid.NewGuid();
                    p.Wnode.ClientContext = 1; // QPC. ProcessTrace converts to UTC FILETIME.
                    p.Wnode.Flags = 0x00020000; // WNODE_FLAG_TRACED_GUID
                    p.BufferSize = 64; // KB; no file output and bounded session buffer pool.
                    p.MinimumBuffers = 2;
                    p.MaximumBuffers = 64;
                    p.LogFileMode = 0x00000100 | 0x02000000; // REAL_TIME | SYSTEM_LOGGER
                    p.FlushTimer = 1;
                    p.EnableFlags = 0x00010000 | 0x10000000; // NETWORK_TCPIP | NO_SYSCONFIG
                    p.LoggerNameOffset = (uint)size;
                    Marshal.StructureToPtr(p, c.Properties, false);
                    failureStage = "StartTrace";
                    uint result = StartTrace(out c.Session, c.Name, c.Properties);
                    if (result != 0)
                    {
                        // Never attach to, update, or stop an existing logger on name collision.
                        c.Session = 0;
                        throw new Win32Exception((int)result);
                    }
                    c.RecordCallback = delegate(IntPtr record) { OnRecord(c, record); };
                    c.BufferCallback = delegate(IntPtr log) { return c.Stopping ? 0u : 1u; };
                    EventTraceLogfile logFile = new EventTraceLogfile();
                    logFile.LoggerName = c.NameBuffer;
                    logFile.ProcessTraceMode = 0x00000100 | 0x10000000; // REAL_TIME | EVENT_RECORD
                    logFile.EventRecordCallback = Marshal.GetFunctionPointerForDelegate(c.RecordCallback);
                    logFile.BufferCallback = Marshal.GetFunctionPointerForDelegate(c.BufferCallback);
                    failureStage = "OpenTrace";
                    c.Consumer = OpenTrace(ref logFile);
                    if (c.Consumer == InvalidTrace) throw new Win32Exception(Marshal.GetLastWin32Error());
                    SetStatus(c, true, processId > 0 ? "게임 ETW 수집 중 · 선택한 PID의 TCP/UDP 메타데이터" :
                        "프로그램별 TCP/UDP 집계 중 · 모든 어댑터");
                    c.Started = true;
                    c.Worker = new Thread(delegate() { Consume(c); });
                    c.Worker.IsBackground = true;
                    c.Worker.Name = "Game network ETW consumer";
                    failureStage = "수집 스레드 시작";
                    c.Worker.Start();
                }
                catch (Exception ex)
                {
                    c.Stopping = true;
                    c.Finished = true;
                    ReleaseNative(c);
                    SetFailure(c, ex, failureStage);
                }
            }
        }

        public TraceHealth GetHealth()
        {
            Context c;
            lock (lifecycle)
            {
                c = current;
                if (c == null) return Clone(last);
            }
            // Query loss counters at most once a second, independently from data callbacks.
            lock (c.NativeGate)
            {
                if (!c.DrainRequested && c.Session != 0 && c.Properties != IntPtr.Zero &&
                    (DateTime.UtcNow - c.LastQueryUtc).TotalSeconds >= 1)
                {
                    c.LastQueryUtc = DateTime.UtcNow;
                    uint result = ControlTrace(c.Session, null, c.Properties, 0); // QUERY own handle only
                    if (result == 0) ReadLossCounters(c);
                    else if (result == 4201) SetStatus(c, false, "ETW 세션이 종료됨 · 게임 통신 수치 사용 불가");
                }
            }
            lock (c.HealthGate)
            {
                TraceHealth health = Clone(c.Health);
                CopyDiagnosticCounters(c, health);
                return health;
            }
        }

        public ProcessTrafficSnapshot GetProcessTraffic(DateTime now)
        {
            Context c;
            lock (lifecycle) c = current;
            if (c == null)
                return new ProcessTrafficSnapshot { TimestampUtc = now, Available = false,
                    Status = "프로그램별 통신 집계용 ETW 세션이 없음" };
            if (!c.IncludeOtherProcesses)
                return new ProcessTrafficSnapshot { TimestampUtc = now, Available = false,
                    Status = "프로그램별 통신 기록 꺼짐" };
            lock (c.HealthGate)
            {
                if (!c.Health.Active || c.Stopping)
                    return new ProcessTrafficSnapshot { TimestampUtc = now, Available = false,
                        Status = c.Health.Status };
            }
            return c.ProcessTraffic.GetSnapshot(now);
        }

        public void Stop()
        {
            lock (lifecycle) StopLocked();
        }

        public TraceStopResult StopAndCapture(int timeoutMilliseconds)
        {
            TraceStopResult result = new TraceStopResult { RequestedUtc = DateTime.UtcNow };
            Context c;
            lock (lifecycle)
            {
                c = current;
                if (c == null)
                {
                    result.Health = Clone(last); result.Drained = false;
                    result.Status = "ETW 수집 세션 없음 · 최종 ETW 자료 미확인"; return result;
                }
                if (c.DrainRequested) { result.Health = GetHealth(); result.Status = "ETW 종료 처리 중 · 중복 종료 요청"; return result; }
                c.DrainRequested = true;
            }
            uint stopCode = 0;
            bool stopIssued = false;
            bool wasActive; lock (c.HealthGate) wasActive = c.Health.Active;
            try
            {
                ulong handle; IntPtr properties;
                lock (c.NativeGate) { handle = c.Session; properties = c.Properties; }
                // Keep callbacks and the consumer alive while STOP flushes the owned session.
                // DrainRequested keeps the worker from freeing this control buffer meanwhile.
                if (handle != 0 && properties != IntPtr.Zero)
                {
                    stopIssued = true;
                    stopCode = ControlTrace(handle, null, properties, 1);
                    lock (c.NativeGate)
                    {
                        if (stopCode == 0 || stopCode == 4201)
                        {
                            if (stopCode == 0) ReadLossCounters(c);
                            c.Session = 0;
                        }
                        else c.StopFailed = true;
                    }
                }
                bool finished = c.Worker == null || (c.Worker != Thread.CurrentThread && c.Worker.Join(Math.Max(0, timeoutMilliseconds)));
                result.TimedOut = !finished; result.StopNativeCode = stopCode; result.ConsumerExitCode = c.ConsumerExitCode;
                result.Drained = wasActive && c.Started && c.ConsumerStarted && stopIssued && finished && stopCode == 0 && c.ConsumerExitCode == 0;
                result.Status = result.Drained ? "종료 버퍼·최종 구간 반영" : "종료 버퍼 반영 미확인 · 끝부분 누락 가능";
                if (!c.Started || !c.ConsumerStarted) result.Status = "ETW 수집 시작/소비 확인 안 됨 · 최종 ETW 자료 미확인";
                else if (!stopIssued) result.Status = "ETW 종료 제어 핸들 없음 · 최종 ETW 자료 미확인";
                result.Status += " (STOP " + stopCode + ", consumer " + c.ConsumerExitCode + ", 대기초과 " + (!finished) + ")";
            }
            catch (Exception error)
            {
                result.Drained = false; result.Status = "종료 버퍼 반영 실패 · 끝부분 누락 가능";
                SetFailure(c, error, "StopAndCapture");
            }
            finally
            {
                c.Stopping = true;
                c.DrainRequested = false;
                ReleaseNative(c);
            }
            DateTime captured = DateTime.UtcNow;
            try
            {
                result.ProcessTraffic = c.IncludeOtherProcesses && c.Started && c.ConsumerStarted ? c.ProcessTraffic.GetSnapshot(captured, true) :
                    new ProcessTrafficSnapshot { TimestampUtc = captured, Available = false, Status = "프로그램별 최종 자료 없음 · 수집 요청/시작 확인 필요" };
                if (c.IncludeOtherProcesses && !result.ProcessTraffic.Available)
                { result.Drained = false; result.Status += " · 최종 프로그램 구간 미확인"; }
            }
            catch (Exception error)
            {
                result.Drained = false; result.Status += " · 최종 프로그램 집계 실패";
                result.ProcessTraffic = new ProcessTrafficSnapshot { TimestampUtc = captured, Available = false, Status = "최종 집계 실패 · " + error.Message };
                SetFailure(c, error, "FinalProcessSnapshot");
            }
            lock (c.HealthGate)
            {
                c.Health.Active = false;
                c.Health.Status = result.Status;
                result.Health = Clone(c.Health); CopyDiagnosticCounters(c, result.Health);
                if (result.Health.EventsLost > 0 || result.Health.BuffersLost > 0 || result.Health.MalformedEvents > 0 || result.Health.UnsupportedEvents > 0)
                { result.Status += " · 일부 수집 기록 제외됨"; result.Health.Status = result.Status; }
            }
            lock (lifecycle)
            {
                last = Clone(result.Health);
                if (Object.ReferenceEquals(current, c) && c.Session == 0) current = null;
            }
            return result;
        }

        private void StopLocked()
        {
            Context c = current;
            if (c == null) return;
            c.Stopping = true;
            ReleaseNative(c);
            // CloseTrace cancels ProcessTrace. Do not join here: a client callback may
            // be waiting for a client lock held by this caller. The worker roots c
            // and frees consumer allocations itself after ProcessTrace returns.
            lock (c.HealthGate)
            {
                c.Health.Active = false;
                if (!c.StopFailed) c.Health.Status = "게임 ETW 수집 중지";
                last = Clone(c.Health);
                CopyDiagnosticCounters(c, last);
            }
            lock (c.NativeGate)
            {
                if (c.Session == 0) current = null;
            }
        }

        public void Dispose()
        {
            lock (lifecycle)
            {
                if (disposed) return;
                StopLocked();
                disposed = true;
            }
        }

        private void Consume(Context c)
        {
            try
            {
                ulong handle;
                lock (c.NativeGate) handle = c.Consumer;
                if (!c.Stopping && handle != InvalidTrace)
                {
                    c.ConsumerStarted = true;
                    uint result = ProcessTrace(new ulong[] { handle }, 1, IntPtr.Zero, IntPtr.Zero);
                    c.ConsumerExitCode = result;
                    if (!c.Stopping && !c.DrainRequested)
                    {
                        if (result == 0) SetStatus(c, false, "ETW 세션이 종료됨 · 게임 통신 수치 사용 불가");
                        else SetFailure(c, new Win32Exception((int)result), "ProcessTrace");
                    }
                }
            }
            catch (Exception ex)
            {
                c.ConsumerExitCode = UInt32.MaxValue;
                if (!c.Stopping) SetFailure(c, ex, "ProcessTrace");
            }
            finally
            {
                // ProcessTrace has returned: no callback can still be using these allocations.
                lock (c.NativeGate) c.Finished = true;
                ReleaseNative(c);
                GC.KeepAlive(c.RecordCallback);
                GC.KeepAlive(c.BufferCallback);
            }
        }

        private static void ReleaseNative(Context c)
        {
            lock (c.NativeGate)
            {
                // A graceful stop caller owns the control allocation until its native
                // STOP call and lock-free consumer wait have both completed.
                if (c.DrainRequested) return;
                if (c.Consumer != InvalidTrace)
                {
                    CloseTrace(c.Consumer);
                    c.Consumer = InvalidTrace;
                }
                if (c.Session != 0 && c.Properties != IntPtr.Zero)
                {
                    uint result = ControlTrace(c.Session, null, c.Properties, 1); // STOP own session
                    if (result == 0 || result == 4201)
                    {
                        if (result == 0) ReadLossCounters(c);
                        c.Session = 0;
                    }
                    else
                    {
                        c.StopFailed = true;
                        SetStatus(c, false, "ETW 정리 실패 (" + result + ") · 프로그램을 종료한 뒤 세션 상태 확인 필요");
                    }
                }
                if (c.Finished)
                {
                    // A rare STOP failure retains the control allocation for a retry by Stop().
                    if (c.Session == 0 && c.Properties != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(c.Properties);
                        c.Properties = IntPtr.Zero;
                    }
                    if (c.NameBuffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(c.NameBuffer);
                        c.NameBuffer = IntPtr.Zero;
                    }
                }
            }
        }

        private static void ReadLossCounters(Context c)
        {
            EventTraceProperties p = (EventTraceProperties)Marshal.PtrToStructure(c.Properties, typeof(EventTraceProperties));
            lock (c.HealthGate)
            {
                c.Health.EventsLost = Math.Max(c.Health.EventsLost, (long)p.EventsLost);
                c.Health.BuffersLost = Math.Max(c.Health.BuffersLost, (long)p.LogBuffersLost + p.RealTimeBuffersLost);
            }
        }

        private void OnRecord(Context c, IntPtr record)
        {
            if (c.Stopping || record == IntPtr.Zero) return;
            try
            {
                EventRecord r = (EventRecord)Marshal.PtrToStructure(record, typeof(EventRecord));
                bool tcp, ipv6, send, retransmit;
                if (!Classify(r.Header.ProviderId, r.Header.Descriptor.Opcode, out tcp, out ipv6, out send, out retransmit)) return;
                // No unselected process addresses or content are decoded or retained.
                Interlocked.Increment(ref c.CandidateEvents);
                // Only documented v1/v2 schemas. Do not guess a new schema or use header PID.
                if (r.Header.Descriptor.Version != 1 && r.Header.Descriptor.Version != 2)
                {
                    Interlocked.Increment(ref c.UnsupportedEvents);
                    return;
                }
                if (r.UserData == IntPtr.Zero || r.UserDataLength < 4) { Malformed(c); return; }
                int payloadPid = Marshal.ReadInt32(r.UserData, 0);
                if (c.IncludeOtherProcesses && r.UserDataLength >= (ipv6 ? 44 : 20))
                    c.ProcessTraffic.Record(payloadPid, unchecked((uint)Marshal.ReadInt32(r.UserData, 4)), send, retransmit,
                        DateTime.FromFileTimeUtc(r.Header.Timestamp));
                if (c.ProcessId <= 0 || payloadPid != c.ProcessId) return;
                Interlocked.Increment(ref c.SelectedPidEvents);
                int length = ipv6 ? 44 : 20;
                if (r.UserDataLength < length) { Malformed(c); return; }
                byte[] prefix = new byte[length];
                Marshal.Copy(r.UserData, prefix, 0, prefix.Length);
                TrafficEvent value = Decode(r.Header.ProviderId, r.Header.Descriptor.Opcode,
                    r.Header.Descriptor.Version, prefix, c.ProcessId, r.Header.Timestamp);
                if (value == null) { Malformed(c); return; }
                value.CaptureGeneration = c.CaptureGeneration;
                Action<TrafficEvent> receiver = EventReceived;
                if (receiver != null && !c.Stopping)
                {
                    try { receiver(value); }
                    catch (Exception) { SetStatus(c, true, "게임 ETW 수집 중 · 수집 결과 전달 오류"); }
                }
            }
            catch (Exception)
            {
                // Never allow a managed exception to escape the native callback boundary.
                Malformed(c);
            }
        }

        private static bool Classify(Guid provider, byte opcode, out bool tcp, out bool ipv6, out bool send, out bool retransmit)
        {
            tcp = provider == TcpProvider;
            ipv6 = opcode == 26 || opcode == 27 || opcode == 30;
            retransmit = tcp && (opcode == 14 || opcode == 30);
            send = opcode == 10 || opcode == 26 || retransmit;
            return (tcp || provider == UdpProvider) &&
                (opcode == 10 || opcode == 11 || opcode == 26 || opcode == 27 || retransmit);
        }

        private static TrafficEvent Decode(Guid provider, byte opcode, byte version, byte[] prefix, int selectedPid, long fileTime)
        {
            bool tcp, ipv6, send, retransmit;
            if (!Classify(provider, opcode, out tcp, out ipv6, out send, out retransmit) ||
                (version != 1 && version != 2) || prefix == null || prefix.Length < (ipv6 ? 44 : 20)) return null;
            int pid = unchecked((int)ReadUInt32(prefix, 0));
            if (pid != selectedPid) return null;
            int addressLength = ipv6 ? 16 : 4;
            byte[] destination = new byte[addressLength], source = new byte[addressLength];
            Buffer.BlockCopy(prefix, 8, destination, 0, addressLength);
            Buffer.BlockCopy(prefix, 8 + addressLength, source, 0, addressLength);
            int ports = 8 + 2 * addressLength;
            TrafficEvent result = new TrafficEvent();
            result.ProcessId = pid;
            result.Protocol = tcp ? "TCP" : "UDP";
            result.IsSend = send;
            result.IsRetransmit = retransmit;
            result.Bytes = ReadUInt32(prefix, 4);
            result.SourceAddress = new IPAddress(source).ToString();
            result.DestinationAddress = new IPAddress(destination).ToString();
            result.DestinationPort = (prefix[ports] << 8) | prefix[ports + 1];
            result.SourcePort = (prefix[ports + 2] << 8) | prefix[ports + 3];
            result.TimestampUtc = DateTime.FromFileTimeUtc(fileTime);
            return result;
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);
        }

        private static void SetStatus(Context c, bool active, string text)
        {
            lock (c.HealthGate) { c.Health.Active = active; c.Health.Status = text; }
        }

        private static void Malformed(Context c)
        {
            lock (c.HealthGate) c.Health.MalformedEvents++;
        }

        private static TraceHealth Clone(TraceHealth h)
        {
            return new TraceHealth { Active = h.Active, Status = h.Status, EventsLost = h.EventsLost,
                BuffersLost = h.BuffersLost, MalformedEvents = h.MalformedEvents,
                PermissionDenied = h.PermissionDenied, NativeErrorCode = h.NativeErrorCode, FailureStage = h.FailureStage,
                CandidateEvents = h.CandidateEvents, SelectedPidEvents = h.SelectedPidEvents, UnsupportedEvents = h.UnsupportedEvents };
        }

        private static void CopyDiagnosticCounters(Context c, TraceHealth health)
        {
            health.CandidateEvents = Interlocked.Read(ref c.CandidateEvents);
            health.SelectedPidEvents = Interlocked.Read(ref c.SelectedPidEvents);
            health.UnsupportedEvents = Interlocked.Read(ref c.UnsupportedEvents);
        }

        private static void SetFailure(Context c, Exception ex, string stage)
        {
            Win32Exception win = ex as Win32Exception;
            int code = win == null ? 0 : win.NativeErrorCode;
            lock (c.HealthGate)
            {
                c.Health.Active = false;
                c.Health.NativeErrorCode = code;
                c.Health.FailureStage = stage;
                c.Health.PermissionDenied = code == 5 || code == 1314;
                c.Health.Status = FailureText(ex, stage);
            }
        }

        private static string FailureText(Exception ex, string stage)
        {
            Win32Exception win = ex as Win32Exception;
            if (win != null)
            {
                string detail = " (" + stage + " · 오류 " + win.NativeErrorCode + ")";
                if (win.NativeErrorCode == 5 || win.NativeErrorCode == 1314)
                    return "ETW 권한 부족 · 게임 통신 수치 사용 불가 · 관리자 권한으로 실행 필요" + detail;
                if (win.NativeErrorCode == 1450)
                    return "ETW 세션 자원 부족 · 다른 수집 작업을 끝낸 뒤 다시 시도" + detail;
                return "ETW 사용 실패 · 게임 통신 수치 사용 불가" + detail;
            }
            return "ETW 사용 불가 (" + stage + ") · " + ex.GetType().Name + ": " + ex.Message;
        }

        private static void ValidateNativeLayouts()
        {
            if (IntPtr.Size != 8) throw new PlatformNotSupportedException("64비트 실행 파일이 필요합니다.");
            if (Marshal.SizeOf(typeof(EventTraceProperties)) != 120 || Marshal.SizeOf(typeof(EventHeader)) != 80 ||
                Marshal.SizeOf(typeof(EventRecord)) != 112 || Marshal.SizeOf(typeof(EventTraceLogfile)) != 448 ||
                Marshal.OffsetOf(typeof(EventRecord), "UserData").ToInt32() != 96 ||
                Marshal.OffsetOf(typeof(EventTraceProperties), "LoggerNameOffset").ToInt32() != 116)
                throw new PlatformNotSupportedException("ETW 구조체 배치 검증 실패");
        }

        // Pure local fixtures: no session is opened and no network traffic is generated.
        public static void SelfTest()
        {
            ValidateNativeLayouts();
            long time = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).ToFileTimeUtc();
            byte[] v4 = new byte[20];
            v4[0] = 0x34; v4[1] = 0x12;
            v4[4] = 0x78; v4[5] = 0x56; v4[6] = 0x34; v4[7] = 0x12;
            v4[8] = 203; v4[9] = 0; v4[10] = 113; v4[11] = 7;
            v4[12] = 192; v4[13] = 168; v4[14] = 1; v4[15] = 8;
            v4[16] = 0x01; v4[17] = 0xbb; v4[18] = 0xc0; v4[19] = 0x01;
            TrafficEvent a = Decode(TcpProvider, 10, 2, v4, 0x1234, time);
            Require(a != null && a.SourceAddress == "192.168.1.8" && a.DestinationAddress == "203.0.113.7" &&
                a.SourcePort == 49153 && a.DestinationPort == 443 && a.Bytes == 0x12345678 && a.IsSend &&
                !a.IsRetransmit && a.Protocol == "TCP" && a.TimestampUtc.ToFileTimeUtc() == time, "IPv4/endian/time");
            Require(Decode(TcpProvider, 11, 1, v4, 0x1234, time).IsSend == false, "IPv4 receive");
            Require(Decode(UdpProvider, 10, 2, v4, 0x1234, time).Protocol == "UDP", "UDP send");
            Require(Decode(UdpProvider, 11, 2, v4, 0x1234, time).IsSend == false, "UDP receive");
            Require(Decode(TcpProvider, 14, 2, v4, 0x1234, time).IsRetransmit, "TCP retransmit");
            Require(Decode(UdpProvider, 14, 2, v4, 0x1234, time) == null, "UDP has no retransmit");
            Require(Decode(TcpProvider, 12, 2, v4, 0x1234, time) == null, "connect ignored");
            Require(Decode(TcpProvider, 10, 3, v4, 0x1234, time) == null, "unknown schema ignored");
            Require(Decode(TcpProvider, 10, 2, v4, 1, time) == null, "PID filtered");
            Require(Decode(TcpProvider, 10, 2, new byte[19], 0, time) == null, "short metadata rejected");
            Require(Decode(Guid.Empty, 10, 2, v4, 0x1234, time) == null, "provider filtered");
            byte[] v6 = new byte[44];
            Buffer.BlockCopy(v4, 0, v6, 0, 8);
            Buffer.BlockCopy(IPAddress.Parse("2001:db8::2").GetAddressBytes(), 0, v6, 8, 16);
            Buffer.BlockCopy(IPAddress.Parse("2001:db8::1").GetAddressBytes(), 0, v6, 24, 16);
            v6[40] = 0x12; v6[41] = 0x34; v6[42] = 0xab; v6[43] = 0xcd;
            TrafficEvent b = Decode(TcpProvider, 26, 2, v6, 0x1234, time);
            Require(b != null && b.SourceAddress == "2001:db8::1" && b.DestinationAddress == "2001:db8::2" &&
                b.DestinationPort == 4660 && b.SourcePort == 43981 && b.IsSend, "IPv6/endian");
            Require(!Decode(TcpProvider, 27, 2, v6, 0x1234, time).IsSend, "IPv6 receive");
            Require(Decode(TcpProvider, 30, 2, v6, 0x1234, time).IsRetransmit, "IPv6 retransmit");
            Require(Decode(UdpProvider, 26, 2, v6, 0x1234, time).IsSend, "UDPv6 send");
            Require(!Decode(UdpProvider, 27, 2, v6, 0x1234, time).IsSend, "UDPv6 receive");
            TraceHealth original = new TraceHealth { Active = true, EventsLost = 3, BuffersLost = 4, MalformedEvents = 5,
                PermissionDenied = true, NativeErrorCode = 5, FailureStage = "StartTrace",
                CandidateEvents = 7, SelectedPidEvents = 2, UnsupportedEvents = 1 };
            TraceHealth copy = Clone(original); copy.EventsLost = 100;
            Require(original.EventsLost == 3 && copy.BuffersLost == 4 && copy.MalformedEvents == 5 &&
                copy.PermissionDenied && copy.NativeErrorCode == 5 && copy.FailureStage == "StartTrace" &&
                copy.CandidateEvents == 7 && copy.SelectedPidEvents == 2 && copy.UnsupportedEvents == 1, "health clone");
            Context failure = new Context();
            SetFailure(failure, new Win32Exception(5), "StartTrace");
            Require(failure.Health.PermissionDenied && failure.Health.NativeErrorCode == 5 &&
                failure.Health.FailureStage == "StartTrace" && failure.Health.Status.Contains("StartTrace") &&
                failure.Health.Status.Contains("오류 5"), "StartTrace access denied diagnostics");
            SetFailure(failure, new Win32Exception(1314), "OpenTrace");
            Require(failure.Health.PermissionDenied && failure.Health.NativeErrorCode == 1314 &&
                failure.Health.FailureStage == "OpenTrace", "OpenTrace privilege diagnostics");
            SetFailure(failure, new Win32Exception(1450), "StartTrace");
            Require(!failure.Health.PermissionDenied && failure.Health.NativeErrorCode == 1450 &&
                failure.Health.Status.Contains("자원 부족"), "resource failure not permission denied");
            SetFailure(failure, new PlatformNotSupportedException("fixture"), "ETW 초기화");
            Require(!failure.Health.PermissionDenied && failure.Health.NativeErrorCode == 0 &&
                failure.Health.FailureStage == "ETW 초기화", "managed failure clears native permission diagnostics");
            NativeCallbackFixture(v4, time);
            ProcessTrafficTracker.SelfTest();
            SyntheticDrainSelfTest();
        }

        // Managed coordination checks only. Every Context has zero native handles:
        // these fixtures cannot establish that native ETW STOP/flush succeeds.
        private static void SyntheticDrainSelfTest()
        {
            SyntheticDrainWorkerFixture(false);
            SyntheticDrainWorkerFixture(true);

            using (KernelNetworkMonitor monitor = new KernelNetworkMonitor())
            {
                TraceStopResult result = monitor.StopAndCapture(0);
                Require(!result.Drained && !result.TimedOut && !result.ProcessTraffic.Available && result.Status.Contains("세션 없음"),
                    "no context cannot report a completed native drain");
            }
            using (KernelNetworkMonitor monitor = new KernelNetworkMonitor())
            {
                Context c = new Context { Started = true, ConsumerStarted = false, Finished = true };
                c.Health.Active = true; monitor.current = c;
                TraceStopResult result = monitor.StopAndCapture(0);
                Require(!result.Drained && !result.ProcessTraffic.Available && result.Status.Contains("시작/소비 확인 안 됨"),
                    "consumer not started cannot report drain completion");
            }
            using (KernelNetworkMonitor monitor = new KernelNetworkMonitor())
            {
                Context c = new Context { Started = false, Finished = true };
                SetFailure(c, new Win32Exception(5), "synthetic consumer startup"); monitor.current = c;
                TraceStopResult result = monitor.StopAndCapture(0);
                Require(!result.Drained && result.Health.PermissionDenied && !result.ProcessTraffic.Available,
                    "failed startup cannot report drain completion or lose its failure state");
            }
            using (KernelNetworkMonitor monitor = new KernelNetworkMonitor())
            {
                Context c = new Context { Started = true, ConsumerStarted = true, ConsumerExitCode = 5, Finished = true };
                c.Health.Active = true; monitor.current = c;
                TraceStopResult result = monitor.StopAndCapture(0);
                Require(!result.Drained && result.ConsumerExitCode == 5 && !result.Health.Active,
                    "failed synthetic consumer cannot report drain completion");
            }
        }

        private static void SyntheticDrainWorkerFixture(bool timeOut)
        {
            KernelNetworkMonitor monitor = new KernelNetworkMonitor();
            Context c = new Context { Started = true, IncludeOtherProcesses = true, ProcessId = Int32.MaxValue };
            c.Health.Active = true; monitor.current = c;
            ManualResetEvent ready = new ManualResetEvent(false), release = new ManualResetEvent(false);
            Exception workerError = null;
            int callbacks = 0;
            bool acquiredLocks = false, deliveredDuringDrain = false;
            monitor.EventReceived += delegate(TrafficEvent value) { Interlocked.Increment(ref callbacks); };
            Thread worker = new Thread(delegate() {
                try
                {
                    c.ConsumerStarted = true; ready.Set();
                    if (!release.WaitOne(3000)) throw new InvalidOperationException("synthetic worker release was not signalled");
                    if (timeOut) return;
                    if (!SpinWait.SpinUntil(delegate { return c.DrainRequested; }, 2000))
                        throw new InvalidOperationException("synthetic worker did not observe the drain request");
                    bool gotLifecycle = false, gotNative = false, gotHealth = false;
                    try
                    {
                        gotLifecycle = Monitor.TryEnter(monitor.lifecycle, 500);
                        if (gotLifecycle) gotNative = Monitor.TryEnter(c.NativeGate, 500);
                        if (gotNative) gotHealth = Monitor.TryEnter(c.HealthGate, 500);
                        acquiredLocks = gotLifecycle && gotNative && gotHealth;
                    }
                    finally
                    {
                        if (gotHealth) Monitor.Exit(c.HealthGate);
                        if (gotNative) Monitor.Exit(c.NativeGate);
                        if (gotLifecycle) Monitor.Exit(monitor.lifecycle);
                    }
                    if (!acquiredLocks) throw new InvalidOperationException("consumer wait retained a monitor lock");
                    deliveredDuringDrain = c.DrainRequested && !c.Stopping;
                    SyntheticDrainRecord(monitor, c);
                }
                catch (Exception error) { workerError = error; c.ConsumerExitCode = UInt32.MaxValue; }
                finally { c.Finished = true; }
            });
            worker.IsBackground = true; worker.Name = "Synthetic ETW drain fixture"; c.Worker = worker;
            try
            {
                Require(c.Session == 0 && c.Properties == IntPtr.Zero && c.Consumer == InvalidTrace,
                    "synthetic fixture must never own native trace handles");
                worker.Start();
                Require(ready.WaitOne(2000), "synthetic consumer did not start");
                if (!timeOut) release.Set();
                TraceStopResult result = monitor.StopAndCapture(timeOut ? 0 : 5000);
                Require(!result.Drained, "no native STOP was issued, so managed worker completion cannot claim a native drain");
                if (timeOut)
                {
                    Require(result.TimedOut && worker.IsAlive && callbacks == 0,
                        "blocked synthetic consumer must time out without claiming final callbacks");
                }
                else
                {
                    Require(!result.TimedOut && !worker.IsAlive && workerError == null && acquiredLocks && deliveredDuringDrain,
                        "stop did not wait outside its locks for a synthetic consumer to finish");
                    Require(callbacks == 1 && result.ProcessTraffic.Available && result.ProcessTraffic.Rows.Count == 1 &&
                        result.ProcessTraffic.Rows[0].ProcessId == Int32.MaxValue && result.ProcessTraffic.Rows[0].SentBytesTotal == 512,
                        "last process bytes delivered during drain were not captured");
                    Require(!result.Health.Active && c.Stopping,
                        "synthetic capture did not finish with inactive collection");
                }
            }
            finally
            {
                // Every wait is bounded and every started worker is released even when
                // an assertion fails. Do not dispose its gates while it can still use them.
                release.Set();
                bool joined = (worker.ThreadState & ThreadState.Unstarted) != 0 || worker.Join(5000);
                monitor.Dispose();
                if (joined) { ready.Close(); release.Close(); }
                Require(joined, "synthetic consumer cleanup did not finish within its bound");
            }
            Require(workerError == null, "synthetic consumer failed during cleanup");
        }

        private static void SyntheticDrainRecord(KernelNetworkMonitor monitor, Context c)
        {
            byte[] prefix = new byte[20];
            Buffer.BlockCopy(BitConverter.GetBytes(c.ProcessId), 0, prefix, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(512), 0, prefix, 4, 4);
            IntPtr metadata = Marshal.AllocHGlobal(prefix.Length);
            IntPtr nativeRecord = IntPtr.Zero;
            try
            {
                nativeRecord = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(EventRecord)));
                Marshal.Copy(prefix, 0, metadata, prefix.Length);
                EventRecord record = new EventRecord();
                record.Header.Size = 80; record.Header.ProviderId = TcpProvider;
                record.Header.Descriptor.Version = 2; record.Header.Descriptor.Opcode = 10;
                record.Header.Timestamp = DateTime.UtcNow.ToFileTimeUtc();
                record.UserDataLength = (ushort)prefix.Length; record.UserData = metadata;
                Marshal.StructureToPtr(record, nativeRecord, false);
                monitor.OnRecord(c, nativeRecord);
            }
            finally
            {
                if (nativeRecord != IntPtr.Zero) Marshal.FreeHGlobal(nativeRecord);
                Marshal.FreeHGlobal(metadata);
            }
        }

        private static void NativeCallbackFixture(byte[] prefix, long time)
        {
            Context c = new Context(); c.ProcessId = 0x1234;
            KernelNetworkMonitor monitor = new KernelNetworkMonitor();
            TrafficEvent observed = null;
            int calls = 0;
            monitor.EventReceived += delegate(TrafficEvent value) { observed = value; calls++; };
            IntPtr metadata = Marshal.AllocHGlobal(prefix.Length);
            IntPtr nativeRecord = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(EventRecord)));
            try
            {
                Marshal.Copy(prefix, 0, metadata, prefix.Length);
                EventRecord record = new EventRecord();
                record.Header.Size = 80;
                record.Header.ProcessId = 999999; // Deliberately incorrect: payload PID must win.
                record.Header.ProviderId = TcpProvider;
                record.Header.Descriptor.Version = 2;
                record.Header.Descriptor.Opcode = 11;
                record.Header.Timestamp = time;
                record.UserDataLength = (ushort)prefix.Length;
                record.UserData = metadata;
                Marshal.StructureToPtr(record, nativeRecord, false);
                monitor.OnRecord(c, nativeRecord);
                Require(calls == 1 && observed.ProcessId == 0x1234 && !observed.IsSend, "native callback payload PID");
                c.IncludeOtherProcesses = true;
                c.ProcessId = 999999;
                monitor.OnRecord(c, nativeRecord);
                Require(calls == 1 && c.Health.MalformedEvents == 0, "unselected metadata ignored");
                c.ProcessId = 0x1234;
                record.UserDataLength = 19;
                Marshal.StructureToPtr(record, nativeRecord, false);
                monitor.OnRecord(c, nativeRecord);
                Require(calls == 1 && c.Health.MalformedEvents == 1, "native callback length guard");
                record.Header.Descriptor.Version = 3;
                record.UserDataLength = (ushort)prefix.Length;
                Marshal.StructureToPtr(record, nativeRecord, false);
                monitor.OnRecord(c, nativeRecord);
                TraceHealth diagnostics = new TraceHealth();
                CopyDiagnosticCounters(c, diagnostics);
                Require(calls == 1 && diagnostics.CandidateEvents == 4 && diagnostics.SelectedPidEvents == 2 &&
                    diagnostics.UnsupportedEvents == 1 && c.Health.MalformedEvents == 1, "candidate PID and unsupported counters");
                ProcessTrafficSnapshot allProcesses = c.ProcessTraffic.GetSnapshot(DateTime.UtcNow.AddSeconds(2));
                Require(allProcesses.Rows.Count == 1 && allProcesses.Rows[0].ProcessId == 0x1234 &&
                    allProcesses.Rows[0].ReceivedBytesTotal == 0x12345678, "unselected PID aggregate without endpoint decoding");
                record.Header.ProviderId = Guid.Empty;
                Marshal.StructureToPtr(record, nativeRecord, false);
                monitor.OnRecord(c, nativeRecord);
                Require(Interlocked.Read(ref c.CandidateEvents) == 4, "unrelated provider not counted");
                record.Header.ProviderId = TcpProvider;
                record.Header.Descriptor.Version = 2;
                record.UserDataLength = 19;
                Marshal.StructureToPtr(record, nativeRecord, false);
                c.Stopping = true;
                monitor.OnRecord(c, nativeRecord);
                Require(c.Health.MalformedEvents == 1 && Interlocked.Read(ref c.CandidateEvents) == 4, "stopped callback ignored");
                c.Stopping = false;
                c.ProcessId = 0;
                record.UserDataLength = (ushort)prefix.Length;
                Marshal.StructureToPtr(record, nativeRecord, false);
                monitor.OnRecord(c, nativeRecord);
                Require(calls == 1 && Interlocked.Read(ref c.SelectedPidEvents) == 2, "no selected game PID still aggregates without emitting game event");
            }
            finally
            {
                Marshal.FreeHGlobal(nativeRecord);
                Marshal.FreeHGlobal(metadata);
                monitor.Dispose();
            }
        }

        private static void Require(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("ETW self-test failed: " + name);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WnodeHeader
        {
            public uint BufferSize, ProviderId;
            public ulong HistoricalContext;
            public long Timestamp;
            public Guid Guid;
            public uint ClientContext, Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EventTraceProperties
        {
            public WnodeHeader Wnode;
            public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
            public int AgeLimit;
            public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
            public IntPtr LoggerThreadId;
            public uint LogFileNameOffset, LoggerNameOffset;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EventDescriptor
        {
            public ushort Id;
            public byte Version, Channel, Level, Opcode;
            public ushort Task;
            public ulong Keyword;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EventHeader
        {
            public ushort Size, HeaderType, Flags, EventProperty;
            public uint ThreadId, ProcessId;
            public long Timestamp;
            public Guid ProviderId;
            public EventDescriptor Descriptor;
            public ulong ProcessorTime;
            public Guid ActivityId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EventRecord
        {
            public EventHeader Header;
            public uint BufferContext;
            public ushort ExtendedDataCount, UserDataLength;
            public IntPtr ExtendedData, UserData, UserContext;
        }

        // x64 EVENT_TRACE_LOGFILEW. Opaque CurrentEvent/LogfileHeader occupy bytes 32..399;
        // native OpenTrace fills them. Only documented input pointer/mode fields are set.
        [StructLayout(LayoutKind.Explicit, Size = 448)]
        private struct EventTraceLogfile
        {
            [FieldOffset(0)] public IntPtr LogFileName;
            [FieldOffset(8)] public IntPtr LoggerName;
            [FieldOffset(28)] public uint ProcessTraceMode;
            [FieldOffset(400)] public IntPtr BufferCallback;
            [FieldOffset(424)] public IntPtr EventRecordCallback;
            [FieldOffset(440)] public IntPtr Context;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void EventRecordCallback(IntPtr record);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate uint BufferCallback(IntPtr logFile);

        [DllImport("advapi32.dll", EntryPoint = "StartTraceW", CharSet = CharSet.Unicode)]
        private static extern uint StartTrace(out ulong handle, string name, IntPtr properties);
        [DllImport("advapi32.dll", EntryPoint = "ControlTraceW", CharSet = CharSet.Unicode)]
        private static extern uint ControlTrace(ulong handle, string name, IntPtr properties, uint control);
        [DllImport("advapi32.dll", EntryPoint = "OpenTraceW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ulong OpenTrace(ref EventTraceLogfile logFile);
        [DllImport("advapi32.dll")]
        private static extern uint ProcessTrace([In] ulong[] handles, uint count, IntPtr startTime, IntPtr endTime);
        [DllImport("advapi32.dll")]
        private static extern uint CloseTrace(ulong handle);
    }
}
