using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Lists the audio capture devices Windows knows about, for the Voice page's microphone
    /// card. Uses the classic waveIn API because it needs no COM setup and answers in
    /// microseconds; the 31-character name limit is a known quirk of that API and is accepted.
    /// Never throws: a failure just reports no devices.
    /// </summary>
    public static class AudioInputDevices
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WAVEINCAPS
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
        }

        [DllImport("winmm.dll")]
        private static extern uint waveInGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern uint waveInGetDevCapsW(UIntPtr deviceId, ref WAVEINCAPS caps, uint size);

        public static string[] GetNames()
        {
            List<string> names = new List<string>();

            try
            {
                uint count = waveInGetNumDevs();
                for (uint i = 0; i < count; i++)
                {
                    WAVEINCAPS caps = new WAVEINCAPS();
                    if (waveInGetDevCapsW(new UIntPtr(i), ref caps, (uint)Marshal.SizeOf(typeof(WAVEINCAPS))) == 0
                        && !string.IsNullOrWhiteSpace(caps.szPname))
                    {
                        names.Add(caps.szPname.Trim());
                    }
                }
            }
            catch (Exception)
            {
                names.Clear();
            }

            return names.ToArray();
        }
    }
}
