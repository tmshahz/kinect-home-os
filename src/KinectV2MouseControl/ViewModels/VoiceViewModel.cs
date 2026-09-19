using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Windows.Threading;

namespace KinectV2MouseControl
{
    public class VoiceCommandGroup
    {
        public string Title { get; set; }
        public string Detail { get; set; }
        public List<VoiceCommand> Commands { get; set; }
    }

    /// <summary>
    /// What the voice HUD is showing. Drives colour and motion in the Voice page, the header
    /// pill and the compact widget.
    /// </summary>
    public enum VoiceHudState
    {
        Hidden,
        Wake,
        Listening,
        Recording,
        Transcribing,
        Thinking,
        Executed,
        Rejected
    }

    /// <summary>
    /// The microphone card's status line.
    /// </summary>
    public enum MicrophoneStatus
    {
        Off,
        Ready,
        Reinitializing,
        Unavailable
    }

    /// <summary>
    /// One entry of the microphone dropdown or of the available-inputs list.
    /// </summary>
    public sealed class MicrophoneOption
    {
        public MicrophoneOption(string id, string name, string detail, bool isAvailable, bool isSystemDefault, bool isWindowsDefault, bool isInUse)
        {
            Id = id ?? "";
            Name = name;
            Detail = detail ?? "";
            IsAvailable = isAvailable;
            IsSystemDefault = isSystemDefault;
            IsWindowsDefault = isWindowsDefault;
            IsInUse = isInUse;
        }

        /// <summary>
        /// Core Audio endpoint ID; empty for "System Default".
        /// </summary>
        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Detail { get; private set; }
        public bool IsAvailable { get; private set; }
        public bool IsSystemDefault { get; private set; }
        public bool IsWindowsDefault { get; private set; }
        public bool IsInUse { get; private set; }

        public string DetailSuffix
        {
            get { return Detail.Length == 0 ? "" : "  ·  " + Detail; }
        }

        public string TagText
        {
            get
            {
                if (IsInUse && IsWindowsDefault)
                {
                    return "in use · Windows default";
                }

                return IsInUse ? "in use" : (IsWindowsDefault ? "Windows default" : "");
            }
        }

        public override string ToString()
        {
            return Name + DetailSuffix;
        }
    }

    /// <summary>
    /// The Voice page and the voice HUD: enable switch, microphone, sensitivity, the live
    /// wake-cycle state, the command list and the developer diagnostics.
    ///
    /// This is the only place a voice command turns into an action, and it only ever does so
    /// for a CommandRecognized event from the wake-gated engine - which the engine raises only
    /// for a phrase heard inside an open command window - after marking that wake session
    /// executed. Everything is marshalled to the UI thread first, so the ActionRouter is
    /// called from the thread that owns the control engine.
    ///
    /// Engine events are stamped with a generation number when they are raised. Every
    /// microphone switch, restart or retry bumps the generation, so anything still queued from
    /// the previous recognizer - including a command recognized a moment before the switch -
    /// is dropped instead of acted on.
    /// </summary>
    public partial class VoiceViewModel : ObservableObject
    {
        private static readonly TimeSpan OutcomeDisplayTime = TimeSpan.FromMilliseconds(2600);
        private const int MaxDecisions = 14;

        private readonly KinectCursorViewModel engine;
        private readonly Dispatcher dispatcher;
        private readonly WakeGatedVoiceEngine voice;
        private readonly VoiceFeedbackSounds sounds;
        private readonly DispatcherTimer uiTimer;
        private readonly DispatcherTimer deviceRefreshTimer;
        private readonly DispatcherTimer retryTimer;
        private readonly DispatcherTimer customCommitTimer;
        private readonly AudioDeviceWatcher watcher;

        private DateTime deadlineUtc;
        private DateTime windowOpenedUtc;
        private DateTime outcomeUntilUtc;
        private int diagnosticsTick;
        private int generation;

        /// <summary>
        /// Commands the shell carries out itself ("open", "compact", "calibrate").
        /// </summary>
        public event EventHandler<string> ShellCommandRequested;

        public VoiceViewModel(KinectCursorViewModel engine, Dispatcher dispatcher)
        {
            this.engine = engine;
            this.dispatcher = dispatcher;

            sounds = new VoiceFeedbackSounds();
            voice = new WakeGatedVoiceEngine();
            voice.Feedback = sounds;
            voice.PhaseChanged += (s, e) => PostCurrent(() => OnPhaseChanged(e));
            voice.CommandRecognized += (s, e) => PostCurrent(() => OnCommandRecognized(e));
            voice.Stopped += (s, e) => PostCurrent(() => OnStopped(e));
            voice.WhisperUnavailable += (s, e) => PostCurrent(() =>
            {
                whisperFailed = true;
                SpeechEngineNotice = "Whisper unavailable: " + e + " Using Windows speech recognition.";
                RequestReinitialize("Whisper fallback");
            });
            voice.DecisionMade += (s, e) => dispatcher.BeginInvoke(new Action(() => OnDecision(e)));

            backendText = WakeGatedVoiceEngine.DescribeAvailability();
            Decisions = new ObservableCollection<VoiceDecision>();
            MicrophoneOptions = new ObservableCollection<MicrophoneOption>();
            AvailableInputs = new ObservableCollection<MicrophoneOption>();
            BuildGroups();

            uiTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
            uiTimer.Interval = TimeSpan.FromMilliseconds(50);
            uiTimer.Tick += UiTimer_Tick;

            // Device notifications arrive in bursts (one per role, per endpoint); settle first.
            deviceRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
            deviceRefreshTimer.Interval = TimeSpan.FromMilliseconds(500);
            deviceRefreshTimer.Tick += (s, e) =>
            {
                deviceRefreshTimer.Stop();
                RefreshMicrophones();
            };

            retryTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
            retryTimer.Tick += (s, e) =>
            {
                retryTimer.Stop();
                RetryStart();
            };

            watcher = new AudioDeviceWatcher();
            watcher.Changed += (s, e) => dispatcher.BeginInvoke(new Action(() =>
            {
                deviceRefreshTimer.Stop();
                deviceRefreshTimer.Start();
            }));

            // Custom-command edits settle before they are saved and before the recognizer is
            // restarted for a changed phrase list.
            customCommitTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
            customCommitTimer.Interval = TimeSpan.FromMilliseconds(600);
            customCommitTimer.Tick += (s, e) =>
            {
                customCommitTimer.Stop();
                CommitCustomCommands();
            };

            ToggleCommand = new RelayCommand(() => IsEnabled = !IsEnabled);
            RefreshMicrophonesCommand = new RelayCommand(() =>
            {
                suspectId = null;
                RefreshMicrophones();
            });
            ClearDiagnosticsCommand = new RelayCommand(() => Decisions.Clear());
            TestWakeCommand = new RelayCommand(() => IsTestMode = !IsTestMode);
            ApplyWakeWordCommand = new RelayCommand(ApplyWakeWord);
            AddCustomCommandCommand = new RelayCommand(AddCustomCommand);

            CustomCommands = new ObservableCollection<CustomCommandRowViewModel>();
            ActionChoices = CustomCommandRules.AssignableActions();
            LoadCustomCommands();

            ApplyIdleText();
            RefreshMicrophones();
        }

        /// <summary>
        /// Runs <paramref name="action"/> on the UI thread only if no restart happened in
        /// between: the generation is read when the engine raises the event.
        /// </summary>
        private void PostCurrent(Action action)
        {
            int stamp = Volatile.Read(ref generation);
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (stamp == Volatile.Read(ref generation))
                {
                    action();
                }
            }));
        }

        private void NewGeneration()
        {
            CancelAssistant();
            Interlocked.Increment(ref generation);
        }

        public RelayCommand ToggleCommand { get; private set; }
        public RelayCommand RefreshMicrophonesCommand { get; private set; }
        public RelayCommand ClearDiagnosticsCommand { get; private set; }
        public RelayCommand TestWakeCommand { get; private set; }
        public RelayCommand ApplyWakeWordCommand { get; private set; }
        public RelayCommand AddCustomCommandCommand { get; private set; }

        public List<VoiceCommandGroup> Groups { get; private set; }

        public int AvailableCount { get; private set; }
        public int PlannedCount { get; private set; }

        // ---- Wake word ----------------------------------------------------------------------------

        private string wakeWord = VoiceWakeWord.Default;

        /// <summary>
        /// The wake word in use (persisted by the shell).
        /// </summary>
        public string WakeWord
        {
            get { return wakeWord; }
        }

        /// <summary>
        /// “Jarvis”, for the many places that quote it.
        /// </summary>
        public string WakeWordQuoted
        {
            get { return "“" + wakeWord + "”"; }
        }

        private string wakeWordDraft = VoiceWakeWord.Default;

        /// <summary>
        /// The wake word text box. Applied with the Apply button or Enter.
        /// </summary>
        public string WakeWordDraft
        {
            get
            {
                return wakeWordDraft;
            }
            set
            {
                if (Set(ref wakeWordDraft, value ?? ""))
                {
                    WakeWordError = "";
                    Raise("IsWakeWordDirty");
                }
            }
        }

        public bool IsWakeWordDirty
        {
            get { return VoicePhrases.Clean(wakeWordDraft) != wakeWord; }
        }

        private string wakeWordError = "";
        public string WakeWordError
        {
            get { return wakeWordError; }
            private set { if (Set(ref wakeWordError, value ?? "")) { Raise("HasWakeWordError"); } }
        }

        public bool HasWakeWordError
        {
            get { return wakeWordError.Length > 0; }
        }

        public string WakeWordAdvice
        {
            get { return VoiceWakeWord.Advice(wakeWord); }
        }

        /// <summary>
        /// Restores the saved wake word (before voice is started). An invalid saved value falls
        /// back to the default rather than stopping voice from starting.
        /// </summary>
        public void SetWakeWord(string saved)
        {
            string cleaned;
            string error;
            if (!VoiceWakeWord.TryValidate(saved, out cleaned, out error))
            {
                if (!string.IsNullOrWhiteSpace(saved))
                {
                    RuntimeLog.Write("Saved wake word '" + saved + "' not usable (" + error + "); using " + VoiceWakeWord.Default);
                }

                cleaned = VoiceWakeWord.Default;
            }

            UseWakeWord(cleaned, false);
        }

        private void ApplyWakeWord()
        {
            string cleaned;
            string error;
            if (!VoiceWakeWord.TryValidate(wakeWordDraft, out cleaned, out error))
            {
                WakeWordError = error;
                return;
            }

            if (cleaned == wakeWord)
            {
                WakeWordDraft = cleaned;
                return;
            }

            RuntimeLog.Write("Wake word changed: “" + wakeWord + "” → “" + cleaned + "”");
            ActivityLog.Post(ActivityKind.Voice, "Wake word changed", "Now “" + cleaned + "”", "control center");
            UseWakeWord(cleaned, true);
        }

        private void UseWakeWord(string cleaned, bool restart)
        {
            wakeWord = cleaned;
            wakeWordDraft = cleaned;
            WakeWordError = "";
            voice.WakeWord = cleaned;
            Raise("WakeWord");
            Raise("WakeWordQuoted");
            Raise("WakeWordDraft");
            Raise("IsWakeWordDirty");
            Raise("WakeWordAdvice");

            // A command that contains the new wake word is no longer allowed.
            ValidateCustomCommands();
            voice.CustomPhrases = BuildCustomPhraseSet();

            if (testMode)
            {
                TestResultText = "Say " + WakeWordQuoted;
            }

            if (restart && isEnabled)
            {
                RequestReinitialize("wake word changed");
            }
            else if (phase == VoicePhase.Off || phase == VoicePhase.WakeOnly)
            {
                ApplyIdleText();
            }
        }

        public string CommandWindowText
        {
            get
            {
                return WakeGatedVoiceEngine.DefaultCommandWindow.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s";
            }
        }

        private void BuildGroups()
        {
            Groups = new List<VoiceCommandGroup>();
            ActionCategory[] order = new ActionCategory[]
            {
                ActionCategory.Media, ActionCategory.System, ActionCategory.Windows, ActionCategory.WindowManagement,
                ActionCategory.Pointer, ActionCategory.Apps, ActionCategory.Intelligence
            };

            for (int i = 0; i < order.Length; i++)
            {
                List<VoiceCommand> commands = new List<VoiceCommand>();
                for (int c = 0; c < VoiceCommandCatalog.BuiltIn.Length; c++)
                {
                    VoiceCommand command = VoiceCommandCatalog.BuiltIn[c];
                    if (command.Category != order[i])
                    {
                        continue;
                    }

                    commands.Add(command);
                    if (command.IsAvailable)
                    {
                        AvailableCount++;
                    }
                    else
                    {
                        PlannedCount++;
                    }
                }

                if (commands.Count > 0)
                {
                    VoiceCommandGroup group = new VoiceCommandGroup();
                    group.Title = ActionCatalog.DescribeCategory(order[i]);
                    group.Detail = ActionCatalog.DescribeCategoryDetail(order[i]);
                    group.Commands = commands;
                    Groups.Add(group);
                }
            }
        }

        // ---- Enable ---------------------------------------------------------------------------

        private bool isEnabled;

        /// <summary>
        /// Master switch. Off by default; persisted by the shell.
        /// </summary>
        public bool IsEnabled
        {
            get
            {
                return isEnabled;
            }
            set
            {
                if (isEnabled == value)
                {
                    return;
                }

                isEnabled = value;
                Raise("IsEnabled");
                Raise("EnabledText");

                if (value)
                {
                    StartListening();
                }
                else
                {
                    retryTimer.Stop();
                    reinitPending = false;
                    testMode = false;
                    Raise("IsTestMode");
                    NewGeneration();
                    voice.Stop();
                    uiTimer.Stop();
                    ErrorText = "";
                    unavailableReported = false;
                    AudioLevel = 0;
                    MicLevel = 0;
                    SetPhase(VoicePhase.Off);
                    HideHud();
                    ActivityLog.Post(ActivityKind.Voice, "Voice commands off", null, "control center");
                }

                IsListening = isEnabled && voice.IsRunning;
                UpdateMicrophoneReadouts();
            }
        }

        public string EnabledText
        {
            get
            {
                return isEnabled ? "On" : "Off";
            }
        }

        private void StartListening()
        {
            whisperFailed = false;
            SpeechEngineNotice = "";
            NewGeneration();
            string error;
            if (!StartEngine(out error))
            {
                ErrorText = error ?? "Could not start listening";
                isEnabled = false;
                Raise("IsEnabled");
                Raise("EnabledText");
                SetPhase(VoicePhase.Off);
                ActivityLog.Post(ActivityKind.Voice, "Voice could not start", error, "control center");
                return;
            }

            ActivityLog.Post(ActivityKind.Voice, "Voice commands on", "Say " + WakeWordQuoted + ", wait for the chime, then the command", "control center");
        }

        /// <summary>
        /// Starts the recognizer on the preferred microphone, or on System Default when that is
        /// not connected or cannot be opened. The engine always starts in WakeOnly with no
        /// session, so a start can never carry a command over.
        /// </summary>
        private bool StartEngine(out string error)
        {
            voice.SpeechEngine = whisperFailed ? VoiceSpeechEngine.Windows : speechEngine;
            voice.ListeningClickEnabled = listeningClick;
            voice.WakeThreshold = WakeThresholdFor(wakeSensitivity);
            voice.CommandThreshold = commandThreshold;
            voice.DismissSoundEnabled = dismissSound;
            voice.WakeWord = wakeWord;
            voice.CustomPhrases = BuildCustomPhraseSet();

            string name;
            string wanted = EffectiveInput(out name);
            voice.InputDeviceId = wanted;
            voice.InputDeviceName = name;

            bool ok = voice.Start(out error);
            if (!ok && wanted != null)
            {
                string first = error;
                RuntimeLog.Write("Microphone “" + name + "” could not be used (" + first + "); falling back to System Default");
                openFailureNotice = "“" + name + "” could not be opened (" + first + "). Using System Default - press Refresh or choose it again to retry.";
                suspectId = wanted;
                voice.InputDeviceId = null;
                voice.InputDeviceName = null;
                ok = voice.Start(out error);
            }
            else if (ok)
            {
                openFailureNotice = null;
            }

            if (ok)
            {
                AudioInputDevice fallbackDefault = DefaultDevice;
                boundDefaultId = voice.ActiveInputId == null && fallbackDefault != null ? fallbackDefault.Id : null;
                lastStartedInputId = voice.ActiveInputId;
                lastStartUtc = DateTime.UtcNow;
                retryAttempts = 0;
                retryTimer.Stop();
                ErrorText = "";
                BackendText = voice.BackendName;
                if (voice.FallbackNotice.Length > 0) { SpeechEngineNotice = voice.FallbackNotice; }
                IsListening = isEnabled;
                uiTimer.Start();
                if (unavailableReported)
                {
                    unavailableReported = false;
                    ActivityLog.Post(ActivityKind.Voice, "Microphone available again", DescribeActiveInput(), "voice");
                }
            }

            UpdateMicrophoneReadouts();
            return ok;
        }

        // ---- Sensitivity -----------------------------------------------------------------------

        private VoiceSpeechEngine speechEngine = VoiceSpeechEngine.Whisper;
        private bool whisperFailed;
        private bool listeningClick = true;
        private string speechEngineNotice = "";
        public Array SpeechEngines { get { return Enum.GetValues(typeof(VoiceSpeechEngine)); } }
        public VoiceSpeechEngine SpeechEngine
        {
            get { return speechEngine; }
            set
            {
                if (Set(ref speechEngine, value))
                {
                    whisperFailed = false;
                    SpeechEngineNotice = "";
                    RequestReinitialize("speech engine changed");
                }
            }
        }
        public bool ListeningClick
        {
            get { return listeningClick; }
            set { if (Set(ref listeningClick, value)) { voice.ListeningClickEnabled = value; } }
        }
        public string SpeechEngineNotice { get { return speechEngineNotice; } private set { Set(ref speechEngineNotice, value); } }

        // Wake Sensitivity (0-100, shown to the user) maps onto the recognizer's confidence floor
        // for the wake word. Higher sensitivity = lower floor = easier to wake. It moves ONLY the
        // confidence floor; the isolation checks (a pause before the word, the word not buried in
        // a sentence, a plausible duration) are what actually keep conversation out, and they do
        // not change - so even at maximum sensitivity, the wake word inside a sentence still
        // cannot wake, and a false wake still only opens the listening window, never an action.
        private const double WakeThresholdAtZero = 0.90;   // least sensitive
        private const double WakeThresholdAtFull = 0.60;   // most sensitive (= the engine's own reject floor)

        private double wakeSensitivity = 65;

        /// <summary>
        /// How readily the wake word is accepted, 0 (strict) to 100 (permissive).
        /// </summary>
        public double WakeSensitivity
        {
            get
            {
                return wakeSensitivity;
            }
            set
            {
                double clamped = Math.Round(Math.Max(0, Math.Min(100, value)));
                if (Set(ref wakeSensitivity, clamped))
                {
                    voice.WakeThreshold = WakeThresholdFor(clamped);
                    Raise("WakeThresholdText");
                    UpdateDiagnostics();
                }
            }
        }

        private static double WakeThresholdFor(double sensitivity)
        {
            double t = WakeThresholdAtZero + (WakeThresholdAtFull - WakeThresholdAtZero) * (sensitivity / 100.0);
            return Math.Round(t, 2);
        }

        /// <summary>
        /// The confidence floor the current sensitivity maps to, for the diagnostics.
        /// </summary>
        public string WakeThresholdText
        {
            get { return "confidence ≥ " + F2(WakeThresholdFor(wakeSensitivity)); }
        }

        private double commandThreshold = WakeGatedVoiceEngine.DefaultCommandThreshold;

        /// <summary>
        /// Minimum recognizer confidence for a phrase in the command window.
        /// </summary>
        public double CommandThreshold
        {
            get
            {
                return commandThreshold;
            }
            set
            {
                double clamped = Math.Round(Math.Max(0.30, Math.Min(0.99, value)), 2);
                if (Set(ref commandThreshold, clamped))
                {
                    voice.CommandThreshold = clamped;
                }
            }
        }

        private bool dismissSound = true;

        /// <summary>
        /// A quiet falling tone when a command window closes without a command.
        /// </summary>
        public bool DismissSound
        {
            get
            {
                return dismissSound;
            }
            set
            {
                if (Set(ref dismissSound, value))
                {
                    voice.DismissSoundEnabled = value;
                }
            }
        }

        // ---- Test Wake Word (diagnostic, never executes) -------------------------------------------

        private bool testMode;
        /// <summary>
        /// Tuning mode: the full wake pipeline runs (so wake confidence and the mic meter are
        /// real), but a recognized command is shown and NOT executed - no Windows action, no
        /// shell command. Turning voice off leaves test mode.
        /// </summary>
        public bool IsTestMode
        {
            get { return testMode; }
            set
            {
                if (testMode == value)
                {
                    return;
                }

                testMode = value;
                Raise("IsTestMode");
                if (value)
                {
                    TestResultText = "Say " + WakeWordQuoted;
                    TestDetail = "Everything you say here is only measured, never carried out.";
                    TestState = VoiceHudState.Listening;
                    if (!isEnabled)
                    {
                        IsEnabled = true;   // needs the recognizer running to hear anything
                    }
                }
                else
                {
                    TestResultText = "";
                    TestDetail = "";
                    TestState = VoiceHudState.Hidden;
                }

                UpdateDiagnostics();
            }
        }

        private string testResultText = "";
        public string TestResultText { get { return testResultText; } private set { Set(ref testResultText, value); } }

        private string testDetail = "";
        public string TestDetail { get { return testDetail; } private set { Set(ref testDetail, value); } }

        private VoiceHudState testState = VoiceHudState.Hidden;
        /// <summary>
        /// Colours the test panel: Listening while waiting, Executed on a heard wake, Rejected on
        /// a near miss.
        /// </summary>
        public VoiceHudState TestState { get { return testState; } private set { Set(ref testState, value); } }

        // ---- Live state ---------------------------------------------------------------------------

        private VoicePhase phase = VoicePhase.Off;
        public VoicePhase Phase { get { return phase; } private set { Set(ref phase, value); } }

        private string phaseTitle = "";
        public string PhaseTitle { get { return phaseTitle; } private set { Set(ref phaseTitle, value); } }

        private string phaseDetail = "";
        public string PhaseDetail { get { return phaseDetail; } private set { Set(ref phaseDetail, value); } }

        private VoiceHudState phaseState = VoiceHudState.Hidden;
        /// <summary>
        /// Visual state of the Voice page's hero, including a lingering outcome.
        /// </summary>
        public VoiceHudState PhaseState { get { return phaseState; } private set { Set(ref phaseState, value); } }

        private bool isCommandWindowOpen;
        /// <summary>
        /// Wake accepted and the session not yet closed (chime or listening).
        /// </summary>
        public bool IsCommandWindowOpen { get { return isCommandWindowOpen; } private set { Set(ref isCommandWindowOpen, value); } }

        private bool isAwaitingCommand;
        public bool IsAwaitingCommand { get { return isAwaitingCommand; } private set { Set(ref isAwaitingCommand, value); } }

        private double listeningRemaining;
        /// <summary>
        /// 1 when the command window opens, 0 when it runs out.
        /// </summary>
        public double ListeningRemaining { get { return listeningRemaining; } private set { Set(ref listeningRemaining, value); } }

        private string listeningSecondsText = "";
        public string ListeningSecondsText { get { return listeningSecondsText; } private set { Set(ref listeningSecondsText, value); } }

        private bool isListening;
        /// <summary>
        /// The recognizer is running (listening for the wake word or in a session).
        /// </summary>
        public bool IsListening { get { return isListening; } private set { Set(ref isListening, value); } }

        private bool isHearingSound;
        public bool IsHearingSound { get { return isHearingSound; } private set { Set(ref isHearingSound, value); } }

        private double audioLevel;
        /// <summary>
        /// Microphone level, 0-1, while voice is on.
        /// </summary>
        public double AudioLevel { get { return audioLevel; } private set { Set(ref audioLevel, value); } }

        private string statusText = "Off";
        /// <summary>
        /// One-line state for Home, Settings and tooltips.
        /// </summary>
        public string StatusText { get { return statusText; } private set { Set(ref statusText, value); } }

        // ---- HUD -----------------------------------------------------------------------------------

        private bool hudVisible;
        public bool HudVisible { get { return hudVisible; } private set { Set(ref hudVisible, value); } }

        private VoiceHudState hudState = VoiceHudState.Hidden;
        public VoiceHudState HudState { get { return hudState; } private set { Set(ref hudState, value); } }

        private string hudText = "";
        public string HudText { get { return hudText; } private set { Set(ref hudText, value); } }

        private string hudDetail = "";
        public string HudDetail { get { return hudDetail; } private set { Set(ref hudDetail, value); } }

        // ---- Readouts ------------------------------------------------------------------------------

        private string backendText = "";
        public string BackendText { get { return backendText; } set { Set(ref backendText, value); } }

        private string errorText = "";
        public string ErrorText { get { return errorText; } set { if (Set(ref errorText, value)) { Raise("HasError"); } } }

        public bool HasError
        {
            get
            {
                return !string.IsNullOrEmpty(errorText);
            }
        }

        private string lastWakeText = "No wake yet";
        public string LastWakeText { get { return lastWakeText; } private set { Set(ref lastWakeText, value); } }

        private string lastWakeDetail = "";
        public string LastWakeDetail { get { return lastWakeDetail; } private set { Set(ref lastWakeDetail, value); } }

        private string lastCommandText = "No command yet";
        public string LastCommandText { get { return lastCommandText; } private set { Set(ref lastCommandText, value); } }

        private string lastCommandDetail = "";
        public string LastCommandDetail { get { return lastCommandDetail; } private set { Set(ref lastCommandDetail, value); } }

        /// <summary>
        /// Newest first. Judgements only - never background speech.
        /// </summary>
        public ObservableCollection<VoiceDecision> Decisions { get; private set; }

        private string diagnosticsText = "";
        public string DiagnosticsText { get { return diagnosticsText; } private set { Set(ref diagnosticsText, value); } }

        // ---- Microphone -----------------------------------------------------------------------------

        private List<AudioInputDevice> devices = new List<AudioInputDevice>();

        /// <summary>
        /// The user's choice: an endpoint ID, or empty for System Default. Kept (and saved) even
        /// while that device is disconnected, so voice returns to it when it comes back.
        /// </summary>
        private string preferredId = "";
        private string preferredName = "";

        /// <summary>
        /// The Windows default input the recognizer was bound to when started on System
        /// Default, to notice the default moving to another device.
        /// </summary>
        private string boundDefaultId;
        private string openFailureNotice;
        private bool rebuildingOptions;
        private bool reinitPending;
        private int retryAttempts;
        private bool unavailableReported;
        private string inputsSignature = "";
        private string deviceSetSignature = "";

        /// <summary>
        /// A selected device whose input died within <see cref="SuspectWindow"/> of starting. It
        /// is skipped (System Default is used) until the user picks it again, presses Refresh, or
        /// the set of connected devices changes - so a flapping Bluetooth link cannot make the
        /// recognizer restart over and over.
        /// </summary>
        private string suspectId;
        private string lastStartedInputId;
        private DateTime lastStartUtc;
        private static readonly TimeSpan SuspectWindow = TimeSpan.FromSeconds(10);

        /// <summary>
        /// The dropdown: System Default, every connected input, and the saved choice marked
        /// "not connected" while it is missing.
        /// </summary>
        public ObservableCollection<MicrophoneOption> MicrophoneOptions { get; private set; }

        /// <summary>
        /// Connected inputs, with the one in use marked.
        /// </summary>
        public ObservableCollection<MicrophoneOption> AvailableInputs { get; private set; }

        public string PreferredMicrophoneId
        {
            get { return preferredId; }
        }

        public string PreferredMicrophoneName
        {
            get { return preferredName; }
        }

        /// <summary>
        /// Restores the saved choice (before voice is started).
        /// </summary>
        public void SetMicrophonePreference(string id, string name)
        {
            preferredId = id ?? "";
            preferredName = preferredId.Length == 0 ? "" : (name ?? "");
            RefreshMicrophones();
        }

        private MicrophoneOption selectedMicrophone;

        public MicrophoneOption SelectedMicrophone
        {
            get
            {
                return selectedMicrophone;
            }
            set
            {
                // The ComboBox pushes null while its list is being rebuilt; that is not a choice.
                if (value == null || rebuildingOptions || ReferenceEquals(value, selectedMicrophone))
                {
                    return;
                }

                selectedMicrophone = value;
                Raise("SelectedMicrophone");
                if (string.Equals(value.Id, preferredId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                bool hadMissingEntry = preferredId.Length > 0 && FindDevice(preferredId) == null;
                preferredId = value.Id;
                preferredName = value.IsSystemDefault ? "" : value.Name;
                openFailureNotice = null;
                suspectId = null;
                RuntimeLog.Write("Microphone selected: " + (value.IsSystemDefault
                    ? "System Default"
                    : "“" + value.Name + "” (" + AudioInputDevices.ShortId(value.Id) + ")"));

                if (hadMissingEntry)
                {
                    // Drop the "not connected" entry, after the ComboBox has finished this change.
                    dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RebuildOptions));
                }

                ApplyMicrophoneChoice("microphone changed");
                UpdateMicrophoneReadouts();
            }
        }

        private string selectedInputText = "";
        /// <summary>
        /// The input in use (or that will be used when voice is switched on).
        /// </summary>
        public string SelectedInputText { get { return selectedInputText; } private set { Set(ref selectedInputText, value); } }

        private MicrophoneStatus microphoneStatus = MicrophoneStatus.Off;
        public MicrophoneStatus MicrophoneStatus { get { return microphoneStatus; } private set { Set(ref microphoneStatus, value); } }

        private string microphoneStatusText = "Voice off";
        public string MicrophoneStatusText { get { return microphoneStatusText; } private set { Set(ref microphoneStatusText, value); } }

        private string microphoneNotice = "";
        public string MicrophoneNotice
        {
            get { return microphoneNotice; }
            private set { if (Set(ref microphoneNotice, value)) { Raise("HasMicrophoneNotice"); } }
        }

        public bool HasMicrophoneNotice
        {
            get { return !string.IsNullOrEmpty(microphoneNotice); }
        }

        private string microphoneDeviceIdText = "";
        public string MicrophoneDeviceIdText { get { return microphoneDeviceIdText; } private set { Set(ref microphoneDeviceIdText, value); } }

        private bool microphoneAvailable;
        /// <summary>
        /// The chosen input is connected (for System Default: any input is).
        /// </summary>
        public bool MicrophoneAvailable { get { return microphoneAvailable; } private set { Set(ref microphoneAvailable, value); } }

        private bool recognizerInitialized;
        public bool RecognizerInitialized { get { return recognizerInitialized; } private set { Set(ref recognizerInitialized, value); } }

        private string microphoneSummary = "";
        /// <summary>
        /// One line for Settings → system health.
        /// </summary>
        public string MicrophoneSummary { get { return microphoneSummary; } private set { Set(ref microphoneSummary, value); } }

        private bool hasMicrophone;
        public bool HasMicrophone { get { return hasMicrophone; } private set { Set(ref hasMicrophone, value); } }

        // ---- Input level (live meter) --------------------------------------------------------------

        private double micLevel;
        /// <summary>
        /// Live microphone level 0-1 (peak of the selected device, or the recognizer's own level
        /// for System Default), for the meter fill.
        /// </summary>
        public double MicLevel { get { return micLevel; } private set { if (Set(ref micLevel, value)) { Raise("MicLevelPercentText"); Raise("MicLevelZone"); } } }

        public string MicLevelPercentText
        {
            get { return recognizerInitialized ? (int)Math.Round(micLevel * 100) + "%" : "—"; }
        }

        /// <summary>
        /// 0 too quiet, 1 good, 2 very loud - for the meter's colour and the guidance line.
        /// </summary>
        public int MicLevelZone
        {
            get
            {
                if (!recognizerInitialized || micLevel < 0.06)
                {
                    return 0;
                }

                return micLevel > 0.92 ? 2 : 1;
            }
        }

        // ---- Input gain (Windows device level) -----------------------------------------------------

        private bool readingGain;

        private double micGain;
        /// <summary>
        /// The selected microphone's Windows input level, 0-100. Setting it writes the device
        /// level (the same slider as Sound settings), so a too-quiet mic can be turned up.
        /// </summary>
        public double MicGain
        {
            get
            {
                return micGain;
            }
            set
            {
                double clamped = Math.Round(Math.Max(0, Math.Min(100, value)));
                if (!Set(ref micGain, clamped) || readingGain || !canControlMicGain)
                {
                    return;
                }

                string error;
                if (!CaptureVolume.TrySetPercent(GainEndpoint(), (int)clamped, out error))
                {
                    RuntimeLog.Write("Microphone input level not set: " + error);
                    CanControlMicGain = false;
                }
            }
        }

        private bool canControlMicGain;
        /// <summary>
        /// The selected microphone exposes a writable input level. False = the slider is shown
        /// read-only (some Bluetooth devices), or there is no device.
        /// </summary>
        public bool CanControlMicGain { get { return canControlMicGain; } private set { if (Set(ref canControlMicGain, value)) { Raise("MicGainStatusText"); } } }

        private bool hasMicGain;
        /// <summary>
        /// A level could be read at all (the card's input-level row is shown).
        /// </summary>
        public bool HasMicGain { get { return hasMicGain; } private set { Set(ref hasMicGain, value); } }

        public string MicGainStatusText
        {
            get { return canControlMicGain ? "" : "read-only for this device"; }
        }

        /// <summary>
        /// The endpoint whose input level the gain slider controls: the running recognizer's
        /// bound device, else the effective choice, else the default capture device (null).
        /// </summary>
        private string GainEndpoint()
        {
            if (isEnabled && voice.IsRunning)
            {
                return voice.ActiveInputId;
            }

            string name;
            return EffectiveInput(out name);
        }

        /// <summary>
        /// Re-reads the selected microphone's input level and whether it can be changed. A no-op
        /// write (inside TryProbe) decides writability without any audible change.
        /// </summary>
        private void ReadGain()
        {
            int percent;
            bool canSet;
            string error;
            bool ok = CaptureVolume.TryProbe(GainEndpoint(), out percent, out canSet, out error);

            readingGain = true;
            try
            {
                HasMicGain = ok;
                CanControlMicGain = ok && canSet;
                if (ok && Set(ref micGain, percent))
                {
                    Raise("MicGain");
                }
            }
            finally
            {
                readingGain = false;
            }
        }

        private AudioInputDevice DefaultDevice
        {
            get
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    if (devices[i].IsDefault)
                    {
                        return devices[i];
                    }
                }

                return null;
            }
        }

        private AudioInputDevice FindDevice(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            for (int i = 0; i < devices.Count; i++)
            {
                if (string.Equals(devices[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return devices[i];
                }
            }

            return null;
        }

        /// <summary>
        /// The endpoint to open: the preferred device when it is connected, otherwise null
        /// (System Default).
        /// </summary>
        private string EffectiveInput(out string name)
        {
            AudioInputDevice preferred = FindDevice(preferredId);
            if (preferred != null && !string.Equals(preferred.Id, suspectId, StringComparison.OrdinalIgnoreCase))
            {
                name = preferred.Name;
                return preferred.Id;
            }

            name = null;
            return null;
        }

        private string DefaultLabel()
        {
            AudioInputDevice fallbackDefault = DefaultDevice;
            return "System Default" + (fallbackDefault != null ? " · " + fallbackDefault.Name : "");
        }

        private string DescribeActiveInput()
        {
            string activeId = voice.ActiveInputId;
            return activeId != null ? (voice.ActiveInputName ?? "Selected microphone") : DefaultLabel();
        }

        /// <summary>
        /// Re-reads the device list and applies the choice to the running recognizer. Called at
        /// start, from the Refresh button and (debounced) on Windows device notifications.
        /// </summary>
        public void RefreshMicrophones()
        {
            ReadDevices();
            RebuildOptions();
            ApplyMicrophoneChoice("device list changed");
            UpdateMicrophoneReadouts();
        }

        private void ReadDevices()
        {
            string error;
            devices = AudioInputDevices.List(out error);
            if (error != null)
            {
                RuntimeLog.Write("Microphone list unavailable: " + error);
            }

            AudioInputDevice preferred = FindDevice(preferredId);
            if (preferred != null)
            {
                preferredName = preferred.Name;
            }

            StringBuilder ids = new StringBuilder();
            for (int i = 0; i < devices.Count; i++)
            {
                ids.Append(devices[i].Id).Append('|');
            }

            if (ids.ToString() != deviceSetSignature)
            {
                deviceSetSignature = ids.ToString();
                suspectId = null;
            }
        }

        private void RebuildOptions()
        {
            rebuildingOptions = true;
            MicrophoneOption chosen = null;
            try
            {
                AudioInputDevice fallbackDefault = DefaultDevice;
                MicrophoneOptions.Clear();
                MicrophoneOption system = new MicrophoneOption("", "System Default",
                    fallbackDefault != null ? fallbackDefault.Name : "no input device", true, true, false, false);
                MicrophoneOptions.Add(system);

                for (int i = 0; i < devices.Count; i++)
                {
                    AudioInputDevice device = devices[i];
                    MicrophoneOptions.Add(new MicrophoneOption(device.Id, device.Name, device.IsDefault ? "Windows default" : "",
                        true, false, device.IsDefault, false));
                }

                if (preferredId.Length > 0 && FindDevice(preferredId) == null)
                {
                    MicrophoneOptions.Add(new MicrophoneOption(preferredId, preferredName.Length > 0 ? preferredName : "Saved microphone",
                        "not connected", false, false, false, false));
                }

                for (int i = 0; i < MicrophoneOptions.Count && chosen == null; i++)
                {
                    if (string.Equals(MicrophoneOptions[i].Id, preferredId, StringComparison.OrdinalIgnoreCase))
                    {
                        chosen = MicrophoneOptions[i];
                    }
                }

                selectedMicrophone = chosen ?? system;
            }
            finally
            {
                rebuildingOptions = false;
            }

            Raise("SelectedMicrophone");
        }

        /// <summary>
        /// Brings the running recognizer in line with the choice and the device list: switches
        /// to the chosen device, falls back to System Default when it disappears, returns to it
        /// when it reconnects, and rebinds System Default when Windows' default input moves.
        /// </summary>
        private void ApplyMicrophoneChoice(string reason)
        {
            if (!isEnabled || reinitPending)
            {
                return;
            }

            if (!voice.IsRunning)
            {
                // Recovering from a lost input: a device change is a good moment to try again.
                ScheduleRetry(TimeSpan.FromMilliseconds(300));
                return;
            }

            string name;
            string wanted = EffectiveInput(out name);
            string bound = voice.ActiveInputId;
            bool switchDevice = !string.Equals(wanted, bound, StringComparison.OrdinalIgnoreCase);

            AudioInputDevice fallbackDefault = DefaultDevice;
            bool defaultMoved = wanted == null && bound == null && boundDefaultId != null && fallbackDefault != null
                && !string.Equals(fallbackDefault.Id, boundDefaultId, StringComparison.OrdinalIgnoreCase);

            if (switchDevice)
            {
                string description = wanted != null
                    ? "“" + name + "”"
                    : (preferredId.Length > 0 ? "System Default (“" + preferredName + "” not connected)" : "System Default");
                RequestReinitialize(reason + " → " + description);
            }
            else if (defaultMoved)
            {
                RequestReinitialize("Windows default input changed → " + fallbackDefault.Name);
            }
        }

        /// <summary>
        /// Restarts the recognizer on the effective input. Shows "Reinitializing" first, drops
        /// every queued event of the old recognizer (a command included), and clears the HUD.
        /// </summary>
        private void RequestReinitialize(string reason)
        {
            if (reinitPending || !isEnabled)
            {
                return;
            }

            reinitPending = true;
            retryTimer.Stop();
            NewGeneration();
            uiTimer.Stop();
            IsListening = false;
            SetPhase(VoicePhase.Off);
            HideHud();
            UpdateMicrophoneReadouts();
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Reinitialize(reason)));
        }

        private void Reinitialize(string reason)
        {
            reinitPending = false;
            if (!isEnabled)
            {
                UpdateMicrophoneReadouts();
                return;
            }

            voice.Stop();
            NewGeneration();

            string error;
            if (StartEngine(out error))
            {
                RuntimeLog.Write("Voice input switched (" + reason + "): now " + DescribeActiveInput());
            }
            else
            {
                ReportUnavailable(error);
                ScheduleRetry(TimeSpan.FromSeconds(2));
            }

            SetPhase(voice.IsRunning ? voice.Phase : VoicePhase.Off);
            UpdateMicrophoneReadouts();
        }

        private void ScheduleRetry(TimeSpan delay)
        {
            retryTimer.Stop();
            retryTimer.Interval = delay;
            retryTimer.Start();
        }

        /// <summary>
        /// Voice is on but its input is gone: try again, on System Default if the chosen
        /// device is still missing. Backs off to one attempt every 10 s.
        /// </summary>
        private void RetryStart()
        {
            if (!isEnabled || reinitPending || voice.IsRunning)
            {
                return;
            }

            ReadDevices();
            RebuildOptions();
            NewGeneration();

            string error;
            if (StartEngine(out error))
            {
                RuntimeLog.Write("Voice input restored: " + DescribeActiveInput());
            }
            else
            {
                retryAttempts++;
                ReportUnavailable(error);
                ScheduleRetry(TimeSpan.FromSeconds(Math.Min(10, 2 * retryAttempts)));
            }

            SetPhase(voice.IsRunning ? voice.Phase : VoicePhase.Off);
            UpdateMicrophoneReadouts();
        }

        private void ReportUnavailable(string reason)
        {
            string text = string.IsNullOrEmpty(reason) ? "the microphone input ended" : reason;
            ErrorText = "Microphone unavailable: " + text + ". Retrying automatically.";
            if (!unavailableReported)
            {
                unavailableReported = true;
                ActivityLog.Post(ActivityKind.Voice, "Microphone unavailable", text, "voice");
            }
        }

        private void UpdateMicrophoneReadouts()
        {
            string name;
            string wanted = EffectiveInput(out name);
            AudioInputDevice fallbackDefault = DefaultDevice;
            bool running = isEnabled && voice.IsRunning;
            string activeId = running ? voice.ActiveInputId : null;

            if (running)
            {
                SelectedInputText = DescribeActiveInput();
            }
            else
            {
                SelectedInputText = wanted != null ? name : DefaultLabel();
            }

            MicrophoneStatus status = !isEnabled
                ? MicrophoneStatus.Off
                : (reinitPending ? MicrophoneStatus.Reinitializing : (running ? MicrophoneStatus.Ready : MicrophoneStatus.Unavailable));
            MicrophoneStatus = status;
            switch (status)
            {
                case MicrophoneStatus.Ready: MicrophoneStatusText = "Ready"; break;
                case MicrophoneStatus.Reinitializing: MicrophoneStatusText = "Reinitializing…"; break;
                case MicrophoneStatus.Unavailable: MicrophoneStatusText = "Unavailable · retrying"; break;
                default: MicrophoneStatusText = "Voice off"; break;
            }

            MicrophoneAvailable = preferredId.Length > 0 ? FindDevice(preferredId) != null : fallbackDefault != null;
            RecognizerInitialized = running;
            MicrophoneDeviceIdText = running
                ? (activeId ?? "(system default) " + (fallbackDefault != null ? fallbackDefault.Id : "none"))
                : (wanted ?? "(system default)");

            if (devices.Count == 0)
            {
                MicrophoneNotice = "No microphone is connected.";
            }
            else if (preferredId.Length > 0 && FindDevice(preferredId) == null)
            {
                MicrophoneNotice = "“" + (preferredName.Length > 0 ? preferredName : "The saved microphone")
                    + "” isn't connected. Using System Default until it is back.";
            }
            else if (openFailureNotice != null && preferredId.Length > 0)
            {
                MicrophoneNotice = openFailureNotice;
            }
            else if (suspectId != null && string.Equals(suspectId, preferredId, StringComparison.OrdinalIgnoreCase))
            {
                MicrophoneNotice = "“" + preferredName + "” stopped delivering audio right after starting. Using System Default - "
                    + "press Refresh or choose it again to retry.";
            }
            else
            {
                MicrophoneNotice = "";
            }

            MicrophoneSummary = status == MicrophoneStatus.Off ? SelectedInputText : SelectedInputText + " · " + MicrophoneStatusText;
            HasMicrophone = devices.Count > 0;

            // The available-inputs list, rebuilt only when something in it changed.
            StringBuilder signature = new StringBuilder();
            signature.Append(activeId).Append('|').Append(running);
            for (int i = 0; i < devices.Count; i++)
            {
                signature.Append('|').Append(devices[i].Id).Append(devices[i].IsDefault ? "*" : "").Append(devices[i].Name);
            }

            if (signature.ToString() != inputsSignature)
            {
                inputsSignature = signature.ToString();
                AvailableInputs.Clear();
                for (int i = 0; i < devices.Count; i++)
                {
                    AudioInputDevice device = devices[i];
                    bool inUse = running && (activeId != null
                        ? string.Equals(device.Id, activeId, StringComparison.OrdinalIgnoreCase)
                        : device.IsDefault);
                    AvailableInputs.Add(new MicrophoneOption(device.Id, device.Name, "", true, false, device.IsDefault, inUse));
                }
            }

            ReadGain();
            Raise("MicLevelPercentText");
            Raise("MicLevelZone");
        }

        // ---- Engine events (UI thread) -----------------------------------------------------------------

        private void OnPhaseChanged(VoicePhaseEventArgs e)
        {
            if (!isEnabled && e.Phase != VoicePhase.Off)
            {
                return;
            }

            SetPhase(e.Phase);
            switch (e.Phase)
            {
                case VoicePhase.Acknowledging:
                    CancelAssistant();
                    ShowHud(VoiceHudState.Wake, "Wake detected", "✦");
                    outcomeUntilUtc = DateTime.MinValue;
                    break;

                case VoicePhase.Listening:
                    if (e.Session != null)
                    {
                        deadlineUtc = e.Session.DeadlineUtc;
                        if (e.Detail != "extended")
                        {
                            windowOpenedUtc = e.Session.GateOpenUtc;
                        }
                    }

                    ShowHud(VoiceHudState.Listening, "Listening… speak now", "Say one request");
                    UpdateCountdown();
                    break;

                case VoicePhase.Recording:
                    ShowHud(VoiceHudState.Recording, "Hearing you…", "Pause when finished");
                    break;
                case VoicePhase.Transcribing:
                    ShowHud(VoiceHudState.Transcribing, "Understanding…", "Transcribing locally");
                    break;

                case VoicePhase.WakeOnly:
                    // An executed command is shown by OnCommandRecognized, which knows whether
                    // the action really happened.
                    if (e.Outcome != VoiceOutcome.None && e.Outcome != VoiceOutcome.Executed)
                    {
                        ShowOutcome(e.Outcome, e.Detail);
                    }
                    else if (e.Outcome == VoiceOutcome.None)
                    {
                        ApplyIdleText();
                    }
                    break;

                case VoicePhase.Off:
                    HideHud();
                    break;
            }

            IsListening = isEnabled && e.Phase != VoicePhase.Off;
        }

        private void OnCommandRecognized(VoiceCommandEventArgs e)
        {
            if (!voice.IsCurrent(e)) { return; }
            // Switched off between recognition and now: do nothing.
            if (!isEnabled || reinitPending || e.Session == null || e.Intent == null)
            {
                return;
            }

            // One wake, one action - even if this event were somehow delivered twice.
            if (!e.Session.TryMarkExecuted())
            {
                return;
            }

            // Test Wake Word: show what would run, but never carry it out.
            if (testMode)
            {
                string would = e.Intent.Feedback;
                if (e.Intent.Kind == VoiceIntentKind.Custom)
                {
                    CustomCommandRowViewModel row = FindCustomCommand(e.Intent.CommandId);
                    would = row != null ? row.Describe() + ForegroundNote(row) : "a custom command that no longer exists";
                }

                TestResultText = "Heard “" + e.Phrase + "”";
                TestDetail = "Would run: " + would + " · confidence " + F2(e.Confidence) + " (not executed - test mode)";
                TestState = VoiceHudState.Executed;
                LastCommandText = "“" + e.Phrase + "”";
                LastCommandDetail = "test only · confidence " + F2(e.Confidence);
                RuntimeLog.Write("Voice test: heard command '" + e.Phrase + "' (confidence " + F2(e.Confidence) + "), not executed");
                ShowOutcome(VoiceOutcome.Cancelled, "Test mode - nothing run");
                return;
            }

            VoiceIntent intent = e.Intent;
            if (intent.Kind == VoiceIntentKind.Request)
            {
                LastCommandText = "“" + e.Phrase + "”";
                if (OtherRequest != null && OtherRequest(e.Phrase)) { return; }
                ShowOutcome(VoiceOutcome.NotRecognized, "Not a command: “" + e.Phrase + "”");
                return;
            }
            bool done = false;
            string feedback = intent.Feedback;
            string failure = null;

            switch (intent.Kind)
            {
                case VoiceIntentKind.Action:
                    done = engine.ExecuteAction(intent.Action, "voice");
                    break;

                case VoiceIntentKind.Shell:
                    done = RunShellCommand(intent.ShellCommand, intent.Feedback, e.Phrase);
                    break;

                case VoiceIntentKind.Custom:
                    done = RunCustomCommand(intent.CommandId, e.Phrase, out feedback, out failure);
                    break;
            }

            LastCommandText = "“" + e.Phrase + "”";
            LastCommandDetail = feedback + (double.IsNaN(e.Confidence) ? " · local Whisper" : " · confidence " + F2(e.Confidence))
                + (done ? "" : " · " + (failure ?? "failed"));
            if (e.Session.TranscriptUtc != default(DateTime))
            {
                string timing = "Whisper transcript → action " + (DateTime.UtcNow - e.Session.TranscriptUtc).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)
                    + " ms; speech end → action " + (DateTime.UtcNow - e.Session.SpeechEndUtc).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms";
                RuntimeLog.Write(timing);
                LastCommandDetail += " · " + timing;
            }
            RuntimeLog.Write("Voice command #" + e.Session.Id + " '" + e.Phrase + "' -> " + feedback
                + (done ? "" : " (FAILED" + (failure != null ? ": " + failure : "") + ")") + " (confidence " + F2(e.Confidence) + ")");

            if (done)
            {
                ShowOutcome(VoiceOutcome.Executed, feedback);
            }
            else
            {
                ShowOutcome(VoiceOutcome.NotRecognized, failure ?? feedback + " could not be carried out");
            }
        }

        private bool RunShellCommand(string shellCommand, string feedback, string phrase)
        {
            ActivityLog.Post(ActivityKind.Voice, feedback, "“" + phrase + "”", "voice");
            EventHandler<string> handler = ShellCommandRequested;
            if (handler != null)
            {
                handler.Invoke(this, shellCommand);
            }

            return true;
        }

        /// <summary>
        /// Carries out a custom command, looked up by id now (so an edit since the recognizer
        /// started is honoured). A key combination limited to one app is only sent when that
        /// app owns the window in front; otherwise nothing is pressed.
        /// </summary>
        private bool RunCustomCommand(string id, string phrase, out string feedback, out string failure)
        {
            failure = null;
            CustomCommandRowViewModel row = FindCustomCommand(id);
            if (row == null || !row.IsListening)
            {
                feedback = "“" + phrase + "”";
                failure = "that custom command has been removed or switched off";
                return false;
            }

            feedback = row.Phrase + " → ";
            switch (row.Kind)
            {
                case CustomCommandKind.Keys:
                    feedback += row.Keys;
                    if (row.OnlyInApp.Length > 0)
                    {
                        string front = ForegroundApp.CurrentProcessName();
                        if (!ForegroundApp.Matches(front, row.OnlyInApp))
                        {
                            failure = row.OnlyInApp + " isn't in front" + (front != null ? " (" + front + " is)" : "") + " - nothing pressed";
                            return false;
                        }
                    }

                    return engine.ExecuteAction(ControlAction.Keys(row.Keys), "voice");

                case CustomCommandKind.OpenApp:
                    feedback += "open " + CustomCommandRowViewModel.DisplayTarget(row.Target);
                    if (!engine.ExecuteAction(ControlAction.Launch(row.Target), "voice"))
                    {
                        failure = "could not open " + row.Target;
                        return false;
                    }

                    return true;

                default:
                    ActionDescriptor descriptor = ActionCatalog.Find(row.ActionId);
                    if (!CustomCommandRules.IsAssignable(descriptor))
                    {
                        failure = "no action chosen";
                        return false;
                    }

                    feedback += descriptor.Name;
                    if (!string.IsNullOrEmpty(descriptor.ShellCommand))
                    {
                        return RunShellCommand(descriptor.ShellCommand, descriptor.Name, phrase);
                    }

                    return engine.ExecuteAction(descriptor.Action, "voice");
            }
        }

        /// <summary>
        /// For test mode: whether a key combination limited to an app would be sent right now.
        /// </summary>
        private static string ForegroundNote(CustomCommandRowViewModel row)
        {
            if (row.Kind != CustomCommandKind.Keys || row.OnlyInApp.Length == 0)
            {
                return "";
            }

            string front = ForegroundApp.CurrentProcessName();
            return ForegroundApp.Matches(front, row.OnlyInApp)
                ? " (" + row.OnlyInApp + " is in front)"
                : " - but " + row.OnlyInApp + " isn't in front" + (front != null ? " (" + front + " is)" : "") + ", so nothing would be pressed";
        }

        // ---- Custom commands ---------------------------------------------------------------------------

        /// <summary>
        /// The user's commands, in the order shown. Saved to voice-commands.json after each edit.
        /// </summary>
        public ObservableCollection<CustomCommandRowViewModel> CustomCommands { get; private set; }

        public CustomKindChoice[] KindChoices
        {
            get { return CustomKindChoice.All; }
        }

        /// <summary>
        /// Built-in actions a custom phrase may run.
        /// </summary>
        public List<ActionDescriptor> ActionChoices { get; private set; }

        private string customCommandsNotice = "";
        public string CustomCommandsNotice
        {
            get { return customCommandsNotice; }
            private set { if (Set(ref customCommandsNotice, value ?? "")) { Raise("HasCustomCommandsNotice"); } }
        }

        public bool HasCustomCommandsNotice
        {
            get { return customCommandsNotice.Length > 0; }
        }

        private string customCommandsSummary = "";
        public string CustomCommandsSummary
        {
            get { return customCommandsSummary; }
            private set { Set(ref customCommandsSummary, value); }
        }

        private bool customDirty;

        private void LoadCustomCommands()
        {
            string warning;
            List<CustomCommandDefinition> saved = CustomCommandStore.Load(out warning);
            CustomCommandsNotice = warning ?? "";
            List<CustomCommandDefinition> definitions = saved ?? CustomCommandDefinition.Starters();
            foreach (CustomCommandDefinition definition in definitions)
            {
                CustomCommands.Add(NewCustomRow(definition));
            }

            ValidateCustomCommands();
            voice.CustomPhrases = BuildCustomPhraseSet();
        }

        private CustomCommandRowViewModel NewCustomRow(CustomCommandDefinition definition)
        {
            return new CustomCommandRowViewModel(definition, OnCustomCommandChanged, DeleteCustomCommand);
        }

        private void AddCustomCommand()
        {
            CustomCommands.Add(NewCustomRow(CustomCommandDefinition.Create("", CustomCommandKind.Keys)));
            ValidateCustomCommands();
            ScheduleCustomCommit();
        }

        private void DeleteCustomCommand(CustomCommandRowViewModel row)
        {
            CustomCommands.Remove(row);
            ValidateCustomCommands();
            ScheduleCustomCommit();
        }

        private void OnCustomCommandChanged(CustomCommandRowViewModel row, bool grammarRelevant)
        {
            ValidateCustomCommands();
            ScheduleCustomCommit();
        }

        private void ScheduleCustomCommit()
        {
            customDirty = true;
            customCommitTimer.Stop();
            customCommitTimer.Start();
        }

        /// <summary>
        /// Saves the list and, if the phrases listened for changed, restarts the recognizer on the
        /// new grammar (dropping any open session, like a microphone switch).
        /// </summary>
        private void CommitCustomCommands()
        {
            if (customDirty)
            {
                customDirty = false;
                SaveCustomCommands();
            }

            CustomPhraseSet set = BuildCustomPhraseSet();
            voice.CustomPhrases = set;
            if (isEnabled && voice.IsRunning && set.Signature != voice.ActiveCustomPhrases.Signature)
            {
                RequestReinitialize("custom commands changed");
            }
        }

        private void SaveCustomCommands()
        {
            List<CustomCommandDefinition> definitions = new List<CustomCommandDefinition>();
            foreach (CustomCommandRowViewModel row in CustomCommands)
            {
                definitions.Add(row.Definition);
            }

            string error;
            CustomCommandsNotice = CustomCommandStore.Save(definitions, out error) ? "" : "Custom commands not saved: " + error;
        }

        /// <summary>
        /// Checks every row (phrase, clashes, duplicates, the action's settings) and shows the
        /// result on it. Only valid, switched-on rows are listened for.
        /// </summary>
        private void ValidateCustomCommands()
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            int listening = 0;
            int problems = 0;
            foreach (CustomCommandRowViewModel row in CustomCommands)
            {
                string error = CustomCommandRules.CheckPhrase(row.Phrase, wakeWord);
                if (error == null && row.HasUnknownKind)
                {
                    error = "Made by a newer version of KINECT-OS - choose what it does.";
                }

                if (error == null)
                {
                    error = CheckCustomAction(row);
                }

                if (error == null && row.IsEnabled && !seen.Add(VoicePhrases.MatchKey(row.Phrase)))
                {
                    error = "Same phrase as another command above.";
                }

                row.Refresh(error);
                if (row.IsListening)
                {
                    listening++;
                }
                else if (error != null)
                {
                    problems++;
                }
            }

            CustomCommandsSummary = listening + " listening" + (problems > 0 ? " · " + problems + " need attention" : "");
        }

        private static string CheckCustomAction(CustomCommandRowViewModel row)
        {
            switch (row.Kind)
            {
                case CustomCommandKind.Keys:
                    KeyChord chord;
                    string error;
                    return KeyChord.TryParse(row.Keys, out chord, out error) ? null : error;

                case CustomCommandKind.OpenApp:
                    return row.Target.Length > 0 ? null : "Choose the app, file or URL to open.";

                default:
                    return CustomCommandRules.IsAssignable(ActionCatalog.Find(row.ActionId)) ? null : "Choose the action to run.";
            }
        }

        private CustomPhraseSet BuildCustomPhraseSet()
        {
            List<KeyValuePair<string, string>> phrases = new List<KeyValuePair<string, string>>();
            foreach (CustomCommandRowViewModel row in CustomCommands)
            {
                if (row.IsListening)
                {
                    phrases.Add(new KeyValuePair<string, string>(row.Id, row.Phrase));
                }
            }

            return new CustomPhraseSet(phrases);
        }

        private CustomCommandRowViewModel FindCustomCommand(string id)
        {
            foreach (CustomCommandRowViewModel row in CustomCommands)
            {
                if (row.Id == id)
                {
                    return row;
                }
            }

            return null;
        }

        /// <summary>
        /// Offers the apps running now in a row's "only in" dropdown (called as it opens).
        /// </summary>
        public void RefreshRunningApps(CustomCommandRowViewModel row)
        {
            if (row != null)
            {
                row.AddAppChoices(ForegroundApp.WindowedAppNames());
            }
        }

        private void OnDecision(VoiceDecision decision)
        {
            Decisions.Insert(0, decision);
            while (Decisions.Count > MaxDecisions)
            {
                Decisions.RemoveAt(Decisions.Count - 1);
            }

            if (decision.Stage == "wake")
            {
                LastWakeText = decision.Verdict == VoiceVerdict.Accepted ? "Accepted" : "Rejected";
                LastWakeDetail = "confidence " + F2(decision.Confidence) + " · " + decision.Time.ToString("HH:mm:ss")
                    + (decision.Verdict == VoiceVerdict.Accepted ? "" : " · " + decision.Reason);

                if (testMode)
                {
                    ShowTestWake(decision);
                }
            }
            else if (decision.Stage == "command" && decision.Verdict == VoiceVerdict.Rejected)
            {
                LastCommandText = decision.Phrase.Length > 0 ? "“" + decision.Phrase + "”" : "Not a command";
                LastCommandDetail = "rejected · " + decision.Reason;
            }

            UpdateDiagnostics();
        }

        /// <summary>
        /// Test-mode feedback for a wake judgement: accepted (chime follows), a near miss with a
        /// confidence (so the user can nudge Wake Sensitivity), or a low-level rejection.
        /// </summary>
        private void ShowTestWake(VoiceDecision decision)
        {
            if (decision.Verdict == VoiceVerdict.Accepted)
            {
                TestResultText = "✓ Wake detected · confidence " + F2(decision.Confidence);
                TestDetail = "Now say a command (it will be measured, not run).";
                TestState = VoiceHudState.Executed;
            }
            else if (!double.IsNaN(decision.Confidence))
            {
                TestResultText = "Almost · heard " + WakeWordQuoted + " at " + F2(decision.Confidence);
                double floor = WakeThresholdFor(wakeSensitivity);
                TestDetail = decision.Confidence < floor
                    ? "Below the current floor (" + F2(floor) + "). Raise Wake Sensitivity or the input level, then try again."
                    : "Rejected: " + decision.Reason + ". Say " + WakeWordQuoted + " on its own, after a short pause.";
                TestState = VoiceHudState.Rejected;
            }
        }

        /// <summary>
        /// The recognizer stopped by itself: its microphone went away (unplugged, Bluetooth
        /// dropped, session locked) or the input ended. Voice stays switched on and recovers on
        /// its own - on System Default if the chosen microphone is gone.
        /// </summary>
        private void OnStopped(Exception error)
        {
            if (!isEnabled)
            {
                return;
            }

            NewGeneration();
            if (lastStartedInputId != null && DateTime.UtcNow - lastStartUtc < SuspectWindow)
            {
                suspectId = lastStartedInputId;
                RuntimeLog.Write("Microphone " + AudioInputDevices.ShortId(suspectId) + " failed within "
                    + SuspectWindow.TotalSeconds + " s of starting; using System Default until it is chosen again");
            }

            uiTimer.Stop();
            IsListening = false;
            SetPhase(VoicePhase.Off);
            HideHud();
            ReportUnavailable(error != null ? error.Message : null);
            ReadDevices();
            RebuildOptions();
            ScheduleRetry(TimeSpan.FromMilliseconds(1500));
            ApplyIdleText();
            UpdateMicrophoneReadouts();
        }

        // ---- Presentation -------------------------------------------------------------------------------

        private void SetPhase(VoicePhase value)
        {
            Phase = value;
            IsCommandWindowOpen = value == VoicePhase.Acknowledging || value == VoicePhase.Listening
                || value == VoicePhase.Recording || value == VoicePhase.Transcribing;
            IsAwaitingCommand = value == VoicePhase.Listening;
            if (!IsAwaitingCommand)
            {
                ListeningRemaining = 0;
                ListeningSecondsText = "";
            }

            if (value == VoicePhase.Off || value == VoicePhase.WakeOnly)
            {
                if (DateTime.UtcNow >= outcomeUntilUtc)
                {
                    ApplyIdleText();
                }
            }
        }

        private void ApplyIdleText()
        {
            if (assistantThinking) { return; }
            if (!isEnabled)
            {
                PhaseTitle = "VOICE OFF";
                PhaseDetail = "Switch on to listen for " + WakeWordQuoted;
                StatusText = "Off";
            }
            else if (phase == VoicePhase.Off)
            {
                // On, but the recognizer is not running: switching or recovering its input.
                PhaseTitle = reinitPending ? "SWITCHING MICROPHONE" : "MICROPHONE UNAVAILABLE";
                PhaseDetail = reinitPending ? "Restarting the recognizer…" : "Retrying automatically";
                StatusText = reinitPending ? "Switching microphone" : "Microphone unavailable";
            }
            else
            {
                PhaseTitle = "VOICE IDLE";
                PhaseDetail = "Listening for " + WakeWordQuoted;
                StatusText = "Listening for " + WakeWordQuoted;
            }

            PhaseState = VoiceHudState.Hidden;
        }

        private void ShowHud(VoiceHudState state, string text, string detail)
        {
            HudState = state;
            HudText = text;
            HudDetail = detail;
            HudVisible = true;
            PhaseState = state;

            switch (state)
            {
                case VoiceHudState.Wake:
                    PhaseTitle = "WAKE DETECTED";
                    PhaseDetail = "✦  chime — speak right after it";
                    StatusText = "Wake detected";
                    break;
                case VoiceHudState.Listening:
                    PhaseTitle = "LISTENING…";
                    PhaseDetail = "Say one command";
                    StatusText = "Listening for a command";
                    break;
                case VoiceHudState.Recording:
                case VoiceHudState.Transcribing:
                case VoiceHudState.Thinking:
                    PhaseTitle = text.ToUpperInvariant();
                    PhaseDetail = state == VoiceHudState.Recording ? "Speak now" : state == VoiceHudState.Transcribing ? "Local speech" : "One moment";
                    StatusText = text;
                    break;
            }
        }

        private void ShowOutcome(VoiceOutcome outcome, string detail)
        {
            string title;
            string text;
            VoiceHudState state = VoiceHudState.Rejected;

            switch (outcome)
            {
                case VoiceOutcome.Executed:
                    title = "COMMAND EXECUTED";
                    text = detail;
                    state = VoiceHudState.Executed;
                    break;
                case VoiceOutcome.TimedOut:
                    title = "TIMED OUT";
                    text = "No command heard";
                    break;
                case VoiceOutcome.Cancelled:
                    title = "CANCELLED";
                    text = "Nothing done";
                    break;
                case VoiceOutcome.SpokeTooEarly:
                    title = "WAIT FOR THE CHIME";
                    text = "Speak after the chime";
                    break;
                default:
                    title = "NOT RECOGNIZED";
                    text = "Say " + WakeWordQuoted + " to try again";
                    break;
            }

            PhaseTitle = title;
            PhaseDetail = outcome == VoiceOutcome.Executed ? detail : (string.IsNullOrEmpty(detail) ? text : text + " · " + detail);
            PhaseState = state;
            StatusText = outcome == VoiceOutcome.Executed ? detail : title.Substring(0, 1) + title.Substring(1).ToLowerInvariant();

            HudState = state;
            HudText = text;
            HudDetail = title;
            HudVisible = true;
            outcomeUntilUtc = DateTime.UtcNow + OutcomeDisplayTime;
            if (!uiTimer.IsEnabled && isEnabled)
            {
                uiTimer.Start();
            }
        }

        private void HideHud()
        {
            HudVisible = false;
            HudState = VoiceHudState.Hidden;
            HudText = "";
            HudDetail = "";
            outcomeUntilUtc = DateTime.MinValue;
            ApplyIdleText();
        }

        private void UiTimer_Tick(object sender, EventArgs e)
        {
            if (IsAwaitingCommand)
            {
                UpdateCountdown();
            }
            else if (hudVisible && !assistantThinking && !IsCommandWindowOpen && DateTime.UtcNow >= outcomeUntilUtc)
            {
                HudVisible = false;
                HudState = VoiceHudState.Hidden;
                ApplyIdleText();
            }

            double level = voice.AudioLevel / 100.0;
            AudioLevel = level;
            MicLevel = level;
            IsHearingSound = voice.IsHearingSound;

            if (++diagnosticsTick % 5 == 0)
            {
                UpdateDiagnostics();
            }
        }

        private void UpdateCountdown()
        {
            double total = (deadlineUtc - windowOpenedUtc).TotalSeconds;
            double left = (deadlineUtc - DateTime.UtcNow).TotalSeconds;
            if (total <= 0)
            {
                total = WakeGatedVoiceEngine.DefaultCommandWindow.TotalSeconds;
            }

            left = Math.Max(0, left);
            ListeningRemaining = Math.Max(0, Math.Min(1, left / total));
            ListeningSecondsText = left.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        }

        private void UpdateDiagnostics()
        {
            VoiceCounters c = voice.Counters;
            VoiceSession s = voice.CurrentSession;
            StringBuilder text = new StringBuilder();

            text.Append("State            ").Append(voice.Phase).AppendLine(isEnabled ? "" : " (voice off)");
            text.Append("Microphone       ").Append(selectedInputText).Append("   [").Append(microphoneStatusText).AppendLine("]");
            text.Append("Device ID        ").AppendLine(microphoneDeviceIdText);
            text.Append("Device available ").Append(microphoneAvailable ? "Yes" : "No")
                .Append("   Recognizer initialized ").Append(recognizerInitialized ? "Yes" : "No")
                .Append("   Device notifications ").AppendLine(watcher.IsActive ? "on" : "off (use Refresh)");
            text.Append("Input level      ").Append(recognizerInitialized ? (int)Math.Round(micLevel * 100) + "%" : "-")
                .Append("   input gain ").Append(hasMicGain ? (int)Math.Round(micGain) + "%" + (canControlMicGain ? "" : " (read-only)") : "-").AppendLine();
            text.Append("Wake word        ").Append(WakeWordQuoted)
                .Append("   custom commands listening ").Append(voice.ActiveCustomPhrases.Count).AppendLine();
            text.Append("Wake sensitivity ").Append((int)Math.Round(wakeSensitivity)).Append("  (").Append(WakeThresholdText)
                .Append(")   command ≥ ").Append(F2(commandThreshold)).Append("   window ").AppendLine(CommandWindowText);
            if (testMode)
            {
                text.AppendLine("TEST MODE        on - commands are measured, never executed");
            }

            if (s != null)
            {
                text.Append("Session          #").Append(s.Id).Append("  wake confidence ").Append(F2(s.WakeConfidence));
                if (s.IsGateOpen)
                {
                    double left = Math.Max(0, (s.DeadlineUtc - DateTime.UtcNow).TotalSeconds);
                    text.Append("  gate at ").Append(s.GatePosition.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture))
                        .Append(" s  deadline in ").Append(left.ToString("0.0", CultureInfo.InvariantCulture)).Append(" s");
                }
                else
                {
                    text.Append("  chime playing");
                }

                text.AppendLine();
            }
            else
            {
                text.AppendLine("Session          none");
            }

            text.Append("Last wake        ").Append(lastWakeText).Append("  ").AppendLine(lastWakeDetail);
            text.Append("Last command     ").Append(lastCommandText).Append("  ").AppendLine(lastCommandDetail);
            text.Append("Wakes ").Append(c.Wakes).Append("  executed ").Append(c.Executed).Append("  not recognized ").Append(c.NotRecognized)
                .Append("  timed out ").Append(c.TimedOut).Append("  cancelled ").Append(c.Cancelled).Append("  too early ").Append(c.SpokeTooEarly)
                .AppendLine();
            text.Append("Background utterances ignored ").Append(c.BackgroundUtterances).Append("  wake near-misses ")
                .Append(c.WakeCandidatesRejected).Append("  echo/noise ignored ").Append(c.EchoOrNoiseIgnored);

            DiagnosticsText = text.ToString();
        }

        /// <summary>
        /// For the UI smoke test's renders: puts the HUD into a state without any audio.
        /// </summary>
        internal void PreviewHud(VoiceHudState state)
        {
            isEnabled = true;
            Raise("IsEnabled");
            Raise("EnabledText");
            IsListening = true;
            switch (state)
            {
                case VoiceHudState.Recording:
                    SetPhase(VoicePhase.Recording);
                    ShowHud(state, "Hearing you…", "Speak your request");
                    MicLevel = 0.72;
                    break;
                case VoiceHudState.Transcribing:
                    SetPhase(VoicePhase.Transcribing);
                    ShowHud(state, "Understanding…", "Transcribing locally");
                    LastCommandText = "“put ChatGPT on screen two”";
                    MicLevel = 0.18;
                    break;
                case VoiceHudState.Thinking:
                    SetPhase(VoicePhase.WakeOnly);
                    ShowHud(state, "Thinking…", "“Put ChatGPT on screen two”");
                    LastCommandText = "“Put ChatGPT on screen two”";
                    break;
                case VoiceHudState.Listening:
                    SetPhase(VoicePhase.Listening);
                    ShowHud(VoiceHudState.Listening, "Listening… speak now", "Say a command");
                    ListeningRemaining = 0.62;
                    ListeningSecondsText = "2.5 s";
                    MicLevel = 0.58;
                    break;
                case VoiceHudState.Executed:
                    SetPhase(VoicePhase.WakeOnly);
                    ShowOutcome(VoiceOutcome.Executed, "Volume → 70%");
                    LastCommandText = "“volume seventy”";
                    LastCommandDetail = "Volume → 70% · confidence 0.91";
                    break;
                case VoiceHudState.Rejected:
                    SetPhase(VoicePhase.WakeOnly);
                    ShowOutcome(VoiceOutcome.NotRecognized, "Not a command");
                    LastCommandText = "“purple bananas”";
                    break;
                default:
                    SetPhase(VoicePhase.WakeOnly);
                    HideHud();
                    MicLevel = 0;
                    break;
            }

            // Show the microphone card as if the recognizer were running on the chosen input.
            MicrophoneStatus = MicrophoneStatus.Ready;
            MicrophoneStatusText = "Ready";
            uiTimer.Stop();
        }

        private static string F2(double value)
        {
            return double.IsNaN(value) ? "-" : value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        public void Shutdown()
        {
            NewGeneration();
            uiTimer.Stop();
            retryTimer.Stop();
            deviceRefreshTimer.Stop();
            customCommitTimer.Stop();
            if (customDirty)
            {
                customDirty = false;
                SaveCustomCommands();
            }

            watcher.Dispose();
            voice.Dispose();
            sounds.Dispose();
        }
    }
}
