using System;
using System.Windows;
using System.Windows.Input;

namespace KinectV2MouseControl
{
    /// <summary>
    /// The compact, always-available face of KINECT-OS: a floating pill that shows whether
    /// control is live, what the hands are doing and the last thing that happened. Shown when
    /// the control center is hidden. Drag it anywhere; double-click or the expand button
    /// brings the full window back. It never takes focus, so it cannot steal keystrokes from
    /// whatever the user is working in.
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
