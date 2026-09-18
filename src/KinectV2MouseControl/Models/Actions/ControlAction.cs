namespace KinectV2MouseControl
{
    /// <summary>
    /// Semantic intents the system can carry out, independent of how they were triggered.
    ///
    /// Recognizers emit these rather than calling Win32 directly, so the same vocabulary can
    /// later be driven by voice, a hotkey, or a scripted command without touching any
    /// recognizer. Adding a new capability means adding a member here and a case in
    /// ActionRouter - no recognizer changes.
    ///
    /// Pointer movement is deliberately absent; see ActionRouter for why.
    /// </summary>
    public enum ControlActionType
    {
        None = 0,

        LeftMouseDown,
        LeftMouseUp,
        LeftClick,
        RightClick,

        /// <summary>
        /// Wheel movement. Value carries signed notches, positive scrolling up.
        /// </summary>
        Scroll,

        NextWindow,
        PreviousWindow,

        /// <summary>
        /// Turns Kinect control of the machine on or off. Unlike the others this acts on the
        /// application rather than on Windows, but it is modelled as an action for the same
        /// reason as the rest: a voice command should be able to invoke it later without
        /// knowing anything about how gestures work.
        /// </summary>
        ToggleControl
    }

    /// <summary>
    /// The master on/off switch for Kinect control, as seen by the action layer.
    ///
    /// Body tracking deliberately keeps running while control is off, since the gesture that
    /// switches it back on has to be observed somehow.
    /// </summary>
    public interface IControlGate
    {
        bool IsControlEnabled { get; }

        void ToggleControl();
    }

    /// <summary>
    /// An intent plus whatever payload it needs. A struct so the per-frame recognizers can emit
    /// actions without allocating.
    /// </summary>
    public struct ControlAction
    {
        public ControlActionType Type;

        /// <summary>
        /// Numeric payload. Currently only Scroll uses it, as signed wheel notches.
        /// </summary>
        public double Value;

        /// <summary>
        /// Text payload for future actions that need a target, such as LaunchApp.
        /// </summary>
        public string Parameter;

        public ControlAction(ControlActionType type, double value = 0, string parameter = null)
        {
            Type = type;
            Value = value;
            Parameter = parameter;
        }

        public static ControlAction Of(ControlActionType type)
        {
            return new ControlAction(type);
        }

        public static ControlAction ScrollBy(double notches)
        {
            return new ControlAction(ControlActionType.Scroll, notches);
        }

        public override string ToString()
        {
            if (Type == ControlActionType.Scroll)
            {
                // Direction only. Value is the fraction requested this frame, not the whole
                // notches actually submitted, so printing it would just be confusing.
                return Value >= 0 ? "Scroll up" : "Scroll down";
            }

            return Type.ToString();
        }
    }

    /// <summary>
    /// Destination for emitted intents. Recognizers depend on this rather than on ActionRouter,
    /// so they can be exercised without any Win32 involvement.
    /// </summary>
    public interface IActionSink
    {
        void Execute(ControlAction action);
    }
}
