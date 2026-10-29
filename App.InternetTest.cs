using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace PcInternetMonitor
{
    public static partial class InternetLiveValidation
    {
        public static void Run(string testDirectory)
        {
            object gate = new object(); bool accepting = true; Dictionary<string, InternetCheckSample> received = new Dictionary<string, InternetCheckSample>();
            using (ManualResetEvent complete = new ManualResetEvent(false))
            using (InternetCheckRunner runner = new InternetCheckRunner())
            {
                runner.Sampled += delegate(InternetCheckSample sample) { lock (gate) { if (!accepting) return; received[sample.Kind] = sample.Copy(); if (received.Count == 2) complete.Set(); } };
                runner.Start("www.microsoft.com", "https://www.gstatic.com/generate_204"); complete.WaitOne(14000); lock (gate) accepting = false; runner.Stop();
                StringBuilder text = new StringBuilder("PC internet live checks; system DNS and public HTTPS endpoint only. No game, ETW, speed test or security changes.\r\n");
                lock (gate) foreach (string kind in new string[] { "dns", "https" }) { InternetCheckSample sample; if (!received.TryGetValue(kind, out sample)) text.AppendLine(kind + " NOT VALIDATED: no completed result"); else text.AppendLine(kind + " " + (sample.Success ? "PASS" : "NOT VALIDATED") + ": " + sample.State + "; target=" + sample.Target + "; duration_ms=" + sample.DurationMs + "; http=" + sample.HttpStatusCode + "; status=" + sample.Status + "; error=" + sample.Error); }
                if (runner.Snapshot(DateTime.UtcNow).Running) throw new InvalidOperationException("Internet worker did not stop");
                Directory.CreateDirectory(testDirectory); File.WriteAllText(Path.Combine(testDirectory, "internet-test.txt"), text.ToString(), new UTF8Encoding(true));
            }
        }
    }
}
