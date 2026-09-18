using System;
using System.IO;
using System.Media;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Audible feedback for the voice pipeline. The wake chime is part of the protocol, not
    /// decoration: it is the "now speak" signal, and the command window only opens once it has
    /// finished, so the engine needs to know exactly when that is.
    /// </summary>
    public interface IVoiceFeedback
    {
        /// <summary>
        /// Length of the wake chime as synthesized (not counting output latency).
        /// </summary>
        TimeSpan WakeChimeDuration { get; }

        /// <summary>
        /// Plays the wake chime and returns once it has been played. Called on a background
        /// thread; must not throw.
        /// </summary>
        void PlayWakeChime();

        /// <summary>
        /// Short, quiet "window closed without a command" tone. Fire and forget.
        /// </summary>
        void PlayDismiss();
    }

    /// <summary>
    /// Both tones are synthesized in code - no asset files, no network, no Windows system
    /// sounds - and played with the winmm PlaySound path through SoundPlayer.
    ///
    /// Wake chime: a soft two-note "activation ping" (G6 rising a few cents into D7), 150 ms,
    /// fast attack and exponential decay, peaking around -10 dBFS. Short on purpose: the
    /// command window opens right after it, and a long sound would both delay the user and
    /// leave more of itself in the microphone.
    ///
    /// Dismiss: a single low, falling blip at roughly a third of the chime's level.
    /// </summary>
    public sealed class VoiceFeedbackSounds : IVoiceFeedback, IDisposable
    {
        public const int PlaybackSampleRate = 44100;

        private readonly SoundPlayer wakePlayer;
        private readonly SoundPlayer dismissPlayer;
        private readonly TimeSpan wakeDuration;

        public VoiceFeedbackSounds()
        {
            short[] wake = SynthesizeWakeChime(PlaybackSampleRate);
            wakeDuration = TimeSpan.FromSeconds(wake.Length / (double)PlaybackSampleRate);
            wakePlayer = CreatePlayer(wake);
            dismissPlayer = CreatePlayer(SynthesizeDismiss(PlaybackSampleRate));
        }

        public TimeSpan WakeChimeDuration
        {
            get
            {
                return wakeDuration;
            }
        }

        public void PlayWakeChime()
        {
            try
            {
                wakePlayer.PlaySync();
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("Wake chime could not play: " + ex.Message);
            }
        }

        public void PlayDismiss()
        {
            try
            {
                dismissPlayer.Play();
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("Dismiss tone could not play: " + ex.Message);
            }
        }

        private static SoundPlayer CreatePlayer(short[] samples)
        {
            SoundPlayer player = new SoundPlayer(new MemoryStream(ToWav(samples, PlaybackSampleRate)));
            try
            {
                // Parse the WAV now rather than on the first wake, so the first chime is as
                // prompt as the rest.
                player.Load();
            }
            catch (Exception)
            {
                // Played lazily instead.
            }

            return player;
        }

        /// <summary>
        /// Two decaying partial pairs, the second note entering 38 ms after the first.
        /// </summary>
        public static short[] SynthesizeWakeChime(int sampleRate)
        {
            const double duration = 0.150;
            const double master = 0.30;
            int count = (int)(sampleRate * duration);
            short[] samples = new short[count];

            for (int i = 0; i < count; i++)
            {
                double t = i / (double)sampleRate;
                double value = 0;

                // Note 1: G6 with a slight upward glide, plus a quiet octave for sparkle.
                double f1 = 1567.98 * (1 + 0.012 * Math.Min(1, t / 0.04));
                double env1 = Attack(t, 0.004) * Math.Exp(-t / 0.045);
                value += env1 * (0.62 * Math.Sin(2 * Math.PI * f1 * t) + 0.10 * Math.Sin(4 * Math.PI * f1 * t));

                // Note 2: D7, entering shortly after - the "rising" feel of an activation.
                double t2 = t - 0.038;
                if (t2 >= 0)
                {
                    double f2 = 2349.32;
                    double env2 = Attack(t2, 0.003) * Math.Exp(-t2 / 0.040);
                    value += env2 * (0.48 * Math.Sin(2 * Math.PI * f2 * t2) + 0.06 * Math.Sin(4 * Math.PI * f2 * t2));
                }

                // Fade the last 12 ms to silence so the end never clicks.
                double tail = Math.Min(1, (duration - t) / 0.012);
                samples[i] = ToSample(Math.Tanh(value * 1.1) * master * tail);
            }

            return samples;
        }

        /// <summary>
        /// A low tone falling from 740 Hz to 520 Hz over 110 ms, quiet.
        /// </summary>
        public static short[] SynthesizeDismiss(int sampleRate)
        {
            const double duration = 0.110;
            const double master = 0.11;
            int count = (int)(sampleRate * duration);
            short[] samples = new short[count];
            double phase = 0;

            for (int i = 0; i < count; i++)
            {
                double t = i / (double)sampleRate;
                double frequency = 740 * Math.Pow(520.0 / 740.0, t / duration);
                phase += 2 * Math.PI * frequency / sampleRate;
                double env = Attack(t, 0.005) * Math.Exp(-t / 0.050) * Math.Min(1, (duration - t) / 0.015);
                samples[i] = ToSample(Math.Sin(phase) * env * master);
            }

            return samples;
        }

        private static double Attack(double t, double seconds)
        {
            if (t >= seconds)
            {
                return 1;
            }

            // Raised cosine: no click at the onset.
            return 0.5 - 0.5 * Math.Cos(Math.PI * t / seconds);
        }

        private static short ToSample(double value)
        {
            double clamped = Math.Max(-1, Math.Min(1, value));
            return (short)Math.Round(clamped * short.MaxValue);
        }

        /// <summary>
        /// Canonical 44-byte-header PCM WAV, mono 16-bit.
        /// </summary>
        public static byte[] ToWav(short[] samples, int sampleRate)
        {
            using (MemoryStream stream = new MemoryStream(44 + samples.Length * 2))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                int dataBytes = samples.Length * 2;
                writer.Write(new[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + dataBytes);
                writer.Write(new[] { 'W', 'A', 'V', 'E' });
                writer.Write(new[] { 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(sampleRate);
                writer.Write(sampleRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(new[] { 'd', 'a', 't', 'a' });
                writer.Write(dataBytes);
                for (int i = 0; i < samples.Length; i++)
                {
                    writer.Write(samples[i]);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        public void Dispose()
        {
            wakePlayer.Dispose();
            dismissPlayer.Dispose();
        }
    }
}
