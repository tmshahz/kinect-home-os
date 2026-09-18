using System;
using System.Diagnostics;
using System.Threading;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Drives the actual cursor position at a rate independent of the sensor.
    ///
    /// The Kinect body stream is 30Hz, so writing the cursor straight from the frame handler
    /// moves it in 33ms jumps. On a 60/120/144Hz display that reads as stepping no matter how
    /// good the filtering upstream is. This loop runs on its own thread, and each tick eases
    /// the cursor toward the latest target the frame handler published.
    ///
    /// It is a background thread rather than a DispatcherTimer or CompositionTarget.Rendering
    /// because cursor output must keep working while the settings window is minimized, which
    /// is the normal way this app is used. WPF stops rendering a minimized window, and
    /// dispatcher timers are subject to UI-thread priority; SetCursorPos needs neither.
    ///
    /// Thread safety: the frame handler is the only writer of the target, this loop is the only
    /// writer of the cursor, and the small shared block is guarded by a lock. Nothing else in
    /// the app moves the cursor, so there is no second path competing with the frame handler.
    /// </summary>
    public sealed class CursorOutputLoop : IDisposable
    {
        /// <summary>
        /// Target tick interval. ~125Hz nominal; the OS timer may deliver closer to 64Hz on a
        /// default 15.6ms timer resolution, which is still well above the sensor's 30Hz. The
        /// interpolation measures real elapsed time, so an irregular tick rate is harmless.
        /// </summary>
        private const int TICK_INTERVAL_MS = 8;

        /// <summary>
        /// Time constant of the exponential ease toward the target, in seconds. 15ms reaches
        /// ~63% of a step within half a sensor frame and ~95% within one and a half, which is
        /// enough to turn the 30Hz steps into continuous motion without a perceptible lag
        /// budget of its own.
        /// </summary>
        private const double FOLLOW_TIME_CONSTANT = 0.015;

        /// <summary>
        /// Upper bound on a single tick's elapsed time, so a suspended or descheduled thread
        /// cannot produce a huge jump in the ease.
        /// </summary>
        private const double MAX_TICK_DELTA = 0.25;

        private readonly object gate = new object();
        private readonly ManualResetEventSlim wake = new ManualResetEventSlim(false);

        private Thread thread;
        private volatile bool isRunning;

        // Shared with the frame handler under 'gate'.
        private MVector2 target;
        private bool hasTarget;
        private bool snapRequested;

        // Owned by the loop thread.
        private MVector2 current;
        private bool hasCurrent;
        private int lastWrittenX = int.MinValue;
        private int lastWrittenY = int.MinValue;

        public void Start()
        {
            if (isRunning)
            {
                return;
            }

            isRunning = true;
            wake.Reset();

            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "KinectCursorOutput";
            thread.Priority = ThreadPriority.AboveNormal;
            thread.Start();
        }

        public void Stop()
        {
            if (!isRunning)
            {
                return;
            }

            isRunning = false;
            ClearTarget();
            wake.Set();

            Thread stopping = thread;
            thread = null;
            if (stopping != null)
            {
                stopping.Join(250);
            }
        }

        /// <summary>
        /// Publishes the latest filtered target. Called from the Kinect frame handler.
        /// </summary>
        public void SetTarget(MVector2 value)
        {
            lock (gate)
            {
                target = value;
                hasTarget = true;
            }
        }

        /// <summary>
        /// Stops driving the cursor and arms a snap for the next target, so control is handed
        /// back to the physical mouse and reacquisition does not glide in from a stale spot.
        /// </summary>
        public void ClearTarget()
        {
            lock (gate)
            {
                hasTarget = false;
                snapRequested = true;
            }
        }

        private void Run()
        {
            Stopwatch clock = Stopwatch.StartNew();
            double lastElapsed = clock.Elapsed.TotalSeconds;

            while (isRunning)
            {
                wake.Wait(TICK_INTERVAL_MS);
                wake.Reset();

                if (!isRunning)
                {
                    break;
                }

                double now = clock.Elapsed.TotalSeconds;
                double deltaTime = now - lastElapsed;
                lastElapsed = now;

                if (deltaTime <= 0)
                {
                    continue;
                }

                if (deltaTime > MAX_TICK_DELTA)
                {
                    deltaTime = MAX_TICK_DELTA;
                }

                MVector2 currentTarget;
                bool isActive;
                bool shouldSnap;

                lock (gate)
                {
                    currentTarget = target;
                    isActive = hasTarget;
                    shouldSnap = snapRequested;
                    snapRequested = false;
                }

                if (!isActive)
                {
                    // Nothing is controlling the cursor; leave it alone entirely so the
                    // physical mouse is unaffected.
                    hasCurrent = false;
                    lastWrittenX = int.MinValue;
                    lastWrittenY = int.MinValue;
                    continue;
                }

                if (!hasCurrent || shouldSnap)
                {
                    current = currentTarget;
                    hasCurrent = true;
                }
                else
                {
                    current += (currentTarget - current) * (1 - Math.Exp(-deltaTime / FOLLOW_TIME_CONSTANT));
                }

                int x = (int)Math.Round(current.X);
                int y = (int)Math.Round(current.Y);

                // Once the ease has converged the rounded position stops changing, and skipping
                // the redundant write means a stationary hand does not pin the physical mouse.
                if (x != lastWrittenX || y != lastWrittenY)
                {
                    MouseControl.MoveTo(x, y);
                    lastWrittenX = x;
                    lastWrittenY = y;
                }
            }
        }

        public void Dispose()
        {
            Stop();
            wake.Dispose();
        }
    }
}
