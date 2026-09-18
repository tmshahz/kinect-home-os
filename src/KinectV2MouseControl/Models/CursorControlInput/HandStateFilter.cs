using Microsoft.Kinect;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Turns the raw per-frame HandState stream into a stable open/closed decision.
    ///
    /// The sensor regularly emits a stray frame, or drops to Unknown/NotTracked for a frame or
    /// two while the fingers are mid-movement. Acting on those directly produces spurious
    /// press/release pairs, so a change is only committed once the new state has held for a
    /// confirmation window:
    ///
    ///  - Unknown, NotTracked and Lasso carry no open-or-closed information. They hold the
    ///    committed state and neither add to nor destroy the evidence already gathered for a
    ///    pending change, which is what stops an Open / Unknown / Closed / Open flicker from
    ///    producing a click.
    ///  - Closing is confirmed faster than opening. The longer release window keeps a brief
    ///    mid-drag flicker from dropping a drag, which matters more than a few milliseconds
    ///    off the release.
    ///  - A press is only committed while the sensor reports high hand confidence. A release
    ///    never waits on confidence, so the mouse button cannot be trapped down by a drop in
    ///    confidence.
    ///  - A candidate that never manages to confirm is abandoned after MaxPendingDuration, so
    ///    a hand hovering at low confidence cannot hold the cursor's click anchor forever.
    /// </summary>
    public class HandStateFilter
    {
        /// <summary>
        /// Seconds that Closed must hold before a press is committed.
        /// </summary>
        public double PressConfirmDuration { get; set; } = 0.08;

        /// <summary>
        /// Seconds that Open must hold before a release is committed. Deliberately longer than
        /// PressConfirmDuration so drags survive a noisy frame.
        /// </summary>
        public double ReleaseConfirmDuration { get; set; } = 0.12;

        /// <summary>
        /// Seconds after which an unconfirmed candidate is given up on.
        /// </summary>
        public double MaxPendingDuration { get; set; } = 0.5;

        /// <summary>
        /// Committed, debounced state. False means open.
        /// </summary>
        public bool IsClosed { get; private set; }

        /// <summary>
        /// True while a close is being confirmed but has not been committed yet. The cursor
        /// anchors on this, so it is already pinned before the press actually fires.
        /// </summary>
        public bool IsPressPending
        {
            get
            {
                return hasCandidate && candidateClosed && !IsClosed;
            }
        }

        private bool hasCandidate;
        private bool candidateClosed;

        /// <summary>
        /// Time the candidate state has actually been observed, which is the evidence used to
        /// confirm it.
        /// </summary>
        private double candidateHeld;

        /// <summary>
        /// Wall time since the candidate appeared, including uninformative frames. Only used to
        /// give up on a candidate that is going nowhere.
        /// </summary>
        private double candidatePending;

        /// <param name="handState">Raw state for this frame.</param>
        /// <param name="isConfident">True when the sensor reports high confidence for this hand.</param>
        /// <param name="deltaTime">Real elapsed seconds since the previous frame.</param>
        /// <returns>The committed open/closed state after debouncing.</returns>
        public bool Update(HandState handState, bool isConfident, double deltaTime)
        {
            bool observedClosed;

            switch (handState)
            {
                case HandState.Closed:
                    observedClosed = true;
                    break;
                case HandState.Open:
                    observedClosed = false;
                    break;
                default:
                    // Unknown, NotTracked and Lasso say nothing about open vs closed. Let any
                    // pending candidate age, but don't count this frame as evidence either way.
                    if (hasCandidate)
                    {
                        candidatePending += deltaTime;
                        if (candidatePending > MaxPendingDuration)
                        {
                            ClearCandidate();
                        }
                    }
                    return IsClosed;
            }

            if (observedClosed == IsClosed)
            {
                ClearCandidate();
                return IsClosed;
            }

            if (!hasCandidate || candidateClosed != observedClosed)
            {
                hasCandidate = true;
                candidateClosed = observedClosed;
                candidateHeld = 0;
                candidatePending = 0;
            }

            candidatePending += deltaTime;

            if (observedClosed && !isConfident)
            {
                // Hold the candidate open so the cursor stays anchored, but refuse to gather
                // evidence for a press we don't trust.
                candidateHeld = 0;
            }
            else
            {
                candidateHeld += deltaTime;

                double requiredDuration = observedClosed ? PressConfirmDuration : ReleaseConfirmDuration;
                if (candidateHeld >= requiredDuration)
                {
                    IsClosed = observedClosed;
                    ClearCandidate();
                    return IsClosed;
                }
            }

            if (candidatePending > MaxPendingDuration)
            {
                ClearCandidate();
            }

            return IsClosed;
        }

        /// <summary>
        /// Forces the filter back to a released state with no pending transition. Used on
        /// tracking loss, controlling-hand changes and disable/re-enable so a stale candidate
        /// cannot fire a click on reacquisition.
        /// </summary>
        public void Reset()
        {
            IsClosed = false;
            ClearCandidate();
        }

        private void ClearCandidate()
        {
            hasCandidate = false;
            candidateClosed = false;
            candidateHeld = 0;
            candidatePending = 0;
        }
    }
}
