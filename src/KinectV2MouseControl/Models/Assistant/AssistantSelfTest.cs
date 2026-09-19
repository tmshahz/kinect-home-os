using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace KinectV2MouseControl
{
    internal static class AssistantSelfTest
    {
        private sealed class FakeModel : IAssistantModel
        {
            public Dictionary<string, object>[] Replies;
            public int Calls;
            public bool Delay;
            public List<object> Messages;
            public async Task<Dictionary<string, object>> CompleteAsync(List<object> messages, object[] tools, CancellationToken token)
            {
                Messages = messages;
                if (Delay) { await Task.Delay(5000, token); }
                return Replies[Math.Min(Calls++, Replies.Length - 1)];
            }
        }
        private static Dictionary<string, object> Reply(params object[] calls)
        { return AssistantJson.Read(AssistantJson.Write(new { role = "assistant", content = calls.Length == 0 ? "Done." : "", tool_calls = calls })); }
        private static object Call(string name, string args)
        { return new { id = Guid.NewGuid().ToString("N"), type = "function", function = new { name, arguments = args } }; }

        private static bool HasRefusal(FakeModel model)
        {
            foreach (object item in model.Messages)
            {
                Dictionary<string, object> message = AssistantJson.Read(AssistantJson.Write(item));
                if ((string)message["role"] == "tool")
                {
                    Dictionary<string, object> result = AssistantJson.Read((string)message["content"]);
                    if (!(bool)result["Success"]) { return true; }
                }
            }
            return false;
        }

        public static async Task<int> RunAsync(StringBuilder report, bool live, Dispatcher dispatcher)
        {
            int failures = 0;
            Stopwatch watch = Stopwatch.StartNew();
            Action<bool, string> check = (passed, name) => { report.AppendLine((passed ? "PASS " : "FAIL ") + name); if (!passed) { failures++; } };
            int executed = 0;
            ActionRouter router = new ActionRouter();
            Func<AssistantTool, Dictionary<string, object>, DesktopActionContext, Task<DesktopActionResult>> sink = (tool, args, context) => dispatcher.InvokeAsync(() =>
            {
                context.Cancellation.ThrowIfCancellationRequested();
                if (!context.DryRun) { throw new InvalidOperationException("Self-test must use dry-run."); }
                executed++;
                if (tool.Name == "builtin_action")
                {
                    ActionDescriptor action = ActionCatalog.Find((string)args["id"]);
                    return CustomCommandRules.IsAssignable(action) ? router.ExecuteRequest(action.Action, context, "ai") : DesktopActionResult.Refused("Built-in action refused.");
                }
                if (tool.Name == "custom_command") { return DesktopActionResult.Refused("No custom commands in test context."); }
                return router.ExecuteRequest(AssistantTools.Action(tool, args), context, "ai");
            }).Task;
            FakeModel multi = new FakeModel { Replies = new[] { Reply(Call("web_search", "{\"site\":\"youtube\",\"query\":\"lo-fi\"}")),
                Reply(Call("builtin_action", "{\"id\":\"volumeup\"}")), Reply() } };
            string answer = await new AssistantSession(multi, sink).RunAsync("test", "{}", true, CancellationToken.None);
            check(answer == "Done." && executed == 2 && multi.Calls == 3, "fake model multi-round tools through UI dry-run router");
            foreach (object bad in new[] { Call("run_shell", "{}"), Call("open_url", "not json"), Call("open_url", "{\"url\":7}"), Call("list_windows", "{\"extra\":\"x\"}") })
            {
                executed = 0;
                FakeModel fake = new FakeModel { Replies = new[] { Reply(bad), Reply() } };
                await new AssistantSession(fake, sink).RunAsync("test", "{}", true, CancellationToken.None);
                check(executed == 0 && HasRefusal(fake), "unknown/malformed arguments returned as error without execution");
            }
            FakeModel refused = new FakeModel { Replies = new[] { Reply(Call("open_file", "{\"path\":\"C:\\\\test.exe\"}")), Reply() } };
            await new AssistantSession(refused, sink).RunAsync("test", "{}", true, CancellationToken.None);
            check(HasRefusal(refused), "action refusal returned to model");
            FakeModel timeout = new FakeModel { Delay = true, Replies = new[] { Reply() } };
            try
            {
                await new AssistantSession(timeout, sink) { TimeLimit = TimeSpan.FromMilliseconds(100) }.RunAsync("test", "{}", true, CancellationToken.None);
                check(false, "overall timeout");
            }
            catch (OperationCanceledException) { check(true, "overall timeout"); }
            using (CancellationTokenSource cancel = new CancellationTokenSource(100))
            {
                executed = 0;
                try { await new AssistantSession(timeout, sink).RunAsync("test", "{}", true, cancel.Token); check(false, "cancellation"); }
                catch (OperationCanceledException) { check(executed == 0, "cancellation prevents tool execution"); }
            }
            executed = 0;
            FakeModel forever = new FakeModel { Replies = new[] { Reply(Call("builtin_action", "{\"id\":\"mute\"}")) } };
            string stopped = await new AssistantSession(forever, sink).RunAsync("test", "{}", true, CancellationToken.None);
            check(executed == 6 && stopped.Contains("limit"), "six-round maximum");
            byte[] secret = Encoding.UTF8.GetBytes("self-test-placeholder");
            byte[] encrypted = ProtectedData.Protect(secret, null, DataProtectionScope.CurrentUser);
            check(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser).SequenceEqual(secret), "DPAPI CurrentUser round trip (no stored key changed)");
            check(DeepSeekClient.SafeError(new Exception("test SECRET"), "SECRET") == "test [key hidden]", "error text masks key");
            VoiceIntent intent; string reason;
            check(VoiceCommandParser.TryParse("Stop.", CustomPhraseSet.Empty, out intent, out reason) && intent.Kind == VoiceIntentKind.Cancel, "stop cancels locally");
            report.AppendLine("Offline assistant checks: " + watch.ElapsedMilliseconds + " ms");

            if (live)
            {
                if (!DeepSeekKeyStore.Exists) { report.AppendLine("SKIP live DeepSeek: no saved key. Add/test it on the AI page, then run --ai-self-test --live."); }
                else
                {
                    string key = DeepSeekKeyStore.Read();
                    using (DeepSeekClient client = new DeepSeekClient(key, "deepseek-flash"))
                    {
                        foreach (string request in new[] { "put ChatGPT on the top right of screen 2", "search YouTube for lo-fi music", "volume up" })
                        {
                            Stopwatch latency = Stopwatch.StartNew();
                            AssistantSession session = new AssistantSession(client, sink);
                            session.Step += step => report.AppendLine("  " + step.Replace(key, "[key hidden]"));
                            try
                            {
                                string result = await session.RunAsync(request, "{\"monitor_count\":2,\"order\":\"left-to-right\",\"foreground_app\":\"notepad\",\"custom_phrases\":[],\"builtin_action_ids\":[\"volumeup\",\"mute\"]}", true, CancellationToken.None);
                                report.AppendLine("LIVE DRY-RUN " + request + " → " + result.Replace(key, "[key hidden]") + " · " + latency.ElapsedMilliseconds + " ms");
                            }
                            catch (Exception ex) { failures++; report.AppendLine("FAIL live: " + DeepSeekClient.SafeError(ex, key)); }
                        }
                    }
                }
            }
            report.AppendLine(failures == 0 ? "ASSISTANT SELF-TEST PASSED" : "ASSISTANT SELF-TEST FAILED");
            return failures == 0 ? 0 : 4;
        }
    }
}
