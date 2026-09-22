using System;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Wheel scrolling driven by the clutched secondary hand.
    ///
    /// Flow:
    ///
    ///     left fist closes         -> clutch engages (SecondaryGestureArmed)
    ///     fist held steady briefly -> its height is captured as neutral
    ///     fist moves up / down     -> scroll at a rate set by the offset from neutral
    ///     fist opens               -> clutch releases, scrolling stops, neutral is discarded
    ///
    /// Nothing happens without the clutch, so an open secondary hand can move freely.
    ///
    /// Neutral is captured only once the clutched hand has been steady for ScrollEngageDwell,
    /// as the average height over that dwell. A fist that closes while the hand is still moving
    /// therefore cannot capture a neutral it has already left behind and scroll hard the moment
    /// it arms. Neutral is then frozen for the whole engagement, so it cannot creep toward the
    /// hand and stall the gesture.
    ///
    /// Scrolling is rate controlled: holding the fist off neutral keeps scrolling, faster the
    /// further it is held, so a long page never runs out of arm travel. The rate follows a
    /// power curve past the dead zone. ScrollCurve 1 reproduces the original linear rate; above
    /// 1 the rate is gentler near neutral for fine positioning and quicker further out, and the
    /// two meet at ScrollCurveReference so ScrollSpeed keeps roughly its old meaning.
    ///
    /// Only vertical offset scrolls. While the hand is moving fast horizontally - the start of
    /// a swipe - scrolling pauses, so a swipe's vertical wobble does not also scroll the page.
    /// </summary>
    public class ScrollRecognizer : IGestureRecognizer
    {
        private readonly GestureTuning tuning;

        private bool hasSmoothed;
        private double smoothedHeight;
        private double smoothedX;
        private double horizontalSpeed;

        private bool hasDwellAnchor;
        private MVector2 dwellAnchor;
        private double dwellElapsed;
        private double dwellHeightSum;
        private int dwellSamples;

        private double originHeight;
        private bool isArmed;
        private double currentOffset;
        private double currentRate;

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
        /// True once neutral is captured and scrolling is live.
        /// </summary>
        public bool IsEngaged
        {
            get
            {
                return isArmed;
            }
        }

        /// <summary>
        /// True while the clutch is held but neutral has not been captured yet.
        /// </summary>
        public bool IsSettling
        {
            get
            {
                return !isArmed && hasDwellAnchor;
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

        /// <summary>
        /// Current signed scroll rate in wheel notches per second, positive scrolling up.
        /// </summary>
        public double CurrentRate
        {
            get
            {
                return currentRate;
            }
        }

        public void Update(GestureContext context, IActionSink sink)
        {
            if (!context.IsSecondaryGestureArmed || context.SuppressScroll)
            {
                Reset();
                return;
            }

            HandSnapshot hand = context.Hands[context.SecondaryHandIndex];
            double deltaTime = context.DeltaTime;

            UpdateSmoothedHand(hand, deltaTime);

            if (!isArmed)
            {
                UpdateEngageDwell(deltaTime);
                return;
            }

            currentOffset = smoothedHeight - originHeight;

            if (Math.Abs(horizontalSpeed) > tuning.ScrollHorizontalHoldSpeed)
            {
                // Swipe territory. Hold off rather than scroll with the swipe's wobble.
                currentRate = 0;
                return;
            }

            currentRate = GetRate(currentOffset);
            if (currentRate == 0)
            {
                return;
            }

            // The router accumulates the fraction and submits whole notches.
            sink.Execute(ControlAction.ScrollBy(currentRate * deltaTime));
        }

        /// <summary>
        /// Maps an offset from neutral to a signed rate in notches per second.
        /// </summary>
        private double GetRate(double offset)
        {
            double magnitude = Math.Abs(offset);
            if (magnitude <= tuning.ScrollDeadzone)
            {
                return 0;
            }

            // Measured from the edge of the dead zone so the rate ramps from zero instead of
            // jumping as soon as the threshold is crossed.
            double beyond = magnitude - tuning.ScrollDeadzone;
            double reference = Math.Max(tuning.ScrollCurveReference, 0.01);
            double curve = Math.Max(tuning.ScrollCurve, 0.1);

            double rate = tuning.ScrollSpeed * reference * Math.Pow(beyond / reference, curve);
            if (rate > tuning.ScrollMaxNotchesPerSecond)
            {
                rate = tuning.ScrollMaxNotchesPerSecond;
            }

            double direction = Math.Sign(offset) * (tuning.InvertScroll ? -1 : 1);
            return rate * direction;
        }

        private void UpdateSmoothedHand(HandSnapshot hand, double deltaTime)
        {
            if (!hasSmoothed)
            {
                smoothedHeight = hand.Height;
                smoothedX = hand.Position.X;
                horizontalSpeed = 0;
                hasSmoothed = true;
                return;
            }

            double heightBlend = 1 - Math.Exp(-deltaTime / Math.Max(tuning.ScrollHeightSmoothing, 0.001));
            smoothedHeight += (hand.Height - smoothedHeight) * heightBlend;

            double previousX = smoothedX;
            smoothedX += (hand.Position.X - smoothedX) * heightBlend;

            if (deltaTime > 0)
            {
                double instantSpeed = (smoothedX - previousX) / deltaTime;
                double speedBlend = 1 - Math.Exp(-deltaTime / 0.10);
                horizontalSpeed += (instantSpeed - horizontalSpeed) * speedBlend;
            }
        }

        /// <summary>
        /// Waits for the clutched hand to hold steady, then captures neutral as the average
        /// height over the steady period.
        /// </summary>
        private void UpdateEngageDwell(double deltaTime)
        {
            MVector2 position = new MVector2(smoothedX, smoothedHeight);

            if (!hasDwellAnchor || (position - dwellAnchor).Length() > tuning.ScrollEngageSteadyRadius)
            {
                // Still moving. Re-anchor rather than abandoning, so a hand that settles arms
                // promptly.
                dwellAnchor = position;
                hasDwellAnchor = true;
                dwellElapsed = 0;
                dwellHeightSum = smoothedHeight;
                dwellSamples = 1;
                return;
            }

            dwellElapsed += deltaTime;
            dwellHeightSum += smoothedHeight;
            dwellSamples++;

            if (dwellElapsed >= tuning.ScrollEngageDwell)
            {
                isArmed = true;
                originHeight = dwellHeightSum / dwellSamples;
                currentOffset = smoothedHeight - originHeight;
                currentRate = 0;
            }
        }

        /// <summary>
        /// Drops the engagement, neutral included. The next clutch captures a fresh neutral,
        /// which is what makes the gesture ergonomic wherever the hand happens to be.
        /// </summary>
        public void Reset()
        {
            hasSmoothed = false;
            smoothedHeight = 0;
            smoothedX = 0;
            horizontalSpeed = 0;
            hasDwellAnchor = false;
            dwellAnchor = MVector2.Zero;
            dwellElapsed = 0;
            dwellHeightSum = 0;
            dwellSamples = 0;
            originHeight = 0;
            currentOffset = 0;
            currentRate = 0;
            isArmed = false;
        }
    }
}
