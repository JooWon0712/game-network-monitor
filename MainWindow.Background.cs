using System;
using System.Windows;
using System.Windows.Threading;

namespace PcInternetMonitor
{
    public sealed partial class MainWindow
    {
        private bool backgroundReady, appExitRequested;
        private WindowState visibleWindowState = WindowState.Normal;
        internal event Action BackgroundClosing;

        private void NotifyBackgroundClosing()
        {
            Action handler = BackgroundClosing;
            try { if (handler != null) handler(); }
            catch (Exception error) { DiagnosticsLog.Write("instance_shutdown_notice_failed", "normal recording cleanup continues", error); }
        }

        internal void EnableBackgroundMode()
        {
            // Enable only after the single-instance restore listener is available.
            backgroundReady = true;
        }

        private bool HideToBackground()
        {
            if (!backgroundReady || disposed || appExitRequested || !Dispatcher.CheckAccess()) return false;
            try
            {
                if (WindowState != WindowState.Minimized) visibleWindowState = WindowState;
                Hide();
                DiagnosticsLog.Write("window_hidden_to_background", "recording=" + running + "; notification_icon=False");
                return true;
            }
            catch (Exception error)
            {
                backgroundReady = false;
                DiagnosticsLog.Write("background_hide_failed", "normal close will finish recording", error);
                return false;
            }
        }

        internal void RestoreFromBackground()
        {
            if (disposed || appExitRequested || sessionEnding || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            if (!Dispatcher.CheckAccess())
            {
                try { Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(RestoreFromBackground)); }
                catch (InvalidOperationException error) { DiagnosticsLog.Write("restore_dispatch_unavailable", "app closing", error); }
                return;
            }
            try
            {
                Show();
                if (WindowState == WindowState.Minimized) WindowState = visibleWindowState;
                if (!isolatedTest) Activate();
                RefreshPresentation();
                DiagnosticsLog.Write("window_restored_from_background", "recording=" + running);
            }
            catch (Exception error)
            {
                DiagnosticsLog.Write("background_restore_failed", "restore request failed", error);
                // Stop hiding future close requests; retain the normal cleanup path.
                backgroundReady = false;
                RequestAppExit();
            }
        }

        private void RequestAppExit()
        {
            if (disposed || appExitRequested) return;
            appExitRequested = true;
            App.NoteExitRequest("앱 종료 버튼에서 종료 요청");
            try { Close(); }
            catch (Exception error)
            {
                DiagnosticsLog.Write("app_exit_close_failed", "finishing the owned monitor", error);
                NotifyBackgroundClosing();
                try { DisposeMonitor(); }
                finally { if (Application.Current != null) Application.Current.Shutdown(1); }
            }
            finally { if (!disposed) appExitRequested = false; }
        }
    }
}
