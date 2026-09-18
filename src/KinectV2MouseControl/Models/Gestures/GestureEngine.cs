namespace KinectV2MouseControl
{
    /// <summary>
    /// Runs the gesture recognizers and arbitrates between them.
    ///
    /// The engine is the only place that decides who may act on a given frame, which is what
    /// keeps the recognizers themselves free of cross-gesture conditionals. Hand roles are
    /// fixed: the right hand points, clicks, drags and right clicks; the left hand only ever
    /// scrolls and swipes, and only while its fist clutch is engaged. Priority, highest first:
    ///
    ///  0. Double clap. Sits above everything, including the control gate, because it is what
    ///     turns control back on - it cannot be subject to the switch it operates. It needs
    ///     neither a pointer session nor an activation zone, only a tracked body.
    ///  1. Disengagement. Control switched off, no tracked body, or no active pointer session,
    ///     means IDLE: every other recognizer and the clutch are reset and nothing is emitted.
    ///  2. Grip on the right hand (drag). Owned by KinectCursor because it is welded to the
    ///     click anchoring, and surfaced here as GestureContext.IsDragActive. While a drag is
    ///     live the lasso stands down.
    ///  3. Lasso on the right hand. Right click.
    ///  4. Left-fist clutch. Decides SecondaryGestureArmed. Releasing it resets the swipe
    ///     history, the scroll engagement and neutral, and any part-accumulated scroll.
    ///  5. Swipe with the clutched left hand. Evaluated before scroll, because a swipe is the
    ///     more specific claim on the same hand. Suppressed while a drag is live.
    ///  6. Scroll with the clutched left hand. Suppressed for the duration of the swipe
    ///     cooldown, so a swipe takes priority and its return stroke does nothing.
    /// </summary>
    public class GestureEngine
    {
        private readonly ActionRouter router;

        private readonly LassoRecognizer lasso;
        private readonly ScrollRecognizer scroll;
        private readonly SwipeRecognizer swipe;
        private readonly ClapRecognizer clap;
        private readonly SecondaryClutch clutch;

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

        public double ScrollRate
        {
            get
            {
                return scroll.CurrentRate;
            }
        }

        public string ClapStateText
        {
            get
            {
                return clap.StateText;
            }
        }

        public string ClutchStateText
        {
            get
            {
                return clutch.StateText;
            }
        }

        public bool IsSecondaryGestureArmed
        {
            get
            {
                return clutch.IsArmed;
            }
        }

        /// <summary>
        /// What the clutched left hand is doing, for the diagnostics readout.
        /// </summary>
        public string SecondaryModeText
        {
            get
            {
                if (swipe.IsInCooldown)
                {
                    return "Swipe (cooldown)";
                }

                if (!clutch.IsArmed)
                {
                    return "None";
                }

                if (scroll.IsEngaged)
                {
                    return "Scroll";
                }

                return scroll.IsSettling ? "Scroll (settling)" : "Armed";
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
            clutch = new SecondaryClutch(tuning);

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

            // 3. Right hand: right click. Stands down on its own while dragging.
            lasso.Update(context, router);

            // 4. Left-fist clutch. Only a left hand inside the activation zone may engage it.
            HandSnapshot secondary = context.Hands[GestureContext.SecondaryHand];
            bool wasArmed = clutch.IsArmed;
            clutch.Update(secondary, secondary.IsActivated, context.DeltaTime);
            context.IsSecondaryGestureArmed = clutch.IsArmed;

            if (wasArmed != clutch.IsArmed)
            {
                if (!clutch.IsArmed)
                {
                    // Releasing the fist ends the secondary gesture outright. The recognizers
                    // reset themselves on seeing the clutch drop; the router's part-notch is
                    // cleared here so it cannot leak into the next engagement.
                    router.ResetScrollAccumulation();
                }

                RuntimeLog.Write(clutch.IsArmed ? "Left clutch armed" : "Left clutch released");
            }

            // A clap in progress owns both hands outright.
            bool clapOwnsSecondHand = clap.IsSuppressingSecondHand;

            // 5. Swipe is the more specific claim on the clutched hand, so it runs first. Held
            // off during a drag, where switching windows could drop whatever is being dragged
            // onto the wrong one.
            context.SuppressSwipe = context.IsDragActive || clapOwnsSecondHand;
            swipe.Update(context, router);

            // 6. Scroll, unless a swipe has just happened.
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
        /// Returns to idle and clears every recognizer's temporal state and the clutch. Called
        /// on tracking loss, disengagement, disable/re-enable and settings changes.
        /// </summary>
        public void Reset()
        {
            for (int i = 0; i < recognizers.Length; i++)
            {
                recognizers[i].Reset();
            }

            clutch.Reset();
            router.ResetScrollAccumulation();
            State = GestureState.Idle;
            ActiveGestureName = "None";
        }

        /// <summary>
        /// Clears the recognizers that belong to a pointer session, and the clutch, leaving the
        /// clap alone so a half-finished double clap survives the pointer session starting or
        /// ending (raising and lowering the hands to clap can do exactly that).
        /// </summary>
        public void ResetControlSession()
        {
            lasso.Reset();
            swipe.Reset();
            scroll.Reset();
            clutch.Reset();
            router.ResetScrollAccumulation();
        }
    }
}
