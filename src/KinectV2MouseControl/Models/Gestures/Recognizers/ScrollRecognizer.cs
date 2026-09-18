using System;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Wheel scrolling driven by the non-controlling hand, used as a modifier.
    ///
    /// Raising the second hand into the activation zone arms scrolling after a short dwell, and
    /// the height at that moment becomes the neutral origin. Vertical offset from that origin
    /// then sets a scroll *rate* rather than a scroll distance: hold the hand above neutral and
    /// the page keeps scrolling up, faster the further it is held. Displacement-mapped
    /// scrolling would be more literal but runs out of arm travel on a long document, and the
    /// hand would have to be recentred constantly.
    ///
    /// Neutral is captured live, at the moment scrolling arms, rather than being a fixed height.
    /// A fixed neutral is ergonomically wrong: wherever it sits, some seated postures put it
    /// well above where the hand naturally rests, and scrolling one way then needs an awkward
    /// reach while the other way is instant. Capturing it means both directions are always
    /// equally reachable from wherever the hand actually is.
    ///
    /// Neutral is then frozen for the whole engagement, so it cannot creep toward the hand while
    /// scrolling and stall the gesture. Disengaging drops it; re-engaging takes a fresh one.
    ///
    /// Only the vertical axis is read, so the horizontal travel of a swipe does not scroll. The
    /// engine additionally suppresses this recognizer during a swipe cooldown and while the
    /// hands are together for a clap.
    /// </summary>
    public class ScrollRecognizer : IGestureRecognizer
    {
        private readonly GestureTuning tuning;

        private int engagedHandIndex = GestureContext.NoHand;
        private double dwellElapsed;
        private double dwellAnchorHeight;
        private bool hasDwellAnchor;
        private double originHeight;
        private bool isArmed;
        private double currentOffset;

        public ScrollRecognizer(GestureTuning tuning)
        {
            this.tuning = tuning;
        }

        public string Name
        {
            get
            {
                return "Scroll";
            }
        }

        public bool IsActive
        {
            get
            {
                return isArmed;
            }
        }

        /// <summary>
        /// True once scrolling is armed, i.e. the mode is genuinely engaged.
        /// </summary>
        public bool IsEngaged
        {
            get
            {
                return isArmed;
            }
        }

        /// <summary>
        /// Captured neutral height in metres above SpineBase, or null when not engaged.
        /// </summary>
        public double? NeutralHeight
        {
            get
            {
                return isArmed ? originHeight : (double?)null;
            }
        }

        /// <summary>
        /// Current signed displacement from neutral, in metres.
        /// </summary>
        public double CurrentOffset
        {
            get
            {
                return currentOffset;
            }
        }

        public void Update(GestureContext context, IActionSink sink)
        {
            int handIndex = context.SecondHandIndex;

            if (handIndex == GestureContext.NoHand || context.SuppressScroll)
            {
                Reset();
                return;
            }

            HandSnapshot hand = context.Hands[handIndex];

            // A different hand took the modifier role; start over rather than carrying the
            // previous hand's origin across.
            if (engagedHandIndex != handIndex)
            {
                Reset();
                engagedHandIndex = handIndex;
            }

            if (!isArmed)
            {
                UpdateEngageDwell(hand, context.DeltaTime);
                return;
            }

            double offset = hand.Height - originHeight;
            currentOffset = offset;
            double magnitude = Math.Abs(offset);

            if (magnitude <= tuning.ScrollDeadzone)
            {
                return;
            }

            // Measure from the edge of the dead zone so the rate ramps from zero instead of
            // jumping as soon as the threshold is crossed.
            double effectiveOffset = (magnitude - tuning.ScrollDeadzone) * Math.Sign(offset);
            double notchesPerSecond = effectiveOffset * tuning.ScrollSpeed;

            if (notchesPerSecond > tuning.ScrollMaxNotchesPerSecond)
            {
                notchesPerSecond = tuning.ScrollMaxNotchesPerSecond;
            }
            else if (notchesPerSecond < -tuning.ScrollMaxNotchesPerSecond)
            {
                notchesPerSecond = -tuning.ScrollMaxNotchesPerSecond;
            }

            // The router accumulates the fraction and submits whole notches.
            sink.Execute(ControlAction.ScrollBy(notchesPerSecond * context.DeltaTime));
        }

        /// <summary>
        /// Waits for the hand to be both present and steady before capturing neutral.
        ///
        /// Time alone is not enough. A hand merely passing through the zone on its way
        /// somewhere else would arm mid-sweep, capture a neutral it has already left behind,
        /// and immediately scroll at full rate. Requiring it to hold within a small radius for
        /// the whole dwell means neutral is only ever taken from a hand that has settled.
        /// </summary>
        private void UpdateEngageDwell(HandSnapshot hand, double deltaTime)
        {
            if (!hasDwellAnchor)
            {
                dwellAnchorHeight = hand.Height;
                dwellElapsed = 0;
                hasDwellAnchor = true;
                return;
            }

            if (Math.Abs(hand.Height - dwellAnchorHeight) > tuning.ScrollEngageSteadyRadius)
            {
                // Still moving. Re-anchor rather than abandoning, so a hand that arrives and
                // then settles arms promptly.
                dwellAnchorHeight = hand.Height;
                dwellElapsed = 0;
                return;
            }

            dwellElapsed += deltaTime;

            if (dwellElapsed >= tuning.ScrollEngageDwell)
            {
                isArmed = true;
                originHeight = hand.Height;
                currentOffset = 0;
            }
        }

        /// <summary>
        /// Drops the engagement, neutral included. Re-engaging captures a fresh neutral, which
        /// is what makes the gesture ergonomic wherever the hand happens to be.
        /// </summary>
        public void Reset()
        {
            engagedHandIndex = GestureContext.NoHand;
            dwellElapsed = 0;
            dwellAnchorHeight = 0;
            hasDwellAnchor = false;
            originHeight = 0;
            currentOffset = 0;
            isArmed = false;
        }
    }
}
