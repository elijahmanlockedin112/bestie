// PhoneServer.cs - Bestie on the phone. A small web server inside Bestie serves the phone app (phone\index.html,
// added to the iPhone home screen) and its API: chat with the same brain and tools, hold-to-talk, its voice, a
// look at the computer's screen, and what's saved (to-dos, reminders, notes).
// It listens on 127.0.0.1 only; the phone reaches it through `tailscale serve` (HTTPS, their tailnet only), and
// every API call needs the pairing key (shown as a QR code on the computer, never sent anywhere else). C# 5 only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bestie
{
    /// <summary>One message from the phone, as it happens: what it's doing, what it's written so far, the answer.</summary>
    internal sealed class PhoneTurn
    {
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly DateTime Created = DateTime.UtcNow;
        readonly object gate = new object();
        readonly List<string> notes = new List<string>();
        string status = "thinking", partial = "", reply, error;
        bool done;

        public void SetStatus(string s) { lock (gate) status = s; }
        public void SetPartial(string s) { lock (gate) partial = s ?? ""; }
        public void Note(string n) { lock (gate) if (notes.Count < 30) notes.Add(n); }
        public void Finish(string r) { lock (gate) { reply = r; done = true; status = "done"; } }
        public void Fail(string e) { lock (gate) { if (done) return; error = e; done = true; status = "error"; } }
        public bool Done { get { lock (gate) return done; } }

        public Dictionary<string, object> Snapshot()
        {
            lock (gate)
                return new Dictionary<string, object> {
                    { "id", Id }, { "status", status }, { "partial", partial }, { "notes", notes.ToArray() }, { "reply", reply }, { "error", error }, { "done", done } };
        }
    }

    internal sealed class PhoneServer
    {
        public const int Port = 47830;
        public const int TailscalePort = 8445;
        const int MaxBody = 16 * 1024 * 1024;
        const string TailscaleExe = @"C:\Program Files\Tailscale\tailscale.exe";

        readonly AssistantModule assistant;
        readonly Func<Dictionary<string, object>> creature;   // name, hue, now playing: read on the UI thread
        readonly Func<int, byte[]> portrait;                  // the creature's app icon as a PNG of this size
        readonly string webDir;
        readonly Dictionary<string, PhoneTurn> turns = new Dictionary<string, PhoneTurn>();
        TcpListener listener;
        volatile bool running;
        byte[] icon180, icon512;
        string tailnetHost;

        public string Key { get; private set; }

        /// <summary>The address the phone opens (with the pairing key), or null until tailscale's name is known.</summary>
        public string PairUrl { get { return tailnetHost == null ? null : "https://" + tailnetHost + ":" + TailscalePort + "/?k=" + Key; } }

        PhoneServer(AssistantModule assistant, Func<Dictionary<string, object>> creature, Func<int, byte[]> portrait, string webDir)
        {
            this.assistant = assistant; this.creature = creature; this.portrait = portrait; this.webDir = webDir;
        }

        public static PhoneServer Start(AssistantModule assistant, Func<Dictionary<string, object>> creature, Func<int, byte[]> portrait)
        {
            var s = new PhoneServer(assistant, creature, portrait, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "phone"));
            s.Key = LoadKey();
            s.running = true;
            new Thread(s.AcceptLoop) { IsBackground = true, Name = "bestie-phone" }.Start();
            new Thread(delegate() { s.tailnetHost = TailnetHost(); }) { IsBackground = true, Name = "bestie-phone-ts" }.Start();
            return s;
        }

        public void Stop()
        {
            running = false;
            try { if (listener != null) listener.Stop(); } catch { }
        }

        /// <summary>Takes the port, waiting for it if the previous Bestie is still letting go of it (a restart).</summary>
        bool Listen()
        {
            for (int attempt = 0; running; attempt++)
            {
                try
                {
                    listener = new TcpListener(IPAddress.Loopback, Port);
                    listener.Start();
                    DebugLog.Write("phone: serving on 127.0.0.1:" + Port);
                    return true;
                }
                catch (Exception ex)
                {
                    if (attempt == 0) DebugLog.Write("phone: port " + Port + " is busy (" + ex.Message + "); trying again every few seconds");
                    Thread.Sleep(3000);
                }
            }
            return false;
        }

        // ================================================================== pairing key and tailscale

        static string KeyPath { get { return Home.State("phone-key.dat"); } }

        static string LoadKey()
        {
            try
            {
                if (File.Exists(KeyPath))
                    return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), null, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) { DebugLog.Write("phone: key unreadable, making a new one: " + ex.Message); }
            var bytes = new byte[24];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            string key = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            File.WriteAllBytes(KeyPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
            return key;
        }

        /// <summary>A new key: phones paired before stop working until they scan again.</summary>
        public void NewKey()
        {
            try { File.Delete(KeyPath); } catch { }
            Key = LoadKey();
        }

        static string RunTailscale(string args, int timeoutMs)
        {
            if (!File.Exists(TailscaleExe)) return null;
            var psi = new ProcessStartInfo(TailscaleExe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(timeoutMs);
                return output;
            }
        }

        static string TailnetHost()
        {
            try
            {
                var j = Json.Obj(RunTailscale("status --json", 15000) ?? "");
                string dns = Json.Str(Json.Obj(j, "Self"), "DNSName");
                return string.IsNullOrEmpty(dns) ? null : dns.TrimEnd('.');
            }
            catch (Exception ex) { DebugLog.Write("phone: tailscale status: " + ex.Message); return null; }
        }

        /// <summary>Makes sure tailscale serves the app at https://&lt;this computer&gt;:8445 on their tailnet (adds only that
        /// port; their other serve mounts are left as they are). Returns a problem to show, or null.</summary>
        public string EnsureTailscale()
        {
            if (!File.Exists(TailscaleExe)) return "tailscale isn't installed on this computer.";
            if (tailnetHost == null) tailnetHost = TailnetHost();
            if (tailnetHost == null) return "tailscale isn't signed in on this computer.";
            string status = RunTailscale("serve status --json", 15000) ?? "";
            if (status.Contains(":" + TailscalePort + "\"") && status.Contains("127.0.0.1:" + Port)) return null;
            string output = RunTailscale("serve --bg --https=" + TailscalePort + " http://127.0.0.1:" + Port, 20000) ?? "";
            DebugLog.Write("phone: tailscale serve: " + OpenAIClient.Trunc(output, 300));
            status = RunTailscale("serve status --json", 15000) ?? "";
            return status.Contains("127.0.0.1:" + Port) ? null : "tailscale wouldn't share the app: " + OpenAIClient.Trunc(output, 200);
        }

        // ================================================================== http

        sealed class Request
        {
            public string Method, Path, Query;
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public byte[] Body = new byte[0];
            public string Q(string name)
            {
                foreach (string part in (Query ?? "").Split('&'))
                {
                    int eq = part.IndexOf('=');
                    if (eq > 0 && Uri.UnescapeDataString(part.Substring(0, eq)) == name) return Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' '));
                }
                return null;
            }
            public string H(string name) { string v; return Headers.TryGetValue(name, out v) ? v : null; }
            /// <summary>Straight from this computer (not through tailscale, which adds who the visitor is).</summary>
            public bool Local { get { return H("X-Forwarded-For") == null && H("Tailscale-User-Login") == null && H("X-Forwarded-Host") == null && H("Forwarded") == null; } }
        }

        void AcceptLoop()
        {
            if (!Listen()) return;
            while (running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { if (!running) return; Thread.Sleep(200); continue; }
                ThreadPool.QueueUserWorkItem(_ => Serve(client));
            }
        }

        void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 30000; client.SendTimeout = 60000;
                    NetworkStream stream = client.GetStream();
                    Request req = Read(stream);
                    if (req == null) return;
                    Route(req, stream);
                }
                catch (Exception ex) { DebugLog.Write("phone: request failed: " + ex.Message); }
            }
        }

        static Request Read(NetworkStream s)
        {
            var head = new MemoryStream();
            int matched = 0;
            while (matched < 4)
            {
                int b = s.ReadByte();
                if (b < 0) return null;
                head.WriteByte((byte)b);
                matched = (b == '\r' && (matched == 0 || matched == 2)) || (b == '\n' && (matched == 1 || matched == 3)) ? matched + 1 : (b == '\r' ? 1 : 0);
                if (head.Length > 32 * 1024) return null;
            }
            string[] lines = Encoding.UTF8.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            var req = new Request { Method = first[0].ToUpperInvariant() };
            string target = first[1];
            int q = target.IndexOf('?');
            req.Path = q >= 0 ? target.Substring(0, q) : target;
            req.Query = q >= 0 ? target.Substring(q + 1) : "";
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) req.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
            int length;
            if (int.TryParse(req.H("Content-Length"), out length) && length > 0)
            {
                if (length > MaxBody) return null;
                req.Body = new byte[length];
                int got = 0;
                while (got < length) { int n = s.Read(req.Body, got, length - got); if (n <= 0) return null; got += n; }
            }
            return req;
        }

        static void Send(Stream s, int code, string type, byte[] body, string extraHeaders)
        {
            string reason = code == 200 ? "OK" : code == 204 ? "No Content" : code == 400 ? "Bad Request" : code == 401 ? "Unauthorized" : code == 404 ? "Not Found" : code == 413 ? "Too Large" : "Error";
            var h = new StringBuilder();
            h.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
            h.Append("Content-Type: ").Append(type).Append("\r\n");
            h.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            h.Append("Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n");
            if (extraHeaders != null) h.Append(extraHeaders);
            h.Append("\r\n");
            byte[] hb = Encoding.ASCII.GetBytes(h.ToString());
            s.Write(hb, 0, hb.Length);
            s.Write(body, 0, body.Length);
            s.Flush();
        }

        static void SendJson(Stream s, object o) { Send(s, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Json.Write(o)), null); }
        static void SendText(Stream s, int code, string text) { Send(s, code, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), null); }
        static void SendError(Stream s, int code, string message) { Send(s, code, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Json.Write(new Dictionary<string, object> { { "error", message } })), null); }

        bool Authorized(Request r)
        {
            string given = r.H("X-Bestie-Key") ?? r.Q("k") ?? "";
            string key = Key ?? "";
            if (given.Length != key.Length || key.Length == 0) return false;
            int diff = 0;
            for (int i = 0; i < key.Length; i++) diff |= given[i] ^ key[i];
            return diff == 0;
        }

        Dictionary<string, object> BodyJson(Request r)
        {
            try { return Json.Obj(Encoding.UTF8.GetString(r.Body)) ?? new Dictionary<string, object>(); }
            catch { return new Dictionary<string, object>(); }
        }

        void Route(Request r, Stream s)
        {
            string path = r.Path;
            if (r.Method == "GET" && (path == "/" || path == "/index.html")) { App(r, s); return; }
            if (r.Method == "GET" && path == "/manifest.webmanifest") { Manifest(r, s); return; }
            if (r.Method == "GET" && (path == "/icon-180.png" || path == "/icon-512.png" || path == "/apple-touch-icon.png")) { Icon(s, path == "/icon-512.png" ? 512 : 180); return; }
            if (r.Method == "GET" && path == "/pair") { Pair(r, s); return; }
            if (!path.StartsWith("/api/")) { SendText(s, 404, "not here"); return; }
            if (!Authorized(r)) { SendError(s, 401, "not paired"); return; }
            switch (r.Method + " " + path)
            {
                case "POST /api/ask": Ask(r, s); return;
                case "GET /api/turn": Turn(r, s); return;
                case "POST /api/listen": Listen(r, s); return;
                case "POST /api/speak": Speak(r, s); return;
                case "GET /api/history": SendJson(s, History(60)); return;
                case "GET /api/status": SendJson(s, Status()); return;
                case "GET /api/screen": Screen(s); return;
                case "GET /api/saved": SendJson(s, Saved()); return;
                case "GET /api/note": Note(r, s); return;
                case "POST /api/todo": Todo(r, s); return;
            }
            SendError(s, 404, "unknown");
        }

        // ================================================================== the app itself

        Dictionary<string, object> CreatureInfo()
        {
            try { return creature() ?? new Dictionary<string, object>(); } catch { return new Dictionary<string, object>(); }
        }

        string Accent(Dictionary<string, object> c)
        {
            double hue = Json.Num(c, "hue", 150);
            Color col = Renderer.Hsl(hue, 0.55, 0.42, 255);
            return "#" + col.R.ToString("x2") + col.G.ToString("x2") + col.B.ToString("x2");
        }

        void App(Request r, Stream s)
        {
            string file = Path.Combine(webDir, "index.html");
            if (!File.Exists(file)) { SendText(s, 404, "the phone app isn't next to Bestie.exe (phone\\index.html)."); return; }
            var c = CreatureInfo();
            string k = r.Q("k");
            bool keyOk = k != null && k == Key;
            string html = File.ReadAllText(file, Encoding.UTF8)
                .Replace("{{NAME}}", WebUtility.HtmlEncode(Json.Str(c, "name") ?? "Bestie"))
                .Replace("{{ACCENT}}", Accent(c))
                .Replace("{{MANIFEST}}", "manifest.webmanifest" + (keyOk ? "?k=" + Key : ""));
            Send(s, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), null);
        }

        void Manifest(Request r, Stream s)
        {
            var c = CreatureInfo();
            string k = r.Q("k");
            string name = Json.Str(c, "name") ?? "Bestie";
            var m = new Dictionary<string, object>
            {
                { "name", name }, { "short_name", name }, { "start_url", k != null && k == Key ? "./?k=" + Key : "./" }, { "scope", "./" },
                { "display", "standalone" }, { "background_color", "#0f0f0f" }, { "theme_color", "#0f0f0f" },
                { "icons", new object[] {
                    new Dictionary<string, object> { { "src", "icon-180.png" }, { "sizes", "180x180" }, { "type", "image/png" } },
                    new Dictionary<string, object> { { "src", "icon-512.png" }, { "sizes", "512x512" }, { "type", "image/png" } } } },
            };
            Send(s, 200, "application/manifest+json", Encoding.UTF8.GetBytes(Json.Write(m)), null);
        }

        void Icon(Stream s, int size)
        {
            byte[] png = size == 512 ? icon512 : icon180;
            if (png == null)
            {
                try { png = portrait(size); } catch (Exception ex) { DebugLog.Write("phone: icon: " + ex.Message); }
                if (png == null) { SendText(s, 404, "no icon"); return; }
                if (size == 512) icon512 = png; else icon180 = png;
            }
            Send(s, 200, "image/png", png, "Cache-Control: max-age=3600\r\n");
        }

        /// <summary>On this computer only: the QR code to scan with the phone.</summary>
        void Pair(Request r, Stream s)
        {
            if (!r.Local) { SendText(s, 404, "not here"); return; }
            string problem = EnsureTailscale();
            string url = PairUrl;
            var c = CreatureInfo();
            string file = Path.Combine(webDir, "pair.html");
            if (!File.Exists(file)) { SendText(s, 404, "phone\\pair.html is missing."); return; }
            string html = File.ReadAllText(file, Encoding.UTF8)
                .Replace("{{NAME}}", WebUtility.HtmlEncode(Json.Str(c, "name") ?? "Bestie"))
                .Replace("{{ACCENT}}", Accent(c))
                .Replace("{{URL}}", WebUtility.HtmlEncode(url ?? ""))
                .Replace("{{URLJSON}}", Json.Write(url ?? ""))
                .Replace("{{PROBLEM}}", WebUtility.HtmlEncode(problem ?? ""));
            Send(s, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), null);
        }

        // ================================================================== api

        void Ask(Request r, Stream s)
        {
            var b = BodyJson(r);
            string text = Json.Str(b, "text") ?? "", image = Json.Str(b, "image");
            if (image != null && !image.StartsWith("data:image/")) image = null;
            PhoneTurn t = assistant.SubmitFromPhone(text, image, Json.Bool(b, "voice", false));
            lock (turns)
            {
                foreach (var old in turns.Where(kv => (DateTime.UtcNow - kv.Value.Created).TotalMinutes > 30).Select(kv => kv.Key).ToList()) turns.Remove(old);
                turns[t.Id] = t;
            }
            SendJson(s, t.Snapshot());
        }

        void Turn(Request r, Stream s)
        {
            PhoneTurn t;
            lock (turns) turns.TryGetValue(r.Q("id") ?? "", out t);
            if (t == null) { SendError(s, 404, "that message is gone"); return; }
            // wait a little for something new, so the phone isn't hammering (and words show up as they come)
            string before = Json.Write(t.Snapshot());
            for (int i = 0; i < 10 && !t.Done && Json.Write(t.Snapshot()) == before; i++) Thread.Sleep(80);
            SendJson(s, t.Snapshot());
        }

        void Listen(Request r, Stream s)
        {
            if (r.Body.Length < 800) { SendJson(s, new Dictionary<string, object> { { "text", "" } }); return; }
            try { SendJson(s, new Dictionary<string, object> { { "text", assistant.TranscribeForPhone(r.Body, r.H("Content-Type")) ?? "" } }); }
            catch (OpenAIException ex) { DebugLog.Write("phone: transcription: " + ex.Message); SendError(s, 500, ex.Friendly); }
        }

        void Speak(Request r, Stream s)
        {
            string text = Json.Str(BodyJson(r), "text") ?? "";
            try
            {
                byte[] mp3 = assistant.SpeakForPhone(text);
                if (mp3 == null) { Send(s, 204, "text/plain", new byte[0], null); return; }
                Send(s, 200, "audio/mpeg", mp3, null);
            }
            catch (Exception ex) { DebugLog.Write("phone: voice: " + ex.Message); Send(s, 204, "text/plain", new byte[0], null); }
        }

        /// <summary>The last few days' conversation from the chats folder: who said what, plus the italic "did a thing" notes.</summary>
        static List<object> History(int max)
        {
            var items = new List<object>();
            for (int back = 2; back >= 0; back--)
            {
                string file = Path.Combine(Home.ChatsDir, DateTime.Today.AddDays(-back).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md");
                if (!File.Exists(file)) continue;
                string text;
                try { using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd(); }
                catch { continue; }
                bool dayMarked = false;
                foreach (string block in Regex.Split(text.Replace("\r\n", "\n"), @"\n{2,}"))
                {
                    string b = block.Trim();
                    if (b.Length == 0 || b.StartsWith("# ")) continue;
                    Match m = Regex.Match(b, @"^\*\*(.+?):\*\*\s?([\s\S]*)$");
                    Dictionary<string, object> item = null;
                    if (m.Success)
                    {
                        string who = m.Groups[1].Value, said = m.Groups[2].Value;
                        bool phone = said.Contains("_(from your phone)_");
                        said = Regex.Replace(said, @"\s*_\((from your phone|with a photo|with a screenshot)\)_", "");
                        item = new Dictionary<string, object> { { "role", who == "you" ? "you" : "bestie" }, { "text", said.Trim() }, { "phone", phone } };
                    }
                    else if (b.StartsWith("_") && b.EndsWith("_")) item = new Dictionary<string, object> { { "role", "note" }, { "text", b.Trim('_') } };
                    else if (items.Count > 0)
                    {
                        var last = (Dictionary<string, object>)items[items.Count - 1];
                        if ((string)last["role"] != "note") last["text"] = (string)last["text"] + "\n\n" + b;
                        continue;
                    }
                    if (item == null) continue;
                    if (!dayMarked && back > 0) { items.Add(new Dictionary<string, object> { { "role", "day" }, { "text", DateTime.Today.AddDays(-back).ToString("dddd", CultureInfo.InvariantCulture).ToLowerInvariant() } }); dayMarked = true; }
                    items.Add(item);
                }
            }
            return items.Skip(Math.Max(0, items.Count - max)).ToList();
        }

        Dictionary<string, object> Status()
        {
            var c = CreatureInfo();
            double idle = Native.IdleSeconds();
            var d = new Dictionary<string, object>
            {
                { "name", Json.Str(c, "name") ?? "Bestie" }, { "accent", Accent(c) },
                { "idleSeconds", (int)idle }, { "active", idle < 120 }, { "now", Behavior.CurrentThing() },
                { "playing", Json.Str(c, "playing") }, { "quietUntil", Quiet.On ? When.Clock(Quiet.Until) : null },
            };
            int steps, files, projects;
            if (Office.TodayCounts(out steps, out files, out projects)) d["agents"] = steps + " agent steps today";
            return d;
        }

        void Screen(Stream s)
        {
            string layout;
            List<string> shots;
            try { shots = ScreenGrab.CaptureAllScreens(1600, 72, out layout); }
            catch (Exception ex) { SendError(s, 500, "couldn't see the screen: " + ex.Message); return; }
            SendJson(s, new Dictionary<string, object> { { "images", shots.ToArray() }, { "at", When.Clock(DateTime.Now) } });
        }

        Dictionary<string, object> Saved()
        {
            var todos = Todos.Load().Where(t => !t.Done || (t.DoneOn.HasValue && t.DoneOn.Value.Date == DateTime.Today))
                .Select(t => (object)new Dictionary<string, object> { { "text", t.Text }, { "done", t.Done } }).ToArray();
            var reminders = assistant.PendingReminders().OrderBy(x => x.DueUtc).Take(30)
                .Select(x => (object)new Dictionary<string, object> { { "text", x.Text }, { "when", When.Describe(x.DueUtc.ToLocalTime(), DateTime.Now) }, { "repeat", When.DescribeRepeat(x.Repeat) } }).ToArray();
            var notes = new List<object>();
            try
            {
                foreach (var f in new DirectoryInfo(Home.NotesDir).GetFiles("*.md", SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTime).Take(40))
                {
                    string preview = "";
                    try
                    {
                        preview = string.Join(" ", File.ReadLines(f.FullName, Encoding.UTF8).Where(l => l.Trim().Length > 0 && !l.StartsWith("#")).Take(3).ToArray());
                        preview = Regex.Replace(preview, @"[*_`>\[\]]|^\s*[-+]\s+", "").Trim();
                    }
                    catch { }
                    notes.Add(new Dictionary<string, object> {
                        { "name", f.FullName.Substring(Home.NotesDir.Length).TrimStart('\\').Replace('\\', '/') }, { "title", Path.GetFileNameWithoutExtension(f.Name).Replace('-', ' ') },
                        { "when", f.LastWriteTime.ToString("MMM d", CultureInfo.InvariantCulture).ToLowerInvariant() }, { "preview", OpenAIClient.Trunc(preview, 140) } });
                }
            }
            catch { }
            return new Dictionary<string, object> { { "todos", todos }, { "reminders", reminders }, { "notes", notes.ToArray() } };
        }

        void Note(Request r, Stream s)
        {
            string name = (r.Q("name") ?? "").Replace('/', '\\');
            string full;
            try { full = Path.GetFullPath(Path.Combine(Home.NotesDir, name)); } catch { SendError(s, 400, "bad name"); return; }
            if (!full.StartsWith(Home.NotesDir + "\\", StringComparison.OrdinalIgnoreCase) || !full.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            { SendError(s, 404, "no such note"); return; }
            SendJson(s, new Dictionary<string, object> { { "title", Path.GetFileNameWithoutExtension(full).Replace('-', ' ') }, { "text", File.ReadAllText(full, Encoding.UTF8) } });
        }

        void Todo(Request r, Stream s)
        {
            var b = BodyJson(r);
            string text = (Json.Str(b, "text") ?? "").Trim(), action = Json.Str(b, "action") ?? "add";
            if (text.Length == 0) { SendError(s, 400, "empty"); return; }
            if (action == "done" || action == "undo") Todos.SetDone(text, action == "done");
            else Todos.Add(text);
            SendJson(s, Saved());
        }
    }
}
