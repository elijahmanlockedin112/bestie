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
            t.Add(Tool("play_music", "play a song, artist, album or playlist in their spotify app (it starts playing right away, and checks it's the right one).", R("kind"),
                PE("kind", "what it is", new[] { "track", "artist", "album", "playlist" }),
                P("title", "string", "the song, album or playlist name exactly as people write it, e.g. 'Bennie and the Jets' (empty for an artist)"),
                P("artist", "string", "who it's by, e.g. 'Elton John' (empty if they didn't say and you don't know)")));
            t.Add(Tool("media_control", "control whatever is playing (spotify, youtube, anything): pause/resume, next, previous, volume, mute. instant: use this, "
                + "not use_computer, for those.", R("action"),
                PE("action", "what to do", new[] { "play_pause", "next", "previous", "volume_up", "volume_down", "mute" }),
                P("times", "number", "for volume: how many steps (each is about 2%). 'a bit' = 3, 'a lot' = 10")));
            t.Add(Tool("check_claude", "see what their coding agents are up to: their agent office (vibe code central: who's working, who's waiting on them, how "
                + "locked in they've been today, and the jobs you handed over) plus any claude code session: still working or done, and the last thing it said.", R(),
                P("project", "string", "the project, job or agent name, if they said one; empty for the whole picture")));
            t.Add(Tool("delegate_to_claude", "hand a job to claude (their claude subscription, not your openai key): anything to do with code (build an app, add a feature, "
                + "fix a bug, scripts) and anything token-heavy (research, long writing, going through documents or lots of files, big plans). coding jobs go to a "
                + "claude agent in their agent office (vibe code central), which works on its own; other jobs open a claude window that asks them before running "
                + "commands. you'll hear when it's done.", R("task"),
                P("task", "string", "the full job, in detail, as they described it"),
                PE("kind", "code: software in a project folder. other: research, writing or analysis (works in their Downloads folder)", new[] { "code", "other" }),
                P("project", "string", "for code: an existing project's folder name if it's about one of theirs, else a short kebab-case name for a new one")));
            t.Add(Tool("start_project", "start one of their apps or servers (synaptic, spark lab, ascent, ffa quiz drill, ai playground lab, ...) and open it in the browser.", R("name"),
                P("name", "string", "the project, as they said it")));
            t.Add(Tool("use_computer", "operate their computer yourself (mouse, keyboard, screen) for anything the other tools don't cover: using apps, clicking through "
                + "websites, settings in apps, filling in things, and finishing what another tool couldn't. it takes over the mouse and keyboard until done; they can "
                + "press esc to stop it. small jobs start right away; bigger ones ask them first.", R("task", "small"),
                P("task", "string", "exactly what to get done, with every detail they gave"),
                P("small", "boolean", "true for a quick, harmless job in an app that's already open or easy to open (pick a song, press a button, scroll, open a menu, "
                    + "switch tabs). false for anything longer, or that sends, buys, deletes, signs in, changes settings or touches their files")));
            AddFileTools(t);
            AddBehaviorTools(t);
            AddSystemTools(t);
            AddPowerTools(t);
        }

        AssistantToolResult AgentTool(string name, Dictionary<string, object> a, AssistantSettings s)
        {
            switch (name)
            {
                case "play_music": return PlayMusic(Json.Str(a, "kind") ?? "track", Json.Str(a, "title") ?? Json.Str(a, "query") ?? "", Json.Str(a, "artist") ?? "");
                case "delegate_to_claude": return DelegateToClaude(Json.Str(a, "task") ?? "", Json.Str(a, "project") ?? "", Json.Str(a, "kind") != "other");
                case "start_project": return StartProject(Json.Str(a, "name") ?? "");
                case "media_control": return MediaControl(Json.Str(a, "action") ?? "", (int)Json.Num(a, "times", 1));
                case "check_claude": return CheckClaude(Json.Str(a, "project") ?? "");
                case "use_computer": return UseComputer(Json.Str(a, "task") ?? "", Json.Bool(a, "small", false), s);
            }
            return FileTool(name, a) ?? BehaviorTool(name, a) ?? SystemTool(name, a) ?? PowerTool(name, a);
        }

        // ================================================================== spotify

        AssistantToolResult PlayMusic(string kind, string title, string artist)
        {
            title = title.Trim(); artist = artist.Trim();
            if (title.Length == 0 && artist.Length == 0) return new AssistantToolResult("play what?");
            string what = title.Length > 0 ? title + (artist.Length > 0 ? " by " + artist : "") : artist;
            if (DryRun) { lock (DryRunLog) DryRunLog.Add("spotify " + kind + ": " + what); return new AssistantToolResult("playing " + what + "."); }
            string result = SpotifyPlayer.Play(kind, title, artist);
            return new AssistantToolResult(result, "spotify: " + what, null);
        }

        AssistantToolResult MediaControl(string action, int times)
        {
            ushort vk;
            switch (action)
            {
                case "play_pause": vk = 0xB3; break;
                case "next": vk = 0xB0; break;
                case "previous": vk = 0xB1; break;
                case "volume_up": vk = 0xAF; break;
                case "volume_down": vk = 0xAE; break;
                case "mute": vk = 0xAD; break;
                default: return new AssistantToolResult("media_control needs play_pause, next, previous, volume_up, volume_down or mute.");
            }
            bool volume = action == "volume_up" || action == "volume_down";
            int n = volume ? Math.Max(1, Math.Min(25, times)) : 1;
            if (DryRun) { lock (DryRunLog) DryRunLog.Add("media " + action + " x" + n); return new AssistantToolResult("done."); }
            string before = SpotifyPlayer.Title();
            for (int i = 0; i < n; i++) { ComputerAgent.Input.Press(vk); if (n > 1) Thread.Sleep(25); }
            if (action == "next" || action == "previous" || action == "play_pause")
            {
                Thread.Sleep(900);
                string now = SpotifyPlayer.Title();
                if (now != null && now != before)
                    return new AssistantToolResult(action == "play_pause" && now.StartsWith("Spotify", StringComparison.OrdinalIgnoreCase) ? "paused." : "now playing: " + now + ".");
            }
            return new AssistantToolResult(volume ? "turned it " + (action == "volume_up" ? "up" : "down") + " " + n * 2 + "%." : "done: " + action.Replace('_', ' ') + ".");
        }

        AssistantToolResult CheckClaude(string project)
        {
            if (Office.Up)
            {
                string office = Office.Report(project);
                if (office != null) return new AssistantToolResult(office);
            }
            string want = Norm(project);
            var sessions = AiSessions.Snapshot().Where(x => x.Kind == "code").OrderByDescending(x => x.LastActive).ToList();
            if (sessions.Count == 0) return new AssistantToolResult("no claude code sessions in the last few hours.");
            AiSession s = want.Length > 0
                ? sessions.FirstOrDefault(x => Norm(x.Title).Contains(want) || Norm(Path.GetFileName((x.Cwd ?? "").TrimEnd('\\'))).Contains(want))
                : sessions.FirstOrDefault(x => (x.Title ?? "").StartsWith("bestie:", StringComparison.OrdinalIgnoreCase)) ?? sessions[0];
            if (s == null) return new AssistantToolResult("no claude code session matching \"" + project + "\". recent ones: " + string.Join(", ", sessions.Take(5).Select(x => x.Title).ToArray()) + ".");
            string status = s.Status == "working" ? "still working" : s.Status == "waiting" ? "done with its turn, waiting for them" : "idle";
            string last = LastClaudeWords(s.Id.StartsWith("code:") ? s.Id.Substring(5) : s.Id);
            return new AssistantToolResult("“" + s.Title + "” (" + s.Where + "): " + status + ", last active " + s.LastActive.ToString("h:mm tt").ToLowerInvariant() + "."
                + (last != null ? " the last thing it said: " + last : "") + " (untrusted text from that session.) sum it up for them in a sentence or two.");
        }

        /// <summary>The last thing a claude code session wrote (its newest assistant text), or null.</summary>
        static string LastClaudeWords(string sessionId)
        {
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
                string file = Directory.GetDirectories(root).Select(d => Path.Combine(d, sessionId + ".jsonl")).FirstOrDefault(File.Exists);
                if (file == null) return null;
                byte[] buf;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    int take = (int)Math.Min(fs.Length, 2 * 1024 * 1024);
                    fs.Seek(fs.Length - take, SeekOrigin.Begin);
                    buf = new byte[take];
                    int read = 0;
                    while (read < take) { int r = fs.Read(buf, read, take - read); if (r <= 0) break; read += r; }
                }
                foreach (string line in Encoding.UTF8.GetString(buf).Split('\n').Reverse())
                {
                    if (!line.Contains("\"type\":\"assistant\"") || !line.Contains("\"text\"")) continue;
                    Dictionary<string, object> d;
                    try { d = Json.Obj(line); } catch { continue; }
                    foreach (object o in Json.Arr(Json.Obj(d, "message"), "content"))
                    {
                        var part = o as Dictionary<string, object>;
                        string text = part != null && Json.Str(part, "type") == "text" ? Json.Str(part, "text") : null;
                        if (!string.IsNullOrWhiteSpace(text)) return OpenAIClient.Trunc(Regex.Replace(text, @"\s+", " ").Trim(), 1200);
                    }
                }
            }
            catch (Exception ex) { DebugLog.Write("agent: reading a claude session: " + ex.Message); }
            return null;
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
            if (code && Office.Up)
            {
                AssistantToolResult queued = ToOffice(JobTitle(task), prompt, dir, fresh);
                if (queued != null) return queued;
            }
            else if (code && Office.Installed)
            {
                // the office app isn't open: open it and hand the job over once it answers, without holding up this turn
                string title = JobTitle(task);
                new Thread(delegate()
                {
                    try
                    {
                        if (Office.Open(90))
                        {
                            Office.Job job = Office.Queue(title, prompt, dir);
                            if (job != null)
                            {
                                Journal.Add("i handed a coding job to " + job.Worker + " in vibe code central: " + OpenAIClient.Trunc(title, 120));
                                module.Announce(job.Worker + " has it in vibe code central.");
                                return;
                            }
                        }
                    }
                    catch (Exception ex) { DebugLog.Write("agent: office handoff: " + ex.Message); }
                    ClaudeWindow(exe, dir, label, prompt, task);
                    module.Announce("vibe code central didn't take it, so claude's working on it in its own window instead.");
                }) { IsBackground = true, Name = "bestie-office-handoff" }.Start();
                return new AssistantToolResult("opening vibe code central (their agent office); the job goes to one of its claude agents as soon as it's up, and you'll "
                    + "say who has it. tell them that in one short sentence.", "to vibe code central: " + label, null);
            }
            ClaudeWindow(exe, dir, label, prompt, task);
            return new AssistantToolResult("started a claude code session in " + dir + (fresh ? " (new project)" : "") + ". it's working in its own window and will ask them "
                + "there before running commands; the ai sessions panel shows it, and you'll announce when it finishes. tell them that in one short sentence.",
                "handed to claude: " + label, null);
        }

        /// <summary>A claude code session in its own terminal window, in normal permission mode: it asks them there
        /// before running commands or changing things.</summary>
        static void ClaudeWindow(string exe, string dir, string label, string prompt, string task)
        {
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = "-n " + Quote("bestie: " + label) + " " + Quote(prompt),
                WorkingDirectory = dir,
                UseShellExecute = true,   // its own terminal window, which they can watch and talk to
            };
            Process.Start(psi);
            DebugLog.Write("agent: handed a job to claude code in " + dir);
            Journal.Add("i handed a job to claude: " + OpenAIClient.Trunc(task, 120));
        }

        /// <summary>Queues a coding job with a claude agent in vibe code central. Null if the office couldn't take it.</summary>
        static AssistantToolResult ToOffice(string title, string prompt, string dir, bool fresh)
        {
            Office.Job job;
            try { job = Office.Queue(title, prompt, dir); }
            catch (Exception ex) { DebugLog.Write("agent: office handoff: " + ex.Message); return null; }
            if (job == null) { DebugLog.Write("agent: the office has no claude agent"); return null; }
            DebugLog.Write("agent: queued office job " + job.Id + " for " + job.Worker + " in " + dir);
            Journal.Add("i handed a coding job to " + job.Worker + " in vibe code central: " + OpenAIClient.Trunc(title, 120));
            return new AssistantToolResult("queued in vibe code central, their agent office: " + job.Worker + " (a claude agent) has it, in " + job.Project
                + (fresh ? " (a new project folder, " + dir + ")" : "")
                + (job.Ahead > 0 ? ", after " + job.Ahead + " job" + (job.Ahead == 1 ? "" : "s") + " already on its desk" : "")
                + ". it works on its own there (edits files and runs checks, never commits or pushes), they can watch it live in the office, and you'll announce "
                + "when it's done. tell them who has it in one short sentence.", "to " + job.Worker + ": " + title, null);
        }

        /// <summary>A short title for a job: its first sentence, at most about 70 characters.</summary>
        static string JobTitle(string task)
        {
            string t = Regex.Replace(task, @"\s+", " ").Trim();
            Match m = Regex.Match(t, @"^(.{12,}?[.!?])(\s|$)");
            if (m.Success) t = m.Groups[1].Value.TrimEnd('.');
            if (t.Length > 70)
            {
                t = t.Substring(0, 70);
                int sp = t.LastIndexOf(' ');
                if (sp > 40) t = t.Substring(0, sp);
                t = t.TrimEnd(',', ';', ':', ' ') + "…";
            }
            return t.Length == 0 ? "Job from Bestie" : char.ToUpperInvariant(t[0]) + t.Substring(1);
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

        internal static bool PortOpen(int port)
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

        AssistantToolResult UseComputer(string task, bool small, AssistantSettings s)
        {
            task = task.Trim();
            if (task.Length < 4) return new AssistantToolResult("do what on the computer?");
            if (DryRun) { lock (DryRunLog) DryRunLog.Add("computer " + (small ? "(small) " : "") + task); return new AssistantToolResult("done."); }
            // small, harmless jobs just go (with a tight step budget, esc, and the banner). anything bigger: they say yes first.
            if (!small)
            {
                int ok = module.AskUser("ok to take over your mouse and keyboard for: " + OpenAIClient.Trunc(task, 140) + "? (esc stops me)", AssistantCalendar.ConfirmTimeoutMs);
                if (ok != 1)
                {
                    Journal.Add("i offered to use your computer for \"" + OpenAIClient.Trunc(task, 80) + "\". " + (ok == 0 ? "you said no." : "you didn't answer."));
                    return new AssistantToolResult(ok == 0 ? "they said no, so you didn't touch the computer." : "they didn't answer, so you didn't touch the computer.", "didn't use the computer", null);
                }
            }
            if (Interlocked.Exchange(ref driving, 1) == 1) return new AssistantToolResult("already using the computer for something else.");
            DrivingBanner banner = null;
            EscWatcher esc = null;
            var agent = new ComputerAgent(s, q => module.AskUser(q, AssistantCalendar.ConfirmTimeoutMs), null);
            agent.Small = small;
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
        /// <summary>Tests: rank the play buttons and say which it would press, without pressing anything.</summary>
#pragma warning disable 649   // only tests set it
        internal static bool PreviewOnly;
#pragma warning restore 649
        static readonly Regex Knockoff = new Regex(@"karaoke|in the style of|instrumental|tribute|\bcover\b|remix|sped up|slowed|8d audio|lullaby|made famous|originally performed|piano version|backing track", RegexOptions.Compiled);

        static System.Diagnostics.Process Window()
        {
            return System.Diagnostics.Process.GetProcessesByName("Spotify").FirstOrDefault(x => x.MainWindowHandle != IntPtr.Zero);
        }

        internal static string Title()
        {
            var p = Window();
            if (p == null) return null;
            p.Refresh();
            return p.MainWindowTitle;
        }

        static bool IsIdleTitle(string t) { return t == null || t.Length == 0 || t.StartsWith("Spotify", StringComparison.OrdinalIgnoreCase); }

        static string[] Words(string s)
        {
            return Regex.Split((s ?? "").ToLowerInvariant(), @"[^a-z0-9']+").Where(w => w.Length > 1 && !Filler.Contains(w)).ToArray();
        }

        /// <summary>kind: track, artist, album or playlist. Presses the best matching play button on the search page,
        /// checks the window title shows the right thing playing, and tries the next candidate if it doesn't.</summary>
        public static string Play(string kind, string title, string artist)
        {
            bool artistOnly = kind == "artist" || title.Length == 0;
            string search = artistOnly ? artist : (title + " " + artist).Trim();
            string[] titleWords = Words(artistOnly ? artist : title);
            string[] artistWords = Words(artist);
            if (titleWords.Length == 0) titleWords = new[] { search.ToLowerInvariant() };
            bool wantsKnockoff = Knockoff.IsMatch((title + " " + artist).ToLowerInvariant());
            string exact = Regex.Replace(Regex.Replace((artistOnly ? artist : title).ToLowerInvariant(), @"[^a-z0-9' ]", " "), @"\s+", " ").Trim();
            System.Diagnostics.Process.Start(new ProcessStartInfo("spotify:search:" + Uri.EscapeDataString(search)) { UseShellExecute = true });
            var sw = Stopwatch.StartNew();
            var tried = new List<string>();
            while (sw.Elapsed.TotalSeconds < 14)
            {
                Thread.Sleep(450);
                var p = Window();
                if (p == null) continue;
                try
                {
                    // candidates: every "Play …" button naming the thing (all of its words), best first, page order breaking ties
                    var root = AutomationElement.FromHandle(p.MainWindowHandle);
                    var cands = new List<KeyValuePair<AutomationElement, double>>();
                    int order = 0;
                    foreach (AutomationElement b in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                    {
                        order++;
                        string n = (b.Current.Name ?? "").ToLowerInvariant();
                        if (!n.StartsWith("play ")) continue;
                        string label = n.Substring(5);
                        if (Knockoff.IsMatch(label) && !wantsKnockoff) continue;
                        if (!titleWords.All(w => label.Contains(w))) continue;
                        double score = 10;
                        string bare = Regex.Replace(Regex.Replace(label, @"[^a-z0-9' ]", " "), @"\s+", " ").Trim();
                        if (bare == exact) score += 6;                                             // exactly the name (the artist itself, not "djo radio")
                        if (bare.StartsWith(string.Join(" ", titleWords))) score += 3;            // starts with the name itself
                        score += artistWords.Count(w => label.Contains(w));                        // names the artist too
                        score -= Math.Max(0, Words(label).Length - titleWords.Length - artistWords.Length) * 0.5;   // "(live)", "- remastered 2014"...
                        score -= order * 0.0001;                                                   // the top result first, when equal
                        cands.Add(new KeyValuePair<AutomationElement, double>(b, score));
                    }
                    if (cands.Count == 0) continue;
                    // the page fills in over a second or two; give the exact match a moment to show up
                    if (cands.Max(x => x.Value) < 15 && sw.Elapsed.TotalSeconds < 3) continue;
                    if (PreviewOnly) return "would press, in order: " + string.Join(" | ", cands.OrderByDescending(x => x.Value).Take(3).Select(x => x.Key.Current.Name.Substring(5)).ToArray());
                    foreach (var c in cands.OrderByDescending(x => x.Value).Take(3))
                    {
                        string label = c.Key.Current.Name.Substring(5);
                        object ip;
                        if (!c.Key.TryGetCurrentPattern(InvokePattern.Pattern, out ip)) continue;
                        string before = Title();
                        ((InvokePattern)ip).Invoke();
                        tried.Add(label);
                        // playing shows up in the window title as "artist - song": is it the right one?
                        string now = null;
                        for (int i = 0; i < 16; i++)
                        {
                            Thread.Sleep(250);
                            now = Title();
                            if (!IsIdleTitle(now) && (now != before || i > 6)) break;
                        }
                        string playing = (now ?? "").ToLowerInvariant();
                        bool right = artistOnly ? artistWords.All(w => playing.Contains(w)) || titleWords.All(w => playing.Contains(w))
                                   : kind == "track" ? titleWords.All(w => playing.Contains(w))
                                   : !IsIdleTitle(now);   // albums and playlists: their first song is what shows
                        if (right) { DebugLog.Write("spotify: playing " + now); return "playing in spotify: " + now + ". (checked: it's the right one.)"; }
                        DebugLog.Write("spotify: pressed \"" + label + "\" but " + (IsIdleTitle(now) ? "nothing started" : now + " is playing") + "; trying the next");
                    }
                    break;
                }
                catch (ElementNotAvailableException) { }
                catch (Exception ex) { DebugLog.Write("spotify: " + ex.Message); }
            }
            string what = artistOnly ? artist : title + (artist.Length > 0 ? " by " + artist : "");
            return "not playing yet: " + (tried.Count == 0 ? "spotify's search page has no play button for " + what
                : "tried " + string.Join(", ", tried.ToArray()) + " but the right song didn't start")
                + ". the search is open in spotify. call use_computer now (small=true) to play " + what + " from there, and check it's the right one.";
        }
    }
}
