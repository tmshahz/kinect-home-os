using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Speech.AudioFormat;
using System.Threading;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Live audio from one specific capture device, as the stream the speech recognizer reads.
    ///
    /// System.Speech can open only "the default audio device"; any other microphone has to be
    /// handed to it with SetInputToAudioStream. This opens the device by its Core Audio endpoint
    /// ID through WASAPI in shared mode, asks Windows to convert to the recognizer's own format
    /// (16 kHz, 16-bit, mono - AUTOCONVERTPCM), captures on a private MTA thread into a queue,
    /// and serves the recognizer's reads from it. The legacy waveIn API was not used: mapping a
    /// Core Audio ID to a waveIn index (DRV_QUERYFUNCTIONINSTANCEID) returned garbage on the
    /// development machine.
    ///
    /// The recognizer treats a short read as the end of the stream, so Read blocks until the
    /// whole request is available. When the device goes away - unplugged, Bluetooth dropped,
    /// disabled, or no audio for a while - the stream ends: Read returns what is left and then
    /// 0, the recognizer finishes, and <see cref="FailureReason"/> says why. Close stops
    /// capture, releases every COM object on the capture thread and never throws.
    /// </summary>
    internal sealed class MicrophoneCaptureStream : Stream
    {
        public const int SampleRate = 16000;
        private const int BytesPerSecond = SampleRate * 2;

        /// <summary>
        /// Opening a Bluetooth headset microphone switches the headset profile, which can take a
        /// few seconds before audio flows.
        /// </summary>
        private static readonly TimeSpan FirstAudioTimeout = TimeSpan.FromSeconds(8);

        /// <summary>
        /// A working shared-mode stream delivers a packet every ~10 ms, silence included. None
        /// for this long means the device has gone.
        /// </summary>
        private static readonly TimeSpan AudioStallTimeout = TimeSpan.FromSeconds(2);

        private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// If the recognizer stops reading, audio older than this is dropped.
        /// </summary>
        private const int MaxQueuedBytes = BytesPerSecond * 10;

        public static readonly SpeechAudioFormatInfo Format =
            new SpeechAudioFormatInfo(SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);

        // ---- WASAPI interop (vtable order must match audioclient.h) ----------------------------------

        private const int CLSCTX_ALL = 23;
        private const int AUDCLNT_SHAREMODE_SHARED = 0;
        private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
        private const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
        private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        private const int DEVICE_STATE_ACTIVE = 0x1;
        private const long BufferDuration100ns = 2000000; // 200 ms

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WaveFormatEx
        {
            public ushort FormatTag;
            public ushort Channels;
            public uint SamplesPerSec;
            public uint AvgBytesPerSec;
            public ushort BlockAlign;
            public ushort BitsPerSample;
            public ushort ExtraSize;
        }

        [ComImport]
        [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig]
            int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, [In] ref WaveFormatEx format, IntPtr audioSessionGuid);

            [PreserveSig]
            int GetBufferSize(out uint frames);

            [PreserveSig]
            int GetStreamLatency(out long latency);

            [PreserveSig]
            int GetCurrentPadding(out uint frames);

            [PreserveSig]
            int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

            [PreserveSig]
            int GetMixFormat(out IntPtr format);

            [PreserveSig]
            int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

            [PreserveSig]
            int Start();

            [PreserveSig]
            int Stop();

            [PreserveSig]
            int Reset();

            [PreserveSig]
            int SetEventHandle(IntPtr eventHandle);

            [PreserveSig]
            int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport]
        [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig]
            int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);

            [PreserveSig]
            int ReleaseBuffer(uint frames);

            [PreserveSig]
            int GetNextPacketSize(out uint frames);
        }

        // ---- State ------------------------------------------------------------------------------------

        private readonly object gate = new object();
        private readonly Queue<byte[]> chunks = new Queue<byte[]>();
        private readonly ManualResetEvent opened = new ManualResetEvent(false);
        private readonly string endpointId;
        private readonly string deviceName;

        private Thread captureThread;
        private volatile bool stopping;
        private string openError;
        private bool ended;
        private bool closed;
        private string failureReason;
        private int headOffset;
        private long queuedBytes;
        private long readBytes;
        private long capturedBytes;
        private int level;

        private MicrophoneCaptureStream(string endpointId, string deviceName)
        {
            this.endpointId = endpointId;
            this.deviceName = deviceName;
        }

        /// <summary>
        /// Opens the capture endpoint <paramref name="endpointId"/> and starts capturing.
        /// Returns null with <paramref name="error"/> set when it is not connected or cannot be
        /// opened (in use exclusively, blocked by privacy settings, driver error).
        /// </summary>
        public static MicrophoneCaptureStream Open(string endpointId, string deviceName, out string error)
        {
            MicrophoneCaptureStream stream = new MicrophoneCaptureStream(endpointId, deviceName);
            stream.captureThread = new Thread(stream.CaptureThread);
            stream.captureThread.IsBackground = true;
            stream.captureThread.Name = "KINECT-OS microphone";
            stream.captureThread.Priority = ThreadPriority.AboveNormal;
            stream.captureThread.SetApartmentState(ApartmentState.MTA);
            stream.captureThread.Start();

            if (!stream.opened.WaitOne(OpenTimeout))
            {
                error = "the microphone did not respond";
                stream.Close();
                return null;
            }

            error = stream.openError;
            if (error != null)
            {
                stream.Close();
                return null;
            }

            return stream;
        }

        public string DeviceName
        {
            get { return deviceName; }
        }

        /// <summary>
        /// Why the stream ended on its own (device lost, stall), or null.
        /// </summary>
        public string FailureReason
        {
            get { lock (gate) { return failureReason; } }
        }

        /// <summary>
        /// Peak input level of the most recent audio, 0-100.
        /// </summary>
        public int Level
        {
            get { return Volatile.Read(ref level); }
        }

        /// <summary>
        /// How much audio the device has delivered, in stream time. Runs slightly ahead of what
        /// the recognizer has read.
        /// </summary>
        public TimeSpan CapturedPosition
        {
            get { return TimeSpan.FromSeconds(Interlocked.Read(ref capturedBytes) / (double)BytesPerSecond); }
        }

        // ---- Capture thread (owns every COM object) ---------------------------------------------------

        private void CaptureThread()
        {
            AudioInputDevices.IMMDeviceEnumerator enumerator = null;
            AudioInputDevices.IMMDevice device = null;
            object clientObject = null;
            IAudioClient client = null;
            object captureObject = null;
            IAudioCaptureClient capture = null;
            bool started = false;

            try
            {
                enumerator = AudioInputDevices.CreateEnumerator();
                int hr = enumerator.GetDevice(endpointId, out device);
                int state = 0;
                if (hr < 0 || device == null || device.GetState(out state) < 0 || state != DEVICE_STATE_ACTIVE)
                {
                    openError = "not connected";
                    return;
                }

                Guid clientId = typeof(IAudioClient).GUID;
                hr = device.Activate(ref clientId, CLSCTX_ALL, IntPtr.Zero, out clientObject);
                client = clientObject as IAudioClient;
                if (hr < 0 || client == null)
                {
                    openError = "could not open the audio client (HRESULT 0x" + hr.ToString("X8") + ")";
                    return;
                }

                WaveFormatEx format = new WaveFormatEx();
                format.FormatTag = 1;
                format.Channels = 1;
                format.SamplesPerSec = SampleRate;
                format.AvgBytesPerSec = BytesPerSecond;
                format.BlockAlign = 2;
                format.BitsPerSample = 16;
                format.ExtraSize = 0;

                hr = client.Initialize(AUDCLNT_SHAREMODE_SHARED,
                    AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                    BufferDuration100ns, 0, ref format, IntPtr.Zero);
                if (hr < 0)
                {
                    openError = DescribeHResult("could not start capture", hr);
                    return;
                }

                Guid captureId = typeof(IAudioCaptureClient).GUID;
                hr = client.GetService(ref captureId, out captureObject);
                capture = captureObject as IAudioCaptureClient;
                if (hr < 0 || capture == null)
                {
                    openError = DescribeHResult("could not read from the microphone", hr);
                    return;
                }

                hr = client.Start();
                if (hr < 0)
                {
                    openError = DescribeHResult("could not start capture", hr);
                    return;
                }

                started = true;
                opened.Set();
                Pump(capture);
            }
            catch (Exception ex)
            {
                if (!started)
                {
                    openError = ex.Message;
                }
                else
                {
                    Fail("capture failed: " + ex.Message);
                }
            }
            finally
            {
                if (started)
                {
                    try
                    {
                        client.Stop();
                    }
                    catch (Exception)
                    {
                        // The device may already be gone.
                    }
                }

                AudioInputDevices.Release(capture ?? captureObject);
                AudioInputDevices.Release(client ?? clientObject);
                AudioInputDevices.Release(device);
                AudioInputDevices.Release(enumerator);
                Fail(null);
                opened.Set();
            }
        }

        private void Pump(IAudioCaptureClient capture)
        {
            DateTime startedUtc = DateTime.UtcNow;
            DateTime lastAudio = DateTime.MinValue;

            while (!stopping)
            {
                Thread.Sleep(10);

                uint packet;
                int hr = capture.GetNextPacketSize(out packet);
                while (hr >= 0 && packet > 0 && !stopping)
                {
                    IntPtr data;
                    uint frames;
                    uint flags;
                    ulong devicePosition;
                    ulong qpcPosition;
                    hr = capture.GetBuffer(out data, out frames, out flags, out devicePosition, out qpcPosition);
                    if (hr < 0)
                    {
                        break;
                    }

                    if (frames > 0)
                    {
                        byte[] chunk = new byte[frames * 2];
                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && data != IntPtr.Zero)
                        {
                            Marshal.Copy(data, chunk, 0, chunk.Length);
                        }

                        Deliver(chunk);
                        lastAudio = DateTime.UtcNow;
                    }

                    hr = capture.ReleaseBuffer(frames);
                    if (hr >= 0)
                    {
                        hr = capture.GetNextPacketSize(out packet);
                    }
                }

                if (hr < 0)
                {
                    // AUDCLNT_E_DEVICE_INVALIDATED (0x88890004) when unplugged or disabled.
                    Fail(DescribeHResult("the microphone was disconnected", hr));
                    return;
                }

                DateTime now = DateTime.UtcNow;
                if (lastAudio == DateTime.MinValue ? now - startedUtc > FirstAudioTimeout : now - lastAudio > AudioStallTimeout)
                {
                    Fail(lastAudio == DateTime.MinValue ? "no audio arrived from the microphone" : "the microphone stopped delivering audio");
                    return;
                }
            }
        }

        private static string DescribeHResult(string what, int hr)
        {
            switch ((uint)hr)
            {
                case 0x88890004: return what + " (device removed or disabled)";
                case 0x8889000A: return what + " (device in use by another application in exclusive mode)";
                case 0x80070005: return what + " (access denied - check Windows microphone privacy settings)";
                default: return what + " (HRESULT 0x" + hr.ToString("X8") + ")";
            }
        }

        private void Deliver(byte[] chunk)
        {
            int peak = 0;
            for (int i = 0; i + 1 < chunk.Length; i += 2)
            {
                int sample = (short)(chunk[i] | (chunk[i + 1] << 8));
                if (sample < 0)
                {
                    sample = -sample;
                }

                if (sample > peak)
                {
                    peak = sample;
                }
            }

            int scaled = (int)Math.Min(100, peak * 100L / 32767);
            int previous = Volatile.Read(ref level);
            Volatile.Write(ref level, scaled >= previous ? scaled : previous - Math.Max(1, (previous - scaled) / 4));
            Interlocked.Add(ref capturedBytes, chunk.Length);

            lock (gate)
            {
                if (ended)
                {
                    return;
                }

                chunks.Enqueue(chunk);
                queuedBytes += chunk.Length;
                while (queuedBytes > MaxQueuedBytes && chunks.Count > 1)
                {
                    byte[] dropped = chunks.Dequeue();
                    queuedBytes -= dropped.Length - headOffset;
                    headOffset = 0;
                }

                Monitor.PulseAll(gate);
            }
        }

        /// <summary>
        /// Ends the stream; <paramref name="reason"/> is recorded unless it ended already (or
        /// null for a normal close).
        /// </summary>
        private void Fail(string reason)
        {
            lock (gate)
            {
                if (!ended)
                {
                    ended = true;
                    failureReason = reason;
                }

                Monitor.PulseAll(gate);
            }
        }

        // ---- Stream --------------------------------------------------------------------------------

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count <= 0)
            {
                return 0;
            }

            lock (gate)
            {
                // A short read means "end of stream" to the recognizer: wait for the whole request.
                while (queuedBytes < count && !ended)
                {
                    Monitor.Wait(gate, 250);
                }

                int copied = 0;
                while (copied < count && chunks.Count > 0)
                {
                    byte[] head = chunks.Peek();
                    int take = Math.Min(count - copied, head.Length - headOffset);
                    Buffer.BlockCopy(head, headOffset, buffer, offset + copied, take);
                    copied += take;
                    headOffset += take;
                    if (headOffset >= head.Length)
                    {
                        chunks.Dequeue();
                        headOffset = 0;
                    }
                }

                queuedBytes -= copied;
                readBytes += copied;
                return copied;
            }
        }

        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return false; }
        }

        /// <summary>
        /// A live stream has no end; the recognizer only needs a large, stable value.
        /// </summary>
        public override long Length
        {
            get { return 1L << 40; }
        }

        public override long Position
        {
            get { lock (gate) { return readBytes; } }
            set { }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return Position;
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Shutdown();
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Ends the stream (a blocked Read returns at once) and stops the capture thread, which
        /// stops the device and releases its COM objects on the way out.
        /// </summary>
        private void Shutdown()
        {
            lock (gate)
            {
                if (closed)
                {
                    return;
                }

                closed = true;
                ended = true;
                chunks.Clear();
                queuedBytes = 0;
                headOffset = 0;
                Monitor.PulseAll(gate);
            }

            stopping = true;
            if (captureThread != null && captureThread != Thread.CurrentThread
                && !captureThread.Join(TimeSpan.FromSeconds(2)))
            {
                RuntimeLog.Write("Microphone capture thread did not stop within 2 s");
            }
        }
    }
}
