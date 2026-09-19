using System;
using System.Runtime.InteropServices;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Windows master volume and mute for the default playback device, through the Core Audio
    /// endpoint API (IAudioEndpointVolume) - the same control the taskbar volume slider moves.
    ///
    /// Media keys can only nudge the volume in 2% steps and toggle mute; an exact level
    /// ("volume 37") and an explicit mute/unmute need the endpoint itself. Every call opens the
    /// current default endpoint afresh, so switching speakers or headphones is picked up
    /// without any notification plumbing. Nothing here throws: failures come back as a reason.
    ///
    /// Success is any non-negative HRESULT: SetMute returns S_FALSE (1) when the endpoint was
    /// already in the requested state, which is not an error (the first version treated it as
    /// one, so "volume 50" set the level and then reported failure).
    /// </summary>
    public static class SystemVolume
    {
        private const int eRender = 0;
        private const int eMultimedia = 1;
        private const int CLSCTX_ALL = 23;

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject
        {
        }

        // Only the leading vtable slots that are used are declared; COM dispatch is by slot
        // order, so the order below must match the SDK header exactly.
        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
        }

        [ComImport]
        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig]
            int RegisterControlChangeNotify(IntPtr notify);

            [PreserveSig]
            int UnregisterControlChangeNotify(IntPtr notify);

            [PreserveSig]
            int GetChannelCount(out uint channelCount);

            [PreserveSig]
            int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

            [PreserveSig]
            int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

            [PreserveSig]
            int GetMasterVolumeLevel(out float levelDb);

            [PreserveSig]
            int GetMasterVolumeLevelScalar(out float level);

            [PreserveSig]
            int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);

            [PreserveSig]
            int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

            [PreserveSig]
            int GetChannelVolumeLevel(uint channel, out float levelDb);

            [PreserveSig]
            int GetChannelVolumeLevelScalar(uint channel, out float level);

            [PreserveSig]
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

            [PreserveSig]
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        /// <summary>
        /// Sets the master volume to <paramref name="percent"/> (0-100). With
        /// <paramref name="unmute"/>, a level above zero also clears mute, because "volume 60"
        /// that stays silent would read as a failure.
        /// </summary>
        public static bool TrySetPercent(int percent, bool unmute, out string error)
        {
            if (percent < 0 || percent > 100)
            {
                error = "level " + percent + " is outside 0-100";
                return false;
            }

            return WithEndpoint(endpoint =>
            {
                Guid context = Guid.Empty;
                int hr = endpoint.SetMasterVolumeLevelScalar(percent / 100f, ref context);
                if (hr < 0)
                {
                    return "SetMasterVolumeLevelScalar HRESULT 0x" + hr.ToString("X8");
                }

                if (unmute && percent > 0)
                {
                    hr = endpoint.SetMute(false, ref context);
                    if (hr < 0)
                    {
                        return "SetMute HRESULT 0x" + hr.ToString("X8");
                    }
                }

                return null;
            }, out error);
        }

        public static bool TrySetMute(bool mute, out string error)
        {
            return WithEndpoint(endpoint =>
            {
                Guid context = Guid.Empty;
                int hr = endpoint.SetMute(mute, ref context);
                return hr >= 0 ? null : "SetMute HRESULT 0x" + hr.ToString("X8");
            }, out error);
        }

        public static bool TryGet(out int percent, out bool muted, out string error)
        {
            int level = 0;
            bool isMuted = false;
            bool ok = WithEndpoint(endpoint =>
            {
                float scalar;
                int hr = endpoint.GetMasterVolumeLevelScalar(out scalar);
                if (hr < 0)
                {
                    return "GetMasterVolumeLevelScalar HRESULT 0x" + hr.ToString("X8");
                }

                hr = endpoint.GetMute(out isMuted);
                if (hr < 0)
                {
                    return "GetMute HRESULT 0x" + hr.ToString("X8");
                }

                level = (int)Math.Round(scalar * 100);
                return null;
            }, out error);

            percent = level;
            muted = isMuted;
            return ok;
        }

        /// <summary>
        /// Writes the level that is already set, and reads it back. Used by the voice self-test
        /// to prove the setter's vtable slot is right without changing anything audible.
        /// </summary>
        internal static bool TryRewriteCurrentLevel(out string detail)
        {
            float before = 0;
            float after = 0;
            string error;
            bool ok = WithEndpoint(endpoint =>
            {
                int hr = endpoint.GetMasterVolumeLevelScalar(out before);
                if (hr < 0)
                {
                    return "get HRESULT 0x" + hr.ToString("X8");
                }

                Guid context = Guid.Empty;
                hr = endpoint.SetMasterVolumeLevelScalar(before, ref context);
                if (hr < 0)
                {
                    return "set HRESULT 0x" + hr.ToString("X8");
                }

                hr = endpoint.GetMasterVolumeLevelScalar(out after);
                return hr >= 0 ? null : "re-read HRESULT 0x" + hr.ToString("X8");
            }, out error);

            detail = ok
                ? "level " + (before * 100).ToString("0.0") + "% rewritten, read back " + (after * 100).ToString("0.0") + "%"
                : error;
            return ok && Math.Abs(after - before) < 0.0005f;
        }

        private static bool WithEndpoint(Func<IAudioEndpointVolume, string> operation, out string error)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            object endpointObject = null;
            error = null;

            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

                int hr = enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out device);
                if (hr < 0 || device == null)
                {
                    error = "no default playback device (HRESULT 0x" + hr.ToString("X8") + ")";
                    return false;
                }

                Guid iid = typeof(IAudioEndpointVolume).GUID;
                hr = device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out endpointObject);
                IAudioEndpointVolume endpoint = endpointObject as IAudioEndpointVolume;
                if (hr < 0 || endpoint == null)
                {
                    error = "could not open the volume control (HRESULT 0x" + hr.ToString("X8") + ")";
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
                Release(endpointObject);
                Release(device);
                Release(enumerator);
            }
        }

        private static void Release(object comObject)
        {
            if (comObject != null && Marshal.IsComObject(comObject))
            {
                Marshal.ReleaseComObject(comObject);
            }
        }
    }
}
