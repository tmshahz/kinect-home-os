using System;

namespace KinectV2MouseControl
{
    public enum CalibrationStep
    {
        None,
        Center,
        Left,
        Right,
        Top,
        Bottom,
        Complete
    }

    /// <summary>
    /// Describes the patch of air the hand actually moves through, so it can be mapped onto the
    /// whole virtual desktop without exaggerated reach.
    ///
    /// The original mapping scales both axes by one factor (ScaleAlignment.LongerRange), which
    /// ties vertical reach to horizontal reach. On a single 16:9 screen that is fine. Across two
    /// monitors side by side the desktop is roughly twice as wide, so covering it horizontally
    /// demands close to a full arm span, while the same factor makes the vertical axis so
    /// sensitive that it is hard to aim.
    ///
    /// Calibrated mode replaces that with an explicit input rectangle and independent per-axis
    /// scaling, so horizontal and vertical reach are chosen separately. It is opt-in: with
    /// UseCalibratedRange off the mapping is bit-for-bit the original one. In calibrated mode
    /// the rectangle alone defines the scale - Movement Scale is not applied on top of it - so
    /// the comfortable extents always map onto the desktop edges.
    ///
    /// Everything here is in body-relative metres, measured from SpineBase, so the comfortable
    /// rectangle travels with the user rather than being anchored to the room. No assumption is
    /// made about how the sensor is mounted, or about which monitor is primary: the rectangle
    /// maps onto the whole virtual desktop bounding box.
    /// </summary>
    public class PointerCalibration
    {
        /// <summary>
        /// Guards against a degenerate rectangle from a mis-run calibration, which would
        /// otherwise divide by something near zero and send the cursor to infinity.
        /// </summary>
        private const double MIN_RANGE = 0.05;

        /// <summary>
        /// How far the hand may drift while a guided point is being held, metres.
        /// </summary>
        private const double STEADY_RADIUS = 0.03;

        /// <summary>
        /// How long a guided point must be held steady before it is captured, seconds.
        /// </summary>
        private const double STEADY_HOLD = 0.6;

        /// <summary>
        /// Minimum distance of an extent from the captured centre, metres, in the step's own
        /// direction. Stops the previous point being captured again because the hand has not
        /// moved yet, and rejects an extent on the wrong side of centre.
        /// </summary>
        private const double MIN_EXTENT_FROM_CENTER = 0.08;

        /// <summary>
        /// Fraction trimmed off each side of the captured extents. The desktop edge is then
        /// reached a little before the hand is at its comfortable limit, which makes corners
        /// easy to hit even with filtering lag, instead of demanding a full stretch every time.
        /// </summary>
        private const double EDGE_ASSIST = 0.05;

        /// <summary>
        /// When off, the original uniform-scale mapping is used unchanged.
        /// </summary>
        public bool UseCalibratedRange { get; set; }

        /// <summary>
        /// Full width, in metres, of the comfortable hand rectangle. Maps to the full width of
        /// the virtual desktop.
        /// </summary>
        public double HandRangeX { get; set; } = 0.50;

        /// <summary>
        /// Full height, in metres, of the comfortable hand rectangle.
        /// </summary>
        public double HandRangeY { get; set; } = 0.30;

        /// <summary>
        /// Sideways shift of the rectangle's centre, in metres, on top of the existing per-hand
        /// centring. Positive moves the comfortable zone to the user's right.
        /// </summary>
        public double HandCenterX { get; set; }

        /// <summary>
        /// The vertical centre is deliberately not stored here. It is already expressed by
        /// GestureTuning.PointerCenterHeight, which the pointer mapping subtracts before this
        /// rectangle is applied, so the rectangle is always centred on zero vertically. Keeping
        /// one owner for that value stops the two from drifting apart.
        /// </summary>
        public MRect BuildInputRect()
        {
            double halfWidth = Math.Max(HandRangeX, MIN_RANGE) * 0.5;
            double halfHeight = Math.Max(HandRangeY, MIN_RANGE) * 0.5;

            // Top above bottom, matching the original gesture rect's sign convention: that is
            // what gives the mapping a negative vertical scale, so raising a hand raises the
            // cursor rather than lowering it.
            return new MRect(
                HandCenterX - halfWidth,
                halfHeight,
                HandCenterX + halfWidth,
                -halfHeight);
        }

        // ---- Guided capture ------------------------------------------------------------------
        //
        // The earlier free sweep took the min/max of every sample, so a single noisy frame or a
        // momentary overreach set the edge, and results were hard to reproduce. The guided flow
        // instead asks for five held points - centre, left, right, top, bottom - each captured
        // as the average of a steady hold, and each validated to lie on the correct side of the
        // centre. Only the right (pointer) hand is sampled, and only while it is inside the
        // activation zone, so the captured range is by construction one that can actually be
        // used for pointing.
        //
        // The mapping stays linear per axis: the rectangle runs from the left extent to the
        // right extent and from the bottom to the top. A piecewise mapping pinning the captured
        // centre to the desktop centre was considered and rejected - it puts a change of gain
        // right in the middle of the most-used area. The centre point is used to validate the
        // extents and is reported in diagnostics.

        private MVector2 anchor;
        private bool hasAnchor;
        private double held;
        private MVector2 heldSum;
        private int heldCount;
        private bool isWrongSide;

        private MVector2 capturedCenter;
        private double capturedLeft;
        private double capturedRight;
        private double capturedTop;
        private double capturedBottom;

        public CalibrationStep Step { get; private set; }

        /// <summary>
        /// True while guided points are being recorded from live hand movement.
        /// </summary>
        public bool IsCapturing
        {
            get
            {
                return Step != CalibrationStep.None && Step != CalibrationStep.Complete;
            }
        }

        /// <summary>
        /// True once all five points are in and the capture is waiting to be applied.
        /// </summary>
        public bool IsCaptureComplete
        {
            get
            {
                return Step == CalibrationStep.Complete;
            }
        }

        /// <summary>
        /// Outcome of the last finished or cancelled capture, for the UI.
        /// </summary>
        public string LastResultText { get; private set; } = "";

        /// <summary>
        /// Progress of the current steady hold, 0-1, for the control center's progress ring.
        /// Zero whenever no hold is in progress.
        /// </summary>
        public double HoldProgress
        {
            get
            {
                if (!IsCapturing || !hasAnchor || isWrongSide)
                {
                    return 0;
                }

                return Math.Min(1, held / STEADY_HOLD);
            }
        }

        /// <summary>
        /// True while capturing but the right hand has not yet entered the control zone.
        /// </summary>
        public bool IsWaitingForHand
        {
            get
            {
                return IsCapturing && !hasAnchor;
            }
        }

        /// <summary>
        /// Instruction for the current step, for the UI.
        /// </summary>
        public string PromptText
        {
            get
            {
                string instruction;
                switch (Step)
                {
                    case CalibrationStep.Center:
                        instruction = "1/5 CENTRE: point RIGHT hand comfortably at the middle of the desktop";
                        break;
                    case CalibrationStep.Left:
                        instruction = "2/5 LEFT: hold RIGHT hand at your comfortable LEFT limit";
                        break;
                    case CalibrationStep.Right:
                        instruction = "3/5 RIGHT: hold RIGHT hand at your comfortable RIGHT limit";
                        break;
                    case CalibrationStep.Top:
                        instruction = "4/5 TOP: hold RIGHT hand at your comfortable TOP limit";
                        break;
                    case CalibrationStep.Bottom:
                        instruction = "5/5 BOTTOM: hold RIGHT hand at your comfortable BOTTOM limit (still raised)";
                        break;
                    default:
                        return LastResultText;
                }

                if (!hasAnchor)
                {
                    return instruction + "  - raise right hand into the control zone";
                }

                if (isWrongSide)
                {
                    return instruction + "  - move further from centre";
                }

                int percent = (int)Math.Min(100, held / STEADY_HOLD * 100);
                return instruction + "  - hold still " + percent + "%";
            }
        }

        public void BeginCapture()
        {
            Step = CalibrationStep.Center;
            ClearHold();
        }

        /// <summary>
        /// Feeds one right-hand position, in the same pointer-mapping frame the rectangle
        /// describes: body-relative and already offset by PointerCenterHeight.
        /// </summary>
        public void AddSample(MVector2 mappingFramePosition, double deltaTime)
        {
            if (!IsCapturing)
            {
                return;
            }

            if (!hasAnchor || (mappingFramePosition - anchor).Length() > STEADY_RADIUS)
            {
                anchor = mappingFramePosition;
                hasAnchor = true;
                held = 0;
                heldSum = mappingFramePosition;
                heldCount = 1;
                isWrongSide = !IsOnCorrectSide(mappingFramePosition);
                return;
            }

            held += deltaTime;
            heldSum += mappingFramePosition;
            heldCount++;

            MVector2 mean = heldSum * (1.0 / heldCount);
            isWrongSide = !IsOnCorrectSide(mean);

            if (isWrongSide)
            {
                held = 0;
                return;
            }

            if (held >= STEADY_HOLD)
            {
                Record(mean);
                ClearHold();
            }
        }

        /// <summary>
        /// The pointer hand is not usable this frame (down, untracked, inferred). The hold in
        /// progress is abandoned so a point is only ever captured from a continuous steady hold.
        /// </summary>
        public void NoSample()
        {
            ClearHold();
        }

        private bool IsOnCorrectSide(MVector2 position)
        {
            switch (Step)
            {
                case CalibrationStep.Left:
                    return position.X <= capturedCenter.X - MIN_EXTENT_FROM_CENTER;
                case CalibrationStep.Right:
                    return position.X >= capturedCenter.X + MIN_EXTENT_FROM_CENTER;
                case CalibrationStep.Top:
                    return position.Y >= capturedCenter.Y + MIN_EXTENT_FROM_CENTER;
                case CalibrationStep.Bottom:
                    return position.Y <= capturedCenter.Y - MIN_EXTENT_FROM_CENTER;
                default:
                    return true;
            }
        }

        private void Record(MVector2 position)
        {
            switch (Step)
            {
                case CalibrationStep.Center:
                    capturedCenter = position;
                    Step = CalibrationStep.Left;
                    break;
                case CalibrationStep.Left:
                    capturedLeft = position.X;
                    Step = CalibrationStep.Right;
                    break;
                case CalibrationStep.Right:
                    capturedRight = position.X;
                    Step = CalibrationStep.Top;
                    break;
                case CalibrationStep.Top:
                    capturedTop = position.Y;
                    Step = CalibrationStep.Bottom;
                    break;
                case CalibrationStep.Bottom:
                    capturedBottom = position.Y;
                    Step = CalibrationStep.Complete;
                    break;
            }
        }

        private void ClearHold()
        {
            hasAnchor = false;
            held = 0;
            heldSum = MVector2.Zero;
            heldCount = 0;
            isWrongSide = false;
        }

        /// <summary>
        /// Adopts a completed capture.
        ///
        /// The vertical centre of the captured extents is folded back into PointerCenterHeight
        /// rather than stored here, which is why it is returned instead of applied.
        /// </summary>
        /// <param name="pointerCenterHeightAdjustment">
        /// Metres to add to PointerCenterHeight so the captured region is vertically centred.
        /// </param>
        /// <returns>True when the capture was usable and the ranges were adopted.</returns>
        public bool EndCapture(out double pointerCenterHeightAdjustment)
        {
            pointerCenterHeightAdjustment = 0;

            if (Step != CalibrationStep.Complete)
            {
                Step = CalibrationStep.None;
                LastResultText = "Calibration cancelled - previous mapping kept";
                return false;
            }

            Step = CalibrationStep.None;

            double rangeX = (capturedRight - capturedLeft) * (1 - 2 * EDGE_ASSIST);
            double rangeY = (capturedTop - capturedBottom) * (1 - 2 * EDGE_ASSIST);

            if (rangeX < MIN_RANGE || rangeY < MIN_RANGE)
            {
                LastResultText = "Calibration rejected: range too small - previous mapping kept";
                return false;
            }

            HandRangeX = rangeX;
            HandRangeY = rangeY;

            // Samples arrive in the same frame the rectangle is expressed in, so the midpoint
            // of the extents is the new centre outright rather than an adjustment to the old one.
            HandCenterX = (capturedLeft + capturedRight) * 0.5;
            pointerCenterHeightAdjustment = (capturedTop + capturedBottom) * 0.5;

            UseCalibratedRange = true;

            LastResultText = "Calibrated: X " + HandRangeX.ToString("0.00") + " m, Y "
                + HandRangeY.ToString("0.00") + " m, centre X " + HandCenterX.ToString("+0.00;-0.00")
                + " m (your centre was " + (capturedCenter.X - HandCenterX).ToString("+0.00;-0.00")
                + " m off the midpoint)";
            return true;
        }

        public void CancelCapture()
        {
            if (IsCapturing || IsCaptureComplete)
            {
                LastResultText = "Calibration cancelled - previous mapping kept";
            }

            Step = CalibrationStep.None;
            ClearHold();
        }
    }
}
