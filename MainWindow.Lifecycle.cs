using System;
using System.Windows;

namespace PcInternetMonitor
{
    public sealed partial class MainWindow
    {
        private bool appExitRequested;

        private void HandleWindowStateChanged(object sender, EventArgs e)
        {
            if (!disposed && !appExitRequested && !sessionEnding && WindowState == WindowState.Minimized && running)
                StopSession("최소화로 검사 중지 · 검사 시작으로 다시 시작하세요", null);
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
                try { DisposeMonitor(); }
                finally { if (Application.Current != null) Application.Current.Shutdown(1); }
            }
            finally { if (!disposed) appExitRequested = false; }
        }
    }
}
