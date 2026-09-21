using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
        public string Uri { get; set; }
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
        private const int MaxRankedCandidates = 300;
        private static readonly HashSet<string> Extensions = new HashSet<string>(
            (".pdf .doc .docx .xls .xlsx .ppt .pptx .txt .md .csv .rtf .odt .png .jpg .jpeg .gif .webp .mp3 .wav .m4a .mp4 .mkv .mov .html").Split(' '), StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> DocumentExtensions = new HashSet<string>(
            ".pdf .doc .docx .xls .xlsx .ppt .pptx .txt .md .csv .rtf .odt".Split(' '), StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> NoTyping = new HashSet<string>(
            ("cmd powershell pwsh WindowsTerminal conhost wt explorer regedit mmc Taskmgr KinectV2MouseControl KINECT-OS").Split(' '), StringComparer.OrdinalIgnoreCase);

        public static bool Handles(ControlActionType type)
        {
            return (type >= ControlActionType.LaunchAppByName && type <= ControlActionType.TypeText)
                || type == ControlActionType.CloseWindowByName || type == ControlActionType.OpenDeepLink;
        }

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

        /// <summary>
        /// Only opens explicit, non-web app routes. Spotify search text is escaped here so a
        /// model-provided title cannot introduce another URI component.
        /// </summary>
        public static string CheckedDeepLink(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.StartsWith(@"\\", StringComparison.Ordinal)
                || value.IndexOfAny(new[] { '\0', '\r', '\n', '"' }) >= 0)
            { throw new ArgumentException("Deep link must be a 1–2048 character spotify: or ms-settings: URI without quotes, line breaks, or a network path."); }

            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri)
                || (uri.Scheme != "spotify" && uri.Scheme != "ms-settings") || !string.IsNullOrEmpty(uri.UserInfo))
            { throw new ArgumentException("Only spotify: and ms-settings: deep links without embedded credentials can be opened."); }

            const string SpotifySearch = "spotify:search:";
            if (value.StartsWith(SpotifySearch, StringComparison.OrdinalIgnoreCase))
            {
                string query = value.Substring(SpotifySearch.Length);
                if (string.IsNullOrWhiteSpace(query)) { throw new ArgumentException("Spotify search needs a song, artist, or album."); }
                return SpotifySearch + Uri.EscapeDataString(query);
            }

            return uri.AbsoluteUri;
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
                    case ControlActionType.OpenDeepLink:
                        string deepLink = CheckedDeepLink(request.Uri);
                        if (!context.DryRun) { Process.Start(new ProcessStartInfo(deepLink) { UseShellExecute = true }); }
                        return DesktopActionResult.Ok((context.DryRun ? "Would open " : "Opened ") + deepLink);
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
                    case ControlActionType.CloseWindowByName:
                        return DesktopWindows.Close(request, context.DryRun);
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
                        else if (FileNameMatches(Path.GetFileName(item), query) && IsPersonalFile(item, roots))
                        {
                            ConsiderCandidate(matches, new FileInfo(item), query);
                        }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }

            return matches
                .OrderBy(file => Relevance(file.Name, query))
                .ThenBy(file => DocumentRank(file.Extension))
                .ThenByDescending(file => file.LastWriteTimeUtc)
                .Take(8)
                .Select(file => file.FullName)
                .ToArray();
        }

        /// <summary>
        /// Hold at most 300 names. A later match is dropped only when it ranks worse than
        /// everything already held, never because it is older. The list is ordered once, after the walk.
        /// </summary>
        private static void ConsiderCandidate(List<FileInfo> matches, FileInfo candidate, string query)
        {
            if (matches.Count < MaxRankedCandidates)
            {
                matches.Add(candidate);
                return;
            }

            int worst = 0;
            for (int i = 1; i < matches.Count; i++)
            {
                if (CompareCandidates(matches[worst], matches[i], query) < 0)
                {
                    worst = i;
                }
            }

            if (CompareCandidates(candidate, matches[worst], query) < 0)
            {
                matches[worst] = candidate;
            }
        }

        /// <summary>Negative when left belongs ahead of right.</summary>
        private static int CompareCandidates(FileInfo left, FileInfo right, string query)
        {
            int relevance = Relevance(left.Name, query).CompareTo(Relevance(right.Name, query));
            if (relevance != 0) { return relevance; }
            int document = DocumentRank(left.Extension).CompareTo(DocumentRank(right.Extension));
            if (document != 0) { return document; }
            return right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
        }

        private static int DocumentRank(string extension)
        {
            return DocumentExtensions.Contains(extension) ? 0 : 1;
        }

        private static bool FileNameMatches(string fileName, string query)
        {
            if (fileName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
            string normalizedQuery = NormalizeName(query);
            return normalizedQuery.Length > 0
                && NormalizeName(fileName).IndexOf(normalizedQuery, StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// 0 exact stem, 1 each query word is a whole token, 2 prefix, 3 substring.
        /// Space, hyphen, underscore and dot are the same token boundary.
        /// </summary>
        private static int Relevance(string fileName, string query)
        {
            string stem = NormalizeName(Path.GetFileNameWithoutExtension(fileName));
            string normalizedQuery = NormalizeName(query);
            if (stem.Length > 0 && stem == normalizedQuery) { return 0; }

            string[] queryTokens = SplitTokens(normalizedQuery);
            string[] nameTokens = SplitTokens(NormalizeName(fileName));
            if (TokensMatch(nameTokens, queryTokens, false)) { return 1; }
            if ((normalizedQuery.Length > 0 && stem.StartsWith(normalizedQuery, StringComparison.Ordinal))
                || TokensMatch(nameTokens, queryTokens, true))
            { return 2; }
            return 3;
        }

        private static bool TokensMatch(string[] nameTokens, string[] queryTokens, bool asPrefix)
        {
            if (queryTokens.Length == 0) { return false; }
            bool[] used = new bool[nameTokens.Length];
            for (int q = 0; q < queryTokens.Length; q++)
            {
                bool found = false;
                for (int n = 0; n < nameTokens.Length; n++)
                {
                    if (used[n]) { continue; }
                    bool hit = asPrefix
                        ? nameTokens[n].StartsWith(queryTokens[q], StringComparison.Ordinal)
                        : nameTokens[n] == queryTokens[q];
                    if (!hit) { continue; }
                    used[n] = true;
                    found = true;
                    break;
                }

                if (!found) { return false; }
            }

            return true;
        }

        private static string[] SplitTokens(string normalized)
        {
            if (string.IsNullOrEmpty(normalized)) { return new string[0]; }
            return normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string NormalizeName(string value)
        {
            if (string.IsNullOrEmpty(value)) { return ""; }
            StringBuilder builder = new StringBuilder(value.Length);
            bool pendingSpace = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = char.ToLowerInvariant(value[i]);
                if (c == ' ' || c == '-' || c == '_' || c == '.')
                {
                    if (builder.Length > 0) { pendingSpace = true; }
                    continue;
                }

                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }

                builder.Append(c);
            }

            return builder.ToString();
        }
    }
}
