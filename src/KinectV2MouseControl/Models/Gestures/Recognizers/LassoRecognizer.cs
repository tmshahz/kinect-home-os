using Microsoft.Kinect;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Right click from a held Lasso (the "pointing" hand shape) on the controlling hand.
    ///
    /// The gesture is latched: once it fires, it cannot fire again until the hand has returned
    /// to a different stable state (Open or Closed) for LassoRearmDuration. Without that latch
    /// a hand held in Lasso would right click repeatedly, and Lasso is a shape the sensor also
    /// reports transiently while a fist opens or closes.
    ///
    /// Uninformative frames - Unknown, NotTracked - hold the current progress rather than
    /// resetting it, matching how the grip debouncer treats them. A low-confidence Lasso earns
    /// no credit toward confirmation.
    /// </summary>
    public class LassoRecognizer : IGestureRecognizer
    {
        private readonly GestureTuning tuning;

        private double lassoHeld;
        private double rearmHeld;
        private double cooldownRemaining;

        /// <summary>
        /// True after a click until a different stable state re-arms the gesture.
        /// </summary>
        private bool isLatched;

        public LassoRecognizer(GestureTuning tuning)
        {
            this.tuning = tuning;
        }

        public string Name
        {
            get
            {
                return "Lasso";
            }
        }

        public bool IsActive
        {
            get
            {
                return lassoHeld > 0;
            }
        }

        public void Update(GestureContext context, IActionSink sink)
        {
            if (cooldownRemaining > 0)
            {
                cooldownRemaining -= context.DeltaTime;
            }

            // A drag in progress owns the pointer hand; don't compete with it.
            if (context.ControllingHandIndex == GestureContext.NoHand || context.IsDragActive)
            {
                lassoHeld = 0;
                rearmHeld = 0;
                return;
            }

            HandSnapshot hand = context.ControllingHand;

            switch (hand.State)
            {
                case HandState.Lasso:
                    rearmHeld = 0;

                    if (!hand.IsConfident)
                    {
                        // Hold position but gather no evidence.
                        return;
                    }

                    lassoHeld += context.DeltaTime;

                    if (!isLatched && lassoHeld >= tuning.LassoConfirmDuration && cooldownRemaining <= 0)
                    {
                        sink.Execute(ControlAction.Of(ControlActionType.RightClick));
                        isLatched = true;
                        cooldownRemaining = tuning.LassoCooldown;
                    }
                    break;

                case HandState.Open:
                case HandState.Closed:
                    lassoHeld = 0;
                    rearmHeld += context.DeltaTime;

                    if (rearmHeld >= tuning.LassoRearmDuration)
                    {
                        isLatched = false;
                    }
                    break;

                default:
                    // Unknown / NotTracked: no information, so change nothing.
                    break;
            }
        }

        public void Reset()
        {
            lassoHeld = 0;
            rearmHeld = 0;
            cooldownRemaining = 0;
            isLatched = false;
        }
    }
}
