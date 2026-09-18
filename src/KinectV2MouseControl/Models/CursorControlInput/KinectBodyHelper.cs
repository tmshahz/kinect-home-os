using Microsoft.Kinect;

namespace KinectV2MouseControl
{
    public static class KinectBodyHelper
    {
        /// <summary>
        /// Sideways offset that centres each hand's neutral position. The hands rest either
        /// side of the spine, so without this the cursor would sit off-centre depending on
        /// which hand is in control. Unlike the vertical offset this needs no tuning - it is
        /// set by shoulder width, not by posture.
        /// </summary>
        const double GESTURE_X_OFFSET = 0.185f;

        /// <summary>
        /// Weight given to a sample whose hand or spine joint is Inferred rather than Tracked.
        /// Inferred joints are the sensor's best guess when the joint is occluded or close to
        /// the body, and they are far noisier than tracked ones, so they still steer the cursor
        /// but contribute less than a confident reading.
        /// </summary>
        private const double INFERRED_POSITION_WEIGHT = 0.35;

        /// <summary>
        /// How far the hand is in front of SpineBase, in metres. Positive means toward the
        /// sensor. Camera-space Z grows away from the sensor, hence the subtraction order.
        /// </summary>
        public static double GetHandForwardDistance(this Body body, bool isLeft)
        {
            CameraSpacePoint hand = body.Joints[isLeft ? JointType.HandLeft : JointType.HandRight].Position;
            CameraSpacePoint spineBase = body.Joints[JointType.SpineBase].Position;

            return spineBase.Z - hand.Z;
        }

        /// <summary>
        /// Raw hand position relative to SpineBase, with no pointer-mapping offsets applied.
        /// X is positive to the user's right, Y positive upward. This is the frame gestures
        /// reason in, so leaning or sliding down a couch moves the reference with the body.
        /// </summary>
        public static MVector2 GetHandBodyRelativePosition(this Body body, bool isLeft)
        {
            CameraSpacePoint handPos = body.Joints[isLeft ? JointType.HandLeft : JointType.HandRight].Position;
            CameraSpacePoint spineBase = body.Joints[JointType.SpineBase].Position;

            return handPos.ToMVector2() - spineBase.ToMVector2();
        }

        /// <summary>
        /// How much a hand position sample from this frame should be trusted, based on the
        /// Kinect TrackingState of the joints it is derived from.
        /// </summary>
        /// <returns>1 when both joints are tracked, a reduced weight when either is inferred, 0 when unusable.</returns>
        public static double GetHandPositionWeight(this Body body, bool isLeft)
        {
            TrackingState handState = body.Joints[isLeft ? JointType.HandLeft : JointType.HandRight].TrackingState;
            TrackingState spineBaseState = body.Joints[JointType.SpineBase].TrackingState;

            if (handState == TrackingState.NotTracked || spineBaseState == TrackingState.NotTracked)
            {
                return 0;
            }

            if (handState == TrackingState.Inferred || spineBaseState == TrackingState.Inferred)
            {
                return INFERRED_POSITION_WEIGHT;
            }

            return 1;
        }

        /// <summary>
        /// Whether the sensor reports high confidence in this hand's open/closed state.
        /// Grip detection is unreliable when the hand is turned away or partly occluded, and
        /// the sensor flags that separately from the state itself.
        /// </summary>
        public static bool IsHandStateConfident(this Body body, bool isLeft)
        {
            return (isLeft ? body.HandLeftConfidence : body.HandRightConfidence) == TrackingConfidence.High;
        }

        public static HandState GetHandState(this Body body, bool isLeft)
        {
            return isLeft ? body.HandLeftState : body.HandRightState;
        }

        /// <summary>
        /// Hand position in the pointer-mapping frame: body-relative, shifted sideways so the
        /// hand's rest position is centred, and shifted down by pointerCenterHeight so that
        /// height maps to the middle of the screen.
        /// </summary>
        /// <param name="pointerCenterHeight">
        /// Metres above SpineBase that should map to the vertical centre of the screen. Was a
        /// fixed 0.65 m, which forces an uncomfortably high arm when seated or reclined.
        /// </param>
        public static MVector2 GetHandRelativePosition(this Body body, bool isLeft, double pointerCenterHeight)
        {
            MVector2 relativePosition = body.GetHandBodyRelativePosition(isLeft);

            relativePosition.X += isLeft ? GESTURE_X_OFFSET : -GESTURE_X_OFFSET;
            relativePosition.Y -= pointerCenterHeight;

            return relativePosition;
        }

        public static MVector2 ToMVector2(this CameraSpacePoint jointPoint)
        {
            return new MVector2(jointPoint.X, jointPoint.Y);
        }

    }
}
