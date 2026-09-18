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

    public enum VoiceCommandKind
    {
        /// <summary>
        /// Fixed phrase(s) bound to one catalog action.
        /// </summary>
        Phrase,

        /// <summary>
        /// "volume 0-100" and its variants. The grammar lists every number, the parser reads it.
        /// </summary>
        VolumePattern,

        /// <summary>
        /// "cancel" / "never mind": closes the command window with no action.
        /// </summary>
        Cancel
    }

    /// <summary>
    /// A spoken command available after the wake word. Local-first: phrases are matched by the
    /// in-process Windows recognizer against a closed grammar, and only while a command window
    /// is open (see WakeGatedVoiceEngine), so nothing leaves the machine.
    ///
    /// Future custom and assistant-routed commands use this same shape, which is why Source
    /// exists even though only BuiltIn entries are created.
    /// </summary>
    public class VoiceCommand
    {
        public string Id { get; set; }

        /// <summary>
        /// Canonical phrase, as shown in the command list.
        /// </summary>
        public string Phrase { get; set; }

        /// <summary>
        /// Alternative phrasings that trigger the same command.
        /// </summary>
        public string[] Aliases { get; set; }

        public string Description { get; set; }
        public ActionCategory Category { get; set; }

        /// <summary>
        /// The catalog action this command executes. Null for Cancel.
        /// </summary>
        public ActionDescriptor Action { get; set; }

        public VoiceCommandKind Kind { get; set; }

        public VoiceCommandSource Source { get; set; }

        public VoiceCommand()
        {
            Aliases = new string[0];
            Kind = VoiceCommandKind.Phrase;
            Source = VoiceCommandSource.BuiltIn;
        }

        /// <summary>
        /// False for commands shown for orientation only, whose action is not implemented yet.
        /// They are left out of the grammar and the parser.
        /// </summary>
        public bool IsAvailable
        {
            get
            {
                return Kind == VoiceCommandKind.Cancel || (Action != null && Action.IsImplemented);
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

        /// <summary>
        /// Phrases the recognizer should listen for. Empty for the volume pattern, whose
        /// grammar is generated separately.
        /// </summary>
        public IEnumerable<string> AllPhrases()
        {
            if (Kind == VoiceCommandKind.VolumePattern)
            {
                yield break;
            }

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
    ///
    /// Phrases are deliberately distinctive. Conversational aliases the first version carried -
    /// "wake up", "go to sleep", "skip", "louder", "quieter" - are gone: they are things people
    /// say to each other, and the old always-on grammar matched them out of normal speech.
    /// </summary>
    public static class VoiceCommandCatalog
    {
        /// <summary>
        /// Fixed. It is a word nobody says by accident, and the recognizer is given its exact
        /// pronunciation (see VoiceGrammars).
        /// </summary>
        public const string WakeWord = "Kinect";

        public static readonly VoiceCommand[] BuiltIn = new VoiceCommand[]
        {
            // System
            Bind("controlon", "control on", "Enable gesture control.", "enable control"),
            Bind("controloff", "control off", "Put gesture control on standby.", "disable control"),
            Bind("togglecontrol", "toggle control", "Flip gesture control."),
            Bind("calibrate", "calibrate", "Start pointer calibration.", "calibrate pointer"),
            Bind("open", "open control center", "Show the full KINECT-OS window.", "show control center"),
            Bind("compact", "compact mode", "Collapse to the floating widget."),
            Cancel("cancel", "Close the command window without doing anything.", "never mind"),

            // Windows
            Bind("nextwindow", "next window", "Switch to the next window."),
            Bind("prevwindow", "previous window", "Switch to the previous window."),
            Bind("taskview", "task view", "Open the task switcher."),
            Bind("showdesktop", "show desktop", "Minimize everything."),

            // Window management
            Bind("maximize", "maximize", "Maximize the foreground window.", "maximize window"),
            Bind("minimize", "minimize", "Minimize the foreground window.", "minimize window"),
            Bind("snapleft", "snap left", "Snap the window to the left half."),
            Bind("snapright", "snap right", "Snap the window to the right half."),
            Bind("closewindow", "close window", "Close the foreground window."),

            // Pointer
            Bind("click", "click", "Left click at the cursor."),
            Bind("rightclick", "right click", "Right click at the cursor."),
            Bind("scrollup", "scroll up", "Scroll up a few notches."),
            Bind("scrolldown", "scroll down", "Scroll down a few notches."),

            // Media
            Volume("volume 0–100", "Set the Windows volume directly, e.g. “volume seventy”.",
                "set volume to fifty", "volume forty percent"),
            Bind("volumeup", "volume up", "Turn the volume up 10%."),
            Bind("volumedown", "volume down", "Turn the volume down 10%."),
            Bind("mute", "mute", "Mute the speakers."),
            Bind("unmute", "unmute", "Unmute the speakers."),
            Bind("playpause", "play", "Media play / pause key.", "pause", "play pause"),
            Bind("nexttrack", "next track", "Skip to the next track."),
            Bind("prevtrack", "previous track", "Go back a track."),

            // Planned (shown, not in the grammar)
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

        private static VoiceCommand Volume(string display, string description, params string[] examples)
        {
            VoiceCommand command = Bind("setvolume", display, description, examples);
            command.Kind = VoiceCommandKind.VolumePattern;
            return command;
        }

        private static VoiceCommand Cancel(string phrase, string description, params string[] aliases)
        {
            VoiceCommand command = new VoiceCommand();
            command.Id = "cancel";
            command.Phrase = phrase;
            command.Aliases = aliases ?? new string[0];
            command.Description = description;
            command.Kind = VoiceCommandKind.Cancel;
            command.Category = ActionCategory.System;
            return command;
        }
    }
}
