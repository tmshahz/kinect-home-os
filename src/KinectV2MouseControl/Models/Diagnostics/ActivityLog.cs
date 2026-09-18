using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace KinectV2MouseControl
{
    /// <summary>
    /// What kind of thing happened. Drives the colour/icon of the entry in the control center
    /// and lets the feed be filtered later without parsing titles.
    /// </summary>
    public enum ActivityKind
    {
        Action,
        Control,
        Tracking,
        Sensor,
        Calibration,
        Profile,
        Voice,
        System
    }

    /// <summary>
    /// One human-rate event for the recent-activity feed. Mutable only through Bump, which
    /// coalesces a burst of identical events (scroll notches, mostly) into one line with a
    /// count rather than flooding the feed.
    /// </summary>
    public class ActivityEntry : INotifyPropertyChanged
    {
        public DateTime Time { get; private set; }
        public ActivityKind Kind { get; private set; }
        public string Title { get; private set; }
        public string Detail { get; private set; }

        /// <summary>
        /// Where the event came from: "gesture", "voice", "control center", "sensor"...
        /// </summary>
        public string Source { get; private set; }

        public string CoalesceKey { get; private set; }
        public int Count { get; private set; }

        public ActivityEntry(DateTime time, ActivityKind kind, string title, string detail, string source, string coalesceKey)
        {
            Time = time;
            Kind = kind;
            Title = title;
            Detail = detail ?? "";
            Source = source ?? "";
            CoalesceKey = coalesceKey;
            Count = 1;
        }

        public string TimeText
        {
            get
            {
                return Time.ToString("HH:mm:ss");
            }
        }

        public string CountText
        {
            get
            {
                return Count > 1 ? "×" + Count : "";
            }
        }

        public string KindText
        {
            get
            {
                return Kind.ToString();
            }
        }

        internal void Bump(DateTime time)
        {
            Time = time;
            Count++;
            Raise("Time");
            Raise("TimeText");
            Raise("Count");
            Raise("CountText");
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }
    }

    /// <summary>
    /// In-memory feed of recent human-rate events for the control center: actions that were
    /// carried out, control switching on and off, tracking and sensor changes, calibration,
    /// profiles and voice. It sits beside RuntimeLog rather than replacing it: the log is the
    /// exhaustive engineering record on disk, this is the short, readable stream the UI shows.
    ///
    /// Posting is allowed from any thread and never throws. Subscribers are called on the
    /// posting thread and must marshal to the UI themselves. Like RuntimeLog it is static, so
    /// the engine can post from anywhere without threading a reference through every class.
    /// </summary>
    public static class ActivityLog
    {
        public const int Capacity = 200;

        private static readonly object gate = new object();
        private static readonly LinkedList<ActivityEntry> entries = new LinkedList<ActivityEntry>();

        /// <summary>
        /// Raised for a new entry. Coalesced repeats update the existing entry instead, which
        /// notifies through its own PropertyChanged.
        /// </summary>
        public static event EventHandler<ActivityEntry> EntryAdded;

        public static event EventHandler Cleared;

        /// <summary>
        /// Adds an entry. When <paramref name="coalesceKey"/> matches the most recent entry and
        /// that entry is younger than <paramref name="coalesceWindowSeconds"/>, the existing
        /// entry is bumped instead of a new one being added.
        /// </summary>
        public static void Post(ActivityKind kind, string title, string detail = null, string source = null,
            string coalesceKey = null, double coalesceWindowSeconds = 0)
        {
            try
            {
                ActivityEntry added;
                DateTime now = DateTime.Now;

                lock (gate)
                {
                    ActivityEntry latest = entries.Count > 0 ? entries.Last.Value : null;
                    if (coalesceKey != null && latest != null && latest.CoalesceKey == coalesceKey
                        && (now - latest.Time).TotalSeconds <= coalesceWindowSeconds)
                    {
                        latest.Bump(now);
                        return;
                    }

                    added = new ActivityEntry(now, kind, title, detail, source, coalesceKey);
                    entries.AddLast(added);
                    while (entries.Count > Capacity)
                    {
                        entries.RemoveFirst();
                    }
                }

                EventHandler<ActivityEntry> handler = EntryAdded;
                if (handler != null)
                {
                    handler.Invoke(null, added);
                }
            }
            catch (Exception)
            {
                // The feed is cosmetic. It must never be able to affect control.
            }
        }

        /// <summary>
        /// Oldest first.
        /// </summary>
        public static ActivityEntry[] Snapshot()
        {
            lock (gate)
            {
                ActivityEntry[] copy = new ActivityEntry[entries.Count];
                entries.CopyTo(copy, 0);
                return copy;
            }
        }

        public static void Clear()
        {
            lock (gate)
            {
                entries.Clear();
            }

            EventHandler handler = Cleared;
            if (handler != null)
            {
                handler.Invoke(null, EventArgs.Empty);
            }
        }
    }
}
