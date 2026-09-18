using System.Text;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Live snapshot of what the gesture layer is seeing, for hardware tuning.
    ///
    /// Written on the Kinect frame thread and read by a slower UI timer. Every field is a
    /// value type or an immutable string, and a torn read would only ever show one stale line
    /// for one refresh, so this is deliberately lock-free rather than synchronized - the cost
    /// of a lock on the 30Hz pointer path is not worth paying for a debug readout.
    /// </summary>
    public class GestureDiagnostics
    {
        public bool IsTracking { get; set; }
        public bool IsControlEnabled { get; set; } = true;
        public string Mode { get; set; } = "Idle";
        public string ControlHand { get; set; } = "None";
        public string LeftHandState { get; set; } = "-";
        public string RightHandState { get; set; } = "-";
        public string Gesture { get; set; } = "None";
        public string LastAction { get; set; } = "-";

        /// <summary>
        /// Moving / Stationary / Locked.
        /// </summary>
        public string PointerState { get; set; } = "-";

        /// <summary>
        /// Distance in pixels between the locked anchor and where the hand now points. Shows
        /// how close the lock is to breaking out.
        /// </summary>
        public double LockDisplacement { get; set; }

        /// <summary>
        /// Captured scroll neutral in metres above SpineBase, or null when not engaged.
        /// </summary>
        public double? ScrollNeutral { get; set; }

        public double ScrollOffset { get; set; }

        public string ClapState { get; set; } = "Idle";

        /// <summary>
        /// Height of the controlling hand above SpineBase, in metres. The single most useful
        /// number when tuning the activation thresholds, since it says exactly where your hand
        /// sits relative to them.
        /// </summary>
        public double ControlHandHeight { get; set; }

        public double ControlHandForward { get; set; }

        public string DesktopBounds { get; set; } = "-";

        public string Calibration { get; set; } = "Off";

        public string BuildText()
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("Tracking: " + (IsTracking ? "Yes" : "No")
                + "    Control: " + (IsControlEnabled ? "Enabled" : "DISABLED"));
            text.AppendLine("Mode: " + Mode + "    Control Hand: " + ControlHand);
            text.AppendLine("Right Hand: " + RightHandState + "    Left Hand: " + LeftHandState);
            text.AppendLine("Gesture: " + Gesture + "    Clap: " + ClapState);
            text.AppendLine("Pointer: " + PointerState + "    lock disp: "
                + LockDisplacement.ToString("0") + " px");
            text.AppendLine("Scroll neutral: " + (ScrollNeutral.HasValue
                ? ScrollNeutral.Value.ToString("0.00") + " m  (off " + ScrollOffset.ToString("+0.00;-0.00;0.00") + ")"
                : "-"));
            text.AppendLine("Hand height: " + ControlHandHeight.ToString("0.00")
                + " m   forward: " + ControlHandForward.ToString("0.00") + " m");
            text.AppendLine("Desktop: " + DesktopBounds);
            text.AppendLine("Calibration: " + Calibration);
            text.Append("Last Action: " + LastAction);
            return text.ToString();
        }
    }
}
