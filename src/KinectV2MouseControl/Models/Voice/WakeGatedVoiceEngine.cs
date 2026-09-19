using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Speech.Recognition;
using System.Threading;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Local voice commands, strictly gated by the wake word.
    ///
    ///   "Jarvis"  →  ✦ chime  →  one command  →  back to waiting for "Jarvis"
    ///
    /// The wake word is the user's choice ("Jarvis" by default, originally "Kinect"); every
    /// check below applies to whatever it is. Custom command phrases join the command grammar
    /// and are judged exactly like the built-in ones.
    ///
    /// Why the first version fired commands out of ordinary conversation: it loaded a single
    /// closed grammar, "Kinect" + command, and executed whatever that grammar reported. A
    /// closed-grammar recognizer has nowhere else to put speech, so it scores every utterance
    /// against that grammar and reports the closest path - including an invented "Kinect" -
    /// and the desktop engine also spots grammar phrases inside longer sentences. Measured on
    /// this machine, "Let's go to sleep, I'm tired" came back as "Kinect go to sleep" at 0.66
    /// confidence, above the 0.62 cut-off: Control off. One false match was one action.
    ///
    /// What replaces it, so that execution is impossible outside a deliberate wake cycle:
    ///
    ///  1. Two grammars, never enabled together. In WakeOnly only the one-word wake grammar
    ///     is live; the command grammar is enabled only after a wake is accepted and disabled
    ///     again when the session ends. A wake result can only open a session, and nothing but
    ///     a command-grammar phrase in an open window can produce an intent.
    ///  2. Isolation. The desktop engine reports where the recognized words start; the engine
    ///     also reports where the speech of that utterance started (SpeechDetected). A phrase
    ///     whose words start well after the speech did was spotted inside a longer sentence
    ///     ("Let's go to sleep" → "go to sleep" started 0.40 s in) and is refused. Isolated
    ///     phrases start within ~0.1 s of the speech.
    ///  3. The gate. The command window opens at the audio position captured when the chime
    ///     (plus output and input latency) has finished. A command must begin after it, so the
    ///     chime's own echo and any sentence that ran on from the wake word can never be a command.
    ///  4. One command per wake, consumed on acceptance; unknown speech, a timeout or a
    ///     "cancel" also close the session.
    ///  5. Confidence floors for the wake word and for commands (see the thresholds).
    ///
    /// Threading: the recognizer is created on a private thread with no synchronization
    /// context, so System.Speech raises its events on the thread pool rather than queueing
    /// them behind WPF. All state lives under one lock; events to subscribers are raised
    /// after the lock is released. Grammar enable/disable runs on the thread pool, outside
    /// the state lock, and always converges to the current phase.
    /// </summary>
    public sealed class WakeGatedVoiceEngine : IDisposable
    {
        /// <summary>
        /// Default wake confidence floor, matching the middle "Wake Sensitivity" position the UI
        /// ships. Field testing showed the earlier 0.80 (the recognizer's HighConfidence band)
        /// made a deliberate "Kinect" too hard, especially on narrowband Bluetooth mics: genuine
        /// and false wakes both scored 0.86-0.95, so a high floor rejected real wakes without
        /// being what actually keeps conversation out. The isolation checks (a pause before the
        /// word, the word not buried in a sentence, single-word duration) are the real guard, so
        /// the floor can sit lower - still comfortably above the engine's own 0.60 reject line -
        /// without weakening safety. Wake Sensitivity moves this floor; it never touches the
        /// isolation checks.
        /// </summary>
        public const double DefaultWakeThreshold = 0.65;

        /// <summary>
        /// Above the engine's own grammar rejection threshold (CFGConfidenceRejectionThreshold
        /// = 60). A command is already behind a wake, a chime and a 4 s window, and must be
        /// isolated, so this does not need to be as strict as the wake.
        /// </summary>
        public const double DefaultCommandThreshold = 0.70;

        public static readonly TimeSpan DefaultCommandWindow = TimeSpan.FromSeconds(4);

        /// <summary>
        /// Wake-grammar candidates at or above this are recorded as near misses in the
        /// diagnostics; below it they are just counted as background.
        /// </summary>
        private const double NearMissConfidence = 0.45;

        private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(50);

        /// <summary>
        /// Isolated words start within ~0.12 s of the detected speech; words spotted inside a
        /// sentence start 0.4 s or more in (measured).
        /// </summary>
        private static readonly TimeSpan WakeMaxLead = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan CommandMaxLead = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Silence required before the wake word. The desktop engine splits running speech into
        /// utterances at short pauses, and a fragment of a sentence can score like "Kinect"
        /// (measured: 0.77-0.81 for a piece of "I was playing something earlier and then paused
        /// it", 0.12 s after the previous fragment). Someone addressing the system pauses first.
        /// </summary>
        private static readonly TimeSpan WakeMinGapBefore = TimeSpan.FromMilliseconds(300);

        /// <summary>
        /// A single wake word lasts 0.2-1.1 s; a longer wake phrase is allowed 0.5 s per extra
        /// word (VoiceWakeWord.MaxDuration).
        /// </summary>
        private static readonly TimeSpan WakeMinDuration = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan CommandMaxDuration = TimeSpan.FromMilliseconds(3500);

        /// <summary>
        /// Longest plausible time per recognized word, plus a fixed allowance, for a command.
        /// A closed grammar can stretch a short phrase over a whole sentence (measured: "next
        /// window" matched across 2.4 s of "Next, can I ask you something?"). Deliberate
        /// commands, even said slowly with a pause, stay well inside this.
        /// </summary>
        private const double CommandSecondsPerWord = 0.6;
        private const double CommandSecondsAllowance = 0.5;

        /// <summary>
        /// An unrecognized sound shorter than this in the window (breath, click, cough onset) is
        /// ignored rather than treated as a failed command.
        /// </summary>
        private static readonly TimeSpan NoiseMaxSpan = TimeSpan.FromMilliseconds(300);

        /// <summary>
        /// Utterances that end this soon after the gate are the chime's echo and reverb.
        /// </summary>
        private static readonly TimeSpan EchoTail = TimeSpan.FromMilliseconds(150);

        /// <summary>
        /// If the chime's echo and the start of the command merged into one utterance, the
        /// part before the gate is at most about this long.
        /// </summary>
        private static readonly TimeSpan EchoMaxPreGate = TimeSpan.FromMilliseconds(300);

        /// <summary>
        /// Wait after the chime has played before the gate position is taken: output latency,
        /// input latency and room reverb, so the chime is entirely before the gate.
        /// </summary>
        private static readonly TimeSpan GateGuard = TimeSpan.FromMilliseconds(80);

        /// <summary>
        /// If a command is being spoken when the window runs out, it may finish.
        /// </summary>
        private static readonly TimeSpan WindowExtension = TimeSpan.FromMilliseconds(2500);

        private const int OnsetMemory = 16;

        private readonly object sync = new object();
        private readonly object grammarSync = new object();

        private SpeechRecognitionEngine recognizer;
        private Grammar wakeGrammar;
        private Grammar commandGrammar;
        private VoicePhase phase = VoicePhase.Off;
        private VoiceSession session;
        private int sessionCounter;
        private Timer deadlineTimer;
        private int deadlineSessionId;
        private readonly List<TimeSpan> onsets = new List<TimeSpan>();
        private TimeSpan lastPhraseEnd;
        private bool utteranceOpen;
        private TimeSpan utteranceOnset;
        private bool isHearingSound;
        private int audioLevel;
        private VoiceCounters counters = new VoiceCounters();

        private double wakeThreshold = DefaultWakeThreshold;
        private double commandThreshold = DefaultCommandThreshold;
        private TimeSpan commandWindow = DefaultCommandWindow;
        private bool dismissSoundEnabled = true;
        private string backendName = "Windows speech recognizer";

        /// <summary>
        /// Asked for (used from the next Start) and in use by the running recognizer. Changing
        /// either takes a restart, like a microphone change: the grammars are built at Start.
        /// </summary>
        private string wakeWord = VoiceWakeWord.Default;
        private CustomPhraseSet customPhrases = CustomPhraseSet.Empty;
        private string activeWakeWord = VoiceWakeWord.Default;
        private CustomPhraseSet activeCustomPhrases = CustomPhraseSet.Empty;
        private TimeSpan wakeMaxDuration = VoiceWakeWord.MaxDuration(VoiceWakeWord.Default);

        /// <summary>
        /// The input asked for (null = Windows default) and the one the running recognizer is
        /// actually bound to. A selected device is captured by <see cref="capture"/>.
        /// </summary>
        private string inputDeviceId;
        private string inputDeviceName;
        private string activeInputId;
        private string activeInputName;
        private MicrophoneCaptureStream capture;

        /// <summary>
        /// A capture clock more than this far ahead of the recognizer is not trusted.
        /// </summary>
        private static readonly TimeSpan MaxCaptureLead = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Raised for every judgement worth showing in the diagnostics.
        /// </summary>
        public event EventHandler<VoiceDecision> DecisionMade;

        /// <summary>
        /// Raised when a command has been authorized. The only way an action can follow.
        /// </summary>
        public event EventHandler<VoiceCommandEventArgs> CommandRecognized;

        public event EventHandler<VoicePhaseEventArgs> PhaseChanged;

        /// <summary>
        /// Recognition ended on its own (device lost, stream finished). The argument is the
        /// error, or null for a clean end of input.
        /// </summary>
        public event EventHandler<Exception> Stopped;

        /// <summary>
        /// Chime and dismiss sounds. The self-test swaps in a player that feeds the chime back
        /// into the simulated microphone instead of the speakers.
        /// </summary>
        public IVoiceFeedback Feedback { get; set; }

        /// <summary>
        /// Test hook: configures the recognizer's input instead of the default microphone.
        /// </summary>
        public Action<SpeechRecognitionEngine> InputConfigurator { get; set; }

        public double WakeThreshold
        {
            get { lock (sync) { return wakeThreshold; } }
            set { lock (sync) { wakeThreshold = Clamp(value, 0.05, 0.99); } }
        }

        public double CommandThreshold
        {
            get { lock (sync) { return commandThreshold; } }
            set { lock (sync) { commandThreshold = Clamp(value, 0.05, 0.99); } }
        }

        public TimeSpan CommandWindow
        {
            get { lock (sync) { return commandWindow; } }
            set { lock (sync) { commandWindow = value < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : value; } }
        }

        public bool DismissSoundEnabled
        {
            get { lock (sync) { return dismissSoundEnabled; } }
            set { lock (sync) { dismissSoundEnabled = value; } }
        }

        /// <summary>
        /// The wake word for the next Start (the caller has validated it). A running recognizer
        /// keeps the one it started with until it is restarted.
        /// </summary>
        public string WakeWord
        {
            get { lock (sync) { return wakeWord; } }
            set { lock (sync) { wakeWord = string.IsNullOrWhiteSpace(value) ? VoiceWakeWord.Default : value; } }
        }

        /// <summary>
        /// The wake word the running recognizer listens for.
        /// </summary>
        public string ActiveWakeWord
        {
            get { lock (sync) { return activeWakeWord; } }
        }

        /// <summary>
        /// Custom command phrases for the next Start. Like the wake word, a change takes a
        /// restart, which drops any open session.
        /// </summary>
        public CustomPhraseSet CustomPhrases
        {
            get { lock (sync) { return customPhrases; } }
            set { lock (sync) { customPhrases = value ?? CustomPhraseSet.Empty; } }
        }

        /// <summary>
        /// The custom phrases the running recognizer's grammar holds.
        /// </summary>
        public CustomPhraseSet ActiveCustomPhrases
        {
            get { lock (sync) { return activeCustomPhrases; } }
        }

        public VoicePhase Phase
        {
            get { lock (sync) { return phase; } }
        }

        public VoiceSession CurrentSession
        {
            get { lock (sync) { return session; } }
        }

        public bool IsRunning
        {
            get { lock (sync) { return phase != VoicePhase.Off; } }
        }

        public bool IsHearingSound
        {
            get { lock (sync) { return isHearingSound; } }
        }

        /// <summary>
        /// Input level 0-100: measured from the samples when a selected device is captured,
        /// otherwise as reported by the recognizer.
        /// </summary>
        public int AudioLevel
        {
            get
            {
                MicrophoneCaptureStream stream;
                lock (sync)
                {
                    stream = capture;
                }

                return stream != null ? stream.Level : Volatile.Read(ref audioLevel);
            }
        }

        /// <summary>
        /// Endpoint ID of the microphone to use from the next Start; null or empty = the
        /// Windows default input. Changing it does not touch a running recognizer: the owner
        /// restarts it (Stop + Start), which also drops any open session.
        /// </summary>
        public string InputDeviceId
        {
            get { lock (sync) { return inputDeviceId; } }
            set { lock (sync) { inputDeviceId = string.IsNullOrEmpty(value) ? null : value; } }
        }

        /// <summary>
        /// Display name of <see cref="InputDeviceId"/>, for messages and the log.
        /// </summary>
        public string InputDeviceName
        {
            get { lock (sync) { return inputDeviceName; } }
            set { lock (sync) { inputDeviceName = value; } }
        }

        /// <summary>
        /// The endpoint the running recognizer is bound to; null for the Windows default (or
        /// when stopped).
        /// </summary>
        public string ActiveInputId
        {
            get { lock (sync) { return activeInputId; } }
        }

        public string ActiveInputName
        {
            get { lock (sync) { return activeInputName; } }
        }

        public string BackendName
        {
            get { lock (sync) { return backendName; } }
        }

        public VoiceCounters Counters
        {
            get { lock (sync) { return counters.Copy(); } }
        }

        private static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value))
            {
                return max;
            }

            return value < min ? min : (value > max ? max : value);
        }

        // ---- Availability ------------------------------------------------------------------------

        /// <summary>
        /// Names the recognizer that would be used, or explains why none can be, without
        /// starting anything.
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

            // The wake word's pronunciation and the command phrases are English.
            CultureInfo ui = CultureInfo.CurrentUICulture;
            for (int i = 0; i < installed.Count; i++)
            {
                if (installed[i].Culture.Equals(ui) && installed[i].Culture.TwoLetterISOLanguageName == "en")
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

            return null;
        }

        // ---- Start / stop ------------------------------------------------------------------------

        public bool Start(out string error)
        {
            error = null;
            Stop();

            RecognizerInfo info;
            try
            {
                info = PickRecognizer();
            }
            catch (Exception ex)
            {
                error = "Speech recognition is unavailable: " + ex.Message;
                return false;
            }

            if (info == null)
            {
                error = "No English speech recognizer is installed (Windows Speech Recognition, en-US).";
                return false;
            }

            SpeechRecognitionEngine created = null;
            Grammar wakeGrammarBuilt = null;
            Grammar commands = null;
            Exception failure = null;
            MicrophoneCaptureStream stream = null;
            string deviceId;
            string deviceName;
            string wake;
            CustomPhraseSet custom;
            lock (sync)
            {
                deviceId = inputDeviceId;
                deviceName = inputDeviceName;
                wake = wakeWord;
                custom = customPhrases;
            }

            // Built on a thread with no synchronization context, so events are raised on the
            // thread pool whatever thread calls Start.
            Thread builder = new Thread(() =>
            {
                try
                {
                    created = new SpeechRecognitionEngine(info);

                    // Acoustic adaptation learns from what gets recognized. Most of what this
                    // engine hears is ordinary speech scored against a one-word grammar; it must
                    // not adapt towards that.
                    TryUpdateSetting(created, "AdaptationOn", 0);

                    wakeGrammarBuilt = VoiceGrammars.BuildWake(info.Culture, wake);
                    commands = VoiceGrammars.BuildCommands(info.Culture, custom);
                    created.LoadGrammar(wakeGrammarBuilt);
                    created.LoadGrammar(commands);
                    wakeGrammarBuilt.Enabled = true;
                    commands.Enabled = false;

                    created.SpeechDetected += OnSpeechDetected;
                    created.SpeechRecognized += OnSpeechRecognized;
                    created.SpeechRecognitionRejected += OnSpeechRejected;
                    created.AudioStateChanged += OnAudioStateChanged;
                    created.AudioLevelUpdated += OnAudioLevelUpdated;
                    created.RecognizeCompleted += OnRecognizeCompleted;

                    Action<SpeechRecognitionEngine> configure = InputConfigurator;
                    if (configure != null)
                    {
                        configure(created);
                        deviceId = null;
                    }
                    else if (deviceId != null)
                    {
                        // A specific microphone: System.Speech cannot open it by itself, so it
                        // is captured here and handed over as a live stream.
                        string openError;
                        stream = MicrophoneCaptureStream.Open(deviceId, deviceName, out openError);
                        if (stream == null)
                        {
                            throw new InvalidOperationException("“" + (deviceName ?? "the selected microphone") + "”: " + openError);
                        }

                        created.SetInputToAudioStream(stream, MicrophoneCaptureStream.Format);
                    }
                    else
                    {
                        // Throws when there is no capture device at all.
                        created.SetInputToDefaultAudioDevice();
                    }

                    lock (sync)
                    {
                        recognizer = created;
                        capture = stream;
                        activeInputId = deviceId;
                        activeInputName = deviceId != null ? deviceName : null;
                        wakeGrammar = wakeGrammarBuilt;
                        commandGrammar = commands;
                        activeWakeWord = wake;
                        activeCustomPhrases = custom;
                        wakeMaxDuration = VoiceWakeWord.MaxDuration(wake);
                        phase = VoicePhase.WakeOnly;
                        session = null;
                        onsets.Clear();
                        lastPhraseEnd = TimeSpan.Zero;
                        utteranceOpen = false;
                        isHearingSound = false;
                        counters = new VoiceCounters();
                        backendName = info.Description;
                    }

                    created.RecognizeAsync(RecognizeMode.Multiple);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            builder.IsBackground = true;
            builder.Name = "KINECT-OS voice start";
            builder.Start();
            builder.Join();

            if (failure != null)
            {
                lock (sync)
                {
                    recognizer = null;
                    capture = null;
                    activeInputId = null;
                    activeInputName = null;
                    phase = VoicePhase.Off;
                }

                CloseCapture(stream);
                DisposeRecognizer(created);
                error = failure.Message;
                RuntimeLog.Write("Voice start failed: " + (failure is InvalidOperationException ? failure.Message : failure.ToString()));
                return false;
            }

            RuntimeLog.Write("Voice started: " + info.Description + ", input "
                + (deviceId != null ? "“" + deviceName + "” (selected, " + AudioInputDevices.ShortId(deviceId) + ")" : "Windows default")
                + ", wake word “" + wake + "”, " + custom.Count + " custom command(s), wake >= "
                + WakeThreshold.ToString("0.00", CultureInfo.InvariantCulture) + ", command >= "
                + CommandThreshold.ToString("0.00", CultureInfo.InvariantCulture) + ", window "
                + CommandWindow.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            Raise(PhaseChanged, new VoicePhaseEventArgs(VoicePhase.WakeOnly, null, VoiceOutcome.None, null));
            return true;
        }

        public void Stop()
        {
            SpeechRecognitionEngine stopping;
            MicrophoneCaptureStream stoppingCapture;
            bool wasRunning;
            lock (sync)
            {
                stopping = recognizer;
                stoppingCapture = capture;
                wasRunning = phase != VoicePhase.Off;
                recognizer = null;
                capture = null;
                activeInputId = null;
                activeInputName = null;
                wakeGrammar = null;
                commandGrammar = null;
                phase = VoicePhase.Off;
                session = null;
                utteranceOpen = false;
                isHearingSound = false;
                if (deadlineTimer != null)
                {
                    deadlineTimer.Change(Timeout.Infinite, Timeout.Infinite);
                }
            }

            // The stream first: a recognizer blocked in Read gets end-of-stream at once, so it
            // can be cancelled and disposed without waiting, and the device is released before
            // any other microphone is opened.
            CloseCapture(stoppingCapture);
            DisposeRecognizer(stopping);

            if (wasRunning)
            {
                RuntimeLog.Write("Voice stopped");
                Raise(PhaseChanged, new VoicePhaseEventArgs(VoicePhase.Off, null, VoiceOutcome.None, null));
            }
        }

        private void DisposeRecognizer(SpeechRecognitionEngine engine)
        {
            if (engine == null)
            {
                return;
            }

            try
            {
                engine.SpeechDetected -= OnSpeechDetected;
                engine.SpeechRecognized -= OnSpeechRecognized;
                engine.SpeechRecognitionRejected -= OnSpeechRejected;
                engine.AudioStateChanged -= OnAudioStateChanged;
                engine.AudioLevelUpdated -= OnAudioLevelUpdated;
                engine.RecognizeCompleted -= OnRecognizeCompleted;
                engine.RecognizeAsyncCancel();
                engine.Dispose();
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("Voice recognizer dispose: " + ex.Message);
            }
        }

        private static void CloseCapture(MicrophoneCaptureStream stream)
        {
            if (stream == null)
            {
                return;
            }

            try
            {
                stream.Close();
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("Microphone close: " + ex.Message);
            }
        }

        private static void TryUpdateSetting(SpeechRecognitionEngine engine, string name, int value)
        {
            try
            {
                engine.UpdateRecognizerSetting(name, value);
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("Recognizer setting " + name + " not applied: " + ex.Message);
            }
        }

        public void Dispose()
        {
            Stop();
            lock (sync)
            {
                if (deadlineTimer != null)
                {
                    deadlineTimer.Dispose();
                    deadlineTimer = null;
                }
            }
        }

        // ---- Recognizer events -------------------------------------------------------------------

        private void OnSpeechDetected(object sender, SpeechDetectedEventArgs e)
        {
            lock (sync)
            {
                if (phase == VoicePhase.Off || !ReferenceEquals(sender, recognizer))
                {
                    return;
                }

                onsets.Add(e.AudioPosition);
                if (onsets.Count > OnsetMemory)
                {
                    onsets.RemoveAt(0);
                }

                utteranceOpen = true;
                utteranceOnset = e.AudioPosition;
            }
        }

        private void OnAudioStateChanged(object sender, AudioStateChangedEventArgs e)
        {
            lock (sync)
            {
                if (!ReferenceEquals(sender, recognizer))
                {
                    return;
                }

                isHearingSound = e.AudioState == AudioState.Speech;
            }
        }

        private void OnAudioLevelUpdated(object sender, AudioLevelUpdatedEventArgs e)
        {
            Volatile.Write(ref audioLevel, e.AudioLevel);
        }

        private void OnRecognizeCompleted(object sender, RecognizeCompletedEventArgs e)
        {
            bool wasOurs;
            MicrophoneCaptureStream ended = null;
            lock (sync)
            {
                wasOurs = ReferenceEquals(sender, recognizer);
                if (wasOurs)
                {
                    ended = capture;
                    phase = VoicePhase.Off;
                    session = null;
                    recognizer = null;
                    capture = null;
                    activeInputId = null;
                    activeInputName = null;
                    wakeGrammar = null;
                    commandGrammar = null;
                    utteranceOpen = false;
                    isHearingSound = false;
                    if (deadlineTimer != null)
                    {
                        deadlineTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    }
                }
            }

            if (!wasOurs)
            {
                return;
            }

            // A selected microphone that went away ends its stream; say so rather than
            // reporting a clean end of input.
            Exception error = e.Error;
            if (error == null && ended != null && ended.FailureReason != null)
            {
                error = new IOException("“" + (ended.DeviceName ?? "Microphone") + "”: " + ended.FailureReason);
            }

            if (error != null)
            {
                RuntimeLog.Write("Voice recognition stopped with error: " + error.Message);
            }
            else
            {
                RuntimeLog.Write("Voice recognition stopped: the audio input ended");
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                CloseCapture(ended);
                DisposeRecognizer(sender as SpeechRecognitionEngine);
            });
            Raise(PhaseChanged, new VoicePhaseEventArgs(VoicePhase.Off, null, VoiceOutcome.None, error != null ? error.Message : null));
            Raise(Stopped, error);
        }

        private void OnSpeechRecognized(object sender, SpeechRecognizedEventArgs e)
        {
            Evaluate(sender, e.Result, true);
        }

        private void OnSpeechRejected(object sender, SpeechRecognitionRejectedEventArgs e)
        {
            Evaluate(sender, e.Result, false);
        }

        // ---- The state machine -------------------------------------------------------------------

        /// <summary>
        /// Everything to do after the state lock is released.
        /// </summary>
        private sealed class Pending
        {
            public readonly List<VoiceDecision> Decisions = new List<VoiceDecision>();
            public VoicePhaseEventArgs PhaseChange;
            public VoiceCommandEventArgs Command;
            public int ChimeSession;
            public bool Dismiss;
            public bool Grammars;
        }

        /// <summary>
        /// What one result says, in audio-stream time.
        /// </summary>
        private struct Evidence
        {
            public bool EngineAccepted;
            public bool IsWake;
            public bool IsCommand;
            public string Text;
            public double Confidence;
            public int WordCount;
            public bool HasAudio;

            /// <summary>
            /// Start and end of the recognized words.
            /// </summary>
            public TimeSpan Start;
            public TimeSpan End;

            /// <summary>
            /// Where the speech of this utterance began (SpeechDetected).
            /// </summary>
            public TimeSpan Onset;

            public TimeSpan Duration
            {
                get { return End - Start; }
            }

            /// <summary>
            /// How far into the speech the recognized words begin. Near zero for a phrase said
            /// on its own; large when it was spotted inside a longer sentence.
            /// </summary>
            public TimeSpan Lead
            {
                get { return Start - Onset; }
            }

            public TimeSpan Span
            {
                get { return End - Onset; }
            }

            /// <summary>
            /// Silence before this utterance: from the end of the previous phrase to this one's
            /// speech onset. Null when nothing has been heard before it.
            /// </summary>
            public TimeSpan? GapBefore;
        }

        private Evidence Collect(RecognitionResult result, bool engineAccepted)
        {
            Evidence ev = new Evidence();
            ev.EngineAccepted = engineAccepted;
            ev.Text = "";
            if (result == null)
            {
                return ev;
            }

            try
            {
                ev.Text = result.Text ?? "";
                ev.Confidence = result.Confidence;
                ev.WordCount = result.Words != null ? result.Words.Count : 0;
                Grammar grammar = result.Grammar;
                ev.IsWake = grammar != null && ReferenceEquals(grammar, wakeGrammar);
                ev.IsCommand = grammar != null && ReferenceEquals(grammar, commandGrammar);

                RecognizedAudio audio = result.Audio;
                if (audio != null)
                {
                    ev.HasAudio = true;
                    ev.Start = audio.AudioPosition;
                    ev.End = audio.AudioPosition + audio.Duration;

                    // The onset of this utterance: the latest speech start at or before the
                    // words, ignoring any that belong to an earlier phrase.
                    TimeSpan onset = ev.Start;
                    bool found = false;
                    for (int i = 0; i < onsets.Count; i++)
                    {
                        TimeSpan candidate = onsets[i];
                        if (candidate <= ev.Start + Tolerance && candidate >= lastPhraseEnd - Tolerance
                            && (!found || candidate > onset))
                        {
                            onset = candidate;
                            found = true;
                        }
                    }

                    ev.Onset = onset > ev.Start ? ev.Start : onset;
                    if (lastPhraseEnd > TimeSpan.Zero)
                    {
                        ev.GapBefore = ev.Onset - lastPhraseEnd;
                    }
                }
            }
            catch (Exception ex)
            {
                // A result we cannot read is a result we do not act on.
                RuntimeLog.Write("Voice result unreadable: " + ex.Message);
                ev.HasAudio = false;
                ev.IsWake = false;
                ev.IsCommand = false;
            }

            return ev;
        }

        private void Evaluate(object sender, RecognitionResult result, bool engineAccepted)
        {
            Pending pending = new Pending();
            lock (sync)
            {
                if (phase == VoicePhase.Off || !ReferenceEquals(sender, recognizer))
                {
                    return;
                }

                utteranceOpen = false;
                Evidence ev = Collect(result, engineAccepted);
                if (ev.HasAudio && ev.End > lastPhraseEnd)
                {
                    lastPhraseEnd = ev.End;
                }

                if (phase == VoicePhase.WakeOnly)
                {
                    EvaluateWake(ev, pending);
                }
                else
                {
                    EvaluateCommand(ev, pending);
                }
            }

            Run(pending);
        }

        /// <summary>
        /// WakeOnly. The wake grammar is the only grammar enabled, so every utterance comes back
        /// as a scored wake-word hypothesis (or nothing). The only possible outcome of accepting
        /// one is a new session in Acknowledging - never an action.
        /// </summary>
        private void EvaluateWake(Evidence ev, Pending pending)
        {
            if (!ev.IsWake)
            {
                counters.BackgroundUtterances++;
                return;
            }

            string reason = null;
            if (!ev.HasAudio)
            {
                reason = "no audio timing";
            }
            else if (ev.Confidence < wakeThreshold)
            {
                reason = "confidence " + F2(ev.Confidence) + " < " + F2(wakeThreshold);
            }
            else if (ev.Lead > WakeMaxLead)
            {
                reason = "inside a longer phrase (began " + F2(ev.Lead.TotalSeconds) + " s into the speech)";
            }
            else if (ev.GapBefore.HasValue && ev.GapBefore.Value < WakeMinGapBefore)
            {
                reason = "no pause before it (" + F2(ev.GapBefore.Value.TotalSeconds) + " s after other speech)";
            }
            else if (ev.Duration < WakeMinDuration || ev.Duration > wakeMaxDuration)
            {
                reason = F2(ev.Duration.TotalSeconds) + " s is not the wake word said once";
            }

            if (reason != null)
            {
                if (ev.Confidence >= NearMissConfidence)
                {
                    counters.WakeCandidatesRejected++;
                    pending.Decisions.Add(Decision("wake", VoiceVerdict.Rejected, ev, null, reason, 0));
                    if (ev.EngineAccepted)
                    {
                        RuntimeLog.Write("Voice wake candidate rejected: " + reason + " (confidence " + F2(ev.Confidence)
                            + ", lead " + F2(ev.Lead.TotalSeconds) + " s, " + F2(ev.Duration.TotalSeconds) + " s, " + GapText(ev) + ")");
                    }
                }
                else
                {
                    counters.BackgroundUtterances++;
                }

                return;
            }

            session = new VoiceSession(++sessionCounter, ev.Confidence, ev.End);
            phase = VoicePhase.Acknowledging;
            counters.Wakes++;
            pending.Decisions.Add(Decision("wake", VoiceVerdict.Accepted, ev, null, "wake word", session.Id));
            pending.PhaseChange = new VoicePhaseEventArgs(VoicePhase.Acknowledging, session, VoiceOutcome.None, null);
            pending.Grammars = true;
            pending.ChimeSession = session.Id;
            RuntimeLog.Write("Voice wake #" + session.Id + " accepted (confidence " + F2(ev.Confidence) + ", lead "
                + F2(ev.Lead.TotalSeconds) + " s, " + F2(ev.Duration.TotalSeconds) + " s, " + GapText(ev) + ")");
        }

        /// <summary>
        /// The pause before the utterance as measured: from the end of the previous RECOGNIZED
        /// phrase. Speech the recognizer produced no phrase for is invisible to it, so this can
        /// overstate the real silence; it is logged so hardware sessions can be tuned from data.
        /// </summary>
        private static string GapText(Evidence ev)
        {
            return ev.GapBefore.HasValue ? "gap " + F2(ev.GapBefore.Value.TotalSeconds) + " s" : "gap -";
        }

        /// <summary>
        /// Acknowledging / Listening. Only a command-grammar phrase that began after the gate,
        /// on its own, confidently, and that the parser understands, becomes a command.
        /// </summary>
        private void EvaluateCommand(Evidence ev, Pending pending)
        {
            VoiceSession current = session;
            if (current == null)
            {
                return;
            }

            if (!ev.HasAudio)
            {
                counters.EchoOrNoiseIgnored++;
                pending.Decisions.Add(Decision("command", VoiceVerdict.Ignored, ev, null, "no audio timing", current.Id));
                return;
            }

            if (phase == VoicePhase.Acknowledging)
            {
                // The gate is not open yet, so this began before the chime finished.
                if (ev.Span > EchoMaxPreGate + EchoTail && ev.WordCount > 0)
                {
                    EndSession(VoiceOutcome.SpokeTooEarly, "speech ran on from the wake word; wait for the chime", pending);
                    pending.Decisions.Add(Decision("command", VoiceVerdict.Rejected, ev, null, "began before the chime", current.Id));
                }
                else
                {
                    counters.EchoOrNoiseIgnored++;
                    pending.Decisions.Add(Decision("command", VoiceVerdict.Ignored, ev, null, "during the acknowledgement", current.Id));
                }

                return;
            }

            TimeSpan gate = current.GatePosition;
            TimeSpan onset = ev.Onset;

            if (onset < gate - Tolerance)
            {
                if (ev.End <= gate + EchoTail)
                {
                    // Wholly inside the chime and its tail.
                    counters.EchoOrNoiseIgnored++;
                    pending.Decisions.Add(Decision("command", VoiceVerdict.Ignored, ev, null, "chime echo", current.Id));
                    return;
                }

                if (gate - onset <= EchoMaxPreGate && ev.Start >= gate - Tolerance)
                {
                    // Echo merged into the start of a real command: judge the words from the gate.
                    onset = gate;
                }
                else
                {
                    pending.Decisions.Add(Decision("command", VoiceVerdict.Rejected, ev, null, "began before the chime", current.Id));
                    EndSession(VoiceOutcome.SpokeTooEarly, "speech began before the chime finished", pending);
                    return;
                }
            }

            TimeSpan span = ev.End - onset;
            TimeSpan lead = ev.Start - onset;

            if (!ev.IsCommand)
            {
                if (span < NoiseMaxSpan)
                {
                    counters.EchoOrNoiseIgnored++;
                    pending.Decisions.Add(Decision("command", VoiceVerdict.Ignored, ev, null, "short noise", current.Id));
                    return;
                }

                pending.Decisions.Add(Decision("command", VoiceVerdict.Rejected, ev, null, "not a command", current.Id));
                EndSession(VoiceOutcome.NotRecognized, "not a command", pending);
                return;
            }

            string reason = null;
            if (ev.Confidence < commandThreshold)
            {
                reason = "confidence " + F2(ev.Confidence) + " < " + F2(commandThreshold);
            }
            else if (lead > CommandMaxLead)
            {
                reason = "inside a longer phrase (began " + F2(lead.TotalSeconds) + " s into the speech)";
            }
            else if (ev.Duration > CommandMaxDuration
                || ev.Duration.TotalSeconds > CommandSecondsAllowance + CommandSecondsPerWord * Math.Max(1, ev.WordCount))
            {
                reason = F2(ev.Duration.TotalSeconds) + " s is too long for “" + ev.Text + "” (stretched over other speech)";
            }

            VoiceIntent intent = null;
            if (reason == null)
            {
                string parseReason;
                if (!VoiceCommandParser.TryParse(ev.Text, activeCustomPhrases, out intent, out parseReason))
                {
                    reason = parseReason;
                    intent = null;
                }
            }

            if (reason != null)
            {
                pending.Decisions.Add(Decision("command", VoiceVerdict.Rejected, ev, null, reason, current.Id));
                EndSession(VoiceOutcome.NotRecognized, reason, pending);
                return;
            }

            if (intent.Kind == VoiceIntentKind.Cancel)
            {
                pending.Decisions.Add(Decision("command", VoiceVerdict.Accepted, ev, "cancel", null, current.Id));
                EndSession(VoiceOutcome.Cancelled, "cancelled", pending);
                return;
            }

            if (!current.TryAuthorize())
            {
                return;
            }

            pending.Decisions.Add(Decision("command", VoiceVerdict.Accepted, ev, intent.Feedback, null, current.Id));
            pending.Command = new VoiceCommandEventArgs(current, intent, ev.Text, ev.Confidence);
            EndSession(VoiceOutcome.Executed, intent.Feedback, pending);
        }

        /// <summary>
        /// Closes the session - for any reason - and returns to WakeOnly.
        /// </summary>
        private void EndSession(VoiceOutcome outcome, string detail, Pending pending)
        {
            VoiceSession closing = session;
            session = null;
            phase = VoicePhase.WakeOnly;
            utteranceOpen = false;
            if (deadlineTimer != null)
            {
                deadlineTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }

            if (closing != null)
            {
                closing.Outcome = outcome;
            }

            switch (outcome)
            {
                case VoiceOutcome.Executed: counters.Executed++; break;
                case VoiceOutcome.NotRecognized: counters.NotRecognized++; break;
                case VoiceOutcome.TimedOut: counters.TimedOut++; break;
                case VoiceOutcome.Cancelled: counters.Cancelled++; break;
                case VoiceOutcome.SpokeTooEarly: counters.SpokeTooEarly++; break;
            }

            pending.PhaseChange = new VoicePhaseEventArgs(VoicePhase.WakeOnly, closing, outcome, detail);
            pending.Grammars = true;
            pending.Dismiss = dismissSoundEnabled && outcome != VoiceOutcome.Executed && outcome != VoiceOutcome.None;
            RuntimeLog.Write("Voice session #" + (closing != null ? closing.Id : 0) + " " + outcome + ": " + detail);
        }

        private bool IsCurrent(int sessionId, VoicePhase expected)
        {
            return session != null && session.Id == sessionId && phase == expected;
        }

        // ---- Chime, gate and deadline ------------------------------------------------------------

        private void StartChime(int sessionId)
        {
            IVoiceFeedback feedback = Feedback;
            Thread player = new Thread(() =>
            {
                Stopwatch clock = Stopwatch.StartNew();
                TimeSpan minimum = TimeSpan.FromMilliseconds(150);
                MarkChimeStart(sessionId);
                if (feedback != null)
                {
                    minimum = feedback.WakeChimeDuration;
                    feedback.PlayWakeChime();
                }

                TimeSpan remaining = minimum - clock.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    Thread.Sleep(remaining);
                }

                Thread.Sleep(GateGuard);
                OpenGate(sessionId);
            });

            player.IsBackground = true;
            player.Name = "KINECT-OS wake chime";
            player.Start();
        }

        private void MarkChimeStart(int sessionId)
        {
            SpeechRecognitionEngine engine;
            lock (sync)
            {
                if (!IsCurrent(sessionId, VoicePhase.Acknowledging))
                {
                    return;
                }

                engine = recognizer;
            }

            try
            {
                TimeSpan position = CurrentAudioPosition(engine);
                lock (sync)
                {
                    if (IsCurrent(sessionId, VoicePhase.Acknowledging))
                    {
                        session.ChimePosition = position;
                    }
                }
            }
            catch (Exception)
            {
                // Diagnostics only.
            }
        }

        /// <summary>
        /// The chime has finished: record the audio position from which a command may start
        /// and open the window.
        /// </summary>
        private void OpenGate(int sessionId)
        {
            SpeechRecognitionEngine engine;
            lock (sync)
            {
                if (!IsCurrent(sessionId, VoicePhase.Acknowledging))
                {
                    return;
                }

                engine = recognizer;
            }

            TimeSpan position = TimeSpan.Zero;
            bool havePosition = false;
            try
            {
                position = CurrentAudioPosition(engine);
                havePosition = true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("Voice gate position unavailable: " + ex.Message);
            }

            Pending pending = new Pending();
            lock (sync)
            {
                if (!IsCurrent(sessionId, VoicePhase.Acknowledging))
                {
                    return;
                }

                if (!havePosition)
                {
                    EndSession(VoiceOutcome.Cancelled, "audio position unavailable", pending);
                }
                else
                {
                    session.GatePosition = position;
                    session.GateOpenUtc = DateTime.UtcNow;
                    session.DeadlineUtc = session.GateOpenUtc + commandWindow;
                    session.IsGateOpen = true;
                    phase = VoicePhase.Listening;
                    ScheduleDeadline(session.Id, commandWindow);
                    pending.PhaseChange = new VoicePhaseEventArgs(VoicePhase.Listening, session, VoiceOutcome.None, null);
                }
            }

            Run(pending);
        }

        /// <summary>
        /// "Now" in audio-stream time. The recognizer's position is what it has read, which can
        /// trail the microphone by its read size; with a captured device, the capture clock is
        /// closer to real time, so the later of the two is used (a later gate is only stricter).
        /// </summary>
        private TimeSpan CurrentAudioPosition(SpeechRecognitionEngine engine)
        {
            TimeSpan position = engine.AudioPosition;
            MicrophoneCaptureStream stream;
            lock (sync)
            {
                stream = capture;
            }

            if (stream != null)
            {
                TimeSpan captured = stream.CapturedPosition;
                if (captured > position && captured - position <= MaxCaptureLead)
                {
                    position = captured;
                }
            }

            return position;
        }

        private void ScheduleDeadline(int sessionId, TimeSpan due)
        {
            if (deadlineTimer == null)
            {
                deadlineTimer = new Timer(OnDeadline);
            }

            deadlineSessionId = sessionId;
            deadlineTimer.Change(due, Timeout.InfiniteTimeSpan);
        }

        private void OnDeadline(object state)
        {
            Pending pending = new Pending();
            lock (sync)
            {
                if (session == null || phase != VoicePhase.Listening || session.Id != deadlineSessionId)
                {
                    return;
                }

                TimeSpan early = session.DeadlineUtc - DateTime.UtcNow;
                if (early > TimeSpan.FromMilliseconds(20))
                {
                    deadlineTimer.Change(early, Timeout.InfiniteTimeSpan);
                    return;
                }

                if (utteranceOpen && utteranceOnset >= session.GatePosition - Tolerance && !session.Extended)
                {
                    // A command is being spoken right now; let it finish once.
                    session.Extended = true;
                    session.DeadlineUtc = DateTime.UtcNow + WindowExtension;
                    deadlineTimer.Change(WindowExtension, Timeout.InfiniteTimeSpan);
                    pending.PhaseChange = new VoicePhaseEventArgs(VoicePhase.Listening, session, VoiceOutcome.None, "extended");
                }
                else
                {
                    pending.Decisions.Add(new VoiceDecision("session", VoiceVerdict.Rejected, null, double.NaN, double.NaN,
                        double.NaN, double.NaN, null, "no command within " + commandWindow.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s",
                        session.Id));
                    EndSession(VoiceOutcome.TimedOut, "no command", pending);
                }
            }

            Run(pending);
        }

        // ---- Plumbing ----------------------------------------------------------------------------

        private void Run(Pending pending)
        {
            for (int i = 0; i < pending.Decisions.Count; i++)
            {
                Raise(DecisionMade, pending.Decisions[i]);
            }

            if (pending.Command != null)
            {
                Raise(CommandRecognized, pending.Command);
            }

            if (pending.PhaseChange != null)
            {
                Raise(PhaseChanged, pending.PhaseChange);
            }

            if (pending.Grammars)
            {
                ThreadPool.QueueUserWorkItem(_ => ApplyGrammars());
            }

            if (pending.ChimeSession != 0)
            {
                StartChime(pending.ChimeSession);
            }

            if (pending.Dismiss)
            {
                IVoiceFeedback feedback = Feedback;
                if (feedback != null)
                {
                    ThreadPool.QueueUserWorkItem(_ => feedback.PlayDismiss());
                }
            }
        }

        /// <summary>
        /// Brings the grammars in line with the current phase. The one being switched off is
        /// always disabled before the other is enabled, so both are never live at once.
        /// </summary>
        private void ApplyGrammars()
        {
            lock (grammarSync)
            {
                Grammar wake;
                Grammar commands;
                bool wantCommands;
                bool wantWake;
                lock (sync)
                {
                    if (recognizer == null)
                    {
                        return;
                    }

                    wake = wakeGrammar;
                    commands = commandGrammar;
                    wantWake = phase == VoicePhase.WakeOnly;
                    wantCommands = phase == VoicePhase.Acknowledging || phase == VoicePhase.Listening;
                }

                try
                {
                    if (wantCommands)
                    {
                        if (wake.Enabled)
                        {
                            wake.Enabled = false;
                        }

                        if (!commands.Enabled)
                        {
                            commands.Enabled = true;
                        }
                    }
                    else
                    {
                        if (commands.Enabled)
                        {
                            commands.Enabled = false;
                        }

                        if (wake.Enabled != wantWake)
                        {
                            wake.Enabled = wantWake;
                        }
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Write("Voice grammar switch failed: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Called under the state lock (reads the active wake word).
        /// </summary>
        private VoiceDecision Decision(string stage, VoiceVerdict verdict, Evidence ev, string intent, string reason, int sessionId)
        {
            string phrase = ev.IsWake ? activeWakeWord : (ev.IsCommand ? ev.Text : "");
            return new VoiceDecision(stage, verdict, phrase, ev.IsWake || ev.IsCommand ? ev.Confidence : double.NaN,
                ev.HasAudio ? ev.Lead.TotalSeconds : double.NaN, ev.HasAudio ? ev.Duration.TotalSeconds : double.NaN,
                ev.GapBefore.HasValue ? ev.GapBefore.Value.TotalSeconds : double.NaN, intent, reason, sessionId);
        }

        private void Raise<T>(EventHandler<T> handler, T args)
        {
            if (handler == null)
            {
                return;
            }

            try
            {
                handler.Invoke(this, args);
            }
            catch (Exception ex)
            {
                // A subscriber failing must not take the recognizer thread down with it.
                RuntimeLog.Write("Voice event handler failed: " + ex);
            }
        }

        private static string F2(double value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
