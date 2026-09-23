using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bestie
{
    public class AiSession
    {
        public string Id;          // "code:<id>", "codex:<id>", "tab:<id>", "app:<hwnd>"
        public string Provider;    // "claude" or "chatgpt"
        public string Kind;        // "code" (Claude Code), "codex", "tab" (browser), "app" (desktop app window)
        public string Title;
        public string Where;       // project folder, website, or "desktop app"
        public string Status;      // "working", "waiting" (finished its turn: your move), "idle", "open"
        public DateTime LastActive;
        public int TabId;
        public IntPtr Hwnd;
        public string Url;
        internal string Cwd, Origin;

        public AiSession Copy() { return (AiSession)MemberwiseClone(); }
    }

    /// <summary>Keeps an eye on the user's AI sessions: Claude Code and Codex sessions (from their logs on disk),
    /// claude.ai / chatgpt.com tabs (through the browser bridge) and the desktop apps. Scans every few seconds
    /// on a background thread; never touches the UI.</summary>
    public static class AiSessions
    {
        static readonly object gate = new object();
        static List<AiSession> current = new List<AiSession>();
        static Timer timer;
        static int scanning;
        static bool primed;
        static readonly Dictionary<string, string> lastStatus = new Dictionary<string, string>();
        static readonly Dictionary<int, bool> tabWasBusy = new Dictionary<int, bool>();
        static readonly HashSet<int> tabsDone = new HashSet<int>();
        static readonly Dictionary<string, CodeFile> cache = new Dictionary<string, CodeFile>();

        /// <summary>Raised (on a background thread) when a session changes status. Args: session, previous status.</summary>
        public static event Action<AiSession, string> Changed;
        /// <summary>Raised (on a background thread) after every scan.</summary>
        public static event Action Updated;

        public static void Start()
        {
            if (timer == null) timer = new Timer(_ => Scan(), null, 800, 4000);
        }

        public static void Stop()
        {
            if (timer != null) { timer.Dispose(); timer = null; }
        }

        public static List<AiSession> Snapshot()
        {
            lock (gate) return current.Select(s => s.Copy()).ToList();
        }

        public static int Count(string provider, string status)
        {
            lock (gate) return current.Count(s => s.Provider == provider && s.Status == status);
        }

        // =====================================================================
        // scanning
        // =====================================================================

        static void Scan()
        {
            if (Interlocked.Exchange(ref scanning, 1) == 1) return;
            try
            {
                var list = new List<AiSession>();
                Try(() => list.AddRange(ScanClaudeCode()), "claude code");
                Try(() => list.AddRange(ScanCodex()), "codex");
                Try(() => list.AddRange(ScanTabs()), "tabs");
                Try(() => list.AddRange(ScanApps()), "apps");

                list = list.OrderBy(s => Rank(s.Status)).ThenByDescending(s => s.LastActive).ToList();

                var changes = new List<KeyValuePair<AiSession, string>>();
                lock (gate)
                {
                    foreach (var s in list)
                    {
                        string before;
                        if (lastStatus.TryGetValue(s.Id, out before) && before != s.Status && primed)
                            changes.Add(new KeyValuePair<AiSession, string>(s.Copy(), before));
                        lastStatus[s.Id] = s.Status;
                    }
                    var alive = new HashSet<string>(list.Select(s => s.Id));
                    foreach (var k in lastStatus.Keys.Where(k => !alive.Contains(k)).ToList()) lastStatus.Remove(k);
                    current = list;
                    primed = true;
                }
                if (Changed != null) foreach (var ch in changes) Changed(ch.Key, ch.Value);
                if (Updated != null) Updated();
            }
            catch (Exception ex) { DebugLog.Write("ai sessions: " + ex.Message); }
            finally { Interlocked.Exchange(ref scanning, 0); }
        }

        static readonly HashSet<string> complained = new HashSet<string>();
        static void Try(Action a, string what)
        {
            try { a(); }
            catch (Exception ex) { if (complained.Add(what)) DebugLog.Write("ai sessions (" + what + "): " + ex.Message); }
        }

        static int Rank(string status)
        {
            switch (status) { case "working": return 0; case "waiting": return 1; case "open": return 2; default: return 3; }
        }

        // ---- Claude Code: ~/.claude/projects/<project>/<session>.jsonl ----

        class CodeFile
        {
            public long Length;
            public DateTime Mtime;
            public bool TurnDone, Known;
            public DateTime StatusTime;   // when the last real message happened (utc)
            public string Title, Cwd, Entry, Id;
        }

        static IEnumerable<AiSession> ScanClaudeCode()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            if (!Directory.Exists(root)) yield break;
            DateTime cutoff = DateTime.UtcNow.AddHours(-8);
            var seen = new HashSet<string>();
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                foreach (var path in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    var fi = new FileInfo(path);
                    if (fi.LastWriteTimeUtc < cutoff) continue;
                    seen.Add(path);
                    CodeFile cf;
                    if (!cache.TryGetValue(path, out cf) || cf.Length != fi.Length || cf.Mtime != fi.LastWriteTimeUtc)
                    {
                        cf = ReadCodeFile(path, fi, cf);
                        cache[path] = cf;
                    }
                    if (cf == null) continue;
                    // the app touches these files for bookkeeping, so age comes from the last real message
                    DateTime last = cf.Known && cf.StatusTime != DateTime.MinValue ? cf.StatusTime : fi.LastWriteTimeUtc;
                    double mins = (DateTime.UtcNow - last).TotalMinutes;
                    string status = !cf.Known ? "idle" : cf.TurnDone ? (mins < 45 ? "waiting" : "idle") : (mins < 10 ? "working" : "idle");
                    yield return new AiSession
                    {
                        Id = "code:" + cf.Id, Provider = "claude", Kind = "code",
                        Title = cf.Title ?? "untitled session", Where = WhereFrom(cf.Cwd, cf.Entry),
                        Status = status, LastActive = last.ToLocalTime(), Cwd = cf.Cwd, Origin = cf.Entry
                    };
                }
            }
            foreach (var k in cache.Keys.Where(k => !seen.Contains(k)).ToList()) cache.Remove(k);
        }

        static CodeFile ReadCodeFile(string path, FileInfo fi, CodeFile old)
        {
            var cf = new CodeFile { Length = fi.Length, Mtime = fi.LastWriteTimeUtc, Id = Path.GetFileNameWithoutExtension(path) };
            if (old != null) { cf.Title = old.Title; cf.Cwd = old.Cwd; cf.Entry = old.Entry; }

            string custom = null, agent = null;
            // sessions full of screenshots have single lines of half a megabyte or more, so the last few hundred KB
            // can hold no complete message at all. look further back until one turns up (instead of calling it idle).
            foreach (int tail in new[] { 384 * 1024, 4 * 1024 * 1024, 16 * 1024 * 1024 })
            {
                ScanCodeTail(path, tail, cf, ref custom, ref agent);
                if (cf.Known || fi.Length <= tail) break;
            }
            if (!cf.Known && old != null && old.Known)
            {
                // still nothing readable: nothing we can see changed, so keep what we knew
                cf.Known = true; cf.TurnDone = old.TurnDone; cf.StatusTime = old.StatusTime;
            }
            cf.Title = Clean(custom ?? agent ?? cf.Title);
            if (cf.Title == null || cf.Cwd == null) ReadCodeHead(path, cf);
            return cf;
        }

        static void ScanCodeTail(string path, int bytes, CodeFile cf, ref string custom, ref string agent)
        {
            bool statusFound = false;
            foreach (var line in TailLines(path, bytes).Reverse())
            {
                bool statusLine = line.Contains("\"type\":\"assistant\"") || line.Contains("\"type\":\"user\"") || line.Contains("\"type\":\"system\"");
                bool titleLine = line.StartsWith("{\"type\":\"custom-title\"") || line.StartsWith("{\"type\":\"agent-name\"") || line.Contains("\"type\":\"custom-title\"") || line.Contains("\"type\":\"agent-name\"");
                if (statusFound && (custom != null || !titleLine)) { if (custom != null) break; if (!titleLine) continue; }
                if (!statusLine && !titleLine) continue;
                Dictionary<string, object> d;
                try { d = Json.Obj(line); } catch { continue; }
                if (d == null) continue;
                string type = Json.Str(d, "type");
                if (type == "custom-title" && custom == null) custom = Json.Str(d, "customTitle");
                else if (type == "agent-name" && agent == null) agent = Json.Str(d, "agentName");
                else if (!statusFound && (type == "assistant" || type == "user" || type == "system"))
                {
                    if (cf.Cwd == null) cf.Cwd = Json.Str(d, "cwd");
                    if (cf.Entry == null) cf.Entry = Json.Str(d, "entrypoint");
                    string sub = Json.Str(d, "subtype");
                    if (type == "system" && sub != "stop_hook_summary" && sub != "turn_duration") continue;   // other system notes don't say much
                    statusFound = true;
                    cf.Known = true;
                    DateTime ts;
                    if (DateTime.TryParse(Json.Str(d, "timestamp"), null, System.Globalization.DateTimeStyles.RoundtripKind, out ts)) cf.StatusTime = ts.ToUniversalTime();
                    var msg = Json.Obj(d, "message");
                    if (type == "system") cf.TurnDone = true;
                    else if (type == "assistant") cf.TurnDone = Json.Str(msg, "stop_reason") == "end_turn";
                    else cf.TurnDone = line.Contains("[Request interrupted by user");
                }
            }
        }

        static void ReadCodeHead(string path, CodeFile cf)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                for (int i = 0; i < 40; i++)
                {
                    string line = sr.ReadLine();
                    if (line == null) break;
                    if (!line.Contains("\"type\":\"user\"") || line.Length > 400000) continue;
                    Dictionary<string, object> d;
                    try { d = Json.Obj(line); } catch { continue; }
                    if (d == null || Json.Str(d, "type") != "user") continue;
                    if (cf.Cwd == null) cf.Cwd = Json.Str(d, "cwd");
                    if (cf.Entry == null) cf.Entry = Json.Str(d, "entrypoint");
                    if (cf.Title == null) cf.Title = Clean(PromptText(Json.Obj(d, "message")));
                    if (cf.Title != null) break;
                }
            }
        }

        static string PromptText(Dictionary<string, object> msg)
        {
            if (msg == null) return null;
            object content;
            if (!msg.TryGetValue("content", out content)) return null;
            var s = content as string;
            if (s != null) return s;
            foreach (var part in Json.Arr(msg, "content"))
            {
                var p = part as Dictionary<string, object>;
                if (p != null && Json.Str(p, "type") == "text") return Json.Str(p, "text");
            }
            return null;
        }

        static readonly Regex Tags = new Regex(@"<[^>]{1,80}>", RegexOptions.Compiled);
        static string Clean(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = Tags.Replace(s, " ");
            s = Regex.Replace(s, @"\[external [^\]]{0,60}\]", " ");
            s = Regex.Replace(s, "@\"[^\"]{0,260}\"", m => "@" + System.IO.Path.GetFileName(m.Value.Trim('@', '"')));
            s = Regex.Replace(s, @"\s+", " ").Trim();
            if (s.Length == 0) return null;
            return s.Length > 70 ? s.Substring(0, 68).TrimEnd() + "…" : s;
        }

        static string WhereFrom(string cwd, string entry)
        {
            if (string.IsNullOrEmpty(cwd)) return entry == "claude-desktop" ? "claude app" : "claude code";
            if (cwd.IndexOf(@"\Claude\scratch-workspaces", StringComparison.OrdinalIgnoreCase) >= 0) return "claude app";
            string name = Path.GetFileName(cwd.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(name) ? cwd : name;
        }

        static IEnumerable<string> TailLines(string path, int bytes)
        {
            byte[] buf;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long len = fs.Length;
                int take = (int)Math.Min(len, bytes);
                fs.Seek(len - take, SeekOrigin.Begin);
                buf = new byte[take];
                int read = 0;
                while (read < take) { int n = fs.Read(buf, read, take - read); if (n <= 0) break; read += n; }
                string text = Encoding.UTF8.GetString(buf, 0, read);
                var lines = text.Split('\n');
                int start = len > take ? 1 : 0;   // the first line is probably cut in half
                var result = new List<string>();
                for (int i = start; i < lines.Length; i++) { string l = lines[i].TrimEnd('\r'); if (l.StartsWith("{")) result.Add(l); }
                return result;
            }
        }

        // ---- Codex (ChatGPT's coding agent): ~/.codex/sessions/yyyy/MM/dd/rollout-*.jsonl ----

        static IEnumerable<AiSession> ScanCodex()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
            if (!Directory.Exists(root)) yield break;
            DateTime cutoff = DateTime.UtcNow.AddHours(-8);
            for (int back = 0; back <= 1; back++)
            {
                DateTime day = DateTime.Now.Date.AddDays(-back);
                string dir = Path.Combine(root, day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd"));
                if (!Directory.Exists(dir)) continue;
                foreach (var path in Directory.EnumerateFiles(dir, "*.jsonl"))
                {
                    var fi = new FileInfo(path);
                    if (fi.LastWriteTimeUtc < cutoff) continue;
                    CodeFile cf;
                    if (!cache.TryGetValue(path, out cf) || cf.Length != fi.Length || cf.Mtime != fi.LastWriteTimeUtc)
                    {
                        cf = ReadCodexFile(path, fi, cf);
                        cache[path] = cf;
                    }
                    double mins = (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalMinutes;
                    string status = cf.TurnDone ? (mins < 45 ? "waiting" : "idle") : (mins < (cf.Known ? 10 : 2) ? "working" : "idle");
                    yield return new AiSession
                    {
                        Id = "codex:" + cf.Id, Provider = "chatgpt", Kind = "codex",
                        Title = cf.Title ?? "codex session",
                        Where = cf.Entry != null && cf.Entry.IndexOf("desktop", StringComparison.OrdinalIgnoreCase) >= 0 ? "codex app" : WhereFrom(cf.Cwd, null),
                        Status = status, LastActive = fi.LastWriteTime, Cwd = cf.Cwd, Origin = cf.Entry
                    };
                }
            }
        }

        static CodeFile ReadCodexFile(string path, FileInfo fi, CodeFile old)
        {
            var cf = new CodeFile { Length = fi.Length, Mtime = fi.LastWriteTimeUtc, Id = Path.GetFileNameWithoutExtension(path) };
            if (old != null) { cf.Title = old.Title; cf.Cwd = old.Cwd; cf.Entry = old.Entry; cf.Id = old.Id; }
            if (cf.Cwd == null || cf.Title == null)
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    for (int i = 0; i < 60; i++)
                    {
                        string line = sr.ReadLine();
                        if (line == null) break;
                        if (line.Length > 200000) continue;
                        Dictionary<string, object> d;
                        try { d = Json.Obj(line); } catch { continue; }
                        var p = Json.Obj(d, "payload");
                        string type = Json.Str(d, "type"), ptype = Json.Str(p, "type");
                        if (type == "session_meta")
                        {
                            cf.Cwd = Json.Str(p, "cwd"); cf.Entry = Json.Str(p, "originator");
                            string id = Json.Str(p, "id"); if (id != null) cf.Id = id;
                        }
                        else if (cf.Title == null && type == "event_msg" && ptype == "user_message") cf.Title = Clean(Json.Str(p, "message"));
                        else if (cf.Title == null && ptype == "transcript_segment" && Json.Str(p, "role") == "user") cf.Title = Clean(Json.Str(p, "text"));
                        if (cf.Cwd != null && cf.Title != null) break;
                    }
                }
                if (cf.Title == null && cf.Cwd != null) cf.Title = Path.GetFileName(cf.Cwd.TrimEnd('\\', '/'));
            }
            foreach (int tail in new[] { 256 * 1024, 4 * 1024 * 1024 })
            {
                foreach (var line in TailLines(path, tail).Reverse())
                {
                    if (!line.Contains("\"task_started\"") && !line.Contains("\"task_complete\"") && !line.Contains("\"turn_aborted\"")) continue;
                    cf.Known = true;
                    cf.TurnDone = !line.Contains("\"task_started\"");
                    break;
                }
                if (cf.Known || fi.Length <= tail) break;
            }
            if (!cf.Known && old != null && old.Known) { cf.Known = true; cf.TurnDone = old.TurnDone; }
            return cf;
        }

        // ---- browser tabs (needs the extension) ----

        static IEnumerable<AiSession> ScanTabs()
        {
            var bridge = BrowserBridge.Instance;
            if (bridge == null || !bridge.Connected) yield break;
            var tabs = bridge.GetTabs();
            var alive = new HashSet<int>();
            foreach (var t in tabs)
            {
                string provider = ProviderOfUrl(t.Url);
                if (provider == null) continue;
                alive.Add(t.Id);
                bool wasBusy;
                tabWasBusy.TryGetValue(t.Id, out wasBusy);
                if (wasBusy && !t.Busy) tabsDone.Add(t.Id);                // it just finished answering
                if (t.Busy || (t.Active && t.FocusedWindow)) tabsDone.Remove(t.Id);   // you looked at it
                tabWasBusy[t.Id] = t.Busy;
                yield return new AiSession
                {
                    Id = "tab:" + t.Id, Provider = provider, Kind = "tab", TabId = t.Id, Url = t.Url,
                    Title = TabTitle(t.Title, provider), Where = HostOf(t.Url),
                    Status = t.Busy ? "working" : tabsDone.Contains(t.Id) ? "waiting" : "open",
                    LastActive = t.LastAccessed > 0 ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(t.LastAccessed).ToLocalTime() : DateTime.Now
                };
            }
            foreach (var id in tabWasBusy.Keys.Where(id => !alive.Contains(id)).ToList()) { tabWasBusy.Remove(id); tabsDone.Remove(id); }
        }

        public static string ProviderOfUrl(string url)
        {
            string host = HostOf(url);
            if (host == null) return null;
            if (host == "claude.ai" || host.EndsWith(".claude.ai")) return "claude";
            if (host == "chatgpt.com" || host.EndsWith(".chatgpt.com") || host == "chat.openai.com") return "chatgpt";
            return null;
        }

        static string HostOf(string url)
        {
            Uri u;
            return Uri.TryCreate(url ?? "", UriKind.Absolute, out u) ? u.Host.ToLowerInvariant() : null;
        }

        static string TabTitle(string title, string provider)
        {
            string t = (title ?? "").Trim();
            foreach (var suffix in new[] { " - Claude", " | Claude", " – Claude", " - ChatGPT", " | ChatGPT" })
                if (t.EndsWith(suffix)) t = t.Substring(0, t.Length - suffix.Length).Trim();
            if (t.Length == 0 || t == "Claude" || t == "ChatGPT") return "new chat";
            return Clean(t);
        }

        // ---- the desktop apps ----

        static IEnumerable<AiSession> ScanApps()
        {
            var found = new List<AiSession>();
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!Native.IsWindowVisible(h)) return true;
                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;
                string title = Native.TitleOf(h);
                if (title.Length == 0) return true;
                uint pid; Native.GetWindowThreadProcessId(h, out pid);
                string proc = Native.ProcessNameOf(pid);
                string provider = proc == "claude" ? "claude" : proc == "chatgpt" ? "chatgpt" : null;
                if (provider == null) return true;
                found.Add(new AiSession
                {
                    Id = "app:" + h.ToInt64(), Provider = provider, Kind = "app", Hwnd = h,
                    Title = provider == "claude" ? "Claude app" : "ChatGPT app", Where = "desktop app",
                    Status = "open", LastActive = DateTime.Now
                });
                return true;
            }, IntPtr.Zero);
            return found.GroupBy(s => s.Provider).Select(g => g.First());   // one row per app is plenty
        }

        // =====================================================================
        // doing things
        // =====================================================================

        public static string Describe(int max)
        {
            var list = Snapshot();
            if (list.Count == 0) return "no ai sessions found right now.";
            var sb = new StringBuilder();
            foreach (var s in list.Take(max))
                sb.AppendLine("- [" + s.Provider + " · " + s.Kind + "] “" + s.Title + "” (" + s.Where + ") — " + StatusWords(s.Status) + ", active " + Ago(s.LastActive) + ". id=" + s.Id);
            if (list.Count > max) sb.AppendLine("- …and " + (list.Count - max) + " more");
            return sb.ToString();
        }

        /// <summary>One line like "claude: 2 working, 1 waiting on you. chatgpt: 1 working." (null if nothing's going on).</summary>
        public static string Headline()
        {
            var list = Snapshot();
            var parts = new List<string>();
            foreach (var p in new[] { "claude", "chatgpt" })
            {
                int w = list.Count(s => s.Provider == p && s.Status == "working"), y = list.Count(s => s.Provider == p && s.Status == "waiting");
                if (w + y == 0) continue;
                var bits = new List<string>();
                if (w > 0) bits.Add(w + " working");
                if (y > 0) bits.Add(y + " waiting on you");
                parts.Add(p + ": " + string.Join(", ", bits));
            }
            return parts.Count == 0 ? null : string.Join(". ", parts) + ".";
        }

        public static string StatusWords(string status)
        {
            switch (status)
            {
                case "working": return "working";
                case "waiting": return "done, your move";
                case "open": return "open";
                default: return "quiet";
            }
        }

        public static string Ago(DateTime t)
        {
            var span = DateTime.Now - t;
            if (span.TotalSeconds < 45) return "just now";
            if (span.TotalMinutes < 60) return (int)Math.Max(1, span.TotalMinutes) + "m ago";
            if (span.TotalHours < 24) return (int)span.TotalHours + "h ago";
            return (int)span.TotalDays + "d ago";
        }

        /// <summary>Bring a session to the front. Safe on any thread (may block a moment for browser tabs).</summary>
        public static bool Focus(string id, out string message)
        {
            message = null;
            var s = Snapshot().FirstOrDefault(x => x.Id == id);
            if (s == null) { message = "that session isn't around anymore."; return false; }
            try
            {
                if (s.Kind == "tab")
                {
                    var bridge = BrowserBridge.Instance;
                    if (bridge == null || !bridge.Connected) { message = "the browser isn't connected."; return false; }
                    bridge.Execute("focus_tab", new Dictionary<string, object> { { "tabId", s.TabId } }, 4000);
                    message = "switched to “" + s.Title + "”.";
                    return true;
                }
                if (s.Kind == "app") return Raise(s.Hwnd, s.Title, out message);

                // Claude Code / Codex: find the window it lives in
                IntPtr h = IntPtr.Zero;
                bool fromApp = s.Kind == "code" ? (s.Origin == "claude-desktop" || s.Where == "claude app") : (s.Origin ?? "").IndexOf("desktop", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!fromApp) h = FindTerminal(s);
                if (h == IntPtr.Zero) h = FindAppWindow(s.Kind == "code" ? new[] { "claude" } : new[] { "codex", "chatgpt" });
                if (h == IntPtr.Zero) h = FindTerminal(s);
                if (h == IntPtr.Zero) { message = "couldn't find its window. it lives in " + (s.Cwd ?? s.Where) + "."; return false; }
                return Raise(h, s.Title, out message);
            }
            catch (Exception ex) { message = ex.Message; return false; }
        }

        static bool Raise(IntPtr h, string title, out string message)
        {
            if (Native.IsIconic(h)) Native.ShowWindow(h, 9 /* SW_RESTORE */);
            bool ok = Native.SetForegroundWindow(h);
            message = ok ? "here's “" + title + "”." : "windows wouldn't let me bring it forward. it's in the taskbar.";
            return ok;
        }

        static IntPtr FindAppWindow(string[] procs)
        {
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!Native.IsWindowVisible(h) || Native.TitleOf(h).Length == 0) return true;
                if ((Native.GetWindowLong(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
                uint pid; Native.GetWindowThreadProcessId(h, out pid);
                if (procs.Contains(Native.ProcessNameOf(pid))) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        static IntPtr FindTerminal(AiSession s)
        {
            var terminals = new[] { "windowsterminal", "cmd", "powershell", "pwsh", "wezterm-gui", "alacritty", "mintty", "code", "cursor" };
            string folder = s.Cwd != null ? Path.GetFileName(s.Cwd.TrimEnd('\\', '/')) : null;
            string head = s.Title != null && s.Title.Length > 12 ? s.Title.Substring(0, 12) : s.Title;
            IntPtr best = IntPtr.Zero; int bestScore = 0;
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!Native.IsWindowVisible(h)) return true;
                string title = Native.TitleOf(h);
                if (title.Length == 0) return true;
                uint pid; Native.GetWindowThreadProcessId(h, out pid);
                if (!terminals.Contains(Native.ProcessNameOf(pid))) return true;
                int score = 1;
                if (head != null && title.IndexOf(head, StringComparison.OrdinalIgnoreCase) >= 0) score += 4;
                if (folder != null && title.IndexOf(folder, StringComparison.OrdinalIgnoreCase) >= 0) score += 2;
                if (title.Contains("✳") || title.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0 || title.IndexOf("codex", StringComparison.OrdinalIgnoreCase) >= 0) score += 2;
                if (score > bestScore && score > 1) { bestScore = score; best = h; }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        public static bool OpenNew(string provider)
        {
            string url = provider == "chatgpt" ? "https://chatgpt.com/" : "https://claude.ai/new";
            try
            {
                var bridge = BrowserBridge.Instance;
                if (bridge != null && bridge.Connected)
                {
                    bridge.Execute("open_tab", new Dictionary<string, object> { { "url", url }, { "active", true } }, 4000);
                    return true;
                }
                Process.Start(url);
                return true;
            }
            catch (Exception ex) { DebugLog.Write("open new " + provider + ": " + ex.Message); return false; }
        }

        public static bool CloseTab(string id)
        {
            var s = Snapshot().FirstOrDefault(x => x.Id == id);
            var bridge = BrowserBridge.Instance;
            if (s == null || s.Kind != "tab" || bridge == null || !bridge.Connected) return false;
            try { bridge.Execute("close_tab", new Dictionary<string, object> { { "tabId", s.TabId } }, 4000); return true; }
            catch { return false; }
        }
    }
}
