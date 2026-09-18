using System;
using System.Collections.Generic;

namespace KinectV2MouseControl
{
    public class VoiceRecognizedEventArgs : EventArgs
    {
        /// <summary>
        /// Matched command, or null when speech was heard but nothing in the grammar matched
        /// with enough confidence.
        /// </summary>
        public VoiceCommand Command { get; private set; }

        public string Text { get; private set; }

        public double Confidence { get; private set; }

        public VoiceRecognizedEventArgs(VoiceCommand command, string text, double confidence)
        {
            Command = command;
            Text = text;
            Confidence = confidence;
        }
    }

    /// <summary>
    /// Contract between the control center and whatever recognizes speech. The only
    /// implementation today is the local, in-process Windows recognizer; a different backend
    /// (a local neural model, a cloud service) would implement this and nothing above it
    /// changes.
    ///
    /// Events are raised on whatever thread the backend uses. The view model marshals.
    /// </summary>
    public interface IVoiceEngine : IDisposable
    {
        bool IsRunning { get; }

        /// <summary>
        /// Short human-readable state: "Listening", "Hearing speech", "Stopped", an error...
        /// </summary>
        string StatusText { get; }

        string BackendName { get; }

        event EventHandler<VoiceRecognizedEventArgs> Recognized;

        event EventHandler StateChanged;

        /// <summary>
        /// Builds the grammar from the given commands and starts listening. Returns false, with
        /// a reason, if the backend cannot start (no recognizer, no microphone...).
        /// </summary>
        bool Start(IList<VoiceCommand> commands, string wakeWord, out string error);

        void Stop();
    }
}
