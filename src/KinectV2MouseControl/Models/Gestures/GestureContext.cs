using Microsoft.Kinect;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Normalized state of one hand for the current frame, in body-relative metres.
    /// </summary>
    public struct HandSnapshot
    {
        /// <summary>
        /// Hand position relative to SpineBase. X is positive to the user's right, Y positive
        /// upward. No pointer-mapping offsets are applied, so gestures reason about raw body
        /// geometry rather than about screen mapping.
        /// </summary>
        public MVector2 Position;

        /// <summary>
        /// How far the hand is in front of SpineBase. Positive means forward, toward the sensor.
        /// </summary>
        public double ForwardDistance;

        public HandState State;

        /// <summary>
        /// True when the sensor reports high confidence in State.
        /// </summary>
        public bool IsConfident;

        /// <summary>
        /// 0-1 trust in Position, from the joints' Kinect TrackingState.
        /// </summary>
        public double PositionWeight;

        /// <summary>
        /// Raw TrackingState of the hand joint itself.
        /// </summary>
        public TrackingState JointState;

        /// <summary>
        /// True when this hand is inside the activation zone, hysteresis included.
        /// </summary>
        public bool IsActivated;

        public double Height
        {
            get
            {
                return Position.Y;
            }
        }
    }

    /// <summary>
    /// Everything a recognizer is allowed to look at for the current frame.
    ///
    /// A single instance is reused frame to frame and refilled in place, so the 30Hz body
    /// stream does not generate garbage. Recognizers must treat it as read-only and keep any
    /// history they need in their own fields.
    /// </summary>
    public class GestureContext
    {
        public const int NoHand = -1;
        public const int LeftHand = 0;
        public const int RightHand = 1;

        /// <summary>
        /// Runtime roles selected by KinectCursor's explicit swap latch. They are fields on
        /// the reused context, not inferred by recognizers, so every consumer sees exactly one
        /// pointer hand and the opposite secondary hand for the whole frame.
        /// </summary>
        public int PointerHandIndex = RightHand;
        public int SecondaryHandIndex = LeftHand;

        /// <summary>
        /// Real elapsed seconds since the previous frame, from the sensor's own timestamps.
        /// </summary>
        public double DeltaTime;

        public bool IsBodyTracked;

        /// <summary>
        /// Master control gate. When false the body is still being tracked and the clap
        /// recognizer still runs, but nothing else may act.
        /// </summary>
        public bool IsControlEnabled = true;

        /// <summary>
        /// PointerHandIndex while a pointer session is active (stabilized), otherwise NoHand.
        /// </summary>
        public int ControllingHandIndex = NoHand;

        /// <summary>
        /// SecondaryGestureArmed: the secondary-hand fist clutch is engaged. Set by the engine before the
        /// secondary recognizers run; scroll and swipe do nothing while it is false.
        /// </summary>
        public bool IsSecondaryGestureArmed;

        /// <summary>
        /// Indexed by LeftHand / RightHand.
        /// </summary>
        public readonly HandSnapshot[] Hands = new HandSnapshot[2];

        /// <summary>
        /// True while a left button injected by the grip gesture is held. Recognizers use this
        /// to stand down: a drag in progress owns the pointer hand.
        /// </summary>
        public bool IsDragActive;

        /// <summary>
        /// False in the control modes that predate the gesture vocabulary, or where the second
        /// hand already has a job. Recognizers idle when this is false.
        /// </summary>
        public bool IsGestureVocabularyEnabled;

        /// <summary>
        /// Set by the engine to keep a lower-priority recognizer out of the way of one that has
        /// already claimed the frame.
        /// </summary>
        public bool SuppressScroll;

        /// <summary>
        /// Set by the engine when switching windows would be destructive, which currently means
        /// during a drag.
        /// </summary>
        public bool SuppressSwipe;

        public HandSnapshot ControllingHand
        {
            get
            {
                return ControllingHandIndex == NoHand ? new HandSnapshot() : Hands[ControllingHandIndex];
            }
        }

        public void Clear()
        {
            DeltaTime = 0;
            IsBodyTracked = false;
            // IsControlEnabled is owned by the host, not by the frame, so it is not cleared.
            ControllingHandIndex = NoHand;
            IsSecondaryGestureArmed = false;
            IsDragActive = false;
            IsGestureVocabularyEnabled = false;
            SuppressScroll = false;
            SuppressSwipe = false;
            Hands[0] = new HandSnapshot();
            Hands[1] = new HandSnapshot();
        }

        public static string DescribeHand(int handIndex)
        {
            switch (handIndex)
            {
                case LeftHand:
                    return "Left";
                case RightHand:
                    return "Right";
                default:
                    return "None";
            }
        }
    }
}
