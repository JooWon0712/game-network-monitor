using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PcInternetMonitor
{
    // EStats Path is a one-byte BOOLEAN RW option and a 160-byte DWORD ROD.
    // TCP_BOOLEAN_OPTIONAL is used by Bandwidth, not by Path.
    // https://learn.microsoft.com/windows/win32/api/tcpestats/ns-tcpestats-tcp_estats_path_rw_v0
    public sealed class TcpRttMonitor : IDisposable
    {
        private const int MaximumConnections = 32;
        private const double FreshSeconds = 3;
        private readonly object gate = new object();
        private readonly INative native;
        private Context current;
        private Thread cleanupTail;
        private bool disposed;
        private TcpRttSnapshot last = new TcpRttSnapshot();

        private sealed class Context
        {
            internal int Pid;
            internal DateTime StartedUtc;
            private readonly object signalGate = new object();
            private readonly ManualResetEvent stopEvent = new ManualResetEvent(false);
            private bool stopping, signalClosed;
            internal Thread Worker, Previous;
            internal readonly Dictionary<string, Tracked> Entries = new Dictionary<string, Tracked>();
            internal bool IsStopping { get { lock (signalGate) return stopping; } }
            internal void RequestStop() { lock (signalGate) { stopping = true; if (!signalClosed) stopEvent.Set(); } }
            internal void Wait(int milliseconds) { stopEvent.WaitOne(milliseconds); }
            internal void CloseSignal() { lock (signalGate) { if (!signalClosed) { stopEvent.Close(); signalClosed = true; } } }
        }

        private sealed class Tracked
        {
            internal NativeConnection Connection;
            internal bool Owned, Baseline;
            internal uint PreviousCount;
            internal DateTime? LastSampleUtc;
            internal double? Smoothed, Sample;
        }

        private sealed class NativeConnection
        {
            internal int Family, Pid;
            internal uint State;
            internal long Created;
            internal byte[] Row;
            internal string Key, Local, Remote;
        }

        private sealed class TableResult
        {
            internal readonly List<NativeConnection> Connections = new List<NativeConnection>();
            internal readonly HashSet<int> CompleteFamilies = new HashSet<int>();
            internal int UnidentifiedConnections;
            internal uint Error;
            internal string Stage = "";
            internal bool Partial;
        }

        private sealed class Stats
        {
            internal uint Error, Count, Smoothed, Sample;
            internal bool Enabled;
        }

        private interface INative
        {
            uint CheckProcess(int pid, DateTime startedUtc, out bool same);
            TableResult Enumerate(int pid);
            Stats Read(NativeConnection connection);
            uint Set(NativeConnection connection, bool enabled);
        }

        public TcpRttMonitor() : this(new WindowsNative()) { }
        private TcpRttMonitor(INative provider) { native = provider; }

        public void Start(int processId, DateTime processStartedUtc)
        {
            Stop();
            if (processId <= 0) return;
            if (processStartedUtc == DateTime.MinValue) throw new ArgumentException("TCP RTT 대상 프로세스 시작 시각이 필요합니다.");
            Context c;
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("TcpRttMonitor");
                c = new Context { Pid = processId, StartedUtc = processStartedUtc.ToUniversalTime(), Previous = cleanupTail };
                c.Worker = new Thread(delegate() { Run(c); }) { IsBackground = true, Name = "Game TCP RTT collector" };
                current = c; cleanupTail = c.Worker;
                last = State(c.Pid, "starting", "TCP RTT 수집 준비 중", DateTime.UtcNow);
                c.Worker.Start();
            }
        }

        public void Stop()
        {
            Context c;
            lock (gate)
            {
                c = current; current = null;
                if (c == null) return;
                c.RequestStop();
                last = State(c.Pid, "stopped", "TCP RTT 수집 중지", DateTime.UtcNow);
            }
            // A native call cannot be cancelled. A successor worker waits for all
            // previous cleanup before enabling any shared connection options.
            if (c.Worker != Thread.CurrentThread && !c.Worker.Join(200))
                DiagnosticsLog.Write("tcp_rtt_cleanup_pending", "pid=" + c.Pid + "; worker completion pending; no stale publication allowed");
        }

        public TcpRttSnapshot Snapshot(DateTime now)
        {
            TcpRttSnapshot result;
            lock (gate) result = Clone(last);
            now = now.Kind == DateTimeKind.Utc ? now : now.ToUniversalTime();
            if (result.Available)
            {
                foreach (TcpRttConnection connection in result.Connections)
                    if (!Fresh(connection.LastSampleUtc, now)) { Hide(connection, "새 RTT 표본 없음"); result.Partial = true; }
                Aggregate(result);
                if (!result.Available)
                { result.Stale = true; result.State = "stale"; result.Status = "새 RTT 표본 없음"; }
            }
            return result;
        }

        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; }
            Stop();
            Thread tail;
            lock (gate) tail = cleanupTail;
            // App shutdown gets an additional bounded cleanup wait. Native calls
            // have no cancellation guarantee; a timeout is an explicit failure
            // to confirm restoration before process exit, not a successful drain.
            if (tail != null && tail != Thread.CurrentThread && !tail.Join(2500))
                DiagnosticsLog.Write("tcp_rtt_dispose_cleanup_timeout", "worker still active after shutdown wait; EStats option restoration not confirmed before process exit");
        }

        private void Run(Context c)
        {
            try
            {
                Thread previous = c.Previous; c.Previous = null;
                while (previous != null && previous.IsAlive)
                { previous.Join(50); }
                while (!c.IsStopping)
                {
                    TcpRttSnapshot value = Poll(c, DateTime.UtcNow);
                    lock (gate) { if (Object.ReferenceEquals(current, c) && !c.IsStopping) last = value; }
                    // One family or connection can lack EStats while another one
                    // appears later. A per-query unsupported result is retryable.
                    if (value.State == "process_ended") break;
                    c.Wait(1000);
                }
            }
            catch (Exception error)
            {
                uint code = NativeCode(error);
                TcpRttSnapshot value = State(c.Pid, Unsupported(error, code) ? "unsupported" : "query_error",
                    Unsupported(error, code) ? "TCP RTT 미지원" : "TCP RTT 조회 오류", DateTime.UtcNow);
                value.NativeErrorCode = unchecked((int)code); value.ErrorStage = error.GetType().Name;
                lock (gate) { if (Object.ReferenceEquals(current, c) && !c.IsStopping) last = value; }
                DiagnosticsLog.Write("tcp_rtt_worker_failed", "pid=" + c.Pid, error);
            }
            finally { try { Cleanup(c); } finally { c.CloseSignal(); } }
        }

        private TcpRttSnapshot Poll(Context c, DateTime now)
        {
            bool same;
            uint identityError = native.CheckProcess(c.Pid, c.StartedUtc, out same);
            if (identityError != 0 || !same)
            {
                bool denied = identityError == 5 || identityError == 1314;
                TcpRttSnapshot identity = State(c.Pid, denied ? "permission_denied" : identityError == 0 ? "process_ended" : "query_error",
                    denied ? "대상 프로세스 확인 권한 부족" : identityError == 0 ? "대상 프로세스 종료 또는 변경" : "대상 프로세스 확인 오류", now);
                identity.PermissionDenied = denied; identity.NativeErrorCode = unchecked((int)identityError); identity.ErrorStage = "ProcessIdentity";
                return identity;
            }
            if (c.IsStopping) return State(c.Pid, "stopped", "TCP RTT 수집 중지", now);
            TableResult table = native.Enumerate(c.Pid);
            TcpRttSnapshot result = State(c.Pid, "waiting", "RTT 표본 대기", now);
            result.Partial = table.Partial; result.NativeErrorCode = unchecked((int)table.Error); result.ErrorStage = table.Stage;
            // All states remain in the owner table for option restoration; only
            // established connections are eligible for current RTT measurements.
            List<NativeConnection> all = table.Connections.Where(x => x.State == 5).OrderBy(x => c.Entries.ContainsKey(x.Key) ? 0 : 1).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
            result.ConnectionsFound = all.Count + table.UnidentifiedConnections; result.ConnectionsSkipped = table.UnidentifiedConnections;
            result.Partial = result.Partial || result.ConnectionsSkipped > 0;
            HashSet<string> present = new HashSet<string>(table.Connections.Select(x => x.Key));
            foreach (string missing in c.Entries.Keys.Where(x => !present.Contains(x)).ToArray())
            {
                // A failed family query is not evidence of connection closure. Keep
                // owned entries for verified stop cleanup, but never display them.
                if (table.CompleteFamilies.Contains(c.Entries[missing].Connection.Family)) c.Entries.Remove(missing);
            }
            uint firstError = table.Error;
            int unavailable = 0, stale = 0;
            foreach (NativeConnection connection in all)
            {
                if (c.IsStopping) break;
                if (result.Connections.Count >= MaximumConnections) { result.ConnectionsSkipped++; continue; }
                Tracked tracked;
                if (!c.Entries.TryGetValue(connection.Key, out tracked))
                {
                    // Retain unresolved owned entries after a family query failure,
                    // but never let those entries grow the collector beyond its cap.
                    if (c.Entries.Count >= MaximumConnections) { result.ConnectionsSkipped++; continue; }
                    tracked = new Tracked { Connection = connection }; c.Entries.Add(connection.Key, tracked);
                }
                else tracked.Connection = connection;
                Stats stats = native.Read(connection);
                if (stats.Error == 0 && !stats.Enabled && !c.IsStopping)
                {
                    uint setError = native.Set(connection, true);
                    if (setError == 0)
                    {
                        tracked.Owned = true; tracked.Baseline = false; tracked.LastSampleUtc = null; tracked.Smoothed = tracked.Sample = null;
                        stats = native.Read(connection);
                    }
                    else stats = new Stats { Error = setError };
                }
                TcpRttConnection row = Observe(tracked, stats, now);
                result.Connections.Add(row);
                if (stats.Error != 0 || !stats.Enabled)
                {
                    unavailable++;
                    if (firstError == 0) firstError = stats.Error;
                    if (stats.Error == 5 || stats.Error == 1314) result.PermissionDenied = true;
                }
                else if (!row.Available && row.LastSampleUtc.HasValue) stale++;
            }
            // A tuple-only EStats read can race connection closure/reuse. Before
            // publishing numeric values, require the same PID/creation identity
            // to still occur in a second owner-module snapshot.
            if (!c.IsStopping && result.Connections.Any(x => x.Available))
            {
                TableResult verifiedTable = native.Enumerate(c.Pid);
                result.Partial = result.Partial || verifiedTable.Partial;
                HashSet<string> verifiedKeys = new HashSet<string>(verifiedTable.Connections.Where(x => x.State == 5).Select(x => x.Key));
                foreach (TcpRttConnection row in result.Connections.Where(x => x.Available && !verifiedKeys.Contains(x.Key)))
                {
                    Hide(row, "연결 식별 정보 재확인 안 됨"); result.Partial = true;
                    Tracked invalidated;
                    if (c.Entries.TryGetValue(row.Key, out invalidated))
                    { invalidated.Baseline = false; invalidated.LastSampleUtc = null; invalidated.Smoothed = invalidated.Sample = null; }
                }
                if (verifiedTable.Error != 0 && firstError == 0) { firstError = verifiedTable.Error; result.ErrorStage = "Verify/" + verifiedTable.Stage; }
                bool stillSame; uint processError = native.CheckProcess(c.Pid, c.StartedUtc, out stillSame);
                if (processError != 0 || !stillSame)
                {
                    foreach (TcpRttConnection row in result.Connections) Hide(row, "대상 프로세스 종료 또는 확인 안 됨");
                    result.State = processError == 5 || processError == 1314 ? "permission_denied" : processError == 0 ? "process_ended" : "query_error";
                    result.Status = result.State == "process_ended" ? "대상 프로세스 종료 또는 변경" : "대상 프로세스 확인 안 됨";
                    result.PermissionDenied = processError == 5 || processError == 1314; result.NativeErrorCode = unchecked((int)processError); result.ErrorStage = "Verify/ProcessIdentity";
                    result.ConnectionsTracked = result.Connections.Count; return result;
                }
            }
            result.ConnectionsTracked = result.Connections.Count;
            result.Partial = result.Partial || unavailable > 0 || result.ConnectionsSkipped > 0;
            if (firstError != 0) { result.NativeErrorCode = unchecked((int)firstError); if (result.ErrorStage.Length == 0) result.ErrorStage = "TcpConnectionEstatsPath"; }
            Aggregate(result);
            result.Partial = result.Partial || result.ConnectionsMeasured < result.ConnectionsTracked;
            if (result.Available)
            { result.State = "available"; result.Status = result.Partial ? "TCP RTT 일부 연결 측정" : "TCP RTT 측정 중"; }
            else if (result.PermissionDenied || firstError == 5 || firstError == 1314)
            { result.PermissionDenied = true; result.State = "permission_denied"; result.Status = "TCP RTT 권한 부족"; }
            else if (firstError == 50 || firstError == 120 || firstError == 127)
            { result.State = "unsupported"; result.Status = "TCP RTT 미지원"; }
            else if (firstError != 0)
            { result.State = "query_error"; result.Status = "TCP RTT 조회 오류"; }
            else if (table.UnidentifiedConnections > 0 && all.Count == 0)
            { result.State = "query_error"; result.Status = "TCP 연결 식별 정보 확인 안 됨"; result.ErrorStage = "TcpConnectionCreationTime"; }
            else if (all.Count == 0)
            { result.State = "no_connections"; result.Status = "측정할 TCP 연결 없음"; }
            else if (stale > 0)
            { result.Stale = true; result.State = "stale"; result.Status = "새 RTT 표본 없음"; }
            return result;
        }

        private static TcpRttConnection Observe(Tracked tracked, Stats stats, DateTime now)
        {
            NativeConnection connection = tracked.Connection;
            TcpRttConnection row = new TcpRttConnection { Key = connection.Key, AddressFamily = connection.Family == 2 ? "IPv4" : "IPv6",
                Local = connection.Local, Remote = connection.Remote, CreationTimestamp = connection.Created,
                CollectionEnabled = stats.Error == 0 && stats.Enabled, CollectionOwned = tracked.Owned,
                SamplesTotal = stats.Error == 0 && stats.Enabled ? stats.Count : 0, NativeErrorCode = unchecked((int)stats.Error), Status = "RTT 표본 대기" };
            if (stats.Error != 0 || !stats.Enabled)
            {
                tracked.Baseline = false; tracked.LastSampleUtc = null; tracked.Smoothed = tracked.Sample = null;
                row.Status = stats.Error == 5 || stats.Error == 1314 ? "권한 부족" : stats.Error != 0 ? "통계 조회 오류" : "통계 수집 비활성";
                return row;
            }
            if (!tracked.Baseline || stats.Count < tracked.PreviousCount)
            {
                tracked.Baseline = true; tracked.PreviousCount = stats.Count; tracked.LastSampleUtc = null; tracked.Smoothed = tracked.Sample = null;
                row.Status = "새 RTT 표본 대기 · 기준값만 확인"; return row;
            }
            row.SamplesDelta = stats.Count - tracked.PreviousCount; tracked.PreviousCount = stats.Count;
            if (row.SamplesDelta > 0)
            {
                if (stats.Smoothed == UInt32.MaxValue || stats.Sample == UInt32.MaxValue)
                { tracked.LastSampleUtc = null; tracked.Smoothed = tracked.Sample = null; row.Status = "RTT 값 확인 안 됨"; return row; }
                tracked.LastSampleUtc = now; tracked.Smoothed = stats.Smoothed; tracked.Sample = stats.Sample;
            }
            row.LastSampleUtc = tracked.LastSampleUtc;
            if (Fresh(tracked.LastSampleUtc, now) && tracked.Smoothed.HasValue)
            { row.Available = true; row.SmoothedRttMs = tracked.Smoothed; row.SampleRttMs = tracked.Sample; row.Status = row.SamplesDelta > 0 ? "새 RTT 표본 확인" : "최근 RTT 표본 유지"; }
            else row.Status = tracked.LastSampleUtc.HasValue ? "새 RTT 표본 없음" : "RTT 표본 대기";
            return row;
        }

        private void Cleanup(Context c)
        {
            int owned = c.Entries.Values.Count(x => x.Owned), restored = 0, gone = 0, unresolved = 0;
            uint failureCode = 0; string failureStage = "";
            try
            {
                if (owned == 0) return;
                bool same; uint identity = native.CheckProcess(c.Pid, c.StartedUtc, out same);
                if (identity == 0 && !same) { gone = owned; return; }
                if (identity != 0) { unresolved = owned; failureCode = identity; failureStage = "ProcessIdentity"; return; }
                TableResult table = native.Enumerate(c.Pid);
                Dictionary<string, NativeConnection> currentRows = table.Connections.GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.First());
                foreach (Tracked tracked in c.Entries.Values.Where(x => x.Owned))
                {
                    NativeConnection verified;
                    if (!currentRows.TryGetValue(tracked.Connection.Key, out verified))
                    {
                        if (table.CompleteFamilies.Contains(tracked.Connection.Family)) gone++;
                        else { unresolved++; if (failureStage.Length == 0) { failureCode = table.Error; failureStage = "OwnerIdentityUnconfirmed/" + table.Stage; } }
                        continue;
                    }
                    Stats before = native.Read(verified);
                    if (before.Error != 0)
                    { if (before.Error == 1168) gone++; else { unresolved++; if (failureStage.Length == 0) { failureCode = before.Error; failureStage = "ReadPath"; } } continue; }
                    if (!before.Enabled) { restored++; continue; }
                    uint result = native.Set(verified, false);
                    if (result == 0) restored++; else if (result == 1168) gone++; else { unresolved++; if (failureStage.Length == 0) { failureCode = result; failureStage = "RestorePath"; } }
                }
            }
            catch (Exception error)
            { unresolved = Math.Max(unresolved, owned - restored - gone); DiagnosticsLog.Write("tcp_rtt_cleanup_failed", "pid=" + c.Pid, error); }
            finally
            {
                if (owned > 0) DiagnosticsLog.Write("tcp_rtt_cleanup", "pid=" + c.Pid + "; owned=" + owned + "; restored=" + restored + "; ended=" + gone + "; unconfirmed=" + unresolved + "; native_error=" + failureCode + "; stage=" + failureStage);
                c.Entries.Clear();
            }
        }

        private static bool Fresh(DateTime? at, DateTime now) { return at.HasValue && (now - at.Value).TotalSeconds >= 0 && (now - at.Value).TotalSeconds < FreshSeconds; }
        private static void Hide(TcpRttConnection row, string status) { row.Available = false; row.SmoothedRttMs = row.SampleRttMs = null; row.Status = status; }
        private static void Aggregate(TcpRttSnapshot snapshot)
        {
            double[] values = snapshot.Connections.Where(x => x.Available && x.SmoothedRttMs.HasValue).Select(x => x.SmoothedRttMs.Value).ToArray();
            snapshot.ConnectionsMeasured = values.Length; snapshot.Available = values.Length > 0;
            snapshot.MinimumMs = values.Length == 0 ? (double?)null : values.Min(); snapshot.MaximumMs = values.Length == 0 ? (double?)null : values.Max();
        }
        private static TcpRttSnapshot State(int pid, string code, string status, DateTime now) { return new TcpRttSnapshot { ProcessId = pid, Requested = pid > 0, TimestampUtc = now, State = code, Status = status }; }
        private static uint NativeCode(Exception error) { Win32Exception win = error as Win32Exception; return win == null ? 1u : unchecked((uint)win.NativeErrorCode); }
        private static bool Unsupported(Exception error, uint code) { return error is DllNotFoundException || error is EntryPointNotFoundException || error is PlatformNotSupportedException || code == 50 || code == 120 || code == 127; }
        private static TcpRttSnapshot Clone(TcpRttSnapshot source)
        {
            TcpRttSnapshot value = new TcpRttSnapshot { TimestampUtc = source.TimestampUtc, ProcessId = source.ProcessId, Requested = source.Requested, Available = source.Available,
                PermissionDenied = source.PermissionDenied, Stale = source.Stale, Partial = source.Partial, State = source.State, Status = source.Status,
                ErrorStage = source.ErrorStage, NativeErrorCode = source.NativeErrorCode, ConnectionsFound = source.ConnectionsFound, ConnectionsTracked = source.ConnectionsTracked,
                ConnectionsMeasured = source.ConnectionsMeasured, ConnectionsSkipped = source.ConnectionsSkipped, MinimumMs = source.MinimumMs, MaximumMs = source.MaximumMs };
            foreach (TcpRttConnection row in source.Connections)
                value.Connections.Add(new TcpRttConnection { LastSampleUtc = row.LastSampleUtc, Key = row.Key, AddressFamily = row.AddressFamily, Local = row.Local, Remote = row.Remote,
                    Status = row.Status, CreationTimestamp = row.CreationTimestamp, CollectionEnabled = row.CollectionEnabled, CollectionOwned = row.CollectionOwned,
                    Available = row.Available, SmoothedRttMs = row.SmoothedRttMs, SampleRttMs = row.SampleRttMs, SamplesTotal = row.SamplesTotal, SamplesDelta = row.SamplesDelta, NativeErrorCode = row.NativeErrorCode });
            return value;
        }

        private sealed class WindowsNative : INative
        {
            private const uint MaximumTableBytes = 8 * 1024 * 1024;
            private readonly Predicate<NativeConnection> permitted;
            internal WindowsNative() { }
            internal WindowsNative(Predicate<NativeConnection> filter) { permitted = filter; }
            public uint CheckProcess(int pid, DateTime startedUtc, out bool same)
            {
                same = false;
                try { using (Process process = Process.GetProcessById(pid)) same = !process.HasExited && process.StartTime.ToUniversalTime() == startedUtc; return 0; }
                catch (ArgumentException) { return 0; }
                catch (InvalidOperationException) { return 0; }
                catch (Win32Exception error) { return unchecked((uint)error.NativeErrorCode); }
            }
            public TableResult Enumerate(int pid)
            {
                ValidateLayouts(); TableResult result = new TableResult();
                foreach (int family in new int[] { 2, 23 })
                {
                    uint size = 0; uint code = GetExtendedTcpTable(IntPtr.Zero, ref size, false, (uint)family, 7, 0);
                    if (code != 122 && code != 0) { FamilyFailure(result, family, code); continue; }
                    bool read = false;
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        if (size < 8) size = 8;
                        if (size > MaximumTableBytes) { FamilyFailure(result, family, 8); break; }
                        IntPtr table = Marshal.AllocHGlobal((int)size);
                        try
                        {
                            uint allocated = size; code = GetExtendedTcpTable(table, ref size, false, (uint)family, 7, 0);
                            if (code == 122) continue;
                            if (code != 0) { FamilyFailure(result, family, code); break; }
                            if (size < 8 || size > allocated) throw new InvalidOperationException("TCP owner table returned an invalid buffer size.");
                            byte[] bytes = new byte[size]; Marshal.Copy(table, bytes, 0, bytes.Length);
                            List<NativeConnection> parsed = new List<NativeConnection>();
                            int unidentified = ParseModuleTable(bytes, family, pid, parsed);
                            foreach (NativeConnection connection in parsed) if (permitted == null || permitted(connection)) result.Connections.Add(connection);
                            // Missing creation identities prevent safe restoration in
                            // this family, even when the table query itself succeeded.
                            result.UnidentifiedConnections += unidentified;
                            if (unidentified == 0) result.CompleteFamilies.Add(family); else result.Partial = true;
                            read = true; break;
                        }
                        finally { Marshal.FreeHGlobal(table); }
                    }
                    if (!read && code == 122) FamilyFailure(result, family, code);
                }
                return result;
            }
            private static void FamilyFailure(TableResult result, int family, uint error)
            { result.Partial = true; if (result.Error == 0) { result.Error = error; result.Stage = "GetExtendedTcpTable/IPv" + (family == 2 ? "4" : "6"); } }
            public Stats Read(NativeConnection connection)
            {
                IntPtr row = IntPtr.Zero, rw = IntPtr.Zero, rod = IntPtr.Zero;
                try
                {
                    row = Marshal.AllocHGlobal(connection.Row.Length); rw = Marshal.AllocHGlobal(1); rod = Marshal.AllocHGlobal(160);
                    Marshal.Copy(connection.Row, 0, row, connection.Row.Length); Marshal.WriteByte(rw, 0); Marshal.Copy(new byte[160], 0, rod, 160);
                    uint code = connection.Family == 2 ? GetPerTcpConnectionEStats(row, 3, rw, 0, 1, IntPtr.Zero, 0, 0, rod, 0, 160) :
                        GetPerTcp6ConnectionEStats(row, 3, rw, 0, 1, IntPtr.Zero, 0, 0, rod, 0, 160);
                    Stats value = new Stats { Error = code, Enabled = code == 0 && Marshal.ReadByte(rw) == 1 };
                    // When collection is disabled, Windows does not define ROD contents.
                    if (value.Enabled) { value.Sample = unchecked((uint)Marshal.ReadInt32(rod, 26 * 4)); value.Smoothed = unchecked((uint)Marshal.ReadInt32(rod, 27 * 4)); value.Count = unchecked((uint)Marshal.ReadInt32(rod, 32 * 4)); }
                    return value;
                }
                finally { if (rod != IntPtr.Zero) Marshal.FreeHGlobal(rod); if (rw != IntPtr.Zero) Marshal.FreeHGlobal(rw); if (row != IntPtr.Zero) Marshal.FreeHGlobal(row); }
            }
            public uint Set(NativeConnection connection, bool enabled)
            {
                // EStats setters identify only the tuple. Recheck PID and creation
                // identity immediately before mutation; never restore a reused tuple.
                TableResult table = Enumerate(connection.Pid);
                NativeConnection verified = table.Connections.FirstOrDefault(x => x.Key == connection.Key);
                if (verified == null) return table.Error != 0 ? table.Error : 1168;
                IntPtr row = IntPtr.Zero, rw = IntPtr.Zero;
                try
                {
                    row = Marshal.AllocHGlobal(verified.Row.Length); rw = Marshal.AllocHGlobal(1);
                    Marshal.Copy(verified.Row, 0, row, verified.Row.Length); Marshal.WriteByte(rw, enabled ? (byte)1 : (byte)0);
                    return verified.Family == 2 ? SetPerTcpConnectionEStats(row, 3, rw, 0, 1, 0) : SetPerTcp6ConnectionEStats(row, 3, rw, 0, 1, 0);
                }
                finally { if (rw != IntPtr.Zero) Marshal.FreeHGlobal(rw); if (row != IntPtr.Zero) Marshal.FreeHGlobal(row); }
            }
        }

        // OWNER_MODULE rows include creation time; PID/4-tuple alone can be reused.
        private static int ParseModuleTable(byte[] table, int family, int pid, List<NativeConnection> output)
        {
            int unidentified = 0;
            int offset = family == 2 ? Marshal.OffsetOf(typeof(Table4Probe), "First").ToInt32() : Marshal.OffsetOf(typeof(Table6Probe), "First").ToInt32();
            int rowSize = family == 2 ? 160 : 192;
            if (table == null || table.Length < offset) throw new InvalidOperationException("TCP owner table header is truncated.");
            uint count = Read32(table, 0);
            if ((long)offset + (long)count * rowSize > table.Length) throw new InvalidOperationException("TCP owner table rows exceed the returned buffer.");
            for (uint i = 0; i < count; i++)
            {
                int at = checked(offset + (int)i * rowSize), pidOffset = family == 2 ? 20 : 52, stateOffset = family == 2 ? 0 : 48;
                if (Read32(table, at + pidOffset) != unchecked((uint)pid)) continue;
                uint state = Read32(table, at + stateOffset);
                long created = BitConverter.ToInt64(table, at + (family == 2 ? 24 : 56));
                // A zero creation timestamp cannot safely distinguish a reused tuple.
                if (created <= 0) { unidentified++; continue; }
                byte[] row = new byte[family == 2 ? 20 : 52];
                if (family == 2) Buffer.BlockCopy(table, at, row, 0, 20);
                else { Buffer.BlockCopy(table, at + 48, row, 0, 4); Buffer.BlockCopy(table, at, row, 4, 48); }
                string local, remote;
                if (family == 2)
                { local = new IPAddress(Slice(table, at + 4, 4)) + ":" + Port(table, at + 8); remote = new IPAddress(Slice(table, at + 12, 4)) + ":" + Port(table, at + 16); }
                else
                { local = "[" + new IPAddress(Slice(table, at, 16), Read32(table, at + 16)) + "]:" + Port(table, at + 20); remote = "[" + new IPAddress(Slice(table, at + 24, 16), Read32(table, at + 40)) + "]:" + Port(table, at + 44); }
                output.Add(new NativeConnection { Family = family, Pid = pid, State = state, Created = created, Row = row, Local = local, Remote = remote,
                    Key = family + "|" + pid + "|" + created.ToString(CultureInfo.InvariantCulture) + "|" + local + "|" + remote });
            }
            return unidentified;
        }
        private static uint Read32(byte[] value, int at) { return BitConverter.ToUInt32(value, at); }
        private static byte[] Slice(byte[] value, int at, int length) { byte[] part = new byte[length]; Buffer.BlockCopy(value, at, part, 0, length); return part; }
        private static int Port(byte[] value, int at) { return (value[at] << 8) | value[at + 1]; }
        private static void ValidateLayouts()
        {
            if (Marshal.SizeOf(typeof(Owner4)) != 160 || Marshal.SizeOf(typeof(Owner6)) != 192 || Marshal.OffsetOf(typeof(Owner4), "Created").ToInt32() != 24 ||
                Marshal.OffsetOf(typeof(Owner6), "State").ToInt32() != 48 || Marshal.OffsetOf(typeof(Owner6), "Created").ToInt32() != 56 ||
                Marshal.OffsetOf(typeof(Table4Probe), "First").ToInt32() != 8 || Marshal.OffsetOf(typeof(Table6Probe), "First").ToInt32() != 8)
                throw new PlatformNotSupportedException("TCP owner-module native layout does not match Windows x64.");
        }
        [StructLayout(LayoutKind.Sequential)] private struct Owner4 { public uint State, Local, LocalPort, Remote, RemotePort, Pid; public long Created; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public ulong[] Modules; }
        [StructLayout(LayoutKind.Sequential)] private struct Owner6 { [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Local; public uint LocalScope, LocalPort; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Remote; public uint RemoteScope, RemotePort, State, Pid; public long Created; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public ulong[] Modules; }
        [StructLayout(LayoutKind.Sequential)] private struct Table4Probe { public uint Count; public Owner4 First; }
        [StructLayout(LayoutKind.Sequential)] private struct Table6Probe { public uint Count; public Owner6 First; }
        [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int tableClass, uint reserved);
        [DllImport("iphlpapi.dll")] private static extern uint GetPerTcpConnectionEStats(IntPtr row, int type, IntPtr rw, uint rwVersion, uint rwSize, IntPtr ros, uint rosVersion, uint rosSize, IntPtr rod, uint rodVersion, uint rodSize);
        [DllImport("iphlpapi.dll")] private static extern uint GetPerTcp6ConnectionEStats(IntPtr row, int type, IntPtr rw, uint rwVersion, uint rwSize, IntPtr ros, uint rosVersion, uint rosSize, IntPtr rod, uint rodVersion, uint rodSize);
        [DllImport("iphlpapi.dll")] private static extern uint SetPerTcpConnectionEStats(IntPtr row, int type, IntPtr rw, uint version, uint size, uint offset);
        [DllImport("iphlpapi.dll")] private static extern uint SetPerTcp6ConnectionEStats(IntPtr row, int type, IntPtr rw, uint version, uint size, uint offset);

        public static void SelfTest()
        {
            ValidateLayouts(); DateTime now = DateTime.UtcNow;
            Tracked tracked = new Tracked { Connection = FixtureConnection(2, 42, 1) };
            TcpRttConnection row = Observe(tracked, new Stats { Enabled = true, Count = 10, Smoothed = 20, Sample = 21 }, now);
            Check(!row.Available, "old counter baseline became a fresh RTT");
            row = Observe(tracked, new Stats { Enabled = true, Count = 11, Smoothed = 0, Sample = 0 }, now.AddSeconds(1));
            Check(row.Available && row.SmoothedRttMs == 0 && row.SamplesDelta == 1, "fresh integer zero-ms RTT was lost");
            row = Observe(tracked, new Stats { Enabled = true, Count = 11, Smoothed = 99, Sample = 99 }, now.AddSeconds(2));
            Check(row.Available && row.SmoothedRttMs == 0 && row.SamplesDelta == 0, "unchanged counter invented new RTT");
            row = Observe(tracked, new Stats { Enabled = true, Count = 12, Smoothed = UInt32.MaxValue }, now.AddSeconds(2));
            Check(!row.Available && !row.SmoothedRttMs.HasValue, "invalid new RTT reused the previous numeric value");
            row = Observe(tracked, new Stats { Enabled = true, Count = 11 }, now.AddSeconds(4));
            Check(!row.Available && !row.SmoothedRttMs.HasValue, "stale connection retained an available RTT");
            row = Observe(tracked, new Stats { Enabled = true, Count = 1, Smoothed = 2 }, now.AddSeconds(5));
            Check(!row.Available && row.SamplesDelta == 0, "counter reset/wrap was treated as fresh samples");
            row = Observe(tracked, new Stats { Enabled = false, Count = 1000, Smoothed = 1 }, now.AddSeconds(6));
            Check(!row.Available && row.SamplesTotal == 0, "undefined disabled ROD was consumed");
            row = Observe(tracked, new Stats { Error = 5, Enabled = true, Count = 1001, Smoothed = 1 }, now.AddSeconds(7));
            Check(!row.Available && !row.CollectionEnabled, "failed native query enabled metrics");
            ParseSelfTest(); PollSelfTest(now); StateTransitionSelfTest(now); LimitSelfTest(now); LifecycleSelfTest();
        }

        private static void ParseSelfTest()
        {
            foreach (int family in new int[] { 2, 23 })
            {
                byte[] table = new byte[8 + (family == 2 ? 160 : 192)]; Buffer.BlockCopy(BitConverter.GetBytes(1u), 0, table, 0, 4);
                int at = 8; Buffer.BlockCopy(BitConverter.GetBytes(5u), 0, table, at + (family == 2 ? 0 : 48), 4); Buffer.BlockCopy(BitConverter.GetBytes(42u), 0, table, at + (family == 2 ? 20 : 52), 4);
                Buffer.BlockCopy(BitConverter.GetBytes(123456789L), 0, table, at + (family == 2 ? 24 : 56), 8);
                if (family == 2) { table[at + 4] = 127; table[at + 7] = 1; table[at + 12] = 203; table[at + 15] = 7; table[at + 8] = 0x12; table[at + 9] = 0x34; table[at + 16] = 0x01; table[at + 17] = 0xBB; }
                else { table[at + 15] = 1; table[at + 39] = 1; table[at + 20] = 0x12; table[at + 21] = 0x34; table[at + 44] = 0x01; table[at + 45] = 0xBB; }
                List<NativeConnection> output = new List<NativeConnection>(); ParseModuleTable(table, family, 42, output);
                Check(output.Count == 1 && output[0].Local.EndsWith(":4660") && output[0].Remote.EndsWith(":443") && output[0].Created == 123456789 && Read32(output[0].Row, 0) == 5,
                    "owner-module parse/port/native EStats row layout failed");
                long oldKey = output[0].Created; Buffer.BlockCopy(BitConverter.GetBytes(oldKey + 1), 0, table, at + (family == 2 ? 24 : 56), 8);
                List<NativeConnection> reused = new List<NativeConnection>(); ParseModuleTable(table, family, 42, reused); Check(output[0].Key != reused[0].Key, "reused tuple retained connection identity");
                Buffer.BlockCopy(BitConverter.GetBytes(8u), 0, table, at + (family == 2 ? 0 : 48), 4);
                List<NativeConnection> closing = new List<NativeConnection>(); ParseModuleTable(table, family, 42, closing);
                Check(closing.Count == 1 && closing[0].State == 8 && closing[0].Key == reused[0].Key && Read32(closing[0].Row, 0) == 8, "CLOSE_WAIT ownership/native row was dropped or changed identity");
            }
        }

        private sealed class FakeNative : INative
        {
            internal bool Same = true, Enabled, PreEnabled;
            internal uint Count, Error;
            internal uint LastSetState;
            internal int Enables, Disables;
            internal readonly List<NativeConnection> Rows = new List<NativeConnection>();
            internal ManualResetEvent Entered, Release;
            public uint CheckProcess(int pid, DateTime start, out bool same) { same = Same; return 0; }
            public TableResult Enumerate(int pid)
            {
                if (Entered != null) { Entered.Set(); if (!Release.WaitOne(3000)) throw new InvalidOperationException("Synthetic RTT enumeration did not release."); }
                TableResult result = new TableResult(); result.Connections.AddRange(Rows); result.CompleteFamilies.Add(2); result.CompleteFamilies.Add(23); return result;
            }
            public Stats Read(NativeConnection c) { return new Stats { Error = Error, Enabled = PreEnabled || Enabled, Count = Count, Smoothed = 12, Sample = 13 }; }
            public uint Set(NativeConnection c, bool enabled) { if (Error != 0) return Error; if (enabled) Enables++; else Disables++; Enabled = enabled; LastSetState = c.State; return 0; }
        }
        private static NativeConnection FixtureConnection(int family, int pid, long created) { return new NativeConnection { Family = family, Pid = pid, State = 5, Created = created, Key = family + "|" + pid + "|" + created, Row = new byte[family == 2 ? 20 : 52], Local = "local", Remote = "remote" }; }
        private static void PollSelfTest(DateTime now)
        {
            FakeNative fake = new FakeNative(); fake.Rows.Add(FixtureConnection(2, 42, 1));
            using (TcpRttMonitor monitor = new TcpRttMonitor(fake))
            {
                Context c = new Context { Pid = 42, StartedUtc = now };
                Check(!monitor.Poll(c, now).Available && fake.Enables == 1, "enable or first baseline failed");
                fake.Count = 1; TcpRttSnapshot sample = monitor.Poll(c, now.AddSeconds(1));
                Check(sample.Available && sample.MinimumMs == 12 && sample.MaximumMs == 12 && sample.ConnectionsMeasured == 1, "fresh selected TCP RTT was not aggregated");
                monitor.last = sample; TcpRttSnapshot copy = monitor.Snapshot(now.AddSeconds(1)); copy.Connections.Clear(); Check(monitor.last.Connections.Count == 1, "snapshot leaked mutable cache");
                Check(!monitor.Snapshot(now.AddSeconds(4)).Available, "cached snapshot freshness was not checked");
                monitor.Cleanup(c); Check(fake.Disables == 1, "owned option was not restored");
                fake.PreEnabled = true; Context shared = new Context { Pid = 42, StartedUtc = now }; monitor.Poll(shared, now); monitor.Cleanup(shared); Check(fake.Disables == 1, "pre-enabled shared statistics were disabled");
                fake.PreEnabled = false; fake.Error = 5; Context denied = new Context { Pid = 42, StartedUtc = now }; sample = monitor.Poll(denied, now); Check(sample.PermissionDenied && !sample.Available, "permission failure fabricated a result");
                fake.Error = 0; fake.Same = false; Check(monitor.Poll(denied, now).State == "process_ended", "PID reuse was accepted");
                c.CloseSignal(); shared.CloseSignal(); denied.CloseSignal();
            }
        }
        private static void LimitSelfTest(DateTime now)
        {
            FakeNative fake = new FakeNative();
            for (int i = 0; i < MaximumConnections + 1; i++) fake.Rows.Add(FixtureConnection(i % 2 == 0 ? 2 : 23, 42, i + 1));
            using (TcpRttMonitor monitor = new TcpRttMonitor(fake))
            {
                Context c = new Context { Pid = 42, StartedUtc = now };
                try
                {
                    monitor.Poll(c, now); fake.Count = 1; TcpRttSnapshot value = monitor.Poll(c, now.AddSeconds(1));
                    Check(value.ConnectionsFound == 33 && value.ConnectionsTracked == 32 && value.ConnectionsMeasured == 32 && value.ConnectionsSkipped == 1 && value.Partial && c.Entries.Count == 32, "connection cap/partial status was not enforced");
                    fake.Rows.Clear(); value = monitor.Poll(c, now.AddSeconds(2));
                    Check(!value.Available && value.State == "no_connections" && c.Entries.Count == 0, "closed connections retained an RTT");
                }
                finally { monitor.Cleanup(c); c.CloseSignal(); }
            }
        }
        private static void StateTransitionSelfTest(DateTime now)
        {
            foreach (int family in new int[] { 2, 23 })
                foreach (uint closingState in new uint[] { 6, 7, 8 })
                {
                    FakeNative fake = new FakeNative(); fake.Rows.Add(FixtureConnection(family, 42, 1));
                    using (TcpRttMonitor monitor = new TcpRttMonitor(fake))
                    {
                        Context c = new Context { Pid = 42, StartedUtc = now };
                        try
                        {
                            monitor.Poll(c, now); fake.Count = 1; Check(monitor.Poll(c, now.AddSeconds(1)).Available, "state-transition fixture did not measure RTT");
                            NativeConnection closing = FixtureConnection(family, 42, 1); closing.State = closingState; fake.Rows[0] = closing;
                            TcpRttSnapshot value = monitor.Poll(c, now.AddSeconds(2));
                            Check(!value.Available && value.State == "no_connections" && c.Entries.Count == 1 && c.Entries.Values.First().Owned, "closing-state connection lost its owned option or remained measurable");
                            monitor.Cleanup(c);
                            Check(fake.Disables == 1 && fake.LastSetState == closingState, "closing-state owned option was not restored using its current row");
                        }
                        finally { monitor.Cleanup(c); c.CloseSignal(); }
                    }
                }
        }
        private static void LifecycleSelfTest()
        {
            FakeNative fake = new FakeNative { Entered = new ManualResetEvent(false), Release = new ManualResetEvent(false) }; fake.Rows.Add(FixtureConnection(2, 42, 1));
            TcpRttMonitor monitor = new TcpRttMonitor(fake);
            try
            {
                monitor.Start(42, DateTime.UtcNow); Check(fake.Entered.WaitOne(2000), "synthetic RTT worker did not start");
                monitor.Stop(); fake.Release.Set(); Thread tail = monitor.cleanupTail; Check(tail.Join(3000), "synthetic worker did not stop");
                Check(monitor.Snapshot(DateTime.UtcNow).State == "stopped" && fake.Enables == 0, "late callback enabled or published after Stop");
            }
            finally { fake.Release.Set(); monitor.Dispose(); if (monitor.cleanupTail != null) monitor.cleanupTail.Join(3000); fake.Entered.Close(); fake.Release.Close(); }
        }
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("TCP RTT test: " + message); }

        // Explicit test mode only; current process and loopback sockets exclusively.
        // This method is never called by the normal collector or SelfTest.
        public static void LiveLoopbackTest(string reportPath)
        {
            StringBuilder report = new StringBuilder(); report.AppendLine("TCP RTT live loopback test; no game, remote host, ETW, ICMP or UAC.");
            foreach (IPAddress address in new IPAddress[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
            {
                TcpListener listener = null; TcpClient client = null, server = null; TcpRttMonitor monitor = null;
                try
                {
                    listener = new TcpListener(address, 0); listener.Start();
                    client = new TcpClient(address.AddressFamily); client.Connect(address, ((IPEndPoint)listener.LocalEndpoint).Port); server = listener.AcceptTcpClient();
                    client.NoDelay = server.NoDelay = true; client.ReceiveTimeout = server.ReceiveTimeout = client.SendTimeout = server.SendTimeout = 1500;
                    int pid; DateTime started; using (Process process = Process.GetCurrentProcess()) { pid = process.Id; started = process.StartTime.ToUniversalTime(); }
                    string clientLocal = client.Client.LocalEndPoint.ToString(), clientRemote = client.Client.RemoteEndPoint.ToString();
                    string serverLocal = server.Client.LocalEndPoint.ToString(), serverRemote = server.Client.RemoteEndPoint.ToString();
                    int family = address.AddressFamily == AddressFamily.InterNetwork ? 2 : 23;
                    Predicate<NativeConnection> ownSockets = delegate(NativeConnection connection)
                    {
                        return connection.Family == family && ((connection.Local == clientLocal && connection.Remote == clientRemote) ||
                            (connection.Local == serverLocal && connection.Remote == serverRemote));
                    };
                    WindowsNative provider = new WindowsNative(ownSockets);
                    bool baselineComplete; string baselineIssue;
                    Dictionary<string, bool> originalOptions = LoopbackOptions(provider, pid, family, address, "BASELINE", report, out baselineComplete, out baselineIssue);
                    HashSet<string> observedOwned = new HashSet<string>();
                    monitor = new TcpRttMonitor(provider); monitor.Start(pid, started); TcpRttSnapshot final = null; bool measuredSuccess = false;
                    for (int i = 0; i < 40; i++)
                    {
                        byte[] value = BitConverter.GetBytes(i); client.GetStream().Write(value, 0, value.Length); byte[] received = new byte[4]; ReadExactly(server.GetStream(), received); server.GetStream().Write(received, 0, received.Length); ReadExactly(client.GetStream(), received);
                        Thread.Sleep(150); final = monitor.Snapshot(DateTime.UtcNow);
                        foreach (TcpRttConnection row in final.Connections.Where(x => x.CollectionOwned && x.CollectionEnabled)) observedOwned.Add(row.Key);
                        TcpRttConnection measured = final.Connections.FirstOrDefault(x => x.Available &&
                            ((x.Local == clientLocal && x.Remote == clientRemote) || (x.Local == serverLocal && x.Remote == serverRemote)));
                        if (measured != null) { measuredSuccess = true; report.AppendLine(address + " PASS: fresh TCP RTT=" + measured.SmoothedRttMs + " ms; samples=" + measured.SamplesTotal + "; last=" + measured.LastSampleUtc.Value.ToString("o")); break; }
                        if (final.State == "permission_denied" || final.State == "unsupported") break;
                    }
                    if (!measuredSuccess)
                        report.AppendLine(address + " NOT VALIDATED: " + (final == null ? "no snapshot" : final.State + " / " + final.Status + " / native=" + final.NativeErrorCode));
                    // Dispose while both sockets are still open, so that restoration
                    // can be checked against exactly the original creation identities.
                    monitor.Dispose();
                    Thread cleanup = monitor.cleanupTail; bool cleanupComplete = cleanup == null || !cleanup.IsAlive;
                    monitor = null;
                    bool restoredComplete; string restoredIssue;
                    Dictionary<string, bool> restoredOptions = LoopbackOptions(provider, pid, family, address, "AFTER DISPOSE", report, out restoredComplete, out restoredIssue);
                    bool sameOptions = baselineComplete && restoredComplete && originalOptions.Count == 2 && restoredOptions.Count == 2 &&
                        originalOptions.All(x => restoredOptions.ContainsKey(x.Key) && restoredOptions[x.Key] == x.Value);
                    bool mutationObserved = originalOptions.Where(x => !x.Value).All(x => observedOwned.Contains(x.Key));
                    if (sameOptions && cleanupComplete && mutationObserved)
                        report.AppendLine(address + " RESTORATION PASS: Path EnableCollection matches the pre-Start baseline on both original socket identities; observed_owned=" + observedOwned.Count);
                    else
                        report.AppendLine(address + " RESTORATION NOT VALIDATED: baseline_complete=" + baselineComplete + "; after_complete=" + restoredComplete +
                            "; cleanup_complete=" + cleanupComplete + "; same_options=" + sameOptions + "; mutation_observed=" + mutationObserved +
                            "; baseline_issue=" + baselineIssue + "; after_issue=" + restoredIssue);
                }
                catch (Exception error) { report.AppendLine(address + " NOT VALIDATED: " + error.GetType().Name + " / " + error.Message); }
                finally { if (monitor != null) monitor.Dispose(); if (client != null) client.Close(); if (server != null) server.Close(); if (listener != null) listener.Stop(); }
            }
            string directory = Path.GetDirectoryName(Path.GetFullPath(reportPath)); Directory.CreateDirectory(directory); File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(true));
        }
        private static Dictionary<string, bool> LoopbackOptions(WindowsNative provider, int pid, int family, IPAddress address, string phase, StringBuilder report, out bool complete, out string issue)
        {
            TableResult table = provider.Enumerate(pid);
            Dictionary<string, bool> options = new Dictionary<string, bool>();
            complete = table.CompleteFamilies.Contains(family) && table.Connections.Count == 2;
            issue = complete ? "" : "original socket table incomplete; rows=" + table.Connections.Count + "; native=" + table.Error + "; stage=" + table.Stage;
            foreach (NativeConnection connection in table.Connections)
            {
                Stats value = provider.Read(connection);
                report.AppendLine(address + " " + phase + ": identity=" + connection.Key + "; EnableCollection=" + (value.Error == 0 ? value.Enabled.ToString() : "unknown") + "; native=" + value.Error);
                if (value.Error == 0) options.Add(connection.Key, value.Enabled);
                else { complete = false; issue += "; Path query native=" + value.Error; }
            }
            if (options.Count != 2) complete = false;
            return options;
        }
        private static void ReadExactly(NetworkStream stream, byte[] buffer) { int at = 0; while (at < buffer.Length) { int count = stream.Read(buffer, at, buffer.Length - at); if (count <= 0) throw new IOException("Loopback TCP connection closed."); at += count; } }
    }
}
