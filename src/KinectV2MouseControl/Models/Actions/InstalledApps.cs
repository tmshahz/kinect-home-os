using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace KinectV2MouseControl
{
    internal static class InstalledApps
    {
        internal sealed class Entry
        {
            public string Name;
            public string Target;
            public bool Packaged;
        }
        private static List<Entry> cache;
        private static DateTime cachedAt;
        private static int warming;

        /// <summary>
        /// Builds the index on a private STA thread so no caller pays for it. Enumerating the
        /// shell AppsFolder measured ~1.8 s on this machine (259 apps), and the assistant reads
        /// the app names on the UI thread for every request - blocking there froze the window
        /// and added that time to the request's own latency. Called once at startup, and again
        /// whenever a read finds the cache stale. Shell.Application is apartment-threaded, so
        /// the thread is STA and owns the COM objects it creates.
        /// </summary>
        internal static void WarmAsync()
        {
            if (Interlocked.CompareExchange(ref warming, 1, 0) != 0) { return; }
            Thread thread = new Thread(() =>
            {
                try { Rebuild(); }
                catch (Exception) { /* A failed index is retried by the next read; never fatal. */ }
                finally { Interlocked.Exchange(ref warming, 0); }
            });
            thread.IsBackground = true;
            thread.Name = "KINECT-OS app index";
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        /// <summary>
        /// The cached index without ever building it: what the assistant sends as context. An
        /// empty result means "not indexed yet", which is reported as a partial list rather
        /// than stalling the request.
        /// </summary>
        private static List<Entry> Cached()
        {
            List<Entry> current = cache;
            if (current == null || DateTime.UtcNow - cachedAt >= TimeSpan.FromMinutes(5)) { WarmAsync(); }
            return current ?? new List<Entry>();
        }

        private static object Member(object target, string name, bool method, params object[] args)
        { return target.GetType().InvokeMember(name, method ? BindingFlags.InvokeMethod : BindingFlags.GetProperty, null, target, args); }
        private static void Release(object item)
        { if (item != null && Marshal.IsComObject(item)) { Marshal.FinalReleaseComObject(item); } }

        /// <summary>Only launch targets supplied by Windows' app index; a model cannot supply an executable path.</summary>
        private static List<Entry> Index()
        {
            if (cache != null && DateTime.UtcNow - cachedAt < TimeSpan.FromMinutes(5)) { return cache; }
            return Rebuild();
        }

        private static List<Entry> Rebuild()
        {
            List<Entry> entries = new List<Entry>();
            foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu })
            {
                Stack<string> directories = new Stack<string>();
                string root = Environment.GetFolderPath(folder);
                if (Directory.Exists(root)) { directories.Push(root); }
                while (directories.Count > 0)
                {
                    string dir = directories.Pop();
                    try
                    {
                        foreach (string file in Directory.EnumerateFiles(dir, "*.lnk"))
                        { entries.Add(new Entry { Name = Path.GetFileNameWithoutExtension(file), Target = file }); }
                        foreach (string child in Directory.EnumerateDirectories(dir))
                        { if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) { directories.Push(child); } }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            object shell = null, apps = null, items = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                apps = Member(shell, "NameSpace", true, "shell:AppsFolder");
                items = Member(apps, "Items", true);
                foreach (object item in (IEnumerable)items)
                {
                    try
                    {
                        string name = Convert.ToString(Member(item, "Name", false));
                        string target = Convert.ToString(Member(item, "Path", false));
                        if (name.Length > 0 && target.Contains("!") && target.IndexOfAny(new[] { '"', '\r', '\n' }) < 0)
                        { entries.Add(new Entry { Name = name, Target = target, Packaged = true }); }
                    }
                    finally { Release(item); }
                }
            }
            catch (Exception) { /* Start Menu entries remain usable when the shell catalog is unavailable. */ }
            finally { Release(items); Release(apps); Release(shell); }
            cache = ResolveDuplicateNames(entries);
            cachedAt = DateTime.UtcNow;
            return cache;
        }

        private static string Normalize(string name) { return Regex.Replace((name ?? "").ToLowerInvariant(), "[^a-z0-9]", ""); }

        /// <summary>
        /// Keeps real same-named applications ambiguous, but suppresses browser PWA shortcuts
        /// when Windows also exposes a desktop or packaged application with that name.
        /// </summary>
        internal static List<Entry> ResolveDuplicateNames(IEnumerable<Entry> entries)
        {
            return entries.GroupBy(e => Normalize(e.Name)).SelectMany(group =>
            {
                List<Entry> realApps = group.Where(e => !IsBrowserPwaShortcut(e)).ToList();
                IEnumerable<Entry> preferred;
                if (realApps.Count > 0) { preferred = realApps; }
                else { preferred = group; }
                return preferred.GroupBy(e => (e.Packaged ? "package:" : "shortcut:") + (e.Target ?? ""), StringComparer.OrdinalIgnoreCase)
                    .Select(duplicates => duplicates.First());
            }).OrderBy(e => Normalize(e.Name), StringComparer.Ordinal).ThenBy(EntryPriority)
                .ThenBy(e => e.Target ?? "", StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Browser PWA shortcuts live in a recognizable Start Menu folder. This is deliberately
        /// a heuristic: the shortcut target is opaque here, so folder location is safer than
        /// resolving and trusting arbitrary .lnk contents.
        /// </summary>
        private static bool IsBrowserPwaShortcut(Entry entry)
        {
            if (entry == null || entry.Packaged || string.IsNullOrEmpty(entry.Target)) { return false; }
            string path = entry.Target.Replace('/', '\\');
            return path.IndexOf("\\Chrome Apps\\", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("\\Edge Apps\\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int EntryPriority(Entry entry)
        {
            if (IsBrowserPwaShortcut(entry)) { return 2; }
            return entry.Packaged ? 0 : 1;
        }

        /// <summary>Returns a stable, names-only subset suitable for remote assistant context.</summary>
        internal static string[] Names(IEnumerable<Entry> entries, int maximumNames, int maximumCharacters, out bool truncated)
        {
            List<string> names = entries.Select(e => e.Name).Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
            List<string> result = new List<string>();
            int characters = 0;
            foreach (string name in names)
            {
                if (result.Count >= maximumNames || characters + name.Length > maximumCharacters)
                { truncated = true; return result.ToArray(); }
                result.Add(name);
                characters += name.Length;
            }
            truncated = false;
            return result.ToArray();
        }

        /// <summary>
        /// Reads only what is already cached, so an assistant request is never blocked by the
        /// shell enumeration. A cold or stale cache triggers a background rebuild and reports
        /// the list as partial.
        /// </summary>
        internal static string[] Names(int maximumNames, int maximumCharacters, out bool truncated)
        {
            List<Entry> cached = Cached();
            string[] names = Names(cached, maximumNames, maximumCharacters, out truncated);
            if (cached.Count == 0) { truncated = true; }
            return names;
        }

        internal static List<Entry> Match(string name, IEnumerable<Entry> entries)
        {
            string key = Normalize(name);
            if (key.Length < 2) { return new List<Entry>(); }
            List<Entry> exact = entries.Where(e => Normalize(e.Name) == key).ToList();
            if (exact.Count > 0) { return exact; }
            var ranked = entries.Select(e => new { Entry = e, Score = Score(key, Normalize(e.Name)) }).Where(e => e.Score >= 0.7).OrderByDescending(e => e.Score).ToList();
            if (ranked.Count == 0) { return new List<Entry>(); }
            return ranked.Where(e => ranked[0].Score - e.Score < 0.12).Select(e => e.Entry).Take(6).ToList();
        }

        private static double Score(string left, string right)
        {
            if (right.Contains(left)) { return 0.9; }
            int[] previous = Enumerable.Range(0, right.Length + 1).ToArray();
            for (int i = 0; i < left.Length; i++)
            {
                int[] next = new int[right.Length + 1]; next[0] = i + 1;
                for (int j = 0; j < right.Length; j++)
                { next[j + 1] = Math.Min(Math.Min(next[j] + 1, previous[j + 1] + 1), previous[j] + (left[i] == right[j] ? 0 : 1)); }
                previous = next;
            }
            return 1.0 - previous[right.Length] / (double)Math.Max(left.Length, right.Length);
        }

        public static DesktopActionResult Launch(string name, bool dryRun)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.IndexOfAny(new[] { '\\', '/', ':', '"' }) >= 0)
            { return DesktopActionResult.Refused("Use an installed app name, not a path or command."); }
            List<Entry> matches = Match(name, Index());
            if (matches.Count == 0) { return DesktopActionResult.Refused("No installed app matches “" + name + "”."); }
            if (matches.Count != 1) { return DesktopActionResult.Refused("Ambiguous app: " + string.Join(", ", matches.Select(e => e.Name))); }
            Entry app = matches[0];
            if (!dryRun)
            {
                ProcessStartInfo start = app.Packaged
                    ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"shell:AppsFolder\\" + app.Target + "\"")
                    : new ProcessStartInfo(app.Target);
                start.UseShellExecute = true;
                Process.Start(start);
            }
            return DesktopActionResult.Ok((dryRun ? "Would open " : "Opened ") + app.Name);
        }
    }
}
