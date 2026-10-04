// AgentTools.cs - the things Bestie does instead of describing them: play music in the Spotify app, hand coding
// work to a Claude Code session, start the user's projects, and (for everything else) drive the computer itself.
// C# 5 only. Tool calls run on the assistant's worker thread.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace Bestie
{
    internal sealed partial class AssistantTools
    {
        static readonly string ProjectsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Coding Projects");
        static string HubDir { get { return Path.Combine(ProjectsDir, "learning-hub"); } }
        const int HubPort = 4100;

        void AddAgentTools(List<object> t)
        {
            t.Add(Tool("play_music", "play a song, artist, album or playlist in their spotify app (it starts playing right away).", R("query"),
                P("query", "string", "what to play, e.g. 'end of beginning djo', 'taylor swift', 'liked songs'")));
            t.Add(Tool("delegate_to_claude", "hand a job to claude (their claude subscription, not your openai key): anything to do with code (build an app, add a feature, "
                + "fix a bug, scripts) and anything token-heavy (research, long writing, going through documents or lots of files, big plans). it works in its own "
                + "window and asks them there before running commands; you'll hear when it's done.", R("task"),
                P("task", "string", "the full job, in detail, as they described it"),
                PE("kind", "code: software in a project folder. other: research, writing or analysis (works in their Downloads folder)", new[] { "code", "other" }),
                P("project", "string", "for code: an existing project's folder name if it's about one of theirs, else a short kebab-case name for a new one")));
            t.Add(Tool("start_project", "start one of their apps or servers (synaptic, spark lab, ascent, ffa quiz drill, ai playground lab, ...) and open it in the browser.", R("name"),
                P("name", "string", "the project, as they said it")));
            t.Add(Tool("use_computer", "operate their computer yourself (mouse, keyboard, screen) for anything the other tools don't cover: using apps, clicking through "
                + "websites, settings in apps, filling in things. it asks them first, then takes over the mouse and keyboard until done; they can press esc to stop it.", R("task"),
                P("task", "string", "exactly what to get done, with every detail they gave")));
        }

        AssistantToolResult AgentTool(string name, Dictionary<string, object> a, AssistantSettings s)
        {
            switch (name)
            {
                case "play_music": return PlayMusic(Json.Str(a, "query") ?? "");
                case "delegate_to_claude": return DelegateToClaude(Json.Str(a, "task") ?? "", Json.Str(a, "project") ?? "", Json.Str(a, "kind") != "other");
                case "start_project": return StartProject(Json.Str(a, "name") ?? "");
                case "use_computer": return UseComputer(Json.Str(a, "task") ?? "", s);
            }
            return null;
        }

        // ================================================================== spotify

        AssistantToolResult PlayMusic(string query)
        {
            query = query.Trim();
            if (query.Length == 0) return new AssistantToolResult("play what?");
            if (DryRun) { lock (DryRunLog) DryRunLog.Add("spotify " + query); return new AssistantToolResult("playing " + query + "."); }
            string result = SpotifyPlayer.Play(query);
            return new AssistantToolResult(result, "spotify: " + query, null);
        }

        // ================================================================== claude code

        AssistantToolResult DelegateToClaude(string task, string project, bool code)
        {
            task = task.Trim();
            if (task.Length < 8) return new AssistantToolResult("what's the job? i need it in a sentence or two.");
            string exe = FindClaude();
            if (exe == null) return new AssistantToolResult("claude code isn't installed here (no claude.exe), so i can't hand it off.");
            string dir = code ? ExistingProject(project) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            bool fresh = dir == null;
            if (fresh)
            {
                string slug = Slug(project.Length > 0 ? project : task);
                dir = Path.Combine(ProjectsDir, slug);
                for (int i = 2; Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(); i++) dir = Path.Combine(ProjectsDir, slug + "-" + i);
                Directory.CreateDirectory(dir);
            }
            string prompt = task + "\n\n(Handed to you by voice through Bestie, the user's desktop assistant. "
                + (!code ? "This isn't a coding job: put anything you make in their Downloads folder and give the path."
                   : fresh ? "This is a new, empty project folder: build it here." : "This is their existing project.")
                + (code ? " When you finish, say what you did and exactly how to run it.)" : " When you finish, sum it up.)");
            string label = code ? Path.GetFileName(dir) : Slug(task);
            if (DryRun) { lock (DryRunLog) DryRunLog.Add("claude " + dir + " :: " + prompt); return new AssistantToolResult("started."); }
            // normal permission mode: it asks them in its own window before running commands or changing things
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = "-n " + Quote("bestie: " + label) + " " + Quote(prompt),
                WorkingDirectory = dir,
                UseShellExecute = true,   // its own terminal window, which they can watch and talk to
            };
            Process.Start(psi);
            DebugLog.Write("agent: handed a job to claude code in " + dir);
            Journal.Add("i handed a job to claude: " + OpenAIClient.Trunc(task, 120));
            return new AssistantToolResult("started a claude code session in " + dir + (fresh ? " (new project)" : "") + ". it's working in its own window and will ask them "
                + "there before running commands; the ai sessions panel shows it, and you'll announce when it finishes. tell them that in one short sentence.",
                "handed to claude: " + label, null);
        }

        static string FindClaude()
        {
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
            if (File.Exists(local)) return local;
            foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try { string p = Path.Combine(d.Trim(), "claude.exe"); if (File.Exists(p)) return p; } catch { }
            }
            return null;
        }

        /// <summary>A Windows command-line argument, quoted so the program gets exactly this text.</summary>
        static string Quote(string arg)
        {
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in arg.Replace("\r", ""))
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { sb.Append('\\', slashes * 2 + 1); slashes = 0; sb.Append('"'); continue; }
                sb.Append('\\', slashes); slashes = 0;
                sb.Append(c == '\n' ? ' ' : c);
            }
            sb.Append('\\', slashes * 2).Append('"');
            return sb.ToString();
        }

        static string Slug(string s)
        {
            string slug = Regex.Replace((s ?? "").ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
            string[] parts = slug.Split('-').Where(p => p.Length > 0 && !new[] { "a", "an", "the", "me", "my", "up", "code", "make", "build", "for", "and", "to", "of", "that" }.Contains(p)).Take(4).ToArray();
            slug = string.Join("-", parts);
            return slug.Length == 0 ? "new-project" : slug;
        }

        /// <summary>A project folder they already have (Coding Projects, or an app in the learning hub), or null.</summary>
        static string ExistingProject(string name)
        {
            string want = Norm(name);
            if (want.Length < 3) return null;
            var dirs = new List<string>();
            try { dirs.AddRange(Directory.GetDirectories(ProjectsDir)); } catch { }
            try { dirs.AddRange(Directory.GetDirectories(Path.Combine(HubDir, "apps"))); } catch { }
            string hit = dirs.FirstOrDefault(d => Norm(Path.GetFileName(d)) == want)
                      ?? dirs.FirstOrDefault(d => Norm(Path.GetFileName(d)).Contains(want) || want.Contains(Norm(Path.GetFileName(d))) && Norm(Path.GetFileName(d)).Length >= 4);
            // the old copy of an app that moved into the learning hub: the hub's one is the live one
            if (hit != null && !hit.StartsWith(HubDir, StringComparison.OrdinalIgnoreCase))
            {
                string hubCopy = Path.Combine(HubDir, "apps", Path.GetFileName(hit).ToLowerInvariant());
                if (Directory.Exists(hubCopy)) hit = hubCopy;
            }
            return hit;
        }

        static string Norm(string s) { return new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()); }

        // ================================================================== starting their projects

        AssistantToolResult StartProject(string name)
        {
            string want = Norm(name);
            if (want.Length < 2) return new AssistantToolResult("start which project?");
            // 1) the learning hub knows how to run its apps
            List<Dictionary<string, object>> hub = HubProjects();
            Dictionary<string, object> p = hub.FirstOrDefault(x => Norm(Json.Str(x, "id")) == want || Norm(Json.Str(x, "name")) == want)
                ?? hub.FirstOrDefault(x => Norm(Json.Str(x, "name")).Contains(want) || Norm(Json.Str(x, "id")).Contains(want) || want.Contains(Norm(Json.Str(x, "id"))));
            if (p != null) return StartHubProject(p);
            // 2) a project with its own launcher (start-*.cmd / .bat)
            string dir = ExistingProject(name);
            if (dir != null)
            {
                string launcher = Directory.GetFiles(dir, "start*.cmd").Concat(Directory.GetFiles(dir, "start*.bat"))
                    .Where(f => !f.EndsWith(".example.cmd", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Length).FirstOrDefault();
                if (launcher != null)
                {
                    if (!DryRun) Process.Start(new ProcessStartInfo(launcher) { WorkingDirectory = dir, UseShellExecute = true });
                    return new AssistantToolResult("ran " + Path.GetFileName(launcher) + " in " + dir + " (its window shows where it's running).", "started " + Path.GetFileName(dir), null);
                }
                return new AssistantToolResult("found " + dir + " but it has no launcher (start-*.cmd). offer to have claude add one (delegate_to_claude), or use_computer.");
            }
            return new AssistantToolResult("i don't know a project called \"" + name + "\". hub apps: " + string.Join(", ", hub.Select(x => Json.Str(x, "name")).ToArray()) + ".");
        }

        static List<Dictionary<string, object>> HubProjects()
        {
            try
            {
                object parsed = Json.Parse(File.ReadAllText(Path.Combine(HubDir, "launcher", "projects.json"), Encoding.UTF8));
                return ((System.Collections.IEnumerable)parsed).Cast<object>().OfType<Dictionary<string, object>>().ToList();
            }
            catch { return new List<Dictionary<string, object>>(); }
        }

        AssistantToolResult StartHubProject(Dictionary<string, object> p)
        {
            string id = Json.Str(p, "id"), name = Json.Str(p, "name") ?? id, url = Json.Str(p, "url"), kind = Json.Str(p, "kind");
            int port = (int)Json.Num(p, "port", 0), boot = (int)Json.Num(p, "bootSeconds", 60);
            if (DryRun) { lock (DryRunLog) DryRunLog.Add("start " + id); return new AssistantToolResult("starting " + name + "."); }
            if (kind == "file")
            {
                string file = Path.Combine(HubDir, (Json.Str(p, "dir") ?? "").Replace('/', '\\'), (Json.Str(p, "file") ?? "").Replace('/', '\\'));
                if (!File.Exists(file)) return new AssistantToolResult(name + " is a page, but " + file + " isn't there (it may need building first).");
                LaunchUrl(file);
                return new AssistantToolResult("opened " + name + ".", "opened " + name, null);
            }
            if (port > 0 && PortOpen(port))
            {
                if (url != null) LaunchUrl(url);
                return new AssistantToolResult(name + " was already running; opened " + url + ".", "opened " + name, null);
            }
            if (PortOpen(HubPort))
            {
                // the hub is up: ask it to start this one (it waits until the app answers), then open it
                new Thread(delegate()
                {
                    try
                    {
                        var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + HubPort + "/api/projects/" + id + "/start");
                        req.Method = "POST"; req.ContentLength = 0; req.Timeout = (boot + 30) * 1000; req.ReadWriteTimeout = req.Timeout;
                        using (req.GetResponse()) { }
                    }
                    catch (Exception ex) { DebugLog.Write("agent: hub start " + id + ": " + ex.Message); }
                    AnnounceWhenUp(name, port, url, boot);
                }) { IsBackground = true, Name = "bestie-start-" + id }.Start();
                return new AssistantToolResult("starting " + name + " through the learning hub (up to " + boot + " s). you'll open it and say so when it's up. tell them it's starting.", "starting " + name, null);
            }
            // nothing running: the hub's own launcher starts it and opens the browser when it's ready
            string cmd = Path.Combine(HubDir, "Start Learning Hub.cmd");
            if (!File.Exists(cmd)) return new AssistantToolResult("the learning hub launcher isn't at " + cmd + ".");
            Process.Start(new ProcessStartInfo(cmd, id) { WorkingDirectory = HubDir, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized });
            new Thread(delegate() { AnnounceWhenUp(name, port, null, boot); }) { IsBackground = true, Name = "bestie-start-" + id }.Start();
            return new AssistantToolResult("starting " + name + " with the learning hub (its window is minimized; closing it stops the app). it opens in the browser when ready, up to "
                + boot + " s. tell them it's starting.", "starting " + name, null);
        }

        void AnnounceWhenUp(string name, int port, string openUrl, int bootSeconds)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < bootSeconds + 30)
            {
                if (PortOpen(port))
                {
                    if (openUrl != null) LaunchUrl(openUrl);
                    module.Announce(name + " is up. took " + (int)sw.Elapsed.TotalSeconds + " seconds.");
                    return;
                }
                Thread.Sleep(1000);
            }
            module.Announce(name + " didn't come up. check the learning hub window.");
        }

        static bool PortOpen(int port)
        {
            foreach (string host in new[] { "127.0.0.1", "::1" })
            {
                try
                {
                    using (var c = new TcpClient(host == "::1" ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork))
                    {
                        var ar = c.BeginConnect(host, port, null, null);
                        if (ar.AsyncWaitHandle.WaitOne(300) && c.Connected) return true;
                    }
                }
                catch { }
            }
            return false;
        }

        // ================================================================== driving the computer

        static int driving;

        AssistantToolResult UseComputer(string task, AssistantSettings s)
        {
            task = task.Trim();
            if (task.Length < 4) return new AssistantToolResult("do what on the computer?");
            if (DryRun) { lock (DryRunLog) DryRunLog.Add("computer " + task); return new AssistantToolResult("done."); }
            // they say yes before it touches the mouse or keyboard, every time
            int ok = module.AskUser("ok to take over your mouse and keyboard for: " + OpenAIClient.Trunc(task, 140) + "? (esc stops me)", AssistantCalendar.ConfirmTimeoutMs);
            if (ok != 1)
            {
                Journal.Add("i offered to use your computer for \"" + OpenAIClient.Trunc(task, 80) + "\". " + (ok == 0 ? "you said no." : "you didn't answer."));
                return new AssistantToolResult(ok == 0 ? "they said no, so you didn't touch the computer." : "they didn't answer, so you didn't touch the computer.", "didn't use the computer", null);
            }
            if (Interlocked.Exchange(ref driving, 1) == 1) return new AssistantToolResult("already using the computer for something else.");
            DrivingBanner banner = null;
            EscWatcher esc = null;
            var agent = new ComputerAgent(s, q => module.AskUser(q, AssistantCalendar.ConfirmTimeoutMs), null);
            try
            {
                agent.Screen = ComputerAgent.PickScreen();
                string who = module.Host.CreatureName ?? "bestie";
                module.OnUi<bool>(delegate
                {
                    banner = new DrivingBanner();
                    banner.ShowOn(agent.Screen, who);
                    esc = new EscWatcher(agent.Stop);
                    module.Host.Driving(true);
                    return true;
                }, false);
                DebugLog.Write("computer: started: " + OpenAIClient.Trunc(task, 140));
                string report = agent.Run(task);
                Journal.Add("i used your computer: " + OpenAIClient.Trunc(task, 100) + " → " + OpenAIClient.Trunc(report, 120));
                return new AssistantToolResult("computer task finished. what happened: " + report + " tell them briefly.", "used the computer: " + OpenAIClient.Trunc(task, 60), null);
            }
            catch (OpenAIException ex)
            {
                DebugLog.Write("computer: " + ex.Message);
                return new AssistantToolResult("couldn't drive the computer: " + ex.Friendly);
            }
            finally
            {
                module.OnUi<bool>(delegate
                {
                    if (banner != null) { banner.Gone(); banner.Dispose(); }
                    if (esc != null) esc.Dispose();
                    module.Host.Driving(false);
                    return true;
                }, false);
                Interlocked.Exchange(ref driving, 0);
            }
        }
    }

    /// <summary>Plays things in the Spotify desktop app: opens its search, then presses the matching play button
    /// through Windows accessibility (no mouse, no screenshots, nothing in the way).</summary>
    internal static class SpotifyPlayer
    {
        static readonly string[] Filler = { "the", "by", "and", "song", "music", "play", "some", "on", "spotify", "a" };
        static readonly Regex Knockoff = new Regex(@"karaoke|in the style of|instrumental|tribute|\bcover\b|remix|sped up|slowed|8d audio|lullaby|made famous|originally performed|piano version|backing track", RegexOptions.Compiled);

        static System.Diagnostics.Process Window()
        {
            return System.Diagnostics.Process.GetProcessesByName("Spotify").FirstOrDefault(x => x.MainWindowHandle != IntPtr.Zero);
        }

        static string Title()
        {
            var p = Window();
            if (p == null) return null;
            p.Refresh();
            return p.MainWindowTitle;
        }

        static bool IsIdleTitle(string t) { return t == null || t.Length == 0 || t.StartsWith("Spotify", StringComparison.OrdinalIgnoreCase); }

        public static string Play(string query)
        {
            string before = Title();
            System.Diagnostics.Process.Start(new ProcessStartInfo("spotify:search:" + Uri.EscapeDataString(query)) { UseShellExecute = true });
            string[] words = Regex.Split(query.ToLowerInvariant(), @"[^a-z0-9']+").Where(w => w.Length > 1 && !Filler.Contains(w)).ToArray();
            if (words.Length == 0) words = new[] { query.ToLowerInvariant() };
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 12)
            {
                Thread.Sleep(450);
                var p = Window();
                if (p == null) continue;
                try
                {
                    var root = AutomationElement.FromHandle(p.MainWindowHandle);
                    AutomationElement best = null;
                    int bestScore = 0;
                    foreach (AutomationElement b in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                    {
                        string n = (b.Current.Name ?? "").ToLowerInvariant();
                        if (!n.StartsWith("play ")) continue;
                        if (Knockoff.IsMatch(n) && !Knockoff.IsMatch(query.ToLowerInvariant())) continue;
                        int score = words.Count(w => n.Contains(w));
                        if (score > bestScore) { best = b; bestScore = score; }   // first (topmost) wins ties: that's the top result
                    }
                    if (best == null || bestScore < Math.Min(2, words.Length)) continue;
                    string label = best.Current.Name.Substring(5);
                    object ip;
                    if (best.TryGetCurrentPattern(InvokePattern.Pattern, out ip)) ((InvokePattern)ip).Invoke();
                    else continue;
                    // it's playing once the window title turns into "artist - song"
                    for (int i = 0; i < 16; i++)
                    {
                        Thread.Sleep(250);
                        string t = Title();
                        if (!IsIdleTitle(t) && t != before) { DebugLog.Write("spotify: playing " + t); return "playing in spotify: " + t + "."; }
                    }
                    return "pressed play on \"" + label + "\" in spotify" + (before != null && !IsIdleTitle(before) ? " (it may already have been playing that)." : ".");
                }
                catch (ElementNotAvailableException) { }
                catch (Exception ex) { DebugLog.Write("spotify: " + ex.Message); }
            }
            return "opened the search in spotify but couldn't find a play button for \"" + query + "\". it's on screen; try use_computer to pick the right one.";
        }
    }
}
