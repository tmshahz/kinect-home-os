using Microsoft.Kinect;

namespace KinectV2MouseControl
{
    /// <summary>
    /// The left-fist clutch that arms the secondary gestures (scroll, swipe).
    ///
    /// The left hand is passive by default. Raising or moving it does nothing; only a
    /// deliberately closed fist, held inside the activation zone, arms the secondary vocabulary.
    /// Opening the fist disarms it. This is what lets the left hand rest, gesture or reposition
    /// freely without scrolling pages or switching windows.
    ///
    /// Debouncing reuses HandStateFilter, so the clutch inherits exactly the rules the grip
    /// already proved on hardware: engaging needs a confident Closed, releasing never waits on
    /// confidence, and Unknown/NotTracked/Lasso frames neither engage nor release. On top of
    /// that, an engaged clutch lets go once no Closed has been seen for ClutchLossTimeout, so
    /// a hand that turns away and reports Unknown forever cannot hold the clutch.
    ///
    /// The clutch answers a single question - is SecondaryGestureArmed - so recognizers never
    /// look at the left hand's open/closed state themselves.
    /// </summary>
    public class SecondaryClutch
    {
        private readonly GestureTuning tuning;
        private readonly HandStateFilter filter = new HandStateFilter();

        private double sinceClosedObserved;
        private bool isEligible;

        public SecondaryClutch(GestureTuning tuning)
        {
            this.tuning = tuning;
        }

        /// <summary>
        /// SecondaryGestureArmed: the clutch is engaged and secondary gestures may act.
        /// </summary>
        public bool IsArmed { get; private set; }

        /// <summary>
        /// Short description for the diagnostics readout.
        /// </summary>
        public string StateText
        {
            get
            {
                if (!isEligible)
                {
                    return "Off";
                }

                if (IsArmed)
                {
                    return "ARMED";
                }

                return filter.IsPressPending ? "Closing" : "Open";
            }
        }

        /// <param name="hand">The secondary (left) hand for this frame.</param>
        /// <param name="isEligible">
        /// False when the clutch may not engage at all: hand outside the activation zone, no
        /// pointer session, vocabulary disabled. Being ineligible releases immediately.
        /// </param>
        /// <param name="deltaTime">Real elapsed seconds since the previous frame.</param>
        /// <returns>The armed state after this frame.</returns>
        public bool Update(HandSnapshot hand, bool isEligible, double deltaTime)
        {
            this.isEligible = isEligible;

            if (!isEligible || hand.PositionWeight <= 0)
            {
                Reset();
                this.isEligible = isEligible;
                return false;
            }

            filter.PressConfirmDuration = tuning.ClutchEngageDuration;
            filter.ReleaseConfirmDuration = tuning.ClutchReleaseDuration;

            bool isClosed = filter.Update(hand.State, hand.IsConfident, deltaTime);

            if (hand.State == HandState.Closed)
            {
                sinceClosedObserved = 0;
            }
            else
            {
                sinceClosedObserved += deltaTime;
            }

            if (isClosed && sinceClosedObserved > tuning.ClutchLossTimeout)
            {
                // Engaged, but the sensor has stopped confirming the fist. Let go rather than
                // leave the secondary gestures armed on stale evidence.
                filter.Reset();
                isClosed = false;
            }

            IsArmed = isClosed;
            return IsArmed;
        }

        public void Reset()
        {
            filter.Reset();
            sinceClosedObserved = 0;
            isEligible = false;
            IsArmed = false;
        }
    }
}
