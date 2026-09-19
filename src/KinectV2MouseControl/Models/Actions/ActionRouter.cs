using System;
using System.Diagnostics;

namespace KinectV2MouseControl
{
    /// <summary>
    /// The single place where a semantic intent becomes a Windows operation.
    ///
    /// Everything upstream - gesture recognizers today, voice commands and the control center's
    /// Actions page now, agent commands later - speaks in ControlAction values and knows
    /// nothing about SendInput. Adding an action means adding a case here; recognizers are
    /// untouched.
    ///
    /// Pointer movement is intentionally NOT routed through here. It is a continuous stream
    /// rather than a discrete intent: the filtered target is republished every sensor frame and
    /// re-read by the output loop at ~125Hz on its own thread. Pushing that through an action
    /// queue would add per-frame indirection to the hot path and would force this class to know
    /// about the output loop's threading, for no gain - there is no plausible voice command for
    /// "move the pointer 3 pixels left". Cursor position therefore stays on the dedicated path
    /// built in stages 2-4, and remains single-writer.
    ///
    /// Wheel scrolling by contrast is discrete and low-rate, so it does belong here.
    ///
    /// Threading: Execute must be called on the UI thread. ToggleControl re-enters the engine,
    /// and the engine's state is owned by that thread. Voice recognition arrives on a worker
    /// thread and is marshalled by its view model before it gets here.
    /// </summary>
    public class ActionRouter : IActionSink
    {
        public const string GestureSource = "gesture";

        /// <summary>
        /// Raised after an action is carried out. Used for the diagnostics readout and the
        /// activity feed; also the natural hook for logging or scripting later.
        /// </summary>
        public event EventHandler<ControlAction> ActionExecuted;

        /// <summary>
        /// Target for the control actions. Set once at startup; left null those actions are
        /// simply ignored, which keeps the router usable without a host.
        /// </summary>
        public IControlGate ControlGate { get; set; }

        /// <summary>
        /// Who asked for the action currently being executed (or the last one). Read by the
        /// ActionExecuted handler so the activity feed can say "voice" or "gesture" without the
        /// action struct having to carry it.
        /// </summary>
        public string LastSource { get; private set; }
        public DesktopActionResult LastResult { get; private set; }
        public Func<string, bool> ControlCenterHandler { get; set; }

        /// <summary>Request context carries file capabilities only for this one assistant request.</summary>
        public DesktopActionResult ExecuteRequest(ControlAction action, DesktopActionContext context, string source)
        {
            if (System.Windows.Application.Current != null) { System.Windows.Application.Current.Dispatcher.VerifyAccess(); }
            context.Cancellation.ThrowIfCancellationRequested();
            LastSource = source;
            if (SafeDesktopActions.Handles(action.Type))
            {
                LastResult = SafeDesktopActions.Execute(action, context);
                if (LastResult.Success && !context.DryRun)
                {
                    EventHandler<ControlAction> handler = ActionExecuted;
                    if (handler != null) { handler(this, action); }
                }
                return LastResult;
            }
            if (context.DryRun) { return DesktopActionResult.Ok("Would run " + action); }
            bool done = Execute(action, source);
            return LastResult = done ? DesktopActionResult.Ok(action.ToString()) : DesktopActionResult.Refused("Could not perform " + action);
        }

        /// <summary>
        /// Accumulates fractional scroll so that sub-notch movement is not simply discarded.
        /// Whole notches are submitted because apps that divide the wheel delta by WHEEL_DELTA
        /// would round a partial delta down to no scroll at all.
        /// </summary>
        private double scrollRemainder;

        public ActionRouter()
        {
            LastSource = GestureSource;
        }

        /// <summary>
        /// IActionSink entry point used by the recognizers and the grip code.
        /// </summary>
        public void Execute(ControlAction action)
        {
            Execute(action, GestureSource);
        }

        /// <summary>
        /// Carries out one action on behalf of <paramref name="source"/>.
        /// </summary>
        /// <returns>
        /// True when the action was actually performed (and ActionExecuted raised). False for a
        /// no-op, an unknown action, or a failure such as the audio endpoint being unavailable,
        /// so a caller like the voice HUD never reports something that did not happen.
        /// </returns>
        public bool Execute(ControlAction action, string source)
        {
            LastSource = source ?? GestureSource;
            if (SafeDesktopActions.Handles(action.Type))
            {
                return ExecuteRequest(action, new DesktopActionContext(), LastSource).Success;
            }

            switch (action.Type)
            {
                case ControlActionType.ControlCenterCommand:
                    if ((action.Parameter != "open" && action.Parameter != "compact" && action.Parameter != "calibrate")
                        || ControlCenterHandler == null || !ControlCenterHandler(action.Parameter)) { return false; }
                    break;
                case ControlActionType.LeftMouseDown:
                    MouseControl.PressDown();
                    break;

                case ControlActionType.LeftMouseUp:
                    MouseControl.PressUp();
                    break;

                case ControlActionType.LeftClick:
                    MouseControl.Click();
                    break;

                case ControlActionType.RightClick:
                    MouseControl.RightClick();
                    break;

                case ControlActionType.Scroll:
                    if (!ExecuteScroll(action.Value))
                    {
                        // Nothing whole to submit yet; don't report a no-op as an action.
                        return false;
                    }
                    break;

                case ControlActionType.NextWindow:
                    KeyboardControl.SwitchToNextWindow();
                    break;

                case ControlActionType.PreviousWindow:
                    KeyboardControl.SwitchToPreviousWindow();
                    break;

                case ControlActionType.ToggleControl:
                    if (ControlGate == null)
                    {
                        return false;
                    }

                    // Note this re-enters the gesture layer: toggling resets every recognizer,
                    // including the one that just emitted this. Recognizers must therefore
                    // finish writing their state before emitting.
                    ControlGate.SetControlEnabled(!ControlGate.IsControlEnabled, LastSource);
                    break;

                case ControlActionType.EnableControl:
                case ControlActionType.DisableControl:
                    if (ControlGate == null)
                    {
                        return false;
                    }

                    ControlGate.SetControlEnabled(action.Type == ControlActionType.EnableControl, LastSource);
                    break;

                case ControlActionType.MaximizeWindow:
                    KeyboardControl.MaximizeWindow();
                    break;

                case ControlActionType.MinimizeWindow:
                    KeyboardControl.MinimizeWindow();
                    break;

                case ControlActionType.SnapWindowLeft:
                    KeyboardControl.SnapWindowLeft();
                    break;

                case ControlActionType.SnapWindowRight:
                    KeyboardControl.SnapWindowRight();
                    break;

                case ControlActionType.ShowDesktop:
                    KeyboardControl.ShowDesktop();
                    break;

                case ControlActionType.TaskView:
                    KeyboardControl.TaskView();
                    break;

                case ControlActionType.CloseWindow:
                    KeyboardControl.CloseWindow();
                    break;

                case ControlActionType.PlayPause:
                    KeyboardControl.Tap(Win32Input.VK_MEDIA_PLAY_PAUSE);
                    break;

                case ControlActionType.NextTrack:
                    KeyboardControl.Tap(Win32Input.VK_MEDIA_NEXT_TRACK);
                    break;

                case ControlActionType.PreviousTrack:
                    KeyboardControl.Tap(Win32Input.VK_MEDIA_PREV_TRACK);
                    break;

                case ControlActionType.VolumeUp:
                    // Each media-key step is 2%; five make a change you can actually hear, and
                    // the keys bring up the Windows volume overlay as feedback.
                    KeyboardControl.Tap(Win32Input.VK_VOLUME_UP, VolumeStepKeys);
                    break;

                case ControlActionType.VolumeDown:
                    KeyboardControl.Tap(Win32Input.VK_VOLUME_DOWN, VolumeStepKeys);
                    break;

                case ControlActionType.SetVolume:
                    if (!ExecuteSetVolume(action.Value))
                    {
                        return false;
                    }
                    break;

                case ControlActionType.Mute:
                case ControlActionType.Unmute:
                    if (!ExecuteMute(action.Type == ControlActionType.Mute))
                    {
                        return false;
                    }
                    break;

                case ControlActionType.LaunchApp:
                    if (!LaunchApp(action.Parameter))
                    {
                        return false;
                    }
                    break;

                case ControlActionType.SendKeys:
                    if (!SendKeys(action.Parameter))
                    {
                        return false;
                    }
                    break;

                case ControlActionType.None:
                    return false;

                default:
                    return false;
            }

            EventHandler<ControlAction> handler = ActionExecuted;
            if (handler != null)
            {
                handler.Invoke(this, action);
            }

            return true;
        }

        /// <summary>
        /// Media-key presses per "volume up/down" (2% each).
        /// </summary>
        private const int VolumeStepKeys = 5;

        /// <summary>
        /// Absolute master volume through Core Audio. The level must already be a whole number
        /// in 0-100: the parser refuses anything else, and this refuses it again rather than
        /// clamping, so a malformed request can never land on some other volume.
        /// </summary>
        private static bool ExecuteSetVolume(double value)
        {
            if (double.IsNaN(value) || value < 0 || value > 100 || value != Math.Floor(value))
            {
                RuntimeLog.Write("SetVolume refused: " + value);
                return false;
            }

            string error;
            if (!SystemVolume.TrySetPercent((int)value, true, out error))
            {
                RuntimeLog.Write("SetVolume failed: " + error);
                return false;
            }

            return true;
        }

        private static bool ExecuteMute(bool mute)
        {
            string error;
            if (!SystemVolume.TrySetMute(mute, out error))
            {
                RuntimeLog.Write((mute ? "Mute" : "Unmute") + " failed: " + error);
                return false;
            }

            return true;
        }

        private bool ExecuteScroll(double notches)
        {
            scrollRemainder += notches;

            int wholeNotches = (int)scrollRemainder;
            if (wholeNotches == 0)
            {
                return false;
            }

            scrollRemainder -= wholeNotches;
            MouseControl.Scroll(wholeNotches);
            return true;
        }

        /// <summary>
        /// Shell-launches a target (exe, document or URL). Failures are logged, never thrown,
        /// so a bad command can't take the control engine down with it.
        /// </summary>
        private static bool LaunchApp(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return false;
            }

            try
            {
                ProcessStartInfo info = new ProcessStartInfo(target);
                info.UseShellExecute = true;
                Process.Start(info);
                return true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("LaunchApp failed for '" + target + "': " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Presses a key combination in the foreground window. A combination that does not
        /// parse is refused rather than approximated.
        /// </summary>
        private static bool SendKeys(string chordText)
        {
            KeyChord chord;
            string error;
            if (!KeyChord.TryParse(chordText, out chord, out error))
            {
                RuntimeLog.Write("SendKeys refused for '" + chordText + "': " + error);
                return false;
            }

            KeyboardControl.SendChord(chord);
            return true;
        }

        /// <summary>
        /// Drops accumulated scroll. Called when a gesture session ends so a part-finished
        /// notch cannot leak into the next one.
        /// </summary>
        public void ResetScrollAccumulation()
        {
            scrollRemainder = 0;
        }
    }
}
