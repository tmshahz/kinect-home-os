using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

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

        private static object Member(object target, string name, bool method, params object[] args)
        { return target.GetType().InvokeMember(name, method ? BindingFlags.InvokeMethod : BindingFlags.GetProperty, null, target, args); }
        private static void Release(object item)
        { if (item != null && Marshal.IsComObject(item)) { Marshal.FinalReleaseComObject(item); } }

        /// <summary>Only launch targets supplied by Windows' app index; a model cannot supply an executable path.</summary>
        private static List<Entry> Index()
        {
            if (cache != null && DateTime.UtcNow - cachedAt < TimeSpan.FromMinutes(5)) { return cache; }
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
            cache = entries.GroupBy(e => Normalize(e.Name)).Select(g => g.First()).ToList();
            cachedAt = DateTime.UtcNow;
            return cache;
        }

        private static string Normalize(string name) { return Regex.Replace((name ?? "").ToLowerInvariant(), "[^a-z0-9]", ""); }
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
