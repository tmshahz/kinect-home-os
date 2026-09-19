using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace KinectV2MouseControl
{
    public partial class VoicePage : UserControl
    {
        public VoicePage()
        {
            InitializeComponent();
            HelpBinding.Attach(VoiceToggle, "Voice commands");
            HelpBinding.Attach(SpeechEngineBox, "Speech engine");
            HelpBinding.Attach(ListeningClickCheck, "End of listening sound");
            HelpBinding.Attach(DismissSoundCheck, "Dismiss sound");
            HelpBinding.Attach(MicrophoneBox, "Microphone");
            HelpBinding.Attach(TestWakeButton, "Test wake word");
            HelpBinding.Attach(WakeWordBox, "Wake word");
            HelpBinding.Attach(AddCommandButton, "Custom commands");
        }

        private VoiceViewModel Voice
        {
            get
            {
                ShellViewModel shell = DataContext as ShellViewModel;
                return shell != null ? shell.Voice : null;
            }
        }

        /// <summary>
        /// Offers the apps that are running right now in the "only in" dropdown.
        /// </summary>
        private void AppChoices_DropDownOpened(object sender, EventArgs e)
        {
            FrameworkElement element = sender as FrameworkElement;
            VoiceViewModel voice = Voice;
            if (element != null && voice != null)
            {
                voice.RefreshRunningApps(element.DataContext as CustomCommandRowViewModel);
            }
        }

        /// <summary>
        /// Picks a program, shortcut or file for an "open" command. Shortcuts are kept as the .lnk
        /// itself (not its target), so apps that launch through their shortcut still work.
        /// </summary>
        private void BrowseTarget_Click(object sender, RoutedEventArgs e)
        {
            FrameworkElement element = sender as FrameworkElement;
            CustomCommandRowViewModel row = element != null ? element.DataContext as CustomCommandRowViewModel : null;
            if (row == null)
            {
                return;
            }

            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Title = "Choose what “" + (row.Phrase.Length > 0 ? row.Phrase : "this command") + "” opens";
            dialog.Filter = "Programs and shortcuts (*.exe;*.lnk;*.url)|*.exe;*.lnk;*.url|All files (*.*)|*.*";
            dialog.DereferenceLinks = false;
            dialog.CheckFileExists = true;

            string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
            string programs = Path.Combine(startMenu, "Programs");
            if (row.Target.Length > 0 && File.Exists(row.Target))
            {
                dialog.InitialDirectory = Path.GetDirectoryName(row.Target);
            }
            else if (Directory.Exists(programs))
            {
                dialog.InitialDirectory = programs;
            }

            Window owner = Window.GetWindow(this);
            bool? chosen = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
            if (chosen == true)
            {
                row.Target = dialog.FileName;
            }
        }
    }
}
