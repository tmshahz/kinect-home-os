namespace KinectV2MouseControl
{
    /// <summary>
    /// Overall state of the system as one word, for the big indicator on Home and the compact
    /// widget's status orb.
    /// </summary>
    public enum ControlState
    {
        /// <summary>
        /// Control mode Disabled: the sensor is closed.
        /// </summary>
        Off,

        /// <summary>
        /// Sensor on, but Kinect control switched off (double clap / control center).
        /// </summary>
        Standby,

        /// <summary>
        /// Control on, waiting for the latched pointer hand.
        /// </summary>
        Ready,

        /// <summary>
        /// The latched pointer hand is driving the cursor.
        /// </summary>
        Active
    }

    /// <summary>
    /// Bindable snapshot of what the engine is doing, refreshed by the view model's
    /// diagnostics timer from GestureDiagnostics. Every property raises only on change, so the
    /// many bindings across the control center cost nothing while the picture is static.
    /// </summary>
    public class LiveStatus : ObservableObject
    {
        private ControlState controlState;
        public ControlState ControlState { get { return controlState; } set { if (Set(ref controlState, value)) { Raise("ControlStateText"); Raise("IsActive"); Raise("IsOff"); } } }

        public string ControlStateText
        {
            get
            {
                switch (controlState)
                {
                    case ControlState.Off: return "Off";
                    case ControlState.Standby: return "Standby";
                    case ControlState.Ready: return "Ready";
                    default: return "Active";
                }
            }
        }

        public bool IsActive { get { return controlState == ControlState.Active; } }
        public bool IsOff { get { return controlState == ControlState.Off; } }

        private bool isEngineOn;
        public bool IsEngineOn { get { return isEngineOn; } set { Set(ref isEngineOn, value); } }

        private bool isControlEnabled = true;
        public bool IsControlEnabled { get { return isControlEnabled; } set { Set(ref isControlEnabled, value); } }

        private string controlDetail = "";
        public string ControlDetail { get { return controlDetail; } set { Set(ref controlDetail, value); } }

        // ---- Sensor and tracking -------------------------------------------------------------

        private bool isSensorConnected;
        public bool IsSensorConnected { get { return isSensorConnected; } set { Set(ref isSensorConnected, value); } }

        private string sensorText = "Off";
        public string SensorText { get { return sensorText; } set { Set(ref sensorText, value); } }

        private string sensorDetail = "";
        public string SensorDetail { get { return sensorDetail; } set { Set(ref sensorDetail, value); } }

        private bool isTracking;
        public bool IsTracking { get { return isTracking; } set { Set(ref isTracking, value); } }

        private string trackingText = "No body";
        public string TrackingText { get { return trackingText; } set { Set(ref trackingText, value); } }

        private string trackingShort = "No body";
        /// <summary>
        /// One or two words for tiles: "Locked", "No body", "Sensor off", "No signal".
        /// </summary>
        public string TrackingShort { get { return trackingShort; } set { Set(ref trackingShort, value); } }

        private string trackingDetail = "";
        public string TrackingDetail { get { return trackingDetail; } set { Set(ref trackingDetail, value); } }

        private int bodyCount;
        public int BodyCount { get { return bodyCount; } set { Set(ref bodyCount, value); } }

        private int bodyScore;
        public int BodyScore { get { return bodyScore; } set { Set(ref bodyScore, value); } }

        // ---- Mode ---------------------------------------------------------------------------

        private int modeIndex;
        public int ModeIndex { get { return modeIndex; } set { Set(ref modeIndex, value); } }

        private string modeText = "";
        public string ModeText { get { return modeText; } set { Set(ref modeText, value); } }

        private bool isGestureVocabularyOn;
        public bool IsGestureVocabularyOn { get { return isGestureVocabularyOn; } set { Set(ref isGestureVocabularyOn, value); } }

        // ---- Pointer ------------------------------------------------------------------------

        private string pointerSessionText = "Waiting";
        public string PointerSessionText { get { return pointerSessionText; } set { Set(ref pointerSessionText, value); } }

        private string pointerHandName = "Right";
        public string PointerHandName { get { return pointerHandName; } set { Set(ref pointerHandName, value); } }

        private string secondaryHandName = "Left";
        public string SecondaryHandName { get { return secondaryHandName; } set { Set(ref secondaryHandName, value); } }

        private bool isPointerActive;
        public bool IsPointerActive { get { return isPointerActive; } set { Set(ref isPointerActive, value); } }

        private string pointerStateText = "-";
        public string PointerStateText { get { return pointerStateText; } set { Set(ref pointerStateText, value); } }

        private bool isLocked;
        public bool IsLocked { get { return isLocked; } set { Set(ref isLocked, value); } }

        private double lockDisplacement;
        public double LockDisplacement { get { return lockDisplacement; } set { Set(ref lockDisplacement, value); } }

        // ---- Hands --------------------------------------------------------------------------

        private string rightHandText = "-";
        public string RightHandText { get { return rightHandText; } set { Set(ref rightHandText, value); } }

        private string leftHandText = "-";
        public string LeftHandText { get { return leftHandText; } set { Set(ref leftHandText, value); } }

        private bool rightHandTracked;
        public bool RightHandTracked { get { return rightHandTracked; } set { Set(ref rightHandTracked, value); } }

        private bool leftHandTracked;
        public bool LeftHandTracked { get { return leftHandTracked; } set { Set(ref leftHandTracked, value); } }

        private bool rightHandInZone;
        public bool RightHandInZone { get { return rightHandInZone; } set { Set(ref rightHandInZone, value); } }

        private bool leftHandInZone;
        public bool LeftHandInZone { get { return leftHandInZone; } set { Set(ref leftHandInZone, value); } }

        private bool rightHandClosed;
        public bool RightHandClosed { get { return rightHandClosed; } set { Set(ref rightHandClosed, value); } }

        private bool leftHandClosed;
        public bool LeftHandClosed { get { return leftHandClosed; } set { Set(ref leftHandClosed, value); } }

        private bool rightHandLasso;
        public bool RightHandLasso { get { return rightHandLasso; } set { Set(ref rightHandLasso, value); } }

        private bool leftHandLasso;
        public bool LeftHandLasso { get { return leftHandLasso; } set { Set(ref leftHandLasso, value); } }

        private string rightHandSummary = "Not tracked";
        public string RightHandSummary { get { return rightHandSummary; } set { Set(ref rightHandSummary, value); } }

        private string leftHandSummary = "Not tracked";
        public string LeftHandSummary { get { return leftHandSummary; } set { Set(ref leftHandSummary, value); } }

        // ---- Gestures -----------------------------------------------------------------------

        private bool isGripHeld;
        public bool IsGripHeld { get { return isGripHeld; } set { Set(ref isGripHeld, value); } }

        private bool isLassoActive;
        public bool IsLassoActive { get { return isLassoActive; } set { Set(ref isLassoActive, value); } }

        private bool isClutchArmed;
        public bool IsClutchArmed { get { return isClutchArmed; } set { Set(ref isClutchArmed, value); } }

        private bool isScrolling;
        public bool IsScrolling { get { return isScrolling; } set { Set(ref isScrolling, value); } }

        private bool isSwiping;
        public bool IsSwiping { get { return isSwiping; } set { Set(ref isSwiping, value); } }

        private bool isClapArmed;
        public bool IsClapArmed { get { return isClapArmed; } set { Set(ref isClapArmed, value); } }

        private string clutchText = "Off";
        public string ClutchText { get { return clutchText; } set { Set(ref clutchText, value); } }

        private string clapText = "Idle";
        public string ClapText { get { return clapText; } set { Set(ref clapText, value); } }

        private string gestureText = "None";
        public string GestureText { get { return gestureText; } set { Set(ref gestureText, value); } }

        private string secondaryModeText = "None";
        public string SecondaryModeText { get { return secondaryModeText; } set { Set(ref secondaryModeText, value); } }

        private string lastAction = "-";
        public string LastAction { get { return lastAction; } set { Set(ref lastAction, value); } }

        private string scrollText = "-";
        public string ScrollText { get { return scrollText; } set { Set(ref scrollText, value); } }

        private double scrollRate;
        public double ScrollRate { get { return scrollRate; } set { Set(ref scrollRate, value); } }

        // ---- Signal quality -------------------------------------------------------------------

        private double frameMs;
        public double FrameMs { get { return frameMs; } set { Set(ref frameMs, value); } }

        private double frameMaxMs;
        public double FrameMaxMs { get { return frameMaxMs; } set { Set(ref frameMaxMs, value); } }

        private double noisePx;
        public double NoisePx { get { return noisePx; } set { Set(ref noisePx, value); } }

        private int glitchCount;
        public int GlitchCount { get { return glitchCount; } set { Set(ref glitchCount, value); } }

        private string frameText = "-";
        public string FrameText { get { return frameText; } set { Set(ref frameText, value); } }

        private string signalText = "-";
        public string SignalText { get { return signalText; } set { Set(ref signalText, value); } }

        // ---- Geometry -------------------------------------------------------------------------

        private double handHeight;
        public double HandHeight { get { return handHeight; } set { Set(ref handHeight, value); } }

        private double handForward;
        public double HandForward { get { return handForward; } set { Set(ref handForward, value); } }

        private double handX;
        public double HandX { get { return handX; } set { Set(ref handX, value); } }

        private double leftHandHeight;
        public double LeftHandHeight { get { return leftHandHeight; } set { Set(ref leftHandHeight, value); } }

        private double leftHandX;
        public double LeftHandX { get { return leftHandX; } set { Set(ref leftHandX, value); } }

        private double leftHandForward;
        public double LeftHandForward { get { return leftHandForward; } set { Set(ref leftHandForward, value); } }

        private double cursorX;
        public double CursorX { get { return cursorX; } set { Set(ref cursorX, value); } }

        private double cursorY;
        public double CursorY { get { return cursorY; } set { Set(ref cursorY, value); } }

        private bool hasCursor;
        public bool HasCursor { get { return hasCursor; } set { Set(ref hasCursor, value); } }

        private string desktopText = "-";
        public string DesktopText { get { return desktopText; } set { Set(ref desktopText, value); } }

        private int monitorCount;
        public int MonitorCount { get { return monitorCount; } set { Set(ref monitorCount, value); } }

        // ---- Calibration ------------------------------------------------------------------------

        private string calibrationText = "Off";
        public string CalibrationText { get { return calibrationText; } set { Set(ref calibrationText, value); } }

        private bool isCalibrating;
        public bool IsCalibrating { get { return isCalibrating; } set { Set(ref isCalibrating, value); } }

        private bool isCalibrated;
        public bool IsCalibrated { get { return isCalibrated; } set { Set(ref isCalibrated, value); } }

        private int calibrationStep;
        public int CalibrationStep { get { return calibrationStep; } set { Set(ref calibrationStep, value); } }

        private double calibrationProgress;
        public double CalibrationProgress { get { return calibrationProgress; } set { Set(ref calibrationProgress, value); } }

        private string calibrationStepText = "";
        public string CalibrationStepText { get { return calibrationStepText; } set { Set(ref calibrationStepText, value); } }

        private string calibrationHint = "";
        public string CalibrationHint { get { return calibrationHint; } set { Set(ref calibrationHint, value); } }

        // ---- One-line summaries ----------------------------------------------------------------

        private string headline = "Off";
        /// <summary>
        /// The single most useful line: "Pointing · grip held", "Standby - double clap to resume"...
        /// </summary>
        public string Headline { get { return headline; } set { Set(ref headline, value); } }

        private string subline = "";
        public string Subline { get { return subline; } set { Set(ref subline, value); } }
    }
}
