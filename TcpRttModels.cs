using System;
using System.Collections.Generic;

namespace PcInternetMonitor
{
    // Windows TCP-stack measurements only; these are not a game's own ping or UDP loss.
    public sealed class TcpRttSnapshot
    {
        public DateTime TimestampUtc;
        public int ProcessId;
        public bool Requested, Available, PermissionDenied, Stale, Partial;
        public string State = "not_requested", Status = "TCP RTT 측정 대기", ErrorStage = "";
        public int NativeErrorCode, ConnectionsFound, ConnectionsTracked, ConnectionsMeasured, ConnectionsSkipped;
        public double? MinimumMs, MaximumMs;
        public List<TcpRttConnection> Connections = new List<TcpRttConnection>();
    }

    public sealed class TcpRttConnection
    {
        public DateTime? LastSampleUtc;
        public string Key, AddressFamily, Local, Remote, Status;
        public long CreationTimestamp;
        public bool CollectionEnabled, CollectionOwned, Available;
        public double? SmoothedRttMs, SampleRttMs;
        public uint SamplesTotal, SamplesDelta;
        public int NativeErrorCode;
    }
}
