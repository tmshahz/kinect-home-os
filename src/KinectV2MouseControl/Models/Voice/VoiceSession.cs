using System;
using System.Globalization;
using System.Threading;

namespace KinectV2MouseControl
{
    /// <summary>
    /// The voice state machine. There is no path from WakeOnly to an action: the only grammar
    /// enabled there is the one-word wake grammar, and the only thing a wake can do is move to
    /// Acknowledging.
    ///
    ///   Off ─start→ WakeOnly ─"Kinect"→ Acknowledging ─chime done→ Listening
    ///                  ↑                       │                        │
    ///                  └───── spoke too early ─┘   command / unknown / timeout / cancel
    ///                  └─────────────────────────────────────────────────┘
    /// </summary>
    public enum VoicePhase
    {
        Off,

        /// <summary>
        /// Only the wake grammar is live. Ordinary speech can at most produce a scored "Kinect"
        /// hypothesis, which is judged and almost always dropped.
        /// </summary>
        WakeOnly,

        /// <summary>
        /// Wake word accepted, chime playing. The command grammar is live but nothing heard
        /// now can be a command: it began before the chime finished.
        /// </summary>
        Acknowledging,

        /// <summary>
        /// The command window: one phrase that begins after the chime may be accepted.
        /// </summary>
        Listening
    }

    public enum VoiceOutcome
    {
        None,
        Executed,
        NotRecognized,
        TimedOut,
        Cancelled,

        /// <summary>
        /// Speech that started before the chime finished and ran on into the window - a
        /// sentence that happened to contain the wake word, or someone not waiting.
        /// </summary>
        SpokeTooEarly
    }

    public enum VoiceVerdict
    {
        Accepted,
        Rejected,

        /// <summary>
        /// Not judged at all: echo of the chime, a breath, a click.
        /// </summary>
        Ignored
    }

    /// <summary>
    /// One wake activation. Authorizes at most one command: the engine consumes it when it
    /// accepts a phrase, and the view model marks it executed before acting, so neither a
    /// duplicate event nor a late callback can run a second action off the same "Kinect".
    /// </summary>
    public sealed class VoiceSession
    {
        private int authorized;
        private int executed;

        internal VoiceSession(int id, double wakeConfidence, TimeSpan wakeEnd)
        {
            Id = id;
            WakeTimeUtc = DateTime.UtcNow;
            WakeConfidence = wakeConfidence;
            WakeEnd = wakeEnd;
        }

        public int Id { get; private set; }
        public DateTime WakeTimeUtc { get; private set; }
        public double WakeConfidence { get; private set; }

        /// <summary>
        /// Audio-stream position where the wake word ended.
        /// </summary>
        public TimeSpan WakeEnd { get; private set; }

        /// <summary>
        /// Audio-stream position when the chime started playing.
        /// </summary>
        public TimeSpan ChimePosition { get; internal set; }

        /// <summary>
        /// Audio-stream position from which a command may begin: captured just after the chime
        /// (plus output/input latency) finished. Anything that started earlier is refused.
        /// </summary>
        public TimeSpan GatePosition { get; internal set; }

        public DateTime GateOpenUtc { get; internal set; }
        public DateTime DeadlineUtc { get; internal set; }
        public bool IsGateOpen { get; internal set; }
        internal bool Extended { get; set; }

        public VoiceOutcome Outcome { get; internal set; }

        internal bool TryAuthorize()
        {
            return Interlocked.Exchange(ref authorized, 1) == 0;
        }

        public bool TryMarkExecuted()
        {
            return Interlocked.Exchange(ref executed, 1) == 0;
        }
    }

    /// <summary>
    /// One judgement by the engine, for the developer diagnostics. Never contains transcribed
    /// background speech: in WakeOnly the only possible text is the wake word itself, and in
    /// the command window only catalog phrases can be recognized.
    /// </summary>
    public sealed class VoiceDecision
    {
        public DateTime Time { get; private set; }
        public string Stage { get; private set; }
        public VoiceVerdict Verdict { get; private set; }
        public string Phrase { get; private set; }
        public double Confidence { get; private set; }
        public double LeadSeconds { get; private set; }
        public double DurationSeconds { get; private set; }

        /// <summary>
        /// Silence between the end of the previous phrase and the start of this one.
        /// </summary>
        public double GapBeforeSeconds { get; private set; }

        public string Intent { get; private set; }
        public string Reason { get; private set; }
        public int SessionId { get; private set; }

        public VoiceDecision(string stage, VoiceVerdict verdict, string phrase, double confidence, double lead, double duration,
            double gapBefore, string intent, string reason, int sessionId)
        {
            GapBeforeSeconds = gapBefore;
            Time = DateTime.Now;
            Stage = stage;
            Verdict = verdict;
            Phrase = phrase ?? "";
            Confidence = confidence;
            LeadSeconds = lead;
            DurationSeconds = duration;
            Intent = intent ?? "";
            Reason = reason ?? "";
            SessionId = sessionId;
        }

        public string VerdictText
        {
            get
            {
                switch (Verdict)
                {
                    case VoiceVerdict.Accepted: return "accepted";
                    case VoiceVerdict.Rejected: return "rejected";
                    default: return "ignored";
                }
            }
        }

        public override string ToString()
        {
            string text = Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + Stage.PadRight(7) + " "
                + VerdictText.PadRight(8);
            if (Phrase.Length > 0)
            {
                text += " “" + Phrase + "”";
            }

            if (!double.IsNaN(Confidence))
            {
                text += " " + Confidence.ToString("0.00", CultureInfo.InvariantCulture);
            }

            if (!double.IsNaN(DurationSeconds))
            {
                text += " [" + DurationSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s, lead "
                    + LeadSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s, gap "
                    + (double.IsNaN(GapBeforeSeconds) ? "-" : GapBeforeSeconds.ToString("0.00", CultureInfo.InvariantCulture)) + " s]";
            }

            if (Intent.Length > 0)
            {
                text += " → " + Intent;
            }

            if (Reason.Length > 0)
            {
                text += "  (" + Reason + ")";
            }

            return text;
        }
    }

    /// <summary>
    /// Totals since voice was switched on, for the diagnostics readout.
    /// </summary>
    public sealed class VoiceCounters
    {
        public int BackgroundUtterances;
        public int WakeCandidatesRejected;
        public int Wakes;
        public int EchoOrNoiseIgnored;
        public int Executed;
        public int NotRecognized;
        public int TimedOut;
        public int Cancelled;
        public int SpokeTooEarly;

        public VoiceCounters Copy()
        {
            return (VoiceCounters)MemberwiseClone();
        }
    }

    public sealed class VoicePhaseEventArgs : EventArgs
    {
        public VoicePhaseEventArgs(VoicePhase phase, VoiceSession session, VoiceOutcome outcome, string detail)
        {
            Phase = phase;
            Session = session;
            Outcome = outcome;
            Detail = detail ?? "";
        }

        public VoicePhase Phase { get; private set; }

        /// <summary>
        /// The session this transition belongs to (the one just closed, for a return to
        /// WakeOnly). Null when voice starts or stops.
        /// </summary>
        public VoiceSession Session { get; private set; }

        public VoiceOutcome Outcome { get; private set; }
        public string Detail { get; private set; }
    }

    /// <summary>
    /// A command the engine has authorized. The only event that can lead to an action.
    /// </summary>
    public sealed class VoiceCommandEventArgs : EventArgs
    {
        public VoiceCommandEventArgs(VoiceSession session, VoiceIntent intent, string phrase, double confidence)
        {
            Session = session;
            Intent = intent;
            Phrase = phrase;
            Confidence = confidence;
        }

        public VoiceSession Session { get; private set; }
        public VoiceIntent Intent { get; private set; }
        public string Phrase { get; private set; }
        public double Confidence { get; private set; }
    }
}
