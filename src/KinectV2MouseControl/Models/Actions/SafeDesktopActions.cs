using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace KinectV2MouseControl
{
    public sealed class DesktopActionRequest
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public string Browser { get; set; }
        public string Site { get; set; }
        public string Query { get; set; }
        public string Path { get; set; }
        public string Window { get; set; }
        public int Monitor { get; set; }
        public string Region { get; set; }
        public string Text { get; set; }
    }

    public sealed class DesktopActionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public object Data { get; set; }
        public static DesktopActionResult Ok(string message, object data = null)
        { return new DesktopActionResult { Success = true, Message = message, Data = data }; }
        public static DesktopActionResult Refused(string reason)
        { return new DesktopActionResult { Message = reason }; }
    }

    /// <summary>Found paths are capabilities: a fresh request starts with no permission to open a file.</summary>
    public sealed class DesktopActionContext
    {
        public bool DryRun { get; set; }
        public CancellationToken Cancellation { get; set; }
        internal readonly HashSet<string> FoundFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    internal static class SafeDesktopActions
    {
        private static readonly HashSet<string> Extensions = new HashSet<string>(
            (".pdf .doc .docx .xls .xlsx .ppt .pptx .txt .md .csv .rtf .odt .png .jpg .jpeg .gif .webp .mp3 .wav .m4a .mp4 .mkv .mov .html").Split(' '), StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> NoTyping = new HashSet<string>(
            ("cmd powershell pwsh WindowsTerminal conhost wt explorer regedit mmc Taskmgr KinectV2MouseControl KINECT-OS").Split(' '), StringComparer.OrdinalIgnoreCase);

        public static bool Handles(ControlActionType type)
        { return type >= ControlActionType.LaunchAppByName && type <= ControlActionType.TypeText; }

        public static bool CanOpenExtension(string path) { return Extensions.Contains(System.IO.Path.GetExtension(path)); }
        public static bool CanType(string process, string text)
        {
            return !string.IsNullOrWhiteSpace(process) && !NoTyping.Contains(process)
                && !string.Equals(process, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(text) && text.Length <= 500 && text.IndexOf('\0') < 0;
        }

        public static string WebUrl(string site, string query)
        {
            if (string.IsNullOrWhiteSpace(query) || query.Length > 2000) { throw new ArgumentException("Search query must contain 1–2000 characters."); }
            string prefix;
            switch ((site ?? "").ToLowerInvariant())
            {
                case "youtube": prefix = "https://www.youtube.com/results?search_query="; break;
                case "google": prefix = "https://www.google.com/search?q="; break;
                case "bing": prefix = "https://www.bing.com/search?q="; break;
                default: throw new ArgumentException("Search site must be youtube, google or bing.");
            }
            return prefix + Uri.EscapeDataString(query);
        }

        public static string CheckedUrl(string value, string browser)
        {
            Uri uri;
            if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || !Uri.TryCreate(value, UriKind.Absolute, out uri)
                || (uri.Scheme != "http" && uri.Scheme != "https") || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0)
            { throw new ArgumentException("Only http/https web pages without embedded credentials can be opened."); }
            if (!string.IsNullOrEmpty(browser) && browser != "default" && browser != "edge")
            { throw new ArgumentException("Browser must be default or edge."); }
            return browser == "edge" ? "microsoft-edge:" + uri.AbsoluteUri : uri.AbsoluteUri;
        }

        public static DesktopActionResult Execute(ControlAction action, DesktopActionContext context)
        {
            DesktopActionRequest request = action.Request ?? new DesktopActionRequest();
            try
            {
                context.Cancellation.ThrowIfCancellationRequested();
                switch (action.Type)
                {
                    case ControlActionType.LaunchAppByName:
                        return InstalledApps.Launch(request.Name, context.DryRun);
                    case ControlActionType.OpenUrl:
                    case ControlActionType.WebSearch:
                        string url = CheckedUrl(action.Type == ControlActionType.WebSearch ? WebUrl(request.Site, request.Query) : request.Url, request.Browser);
                        if (!context.DryRun) { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                        return DesktopActionResult.Ok((context.DryRun ? "Would open " : "Opened ") + url);
                    case ControlActionType.FindFiles:
                        bool partial;
                        string[] found = Find(request.Query, PersonalFolders(), context.Cancellation, out partial);
                        foreach (string path in found) { context.FoundFiles.Add(path); }
                        return DesktopActionResult.Ok("Found " + found.Length + " file(s)" + (partial ? " (search time limit reached)" : ""), found);
                    case ControlActionType.OpenFile:
                        string file = Path.GetFullPath(request.Path ?? "");
                        if (!context.FoundFiles.Contains(file)) { return DesktopActionResult.Refused("Find this file in the same request before opening it."); }
                        if (!CanOpenExtension(file) || !IsPersonalFile(file, PersonalFolders()) || !File.Exists(file))
                        { return DesktopActionResult.Refused("File is missing, outside personal folders, a link, or not an allowed document/media type."); }
                        if (!context.DryRun) { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
                        return DesktopActionResult.Ok((context.DryRun ? "Would open " : "Opened ") + Path.GetFileName(file));
                    case ControlActionType.ListWindows:
                        return DesktopActionResult.Ok("Visible windows", DesktopWindows.List().Select(w => new { process = w.Process, title = w.Title, monitor = w.Monitor }).ToArray());
                    case ControlActionType.PlaceWindow:
                        return DesktopWindows.Place(request, context.DryRun);
                    case ControlActionType.TypeText:
                        string foreground = ForegroundApp.CurrentProcessName();
                        if (!CanType(foreground, request.Text))
                        { return DesktopActionResult.Refused("Text entry requires 1–500 characters and a foreground app other than a shell, system tool, or KINECT-OS."); }
                        if (!context.DryRun && !Win32Input.SendUnicode(request.Text)) { return DesktopActionResult.Refused("Windows refused some or all of the text input."); }
                        return DesktopActionResult.Ok((context.DryRun ? "Would type " : "Typed ") + request.Text.Length + " characters into " + foreground);
                    default: return DesktopActionResult.Refused("Unsupported desktop action.");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return DesktopActionResult.Refused(ex.Message); }
        }

        internal static string[] PersonalFolders()
        {
            List<string> roots = new List<string>();
            foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic })
            { roots.Add(Environment.GetFolderPath(folder)); }
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
            foreach (string name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            { roots.Add(Environment.GetEnvironmentVariable(name)); }
            return roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        internal static bool IsBelow(string path, string root)
        { return path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }

        /// <summary>Check every ancestor again when opening: a discovered directory can have been replaced by a junction.</summary>
        internal static bool IsPersonalFile(string path, string[] roots)
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.IndexOf(':', 2) >= 0 || !roots.Any(r => IsBelow(full, r))) { return false; }
                foreach (string forbidden in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) })
                { if (forbidden.Length > 0 && IsBelow(full, forbidden)) { return false; } }
                string item = full;
                while (!string.IsNullOrEmpty(item))
                {
                    if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) { return false; }
                    item = Path.GetDirectoryName(item);
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        internal static string[] Find(string query, string[] roots, CancellationToken token, out bool partial)
        {
            if (string.IsNullOrWhiteSpace(query) || query.Length > 200 || query.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
            { throw new ArgumentException("Search for a file name, not a path (1–200 characters)."); }
            List<FileInfo> matches = new List<FileInfo>();
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Stack<string> pending = new Stack<string>(roots.Where(Directory.Exists));
            Stopwatch clock = Stopwatch.StartNew();
            partial = false;
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                if (clock.Elapsed.TotalSeconds > 2) { partial = true; break; }
                string directory = pending.Pop();
                if (!visited.Add(directory)) { continue; }
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { continue; }
                    foreach (string item in Directory.EnumerateFileSystemEntries(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        if (clock.Elapsed.TotalSeconds > 2) { partial = true; break; }
                        FileAttributes attributes = File.GetAttributes(item);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { continue; }
                        if ((attributes & FileAttributes.Directory) != 0) { pending.Push(item); }
                        else if (Path.GetFileName(item).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 && IsPersonalFile(item, roots))
                        {
                            matches.Add(new FileInfo(item));
                            matches = matches.OrderByDescending(f => f.LastWriteTimeUtc).Take(8).ToList();
                        }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            return matches.Select(f => f.FullName).ToArray();
        }
    }
}
