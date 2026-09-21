using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace KinectV2MouseControl
{
    internal static class AiSelfTest
    {
        public static int Run(out string report)
        {
            StringBuilder output = new StringBuilder("KINECT-OS AI action self-test (dry-run only)\n");
            int failures = 0;
            Action<bool, string> check = (passed, name) => { output.AppendLine((passed ? "PASS " : "FAIL ") + name); if (!passed) { failures++; } };
            try
            {
                check(SafeDesktopActions.WebUrl("youtube", "lo-fi & café") == "https://www.youtube.com/results?search_query=lo-fi%20%26%20caf%C3%A9", "URL query encoding");
                check(SafeDesktopActions.CheckedUrl("https://example.com/", "edge") == "microsoft-edge:https://example.com/", "Edge URL construction");
                ActionRouter router = new ActionRouter();
                DesktopActionContext context = new DesktopActionContext { DryRun = true };
                foreach (string bad in new[] { "file:///C:/x", "javascript:alert(1)", "ms-settings:", "https://user:secret@example.com/" })
                {
                    check(!router.ExecuteRequest(new ControlAction(ControlActionType.OpenUrl) { Request = new DesktopActionRequest { Url = bad } }, context, "ai").Success, "URL refused: " + bad.Split(':')[0]);
                }
                check(router.ExecuteRequest(new ControlAction(ControlActionType.WebSearch) { Request = new DesktopActionRequest { Site = "bing", Query = "test & words" } }, context, "ai").Success, "router dry-run search, no browser launched");
                foreach (string extension in new[] { ".pdf", ".DOCX", ".mp3", ".html" }) { check(SafeDesktopActions.CanOpenExtension("test" + extension), "allow " + extension); }
                foreach (string extension in new[] { ".exe", ".ps1", ".cmd", ".bat", ".lnk", ".msi", ".js", ".pdf.exe" }) { check(!SafeDesktopActions.CanOpenExtension("test" + extension), "refuse " + extension); }
                foreach (string process in "cmd powershell pwsh WindowsTerminal conhost wt explorer regedit mmc Taskmgr KinectV2MouseControl KINECT-OS".Split(' '))
                { check(!SafeDesktopActions.CanType(process, "hello"), "text refused in " + process); }
                check(SafeDesktopActions.CanType("notepad", "Hello 世界") && !SafeDesktopActions.CanType("notepad", new string('x', 501)), "Unicode text and length boundary");
                check(!router.ExecuteRequest(new ControlAction(ControlActionType.OpenFile) { Request = new DesktopActionRequest { Path = @"C:\not-found.pdf" } }, context, "ai").Success, "file capability required in same request");
                DesktopWindows.Rect left = new DesktopWindows.Rect(-1920, -200, 0, 840);
                DesktopWindows.Rect quadrant = DesktopWindows.Region(left, "top-right");
                check(quadrant.Left == -960 && quadrant.Top == -200 && quadrant.Right == 0 && quadrant.Bottom == 320, "negative-origin quadrant and work area");
                DesktopWindows.Rect primary = new DesktopWindows.Rect(0, 0, 1920, 1040);
                DesktopWindows.Rect half = DesktopWindows.Region(primary, "right");
                check(half.Left == 960 && half.Right == 1920 && half.Bottom == 1040, "single monitor half, taskbar excluded");
                foreach (DesktopWindows.Rect work in new[] { left, primary })
                {
                    foreach (string region in "maximize left right top bottom top-left top-right bottom-left bottom-right".Split(' '))
                    {
                        DesktopWindows.Rect result = DesktopWindows.Region(work, region);
                        check(result.Left >= work.Left && result.Right <= work.Right && result.Top >= work.Top && result.Bottom <= work.Bottom
                            && result.Right > result.Left && result.Bottom > result.Top, "two-monitor region " + region);
                    }
                }
                InstalledApps.Entry[] apps = { new InstalledApps.Entry { Name = "Microsoft Edge" }, new InstalledApps.Entry { Name = "Edge Beta" }, new InstalledApps.Entry { Name = "Claude" } };
                check(InstalledApps.Match("edge", apps).Count == 2 && InstalledApps.Match("Claude", apps).Single().Name == "Claude"
                    && InstalledApps.Match("Claud", apps).Single().Name == "Claude", "app ambiguity, exact and fuzzy matching");
                TestFiles(check);
                TestFileRanking(check);
            }
            catch (Exception ex) { failures++; output.AppendLine("FAIL " + ex); }
            output.AppendLine(failures == 0 ? "AI SELF-TEST PASSED" : "AI SELF-TEST FAILED: " + failures);
            report = output.ToString();
            return failures == 0 ? 0 : 4;
        }

        private static void TestFiles(Action<bool, string> check)
        {
            string root = Path.Combine(Path.GetTempPath(), "KinectOS-action-test-" + Guid.NewGuid().ToString("N"));
            string folder = Path.Combine(root, "Documents");
            Directory.CreateDirectory(folder);
            string outside = Path.Combine(root, "resume-outside.txt");
            try
            {
                File.WriteAllText(outside, "test");
                for (int i = 0; i < 10; i++)
                {
                    string file = Path.Combine(folder, "resume-" + i + ".txt");
                    File.WriteAllText(file, "test");
                    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-i));
                }
                bool partial;
                string[] found = SafeDesktopActions.Find("resume", new[] { folder }, CancellationToken.None, out partial);
                check(found.Length == 8 && Path.GetFileName(found[0]) == "resume-0.txt" && !found.Contains(outside) && !partial, "file search: personal root, equal names newest first, maximum eight");
                check(!SafeDesktopActions.IsPersonalFile(outside, new[] { folder })
                    && !SafeDesktopActions.IsBelow(folder + "-escape\\resume.txt", folder), "sibling directory cannot escape root boundary");
            }
            finally
            {
                // Only remove the exact files this test created; never recursively remove an inferred path.
                for (int i = 0; i < 10; i++) { File.Delete(Path.Combine(folder, "resume-" + i + ".txt")); }
                File.Delete(outside); Directory.Delete(folder); Directory.Delete(root);
            }
        }

        private static void TestFileRanking(Action<bool, string> check)
        {
            check(RejectsQuery(@"folder\resume") && RejectsQuery("folder/resume") && RejectsQuery("C:resume")
                && RejectsQuery("") && RejectsQuery(" ") && RejectsQuery(new string('r', 201)),
                "file search still refuses paths, empty queries and long queries");

            string root = Path.Combine(Path.GetTempPath(), "KinectOS-rank-test-" + Guid.NewGuid().ToString("N"));
            string folder = Path.Combine(root, "Documents");
            Directory.CreateDirectory(folder);
            string exact = Path.Combine(folder, "Resume.pdf");
            string sameNameImage = Path.Combine(folder, "resume.png");
            string newerPartial = Path.Combine(folder, "resume-screenshot.png");
            string token = Path.Combine(folder, "My_Resume_2026.pdf");
            string embedded = Path.Combine(folder, "preresume.txt");
            string outside = Path.Combine(root, "Resume.pdf");
            try
            {
                File.WriteAllText(outside, "no");
                File.WriteAllText(exact, "cv");
                File.SetLastWriteTimeUtc(exact, DateTime.UtcNow.AddDays(-30));
                File.WriteAllText(sameNameImage, "img");
                File.SetLastWriteTimeUtc(sameNameImage, DateTime.UtcNow.AddDays(-1));
                File.WriteAllText(newerPartial, "img");
                File.SetLastWriteTimeUtc(newerPartial, DateTime.UtcNow);
                bool partial;
                string[] found = SafeDesktopActions.Find("resume", new[] { folder }, CancellationToken.None, out partial);
                check(!partial && found.Length == 3 && Path.GetFileName(found[0]) == "Resume.pdf"
                    && Path.GetFileName(found[1]) == "resume.png" && !found.Contains(outside),
                    "exact file name beats a newer partial match, and a document beats a newer same-name image");

                File.Delete(exact);
                File.Delete(sameNameImage);
                File.Delete(newerPartial);
                File.WriteAllText(token, "cv");
                File.SetLastWriteTimeUtc(token, DateTime.UtcNow.AddDays(-10));
                File.WriteAllText(embedded, "no");
                File.SetLastWriteTimeUtc(embedded, DateTime.UtcNow);
                found = SafeDesktopActions.Find("resume", new[] { folder }, CancellationToken.None, out partial);
                check(!partial && found.Length == 2 && Path.GetFileName(found[0]) == "My_Resume_2026.pdf",
                    "whole-token match beats a newer mid-word substring");
                found = SafeDesktopActions.Find("my resume", new[] { folder }, CancellationToken.None, out partial);
                check(found.Length == 1 && Path.GetFileName(found[0]) == "My_Resume_2026.pdf",
                    "separator-insensitive match for a multi-word name");
            }
            finally
            {
                File.Delete(exact);
                File.Delete(sameNameImage);
                File.Delete(newerPartial);
                File.Delete(token);
                File.Delete(embedded);
                File.Delete(outside);
                Directory.Delete(folder);
                Directory.Delete(root);
            }
        }

        private static bool RejectsQuery(string query)
        {
            try
            {
                bool partial;
                SafeDesktopActions.Find(query, new string[0], CancellationToken.None, out partial);
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }
    }
}
