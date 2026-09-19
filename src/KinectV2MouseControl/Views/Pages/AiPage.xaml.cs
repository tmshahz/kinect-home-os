using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

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
            Loaded += AiPage_Loaded;
        }

        private void AiPage_Loaded(object sender, RoutedEventArgs e)
        {
            ShellViewModel shell = DataContext as ShellViewModel;
            if (shell == null)
            {
                return;
            }

            shell.Assistant.Steps.CollectionChanged -= Steps_CollectionChanged;
            shell.Assistant.Steps.CollectionChanged += Steps_CollectionChanged;
        }

        private void Steps_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (StepsScroll == null)
            {
                return;
            }

            StepsScroll.Dispatcher.BeginInvoke(new System.Action(() => StepsScroll.ScrollToEnd()),
                DispatcherPriority.Loaded);
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
