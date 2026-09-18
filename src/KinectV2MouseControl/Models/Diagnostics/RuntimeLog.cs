using System;
using System.IO;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Plain-text event log for diagnosing intermittent runtime problems on real hardware.
    ///
    /// Written to %LOCALAPPDATA%\KinectHomeOS\runtime.log. Each launch starts a fresh file and
    /// keeps the previous launch as runtime.prev.log, so a bad session can be inspected after
    /// restarting the app. Only human-rate events are logged - sensor availability, body lock
    /// changes, pointer sessions, resets, clutch transitions, calibration, profiles - never
    /// per-frame data, so the log costs nothing on the 30Hz path.
    ///
    /// Logging must never be able to break control, so every failure is swallowed.
    /// </summary>
    public static class RuntimeLog
    {
        private static readonly object gate = new object();
        private static string path;
        private static bool isInitialized;
        private static bool isSuspended;

        /// <summary>
        /// Turns logging into a no-op for this process. Used by the UI smoke test so a
        /// verification run never rotates or appends to the log of a real session that may be
        /// running at the same time.
        /// </summary>
        public static void Suspend()
        {
            lock (gate)
            {
                isSuspended = true;
            }
        }

        public static string DirectoryPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "KinectHomeOS");
            }
        }

        public static string FilePath
        {
            get
            {
                return Path.Combine(DirectoryPath, "runtime.log");
            }
        }

        public static void Write(string message)
        {
            try
            {
                lock (gate)
                {
                    if (isSuspended)
                    {
                        return;
                    }

                    EnsureInitialized();
                    if (path == null)
                    {
                        return;
                    }

                    File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
            }
            catch (Exception)
            {
                // Diagnostics only. A locked or read-only log must not affect control.
            }
        }

        private static void EnsureInitialized()
        {
            if (isInitialized)
            {
                return;
            }

            isInitialized = true;

            Directory.CreateDirectory(DirectoryPath);
            string current = FilePath;
            string previous = Path.Combine(DirectoryPath, "runtime.prev.log");

            if (File.Exists(current))
            {
                if (File.Exists(previous))
                {
                    File.Delete(previous);
                }

                File.Move(current, previous);
            }

            path = current;
            File.WriteAllText(path, "Kinect Home OS runtime log, started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                + "  exe " + AppDomain.CurrentDomain.BaseDirectory + Environment.NewLine);
        }
    }
}
