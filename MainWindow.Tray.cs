using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace PcInternetMonitor
{
    public sealed partial class MainWindow
    {
        private Forms.NotifyIcon trayIcon;
        private Forms.ContextMenuStrip trayMenu;
        private Forms.ToolStripMenuItem trayOpenItem, trayStartItem, trayStopItem, trayExitItem;
        private Drawing.Icon trayDrawingIcon;
        private Stream trayIconStream;
        private bool trayReady, trayExitRequested, trayDisposed, trayHideNoticeShown;

        private void InitializeTray()
        {
            if (!Dispatcher.CheckAccess()) { DispatchTrayAction(InitializeTray); return; }
            if (trayReady || trayDisposed || disposed) return;
            try
            {
                // System.Drawing.Icon can retain its input stream; keep both until tray disposal.
                trayIconStream = typeof(MainWindow).Assembly.GetManifestResourceStream("PcInternetMonitor.AppIcon.ico");
                if (trayIconStream == null) throw new InvalidOperationException("내장 트레이 아이콘을 찾지 못했습니다.");
                trayDrawingIcon = new Drawing.Icon(trayIconStream);
                trayOpenItem = new Forms.ToolStripMenuItem("창 열기");
                trayStartItem = new Forms.ToolStripMenuItem("검사 시작");
                trayStopItem = new Forms.ToolStripMenuItem("검사 중지");
                trayExitItem = new Forms.ToolStripMenuItem("종료");
                trayOpenItem.Click += delegate { DispatchTrayAction(RestoreFromTray); };
                trayStartItem.Click += delegate { DispatchTrayAction(delegate { if (!running && adapterPicker.SelectedItem is AdapterInfo) StartSession(); }); };
                trayStopItem.Click += delegate { DispatchTrayAction(delegate { if (running) StopSession("트레이 메뉴에서 검사 중지", null); }); };
                trayExitItem.Click += delegate { DispatchTrayAction(RequestTrayExit); };
                trayMenu = new Forms.ContextMenuStrip();
                trayMenu.Items.AddRange(new Forms.ToolStripItem[] { trayOpenItem, trayStartItem, trayStopItem, new Forms.ToolStripSeparator(), trayExitItem });
                trayMenu.Opening += delegate { DispatchTrayAction(UpdateTrayState); };
                trayIcon = new Forms.NotifyIcon();
                trayIcon.Icon = trayDrawingIcon;
                trayIcon.ContextMenuStrip = trayMenu;
                trayIcon.DoubleClick += delegate { DispatchTrayAction(RestoreFromTray); };
                trayIcon.Visible = true;
                trayReady = true;
                UpdateTrayState();
                if (trayReady) DiagnosticsLog.Write("tray_initialized", "notification icon and menu available");
            }
            catch (Exception error) { HandleTrayFailure("tray_initialize_failed", error, false); }
        }

        private bool HideToTray()
        {
            // Closing is raised on the WPF dispatcher. Never cancel it from an unavailable tray.
            if (!Dispatcher.CheckAccess() || !trayReady || trayDisposed || trayIcon == null || disposed || trayExitRequested) return false;
            try
            {
                trayIcon.Visible = true;
                Hide();
                if (IsVisible) return false;
                DiagnosticsLog.Write("window_hidden_to_tray", "recording=" + running);
                if (!trayHideNoticeShown && !isolatedTest)
                {
                    trayHideNoticeShown = true;
                    try
                    {
                        trayIcon.ShowBalloonTip(4000, "PC 인터넷 모니터", "창을 트레이에 숨겼습니다. 아이콘을 두 번 클릭하면 창을 열고, 메뉴의 종료로 앱을 종료할 수 있습니다.", Forms.ToolTipIcon.Info);
                    }
                    catch (Exception error) { DiagnosticsLog.Write("tray_notice_failed", "one-time tray help unavailable", error); }
                }
                return true;
            }
            catch (Exception error)
            {
                HandleTrayFailure("tray_hide_failed", error, true);
                return false;
            }
        }

        private void RestoreFromTray()
        {
            if (!Dispatcher.CheckAccess()) { DispatchTrayAction(RestoreFromTray); return; }
            if (disposed || trayExitRequested || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Show();
                WindowState = WindowState.Normal;
                if (!isolatedTest) Activate();
                UpdateTrayState();
                DiagnosticsLog.Write("window_restored_from_tray", "recording=" + running);
            }
            catch (Exception error) { HandleTrayFailure("tray_restore_failed", error, true); }
        }

        private void UpdateTrayState()
        {
            if (!Dispatcher.CheckAccess()) { DispatchTrayAction(UpdateTrayState); return; }
            if (!trayReady || trayDisposed || trayIcon == null) return;
            try
            {
                bool usable = !disposed && !trayExitRequested;
                trayOpenItem.Enabled = usable;
                trayStartItem.Enabled = usable && !running && !stopPending && adapterPicker.SelectedItem is AdapterInfo;
                trayStopItem.Enabled = usable && running && !stopPending;
                trayExitItem.Enabled = usable;
                string status = trayExitRequested ? "종료 중" : !running && clockStopUtc != DateTime.MinValue ? "시각 변경으로 중지" : fault ? "기록 오류" : running ? "검사 중" : "검사 중지";
                string text = "PC Internet Monitor " + AppVersion + " | " + status;
                if (text.Length > 63) text = text.Substring(0, 63);
                if (trayIcon.Text != text) trayIcon.Text = text;
            }
            catch (Exception error) { HandleTrayFailure("tray_state_failed", error, true); }
        }

        private void RequestTrayExit()
        {
            if (!Dispatcher.CheckAccess()) { DispatchTrayAction(RequestTrayExit); return; }
            if (disposed || trayExitRequested) return;
            trayExitRequested = true;
            UpdateTrayState();
            App.NoteExitRequest("트레이 메뉴에서 종료 요청");
            DiagnosticsLog.Write("tray_exit_requested", "recording=" + running);
            try
            {
                // The normal Closing path completes the recording and releases the monitor.
                Close();
                if (!disposed) { trayExitRequested = false; UpdateTrayState(); }
            }
            catch (Exception error)
            {
                trayExitRequested = false;
                HandleTrayFailure("tray_exit_failed", error, true);
            }
        }

        private void DispatchTrayAction(Action action)
        {
            if (disposed || trayDisposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Action guarded = delegate
            {
                if (disposed || trayDisposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                try { action(); }
                catch (Exception error) { HandleTrayFailure("tray_command_failed", error, true); }
            };
            if (Dispatcher.CheckAccess()) guarded();
            else
            {
                try { Dispatcher.BeginInvoke(DispatcherPriority.Normal, guarded); }
                catch (InvalidOperationException error) { DiagnosticsLog.Write("tray_dispatch_unavailable", "dispatcher shutting down", error); }
            }
        }

        private void HandleTrayFailure(string stage, Exception error, bool restoreWindow)
        {
            DiagnosticsLog.Write(stage, "tray disabled; normal window remains available", error);
            DisposeTray();
            if (disposed || trayExitRequested || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            notice = "트레이 기능을 사용할 수 없어 창 모드로 전환했습니다.";
            try
            {
                if (restoreWindow)
                {
                    Show();
                    WindowState = WindowState.Normal;
                    if (!isolatedTest) Activate();
                }
                RefreshPresentation();
            }
            catch (Exception recoveryError)
            {
                DiagnosticsLog.Write("tray_window_recovery_failed", "normal window unavailable; finishing owned app", recoveryError);
                trayExitRequested = true;
                // Queue Close outside any active Closing callback. With no tray the
                // normal close path completes logging instead of leaving a hidden app.
                try
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(delegate {
                        try { if (!disposed) Close(); }
                        catch (Exception closeError) { DiagnosticsLog.Write("tray_recovery_close_failed", "owned window", closeError); if (Application.Current != null) Application.Current.Shutdown(1); }
                        finally { try { DisposeMonitor(); } finally { DisposeTray(); } }
                    }));
                }
                catch (InvalidOperationException dispatchError)
                {
                    DiagnosticsLog.Write("tray_recovery_dispatch_failed", "dispatcher unavailable", dispatchError);
                    DisposeMonitor(); DisposeTray();
                }
            }
        }

        private void DisposeTray()
        {
            if (trayDisposed) return;
            trayDisposed = true;
            trayReady = false;
            Forms.NotifyIcon oldTray = trayIcon;
            Forms.ContextMenuStrip oldMenu = trayMenu;
            Drawing.Icon oldIcon = trayDrawingIcon;
            Stream oldStream = trayIconStream;
            trayIcon = null;
            trayMenu = null;
            trayOpenItem = trayStartItem = trayStopItem = trayExitItem = null;
            trayDrawingIcon = null;
            trayIconStream = null;
            if (oldTray != null)
            {
                try { oldTray.Visible = false; }
                catch (Exception error) { DiagnosticsLog.Write("tray_visibility_cleanup_failed", "notification icon", error); }
                try { oldTray.ContextMenuStrip = null; }
                catch (Exception error) { DiagnosticsLog.Write("tray_menu_detach_failed", "notification icon", error); }
                try { oldTray.Dispose(); }
                catch (Exception error) { DiagnosticsLog.Write("tray_dispose_failed", "notification icon", error); }
            }
            if (oldMenu != null)
                try { oldMenu.Dispose(); }
                catch (Exception error) { DiagnosticsLog.Write("tray_menu_dispose_failed", "notification menu", error); }
            if (oldIcon != null)
                try { oldIcon.Dispose(); }
                catch (Exception error) { DiagnosticsLog.Write("tray_icon_dispose_failed", "embedded drawing icon", error); }
            if (oldStream != null)
                try { oldStream.Dispose(); }
                catch (Exception error) { DiagnosticsLog.Write("tray_stream_dispose_failed", "embedded icon stream", error); }
        }
    }
}
