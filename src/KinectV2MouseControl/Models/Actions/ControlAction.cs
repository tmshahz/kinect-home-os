namespace KinectV2MouseControl
{
    /// <summary>
    /// Semantic intents the system can carry out, independent of how they were triggered.
    ///
    /// Recognizers emit these rather than calling Win32 directly, so the same vocabulary can
    /// be driven by voice, a hotkey, or a scripted command without touching any recognizer.
    /// Adding a new capability means adding a member here, a case in ActionRouter and a
    /// descriptor in ActionCatalog - no recognizer changes.
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
        ToggleControl,

        // ---- Added with the KINECT-OS control center. Voice and the Actions page use these;
        // no gesture emits them yet. --------------------------------------------------------

        /// <summary>
        /// Explicit on/off, for inputs that know which way they mean ("control on").
        /// </summary>
        EnableControl,
        DisableControl,

        /// <summary>
        /// Window management chords, acting on whatever window is in the foreground.
        /// </summary>
        MaximizeWindow,
        MinimizeWindow,
        SnapWindowLeft,
        SnapWindowRight,
        ShowDesktop,
        TaskView,
        CloseWindow,

        /// <summary>
        /// Media keys. Windows routes them to whichever player is active.
        /// </summary>
        PlayPause,
        NextTrack,
        PreviousTrack,
        VolumeUp,
        VolumeDown,
        VolumeMute,

        /// <summary>
        /// Starts a program, document or URL. Parameter carries the target.
        /// </summary>
        LaunchApp
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

        /// <summary>
        /// Explicit form. <paramref name="source"/> names who asked ("double clap", "voice",
        /// "control center") and only feeds the log and the activity feed.
        /// </summary>
        void SetControlEnabled(bool enabled, string source);
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
        /// Text payload for actions that need a target, such as LaunchApp.
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

        public static ControlAction Launch(string target)
        {
            return new ControlAction(ControlActionType.LaunchApp, 0, target);
        }

        /// <summary>
        /// Human-readable name, shared by the diagnostics readout, the activity feed and the
        /// control center's action catalog.
        /// </summary>
        public static string Describe(ControlActionType type)
        {
            switch (type)
            {
                case ControlActionType.LeftMouseDown: return "Grip press";
                case ControlActionType.LeftMouseUp: return "Grip release";
                case ControlActionType.LeftClick: return "Click";
                case ControlActionType.RightClick: return "Right click";
                case ControlActionType.Scroll: return "Scroll";
                case ControlActionType.NextWindow: return "Next window";
                case ControlActionType.PreviousWindow: return "Previous window";
                case ControlActionType.ToggleControl: return "Toggle control";
                case ControlActionType.EnableControl: return "Control on";
                case ControlActionType.DisableControl: return "Control off";
                case ControlActionType.MaximizeWindow: return "Maximize window";
                case ControlActionType.MinimizeWindow: return "Minimize window";
                case ControlActionType.SnapWindowLeft: return "Snap window left";
                case ControlActionType.SnapWindowRight: return "Snap window right";
                case ControlActionType.ShowDesktop: return "Show desktop";
                case ControlActionType.TaskView: return "Task view";
                case ControlActionType.CloseWindow: return "Close window";
                case ControlActionType.PlayPause: return "Play / pause";
                case ControlActionType.NextTrack: return "Next track";
                case ControlActionType.PreviousTrack: return "Previous track";
                case ControlActionType.VolumeUp: return "Volume up";
                case ControlActionType.VolumeDown: return "Volume down";
                case ControlActionType.VolumeMute: return "Mute";
                case ControlActionType.LaunchApp: return "Launch app";
                default: return type.ToString();
            }
        }

        public override string ToString()
        {
            if (Type == ControlActionType.Scroll)
            {
                // Direction only. Value is the fraction requested this frame, not the whole
                // notches actually submitted, so printing it would just be confusing.
                return Value >= 0 ? "Scroll up" : "Scroll down";
            }

            if (Type == ControlActionType.LaunchApp && !string.IsNullOrEmpty(Parameter))
            {
                return "Launch " + Parameter;
            }

            return Describe(Type);
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
