using System.Collections.Generic;
using System.Text;

namespace KinectV2MouseControl
{
    /// <summary>
    /// One explanation of one control.
    /// </summary>
    public class HelpEntry
    {
        /// <summary>
        /// Must match the control's label exactly: that is how MainWindow attaches the tooltip.
        /// </summary>
        public string Title { get; private set; }
        public string What { get; private set; }
        public string Increase { get; private set; }
        public string Decrease { get; private set; }
        public string TooHigh { get; private set; }
        public string TooLow { get; private set; }

        public HelpEntry(string title, string what, string increase, string decrease, string tooHigh, string tooLow)
        {
            Title = title;
            What = what;
            Increase = increase;
            Decrease = decrease;
            TooHigh = tooHigh;
            TooLow = tooLow;
        }

        /// <summary>
        /// BuildText as a property, for data binding in the Settings guide.
        /// </summary>
        public string Summary
        {
            get
            {
                return BuildText();
            }
        }

        public string BuildText()
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine(What);
            if (Increase != null)
            {
                text.AppendLine("Higher: " + Increase);
            }

            if (Decrease != null)
            {
                text.AppendLine("Lower: " + Decrease);
            }

            if (TooHigh != null)
            {
                text.AppendLine("Too high: " + TooHigh);
            }

            if (TooLow != null)
            {
                text.Append("Too low: " + TooLow);
            }

            return text.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// Single source for every control explanation. Used both for the tooltips on the controls
    /// and for the Help window, so the two can never disagree.
    /// </summary>
    public static class ControlHelp
    {
        public const string Overview =
            "RIGHT hand = pointer. Raise it forward into the control zone; after a short settle the cursor follows it. " +
            "Close the right fist to press/drag, make a Lasso (two fingers) to right click.\n" +
            "LEFT hand = secondary gestures only, and only while its fist is CLOSED (the clutch). " +
            "Closed fist held still, then moved up/down = scroll. Closed fist swept quickly sideways = switch window. " +
            "An open left hand does nothing.\n" +
            "Double clap = turn Kinect control off / on (tracking keeps running while off).\n" +
            "Lasso, scroll and swipe work in 'Grip to press' mode. Hover over any tuning control for its explanation, or click its ? to pin it in the help drawer.";

        public static readonly List<HelpEntry> Entries = new List<HelpEntry>
        {
            new HelpEntry("Speech engine", "Whisper transcribes one request locally after the chime. Pause to finish. Windows uses the original command grammar and is the automatic fallback if Whisper is unavailable.", null, null, null, null),
            new HelpEntry("DeepSeek key", "Test and save your key encrypted for this Windows account. Leave the box empty to test the saved key. Remove key deletes the saved credential. The key is sent only to DeepSeek.", null, null, null, null),
            new HelpEntry("Assistant model", "Choose the DeepSeek model for other requests. Flash is the default. Thinking is disabled to reduce delay.", null, null, null, null),
            new HelpEntry("Send other requests to AI", "Exact built-in and custom commands stay local. When this is on and a key is saved, other request text can be sent to DeepSeek. Audio remains local.", null, null, null, null),
            new HelpEntry("Assistant request", "Run a typed request using the same exact-command and AI path as voice. Steps and results appear here and in Activity. Text entry acts on the foreground app; KINECT-OS itself refuses it.", null, null, null, null),
            new HelpEntry("Cancel assistant", "Cancel remaining steps. A new wake word, switching voice off, or double clap also cancels. Steps already completed are not undone.", null, null, null, null),
            new HelpEntry("Ask KINECT-OS", "No wake word needed. Opens the same command window as saying the wake word: the chime plays, then you have one request. Disabled while voice is off or a session is already open.", null, null, null, null),
            new HelpEntry("Widget chat panel", "Slide-down panel on the compact widget. An assistant request shows your question and the answer here, then closes after a few seconds. That automatic open is not remembered. Hover the panel, click the request box, or press the chevron to keep it open; the chevron is what is remembered across restarts. Recent steps stay listed, with a one-line request box and Cancel while a request is running.", null, null, null, null),
            new HelpEntry("Manual request", "Type a request on the compact widget without saying the wake word. Enter sends it through the same local-command then assistant path as the AI page. Click the box to give the widget keyboard focus; it does not steal keys until then.", null, null, null, null),
            new HelpEntry("End of listening sound", "A short click confirms that recording has finished and your request is being transcribed.", null, null, null, null),
            new HelpEntry("Movement scale",
                "How far the cursor moves per centimetre of hand movement, in the normal (uncalibrated) mapping. Ignored while Calibrated range is on.",
                "less arm movement needed to cross the desktop, but aiming gets twitchier.",
                "more precise, but you must reach further.",
                "hand shimmer becomes visible cursor jitter; small targets are hard to hit.",
                "the far edges of the desktop cannot be reached comfortably."),

            new HelpEntry("Cursor smoothing",
                "How strongly the adaptive filter calms the cursor while the hand is nearly still.",
                "steadier at rest, with a little more lag when you start moving.",
                "more immediate, but more shimmer.",
                "cursor feels heavy and trails behind the hand.",
                "cursor shakes while you try to hold still."),

            new HelpEntry("Speed responsiveness",
                "How much the filter opens up when the hand moves fast, so deliberate sweeps keep up.",
                "fast movements follow more tightly.",
                "the cursor stays smooth but lags on fast sweeps.",
                "some jitter leaks through while moving.",
                "quick sweeps feel rubbery and overshoot late."),

            new HelpEntry("Jitter dead zone (px)",
                "Tiny movements inside this radius are ignored. Beyond it the cursor trails smoothly (no jumps); the trailing shrinks as you move faster.",
                "absorbs more tremor and sensor shimmer.",
                "finer positioning is possible.",
                "slow, careful moves feel slightly sticky/behind.",
                "residual shimmer is visible when holding still."),

            new HelpEntry("Click freeze (s)",
                "How long the cursor stays pinned after a grip press is confirmed, before the hand can drag.",
                "clicks land more reliably on small targets.",
                "drags start sooner.",
                "drags feel delayed; the first part of a drag is lost.",
                "the cursor slips as the fist closes, clicking beside the target."),

            new HelpEntry("Pointer settle (s)",
                "When the right hand enters the zone (at startup and after any tracking loss), how long it must be tracked cleanly before it takes the cursor.",
                "more reliable, cleaner starts; the cursor takes over a little later.",
                "the cursor takes over sooner.",
                "noticeable wait before the cursor responds after raising the hand.",
                "startup/reacquisition can begin from a bad sample (jump or jitter burst)."),

            new HelpEntry("Lock radius (px)",
                "Stationary lock: the hand must stay within this radius, slowly, for the lock dwell time before the cursor is pinned.",
                "the lock engages more easily.",
                "the hand must be stiller to lock.",
                "slow deliberate moves get locked (cursor stops when you meant to move).",
                "the lock rarely engages, so resting shimmer shows."),

            new HelpEntry("Lock dwell (s)",
                "Stationary lock: how long the hand must be still before the cursor is pinned.",
                "fewer accidental locks during slow moves.",
                "the cursor pins sooner when you stop.",
                "the cursor wanders briefly every time you stop.",
                "brief pauses mid-movement lock the cursor."),

            new HelpEntry("Breakout radius (px)",
                "Stationary lock: how far the hand must move away from the locked point to unlock. Keep it above the lock radius.",
                "a locked cursor is harder to disturb (good for clicking).",
                "unlocking is quicker.",
                "you must move noticeably before the cursor responds after a pause.",
                "shimmer unlocks the cursor and it drifts again."),

            new HelpEntry("Hand range X (m)",
                "Calibrated range: width of the hand region that maps to the full desktop width. Set automatically by Calibrate.",
                "more arm movement per desktop width, more precise.",
                "less arm movement, more sensitive.",
                "desktop edges need an uncomfortable stretch.",
                "horizontal aiming is twitchy."),

            new HelpEntry("Hand range Y (m)",
                "Calibrated range: height of the hand region that maps to the full desktop height.",
                "more arm movement top-to-bottom, more precise.",
                "less arm movement, more sensitive.",
                "top/bottom edges need an uncomfortable stretch.",
                "vertical aiming is twitchy."),

            new HelpEntry("Hand centre X (m)",
                "Calibrated range: sideways position of the hand region. Positive moves it to your right.",
                "the region shifts right (hand further right for the same cursor spot).",
                "the region shifts left.",
                "the left edge is easy but the right edge is out of reach.",
                "the right edge is easy but the left edge is out of reach."),

            new HelpEntry("Pointer height (m)",
                "Hand height above your hips that maps to the vertical middle of the desktop. Calibrate adjusts it.",
                "you hold your hand higher to point at the middle.",
                "you point from a lower, more relaxed position.",
                "tiring arm; the bottom of the screen is hard to reach.",
                "the top of the screen needs an uncomfortable reach."),

            new HelpEntry("Activation height (m)",
                "Minimum hand height above your hips before a hand counts as 'in the zone' (pointer or clutch).",
                "hands resting low never engage.",
                "control engages from a lower, more relaxed hand.",
                "you must hold your arm up to keep control.",
                "a hand resting on your lap or armrest grabs the cursor."),

            new HelpEntry("Forward activation (m)",
                "How far in front of your hips a hand must be to count as 'in the zone'.",
                "only a clearly extended hand engages.",
                "control engages with the hand closer to the body.",
                "you must stretch forward to keep control.",
                "casual hand movement near the body engages control."),

            new HelpEntry("Scroll speed",
                "Scroll rate for the clutched left fist, at 10 cm past the dead zone (notches/s per metre).",
                "faster scrolling for the same hand offset.",
                "slower, more controllable scrolling.",
                "pages fly past; hard to stop where you want.",
                "long pages take too much time."),

            new HelpEntry("Scroll curve",
                "Shape of the scroll response. 1 = linear. Higher = gentler near neutral, faster further out.",
                "finer control for small offsets, bigger boost for large ones.",
                "more uniform response.",
                "small offsets barely scroll; large ones jump to full speed.",
                "(1.0 is the lowest useful value) small offsets scroll as fast as linear."),

            new HelpEntry("Swipe distance (m)",
                "How far the clutched left fist must sweep sideways (quickly) to switch window.",
                "fewer accidental window switches.",
                "switching needs a smaller sweep.",
                "swipes are often not recognized.",
                "normal repositioning with a closed left fist switches windows."),

            new HelpEntry("Hover-to-click range",
                "Hover to click mode only: how far (px) the cursor may move while the hover timer runs.",
                "clicks happen despite a slightly wandering hand.",
                "the hand must be stiller for a hover click.",
                "unintended clicks while moving slowly.",
                "hover clicks rarely complete."),

            new HelpEntry("Hover-to-click duration",
                "Hover to click mode only: seconds of hovering before a click.",
                "fewer accidental clicks.",
                "faster clicks.",
                "clicking feels slow.",
                "unintended clicks whenever you pause."),

            new HelpEntry("Stationary lock",
                "Pins the cursor completely once the hand is held still, so clicks land exactly. Released automatically while dragging.",
                null, null, null, null),

            new HelpEntry("Calibrated range",
                "On: the hand region set by Calibrate (or the Hand range / centre sliders) maps onto the whole desktop, with separate horizontal and vertical scaling. Movement scale is ignored. Off: the original uniform mapping with Movement scale. Untick to revert instantly.",
                null, null, null, null),

            new HelpEntry("Invert scroll",
                "Off: raising the clutched left fist scrolls up. On: raising it scrolls down.",
                null, null, null, null),

            new HelpEntry("Calibrate",
                "Guided 5-point capture with the RIGHT hand: centre, left, right, top, bottom. Hold each point still until it is captured; the prompt shows progress. Kinect control is paused while capturing; the button cancels. A 5% edge assist is applied so edges are reached just before full stretch.",
                null, null, null, null),

            new HelpEntry("Profiles",
                "Three slots for tuning experiments. Save stores every tuning and calibration value (not the control mode); Load applies them safely (buttons released, filters and sessions reset). Renaming a slot is saved immediately. Shared by Debug and Release builds.",
                null, null, null, null),

            // ---- Control center ----------------------------------------------------------------

            new HelpEntry("Kinect control",
                "The master switch. Off is standby: the sensor keeps tracking you but nothing moves or clicks, and a double clap (or this switch) brings control back. The Disabled control mode is different - it closes the sensor entirely.",
                null, null, null, null),

            new HelpEntry("Voice commands",
                "Wake-gated local voice control. Say the wake word (“Jarvis” unless you changed it) on its own and wait " +
                "for the chime; the next 4 seconds accept exactly one command, then it goes back to waiting for the wake word. " +
                "Nothing else is acted on: commands said without the wake cycle, a second command after the " +
                "first, and the wake word in the middle of a sentence are all ignored. Recognition runs on this PC " +
                "(Windows speech recognizer); commands go through the same action router as gestures. " +
                "If the Kinect starts reconnecting after enabling it, pick a different microphone below.",
                null, null, null, null),

            new HelpEntry("Wake word",
                "The word you say to get KINECT-OS's attention, “Jarvis” by default. Type one to three words and press Apply " +
                "(or Enter); the recognizer restarts with it. Distinctive names work best - a word you use in everyday " +
                "conversation will chime more often (though a chime alone never runs anything). It can't be a command " +
                "phrase, and custom commands can't contain it. Use Test wake word to see how confidently your voice is heard.",
                null, null, null, null),

            new HelpEntry("Custom commands",
                "Your own phrases for after the chime. Each one either presses a key combination (e.g. an app's dictation " +
                "shortcut), opens an app, file or web address, or runs a built-in action. A key combination can be limited to " +
                "one app: then it is only pressed when that app is the window in front, and nothing happens otherwise. " +
                "Click the Keys box and press the shortcut to record it, or type it (Ctrl+Shift+D). A command needs a phrase " +
                "that is not already a built-in command and does not contain the wake word; one with a problem stays in the " +
                "list, marked, but is not listened for. Saved automatically to voice-commands.json.",
                null, null, null, null),

            new HelpEntry("Microphone",
                "Which input KINECT-OS listens on. System Default follows the Windows default input device; picking a " +
                "specific microphone (Kinect array, laptop mic, headset) captures that device directly, whatever the " +
                "Windows default is. Switching restarts the recognizer: any open command window is dropped and it goes " +
                "back to waiting for the wake word - nothing is executed during the switch. The choice is remembered. If the " +
                "chosen microphone disappears (unplugged, Bluetooth dropped) voice falls back to System Default and " +
                "returns to your choice when it reconnects.",
                null, null, null, null),

            new HelpEntry("Wake Sensitivity",
                "How readily the wake word is accepted. It moves only the confidence the recognizer needs; it does not change the " +
                "checks that keep conversation out (a short pause before the word, the word said on its own and not buried in a " +
                "sentence), so even at maximum a false wake only opens the listening window - it never runs a command. Use Test " +
                "wake word to see the confidence your voice actually lands at.",
                "the wake word is accepted more readily - at a normal volume, from further away, or through a Bluetooth mic.",
                "only a clear, confident wake word wakes it.",
                "it sometimes chimes on its own during conversation (distracting, but no command runs without one).",
                "you have to repeat the wake word, raise your voice, or turn up the input level."),

            new HelpEntry("Input level",
                "The Windows input level of the selected microphone - the same slider as Sound settings. Turn it up if the meter " +
                "barely moves when you speak, or down if it is pinned at the top. This is the strength of the signal, separate " +
                "from Wake Sensitivity (how willing the recognizer is). Some devices do not allow it to be set and show read-only.",
                "a quiet microphone is boosted, so speech reaches the recognizer.",
                "a hot microphone that clips is calmed down.",
                "the signal clips and recognition gets worse.",
                "the microphone is too quiet to recognize reliably."),

            new HelpEntry("Test wake word",
                "A tuning mode: the full wake pipeline runs so you can see the confidence and the live level, but nothing is ever " +
                "carried out - no volume change, no window action, no key pressed. Say the wake word, read the confidence, adjust Wake Sensitivity or " +
                "Input level, and compare microphones safely. Turn it off (or switch voice off) to leave test mode.",
                null, null, null, null),

            new HelpEntry("Command confidence",
                "How sure the recognizer must be about the phrase you say after the chime. " +
                "A rejected phrase just closes the window; nothing is executed.",
                "misheard commands are refused rather than run.",
                "commands said softly or from further away are accepted.",
                "clear commands are refused and you have to wake it again.",
                "a similar-sounding command may run instead of the one you said."),

            new HelpEntry("Dismiss sound",
                "A quiet falling tone when a command window closes without a command - timed out, not recognized, " +
                "cancelled, or you spoke before the chime. The wake chime itself always plays: it is the signal to speak.",
                null, null, null, null),

            new HelpEntry("Minimize to the floating widget",
                "On: minimizing the control center hides it and shows the compact status widget instead of a taskbar button. Off: the window minimizes normally. The tray icon and the widget both bring the window back.",
                null, null, null, null),

            new HelpEntry("Start in compact mode",
                "Launch straight into the floating widget, with the control center hidden until you open it from the widget or the tray icon.",
                null, null, null, null),

            new HelpEntry("Keep the widget above other windows",
                "On: the compact widget floats over everything so the status is always visible. Off: it behaves like an ordinary window and can be covered.",
                null, null, null, null),

            new HelpEntry("Reload the last profile",
                "On: the tuning profile you last loaded or saved is applied again at startup, after your saved settings and before the control mode opens the sensor. Off: startup uses the saved settings only. An empty or unreadable slot is skipped and those saved settings stay. This choice is not stored inside a profile.",
                null, null, null, null)
        };

        public static HelpEntry Find(string title)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                if (Entries[i].Title == title)
                {
                    return Entries[i];
                }
            }

            return null;
        }

        public static string BuildTooltip(string title)
        {
            HelpEntry entry = Find(title);
            return entry == null ? null : entry.Title + "\n" + entry.BuildText();
        }
    }
}
