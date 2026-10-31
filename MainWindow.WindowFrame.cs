using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PcInternetMonitor
{
    public sealed partial class MainWindow
    {
        // Native DWM attributes preserve Windows move/resize/system-button behavior.
        // Color attributes are supported on Windows 11; older systems may reject them.
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        private HwndSource frameSource;
        private bool frameClosed, frameUpdateQueued;
        private string frameLastResult;

        private void InitializeWindowFrame()
        {
            SourceInitialized += delegate {
                frameSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                if (frameSource != null) { frameSource.AddHook(OnFrameMessage); ApplyWindowFrame(); }
            };
            Closed += delegate {
                frameClosed = true;
                if (frameSource != null) { frameSource.RemoveHook(OnFrameMessage); frameSource = null; }
            };
        }

        private IntPtr OnFrameMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Queue after WPF has refreshed its cached system settings. Never consume
            // native messages: sizing, snap, title-bar buttons and close stay native.
            if ((message == 0x001A || message == 0x031A || message == 0x0320) && !frameUpdateQueued && !frameClosed)
            {
                frameUpdateQueued = true;
                try { Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate {
                    frameUpdateQueued = false;
                    if (!frameClosed) ApplyWindowFrame();
                })); }
                catch (InvalidOperationException) { frameUpdateQueued = false; }
            }
            return IntPtr.Zero;
        }

        private void ApplyWindowFrame()
        {
            if (frameClosed || frameSource == null || frameSource.Handle == IntPtr.Zero) return;
            try
            {
                bool highContrast = SystemParameters.HighContrast;
                int dark = highContrast ? 0 : 1;
                int border = highContrast ? -1 : FrameColorRef(BorderColor);
                int caption = highContrast ? -1 : FrameColorRef(BackColor);
                int text = highContrast ? -1 : FrameColorRef(TextColor);
                int darkResult = DwmSetWindowAttribute(frameSource.Handle, 20, ref dark, 4);
                int borderResult = DwmSetWindowAttribute(frameSource.Handle, 34, ref border, 4);
                int captionResult = DwmSetWindowAttribute(frameSource.Handle, 35, ref caption, 4);
                int textResult = DwmSetWindowAttribute(frameSource.Handle, 36, ref text, 4);
                string result = "high_contrast=" + highContrast + "; dark_hr=" + darkResult +
                    "; border_hr=" + borderResult + "; caption_hr=" + captionResult + "; text_hr=" + textResult;
                if (frameLastResult != result) { frameLastResult = result; DiagnosticsLog.Write("native_frame_style", result); }
            }
            catch (Exception error)
            {
                // Styling must never prevent monitoring on an older or restricted OS.
                if (frameLastResult != "unavailable") { frameLastResult = "unavailable"; DiagnosticsLog.Write("native_frame_style_unavailable", "Windows default frame retained", error); }
            }
        }

        private static int FrameColorRef(Color color) { return color.R | (color.G << 8) | (color.B << 16); }
    }
}
