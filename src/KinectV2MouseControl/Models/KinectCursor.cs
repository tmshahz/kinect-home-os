using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Kinect;
using Microsoft.Win32;

namespace KinectV2MouseControl
{
    public class KinectCursor : IControlGate
    {
        private KinectReader sensorReader;
        private CursorMapper cursorMapper;
        private CursorOutputLoop outputLoop;
        private DesktopLayout desktop;

        private readonly StationaryLock stationaryLock = new StationaryLock();
        private readonly PointerCalibration calibration = new PointerCalibration();

        private readonly GestureTuning tuning = new GestureTuning();
        private readonly ActionRouter actionRouter = new ActionRouter();
        private readonly GestureContext gestureContext = new GestureContext();
        private readonly GestureEngine gestureEngine;
        private readonly PointerStabilizer stabilizer;

        /// <summary>
        /// Fixed hand roles. The right hand is the only hand that can own the pointer; the left
        /// hand never can, whatever the right hand is doing. See GestureContext.PointerHand.
        /// </summary>
        private const int PointerHand = GestureContext.PointerHand;
        private const int SecondaryHand = GestureContext.SecondaryHand;

        /// <summary>
        /// Live view of what the control engine is seeing. Read by the settings window.
        /// </summary>
        public GestureDiagnostics Diagnostics { get; private set; }

        /// <summary>
        /// Per-hand activation latch. Activation has hysteresis, so it cannot be a pure
        /// function of the current frame.
        /// </summary>
        private readonly bool[] handActivated = new bool[2];

        public enum ControlMode
        {
            Disabled = 0,
            MoveOnly,
            GripToPress,
            HoverToClick,
            MoveGripPressing,
            MoveLiftClicking
        }

        private ControlMode _mode = ControlMode.Disabled;
        public ControlMode Mode
        {
            get
            {
                return _mode;
            }
            set
            {
                RuntimeLog.Write("Mode -> " + value);
                if (value != _mode)
                {
                    ActivityLog.Post(ActivityKind.Control, "Control mode: " + DescribeMode(value),
                        value == ControlMode.Disabled ? "Sensor switched off" : "Sensor on, tracking", "control center");
                }

                _mode = value;
                if (value == ControlMode.Disabled)
                {
                    ToggleHoverTimer(false);
                    // Releasing before anything else: disabling while a fist is closed must
                    // never leave the mouse button held down.
                    ReleaseAllGrips();
                    calibration.CancelCapture();
                    ResetControlState();
                    safetyTimer.Stop();
                    UpdateOutputLoopState();
                    sensorReader.Close();
                    SetDiagnosticsIdle(false);
                }
                else
                {
                    // Picking a mode from the UI is an explicit request for control, and is the
                    // way back if a double clap left control off and the gesture will not take.
                    controlEnabled = true;
                    calibration.CancelCapture();

                    ToggleHoverTimer(false);
                    // Switching between two active modes can happen mid-drag; release first,
                    // like every other teardown path, rather than waiting for the next frame.
                    ReleaseAllGrips();
                    ResetControlState();
                    UpdateOutputLoopState();
                    safetyTimer.Start();
                    sensorReader.Open();
                }

                Diagnostics.IsControlEnabled = controlEnabled;
            }
        }

        private bool controlEnabled = true;

        /// <summary>
        /// Master gate for acting on the machine.
        ///
        /// Separate from ControlMode.Disabled, which closes the sensor outright. Control being
        /// off still leaves body tracking running, because the double clap that switches it
        /// back on has to be seen. While off: no cursor output, no actions, no held buttons.
        /// </summary>
        public bool IsControlEnabled
        {
            get
            {
                return controlEnabled;
            }
        }

        public event EventHandler ControlEnabledChanged;

        public void ToggleControl()
        {
            SetControlEnabled(!controlEnabled, "double clap");
        }

        /// <summary>
        /// Human-readable control mode, shared by the activity feed and the control center.
        /// </summary>
        public static string DescribeMode(ControlMode mode)
        {
            switch (mode)
            {
                case ControlMode.Disabled: return "Disabled";
                case ControlMode.MoveOnly: return "Move only";
                case ControlMode.GripToPress: return "Grip to press";
                case ControlMode.HoverToClick: return "Hover to click";
                case ControlMode.MoveGripPressing: return "Move + grip pressing";
                case ControlMode.MoveLiftClicking: return "Move + lift clicking";
                default: return mode.ToString();
            }
        }

        /// <summary>
        /// The router (IActionSink) for inputs other than gestures: voice commands and the
        /// control center's Actions page execute through it, on the UI thread.
        /// </summary>
        public ActionRouter Actions
        {
            get
            {
                return actionRouter;
            }
        }

        /// <summary>
        /// Current desktop geometry, for the Displays page.
        /// </summary>
        public DesktopLayout Desktop
        {
            get
            {
                return desktop;
            }
        }

        public bool IsSensorOpen
        {
            get
            {
                return sensorReader.IsSensorOpen;
            }
        }

        /// <summary>
        /// The right-hand region, in raw body-relative metres (SpineBase origin, Y up), that
        /// currently maps onto the whole virtual desktop. Purely descriptive - the control
        /// center draws it so the user can see where the reach limits sit - and derived from
        /// the same mapper state the pointer path uses, so it can never disagree with it.
        /// </summary>
        public MRect GetPointerReachRect()
        {
            MVector2 scale = cursorMapper.TotalScale;
            double halfWidth = scale.X == 0 ? 0 : Math.Abs(desktop.Bounds.DeltaX * 0.5 / scale.X);
            double halfHeight = scale.Y == 0 ? 0 : Math.Abs(desktop.Bounds.DeltaY * 0.5 / scale.Y);

            // The mapping frame is offset sideways per hand and vertically by the pointer
            // height (see KinectBodyHelper.GetHandRelativePosition); undo both.
            MVector2 centre = cursorMapper.InputRect.Center;
            double centreX = centre.X + KinectBodyHelper.GESTURE_X_OFFSET;
            double centreY = centre.Y + tuning.PointerCenterHeight;

            return new MRect(centreX - halfWidth, centreY + halfHeight, centreX + halfWidth, centreY - halfHeight);
        }

        public bool IsSensorAvailable
        {
            get
            {
                return sensorReader.IsSensorAvailable;
            }
        }

        /// <summary>
        /// Switches the control gate. <paramref name="source"/> is who asked - "double clap",
        /// "voice", "control center" - and only feeds the log and the activity feed.
        /// </summary>
        public void SetControlEnabled(bool enabled, string source)
        {
            if (controlEnabled == enabled)
            {
                return;
            }

            RuntimeLog.Write("Control " + (enabled ? "ENABLED" : "DISABLED") + " by " + source);
            ActivityLog.Post(ActivityKind.Control, enabled ? "Kinect control on" : "Kinect control off",
                "By " + source, source);
            controlEnabled = enabled;

            ToggleHoverTimer(false);
            // Order matters: buttons go up before any state is torn down, so nothing can be
            // left held by a path that returns early.
            ReleaseAllGrips();
            ResetControlState();
            UpdateOutputLoopState();

            if (!enabled)
            {
                // Still tracking - that is the whole point - just not acting.
                SetDiagnosticsIdle(true);
            }

            Diagnostics.IsControlEnabled = enabled;

            EventHandler handler = ControlEnabledChanged;
            if (handler != null)
            {
                handler.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// The output loop runs only when every gate agrees: a mode is selected, control is on,
        /// and we are not mid-calibration. Single place to decide, so the gates cannot contradict
        /// each other.
        /// </summary>
        private void UpdateOutputLoopState()
        {
            if (_mode != ControlMode.Disabled && controlEnabled && !calibration.IsCapturing)
            {
                outputLoop.Start();
            }
            else
            {
                outputLoop.Stop();
            }
        }

        /// <summary>
        /// Applies a batch of settings changes - a profile load, Reset to defaults - safely.
        /// Buttons are released first, any calibration capture is abandoned, and afterwards
        /// every piece of derived state (filters, lock, mapping, activation latches, gestures,
        /// pointer session) is rebuilt, so the next session starts cleanly under the new values.
        /// </summary>
        public void ApplySettings(Action apply)
        {
            ToggleHoverTimer(false);
            ReleaseAllGrips();
            calibration.CancelCapture();

            apply();

            ReleaseAllGrips();
            ResetControlState();
            ApplyInputMapping();
            UpdateOutputLoopState();
        }

        public double Smoothing
        {
            get
            {
                return cursorMapper.Smoothing;
            }
            set
            {
                cursorMapper.Smoothing = value;
            }
        }

        /// <summary>
        /// 0-100 dial for how much the adaptive filter opens up during fast hand movement.
        /// </summary>
        public double SpeedResponsiveness
        {
            get
            {
                return cursorMapper.SpeedResponsiveness;
            }
            set
            {
                cursorMapper.SpeedResponsiveness = value;
            }
        }

        /// <summary>
        /// Radius in screen pixels of the (continuous) jitter dead zone.
        /// </summary>
        public double JitterDeadzone
        {
            get
            {
                return cursorMapper.JitterDeadzone;
            }
            set
            {
                cursorMapper.JitterDeadzone = value;
            }
        }

        /// <summary>
        /// Seconds the cursor stays pinned after a press is confirmed, before the hand is
        /// allowed to drag it. The cursor is already held from the moment the fist starts to
        /// close, so this only covers the settling time after the button goes down.
        /// </summary>
        public double ClickFreezeDuration { get; set; } = 0.15;

        /// <summary>
        /// Height above SpineBase, in metres, that maps to the vertical centre of the screen.
        /// Lower it to point comfortably while seated or reclined.
        /// </summary>
        public double PointerCenterHeight
        {
            get
            {
                return tuning.PointerCenterHeight;
            }
            set
            {
                tuning.PointerCenterHeight = value;
                // The pointer mapping frame just moved, so old filter history is meaningless.
                cursorMapper.ResetSmoothing();
                ClearCursorState();
            }
        }

        /// <summary>
        /// Rebuilds the input rectangle and scaling mode from the calibration settings.
        ///
        /// Calibrated mode uses ScaleAlignment.Both so the axes scale independently, which is
        /// the whole point: on a dual-monitor desktop a single uniform scale either makes
        /// horizontal reach impossible or vertical aim hopelessly twitchy. In calibrated mode
        /// the rectangle alone sets the scale, so Movement Scale is not applied; otherwise a
        /// Movement Scale below 1 would silently put the desktop edges beyond the calibrated
        /// reach. Uncalibrated keeps the original rect, LongerRange alignment and Movement Scale
        /// untouched.
        /// </summary>
        private void ApplyInputMapping()
        {
            if (calibration.UseCalibratedRange)
            {
                cursorMapper.ScaleAlign = CursorMapper.ScaleAlignment.Both;
                cursorMapper.InputRect = calibration.BuildInputRect();
                cursorMapper.MoveScale = 1;
            }
            else
            {
                cursorMapper.ScaleAlign = CursorMapper.ScaleAlignment.LongerRange;
                cursorMapper.InputRect = gestureRect;
                cursorMapper.MoveScale = moveScale;
            }

            cursorMapper.ResetSmoothing();
            stationaryLock.Reset();
            ClearCursorState();
            UpdateCalibrationDiagnostics();
        }

        /// <summary>
        /// Starts the guided five-point calibration. Cursor output and actions are held off
        /// while capturing, so reaching for the extents does not fling the pointer around or
        /// click on anything.
        /// </summary>
        /// <returns>False when no control mode is selected, so the sensor is closed.</returns>
        public bool BeginCalibration()
        {
            if (_mode == ControlMode.Disabled)
            {
                return false;
            }

            ToggleHoverTimer(false);
            ReleaseAllGrips();
            ResetControlState();
            calibration.BeginCapture();
            UpdateOutputLoopState();
            UpdateCalibrationDiagnostics();
            RuntimeLog.Write("Calibration started");
            ActivityLog.Post(ActivityKind.Calibration, "Calibration started", "Guided 5-point capture with the right hand", "control center");
            return true;
        }

        public void CancelCalibration()
        {
            bool wasCapturing = calibration.IsCapturing;
            calibration.CancelCapture();
            ResetControlState();
            UpdateOutputLoopState();
            UpdateCalibrationDiagnostics();
            RuntimeLog.Write("Calibration cancelled");
            if (wasCapturing)
            {
                ActivityLog.Post(ActivityKind.Calibration, "Calibration cancelled", "Previous mapping kept", "control center");
            }
        }

        /// <summary>
        /// Adopts a completed capture. Runs from the frame handler on the frame the last point
        /// is captured.
        /// </summary>
        private void FinishCalibration()
        {
            double heightAdjustment;
            bool accepted = calibration.EndCapture(out heightAdjustment);

            if (accepted)
            {
                // The captured vertical centre belongs to PointerCenterHeight, which already
                // owns the vertical origin, rather than being duplicated in the rectangle.
                tuning.PointerCenterHeight += heightAdjustment;
            }

            ApplyInputMapping();
            ResetControlState();
            UpdateOutputLoopState();
            CalibrationVersion++;
            RuntimeLog.Write(calibration.LastResultText);
            ActivityLog.Post(ActivityKind.Calibration, accepted ? "Calibration complete" : "Calibration rejected",
                calibration.LastResultText, "sensor");
        }

        /// <summary>
        /// Increments whenever a calibration capture finishes, so the UI knows to re-read the
        /// ranges, the pointer height and the calibrated flag.
        /// </summary>
        public int CalibrationVersion { get; private set; }

        /// <summary>
        /// Current guided-calibration instruction, or the last result when idle.
        /// </summary>
        public string CalibrationPrompt
        {
            get
            {
                return calibration.PromptText;
            }
        }

        private void UpdateCalibrationDiagnostics()
        {
            Diagnostics.CalibrationStep = calibration.IsCapturing ? (int)calibration.Step : 0;
            Diagnostics.CalibrationHoldProgress = calibration.HoldProgress;
            Diagnostics.CalibrationWaitingForHand = calibration.IsWaitingForHand;

            if (calibration.IsCapturing)
            {
                Diagnostics.Calibration = "CAPTURING step " + calibration.Step;
                return;
            }

            Diagnostics.Calibration = calibration.UseCalibratedRange
                ? "On  X " + calibration.HandRangeX.ToString("0.00")
                    + " Y " + calibration.HandRangeY.ToString("0.00")
                    + " m  cX " + calibration.HandCenterX.ToString("+0.00;-0.00")
                    + "  ptrH " + tuning.PointerCenterHeight.ToString("0.00")
                : "Off (uniform scale x" + moveScale.ToString("0.00") + ")";
        }

        /// <summary>
        /// Metres the hand must be in front of SpineBase to engage control.
        /// </summary>
        public double ForwardActivationDistance
        {
            get
            {
                return tuning.ForwardActivationDistance;
            }
            set
            {
                tuning.ForwardActivationDistance = value;
            }
        }

        /// <summary>
        /// Metres above SpineBase the hand must reach to engage control.
        /// </summary>
        public double ActivationMinHeight
        {
            get
            {
                return tuning.ActivationMinHeight;
            }
            set
            {
                tuning.ActivationMinHeight = value;
            }
        }

        /// <summary>
        /// Scroll rate, in wheel notches per second per metre, at the curve's reference offset.
        /// </summary>
        public double ScrollSpeed
        {
            get
            {
                return tuning.ScrollSpeed;
            }
            set
            {
                tuning.ScrollSpeed = value;
            }
        }

        /// <summary>
        /// Scroll rate curve exponent. 1 is linear.
        /// </summary>
        public double ScrollCurve
        {
            get
            {
                return tuning.ScrollCurve;
            }
            set
            {
                tuning.ScrollCurve = value;
            }
        }

        public bool InvertScroll
        {
            get
            {
                return tuning.InvertScroll;
            }
            set
            {
                tuning.InvertScroll = value;
            }
        }

        /// <summary>
        /// Seconds of good right-hand samples before a pointer session takes the cursor.
        /// </summary>
        public double PointerSettleTime
        {
            get
            {
                return tuning.PointerSettleTime;
            }
            set
            {
                tuning.PointerSettleTime = value;
            }
        }

        public bool StationaryLockEnabled
        {
            get
            {
                return tuning.StationaryLockEnabled;
            }
            set
            {
                tuning.StationaryLockEnabled = value;
                stationaryLock.Enabled = value;
                stationaryLock.Reset();
            }
        }

        /// <summary>
        /// Output pixels the pointer may wander within while the lock dwell counts down.
        /// </summary>
        public double StationaryLockRadius
        {
            get
            {
                return tuning.StationaryLockRadius;
            }
            set
            {
                tuning.StationaryLockRadius = value;
                stationaryLock.LockRadius = value;
            }
        }

        /// <summary>
        /// Seconds of stillness before the pointer locks.
        /// </summary>
        public double StationaryLockDwell
        {
            get
            {
                return tuning.StationaryLockDwell;
            }
            set
            {
                tuning.StationaryLockDwell = value;
                stationaryLock.LockDwell = value;
            }
        }

        /// <summary>
        /// Output pixels from the anchor that break the lock. Should stay above the lock radius;
        /// that gap is the hysteresis.
        /// </summary>
        public double StationaryBreakoutRadius
        {
            get
            {
                return tuning.StationaryBreakoutRadius;
            }
            set
            {
                tuning.StationaryBreakoutRadius = value;
                stationaryLock.BreakoutRadius = value;
            }
        }

        /// <summary>
        /// Switches the pointer mapping from one uniform scale to independent per-axis ranges.
        /// Off by default, so an existing tuned MoveScale keeps behaving exactly as before.
        /// </summary>
        public bool UseCalibratedRange
        {
            get
            {
                return calibration.UseCalibratedRange;
            }
            set
            {
                calibration.UseCalibratedRange = value;
                ApplyInputMapping();
            }
        }

        /// <summary>
        /// Full width in metres of the comfortable hand rectangle, mapped to the whole virtual
        /// desktop width.
        /// </summary>
        public double HandRangeX
        {
            get
            {
                return calibration.HandRangeX;
            }
            set
            {
                calibration.HandRangeX = value;
                ApplyInputMapping();
            }
        }

        public double HandRangeY
        {
            get
            {
                return calibration.HandRangeY;
            }
            set
            {
                calibration.HandRangeY = value;
                ApplyInputMapping();
            }
        }

        public double HandCenterX
        {
            get
            {
                return calibration.HandCenterX;
            }
            set
            {
                calibration.HandCenterX = value;
                ApplyInputMapping();
            }
        }

        public bool IsCalibrating
        {
            get
            {
                return calibration.IsCapturing;
            }
        }

        /// <summary>
        /// Metres of horizontal travel required for a swipe.
        /// </summary>
        public double SwipeMinDisplacement
        {
            get
            {
                return tuning.SwipeMinDisplacement;
            }
            set
            {
                tuning.SwipeMinDisplacement = value;
            }
        }

        /// <summary>
        /// Rect value worked out by pointing to left, top, right, bottom spot with my hand as the ideal edges, and noting down the x, y values got from GetHandRelativePosition.
        /// This may only fit more for me, and you can test out your rect value. Approximately it works fine for most people.
        ///
        /// The vertical origin of this rect is now supplied separately by
        /// GestureTuning.PointerCenterHeight, so the rect only describes the size of the region.
        /// </summary>
        private MRect gestureRect = new MRect(-0.18, 1.65, 0.18, -1.65);

        private bool[] handGrips = new bool[2] { false, false };

        private readonly HandStateFilter[] handStateFilters = new HandStateFilter[2]
        {
            new HandStateFilter(),
            new HandStateFilter()
        };

        private double _hoverDuration = 2;

        /// <summary>
        /// Wait time for a hover gesture.
        /// </summary>
        public double HoverDuration {
            get
            {
                return _hoverDuration;
            }
            set
            {
                _hoverDuration = value;
                hoverTimer.Interval = TimeSpan.FromSeconds(value);
            }
        }

        /// <summary>
        /// Hand moves further than distance will cause a hover fail.
        /// Default as 20, this needs to be modified according to usual movement distance/speed in your specific case.
        /// </summary>
        public double HoverRange { get; set; } = 20;

        /// <summary>
        /// The user's Movement Scale. Applied to the uncalibrated mapping only; see
        /// ApplyInputMapping.
        /// </summary>
        private double moveScale = 1;

        public double MoveScale
        {
            get
            {
                return moveScale;
            }
            set
            {
                moveScale = value;
                if (!calibration.UseCalibratedRange)
                {
                    cursorMapper.MoveScale = value;
                }

                UpdateCalibrationDiagnostics();
            }
        }

        public double HandLiftYForClick { get; set; } = 0.02f;

        private MVector2 lastCursorPos = MVector2.Zero;
        private bool hasLastCursorPos = false;

        /// <summary>
        /// Latest position handed to the output loop, including any click anchor offset.
        /// </summary>
        private MVector2 latestTarget = MVector2.Zero;
        private bool hasLatestTarget = false;

        /// <summary>
        /// While true the cursor is pinned at frozenTarget and hand movement is ignored.
        /// </summary>
        private bool isCursorFrozen = false;
        private MVector2 frozenTarget = MVector2.Zero;
        private double freezeHoldRemaining = 0;

        /// <summary>
        /// Difference between where the cursor was pinned and where the hand actually ended up
        /// during a grip. Applied after the freeze so the cursor does not jump to the drifted
        /// hand position, and decayed away once the button is released.
        /// </summary>
        private MVector2 clickAnchorOffset = MVector2.Zero;

        /// <summary>
        /// Time constant for returning to true absolute mapping after a grip is released.
        /// </summary>
        private const double ANCHOR_DECAY_TIME_CONSTANT = 0.12;

        private const double DEFAULT_FRAME_DELTA = 1.0 / 30.0;
        private const double MIN_FRAME_DELTA = 0.002;
        private const double MAX_FRAME_DELTA = 0.2;

        /// <summary>
        /// How long body frames may stall before the watchdog force-releases the mouse button.
        /// </summary>
        private const double FRAME_STALL_TIMEOUT = 1.0;

        private TimeSpan lastFrameTime = TimeSpan.Zero;
        private bool hasLastFrameTime = false;

        private readonly Stopwatch frameClock = Stopwatch.StartNew();
        private double lastFrameArrivalTime = 0;
        private bool hasFrameArrived = false;

        /// <summary>
        /// Time from pointer samples skipped as glitches, carried into the next accepted
        /// sample so the time-aware filter still sees the real elapsed time.
        /// </summary>
        private double skippedPointerTime;

        // Frame-rate instrumentation, for diagnostics and the runtime log.
        private double frameDeltaAverage = DEFAULT_FRAME_DELTA;
        private double frameDeltaRecentMax;
        private double sessionStartTime;
        private int sessionFrames;
        private double sessionDeltaMax;

        /// <summary>
        /// Timer for hover detection.
        /// </summary>
        private DispatcherTimer hoverTimer = new DispatcherTimer();

        /// <summary>
        /// Watchdog that catches the sensor going away without raising a tracking-lost event.
        /// </summary>
        private DispatcherTimer safetyTimer = new DispatcherTimer();

        private const int NONE_USED = -1;

        /// <summary>
        /// PointerHand while a pointer session is active (stabilized and driving the cursor),
        /// otherwise NONE_USED. It can never hold the left hand.
        /// </summary>
        private int usedHandIndex = NONE_USED;
        private bool hoverClicked = false;

        public KinectCursor()
        {
            // Physical-pixel geometry of the whole virtual desktop, which is the coordinate
            // space SetCursorPos works in. Replaces WPF's primary-screen-only, DPI-scaled values.
            desktop = DesktopLayout.Capture();
            cursorMapper = new CursorMapper(gestureRect, desktop.Bounds, CursorMapper.ScaleAlignment.LongerRange);
            cursorMapper.Desktop = desktop;

            outputLoop = new CursorOutputLoop();

            Diagnostics = new GestureDiagnostics();
            stabilizer = new PointerStabilizer(tuning);
            gestureEngine = new GestureEngine(tuning, actionRouter);
            actionRouter.ActionExecuted += ActionRouter_ActionExecuted;
            actionRouter.ControlGate = this;

            stationaryLock.Enabled = tuning.StationaryLockEnabled;
            stationaryLock.LockRadius = tuning.StationaryLockRadius;
            stationaryLock.LockDwell = tuning.StationaryLockDwell;
            stationaryLock.BreakoutRadius = tuning.StationaryBreakoutRadius;

            UpdateDesktopDiagnostics();
            UpdateCalibrationDiagnostics();
            RuntimeLog.Write("Engine created. Desktop " + desktop.Describe());

            sensorReader = new KinectReader(false);
            sensorReader.OnTrackedBody += Kinect_OnTrackedBody;
            sensorReader.OnLostTracking += Kinect_OnLostTracking;
            hoverTimer.Interval = TimeSpan.FromSeconds(HoverDuration);
            hoverTimer.Tick += new EventHandler(HoverTimer_Tick);

            safetyTimer.Interval = TimeSpan.FromSeconds(0.25);
            safetyTimer.Tick += new EventHandler(SafetyTimer_Tick);

            SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        }

        /// <summary>
        /// Monitors added, removed or rearranged change the virtual desktop, so the output rect
        /// has to be re-read rather than captured once at startup.
        /// </summary>
        private void SystemEvents_DisplaySettingsChanged(object sender, EventArgs e)
        {
            // Raised on the SystemEvents thread, so hop back to the thread that owns our state.
            Application app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(new Action(ApplyDisplayBounds));
            }
            else
            {
                ApplyDisplayBounds();
            }
        }

        private void ApplyDisplayBounds()
        {
            desktop = DesktopLayout.Capture();
            cursorMapper.OutputRect = desktop.Bounds;
            cursorMapper.Desktop = desktop;

            // The coordinate space just moved under us, so abandon any drag rather than
            // continuing it against a stale mapping.
            ReleaseAllGrips();
            ResetControlState();
            UpdateDesktopDiagnostics();
            RuntimeLog.Write("Display settings changed. Desktop " + desktop.Describe());
            ActivityLog.Post(ActivityKind.System, "Display layout changed", desktop.Describe(), "windows");

            EventHandler handler = DesktopChanged;
            if (handler != null)
            {
                handler.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Raised on the UI thread after the desktop geometry has been re-captured.
        /// </summary>
        public event EventHandler DesktopChanged;

        private void UpdateDesktopDiagnostics()
        {
            Diagnostics.DesktopBounds = desktop.Describe();
        }

        private void Kinect_OnLostTracking(object sender, EventArgs e)
        {
            EndSessionLog("tracking lost");
            ToggleHoverTimer(false);
            ReleaseAllGrips();
            ResetControlState();
            SetDiagnosticsIdle(false);
        }

        private void SafetyTimer_Tick(object sender, EventArgs e)
        {
            if (_mode == ControlMode.Disabled || !hasFrameArrived)
            {
                return;
            }

            if (frameClock.Elapsed.TotalSeconds - lastFrameArrivalTime < FRAME_STALL_TIMEOUT)
            {
                return;
            }

            // Frames stopped arriving without a tracking-lost event - sensor unplugged, driver
            // reset, machine resumed. Never leave a mouse button held down because of it.
            EndSessionLog("frame stall");
            RuntimeLog.Write("Frame stall > " + FRAME_STALL_TIMEOUT + " s: released and reset");
            ActivityLog.Post(ActivityKind.System, "Frame stall", "No body frames for " + FRAME_STALL_TIMEOUT + " s - buttons released, session reset", "watchdog");
            ToggleHoverTimer(false);
            ReleaseAllGrips();
            ResetControlState();
            SetDiagnosticsIdle(false);
        }

        private void Kinect_OnTrackedBody(object sender, BodyEventArgs e)
        {
            Body body = e.BodyData;

            if (Mode == ControlMode.Disabled)
            {
                return;
            }

            lastFrameArrivalTime = frameClock.Elapsed.TotalSeconds;
            hasFrameArrived = true;

            double deltaTime = GetDeltaTime(e.RelativeTime);
            UpdateFrameStatistics(deltaTime);

            // Normalize the body into the gesture context first, so both the pointer path below
            // and the recognizers afterwards read exactly the same view of this frame.
            BuildHandSnapshots(body, deltaTime);

            // Control switched off, or a calibration capture in progress: the body is still
            // being tracked and the clap recognizer still runs, but nothing may touch the
            // machine.
            if (!controlEnabled || calibration.IsCapturing)
            {
                if (calibration.IsCapturing)
                {
                    RecordCalibrationSample(body, deltaTime);
                }

                RunGestureLayer();
                UpdateDiagnostics();
                return;
            }

            UpdatePointerHand(body, deltaTime);
            UpdateSecondaryHandClicking(body, deltaTime);

            ToggleHoverTimer(Mode == ControlMode.HoverToClick && usedHandIndex != NONE_USED);

            // The pointer session state is only known once the pointer path has run, so the
            // gesture layer is driven here, after it.
            RunGestureLayer();

            UpdateDiagnostics();
        }

        /// <summary>
        /// The right hand's whole pointer path: session start (via the stabilizer), grip, the
        /// filter chain and publishing the target. The left hand never enters here.
        /// </summary>
        private void UpdatePointerHand(Body body, double deltaTime)
        {
            HandSnapshot hand = gestureContext.Hands[PointerHand];

            if (!hand.IsActivated)
            {
                // Right hand down or gone: pointer control goes idle. There is no fallback to
                // the left hand.
                if (usedHandIndex != NONE_USED)
                {
                    EndPointerSession("right hand left the zone");
                }
                else
                {
                    stabilizer.Reset();
                    ReleaseGrip(PointerHand);
                }

                return;
            }

            MVector2 handPos = body.GetHandRelativePosition(false, tuning.PointerCenterHeight);

            if (usedHandIndex == NONE_USED)
            {
                bool isGoodSample = hand.JointState == TrackingState.Tracked && hand.PositionWeight > 0;
                if (!stabilizer.Stabilize(handPos, isGoodSample, deltaTime))
                {
                    return;
                }

                usedHandIndex = PointerHand;
                BeginControlSession(stabilizer.SeedPosition);
            }

            // Grip is resolved before the cursor target so that a fist which has just started
            // closing can pin the cursor on this same frame, rather than a frame later once the
            // press is confirmed.
            if (Mode == ControlMode.GripToPress)
            {
                DoMouseControlByHandState(PointerHand, hand, deltaTime);
            }

            PointerStabilizer.Verdict verdict = stabilizer.CheckActiveSample(handPos, deltaTime + skippedPointerTime);
            if (verdict == PointerStabilizer.Verdict.Destabilize)
            {
                EndPointerSession("repeated tracking glitches");
                return;
            }

            if (verdict == PointerStabilizer.Verdict.SkipGlitch)
            {
                // One implausible jump: hold the previous target rather than filtering it in.
                skippedPointerTime += deltaTime;
                return;
            }

            double filterDelta = deltaTime + skippedPointerTime;
            skippedPointerTime = 0;

            MVector2 filteredTarget = cursorMapper.GetSmoothedOutputPosition(handPos, filterDelta, hand.PositionWeight);

            // Stationary lock sits between the filter chain and the click anchor. It is stood
            // down whenever a button is held, so it can never fight a deliberate drag; during a
            // drag the click anchor is already providing the stability.
            filteredTarget = stationaryLock.Apply(filteredTarget, filterDelta, !IsAnyGripHeld());

            PublishCursorTarget(filteredTarget, filterDelta, ShouldHoldCursorAnchor(PointerHand));

            if (Mode == ControlMode.HoverToClick)
            {
                if (hasLastCursorPos && (latestTarget - lastCursorPos).Length() > HoverRange)
                {
                    ToggleHoverTimer(false);
                    hoverClicked = false;
                }

                lastCursorPos = latestTarget;
                hasLastCursorPos = true;
            }
        }

        /// <summary>
        /// The left hand's job in the two-hand clicking modes, which predate the gesture
        /// vocabulary: in MoveGripPressing its fist holds the button, in MoveLiftClicking lifting
        /// it clicks. Both act only while the right hand has an active pointer session. In every
        /// other mode the left hand never touches a button.
        /// </summary>
        private void UpdateSecondaryHandClicking(Body body, double deltaTime)
        {
            HandSnapshot hand = gestureContext.Hands[SecondaryHand];
            bool isPointerLive = usedHandIndex != NONE_USED;

            if (Mode == ControlMode.MoveGripPressing && isPointerLive && hand.IsActivated)
            {
                DoMouseControlByHandState(SecondaryHand, hand, deltaTime);
            }
            else if (Mode == ControlMode.MoveLiftClicking && isPointerLive && !hand.IsActivated && hand.PositionWeight > 0)
            {
                DoMouseClickByHandLifting(SecondaryHand, body.GetHandRelativePosition(true, tuning.PointerCenterHeight));
            }
            else
            {
                // Release mouse button when it's not regularly released, such as hand tracking lost.
                ReleaseGrip(SecondaryHand);
            }
        }

        private void RunGestureLayer()
        {
            gestureContext.ControllingHandIndex = usedHandIndex;
            gestureContext.IsDragActive = IsAnyGripHeld();
            gestureContext.IsGestureVocabularyEnabled = IsGestureVocabularyEnabled();
            gestureContext.IsControlEnabled = controlEnabled && !calibration.IsCapturing;
            gestureEngine.Update(gestureContext);
        }

        private void RecordCalibrationSample(Body body, double deltaTime)
        {
            HandSnapshot hand = gestureContext.Hands[PointerHand];

            // Only a confidently tracked, raised right hand contributes, so the captured range
            // is one that can actually be reached while pointing.
            if (hand.IsActivated && hand.PositionWeight >= 1)
            {
                calibration.AddSample(body.GetHandRelativePosition(false, tuning.PointerCenterHeight), deltaTime);
            }
            else
            {
                calibration.NoSample();
            }

            if (calibration.IsCaptureComplete)
            {
                FinishCalibration();
            }

            UpdateCalibrationDiagnostics();
        }

        /// <summary>
        /// Fills the per-hand view of this frame: body-relative geometry, hand state and
        /// confidence, plus the activation latch.
        /// </summary>
        private void BuildHandSnapshots(Body body, double deltaTime)
        {
            gestureContext.DeltaTime = deltaTime;
            gestureContext.IsBodyTracked = true;

            for (int i = 0; i < 2; i++)
            {
                bool isLeft = (i == 0);

                HandSnapshot snapshot = new HandSnapshot();
                snapshot.Position = body.GetHandBodyRelativePosition(isLeft);
                snapshot.ForwardDistance = body.GetHandForwardDistance(isLeft);
                snapshot.State = body.GetHandState(isLeft);
                snapshot.IsConfident = body.IsHandStateConfident(isLeft);
                snapshot.PositionWeight = body.GetHandPositionWeight(isLeft);
                snapshot.JointState = body.GetHandJointState(isLeft);
                snapshot.IsActivated = UpdateHandActivation(i, snapshot);

                gestureContext.Hands[i] = snapshot;
            }
        }

        /// <summary>
        /// Activation zone test with hysteresis.
        ///
        /// Engaging needs both a forward reach and a minimum height above SpineBase. The height
        /// requirement matters when reclined: leaning back puts a hand resting in the lap in
        /// front of the spine in Z, which would satisfy a forward-only test on its own.
        ///
        /// Disengaging requires falling a further ActivationReleaseMargin below whichever
        /// threshold was crossed, so a hand held near the boundary does not flicker the session
        /// on and off.
        /// </summary>
        private bool UpdateHandActivation(int handIndex, HandSnapshot snapshot)
        {
            if (snapshot.PositionWeight <= 0)
            {
                handActivated[handIndex] = false;
                return false;
            }

            if (handActivated[handIndex])
            {
                double margin = tuning.ActivationReleaseMargin;
                if (snapshot.Height < tuning.ActivationMinHeight - margin
                    || snapshot.ForwardDistance < tuning.ForwardActivationDistance - margin)
                {
                    handActivated[handIndex] = false;
                }
            }
            else if (snapshot.Height >= tuning.ActivationMinHeight
                && snapshot.ForwardDistance >= tuning.ForwardActivationDistance)
            {
                handActivated[handIndex] = true;
            }

            return handActivated[handIndex];
        }

        /// <summary>
        /// Whether the lasso/scroll/swipe vocabulary is live.
        ///
        /// It is confined to GripToPress: that is the one mode where the right hand's hand
        /// state already drives actions and the left hand has no existing job.
        /// MoveGripPressing and MoveLiftClicking both give the left hand to clicking, and
        /// MoveOnly means pointer-only by definition.
        /// </summary>
        private bool IsGestureVocabularyEnabled()
        {
            return _mode == ControlMode.GripToPress;
        }

        /// <summary>
        /// Real elapsed time between sensor frames, from the sensor's own timestamps. Clamped
        /// so a stall or a duplicate timestamp cannot destabilise the filters.
        /// </summary>
        private double GetDeltaTime(TimeSpan relativeTime)
        {
            if (!hasLastFrameTime)
            {
                lastFrameTime = relativeTime;
                hasLastFrameTime = true;
                return DEFAULT_FRAME_DELTA;
            }

            double deltaTime = (relativeTime - lastFrameTime).TotalSeconds;
            lastFrameTime = relativeTime;

            if (deltaTime <= 0)
            {
                return DEFAULT_FRAME_DELTA;
            }

            if (deltaTime < MIN_FRAME_DELTA)
            {
                return MIN_FRAME_DELTA;
            }

            return deltaTime > MAX_FRAME_DELTA ? MAX_FRAME_DELTA : deltaTime;
        }

        private void UpdateFrameStatistics(double deltaTime)
        {
            frameDeltaAverage += (deltaTime - frameDeltaAverage) * (1 - Math.Exp(-deltaTime / 1.0));

            // A decaying peak: shows a recent hitch for a couple of seconds, then fades.
            frameDeltaRecentMax = Math.Max(deltaTime, frameDeltaRecentMax * Math.Exp(-deltaTime / 2.0));

            if (usedHandIndex != NONE_USED)
            {
                sessionFrames++;
                sessionDeltaMax = Math.Max(sessionDeltaMax, deltaTime);
            }
        }

        /// <summary>
        /// Whether the right hand currently wants the cursor pinned: either a press is being
        /// confirmed, or one was just confirmed and is still inside its freeze window. Only the
        /// pointer hand can pin it - in the two-hand modes the clicking hand is the left hand
        /// and cannot disturb the pointer.
        /// </summary>
        private bool ShouldHoldCursorAnchor(int handIndex)
        {
            if (handIndex != usedHandIndex)
            {
                return false;
            }

            HandStateFilter filter = handStateFilters[handIndex];

            if (filter.IsPressPending)
            {
                return true;
            }

            return filter.IsClosed && freezeHoldRemaining > 0;
        }

        private void PublishCursorTarget(MVector2 filteredTarget, double deltaTime, bool holdAnchor)
        {
            if (holdAnchor)
            {
                if (!isCursorFrozen)
                {
                    isCursorFrozen = true;
                    frozenTarget = hasLatestTarget ? latestTarget : filteredTarget;
                }

                if (freezeHoldRemaining > 0)
                {
                    freezeHoldRemaining -= deltaTime;
                }

                SetCursorTarget(frozenTarget);
                return;
            }

            if (isCursorFrozen)
            {
                isCursorFrozen = false;

                // Carry the drift that happened while the cursor was pinned, so releasing the
                // freeze does not snap the pointer over to wherever the hand ended up. Held
                // constant while the button stays down, which is what keeps a drag anchored to
                // the spot that was actually clicked.
                clickAnchorOffset = frozenTarget - filteredTarget;
            }

            if (!IsAnyGripHeld())
            {
                clickAnchorOffset = clickAnchorOffset * Math.Exp(-deltaTime / ANCHOR_DECAY_TIME_CONSTANT);
            }

            SetCursorTarget(filteredTarget + clickAnchorOffset);
        }

        private void SetCursorTarget(MVector2 target)
        {
            // Keep the target on a real monitor. The mapped position is already clamped before
            // filtering; this catches the click anchor offset pushing it off again.
            target = desktop.Clamp(target);
            latestTarget = target;
            hasLatestTarget = true;
            outputLoop.SetTarget(target);

            Diagnostics.CursorX = target.X;
            Diagnostics.CursorY = target.Y;
            Diagnostics.HasCursorTarget = true;
        }

        private bool IsAnyGripHeld()
        {
            return handGrips[0] || handGrips[1];
        }

        /// <summary>
        /// The right hand has stabilized and takes control. Clears everything left over from
        /// any previous session and seeds the filter at the stabilized hand position, so the
        /// cursor snaps once to where the hand is and continues from there.
        /// </summary>
        private void BeginControlSession(MVector2 seedPosition)
        {
            ReleaseAllGrips();
            cursorMapper.ResetSmoothing();
            handStateFilters[0].Reset();
            handStateFilters[1].Reset();
            // A gesture belongs to the session that started it. The clap is deliberately
            // spared: bringing the hands together to clap can itself start or end a session,
            // and clearing it here would make a double clap impossible to complete.
            gestureEngine.ResetControlSession();
            ClearCursorState();
            cursorMapper.SeedSmoothing(seedPosition);
            skippedPointerTime = 0;

            sessionStartTime = frameClock.Elapsed.TotalSeconds;
            sessionFrames = 0;
            sessionDeltaMax = 0;
            RuntimeLog.Write("Pointer session ACTIVE (body q " + sensorReader.LockedBodyScore + "/6)");
        }

        /// <summary>
        /// Ends the active pointer session: releases buttons, hands the cursor back to the
        /// physical mouse and returns the stabilizer to Waiting.
        /// </summary>
        private void EndPointerSession(string reason)
        {
            EndSessionLog(reason);
            usedHandIndex = NONE_USED;
            ReleaseGrip(PointerHand);
            EndControlSession();
        }

        /// <summary>
        /// Drops any held button - a drag cannot be meaningfully continued without cursor
        /// control - and every piece of session state.
        /// </summary>
        private void EndControlSession()
        {
            ReleaseAllGrips();
            cursorMapper.ResetSmoothing();
            handStateFilters[0].Reset();
            handStateFilters[1].Reset();
            gestureEngine.ResetControlSession();
            stabilizer.Reset();
            ClearCursorState();
        }

        private void EndSessionLog(string reason)
        {
            if (usedHandIndex == NONE_USED)
            {
                return;
            }

            double duration = frameClock.Elapsed.TotalSeconds - sessionStartTime;
            RuntimeLog.Write("Pointer session ended (" + reason + "): " + duration.ToString("0.0") + " s, "
                + sessionFrames + " frames, avg dt " + (frameDeltaAverage * 1000).ToString("0.0")
                + " ms, max dt " + (sessionDeltaMax * 1000).ToString("0") + " ms, glitches "
                + stabilizer.GlitchCount + ", noise " + cursorMapper.ResidualNoise.ToString("0") + " px");
        }

        private void ClearCursorState()
        {
            // The lock is part of the cursor's state, so it clears with everything else. This
            // is the single choke point every teardown path runs through.
            stationaryLock.Reset();

            latestTarget = MVector2.Zero;
            hasLatestTarget = false;
            isCursorFrozen = false;
            frozenTarget = MVector2.Zero;
            freezeHoldRemaining = 0;
            clickAnchorOffset = MVector2.Zero;
            lastCursorPos = MVector2.Zero;
            hasLastCursorPos = false;
            hoverClicked = false;
            hasLastFrameTime = false;
            skippedPointerTime = 0;
            outputLoop.ClearTarget();
            Diagnostics.HasCursorTarget = false;
        }

        /// <summary>
        /// Full reset of everything derived from tracking. Safe to call at any time; does not
        /// itself release the mouse button, so call ReleaseAllGrips first where that matters.
        /// </summary>
        private void ResetControlState()
        {
            usedHandIndex = NONE_USED;
            hasFrameArrived = false;
            cursorMapper.ResetSmoothing();
            handStateFilters[0].Reset();
            handStateFilters[1].Reset();
            stabilizer.Reset();

            // Gestures and the clutch must not survive the session that produced them, and the
            // activation latches have to drop so reacquisition requires crossing the full
            // threshold again rather than only the hysteresis-relaxed one.
            gestureEngine.Reset();
            gestureContext.Clear();
            handActivated[0] = false;
            handActivated[1] = false;

            ClearCursorState();
        }

        private void DoMouseControlByHandState(int handIndex, HandSnapshot hand, double deltaTime)
        {
            bool isClosed = handStateFilters[handIndex].Update(hand.State, hand.IsConfident, deltaTime);

            UpdateHandMouseControl(handIndex, isClosed ? MouseControlState.ShouldPress : MouseControlState.ShouldRelease);
        }

        private void DoMouseClickByHandLifting(int handIndex, MVector2 handRelativePos)
        {
            UpdateHandMouseControl(handIndex, handRelativePos.Y > HandLiftYForClick ? MouseControlState.ShouldClick : MouseControlState.ShouldRelease);
        }

        private enum MouseControlState
        {
            None,
            ShouldPress,
            ShouldRelease,
            ShouldClick,
        }

        private void UpdateHandMouseControl(int handIndex, MouseControlState controlState)
        {
            if (controlState == MouseControlState.ShouldClick)
            {
                if (!handGrips[handIndex])
                {
                    actionRouter.Execute(ControlAction.Of(ControlActionType.LeftClick));
                    handGrips[handIndex] = true;
                    BeginClickFreeze(handIndex);
                }
            }else if (controlState == MouseControlState.ShouldPress)
            {
                if (!handGrips[handIndex])
                {
                    actionRouter.Execute(ControlAction.Of(ControlActionType.LeftMouseDown));
                    handGrips[handIndex] = true;
                    BeginClickFreeze(handIndex);
                }
            }
            else if (controlState == MouseControlState.ShouldRelease && handGrips[handIndex])
            {
                ReleaseGrip(handIndex);
            }
        }

        /// <summary>
        /// Starts the post-press settling window. The cursor is already pinned from the moment
        /// the fist began closing; this keeps it pinned a little longer so the press lands and
        /// registers before the hand is allowed to drag.
        /// </summary>
        private void BeginClickFreeze(int handIndex)
        {
            if (handIndex == usedHandIndex)
            {
                freezeHoldRemaining = ClickFreezeDuration;
            }
        }

        private void ReleaseGrip(int index)
        {
            if (handGrips[index])
            {
                actionRouter.Execute(ControlAction.Of(ControlActionType.LeftMouseUp));
                handGrips[index] = false;
            }

            // Clear the debounced state too. A hand that was closed when it stopped being
            // tracked would otherwise come back already committed to Closed and press again
            // immediately, skipping the confirmation window.
            handStateFilters[index].Reset();
        }

        private void ReleaseAllGrips()
        {
            ReleaseGrip(0);
            ReleaseGrip(1);
        }

        private void ToggleHoverTimer(bool isOn)
        {
            if(hoverTimer.IsEnabled != isOn)
            {
                if (isOn)
                {
                    hoverTimer.Start();
                }
                else
                {
                    hoverTimer.Stop();
                }
            }
        }

        void HoverTimer_Tick(object sender, EventArgs e)
        {
            if (!hoverClicked)
            {
                actionRouter.Execute(ControlAction.Of(ControlActionType.LeftClick));
                hoverTimer.Stop();
                hoverClicked = true;
            }
        }

        private void ActionRouter_ActionExecuted(object sender, ControlAction action)
        {
            Diagnostics.LastAction = action.ToString();
            PostActionActivity(action, actionRouter.LastSource);
        }

        /// <summary>
        /// Feeds the recent-activity stream. Grip releases are implied by the press that
        /// preceded them, and consecutive scroll notches in the same direction collapse into
        /// one line with a count, so a long scroll does not drown everything else.
        /// </summary>
        private static void PostActionActivity(ControlAction action, string source)
        {
            switch (action.Type)
            {
                case ControlActionType.LeftMouseUp:
                    return;

                case ControlActionType.LeftMouseDown:
                    ActivityLog.Post(ActivityKind.Action, "Grip press", "Right fist - click or drag", source);
                    return;

                case ControlActionType.LeftClick:
                    ActivityLog.Post(ActivityKind.Action, "Click", source == ActionRouter.GestureSource ? "Hover / lift click" : null, source);
                    return;

                case ControlActionType.RightClick:
                    ActivityLog.Post(ActivityKind.Action, "Right click", source == ActionRouter.GestureSource ? "Right-hand lasso" : null, source);
                    return;

                case ControlActionType.Scroll:
                    {
                        bool up = action.Value >= 0;
                        ActivityLog.Post(ActivityKind.Action, up ? "Scroll up" : "Scroll down",
                            source == ActionRouter.GestureSource ? "Left fist clutch" : null, source,
                            "scroll" + (up ? "+" : "-"), 1.5);
                        return;
                    }

                case ControlActionType.NextWindow:
                case ControlActionType.PreviousWindow:
                    ActivityLog.Post(ActivityKind.Action, action.ToString(),
                        source == ActionRouter.GestureSource ? "Left fist swipe" : null, source);
                    return;

                case ControlActionType.ToggleControl:
                case ControlActionType.EnableControl:
                case ControlActionType.DisableControl:
                    // SetControlEnabled posts its own entry, with the outcome.
                    return;

                default:
                    ActivityLog.Post(ActivityKind.Action, action.ToString(), null, source);
                    return;
            }
        }

        private static string DescribeHand(HandSnapshot hand)
        {
            if (hand.PositionWeight <= 0)
            {
                return "not tracked";
            }

            return hand.State + "/" + (hand.IsConfident ? "Hi" : "Lo") + "/"
                + (hand.JointState == TrackingState.Tracked ? "T" : "Inf")
                + (hand.IsActivated ? " zone" : "");
        }

        private void UpdateDiagnostics()
        {
            Diagnostics.IsTracking = true;
            Diagnostics.IsControlEnabled = controlEnabled;
            Diagnostics.BodyCount = sensorReader.TrackedBodyCount;
            Diagnostics.BodyScore = sensorReader.LockedBodyScore;
            Diagnostics.Mode = gestureEngine.State.ToString();
            Diagnostics.ClapState = gestureEngine.ClapStateText;
            Diagnostics.ClutchState = gestureEngine.ClutchStateText;
            Diagnostics.SecondaryMode = gestureEngine.SecondaryModeText;
            Diagnostics.ScrollNeutral = gestureEngine.ScrollNeutralHeight;
            Diagnostics.ScrollOffset = gestureEngine.ScrollOffset;
            Diagnostics.ScrollRate = gestureEngine.ScrollRate;
            Diagnostics.LockDisplacement = stationaryLock.LockDisplacement;
            Diagnostics.FrameDeltaMs = frameDeltaAverage * 1000;
            Diagnostics.FrameDeltaMaxMs = frameDeltaRecentMax * 1000;
            Diagnostics.ResidualNoise = cursorMapper.ResidualNoise;
            Diagnostics.GlitchCount = stabilizer.GlitchCount;

            if (!controlEnabled)
            {
                Diagnostics.PointerSession = "Off (control disabled)";
            }
            else if (calibration.IsCapturing)
            {
                Diagnostics.PointerSession = "Off (calibrating)";
            }
            else
            {
                Diagnostics.PointerSession = stabilizer.StateText;
            }

            if (!controlEnabled || usedHandIndex == NONE_USED)
            {
                Diagnostics.PointerState = "-";
            }
            else if (stationaryLock.IsLocked)
            {
                Diagnostics.PointerState = "Locked";
            }
            else if (stationaryLock.IsSettling)
            {
                Diagnostics.PointerState = "Stationary";
            }
            else
            {
                Diagnostics.PointerState = "Moving";
            }

            HandSnapshot right = gestureContext.Hands[PointerHand];
            HandSnapshot left = gestureContext.Hands[SecondaryHand];

            Diagnostics.LeftHandState = DescribeHand(left);
            Diagnostics.RightHandState = DescribeHand(right);
            Diagnostics.Gesture = gestureEngine.ActiveGestureName;

            Diagnostics.ControlHandHeight = right.Height;
            Diagnostics.ControlHandForward = right.ForwardDistance;
            Diagnostics.ControlHandX = right.Position.X;
            Diagnostics.SecondaryHandHeight = left.Height;
            Diagnostics.SecondaryHandX = left.Position.X;
            Diagnostics.SecondaryHandForward = left.ForwardDistance;

            Diagnostics.RightHandTracked = right.PositionWeight > 0;
            Diagnostics.LeftHandTracked = left.PositionWeight > 0;
            Diagnostics.RightHandActivated = right.IsActivated;
            Diagnostics.LeftHandActivated = left.IsActivated;
            Diagnostics.RightHandClosed = right.State == HandState.Closed;
            Diagnostics.LeftHandClosed = left.State == HandState.Closed;
            Diagnostics.RightHandLasso = right.State == HandState.Lasso;
            Diagnostics.IsPointerActive = controlEnabled && usedHandIndex != NONE_USED;
            Diagnostics.IsClutchArmed = gestureEngine.IsSecondaryGestureArmed;
            Diagnostics.IsGripHeld = IsAnyGripHeld();
        }

        private void SetDiagnosticsIdle(bool isTracking)
        {
            Diagnostics.IsTracking = isTracking;
            Diagnostics.IsControlEnabled = controlEnabled;
            Diagnostics.BodyCount = sensorReader.TrackedBodyCount;
            Diagnostics.BodyScore = sensorReader.LockedBodyScore;
            Diagnostics.Mode = GestureState.Idle.ToString();
            Diagnostics.PointerSession = controlEnabled ? "Waiting" : "Off (control disabled)";
            Diagnostics.LeftHandState = "-";
            Diagnostics.RightHandState = "-";
            Diagnostics.ClutchState = "Off";
            Diagnostics.SecondaryMode = "None";
            Diagnostics.Gesture = "None";
            Diagnostics.PointerState = "-";
            Diagnostics.LockDisplacement = 0;
            Diagnostics.ScrollNeutral = null;
            Diagnostics.ScrollOffset = 0;
            Diagnostics.ScrollRate = 0;
            Diagnostics.ClapState = "Idle";
            Diagnostics.ControlHandHeight = 0;
            Diagnostics.ControlHandForward = 0;
            Diagnostics.ControlHandX = 0;
            Diagnostics.SecondaryHandHeight = 0;
            Diagnostics.SecondaryHandX = 0;
            Diagnostics.SecondaryHandForward = 0;
            Diagnostics.RightHandTracked = false;
            Diagnostics.LeftHandTracked = false;
            Diagnostics.RightHandActivated = false;
            Diagnostics.LeftHandActivated = false;
            Diagnostics.RightHandClosed = false;
            Diagnostics.LeftHandClosed = false;
            Diagnostics.RightHandLasso = false;
            Diagnostics.IsPointerActive = false;
            Diagnostics.IsClutchArmed = false;
            Diagnostics.IsGripHeld = false;
            Diagnostics.HasCursorTarget = false;
        }

    }

}
