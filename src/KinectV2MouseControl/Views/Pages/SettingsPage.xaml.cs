using System.Windows.Controls;

namespace KinectV2MouseControl
{
    public partial class SettingsPage : UserControl
    {
        public SettingsPage()
        {
            InitializeComponent();
            HelpBinding.Attach(CompactOnMinimizeCheck, "Minimize to the floating widget");
            HelpBinding.Attach(StartCompactCheck, "Start in compact mode");
            HelpBinding.Attach(OverlayTopCheck, "Keep the widget above other windows");
        }
    }
}
