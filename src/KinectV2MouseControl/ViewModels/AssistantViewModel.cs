using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace KinectV2MouseControl
{
    public sealed class AssistantViewModel : ObservableObject
    {
        private const int AssistantAppNameLimit = 60;
        private const int AssistantAppCharacterLimit = 2400;
        private readonly KinectCursorViewModel engine;
        private readonly VoiceViewModel voice;
        private readonly Dispatcher dispatcher;
        private CancellationTokenSource active;
        private int generation;
        private bool busy;
        private string requestText = "";
        private string status = "Add a DeepSeek key to enable other requests.";
        private string model = "deepseek-flash";
        private bool sendOtherRequests = true;
        public string[] Models { get { return DeepSeekClient.Models; } }
        public string Model { get { return model; } set { if (Array.IndexOf(Models, value) >= 0) { Set(ref model, value); } } }
        public bool SendOtherRequests { get { return sendOtherRequests; } set { if (Set(ref sendOtherRequests, value) && !value) { Cancel(); } } }
        public bool HasKey { get { return DeepSeekKeyStore.Exists; } }
        public string KeyStatus { get { return HasKey ? "Key saved for this Windows account" : "No key saved"; } }
        public bool IsBusy { get { return busy; } private set { Set(ref busy, value); } }
        public string RequestText { get { return requestText; } set { Set(ref requestText, value); } }
        public string Status { get { return status; } private set { Set(ref status, value); } }
        public ObservableCollection<string> Steps { get; private set; } = new ObservableCollection<string>();
        public RelayCommand SubmitCommand { get; private set; }
        public RelayCommand CancelCommand { get; private set; }
        public RelayCommand RemoveKeyCommand { get; private set; }

        public AssistantViewModel(KinectCursorViewModel engine, VoiceViewModel voice, Dispatcher dispatcher)
        {
            this.engine = engine; this.voice = voice; this.dispatcher = dispatcher;
            SubmitCommand = new RelayCommand(() => { _ = SubmitAsync(RequestText, false); }, () => !string.IsNullOrWhiteSpace(RequestText));
            CancelCommand = new RelayCommand(Cancel);
            RemoveKeyCommand = new RelayCommand(() =>
            {
                Cancel();
                try { DeepSeekKeyStore.Remove(); Status = "Key removed."; }
                catch (Exception) { Status = "Could not remove the saved key."; }
                Raise("HasKey"); Raise("KeyStatus");
            });
            voice.OtherRequest = text =>
            {
                if (!SendOtherRequests || !HasKey) { return false; }
                _ = SubmitAsync(text, true); return true;
            };
            voice.AssistantCancelRequested += (s, e) => Cancel();
            engine.GestureControlToggled += (s, e) => Cancel();
        }

        public void Cancel()
        {
            generation++;
            if (active != null) { active.Cancel(); active = null; }
            if (IsBusy) { Status = "Cancelled; completed steps are not undone."; voice.ShowAssistantResult(Status, false); }
            IsBusy = false;
        }

        public async Task TestAndSaveKeyAsync(string proposed)
        {
            Cancel();
            string key = proposed;
            int stamp = generation;
            using (CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                active = cancellation; IsBusy = true;
                try
                {
                    if (string.IsNullOrWhiteSpace(key)) { key = DeepSeekKeyStore.Read(); }
                    if (string.IsNullOrWhiteSpace(key)) { Status = "Enter an API key first."; return; }
                    key = key.Trim();
                    using (DeepSeekClient client = new DeepSeekClient(key.Trim(), Model))
                    { await client.CompleteAsync(new List<object> { new { role = "user", content = "Reply OK." } }, null, cancellation.Token); }
                    if (stamp != generation) { return; }
                    DeepSeekKeyStore.Save(key.Trim());
                    Raise("HasKey"); Raise("KeyStatus"); Status = "Key tested and saved.";
                }
                catch (OperationCanceledException) { if (stamp == generation) { Status = "Key test timed out."; } }
                catch (Exception ex) { if (stamp == generation) { Status = DeepSeekClient.SafeError(ex, key); } }
                finally { if (stamp == generation) { active = null; IsBusy = false; } }
            }
        }

        private async Task SubmitAsync(string text, bool fromVoice)
        {
            if (string.IsNullOrWhiteSpace(text)) { return; }
            Cancel();
            int stamp = generation;
            Stopwatch clock = Stopwatch.StartNew();
            Steps.Clear(); AddStep("Request: “" + text + "”");
            DesktopActionResult local;
            if (!fromVoice && voice.TryRunText(text, new DesktopActionContext(), out local))
            {
                Status = local.Message; AddStep(Status + " · " + clock.ElapsedMilliseconds + " ms");
                voice.ShowAssistantResult(Status, local.Success); return;
            }
            if (!SendOtherRequests || !HasKey || text.Length > 4000)
            { Status = "Not a command. Enable AI and add a key for other requests (maximum 4,000 characters)."; AddStep(Status); return; }
            string key = null;
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                active = cancellation; IsBusy = true;
                try
                {
                    key = DeepSeekKeyStore.Read();
                    using (DeepSeekClient client = new DeepSeekClient(key, Model))
                    {
                        AssistantSession session = new AssistantSession(client, ExecuteAsync);
                        session.Step += step => dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (stamp != generation) { return; }
                            string safe = string.IsNullOrEmpty(key) ? step : step.Replace(key, "[key hidden]");
                            AddStep(safe); voice.ShowAssistantThinking(safe);
                        }));
                        Status = "Thinking…"; voice.ShowAssistantThinking("“" + text + "”");
                        bool installedAppsPartial;
                        string[] installedApps = InstalledApps.Names(AssistantAppNameLimit, AssistantAppCharacterLimit, out installedAppsPartial);
                        string context = AssistantJson.Write(new { monitors = DesktopWindows.Monitors().Select((m, i) => new { number = i + 1, left = m.Left, top = m.Top, right = m.Right, bottom = m.Bottom }),
                            foreground_app = ForegroundApp.CurrentProcessName(), custom_phrases = voice.AssistantPhrases,
                            builtin_action_ids = CustomCommandRules.AssignableActions().Select(a => a.Id).ToArray(),
                            installed_app_names = installedApps, installed_app_names_partial = installedAppsPartial });
                        string answer = await session.RunAsync(text, context, false, cancellation.Token);
                        if (stamp != generation) { return; }
                        Status = string.IsNullOrEmpty(key) ? answer : answer.Replace(key, "[key hidden]");
                        AddStep(Status + " · total " + clock.ElapsedMilliseconds + " ms");
                        voice.ShowAssistantResult(Status, true);
                    }
                }
                catch (OperationCanceledException)
                { if (stamp == generation) { Status = "Request timed out; completed steps are not undone."; AddStep(Status); voice.ShowAssistantResult(Status, false); } }
                catch (Exception ex)
                { if (stamp == generation) { Status = DeepSeekClient.SafeError(ex, key); AddStep(Status); voice.ShowAssistantResult(Status, false); } }
                finally { if (stamp == generation) { active = null; IsBusy = false; } }
            }
        }

        private Task<DesktopActionResult> ExecuteAsync(AssistantTool tool, Dictionary<string, object> args, DesktopActionContext context)
        {
            return dispatcher.InvokeAsync(() =>
            {
                context.Cancellation.ThrowIfCancellationRequested();
                if (tool.Name == "custom_command") { return voice.RunAssistantCustom((string)args["phrase"], context); }
                if (tool.Name == "builtin_action") { return voice.RunAssistantBuiltIn((string)args["id"], context); }
                return engine.ExecuteRequest(AssistantTools.Action(tool, args), context);
            }).Task;
        }

        private void AddStep(string text)
        {
            if (Steps.Count >= 100) { Steps.RemoveAt(0); }
            Steps.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + text);
            ActivityLog.Post(ActivityKind.Action, text, "Assistant", "ai");
            RuntimeLog.Write("AI " + text);
        }
    }
}
