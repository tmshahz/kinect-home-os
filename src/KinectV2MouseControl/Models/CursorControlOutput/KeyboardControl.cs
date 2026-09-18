namespace KinectV2MouseControl
{
    /// <summary>
    /// Keyboard output for actions that have no mouse equivalent.
    ///
    /// Window switching is done with Alt+Tab rather than by enumerating and activating windows
    /// directly. SetForegroundWindow is restricted for a process that does not own the current
    /// foreground window, so driving the shell's own switcher is both simpler and more reliable
    /// than reimplementing it. The same reasoning applies to the window-management and media
    /// actions added for voice: Win+Arrow, Win+D and the media keys are what the shell already
    /// listens for, so sending them is exactly as robust as pressing them. Every chord is
    /// submitted in one SendInput batch so the shell sees a well-formed sequence.
    /// </summary>
    public static class KeyboardControl
    {
        /// <summary>
        /// Alt+Tab - move forward through the window order.
        /// </summary>
        public static void SwitchToNextWindow()
        {
            Win32Input.Send(
                Win32Input.CreateKeyInput(Win32Input.VK_MENU, false),
                Win32Input.CreateKeyInput(Win32Input.VK_TAB, false),
                Win32Input.CreateKeyInput(Win32Input.VK_TAB, true),
                Win32Input.CreateKeyInput(Win32Input.VK_MENU, true));
        }

        /// <summary>
        /// Shift+Alt+Tab - move backward through the window order.
        /// </summary>
        public static void SwitchToPreviousWindow()
        {
            Win32Input.Send(
                Win32Input.CreateKeyInput(Win32Input.VK_MENU, false),
                Win32Input.CreateKeyInput(Win32Input.VK_SHIFT, false),
                Win32Input.CreateKeyInput(Win32Input.VK_TAB, false),
                Win32Input.CreateKeyInput(Win32Input.VK_TAB, true),
                Win32Input.CreateKeyInput(Win32Input.VK_SHIFT, true),
                Win32Input.CreateKeyInput(Win32Input.VK_MENU, true));
        }

        public static void MaximizeWindow()
        {
            Chord(Win32Input.VK_LWIN, Win32Input.VK_UP);
        }

        public static void MinimizeWindow()
        {
            Chord(Win32Input.VK_LWIN, Win32Input.VK_DOWN);
        }

        public static void SnapWindowLeft()
        {
            Chord(Win32Input.VK_LWIN, Win32Input.VK_LEFT);
        }

        public static void SnapWindowRight()
        {
            Chord(Win32Input.VK_LWIN, Win32Input.VK_RIGHT);
        }

        public static void ShowDesktop()
        {
            Chord(Win32Input.VK_LWIN, Win32Input.VK_D);
        }

        public static void TaskView()
        {
            Chord(Win32Input.VK_LWIN, Win32Input.VK_TAB);
        }

        /// <summary>
        /// Alt+F4 on the foreground window.
        /// </summary>
        public static void CloseWindow()
        {
            Chord(Win32Input.VK_MENU, Win32Input.VK_F4);
        }

        /// <summary>
        /// A single key press and release, for the media and volume keys.
        /// </summary>
        public static void Tap(ushort virtualKey)
        {
            Win32Input.Send(
                Win32Input.CreateKeyInput(virtualKey, false),
                Win32Input.CreateKeyInput(virtualKey, true));
        }

        /// <summary>
        /// Modifier held around a key tap. Sending the whole chord in one batch means the
        /// modifier can never be left pressed if something interrupts between the events.
        /// </summary>
        public static void Chord(ushort modifier, ushort key)
        {
            Win32Input.Send(
                Win32Input.CreateKeyInput(modifier, false),
                Win32Input.CreateKeyInput(key, false),
                Win32Input.CreateKeyInput(key, true),
                Win32Input.CreateKeyInput(modifier, true));
        }
    }
}
