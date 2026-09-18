using System;
using System.Collections.Generic;
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
    /// The Voice page: microphone and recognizer status, the enable switch, the wake word and
    /// the command list. Recognition results arrive on a worker thread and are marshalled to
    /// the UI thread here before anything touches the engine, so the router is only ever
    /// called from the thread that owns the engine's state.
    /// </summary>
    public class VoiceViewModel : ObservableObject
    {
        private readonly KinectCursorViewModel engine;
        private readonly Dispatcher dispatcher;
        private readonly IVoiceEngine voice;

        /// <summary>
        /// Commands the shell carries out itself ("open", "compact", "calibrate").
        /// </summary>
        public event EventHandler<string> ShellCommandRequested;

        public VoiceViewModel(KinectCursorViewModel engine, Dispatcher dispatcher)
        {
            this.engine = engine;
            this.dispatcher = dispatcher;
            voice = new LocalVoiceEngine();
            voice.Recognized += Voice_Recognized;
            voice.StateChanged += Voice_StateChanged;

            wakeWord = VoiceCommandCatalog.DefaultWakeWord;
            backendText = LocalVoiceEngine.DescribeAvailability();
            BuildGroups();
            RefreshMicrophones();

            ToggleCommand = new RelayCommand(() => IsEnabled = !IsEnabled);
            RefreshMicrophonesCommand = new RelayCommand(RefreshMicrophones);
        }

        public RelayCommand ToggleCommand { get; private set; }
        public RelayCommand RefreshMicrophonesCommand { get; private set; }

        public List<VoiceCommandGroup> Groups { get; private set; }

        public int AvailableCount { get; private set; }
        public int PlannedCount { get; private set; }

        private void BuildGroups()
        {
            Groups = new List<VoiceCommandGroup>();
            ActionCategory[] order = new ActionCategory[]
            {
                ActionCategory.System, ActionCategory.Windows, ActionCategory.WindowManagement,
                ActionCategory.Pointer, ActionCategory.Media, ActionCategory.Apps, ActionCategory.Intelligence
            };

            for (int i = 0; i < order.Length; i++)
            {
                List<VoiceCommand> commands = new List<VoiceCommand>();
                for (int c = 0; c < VoiceCommandCatalog.BuiltIn.Length; c++)
                {
                    VoiceCommand command = VoiceCommandCatalog.BuiltIn[c];
                    if (command.Category == order[i])
                    {
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

        // ---- State ----------------------------------------------------------------------------

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
                    ErrorText = "";
                    ActivityLog.Post(ActivityKind.Voice, "Voice commands off", null, "control center");
                }

                RefreshState();
            }
        }

        public string EnabledText
        {
            get
            {
                return isEnabled ? "Listening" : "Off";
            }
        }

        private string wakeWord;

        /// <summary>
        /// Word said before every command. Empty means commands are recognized bare, which is
        /// convenient but far more prone to false triggers from conversation.
        /// </summary>
        public string WakeWord
        {
            get
            {
                return wakeWord;
            }
            set
            {
                string trimmed = (value ?? "").Trim();
                if (Set(ref wakeWord, trimmed))
                {
                    Raise("WakeWordExample");
                    if (isEnabled)
                    {
                        StartListening();
                    }
                }
            }
        }

        public string WakeWordExample
        {
            get
            {
                return string.IsNullOrEmpty(wakeWord)
                    ? "Say a command on its own, e.g. “next window”"
                    : "Say “" + wakeWord + ", next window”";
            }
        }

        private string statusText = "Off";
        public string StatusText { get { return statusText; } set { Set(ref statusText, value); } }

        private string backendText = "";
        public string BackendText { get { return backendText; } set { Set(ref backendText, value); } }

        private bool isListening;
        public bool IsListening { get { return isListening; } set { Set(ref isListening, value); } }

        private bool isHearingSpeech;
        public bool IsHearingSpeech { get { return isHearingSpeech; } set { Set(ref isHearingSpeech, value); } }

        private string lastHeardText = "Nothing heard yet";
        public string LastHeardText { get { return lastHeardText; } set { Set(ref lastHeardText, value); } }

        private string lastHeardDetail = "";
        public string LastHeardDetail { get { return lastHeardDetail; } set { Set(ref lastHeardDetail, value); } }

        private string errorText = "";
        public string ErrorText { get { return errorText; } set { if (Set(ref errorText, value)) { Raise("HasError"); } } }

        public bool HasError
        {
            get
            {
                return !string.IsNullOrEmpty(errorText);
            }
        }

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

        private void StartListening()
        {
            string error;
            if (!voice.Start(VoiceCommandCatalog.BuiltIn, wakeWord, out error))
            {
                ErrorText = error ?? "Could not start listening";
                isEnabled = false;
                Raise("IsEnabled");
                Raise("EnabledText");
                ActivityLog.Post(ActivityKind.Voice, "Voice could not start", error, "control center");
                return;
            }

            ErrorText = "";
            BackendText = voice.BackendName;
            ActivityLog.Post(ActivityKind.Voice, "Voice commands on",
                string.IsNullOrEmpty(wakeWord) ? "No wake word" : "Wake word “" + wakeWord + "”", "control center");
        }

        private void RefreshState()
        {
            StatusText = isEnabled ? voice.StatusText : "Off";
            IsListening = isEnabled && voice.IsRunning;
            IsHearingSpeech = IsListening && voice.StatusText == "Hearing speech";
        }

        private void Voice_StateChanged(object sender, EventArgs e)
        {
            dispatcher.BeginInvoke(new Action(RefreshState));
        }

        private void Voice_Recognized(object sender, VoiceRecognizedEventArgs e)
        {
            dispatcher.BeginInvoke(new Action(() => HandleRecognized(e)));
        }

        private void HandleRecognized(VoiceRecognizedEventArgs e)
        {
            string confidence = (e.Confidence * 100).ToString("0") + "%";

            if (e.Command == null)
            {
                LastHeardText = string.IsNullOrEmpty(e.Text) ? "Didn't catch that" : "“" + e.Text + "”";
                LastHeardDetail = "Not confident enough (" + confidence + ")";
                return;
            }

            LastHeardText = "“" + e.Text + "”";
            LastHeardDetail = e.Command.Action.Name + " · " + confidence;
            RuntimeLog.Write("Voice: '" + e.Text + "' -> " + e.Command.Id + " (" + confidence + ")");

            if (!string.IsNullOrEmpty(e.Command.Action.ShellCommand))
            {
                ActivityLog.Post(ActivityKind.Voice, e.Command.Action.Name, "“" + e.Text + "”", "voice");
                EventHandler<string> handler = ShellCommandRequested;
                if (handler != null)
                {
                    handler.Invoke(this, e.Command.Action.ShellCommand);
                }

                return;
            }

            engine.ExecuteAction(e.Command.Action.Action, "voice");
        }

        public void Shutdown()
        {
            voice.Recognized -= Voice_Recognized;
            voice.StateChanged -= Voice_StateChanged;
            voice.Dispose();
        }
    }
}
