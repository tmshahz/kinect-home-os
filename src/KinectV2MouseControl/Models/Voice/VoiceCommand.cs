using System.Collections.Generic;

namespace KinectV2MouseControl
{
    public enum VoiceCommandSource
    {
        /// <summary>
        /// Ships with KINECT-OS.
        /// </summary>
        BuiltIn,

        /// <summary>
        /// Defined by the user (planned: not editable in this version).
        /// </summary>
        Custom,

        /// <summary>
        /// Interpreted by the assistant layer (planned).
        /// </summary>
        Assistant
    }

    /// <summary>
    /// A spoken phrase bound to an action from the catalog. Local-first by design: the phrase
    /// is matched by the in-process Windows speech recognizer against a fixed grammar, so
    /// nothing leaves the machine and nothing needs a network.
    ///
    /// Future custom commands and assistant-routed commands use this same shape, which is why
    /// Source exists now even though only BuiltIn entries are created.
    /// </summary>
    public class VoiceCommand
    {
        public string Id { get; set; }

        /// <summary>
        /// Canonical phrase, spoken after the wake word (if one is set).
        /// </summary>
        public string Phrase { get; set; }

        /// <summary>
        /// Alternative phrasings that trigger the same command.
        /// </summary>
        public string[] Aliases { get; set; }

        public string Description { get; set; }
        public ActionCategory Category { get; set; }

        /// <summary>
        /// The catalog action this command executes.
        /// </summary>
        public ActionDescriptor Action { get; set; }

        public VoiceCommandSource Source { get; set; }

        public VoiceCommand()
        {
            Aliases = new string[0];
            Source = VoiceCommandSource.BuiltIn;
        }

        /// <summary>
        /// False for commands shown for orientation only, whose action is not implemented yet.
        /// They are left out of the grammar.
        /// </summary>
        public bool IsAvailable
        {
            get
            {
                return Action != null && Action.IsImplemented;
            }
        }

        public string StatusText
        {
            get
            {
                if (!IsAvailable)
                {
                    return "Planned";
                }

                return Source == VoiceCommandSource.BuiltIn ? "Built in" : Source.ToString();
            }
        }

        public string AliasText
        {
            get
            {
                return Aliases.Length == 0 ? "" : "also: " + string.Join(", ", Aliases);
            }
        }

        public IEnumerable<string> AllPhrases()
        {
            yield return Phrase;
            for (int i = 0; i < Aliases.Length; i++)
            {
                yield return Aliases[i];
            }
        }
    }

    /// <summary>
    /// The built-in local vocabulary. Every entry maps onto an ActionCatalog descriptor, so
    /// voice can never do something the Actions page does not list.
    /// </summary>
    public static class VoiceCommandCatalog
    {
        public const string DefaultWakeWord = "Kinect";

        public static readonly VoiceCommand[] BuiltIn = new VoiceCommand[]
        {
            Bind("nextwindow", "next window", "Switch to the next window.", "switch window"),
            Bind("prevwindow", "previous window", "Switch to the previous window.", "last window"),
            Bind("taskview", "task view", "Open the task switcher."),
            Bind("showdesktop", "show desktop", "Minimize everything."),
            Bind("maximize", "maximize", "Maximize the foreground window.", "maximize window"),
            Bind("minimize", "minimize", "Minimize the foreground window.", "minimize window"),
            Bind("snapleft", "snap left", "Snap the window to the left half."),
            Bind("snapright", "snap right", "Snap the window to the right half."),
            Bind("closewindow", "close window", "Close the foreground window."),
            Bind("click", "click", "Left click at the cursor."),
            Bind("rightclick", "right click", "Right click at the cursor."),
            Bind("scrollup", "scroll up", "Scroll up a few notches.", "page up"),
            Bind("scrolldown", "scroll down", "Scroll down a few notches.", "page down"),
            Bind("playpause", "play", "Play or pause media.", "pause", "play pause"),
            Bind("nexttrack", "next track", "Skip to the next track.", "skip"),
            Bind("prevtrack", "previous track", "Go back a track."),
            Bind("volumeup", "volume up", "Turn the volume up.", "louder"),
            Bind("volumedown", "volume down", "Turn the volume down.", "quieter"),
            Bind("mute", "mute", "Toggle mute.", "unmute"),
            Bind("controlon", "control on", "Enable gesture control.", "wake up", "enable control"),
            Bind("controloff", "control off", "Put gesture control on standby.", "go to sleep", "disable control"),
            Bind("togglecontrol", "toggle control", "Flip gesture control."),
            Bind("calibrate", "calibrate", "Start pointer calibration.", "calibrate pointer"),
            Bind("open", "open control center", "Show the full KINECT-OS window.", "show control center"),
            Bind("compact", "compact mode", "Collapse to the floating widget.", "minimize kinect"),
            Bind("launch", "open …", "Launch a named app (custom commands, planned)."),
            Bind("movedisplay", "move to display …", "Move the window to another monitor (planned).")
        };

        private static VoiceCommand Bind(string actionId, string phrase, string description, params string[] aliases)
        {
            ActionDescriptor action = ActionCatalog.Find(actionId);
            VoiceCommand command = new VoiceCommand();
            command.Id = actionId;
            command.Phrase = phrase;
            command.Aliases = aliases ?? new string[0];
            command.Description = description;
            command.Action = action;
            command.Category = action != null ? action.Category : ActionCategory.System;
            return command;
        }
    }
}
