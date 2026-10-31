using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PcInternetMonitor
{
    public sealed partial class MainWindow
    {
        public static int RunWindowCloseHelper(Application app, string[] args)
        {
            Guid id;
            if ((args.Length != 3 && args.Length != 4) || !WindowCloseTests.Cases.Contains(args[1]) || !Guid.TryParseExact(args[2], "N", out id)) return 3;
            Require(app.ShutdownMode == ShutdownMode.OnMainWindowClose, "close helper must use normal application shutdown");
            string scenario = args[1];
            string root = Path.Combine(App.TestDirectory, "window-close-fixture-" + args[2], scenario);
            Directory.CreateDirectory(root);
            MainWindow window = new MainWindow(null, Path.Combine(root, "records")) { ShowActivated = false, ShowInTaskbar = false };
            window.gatewayOverride = window.referenceOverride = "127.0.0.1";
            DispatcherTimer steps = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            Stopwatch deadline = Stopwatch.StartNew(), phaseTime = Stopwatch.StartNew();
            Exception failure = null; int phase = 0; string firstSession = null, openId = null; long stoppedSamples = 0;
            window.Loaded += delegate { window.StartSession(); steps.Start(); };
            window.Closed += delegate {
                steps.Stop();
                try
                {
                    Require(window.disposed && !window.running && window.automaticLog == null && !window.timer.IsEnabled && !window.IsVisible, "window close left the monitor active");
                    RequireCompletedWindowSession(window.lastDirectory);
                    Require(window.history.GetRecent(100).Any(x => x.Id == openId && x.State == "ended" && !x.RecoveredUtc.HasValue), "close/minimize invented recovery or lost history");
                    Require(!window.fault, "close or minimize failed recording cleanup");
                }
                catch (Exception error) { failure = failure ?? error; }
            };
            steps.Tick += delegate {
                try
                {
                    if (deadline.ElapsedMilliseconds > 12000) throw new InvalidOperationException("close helper timed out");
                    if (phase == 0)
                    {
                        if (window.sampleCount < 2) return;
                        firstSession = window.lastDirectory;
                        window.RecordInternetCheck(Fixture("dns", "timeout", DateTime.UtcNow)); Thread.Sleep(20);
                        window.RecordInternetCheck(Fixture("dns", "timeout", DateTime.UtcNow));
                        ConnectivityIncident open = window.history.GetRecent(100).FirstOrDefault(x => x.Kind == "dns" && x.State == "open");
                        Require(open != null, "close fixture needs an open incident"); openId = open.Id;
                        if (scenario == "minimize")
                        {
                            window.WindowState = WindowState.Minimized;
                            Require(!window.running && !window.timer.IsEnabled && window.automaticLog == null && !window.disposed, "minimize did not stop recording");
                            RequireCompletedWindowSession(firstSession); stoppedSamples = window.sampleCount;
                            window.StartSession(); Require(!window.running && window.lastDirectory == firstSession, "minimized start was allowed");
                            phase = 1; phaseTime.Restart(); return;
                        }
                        steps.Stop();
                        if (scenario == "exit-button") window.exitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        else { if (scenario == "system-ending") window.AllowSystemShutdown(); SystemCommands.CloseWindow(window); }
                    }
                    else if (phase == 1 && phaseTime.ElapsedMilliseconds >= 1100)
                    {
                        Require(!window.running && window.sampleCount == stoppedSamples, "minimized recording continued");
                        window.WindowState = WindowState.Normal; phase = 2; phaseTime.Restart();
                    }
                    else if (phase == 2 && phaseTime.ElapsedMilliseconds >= 500)
                    {
                        Require(!window.running && window.sampleCount == stoppedSamples, "restore restarted measurement");
                        window.StartSession(); Require(window.running && window.lastDirectory != firstSession, "explicit restart failed"); phase = 3;
                    }
                    else if (phase == 3 && window.sampleCount >= 2) { steps.Stop(); SystemCommands.CloseWindow(window); }
                }
                catch (Exception error) { failure = error; steps.Stop(); window.RequestAppExit(); }
            };
            int result;
            try { result = app.Run(window); }
            finally { steps.Stop(); window.DisposeMonitor(); }
            File.WriteAllText(Path.Combine(root, "result.txt"), failure == null ? "PASS: " + scenario + "; app.Run returned; recording completed.\r\n" : "FAIL: " + failure, new UTF8Encoding(true));
            return failure == null ? result : 1;
        }

        private static void RequireCompletedWindowSession(string session)
        {
            Require(!String.IsNullOrEmpty(session) && File.ReadAllLines(Path.Combine(session, "session.txt")).Contains("state=completed"), "session writer not completed");
            Require(File.Exists(Path.Combine(session, "summary.txt")) && File.ReadAllLines(Path.Combine(session, "samples.csv")).Length >= 2, "summary or samples missing");
        }

        private static void PumpWindowTestUntil(Func<bool> completed, int timeoutMs, string message)
        {
            Stopwatch elapsed = Stopwatch.StartNew(); DispatcherFrame frame = new DispatcherFrame(); Exception failure = null;
            DispatcherTimer pump = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            pump.Tick += delegate { try { if (completed() || elapsed.ElapsedMilliseconds >= timeoutMs) frame.Continue = false; } catch (Exception error) { failure = error; frame.Continue = false; } };
            try { pump.Start(); Dispatcher.PushFrame(frame); } finally { pump.Stop(); }
            if (failure != null) throw new InvalidOperationException("UI test callback failed", failure);
            Require(completed(), message);
        }
    }
}
