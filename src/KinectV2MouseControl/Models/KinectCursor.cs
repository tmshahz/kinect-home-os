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

        private readonly StationaryLock stationaryLock = new StationaryLock();
        private readonly PointerCalibration calibration = new PointerCalibration();

        private readonly GestureTuning tuning = new GestureTuning();
        private readonly ActionRouter actionRouter = new ActionRouter();
        private readonly GestureContext gestureContext = new GestureContext();
        private readonly GestureEngine gestureEngine;

        /// <summary>
        /// Live view of what the gesture layer is seeing. Read by the settings window.
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
                _mode = value;
                if (value == ControlMode.Disabled)
                {
                    ToggleHoverTimer(false);
                    // Releasing before anything else: disabling while a fist is closed must
                    // never leave the mouse button held down.
                    ReleaseAllGrips();
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
            SetControlEnabled(!controlEnabled);
        }

        private void SetControlEnabled(bool enabled)
        {
            if (controlEnabled == enabled)
            {
                return;
            }

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
        /// Radius in screen pixels the filtered position must leave before the cursor moves.
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
        /// horizontal reach impossible or vertical aim hopelessly twitchy. Uncalibrated keeps
        /// the original rect and LongerRange alignment untouched.
        /// </summary>
        private void ApplyInputMapping()
        {
            if (calibration.UseCalibratedRange)
            {
                cursorMapper.ScaleAlign = CursorMapper.ScaleAlignment.Both;
                cursorMapper.InputRect = calibration.BuildInputRect();
            }
            else
            {
                cursorMapper.ScaleAlign = CursorMapper.ScaleAlignment.LongerRange;
                cursorMapper.InputRect = gestureRect;
            }

            cursorMapper.ResetSmoothing();
            stationaryLock.Reset();
            ClearCursorState();
            UpdateCalibrationDiagnostics();
        }

        /// <summary>
        /// Starts recording the extents of the controlling hand. Cursor output and actions are
        /// held off while capturing, so sweeping out the comfortable rectangle does not fling
        /// the pointer around or click on anything.
        /// </summary>
        public void BeginCalibration()
        {
            ReleaseAllGrips();
            ResetControlState();
            calibration.BeginCapture();
            UpdateOutputLoopState();
            UpdateCalibrationDiagnostics();
        }

        /// <summary>
        /// Ends recording and adopts the swept rectangle.
        /// </summary>
        /// <returns>True when the sweep was large enough to use.</returns>
        public bool EndCalibration()
        {
            double heightAdjustment;
            bool accepted = calibration.EndCapture(out heightAdjustment);

            if (accepted)
            {
                // The sweep's vertical centre belongs to PointerCenterHeight, which already owns
                // the vertical origin, rather than being duplicated in the rectangle.
                tuning.PointerCenterHeight += heightAdjustment;
            }

            ApplyInputMapping();
            ResetControlState();
            UpdateOutputLoopState();
            return accepted;
        }

        public void CancelCalibration()
        {
            calibration.CancelCapture();
            ResetControlState();
            UpdateOutputLoopState();
            UpdateCalibrationDiagnostics();
        }

        private void UpdateCalibrationDiagnostics()
        {
            if (calibration.IsCapturing)
            {
                Diagnostics.Calibration = "CAPTURING  swept X "
                    + calibration.CapturedRangeX.ToString("0.00") + " Y "
                    + calibration.CapturedRangeY.ToString("0.00") + " m";
                return;
            }

            Diagnostics.Calibration = calibration.UseCalibratedRange
                ? "On  X " + calibration.HandRangeX.ToString("0.00")
                    + " Y " + calibration.HandRangeY.ToString("0.00")
                    + " m  centreX " + calibration.HandCenterX.ToString("0.00")
                : "Off (uniform scale)";
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
        /// Wheel notches per second per metre of second-hand vertical offset.
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
        
        public double MoveScale
        {
            get
            {
                return cursorMapper.MoveScale;
            }
            set
            {
                cursorMapper.MoveScale = value;
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
        /// Timer for hover detection.
        /// </summary>
        private DispatcherTimer hoverTimer = new DispatcherTimer();

        /// <summary>
        /// Watchdog that catches the sensor going away without raising a tracking-lost event.
        /// </summary>
        private DispatcherTimer safetyTimer = new DispatcherTimer();

        private const int NONE_USED = -1;

        /// <summary>
        /// Used to keep track of the controlling hand. So when another hand lift up forward, the cursor would still follow the first controlling hand.
        /// </summary>
        private int usedHandIndex = NONE_USED;
        private bool hoverClicked = false;

        public KinectCursor()
        {
            // Physical-pixel bounds of the whole virtual desktop, which is the coordinate space
            // SetCursorPos works in. Replaces WPF's primary-screen-only, DPI-scaled values.
            cursorMapper = new CursorMapper(gestureRect, VirtualScreen.GetBounds(), CursorMapper.ScaleAlignment.LongerRange);

            outputLoop = new CursorOutputLoop();

            Diagnostics = new GestureDiagnostics();
            gestureEngine = new GestureEngine(tuning, actionRouter);
            actionRouter.ActionExecuted += ActionRouter_ActionExecuted;
            actionRouter.ControlGate = this;

            stationaryLock.Enabled = tuning.StationaryLockEnabled;
            stationaryLock.LockRadius = tuning.StationaryLockRadius;
            stationaryLock.LockDwell = tuning.StationaryLockDwell;
            stationaryLock.BreakoutRadius = tuning.StationaryBreakoutRadius;

            UpdateDesktopDiagnostics();
            UpdateCalibrationDiagnostics();

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
            cursorMapper.OutputRect = VirtualScreen.GetBounds();

            // The coordinate space just moved under us, so abandon any drag rather than
            // continuing it against a stale mapping.
            ReleaseAllGrips();
            ResetControlState();
            UpdateDesktopDiagnostics();
        }

        private void UpdateDesktopDiagnostics()
        {
            MRect bounds = cursorMapper.OutputRect;
            Diagnostics.DesktopBounds =
                Math.Abs(bounds.DeltaX).ToString("0") + "x" + Math.Abs(bounds.DeltaY).ToString("0")
                + " @ (" + bounds.Left.ToString("0") + "," + bounds.Top.ToString("0") + ")";
        }

        private void Kinect_OnLostTracking(object sender, EventArgs e)
        {
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

            // Normalize the body into the gesture context first, so both the pointer path below
            // and the recognizers afterwards read exactly the same view of this frame.
            BuildHandSnapshots(body, deltaTime);

            // Control switched off, or a calibration sweep in progress: the body is still being
            // tracked and the clap recognizer still runs, but nothing may touch the machine.
            if (!controlEnabled || calibration.IsCapturing)
            {
                if (calibration.IsCapturing)
                {
                    RecordCalibrationSample(body);
                }

                RunGestureLayer();
                UpdateDiagnostics();
                return;
            }

            for(int i = 1; i >= 0; i--) // Starts looking from right hand.
            {
                bool isLeft = (i == 0);
                if (gestureContext.Hands[i].IsActivated)
                {
                    if (usedHandIndex == NONE_USED)
                    {
                        usedHandIndex = i;
                        BeginControlSession();
                    } else if (usedHandIndex != i)
                    {
                        // In two-hand control mode, non-used hand would be used for pressing/releasing mouse button.
                        if (Mode == ControlMode.MoveGripPressing)
                        {
                            DoMouseControlByHandState(i, gestureContext.Hands[i], deltaTime);
                        } 

                        continue;
                    }

                    // Grip is resolved before the cursor target so that a fist which has just
                    // started closing can pin the cursor on this same frame, rather than a
                    // frame later once the press is confirmed.
                    if (Mode == ControlMode.GripToPress)
                    {
                        DoMouseControlByHandState(i, gestureContext.Hands[i], deltaTime);
                    }

                    double positionWeight = gestureContext.Hands[i].PositionWeight;
                    if (positionWeight > 0)
                    {
                        MVector2 handPos = body.GetHandRelativePosition(isLeft, tuning.PointerCenterHeight);
                        MVector2 filteredTarget = cursorMapper.GetSmoothedOutputPosition(handPos, deltaTime, positionWeight);

                        // Stationary lock sits between the filter chain and the click anchor.
                        // It is stood down whenever a button is held, so it can never fight a
                        // deliberate drag; during a drag the stage 4 click anchor is already
                        // providing the stability.
                        filteredTarget = stationaryLock.Apply(filteredTarget, deltaTime, !IsAnyGripHeld());

                        PublishCursorTarget(filteredTarget, deltaTime, ShouldHoldCursorAnchor(i));
                    }

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
                else
                {
                    if(usedHandIndex == i)
                    {
                        // Reset to none.
                        usedHandIndex = NONE_USED;
                        ReleaseGrip(i);
                        EndControlSession();
                    }
                    else  if (Mode == ControlMode.MoveLiftClicking)
                    {
                        if (gestureContext.Hands[i].PositionWeight > 0)
                        {
                            DoMouseClickByHandLifting(i, body.GetHandRelativePosition(isLeft, tuning.PointerCenterHeight));
                        }
                        else
                        {
                            ReleaseGrip(i);
                        }
                    }
                    else // Release mouse button when it's not regularly released, such as hand tracking lost.
                    {
                        ReleaseGrip(i);
                    }
                    
                }
                
            }

            ToggleHoverTimer(Mode == ControlMode.HoverToClick && usedHandIndex != NONE_USED);

            // The controlling hand is only known once the loop above has run, so the gesture
            // layer is driven here, after the pointer path has had its turn.
            RunGestureLayer();

            UpdateDiagnostics();
        }

        private void RunGestureLayer()
        {
            gestureContext.ControllingHandIndex = usedHandIndex;
            gestureContext.SecondHandIndex = GetSecondHandIndex();
            gestureContext.IsDragActive = IsAnyGripHeld();
            gestureContext.IsGestureVocabularyEnabled = IsGestureVocabularyEnabled();
            gestureContext.IsControlEnabled = controlEnabled && !calibration.IsCapturing;
            gestureEngine.Update(gestureContext);
        }

        private void RecordCalibrationSample(Body body)
        {
            for (int i = 0; i < 2; i++)
            {
                bool isLeft = (i == 0);
                HandSnapshot hand = gestureContext.Hands[i];

                // Only a confidently tracked, raised hand contributes, so a hand resting in the
                // lap cannot stretch the rectangle to something unusable.
                if (hand.PositionWeight < 1 || !hand.IsActivated)
                {
                    continue;
                }

                calibration.AddSample(body.GetHandRelativePosition(isLeft, tuning.PointerCenterHeight));
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
        /// The activated hand that is not steering the pointer, if any. This is the hand the
        /// scroll and swipe recognizers are allowed to use.
        /// </summary>
        private int GetSecondHandIndex()
        {
            if (usedHandIndex == NONE_USED)
            {
                return GestureContext.NoHand;
            }

            int otherHand = usedHandIndex == GestureContext.LeftHand
                ? GestureContext.RightHand
                : GestureContext.LeftHand;

            return gestureContext.Hands[otherHand].IsActivated ? otherHand : GestureContext.NoHand;
        }

        /// <summary>
        /// Whether the lasso/scroll/swipe vocabulary is live.
        ///
        /// It is confined to GripToPress: that is the one mode where the controlling hand's
        /// hand state already drives actions and the second hand has no existing job.
        /// MoveGripPressing and MoveLiftClicking both give the second hand to clicking, and
        /// MoveOnly means pointer-only by definition, so switching gestures on there would
        /// change the documented behaviour of modes that already work. Widening this is a
        /// one-line change once the vocabulary has been tested on hardware.
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

        /// <summary>
        /// Whether the controlling hand currently wants the cursor pinned: either a press is
        /// being confirmed, or one was just confirmed and is still inside its freeze window.
        /// Only the hand steering the cursor can pin it - in the two-hand modes the clicking
        /// hand is a different hand and cannot disturb the pointer.
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
            latestTarget = target;
            hasLatestTarget = true;
            outputLoop.SetTarget(ClampToOutputRect(target));
        }

        /// <summary>
        /// Keeps the target on the desktop. With a MoveScale above 1 the mapped position runs
        /// well past the screen edges, and letting the filter chase a far-off target would make
        /// the cursor sluggish to come back.
        /// </summary>
        private MVector2 ClampToOutputRect(MVector2 target)
        {
            MRect rect = cursorMapper.OutputRect;

            double left = Math.Min(rect.Left, rect.Right);
            double top = Math.Min(rect.Top, rect.Bottom);
            // Right/Bottom are exclusive, so the last addressable pixel is one inside.
            double right = Math.Max(rect.Left, rect.Right) - 1;
            double bottom = Math.Max(rect.Top, rect.Bottom) - 1;

            if (target.X < left)
            {
                target.X = left;
            }
            else if (target.X > right)
            {
                target.X = right;
            }

            if (target.Y < top)
            {
                target.Y = top;
            }
            else if (target.Y > bottom)
            {
                target.Y = bottom;
            }

            return target;
        }

        private bool IsAnyGripHeld()
        {
            return handGrips[0] || handGrips[1];
        }

        /// <summary>
        /// A new hand has taken control. Clears filter and anchor state so the cursor starts
        /// from this hand's actual position instead of easing in from the previous one.
        ///
        /// Grips are released here as well. When control passes to the hand that was until now
        /// the clicking hand of a two-hand mode, that hand stops being polled for hand state,
        /// so a button it was holding would otherwise never be released.
        /// </summary>
        private void BeginControlSession()
        {
            ReleaseAllGrips();
            cursorMapper.ResetSmoothing();
            handStateFilters[0].Reset();
            handStateFilters[1].Reset();
            // A gesture belongs to the hand that started it. Handing control to the other hand
            // must not let a half-formed swipe or scroll carry over. The clap is deliberately
            // spared: bringing the hands together to clap can itself change which hand is in
            // charge, and clearing it here would make a double clap impossible to complete.
            gestureEngine.ResetControlSession();
            ClearCursorState();
        }

        /// <summary>
        /// The controlling hand is gone. Hands the cursor back to the physical mouse, and drops
        /// any held button - a drag cannot be meaningfully continued without cursor control.
        /// </summary>
        private void EndControlSession()
        {
            ReleaseAllGrips();
            cursorMapper.ResetSmoothing();
            handStateFilters[0].Reset();
            handStateFilters[1].Reset();
            gestureEngine.ResetControlSession();
            ClearCursorState();
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
            outputLoop.ClearTarget();
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

            // Gestures must not survive the session that produced them, and the activation
            // latches have to drop so reacquisition requires crossing the full threshold again
            // rather than only the hysteresis-relaxed one.
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
            
            //DoMouseControlByHandLifting(with press and releas rather than just a click):
            //MouseControlState controlState = handRelativePos.Y > HandLiftYForClick ? MouseControlState.ShouldPress : MouseControlState.ShouldRelease;
            //UpdateHandMouseControl(handIndex, controlState);
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
        }

        private void UpdateDiagnostics()
        {
            Diagnostics.IsTracking = true;
            Diagnostics.IsControlEnabled = controlEnabled;
            Diagnostics.Mode = gestureEngine.State.ToString();
            Diagnostics.ClapState = gestureEngine.ClapStateText;
            Diagnostics.ScrollNeutral = gestureEngine.ScrollNeutralHeight;
            Diagnostics.ScrollOffset = gestureEngine.ScrollOffset;
            Diagnostics.LockDisplacement = stationaryLock.LockDisplacement;

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

            Diagnostics.ControlHand = GestureContext.DescribeHand(usedHandIndex);
            Diagnostics.LeftHandState = gestureContext.Hands[GestureContext.LeftHand].State.ToString();
            Diagnostics.RightHandState = gestureContext.Hands[GestureContext.RightHand].State.ToString();
            Diagnostics.Gesture = gestureEngine.ActiveGestureName;

            // Report whichever hand is in charge, falling back to the right hand so the
            // height readout is still usable while working out why control will not engage.
            int reportedHand = usedHandIndex == NONE_USED ? GestureContext.RightHand : usedHandIndex;
            Diagnostics.ControlHandHeight = gestureContext.Hands[reportedHand].Height;
            Diagnostics.ControlHandForward = gestureContext.Hands[reportedHand].ForwardDistance;
        }

        private void SetDiagnosticsIdle(bool isTracking)
        {
            Diagnostics.IsTracking = isTracking;
            Diagnostics.IsControlEnabled = controlEnabled;
            Diagnostics.Mode = GestureState.Idle.ToString();
            Diagnostics.ControlHand = "None";
            Diagnostics.LeftHandState = "-";
            Diagnostics.RightHandState = "-";
            Diagnostics.Gesture = "None";
            Diagnostics.PointerState = "-";
            Diagnostics.LockDisplacement = 0;
            Diagnostics.ScrollNeutral = null;
            Diagnostics.ScrollOffset = 0;
            Diagnostics.ClapState = "Idle";
            Diagnostics.ControlHandHeight = 0;
            Diagnostics.ControlHandForward = 0;
        }

    }
    
}
