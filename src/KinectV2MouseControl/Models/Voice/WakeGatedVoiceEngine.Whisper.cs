using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KinectV2MouseControl
{
    public sealed partial class WakeGatedVoiceEngine
    {
        private void StopWhisper()
        {
            PcmTapStream closingTap;
            WhisperService closingServer;
            lock (sync)
            {
                inputGeneration++;
                if (recordingCancellation != null) { recordingCancellation.Cancel(); recordingCancellation = null; }
                closingTap = tap;
                tap = null;
                closingServer = whisper;
                whisper = null;
            }
            if (closingTap != null) { closingTap.Dispose(); }
            if (closingServer != null) { closingServer.Dispose(); }
        }

        /// <summary>
        /// Checks the generation again on the UI thread: a finished worker can raise its event
        /// after a microphone restart, even though its earlier session check passed.
        /// </summary>
        public bool IsCurrent(VoiceCommandEventArgs command)
        {
            lock (sync) { return command.Generation == inputGeneration && phase != VoicePhase.Off; }
        }

        private void BeginWhisperRecording(int id, TimeSpan gate)
        {
            PcmTapStream source;
            WhisperService service;
            CancellationTokenSource cancellation;
            VoiceSession current;
            CustomPhraseSet phrases;
            string prompt;
            int generation;
            lock (sync)
            {
                if (!IsCurrent(id, VoicePhase.Listening) || tap == null || whisper == null) { return; }
                source = tap;
                service = whisper;
                current = session;
                generation = inputGeneration;
                phrases = activeCustomPhrases;
                StringBuilder vocabulary = new StringBuilder(activeWakeWord + ", ChatGPT, Claude, Cursor, Edge, YouTube, DeepSeek, Spotify");
                foreach (CustomPhraseSet.Entry entry in phrases.Entries) { vocabulary.Append(", ").Append(entry.Phrase); }
                prompt = vocabulary.Append('.').ToString();
                cancellation = new CancellationTokenSource();
                recordingCancellation = cancellation;
            }
            Task.Run(async () =>
            {
                try
                {
                    VoiceRecording recording = await PostGateRecorder.RecordAsync(source, gate, commandWindow,
                        () => RecordingPhase(current, generation, VoicePhase.Recording, "Hearing you…"), cancellation.Token).ConfigureAwait(false);
                    if (recording == null)
                    {
                        FinishWhisper(current, generation, VoiceOutcome.TimedOut, "no speech after the chime", null, null);
                        return;
                    }
                    cancellation.Token.ThrowIfCancellationRequested();
                    current.SpeechEndUtc = recording.SpeechEndUtc;
                    RecordingPhase(current, generation, VoicePhase.Transcribing, "Understanding…");
                    if (ListeningClickEnabled && Feedback != null) { Feedback.PlayListeningEnd(); }
                    lock (sync)
                    {
                        if (generation != inputGeneration || !ReferenceEquals(current, session)) { return; }
                        TranscriptionRequests++;
                    }
                    string text = await service.TranscribeAsync(recording.Samples, prompt, cancellation.Token).ConfigureAwait(false);
                    current.TranscriptUtc = DateTime.UtcNow;
                    RuntimeLog.Write("Whisper #" + id + " speech end → transcript "
                        + (current.TranscriptUtc - current.SpeechEndUtc).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms");
                    if (text.Length == 0)
                    {
                        FinishWhisper(current, generation, VoiceOutcome.NotRecognized, "No speech was transcribed", null, null);
                        return;
                    }
                    VoiceIntent intent;
                    string reason;
                    if (!VoiceCommandParser.TryParse(text, phrases, out intent, out reason))
                    {
                        if (VoiceCommandParser.IsBuiltInCommand(text))
                        {
                            FinishWhisper(current, generation, VoiceOutcome.NotRecognized, reason, text, null);
                            return;
                        }
                        intent = VoiceIntent.ForRequest(text);
                    }
                    FinishWhisper(current, generation, intent.Kind == VoiceIntentKind.Cancel ? VoiceOutcome.Cancelled : VoiceOutcome.Executed,
                        intent.Feedback, text, intent);
                }
                catch (OperationCanceledException)
                {
                    if (!cancellation.IsCancellationRequested)
                    { FinishWhisper(current, generation, VoiceOutcome.NotRecognized, "Whisper transcription timed out", null, null); }
                }
                catch (Exception ex)
                { FinishWhisper(current, generation, VoiceOutcome.NotRecognized, "Whisper: " + ex.Message, null, null); }
                finally
                {
                    lock (sync) { if (ReferenceEquals(recordingCancellation, cancellation)) { recordingCancellation = null; } }
                    cancellation.Dispose();
                }
            });
        }

        private void RecordingPhase(VoiceSession current, int generation, VoicePhase value, string detail)
        {
            lock (sync)
            {
                if (generation != inputGeneration || !ReferenceEquals(current, session)) { return; }
                phase = value;
            }
            Raise(PhaseChanged, new VoicePhaseEventArgs(value, current, VoiceOutcome.None, detail));
        }

        private void FinishWhisper(VoiceSession current, int generation, VoiceOutcome outcome, string detail, string text, VoiceIntent intent)
        {
            Pending pending = new Pending();
            lock (sync)
            {
                if (generation != inputGeneration || !ReferenceEquals(current, session)) { return; }
                if (intent != null && intent.Kind != VoiceIntentKind.Cancel)
                {
                    if (!current.TryAuthorize()) { return; }
                    pending.Command = new VoiceCommandEventArgs(current, intent, text, double.NaN) { Generation = generation };
                }
                pending.Decisions.Add(new VoiceDecision("Whisper", intent != null ? VoiceVerdict.Accepted : VoiceVerdict.Rejected,
                    text, double.NaN, double.NaN, double.NaN, double.NaN, detail,
                    current.TranscriptUtc == default(DateTime) ? "" : "speech end → transcript "
                    + (current.TranscriptUtc - current.SpeechEndUtc).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms", current.Id));
                EndSession(outcome, detail, pending);
            }
            Run(pending);
        }
    }
}
