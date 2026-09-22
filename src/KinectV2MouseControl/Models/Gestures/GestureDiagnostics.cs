using System.Text;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Live snapshot of what the control engine is seeing, for hardware tuning.
    ///
    /// Written from the Kinect frame handler and read by a slower UI timer, both on the UI
    /// thread. Every field is a value type or an immutable string, so this is deliberately
    /// lock-free - the cost of a lock on the 30Hz pointer path is not worth paying for a debug
    /// readout.
    /// </summary>
    public class GestureDiagnostics
    {
        public bool IsTracking { get; set; }
        public bool IsControlEnabled { get; set; } = true;
        public int BodyCount { get; set; }

        /// <summary>
        /// Core joints Tracked on the locked body, out of 6.
        /// </summary>
        public int BodyScore { get; set; }

        public string Mode { get; set; } = "Idle";

        /// <summary>
        /// Waiting / Stabilizing / Active.
        /// </summary>
        public string PointerSession { get; set; } = "Waiting";
        public int PointerHandIndex { get; set; } = GestureContext.RightHand;
        public string PointerHandName { get; set; } = "Right";
        public string SecondaryHandName { get; set; } = "Left";

        public string RightHandState { get; set; } = "-";
        public string LeftHandState { get; set; } = "-";
        public string ClutchState { get; set; } = "Off";
        public string SecondaryMode { get; set; } = "None";
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

        public double ScrollRate { get; set; }

        public string ClapState { get; set; } = "Idle";

        /// <summary>
        /// Smoothed sensor frame interval, and the worst recent one, in milliseconds.
        /// </summary>
        public double FrameDeltaMs { get; set; }
        public double FrameDeltaMaxMs { get; set; }

        /// <summary>
        /// Raw-vs-filtered pointer distance in pixels. High while the hand is still means the
        /// tracking itself is noisy right now.
        /// </summary>
        public double ResidualNoise { get; set; }

        public int GlitchCount { get; set; }

        /// <summary>
        /// Height of the right hand above SpineBase, in metres. The single most useful number
        /// when tuning the activation thresholds, since it says exactly where the hand sits
        /// relative to them.
        /// </summary>
        public double ControlHandHeight { get; set; }

        public double ControlHandForward { get; set; }

        /// <summary>
        /// Sideways position of the right hand relative to SpineBase, metres, positive to the
        /// user's right. With the height above, this places the hand on the control center's
        /// spatial preview.
        /// </summary>
        public double ControlHandX { get; set; }

        public double SecondaryHandHeight { get; set; }
        public double SecondaryHandX { get; set; }
        public double SecondaryHandForward { get; set; }

        // Structured hand flags for the control center, so the UI never has to parse the
        // RightHandState / LeftHandState strings above.
        public bool RightHandTracked { get; set; }
        public bool LeftHandTracked { get; set; }
        public bool RightHandActivated { get; set; }
        public bool LeftHandActivated { get; set; }
        public bool RightHandClosed { get; set; }
        public bool LeftHandClosed { get; set; }
        public bool RightHandLasso { get; set; }
        public bool LeftHandLasso { get; set; }

        /// <summary>
        /// True while the latched pointer hand has an Active session.
        /// </summary>
        public bool IsPointerActive { get; set; }

        public bool IsClutchArmed { get; set; }

        /// <summary>
        /// True while an injected left button is held by a grip.
        /// </summary>
        public bool IsGripHeld { get; set; }

        /// <summary>
        /// Latest cursor target published to the output loop, in virtual-desktop pixels. Valid
        /// only while HasCursorTarget.
        /// </summary>
        public double CursorX { get; set; }
        public double CursorY { get; set; }
        public bool HasCursorTarget { get; set; }

        public string DesktopBounds { get; set; } = "-";

        public string Calibration { get; set; } = "Off";

        /// <summary>
        /// Guided-capture progress for the control center: which of the five points is being
        /// captured (CalibrationStep cast to int, 0 when idle) and how far the current steady
        /// hold has progressed, 0-1.
        /// </summary>
        public int CalibrationStep { get; set; }
        public int CalibrationPass { get; set; }
        public int CalibrationPasses { get; set; }
        public double CalibrationHoldProgress { get; set; }
        public bool CalibrationWaitingForHand { get; set; }

        public string BuildText()
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("Tracking: " + (IsTracking ? "Yes (" + BodyCount + " body, q " + BodyScore + "/6)" : "No")
                + "   Control: " + (IsControlEnabled ? "Enabled" : "DISABLED"));
            text.AppendLine("Pointer (" + PointerHandName + "): " + PointerSession + "   " + PointerState
                + "   lock " + LockDisplacement.ToString("0") + " px");
            text.AppendLine("R: " + RightHandState + "   L: " + LeftHandState);
            text.AppendLine(SecondaryHandName + " clutch: " + ClutchState
                + "   Secondary: " + SecondaryMode + "   Clap: " + ClapState);
            text.AppendLine("Gesture: " + Gesture + "   Last action: " + LastAction);
            text.AppendLine("Scroll neutral: " + (ScrollNeutral.HasValue
                ? ScrollNeutral.Value.ToString("0.00") + " m  off " + ScrollOffset.ToString("+0.00;-0.00;0.00")
                    + "  rate " + ScrollRate.ToString("+0.0;-0.0;0.0") + "/s"
                : "-"));
            text.AppendLine("Frame " + FrameDeltaMs.ToString("0.0") + " ms (max " + FrameDeltaMaxMs.ToString("0")
                + ")  noise " + ResidualNoise.ToString("0") + " px  glitches " + GlitchCount);
            text.AppendLine("R hand height " + ControlHandHeight.ToString("0.00")
                + " m   forward " + ControlHandForward.ToString("0.00") + " m");
            text.AppendLine("Desktop: " + DesktopBounds);
            text.Append("Calibration: " + Calibration);
            return text.ToString();
        }
    }
}
