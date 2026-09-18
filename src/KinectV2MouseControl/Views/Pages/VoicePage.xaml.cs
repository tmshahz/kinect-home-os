using System.Windows.Controls;
using System.Windows.Input;

namespace KinectV2MouseControl
{
    public partial class VoicePage : UserControl
    {
        public VoicePage()
        {
            InitializeComponent();
            HelpBinding.Attach(VoiceToggle, "Voice commands");
            HelpBinding.Attach(WakeWordBox, "Wake word");
        }

        private void WakeWordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                WakeWordBox.GetBindingExpression(TextBox.TextProperty).UpdateSource();
                e.Handled = true;
            }
        }
    }
}
