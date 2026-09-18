using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Threading;
using System.Windows.Threading;

namespace KinectV2MouseControl
{
    public partial class App : Application
    {
        /// <summary>
        /// Command-line switch that loads and lays out every window and page without showing
        /// anything, opening the sensor or touching the cursor, then exits. It exists so the
        /// XAML can be verified after a change without launching the real app - a missing
        /// resource or a bad template throws at load time, which is exactly what this catches.
        /// Exit code 0 = all views loaded, 2 = a view failed (details on stderr and in the log).
        /// </summary>
        public const string SmokeTestSwitch = "--ui-smoke-test";

        /// <summary>
        /// Command-line switch that runs the voice pipeline's offline acceptance test (see
        /// VoiceSelfTest): parser, grammars, Core Audio interop, chime, and the real wake-gated
        /// engine fed synthesized speech with the chime looped back in as echo. No microphone,
        /// no speakers, nothing executed. Exit code 0 = passed, 3 = failed. Report in
        /// %LOCALAPPDATA%\KinectHomeOS\voice-self-test.txt.
        /// </summary>
        public const string VoiceSelfTestSwitch = "--voice-self-test";

        /// <summary>
        /// Installs the process-level fail-safes. A normal window close already releases every
        /// button through KinectCursor; these cover the exits that skip it - an unhandled
        /// exception, Windows logging off or shutting down, and the process being torn down -
        /// so an injected mouse-down can never outlive the app.
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (e.Args != null && Array.IndexOf(e.Args, VoiceSelfTestSwitch) >= 0)
            {
                RuntimeLog.Suspend();
                Shutdown(RunVoiceSelfTest());
                return;
            }

            if (e.Args != null && Array.IndexOf(e.Args, SmokeTestSwitch) >= 0)
            {
                // Verification only: no log rotation, no window, no sensor.
                RuntimeLog.Suspend();
                Shutdown(RunSmokeTest());
                return;
            }

            RuntimeLog.Write("App starting");
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;
            SessionEnding += App_SessionEnding;

            MainWindow window = new MainWindow();
            MainWindow = window;
            window.Show();
        }

        private static int RunVoiceSelfTest()
        {
            string report = "";
            int code = 3;

            // Off the UI thread: the recognizer must not capture the WPF dispatcher, which this
            // thread is about to block on.
            Thread worker = new Thread(() =>
            {
                try
                {
                    code = VoiceSelfTest.Run(out report);
                }
                catch (Exception ex)
                {
                    report += Environment.NewLine + "VOICE SELF-TEST CRASHED: " + ex;
                    code = 3;
                }
            });
            worker.IsBackground = true;
            worker.Start();
            worker.Join();

            try
            {
                Directory.CreateDirectory(RuntimeLog.DirectoryPath);
                File.WriteAllText(Path.Combine(RuntimeLog.DirectoryPath, "voice-self-test.txt"), report);
            }
            catch (Exception)
            {
                // stderr still gets it.
            }

            Console.Error.WriteLine(report);
            return code;
        }

        private static int RunSmokeTest()
        {
            string report = "";
            int failures = 0;

            try
            {
                ShellViewModel shell = new ShellViewModel(Dispatcher.CurrentDispatcher);
                MainWindow window = new MainWindow(shell);
                report += "MainWindow: ok\n";

                foreach (UserControl page in window.Pages)
                {
                    try
                    {
                        page.DataContext = shell;
                        page.Measure(new Size(1240, 820));
                        page.Arrange(new Rect(0, 0, 1240, 820));
                        page.UpdateLayout();
                        report += page.GetType().Name + ": ok\n";
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        report += page.GetType().Name + ": FAILED " + ex + "\n";
                    }
                }

                OverlayWindow overlay = null;
                try
                {
                    overlay = new OverlayWindow(shell);
                    report += "OverlayWindow: ok\n";
                }
                catch (Exception ex)
                {
                    failures++;
                    report += "OverlayWindow: FAILED " + ex + "\n";
                }

                // Measuring the window's content tree exercises the shell chrome templates too.
                window.Measure(new Size(1240, 820));
                report += "Shell layout: ok\n";

                // Offscreen renders of the shell and every page, so the design can be reviewed
                // from PNGs without ever showing a window.
                try
                {
                    string previewDirectory = Path.Combine(RuntimeLog.DirectoryPath, "ui-preview");
                    Directory.CreateDirectory(previewDirectory);
                    RenderPreviews(window, overlay, shell, previewDirectory);
                    report += "Previews: " + previewDirectory + "\n";
                }
                catch (Exception ex)
                {
                    report += "Previews: skipped (" + ex.Message + ")\n";
                }

                if (overlay != null)
                {
                    overlay.Close();
                }
            }
            catch (Exception ex)
            {
                failures++;
                report += "Startup: FAILED " + ex + "\n";
            }

            report += failures == 0 ? "UI SMOKE TEST PASSED" : "UI SMOKE TEST FAILED (" + failures + ")";

            try
            {
                Directory.CreateDirectory(RuntimeLog.DirectoryPath);
                File.WriteAllText(Path.Combine(RuntimeLog.DirectoryPath, "ui-smoke-test.txt"), report);
            }
            catch (Exception)
            {
                // stderr still gets it.
            }

            Console.Error.WriteLine(report);
            return failures == 0 ? 0 : 2;
        }

        /// <summary>
        /// Detaches the shell's visual tree from the (never shown) window and renders it with
        /// each page in turn, plus the compact widget, to PNG files.
        /// </summary>
        private static void RenderPreviews(MainWindow window, OverlayWindow overlay, ShellViewModel shell, string directory)
        {
            const double width = 1240;
            const double height = 820;

            System.Windows.Controls.Grid root = window.Content as System.Windows.Controls.Grid;
            if (root == null)
            {
                return;
            }

            window.EnablePageTransitions = false;
            window.Content = null;
            foreach (ShellSection section in Enum.GetValues(typeof(ShellSection)))
            {
                shell.Navigate(section);
                RenderElement(root, shell, Path.Combine(directory, "shell-" + section + ".png"), width, height, false);
            }

            // The drawer's width is animated by a trigger, which cannot run offscreen; set the
            // width directly on the named element for the capture.
            shell.Navigate(ShellSection.Gestures);
            HelpHub.Show("Cursor smoothing", true);
            System.Windows.Controls.Border drawer = root.FindName("HelpDrawer") as System.Windows.Controls.Border;
            if (drawer != null)
            {
                drawer.BeginAnimation(FrameworkElement.WidthProperty, null);
                drawer.Width = 340;
            }

            RenderElement(root, shell, Path.Combine(directory, "shell-help-drawer.png"), width, height, false);
            if (drawer != null)
            {
                drawer.Width = 0;
            }

            shell.IsHelpOpen = false;

            // A simulated live state, so the accent and glow treatments can be reviewed.
            LiveStatus s = shell.Status;
            s.IsEngineOn = true;
            s.IsSensorConnected = true;
            s.SensorText = "Connected";
            s.SensorDetail = "Kinect v2 streaming body frames";
            s.IsTracking = true;
            s.TrackingText = "Locked · quality 6/6";
            s.TrackingShort = "Locked";
            s.TrackingDetail = "Quality 6/6 · pointer Active";
            s.ControlState = ControlState.Active;
            s.ControlDetail = "Your right hand is driving the cursor";
            s.Headline = "Pointing · grip held";
            s.Subline = "Left clutch armed";
            s.IsPointerActive = true;
            s.PointerSessionText = "Active";
            s.PointerStateText = "Moving";
            s.RightHandTracked = true;
            s.RightHandInZone = true;
            s.RightHandClosed = true;
            s.RightHandSummary = "In zone · fist";
            s.LeftHandTracked = true;
            s.LeftHandInZone = true;
            s.LeftHandSummary = "In zone · fist";
            s.IsGripHeld = true;
            s.IsClutchArmed = true;
            s.ClutchText = "ARMED";
            s.SecondaryModeText = "Armed";
            s.LastAction = "Scroll up";
            s.ModeText = "Grip to press";
            s.IsGestureVocabularyOn = true;
            s.FrameText = "33.4 ms (max 41)";
            s.SignalText = "noise 2 px · glitches 0";
            ActivityLog.Post(ActivityKind.Control, "Kinect control on", "By control center", "control center");
            ActivityLog.Post(ActivityKind.Tracking, "Body locked", "Quality 6/6 at 1.8 m", "sensor");
            ActivityLog.Post(ActivityKind.Action, "Right click", "Right-hand lasso", "gesture");
            ActivityLog.Post(ActivityKind.Action, "Scroll up", "Left fist clutch", "gesture", "scroll+", 1.5);
            ActivityLog.Post(ActivityKind.Action, "Scroll up", "Left fist clutch", "gesture", "scroll+", 1.5);
            ActivityLog.Post(ActivityKind.Voice, "Voice commands on", "Wake word “Kinect”", "control center");
            shell.Voice.PreviewHud(VoiceHudState.Listening);
            shell.Navigate(ShellSection.Voice);
            RenderElement(root, shell, Path.Combine(directory, "shell-Voice-listening.png"), width, height, false);
            shell.Voice.PreviewHud(VoiceHudState.Executed);
            RenderElement(root, shell, Path.Combine(directory, "shell-Voice-executed.png"), width, height, false);
            shell.Navigate(ShellSection.Home);
            RenderElement(root, shell, Path.Combine(directory, "shell-Home-active.png"), width, height, false);
            shell.Navigate(ShellSection.Gestures);
            RenderElement(root, shell, Path.Combine(directory, "shell-Gestures-active.png"), width, height, false);

            if (overlay != null)
            {
                UIElement widget = overlay.Content as UIElement;
                if (widget != null)
                {
                    overlay.Content = null;
                    RenderElement(widget, shell, Path.Combine(directory, "overlay-voice-executed.png"), 460, 110, true);
                    shell.Voice.PreviewHud(VoiceHudState.Listening);
                    RenderElement(widget, shell, Path.Combine(directory, "overlay-voice-listening.png"), 460, 110, true);
                    shell.Voice.PreviewHud(VoiceHudState.Hidden);
                    RenderElement(widget, shell, Path.Combine(directory, "overlay-widget.png"), 460, 110, true);
                }
            }
        }

        private static void RenderElement(UIElement element, object dataContext, string path, double width, double height, bool transparent)
        {
            System.Windows.Controls.Grid host = new System.Windows.Controls.Grid();
            host.Width = width;
            host.Height = height;
            host.DataContext = dataContext;
            System.Windows.Documents.TextElement.SetForeground(host, (System.Windows.Media.Brush)Application.Current.FindResource("Brush.Text"));
            System.Windows.Documents.TextElement.SetFontFamily(host, (System.Windows.Media.FontFamily)Application.Current.FindResource("Font.UI"));

            if (!transparent)
            {
                host.Children.Add(new System.Windows.Controls.Border { Background = (System.Windows.Media.Brush)Application.Current.FindResource("Brush.BackdropOpaque") });
                host.Children.Add(new System.Windows.Controls.Border { Background = (System.Windows.Media.Brush)Application.Current.FindResource("Brush.Bloom") });
            }

            host.Children.Add(element);
            host.Measure(new Size(width, height));
            host.Arrange(new Rect(0, 0, width, height));
            host.UpdateLayout();

            // Let the dispatcher run pending layout/binding work (data-bound ItemsControls
            // populate on a later dispatcher pass) before capturing.
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Loaded, new Action(() => { }));
            host.UpdateLayout();

            System.Windows.Media.Imaging.RenderTargetBitmap bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)width, (int)height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(host);

            System.Windows.Media.Imaging.PngBitmapEncoder encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            host.Children.Remove(element);
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            RuntimeLog.Write("UNHANDLED (UI thread): " + e.Exception);
            ReleaseMouse("unhandled exception");
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            RuntimeLog.Write("UNHANDLED: " + e.ExceptionObject);
            ReleaseMouse("unhandled exception");
        }

        private void CurrentDomain_ProcessExit(object sender, EventArgs e)
        {
            ReleaseMouse("process exit");
        }

        private void App_SessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            ReleaseMouse("Windows session ending");
        }

        private static void ReleaseMouse(string reason)
        {
            if (MouseControl.ReleaseIfInjected())
            {
                RuntimeLog.Write("Fail-safe released injected left button (" + reason + ")");
            }
        }
    }
}
