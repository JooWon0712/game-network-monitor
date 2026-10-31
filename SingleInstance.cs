using System;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace PcInternetMonitor
{
    // Own and dispose this object on the STA entry thread: Windows mutex ownership
    // belongs to that thread. Restore callbacks must queue work, never block on UI.
    public sealed class SingleInstanceCoordinator : IDisposable
    {
        private const int StartupWaitMilliseconds = 3000;
        private readonly object gate = new object();
        private readonly int acquiringThread;
        private Mutex instanceMutex;
        private EventWaitHandle restoreEvent;
        private EventWaitHandle closingEvent;
        private ManualResetEvent stopEvent;
        private Thread listener;
        private Action restore;
        private bool ownsMutex, disposed, shuttingDown;

        public bool IsOwner { get; private set; }
        public bool RestoreSignalled { get; private set; }
        public string ErrorMessage { get; private set; }

        private SingleInstanceCoordinator() { acquiringThread = Thread.CurrentThread.ManagedThreadId; ErrorMessage = ""; }

        public static SingleInstanceCoordinator AcquireOrSignal()
        {
            string identity;
            try
            {
                using (WindowsIdentity current = WindowsIdentity.GetCurrent())
                    identity = "user=" + current.User.Value + ";session=" + Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception error) { return Failed("사용자 실행 범위를 확인하지 못했습니다.", error); }
            return AcquireIdentity(identity);
        }

        internal static SingleInstanceCoordinator AcquireOrSignal(string uniqueKey)
        {
            Guid parsed;
            if (uniqueKey == null || !uniqueKey.StartsWith("test-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(uniqueKey.Substring(5), "N", out parsed))
                throw new ArgumentException("An isolated test GUID is required.", "uniqueKey");
            return AcquireIdentity("isolated-test=" + uniqueKey);
        }

        private static SingleInstanceCoordinator AcquireIdentity(string identity)
        {
            SingleInstanceCoordinator result = new SingleInstanceCoordinator();
            string name;
            using (SHA256 hash = SHA256.Create())
                name = "Local\\PcInternetMonitor.Instance.v1." + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "");
            try
            {
                result.instanceMutex = new Mutex(false, name + ".Owner");
                Stopwatch elapsed = Stopwatch.StartNew();
                bool sawClosing = false;
                do
                {
                    // If startup failed or the previous process exited, take over
                    // instead of requiring an obsolete readiness event to appear.
                    if (result.TryAcquireMutex())
                    {
                        result.closingEvent = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".Closing");
                        // A previous owner's handles can briefly outlive mutex
                        // release. Only the new owner clears its closing marker.
                        result.closingEvent.Reset();
                        result.restoreEvent = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Restore");
                        result.stopEvent = new ManualResetEvent(false);
                        result.IsOwner = true;
                        return result;
                    }
                    try
                    {
                        using (EventWaitHandle closing = EventWaitHandle.OpenExisting(name + ".Closing"))
                        {
                            if (closing.WaitOne(0)) sawClosing = true;
                            else
                            {
                                using (EventWaitHandle existing = EventWaitHandle.OpenExisting(name + ".Restore"))
                                {
                                    existing.Set();
                                    if (!closing.WaitOne(0))
                                    {
                                        result.RestoreSignalled = true;
                                        return result;
                                    }
                                    sawClosing = true;
                                }
                            }
                        }
                    }
                    catch (WaitHandleCannotBeOpenedException) { /* The owner is still preparing its event. */ }
                    Thread.Sleep(25);
                } while (elapsed.ElapsedMilliseconds < StartupWaitMilliseconds);
                result.ErrorMessage = sawClosing ? "프로그램을 종료하는 중입니다. 종료를 마친 뒤 다시 실행해 주세요." : "실행 중인 프로그램의 창을 열 준비가 끝나지 않았습니다. 잠시 후 다시 실행해 주세요.";
            }
            catch (Exception error)
            {
                result.ErrorMessage = "실행 중인 프로그램과 연결하지 못했습니다. " + error.Message;
            }
            result.Dispose();
            return result;
        }

        private bool TryAcquireMutex()
        {
            try { ownsMutex = instanceMutex.WaitOne(0); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            return ownsMutex;
        }

        private static SingleInstanceCoordinator Failed(string message, Exception error)
        {
            SingleInstanceCoordinator result = new SingleInstanceCoordinator();
            result.ErrorMessage = message + " " + error.Message;
            return result;
        }

        public void AttachRestore(Action restoreRequest)
        {
            if (restoreRequest == null) throw new ArgumentNullException("restoreRequest");
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("SingleInstanceCoordinator");
                if (!IsOwner) throw new InvalidOperationException("Only the primary instance can receive restore requests.");
                if (shuttingDown) throw new InvalidOperationException("The primary instance is shutting down.");
                if (listener != null) throw new InvalidOperationException("A restore receiver is already attached.");
                restore = restoreRequest;
                listener = new Thread(Listen) { IsBackground = true, Name = "PC internet monitor window restore" };
                try { listener.Start(); }
                catch { listener = null; restore = null; throw; }
            }
        }

        private void Listen()
        {
            try
            {
                WaitHandle[] waits = { stopEvent, restoreEvent };
                while (WaitHandle.WaitAny(waits) == 1)
                {
                    Action request;
                    lock (gate) { if (disposed) return; if (shuttingDown) continue; request = restore; }
                    try { request(); }
                    catch (Exception error) { DiagnosticsLog.Write("instance_restore_dispatch_failed", "existing window restore request", error); }
                }
            }
            catch (Exception error) { DiagnosticsLog.Write("instance_restore_listener_failed", "window restore receiver", error); }
            finally { DisposeEvents(); }
        }

        public void Dispose()
        {
            Thread activeListener;
            lock (gate)
            {
                if (disposed) return;
                if (ownsMutex && acquiringThread != Thread.CurrentThread.ManagedThreadId)
                    throw new InvalidOperationException("Dispose the instance coordinator on its acquiring thread.");
                if (IsOwner) BeginShutdownLocked();
                disposed = true;
                activeListener = listener;
                if (stopEvent != null) stopEvent.Set();
            }
            // The callback only queues a dispatcher action. If a caller violates
            // that contract, the background receiver owns eventual event cleanup.
            if (activeListener == null) DisposeEvents();
            else if (activeListener != Thread.CurrentThread) activeListener.Join(1000);
            if (ownsMutex) { instanceMutex.ReleaseMutex(); ownsMutex = false; }
            if (instanceMutex != null) { instanceMutex.Dispose(); instanceMutex = null; }
        }

        public void BeginShutdown()
        {
            lock (gate)
            {
                if (disposed) return;
                if (!IsOwner) throw new InvalidOperationException("Only the primary instance can announce shutdown.");
                BeginShutdownLocked();
            }
        }

        private void BeginShutdownLocked()
        {
            shuttingDown = true;
            if (closingEvent != null) closingEvent.Set();
        }

        private void DisposeEvents()
        {
            lock (gate)
            {
                if (restoreEvent != null) { restoreEvent.Dispose(); restoreEvent = null; }
                if (closingEvent != null) { closingEvent.Dispose(); closingEvent = null; }
                if (stopEvent != null) { stopEvent.Dispose(); stopEvent = null; }
                restore = null;
            }
        }
    }
}
