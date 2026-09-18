using System;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Fast horizontal swipe of the non-controlling hand, mapped to window switching.
    ///
    /// A short history of the hand's body-relative position is kept, and every frame the widest
    /// horizontal excursion inside the time window is measured. A swipe is accepted only when
    /// the travel, the average speed and the vertical tidiness all pass at once - that
    /// combination is what separates a deliberate flick from the ordinary business of moving a
    /// hand around, which covers similar distances but far more slowly.
    ///
    /// After a hit there is a cooldown, which the engine also uses to suppress scrolling. That
    /// is what keeps the return stroke from registering as the opposite swipe.
    /// </summary>
    public class SwipeRecognizer : IGestureRecognizer
    {
        /// <summary>
        /// Enough for the longest window at well above the sensor's frame rate.
        /// </summary>
        private const int MAX_SAMPLES = 32;

        /// <summary>
        /// Shortest span accepted as a swipe. A single-frame position glitch can show a huge
        /// displacement and velocity; requiring the movement to span real time, across several
        /// samples, rejects that without needing an outlier filter.
        /// </summary>
        private const double MIN_SPAN_DURATION = 0.08;
        private const int MIN_SPAN_SAMPLES = 3;

        private struct Sample
        {
            public double Time;
            public double X;
            public double Y;
        }

        private readonly GestureTuning tuning;
        private readonly Sample[] samples = new Sample[MAX_SAMPLES];

        private int sampleCount;
        private int writeIndex;
        private double clock;
        private double cooldownRemaining;
        private int trackedHandIndex = GestureContext.NoHand;

        public SwipeRecognizer(GestureTuning tuning)
        {
            this.tuning = tuning;
        }

        public string Name
        {
            get
            {
                return "Swipe";
            }
        }

        public bool IsActive
        {
            get
            {
                return cooldownRemaining > 0;
            }
        }

        /// <summary>
        /// True during the quiet period after a recognized swipe.
        /// </summary>
        public bool IsInCooldown
        {
            get
            {
                return cooldownRemaining > 0;
            }
        }

        public void Update(GestureContext context, IActionSink sink)
        {
            clock += context.DeltaTime;

            // The cooldown deliberately keeps running even once the hand has left the zone, so
            // bringing it back does not read as a swipe in the opposite direction.
            if (cooldownRemaining > 0)
            {
                cooldownRemaining -= context.DeltaTime;
            }

            int handIndex = context.SecondHandIndex;
            if (handIndex == GestureContext.NoHand || context.SuppressSwipe)
            {
                ClearSamples();
                return;
            }

            if (trackedHandIndex != handIndex)
            {
                ClearSamples();
                trackedHandIndex = handIndex;
            }

            HandSnapshot hand = context.Hands[handIndex];

            // Don't let a low-confidence position into the history at all.
            if (hand.PositionWeight < 1)
            {
                return;
            }

            AddSample(hand.Position.X, hand.Position.Y);

            if (cooldownRemaining > 0)
            {
                return;
            }

            if (TryDetectSwipe(out double displacement))
            {
                sink.Execute(ControlAction.Of(displacement > 0
                    ? ControlActionType.NextWindow
                    : ControlActionType.PreviousWindow));

                cooldownRemaining = tuning.SwipeCooldown;
                ClearSamples();
            }
        }

        private bool TryDetectSwipe(out double displacement)
        {
            displacement = 0;

            if (sampleCount < MIN_SPAN_SAMPLES)
            {
                return false;
            }

            Sample newest = GetSample(sampleCount - 1);
            double windowStart = newest.Time - tuning.SwipeTimeWindow;

            double bestDisplacement = 0;
            int bestIndex = -1;

            // Walk back from the newest sample to find the widest horizontal excursion still
            // inside the time window.
            for (int i = sampleCount - 2; i >= 0; i--)
            {
                Sample candidate = GetSample(i);
                if (candidate.Time < windowStart)
                {
                    break;
                }

                double candidateDisplacement = newest.X - candidate.X;
                if (Math.Abs(candidateDisplacement) > Math.Abs(bestDisplacement))
                {
                    bestDisplacement = candidateDisplacement;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0 || Math.Abs(bestDisplacement) < tuning.SwipeMinDisplacement)
            {
                return false;
            }

            Sample start = GetSample(bestIndex);
            double span = newest.Time - start.Time;

            if (span < MIN_SPAN_DURATION || (sampleCount - bestIndex) < MIN_SPAN_SAMPLES)
            {
                return false;
            }

            if (Math.Abs(bestDisplacement) / span < tuning.SwipeMinVelocity)
            {
                return false;
            }

            // Largest vertical wander anywhere in the span, so an arcing reach is rejected
            // rather than only a movement that happens to end level with where it began.
            for (int i = bestIndex; i < sampleCount; i++)
            {
                if (Math.Abs(GetSample(i).Y - start.Y) > tuning.SwipeMaxVerticalDeviation)
                {
                    return false;
                }
            }

            displacement = bestDisplacement;
            return true;
        }

        private void AddSample(double x, double y)
        {
            samples[writeIndex] = new Sample { Time = clock, X = x, Y = y };
            writeIndex = (writeIndex + 1) % MAX_SAMPLES;

            if (sampleCount < MAX_SAMPLES)
            {
                sampleCount++;
            }
        }

        /// <summary>
        /// Indexes the ring oldest-first, 0 being the oldest retained sample.
        /// </summary>
        private Sample GetSample(int index)
        {
            int start = (writeIndex - sampleCount + MAX_SAMPLES) % MAX_SAMPLES;
            return samples[(start + index) % MAX_SAMPLES];
        }

        private void ClearSamples()
        {
            sampleCount = 0;
            writeIndex = 0;
            trackedHandIndex = GestureContext.NoHand;
        }

        public void Reset()
        {
            ClearSamples();
            cooldownRemaining = 0;
        }
    }
}
