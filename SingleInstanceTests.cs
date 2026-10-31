using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace PcInternetMonitor
{
    internal static class SingleInstanceTests
    {
        public static void Run(string testDirectory)
        {
            string key = "test-" + Guid.NewGuid().ToString("N");
            string otherKey = "test-" + Guid.NewGuid().ToString("N");
            using (ManualResetEvent restored = new ManualResetEvent(false))
            {
                int callbacks = 0;
                using (SingleInstanceCoordinator owner = SingleInstanceCoordinator.AcquireOrSignal(key))
                {
                    Require(owner.IsOwner && !owner.RestoreSignalled && owner.ErrorMessage.Length == 0, "initial ownership");
                    RunChild("signal", key); // A request before UI attachment must remain pending.
                    owner.AttachRestore(delegate { Interlocked.Increment(ref callbacks); restored.Set(); });
                    Require(restored.WaitOne(3000) && callbacks == 1, "pending startup restore request");
                    for (int index = 0; index < 3; index++)
                    {
                        restored.Reset();
                        RunChild("signal", key);
                        Require(restored.WaitOne(3000) && callbacks == index + 2, "repeated cooperating-process restore");
                    }
                    RunChild("acquire", otherKey);
                    Require(callbacks == 4, "independent test identity affected the owner");
                    owner.BeginShutdown();
                    RunChild("closing", key);
                    Require(callbacks == 4, "closing owner received a restore callback");
                }
                RunChild("acquire", key);
            }

            string takeoverKey = "test-" + Guid.NewGuid().ToString("N");
            using (SingleInstanceCoordinator closingOwner = SingleInstanceCoordinator.AcquireOrSignal(takeoverKey))
            {
                Require(closingOwner.IsOwner, "takeover fixture ownership");
                closingOwner.BeginShutdown();
                using (Process replacement = StartChild("acquire", takeoverKey))
                {
                    Thread.Sleep(150);
                    closingOwner.Dispose();
                    RequireChildExit(replacement, "acquire after announced shutdown");
                }
            }

            string abandonedKey = "test-" + Guid.NewGuid().ToString("N");
            RunChild("abandon", abandonedKey);
            using (SingleInstanceCoordinator replacement = SingleInstanceCoordinator.AcquireOrSignal(abandonedKey))
                Require(replacement.IsOwner && replacement.ErrorMessage.Length == 0, "ownership after an abrupt helper exit");

            Directory.CreateDirectory(testDirectory);
            File.WriteAllText(Path.Combine(testDirectory, "single-instance-test.txt"),
                "PASS v" + App.Version + ": canonical EXE cooperating processes; one owner, pending and repeated restore delivery, isolated identities, announced shutdown rejects restore and permits takeover after release, ownership after abrupt helper exit. Test-only named objects; no user window, logs, network, startup registration or UAC.\r\n", new UTF8Encoding(true));
        }

        public static int RunHelper(string[] args)
        {
            if (args.Length != 3 || (args[1] != "signal" && args[1] != "acquire" && args[1] != "abandon" && args[1] != "closing")) return 3;
            SingleInstanceCoordinator coordinator = SingleInstanceCoordinator.AcquireOrSignal(args[2]);
            if (args[1] == "abandon")
            {
                // Deliberately leave the helper-owned mutex until this test-only
                // process exits, exercising OS release without orderly disposal.
                return coordinator.IsOwner ? 0 : 2;
            }
            using (coordinator)
                return args[1] == "closing" ? (!coordinator.IsOwner && !coordinator.RestoreSignalled && coordinator.ErrorMessage.Length > 0 ? 0 : 2) : args[1] == "signal" ? (!coordinator.IsOwner && coordinator.RestoreSignalled ? 0 : 2) : (coordinator.IsOwner ? 0 : 2);
        }

        private static void RunChild(string command, string key)
        {
            using (Process child = StartChild(command, key)) RequireChildExit(child, command);
        }

        private static Process StartChild(string command, string key)
        {
            return Process.Start(new ProcessStartInfo {
                FileName = Assembly.GetExecutingAssembly().Location,
                Arguments = "--single-instance-helper " + command + " " + key,
                UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            });
        }

        private static void RequireChildExit(Process child, string command)
        {
            if (!child.WaitForExit(7000))
            {
                // This exact child is an isolated, measurement-free test
                // helper created above. Never touch other app processes.
                child.Kill(); child.WaitForExit();
                throw new InvalidOperationException("Single-instance helper did not finish.");
            }
            Require(child.ExitCode == 0, "cooperating helper " + command + " exit=" + child.ExitCode);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Single-instance regression: " + message);
        }
    }
}
