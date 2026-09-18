using System;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Double clap, from skeleton geometry rather than audio, used to toggle control on and off.
    ///
    /// Recognition is deliberately demanding, because a false positive here is far more
    /// disruptive than a missed one - it either seizes or surrenders control of the machine.
    /// A single clap only counts when all of these hold:
    ///
    ///  - both hands are tracked;
    ///  - the hands were confidently apart by at least SeparationDistance beforehand;
    ///  - they then came within ClapDistance of each other;
    ///  - the whole approach took no longer than MaxApproachDuration.
    ///
    /// That last condition is how the closing-velocity requirement is expressed. Covering the
    /// gap from SeparationDistance to ClapDistance inside the time limit implies an average
    /// closing speed, and measuring it over the whole approach is far steadier than
    /// differentiating a noisy position between two frames. Hands brought slowly together, or
    /// merely crossing while resting, never qualify.
    ///
    /// Re-arming needs a full separation again, so holding the palms together produces exactly
    /// one clap. Two claps inside DoubleClapWindow emit the toggle.
    /// </summary>
    public class ClapRecognizer : IGestureRecognizer
    {
        private readonly GestureTuning tuning;

        /// <summary>
        /// True once the hands have been confidently apart, which is the precondition for the
        /// next contact counting as a clap.
        /// </summary>
        private bool isArmed;

        /// <summary>
        /// Seconds since arming, used to bound how long the approach may take.
        /// </summary>
        private double approachElapsed;

        private int clapCount;
        private double sinceFirstClap;
        private double refractoryRemaining;

        private double lastDistance;
        private bool hasDistance;

        public ClapRecognizer(GestureTuning tuning)
        {
            this.tuning = tuning;
        }

        public string Name
        {
            get
            {
                return "Clap";
            }
        }

        public bool IsActive
        {
            get
            {
                return clapCount > 0;
            }
        }

        /// <summary>
        /// True when the second hand should be left alone by the scroll and swipe recognizers.
        ///
        /// Bringing the hands together covers exactly the horizontal distance, at exactly the
        /// speed, that the swipe recognizer is looking for, so without this a clap would also
        /// switch windows. Hands this close to each other are not plausibly scrolling or
        /// swiping either way.
        /// </summary>
        public bool IsSuppressingSecondHand
        {
            get
            {
                return clapCount > 0 || (hasDistance && lastDistance < tuning.ClapSeparationDistance);
            }
        }

        /// <summary>
        /// Short description of progress, for the diagnostics readout.
        /// </summary>
        public string StateText
        {
            get
            {
                if (clapCount > 0)
                {
                    return "1 of 2";
                }

                return isArmed ? "Armed" : "Idle";
            }
        }

        public void Update(GestureContext context, IActionSink sink)
        {
            if (refractoryRemaining > 0)
            {
                refractoryRemaining -= context.DeltaTime;
            }

            if (clapCount > 0)
            {
                sinceFirstClap += context.DeltaTime;
                if (sinceFirstClap > tuning.DoubleClapWindow)
                {
                    // The second clap never came, so the first one is forgotten rather than
                    // waiting around to pair with something unrelated minutes later.
                    clapCount = 0;
                    sinceFirstClap = 0;
                }
            }

            HandSnapshot left = context.Hands[GestureContext.LeftHand];
            HandSnapshot right = context.Hands[GestureContext.RightHand];

            if (left.PositionWeight <= 0 || right.PositionWeight <= 0)
            {
                // Without both hands there is nothing to measure. Disarm, so tracking coming
                // back cannot be read as a sudden approach.
                isArmed = false;
                approachElapsed = 0;
                hasDistance = false;
                return;
            }

            double distance = GetHandSeparation(left, right);
            lastDistance = distance;
            hasDistance = true;

            if (distance >= tuning.ClapSeparationDistance)
            {
                // Arming requires solidly tracked joints. Inferred positions are allowed to
                // complete a clap - the hands partly occlude each other as they meet - but they
                // must not be what establishes that the hands were apart to begin with.
                if (left.PositionWeight >= 1 && right.PositionWeight >= 1)
                {
                    isArmed = true;
                    approachElapsed = 0;
                }

                return;
            }

            if (!isArmed)
            {
                return;
            }

            approachElapsed += context.DeltaTime;

            if (distance > tuning.ClapDistance)
            {
                // Still closing. Give up if it is taking too long to be a clap.
                if (approachElapsed > tuning.ClapMaxApproachDuration)
                {
                    isArmed = false;
                }

                return;
            }

            if (approachElapsed > tuning.ClapMaxApproachDuration || refractoryRemaining > 0)
            {
                isArmed = false;
                return;
            }

            if (left.Height < tuning.ClapMinHeight || right.Height < tuning.ClapMinHeight)
            {
                // Hands meeting down by the lap are almost always someone resting or fidgeting.
                isArmed = false;
                return;
            }

            RegisterClap(sink);
        }

        private void RegisterClap(IActionSink sink)
        {
            isArmed = false;
            approachElapsed = 0;
            refractoryRemaining = tuning.ClapRefractory;

            if (clapCount == 0)
            {
                clapCount = 1;
                sinceFirstClap = 0;
                return;
            }

            // Clear before emitting. Toggling control resets every recognizer, this one
            // included, and that reset must not be undone by state written afterwards.
            clapCount = 0;
            sinceFirstClap = 0;

            sink.Execute(ControlAction.Of(ControlActionType.ToggleControl));
        }

        /// <summary>
        /// Straight-line distance between the hands. The context carries X and Y relative to
        /// SpineBase plus a forward distance, which together give the third axis - hands can
        /// easily line up in X and Y while still being far apart in depth.
        /// </summary>
        private static double GetHandSeparation(HandSnapshot left, HandSnapshot right)
        {
            double dx = left.Position.X - right.Position.X;
            double dy = left.Position.Y - right.Position.Y;
            double dz = left.ForwardDistance - right.ForwardDistance;

            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public void Reset()
        {
            isArmed = false;
            approachElapsed = 0;
            clapCount = 0;
            sinceFirstClap = 0;
            refractoryRemaining = 0;
            lastDistance = 0;
            hasDistance = false;
        }
    }
}
