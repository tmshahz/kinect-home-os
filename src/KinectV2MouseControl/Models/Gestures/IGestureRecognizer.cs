namespace KinectV2MouseControl
{
    /// <summary>
    /// Overall state of the control session. Reported for diagnostics and used by the engine to
    /// decide what is allowed to run.
    /// </summary>
    public enum GestureState
    {
        /// <summary>
        /// Nothing engaged. The physical mouse is untouched.
        /// </summary>
        Idle,

        /// <summary>
        /// A hand is steering the pointer.
        /// </summary>
        Pointer,

        /// <summary>
        /// Pointer control is still live, and the second hand has claimed the wheel.
        /// </summary>
        Scroll
    }

    /// <summary>
    /// A single gesture, responsible for its own temporal state.
    ///
    /// Contract:
    ///  - Update is called once per body frame while a control session is live, and must not
    ///    retain the context instance, which is reused and refilled each frame.
    ///  - Emitted intents go to the sink. A recognizer never touches Win32 or the cursor.
    ///  - Reset must drop all accumulated state. It is called on tracking loss, controlling-hand
    ///    changes, disengagement and disable/re-enable, so a gesture cannot survive a session.
    /// </summary>
    public interface IGestureRecognizer
    {
        /// <summary>
        /// Short name for the diagnostics readout.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// True while this recognizer is mid-gesture and therefore owns its input.
        /// </summary>
        bool IsActive { get; }

        void Update(GestureContext context, IActionSink sink);

        void Reset();
    }
}
