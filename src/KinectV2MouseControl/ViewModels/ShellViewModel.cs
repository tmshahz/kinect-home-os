using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Threading;

namespace KinectV2MouseControl
{
    public enum ShellSection
    {
        Home,
        Gestures,
        Voice,
        Actions,
        Displays,
        Profiles,
        Settings,
        Intelligence
    }

    public class NavItem
    {
        public ShellSection Section { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string IconKey { get; set; }
        public string Badge { get; set; }
    }

    /// <summary>
    /// The KINECT-OS shell: navigation, the help drawer, the compact widget state, the recent
    /// activity feed, the voice and action pages, and the UI-only settings. It owns the engine
    /// view model but never touches the engine directly - everything that changes control goes
    /// through KinectCursorViewModel, which keeps the engine's release/reset paths intact.
    /// </summary>
    public class ShellViewModel : ObservableObject
    {
        private readonly Dispatcher dispatcher;

        public KinectCursorViewModel Engine { get; private set; }
        public VoiceViewModel Voice { get; private set; }
        public ActionsViewModel Actions { get; private set; }
        public DisplaysViewModel Displays { get; private set; }

        public LiveStatus Status
        {
            get
            {
                return Engine.Status;
            }
        }

        /// <summary>
        /// Raised when the shell wants the window to change shape: "compact", "expand", "quit".
        /// The window owns the actual Hide/Show calls.
        /// </summary>
        public event EventHandler<string> WindowRequest;

        /// <summary>
        /// Set by the window. Asked before destructive things (overwrite profile, restore
        /// defaults). Null means "yes".
        /// </summary>
        public Func<string, string, bool> Confirm { get; set; }

        public ShellViewModel(Dispatcher dispatcher)
        {
            this.dispatcher = dispatcher;

            Engine = new KinectCursorViewModel();
            Engine.ConfirmOverwrite = slot => Ask("Save profile",
                "Overwrite “" + slot.Name + "” with the current settings?");

            Voice = new VoiceViewModel(Engine, dispatcher);
            Voice.ShellCommandRequested += (s, command) => HandleShellCommand(command);
            Actions = new ActionsViewModel(Engine, HandleShellCommand);
            Displays = new DisplaysViewModel(Engine);

            Sections = new ObservableCollection<NavItem>
            {
                new NavItem { Section = ShellSection.Home, Title = "Home", Subtitle = "Status at a glance", IconKey = "IconHome" },
                new NavItem { Section = ShellSection.Gestures, Title = "Gestures", Subtitle = "Hand language & tuning", IconKey = "IconGestures" },
                new NavItem { Section = ShellSection.Voice, Title = "Voice", Subtitle = "Wake word first", IconKey = "IconVoice", Badge = "BETA" },
                new NavItem { Section = ShellSection.Actions, Title = "Actions", Subtitle = "What the system can do", IconKey = "IconActions" },
                new NavItem { Section = ShellSection.Displays, Title = "Displays", Subtitle = "Spatial setup & calibration", IconKey = "IconDisplays" },
                new NavItem { Section = ShellSection.Profiles, Title = "Profiles", Subtitle = "Saved tunings", IconKey = "IconProfiles" },
                new NavItem { Section = ShellSection.Settings, Title = "Settings", Subtitle = "System, help & diagnostics", IconKey = "IconSettings" },
                new NavItem { Section = ShellSection.Intelligence, Title = "AI", Subtitle = "Assistant layer", IconKey = "IconAi", Badge = "SOON" }
            };
            selectedSection = Sections[0];

            Activity = new ObservableCollection<ActivityEntry>();
            ActivityEntry[] existing = ActivityLog.Snapshot();
            for (int i = existing.Length - 1; i >= 0; i--)
            {
                Activity.Add(existing[i]);
            }

            ActivityLog.EntryAdded += ActivityLog_EntryAdded;
            ActivityLog.Cleared += ActivityLog_Cleared;
            HelpHub.Requested += HelpHub_Requested;

            NavigateCommand = new RelayCommand(p => Navigate(p));
            ToggleHelpCommand = new RelayCommand(() => IsHelpOpen = !IsHelpOpen);
            CloseHelpCommand = new RelayCommand(() => IsHelpOpen = false);
            ShowHelpCommand = new RelayCommand(p => HelpHub.Show(p as string, true));
            ToggleControlCommand = new RelayCommand(() => Engine.IsControlEnabled = !Engine.IsControlEnabled);
            CalibrateCommand = new RelayCommand(() => { Engine.ToggleCalibration(); Navigate(ShellSection.Displays); });
            RestoreDefaultsCommand = new RelayCommand(RestoreDefaults);
            EnterCompactCommand = new RelayCommand(() => RequestWindow("compact"));
            ExitCompactCommand = new RelayCommand(() => RequestWindow("expand"));
            QuitCommand = new RelayCommand(() => RequestWindow("quit"));
            ClearActivityCommand = new RelayCommand(() => ActivityLog.Clear());
            OpenLogFolderCommand = new RelayCommand(() => OpenPath(RuntimeLog.DirectoryPath));
            OpenLogCommand = new RelayCommand(() => OpenPath(RuntimeLog.FilePath));
            SelectModeCommand = new RelayCommand(p => SelectMode(p));

            LoadUiSettings();
        }

        // ---- Navigation --------------------------------------------------------------------------

        public ObservableCollection<NavItem> Sections { get; private set; }

        private NavItem selectedSection;
        public NavItem SelectedSection
        {
            get
            {
                return selectedSection;
            }
            set
            {
                if (value != null && Set(ref selectedSection, value))
                {
                    Raise("CurrentSection");
                    Raise("PageTitle");
                    Raise("PageSubtitle");
                }
            }
        }

        public ShellSection CurrentSection
        {
            get
            {
                return selectedSection.Section;
            }
        }

        public string PageTitle
        {
            get
            {
                return selectedSection.Title;
            }
        }

        public string PageSubtitle
        {
            get
            {
                return selectedSection.Subtitle;
            }
        }

        public RelayCommand NavigateCommand { get; private set; }

        public void Navigate(object target)
        {
            NavItem item = target as NavItem;
            if (item == null)
            {
                ShellSection section;
                if (target is ShellSection)
                {
                    section = (ShellSection)target;
                }
                else if (target is string && Enum.TryParse((string)target, true, out section))
                {
                    // parsed
                }
                else
                {
                    return;
                }

                for (int i = 0; i < Sections.Count; i++)
                {
                    if (Sections[i].Section == section)
                    {
                        item = Sections[i];
                        break;
                    }
                }
            }

            if (item != null)
            {
                SelectedSection = item;
            }
        }

        // ---- Header commands --------------------------------------------------------------------

        public RelayCommand ToggleControlCommand { get; private set; }
        public RelayCommand CalibrateCommand { get; private set; }
        public RelayCommand RestoreDefaultsCommand { get; private set; }
        public RelayCommand EnterCompactCommand { get; private set; }
        public RelayCommand ExitCompactCommand { get; private set; }
        public RelayCommand QuitCommand { get; private set; }
        public RelayCommand ClearActivityCommand { get; private set; }
        public RelayCommand OpenLogFolderCommand { get; private set; }
        public RelayCommand OpenLogCommand { get; private set; }
        public RelayCommand SelectModeCommand { get; private set; }

        private void SelectMode(object parameter)
        {
            int mode;
            if (parameter is int)
            {
                mode = (int)parameter;
            }
            else if (!(parameter is string) || !int.TryParse((string)parameter, out mode))
            {
                return;
            }

            if (Engine.ControlModeIndex != mode)
            {
                Engine.ControlModeIndex = mode;
            }
        }

        private void RestoreDefaults()
        {
            if (!Ask("Restore defaults", "Reset every tuning value to the built-in defaults? Your saved profiles are not touched."))
            {
                return;
            }

            Engine.ResetToDefault();
        }

        private bool Ask(string title, string question)
        {
            return Confirm == null || Confirm(title, question);
        }

        private void RequestWindow(string request)
        {
            EventHandler<string> handler = WindowRequest;
            if (handler != null)
            {
                handler.Invoke(this, request);
            }
        }

        /// <summary>
        /// Commands that act on the shell itself, from voice or the Actions page.
        /// </summary>
        public void HandleShellCommand(string command)
        {
            switch (command)
            {
                case "open":
                    RequestWindow("expand");
                    break;
                case "compact":
                    RequestWindow("compact");
                    break;
                case "calibrate":
                    if (!Engine.IsCalibrating)
                    {
                        Engine.ToggleCalibration();
                    }

                    RequestWindow("expand");
                    Navigate(ShellSection.Displays);
                    break;
            }
        }

        private static void OpenPath(string path)
        {
            try
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("Open path failed: " + ex.Message);
            }
        }

        // ---- Compact mode ----------------------------------------------------------------------

        private bool isCompact;

        /// <summary>
        /// True while the main window is hidden and the floating widget is showing. Set by the
        /// window, which owns the actual transition.
        /// </summary>
        public bool IsCompact
        {
            get
            {
                return isCompact;
            }
            set
            {
                Set(ref isCompact, value);
            }
        }

        private bool compactOnMinimize = true;

        /// <summary>
        /// Minimizing the window switches to the floating widget instead of the taskbar.
        /// </summary>
        public bool CompactOnMinimize
        {
            get
            {
                return compactOnMinimize;
            }
            set
            {
                Set(ref compactOnMinimize, value);
            }
        }

        private bool startCompact;

        /// <summary>
        /// Launch straight into the floating widget.
        /// </summary>
        public bool StartCompact
        {
            get
            {
                return startCompact;
            }
            set
            {
                Set(ref startCompact, value);
            }
        }

        private bool overlayAlwaysOnTop = true;
        public bool OverlayAlwaysOnTop
        {
            get
            {
                return overlayAlwaysOnTop;
            }
            set
            {
                Set(ref overlayAlwaysOnTop, value);
            }
        }

        public double OverlayLeft { get; set; }
        public double OverlayTop { get; set; }

        // ---- Help drawer --------------------------------------------------------------------------

        public RelayCommand ToggleHelpCommand { get; private set; }
        public RelayCommand CloseHelpCommand { get; private set; }
        public RelayCommand ShowHelpCommand { get; private set; }

        private bool isHelpOpen;
        public bool IsHelpOpen
        {
            get
            {
                return isHelpOpen;
            }
            set
            {
                Set(ref isHelpOpen, value);
            }
        }

        private HelpEntry helpTopic;
        public HelpEntry HelpTopic
        {
            get
            {
                return helpTopic;
            }
            private set
            {
                if (Set(ref helpTopic, value))
                {
                    Raise("HasHelpTopic");
                    Raise("HelpTitle");
                    Raise("HelpWhat");
                    Raise("HelpHigher");
                    Raise("HelpLower");
                    Raise("HelpTooHigh");
                    Raise("HelpTooLow");
                    Raise("HasHelpScale");
                }
            }
        }

        public bool HasHelpTopic { get { return helpTopic != null; } }
        public string HelpTitle { get { return helpTopic == null ? "How to use KINECT-OS" : helpTopic.Title; } }
        public string HelpWhat { get { return helpTopic == null ? ControlHelp.Overview : helpTopic.What; } }
        public string HelpHigher { get { return helpTopic == null ? null : helpTopic.Increase; } }
        public string HelpLower { get { return helpTopic == null ? null : helpTopic.Decrease; } }
        public string HelpTooHigh { get { return helpTopic == null ? null : helpTopic.TooHigh; } }
        public string HelpTooLow { get { return helpTopic == null ? null : helpTopic.TooLow; } }
        public bool HasHelpScale { get { return helpTopic != null && helpTopic.Increase != null; } }

        public HelpEntry[] AllHelpEntries
        {
            get
            {
                return ControlHelp.Entries.ToArray();
            }
        }

        public string HelpOverview
        {
            get
            {
                return ControlHelp.Overview;
            }
        }

        private void HelpHub_Requested(object sender, HelpHub.HelpRequest e)
        {
            if (e.IsExplicit)
            {
                HelpTopic = e.Entry;
                IsHelpOpen = true;
            }
            else if (isHelpOpen)
            {
                HelpTopic = e.Entry;
            }
        }

        // ---- Activity ---------------------------------------------------------------------------

        /// <summary>
        /// Newest first, capped at ActivityLog.Capacity.
        /// </summary>
        public ObservableCollection<ActivityEntry> Activity { get; private set; }

        private void ActivityLog_EntryAdded(object sender, ActivityEntry entry)
        {
            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => AddActivity(entry)));
            }
            else
            {
                AddActivity(entry);
            }
        }

        private void AddActivity(ActivityEntry entry)
        {
            Activity.Insert(0, entry);
            while (Activity.Count > ActivityLog.Capacity)
            {
                Activity.RemoveAt(Activity.Count - 1);
            }

            Raise("LatestActivity");
        }

        private void ActivityLog_Cleared(object sender, EventArgs e)
        {
            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => Activity.Clear()));
            }
            else
            {
                Activity.Clear();
            }
        }

        public ActivityEntry LatestActivity
        {
            get
            {
                return Activity.Count > 0 ? Activity[0] : null;
            }
        }

        // ---- About ------------------------------------------------------------------------------

        public string AppVersion
        {
            get
            {
                return Assembly.GetExecutingAssembly().GetName().Version.ToString();
            }
        }

        public string BuildConfiguration
        {
            get
            {
#if DEBUG
                return "Debug";
#else
                return "Release";
#endif
            }
        }

        public string ExecutablePath
        {
            get
            {
                return Assembly.GetExecutingAssembly().Location;
            }
        }

        public string LogPath
        {
            get
            {
                return RuntimeLog.FilePath;
            }
        }

        public string ProfilePath
        {
            get
            {
                return ProfileStore.FilePath;
            }
        }

        public string OsVersion
        {
            get
            {
                return Environment.OSVersion.VersionString;
            }
        }

        // ---- UI settings ------------------------------------------------------------------------

        private void LoadUiSettings()
        {
            Properties.Settings s = Properties.Settings.Default;
            compactOnMinimize = s.CompactOnMinimize;
            startCompact = s.StartCompact;
            overlayAlwaysOnTop = s.OverlayAlwaysOnTop;
            OverlayLeft = s.OverlayLeft;
            OverlayTop = s.OverlayTop;
            // Wake Sensitivity is a 0-100 scale. A value at or below 1 is a legacy 0-1 fraction
            // (an earlier default), so scale it up rather than reading it as ~1%.
            double sensitivity = s.VoiceWakeSensitivity;
            Voice.WakeSensitivity = sensitivity <= 1.0 ? sensitivity * 100.0 : sensitivity;
            Voice.CommandThreshold = s.VoiceCommandThreshold;
            Voice.DismissSound = s.VoiceDismissSound;
            VoiceSpeechEngine speechEngine;
            Voice.SpeechEngine = Enum.TryParse(s.VoiceSpeechEngine, out speechEngine) ? speechEngine : VoiceSpeechEngine.Whisper;
            Voice.ListeningClick = s.VoiceListeningClick;
            Voice.SetWakeWord(s.VoiceWakePhrase);
            Voice.SetMicrophonePreference(s.VoiceInputDeviceId, s.VoiceInputDeviceName);
        }

        /// <summary>
        /// Voice is started after the window is up rather than in the constructor, so a
        /// microphone problem can never delay or break the window appearing.
        /// </summary>
        public void ApplyDeferredStartup()
        {
            if (Properties.Settings.Default.VoiceEnabled)
            {
                Voice.IsEnabled = true;
            }
        }

        public void SaveUiSettings()
        {
            Properties.Settings s = Properties.Settings.Default;
            s.CompactOnMinimize = compactOnMinimize;
            s.StartCompact = startCompact;
            s.OverlayAlwaysOnTop = overlayAlwaysOnTop;
            s.OverlayLeft = OverlayLeft;
            s.OverlayTop = OverlayTop;
            s.VoiceEnabled = Voice.IsEnabled;
            s.VoiceWakeSensitivity = Voice.WakeSensitivity;
            s.VoiceCommandThreshold = Voice.CommandThreshold;
            s.VoiceDismissSound = Voice.DismissSound;
            s.VoiceSpeechEngine = Voice.SpeechEngine.ToString();
            s.VoiceListeningClick = Voice.ListeningClick;
            s.VoiceWakePhrase = Voice.WakeWord;
            s.VoiceInputDeviceId = Voice.PreferredMicrophoneId;
            s.VoiceInputDeviceName = Voice.PreferredMicrophoneName;
        }

        /// <summary>
        /// Orderly shutdown: voice off, UI settings captured, then the engine's own Quit, which
        /// saves the tuning and closes the sensor through the normal release path.
        /// </summary>
        public void Quit()
        {
            Voice.Shutdown();
            SaveUiSettings();
            ActivityLog.EntryAdded -= ActivityLog_EntryAdded;
            ActivityLog.Cleared -= ActivityLog_Cleared;
            HelpHub.Requested -= HelpHub_Requested;
            Engine.Quit();
        }
    }
}
