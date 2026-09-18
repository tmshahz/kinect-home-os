using System;
using System.Runtime.InteropServices;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Mouse pressing down/up, clicking, wheel and moving control.
    ///
    /// Buttons and wheel go through SendInput (see Win32Input), which supersedes the legacy
    /// mouse_event entry point. Compound actions are submitted as a single batch so nothing
    /// can be interleaved between the down and the up.
    ///
    /// Movement stays on SetCursorPos:
    /// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setcursorpos
    /// SendInput's absolute mode normalizes coordinates to a 0-65535 range over the virtual
    /// desktop, which both quantizes the position and cannot express the negative coordinates
    /// a monitor left of or above the primary one produces. SetCursorPos takes signed physical
    /// pixels directly, so it is exact across the whole virtual desktop. It is also safe to
    /// call from a background thread, which is what the cursor output loop does.
    /// </summary>
    public static class MouseControl
    {
        /// <summary>
        /// True between an injected left-button down and its up. Lets the process-level crash
        /// and shutdown handlers release a button the normal teardown paths never got to.
        /// </summary>
        private static volatile bool isLeftDownInjected;

        public static void PressDown()
        {
            isLeftDownInjected = true;
            Win32Input.Send(Win32Input.CreateMouseInput(Win32Input.MOUSEEVENTF_LEFTDOWN));
        }

        public static void PressUp()
        {
            Win32Input.Send(Win32Input.CreateMouseInput(Win32Input.MOUSEEVENTF_LEFTUP));
            isLeftDownInjected = false;
        }

        /// <summary>
        /// Last-resort fail-safe for unhandled exceptions, session end and process exit: if this
        /// process pressed the left button and has not released it, release it now. The normal
        /// release paths all go through ActionRouter; this bypasses it deliberately, because by
        /// the time it runs the engine may be in no state to route anything.
        /// </summary>
        /// <returns>True when a release had to be sent.</returns>
        public static bool ReleaseIfInjected()
        {
            if (!isLeftDownInjected)
            {
                return false;
            }

            PressUp();
            return true;
        }

        public static void Click()
        {
            Win32Input.Send(
                Win32Input.CreateMouseInput(Win32Input.MOUSEEVENTF_LEFTDOWN),
                Win32Input.CreateMouseInput(Win32Input.MOUSEEVENTF_LEFTUP));
        }

        public static void RightClick()
        {
            Win32Input.Send(
                Win32Input.CreateMouseInput(Win32Input.MOUSEEVENTF_RIGHTDOWN),
                Win32Input.CreateMouseInput(Win32Input.MOUSEEVENTF_RIGHTUP));
        }

        /// <summary>
        /// Turns the wheel. Positive scrolls up/away from the user.
        /// </summary>
        /// <param name="notches">Wheel notches; fractional values are not submitted.</param>
        public static void Scroll(int notches)
        {
            if (notches == 0)
            {
                return;
            }

            Win32Input.Send(Win32Input.CreateMouseInput(
                Win32Input.MOUSEEVENTF_WHEEL,
                notches * Win32Input.WHEEL_DELTA));
        }

        /// <summary>
        /// Moves the cursor to an absolute physical-pixel position on the virtual desktop.
        /// Coordinates are rounded rather than truncated, so a target of 100.6 lands on 101
        /// instead of biasing every position toward the desktop origin.
        /// </summary>
        public static bool MoveTo(double x, double y)
        {
            return MoveTo((int)Math.Round(x), (int)Math.Round(y));
        }

        public static bool MoveTo(int x, int y)
        {
            return SetCursorPos(x, y);
        }

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);
    }
}
