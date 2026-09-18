using System;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Exponential moving average with an externally supplied smoothing factor.
    /// Kept separate from OneEuroVectorFilter so the position and the derivative
    /// can each keep their own history while sharing the same maths.
    /// </summary>
    public class LowPassFilter
    {
        private double lastValue;
        private bool hasValue;

        public bool HasValue
        {
            get
            {
                return hasValue;
            }
        }

        public double Value
        {
            get
            {
                return lastValue;
            }
        }

        public double Filter(double value, double alpha)
        {
            if (!hasValue)
            {
                lastValue = value;
                hasValue = true;
                return lastValue;
            }

            lastValue = alpha * value + (1 - alpha) * lastValue;
            return lastValue;
        }

        public void Reset()
        {
            hasValue = false;
            lastValue = 0;
        }
    }

    /// <summary>
    /// 2D One Euro Filter (Casiez, Roussel, Vogel - CHI 2012).
    ///
    /// A plain exponential filter has to trade jitter against lag with a single constant.
    /// This one varies its cutoff frequency with the measured speed of the signal instead:
    /// when the hand is nearly still the cutoff drops to MinCutoff and jitter is suppressed
    /// hard, and when the hand moves deliberately the cutoff rises so the cursor keeps up.
    ///
    ///     tau       = 1 / (2 * pi * cutoff)
    ///     alpha(dt) = 1 / (1 + tau / dt)
    ///     cutoff    = MinCutoff + Beta * |filtered speed|
    ///
    /// Both axes are filtered with a single cutoff derived from the 2D speed magnitude, so
    /// the response stays isotropic - a diagonal movement is not smoothed differently from
    /// a horizontal one of the same speed.
    /// </summary>
    public class OneEuroVectorFilter
    {
        /// <summary>
        /// Cutoff frequency (Hz) used when the signal is stationary. Lower means calmer at rest.
        /// </summary>
        public double MinCutoff { get; set; } = 1.0;

        /// <summary>
        /// How aggressively the cutoff opens up with speed. Higher means more responsive
        /// during fast movement, at the cost of letting through more jitter while moving.
        /// </summary>
        public double Beta { get; set; } = 0.002;

        /// <summary>
        /// Cutoff frequency (Hz) for the speed estimate itself. The paper's default of 1Hz
        /// keeps the speed estimate from being driven by the very noise we are filtering out.
        /// </summary>
        public double DerivativeCutoff { get; set; } = 1.0;

        private readonly LowPassFilter xFilter = new LowPassFilter();
        private readonly LowPassFilter yFilter = new LowPassFilter();
        private readonly LowPassFilter dxFilter = new LowPassFilter();
        private readonly LowPassFilter dyFilter = new LowPassFilter();

        private MVector2 lastRawValue;
        private bool hasRawValue;

        public bool HasValue
        {
            get
            {
                return xFilter.HasValue;
            }
        }

        public MVector2 Value
        {
            get
            {
                return new MVector2(xFilter.Value, yFilter.Value);
            }
        }

        /// <summary>
        /// Speed of the filtered signal in units per second, as used to pick the cutoff.
        /// Exposed for diagnostics and tuning.
        /// </summary>
        public double FilteredSpeed { get; private set; }

        private static double GetAlpha(double cutoff, double deltaTime)
        {
            double tau = 1.0 / (2.0 * Math.PI * cutoff);
            return 1.0 / (1.0 + tau / deltaTime);
        }

        /// <param name="value">Latest raw sample.</param>
        /// <param name="deltaTime">Real elapsed seconds since the previous sample. Must be > 0.</param>
        /// <param name="responsiveness">
        /// Extra 0-1 multiplier on the smoothing factor, used to down-weight samples we do not
        /// fully trust (for example an Inferred Kinect joint). 1 leaves the filter untouched;
        /// lower values make the filter lean harder on its own history than on the new sample.
        /// </param>
        public MVector2 Filter(MVector2 value, double deltaTime, double responsiveness = 1.0)
        {
            if (deltaTime <= 0)
            {
                return HasValue ? Value : value;
            }

            if (!hasRawValue)
            {
                lastRawValue = value;
                hasRawValue = true;
            }

            double derivativeAlpha = GetAlpha(DerivativeCutoff, deltaTime);
            MVector2 rawSpeed = (value - lastRawValue) / deltaTime;
            lastRawValue = value;

            double speedX = dxFilter.Filter(rawSpeed.X, derivativeAlpha);
            double speedY = dyFilter.Filter(rawSpeed.Y, derivativeAlpha);
            FilteredSpeed = Math.Sqrt(speedX * speedX + speedY * speedY);

            double cutoff = MinCutoff + Beta * FilteredSpeed;
            double alpha = GetAlpha(cutoff, deltaTime) * responsiveness;

            if (alpha > 1)
            {
                alpha = 1;
            }
            else if (alpha < 0)
            {
                alpha = 0;
            }

            return new MVector2(xFilter.Filter(value.X, alpha), yFilter.Filter(value.Y, alpha));
        }

        /// <summary>
        /// Starts the filter at a known position with zero velocity, as if it had been resting
        /// there. Used to begin a pointer session from an averaged, validated position.
        /// </summary>
        public void Seed(MVector2 value)
        {
            Reset();
            xFilter.Filter(value.X, 1);
            yFilter.Filter(value.Y, 1);
            dxFilter.Filter(0, 1);
            dyFilter.Filter(0, 1);
            lastRawValue = value;
            hasRawValue = true;
        }

        /// <summary>
        /// Drops all history so the next sample is taken as-is instead of being blended with
        /// a stale position. Call on tracking loss, session changes and disable/re-enable.
        /// </summary>
        public void Reset()
        {
            xFilter.Reset();
            yFilter.Reset();
            dxFilter.Reset();
            dyFilter.Reset();
            lastRawValue = MVector2.Zero;
            hasRawValue = false;
            FilteredSpeed = 0;
        }
    }
}
