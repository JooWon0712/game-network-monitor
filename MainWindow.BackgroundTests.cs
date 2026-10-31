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
        public static void RunBackgroundTests(string reportPath)
        {
            Require(Application.Current != null && Application.Current.ShutdownMode == ShutdownMode.OnExplicitShutdown,
                "background test requires explicit application shutdown");
            string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath)), "background-fixture-" + Guid.NewGuid().ToString("N"));
            string key = "test-" + Guid.NewGuid().ToString("N");
            MainWindow window = null, shutdownWindow = null, fallbackWindow = null;
            using (SingleInstanceCoordinator owner = SingleInstanceCoordinator.AcquireOrSignal(key))
            {
                Require(owner.IsOwner, "isolated restore channel ownership");
                try
                {
                    window = CreateBackgroundTestWindow(Path.Combine(root, "recording"));
                    MainWindow current = window;
                    owner.AttachRestore(current.RestoreFromBackground); current.BackgroundClosing += owner.BeginShutdown; current.EnableBackgroundMode();
                    Require(!typeof(MainWindow).Assembly.GetReferencedAssemblies().Any(x => x.Name == "System.Windows.Forms"), "notification icon dependency remains");
                    current.gatewayOverride = current.referenceOverride = "127.0.0.1";
                    current.StartSession();
                    PumpBackgroundUntil(delegate { return current.running && current.sampleCount >= 2; }, 4000, "initial samples missing");
                    string session = current.lastDirectory;
                    string samplesPath = Path.Combine(session, "samples.csv");
                    PumpBackgroundUntil(delegate { return File.Exists(samplesPath) && ReadBackgroundRows(samplesPath) >= 3; }, 2500, "samples not flushed");
                    current.RecordInternetCheck(Fixture("dns", "timeout", DateTime.UtcNow)); Thread.Sleep(20);
                    current.RecordInternetCheck(Fixture("dns", "timeout", DateTime.UtcNow));
                    ConnectivityIncident open = current.history.GetRecent(100).FirstOrDefault(x => x.Kind == "dns" && x.State == "open");
                    Require(open != null, "open incident fixture missing");
                    long samplesBeforeHide = current.sampleCount; int rowsBeforeHide = ReadBackgroundRows(samplesPath);
                    current.Close();
                    Require(!current.IsVisible && current.running && !current.disposed && current.timer.IsEnabled, "X stopped or disposed the monitor");
                    PumpBackgroundUntil(delegate { return current.sampleCount > samplesBeforeHide && ReadBackgroundRows(samplesPath) > rowsBeforeHide; }, 4000, "hidden recording stopped");
                    Require(current.history.GetRecent(100).Any(x => x.Id == open.Id && x.State == "open" && !x.RecoveredUtc.HasValue), "hide changed incident state");

                    using (Process child = Process.Start(new ProcessStartInfo {
                        FileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PcInternetMonitor.exe"),
                        Arguments = "--single-instance-helper signal " + key,
                        UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory }))
                    {
                        PumpBackgroundUntil(delegate { return child.HasExited && current.IsVisible; }, 7000, "second process did not restore the existing window");
                        Require(child.ExitCode == 0, "second process created a duplicate owner or failed to signal");
                    }
                    Require(current.running && current.lastDirectory == session && current.sampleCount > samplesBeforeHide, "restore restarted or replaced the session");
                    current.WindowState = WindowState.Minimized;
                    Require(current.running && current.IsVisible, "minimize stopped measurement");
                    current.RestoreFromBackground(); Require(current.WindowState != WindowState.Minimized, "minimized restore failed");
                    current.exitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(current.disposed && !current.running && current.automaticLog == null && !current.IsVisible, "app exit left a background session");
                    RequireCompletedBackgroundSession(session);
                    Require(current.history.GetRecent(100).Any(x => x.Id == open.Id && x.State == "ended" && !x.RecoveredUtc.HasValue), "exit invented recovery");
                    current.RestoreFromBackground(); Require(!current.IsVisible, "late restore reopened disposed window");

                    shutdownWindow = CreateBackgroundTestWindow(Path.Combine(root, "shutdown"));
                    shutdownWindow.EnableBackgroundMode(); shutdownWindow.gatewayOverride = shutdownWindow.referenceOverride = "127.0.0.1";
                    shutdownWindow.StartSession(); string shutdownSession = shutdownWindow.lastDirectory;
                    shutdownWindow.Close(); Require(!shutdownWindow.IsVisible && shutdownWindow.running, "shutdown fixture did not hide");
                    shutdownWindow.AllowSystemShutdown(); shutdownWindow.Close();
                    Require(shutdownWindow.disposed && !shutdownWindow.running, "Windows ending was cancelled by hide mode");
                    RequireCompletedBackgroundSession(shutdownSession);
                    fallbackWindow = CreateBackgroundTestWindow(Path.Combine(root, "fallback"));
                    fallbackWindow.Close(); Require(fallbackWindow.disposed, "missing restore channel left an invisible app");
                    File.WriteAllText(reportPath, "PASS: no notification-icon dependency, hidden WPF dispatcher and disk recording continue, open history preserved, actual second process signals existing window without a new session, minimize/restore, app exit completes logs, simulated Windows ending bypasses hide, unavailable-channel close exits. Loopback ICMP and synthetic DNS only; no real logoff or long-run test.\r\n", new UTF8Encoding(true));
                }
                finally { CloseBackgroundTestWindow(window); CloseBackgroundTestWindow(shutdownWindow); CloseBackgroundTestWindow(fallbackWindow); }
            }
        }

        private static MainWindow CreateBackgroundTestWindow(string root)
        {
            MainWindow window = new MainWindow(null, root) { ShowActivated = false, ShowInTaskbar = false };
            window.Show(); return window;
        }

        private static void PumpBackgroundUntil(Func<bool> completed, int timeoutMs, string message)
        {
            Stopwatch elapsed = Stopwatch.StartNew(); DispatcherFrame frame = new DispatcherFrame(); Exception callbackError = null;
            DispatcherTimer pump = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            pump.Tick += delegate {
                try { if (completed() || elapsed.ElapsedMilliseconds >= timeoutMs) frame.Continue = false; }
                catch (Exception error) { callbackError = error; frame.Continue = false; }
            };
            try { pump.Start(); Dispatcher.PushFrame(frame); }
            finally { pump.Stop(); }
            if (callbackError != null) throw new InvalidOperationException("Background test callback failed", callbackError);
            Require(completed(), message);
        }

        private static int ReadBackgroundRows(string path)
        {
            using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new StreamReader(input, Encoding.UTF8, true))
            { int count = 0; while (reader.ReadLine() != null) count++; return count; }
        }

        private static void RequireCompletedBackgroundSession(string session)
        {
            Require(File.ReadAllLines(Path.Combine(session, "session.txt")).Any(x => x == "state=completed"), "writer not completed");
            Require(File.Exists(Path.Combine(session, "summary.txt")) && File.ReadAllLines(Path.Combine(session, "samples.csv")).Length >= 2, "summary or sample rows missing");
        }

        private static void CloseBackgroundTestWindow(MainWindow window)
        {
            if (window == null) return;
            window.appExitRequested = true;
            try { if (!window.disposed) window.Close(); }
            finally { window.DisposeMonitor(); }
        }
    }
}
