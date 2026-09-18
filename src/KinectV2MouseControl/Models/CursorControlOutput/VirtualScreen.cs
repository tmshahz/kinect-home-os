using System.Runtime.InteropServices;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Bounds of the Windows virtual desktop in real physical pixels.
    ///
    /// WPF's SystemParameters.PrimaryScreen* report device-independent units for the primary
    /// display only, which is wrong on two counts for cursor work: SetCursorPos takes physical
    /// pixels, so on a scaled display the mapping falls short of the screen edges, and a
    /// multi-monitor desktop is larger than its primary monitor.
    ///
    /// GetSystemMetrics with the SM_*VIRTUALSCREEN metrics reports the whole desktop in
    /// physical pixels, which is the same coordinate space SetCursorPos uses. Note this is only
    /// true because the process declares itself DPI aware in app.manifest - without that,
    /// Windows would hand back DPI-virtualized values here as well.
    ///
    /// The origin can be negative: a monitor placed to the left of or above the primary one
    /// gives a negative Left/Top, so the rect is signed rather than a plain width/height pair.
    /// </summary>
    public static class VirtualScreen
    {
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        /// <summary>
        /// Virtual desktop rect in physical pixels. Right/Bottom are exclusive, matching the
        /// Win32 convention (a 1920x1080 primary-only desktop returns 0,0,1920,1080).
        /// </summary>
        public static MRect GetBounds()
        {
            int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            if (width <= 0 || height <= 0)
            {
                // Very old or unusual configurations may not report the virtual metrics.
                left = 0;
                top = 0;
                width = GetSystemMetrics(SM_CXSCREEN);
                height = GetSystemMetrics(SM_CYSCREEN);
            }

            if (width <= 0 || height <= 0)
            {
                width = 1920;
                height = 1080;
            }

            return new MRect(left, top, left + width, top + height);
        }
    }
}
