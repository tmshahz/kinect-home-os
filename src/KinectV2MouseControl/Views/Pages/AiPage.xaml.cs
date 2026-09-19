using System.Windows.Controls;
using System.Windows;

namespace KinectV2MouseControl
{
    public partial class AiPage : UserControl
    {
        public AiPage()
        {
            InitializeComponent();
            HelpBinding.Attach(KeyBox, "DeepSeek key");
            HelpBinding.Attach(TestKeyButton, "DeepSeek key");
            HelpBinding.Attach(RemoveKeyButton, "DeepSeek key");
            HelpBinding.Attach(ModelBox, "Assistant model");
            HelpBinding.Attach(OtherRequestsCheck, "Send other requests to AI");
            HelpBinding.Attach(RequestBox, "Assistant request");
            HelpBinding.Attach(SendButton, "Assistant request");
            HelpBinding.Attach(CancelButton, "Cancel assistant");
        }

        private async void TestKey_Click(object sender, RoutedEventArgs e)
        {
            ShellViewModel shell = DataContext as ShellViewModel;
            if (shell == null) { return; }
            string key = KeyBox.Password;
            KeyBox.Clear();
            await shell.Assistant.TestAndSaveKeyAsync(key);
        }
    }
}
