using System;
using System.Collections.Generic;
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

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        /// <summary>
        /// Rectangle of every attached monitor, in the same physical-pixel virtual-desktop
        /// coordinates as GetBounds (the process is PerMonitorV2 DPI aware, so these are not
        /// scaled). Empty if enumeration fails.
        /// </summary>
        public static MRect[] GetMonitorRects()
        {
            List<MRect> monitors = new List<MRect>();

            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
                {
                    if (rect.Right > rect.Left && rect.Bottom > rect.Top)
                    {
                        monitors.Add(new MRect(rect.Left, rect.Top, rect.Right, rect.Bottom));
                    }

                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception)
            {
                monitors.Clear();
            }

            return monitors.ToArray();
        }
    }

    /// <summary>
    /// Snapshot of the desktop geometry: the virtual-desktop bounding box plus the individual
    /// monitor rectangles inside it.
    ///
    /// The bounding box alone is not enough when monitors differ in resolution or are offset
    /// vertically: parts of the box belong to no monitor. Windows keeps the real cursor out of
    /// those dead areas, but the mapped pointer target does not know that, so a hand pointing
    /// into a dead corner would push the target somewhere the cursor can never go - and the
    /// hand then has to travel all the way back before the cursor responds. Clamping to the
    /// nearest real monitor instead keeps the target where the cursor actually is.
    /// </summary>
    public sealed class DesktopLayout
    {
        public MRect Bounds { get; private set; }

        public MRect[] Monitors { get; private set; }

        private DesktopLayout(MRect bounds, MRect[] monitors)
        {
            Bounds = bounds;
            Monitors = monitors;
        }

        public static DesktopLayout Capture()
        {
            return new DesktopLayout(VirtualScreen.GetBounds(), VirtualScreen.GetMonitorRects());
        }

        /// <summary>
        /// Returns the point unchanged if it is on a monitor, otherwise the nearest point that
        /// is. Right/Bottom are exclusive, so the last addressable pixel is one inside.
        /// </summary>
        public MVector2 Clamp(MVector2 point)
        {
            if (Monitors == null || Monitors.Length == 0)
            {
                return ClampToRect(point, Bounds);
            }

            MVector2 best = point;
            double bestDistance = double.MaxValue;

            for (int i = 0; i < Monitors.Length; i++)
            {
                MVector2 clamped = ClampToRect(point, Monitors[i]);
                double distance = (clamped - point).Length();

                if (distance == 0)
                {
                    return point;
                }

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = clamped;
                }
            }

            return best;
        }

        private static MVector2 ClampToRect(MVector2 point, MRect rect)
        {
            double left = Math.Min(rect.Left, rect.Right);
            double top = Math.Min(rect.Top, rect.Bottom);
            double right = Math.Max(rect.Left, rect.Right) - 1;
            double bottom = Math.Max(rect.Top, rect.Bottom) - 1;

            point.X = Math.Max(left, Math.Min(right, point.X));
            point.Y = Math.Max(top, Math.Min(bottom, point.Y));
            return point;
        }

        public string Describe()
        {
            return Math.Abs(Bounds.DeltaX).ToString("0") + "x" + Math.Abs(Bounds.DeltaY).ToString("0")
                + " @ (" + Bounds.Left.ToString("0") + "," + Bounds.Top.ToString("0") + "), "
                + (Monitors == null ? 0 : Monitors.Length) + " monitor(s)";
        }
    }
}
