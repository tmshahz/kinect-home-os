using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KinectV2MouseControl
{
    /// <summary>
    /// The KINECT-OS control center. Owns the ShellViewModel, the page instances, the floating
    /// compact widget and the tray icon. Everything that changes the engine still flows
    /// through the view models; this class only moves windows around.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly ShellViewModel shell;
        private readonly Dictionary<ShellSection, UserControl> pages = new Dictionary<ShellSection, UserControl>();

        private OverlayWindow overlay;
        private TrayIcon tray;
        private bool isQuitting;
        private bool suppressMinimizeHook;

        public ShellViewModel Shell
        {
            get
            {
                return shell;
            }
        }

        public MainWindow()
            : this(new ShellViewModel(System.Windows.Threading.Dispatcher.CurrentDispatcher))
        {
        }

        public MainWindow(ShellViewModel shellViewModel)
        {
            shell = shellViewModel;
            shell.Confirm = (title, question) => MessageBox.Show(this, question, title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            shell.WindowRequest += Shell_WindowRequest;
            shell.PropertyChanged += Shell_PropertyChanged;

            EnablePageTransitions = true;
            DataContext = shell;
            InitializeComponent();
            BuildPages();
            ShowPage(shell.CurrentSection, false);
        }

        /// <summary>
        /// Every page is created up front so switching is instant and each page's live
        /// bindings are already warm. Pages inherit the shell as DataContext.
        /// </summary>
        private void BuildPages()
        {
            pages[ShellSection.Home] = new HomePage();
            pages[ShellSection.Gestures] = new GesturesPage();
            pages[ShellSection.Voice] = new VoicePage();
            pages[ShellSection.Actions] = new ActionsPage();
            pages[ShellSection.Displays] = new DisplaysPage();
            pages[ShellSection.Profiles] = new ProfilesPage();
            pages[ShellSection.Settings] = new SettingsPage();
            pages[ShellSection.Intelligence] = new AiPage();
        }

        /// <summary>
        /// For the UI smoke test: every page, so each one's XAML can be loaded and laid out.
        /// </summary>
        public IEnumerable<UserControl> Pages
        {
            get
            {
                return pages.Values;
            }
        }

        /// <summary>
        /// The fade-and-rise on page switches. Off for offscreen rendering, where no animation
        /// clock runs and a page would be captured at its starting opacity of zero.
        /// </summary>
        public bool EnablePageTransitions { get; set; }

        /// <summary>
        /// Set by the UI smoke test. The window is never shown or loaded there, so the engine
        /// still holds its constructor defaults and the previews have faked live state; when the
        /// application shuts down it closes this window, and the normal quit path would save all
        /// of that over the user's settings. With this set, closing saves nothing.
        /// </summary>
        public bool IsOffscreenCheck { get; set; }

        private void Shell_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "CurrentSection")
            {
                ShowPage(shell.CurrentSection, true);
            }
        }

        private void ShowPage(ShellSection section, bool animate)
        {
            UserControl page;
            if (!pages.TryGetValue(section, out page))
            {
                return;
            }

            PageHost.Content = page;

            if (!animate || !EnablePageTransitions)
            {
                page.BeginAnimation(OpacityProperty, null);
                page.Opacity = 1;
                page.RenderTransform = null;
                return;
            }

            // A short fade-and-rise; cheap, and enough to make the switch feel deliberate.
            TranslateTransform rise = new TranslateTransform(0, 10);
            page.RenderTransform = rise;
            page.Opacity = 0;

            DoubleAnimation fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220));
            fade.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            DoubleAnimation lift = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260));
            lift.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };

            page.BeginAnimation(OpacityProperty, fade);
            rise.BeginAnimation(TranslateTransform.YProperty, lift);
        }

        // ---- Window lifecycle ---------------------------------------------------------------------

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            // Only the app's own caption buttons (top right) should show and take clicks.
            WindowBackdrop.HideSystemCaptionButtons(this);

            if (WindowBackdrop.TryApply(this))
            {
                // Acrylic is live behind the window: paint a translucent tint instead of the
                // opaque fallback so the blur shows through every surface.
                Backdrop.Background = (Brush)FindResource("Brush.BackdropGlass");
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Settings last, and here rather than in the constructor: selecting the saved
            // mode opens the sensor, and every other value must already be in place.
            shell.Engine.LoadSettings();
            shell.ApplyDeferredStartup();

            tray = new TrayIcon();
            tray.OpenRequested += (s, a) => ExitCompact();
            tray.CompactRequested += (s, a) => EnterCompact();
            tray.ToggleControlRequested += (s, a) => shell.Engine.IsControlEnabled = !shell.Engine.IsControlEnabled;
            tray.QuitRequested += (s, a) => Quit();
            shell.Status.PropertyChanged += Status_PropertyChanged;
            UpdateTray();

            if (shell.StartCompact)
            {
                EnterCompact();
            }
        }

        private void Status_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "ControlState" || e.PropertyName == "Headline" || e.PropertyName == "IsControlEnabled")
            {
                UpdateTray();
            }
        }

        private void UpdateTray()
        {
            if (tray != null)
            {
                tray.Update(shell.Status.IsActive, shell.Status.IsControlEnabled, shell.Status.Headline);
            }
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            // With a custom chrome the maximized window overlaps the screen edge by the
            // resize border, so pull the content in by that much.
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaximizeGlyph.Icon = (Geometry)FindResource(WindowState == WindowState.Maximized ? "IconRestore" : "IconMaximize");

            // WPF can re-apply the window style on some state changes; keep the system buttons off.
            WindowBackdrop.HideSystemCaptionButtons(this);

            if (WindowState == WindowState.Minimized && shell.CompactOnMinimize && !suppressMinimizeHook && !isQuitting)
            {
                // Minimizing means "get out of my way", which in KINECT-OS is compact mode.
                WindowState = WindowState.Normal;
                EnterCompact();
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            isQuitting = true;
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            isQuitting = true;
            if (overlay != null)
            {
                overlay.Close();
                overlay = null;
            }

            if (tray != null)
            {
                tray.Dispose();
                tray = null;
            }

            if (IsOffscreenCheck)
            {
                return;
            }

            shell.Quit();
        }

        private void Quit()
        {
            isQuitting = true;
            Close();
        }

        // ---- Compact mode -------------------------------------------------------------------------

        private void Shell_WindowRequest(object sender, string request)
        {
            switch (request)
            {
                case "compact":
                    EnterCompact();
                    break;
                case "expand":
                    ExitCompact();
                    break;
                case "quit":
                    Quit();
                    break;
            }
        }

        private void EnterCompact()
        {
            if (isQuitting)
            {
                return;
            }

            if (overlay == null)
            {
                overlay = new OverlayWindow(shell);
                overlay.ExpandRequested += (s, e) => ExitCompact();
            }

            shell.IsCompact = true;
            overlay.ShowAt(shell.OverlayLeft, shell.OverlayTop);
            Hide();
            ActivityLog.Post(ActivityKind.System, "Compact mode", "Control center hidden; floating widget shown", "control center");
        }

        private void ExitCompact()
        {
            if (isQuitting)
            {
                return;
            }

            if (overlay != null && overlay.IsVisible)
            {
                shell.OverlayLeft = overlay.Left;
                shell.OverlayTop = overlay.Top;
                overlay.Hide();
            }

            shell.IsCompact = false;
            Show();
            if (WindowState == WindowState.Minimized)
            {
                suppressMinimizeHook = true;
                WindowState = WindowState.Normal;
                suppressMinimizeHook = false;
            }

            Activate();
        }

        // ---- Chrome ------------------------------------------------------------------------------

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            if (shell.CompactOnMinimize)
            {
                EnterCompact();
            }
            else
            {
                suppressMinimizeHook = true;
                WindowState = WindowState.Minimized;
                suppressMinimizeHook = false;
            }
        }

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Quit();
        }

        private void ControlPill_Click(object sender, MouseButtonEventArgs e)
        {
            shell.Engine.IsControlEnabled = !shell.Engine.IsControlEnabled;
        }
    }
}
