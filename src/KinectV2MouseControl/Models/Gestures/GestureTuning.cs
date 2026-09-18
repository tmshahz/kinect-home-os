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

        // ---- Scroll -----------------------------------------------------------------------

        /// <summary>
        /// How long the second hand must sit inside the activation zone before scrolling arms.
        /// Stops a hand passing through the zone from scrolling.
        /// </summary>
        public double ScrollEngageDwell { get; set; } = 0.15;

        /// <summary>
        /// Metres the hand may wander during that dwell. Keeps neutral from being captured off
        /// a hand that is still moving, which would scroll hard the instant it armed.
        /// </summary>
        public double ScrollEngageSteadyRadius { get; set; } = 0.05;

        /// <summary>
        /// Vertical dead zone around the position where scrolling armed.
        /// </summary>
        public double ScrollDeadzone { get; set; } = 0.03;

        /// <summary>
        /// Wheel notches per second per metre of vertical offset past the dead zone. Scrolling
        /// is rate-controlled rather than displacement-mapped: holding the hand off-centre
        /// scrolls continuously at a speed proportional to the offset, so you never run out of
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
