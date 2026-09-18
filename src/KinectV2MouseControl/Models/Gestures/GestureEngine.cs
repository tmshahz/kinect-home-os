namespace KinectV2MouseControl
{
    /// <summary>
    /// Runs the gesture recognizers and arbitrates between them.
    ///
    /// The engine is the only place that decides who may act on a given frame, which is what
    /// keeps the recognizers themselves free of cross-gesture conditionals. Priority, highest
    /// first:
    ///
    ///  0. Double clap. Sits above everything, including the control gate, because it is what
    ///     turns control back on - it cannot be subject to the switch it operates. It needs
    ///     neither a controlling hand nor an activation zone, only a tracked body.
    ///  1. Disengagement. Control switched off, no tracked body, or no controlling hand, means
    ///     IDLE: every other recognizer is reset and nothing is emitted. Checked before the
    ///     rest, so a gesture can never outlive the session that started it.
    ///  2. Grip on the controlling hand (drag). Owned by KinectCursor because it is welded to
    ///     the stage 4 click anchoring, and surfaced here as GestureContext.IsDragActive. While
    ///     a drag is live it owns the pointer hand and the lasso stands down.
    ///  3. Lasso on the controlling hand. Right click. Mutually exclusive with grip by
    ///     construction - HandState is one value, so Closed and Lasso cannot co-occur - and
    ///     explicitly suppressed during a drag in case a debouncer is mid-transition.
    ///  4. Swipe with the second hand. Evaluated before scroll, because a swipe is the more
    ///     specific claim on the same hand: it demands speed and horizontal tidiness, whereas
    ///     scroll accepts any sustained vertical offset. Suppressed while a drag is live.
    ///  5. Scroll with the second hand. Suppressed for the duration of the swipe cooldown, so
    ///     returning the hand after a swipe neither scrolls nor swipes back.
    ///
    /// The two hands therefore have disjoint jobs: the controlling hand does pointing, clicking
    /// and right-clicking; the second hand does scrolling and swiping. Neither can take the
    /// other's role while a session is live, and the second hand can never steal pointer
    /// control because KinectCursor latches the controlling hand for the whole session.
    /// </summary>
    public class GestureEngine
    {
        private readonly ActionRouter router;

        private readonly LassoRecognizer lasso;
        private readonly ScrollRecognizer scroll;
        private readonly SwipeRecognizer swipe;
        private readonly ClapRecognizer clap;

        /// <summary>
        /// Everything reset by Reset. Includes the clap: a half-finished double clap must not
        /// survive tracking loss either.
        /// </summary>
        private readonly IGestureRecognizer[] recognizers;

        public GestureState State { get; private set; }

        /// <summary>
        /// Live scroll neutral height in metres, or null when scroll is not engaged.
        /// </summary>
        public double? ScrollNeutralHeight
        {
            get
            {
                return scroll.NeutralHeight;
            }
        }

        public double ScrollOffset
        {
            get
            {
                return scroll.CurrentOffset;
            }
        }

        public string ClapStateText
        {
            get
            {
                return clap.StateText;
            }
        }

        /// <summary>
        /// Name of whatever currently has the floor, for the diagnostics readout.
        /// </summary>
        public string ActiveGestureName { get; private set; }

        public GestureEngine(GestureTuning tuning, ActionRouter router)
        {
            this.router = router;

            lasso = new LassoRecognizer(tuning);
            scroll = new ScrollRecognizer(tuning);
            swipe = new SwipeRecognizer(tuning);
            clap = new ClapRecognizer(tuning);

            // Registration order is documentation only; arbitration is explicit in Update.
            recognizers = new IGestureRecognizer[] { clap, lasso, swipe, scroll };

            State = GestureState.Idle;
            ActiveGestureName = "None";
        }

        public void Update(GestureContext context)
        {
            // 0. Clap runs first and unconditionally, because it owns the control switch. It
            // only needs a tracked body, not a control session.
            if (context.IsBodyTracked)
            {
                clap.Update(context, router);
            }
            else
            {
                clap.Reset();
            }

            // The clap above may have just flipped the gate, so the value captured when the
            // context was built is already stale. Both must agree before anything else runs:
            // switching off takes effect on this frame, and switching on waits for the next, so
            // the clap that re-enables control cannot also drive a gesture with it.
            if (!context.IsControlEnabled || !IsGateOpen())
            {
                ResetControlSession();
                State = GestureState.Idle;
                ActiveGestureName = "None";
                return;
            }

            // 1. Disengagement wins over everything else.
            if (!context.IsBodyTracked || context.ControllingHandIndex == GestureContext.NoHand)
            {
                ResetControlSession();
                router.ResetScrollAccumulation();
                State = GestureState.Idle;
                ActiveGestureName = "None";
                return;
            }

            State = GestureState.Pointer;

            if (!context.IsGestureVocabularyEnabled)
            {
                ResetControlSession();
                ActiveGestureName = context.IsDragActive ? "Drag" : "None";
                return;
            }

            // 3. Controlling hand: right click. Stands down on its own while dragging.
            lasso.Update(context, router);

            // A clap in progress owns both hands outright.
            bool clapOwnsSecondHand = clap.IsSuppressingSecondHand;

            // 4. Second hand: swipe is the more specific claim, so it runs first. Held off
            // during a drag, where switching windows could drop whatever is being dragged onto
            // the wrong one.
            context.SuppressSwipe = context.IsDragActive || clapOwnsSecondHand;
            swipe.Update(context, router);

            // 5. Second hand: scroll, unless a swipe has just happened.
            context.SuppressScroll = swipe.IsInCooldown || clapOwnsSecondHand;
            scroll.Update(context, router);

            if (scroll.IsEngaged)
            {
                State = GestureState.Scroll;
            }

            ActiveGestureName = DescribeActiveGesture(context);
        }

        /// <summary>
        /// Current state of the control gate, read live rather than from the context.
        /// </summary>
        private bool IsGateOpen()
        {
            return router.ControlGate == null || router.ControlGate.IsControlEnabled;
        }

        private string DescribeActiveGesture(GestureContext context)
        {
            if (context.IsDragActive)
            {
                return "Drag";
            }

            if (lasso.IsActive)
            {
                return "Lasso";
            }

            if (swipe.IsInCooldown)
            {
                return "Swipe";
            }

            if (scroll.IsEngaged)
            {
                return "Scroll";
            }

            return "None";
        }

        /// <summary>
        /// Returns to idle and clears every recognizer's temporal state. Called on tracking
        /// loss, controlling-hand changes, disengagement and disable/re-enable.
        /// </summary>
        public void Reset()
        {
            for (int i = 0; i < recognizers.Length; i++)
            {
                recognizers[i].Reset();
            }

            router.ResetScrollAccumulation();
            State = GestureState.Idle;
            ActiveGestureName = "None";
        }

        /// <summary>
        /// Clears the recognizers that belong to a control session, leaving the clap alone.
        ///
        /// Used on the per-frame idle paths, where the clap must keep watching so control can
        /// be switched back on, and when the controlling hand changes - bringing the hands
        /// together to clap can itself shuffle which hand is in charge, and a full reset there
        /// would discard the half-finished double clap every time.
        /// </summary>
        public void ResetControlSession()
        {
            lasso.Reset();
            swipe.Reset();
            scroll.Reset();
        }
    }
}
