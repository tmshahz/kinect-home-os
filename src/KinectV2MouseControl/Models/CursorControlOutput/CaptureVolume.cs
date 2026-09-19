using System;
using System.Runtime.InteropServices;

namespace KinectV2MouseControl
{
    /// <summary>
    /// The Windows input level of a capture endpoint - the same 0-100 slider that Sound settings
    /// shows for a microphone - through the Core Audio endpoint API (IAudioEndpointVolume on the
    /// capture device). This raises or lowers the signal the speech recognizer receives, which is
    /// separate from Wake Sensitivity (how willing the recognizer is to accept "Kinect").
    ///
    /// The endpoint's scalar level maps to the Sound-settings slider and can be written even when
    /// the device reports no hardware volume support (Windows applies a software gain), so the
    /// hardware-support flags are not used to gate control; instead, writability is confirmed by a
    /// no-op write. A device that refuses the write (some Bluetooth headsets) is reported as
    /// read-only rather than failing. Nothing here throws: failures come back as a reason.
    /// </summary>
    public static class CaptureVolume
    {
        private const int eCapture = 1;
        private const int eConsole = 0;
        private const int CLSCTX_ALL = 23;

        [ComImport]
        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint channelCount);
            [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
            [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        /// <summary>
        /// Reads the input level of <paramref name="endpointId"/> (null = the default capture
        /// device) and whether it can be written. <paramref name="canSet"/> is decided by a
        /// no-op write of the current level, so it never changes anything audible.
        /// </summary>
        public static bool TryProbe(string endpointId, out int percent, out bool canSet, out string error)
        {
            int level = 0;
            bool writable = false;
            bool ok = WithEndpoint(endpointId, endpoint =>
            {
                float scalar;
                int hr = endpoint.GetMasterVolumeLevelScalar(out scalar);
                if (hr < 0)
                {
                    return "the input level could not be read (HRESULT 0x" + hr.ToString("X8") + ")";
                }

                level = Clamp(scalar);
                Guid context = Guid.Empty;
                writable = endpoint.SetMasterVolumeLevelScalar(scalar, ref context) >= 0;
                return null;
            }, out error);

            percent = level;
            canSet = ok && writable;
            return ok;
        }

        public static bool TrySetPercent(string endpointId, int percent, out string error)
        {
            if (percent < 0 || percent > 100)
            {
                error = "level " + percent + " is outside 0-100";
                return false;
            }

            return WithEndpoint(endpointId, endpoint =>
            {
                Guid context = Guid.Empty;
                int hr = endpoint.SetMasterVolumeLevelScalar(percent / 100f, ref context);
                return hr >= 0 ? null : "the input level could not be set (HRESULT 0x" + hr.ToString("X8") + ")";
            }, out error);
        }

        private static int Clamp(float scalar)
        {
            int value = (int)Math.Round(scalar * 100);
            return value < 0 ? 0 : (value > 100 ? 100 : value);
        }

        private static bool WithEndpoint(string endpointId, Func<IAudioEndpointVolume, string> operation, out string error)
        {
            AudioInputDevices.IMMDeviceEnumerator enumerator = null;
            AudioInputDevices.IMMDevice device = null;
            object endpointObject = null;
            error = null;

            try
            {
                enumerator = AudioInputDevices.CreateEnumerator();

                int hr;
                if (string.IsNullOrEmpty(endpointId))
                {
                    hr = enumerator.GetDefaultAudioEndpoint(eCapture, eConsole, out device);
                }
                else
                {
                    hr = enumerator.GetDevice(endpointId, out device);
                }

                if (hr < 0 || device == null)
                {
                    error = "the microphone is not available (HRESULT 0x" + hr.ToString("X8") + ")";
                    return false;
                }

                Guid iid = typeof(IAudioEndpointVolume).GUID;
                hr = device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out endpointObject);
                IAudioEndpointVolume endpoint = endpointObject as IAudioEndpointVolume;
                if (hr < 0 || endpoint == null)
                {
                    error = "this microphone does not expose an input level (HRESULT 0x" + hr.ToString("X8") + ")";
                    return false;
                }

                error = operation(endpoint);
                return error == null;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                AudioInputDevices.Release(endpointObject);
                AudioInputDevices.Release(device);
                AudioInputDevices.Release(enumerator);
            }
        }
    }
}
