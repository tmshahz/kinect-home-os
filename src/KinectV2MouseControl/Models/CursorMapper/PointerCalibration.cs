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
    /// Describes the patch of air the pointer hand moves through and records a repeatable
    /// guided capture of that patch.
    ///
    /// Calibrated mode remains a single linear mapping per axis. The agreed left/right and
    /// top/bottom extents define that rectangle; the user's separately captured comfort centre
    /// is retained as useful posture data without introducing a gain change around the centre.
    /// </summary>
    public class PointerCalibration
    {
        /// <summary>
        /// Guards against a degenerate rectangle from a mis-run calibration.
        /// </summary>
        private const double MIN_RANGE = 0.05;

        private readonly GestureTuning tuning;
        private readonly MVector2[] holdSamples;
        private readonly double[] sampleX;
        private readonly double[] sampleY;
        private readonly double[] sampleDistance;
        private readonly MVector2[,] capturedPasses;

        public PointerCalibration(GestureTuning tuning)
        {
            this.tuning = tuning;

            int sampleCapacity = Math.Max(8, tuning.CalibrationMaxHoldSamples);
            int passCapacity = Math.Max(2, tuning.CalibrationPassesPerPoint);
            holdSamples = new MVector2[sampleCapacity];
            sampleX = new double[sampleCapacity];
            sampleY = new double[sampleCapacity];
            sampleDistance = new double[sampleCapacity];
            capturedPasses = new MVector2[5, passCapacity];
        }

        /// <summary>
        /// When off, the original uniform-scale mapping is used unchanged.
        /// </summary>
        public bool UseCalibratedRange { get; set; }

        public double HandRangeX { get; set; } = 0.50;

        public double HandRangeY { get; set; } = 0.30;

        /// <summary>
        /// Geometric centre of the agreed left/right extents. This remains the centre of the
        /// one-piece linear map so there is no gain transition in the most-used area.
        /// </summary>
        public double HandCenterX { get; set; }

        /// <summary>
        /// Where the user naturally pointed at the desktop centre. Stored separately from the
        /// geometric midpoint so posture drift and asymmetric reach are visible and reusable.
        /// </summary>
        public double HandComfortCenterX { get; set; }

        /// <summary>
        /// Worst horizontal disagreement between the independent holds, in metres.
        /// </summary>
        public double CalibrationSpreadX { get; set; } = -1;

        /// <summary>
        /// Worst vertical disagreement between the independent holds, in metres.
        /// </summary>
        public double CalibrationSpreadY { get; set; } = -1;

        public bool HasCalibrationQuality
        {
            get
            {
                return CalibrationSpreadX >= 0 && CalibrationSpreadY >= 0;
            }
        }

        public bool IsCalibrationNoisy
        {
            get
            {
                return HasCalibrationQuality && (CalibrationSpreadX >= tuning.CalibrationNoisySpread
                    || CalibrationSpreadY >= tuning.CalibrationNoisySpread);
            }
        }

        public MRect BuildInputRect()
        {
            double halfWidth = Math.Max(HandRangeX, MIN_RANGE) * 0.5;
            double halfHeight = Math.Max(HandRangeY, MIN_RANGE) * 0.5;

            return new MRect(
                HandCenterX - halfWidth,
                halfHeight,
                HandCenterX + halfWidth,
                -halfHeight);
        }

        // ---- Guided capture ------------------------------------------------------------------

        private MVector2 anchor;
        private bool hasAnchor;
        private double held;
        private int heldCount;
        private bool isWrongSide;

        private int passCount;
        private bool awaitingReposition;
        private MVector2 repositionOrigin;
        private string retryMessage = "";

        private MVector2 capturedCenter;
        private double capturedLeft;
        private double capturedRight;
        private double capturedTop;
        private double capturedBottom;

        public CalibrationStep Step { get; private set; }

        public bool IsCapturing
        {
            get
            {
                return Step != CalibrationStep.None && Step != CalibrationStep.Complete;
            }
        }

        public bool IsCaptureComplete
        {
            get
            {
                return Step == CalibrationStep.Complete;
            }
        }

        public string LastResultText { get; private set; } = "";

        public double HoldProgress
        {
            get
            {
                if (!IsCapturing || !hasAnchor || isWrongSide || awaitingReposition)
                {
                    return 0;
                }

                return Math.Min(1, held / tuning.CalibrationHoldDuration);
            }
        }

        public bool IsWaitingForHand
        {
            get
            {
                return IsCapturing && !hasAnchor && !awaitingReposition;
            }
        }

        public int CurrentPass
        {
            get
            {
                return Math.Min(passCount + 1, tuning.CalibrationPassesPerPoint);
            }
        }

        public int PassesPerPoint
        {
            get
            {
                return tuning.CalibrationPassesPerPoint;
            }
        }

        public string PromptText
        {
            get
            {
                string instruction = GetInstruction();
                if (instruction.Length == 0)
                {
                    return LastResultText;
                }

                if (awaitingReposition)
                {
                    string prefix = retryMessage.Length == 0
                        ? "Hold " + passCount + "/" + tuning.CalibrationPassesPerPoint + " captured. "
                        : retryMessage + " ";
                    return instruction + "  - " + prefix
                        + "relax away from this point, then return";
                }

                if (!hasAnchor)
                {
                    return instruction + "  - raise the pointer hand into the control zone";
                }

                if (isWrongSide)
                {
                    return instruction + "  - move further from centre";
                }

                int percent = (int)Math.Min(100, held / tuning.CalibrationHoldDuration * 100);
                return instruction + "  - hold " + CurrentPass + "/"
                    + tuning.CalibrationPassesPerPoint + " still " + percent + "%";
            }
        }

        private string GetInstruction()
        {
            switch (Step)
            {
                case CalibrationStep.Center:
                    return "1/5 CENTRE: point the POINTER hand comfortably at the middle of the desktop";
                case CalibrationStep.Left:
                    return "2/5 LEFT: hold the POINTER hand at your comfortable LEFT limit";
                case CalibrationStep.Right:
                    return "3/5 RIGHT: hold the POINTER hand at your comfortable RIGHT limit";
                case CalibrationStep.Top:
                    return "4/5 TOP: hold the POINTER hand at your comfortable TOP limit";
                case CalibrationStep.Bottom:
                    return "5/5 BOTTOM: hold the POINTER hand at your comfortable BOTTOM limit (still raised)";
                default:
                    return "";
            }
        }

        public void BeginCapture()
        {
            Step = CalibrationStep.Center;
            passCount = 0;
            awaitingReposition = false;
            retryMessage = "";
            ClearHold();
        }

        /// <summary>
        /// Feeds one fully tracked pointer-hand position in the mapping frame. A hold is kept
        /// allocation-free; robust reduction happens only when its timer completes.
        /// </summary>
        public void AddSample(MVector2 mappingFramePosition, double deltaTime)
        {
            if (!IsCapturing)
            {
                return;
            }

            if (awaitingReposition)
            {
                if (HasRepositioned(mappingFramePosition))
                {
                    awaitingReposition = false;
                    retryMessage = "";
                    ClearHold();
                }

                return;
            }

            if (!hasAnchor || (mappingFramePosition - anchor).Length() > tuning.CalibrationSteadyRadius)
            {
                anchor = mappingFramePosition;
                hasAnchor = true;
                held = 0;
                heldCount = 0;
                AddHoldSample(mappingFramePosition);
                isWrongSide = !IsOnCorrectSide(mappingFramePosition);
                return;
            }

            held += deltaTime;
            AddHoldSample(mappingFramePosition);

            MVector2 runningMean = GetSimpleHoldMean();
            isWrongSide = !IsOnCorrectSide(runningMean);
            if (isWrongSide)
            {
                held = 0;
                heldCount = 0;
                AddHoldSample(mappingFramePosition);
                return;
            }

            if (held >= tuning.CalibrationHoldDuration)
            {
                RecordPass(GetRobustHoldMean());
                ClearHold();
            }
        }

        public void NoSample()
        {
            ClearHold();
        }

        private void AddHoldSample(MVector2 position)
        {
            if (heldCount < holdSamples.Length)
            {
                holdSamples[heldCount++] = position;
            }
            else
            {
                // A configured hold longer than the buffer keeps the newest evidence.
                for (int i = 1; i < holdSamples.Length; i++)
                {
                    holdSamples[i - 1] = holdSamples[i];
                }

                holdSamples[holdSamples.Length - 1] = position;
            }
        }

        private MVector2 GetSimpleHoldMean()
        {
            MVector2 sum = MVector2.Zero;
            for (int i = 0; i < heldCount; i++)
            {
                sum += holdSamples[i];
            }

            return heldCount == 0 ? anchor : sum * (1.0 / heldCount);
        }

        /// <summary>
        /// Discards the fraction of samples furthest from the coordinate median, then averages
        /// the rest. One-frame excursions inside the steady radius therefore cannot pull the
        /// result as a plain mean did.
        /// </summary>
        private MVector2 GetRobustHoldMean()
        {
            if (heldCount == 0)
            {
                return anchor;
            }

            for (int i = 0; i < heldCount; i++)
            {
                sampleX[i] = holdSamples[i].X;
                sampleY[i] = holdSamples[i].Y;
            }

            Array.Sort(sampleX, 0, heldCount);
            Array.Sort(sampleY, 0, heldCount);
            MVector2 median = new MVector2(GetMedian(sampleX, heldCount), GetMedian(sampleY, heldCount));

            for (int i = 0; i < heldCount; i++)
            {
                sampleDistance[i] = (holdSamples[i] - median).Length();
            }

            Array.Sort(sampleDistance, 0, heldCount);
            double trim = Math.Max(0, Math.Min(0.45, tuning.CalibrationOutlierTrimFraction));
            int keptTarget = Math.Max(1, (int)Math.Ceiling(heldCount * (1 - trim)));
            double distanceLimit = sampleDistance[keptTarget - 1];

            MVector2 sum = MVector2.Zero;
            int kept = 0;
            for (int i = 0; i < heldCount; i++)
            {
                if ((holdSamples[i] - median).Length() <= distanceLimit)
                {
                    sum += holdSamples[i];
                    kept++;
                }
            }

            return kept == 0 ? median : sum * (1.0 / kept);
        }

        private static double GetMedian(double[] values, int count)
        {
            int middle = count / 2;
            return count % 2 == 0
                ? (values[middle - 1] + values[middle]) * 0.5
                : values[middle];
        }

        private bool IsOnCorrectSide(MVector2 position)
        {
            switch (Step)
            {
                case CalibrationStep.Left:
                    return position.X <= capturedCenter.X - tuning.CalibrationMinimumExtent;
                case CalibrationStep.Right:
                    return position.X >= capturedCenter.X + tuning.CalibrationMinimumExtent;
                case CalibrationStep.Top:
                    return position.Y >= capturedCenter.Y + tuning.CalibrationMinimumExtent;
                case CalibrationStep.Bottom:
                    return position.Y <= capturedCenter.Y - tuning.CalibrationMinimumExtent;
                default:
                    return true;
            }
        }

        private bool HasRepositioned(MVector2 position)
        {
            double required = tuning.CalibrationPassResetDistance;
            switch (Step)
            {
                case CalibrationStep.Left:
                    return position.X - repositionOrigin.X >= required;
                case CalibrationStep.Right:
                    return repositionOrigin.X - position.X >= required;
                case CalibrationStep.Top:
                    return repositionOrigin.Y - position.Y >= required;
                case CalibrationStep.Bottom:
                    return position.Y - repositionOrigin.Y >= required;
                default:
                    return (position - repositionOrigin).Length() >= required;
            }
        }

        private void RecordPass(MVector2 position)
        {
            int point = (int)Step - 1;
            capturedPasses[point, passCount] = position;
            passCount++;

            if (passCount < tuning.CalibrationPassesPerPoint)
            {
                repositionOrigin = position;
                awaitingReposition = true;
                return;
            }

            double disagreement = GetPassDisagreement(point);
            if (disagreement > tuning.CalibrationAgreementTolerance)
            {
                retryMessage = "Holds differed by " + disagreement.ToString("0.00")
                    + " m - hold steadier / try again.";
                repositionOrigin = position;
                passCount = 0;
                awaitingReposition = true;
                return;
            }

            MVector2 agreed = MVector2.Zero;
            for (int i = 0; i < tuning.CalibrationPassesPerPoint; i++)
            {
                agreed += capturedPasses[point, i];
            }

            agreed = agreed * (1.0 / tuning.CalibrationPassesPerPoint);
            RecordAgreedPoint(agreed);
            passCount = 0;
            awaitingReposition = false;
            retryMessage = "";
        }

        private double GetPassDisagreement(int point)
        {
            if (Step == CalibrationStep.Center)
            {
                double worst = 0;
                for (int i = 1; i < tuning.CalibrationPassesPerPoint; i++)
                {
                    worst = Math.Max(worst, (capturedPasses[point, i] - capturedPasses[point, 0]).Length());
                }

                return worst;
            }

            double minimum = double.MaxValue;
            double maximum = double.MinValue;
            for (int i = 0; i < tuning.CalibrationPassesPerPoint; i++)
            {
                MVector2 pass = capturedPasses[point, i];
                double value = Step == CalibrationStep.Left || Step == CalibrationStep.Right
                    ? pass.X : pass.Y;
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
            }

            return maximum - minimum;
        }

        private void RecordAgreedPoint(MVector2 position)
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
            heldCount = 0;
            isWrongSide = false;
        }

        /// <summary>
        /// Adopts a completed capture, leaving the previous mapping untouched on rejection.
        /// The vertical midpoint adjustment is returned because PointerCenterHeight owns it.
        /// </summary>
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

            double rangeX = (capturedRight - capturedLeft) * (1 - 2 * tuning.CalibrationEdgeAssist);
            double rangeY = (capturedTop - capturedBottom) * (1 - 2 * tuning.CalibrationEdgeAssist);
            if (rangeX < MIN_RANGE || rangeY < MIN_RANGE)
            {
                LastResultText = "Calibration rejected: range too small - previous mapping kept";
                return false;
            }

            HandRangeX = rangeX;
            HandRangeY = rangeY;
            HandCenterX = (capturedLeft + capturedRight) * 0.5;
            HandComfortCenterX = capturedCenter.X;
            pointerCenterHeightAdjustment = (capturedTop + capturedBottom) * 0.5;

            CalibrationSpreadX = GetAxisSpread(true);
            CalibrationSpreadY = GetAxisSpread(false);
            UseCalibratedRange = true;

            string quality = IsCalibrationNoisy ? "NOISY - repeat if aiming feels twitchy" : "good agreement";
            LastResultText = "Calibrated: X " + HandRangeX.ToString("0.00") + " m, Y "
                + HandRangeY.ToString("0.00") + " m, centre X " + HandCenterX.ToString("+0.00;-0.00")
                + " m, comfort X " + HandComfortCenterX.ToString("+0.00;-0.00")
                + " m; spread X/Y " + CalibrationSpreadX.ToString("0.000") + "/"
                + CalibrationSpreadY.ToString("0.000") + " m (" + quality + ")";
            return true;
        }

        private double GetAxisSpread(bool horizontal)
        {
            double worst = 0;
            int firstPoint = horizontal ? 0 : 0;
            int secondPoint = horizontal ? 1 : 3;
            int thirdPoint = horizontal ? 2 : 4;

            worst = Math.Max(worst, GetPointAxisSpread(firstPoint, horizontal));
            worst = Math.Max(worst, GetPointAxisSpread(secondPoint, horizontal));
            worst = Math.Max(worst, GetPointAxisSpread(thirdPoint, horizontal));
            return worst;
        }

        private double GetPointAxisSpread(int point, bool horizontal)
        {
            double minimum = double.MaxValue;
            double maximum = double.MinValue;
            for (int i = 0; i < tuning.CalibrationPassesPerPoint; i++)
            {
                MVector2 pass = capturedPasses[point, i];
                double value = horizontal ? pass.X : pass.Y;
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
            }

            return maximum - minimum;
        }

        public void CancelCapture()
        {
            if (IsCapturing || IsCaptureComplete)
            {
                LastResultText = "Calibration cancelled - previous mapping kept";
            }

            Step = CalibrationStep.None;
            passCount = 0;
            awaitingReposition = false;
            retryMessage = "";
            ClearHold();
        }
    }
}
