using System;

namespace PcInternetMonitor
{
    // Durations cover the complete check, not ICMP latency or a selected NIC path.
    public sealed class InternetCheckSample
    {
        public string Kind = "", Target = "", State = "waiting", Status = "검사 대기", Error = "";
        public DateTime StartedUtc, TimestampUtc;
        public bool Success, TimedOut, Cancelled, InProgress, Stale;
        public double? DurationMs;
        public int? HttpStatusCode;
        public long Generation;

        public InternetCheckSample Copy() { return (InternetCheckSample)MemberwiseClone(); }
    }

    public sealed class InternetCheckSnapshot
    {
        public InternetCheckSample Dns, Https;
        public bool Running;
        public long Generation;
    }
}
