namespace KinectV2MouseControl
{
    /// <summary>
    /// Keyboard output for actions that have no mouse equivalent.
    ///
    /// Window switching is done with Alt+Tab rather than by enumerating and activating windows
    /// directly. SetForegroundWindow is restricted for a process that does not own the current
    /// foreground window, so driving the shell's own switcher is both simpler and more reliable
    /// than reimplementing it. The whole chord is submitted in one SendInput batch so the shell
    /// sees a well-formed sequence.
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
    }
}
