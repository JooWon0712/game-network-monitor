using System;
using System.Collections.Generic;

namespace PcInternetMonitor
{
    public sealed class TrafficEvent
    {
        public DateTime TimestampUtc;
        public DateTime ObservedUtc;
        public int ProcessId;
        public long CaptureGeneration;
        public string Protocol;
        public bool IsSend;
        public bool IsRetransmit;
        public long Bytes;
        public string SourceAddress;
        public int SourcePort;
        public string DestinationAddress;
        public int DestinationPort;
    }

    public sealed class TraceHealth
    {
        public bool Active;
        public string Status = "게임 수집 대기";
        public bool PermissionDenied;
        public int NativeErrorCode;
        public string FailureStage;
        public long EventsLost;
        public long BuffersLost;
        public long MalformedEvents;
        public long CandidateEvents;
        public long SelectedPidEvents;
        public long UnsupportedEvents;
    }

    public sealed class TraceStopResult
    {
        public TraceHealth Health = new TraceHealth();
        public ProcessTrafficSnapshot ProcessTraffic = new ProcessTrafficSnapshot();
        public bool Drained;
        public DateTime RequestedUtc;
        public string Status;
        public uint StopNativeCode;
        public uint ConsumerExitCode;
        public bool TimedOut;
    }

    public sealed class AdapterInfo
    {
        public string Id;
        public string Name;
        public string Description;
        public string Gateway;
        public string LocalAddresses;
        public bool IsUp;
        public override string ToString() { return Name + (IsUp ? "" : " · 연결 끊김"); }
    }

    public sealed class AdapterSample
    {
        public DateTime TimestampUtc;
        public bool Available;
        public string Status;
        public long ReceivedBytes;
        public long SentBytes;
        public long ReceivedPackets;
        public long SentPackets;
        public long ReceiveErrors;
        public long SendErrors;
        public long ReceiveDiscards;
        public long SendDiscards;
    }

    public sealed class ProbeSample
    {
        public DateTime TimestampUtc;
        public string Target;
        public string Status;
        public double? RoundtripMs;
        public bool TimedOut;
        public bool Countable;
        // Independent of the timeout-ratio denominator: a known route/ICMP failure.
        public bool NetworkFailure;
    }

    public sealed class EndpointRow
    {
        public string Protocol { get; set; }
        public string Local { get; set; }
        public string Remote { get; set; }
        public long SentEvents { get; set; }
        public long ReceivedEvents { get; set; }
        public long RetransmitEvents { get; set; }
        public long SentBytes { get; set; }
        public long ReceivedBytes { get; set; }
        public DateTime LastSeen { get; set; }
    }

    public sealed class ProbeSummary
    {
        public string Target;
        public string Status;
        public double? CurrentMs;
        public double? AverageMs;
        public double? VariationMs;
        public double? MissingPercent;
        public int Sent;
        public int Missing;
        public int Errors;
        public int NetworkFailures;
        public DateTime? LastSampleUtc;
        public bool CurrentError;
        public bool CurrentNetworkFailure;
        public bool CurrentTimedOut;
        public bool Stale;
    }

    public sealed class MonitorSnapshot
    {
        public DateTime TimestampUtc;
        public bool Running;
        public int ProcessId;
        public string ProcessName;
        public string GameStatus;
        public TraceHealth Trace = new TraceHealth();
        public bool GameRatesAvailable;
        public bool GameCollectionRequested;
        public TcpRttSnapshot TcpRtt = new TcpRttSnapshot();
        public bool ProcessCollectionRequested;
        public bool GameProcessEnded;
        public bool AdapterExpected;
        public long AdapterCounterResets;
        public long AdapterCounterGaps;
        public bool IsFinal;
        public bool StopDrainComplete;
        public string StopBoundaryStatus;
        public DateTime? StopRequestedUtc;
        public double GameSentBytesPerSecond;
        public double GameReceivedBytesPerSecond;
        public double GameSentEventsPerSecond;
        public double GameReceivedEventsPerSecond;
        public long GameSentEvents;
        public long GameReceivedEvents;
        public long RetransmitEvents;
        public double? GameReceiveIdleSeconds;
        public double? GameReceiveDeliveryIdleSeconds;
        public double? GameEventDeliveryLagMs;
        public double? GameMaxEventDeliveryLagMs;
        public DateTime? GameLastReceiveEventUtc;
        public DateTime? GameLastReceiveObservedUtc;
        public long GameInvalidEventTimeCount;
        public ProcessTrafficSnapshot ProcessTraffic = new ProcessTrafficSnapshot();
        public ProbeSummary Gateway = new ProbeSummary();
        public ProbeSummary Reference = new ProbeSummary();
        public string AdapterStatus;
        public bool AdapterCountersAvailable;
        public bool AdapterRatesAvailable;
        public double AdapterSentBytesPerSecond;
        public double AdapterReceivedBytesPerSecond;
        public double AdapterSentPacketsPerSecond;
        public double AdapterReceivedPacketsPerSecond;
        public long AdapterReceiveErrors;
        public long AdapterSendErrors;
        public long AdapterReceiveDiscards;
        public long AdapterSendDiscards;
        public List<EndpointRow> Endpoints = new List<EndpointRow>();
        public List<EndpointRow> EndpointObservations = new List<EndpointRow>();
        public long GameEndpointEvictions;
    }
}
