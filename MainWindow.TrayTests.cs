using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace PcInternetMonitor
{
    public sealed partial class MainWindow
    {
        public static void RunTrayTests(string reportPath)
        {
            Require(Application.Current != null && Application.Current.ShutdownMode == ShutdownMode.OnExplicitShutdown,
                "tray test requires explicit application shutdown");
            string testRoot = Path.GetDirectoryName(Path.GetFullPath(reportPath));
            string fixtureRoot = Path.Combine(testRoot, "tray-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtureRoot);
            MainWindow recordingWindow = null, shutdownWindow = null, fallbackWindow = null;
            try
            {
                recordingWindow = CreateTrayTestWindow(Path.Combine(fixtureRoot, "recording"));
                MainWindow current = recordingWindow;
                Require(current.trayReady && current.trayIcon != null && current.trayIcon.Visible,
                    "tray icon was not initialized");
                Require(!current.running && current.trayStartItem.Enabled && !current.trayStopItem.Enabled,
                    "initial tray command state");
                current.gatewayOverride = current.referenceOverride = "127.0.0.1";
                current.trayStartItem.PerformClick();
                PumpTrayUntil(delegate { return current.running && current.sampleCount >= 2; }, 3500,
                    "tray start did not collect PC samples");
                Require(!current.trayStartItem.Enabled && current.trayStopItem.Enabled,
                    "running tray command state");
                string session = current.lastDirectory;
                string samplesPath = Path.Combine(session, "samples.csv");
                PumpTrayUntil(delegate { return File.Exists(samplesPath) && ReadTrayRowCount(samplesPath) >= 3; }, 2500,
                    "initial PC samples did not reach disk");

                // An open synthetic failure must remain open while hidden and end,
                // without inventing a recovery, only when the user really exits.
                current.RecordInternetCheck(Fixture("dns", "timeout", DateTime.UtcNow));
                Thread.Sleep(2);
                current.RecordInternetCheck(Fixture("dns", "timeout", DateTime.UtcNow));
                ConnectivityIncident open = current.history.GetRecent(100).FirstOrDefault(x => x.Kind == "dns" && x.State == "open");
                Require(open != null && !open.RecoveredUtc.HasValue, "synthetic open incident missing");
                long samplesBeforeHide = current.sampleCount;
                int diskRowsBeforeHide = ReadTrayRowCount(samplesPath);
                current.Close();
                Require(!current.disposed && current.running && !current.IsVisible && current.timer.IsEnabled,
                    "X ended measurement instead of hiding the window");
                Require(current.trayReady && current.trayIcon != null && current.trayIcon.Visible,
                    "hidden window lost its tray icon");
                PumpTrayUntil(delegate {
                    return current.sampleCount > samplesBeforeHide && ReadTrayRowCount(samplesPath) > diskRowsBeforeHide;
                }, 3500, "hidden dispatcher did not continue recording");
                Require(current.history.GetRecent(100).Any(x => x.Id == open.Id && x.State == "open" && !x.RecoveredUtc.HasValue),
                    "hiding ended or recovered an open incident");

                current.trayOpenItem.PerformClick();
                PumpTrayUntil(delegate { return current.IsVisible && current.WindowState == WindowState.Normal; }, 1500,
                    "tray open did not restore the window");
                Require(current.running && !current.disposed, "restoring restarted or stopped measurement");
                current.trayExitItem.PerformClick();
                PumpTrayUntil(delegate { return current.disposed && !current.trayReady && current.trayIcon == null; }, 2000,
                    "tray exit did not close the monitor and dispose the icon");
                Require(!current.running && current.automaticLog == null && current.trayDisposed,
                    "tray exit left a session or tray resource active");
                RequireCompletedTraySession(session);
                ConnectivityIncident ended = current.history.GetRecent(100).FirstOrDefault(x => x.Id == open.Id);
                Require(ended != null && ended.State == "ended" && ended.ClosedUtc.HasValue &&
                    !ended.RecoveredUtc.HasValue && !ended.EndedUtc.HasValue,
                    "tray exit fabricated a recovered incident");
                string incidentCsv = File.ReadAllText(Path.Combine(session, "connectivity-incidents.csv"));
                Require(incidentCsv.Contains("," + SessionLog.Csv("ended") + ",") && !incidentCsv.Contains("," + SessionLog.Csv("recovered") + ","),
                    "tray exit history CSV did not preserve recovery unknown");

                shutdownWindow = CreateTrayTestWindow(Path.Combine(fixtureRoot, "shutdown"));
                current = shutdownWindow;
                current.Close();
                Require(!current.disposed && !current.IsVisible && !current.running,
                    "closing an idle window started measurement or exited");
                current.gatewayOverride = current.referenceOverride = "127.0.0.1";
                current.trayStartItem.PerformClick();
                PumpTrayUntil(delegate { return current.running && current.sampleCount >= 2; }, 3500,
                    "hidden tray start did not collect samples");
                Require(!current.IsVisible, "tray start unexpectedly opened the hidden window");
                string stoppedSession = current.lastDirectory;
                current.trayStopItem.PerformClick();
                PumpTrayUntil(delegate { return !current.running && current.automaticLog == null; }, 2000,
                    "hidden tray stop did not finish recording");
                Require(!current.IsVisible && current.trayReady && current.trayStartItem.Enabled && !current.trayStopItem.Enabled,
                    "hidden tray stop lost its tray or command state");
                RequireCompletedTraySession(stoppedSession);
                current.trayStartItem.PerformClick();
                Require(current.running && current.lastDirectory != stoppedSession,
                    "hidden tray restart did not create a new session");
                string shutdownSession = current.lastDirectory;
                current.sessionEnding = true;
                current.Close();
                Require(current.disposed && !current.running && !current.trayReady && current.trayIcon == null,
                    "system-session ending was cancelled into the tray");
                RequireCompletedTraySession(shutdownSession);

                fallbackWindow = CreateTrayTestWindow(Path.Combine(fixtureRoot, "fallback"));
                fallbackWindow.DisposeTray();
                Require(!fallbackWindow.trayReady, "fallback fixture still has a tray");
                fallbackWindow.Close();
                Require(fallbackWindow.disposed && !fallbackWindow.IsVisible,
                    "window without a tray was hidden instead of closed");

                File.WriteAllText(reportPath,
                    "PASS v" + AppVersion + ": actual WPF dispatcher and NotifyIcon lifecycle; tray start, X hides with continued loopback PC recording, open restores, tray exit flushes CSV/summary and preserves recovery unknown; idle close does not start measurement; hidden tray start/stop/restart; simulated Windows session ending and missing-tray fallback close normally; tray resources released. No external DNS/HTTPS, startup registration, UAC or actual Windows logoff in this test.\r\n",
                    new UTF8Encoding(true));
            }
            finally
            {
                CloseTrayTestWindow(recordingWindow);
                CloseTrayTestWindow(shutdownWindow);
                CloseTrayTestWindow(fallbackWindow);
            }
        }

        private static MainWindow CreateTrayTestWindow(string fixture)
        {
            MainWindow window = new MainWindow(null, fixture);
            try
            {
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.WindowState = WindowState.Minimized;
                window.InitializeTray();
                Require(window.trayReady, "tray unavailable in validation session");
                window.Show();
                return window;
            }
            catch
            {
                CloseTrayTestWindow(window);
                throw;
            }
        }

        private static void PumpTrayUntil(Func<bool> completed, int timeoutMilliseconds, string message)
        {
            if (completed()) return;
            DispatcherFrame frame = new DispatcherFrame();
            DispatcherTimer pump = new DispatcherTimer(DispatcherPriority.Background);
            Stopwatch elapsed = Stopwatch.StartNew();
            Exception callbackError = null;
            pump.Interval = TimeSpan.FromMilliseconds(30);
            pump.Tick += delegate {
                try { if (completed() || elapsed.ElapsedMilliseconds >= timeoutMilliseconds) frame.Continue = false; }
                catch (Exception error) { callbackError = error; frame.Continue = false; }
            };
            try { pump.Start(); Dispatcher.PushFrame(frame); }
            finally { pump.Stop(); }
            if (callbackError != null) throw new InvalidOperationException("Tray test callback failed", callbackError);
            Require(completed(), message);
        }

        private static int ReadTrayRowCount(string path)
        {
            // The writer remains open during the background test. Readers must
            // allow its existing write access rather than acquire an exclusive share.
            using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new StreamReader(input, Encoding.UTF8, true))
            {
                int count = 0;
                while (reader.ReadLine() != null) count++;
                return count;
            }
        }
        private static void RequireCompletedTraySession(string session)
        {
            Require(File.ReadAllLines(Path.Combine(session, "session.txt")).Any(x => x == "state=completed"),
                "tray session writer did not complete");
            Require(File.Exists(Path.Combine(session, "summary.txt")) && File.ReadAllLines(Path.Combine(session, "samples.csv")).Length >= 2,
                "tray session summary or sample rows missing");
        }

        private static void CloseTrayTestWindow(MainWindow window)
        {
            if (window == null) return;
            window.trayExitRequested = true;
            window.sessionEnding = true;
            try { if (!window.disposed) window.Close(); }
            catch (Exception error) { DiagnosticsLog.Write("tray_test_cleanup_close_failed", "owned validation window", error); }
            finally
            {
                try { window.DisposeTray(); }
                finally { window.DisposeMonitor(); }
            }
        }
    }
}