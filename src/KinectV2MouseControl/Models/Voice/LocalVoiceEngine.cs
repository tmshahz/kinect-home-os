using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Speech.Recognition;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Voice commands through the Windows desktop speech recognizer (System.Speech), entirely
    /// on this machine.
    ///
    /// The recognizer is given a closed grammar - optional wake word followed by exactly one of
    /// the known phrases - rather than free dictation. A closed grammar is what makes a small
    /// vocabulary reliable: the engine only has to decide which of thirty phrases it heard,
    /// not transcribe arbitrary speech. The wake word is part of that same grammar, so "next
    /// window" said in conversation does nothing while "Kinect, next window" does.
    ///
    /// Everything here is off by default and started only from the Voice page. It is
    /// independent of the gesture engine: recognition results are handed to the view model,
    /// which executes them through the same ActionRouter the gestures use, on the UI thread.
    ///
    /// Note for Kinect users: the sensor's microphone array is a perfectly good input, but on
    /// some Windows 11 machines its audio enhancements have caused the sensor to reconnect
    /// repeatedly (see CLAUDE.md §7). If enabling voice coincides with "Kinect unavailable"
    /// entries in the activity feed, pick another microphone as the Windows default or disable
    /// the enhancements.
    /// </summary>
    public sealed class LocalVoiceEngine : IVoiceEngine
    {
        /// <summary>
        /// Minimum recognizer confidence for a phrase to be acted on. The closed grammar makes
        /// the engine fairly sure of itself; below this it usually heard something else.
        /// </summary>
        public const double MinimumConfidence = 0.62;

        private const string SemanticKey = "command";

        private SpeechRecognitionEngine recognizer;
        private Dictionary<string, VoiceCommand> commandsById = new Dictionary<string, VoiceCommand>();
        private string statusText = "Off";
        private string backendName = "Windows speech recognizer";

        public bool IsRunning { get; private set; }

        public string StatusText
        {
            get
            {
                return statusText;
            }
        }

        public string BackendName
        {
            get
            {
                return backendName;
            }
        }

        public event EventHandler<VoiceRecognizedEventArgs> Recognized;

        public event EventHandler StateChanged;

        /// <summary>
        /// Names the recognizer that would be used, or explains why none can be, without
        /// starting anything. Used for the Voice page's status card.
        /// </summary>
        public static string DescribeAvailability()
        {
            try
            {
                RecognizerInfo info = PickRecognizer();
                return info == null
                    ? "No speech recognizer is installed for this Windows language"
                    : info.Description;
            }
            catch (Exception ex)
            {
                return "Speech recognition unavailable (" + ex.GetType().Name + ")";
            }
        }

        private static RecognizerInfo PickRecognizer()
        {
            ReadOnlyCollection<RecognizerInfo> installed = SpeechRecognitionEngine.InstalledRecognizers();
            if (installed.Count == 0)
            {
                return null;
            }

            CultureInfo ui = CultureInfo.CurrentUICulture;
            for (int i = 0; i < installed.Count; i++)
            {
                if (installed[i].Culture.Equals(ui))
                {
                    return installed[i];
                }
            }

            for (int i = 0; i < installed.Count; i++)
            {
                if (installed[i].Culture.TwoLetterISOLanguageName == "en")
                {
                    return installed[i];
                }
            }

            return installed[0];
        }

        public bool Start(IList<VoiceCommand> commands, string wakeWord, out string error)
        {
            error = null;
            Stop();

            try
            {
                RecognizerInfo info = PickRecognizer();
                if (info == null)
                {
                    error = "No speech recognizer is installed for this Windows language.";
                    SetStatus("Unavailable");
                    return false;
                }

                backendName = info.Description;

                recognizer = new SpeechRecognitionEngine(info);
                recognizer.LoadGrammar(BuildGrammar(commands, wakeWord, info.Culture));
                recognizer.SpeechRecognized += Recognizer_SpeechRecognized;
                recognizer.SpeechRecognitionRejected += Recognizer_SpeechRecognitionRejected;
                recognizer.AudioStateChanged += Recognizer_AudioStateChanged;
                recognizer.RecognizeCompleted += Recognizer_RecognizeCompleted;

                // Throws when there is no default capture device at all.
                recognizer.SetInputToDefaultAudioDevice();
                recognizer.RecognizeAsync(RecognizeMode.Multiple);

                IsRunning = true;
                SetStatus("Listening");
                RuntimeLog.Write("Voice started (" + backendName + ", wake word '" + (wakeWord ?? "") + "', "
                    + commandsById.Count + " commands)");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                RuntimeLog.Write("Voice start failed: " + ex);
                DisposeRecognizer();
                IsRunning = false;
                SetStatus("Failed to start");
                return false;
            }
        }

        private Grammar BuildGrammar(IList<VoiceCommand> commands, string wakeWord, CultureInfo culture)
        {
            commandsById = new Dictionary<string, VoiceCommand>();
            Choices phrases = new Choices();
            int count = 0;

            for (int i = 0; i < commands.Count; i++)
            {
                VoiceCommand command = commands[i];
                if (!command.IsAvailable)
                {
                    continue;
                }

                commandsById[command.Id] = command;
                foreach (string phrase in command.AllPhrases())
                {
                    if (string.IsNullOrWhiteSpace(phrase))
                    {
                        continue;
                    }

                    phrases.Add(new GrammarBuilder(new SemanticResultValue(phrase, command.Id)));
                    count++;
                }
            }

            if (count == 0)
            {
                throw new InvalidOperationException("No available voice commands to listen for.");
            }

            GrammarBuilder builder = new GrammarBuilder();
            builder.Culture = culture;

            if (!string.IsNullOrWhiteSpace(wakeWord))
            {
                builder.Append(wakeWord.Trim());
            }

            builder.Append(new SemanticResultKey(SemanticKey, phrases));

            Grammar grammar = new Grammar(builder);
            grammar.Name = "KINECT-OS commands";
            return grammar;
        }

        private void Recognizer_SpeechRecognized(object sender, SpeechRecognizedEventArgs e)
        {
            VoiceCommand command = null;

            if (e.Result != null && e.Result.Semantics != null && e.Result.Semantics.ContainsKey(SemanticKey))
            {
                string id = e.Result.Semantics[SemanticKey].Value as string;
                if (id != null && e.Result.Confidence >= MinimumConfidence)
                {
                    commandsById.TryGetValue(id, out command);
                }
            }

            string text = e.Result != null ? e.Result.Text : "";
            double confidence = e.Result != null ? e.Result.Confidence : 0;
            RaiseRecognized(command, text, confidence);
        }

        private void Recognizer_SpeechRecognitionRejected(object sender, SpeechRecognitionRejectedEventArgs e)
        {
            string text = e.Result != null ? e.Result.Text : "";
            double confidence = e.Result != null ? e.Result.Confidence : 0;
            RaiseRecognized(null, text, confidence);
        }

        private void Recognizer_AudioStateChanged(object sender, AudioStateChangedEventArgs e)
        {
            if (!IsRunning)
            {
                return;
            }

            switch (e.AudioState)
            {
                case AudioState.Speech:
                    SetStatus("Hearing speech");
                    break;
                case AudioState.Silence:
                    SetStatus("Listening");
                    break;
                default:
                    SetStatus("No audio");
                    break;
            }
        }

        private void Recognizer_RecognizeCompleted(object sender, RecognizeCompletedEventArgs e)
        {
            if (e.Error != null)
            {
                RuntimeLog.Write("Voice recognition stopped with error: " + e.Error.Message);
                IsRunning = false;
                SetStatus("Stopped: " + e.Error.Message);
            }
        }

        private void RaiseRecognized(VoiceCommand command, string text, double confidence)
        {
            EventHandler<VoiceRecognizedEventArgs> handler = Recognized;
            if (handler != null)
            {
                handler.Invoke(this, new VoiceRecognizedEventArgs(command, text, confidence));
            }
        }

        private void SetStatus(string text)
        {
            statusText = text;
            EventHandler handler = StateChanged;
            if (handler != null)
            {
                handler.Invoke(this, EventArgs.Empty);
            }
        }

        public void Stop()
        {
            if (recognizer == null)
            {
                return;
            }

            DisposeRecognizer();
            IsRunning = false;
            SetStatus("Off");
            RuntimeLog.Write("Voice stopped");
        }

        private void DisposeRecognizer()
        {
            SpeechRecognitionEngine engine = recognizer;
            recognizer = null;
            if (engine == null)
            {
                return;
            }

            try
            {
                engine.SpeechRecognized -= Recognizer_SpeechRecognized;
                engine.SpeechRecognitionRejected -= Recognizer_SpeechRecognitionRejected;
                engine.AudioStateChanged -= Recognizer_AudioStateChanged;
                engine.RecognizeCompleted -= Recognizer_RecognizeCompleted;
                engine.RecognizeAsyncCancel();
                engine.Dispose();
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("Voice recognizer dispose: " + ex.Message);
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
