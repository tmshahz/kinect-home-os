using Microsoft.Kinect;
using System;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Read Kinect sensor body data.
    ///
    /// Body selection: control locks onto one body and follows it until it is lost, so other
    /// people walking past cannot steal the cursor. Which body gets locked matters. The sensor
    /// occasionally reports a short-lived or partial "body" - a chair back, a second person
    /// half in frame, the user's own silhouette while sitting down - and locking onto one of
    /// those gives a skeleton whose hand joints jump around wildly, which from the chair looks
    /// like the cursor jittering uncontrollably until the app is restarted. So:
    ///
    ///  - a body is only locked once enough of its core joints (head, spine, shoulders) are
    ///    actually Tracked, preferring the best-tracked and then the nearest body;
    ///  - if the locked body degrades to almost nothing while a well-tracked body is present,
    ///    the lock is dropped (raising OnLostTracking, which runs the full release/reset path)
    ///    and the good body is picked up on the next frame.
    ///
    /// Sensor availability: when the sensor disconnects (USB reset, power, the Windows audio
    /// enhancement issue) frames just stop. That is reported immediately as lost tracking
    /// rather than waiting for the stall watchdog.
    /// </summary>
    public class KinectReader
    {
        public EventHandler<BodyEventArgs> OnTrackedBody;
        public EventHandler OnLostTracking;

        const int NO_LOST_FRAME_TRACK = -1;
        const int MAX_LOST_TRACKING_FRAME_ALLOWED = 5;

        /// <summary>
        /// Core joints Tracked (not Inferred) out of the six scored before a body may be locked.
        /// </summary>
        const int MIN_LOCK_SCORE = 3;

        /// <summary>
        /// Frames to wait for a body reaching MIN_LOCK_SCORE before settling for the best one
        /// available, so an unusually occluded user is still picked up eventually.
        /// </summary>
        const int LOW_SCORE_LOCK_FRAMES = 30;

        /// <summary>
        /// A locked body at or below this score is considered degraded...
        /// </summary>
        const int DEGRADED_SCORE = 1;

        /// <summary>
        /// ...and is abandoned after this many consecutive degraded frames, if a body scoring at
        /// least SWITCH_TARGET_SCORE is available instead.
        /// </summary>
        const int DEGRADED_SWITCH_FRAMES = 30;
        const int SWITCH_TARGET_SCORE = 4;

        private static readonly JointType[] CoreJoints = new JointType[]
        {
            JointType.Head,
            JointType.SpineShoulder,
            JointType.SpineMid,
            JointType.SpineBase,
            JointType.ShoulderLeft,
            JointType.ShoulderRight
        };

        /// <summary>
        /// Allowing some tracking lost frames before raising OnLostTracking events.
        /// So the tracking effected result won't get stuck for instant small frames loses, and will be seen more continuous.
        /// (Especially when there're more changes happen between tracking and losing tracking.)
        /// </summary>
        int lostTrackingFrames = NO_LOST_FRAME_TRACK;

        KinectSensor sensor;

        /// <summary>
        /// Reader for body frames.
        /// </summary>
        BodyFrameReader bodyFrameReader;

        /// <summary>
        /// Array for bodies data.
        /// </summary>
        Body[] bodies = null;

        ulong usedTrackingId = 0;

        int lowScoreFrames;
        int degradedFrames;

        /// <summary>
        /// Bodies the sensor is currently tracking, for diagnostics.
        /// </summary>
        public int TrackedBodyCount { get; private set; }

        /// <summary>
        /// Core-joint score of the locked body, 0-6, for diagnostics.
        /// </summary>
        public int LockedBodyScore { get; private set; }

        public bool IsSensorAvailable
        {
            get
            {
                return sensor != null && sensor.IsAvailable;
            }
        }

        /// <summary>
        /// True between Open and Close. IsAvailable is only meaningful while open, so the UI
        /// needs both to tell "sensor switched off" from "sensor not detected".
        /// </summary>
        public bool IsSensorOpen
        {
            get
            {
                return sensor != null && sensor.IsOpen;
            }
        }

        public KinectReader(bool openSensor = false)
        {
            sensor = KinectSensor.GetDefault();
            sensor.IsAvailableChanged += Sensor_IsAvailableChanged;
            bodyFrameReader = sensor.BodyFrameSource.OpenReader();
            bodyFrameReader.FrameArrived += BodyFrameReader_FrameArrived;

            if (openSensor)
            {
                Open();
            }
        }

        private void Sensor_IsAvailableChanged(object sender, IsAvailableChangedEventArgs e)
        {
            RuntimeLog.Write("Sensor " + (e.IsAvailable ? "available" : "UNAVAILABLE"));
            ActivityLog.Post(ActivityKind.Sensor,
                e.IsAvailable ? "Kinect connected" : "Kinect unavailable",
                e.IsAvailable ? "Sensor is streaming" : "Sensor stopped responding - check the USB / power connection",
                "sensor");

            if (!e.IsAvailable)
            {
                DropLock("sensor unavailable");
            }
        }

        private void BodyFrameReader_FrameArrived(object sender, BodyFrameArrivedEventArgs e)
        {
            bool refreshedBodyData = false;
            TimeSpan relativeTime = TimeSpan.Zero;

            using (BodyFrame bodyFrame = e.FrameReference.AcquireFrame())
            {
                if (bodyFrame != null)
                {
                    if (bodies == null)
                    {
                        bodies = new Body[bodyFrame.BodyCount];
                    }

                    bodyFrame.GetAndRefreshBodyData(bodies);

                    // The sensor's own timestamp for this frame. Used downstream to drive
                    // time-aware filtering, which is more faithful than assuming 30Hz - frames
                    // do get dropped under load, and assuming a fixed interval silently
                    // changes how much smoothing is really applied when they are.
                    relativeTime = bodyFrame.RelativeTime;
                    refreshedBodyData = true;
                }
            }

            if (refreshedBodyData)
            {
                HandleBodyData(relativeTime);
            }
        }

        private void HandleBodyData(TimeSpan relativeTime)
        {
            Body lockedBody = null;
            Body bestBody = null;
            int bestScore = -1;
            float bestDepth = float.MaxValue;
            int trackedCount = 0;

            for (int i = 0; i < bodies.Length; i++)
            {
                Body body = bodies[i];
                if (body == null || !body.IsTracked)
                {
                    continue;
                }

                trackedCount++;

                if (usedTrackingId != 0 && body.TrackingId == usedTrackingId)
                {
                    lockedBody = body;
                    continue;
                }

                int score = ScoreBody(body);
                float depth = body.Joints[JointType.SpineMid].Position.Z;
                if (score > bestScore || (score == bestScore && depth < bestDepth))
                {
                    bestBody = body;
                    bestScore = score;
                    bestDepth = depth;
                }
            }

            TrackedBodyCount = trackedCount;

            if (lockedBody != null)
            {
                LockedBodyScore = ScoreBody(lockedBody);

                if (LockedBodyScore <= DEGRADED_SCORE && bestScore >= SWITCH_TARGET_SCORE)
                {
                    if (++degradedFrames > DEGRADED_SWITCH_FRAMES)
                    {
                        DropLock("locked body degraded (score " + LockedBodyScore
                            + "), switching to body with score " + bestScore);
                        return;
                    }
                }
                else
                {
                    degradedFrames = 0;
                }

                GetTrackedBody(lockedBody, relativeTime);
                return;
            }

            if (usedTrackingId == 0 && bestBody != null)
            {
                if (bestScore >= MIN_LOCK_SCORE || ++lowScoreFrames > LOW_SCORE_LOCK_FRAMES)
                {
                    usedTrackingId = bestBody.TrackingId;
                    lowScoreFrames = 0;
                    degradedFrames = 0;
                    LockedBodyScore = bestScore;
                    RuntimeLog.Write("Body locked (score " + bestScore + "/6, depth "
                        + bestDepth.ToString("0.00") + " m, " + trackedCount + " tracked)");
                    ActivityLog.Post(ActivityKind.Tracking, "Body locked",
                        "Quality " + bestScore + "/6 at " + bestDepth.ToString("0.0") + " m", "sensor");
                    GetTrackedBody(bestBody, relativeTime);
                    return;
                }
            }
            else if (bestBody == null)
            {
                lowScoreFrames = 0;
            }

            if (lostTrackingFrames != NO_LOST_FRAME_TRACK && ++lostTrackingFrames > MAX_LOST_TRACKING_FRAME_ALLOWED)
            {
                DropLock("body lost");
            }
        }

        /// <summary>
        /// Number of core joints the sensor is actually tracking, not inferring.
        /// </summary>
        private static int ScoreBody(Body body)
        {
            int score = 0;
            for (int i = 0; i < CoreJoints.Length; i++)
            {
                if (body.Joints[CoreJoints[i]].TrackingState == TrackingState.Tracked)
                {
                    score++;
                }
            }

            return score;
        }

        /// <summary>
        /// Forgets the locked body and reports lost tracking, which runs the full release and
        /// reset path downstream.
        /// </summary>
        private void DropLock(string reason)
        {
            bool hadBody = usedTrackingId != 0 || lostTrackingFrames != NO_LOST_FRAME_TRACK;

            lostTrackingFrames = NO_LOST_FRAME_TRACK;
            usedTrackingId = 0;
            lowScoreFrames = 0;
            degradedFrames = 0;
            LockedBodyScore = 0;

            if (hadBody)
            {
                RuntimeLog.Write("Tracking lost: " + reason);
                ActivityLog.Post(ActivityKind.Tracking, "Tracking lost", reason, "sensor");
            }

            if (OnLostTracking != null)
            {
                OnLostTracking.Invoke(this, EventArgs.Empty);
            }
        }

        private void GetTrackedBody(Body body, TimeSpan relativeTime)
        {
            lostTrackingFrames = 0;
            if (OnTrackedBody != null)
            {
                OnTrackedBody.Invoke(this, new BodyEventArgs(body, relativeTime));
            }
        }

        /// <summary>
        /// Open sensor.
        /// </summary>
        public void Open()
        {
            if (sensor != null && !sensor.IsOpen)
            {
                sensor.Open();
                RuntimeLog.Write("Sensor opened");
            }
        }

        /// <summary>
        /// Close sensor.
        /// </summary>
        public void Close()
        {
            if (sensor != null && sensor.IsOpen)
            {
                sensor.Close();
                RuntimeLog.Write("Sensor closed");
            }

            // Closing ends the session outright, so a reopened sensor has to pick a body afresh.
            usedTrackingId = 0;
            lostTrackingFrames = NO_LOST_FRAME_TRACK;
            lowScoreFrames = 0;
            degradedFrames = 0;
            TrackedBodyCount = 0;
            LockedBodyScore = 0;
        }
    }

    public class BodyEventArgs : EventArgs
    {
        public Body BodyData { get; private set; }

        /// <summary>
        /// Sensor timestamp of the frame this body data came from.
        /// </summary>
        public TimeSpan RelativeTime { get; private set; }

        public BodyEventArgs(Body bodyData, TimeSpan relativeTime)
        {
            BodyData = bodyData;
            RelativeTime = relativeTime;
        }
    }

}
