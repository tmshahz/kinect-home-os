using System;

namespace KinectV2MouseControl
{
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
    /// UseCalibratedRange off the mapping is bit-for-bit the original one, so an existing tuned
    /// MoveScale keeps its meaning.
    ///
    /// Everything here is in body-relative metres, measured from SpineBase, so the comfortable
    /// rectangle travels with the user rather than being anchored to the room. No assumption is
    /// made about how the sensor is mounted.
    /// </summary>
    public class PointerCalibration
    {
        /// <summary>
        /// Guards against a degenerate rectangle from a mis-run calibration, which would
        /// otherwise divide by something near zero and send the cursor to infinity.
        /// </summary>
        private const double MIN_RANGE = 0.05;

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

        // ---- Capture ----------------------------------------------------------------------

        private double minX;
        private double maxX;
        private double minY;
        private double maxY;
        private bool hasSample;

        /// <summary>
        /// True while extents are being recorded from live hand movement.
        /// </summary>
        public bool IsCapturing { get; private set; }

        public double CapturedRangeX
        {
            get
            {
                return hasSample ? maxX - minX : 0;
            }
        }

        public double CapturedRangeY
        {
            get
            {
                return hasSample ? maxY - minY : 0;
            }
        }

        public void BeginCapture()
        {
            hasSample = false;
            minX = 0;
            maxX = 0;
            minY = 0;
            maxY = 0;
            IsCapturing = true;
        }

        /// <summary>
        /// Feeds one hand position, in the same pointer-mapping frame the rectangle describes:
        /// body-relative and already offset by PointerCenterHeight.
        /// </summary>
        public void AddSample(MVector2 mappingFramePosition)
        {
            if (!IsCapturing)
            {
                return;
            }

            if (!hasSample)
            {
                minX = maxX = mappingFramePosition.X;
                minY = maxY = mappingFramePosition.Y;
                hasSample = true;
                return;
            }

            if (mappingFramePosition.X < minX) minX = mappingFramePosition.X;
            if (mappingFramePosition.X > maxX) maxX = mappingFramePosition.X;
            if (mappingFramePosition.Y < minY) minY = mappingFramePosition.Y;
            if (mappingFramePosition.Y > maxY) maxY = mappingFramePosition.Y;
        }

        /// <summary>
        /// Ends capture and adopts the swept extents, if they describe a usable rectangle.
        ///
        /// The vertical centre of the sweep is folded back into PointerCenterHeight rather than
        /// stored here, which is why it is returned instead of applied: a sweep centred 6 cm
        /// above the current pointer height means the pointer height itself was 6 cm too low.
        /// </summary>
        /// <param name="pointerCenterHeightAdjustment">
        /// Metres to add to PointerCenterHeight so the swept region is vertically centred.
        /// </param>
        /// <returns>True when the capture was usable and the ranges were adopted.</returns>
        public bool EndCapture(out double pointerCenterHeightAdjustment)
        {
            pointerCenterHeightAdjustment = 0;
            IsCapturing = false;

            if (!hasSample)
            {
                return false;
            }

            double rangeX = maxX - minX;
            double rangeY = maxY - minY;

            if (rangeX < MIN_RANGE || rangeY < MIN_RANGE)
            {
                return false;
            }

            HandRangeX = rangeX;
            HandRangeY = rangeY;

            // Samples arrive in the same frame the rectangle is expressed in, so the sweep's
            // centre is the new centre outright rather than an adjustment to the old one.
            HandCenterX = (minX + maxX) * 0.5;
            pointerCenterHeightAdjustment = (minY + maxY) * 0.5;

            UseCalibratedRange = true;
            return true;
        }

        public void CancelCapture()
        {
            IsCapturing = false;
            hasSample = false;
        }
    }
}
