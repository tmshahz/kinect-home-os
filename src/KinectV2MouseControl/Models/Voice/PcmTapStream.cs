using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace KinectV2MouseControl
{
    /// <summary>
    /// A single input owner with a 30-second PCM ring. A small pump keeps capture flowing even
    /// while SAPI has no enabled grammar. Both readers use the same zero-based sample clock.
    /// </summary>
    internal sealed class PcmTapStream : Stream
    {
        public const int Rate = 16000;
        public const int FrameSamples = 320;
        private readonly Stream input;
        private readonly object sync = new object();
        private readonly byte[] ring = new byte[Rate * 2 * 30];
        private long written;
        private long read;
        private bool ended;
        private Exception failure;
        private double noise = 0.001;

        public PcmTapStream(Stream input)
        {
            this.input = input;
            Thread pump = new Thread(Pump) { IsBackground = true, Name = "KINECT-OS voice PCM tap" };
            pump.Start();
        }

        public TimeSpan CapturedPosition { get { lock (sync) { return TimeSpan.FromSeconds(written / 32000.0); } } }
        public double NoiseFloor { get { lock (sync) { return noise; } } }

        private void Pump()
        {
            byte[] buffer = new byte[FrameSamples * 2];
            try
            {
                while (true)
                {
                    int count = input.Read(buffer, 0, buffer.Length);
                    if (count == 0) { break; }
                    lock (sync)
                    {
                        if (ended) { break; }
                        for (int i = 0; i < count; i++) { ring[(written + i) % ring.Length] = buffer[i]; }
                        written += count;
                        double energy = Energy(buffer, count);
                        // Follow quieter frames promptly; do not learn a spoken word as room noise.
                        noise += (energy - noise) * (energy < noise ? 0.1 : 0.0005);
                        noise = Math.Max(0.0001, Math.Min(0.02, noise));
                        Monitor.PulseAll(sync);
                    }
                }
            }
            catch (Exception ex) { lock (sync) { failure = ex; } }
            finally { lock (sync) { ended = true; Monitor.PulseAll(sync); } }
        }

        internal static double Energy(byte[] frame, int count)
        {
            double sum = 0;
            for (int i = 0; i + 1 < count; i += 2)
            {
                double sample = (short)(frame[i] | frame[i + 1] << 8) / 32768.0;
                sum += sample * sample;
            }
            return Math.Sqrt(sum / Math.Max(1, count / 2));
        }

        public bool CopyFrame(long bytePosition, byte[] frame)
        {
            lock (sync)
            {
                if (bytePosition < written - ring.Length) { throw new IOException("Voice recording fell behind the input."); }
                if (written < bytePosition + frame.Length)
                {
                    if (ended) { throw new EndOfStreamException("Voice input ended.", failure); }
                    return false;
                }
                for (int i = 0; i < frame.Length; i++) { frame[i] = ring[(bytePosition + i) % ring.Length]; }
                return true;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (sync)
            {
                while (!ended && written - read < count) { Monitor.Wait(sync, 100); }
                if (read < written - ring.Length) { throw new IOException("Speech recognizer fell behind the input."); }
                int available = (int)Math.Min(count, written - read);
                for (int i = 0; i < available; i++) { buffer[offset + i] = ring[(read + i) % ring.Length]; }
                read += available;
                return available;
            }
        }

        protected override void Dispose(bool disposing)
        {
            lock (sync) { ended = true; Monitor.PulseAll(sync); }
            if (disposing) { input.Dispose(); }
            base.Dispose(disposing);
        }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return 1L << 40; } }
        public override long Position { get { lock (sync) { return read; } } set { } }
        public override long Seek(long offset, SeekOrigin origin) { return Position; }
        public override void Flush() { }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }

    internal sealed class VoiceRecording
    {
        public short[] Samples { get; set; }
        public DateTime SpeechEndUtc { get; set; }
        public double SpeechSeconds { get; set; }
    }

    /// <summary>
    /// Records only samples at or after the chime gate. Silence never reaches Whisper, which
    /// can otherwise produce plausible text even from an empty room.
    /// </summary>
    internal static class PostGateRecorder
    {
        public static async Task<VoiceRecording> RecordAsync(PcmTapStream tap, TimeSpan gate, TimeSpan startWindow,
            Action speechStarted, CancellationToken token)
        {
            long start = (long)Math.Ceiling(gate.TotalSeconds * 16000) * 2;
            long position = start;
            byte[] frame = new byte[PcmTapStream.FrameSamples * 2];
            List<short> samples = new List<short>();
            Queue<short[]> lead = new Queue<short[]>();
            double floor = tap.NoiseFloor;
            int voiced = 0;
            int quiet = 0;
            int elapsedFrames = 0;
            bool recording = false;
            Stopwatch watchdog = Stopwatch.StartNew();
            DateTime lastSpeech = DateTime.UtcNow;
            while (watchdog.Elapsed < TimeSpan.FromSeconds(startWindow.TotalSeconds + 14))
            {
                token.ThrowIfCancellationRequested();
                if (!tap.CopyFrame(position, frame)) { await Task.Delay(10, token).ConfigureAwait(false); continue; }
                position += frame.Length;
                double energy = PcmTapStream.Energy(frame, frame.Length);
                bool speech = energy >= Math.Max(0.004, floor * 3.5);
                short[] chunk = new short[PcmTapStream.FrameSamples];
                Buffer.BlockCopy(frame, 0, chunk, 0, frame.Length);
                if (!recording)
                {
                    lead.Enqueue(chunk);
                    if (lead.Count > 12) { lead.Dequeue(); }
                    voiced = speech ? voiced + 1 : Math.Max(0, voiced - 1);
                    if (!speech) { floor += (energy - floor) * 0.02; }
                    if (voiced >= 8)
                    {
                        recording = true;
                        foreach (short[] pre in lead) { samples.AddRange(pre); }
                        elapsedFrames = lead.Count;
                        lastSpeech = DateTime.UtcNow;
                        speechStarted();
                    }
                    else if ((position - start) / 32000.0 >= startWindow.TotalSeconds) { return null; }
                }
                else
                {
                    samples.AddRange(chunk);
                    elapsedFrames++;
                    quiet = speech ? 0 : quiet + 1;
                    if (speech) { lastSpeech = DateTime.UtcNow; }
                    if (quiet >= 35 || elapsedFrames >= 500)
                    {
                        // Keep 100 ms of tail; the 700 ms endpoint wait is not model input.
                        int trim = Math.Max(0, quiet - 5) * PcmTapStream.FrameSamples;
                        if (trim > 0) { samples.RemoveRange(samples.Count - trim, trim); }
                        return new VoiceRecording { Samples = samples.ToArray(), SpeechEndUtc = lastSpeech,
                            SpeechSeconds = (elapsedFrames - quiet) * 0.02 };
                    }
                }
            }
            throw new IOException("Voice input stopped advancing during recording.");
        }
    }
}
