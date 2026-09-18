namespace KinectV2MouseControl
{
    /// <summary>
    /// Every threshold the gesture layer uses, in one place, so nothing is buried as a magic
    /// number inside a recognizer. All distances are metres in body-relative space (measured
    /// from the SpineBase joint) and all durations are seconds.
    ///
    /// Body-relative means these values keep working when you lean, turn or slide down the
    /// couch: the reference point moves with you, so the control region moves with you too.
    /// </summary>
    public class GestureTuning
    {
        // ---- Activation -------------------------------------------------------------------

        /// <summary>
        /// Height above SpineBase that maps to the vertical centre of the screen. This is the
        /// value that decides how high you have to hold your hand to point comfortably; the
        /// usable band is centred on it. Lower it for seated/reclined use.
        /// </summary>
        public double PointerCenterHeight { get; set; } = 0.50;

        /// <summary>
        /// How far in front of SpineBase the hand must be for control to engage.
        /// </summary>
        public double ForwardActivationDistance { get; set; } = 0.15;

        /// <summary>
        /// Minimum height above SpineBase for control to engage. Keeps a hand resting in your
        /// lap from taking over, which matters when reclining puts the lap in front of the
        /// spine in Z and satisfies the forward test on its own.
        /// </summary>
        public double ActivationMinHeight { get; set; } = 0.25;

        /// <summary>
        /// Hysteresis band. Once engaged, the hand has to fall this much below a threshold
        /// before disengaging, so hovering near the edge does not flicker in and out.
        /// </summary>
        public double ActivationReleaseMargin { get; set; } = 0.08;

        // ---- Lasso -> right click ---------------------------------------------------------

        /// <summary>
        /// How long a confident Lasso must hold before a right click fires.
        /// </summary>
        public double LassoConfirmDuration { get; set; } = 0.15;

        /// <summary>
        /// How long a different stable state (Open or Closed) must hold before another lasso
        /// right click is allowed. This is what makes it one click per deliberate gesture.
        /// </summary>
        public double LassoRearmDuration { get; set; } = 0.12;

        /// <summary>
        /// Floor on the time between two lasso right clicks.
        /// </summary>
        public double LassoCooldown { get; set; } = 0.60;

        // ---- Secondary clutch (left fist) ---------------------------------------------------

        /// <summary>
        /// Seconds a confident Closed left hand must hold before the clutch engages. Short, so
        /// the clutch feels immediate, but long enough to ignore a single stray Closed frame.
        /// </summary>
        public double ClutchEngageDuration { get; set; } = 0.10;

        /// <summary>
        /// Seconds an Open left hand must hold before the clutch releases. Slightly longer than
        /// engaging, so a flicker while the fist moves does not drop a scroll mid-gesture.
        /// </summary>
        public double ClutchReleaseDuration { get; set; } = 0.15;

        /// <summary>
        /// Seconds without a single Closed observation after which an engaged clutch lets go,
        /// even if the sensor never reports Open. Covers a hand that turns away or is occluded
        /// and reports Unknown indefinitely.
        /// </summary>
        public double ClutchLossTimeout { get; set; } = 0.40;

        // ---- Scroll -----------------------------------------------------------------------

        /// <summary>
        /// How long the clutched left hand must be held steady before scroll neutral is
        /// captured. Stops a fist that closes mid-movement from capturing a neutral it has
        /// already left behind.
        /// </summary>
        public double ScrollEngageDwell { get; set; } = 0.20;

        /// <summary>
        /// Metres the hand may wander during that dwell. Keeps neutral from being captured off
        /// a hand that is still moving, which would scroll hard the instant it armed.
        /// </summary>
        public double ScrollEngageSteadyRadius { get; set; } = 0.03;

        /// <summary>
        /// Shape of the rate curve. 1 is linear (the original behaviour); higher values give
        /// finer control near neutral and more speed further out. The rate at
        /// ScrollCurveReference past the dead zone is the same whatever the curve.
        /// </summary>
        public double ScrollCurve { get; set; } = 1.5;

        /// <summary>
        /// Offset past the dead zone, in metres, where the curve crosses the linear rate. Keeps
        /// ScrollSpeed meaning roughly the same thing as the curve is changed.
        /// </summary>
        public double ScrollCurveReference { get; set; } = 0.10;

        /// <summary>
        /// False: raising the fist scrolls up (wheel away). True: raising it scrolls down.
        /// </summary>
        public bool InvertScroll { get; set; }

        /// <summary>
        /// Horizontal hand speed, metres per second, above which scrolling pauses. A swipe is a
        /// fast horizontal movement and takes priority; this stops its vertical wobble from
        /// scrolling the page while the swipe recognizer is still accumulating evidence.
        /// </summary>
        public double ScrollHorizontalHoldSpeed { get; set; } = 0.6;

        /// <summary>
        /// Time constant, seconds, of the light smoothing applied to the clutched hand's height
        /// before it is compared with neutral. Kinect hand joints shimmer by about a centimetre,
        /// which is the same size as the dead zone, so unsmoothed input chatters at its edge.
        /// </summary>
        public double ScrollHeightSmoothing { get; set; } = 0.06;

        /// <summary>
        /// Vertical dead zone around the position where scrolling armed.
        /// </summary>
        public double ScrollDeadzone { get; set; } = 0.03;

        /// <summary>
        /// Wheel notches per second per metre of vertical offset past the dead zone, measured at
        /// ScrollCurveReference (the curve bends the rate either side of that point). Scrolling
        /// is rate-controlled rather than displacement-mapped: holding the hand off-centre
        /// scrolls continuously at a speed that grows with the offset, so you never run out of
        /// arm travel on a long page.
        /// </summary>
        public double ScrollSpeed { get; set; } = 60;

        /// <summary>
        /// Upper bound on scroll rate, so a fully extended arm cannot fling a page.
        /// </summary>
        public double ScrollMaxNotchesPerSecond { get; set; } = 25;

        // ---- Horizontal swipe -------------------------------------------------------------

        /// <summary>
        /// Minimum horizontal travel for a swipe.
        /// </summary>
        public double SwipeMinDisplacement { get; set; } = 0.25;

        /// <summary>
        /// Minimum average horizontal speed over that travel. This is what separates a swipe
        /// from ordinary repositioning of the hand.
        /// </summary>
        public double SwipeMinVelocity { get; set; } = 0.90;

        /// <summary>
        /// Maximum vertical wander allowed during the swipe, so a diagonal reach is rejected.
        /// </summary>
        public double SwipeMaxVerticalDeviation { get; set; } = 0.12;

        /// <summary>
        /// Longest span considered when looking for a swipe. Samples older than this are
        /// dropped, which is also what forces the movement to be quick.
        /// </summary>
        public double SwipeTimeWindow { get; set; } = 0.40;

        /// <summary>
        /// Quiet period after a recognized swipe. Also suppresses scrolling, so the hand can be
        /// brought back without the return stroke doing anything.
        /// </summary>
        public double SwipeCooldown { get; set; } = 1.00;

        // ---- Double clap ------------------------------------------------------------------

        /// <summary>
        /// Hand-to-hand distance that counts as contact.
        /// </summary>
        public double ClapDistance { get; set; } = 0.14;

        /// <summary>
        /// Distance the hands must reach to arm, and to re-arm between the two claps. Holding
        /// the palms together therefore produces exactly one clap however long it is held.
        /// </summary>
        public double ClapSeparationDistance { get; set; } = 0.35;

        /// <summary>
        /// Longest an approach may take and still count. This is the closing-speed requirement:
        /// crossing the gap between the two distances above inside this window implies roughly
        /// 0.5 m/s, which a deliberate clap clears easily and a casual movement does not.
        /// </summary>
        public double ClapMaxApproachDuration { get; set; } = 0.40;

        /// <summary>
        /// Floor on the time between two counted claps, so one noisy contact cannot register
        /// twice and complete a double clap on its own.
        /// </summary>
        public double ClapRefractory { get; set; } = 0.18;

        /// <summary>
        /// How long the second clap has to arrive before the first is forgotten.
        /// </summary>
        public double DoubleClapWindow { get; set; } = 1.20;

        /// <summary>
        /// Minimum height above SpineBase for both hands. Keeps hands meeting in the lap from
        /// being read as clapping.
        /// </summary>
        public double ClapMinHeight { get; set; } = 0.10;

        // ---- Pointer session stabilization --------------------------------------------------

        /// <summary>
        /// Seconds of consistently good right-hand samples required before a pointer session
        /// starts driving the cursor. Applies on startup and on every reacquisition.
        /// </summary>
        public double PointerSettleTime { get; set; } = 0.25;

        /// <summary>
        /// Minimum number of good frames inside that window, so a burst of delayed frames
        /// cannot satisfy the settle time on its own.
        /// </summary>
        public int PointerSettleMinFrames { get; set; } = 5;

        /// <summary>
        /// Hand speed, metres per second, above which a sample is treated as a tracking glitch
        /// rather than movement. A pointing hand does not approach this; a joint that snaps to
        /// the wrong place for a frame exceeds it easily.
        /// </summary>
        public double PointerMaxHandSpeed { get; set; } = 6.0;

        /// <summary>
        /// Consecutive glitch samples tolerated during an active session before the session is
        /// torn down and re-stabilized.
        /// </summary>
        public int PointerMaxGlitchFrames { get; set; } = 3;

        // ---- Stationary lock ---------------------------------------------------------------

        public bool StationaryLockEnabled { get; set; } = true;

        /// <summary>
        /// Output pixels the filtered position may wander within while the dwell counts down.
        /// </summary>
        public double StationaryLockRadius { get; set; } = 15;

        /// <summary>
        /// Seconds of stillness before the cursor locks.
        /// </summary>
        public double StationaryLockDwell { get; set; } = 0.35;

        /// <summary>
        /// Output pixels from the anchor that break the lock. The gap above the lock radius is
        /// the hysteresis.
        /// </summary>
        public double StationaryBreakoutRadius { get; set; } = 35;
    }
}
