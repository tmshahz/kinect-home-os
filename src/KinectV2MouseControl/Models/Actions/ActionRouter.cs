using System;

namespace KinectV2MouseControl
{
    /// <summary>
    /// The single place where a semantic intent becomes a Windows operation.
    ///
    /// Everything upstream - gesture recognizers today, voice or scripted commands later -
    /// speaks in ControlAction values and knows nothing about SendInput. Adding an action means
    /// adding a case here; recognizers are untouched.
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
    /// </summary>
    public class ActionRouter : IActionSink
    {
        /// <summary>
        /// Raised after an action is carried out. Used for the diagnostics readout; also the
        /// natural hook for logging or scripting later.
        /// </summary>
        public event EventHandler<ControlAction> ActionExecuted;

        /// <summary>
        /// Target for ToggleControl. Set once at startup; left null the action is simply
        /// ignored, which keeps the router usable without a host.
        /// </summary>
        public IControlGate ControlGate { get; set; }

        /// <summary>
        /// Accumulates fractional scroll so that sub-notch movement is not simply discarded.
        /// Whole notches are submitted because apps that divide the wheel delta by WHEEL_DELTA
        /// would round a partial delta down to no scroll at all.
        /// </summary>
        private double scrollRemainder;

        public void Execute(ControlAction action)
        {
            switch (action.Type)
            {
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
                        return;
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
                        return;
                    }

                    // Note this re-enters the gesture layer: toggling resets every recognizer,
                    // including the one that just emitted this. Recognizers must therefore
                    // finish writing their state before emitting.
                    ControlGate.ToggleControl();
                    break;

                case ControlActionType.None:
                    return;

                default:
                    return;
            }

            EventHandler<ControlAction> handler = ActionExecuted;
            if (handler != null)
            {
                handler.Invoke(this, action);
            }
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
        /// Drops accumulated scroll. Called when a gesture session ends so a part-finished
        /// notch cannot leak into the next one.
        /// </summary>
        public void ResetScrollAccumulation()
        {
            scrollRemainder = 0;
        }
    }
}
