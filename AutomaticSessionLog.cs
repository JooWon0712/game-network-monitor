using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace PcInternetMonitor
{
    // Only writes rows supplied by an active window. No timer, service, or network access.
    public sealed class AutomaticSessionLog : IDisposable
    {
        private sealed class PendingRow { public int Kind; public string Text; public int Count; }
        private const int MaximumQueuedRows = 4096;
        private const int MaximumQueuedCharacters = 16 * 1024 * 1024;
        private const string ProbeHeader = "timestamp_utc,timestamp_local,kind,target,status,rtt_ms,countable,timed_out";
        internal const string TcpRttHeader = "snapshot_utc,rtt_snapshot_utc,pid,process,requested,state,status,available,permission_denied,stale,partial,connections_found,connections_tracked,connections_measured,connections_skipped,minimum_smoothed_rtt_ms,maximum_smoothed_rtt_ms,native_error_code,error_stage,connection_key,address_family,local,remote,connection_created_timestamp,connection_available,collection_enabled,collection_owned,last_sample_utc,smoothed_rtt_ms,sample_rtt_ms,rtt_samples_total,rtt_samples_delta,connection_error_code,connection_status,actual_game_ping_support,game_packet_loss_support";
        internal const string InternetCheckHeader = "started_utc,completed_utc,completed_local,kind,target,state,status,success,timed_out,cancelled,duration_ms,http_status,error";
        internal const string ConnectivityIncidentHeader = "id,version,kind,target,category,scope,state,first_failed_check_utc,last_observed_utc,recovered_utc,observation_closed_utc,duration_seconds,failed_checks,summary,end_reason";
        private readonly object gate = new object();
        private readonly Queue<PendingRow> pending = new Queue<PendingRow>();
        // Retain only the previous observed endpoint set for change detection.
        // TrafficWindow supplies at most 512 endpoints; this is not a packet capture.
        private Dictionary<string, string> previousEndpoints = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> lastInternetChecks = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> lastIncidentVersions = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Queue<string> incidentIdentityOrder = new Queue<string>();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Thread worker;
        private readonly DateTime startedUtc = DateTime.UtcNow;
        private StreamWriter samples, marks, probes, processes, endpoints, tcpRtt, internetChecks, connectivityIncidents, metadata;
        private bool healthy = true, completing, disposed;
        private string errorMessage = "", completionReason = "측정 종료";
        private long acceptedSamples, writtenSamples, writtenMarks, writtenProbes, writtenProcessRows, writtenEndpointRows, writtenTcpRttRows;
        private long writtenInternetChecks, writtenConnectivityIncidents;
        private DateTime lastProcessTimestamp, lastTcpRttTimestamp;
        private long queuedCharacters;

        public string SessionDirectory { get; private set; }
        public string SamplesPath { get; private set; }
        public bool IsHealthy { get { lock (gate) return healthy; } }
        public string ErrorMessage { get { lock (gate) return errorMessage; } }
        public long SampleCount { get { lock (gate) return acceptedSamples; } }

        public AutomaticSessionLog(string rootFolder)
        {
            if (String.IsNullOrWhiteSpace(rootFolder)) throw new ArgumentException("자동 기록 폴더가 비어 있습니다.", "rootFolder");
            string root = Path.GetFullPath(rootFolder);
            Directory.CreateDirectory(root);
            for (int attempt = 0; attempt < 10; attempt++)
            {
                string candidate = Path.Combine(root, startedUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
                if (Directory.Exists(candidate) || File.Exists(candidate)) continue;
                Directory.CreateDirectory(candidate);
                SessionDirectory = candidate;
                break;
            }
            if (SessionDirectory == null) throw new IOException("고유한 자동 기록 폴더를 만들지 못했습니다.");
            SamplesPath = Path.Combine(SessionDirectory, "samples.csv");
            try
            {
                // CreateNew also prevents accidental overwrite if another process creates a file.
                samples = CreateWriter(SamplesPath);
                marks = CreateWriter(Path.Combine(SessionDirectory, "marks.csv"));
                probes = CreateWriter(Path.Combine(SessionDirectory, "probes.csv"));
                processes = CreateWriter(Path.Combine(SessionDirectory, "process-traffic.csv"));
                endpoints = CreateWriter(Path.Combine(SessionDirectory, "game-endpoints.csv"));
                tcpRtt = CreateWriter(Path.Combine(SessionDirectory, "tcp-rtt.csv"));
                internetChecks = CreateWriter(Path.Combine(SessionDirectory, "internet-checks.csv"));
                connectivityIncidents = CreateWriter(Path.Combine(SessionDirectory, "connectivity-incidents.csv"));
                metadata = CreateWriter(Path.Combine(SessionDirectory, "session.txt"));
                samples.WriteLine(SessionLog.CsvHeader);
                marks.WriteLine("timestamp_local,label");
                probes.WriteLine(ProbeHeader);
                processes.WriteLine("timestamp_utc,interval_start_utc,interval_end_utc,interval_seconds,pid,process,process_instance,identity_available,send_bytes_s,receive_bytes_s,send_bytes_total,receive_bytes_total,scope");
                endpoints.WriteLine("timestamp_utc,timestamp_local,process,pid,protocol,local,remote,send_bytes_total,receive_bytes_total,send_events_total,receive_events_total,tcp_retransmit_events_total,last_seen_utc");
                tcpRtt.WriteLine(TcpRttHeader);
                internetChecks.WriteLine(InternetCheckHeader);
                connectivityIncidents.WriteLine(ConnectivityIncidentHeader);
                metadata.WriteLine("format_version=5");
                metadata.WriteLine("started_utc=" + startedUtc.ToString("o", CultureInfo.InvariantCulture));
                metadata.WriteLine("started_local=" + startedUtc.ToLocalTime().ToString("o", CultureInfo.InvariantCulture));
                metadata.WriteLine("state=recording");
                metadata.WriteLine("state_scope=record_file_writer_only; not application process exit status");
                metadata.WriteLine("timestamps=ISO-8601; sample cadence is controlled by the monitor window");
                metadata.WriteLine("privacy=local files only; packet payloads are not stored");
                metadata.WriteLine("endpoint_scope=selected game; changed cumulative snapshots, not individual packets; observed endpoint retention limit 512");
                metadata.WriteLine("tcp_rtt_scope=selected process TCP connections; Windows smoothed RTT; cached collector timestamps are stored once; not actual game server ping or packet loss");
                metadata.WriteLine("internet_check_scope=individual configured DNS/HTTPS targets; system routing/proxy apply; failed target check is not proof of whole Internet or ISP outage");
                metadata.WriteLine("incident_scope=observed check failures and local connection state; first_failed_check is an observation, not the exact physical outage start; observation_closed is not recovered");
                metadata.WriteLine("internet_check_dedup=monotonic completion timestamp per kind/target; at most 64 distinct target keys");
                metadata.WriteLine("incident_dedup=monotonic version for most recent 4096 incident identities");
                FlushAll();
                worker = new Thread(WriteLoop);
                worker.IsBackground = true;
                worker.Name = "Network monitor local session log";
                worker.Priority = ThreadPriority.BelowNormal;
                worker.Start();
            }
            catch
            {
                // Preserve any partial files for diagnosis instead of deleting user data.
                CloseWriter(ref samples); CloseWriter(ref marks); CloseWriter(ref probes); CloseWriter(ref processes); CloseWriter(ref endpoints); CloseWriter(ref tcpRtt); CloseWriter(ref internetChecks); CloseWriter(ref connectivityIncidents); CloseWriter(ref metadata);
                wake.Dispose();
                throw;
            }
        }

        private static StreamWriter CreateWriter(string path)
        {
            return new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(true), 4096);
        }

        public bool Append(MonitorSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            // Serialize before enqueueing so later UI mutations cannot change the stored sample.
            string sampleText = SessionLog.ToCsvRow(snapshot);
            int tcpRttCount;
            string tcpRttText = SerializeTcpRtt(snapshot, out tcpRttCount);
            ProcessTrafficSnapshot p = snapshot.ProcessTraffic;
            StringBuilder batch = new StringBuilder();
            if (p != null && p.Available && p.IntervalSeconds > 0)
                foreach (ProcessTrafficRow row in p.Rows)
                    batch.AppendLine(String.Join(",", new string[] {
                        p.TimestampUtc.ToUniversalTime().ToString("o"), p.IntervalStartUtc.ToUniversalTime().ToString("o"), p.IntervalEndUtc.ToUniversalTime().ToString("o"), p.IntervalSeconds.ToString("0.######", CultureInfo.InvariantCulture),
                        row.ProcessId.ToString(CultureInfo.InvariantCulture), SessionLog.Csv(row.ProcessName), SessionLog.Csv(row.ProcessInstance), row.IdentityAvailable ? "true" : "false",
                        row.SentBytesPerSecond.ToString("0.###", CultureInfo.InvariantCulture), row.ReceivedBytesPerSecond.ToString("0.###", CultureInfo.InvariantCulture),
                        row.SentBytesTotal.ToString(CultureInfo.InvariantCulture), row.ReceivedBytesTotal.ToString(CultureInfo.InvariantCulture), "all_adapters_etw_delivery"
                    }));
            lock (gate)
            {
                bool writeProcesses = batch.Length > 0 && p.TimestampUtc > lastProcessTimestamp;
                bool writeTcpRtt = tcpRttCount > 0 && snapshot.TcpRtt.TimestampUtc > lastTcpRttTimestamp;
                if (!healthy || completing || disposed) return false;
                Dictionary<string, string> nextEndpoints = null;
                StringBuilder endpointBatch = new StringBuilder();
                int endpointCount = 0;
                if (snapshot.EndpointObservations != null)
                {
                    nextEndpoints = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (EndpointRow row in snapshot.EndpointObservations)
                    {
                        if (row == null) continue;
                        string key = snapshot.ProcessId.ToString(CultureInfo.InvariantCulture) + "|" + row.Protocol + "|" + row.Local + "|" + row.Remote;
                        if (nextEndpoints.Count >= 512 && !nextEndpoints.ContainsKey(key)) break;
                        string value = String.Join(",", new string[] {
                            SessionLog.Csv(snapshot.ProcessName), snapshot.ProcessId.ToString(CultureInfo.InvariantCulture), SessionLog.Csv(row.Protocol), SessionLog.Csv(row.Local), SessionLog.Csv(row.Remote),
                            row.SentBytes.ToString(CultureInfo.InvariantCulture), row.ReceivedBytes.ToString(CultureInfo.InvariantCulture), row.SentEvents.ToString(CultureInfo.InvariantCulture),
                            row.ReceivedEvents.ToString(CultureInfo.InvariantCulture), row.RetransmitEvents.ToString(CultureInfo.InvariantCulture), row.LastSeen.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                        });
                        string previous;
                        if (!nextEndpoints.ContainsKey(key) && (!previousEndpoints.TryGetValue(key, out previous) || previous != value))
                        {
                            endpointBatch.Append(snapshot.TimestampUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)); endpointBatch.Append(',');
                            endpointBatch.Append(snapshot.TimestampUtc.ToLocalTime().ToString("o", CultureInfo.InvariantCulture)); endpointBatch.Append(',');
                            endpointBatch.AppendLine(value); endpointCount++;
                        }
                        nextEndpoints[key] = value;
                    }
                }
                long addedCharacters = (long)sampleText.Length + (writeProcesses ? batch.Length : 0) + endpointBatch.Length + (writeTcpRtt ? tcpRttText.Length : 0);
                if (pending.Count + 1 + (writeProcesses ? 1 : 0) + (endpointCount > 0 ? 1 : 0) + (writeTcpRtt ? 1 : 0) > MaximumQueuedRows || queuedCharacters + addedCharacters > MaximumQueuedCharacters)
                { FailLocked("디스크 기록 대기열이 가득 찼습니다. 이후 데이터는 자동 저장되지 않습니다."); return false; }
                QueueLocked(new PendingRow { Kind = 0, Text = sampleText }); acceptedSamples++;
                if (writeProcesses)
                {
                    QueueLocked(new PendingRow { Kind = 3, Text = batch.ToString(), Count = p.Rows.Count }); lastProcessTimestamp = p.TimestampUtc;
                }
                if (endpointCount > 0) QueueLocked(new PendingRow { Kind = 4, Text = endpointBatch.ToString(), Count = endpointCount });
                if (writeTcpRtt)
                {
                    QueueLocked(new PendingRow { Kind = 5, Text = tcpRttText, Count = tcpRttCount });
                    lastTcpRttTimestamp = snapshot.TcpRtt.TimestampUtc;
                }
                if (nextEndpoints != null) previousEndpoints = nextEndpoints;
                wake.Set(); return true;
            }
        }

        private static string SerializeTcpRtt(MonitorSnapshot snapshot, out int count)
        {
            count = 0;
            TcpRttSnapshot value = snapshot.TcpRtt;
            if (value == null || snapshot.ProcessId <= 0 || value.TimestampUtc == DateTime.MinValue) return "";
            bool sameProcess = value.ProcessId == snapshot.ProcessId;
            bool available = sameProcess && value.Available && !value.Stale;
            string[] prefix = new string[] {
                snapshot.TimestampUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture), value.TimestampUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                snapshot.ProcessId.ToString(CultureInfo.InvariantCulture), SessionLog.Csv(snapshot.ProcessName), value.Requested ? "true" : "false",
                SessionLog.Csv(sameProcess ? value.State : "pid_mismatch"), SessionLog.Csv(sameProcess ? value.Status : "선택한 PID와 TCP RTT 자료가 다름"),
                available ? "true" : "false", value.PermissionDenied ? "true" : "false", value.Stale ? "true" : "false", value.Partial ? "true" : "false",
                value.ConnectionsFound.ToString(CultureInfo.InvariantCulture), value.ConnectionsTracked.ToString(CultureInfo.InvariantCulture),
                value.ConnectionsMeasured.ToString(CultureInfo.InvariantCulture), value.ConnectionsSkipped.ToString(CultureInfo.InvariantCulture),
                RttNumber(available ? value.MinimumMs : null), RttNumber(available ? value.MaximumMs : null),
                value.NativeErrorCode.ToString(CultureInfo.InvariantCulture), SessionLog.Csv(value.ErrorStage)
            };
            StringBuilder rows = new StringBuilder();
            if (sameProcess && value.Connections != null)
                foreach (TcpRttConnection connection in value.Connections)
                {
                    if (connection == null) continue;
                    bool measured = available && connection.Available && connection.CollectionEnabled;
                    rows.AppendLine(String.Join(",", prefix) + "," + String.Join(",", new string[] {
                        SessionLog.Csv(connection.Key), SessionLog.Csv(connection.AddressFamily), SessionLog.Csv(connection.Local), SessionLog.Csv(connection.Remote),
                        connection.CreationTimestamp.ToString(CultureInfo.InvariantCulture), measured ? "true" : "false",
                        connection.CollectionEnabled ? "true" : "false", connection.CollectionOwned ? "true" : "false",
                        connection.LastSampleUtc.HasValue ? connection.LastSampleUtc.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : "",
                        RttNumber(measured ? connection.SmoothedRttMs : null), RttNumber(measured ? connection.SampleRttMs : null),
                        connection.SamplesTotal.ToString(CultureInfo.InvariantCulture), connection.SamplesDelta.ToString(CultureInfo.InvariantCulture),
                        connection.NativeErrorCode.ToString(CultureInfo.InvariantCulture), SessionLog.Csv(connection.Status), "unsupported", "unsupported"
                    }));
                    count++;
                }
            if (count == 0)
            {
                // A state row is evidence of unavailability; it is never a measured zero RTT.
                rows.AppendLine(String.Join(",", prefix) + "," + String.Join(",", new string[] {
                    "", "", "", "", "", "false", "false", "false", "", "", "", "", "", "", "", "unsupported", "unsupported"
                }));
                count = 1;
            }
            return rows.ToString();
        }

        private static string RttNumber(double? value)
        {
            return value.HasValue && !Double.IsNaN(value.Value) && !Double.IsInfinity(value.Value) && value.Value >= 0 ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : "";
        }

        public bool AppendInternetCheck(InternetCheckSample sample)
        {
            if (sample == null) throw new ArgumentNullException("sample");
            // Copy values to a row before enqueueing; callers can reuse their snapshots.
            string key = (sample.Kind ?? "") + "\n" + (sample.Target ?? "");
            DateTime timestamp = sample.TimestampUtc;
            string text = String.Join(",", new string[] {
                Utc(sample.StartedUtc), Utc(timestamp), timestamp == DateTime.MinValue ? "" : timestamp.ToLocalTime().ToString("o", CultureInfo.InvariantCulture),
                SessionLog.Csv(sample.Kind), SessionLog.Csv(sample.Target), SessionLog.Csv(sample.State), SessionLog.Csv(sample.Status),
                sample.Success ? "true" : "false", sample.TimedOut ? "true" : "false", sample.Cancelled ? "true" : "false",
                RttNumber(sample.DurationMs), sample.HttpStatusCode.HasValue ? sample.HttpStatusCode.Value.ToString(CultureInfo.InvariantCulture) : "", SessionLog.Csv(sample.Error)
            });
            lock (gate)
            {
                if (!healthy || completing || disposed) return false;
                DateTime previous;
                if (timestamp == DateTime.MinValue || (lastInternetChecks.TryGetValue(key, out previous) && timestamp <= previous)) return true;
                if (!lastInternetChecks.ContainsKey(key) && lastInternetChecks.Count >= 64)
                { FailLocked("인터넷 검사 대상 식별 한도를 초과했습니다. 이후 데이터는 자동 저장되지 않습니다."); return false; }
                if (!QueueSingleLocked(6, text)) return false;
                lastInternetChecks[key] = timestamp;
                return true;
            }
        }

        public bool AppendConnectivityIncident(ConnectivityIncident incident)
        {
            if (incident == null) throw new ArgumentNullException("incident");
            string id = incident.Id ?? ""; long version = incident.Version;
            string text = String.Join(",", new string[] {
                SessionLog.Csv(id), version.ToString(CultureInfo.InvariantCulture), SessionLog.Csv(incident.Kind), SessionLog.Csv(incident.Target),
                SessionLog.Csv(incident.Category), SessionLog.Csv(incident.Scope), SessionLog.Csv(incident.State),
                Utc(incident.StartedUtc), Utc(incident.LastObservedUtc), OptionalUtc(incident.RecoveredUtc), OptionalUtc(incident.ClosedUtc),
                RttNumber(incident.DurationSeconds), incident.Failures.ToString(CultureInfo.InvariantCulture), SessionLog.Csv(incident.Summary), SessionLog.Csv(incident.EndReason)
            });
            lock (gate)
            {
                if (!healthy || completing || disposed) return false;
                if (id.Length == 0 || version <= 0) return true;
                long previous;
                if (lastIncidentVersions.TryGetValue(id, out previous) && version <= previous) return true;
                if (!QueueSingleLocked(7, text)) return false;
                if (!lastIncidentVersions.ContainsKey(id))
                {
                    while (incidentIdentityOrder.Count >= 4096) lastIncidentVersions.Remove(incidentIdentityOrder.Dequeue());
                    incidentIdentityOrder.Enqueue(id);
                }
                lastIncidentVersions[id] = version;
                return true;
            }
        }

        private bool QueueSingleLocked(int kind, string text)
        {
            if (pending.Count >= MaximumQueuedRows || queuedCharacters + text.Length > MaximumQueuedCharacters)
            { FailLocked("디스크 기록 대기열이 가득 찼습니다. 이후 데이터는 자동 저장되지 않습니다."); return false; }
            QueueLocked(new PendingRow { Kind = kind, Text = text, Count = 1 });
            wake.Set(); return true;
        }

        private static string Utc(DateTime value) { return value == DateTime.MinValue ? "" : value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture); }
        private static string OptionalUtc(DateTime? value) { return value.HasValue ? Utc(value.Value) : ""; }

        public bool Mark(DateTime timestampUtc, string label)
        {
            return Enqueue(1, timestampUtc.ToLocalTime().ToString("o", CultureInfo.InvariantCulture) + "," + SessionLog.Csv(label));
        }

        public bool RecordProbe(string kind, ProbeSample sample)
        {
            if (sample == null) throw new ArgumentNullException("sample");
            return Enqueue(2, String.Join(",", new string[] {
                sample.TimestampUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                sample.TimestampUtc.ToLocalTime().ToString("o", CultureInfo.InvariantCulture),
                SessionLog.Csv(kind), SessionLog.Csv(sample.Target), SessionLog.Csv(sample.Status),
                sample.RoundtripMs.HasValue ? sample.RoundtripMs.Value.ToString("0.###", CultureInfo.InvariantCulture) : "",
                sample.Countable ? "true" : "false", sample.TimedOut ? "true" : "false"
            }));
        }

        private bool Enqueue(int kind, string text)
        {
            lock (gate)
            {
                if (!healthy || completing || disposed) return false;
                if (pending.Count >= MaximumQueuedRows || queuedCharacters + text.Length > MaximumQueuedCharacters)
                {
                    FailLocked("디스크 기록 대기열이 가득 찼습니다. 이후 데이터는 자동 저장되지 않습니다.");
                    return false;
                }
                QueueLocked(new PendingRow { Kind = kind, Text = text });
                if (kind == 0) acceptedSamples++;
                wake.Set();
                return true;
            }
        }

        private void QueueLocked(PendingRow row)
        {
            pending.Enqueue(row); queuedCharacters += row.Text.Length;
        }

        private void WriteLoop()
        {
            Stopwatch flushClock = Stopwatch.StartNew();
            try
            {
                while (true)
                {
                    PendingRow row = null;
                    bool done;
                    lock (gate)
                    {
                        if (pending.Count > 0) { row = pending.Dequeue(); queuedCharacters -= row.Text.Length; }
                        done = completing && row == null;
                    }
                    if (done) break;
                    if (row != null)
                    {
                        if (row.Kind == 0) { samples.WriteLine(row.Text); writtenSamples++; }
                        else if (row.Kind == 1) { marks.WriteLine(row.Text); writtenMarks++; }
                        else if (row.Kind == 2) { probes.WriteLine(row.Text); writtenProbes++; }
                        else if (row.Kind == 3) { processes.Write(row.Text); writtenProcessRows += row.Count; }
                        else if (row.Kind == 4) { endpoints.Write(row.Text); writtenEndpointRows += row.Count; }
                        else if (row.Kind == 5) { tcpRtt.Write(row.Text); writtenTcpRttRows += row.Count; }
                        else if (row.Kind == 6) { internetChecks.WriteLine(row.Text); writtenInternetChecks++; }
                        else if (row.Kind == 7) { connectivityIncidents.WriteLine(row.Text); writtenConnectivityIncidents++; }
                    }
                    if (flushClock.ElapsedMilliseconds >= 1000)
                    {
                        FlushAll(); flushClock.Restart();
                    }
                    if (row == null) wake.WaitOne(250);
                }
                FlushAll();
            }
            catch (Exception ex) { Fail("자동 기록에 실패했습니다: " + ex.Message); }
            finally
            {
                CloseWriter(ref samples); CloseWriter(ref marks); CloseWriter(ref probes); CloseWriter(ref processes); CloseWriter(ref endpoints); CloseWriter(ref tcpRtt); CloseWriter(ref internetChecks); CloseWriter(ref connectivityIncidents);
                try
                {
                    metadata.WriteLine("ended_utc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    metadata.WriteLine("reason=" + SingleLine(completionReason));
                    metadata.WriteLine("samples_accepted=" + SampleCount.ToString(CultureInfo.InvariantCulture));
                    metadata.WriteLine("samples_written=" + writtenSamples.ToString(CultureInfo.InvariantCulture));
                    metadata.WriteLine("marks_written=" + writtenMarks.ToString(CultureInfo.InvariantCulture));
                    metadata.WriteLine("probes_written=" + writtenProbes.ToString(CultureInfo.InvariantCulture));
                    metadata.WriteLine("process_rows_written=" + writtenProcessRows.ToString(CultureInfo.InvariantCulture));
                    metadata.WriteLine("endpoint_rows_written=" + writtenEndpointRows.ToString(CultureInfo.InvariantCulture));
                    metadata.WriteLine("tcp_rtt_rows_written=" + writtenTcpRttRows.ToString(CultureInfo.InvariantCulture));
                    metadata.WriteLine("internet_checks_written=" + writtenInternetChecks.ToString(CultureInfo.InvariantCulture));
                    metadata.WriteLine("connectivity_incident_versions_written=" + writtenConnectivityIncidents.ToString(CultureInfo.InvariantCulture));
                    metadata.WriteLine("state=" + (IsHealthy ? "completed" : "error"));
                    metadata.WriteLine("error=" + SingleLine(ErrorMessage));
                    metadata.Flush();
                }
                catch (Exception ex) { Fail("기록 종료 정보를 저장하지 못했습니다: " + ex.Message); }
                CloseWriter(ref metadata);
            }
        }

        private static string SingleLine(string value) { return (value ?? "").Replace("\r", " ").Replace("\n", " "); }
        private void FlushAll() { samples.Flush(); marks.Flush(); probes.Flush(); processes.Flush(); endpoints.Flush(); tcpRtt.Flush(); internetChecks.Flush(); connectivityIncidents.Flush(); metadata.Flush(); }
        private void Fail(string message) { lock (gate) FailLocked(message); }
        private void FailLocked(string message)
        {
            healthy = false;
            if (errorMessage.Length == 0) errorMessage = message;
        }
        private void CloseWriter(ref StreamWriter writer)
        {
            StreamWriter value = writer; writer = null;
            if (value == null) return;
            try { value.Dispose(); }
            catch (Exception ex) { Fail("기록 파일을 닫지 못했습니다: " + ex.Message); }
        }

        public void Complete(string reason)
        {
            lock (gate)
            {
                if (disposed) return;
                if (!completing)
                {
                    completionReason = reason ?? "측정 종료";
                    completing = true;
                    wake.Set();
                }
            }
            if (Thread.CurrentThread != worker && !worker.Join(3000))
                Fail("디스크 기록 종료가 지연되고 있습니다. 최근 기록이 아직 저장되지 않았을 수 있습니다.");
        }

        public void Dispose()
        {
            Complete("프로그램 종료");
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                // A stalled disk writer can still use the event. Do not close its wait handle.
                if (!worker.IsAlive) wake.Dispose();
            }
        }

        public static void SelfTest(string testDir)
        {
            string fixtureRoot = Path.Combine(Path.GetFullPath(testDir), "automatic-log-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtureRoot);
            List<string> sessionDirs = new List<string>();
            string blockedRoot = Path.Combine(fixtureRoot, "not-a-directory");
            try
            {
                DateTime now = DateTime.UtcNow;
                MonitorSnapshot sample = new MonitorSnapshot { TimestampUtc = now, ProcessId = 42, ProcessName = "test,process", GameStatus = "fixture" };
                string firstSamples;
                using (AutomaticSessionLog log = new AutomaticSessionLog(fixtureRoot))
                {
                    sessionDirs.Add(log.SessionDirectory); firstSamples = log.SamplesPath;
                    Check(log.Append(sample), "enqueue sample");
                    Check(log.Mark(now, "manual, \"mark\""), "enqueue optional mark");
                    Check(log.RecordProbe("gateway", new ProbeSample { TimestampUtc = now, Target = "127.0.0.1", Status = "fixture reply", RoundtripMs = 7.5, Countable = true }), "enqueue reply");
                    Check(log.RecordProbe("reference", new ProbeSample { TimestampUtc = now.AddSeconds(1), Target = "127.0.0.1", Status = "fixture timeout", TimedOut = true, Countable = true }), "enqueue timeout");
                    Stopwatch deadline = Stopwatch.StartNew();
                    bool flushed = false;
                    while (deadline.ElapsedMilliseconds < 2500)
                    {
                        using (StreamReader reader = new StreamReader(new FileStream(firstSamples, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8))
                            flushed = reader.ReadToEnd().Contains(SessionLog.ToCsvRow(sample));
                        if (flushed) break;
                        Thread.Sleep(50);
                    }
                    Check(flushed, "periodic flush while measuring");
                    log.Complete("self-test");
                    Check(log.IsHealthy && log.SampleCount == 1, "healthy complete");
                    Check(!log.Append(sample), "closed log rejects new samples");
                }
                string[] rows = File.ReadAllLines(firstSamples, Encoding.UTF8);
                Check(rows.Length == 2 && rows[0] == SessionLog.CsvHeader && rows[1] == SessionLog.ToCsvRow(sample), "CSV serialization");
                byte[] bytes = File.ReadAllBytes(firstSamples);
                Check(bytes.Length > 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191, "UTF-8 BOM");
                string firstDir = Path.GetDirectoryName(firstSamples);
                string[] probeRows = File.ReadAllLines(Path.Combine(firstDir, "probes.csv"), Encoding.UTF8);
                Check(probeRows.Length == 3 && probeRows[1].Contains(",7.5,true,false") && probeRows[2].EndsWith(",,true,true", StringComparison.Ordinal), "raw probe details");
                Check(File.ReadAllText(Path.Combine(firstDir, "marks.csv"), Encoding.UTF8).Contains("\"manual, \"\"mark\"\"\""), "mark quoting");
                Check(File.ReadAllText(Path.Combine(firstDir, "session.txt"), Encoding.UTF8).Contains("state=completed"), "completion metadata");
                string previousContent = File.ReadAllText(firstSamples, Encoding.UTF8);
                using (AutomaticSessionLog second = new AutomaticSessionLog(fixtureRoot))
                {
                    sessionDirs.Add(second.SessionDirectory);
                    Check(second.SessionDirectory != firstDir, "unique session directory");
                    sample.ProcessTraffic = new ProcessTrafficSnapshot { Available = true, TimestampUtc = now, IntervalStartUtc = now.AddSeconds(-1), IntervalEndUtc = now, IntervalSeconds = 1 };
                    for (int pid = 1; pid <= 40; pid++)
                        sample.ProcessTraffic.Rows.Add(new ProcessTrafficRow { ProcessId = pid, ProcessName = "test, sender " + pid.ToString(CultureInfo.InvariantCulture), ProcessInstance = "fixture-" + pid.ToString(CultureInfo.InvariantCulture), IdentityAvailable = true, SentBytesPerSecond = 2000, SentBytesTotal = 2000 });
                    sample.EndpointObservations.Add(new EndpointRow { Protocol = "UDP", Local = "127.0.0.1:1200", Remote = "203.0.113.1:1300", SentBytes = 200, SentEvents = 2, LastSeen = now });
                    sample.EndpointObservations.Add(new EndpointRow { Protocol = "TCP", Local = "127.0.0.1:1400", Remote = "203.0.113.2:443", ReceivedBytes = 500, ReceivedEvents = 5, RetransmitEvents = 1, LastSeen = now });
                    Check(second.Append(sample) && second.Append(sample), "append shared process interval");
                    sample.TimestampUtc = now.AddSeconds(1);
                    sample.EndpointObservations[0].SentBytes += 100;
                    sample.EndpointObservations[0].SentEvents++;
                    sample.EndpointObservations[0].LastSeen = sample.TimestampUtc;
                    Check(second.Append(sample), "append one changed game endpoint");
                    List<EndpointRow> observed = sample.EndpointObservations;
                    sample.EndpointObservations = null;
                    Check(second.Append(sample), "missing endpoint snapshot does not change previous observations");
                    sample.EndpointObservations = observed;
                    Check(second.Append(sample), "unchanged endpoints after missing snapshot");
                    second.Complete("second fixture");
                    Check(second.IsHealthy && File.ReadAllLines(Path.Combine(second.SessionDirectory, "process-traffic.csv")).Length == 41, "all 40 process rows are persisted and cached interval is stored once");
                    string[] endpointRows = File.ReadAllLines(Path.Combine(second.SessionDirectory, "game-endpoints.csv"), Encoding.UTF8);
                    Check(endpointRows.Length == 4 && endpointRows[1].Contains("\"UDP\",\"127.0.0.1:1200\",\"203.0.113.1:1300\",200,0,2,0,0,") && endpointRows[3].Contains("\"UDP\",\"127.0.0.1:1200\",\"203.0.113.1:1300\",300,0,3,0,0,"), "endpoint metadata and changed cumulative counts persist without unchanged duplicate rows");
                    Check(second.previousEndpoints.Count == 2 && File.ReadAllText(Path.Combine(second.SessionDirectory, "session.txt"), Encoding.UTF8).Contains("endpoint_rows_written=3"), "endpoint metadata reports written rows and deduplication retains only current endpoints");
                }
                Check(File.ReadAllText(firstSamples, Encoding.UTF8) == previousContent, "previous session preserved");
                File.WriteAllText(blockedRoot, "fixture root is a file", Encoding.UTF8);
                bool failed = false;
                try { using (AutomaticSessionLog invalid = new AutomaticSessionLog(blockedRoot)) { } }
                catch (IOException) { failed = true; }
                catch (UnauthorizedAccessException) { failed = true; }
                Check(failed && File.ReadAllText(blockedRoot, Encoding.UTF8) == "fixture root is a file", "unwritable root failure preserves data");
            }
            finally
            {
                // Only this test's exact files and empty directories are removed, never recursively.
                foreach (string dir in sessionDirs)
                {
                    foreach (string name in new string[] { "samples.csv", "marks.csv", "probes.csv", "process-traffic.csv", "game-endpoints.csv", "tcp-rtt.csv", "internet-checks.csv", "connectivity-incidents.csv", "session.txt" })
                    {
                        string path = Path.Combine(dir, name); if (File.Exists(path)) File.Delete(path);
                    }
                    if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
                }
                if (File.Exists(blockedRoot)) File.Delete(blockedRoot);
                if (Directory.Exists(fixtureRoot) && Directory.GetFileSystemEntries(fixtureRoot).Length == 0) Directory.Delete(fixtureRoot);
            }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Automatic log test: " + message);
        }
    }
}
