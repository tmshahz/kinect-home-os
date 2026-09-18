using System;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Pins the cursor outright once the hand is genuinely being held still.
    ///
    /// This solves a different problem from the jitter dead zone, which is why raising the dead
    /// zone does not fix it. The dead zone is a *relative* test: it holds the output while the
    /// filtered position stays within N pixels of the last position handed out. Sensor noise
    /// that drifts rather than oscillates keeps nudging the filtered position just past that
    /// radius, and each time it does, the reference moves with it. The cursor therefore takes a
    /// slow random walk around the target - small steps, but unbounded. Widening the dead zone
    /// only makes the steps bigger and adds lag to real movement.
    ///
    /// The lock is an *absolute* test instead. Once stationary intent is established it stores a
    /// fixed anchor and keeps returning it, so noise of any shape produces no motion at all. The
    /// anchor only moves when the user demonstrably means it.
    ///
    /// Entering and leaving use different thresholds, so a hand hovering near the boundary
    /// cannot rapidly latch and unlatch:
    ///
    ///     moving -> [within LockRadius and slow, for LockDwell] -> LOCKED
    ///     LOCKED -> [beyond BreakoutRadius, or moving faster than BreakoutSpeed] -> moving
    ///
    /// Unlocking never snaps. The gap between the anchor and where the hand actually is becomes
    /// an offset that decays away over a few frames, so the cursor slides out of the lock.
    /// </summary>
    public class StationaryLock
    {
        /// <summary>
        /// Seconds for the post-unlock offset to shrink by ~63%. Short enough to feel immediate,
        /// long enough that leaving a lock is a slide rather than a jump.
        /// </summary>
        private const double RELEASE_DECAY_TIME_CONSTANT = 0.10;

        /// <summary>
        /// Offset magnitude in pixels below which the release is treated as finished.
        /// </summary>
        private const double RELEASE_SETTLED_DISTANCE = 0.5;

        /// <summary>
        /// Time constant for smoothing the speed estimate.
        ///
        /// Frame-to-frame speed cannot be used raw here. The jitter dead zone upstream holds the
        /// position perfectly still and then releases it in one step of at least its own radius,
        /// so a hand that is barely moving still produces periodic single-frame jumps - an 8 px
        /// dead zone stepping at 30 Hz reads as 240 px/s instantaneously. Averaging over a
        /// window separates those spikes, which do not persist, from a deliberate sweep, which
        /// does.
        /// </summary>
        private const double SPEED_SMOOTHING_TIME_CONSTANT = 0.12;

        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Radius in output pixels the filtered position must stay inside for the dwell to
        /// count. Wants to be larger than the jitter dead zone, since it has to contain the
        /// drift the dead zone lets through rather than the per-frame shimmer it already
        /// absorbs.
        /// </summary>
        public double LockRadius { get; set; } = 15;

        /// <summary>
        /// Seconds of holding still before the anchor is taken.
        /// </summary>
        public double LockDwell { get; set; } = 0.35;

        /// <summary>
        /// Distance in output pixels from the anchor that breaks the lock. Larger than
        /// LockRadius; that gap is the hysteresis.
        /// </summary>
        public double BreakoutRadius { get; set; } = 35;

        /// <summary>
        /// Speed in output pixels per second that breaks the lock immediately, without waiting
        /// for BreakoutRadius to be covered. Catches a fast deliberate sweep a little earlier
        /// than distance alone would.
        /// </summary>
        public double BreakoutSpeed { get; set; } = 220;

        /// <summary>
        /// Speed in output pixels per second below which the hand counts as still. Above this
        /// the dwell restarts even if the position has not left LockRadius.
        /// </summary>
        public double StillSpeed { get; set; } = 45;

        public bool IsLocked { get; private set; }

        /// <summary>
        /// True while stationary intent is being established but the lock has not taken yet.
        /// </summary>
        public bool IsSettling
        {
            get
            {
                return !IsLocked && hasCandidate && dwellElapsed > 0;
            }
        }

        /// <summary>
        /// How far the live filtered position currently sits from the anchor, in pixels. Zero
        /// when unlocked. Surfaced for the diagnostics readout, where it shows how close the
        /// lock is to breaking.
        /// </summary>
        public double LockDisplacement { get; private set; }

        private MVector2 anchor;
        private MVector2 candidateCenter;
        private bool hasCandidate;
        private double dwellElapsed;

        private MVector2 previousPosition;
        private bool hasPreviousPosition;
        private double smoothedSpeed;

        private MVector2 releaseOffset;
        private bool hasReleaseOffset;

        /// <param name="filteredPosition">Output-space position from the existing filter chain.</param>
        /// <param name="deltaTime">Real elapsed seconds since the previous sample.</param>
        /// <param name="allowLock">
        /// False suppresses locking and releases any existing lock, smoothly. Used while a
        /// button is held so the lock can never fight a deliberate drag.
        /// </param>
        /// <returns>The position the cursor should be driven to.</returns>
        public MVector2 Apply(MVector2 filteredPosition, double deltaTime, bool allowLock)
        {
            double speed = GetSpeed(filteredPosition, deltaTime);

            if (!Enabled || !allowLock)
            {
                if (IsLocked)
                {
                    BeginRelease(filteredPosition);
                }

                ClearCandidate();
                return ApplyReleaseOffset(filteredPosition, deltaTime);
            }

            if (IsLocked)
            {
                LockDisplacement = (filteredPosition - anchor).Length();

                if (LockDisplacement > BreakoutRadius || speed > BreakoutSpeed)
                {
                    BeginRelease(filteredPosition);
                    ClearCandidate();
                    return ApplyReleaseOffset(filteredPosition, deltaTime);
                }

                return anchor;
            }

            UpdateDwell(filteredPosition, deltaTime, speed);

            if (IsLocked)
            {
                // Anchor where the cursor already is, so engaging the lock is invisible.
                return anchor;
            }

            return ApplyReleaseOffset(filteredPosition, deltaTime);
        }

        private void UpdateDwell(MVector2 filteredPosition, double deltaTime, double speed)
        {
            if (!hasCandidate)
            {
                candidateCenter = filteredPosition;
                dwellElapsed = 0;
                hasCandidate = true;
                return;
            }

            bool isStill = speed <= StillSpeed
                && (filteredPosition - candidateCenter).Length() <= LockRadius;

            if (!isStill)
            {
                // Re-centre on the current position rather than giving up entirely, so a hand
                // that drifts and then settles somewhere new starts its dwell immediately.
                candidateCenter = filteredPosition;
                dwellElapsed = 0;
                return;
            }

            dwellElapsed += deltaTime;

            if (dwellElapsed >= LockDwell)
            {
                IsLocked = true;
                anchor = filteredPosition;
                LockDisplacement = 0;

                // The lock supersedes any release still decaying from a previous one.
                releaseOffset = MVector2.Zero;
                hasReleaseOffset = false;
            }
        }

        private double GetSpeed(MVector2 position, double deltaTime)
        {
            if (!hasPreviousPosition || deltaTime <= 0)
            {
                previousPosition = position;
                hasPreviousPosition = true;
                smoothedSpeed = 0;
                return 0;
            }

            double instantSpeed = (position - previousPosition).Length() / deltaTime;
            previousPosition = position;

            double blend = 1 - Math.Exp(-deltaTime / SPEED_SMOOTHING_TIME_CONSTANT);
            smoothedSpeed += (instantSpeed - smoothedSpeed) * blend;

            return smoothedSpeed;
        }

        private void BeginRelease(MVector2 filteredPosition)
        {
            releaseOffset = anchor - filteredPosition;
            hasReleaseOffset = true;
            IsLocked = false;
            LockDisplacement = 0;
        }

        private MVector2 ApplyReleaseOffset(MVector2 filteredPosition, double deltaTime)
        {
            if (!hasReleaseOffset)
            {
                return filteredPosition;
            }

            releaseOffset = releaseOffset * Math.Exp(-deltaTime / RELEASE_DECAY_TIME_CONSTANT);

            if (releaseOffset.Length() < RELEASE_SETTLED_DISTANCE)
            {
                releaseOffset = MVector2.Zero;
                hasReleaseOffset = false;
                return filteredPosition;
            }

            return filteredPosition + releaseOffset;
        }

        private void ClearCandidate()
        {
            hasCandidate = false;
            dwellElapsed = 0;
        }

        /// <summary>
        /// Drops everything, including any decaying release. Called on tracking loss,
        /// controlling-hand changes, disable/re-enable and display changes, so a lock can never
        /// outlive the session that created it.
        /// </summary>
        public void Reset()
        {
            IsLocked = false;
            LockDisplacement = 0;
            anchor = MVector2.Zero;
            candidateCenter = MVector2.Zero;
            hasCandidate = false;
            dwellElapsed = 0;
            previousPosition = MVector2.Zero;
            hasPreviousPosition = false;
            smoothedSpeed = 0;
            releaseOffset = MVector2.Zero;
            hasReleaseOffset = false;
        }
    }
}
