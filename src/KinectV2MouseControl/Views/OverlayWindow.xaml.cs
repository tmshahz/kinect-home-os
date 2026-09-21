using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
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

        /// <summary>
        /// How long the answer card stays up after the final answer, unless the user has the
        /// panel open by hand. One place so the dwell cannot drift.
        /// </summary>
        private const double AnswerCardDwellSeconds = 6;

        private DispatcherTimer answerDwell;

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
            StopAnswerDwell();
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
            // A press while the card is up on its own pins the panel as the user's choice.
            // It does not close it; the next press is an ordinary toggle.
            if (shell.WidgetAnswerTransient && !shell.OverlayChatOpen)
            {
                PinChatPanelOpen();
                ScrollChatToEnd();
                return;
            }

            StopAnswerDwell();
            shell.WidgetAnswerTransient = false;
            shell.OverlayChatOpen = !shell.OverlayChatOpen;
            if (shell.OverlayChatOpen)
            {
                ScrollChatToEnd();
            }
        }

        private void ChatPanel_MouseEnter(object sender, MouseEventArgs e)
        {
            if (shell.WidgetAnswerTransient && !shell.OverlayChatOpen)
            {
                PinChatPanelOpen();
            }
        }

        /// <summary>
        /// The widget is shown with ShowActivated=false. A click in the request box is the
        /// user asking to type, so the window must become the active window or the caret
        /// never appears.
        /// </summary>
        private void ChatRequestBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (shell.WidgetAnswerTransient && !shell.OverlayChatOpen)
            {
                PinChatPanelOpen();
            }

            if (!IsActive)
            {
                Activate();
            }

            ChatRequestBox.Focus();
        }

        private void ChatRequestBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (shell.WidgetAnswerTransient && !shell.OverlayChatOpen)
            {
                PinChatPanelOpen();
            }

            if (!IsActive)
            {
                Activate();
            }
        }

        private void Assistant_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "IsBusy")
            {
                if (shell.Assistant.IsBusy)
                {
                    OnAssistantBecameBusy();
                }
                else
                {
                    OnAssistantBecameIdle();
                }
            }
            else if (e.PropertyName == "Status")
            {
                ApplyStatusAsAnswer();
            }
        }

        /// <summary>
        /// An assistant request opens the panel. A panel the user already opened is left
        /// alone: no dwell, and OverlayChatOpen is not rewritten.
        /// </summary>
        private void OnAssistantBecameBusy()
        {
            string request = FindRequestLine();
            if (request.Length == 0)
            {
                return;
            }

            shell.WidgetAnswerRequest = request;
            shell.WidgetAnswerText = "Thinking…";
            ScrollChatToEnd();

            if (shell.OverlayChatOpen)
            {
                StopAnswerDwell();
                return;
            }

            shell.WidgetAnswerTransient = true;
            StopAnswerDwell();
        }

        /// <summary>
        /// The final answer is already in the status line. Dwell starts here, and only for
        /// a card the user did not pin open.
        /// </summary>
        private void OnAssistantBecameIdle()
        {
            if (!shell.WidgetAnswerTransient && !shell.OverlayChatOpen)
            {
                return;
            }

            ApplyStatusAsAnswer();
            if (shell.OverlayChatOpen || !shell.WidgetAnswerTransient)
            {
                StopAnswerDwell();
                return;
            }

            StartAnswerDwell();
        }

        private void ApplyStatusAsAnswer()
        {
            if (!shell.WidgetAnswerTransient && !shell.OverlayChatOpen)
            {
                return;
            }

            string status = shell.Assistant.Status ?? "";
            if (status.Length == 0 || status == "Thinking…")
            {
                return;
            }

            shell.WidgetAnswerText = status;
        }

        /// <summary>
        /// Hover, the request box, or the chevron turns the automatic card into the user's
        /// open panel. OverlayChatOpen is written here because they asked to keep it.
        /// </summary>
        private void PinChatPanelOpen()
        {
            StopAnswerDwell();
            if (!shell.OverlayChatOpen)
            {
                shell.OverlayChatOpen = true;
            }

            shell.WidgetAnswerTransient = false;
        }

        private void StartAnswerDwell()
        {
            if (answerDwell == null)
            {
                answerDwell = new DispatcherTimer();
                answerDwell.Interval = TimeSpan.FromSeconds(AnswerCardDwellSeconds);
                answerDwell.Tick += AnswerDwell_Tick;
            }

            answerDwell.Stop();
            answerDwell.Start();
        }

        private void StopAnswerDwell()
        {
            if (answerDwell != null)
            {
                answerDwell.Stop();
            }
        }

        private void AnswerDwell_Tick(object sender, EventArgs e)
        {
            StopAnswerDwell();
            if (shell.OverlayChatOpen)
            {
                return;
            }

            shell.WidgetAnswerTransient = false;
        }

        /// <summary>
        /// SubmitAsync records the user's words as a step ("Request: …") before it goes busy.
        /// Key tests and other non-request work do not, and must not pop the card.
        /// </summary>
        private string FindRequestLine()
        {
            const string marker = "Request:";
            for (int i = shell.Assistant.Steps.Count - 1; i >= 0; i--)
            {
                string step = shell.Assistant.Steps[i] ?? "";
                int at = step.IndexOf(marker, StringComparison.Ordinal);
                if (at < 0)
                {
                    continue;
                }

                string body = step.Substring(at + marker.Length).Trim();
                if (body.Length >= 2 && body[0] == '\u201C' && body[body.Length - 1] == '\u201D')
                {
                    body = body.Substring(1, body.Length - 2).Trim();
                }

                return body;
            }

            return "";
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

    /// <summary>
    /// Slides <see cref="FrameworkElement.Width"/> or <see cref="FrameworkElement.Height"/>
    /// between two concrete numbers. The clock is started only while the element is on a
    /// shown window. A style storyboard does not work here: offscreen, including the smoke
    /// test, the clock sits at time zero and a From of 0 holds the panel shut, so the
    /// Setter never shows through. From and To are both set so a resize that re-enters the
    /// clock (SizeToContent, every tick) cannot throw on an unresolved origin.
    /// </summary>
    public static class PanelSlide
    {
        public static readonly DependencyProperty ToHeightProperty = DependencyProperty.RegisterAttached(
            "ToHeight", typeof(double), typeof(PanelSlide), new PropertyMetadata(0.0, OnToHeightChanged));

        public static readonly DependencyProperty ToWidthProperty = DependencyProperty.RegisterAttached(
            "ToWidth", typeof(double), typeof(PanelSlide), new PropertyMetadata(0.0, OnToWidthChanged));

        public static void SetToHeight(DependencyObject element, double value)
        {
            element.SetValue(ToHeightProperty, value);
        }

        public static double GetToHeight(DependencyObject element)
        {
            return (double)element.GetValue(ToHeightProperty);
        }

        public static void SetToWidth(DependencyObject element, double value)
        {
            element.SetValue(ToWidthProperty, value);
        }

        public static double GetToWidth(DependencyObject element)
        {
            return (double)element.GetValue(ToWidthProperty);
        }

        private static void OnToHeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            Slide(d as FrameworkElement, FrameworkElement.HeightProperty, e);
        }

        private static void OnToWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            Slide(d as FrameworkElement, FrameworkElement.WidthProperty, e);
        }

        private static void Slide(FrameworkElement element, DependencyProperty property, DependencyPropertyChangedEventArgs e)
        {
            if (element == null)
            {
                return;
            }

            double to = e.NewValue is double ? (double)e.NewValue : 0;
            if (double.IsNaN(to))
            {
                to = 0;
            }

            // Not shown: leave the style Setter as the value. That is what the smoke test renders.
            if (!element.IsVisible)
            {
                element.BeginAnimation(property, null);
                return;
            }

            double from = property == FrameworkElement.HeightProperty ? element.ActualHeight : element.ActualWidth;
            if (from < 0.5 && e.OldValue is double && !double.IsNaN((double)e.OldValue))
            {
                from = (double)e.OldValue;
            }

            if (Math.Abs(from - to) < 0.5)
            {
                element.BeginAnimation(property, null);
                return;
            }

            bool opening = to > from;
            DoubleAnimation animation = new DoubleAnimation();
            animation.From = from;
            animation.To = to;
            animation.Duration = TimeSpan.FromMilliseconds(opening ? 220 : 180);
            animation.EasingFunction = new CubicEase { EasingMode = opening ? EasingMode.EaseOut : EasingMode.EaseIn };
            // Stop, so the style Setter (280 / 340 open, 0 closed) holds the resting value.
            animation.FillBehavior = FillBehavior.Stop;
            element.BeginAnimation(property, animation);
        }
    }
}
