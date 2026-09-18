using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Asks the Desktop Window Manager for the Windows 11 acrylic backdrop behind a WPF
    /// window, plus the dark frame and rounded corners that go with it.
    ///
    /// How it works: WindowChrome (GlassFrameThickness -1) extends the DWM frame over the
    /// whole client area, the WPF surface is made transparent, and DWMWA_SYSTEMBACKDROP_TYPE
    /// tells DWM to paint blurred desktop content behind it. Everything the window draws with
    /// alpha then sits on that blur, which is the "liquid glass" look. On builds where the
    /// attribute is unknown (Windows 10, early Windows 11) the call fails and the caller keeps
    /// its opaque painted background, so nothing depends on it.
    /// </summary>
    public static class WindowBackdrop
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        private const int DWMWCP_ROUND = 2;

        /// <summary>
        /// DWMSBT_TRANSIENTWINDOW: acrylic. DWMSBT_MAINWINDOW (2) is Mica, which only tints
        /// with the wallpaper; acrylic blurs whatever is actually behind the window.
        /// </summary>
        private const int DWMSBT_TRANSIENTWINDOW = 3;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>
        /// Applies dark mode, rounded corners and the acrylic backdrop.
        /// </summary>
        /// <returns>True when the backdrop was accepted and the window should paint translucently.</returns>
        public static bool TryApply(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                int dark = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

                int corner = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

                int backdrop = DWMSBT_TRANSIENTWINDOW;
                int result = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
                if (result != 0)
                {
                    RuntimeLog.Write("Acrylic backdrop not available (HRESULT 0x" + result.ToString("X8") + "); using painted background");
                    return false;
                }

                HwndSource source = HwndSource.FromHwnd(hwnd);
                if (source != null && source.CompositionTarget != null)
                {
                    source.CompositionTarget.BackgroundColor = Colors.Transparent;
                }

                return true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("Backdrop setup failed: " + ex.Message);
                return false;
            }
        }
    }
}
