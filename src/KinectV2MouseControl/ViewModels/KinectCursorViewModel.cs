using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using ControlMode = KinectV2MouseControl.KinectCursor.ControlMode;

namespace KinectV2MouseControl
{
    public class KinectCursorViewModel : INotifyPropertyChanged
    {
        KinectCursor kinectCursor;

        const double DEFAULT_MOVE_SCALE = 1f;
        // Smoothing now sets the resting cutoff of an adaptive filter rather than a fixed
        // blend fraction, so a much higher resting value is both usable and calmer.
        const double DEFAULT_SMOOTHING = 0.7f;
        const double DEFAULT_HOVER_RANGE = 20f;
        const double DEFAULT_HOVER_DURATION = 2;
        const double DEFAULT_SPEED_RESPONSIVENESS = 20f;
        const double DEFAULT_JITTER_DEADZONE = 3f;
        const double DEFAULT_CLICK_FREEZE_DURATION = 0.15f;
        const double DEFAULT_POINTER_CENTER_HEIGHT = 0.5f;
        const double DEFAULT_FORWARD_ACTIVATION = 0.15f;
        const double DEFAULT_ACTIVATION_HEIGHT = 0.25f;
        const double DEFAULT_SCROLL_SPEED = 60f;
        const double DEFAULT_SWIPE_DISTANCE = 0.25f;
        const bool DEFAULT_STATIONARY_LOCK_ENABLED = true;
        const double DEFAULT_STATIONARY_LOCK_RADIUS = 15f;
        const double DEFAULT_STATIONARY_LOCK_DWELL = 0.35f;
        const double DEFAULT_STATIONARY_BREAKOUT_RADIUS = 35f;
        const bool DEFAULT_USE_CALIBRATED_RANGE = false;
        const double DEFAULT_HAND_RANGE_X = 0.5f;
        const double DEFAULT_HAND_RANGE_Y = 0.3f;
        const double DEFAULT_HAND_CENTER_X = 0f;

        /// <summary>
        /// Refresh rate of the diagnostics readout. Body frames arrive at 30Hz, but re-running
        /// WPF's binding and layout work that often for a debug panel is wasted effort, so the
        /// text is pulled on a slower timer instead of pushed from the frame handler.
        /// </summary>
        private readonly DispatcherTimer diagnosticsTimer = new DispatcherTimer();

        public KinectCursorViewModel()
        {
            kinectCursor = new KinectCursor();

            diagnosticsTimer.Interval = TimeSpan.FromMilliseconds(200);
            diagnosticsTimer.Tick += DiagnosticsTimer_Tick;
            diagnosticsTimer.Start();
        }

        private void DiagnosticsTimer_Tick(object sender, EventArgs e)
        {
            string text = kinectCursor.Diagnostics.BuildText();
            if (text == diagnosticsText)
            {
                return;
            }

            diagnosticsText = text;
            RaisePropertyChanged("DiagnosticsText");
        }

        private string diagnosticsText = string.Empty;

        /// <summary>
        /// Live gesture state, as a block of text for the diagnostics panel.
        /// </summary>
        public string DiagnosticsText
        {
            get
            {
                return diagnosticsText;
            }
        }

        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null)
        {
            if (PropertyChanged != null)
                PropertyChanged.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public double MoveScale
        {
            get
            {
                return kinectCursor.MoveScale;
            }
            set
            {
                kinectCursor.MoveScale = value;
                RaisePropertyChanged();
            }
        }

        public double Smoothing
        {
            get
            {
                return kinectCursor.Smoothing;
            }
            set
            {
                kinectCursor.Smoothing = value;
                RaisePropertyChanged();
            }
        }

        public double SpeedResponsiveness
        {
            get
            {
                return kinectCursor.SpeedResponsiveness;
            }
            set
            {
                kinectCursor.SpeedResponsiveness = value;
                RaisePropertyChanged();
            }
        }

        public double JitterDeadzone
        {
            get
            {
                return kinectCursor.JitterDeadzone;
            }
            set
            {
                kinectCursor.JitterDeadzone = value;
                RaisePropertyChanged();
            }
        }

        public double ClickFreezeDuration
        {
            get
            {
                return kinectCursor.ClickFreezeDuration;
            }
            set
            {
                kinectCursor.ClickFreezeDuration = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Metres above the hip that maps to the middle of the screen - i.e. how high you have
        /// to hold your hand to point.
        /// </summary>
        public double PointerCenterHeight
        {
            get
            {
                return kinectCursor.PointerCenterHeight;
            }
            set
            {
                kinectCursor.PointerCenterHeight = value;
                RaisePropertyChanged();
            }
        }

        public double ForwardActivationDistance
        {
            get
            {
                return kinectCursor.ForwardActivationDistance;
            }
            set
            {
                kinectCursor.ForwardActivationDistance = value;
                RaisePropertyChanged();
            }
        }

        public double ActivationMinHeight
        {
            get
            {
                return kinectCursor.ActivationMinHeight;
            }
            set
            {
                kinectCursor.ActivationMinHeight = value;
                RaisePropertyChanged();
            }
        }

        public double ScrollSpeed
        {
            get
            {
                return kinectCursor.ScrollSpeed;
            }
            set
            {
                kinectCursor.ScrollSpeed = value;
                RaisePropertyChanged();
            }
        }

        public double SwipeMinDisplacement
        {
            get
            {
                return kinectCursor.SwipeMinDisplacement;
            }
            set
            {
                kinectCursor.SwipeMinDisplacement = value;
                RaisePropertyChanged();
            }
        }

        public bool StationaryLockEnabled
        {
            get
            {
                return kinectCursor.StationaryLockEnabled;
            }
            set
            {
                kinectCursor.StationaryLockEnabled = value;
                RaisePropertyChanged();
            }
        }

        public double StationaryLockRadius
        {
            get
            {
                return kinectCursor.StationaryLockRadius;
            }
            set
            {
                kinectCursor.StationaryLockRadius = value;
                RaisePropertyChanged();
            }
        }

        public double StationaryLockDwell
        {
            get
            {
                return kinectCursor.StationaryLockDwell;
            }
            set
            {
                kinectCursor.StationaryLockDwell = value;
                RaisePropertyChanged();
            }
        }

        public double StationaryBreakoutRadius
        {
            get
            {
                return kinectCursor.StationaryBreakoutRadius;
            }
            set
            {
                kinectCursor.StationaryBreakoutRadius = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Independent per-axis hand ranges instead of one uniform scale. Off keeps the original
        /// mapping exactly, so a tuned Movement scale is unaffected.
        /// </summary>
        public bool UseCalibratedRange
        {
            get
            {
                return kinectCursor.UseCalibratedRange;
            }
            set
            {
                kinectCursor.UseCalibratedRange = value;
                RaisePropertyChanged();
            }
        }

        public double HandRangeX
        {
            get
            {
                return kinectCursor.HandRangeX;
            }
            set
            {
                kinectCursor.HandRangeX = value;
                RaisePropertyChanged();
            }
        }

        public double HandRangeY
        {
            get
            {
                return kinectCursor.HandRangeY;
            }
            set
            {
                kinectCursor.HandRangeY = value;
                RaisePropertyChanged();
            }
        }

        public double HandCenterX
        {
            get
            {
                return kinectCursor.HandCenterX;
            }
            set
            {
                kinectCursor.HandCenterX = value;
                RaisePropertyChanged();
            }
        }

        public string CalibrateButtonText
        {
            get
            {
                return kinectCursor.IsCalibrating ? "Finish" : "Calibrate";
            }
        }

        /// <summary>
        /// Starts a calibration sweep, or ends one and adopts the result. While a sweep is
        /// running the cursor and all actions are held off, so the physical mouse is needed to
        /// press the button again.
        /// </summary>
        public void ToggleCalibration()
        {
            if (kinectCursor.IsCalibrating)
            {
                kinectCursor.EndCalibration();
                RaisePropertyChanged("HandRangeX");
                RaisePropertyChanged("HandRangeY");
                RaisePropertyChanged("HandCenterX");
                RaisePropertyChanged("UseCalibratedRange");
                RaisePropertyChanged("PointerCenterHeight");
            }
            else
            {
                kinectCursor.BeginCalibration();
            }

            RaisePropertyChanged("CalibrateButtonText");
        }

        public double HoverRange
        {
            get
            {
                return kinectCursor.HoverRange;
            }
            set
            {
                kinectCursor.HoverRange = value;
                RaisePropertyChanged();
            }
        }

        public double HoverDuration
        {
            get
            {
                return kinectCursor.HoverDuration;
            }
            set
            {
                kinectCursor.HoverDuration = value;
                RaisePropertyChanged();
            }
        }

        public int ControlModeIndex
        {
            get
            {
                return (int)kinectCursor.Mode;
            }
            set
            {
                kinectCursor.Mode = (ControlMode)value;
                RaisePropertyChanged();
            }
        }

        public void LoadSettings()
        {
            MoveScale = Properties.Settings.Default.MoveScale;
            HoverDuration = Properties.Settings.Default.HoverDuration;
            HoverRange = Properties.Settings.Default.HoverRange;
            Smoothing = Properties.Settings.Default.Smoothing;
            SpeedResponsiveness = Properties.Settings.Default.SpeedResponsiveness;
            JitterDeadzone = Properties.Settings.Default.JitterDeadzone;
            ClickFreezeDuration = Properties.Settings.Default.ClickFreezeDuration;
            PointerCenterHeight = Properties.Settings.Default.PointerCenterHeight;
            ForwardActivationDistance = Properties.Settings.Default.ForwardActivationDistance;
            ActivationMinHeight = Properties.Settings.Default.ActivationMinHeight;
            ScrollSpeed = Properties.Settings.Default.ScrollSpeed;
            SwipeMinDisplacement = Properties.Settings.Default.SwipeMinDisplacement;
            StationaryLockEnabled = Properties.Settings.Default.StationaryLockEnabled;
            StationaryLockRadius = Properties.Settings.Default.StationaryLockRadius;
            StationaryLockDwell = Properties.Settings.Default.StationaryLockDwell;
            StationaryBreakoutRadius = Properties.Settings.Default.StationaryBreakoutRadius;

            // Ranges before the toggle, so switching calibrated mode on rebuilds the mapping
            // from the saved rectangle rather than from the defaults.
            HandRangeX = Properties.Settings.Default.HandRangeX;
            HandRangeY = Properties.Settings.Default.HandRangeY;
            HandCenterX = Properties.Settings.Default.HandCenterX;
            UseCalibratedRange = Properties.Settings.Default.UseCalibratedRange;

            ControlModeIndex = Properties.Settings.Default.Mode;
        }

        public void SaveSettings()
        {
            Properties.Settings.Default.MoveScale = MoveScale;
            Properties.Settings.Default.Smoothing = Smoothing;
            Properties.Settings.Default.HoverRange = HoverRange;
            Properties.Settings.Default.HoverDuration = HoverDuration;
            Properties.Settings.Default.SpeedResponsiveness = SpeedResponsiveness;
            Properties.Settings.Default.JitterDeadzone = JitterDeadzone;
            Properties.Settings.Default.ClickFreezeDuration = ClickFreezeDuration;
            Properties.Settings.Default.PointerCenterHeight = PointerCenterHeight;
            Properties.Settings.Default.ForwardActivationDistance = ForwardActivationDistance;
            Properties.Settings.Default.ActivationMinHeight = ActivationMinHeight;
            Properties.Settings.Default.ScrollSpeed = ScrollSpeed;
            Properties.Settings.Default.SwipeMinDisplacement = SwipeMinDisplacement;
            Properties.Settings.Default.StationaryLockEnabled = StationaryLockEnabled;
            Properties.Settings.Default.StationaryLockRadius = StationaryLockRadius;
            Properties.Settings.Default.StationaryLockDwell = StationaryLockDwell;
            Properties.Settings.Default.StationaryBreakoutRadius = StationaryBreakoutRadius;
            Properties.Settings.Default.UseCalibratedRange = UseCalibratedRange;
            Properties.Settings.Default.HandRangeX = HandRangeX;
            Properties.Settings.Default.HandRangeY = HandRangeY;
            Properties.Settings.Default.HandCenterX = HandCenterX;
            Properties.Settings.Default.Mode = ControlModeIndex;

            Properties.Settings.Default.Save();
        }

        public void ResetToDefault()
        {
            MoveScale = DEFAULT_MOVE_SCALE;
            Smoothing = DEFAULT_SMOOTHING;
            HoverRange = DEFAULT_HOVER_RANGE;
            HoverDuration = DEFAULT_HOVER_DURATION;
            SpeedResponsiveness = DEFAULT_SPEED_RESPONSIVENESS;
            JitterDeadzone = DEFAULT_JITTER_DEADZONE;
            ClickFreezeDuration = DEFAULT_CLICK_FREEZE_DURATION;
            PointerCenterHeight = DEFAULT_POINTER_CENTER_HEIGHT;
            ForwardActivationDistance = DEFAULT_FORWARD_ACTIVATION;
            ActivationMinHeight = DEFAULT_ACTIVATION_HEIGHT;
            ScrollSpeed = DEFAULT_SCROLL_SPEED;
            SwipeMinDisplacement = DEFAULT_SWIPE_DISTANCE;
            StationaryLockEnabled = DEFAULT_STATIONARY_LOCK_ENABLED;
            StationaryLockRadius = DEFAULT_STATIONARY_LOCK_RADIUS;
            StationaryLockDwell = DEFAULT_STATIONARY_LOCK_DWELL;
            StationaryBreakoutRadius = DEFAULT_STATIONARY_BREAKOUT_RADIUS;
            HandRangeX = DEFAULT_HAND_RANGE_X;
            HandRangeY = DEFAULT_HAND_RANGE_Y;
            HandCenterX = DEFAULT_HAND_CENTER_X;
            UseCalibratedRange = DEFAULT_USE_CALIBRATED_RANGE;
        }

        public void Quit()
        {
            diagnosticsTimer.Stop();
            SaveSettings();
            // Last, and via the mode setter, so shutdown goes through the same release path as
            // any other disable: grips up, output loop stopped, sensor closed.
            ControlModeIndex = 0;
        }

    }
}
