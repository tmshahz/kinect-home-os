using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace KinectV2MouseControl
{
    public enum VoiceSpeechEngine
    {
        Whisper,
        Windows
    }

    /// <summary>
    /// Owns one loopback-only server for the lifetime of voice. The job also owns the child
    /// on a crash, when managed disposal cannot run. No audio is written to disk.
    /// </summary>
    public sealed class WhisperService : IDisposable
    {
        public static readonly string Root = Environment.GetEnvironmentVariable("KINECTOS_WHISPER_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KinectHomeOS", "whisper");
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly object sync = new object();
        private readonly HttpClient http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        private TaskCompletionSource<Uri> ready = NewReady();
        private Process process;
        private IntPtr job;
        private bool started;
        private bool disposed;
        private StreamWriter log;

        public event EventHandler<string> Unavailable;
        public int Transcriptions { get; private set; }

        public static bool IsInstalled
        {
            get { return File.Exists(Path.Combine(Root, "bin", "whisper-server.exe")) && File.Exists(Path.Combine(Root, "models", "ggml-base.en.bin")); }
        }

        private static TaskCompletionSource<Uri> NewReady()
        {
            return new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Start()
        {
            lock (sync)
            {
                if (started || disposed) { return; }
                started = true;
            }
            Task.Run(() => SuperviseAsync(lifetime.Token));
        }

        private async Task SuperviseAsync(CancellationToken token)
        {
            string failure = "Whisper files are missing.";
            try
            {
                for (int attempt = 0; attempt < 3 && !token.IsCancellationRequested; attempt++)
                {
                    try
                    {
                        if (!IsInstalled) { break; }
                        Uri address = StartServer();
                        Stopwatch startup = Stopwatch.StartNew();
                        while (startup.Elapsed < TimeSpan.FromSeconds(20))
                        {
                            token.ThrowIfCancellationRequested();
                            if (!Alive()) { throw new IOException("Whisper server exited during startup."); }
                            try
                            {
                                using (CancellationTokenSource probe = CancellationTokenSource.CreateLinkedTokenSource(token))
                                {
                                    probe.CancelAfter(600);
                                    using (HttpResponseMessage response = await http.GetAsync(address, probe.Token).ConfigureAwait(false))
                                    {
                                        if (response.IsSuccessStatusCode)
                                        {
                                            lock (sync) { ready.TrySetResult(address); }
                                            RuntimeLog.Write("Whisper ready in " + startup.ElapsedMilliseconds + " ms (loopback, CPU).");
                                            break;
                                        }
                                    }
                                }
                            }
                            catch (HttpRequestException) { }
                            catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); }
                            await Task.Delay(100, token).ConfigureAwait(false);
                        }
                        if (startup.Elapsed >= TimeSpan.FromSeconds(20)) { throw new TimeoutException("Whisper startup timed out."); }
                        while (Alive()) { await Task.Delay(250, token).ConfigureAwait(false); }
                        failure = "Whisper server exited repeatedly.";
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { failure = ex.Message; }
                    finally { StopServer(); }
                    lock (sync)
                    {
                        ready.TrySetCanceled();
                        ready = NewReady();
                    }
                    await Task.Delay(1000 * (attempt + 1), token).ConfigureAwait(false);
                }
                if (!token.IsCancellationRequested)
                {
                    lock (sync) { ready.TrySetException(new IOException(failure)); }
                    EventHandler<string> handler = Unavailable;
                    if (handler != null) { handler(this, failure); }
                }
            }
            catch (OperationCanceledException) { }
            finally { StopServer(); }
        }

        private Uri StartServer()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            lock (sync)
            {
                if (disposed) { throw new OperationCanceledException(); }
                job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) { throw new IOException("Cannot create the Whisper process job."); }
                JobLimits limits = new JobLimits();
                limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                int length = Marshal.SizeOf(typeof(JobLimits));
                IntPtr memory = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(limits, memory, false);
                    if (!SetInformationJobObject(job, 9, memory, (uint)length)) { throw new IOException("Cannot configure the Whisper process job."); }
                }
                finally { Marshal.FreeHGlobal(memory); }
                Directory.CreateDirectory(Root);
                log = new StreamWriter(Path.Combine(Root, "server.log"), false) { AutoFlush = true };
                process = new Process();
                process.StartInfo = new ProcessStartInfo(Path.Combine(Root, "bin", "whisper-server.exe"),
                    "-m \"" + Path.Combine(Root, "models", "ggml-base.en.bin") + "\" --host 127.0.0.1 --port " + port + " -t 4")
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.Combine(Root, "bin")
                };
                process.OutputDataReceived += Drain;
                process.ErrorDataReceived += Drain;
                process.Start();
                if (!AssignProcessToJobObject(job, process.Handle)) { process.Kill(); throw new IOException("Cannot assign Whisper to its process job."); }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                return new Uri("http://127.0.0.1:" + port + "/");
            }
        }

        private void Drain(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) { return; }
            lock (sync)
            {
                try { if (log != null) { log.WriteLine(e.Data); } }
                catch (IOException) { }
            }
        }

        private bool Alive()
        {
            lock (sync) { return !disposed && process != null && !process.HasExited; }
        }

        public async Task<string> TranscribeAsync(short[] pcm, string vocabulary, CancellationToken token)
        {
            Start();
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                Task<Uri> pending;
                lock (sync) { pending = ready.Task; }
                Task delay = Task.Delay(Timeout.Infinite, timeout.Token);
                if (await Task.WhenAny(pending, delay).ConfigureAwait(false) != pending) { timeout.Token.ThrowIfCancellationRequested(); }
                Uri address = await pending.ConfigureAwait(false);
                using (MultipartFormDataContent form = new MultipartFormDataContent())
                {
                    form.Add(new ByteArrayContent(VoiceFeedbackSounds.ToWav(pcm, 16000)), "file", "request.wav");
                    form.Add(new StringContent("text"), "response_format");
                    form.Add(new StringContent("0"), "temperature");
                    form.Add(new StringContent("512"), "audio_ctx");
                    form.Add(new StringContent(vocabulary ?? ""), "prompt");
                    Transcriptions++;
                    using (HttpResponseMessage response = await http.PostAsync(new Uri(address, "inference"), form, timeout.Token).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        string result = (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim();
                        return IsEmptyTranscript(result, pcm.Length / 16000.0) ? "" : result;
                    }
                }
            }
        }

        public static bool IsEmptyTranscript(string text, double audioSeconds)
        {
            if (string.IsNullOrWhiteSpace(text)) { return true; }
            string cleaned = Regex.Replace(text, @"\[[^\]]*\]|\([^)]*\)", "").Trim(' ', '.', ',', '!');
            if (cleaned.Length == 0) { return true; }
            string normalized = VoiceCommandParser.Normalize(cleaned);
            return audioSeconds < 2 && (normalized == "thank you" || normalized == "thanks for watching" || normalized == "you" || normalized == "subscribe");
        }

        private void StopServer()
        {
            lock (sync)
            {
                if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
                if (process != null) { process.Dispose(); process = null; }
                if (log != null) { log.Dispose(); log = null; }
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) { return; }
                disposed = true;
                lifetime.Cancel();
                ready.TrySetCanceled();
            }
            StopServer();
            http.Dispose();
            // The supervisor still observes the lifetime token while leaving a probe.
        }

        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong A, B, C, D, E, F; }
        [StructLayout(LayoutKind.Sequential)] private struct JobLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int kind, IntPtr information, uint length);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
