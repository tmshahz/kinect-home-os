using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace KinectV2MouseControl
{
    internal static class DesktopWindows
    {
        internal sealed class Window
        {
            public IntPtr Handle;
            public string Process;
            public string Title;
            public int Monitor;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left, Top, Right, Bottom;
            public Rect(int left, int top, int right, int bottom) { Left = left; Top = top; Right = right; Bottom = bottom; }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
        private delegate bool EnumCallback(IntPtr window, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr data);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect rect, int size);

        internal static Rect[] Monitors()
        {
            return DesktopLayout.Capture().Monitors.OrderBy(m => m.Left).ThenBy(m => m.Top)
                .Select(m => new Rect((int)m.Left, (int)m.Top, (int)m.Right, (int)m.Bottom)).ToArray();
        }
        private static MonitorInfo Info(IntPtr monitor)
        {
            MonitorInfo info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (!GetMonitorInfo(monitor, ref info)) { throw new InvalidOperationException("Monitor information is unavailable."); }
            return info;
        }
        public static List<Window> List()
        {
            Rect[] monitors = Monitors();
            List<Window> windows = new List<Window>();
            EnumWindows((handle, data) =>
            {
                if (!IsWindowVisible(handle) || GetWindow(handle, 4) != IntPtr.Zero) { return true; }
                StringBuilder title = new StringBuilder(1024);
                if (GetWindowText(handle, title, title.Capacity) == 0) { return true; }
                uint id; GetWindowThreadProcessId(handle, out id);
                try
                {
                    using (Process process = Process.GetProcessById((int)id))
                    {
                        Rect monitor = Info(MonitorFromWindow(handle, 2)).Monitor;
                        windows.Add(new Window { Handle = handle, Process = process.ProcessName, Title = title.ToString(),
                            Monitor = Array.FindIndex(monitors, m => m.Left == monitor.Left && m.Top == monitor.Top) + 1 });
                    }
                }
                catch (Exception) { /* A window can close during enumeration. */ }
                return true;
            }, IntPtr.Zero);
            return windows;
        }

        internal static Rect Region(Rect work, string region)
        {
            int midX = work.Left + (work.Right - work.Left) / 2;
            int midY = work.Top + (work.Bottom - work.Top) / 2;
            switch (region)
            {
                case "maximize": return work;
                case "left": return new Rect(work.Left, work.Top, midX, work.Bottom);
                case "right": return new Rect(midX, work.Top, work.Right, work.Bottom);
                case "top": return new Rect(work.Left, work.Top, work.Right, midY);
                case "bottom": return new Rect(work.Left, midY, work.Right, work.Bottom);
                case "top-left": return new Rect(work.Left, work.Top, midX, midY);
                case "top-right": return new Rect(midX, work.Top, work.Right, midY);
                case "bottom-left": return new Rect(work.Left, midY, midX, work.Bottom);
                case "bottom-right": return new Rect(midX, midY, work.Right, work.Bottom);
                default: throw new ArgumentException("Unknown window region.");
            }
        }

        public static DesktopActionResult Place(DesktopActionRequest request, bool dryRun)
        {
            Rect[] monitors = Monitors();
            if (request.Monitor < 1 || request.Monitor > monitors.Length) { return DesktopActionResult.Refused("Monitor must be 1–" + monitors.Length + " (left to right)."); }
            if (string.IsNullOrWhiteSpace(request.Window)) { return DesktopActionResult.Refused("Specify a window process name or title."); }
            List<Window> all = List();
            List<Window> matches = all.Where(w => string.Equals(w.Process, request.Window, StringComparison.OrdinalIgnoreCase)
                || string.Equals(w.Title, request.Window, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) { matches = all.Where(w => w.Title.IndexOf(request.Window, StringComparison.OrdinalIgnoreCase) >= 0).ToList(); }
            if (matches.Count == 0) { return DesktopActionResult.Refused("No visible window matches “" + request.Window + "”."); }
            if (matches.Count != 1) { return DesktopActionResult.Refused("Several windows match. Use ListWindows and choose an exact title."); }
            Window window = matches[0];
            Rect bounds = monitors[request.Monitor - 1];
            Rect desired = Region(Info(MonitorFromRect(ref bounds, 2)).Work, request.Region);
            if (!dryRun)
            {
                ShowWindow(window.Handle, 9); // Restore before measuring the normal frame.
                Rect outer, visible;
                int left = 0, top = 0, right = 0, bottom = 0;
                if (GetWindowRect(window.Handle, out outer) && DwmGetWindowAttribute(window.Handle, 9, out visible, Marshal.SizeOf(typeof(Rect))) == 0)
                { left = visible.Left - outer.Left; top = visible.Top - outer.Top; right = outer.Right - visible.Right; bottom = outer.Bottom - visible.Bottom; }
                if (!SetWindowPos(window.Handle, IntPtr.Zero, desired.Left - left, desired.Top - top,
                    desired.Right - desired.Left + left + right, desired.Bottom - desired.Top + top + bottom, 0x0014))
                { return DesktopActionResult.Refused("Windows refused to place the window."); }
                if (request.Region == "maximize") { ShowWindow(window.Handle, 3); }
            }
            return DesktopActionResult.Ok((dryRun ? "Would place " : "Placed ") + window.Process + " on monitor " + request.Monitor + " " + request.Region);
        }
    }
}
