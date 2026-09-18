using System.Windows.Controls;

namespace KinectV2MouseControl
{
    public partial class VoicePage : UserControl
    {
        public VoicePage()
        {
            InitializeComponent();
            HelpBinding.Attach(VoiceToggle, "Voice commands");
            HelpBinding.Attach(DismissSoundCheck, "Dismiss sound");
        }
    }
}
