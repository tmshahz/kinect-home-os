using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace KinectV2MouseControl
{
    /// <summary>
    /// One active Windows audio capture endpoint.
    /// </summary>
    public sealed class AudioInputDevice
    {
        public AudioInputDevice(string id, string name, bool isDefault)
        {
            Id = id;
            Name = name;
            IsDefault = isDefault;
        }

        /// <summary>
        /// The Core Audio endpoint ID, e.g. "{0.0.1.00000000}.{guid}". Stable across reboots,
        /// USB ports and Bluetooth reconnects of the same device, so this is what gets saved.
        /// </summary>
        public string Id { get; private set; }

        /// <summary>
        /// The full friendly name shown in Windows Sound settings.
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// Windows' default input device (the console role, which is what the speech
        /// recognizer's "default audio device" opens).
        /// </summary>
        public bool IsDefault { get; private set; }
    }

    /// <summary>
    /// Microphone enumeration for the Voice page and the recognizer.
    ///
    /// Lists the ACTIVE capture endpoints through Core Audio (IMMDeviceEnumerator): full names,
    /// stable IDs and the current default. System.Speech itself can only open "the default
    /// audio device" or a stream, so a selected device is captured by ID through WASAPI
    /// (<see cref="MicrophoneCaptureStream"/>) and handed to the recognizer as a stream.
    ///
    /// Nothing here throws: failures come back as an empty list or a reason.
    /// </summary>
    public static class AudioInputDevices
    {
        private const int eCapture = 1;
        private const int eConsole = 0;
        private const int DEVICE_STATE_ACTIVE = 0x1;
        private const int STGM_READ = 0;
        private const ushort VT_LPWSTR = 31;

        private static readonly Guid FriendlyNameKeyFormat = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");
        private const int FriendlyNameKeyId = 14;

        // ---- Core Audio interop (vtable order must match mmdeviceapi.h) ------------------------------

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject
        {
        }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);

            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);

            [PreserveSig]
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

            [PreserveSig]
            int RegisterEndpointNotificationCallback(IMMNotificationClient client);

            [PreserveSig]
            int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
        }

        [ComImport]
        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDeviceCollection
        {
            [PreserveSig]
            int GetCount(out uint count);

            [PreserveSig]
            int Item(uint index, out IMMDevice device);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);

            [PreserveSig]
            int OpenPropertyStore(int access, out IPropertyStore properties);

            [PreserveSig]
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

            [PreserveSig]
            int GetState(out int state);
        }

        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IPropertyStore
        {
            [PreserveSig]
            int GetCount(out uint count);

            [PreserveSig]
            int GetAt(uint index, out PropertyKey key);

            [PreserveSig]
            int GetValue(ref PropertyKey key, out PropVariant value);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PropertyKey
        {
            public Guid FormatId;
            public int PropertyId;
        }

        /// <summary>
        /// Only VT_LPWSTR is read. The layout gives the right size on x86 (16) and x64 (24).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct PropVariant
        {
            public ushort VarType;
            public ushort Reserved1;
            public ushort Reserved2;
            public ushort Reserved3;
            public IntPtr Data;
            public IntPtr Data2;
        }

        /// <summary>
        /// Device add/remove/state/default notifications. Called on a Windows audio thread;
        /// implementations must only record or post, never block.
        /// </summary>
        [ComImport]
        [Guid("7991EEC9-7C89-4D85-8390-6C703CEC60C0")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMNotificationClient
        {
            [PreserveSig]
            int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);

            [PreserveSig]
            int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

            [PreserveSig]
            int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

            [PreserveSig]
            int OnDefaultDeviceChanged(int dataFlow, int role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);

            [PreserveSig]
            int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant value);

        // ---- Queries -------------------------------------------------------------------------------------

        internal static IMMDeviceEnumerator CreateEnumerator()
        {
            return (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        }

        /// <summary>
        /// Active capture devices, default first, then by name. Empty when there are none or
        /// Core Audio is unavailable (<paramref name="error"/> then says why).
        /// </summary>
        public static List<AudioInputDevice> List(out string error)
        {
            List<AudioInputDevice> devices = new List<AudioInputDevice>();
            IMMDeviceEnumerator enumerator = null;
            IMMDeviceCollection collection = null;
            error = null;

            try
            {
                enumerator = CreateEnumerator();
                string defaultId = GetDefaultId(enumerator);

                int hr = enumerator.EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE, out collection);
                if (hr < 0 || collection == null)
                {
                    error = "capture devices could not be listed (HRESULT 0x" + hr.ToString("X8") + ")";
                    return devices;
                }

                uint count;
                if (collection.GetCount(out count) < 0)
                {
                    return devices;
                }

                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device = null;
                    try
                    {
                        if (collection.Item(i, out device) < 0 || device == null)
                        {
                            continue;
                        }

                        string id;
                        if (device.GetId(out id) < 0 || string.IsNullOrEmpty(id))
                        {
                            continue;
                        }

                        string name = ReadFriendlyName(device) ?? "Microphone";
                        devices.Add(new AudioInputDevice(id, name, string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
                    }
                    finally
                    {
                        Release(device);
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                devices.Clear();
            }
            finally
            {
                Release(collection);
                Release(enumerator);
            }

            devices.Sort((a, b) =>
            {
                if (a.IsDefault != b.IsDefault)
                {
                    return a.IsDefault ? -1 : 1;
                }

                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return devices;
        }

        private static string GetDefaultId(IMMDeviceEnumerator enumerator)
        {
            IMMDevice device = null;
            try
            {
                string id;
                if (enumerator.GetDefaultAudioEndpoint(eCapture, eConsole, out device) >= 0 && device != null
                    && device.GetId(out id) >= 0)
                {
                    return id;
                }
            }
            catch (Exception)
            {
                // No default capture device.
            }
            finally
            {
                Release(device);
            }

            return null;
        }

        private static string ReadFriendlyName(IMMDevice device)
        {
            IPropertyStore store = null;
            try
            {
                if (device.OpenPropertyStore(STGM_READ, out store) < 0 || store == null)
                {
                    return null;
                }

                PropertyKey key = new PropertyKey();
                key.FormatId = FriendlyNameKeyFormat;
                key.PropertyId = FriendlyNameKeyId;
                PropVariant value;
                if (store.GetValue(ref key, out value) < 0)
                {
                    return null;
                }

                try
                {
                    return value.VarType == VT_LPWSTR && value.Data != IntPtr.Zero ? Marshal.PtrToStringUni(value.Data) : null;
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                Release(store);
            }
        }

        internal static void Release(object comObject)
        {
            try
            {
                if (comObject != null && Marshal.IsComObject(comObject))
                {
                    Marshal.ReleaseComObject(comObject);
                }
            }
            catch (Exception)
            {
                // Already released.
            }
        }

        /// <summary>
        /// Short form of an endpoint ID for the diagnostics.
        /// </summary>
        public static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return "(system default)";
            }

            int brace = id.LastIndexOf('{');
            string tail = brace >= 0 ? id.Substring(brace) : id;
            return tail.Length > 14 ? tail.Substring(0, 14) + "…}" : tail;
        }
    }

    /// <summary>
    /// Raises <see cref="Changed"/> when a capture device is added, removed, enabled, disabled or
    /// becomes the default. The event comes on a Windows audio thread; subscribers marshal.
    /// Registered once, unregistered by Dispose.
    /// </summary>
    public sealed class AudioDeviceWatcher : IDisposable
    {
        private AudioInputDevices.IMMDeviceEnumerator enumerator;
        private Client client;

        public event EventHandler Changed;

        /// <summary>
        /// False when Core Audio notifications could not be registered; the Refresh button and
        /// the recognizer's own stop detection still work.
        /// </summary>
        public bool IsActive { get; private set; }

        public AudioDeviceWatcher()
        {
            try
            {
                enumerator = AudioInputDevices.CreateEnumerator();
                client = new Client(this);
                IsActive = enumerator.RegisterEndpointNotificationCallback(client) >= 0;
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("Audio device notifications unavailable: " + ex.Message);
                IsActive = false;
            }
        }

        private void OnChanged()
        {
            EventHandler handler = Changed;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler.Invoke(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // Never throw back into the audio service.
            }
        }

        public void Dispose()
        {
            Changed = null;
            if (enumerator != null && client != null && IsActive)
            {
                try
                {
                    enumerator.UnregisterEndpointNotificationCallback(client);
                }
                catch (Exception)
                {
                    // Shutting down.
                }
            }

            IsActive = false;
            AudioInputDevices.Release(enumerator);
            enumerator = null;
            client = null;
        }

        [ComVisible(true)]
        public sealed class Client : AudioInputDevices.IMMNotificationClient
        {
            private const int eCapture = 1;
            private readonly AudioDeviceWatcher owner;

            internal Client(AudioDeviceWatcher owner)
            {
                this.owner = owner;
            }

            public int OnDeviceStateChanged(string deviceId, int newState)
            {
                owner.OnChanged();
                return 0;
            }

            public int OnDeviceAdded(string deviceId)
            {
                owner.OnChanged();
                return 0;
            }

            public int OnDeviceRemoved(string deviceId)
            {
                owner.OnChanged();
                return 0;
            }

            public int OnDefaultDeviceChanged(int dataFlow, int role, string defaultDeviceId)
            {
                if (dataFlow == eCapture)
                {
                    owner.OnChanged();
                }

                return 0;
            }

            public int OnPropertyValueChanged(string deviceId, AudioInputDevices.PropertyKey key)
            {
                // Fires constantly for unrelated properties; names are re-read on refresh.
                return 0;
            }
        }
    }
}
