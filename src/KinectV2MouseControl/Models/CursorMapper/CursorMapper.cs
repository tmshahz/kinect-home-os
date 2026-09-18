using System;

namespace KinectV2MouseControl
{
    public class CursorMapper
    {
        private MRect _inputRect;
        public MRect InputRect
        {
            get
            {
                return _inputRect;
            }
            set
            {
                _inputRect = value;
                UpdateMapping();
            }
        }

        private MRect _outputRect;
        public MRect OutputRect
        {
            get
            {
                return _outputRect;
            }
            set
            {
                _outputRect = value;
                UpdateMapping();
            }
        }

        private MVector2 _alignScale;
        public MVector2 AlignScale
        {
            get
            {
                return _alignScale;
            }
        }

        private double _moveScale = 1;
        public double MoveScale
        {
            get
            {
                return _moveScale;
            }
            set
            {
                _moveScale = value;
                totalScale = _moveScale * _alignScale;
            }
        }

        private MVector2 totalScale;

        /// <summary>
        /// Output pixels per input metre on each axis (MoveScale × AlignScale), signed. The
        /// control center uses it to draw the hand region that reaches the whole desktop.
        /// </summary>
        public MVector2 TotalScale
        {
            get
            {
                return totalScale;
            }
        }

        private readonly OneEuroVectorFilter positionFilter = new OneEuroVectorFilter();

        /// <summary>
        /// Last position actually handed out. Held still while the filtered position stays
        /// inside the dead zone.
        /// </summary>
        private MVector2 smoothedPosition;

        /// <summary>
        /// False until smoothedPosition holds a position from the current tracking session.
        /// While false, the next smoothed position snaps straight to the target instead of
        /// easing in from a stale (or initial zero) position.
        /// </summary>
        private bool hasSmoothedPosition = false;

        public enum ScaleAlignment
        {
            None,
            Horizontal,
            Vertical,
            Both,
            ShorterRange,
            LongerRange
        }

        private ScaleAlignment _scaleAlign = ScaleAlignment.None;
        public ScaleAlignment ScaleAlign
        {
            get
            {
                return _scaleAlign;
            }
            set
            {
                _scaleAlign = value;
                UpdateMapping();
            }
        }

        private const double SMOOTH_MAX = 1;
        private const double SMOOTH_MIN = 0;

        /// <summary>
        /// Cutoff frequency the filter settles at with Smoothing 0 - effectively unfiltered.
        /// </summary>
        private const double CUTOFF_AT_NO_SMOOTHING = 15.0;

        /// <summary>
        /// Cutoff frequency at Smoothing 1 - very calm, noticeably heavy.
        /// </summary>
        private const double CUTOFF_AT_FULL_SMOOTHING = 0.4;

        /// <summary>
        /// Scale from the 0-100 SpeedResponsiveness dial to the filter's beta coefficient.
        /// </summary>
        private const double BETA_PER_RESPONSIVENESS_UNIT = 0.0001;

        private double _smoothing;

        /*
         * Smoothing no longer applies a fixed fraction of each movement. It now picks the
         * resting cutoff frequency of an adaptive One Euro filter, mapped geometrically so the
         * slider stays useful across its whole travel:
         *
         *     0.00 -> 15.0 Hz   (near raw)
         *     0.50 ->  2.4 Hz
         *     0.75 ->  1.0 Hz
         *     1.00 ->  0.4 Hz   (very heavy)
         *
         * Higher still means smoother, so an existing saved value keeps its meaning.
         */
        public double Smoothing
        {
            get
            {
                return _smoothing;
            }
            set
            {
                //Clamp value so it ranges from 0 to 1.
                if (value > SMOOTH_MAX)
                {
                    value = SMOOTH_MAX;
                }
                else if (value < SMOOTH_MIN)
                {
                    value = SMOOTH_MIN;
                }

                _smoothing = value;
                positionFilter.MinCutoff = CUTOFF_AT_NO_SMOOTHING
                    * Math.Pow(CUTOFF_AT_FULL_SMOOTHING / CUTOFF_AT_NO_SMOOTHING, value);
            }
        }

        private double _speedResponsiveness;

        /// <summary>
        /// 0-100 dial for how much the filter opens up during fast movement. 0 makes the
        /// filter a plain fixed-cutoff low pass; higher values trade a little jitter during
        /// motion for a cursor that keeps up with a deliberate sweep.
        /// </summary>
        public double SpeedResponsiveness
        {
            get
            {
                return _speedResponsiveness;
            }
            set
            {
                if (value < 0)
                {
                    value = 0;
                }

                _speedResponsiveness = value;
                positionFilter.Beta = value * BETA_PER_RESPONSIVENESS_UNIT;
            }
        }

        /// <summary>
        /// Radius in output pixels of the jitter dead zone. Absorbs the last of the sensor
        /// shimmer and any physical hand tremor while aiming.
        ///
        /// This is a continuous (soft) dead zone. The original was all-or-nothing: inside the
        /// radius the output held still, and once the radius was exceeded the output jumped to
        /// the full filtered position. That made it a noise gate with two modes. When tracking
        /// noise sat below the radius the cursor was rock steady; when conditions made the noise
        /// slightly larger than the radius - lighting, distance, posture, a seated pose - every
        /// crossing produced a jump of at least the whole radius, back and forth, and those
        /// jumps also kept the stationary lock from ever engaging. That is the intermittent
        /// "sometimes perfect, sometimes badly jittery" behaviour.
        ///
        /// Now the output trails the filtered position at a distance of radius²/distance once
        /// outside the radius. That is continuous at the boundary (no jump), moves the cursor
        /// only by roughly twice the excess for noise just past the radius, and shrinks toward
        /// zero lag as the movement gets faster, so deliberate movement is still not held back.
        /// </summary>
        public double JitterDeadzone { get; set; } = 3.0;

        /// <summary>
        /// Monitor layout used to keep the mapped position on a real monitor before it is
        /// filtered. Null leaves the mapped position unclamped.
        ///
        /// Clamping before the filter, rather than only afterwards, is what removes the "sticky
        /// edge": with a post-filter clamp alone, a hand that overshoots the screen edge drags
        /// the filter state off screen, and the cursor does not move again until the hand has
        /// travelled all the way back. Clamped first, reversing at the edge responds at once.
        /// </summary>
        public DesktopLayout Desktop { get; set; }

        /// <summary>
        /// Smoothed RMS distance, in output pixels, between the raw mapped position and the
        /// filtered one. While the hand is held still this is a direct reading of how noisy the
        /// tracking is right now; during movement it also includes normal filter lag.
        /// </summary>
        public double ResidualNoise
        {
            get
            {
                return Math.Sqrt(residualMeanSquare);
            }
        }

        private double residualMeanSquare;

        private const double RESIDUAL_TIME_CONSTANT = 1.0;

        public CursorMapper(MRect inputRect, MRect outputRect, ScaleAlignment scaleAlign = ScaleAlignment.None)
        {
            ScaleAlign = scaleAlign;
            SetRects(inputRect, outputRect);

            // Push the dials through their setters so the backing fields and the filter's own
            // defaults agree before any settings are loaded.
            Smoothing = 0.7;
            SpeedResponsiveness = 20;
        }

        public MVector2 GetOutputPosition(MVector2 inputPosition)
        {
            return _outputRect.Center + (inputPosition - _inputRect.Center) * totalScale;
        }

        /// <summary>
        /// Maps an input position to the output rect, runs it through the adaptive filter and
        /// applies the jitter dead zone.
        /// </summary>
        /// <param name="inputPosition">Position from input.</param>
        /// <param name="deltaTime">
        /// Real elapsed seconds since the previous sample. The filter is time-aware, so a
        /// dropped sensor frame no longer silently changes how much smoothing is applied.
        /// </param>
        /// <param name="positionWeight">
        /// 0-1 confidence in this sample, from the joint's Kinect TrackingState. Below 1 the
        /// filter leans further on its own history, so an Inferred joint contributes less.
        /// </param>
        public MVector2 GetSmoothedOutputPosition(MVector2 inputPosition, double deltaTime, double positionWeight = 1)
        {
            MVector2 mappedPosition = GetClampedOutputPosition(inputPosition);
            MVector2 filteredPosition = positionFilter.Filter(mappedPosition, deltaTime, positionWeight);

            if (deltaTime > 0)
            {
                double residual = (mappedPosition - filteredPosition).Length();
                double blend = 1 - Math.Exp(-deltaTime / RESIDUAL_TIME_CONSTANT);
                residualMeanSquare += (residual * residual - residualMeanSquare) * blend;
            }

            if (!hasSmoothedPosition)
            {
                smoothedPosition = filteredPosition;
                hasSmoothedPosition = true;
                return smoothedPosition;
            }

            MVector2 offset = filteredPosition - smoothedPosition;
            double distance = offset.Length();

            if (distance > JitterDeadzone)
            {
                if (JitterDeadzone <= 0)
                {
                    smoothedPosition = filteredPosition;
                }
                else
                {
                    // Trail at radius²/distance behind the filtered position: equal to the
                    // radius at the boundary, falling toward zero as the step grows.
                    double lag = JitterDeadzone * JitterDeadzone / distance;
                    smoothedPosition = filteredPosition - offset * (lag / distance);
                }
            }

            return smoothedPosition;
        }

        private MVector2 GetClampedOutputPosition(MVector2 inputPosition)
        {
            MVector2 mapped = GetOutputPosition(inputPosition);
            return Desktop != null ? Desktop.Clamp(mapped) : mapped;
        }

        /// <summary>
        /// Starts the smoothing state at a known-good input position, with no velocity history,
        /// so a new pointer session begins exactly where the hand is instead of from whatever
        /// single sample happened to arrive first.
        /// </summary>
        public void SeedSmoothing(MVector2 inputPosition)
        {
            ResetSmoothing();

            MVector2 mapped = GetClampedOutputPosition(inputPosition);
            positionFilter.Seed(mapped);
            smoothedPosition = mapped;
            hasSmoothedPosition = true;
        }

        /// <summary>
        /// Drops the smoothing state, so the next smoothed position starts from the hand's
        /// actual mapped position. Call this whenever tracking is lost or a new session starts,
        /// otherwise the cursor eases in from wherever it was left behind.
        /// </summary>
        public void ResetSmoothing()
        {
            hasSmoothedPosition = false;
            smoothedPosition = MVector2.Zero;
            positionFilter.Reset();
            residualMeanSquare = 0;
        }

        public void SetRects(MRect inputRect, MRect outputRect)
        {
            _inputRect = inputRect;
            _outputRect = outputRect;
            UpdateMapping();
        }

        public void UpdateMapping()
        {
            if (_scaleAlign == ScaleAlignment.None) return;

            double scaleX = OutputRect.DeltaX / InputRect.DeltaX;
            double scaleY = OutputRect.DeltaY / InputRect.DeltaY;

            switch (_scaleAlign)
            {
                case ScaleAlignment.Both:
                    _alignScale.X = scaleX;
                    _alignScale.Y = scaleY;
                    break;
                case ScaleAlignment.Horizontal:
                    _alignScale.X = scaleX;
                    _alignScale.Y = scaleX;
                    break;
                case ScaleAlignment.LongerRange:
                    double scaleOfLongerRange = Math.Abs(OutputRect.Width > OutputRect.Height ? scaleX : scaleY);
                    _alignScale.X = scaleOfLongerRange * (scaleX < 0 ? -1 : 1);
                    _alignScale.Y = scaleOfLongerRange * (scaleY < 0 ? -1 : 1);
                    break;
                case ScaleAlignment.Vertical:
                    _alignScale.X = scaleY;
                    _alignScale.Y = scaleY;
                    break;
                case ScaleAlignment.ShorterRange:
                    double scaleOfShorterRange = Math.Abs(OutputRect.Width < OutputRect.Height ? scaleX : scaleY);
                    _alignScale.X = scaleOfShorterRange * (scaleX < 0 ? -1 : 1);
                    _alignScale.Y = scaleOfShorterRange * (scaleY < 0 ? -1 : 1);
                    break;
                default:
                    _alignScale.X = scaleX < 0 ? -1 : 1;
                    _alignScale.Y = scaleY < 0 ? -1 : 1;
                    break;
            }

            // No separate origin offset: OutputRect.Center already carries the rect's origin,
            // so adding Left/Top again only happened to be harmless while the output rect
            // started at (0,0). A virtual desktop whose origin is negative - a monitor placed
            // left of or above the primary one - would otherwise be shifted by that origin.
            totalScale = _moveScale * _alignScale;
        }

    }
}
