using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
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
        Executed,
        Rejected
    }

    /// <summary>
    /// The Voice page and the voice HUD: enable switch, sensitivity, the live wake-cycle state,
    /// the command list and the developer diagnostics.
    ///
    /// This is the only place a voice command turns into an action, and it only ever does so
    /// for a CommandRecognized event from the wake-gated engine - which the engine raises only
    /// for a phrase heard inside an open command window - after marking that wake session
    /// executed. Everything is marshalled to the UI thread first, so the ActionRouter is
    /// called from the thread that owns the control engine.
    /// </summary>
    public class VoiceViewModel : ObservableObject
    {
        private static readonly TimeSpan OutcomeDisplayTime = TimeSpan.FromMilliseconds(2600);
        private const int MaxDecisions = 14;

        private readonly KinectCursorViewModel engine;
        private readonly Dispatcher dispatcher;
        private readonly WakeGatedVoiceEngine voice;
        private readonly VoiceFeedbackSounds sounds;
        private readonly DispatcherTimer uiTimer;

        private DateTime deadlineUtc;
        private DateTime windowOpenedUtc;
        private DateTime outcomeUntilUtc;
        private int diagnosticsTick;

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
            voice.PhaseChanged += (s, e) => dispatcher.BeginInvoke(new Action(() => OnPhaseChanged(e)));
            voice.CommandRecognized += (s, e) => dispatcher.BeginInvoke(new Action(() => OnCommandRecognized(e)));
            voice.DecisionMade += (s, e) => dispatcher.BeginInvoke(new Action(() => OnDecision(e)));
            voice.Stopped += (s, e) => dispatcher.BeginInvoke(new Action(() => OnStopped(e)));

            backendText = WakeGatedVoiceEngine.DescribeAvailability();
            Decisions = new ObservableCollection<VoiceDecision>();
            BuildGroups();
            RefreshMicrophones();

            uiTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
            uiTimer.Interval = TimeSpan.FromMilliseconds(50);
            uiTimer.Tick += UiTimer_Tick;

            ToggleCommand = new RelayCommand(() => IsEnabled = !IsEnabled);
            RefreshMicrophonesCommand = new RelayCommand(RefreshMicrophones);
            ClearDiagnosticsCommand = new RelayCommand(() => Decisions.Clear());
            ApplyIdleText();
        }

        public RelayCommand ToggleCommand { get; private set; }
        public RelayCommand RefreshMicrophonesCommand { get; private set; }
        public RelayCommand ClearDiagnosticsCommand { get; private set; }

        public List<VoiceCommandGroup> Groups { get; private set; }

        public int AvailableCount { get; private set; }
        public int PlannedCount { get; private set; }

        public string WakeWord
        {
            get
            {
                return VoiceCommandCatalog.WakeWord;
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
                    voice.Stop();
                    uiTimer.Stop();
                    ErrorText = "";
                    SetPhase(VoicePhase.Off);
                    HideHud();
                    ActivityLog.Post(ActivityKind.Voice, "Voice commands off", null, "control center");
                }

                IsListening = isEnabled && voice.IsRunning;
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
            voice.WakeThreshold = wakeThreshold;
            voice.CommandThreshold = commandThreshold;
            voice.DismissSoundEnabled = dismissSound;

            string error;
            if (!voice.Start(out error))
            {
                ErrorText = error ?? "Could not start listening";
                isEnabled = false;
                Raise("IsEnabled");
                Raise("EnabledText");
                SetPhase(VoicePhase.Off);
                ActivityLog.Post(ActivityKind.Voice, "Voice could not start", error, "control center");
                return;
            }

            ErrorText = "";
            BackendText = voice.BackendName;
            uiTimer.Start();
            ActivityLog.Post(ActivityKind.Voice, "Voice commands on", "Say “Kinect”, wait for the chime, then the command", "control center");
        }

        // ---- Sensitivity -----------------------------------------------------------------------

        private double wakeThreshold = WakeGatedVoiceEngine.DefaultWakeThreshold;

        /// <summary>
        /// Minimum recognizer confidence for "Kinect" to open a command window.
        /// </summary>
        public double WakeThreshold
        {
            get
            {
                return wakeThreshold;
            }
            set
            {
                double clamped = Math.Round(Math.Max(0.30, Math.Min(0.99, value)), 2);
                if (Set(ref wakeThreshold, clamped))
                {
                    voice.WakeThreshold = clamped;
                }
            }
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

        private string microphoneSummary = "";
        public string MicrophoneSummary { get { return microphoneSummary; } set { Set(ref microphoneSummary, value); } }

        private string microphoneList = "";
        public string MicrophoneList { get { return microphoneList; } set { Set(ref microphoneList, value); } }

        private bool hasMicrophone;
        public bool HasMicrophone { get { return hasMicrophone; } set { Set(ref hasMicrophone, value); } }

        public void RefreshMicrophones()
        {
            string[] names = AudioInputDevices.GetNames();
            HasMicrophone = names.Length > 0;
            MicrophoneSummary = names.Length == 0
                ? "No microphone detected"
                : names.Length + (names.Length == 1 ? " input device" : " input devices") + " · Windows default is used";
            MicrophoneList = names.Length == 0 ? "" : string.Join("\n", names);
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

                    ShowHud(VoiceHudState.Listening, "Listening…", "Say a command");
                    UpdateCountdown();
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
                    if (!string.IsNullOrEmpty(e.Detail))
                    {
                        ErrorText = "Voice stopped: " + e.Detail;
                    }
                    break;
            }

            IsListening = isEnabled && e.Phase != VoicePhase.Off;
        }

        private void OnCommandRecognized(VoiceCommandEventArgs e)
        {
            // Switched off between recognition and now: do nothing.
            if (!isEnabled || e.Session == null || e.Intent == null)
            {
                return;
            }

            // One wake, one action - even if this event were somehow delivered twice.
            if (!e.Session.TryMarkExecuted())
            {
                return;
            }

            VoiceIntent intent = e.Intent;
            bool done = false;

            switch (intent.Kind)
            {
                case VoiceIntentKind.Action:
                    done = engine.ExecuteAction(intent.Action, "voice");
                    break;

                case VoiceIntentKind.Shell:
                    ActivityLog.Post(ActivityKind.Voice, intent.Feedback, "“" + e.Phrase + "”", "voice");
                    EventHandler<string> handler = ShellCommandRequested;
                    if (handler != null)
                    {
                        handler.Invoke(this, intent.ShellCommand);
                    }

                    done = true;
                    break;
            }

            LastCommandText = "“" + e.Phrase + "”";
            LastCommandDetail = intent.Feedback + " · confidence " + F2(e.Confidence) + (done ? "" : " · failed");
            RuntimeLog.Write("Voice command #" + e.Session.Id + " '" + e.Phrase + "' -> " + intent.Feedback
                + (done ? "" : " (FAILED)") + " (confidence " + F2(e.Confidence) + ")");

            if (done)
            {
                ShowOutcome(VoiceOutcome.Executed, intent.Feedback);
            }
            else
            {
                ShowOutcome(VoiceOutcome.NotRecognized, intent.Feedback + " could not be carried out");
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
            }
            else if (decision.Stage == "command" && decision.Verdict == VoiceVerdict.Rejected)
            {
                LastCommandText = decision.Phrase.Length > 0 ? "“" + decision.Phrase + "”" : "Not a command";
                LastCommandDetail = "rejected · " + decision.Reason;
            }

            UpdateDiagnostics();
        }

        private void OnStopped(Exception error)
        {
            if (!isEnabled)
            {
                return;
            }

            isEnabled = false;
            Raise("IsEnabled");
            Raise("EnabledText");
            uiTimer.Stop();
            IsListening = false;
            SetPhase(VoicePhase.Off);
            HideHud();
            ErrorText = error != null ? "Voice stopped: " + error.Message : "Voice stopped: the microphone input ended";
            ActivityLog.Post(ActivityKind.Voice, "Voice stopped", ErrorText, "voice");
        }

        // ---- Presentation -------------------------------------------------------------------------------

        private void SetPhase(VoicePhase value)
        {
            Phase = value;
            IsCommandWindowOpen = value == VoicePhase.Acknowledging || value == VoicePhase.Listening;
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
            if (phase == VoicePhase.Off || !isEnabled)
            {
                PhaseTitle = "VOICE OFF";
                PhaseDetail = "Switch on to listen for “Kinect”";
                StatusText = "Off";
            }
            else
            {
                PhaseTitle = "VOICE IDLE";
                PhaseDetail = "Listening for “Kinect”";
                StatusText = "Listening for “Kinect”";
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
                    text = "Say “Kinect” to try again";
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
            else if (hudVisible && !IsCommandWindowOpen && DateTime.UtcNow >= outcomeUntilUtc)
            {
                HudVisible = false;
                HudState = VoiceHudState.Hidden;
                ApplyIdleText();
            }

            AudioLevel = voice.AudioLevel / 100.0;
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
            text.Append("Thresholds       wake ").Append(F2(wakeThreshold)).Append("   command ").Append(F2(commandThreshold))
                .Append("   window ").AppendLine(CommandWindowText);

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
                case VoiceHudState.Listening:
                    SetPhase(VoicePhase.Listening);
                    ShowHud(VoiceHudState.Listening, "Listening…", "Say a command");
                    ListeningRemaining = 0.62;
                    ListeningSecondsText = "2.5 s";
                    break;
                case VoiceHudState.Executed:
                    SetPhase(VoicePhase.WakeOnly);
                    ShowOutcome(VoiceOutcome.Executed, "Volume → 70%");
                    LastCommandText = "“volume seventy”";
                    LastCommandDetail = "Volume → 70% · confidence 0.91";
                    break;
                default:
                    SetPhase(VoicePhase.WakeOnly);
                    HideHud();
                    break;
            }

            uiTimer.Stop();
        }

        private static string F2(double value)
        {
            return double.IsNaN(value) ? "-" : value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        public void Shutdown()
        {
            uiTimer.Stop();
            voice.Dispose();
            sounds.Dispose();
        }
    }
}
