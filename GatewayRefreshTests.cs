using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PcInternetMonitor
{
    internal static class GatewayRefreshTests
    {
        public static void SelfTest()
        {
            MonitorController.GatewayRefreshSelfTest();
            MonitorController.ProbeNetworkFailureSelfTest();
        }
    }

    public sealed partial class MonitorController
    {
        internal static void GatewayRefreshSelfTest()
        {
            const string id = "synthetic-selected-adapter", first = "192.0.2.1", second = "192.0.2.2", external = "203.0.113.8";
            long clock = 0;
            int lookups = 0, stops = 0, delivered = 0;
            bool throwLookup = false;
            AdapterInfo found = new AdapterInfo { Id = id, Gateway = first };
            Action duringLookup = null;
            List<string> starts = new List<string>(), transitions = new List<string>();
            int startsAtEvent = -1;
            MonitorController controller = null;
            using (controller = new MonitorController(delegate(string requestedId) {
                Check(!Monitor.IsEntered(controller.gate), "gateway enumeration held the sample gate");
                Check(!Monitor.IsEntered(controller.gatewayControlGate), "gateway enumeration held the probe control gate");
                Check(requestedId == id, "gateway lookup switched the selected adapter");
                lookups++;
                if (throwLookup) throw new InvalidOperationException("synthetic adapter lookup failure");
                if (duringLookup != null) { Action action = duringLookup; duringLookup = null; action(); }
                return found;
            }, delegate(string target) { starts.Add(target); }, delegate { stops++; }, delegate { return clock; }))
            {
                controller.ProbeSampled += delegate(string kind, ProbeSample sample) { if (kind == "gateway") delivered++; };
                controller.GatewayTargetChanged += delegate(string oldTarget, string newTarget) {
                    Check(!Monitor.IsEntered(controller.gate), "gateway target subscriber held the sample gate");
                    transitions.Add(oldTarget + ">" + newTarget); startsAtEvent = starts.Count;
                };
                controller.Start(0, id, first, "", false, false, true);
                long session = controller.captureGeneration;
                controller.reference.Clear(external);
                controller.reference.Add(GatewayReply(external));
                Action<ProbeSample> firstReceiver = controller.gatewaySampleHandler;
                firstReceiver(GatewayReply(first));
                Check(controller.Snapshot().Gateway.Sent == 1 && delivered == 1 && lookups == 0 && starts.Count == 1,
                    "initial gateway or refresh throttle invalid");

                found = new AdapterInfo { Id = id, Gateway = second };
                clock += 2 * Stopwatch.Frequency;
                MonitorSnapshot changed = controller.Snapshot();
                Check(changed.Gateway.Target == second && changed.Gateway.Sent == 0 && !changed.Gateway.CurrentMs.HasValue,
                    "gateway change retained old measurements");
                Check(changed.Reference.Target == external && changed.Reference.Sent == 1 && controller.captureGeneration == session,
                    "gateway refresh reset an independent lane or session");
                Check(starts.Count == 2 && starts[1] == second && startsAtEvent == 1 && transitions[0] == first + ">" + second,
                    "replacement probe started before target-change history notification");
                firstReceiver(GatewayReply(first));
                firstReceiver(GatewayReply(second));
                Check(delivered == 1 && controller.Snapshot().Gateway.Sent == 0,
                    "old target callback reached the replacement epoch or logger");
                Action<ProbeSample> secondReceiver = controller.gatewaySampleHandler;
                secondReceiver(GatewayReply(second));
                Check(controller.Snapshot().Gateway.Sent == 1 && delivered == 2, "current gateway reply was dropped");

                int beforeRemovalStarts = starts.Count, beforeRemovalStops = stops;
                found = new AdapterInfo { Id = id, Gateway = "" };
                clock += 2 * Stopwatch.Frequency;
                MonitorSnapshot removed = controller.Snapshot();
                Check(removed.Gateway.Target == "" && removed.Gateway.Sent == 0 && !removed.Gateway.CurrentMs.HasValue &&
                    starts.Count == beforeRemovalStarts && stops == beforeRemovalStops + 1 && controller.gatewaySampleHandler == null,
                    "gateway removal did not stop and clear only its probe");
                secondReceiver(GatewayReply(second));
                Check(delivered == 2, "removed gateway callback was logged");

                found = new AdapterInfo { Id = id, Gateway = first };
                clock += 2 * Stopwatch.Frequency;
                MonitorSnapshot returned = controller.Snapshot();
                Check(returned.Gateway.Target == first && returned.Gateway.Sent == 0 && starts.Count == 3,
                    "gateway return did not start a fresh epoch");
                firstReceiver(GatewayReply(first));
                Check(delivered == 2 && controller.Snapshot().Gateway.Sent == 0,
                    "same-address return accepted a callback from its previous epoch");
                Action<ProbeSample> returnedReceiver = controller.gatewaySampleHandler;
                returnedReceiver(GatewayReply(first));
                Check(delivered == 3 && controller.Snapshot().Gateway.Sent == 1, "returned gateway reply missing");
                int stableStarts = starts.Count, stableTransitions = transitions.Count;
                clock += 2 * Stopwatch.Frequency;
                controller.Snapshot();
                Check(starts.Count == stableStarts && transitions.Count == stableTransitions && controller.Snapshot().Gateway.Sent == 1,
                    "unchanged gateway discarded measurements or restarted its probe");

                throwLookup = true; clock += 2 * Stopwatch.Frequency;
                Check(controller.Snapshot().Gateway.Target == first && controller.Snapshot().Gateway.Sent == 1,
                    "gateway lookup exception was mistaken for gateway removal");
                throwLookup = false; found = null; clock += 2 * Stopwatch.Frequency;
                Check(controller.Snapshot().Gateway.Target == first, "missing enumeration entry was mistaken for confirmed gateway absence");
                found = new AdapterInfo { Id = "different-adapter", Gateway = second }; clock += 2 * Stopwatch.Frequency;
                Check(controller.Snapshot().Gateway.Target == first && starts.Count == stableStarts && transitions.Count == stableTransitions,
                    "gateway lookup switched adapters or reset an unknown result");
                int beforeThrottled = lookups;
                controller.Snapshot(); controller.Snapshot();
                Check(lookups == beforeThrottled, "gateway refresh ignored the monotonic two-second throttle");

                found = new AdapterInfo { Id = id, Gateway = second };
                duringLookup = delegate { controller.Stop(); }; clock += 2 * Stopwatch.Frequency;
                Check(!controller.Snapshot().Running && starts.Count == stableStarts && transitions.Count == stableTransitions,
                    "in-flight gateway lookup restarted the probe after Stop");
                controller.Start(0, id, first, "", false, false, true);
                returnedReceiver(GatewayReply(first));
                Check(controller.Snapshot().Gateway.Sent == 0 && delivered == 3, "previous session callback reached a restarted session");
                duringLookup = delegate { controller.Start(0, id, first, "", false, false, true); };
                clock += 2 * Stopwatch.Frequency;
                int beforeSessionReplacement = starts.Count;
                Check(controller.Snapshot().Gateway.Target == first && starts.Count == beforeSessionReplacement + 1,
                    "old gateway lookup modified a replacement session");

                controller.GatewayTargetChanged += delegate { controller.Stop(); };
                clock += 2 * Stopwatch.Frequency;
                int beforeSubscriberStop = starts.Count;
                Check(!controller.Snapshot().Running && starts.Count == beforeSubscriberStop,
                    "target-change subscriber Stop was followed by a new probe start");
            }
            int fixedLookups = 0;
            using (MonitorController fixedTarget = new MonitorController(delegate(string selectedId) {
                fixedLookups++; return new AdapterInfo { Id = selectedId, Gateway = second };
            }, delegate(string target) { }, delegate { }, delegate { return clock; }))
            {
                fixedTarget.Start(0, id, first, "", false, false);
                clock += 20 * Stopwatch.Frequency;
                Check(fixedTarget.Snapshot().Gateway.Target == first && fixedLookups == 0,
                    "legacy manual/loopback override unexpectedly followed adapter gateway changes");
            }
        }

        private static ProbeSample GatewayReply(string target)
        {
            return new ProbeSample { TimestampUtc = DateTime.UtcNow, Target = target, Countable = true, RoundtripMs = 3, Status = "synthetic reply" };
        }

        internal static void ProbeNetworkFailureSelfTest()
        {
            DateTime now = DateTime.UtcNow;
            ProbeWindow window = new ProbeWindow(); window.Clear("192.0.2.1");
            window.Add(new ProbeSample { TimestampUtc = now.AddSeconds(-1), Target = "192.0.2.1", Countable = true, RoundtripMs = 2, Status = "synthetic reply" });
            window.Add(new ProbeSample { TimestampUtc = now, Target = "192.0.2.1", NetworkFailure = true, Status = "synthetic destination unreachable" });
            ProbeSummary summary = window.Summary(now);
            Check(summary.Sent == 1 && summary.Missing == 0 && summary.MissingPercent == 0 && summary.Errors == 0 &&
                summary.NetworkFailures == 1 && summary.CurrentNetworkFailure && !summary.CurrentError && !summary.CurrentMs.HasValue,
                "network failure was hidden by historical zero timeout ratio or counted as a local error");
            summary = window.Summary(now.AddSeconds(3));
            Check(summary.Stale && !summary.CurrentNetworkFailure && !summary.CurrentError && !summary.CurrentMs.HasValue,
                "old network failure remained a current failure");
            summary = window.Summary(now.AddSeconds(61));
            Check(summary.NetworkFailures == 0 && summary.Errors == 0 && summary.Sent == 0 && summary.Stale,
                "network failure did not expire from the rolling window");
            window.Clear("192.0.2.2"); summary = window.Summary(now);
            Check(summary.NetworkFailures == 0 && !summary.CurrentNetworkFailure && !summary.Stale && summary.LastSampleUtc == null,
                "target reset retained network failure state");
        }
    }
}
