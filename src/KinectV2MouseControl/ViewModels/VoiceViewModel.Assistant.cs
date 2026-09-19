using System;
using System.Linq;

namespace KinectV2MouseControl
{
    public partial class VoiceViewModel
    {
        public Func<string, bool> OtherRequest { get; set; }
        public event EventHandler AssistantCancelRequested;
        private bool assistantThinking;
        public string[] AssistantPhrases { get { return CustomCommands.Where(c => c.IsListening).Select(c => c.Phrase).ToArray(); } }

        private void CancelAssistant()
        {
            assistantThinking = false;
            EventHandler handler = AssistantCancelRequested;
            if (handler != null) { handler(this, EventArgs.Empty); }
        }
        public void ShowAssistantThinking(string detail)
        {
            assistantThinking = true;
            ShowHud(VoiceHudState.Thinking, "Thinking…", detail);
            LastCommandDetail = detail;
        }
        public void ShowAssistantResult(string text, bool success)
        {
            assistantThinking = false;
            LastCommandDetail = text;
            ShowOutcome(success ? VoiceOutcome.Executed : VoiceOutcome.NotRecognized, text);
            PhaseTitle = success ? "ASSISTANT RESULT" : "ASSISTANT STOPPED";
            HudText = text;
            HudDetail = PhaseTitle;
        }

        public bool TryRunText(string text, DesktopActionContext context, out DesktopActionResult result)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            VoiceIntent intent; string reason;
            if (!VoiceCommandParser.TryParse(text, BuildCustomPhraseSet(), out intent, out reason))
            {
                result = DesktopActionResult.Refused(reason);
                return VoiceCommandParser.IsBuiltInCommand(text);
            }
            if (intent.Kind == VoiceIntentKind.Cancel) { CancelAssistant(); result = DesktopActionResult.Ok("Cancelled."); }
            else if (intent.Kind == VoiceIntentKind.Custom) { result = RunAssistantCustom(text, context); }
            else if (intent.Kind == VoiceIntentKind.Shell) { result = RunAssistantBuiltIn(intent.CommandId, context); }
            else { result = engine.ExecuteRequest(intent.Action, context); }
            return true;
        }

        public DesktopActionResult RunAssistantBuiltIn(string id, DesktopActionContext context)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            ActionDescriptor action = ActionCatalog.Find(id);
            if (!CustomCommandRules.IsAssignable(action)) { return DesktopActionResult.Refused("That built-in action is not allowed."); }
            return engine.ExecuteRequest(action.Action, context);
        }

        public DesktopActionResult RunAssistantCustom(string phrase, DesktopActionContext context)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            CustomCommandRowViewModel row = CustomCommands.FirstOrDefault(c => c.IsListening && VoicePhrases.MatchKey(c.Phrase) == VoicePhrases.MatchKey(phrase));
            if (row == null) { return DesktopActionResult.Refused("No enabled custom command exactly matches that phrase."); }
            if (row.Kind == CustomCommandKind.Keys)
            {
                if (row.OnlyInApp.Length > 0 && !ForegroundApp.Matches(ForegroundApp.CurrentProcessName(), row.OnlyInApp))
                { return DesktopActionResult.Refused(row.OnlyInApp + " is not in front; nothing pressed."); }
                return engine.ExecuteRequest(ControlAction.Keys(row.Keys), context);
            }
            if (row.Kind == CustomCommandKind.OpenApp) { return engine.ExecuteRequest(ControlAction.Launch(row.Target), context); }
            return RunAssistantBuiltIn(row.ActionId, context);
        }
    }
}
