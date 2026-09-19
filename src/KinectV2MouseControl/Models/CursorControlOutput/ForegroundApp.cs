using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Which app owns the window in front, by process name ("ChatGPT", "claude", "Cursor").
    /// Read-only: used to check that a key combination meant for one app is only ever sent
    /// when that app is the one in front, and to offer the running apps as choices.
    /// </summary>
    public static class ForegroundApp
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        /// <summary>
        /// Process name of the foreground window's owner, or null if it cannot be read.
        /// </summary>
        public static string CurrentProcessName()
        {
            try
            {
                IntPtr window = GetForegroundWindow();
                if (window == IntPtr.Zero)
                {
                    return null;
                }

                uint processId;
                GetWindowThreadProcessId(window, out processId);
                if (processId == 0)
                {
                    return null;
                }

                using (Process process = Process.GetProcessById((int)processId))
                {
                    return process.ProcessName;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Case-insensitive, and tolerant of a typed ".exe".
        /// </summary>
        public static bool Matches(string processName, string wanted)
        {
            if (string.IsNullOrEmpty(processName) || string.IsNullOrWhiteSpace(wanted))
            {
                return false;
            }

            string name = wanted.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - 4);
            }

            return string.Equals(processName, name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Process names of apps that currently have a titled main window, sorted, without this
        /// process.
        /// </summary>
        public static List<string> WindowedAppNames()
        {
            List<string> names = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int self;
            using (Process current = Process.GetCurrentProcess())
            {
                self = current.Id;
            }

            Process[] processes;
            try
            {
                processes = Process.GetProcesses();
            }
            catch (Exception)
            {
                return names;
            }

            foreach (Process process in processes)
            {
                try
                {
                    if (process.Id != self && process.MainWindowHandle != IntPtr.Zero
                        && !string.IsNullOrEmpty(process.MainWindowTitle) && seen.Add(process.ProcessName))
                    {
                        names.Add(process.ProcessName);
                    }
                }
                catch (Exception)
                {
                    // Exited or not accessible: skip it.
                }
                finally
                {
                    process.Dispose();
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
    }
}
