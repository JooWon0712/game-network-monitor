using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace PcInternetMonitor
{
    internal static class WindowCloseTests
    {
        internal static readonly string[] Cases = { "native-close", "exit-button", "minimize", "system-ending" };

        public static void Run(string testDirectory)
        {
            string id = Guid.NewGuid().ToString("N");
            string root = Path.Combine(testDirectory, "window-close-fixture-" + id);
            foreach (string scenario in Cases)
            {
                using (Process child = Process.Start(new ProcessStartInfo {
                    FileName = Assembly.GetExecutingAssembly().Location,
                    Arguments = "--window-close-helper " + scenario + " " + id,
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory }))
                {
                    if (!child.WaitForExit(20000))
                    {
                        // Only this isolated helper, never a user's running app.
                        child.Kill(); child.WaitForExit();
                        throw new InvalidOperationException("Window close helper did not exit: " + scenario);
                    }
                    string report = Path.Combine(root, scenario, "result.txt");
                    string text = File.Exists(report) ? File.ReadAllText(report) : "missing helper result";
                    if (child.ExitCode != 0 || !text.StartsWith("PASS", StringComparison.Ordinal))
                        throw new InvalidOperationException("Window close helper " + scenario + " exit=" + child.ExitCode + ": " + text);
                }
            }
            Directory.CreateDirectory(testDirectory);
            File.WriteAllText(Path.Combine(testDirectory, "window-close-test.txt"),
                "PASS v" + App.Version + ": four canonical EXE child processes exit normally after recording; native system close (shared by X/Alt+F4), exit button, minimize stops recording and restore needs explicit restart, simulated Windows ending. Writer completion, summary and recovery-unknown history checked. Loopback ICMP/synthetic DNS only; no physical keyboard, real logoff, privacy or security guarantee.\r\n", new UTF8Encoding(true));
        }
    }
}
