using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using ControlMode = KinectV2MouseControl.KinectCursor.ControlMode;

namespace KinectV2MouseControl
{
    /// <summary>
    /// The engine-facing view model: every tunable setting, persistence, profiles, calibration
    /// and the live status the control center displays. The shell (ShellViewModel) sits on
    /// top of this and owns navigation, help, voice and the compact widget.
    /// </summary>
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
        const double DEFAULT_POINTER_SETTLE_TIME = 0.25;
        const double DEFAULT_POINTER_CENTER_HEIGHT = 0.5f;
        const double DEFAULT_FORWARD_ACTIVATION = 0.15f;
        const double DEFAULT_ACTIVATION_HEIGHT = 0.25f;
        const double DEFAULT_SCROLL_SPEED = 60f;
        const double DEFAULT_SCROLL_CURVE = 1.5;
        const bool DEFAULT_INVERT_SCROLL = false;
        const double DEFAULT_SWIPE_DISTANCE = 0.25f;
        const bool DEFAULT_STATIONARY_LOCK_ENABLED = true;
        const double DEFAULT_STATIONARY_LOCK_RADIUS = 15f;
        const double DEFAULT_STATIONARY_LOCK_DWELL = 0.35f;
        const double DEFAULT_STATIONARY_BREAKOUT_RADIUS = 35f;
        const bool DEFAULT_USE_CALIBRATED_RANGE = false;
        const double DEFAULT_HAND_RANGE_X = 0.5f;
        const double DEFAULT_HAND_RANGE_Y = 0.3f;
        const double DEFAULT_HAND_CENTER_X = 0f;
        const double DEFAULT_HAND_COMFORT_CENTER_X = 0f;
        const double DEFAULT_CALIBRATION_SPREAD_X = -1f;
        const double DEFAULT_CALIBRATION_SPREAD_Y = -1f;
        const bool DEFAULT_LEFT_HAND_CALIBRATED = false;
        const double DEFAULT_LEFT_HAND_RANGE_X = 0.5f;
        const double DEFAULT_LEFT_HAND_RANGE_Y = 0.3f;
        const double DEFAULT_LEFT_HAND_CENTER_X = 0f;
        const double DEFAULT_LEFT_HAND_COMFORT_CENTER_X = 0f;
        const double DEFAULT_LEFT_CALIBRATION_SPREAD_X = -1f;
        const double DEFAULT_LEFT_CALIBRATION_SPREAD_Y = -1f;
        const double DEFAULT_LEFT_POINTER_CENTER_HEIGHT = 0.5f;

        /// <summary>
        /// Properties that are part of a tuning profile. Changing any of them after a profile
        /// was loaded marks that profile as modified.
        /// </summary>
        private static readonly HashSet<string> TuningProperties = new HashSet<string>
        {
            "MoveScale", "Smoothing", "SpeedResponsiveness", "JitterDeadzone", "ClickFreezeDuration",
            "PointerSettleTime", "PointerCenterHeight", "ForwardActivationDistance", "ActivationMinHeight",
            "ScrollSpeed", "ScrollCurve", "InvertScroll", "SwipeMinDisplacement", "StationaryLockEnabled",
            "StationaryLockRadius", "StationaryLockDwell", "StationaryBreakoutRadius", "UseCalibratedRange",
            "HandRangeX", "HandRangeY", "HandCenterX", "HandComfortCenterX",
            "CalibrationSpreadX", "CalibrationSpreadY", "LeftHandCalibrated", "LeftHandRangeX",
            "LeftHandRangeY", "LeftHandCenterX", "LeftHandComfortCenterX",
            "LeftCalibrationSpreadX", "LeftCalibrationSpreadY", "LeftPointerCenterHeight",
            "HoverRange", "HoverDuration"
        };

        /// <summary>
        /// Refresh rate of the live status. Body frames arrive at 30Hz, but re-running WPF's
        /// binding and layout work that often for a readout is wasted effort, so the values
        /// are pulled on a slower timer instead of pushed from the frame handler.
        /// </summary>
        private readonly DispatcherTimer diagnosticsTimer = new DispatcherTimer();

        private readonly ProfileStore profileStore = new ProfileStore();
        private ProfileFile profiles;

        private int lastCalibrationVersion;
        private bool lastIsCalibrating;
        private int lastPointerHandIndex = GestureContext.RightHand;
        private string calibrationMessage;

        /// <summary>
        /// Bindable live picture of the engine. Refreshed by the diagnostics timer.
        /// </summary>
        public LiveStatus Status { get; private set; }

        /// <summary>
        /// Raised by the diagnostics timer after Status has been refreshed, for view models
        /// that derive geometry from it (the Displays page).
        /// </summary>
        public event EventHandler StatusRefreshed;
        public event EventHandler GestureControlToggled;
        public Func<string, bool> ControlCenterHandler { set { kinectCursor.Actions.ControlCenterHandler = value; } }

        public KinectCursorViewModel()
        {
            kinectCursor = new KinectCursor();
            kinectCursor.Actions.ActionExecuted += (sender, action) =>
            {
                if (action.Type == ControlActionType.ToggleControl && kinectCursor.Actions.LastSource == ActionRouter.GestureSource)
                {
                    EventHandler handler = GestureControlToggled;
                    if (handler != null) { handler(this, EventArgs.Empty); }
                }
            };
            kinectCursor.ControlEnabledChanged += KinectCursor_ControlEnabledChanged;
            kinectCursor.DesktopChanged += KinectCursor_DesktopChanged;
            Status = new LiveStatus();

            string warning;
            profiles = profileStore.Load(out warning);
            profileStatus = warning ?? "Profiles: " + ProfileStore.FilePath;
            if (warning != null)
            {
                ActivityLog.Post(ActivityKind.Profile, "Profiles file unreadable", warning, "profiles");
            }

            Slots = new ObservableCollection<ProfileSlotViewModel>();
            for (int i = 0; i < ProfileFile.SlotCount; i++)
            {
                Slots.Add(new ProfileSlotViewModel(i, profiles.Slots[i], this));
            }

            diagnosticsTimer.Interval = TimeSpan.FromMilliseconds(200);
            diagnosticsTimer.Tick += DiagnosticsTimer_Tick;
            diagnosticsTimer.Start();
            diagnosticsText = kinectCursor.Diagnostics.BuildText();
            UpdateStatus();
        }

        private void KinectCursor_ControlEnabledChanged(object sender, EventArgs e)
        {
            RaisePropertyChanged("IsControlEnabled");
            UpdateStatus();
        }

        private void KinectCursor_DesktopChanged(object sender, EventArgs e)
        {
            EventHandler handler = DesktopChanged;
            if (handler != null)
            {
                handler.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Raised on the UI thread when monitors are added, removed or rearranged.
        /// </summary>
        public event EventHandler DesktopChanged;

        private void DiagnosticsTimer_Tick(object sender, EventArgs e)
        {
            string text = kinectCursor.Diagnostics.BuildText();
            if (text != diagnosticsText)
            {
                diagnosticsText = text;
                RaisePropertyChanged("DiagnosticsText");
            }

            // Calibration runs from the frame handler and finishes on its own, so the window
            // polls for it here rather than being called back.
            if (kinectCursor.CalibrationVersion != lastCalibrationVersion)
            {
                lastCalibrationVersion = kinectCursor.CalibrationVersion;
                RaisePropertyChanged("HandRangeX");
                RaisePropertyChanged("HandRangeY");
                RaisePropertyChanged("HandCenterX");
                RaisePropertyChanged("HandComfortCenterX");
                RaisePropertyChanged("CalibrationSpreadX");
                RaisePropertyChanged("CalibrationSpreadY");
                RaisePropertyChanged("CalibrationQualityText");
                RaisePropertyChanged("LeftHandCalibrated");
                RaisePropertyChanged("LeftHandRangeX");
                RaisePropertyChanged("LeftHandRangeY");
                RaisePropertyChanged("LeftHandCenterX");
                RaisePropertyChanged("LeftHandComfortCenterX");
                RaisePropertyChanged("LeftCalibrationSpreadX");
                RaisePropertyChanged("LeftCalibrationSpreadY");
                RaisePropertyChanged("LeftPointerCenterHeight");
                RaisePropertyChanged("UseCalibratedRange");
                RaisePropertyChanged("IsPointerHandCalibrated");
                RaisePropertyChanged("CurrentPointerCalibrationEnabled");
                RaisePropertyChanged("PointerCenterHeight");
            }

            if (kinectCursor.PointerHandIndex != lastPointerHandIndex)
            {
                lastPointerHandIndex = kinectCursor.PointerHandIndex;
                RaisePropertyChanged("PointerHandName");
                RaisePropertyChanged("SecondaryHandName");
                RaisePropertyChanged("CalibrateButtonText");
                RaisePropertyChanged("CalibrationQualityText");
                RaisePropertyChanged("PointerReachRect");
                RaisePropertyChanged("IsPointerHandCalibrated");
                RaisePropertyChanged("CurrentPointerCalibrationEnabled");
            }

            if (kinectCursor.IsCalibrating != lastIsCalibrating)
            {
                lastIsCalibrating = kinectCursor.IsCalibrating;
                RaisePropertyChanged("CalibrateButtonText");
                RaisePropertyChanged("IsCalibrating");
            }

            string prompt = calibrationMessage ?? kinectCursor.CalibrationPrompt;
            if (prompt != calibrationPrompt)
            {
                calibrationPrompt = prompt;
                RaisePropertyChanged("CalibrationPrompt");
            }

            UpdateStatus();

            EventHandler handler = StatusRefreshed;
            if (handler != null)
            {
                handler.Invoke(this, EventArgs.Empty);
            }
        }

        private string diagnosticsText = string.Empty;

        /// <summary>
        /// Live engine state, as a block of text for the advanced diagnostics panel.
        /// </summary>
        public string DiagnosticsText
        {
            get
            {
                return diagnosticsText;
            }
        }

        // ---- Live status ---------------------------------------------------------------------

        /// <summary>
        /// Pulls the engine's diagnostics into the bindable LiveStatus. Called on the timer and
        /// after the few engine events that change the picture immediately.
        /// </summary>
        private void UpdateStatus()
        {
            GestureDiagnostics d = kinectCursor.Diagnostics;
            LiveStatus s = Status;

            bool engineOn = kinectCursor.Mode != ControlMode.Disabled;
            bool sensorAvailable = engineOn && kinectCursor.IsSensorAvailable;
            bool controlOn = kinectCursor.IsControlEnabled;
            bool tracking = engineOn && d.IsTracking;

            s.IsEngineOn = engineOn;
            s.IsSensorConnected = sensorAvailable;
            s.SensorText = !engineOn ? "Off" : (sensorAvailable ? "Connected" : "Not detected");
            s.SensorDetail = !engineOn
                ? "Choose a control mode to switch the sensor on"
                : (sensorAvailable ? "Kinect v2 streaming body frames" : "Waiting for the Kinect - check USB and power");

            s.IsTracking = tracking;
            s.BodyCount = d.BodyCount;
            s.BodyScore = d.BodyScore;
            s.TrackingText = !engineOn
                ? "Sensor off"
                : (tracking ? "Locked · quality " + d.BodyScore + "/6" : (sensorAvailable ? "No body in view" : "No signal"));
            s.TrackingShort = !engineOn ? "Sensor off" : (tracking ? "Locked" : (sensorAvailable ? "No body" : "No signal"));
            s.TrackingDetail = tracking
                ? "Quality " + d.BodyScore + "/6 · pointer " + d.PointerSession
                : (engineOn ? (sensorAvailable ? "Step into the sensor's view" : "Waiting for the sensor") : "Choose a control mode");

            s.ModeIndex = (int)kinectCursor.Mode;
            s.ModeText = KinectCursor.DescribeMode(kinectCursor.Mode);
            s.IsGestureVocabularyOn = kinectCursor.Mode == ControlMode.GripToPress;
            s.IsControlEnabled = controlOn;
            s.PointerHandName = d.PointerHandName;
            s.SecondaryHandName = d.SecondaryHandName;

            ControlState state;
            if (!engineOn)
            {
                state = ControlState.Off;
            }
            else if (!controlOn)
            {
                state = ControlState.Standby;
            }
            else if (d.IsPointerActive)
            {
                state = ControlState.Active;
            }
            else
            {
                state = ControlState.Ready;
            }

            s.ControlState = state;
            switch (state)
            {
                case ControlState.Off:
                    s.ControlDetail = "Control mode is Disabled - the sensor is closed";
                    break;
                case ControlState.Standby:
                    s.ControlDetail = "Tracking continues; double clap or switch on here to resume";
                    break;
                case ControlState.Ready:
                    s.ControlDetail = kinectCursor.IsCalibrating
                        ? "Calibrating - cursor control paused"
                        : "Raise your " + d.PointerHandName.ToLowerInvariant()
                            + " hand forward to take the cursor";
                    break;
                default:
                    s.ControlDetail = "Your " + d.PointerHandName.ToLowerInvariant()
                        + " hand is driving the cursor";
                    break;
            }

            s.PointerSessionText = d.PointerSession;
            s.IsPointerActive = d.IsPointerActive;
            s.PointerStateText = d.PointerState;
            s.IsLocked = d.PointerState == "Locked";
            s.LockDisplacement = d.LockDisplacement;

            s.RightHandText = d.RightHandState;
            s.LeftHandText = d.LeftHandState;
            s.RightHandTracked = tracking && d.RightHandTracked;
            s.LeftHandTracked = tracking && d.LeftHandTracked;
            s.RightHandInZone = tracking && d.RightHandActivated;
            s.LeftHandInZone = tracking && d.LeftHandActivated;
            s.RightHandClosed = tracking && d.RightHandClosed;
            s.LeftHandClosed = tracking && d.LeftHandClosed;
            s.RightHandLasso = tracking && d.RightHandLasso;
            s.LeftHandLasso = tracking && d.LeftHandLasso;
            s.RightHandSummary = DescribeHand(s.RightHandTracked, s.RightHandInZone, s.RightHandClosed, s.RightHandLasso);
            s.LeftHandSummary = DescribeHand(s.LeftHandTracked, s.LeftHandInZone, s.LeftHandClosed, s.LeftHandLasso);
            s.IsGripHeld = d.IsGripHeld;
            s.IsLassoActive = d.Gesture == "Lasso";
            s.IsClutchArmed = d.IsClutchArmed;
            s.IsScrolling = d.Gesture == "Scroll";
            s.IsSwiping = d.Gesture == "Swipe";
            s.IsClapArmed = d.ClapState != "Idle";
            s.ClutchText = d.ClutchState;
            s.ClapText = d.ClapState;
            s.GestureText = d.Gesture;
            s.SecondaryModeText = d.SecondaryMode;
            s.LastAction = d.LastAction;
            s.ScrollRate = d.ScrollRate;
            s.ScrollText = d.ScrollNeutral.HasValue
                ? "neutral " + d.ScrollNeutral.Value.ToString("0.00") + " m · offset "
                    + d.ScrollOffset.ToString("+0.00;-0.00;0.00") + " · " + d.ScrollRate.ToString("+0.0;-0.0;0.0") + " notches/s"
                : "not engaged";

            s.FrameMs = d.FrameDeltaMs;
            s.FrameMaxMs = d.FrameDeltaMaxMs;
            s.NoisePx = d.ResidualNoise;
            s.GlitchCount = d.GlitchCount;
            s.FrameText = tracking ? d.FrameDeltaMs.ToString("0.0") + " ms (max " + d.FrameDeltaMaxMs.ToString("0") + ")" : "-";
            s.SignalText = tracking ? "noise " + d.ResidualNoise.ToString("0") + " px · glitches " + d.GlitchCount : "-";

            s.HandHeight = d.ControlHandHeight;
            s.HandForward = d.ControlHandForward;
            s.HandX = d.ControlHandX;
            s.LeftHandHeight = d.SecondaryHandHeight;
            s.LeftHandX = d.SecondaryHandX;
            s.LeftHandForward = d.SecondaryHandForward;
            s.CursorX = d.CursorX;
            s.CursorY = d.CursorY;
            s.HasCursor = d.HasCursorTarget && d.IsPointerActive;
            s.DesktopText = d.DesktopBounds;
            s.MonitorCount = kinectCursor.Desktop.Monitors == null ? 0 : kinectCursor.Desktop.Monitors.Length;

            s.CalibrationText = d.Calibration;
            s.IsCalibrating = kinectCursor.IsCalibrating;
            s.IsCalibrated = kinectCursor.IsPointerHandCalibrated;
            s.CalibrationStep = d.CalibrationStep;
            s.CalibrationProgress = d.CalibrationHoldProgress;
            s.CalibrationStepText = DescribeCalibrationStep(d.CalibrationStep,
                d.CalibrationPass, d.CalibrationPasses);
            s.CalibrationHint = calibrationMessage ?? kinectCursor.CalibrationPrompt;

            switch (state)
            {
                case ControlState.Off:
                    s.Headline = "Sensor off";
                    s.Subline = "Choose a control mode to start";
                    break;
                case ControlState.Standby:
                    s.Headline = "Standby";
                    s.Subline = "Kinect control is off · double clap to resume";
                    break;
                case ControlState.Ready:
                    if (kinectCursor.IsCalibrating)
                    {
                        s.Headline = "Calibrating";
                        s.Subline = s.CalibrationStepText;
                    }
                    else if (!sensorAvailable)
                    {
                        s.Headline = "Waiting for Kinect";
                        s.Subline = "Sensor not detected";
                    }
                    else if (!tracking)
                    {
                        s.Headline = "Looking for you";
                        s.Subline = "Step into the sensor's view";
                    }
                    else
                    {
                        s.Headline = "Ready";
                        s.Subline = "Raise your " + d.PointerHandName.ToLowerInvariant() + " hand to point";
                    }
                    break;
                default:
                    if (d.IsGripHeld)
                    {
                        s.Headline = "Pointing · grip held";
                    }
                    else if (s.IsLassoActive)
                    {
                        s.Headline = "Pointing · right click";
                    }
                    else if (s.IsLocked)
                    {
                        s.Headline = "Pointing · locked";
                    }
                    else
                    {
                        s.Headline = "Pointing";
                    }

                    if (s.IsScrolling)
                    {
                        s.Subline = d.SecondaryHandName + " clutch · scrolling";
                    }
                    else if (s.IsSwiping)
                    {
                        s.Subline = d.SecondaryHandName + " clutch · swipe";
                    }
                    else if (s.IsClutchArmed)
                    {
                        s.Subline = d.SecondaryHandName + " clutch armed";
                    }
                    else
                    {
                        s.Subline = d.LastAction == "-" ? d.PointerHandName
                            + " fist to click, lasso to right click" : "Last: " + d.LastAction;
                    }
                    break;
            }
        }

        private static string DescribeHand(bool tracked, bool inZone, bool closed, bool lasso)
        {
            if (!tracked)
            {
                return "Not tracked";
            }

            string zone = inZone ? "In zone" : "Resting";
            if (closed)
            {
                return zone + " · fist";
            }

            if (lasso)
            {
                return zone + " · lasso";
            }

            return zone + " · open";
        }

        private static string DescribeCalibrationStep(int step, int pass, int passes)
        {
            string point;
            switch ((CalibrationStep)step)
            {
                case CalibrationStep.Center: point = "1 of 5 · Centre"; break;
                case CalibrationStep.Left: point = "2 of 5 · Left limit"; break;
                case CalibrationStep.Right: point = "3 of 5 · Right limit"; break;
                case CalibrationStep.Top: point = "4 of 5 · Top limit"; break;
                case CalibrationStep.Bottom: point = "5 of 5 · Bottom limit"; break;
                default: return "";
            }

            return passes > 0 ? point + " · hold " + pass + " of " + passes : point;
        }

        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null)
        {
            if (propertyName != null && TuningProperties.Contains(propertyName))
            {
                MarkProfileModified();
            }

            if (PropertyChanged != null)
                PropertyChanged.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        // ---- Control gate and actions ------------------------------------------------------------

        /// <summary>
        /// The master switch as a bindable toggle. Setting it goes through the same release
        /// and reset path as the double clap.
        /// </summary>
        public bool IsControlEnabled
        {
            get
            {
                return kinectCursor.IsControlEnabled;
            }
            set
            {
                if (kinectCursor.IsControlEnabled != value)
                {
                    kinectCursor.SetControlEnabled(value, "control center");
                    RaisePropertyChanged();
                    UpdateStatus();
                }
            }
        }

        public bool IsEngineOn
        {
            get
            {
                return kinectCursor.Mode != ControlMode.Disabled;
            }
        }

        /// <summary>
        /// Executes an action through the router on behalf of a non-gesture input. UI thread only.
        /// </summary>
        /// <returns>True when the router actually carried it out.</returns>
        public bool ExecuteAction(ControlAction action, string source)
        {
            return kinectCursor.Actions.Execute(action, source);
        }

        public DesktopActionResult ExecuteRequest(ControlAction action, DesktopActionContext context)
        {
            return kinectCursor.Actions.ExecuteRequest(action, context, "ai");
        }

        public DesktopLayout Desktop
        {
            get
            {
                return kinectCursor.Desktop;
            }
        }

        /// <summary>
        /// Right-hand region, body-relative metres, that maps onto the whole desktop right now.
        /// </summary>
        public MRect PointerReachRect
        {
            get
            {
                return kinectCursor.GetPointerReachRect();
            }
        }

        // ---- Tuning ----------------------------------------------------------------------------

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

        public double PointerSettleTime
        {
            get
            {
                return kinectCursor.PointerSettleTime;
            }
            set
            {
                kinectCursor.PointerSettleTime = value;
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

        public double ScrollCurve
        {
            get
            {
                return kinectCursor.ScrollCurve;
            }
            set
            {
                kinectCursor.ScrollCurve = value;
                RaisePropertyChanged();
            }
        }

        public bool InvertScroll
        {
            get
            {
                return kinectCursor.InvertScroll;
            }
            set
            {
                kinectCursor.InvertScroll = value;
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
                RaisePropertyChanged("CurrentPointerCalibrationEnabled");
                RaisePropertyChanged("IsPointerHandCalibrated");
            }
        }

        /// <summary>
        /// Enables calibrated mapping for the hand that currently owns the pointer. The
        /// persisted UseCalibratedRange property remains the right-hand flag so profiles
        /// created before per-hand geometry keep their exact meaning.
        /// </summary>
        public bool CurrentPointerCalibrationEnabled
        {
            get
            {
                return IsPointerHandCalibrated;
            }
            set
            {
                if (PointerHandName == "Left")
                {
                    LeftHandCalibrated = value;
                }
                else
                {
                    UseCalibratedRange = value;
                }

                RaisePropertyChanged("CurrentPointerCalibrationEnabled");
                RaisePropertyChanged("IsPointerHandCalibrated");
                RaisePropertyChanged("CalibrationQualityText");
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

        public string PointerHandName
        {
            get
            {
                return kinectCursor.PointerHandName;
            }
        }

        public string SecondaryHandName
        {
            get
            {
                return kinectCursor.SecondaryHandName;
            }
        }

        public bool IsPointerHandCalibrated
        {
            get
            {
                return kinectCursor.IsPointerHandCalibrated;
            }
        }

        public double HandComfortCenterX
        {
            get
            {
                return kinectCursor.HandComfortCenterX;
            }
            set
            {
                kinectCursor.HandComfortCenterX = value;
                RaisePropertyChanged();
                RaisePropertyChanged("CalibrationQualityText");
            }
        }

        public double CalibrationSpreadX
        {
            get
            {
                return kinectCursor.CalibrationSpreadX;
            }
            set
            {
                kinectCursor.CalibrationSpreadX = value;
                RaisePropertyChanged();
                RaisePropertyChanged("CalibrationQualityText");
            }
        }

        public double CalibrationSpreadY
        {
            get
            {
                return kinectCursor.CalibrationSpreadY;
            }
            set
            {
                kinectCursor.CalibrationSpreadY = value;
                RaisePropertyChanged();
                RaisePropertyChanged("CalibrationQualityText");
            }
        }

        public bool LeftHandCalibrated
        {
            get
            {
                return kinectCursor.LeftHandCalibrated;
            }
            set
            {
                kinectCursor.LeftHandCalibrated = value;
                RaisePropertyChanged();
                RaisePropertyChanged("CurrentPointerCalibrationEnabled");
                RaisePropertyChanged("IsPointerHandCalibrated");
                RaisePropertyChanged("CalibrationQualityText");
            }
        }

        public double LeftHandRangeX
        {
            get
            {
                return kinectCursor.LeftHandRangeX;
            }
            set
            {
                kinectCursor.LeftHandRangeX = value;
                RaisePropertyChanged();
            }
        }

        public double LeftHandRangeY
        {
            get
            {
                return kinectCursor.LeftHandRangeY;
            }
            set
            {
                kinectCursor.LeftHandRangeY = value;
                RaisePropertyChanged();
            }
        }

        public double LeftHandCenterX
        {
            get
            {
                return kinectCursor.LeftHandCenterX;
            }
            set
            {
                kinectCursor.LeftHandCenterX = value;
                RaisePropertyChanged();
            }
        }

        public double LeftHandComfortCenterX
        {
            get
            {
                return kinectCursor.LeftHandComfortCenterX;
            }
            set
            {
                kinectCursor.LeftHandComfortCenterX = value;
                RaisePropertyChanged();
                RaisePropertyChanged("CalibrationQualityText");
            }
        }

        public double LeftCalibrationSpreadX
        {
            get
            {
                return kinectCursor.LeftCalibrationSpreadX;
            }
            set
            {
                kinectCursor.LeftCalibrationSpreadX = value;
                RaisePropertyChanged();
                RaisePropertyChanged("CalibrationQualityText");
            }
        }

        public double LeftCalibrationSpreadY
        {
            get
            {
                return kinectCursor.LeftCalibrationSpreadY;
            }
            set
            {
                kinectCursor.LeftCalibrationSpreadY = value;
                RaisePropertyChanged();
                RaisePropertyChanged("CalibrationQualityText");
            }
        }

        public double LeftPointerCenterHeight
        {
            get
            {
                return kinectCursor.LeftPointerCenterHeight;
            }
            set
            {
                kinectCursor.LeftPointerCenterHeight = value;
                RaisePropertyChanged();
            }
        }

        public string CalibrationQualityText
        {
            get
            {
                if (PointerHandName == "Left" && !LeftHandCalibrated)
                {
                    return "Left hand is uncalibrated · swap to it, then choose Calibrate Left";
                }

                bool isLeft = PointerHandName == "Left";
                double spreadX = isLeft ? LeftCalibrationSpreadX : CalibrationSpreadX;
                double spreadY = isLeft ? LeftCalibrationSpreadY : CalibrationSpreadY;
                double comfortX = isLeft ? LeftHandComfortCenterX : HandComfortCenterX;
                if (spreadX < 0 || spreadY < 0)
                {
                    return PointerHandName + " legacy calibration · repeatability was not recorded; recalibrate to measure it";
                }

                bool noisy = isLeft ? kinectCursor.IsLeftCalibrationNoisy : kinectCursor.IsCalibrationNoisy;
                string quality = noisy ? "NOISY - repeat calibration" : "Good agreement";
                return PointerHandName + " · " + quality + " · hold spread X "
                    + spreadX.ToString("0.000") + " m / Y " + spreadY.ToString("0.000")
                    + " m · comfort X " + comfortX.ToString("+0.00;-0.00;0.00") + " m";
            }
        }

        public string CalibrateButtonText
        {
            get
            {
                return kinectCursor.IsCalibrating
                    ? "Cancel calibration" : "Calibrate " + PointerHandName;
            }
        }

        public bool IsCalibrating
        {
            get
            {
                return kinectCursor.IsCalibrating;
            }
        }

        private string calibrationPrompt = "";

        /// <summary>
        /// Guided-calibration instruction while capturing, otherwise the last result.
        /// </summary>
        public string CalibrationPrompt
        {
            get
            {
                return calibrationPrompt;
            }
        }

        /// <summary>
        /// Starts the guided calibration, or cancels one in progress. While capturing, the
        /// cursor and all actions are held off, so the physical mouse is needed to cancel.
        /// </summary>
        public void ToggleCalibration()
        {
            calibrationMessage = null;

            if (kinectCursor.IsCalibrating)
            {
                kinectCursor.CancelCalibration();
            }
            else if (!kinectCursor.BeginCalibration())
            {
                calibrationMessage = "Select a control mode first - the sensor is off while Disabled";
            }

            RaisePropertyChanged("CalibrateButtonText");
            RaisePropertyChanged("IsCalibrating");
            UpdateStatus();
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
                if (value != 0)
                {
                    calibrationMessage = null;
                }

                RaisePropertyChanged();
                RaisePropertyChanged("IsEngineOn");
                RaisePropertyChanged("IsControlEnabled");
                UpdateStatus();
            }
        }

        // ---- Profiles --------------------------------------------------------------------

        private int activeProfileSlot = -1;
        private bool reloadLastProfile = true;
        private bool isActiveProfileModified;
        private bool isApplyingProfile;
        private string profileStatus = "";

        public ObservableCollection<ProfileSlotViewModel> Slots { get; private set; }

        /// <summary>
        /// Set by the window: asked before a non-empty slot is overwritten. Null saves without
        /// asking.
        /// </summary>
        public Func<ProfileSlotViewModel, bool> ConfirmOverwrite { get; set; }

        public string ProfileFilePath
        {
            get
            {
                return ProfileStore.FilePath;
            }
        }

        /// <summary>
        /// Name of the loaded profile, for the header and the compact widget.
        /// </summary>
        public string ActiveProfileName
        {
            get
            {
                if (activeProfileSlot < 0)
                {
                    return "Unsaved settings";
                }

                return profiles.Slots[activeProfileSlot].Name + (isActiveProfileModified ? " · modified" : "");
            }
        }

        public bool HasActiveProfile
        {
            get
            {
                return activeProfileSlot >= 0;
            }
        }

        /// <summary>
        /// When on, startup applies the profile last loaded or saved, after the saved settings
        /// and before the control mode. Not part of a tuning profile.
        /// </summary>
        public bool ReloadLastProfile
        {
            get
            {
                return reloadLastProfile;
            }
            set
            {
                if (reloadLastProfile == value)
                {
                    return;
                }

                reloadLastProfile = value;
                RaisePropertyChanged();
            }
        }

        public bool IsActiveProfileModified
        {
            get
            {
                return isActiveProfileModified;
            }
        }

        /// <summary>
        /// Which profile is loaded, whether it has been changed since, and the last save/load
        /// result.
        /// </summary>
        public string ProfileStatusText
        {
            get
            {
                string active = activeProfileSlot < 0
                    ? "Active: saved settings (no profile loaded)"
                    : "Active: " + profiles.Slots[activeProfileSlot].Name + (isActiveProfileModified ? " (modified)" : "");
                return active + "\n" + profileStatus;
            }
        }

        public string ProfileLastResult
        {
            get
            {
                return profileStatus;
            }
        }

        public bool IsProfileEmpty(int slot)
        {
            return profiles.Slots[slot].IsEmpty;
        }

        public string GetProfileName(int slot)
        {
            return profiles.Slots[slot].Name;
        }

        private void MarkProfileModified()
        {
            if (isApplyingProfile || activeProfileSlot < 0 || isActiveProfileModified)
            {
                return;
            }

            isActiveProfileModified = true;
            RefreshProfileState();
        }

        private void RefreshProfileState()
        {
            for (int i = 0; i < Slots.Count; i++)
            {
                Slots[i].IsActive = i == activeProfileSlot;
                Slots[i].IsModified = i == activeProfileSlot && isActiveProfileModified;
                Slots[i].Refresh();
            }

            RaisePropertyChanged("ProfileStatusText");
            RaisePropertyChanged("ProfileLastResult");
            RaisePropertyChanged("ActiveProfileName");
            RaisePropertyChanged("HasActiveProfile");
            RaisePropertyChanged("IsActiveProfileModified");
        }

        /// <summary>
        /// Save with the overwrite confirmation the window provides.
        /// </summary>
        public void RequestSaveProfile(int slot)
        {
            if (!IsProfileEmpty(slot) && ConfirmOverwrite != null && !ConfirmOverwrite(Slots[slot]))
            {
                return;
            }

            SaveProfile(slot);
        }

        public void SaveProfile(int slot)
        {
            ProfileSlot target = profiles.Slots[slot];
            TuningProfile previousSettings = target.Settings;
            string previousSavedAt = target.SavedAt;

            target.Settings = CaptureProfile();
            target.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            string error;
            if (profileStore.Save(profiles, out error))
            {
                NoteProfileSlot(slot);
                isActiveProfileModified = false;
                profileStatus = "Saved " + target.Name + " at " + DateTime.Now.ToString("HH:mm:ss");
                RuntimeLog.Write("Profile saved to slot " + (slot + 1) + " (" + target.Name + ")");
                ActivityLog.Post(ActivityKind.Profile, "Profile saved", target.Name + " (slot " + (slot + 1) + ")", "control center");
            }
            else
            {
                target.Settings = previousSettings;
                target.SavedAt = previousSavedAt;
                profileStatus = "SAVE FAILED: " + error;
                ActivityLog.Post(ActivityKind.Profile, "Profile save failed", error, "control center");
            }

            RefreshProfileState();
        }

        public void LoadProfile(int slot)
        {
            ProfileSlot source = profiles.Slots[slot];
            if (source.IsEmpty)
            {
                profileStatus = source.Name + " is empty - save to it first";
                RefreshProfileState();
                return;
            }

            isApplyingProfile = true;
            try
            {
                kinectCursor.ApplySettings(() => ApplyProfile(source.Settings));
            }
            finally
            {
                isApplyingProfile = false;
            }

            NoteProfileSlot(slot);
            isActiveProfileModified = false;
            profileStatus = "Loaded " + source.Name + " (saved " + source.SavedAt + ")";
            RuntimeLog.Write("Profile loaded from slot " + (slot + 1) + " (" + source.Name + ")");
            ActivityLog.Post(ActivityKind.Profile, "Profile loaded", source.Name + " (slot " + (slot + 1) + ")", "control center");
            RefreshProfileState();
            UpdateStatus();
        }

        private void NoteProfileSlot(int slot)
        {
            activeProfileSlot = slot;
            Properties.Settings.Default.LastProfileSlot = slot;
        }

        /// <summary>
        /// A rename is written to disk straight away so it cannot be lost by closing the app,
        /// which is what the old "rename, then remember to Save" flow risked. Settings in the
        /// slot are untouched.
        /// </summary>
        internal void OnProfileRenamed(int slot)
        {
            string error;
            if (!profileStore.Save(profiles, out error))
            {
                profileStatus = "RENAME NOT SAVED: " + error;
            }

            RefreshProfileState();
        }

        private TuningProfile CaptureProfile()
        {
            TuningProfile profile = new TuningProfile();
            profile.MoveScale = MoveScale;
            profile.Smoothing = Smoothing;
            profile.SpeedResponsiveness = SpeedResponsiveness;
            profile.JitterDeadzone = JitterDeadzone;
            profile.ClickFreezeDuration = ClickFreezeDuration;
            profile.PointerSettleTime = PointerSettleTime;
            profile.PointerCenterHeight = PointerCenterHeight;
            profile.ActivationMinHeight = ActivationMinHeight;
            profile.ForwardActivationDistance = ForwardActivationDistance;
            profile.ScrollSpeed = ScrollSpeed;
            profile.ScrollCurve = ScrollCurve;
            profile.InvertScroll = InvertScroll;
            profile.SwipeMinDisplacement = SwipeMinDisplacement;
            profile.HoverRange = HoverRange;
            profile.HoverDuration = HoverDuration;
            profile.StationaryLockEnabled = StationaryLockEnabled;
            profile.StationaryLockRadius = StationaryLockRadius;
            profile.StationaryLockDwell = StationaryLockDwell;
            profile.StationaryBreakoutRadius = StationaryBreakoutRadius;
            profile.UseCalibratedRange = UseCalibratedRange;
            profile.HandRangeX = HandRangeX;
            profile.HandRangeY = HandRangeY;
            profile.HandCenterX = HandCenterX;
            profile.HandComfortCenterX = HandComfortCenterX;
            profile.CalibrationSpreadX = CalibrationSpreadX;
            profile.CalibrationSpreadY = CalibrationSpreadY;
            profile.LeftHandCalibrated = LeftHandCalibrated;
            profile.LeftHandRangeX = LeftHandRangeX;
            profile.LeftHandRangeY = LeftHandRangeY;
            profile.LeftHandCenterX = LeftHandCenterX;
            profile.LeftHandComfortCenterX = LeftHandComfortCenterX;
            profile.LeftCalibrationSpreadX = LeftCalibrationSpreadX;
            profile.LeftCalibrationSpreadY = LeftCalibrationSpreadY;
            profile.LeftPointerCenterHeight = LeftPointerCenterHeight;
            return profile;
        }

        /// <summary>
        /// Applies whatever the profile contains; missing values (from an older build) are left
        /// as they are. Always called inside KinectCursor.ApplySettings.
        /// </summary>
        private void ApplyProfile(TuningProfile p)
        {
            if (p.MoveScale.HasValue) MoveScale = p.MoveScale.Value;
            if (p.Smoothing.HasValue) Smoothing = p.Smoothing.Value;
            if (p.SpeedResponsiveness.HasValue) SpeedResponsiveness = p.SpeedResponsiveness.Value;
            if (p.JitterDeadzone.HasValue) JitterDeadzone = p.JitterDeadzone.Value;
            if (p.ClickFreezeDuration.HasValue) ClickFreezeDuration = p.ClickFreezeDuration.Value;
            if (p.PointerSettleTime.HasValue) PointerSettleTime = p.PointerSettleTime.Value;
            if (p.PointerCenterHeight.HasValue) PointerCenterHeight = p.PointerCenterHeight.Value;
            if (p.ActivationMinHeight.HasValue) ActivationMinHeight = p.ActivationMinHeight.Value;
            if (p.ForwardActivationDistance.HasValue) ForwardActivationDistance = p.ForwardActivationDistance.Value;
            if (p.ScrollSpeed.HasValue) ScrollSpeed = p.ScrollSpeed.Value;
            if (p.ScrollCurve.HasValue) ScrollCurve = p.ScrollCurve.Value;
            if (p.InvertScroll.HasValue) InvertScroll = p.InvertScroll.Value;
            if (p.SwipeMinDisplacement.HasValue) SwipeMinDisplacement = p.SwipeMinDisplacement.Value;
            if (p.HoverRange.HasValue) HoverRange = p.HoverRange.Value;
            if (p.HoverDuration.HasValue) HoverDuration = p.HoverDuration.Value;
            if (p.StationaryLockEnabled.HasValue) StationaryLockEnabled = p.StationaryLockEnabled.Value;
            if (p.StationaryLockRadius.HasValue) StationaryLockRadius = p.StationaryLockRadius.Value;
            if (p.StationaryLockDwell.HasValue) StationaryLockDwell = p.StationaryLockDwell.Value;
            if (p.StationaryBreakoutRadius.HasValue) StationaryBreakoutRadius = p.StationaryBreakoutRadius.Value;

            // Ranges before the toggle, so switching calibrated mode on rebuilds the mapping
            // from the profile's rectangle rather than from the previous one.
            if (p.HandRangeX.HasValue) HandRangeX = p.HandRangeX.Value;
            if (p.HandRangeY.HasValue) HandRangeY = p.HandRangeY.Value;
            if (p.HandCenterX.HasValue) HandCenterX = p.HandCenterX.Value;
            if (p.HandComfortCenterX.HasValue) HandComfortCenterX = p.HandComfortCenterX.Value;
            if (p.CalibrationSpreadX.HasValue) CalibrationSpreadX = p.CalibrationSpreadX.Value;
            if (p.CalibrationSpreadY.HasValue) CalibrationSpreadY = p.CalibrationSpreadY.Value;
            if (p.LeftHandRangeX.HasValue) LeftHandRangeX = p.LeftHandRangeX.Value;
            if (p.LeftHandRangeY.HasValue) LeftHandRangeY = p.LeftHandRangeY.Value;
            if (p.LeftHandCenterX.HasValue) LeftHandCenterX = p.LeftHandCenterX.Value;
            if (p.LeftHandComfortCenterX.HasValue) LeftHandComfortCenterX = p.LeftHandComfortCenterX.Value;
            if (p.LeftCalibrationSpreadX.HasValue) LeftCalibrationSpreadX = p.LeftCalibrationSpreadX.Value;
            if (p.LeftCalibrationSpreadY.HasValue) LeftCalibrationSpreadY = p.LeftCalibrationSpreadY.Value;
            if (p.LeftPointerCenterHeight.HasValue) LeftPointerCenterHeight = p.LeftPointerCenterHeight.Value;
            if (p.LeftHandCalibrated.HasValue) LeftHandCalibrated = p.LeftHandCalibrated.Value;
            if (p.UseCalibratedRange.HasValue) UseCalibratedRange = p.UseCalibratedRange.Value;
        }

        // ---- Persistence -----------------------------------------------------------------

        public void LoadSettings()
        {
            MoveScale = Properties.Settings.Default.MoveScale;
            HoverDuration = Properties.Settings.Default.HoverDuration;
            HoverRange = Properties.Settings.Default.HoverRange;
            Smoothing = Properties.Settings.Default.Smoothing;
            SpeedResponsiveness = Properties.Settings.Default.SpeedResponsiveness;
            JitterDeadzone = Properties.Settings.Default.JitterDeadzone;
            ClickFreezeDuration = Properties.Settings.Default.ClickFreezeDuration;
            PointerSettleTime = Properties.Settings.Default.PointerSettleTime;
            PointerCenterHeight = Properties.Settings.Default.PointerCenterHeight;
            ForwardActivationDistance = Properties.Settings.Default.ForwardActivationDistance;
            ActivationMinHeight = Properties.Settings.Default.ActivationMinHeight;
            ScrollSpeed = Properties.Settings.Default.ScrollSpeed;
            ScrollCurve = Properties.Settings.Default.ScrollCurve;
            InvertScroll = Properties.Settings.Default.InvertScroll;
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
            HandComfortCenterX = Properties.Settings.Default.HandComfortCenterX;
            CalibrationSpreadX = Properties.Settings.Default.CalibrationSpreadX;
            CalibrationSpreadY = Properties.Settings.Default.CalibrationSpreadY;
            LeftHandRangeX = Properties.Settings.Default.LeftHandRangeX;
            LeftHandRangeY = Properties.Settings.Default.LeftHandRangeY;
            LeftHandCenterX = Properties.Settings.Default.LeftHandCenterX;
            LeftHandComfortCenterX = Properties.Settings.Default.LeftHandComfortCenterX;
            LeftCalibrationSpreadX = Properties.Settings.Default.LeftCalibrationSpreadX;
            LeftCalibrationSpreadY = Properties.Settings.Default.LeftCalibrationSpreadY;
            LeftPointerCenterHeight = Properties.Settings.Default.LeftPointerCenterHeight;
            LeftHandCalibrated = Properties.Settings.Default.LeftHandCalibrated;
            UseCalibratedRange = Properties.Settings.Default.UseCalibratedRange;

            reloadLastProfile = Properties.Settings.Default.ReloadLastProfile;
            RaisePropertyChanged("ReloadLastProfile");
            TryReloadLastProfile();

            // Last: selecting a mode opens the sensor, so every setting is in place before the
            // first frame can arrive.
            ControlModeIndex = Properties.Settings.Default.Mode;
        }

        /// <summary>
        /// Applies the last loaded or saved slot through LoadProfile. An empty, missing or
        /// failed slot leaves the settings just loaded and never blocks startup.
        /// </summary>
        private void TryReloadLastProfile()
        {
            if (!reloadLastProfile)
            {
                return;
            }

            int slot = Properties.Settings.Default.LastProfileSlot;
            if (slot < 0)
            {
                return;
            }

            if (slot >= profiles.Slots.Length || profiles.Slots[slot] == null || profiles.Slots[slot].IsEmpty)
            {
                RuntimeLog.Write("Last profile slot " + (slot + 1) + " is empty or missing; keeping saved settings");
                return;
            }

            try
            {
                LoadProfile(slot);
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("Last profile could not be loaded; keeping saved settings (" + ex.Message + ")");
            }
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
            Properties.Settings.Default.PointerSettleTime = PointerSettleTime;
            Properties.Settings.Default.PointerCenterHeight = PointerCenterHeight;
            Properties.Settings.Default.ForwardActivationDistance = ForwardActivationDistance;
            Properties.Settings.Default.ActivationMinHeight = ActivationMinHeight;
            Properties.Settings.Default.ScrollSpeed = ScrollSpeed;
            Properties.Settings.Default.ScrollCurve = ScrollCurve;
            Properties.Settings.Default.InvertScroll = InvertScroll;
            Properties.Settings.Default.SwipeMinDisplacement = SwipeMinDisplacement;
            Properties.Settings.Default.StationaryLockEnabled = StationaryLockEnabled;
            Properties.Settings.Default.StationaryLockRadius = StationaryLockRadius;
            Properties.Settings.Default.StationaryLockDwell = StationaryLockDwell;
            Properties.Settings.Default.StationaryBreakoutRadius = StationaryBreakoutRadius;
            Properties.Settings.Default.UseCalibratedRange = UseCalibratedRange;
            Properties.Settings.Default.HandRangeX = HandRangeX;
            Properties.Settings.Default.HandRangeY = HandRangeY;
            Properties.Settings.Default.HandCenterX = HandCenterX;
            Properties.Settings.Default.HandComfortCenterX = HandComfortCenterX;
            Properties.Settings.Default.CalibrationSpreadX = CalibrationSpreadX;
            Properties.Settings.Default.CalibrationSpreadY = CalibrationSpreadY;
            Properties.Settings.Default.LeftHandCalibrated = LeftHandCalibrated;
            Properties.Settings.Default.LeftHandRangeX = LeftHandRangeX;
            Properties.Settings.Default.LeftHandRangeY = LeftHandRangeY;
            Properties.Settings.Default.LeftHandCenterX = LeftHandCenterX;
            Properties.Settings.Default.LeftHandComfortCenterX = LeftHandComfortCenterX;
            Properties.Settings.Default.LeftCalibrationSpreadX = LeftCalibrationSpreadX;
            Properties.Settings.Default.LeftCalibrationSpreadY = LeftCalibrationSpreadY;
            Properties.Settings.Default.LeftPointerCenterHeight = LeftPointerCenterHeight;
            Properties.Settings.Default.Mode = ControlModeIndex;
            Properties.Settings.Default.ReloadLastProfile = reloadLastProfile;
            if (activeProfileSlot >= 0)
            {
                Properties.Settings.Default.LastProfileSlot = activeProfileSlot;
            }

            Properties.Settings.Default.Save();
        }

        public void ResetToDefault()
        {
            kinectCursor.ApplySettings(() =>
            {
                MoveScale = DEFAULT_MOVE_SCALE;
                Smoothing = DEFAULT_SMOOTHING;
                HoverRange = DEFAULT_HOVER_RANGE;
                HoverDuration = DEFAULT_HOVER_DURATION;
                SpeedResponsiveness = DEFAULT_SPEED_RESPONSIVENESS;
                JitterDeadzone = DEFAULT_JITTER_DEADZONE;
                ClickFreezeDuration = DEFAULT_CLICK_FREEZE_DURATION;
                PointerSettleTime = DEFAULT_POINTER_SETTLE_TIME;
                PointerCenterHeight = DEFAULT_POINTER_CENTER_HEIGHT;
                ForwardActivationDistance = DEFAULT_FORWARD_ACTIVATION;
                ActivationMinHeight = DEFAULT_ACTIVATION_HEIGHT;
                ScrollSpeed = DEFAULT_SCROLL_SPEED;
                ScrollCurve = DEFAULT_SCROLL_CURVE;
                InvertScroll = DEFAULT_INVERT_SCROLL;
                SwipeMinDisplacement = DEFAULT_SWIPE_DISTANCE;
                StationaryLockEnabled = DEFAULT_STATIONARY_LOCK_ENABLED;
                StationaryLockRadius = DEFAULT_STATIONARY_LOCK_RADIUS;
                StationaryLockDwell = DEFAULT_STATIONARY_LOCK_DWELL;
                StationaryBreakoutRadius = DEFAULT_STATIONARY_BREAKOUT_RADIUS;
                HandRangeX = DEFAULT_HAND_RANGE_X;
                HandRangeY = DEFAULT_HAND_RANGE_Y;
                HandCenterX = DEFAULT_HAND_CENTER_X;
                HandComfortCenterX = DEFAULT_HAND_COMFORT_CENTER_X;
                CalibrationSpreadX = DEFAULT_CALIBRATION_SPREAD_X;
                CalibrationSpreadY = DEFAULT_CALIBRATION_SPREAD_Y;
                LeftHandCalibrated = DEFAULT_LEFT_HAND_CALIBRATED;
                LeftHandRangeX = DEFAULT_LEFT_HAND_RANGE_X;
                LeftHandRangeY = DEFAULT_LEFT_HAND_RANGE_Y;
                LeftHandCenterX = DEFAULT_LEFT_HAND_CENTER_X;
                LeftHandComfortCenterX = DEFAULT_LEFT_HAND_COMFORT_CENTER_X;
                LeftCalibrationSpreadX = DEFAULT_LEFT_CALIBRATION_SPREAD_X;
                LeftCalibrationSpreadY = DEFAULT_LEFT_CALIBRATION_SPREAD_Y;
                LeftPointerCenterHeight = DEFAULT_LEFT_POINTER_CENTER_HEIGHT;
                UseCalibratedRange = DEFAULT_USE_CALIBRATED_RANGE;
            });

            ActivityLog.Post(ActivityKind.Profile, "Defaults restored", "All tuning values reset to the built-in defaults", "control center");
            UpdateStatus();
        }

        public void Quit()
        {
            diagnosticsTimer.Stop();
            SaveSettings();
            // Last, and via the mode setter, so shutdown goes through the same release path as
            // any other disable: grips up, output loop stopped, sensor closed.
            ControlModeIndex = 0;
            RuntimeLog.Write("App closed normally");
        }

    }
}
