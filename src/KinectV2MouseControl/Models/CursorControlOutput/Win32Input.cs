using System;
using System.Runtime.InteropServices;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Shared SendInput plumbing for the mouse and keyboard output paths.
    ///
    /// SendInput validates cbSize against the size of the full INPUT union, so the union has to
    /// be declared properly rather than inlining just the member a given call site needs - a
    /// keyboard-only struct would be smaller than MOUSEINPUT and every call would fail. Keeping
    /// one declaration here means both output paths agree on it.
    /// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-sendinput
    /// </summary>
    internal static class Win32Input
    {
        public const uint INPUT_MOUSE = 0;
        public const uint INPUT_KEYBOARD = 1;

        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_WHEEL = 0x0800;

        public const uint KEYEVENTF_KEYUP = 0x0002;

        public const ushort VK_SHIFT = 0x10;
        public const ushort VK_MENU = 0x12;
        public const ushort VK_TAB = 0x09;

        /// <summary>
        /// One notch of the mouse wheel, as defined by WHEEL_DELTA.
        /// </summary>
        public const int WHEEL_DELTA = 120;

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public int mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)]
            public MOUSEINPUT Mouse;
            [FieldOffset(0)]
            public KEYBDINPUT Keyboard;
            [FieldOffset(0)]
            public HARDWAREINPUT Hardware;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint Type;
            public InputUnion Data;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        public static INPUT CreateMouseInput(uint flags, int mouseData = 0)
        {
            INPUT input = new INPUT();
            input.Type = INPUT_MOUSE;
            input.Data.Mouse.dwFlags = flags;
            input.Data.Mouse.mouseData = mouseData;
            return input;
        }

        public static INPUT CreateKeyInput(ushort virtualKey, bool isKeyUp)
        {
            INPUT input = new INPUT();
            input.Type = INPUT_KEYBOARD;
            input.Data.Keyboard.wVk = virtualKey;
            input.Data.Keyboard.dwFlags = isKeyUp ? KEYEVENTF_KEYUP : 0;
            return input;
        }

        /// <summary>
        /// Submits a batch of events in one call, so nothing from another source can be
        /// interleaved partway through a compound gesture such as Alt+Tab or a click.
        /// </summary>
        public static void Send(params INPUT[] inputs)
        {
            if (inputs == null || inputs.Length == 0)
            {
                return;
            }

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }
    }
}
