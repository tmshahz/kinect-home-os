using System;

namespace KinectV2MouseControl
{
    public enum PointerSessionState
    {
        /// <summary>
        /// Right hand not in the activation zone. The cursor belongs to the physical mouse.
        /// </summary>
        Waiting,

        /// <summary>
        /// Right hand is in the zone; collecting good samples before taking the cursor.
        /// </summary>
        Stabilizing,

        /// <summary>
        /// Pointer session live: the right hand drives the cursor.
        /// </summary>
        Active
    }

    /// <summary>
    /// Deterministic start-up for every pointer session - first acquisition, and every
    /// reacquisition after tracking loss, a stall, a reset or the hand leaving the zone.
    ///
    /// The raw first samples of a session are the least trustworthy the sensor produces: the
    /// arm is still rising, joints flip between Tracked and Inferred, and a freshly acquired
    /// skeleton can put the hand somewhere implausible for a frame or two. Starting the filter
    /// from one of those samples, and letting the output loop snap to it, is how a session
    /// began with a jump or a burst of wild movement. So a session does not take the cursor
    /// until:
    ///
    ///  - the hand joint has been genuinely Tracked (not Inferred) with a usable SpineBase,
    ///  - for PointerSettleTime seconds and at least PointerSettleMinFrames frames,
    ///  - with no sample moving implausibly fast relative to the previous one.
    ///
    /// Any bad sample restarts the count. The filter is then seeded from the average of the
    /// last few good samples, and the output loop snaps once to that position.
    ///
    /// Once active, a single sample that jumps faster than PointerMaxHandSpeed is skipped as a
    /// glitch instead of being filtered in. More than PointerMaxGlitchFrames in a row means the
    /// tracking itself has gone bad, and the session is torn down and re-stabilized.
    /// </summary>
    public class PointerStabilizer
    {
        public enum Verdict
        {
            Accept,
            SkipGlitch,
            Destabilize
        }

        private const int SEED_SAMPLE_COUNT = 3;

        private readonly GestureTuning tuning;

        private double settleElapsed;
        private int settleFrames;

        private MVector2 lastSample;
        private bool hasLastSample;
        private int glitchFrames;

        private readonly MVector2[] recent = new MVector2[SEED_SAMPLE_COUNT];
        private int recentCount;
        private int recentIndex;

        public PointerStabilizer(GestureTuning tuning)
        {
            this.tuning = tuning;
        }

        public PointerSessionState State { get; private set; }

        /// <summary>
        /// Glitch samples skipped since the session started, for diagnostics.
        /// </summary>
        public int GlitchCount { get; private set; }

        public string StateText
        {
            get
            {
                if (State == PointerSessionState.Stabilizing)
                {
                    return "Stabilizing " + settleElapsed.ToString("0.00") + "/"
                        + tuning.PointerSettleTime.ToString("0.00") + "s";
                }

                return State.ToString();
            }
        }

        /// <summary>
        /// Average of the last few good samples, used to seed the filter so the session starts
        /// from where the hand actually is rather than from one noisy frame.
        /// </summary>
        public MVector2 SeedPosition
        {
            get
            {
                if (recentCount == 0)
                {
                    return lastSample;
                }

                MVector2 sum = MVector2.Zero;
                for (int i = 0; i < recentCount; i++)
                {
                    sum += recent[i];
                }

                return sum * (1.0 / recentCount);
            }
        }

        /// <summary>
        /// Feeds one sample while the hand is in the zone but the session is not active yet.
        /// </summary>
        /// <param name="position">Hand position in the pointer-mapping frame, metres.</param>
        /// <param name="isGoodSample">Hand joint Tracked and SpineBase usable.</param>
        /// <returns>True on the frame the session becomes active.</returns>
        public bool Stabilize(MVector2 position, bool isGoodSample, double deltaTime)
        {
            if (State == PointerSessionState.Active)
            {
                return false;
            }

            if (State == PointerSessionState.Waiting)
            {
                State = PointerSessionState.Stabilizing;
                RestartSettle();
            }

            if (!isGoodSample || IsImplausible(position, deltaTime))
            {
                RestartSettle();
                // Still remember where the hand is, so plausibility is judged against it.
                if (isGoodSample)
                {
                    lastSample = position;
                    hasLastSample = true;
                }

                return false;
            }

            lastSample = position;
            hasLastSample = true;
            AddRecent(position);

            settleElapsed += deltaTime;
            settleFrames++;

            if (settleElapsed >= tuning.PointerSettleTime && settleFrames >= tuning.PointerSettleMinFrames)
            {
                State = PointerSessionState.Active;
                glitchFrames = 0;
                GlitchCount = 0;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Checks one sample during an active session.
        /// </summary>
        public Verdict CheckActiveSample(MVector2 position, double deltaTime)
        {
            if (!IsImplausible(position, deltaTime))
            {
                lastSample = position;
                hasLastSample = true;
                glitchFrames = 0;
                return Verdict.Accept;
            }

            GlitchCount++;
            if (++glitchFrames > tuning.PointerMaxGlitchFrames)
            {
                return Verdict.Destabilize;
            }

            // lastSample is deliberately not advanced: the next sample is judged against the
            // last good one, so a joint that snaps away and back is skipped both ways.
            return Verdict.SkipGlitch;
        }

        private bool IsImplausible(MVector2 position, double deltaTime)
        {
            if (!hasLastSample || deltaTime <= 0)
            {
                return false;
            }

            double speed = (position - lastSample).Length() / deltaTime;
            return speed > tuning.PointerMaxHandSpeed;
        }

        private void AddRecent(MVector2 position)
        {
            recent[recentIndex] = position;
            recentIndex = (recentIndex + 1) % SEED_SAMPLE_COUNT;
            if (recentCount < SEED_SAMPLE_COUNT)
            {
                recentCount++;
            }
        }

        private void RestartSettle()
        {
            settleElapsed = 0;
            settleFrames = 0;
            recentCount = 0;
            recentIndex = 0;
        }

        /// <summary>
        /// Back to Waiting. Every teardown path runs through here, so every new session goes
        /// through the full stabilization again.
        /// </summary>
        public void Reset()
        {
            State = PointerSessionState.Waiting;
            RestartSettle();
            hasLastSample = false;
            lastSample = MVector2.Zero;
            glitchFrames = 0;
        }
    }
}
