using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace KinectV2MouseControl
{
    /// <summary>
    /// The compact, always-available face of KINECT-OS: a floating pill that shows whether
    /// control is live, what the hands are doing and the last thing that happened, plus an
    /// optional slide-down chat panel. Shown when the control center is hidden. Drag the pill
    /// anywhere; double-click or the expand button brings the full window back.
    ///
    /// The window is created with ShowActivated=false so it does not steal keystrokes from the
    /// user's work. While the chat panel is open, a click into the request box calls Activate
    /// so typing can reach the TextBox; the rest of the widget still does not grab focus on its
    /// own.
    /// </summary>
    public partial class OverlayWindow : Window
    {
        private readonly ShellViewModel shell;

        public event EventHandler ExpandRequested;

        public OverlayWindow(ShellViewModel shell)
        {
            this.shell = shell;
            DataContext = shell;
            InitializeComponent();

            HelpBinding.Attach(AskButton, "Ask KINECT-OS");
            HelpBinding.Attach(ChatToggle, "Widget chat panel");
            HelpBinding.Attach(ChatPanel, "Widget chat panel");
            HelpBinding.Attach(ChatRequestBox, "Manual request");

            shell.Assistant.PropertyChanged += Assistant_PropertyChanged;
            shell.Assistant.Steps.CollectionChanged += Steps_CollectionChanged;
            Closed += OverlayWindow_Closed;
        }

        private void OverlayWindow_Closed(object sender, EventArgs e)
        {
            shell.Assistant.PropertyChanged -= Assistant_PropertyChanged;
            shell.Assistant.Steps.CollectionChanged -= Steps_CollectionChanged;
        }

        /// <summary>
        /// Shows the widget at the remembered position, or bottom-right of the primary work
        /// area the first time, always pulled back onto a visible screen.
        /// </summary>
        public void ShowAt(double left, double top)
        {
            // Position before showing so the widget never flashes at a default spot. The
            // size is only known after the first layout, so estimate, show, then correct.
            WindowStartupLocation = WindowStartupLocation.Manual;
            double width = ActualWidth > 0 ? ActualWidth : 400;
            double height = ActualHeight > 0 ? ActualHeight : 100;
            Place(left, top, width, height);

            Show();
            UpdateLayout();

            if (ActualWidth > 0 && ActualHeight > 0 && (ActualWidth != width || ActualHeight != height))
            {
                Place(left, top, ActualWidth, ActualHeight);
            }
        }

        private void Place(double left, double top, double width, double height)
        {
            Rect work = SystemParameters.WorkArea;
            if (left < 0 && top < 0)
            {
                left = work.Right - width - 8;
                top = work.Bottom - height - 8;
            }

            Rect virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

            left = Math.Max(virtualScreen.Left - 16, Math.Min(virtualScreen.Right - width + 16, left));
            top = Math.Max(virtualScreen.Top - 16, Math.Min(virtualScreen.Bottom - height + 16, top));

            Left = left;
            Top = top;
        }

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                RequestExpand();
                return;
            }

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // DragMove throws if the button was released before it could start; harmless.
            }
        }

        private void Window_LocationChanged(object sender, EventArgs e)
        {
            if (IsVisible)
            {
                shell.OverlayLeft = Left;
                shell.OverlayTop = Top;
            }
        }

        private void Expand_Click(object sender, RoutedEventArgs e)
        {
            RequestExpand();
        }

        private void Power_Click(object sender, RoutedEventArgs e)
        {
            shell.Engine.IsControlEnabled = !shell.Engine.IsControlEnabled;
        }

        private void ChatToggle_Click(object sender, RoutedEventArgs e)
        {
            shell.OverlayChatOpen = !shell.OverlayChatOpen;
            if (shell.OverlayChatOpen)
            {
                ScrollChatToEnd();
            }
        }

        /// <summary>
        /// The widget is shown with ShowActivated=false. A click in the request box is the
        /// user asking to type, so the window must become the active window or the caret
        /// never appears.
        /// </summary>
        private void ChatRequestBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsActive)
            {
                Activate();
            }

            ChatRequestBox.Focus();
        }

        private void ChatRequestBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!IsActive)
            {
                Activate();
            }
        }

        private void Assistant_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "IsBusy" && shell.Assistant.IsBusy)
            {
                shell.OverlayChatOpen = true;
                ScrollChatToEnd();
            }
        }

        private void Steps_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            ScrollChatToEnd();
        }

        private void ScrollChatToEnd()
        {
            if (ChatStepsScroll == null)
            {
                return;
            }

            ChatStepsScroll.Dispatcher.BeginInvoke(new Action(() => ChatStepsScroll.ScrollToEnd()),
                DispatcherPriority.Loaded);
        }

        private void RequestExpand()
        {
            EventHandler handler = ExpandRequested;
            if (handler != null)
            {
                handler.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
