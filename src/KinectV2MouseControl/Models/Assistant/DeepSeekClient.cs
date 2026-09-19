using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace KinectV2MouseControl
{
    internal static class AssistantJson
    {
        public static string Write(object value) { return new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 }.Serialize(value); }
        public static Dictionary<string, object> Read(string text)
        {
            Dictionary<string, object> value = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024, RecursionLimit = 32 }.DeserializeObject(text) as Dictionary<string, object>;
            if (value == null) { throw new ArgumentException("Expected a JSON object."); }
            return value;
        }
    }

    internal static class DeepSeekKeyStore
    {
        private static readonly string KeyPath = Path.Combine(RuntimeLog.DirectoryPath, "secrets", "deepseek.key");
        public static bool Exists { get { return File.Exists(KeyPath); } }
        public static string Read()
        {
            if (!Exists) { return null; }
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), null, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plain); }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
        public static void Save(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 512 || key.IndexOfAny(new[] { '\r', '\n', ' ' }) >= 0)
            { throw new ArgumentException("Enter a valid API key without spaces."); }
            byte[] plain = Encoding.UTF8.GetBytes(key);
            try
            {
                byte[] encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(Path.GetDirectoryName(KeyPath));
                string temporary = KeyPath + ".tmp";
                File.WriteAllBytes(temporary, encrypted);
                if (File.Exists(KeyPath)) { File.Replace(temporary, KeyPath, null); }
                else { File.Move(temporary, KeyPath); }
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
        public static void Remove() { File.Delete(KeyPath); }
    }

    internal interface IAssistantModel
    {
        Task<Dictionary<string, object>> CompleteAsync(List<object> messages, object[] tools, CancellationToken token);
    }

    /// <summary>Fixed origin and redirects disabled: the credential has exactly one network destination.</summary>
    internal sealed class DeepSeekClient : IAssistantModel, IDisposable
    {
        public static readonly string[] Models = { "deepseek-flash", "deepseek-v4-pro" };
        private readonly string key;
        private readonly string model;
        private readonly HttpClient http;
        public DeepSeekClient(string key, string model)
        {
            if (Array.IndexOf(Models, model) < 0) { throw new ArgumentException("Unknown DeepSeek model."); }
            this.key = key;
            this.model = model;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        }
        public async Task<Dictionary<string, object>> CompleteAsync(List<object> messages, object[] tools, CancellationToken token)
        {
            Dictionary<string, object> body = new Dictionary<string, object> {
                { "model", model }, { "messages", messages }, { "stream", false }, { "max_tokens", 1200 },
                { "thinking", new { type = "disabled" } }
            };
            if (tools != null && tools.Length > 0) { body.Add("tools", tools); body.Add("tool_choice", "auto"); }
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/chat/completions"))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Content = new StringContent(AssistantJson.Write(body), Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await http.SendAsync(request, token).ConfigureAwait(false))
                {
                    // A provider error body can echo request headers. Never display or log it.
                    if (!response.IsSuccessStatusCode) { throw new IOException("DeepSeek returned HTTP " + (int)response.StatusCode + ". Check the key, account balance and model."); }
                    Dictionary<string, object> parsed = AssistantJson.Read(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    object choices;
                    if (!parsed.TryGetValue("choices", out choices) || !(choices is object[]) || ((object[])choices).Length == 0)
                    { throw new IOException("DeepSeek returned no completion."); }
                    Dictionary<string, object> first = ((object[])choices)[0] as Dictionary<string, object>;
                    object message;
                    if (first == null || !first.TryGetValue("message", out message) || !(message is Dictionary<string, object>))
                    { throw new IOException("DeepSeek returned an invalid completion."); }
                    return (Dictionary<string, object>)message;
                }
            }
        }
        public static string SafeError(Exception error, string key)
        {
            string text = error is HttpRequestException ? "Could not reach DeepSeek. Check the internet connection." : error.Message;
            return string.IsNullOrEmpty(key) ? text : text.Replace(key, "[key hidden]");
        }
        public void Dispose() { http.Dispose(); }
    }
}
