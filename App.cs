using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace PcInternetMonitor
{
    public static class App
    {
        public const string Version = "0.2.8";
        private static string exitContext = "명시적 종료 요청 미확인";
        private static volatile bool unhandledExceptionSeen;
        private static TestRunScope testRun;
        internal static string TestDirectory { get { return testRun.DirectoryPath; } }
        internal static string TestRunId { get { return testRun.Id; } }

        [STAThread]
        public static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0] : "";
            try { if (IsTestMode(mode)) testRun = mode == "--window-close-helper" && args.Length == 4 ? TestRunScope.Borrow(args[3]) : TestRunScope.Create(); }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            string diagnosticRoot = testRun == null ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostics") : Path.Combine(TestDirectory, "diagnostics");
            DiagnosticsLog.Initialize(diagnosticRoot, Version, mode.Length == 0 ? "normal" : mode);
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) {
                unhandledExceptionSeen = true;
                DiagnosticsLog.Write("domain_unhandled_exception", "is_terminating=" + e.IsTerminating, e.ExceptionObject as Exception);
            };
            TaskScheduler.UnobservedTaskException += delegate(object sender, UnobservedTaskExceptionEventArgs e) {
                DiagnosticsLog.Write("task_unobserved_exception", "observed=" + e.Observed, e.Exception);
                // Preserve the runtime's exception policy; do not mark it observed here.
            };
            AppDomain.CurrentDomain.ProcessExit += delegate {
                if (testRun != null) return; // Test diagnostics are closed before their owned files are removed.
                DiagnosticsLog.Write("process_exit", "exit_code=" + Environment.ExitCode + "; context=" + exitContext + "; unhandled_exception_seen=" + unhandledExceptionSeen);
                DiagnosticsLog.Shutdown("process_exit observed; this event alone does not prove a normal exit");
            };
            int result = 1;
            try { result = Run(args); }
            catch (Exception error) {
                unhandledExceptionSeen = true;
                DiagnosticsLog.Write("entry_failure", "main entry failed", error);
                if (!IsTestMode(mode)) MessageBox.Show("프로그램 오류로 종료합니다.\n" + error.Message + "\n진단 기록: " + DiagnosticsLog.FilePath, "PC 인터넷 모니터");
                else Console.Error.WriteLine(error);
            }
            finally
            {
                DiagnosticsLog.Write("main_return", "exit_code=" + result + "; context=" + exitContext + "; unhandled_exception_seen=" + unhandledExceptionSeen);
                if (testRun != null)
                {
                    DiagnosticsLog.Shutdown("test completed; removing this run's temporary files");
                    try { testRun.Dispose(); }
                    catch (Exception error) { Console.Error.WriteLine("Test temporary file cleanup failed: " + error); result = 2; }
                }
            }
            return result;
        }

        public static void NoteExitRequest(string context)
        {
            exitContext = context;
            DiagnosticsLog.Write("exit_request", context);
        }

        private static bool IsTestMode(string mode)
        {
            return mode == "--window-close-test" || mode == "--window-close-helper" || mode == "--feedback-test" || mode == "--self-test" || mode == "--smoke-test" || mode == "--recording-test" || mode == "--probe-test" || mode == "--lifecycle-test" || mode == "--tcp-rtt-test" || mode == "--internet-test" || mode == "--internet-loopback-test" || mode == "--internet-recording-test";
        }

        private static int Run(string[] args)
        {
            string mode = args.Length > 0 ? args[0] : "";
            string testDir = testRun == null ? null : TestDirectory;
            try
            {
                if (mode == "--window-close-test") { WindowCloseTests.Run(testDir); return 0; }
                if (mode == "--self-test")
                {
                    TestRunScope.SelfTest(); KernelNetworkMonitor.SelfTest(); ProbeRunner.SelfTest(); MonitorController.SelfTest(); StartupSelection.SelfTest();
                    AutomaticSessionLog.SelfTest(testDir); SessionAnalysis.SelfTest(); DiagnosticsLog.SelfTest(testDir); ProgramUsageTotals.SelfTest();
                    ExportTest(testDir);
                    AdapterCounterTotals.SelfTest(); MeasurementQuality.SelfTest();
                    FinalSnapshotTests.SelfTest(testDir);
                    GameCheckPresentation.SelfTest(); TcpRttMonitor.SelfTest(); TcpRttLoggingTests.SelfTest(testDir); TcpRttPresentation.SelfTest();
                    InternetCheckRunner.SelfTest(); ConnectivityHistory.SelfTest(); InternetLoggingTests.SelfTest(testDir); SessionClockGuard.SelfTest(); ClockGuardTests.SelfTest(testDir); GatewayRefreshTests.SelfTest();
                    Directory.CreateDirectory(testDir);
                    File.WriteAllText(Path.Combine(testDir, "self-test.txt"), "PASS v" + Version + ": DNS/HTTPS timeout/cancel/generation/freshness, connectivity failure/recovery/gap/history, CSV dedup/flush/archive/PC summary, retained ETW/probe/adapter/TCP RTT synthetic regression checks. No live network or performance validation in this test.\r\n", Encoding.UTF8);
                    return 0;
                }
                if (mode == "--probe-test") { ProbeTest(testDir); return 0; }
                if (mode == "--tcp-rtt-test") { Directory.CreateDirectory(testDir); TcpRttMonitor.LiveLoopbackTest(Path.Combine(testDir, "tcp-rtt-test.txt")); return 0; }
                if (mode == "--internet-test") { InternetLiveValidation.Run(testDir); return 0; }
                if (mode == "--internet-loopback-test") { Directory.CreateDirectory(testDir); InternetCheckRunner.LiveLoopbackTest(Path.Combine(testDir, "internet-loopback-test.txt")); return 0; }
                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) {
                    unhandledExceptionSeen = true;
                    DiagnosticsLog.Write("dispatcher_unhandled_exception", "handled=" + e.Handled, e.Exception);
                    // Do not set Handled=true or continue after an unknown UI failure.
                };
                app.SessionEnding += delegate(object sender, SessionEndingCancelEventArgs e) { foreach (Window owned in app.Windows) { MainWindow monitor = owned as MainWindow; if (monitor != null) monitor.AllowSystemShutdown(); } NoteExitRequest("Windows 종료 요청: " + e.ReasonSessionEnding); };
                app.Exit += delegate(object sender, ExitEventArgs e) {
                    DiagnosticsLog.Write("application_exit", "exit_code=" + e.ApplicationExitCode + "; context=" + exitContext + "; unhandled_exception_seen=" + unhandledExceptionSeen);
                };
                if (mode == "--feedback-test")
                {
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    try { MainWindow.RunFeedbackTests(Path.Combine(testDir, "feedback-test.txt")); }
                    finally { app.Shutdown(); }
                    return 0;
                }
                if (mode == "--window-close-helper") return MainWindow.RunWindowCloseHelper(app, args);
                MainWindow window = IsTestMode(mode) ? new MainWindow(null, Path.Combine(testDir, "internet-ui-fixture-" + Guid.NewGuid().ToString("N"))) : new MainWindow(StartupSelection.FromArgs(args));
                if (mode == "--internet-recording-test") { window.InternetRecordingTest(Path.Combine(testDir, "internet-recording-test.txt")); window.Close(); app.Shutdown(); return 0; }
                if (mode == "--lifecycle-test")
                {
                    // Enter the actual WPF dispatcher, then close only this idle test window.
                    // Do not activate it, start measurement, send probes or request UAC.
                    window.ShowActivated = false; window.ShowInTaskbar = false; window.WindowState = WindowState.Minimized;
                    app.Startup += delegate { app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(delegate { window.Close(); })); };
                    return app.Run(window);
                }
                if (mode == "--recording-test")
                {
                    Directory.CreateDirectory(testDir);
                    window.RecordingTest(Path.Combine(testDir, "recording-test.txt"));
                    window.Close(); app.Shutdown(); return 0;
                }
                if (mode == "--smoke-test")
                {
                    Directory.CreateDirectory(testDir);
                    window.SmokeTest(Path.Combine(testDir, "dashboard.png"));
                    window.Close();
                    File.WriteAllText(Path.Combine(testDir, "smoke-test.txt"), "PASS v" + Version + ": PC internet dashboard, four checks, DNS/HTTPS success/failure/stale/stopped, persistent history merge, fixed connection controls and scrollable history. Synthetic UI only, no network or UAC.\r\n", Encoding.UTF8);
                    app.Shutdown(); return 0;
                }
                return app.Run(window);
            }
            catch (Exception e)
            {
                unhandledExceptionSeen = true;
                DiagnosticsLog.Write("main_exception", "application startup or dispatch failed", e);
                try { if (testDir != null) { Directory.CreateDirectory(testDir); File.WriteAllText(Path.Combine(testDir, "last-error.txt"), e.ToString(), Encoding.UTF8); } }
                catch (Exception writeError) { DiagnosticsLog.Write("legacy_error_write_failed", "could not write test error file", writeError); }
                if (!IsTestMode(mode)) MessageBox.Show("프로그램 오류로 종료합니다.\n" + e.Message + "\n진단 기록: " + DiagnosticsLog.FilePath, "PC 인터넷 모니터");
                else Console.Error.WriteLine(e);
                return 1;
            }
        }

        private static void ExportTest(string testDir)
        {
            Directory.CreateDirectory(testDir);
            string folder = Path.Combine(testDir, "export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "session.csv");
            SessionLog log = new SessionLog();
            log.Add(new MonitorSnapshot { TimestampUtc = DateTime.UtcNow, ProcessName = "=CSV,\"test\"", GameStatus = "테스트", Trace = new TraceHealth { Status = "권한 없음", Active = false } });
            log.Mark(DateTime.UtcNow, "방금 끊김"); log.Export(path);
            string csv = File.ReadAllText(path);
            if (!csv.Contains("'" + "=CSV," + "\"\"test\"\"") || !csv.Contains("false,,,,")) throw new InvalidOperationException("CSV quoting or unavailable metrics failed");
            if (!File.ReadAllText(Path.Combine(folder, "session.marks.csv")).Contains("방금 끊김")) throw new InvalidOperationException("CSV marks missing");
            // Delete only this run's exact temporary fixture files; no recursive directory operations.
            File.Delete(path); File.Delete(Path.Combine(folder, "session.marks.csv")); Directory.Delete(folder);
        }

        private static void ProbeTest(string testDir)
        {
            Directory.CreateDirectory(testDir);
            StringBuilder report = new StringBuilder();
            report.AppendLine("Local-only integration check; no game launched, no router configuration changed.");
            int responses = 0, errors = 0;
            using (ProbeRunner probe = new ProbeRunner())
            {
                probe.Sampled += delegate(ProbeSample s) { if (s.RoundtripMs.HasValue) Interlocked.Increment(ref responses); else Interlocked.Increment(ref errors); };
                probe.Start("127.0.0.1"); Thread.Sleep(3300); probe.Stop();
            }
            report.AppendLine("Loopback ICMP responses=" + responses + ", errors=" + errors);
            if (responses == 0) throw new InvalidOperationException("Loopback ICMP did not produce any successful samples");
            string adapterId = AdapterCatalog.GuessDefaultAdapterId();
            AdapterReader reader = new AdapterReader(adapterId);
            AdapterSample before = reader.Read(); Thread.Sleep(100); AdapterSample after = reader.Read();
            report.AppendLine("Adapter counters available=" + after.Available + ", monotonic=" + (after.ReceivedBytes >= before.ReceivedBytes && after.SentBytes >= before.SentBytes));
            if (!after.Available) throw new InvalidOperationException("Adapter counter reader unavailable");
            using (KernelNetworkMonitor trace = new KernelNetworkMonitor())
            {
                int sent = 0, received = 0;
                trace.EventReceived += delegate(TrafficEvent e) { if (e.IsRetransmit) return; if (e.IsSend) Interlocked.Increment(ref sent); else Interlocked.Increment(ref received); };
                trace.Start(Process.GetCurrentProcess().Id);
                TraceHealth first = trace.GetHealth();
                if (first.Active)
                {
                    // Controlled localhost traffic generated by this test process only.
                    using (UdpClient receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                    using (UdpClient sender = new UdpClient())
                    {
                        receiver.Client.ReceiveTimeout = 1500;
                        IPEndPoint endpoint = (IPEndPoint)receiver.Client.LocalEndPoint;
                        for (int i = 0; i < 10; i++)
                        {
                            byte[] data = Encoding.ASCII.GetBytes("PcInternetMonitor local fixture");
                            sender.Send(data, data.Length, endpoint);
                            IPEndPoint peer = null; receiver.Receive(ref peer); Thread.Sleep(30);
                        }
                    }
                    Thread.Sleep(1800);
                }
                TraceHealth health = trace.GetHealth();
                report.AppendLine("ETW active=" + health.Active + ", sentEvents=" + sent + ", receivedEvents=" + received);
                report.AppendLine("ETW status=" + health.Status);
                report.AppendLine("ETW collection eventsLost=" + health.EventsLost + ", buffersLost=" + health.BuffersLost);
                trace.Stop(); report.AppendLine("ETW stopped=" + !trace.GetHealth().Active);
                if (first.Active && sent + received == 0) throw new InvalidOperationException("ETW started but controlled local traffic produced no decoded events");
            }
            using (MonitorController controller = new MonitorController())
            {
                controller.Start(Process.GetCurrentProcess().Id, adapterId, "127.000.000.001", "", true);
                Thread.Sleep(1500);
                MonitorSnapshot sample = controller.Snapshot();
                if (sample.Gateway.Sent < 1 || sample.Gateway.Target != "127.0.0.1") throw new InvalidOperationException("Controller canonical probe integration failed");
                if (!sample.Trace.Active && sample.GameRatesAvailable) throw new InvalidOperationException("Unavailable game tracing was reported as available");
                report.AppendLine("Controller canonical loopback samples=" + sample.Gateway.Sent + ", gameRatesAvailable=" + sample.GameRatesAvailable);
                controller.Stop();
                if (controller.Snapshot().Running) throw new InvalidOperationException("Controller did not stop");
            }
            File.WriteAllText(Path.Combine(testDir, "probe-test.txt"), report.ToString(), Encoding.UTF8);
        }
    }
}
