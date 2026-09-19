using System.Collections.Generic;

namespace KinectV2MouseControl
{
    public enum ActionCategory
    {
        Pointer,
        Windows,
        WindowManagement,
        Media,
        System,
        Apps,
        Intelligence
    }

    /// <summary>
    /// One thing the system can do, described for people. The catalog is what the Actions page
    /// and the voice grammar are built from, so an action that exists in the router but not
    /// here is invisible to the control center.
    ///
    /// Trigger text is descriptive only: which gesture produces the action is decided by the
    /// recognizers, and which phrase produces it by VoiceCommandCatalog.
    /// </summary>
    public class ActionDescriptor
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public ActionCategory Category { get; set; }

        /// <summary>
        /// The router action this descriptor executes. Type None for entries that are only
        /// planned, or that the shell handles itself (ShellCommand).
        /// </summary>
        public ControlAction Action { get; set; }

        /// <summary>
        /// For actions carried out by the control center rather than by the router: "open",
        /// "compact", "calibrate".
        /// </summary>
        public string ShellCommand { get; set; }

        /// <summary>
        /// False for planned actions that have no implementation behind them yet.
        /// </summary>
        public bool IsImplemented { get; set; }

        /// <summary>
        /// True when the action makes sense to fire from a button in the control center. Mouse
        /// button actions would just click the button itself, so they are excluded.
        /// </summary>
        public bool CanRunFromUi { get; set; }

        public string GestureTrigger { get; set; }
        public string VoiceTrigger { get; set; }

        public string StatusText
        {
            get
            {
                return IsImplemented ? "Available" : "Planned";
            }
        }

        public string TriggerText
        {
            get
            {
                List<string> parts = new List<string>();
                if (!string.IsNullOrEmpty(GestureTrigger))
                {
                    parts.Add("Gesture: " + GestureTrigger);
                }

                if (!string.IsNullOrEmpty(VoiceTrigger))
                {
                    parts.Add("Voice: wake word ✦ “" + VoiceTrigger + "”");
                }

                if (parts.Count == 0)
                {
                    return IsImplemented ? "No default trigger yet" : "Not wired yet";
                }

                return string.Join("   ·   ", parts.ToArray());
            }
        }
    }

    /// <summary>
    /// Everything the action layer can (or will) do, grouped for display. Intent flows
    /// Input → Intent → Action: gestures and voice produce ControlActions, the router carries
    /// them out, and this list is the human-readable index of that vocabulary.
    /// </summary>
    public static class ActionCatalog
    {
        public static readonly ActionDescriptor[] All = new ActionDescriptor[]
        {
            // ---- Pointer & mouse (gesture-owned) -------------------------------------------
            Gesture("pointer", "Pointer", "Move the cursor with the right hand. Continuous, so it never goes through the action router.",
                ActionCategory.Pointer, ControlActionType.None, "Right hand raised forward", null, false),
            Gesture("grip", "Click / drag", "Press and hold the left mouse button while the right fist is closed.",
                ActionCategory.Pointer, ControlActionType.LeftMouseDown, "Right fist", null, false),
            Gesture("click", "Click", "A single left click.",
                ActionCategory.Pointer, ControlActionType.LeftClick, "Hover (hover mode) / lift (lift mode)", "click", false),
            Gesture("rightclick", "Right click", "Opens the context menu under the cursor.",
                ActionCategory.Pointer, ControlActionType.RightClick, "Right-hand lasso", "right click", false),
            Scroll("scrollup", "Scroll up", "Wheel scroll away from you.", "Left fist clutch, raise", "scroll up", 3),
            Scroll("scrolldown", "Scroll down", "Wheel scroll toward you.", "Left fist clutch, lower", "scroll down", -3),

            // ---- Windows -----------------------------------------------------------------
            Routed("nextwindow", "Next window", "Alt+Tab forward through open windows.",
                ActionCategory.Windows, ControlActionType.NextWindow, "Left fist swipe right", "next window"),
            Routed("prevwindow", "Previous window", "Alt+Tab backward.",
                ActionCategory.Windows, ControlActionType.PreviousWindow, "Left fist swipe left", "previous window"),
            Routed("taskview", "Task view", "Opens the Windows task switcher (Win+Tab).",
                ActionCategory.Windows, ControlActionType.TaskView, null, "task view"),
            Routed("showdesktop", "Show desktop", "Minimizes everything (Win+D). Say it again to restore.",
                ActionCategory.Windows, ControlActionType.ShowDesktop, null, "show desktop"),

            // ---- Window management -------------------------------------------------------
            Routed("maximize", "Maximize window", "Maximizes the foreground window (Win+Up).",
                ActionCategory.WindowManagement, ControlActionType.MaximizeWindow, null, "maximize"),
            Routed("minimize", "Minimize window", "Minimizes the foreground window (Win+Down).",
                ActionCategory.WindowManagement, ControlActionType.MinimizeWindow, null, "minimize"),
            Routed("snapleft", "Snap left", "Snaps the foreground window to the left half (Win+Left).",
                ActionCategory.WindowManagement, ControlActionType.SnapWindowLeft, null, "snap left"),
            Routed("snapright", "Snap right", "Snaps the foreground window to the right half (Win+Right).",
                ActionCategory.WindowManagement, ControlActionType.SnapWindowRight, null, "snap right"),
            RoutedNoRun("closewindow", "Close window", "Closes the foreground window (Alt+F4). Not offered as a button here, because the foreground window would be this one.",
                ActionCategory.WindowManagement, ControlActionType.CloseWindow, null, "close window"),
            Planned("movedisplay", "Move to display", "Send the foreground window to another monitor.",
                ActionCategory.WindowManagement, "move to display"),

            // ---- Media -------------------------------------------------------------------
            Routed("playpause", "Play / pause", "Media play-pause key.", ActionCategory.Media, ControlActionType.PlayPause, null, "play / pause"),
            Routed("nexttrack", "Next track", "Media next-track key.", ActionCategory.Media, ControlActionType.NextTrack, null, "next track"),
            Routed("prevtrack", "Previous track", "Media previous-track key.", ActionCategory.Media, ControlActionType.PreviousTrack, null, "previous track"),
            SetVolume("setvolume", "Set volume", "Sets the Windows master volume to an exact level, 0-100%. Unmutes when the level is above zero.",
                "volume 0–100"),
            Routed("volumeup", "Volume up", "System volume up 10% (five media-key steps, with the Windows volume overlay).", ActionCategory.Media, ControlActionType.VolumeUp, null, "volume up"),
            Routed("volumedown", "Volume down", "System volume down 10%.", ActionCategory.Media, ControlActionType.VolumeDown, null, "volume down"),
            Routed("mute", "Mute", "Mutes the speakers (explicit, not a toggle).", ActionCategory.Media, ControlActionType.Mute, null, "mute"),
            Routed("unmute", "Unmute", "Unmutes the speakers.", ActionCategory.Media, ControlActionType.Unmute, null, "unmute"),

            // ---- System ------------------------------------------------------------------
            Routed("togglecontrol", "Toggle Kinect control", "Switches gesture control off or on. Tracking keeps running while off.",
                ActionCategory.System, ControlActionType.ToggleControl, "Double clap", "toggle control"),
            Routed("controlon", "Kinect control on", "Explicitly enables gesture control.",
                ActionCategory.System, ControlActionType.EnableControl, null, "control on"),
            Routed("controloff", "Kinect control off", "Explicitly disables gesture control (standby).",
                ActionCategory.System, ControlActionType.DisableControl, null, "control off"),
            Shell("calibrate", "Calibrate pointer", "Starts the guided five-point calibration.", ActionCategory.System, "calibrate", "calibrate"),
            Shell("open", "Open control center", "Brings the full KINECT-OS window back from compact mode.", ActionCategory.System, "open", "open control center"),
            Shell("compact", "Compact mode", "Hides the window and shows the floating status widget.", ActionCategory.System, "compact", "compact mode"),

            // ---- Apps --------------------------------------------------------------------
            Custom("launch", "Launch app", "Starts a program, shortcut, file or URL. Give it a phrase as a custom voice command (Voice page).",
                ControlAction.Launch(null)),
            Custom("keys", "Key combination", "Presses a key combination in the window in front, e.g. an app's dictation shortcut. Give it a phrase as a custom voice command (Voice page).",
                ControlAction.Keys(null)),

            // ---- Intelligence ------------------------------------------------------------
            Planned("intent", "Natural-language intent", "An assistant turns a spoken request into one or more actions from this catalog.",
                ActionCategory.Intelligence, null),
            Planned("workflow", "Workflows", "Named multi-step routines, e.g. “focus mode” = mute, maximize, next window.",
                ActionCategory.Intelligence, null)
        };

        public static string DescribeCategory(ActionCategory category)
        {
            switch (category)
            {
                case ActionCategory.Pointer: return "Pointer & mouse";
                case ActionCategory.Windows: return "Windows";
                case ActionCategory.WindowManagement: return "Window management";
                case ActionCategory.Media: return "Media";
                case ActionCategory.System: return "System";
                case ActionCategory.Apps: return "App launching";
                case ActionCategory.Intelligence: return "AI & workflows";
                default: return category.ToString();
            }
        }

        public static string DescribeCategoryDetail(ActionCategory category)
        {
            switch (category)
            {
                case ActionCategory.Pointer: return "Owned by the right hand. Pointer movement bypasses the router on purpose.";
                case ActionCategory.Windows: return "Switching between what is open.";
                case ActionCategory.WindowManagement: return "Acts on whichever window is in the foreground.";
                case ActionCategory.Media: return "Media keys for the active player; exact volume and mute through Windows Core Audio.";
                case ActionCategory.System: return "Actions on KINECT-OS itself.";
                case ActionCategory.Apps: return "Starting things and pressing app shortcuts, through custom voice commands.";
                case ActionCategory.Intelligence: return "Reserved for the assistant layer.";
                default: return "";
            }
        }

        public static ActionDescriptor Find(string id)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (All[i].Id == id)
                {
                    return All[i];
                }
            }

            return null;
        }

        private static ActionDescriptor Gesture(string id, string name, string description, ActionCategory category,
            ControlActionType type, string gesture, string voice, bool canRun)
        {
            ActionDescriptor d = new ActionDescriptor();
            d.Id = id;
            d.Name = name;
            d.Description = description;
            d.Category = category;
            d.Action = ControlAction.Of(type);
            d.IsImplemented = true;
            d.CanRunFromUi = canRun;
            d.GestureTrigger = gesture;
            d.VoiceTrigger = voice;
            return d;
        }

        private static ActionDescriptor SetVolume(string id, string name, string description, string voice)
        {
            // Needs a level, which a Run button cannot supply; voice is the way in.
            ActionDescriptor d = Gesture(id, name, description, ActionCategory.Media, ControlActionType.SetVolume, null, voice, false);
            d.Action = ControlAction.SetVolumeTo(50);
            return d;
        }

        private static ActionDescriptor Scroll(string id, string name, string description, string gesture, string voice, double notches)
        {
            ActionDescriptor d = Gesture(id, name, description, ActionCategory.Pointer, ControlActionType.Scroll, gesture, voice, true);
            d.Action = ControlAction.ScrollBy(notches);
            return d;
        }

        private static ActionDescriptor Routed(string id, string name, string description, ActionCategory category,
            ControlActionType type, string gesture, string voice)
        {
            return Gesture(id, name, description, category, type, gesture, voice, true);
        }

        private static ActionDescriptor RoutedNoRun(string id, string name, string description, ActionCategory category,
            ControlActionType type, string gesture, string voice)
        {
            return Gesture(id, name, description, category, type, gesture, voice, false);
        }

        private static ActionDescriptor Shell(string id, string name, string description, ActionCategory category, string shellCommand, string voice)
        {
            ActionDescriptor d = new ActionDescriptor();
            d.Id = id;
            d.Name = name;
            d.Description = description;
            d.Category = category;
            d.Action = ControlAction.Of(ControlActionType.None);
            d.ShellCommand = shellCommand;
            d.IsImplemented = true;
            d.CanRunFromUi = true;
            d.VoiceTrigger = voice;
            return d;
        }

        /// <summary>
        /// Implemented, but needs a target only a custom voice command supplies, so it has no
        /// Run button and no built-in phrase.
        /// </summary>
        private static ActionDescriptor Custom(string id, string name, string description, ControlAction action)
        {
            ActionDescriptor d = new ActionDescriptor();
            d.Id = id;
            d.Name = name;
            d.Description = description;
            d.Category = ActionCategory.Apps;
            d.Action = action;
            d.IsImplemented = true;
            d.CanRunFromUi = false;
            d.VoiceTrigger = "your phrase";
            return d;
        }

        private static ActionDescriptor Planned(string id, string name, string description, ActionCategory category, string voice)
        {
            ActionDescriptor d = new ActionDescriptor();
            d.Id = id;
            d.Name = name;
            d.Description = description;
            d.Category = category;
            d.Action = ControlAction.Of(ControlActionType.None);
            d.IsImplemented = false;
            d.CanRunFromUi = false;
            d.VoiceTrigger = voice;
            return d;
        }
    }
}
