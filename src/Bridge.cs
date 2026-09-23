// Bridge.cs - the C# half of the browser bridge. C# 5 only.
//
// The "Bestie bridge" browser extension (extension\ folder) is the client; this is a tiny
// HTTP/1.1 server bound to 127.0.0.1 only. The extension says hello, pushes tab snapshots,
// and long-polls for commands we queue for it, posting each answer back to /result.
//
//   POST /hello   {browser, extensionVersion}  -> {ok:true, app:"bestie"}
//   POST /tabs    {tabs:[...]}                 -> {ok:true}   (replaces the snapshot)
//   GET|POST /poll                             -> {commands:[{id, type, args}]}  (held up to ~20 s)
//   POST /result  {id, ok, result, error}      -> {ok:true}
//   GET  /ping                                 -> {ok:true, app:"bestie"}  (no origin needed)
//
// The extension polls with POST: Chrome leaves the Origin header off GET requests an
// extension makes to a host it has permission for, and we refuse anything without one.
//
// Security: every endpoint but /ping needs "Origin: chrome-extension://<id>". The first id we
// ever see is pinned in .state\bridge.json (trust on first use); other ids get 403. The Host
// header must be 127.0.0.1:port or localhost:port, so a DNS-rebinding page can't sneak in.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bestie
{
    /// <summary>One browser tab, as the extension last described it.</summary>
    public class TabInfo
    {
        public int Id;
        public int WindowId;
        public int Index;
        public string Title;
        public string Url;
        public bool Active;          // active tab in its window
        public bool FocusedWindow;   // its window is the (last) focused browser window
        public bool Audible;
        public bool Pinned;
        public double LastAccessed;  // ms since epoch, 0 if unknown
        public string Ai;            // "claude", "chatgpt", or null for any other site
        public bool Busy;            // AI tab that's writing a reply right now (visible stop button)
        public bool Convo;           // AI tab showing an actual conversation, not the home / new-chat page
    }

    /// <summary>Talking to the browser didn't work. The message is fine to show the user.</summary>
    public class BridgeException : Exception
    {
        public BridgeException(string message) : base(message) { }
    }

    public sealed class BrowserBridge
    {
        public const int DefaultPort = 47823;

        const int PollHoldMs = 20000;            // how long a long-poll waits for work
        const int ConnectedWindowMs = 75000;     // "connected" = heard from the extension this recently
        const int MaxBodyBytes = 4 * 1024 * 1024;
        const int MaxHeaderBytes = 16 * 1024;
        const int MaxConnections = 48;
        const int MaxTabs = 2000;
        const int IoTimeoutMs = 15000;
        const string ExtOrigin = "chrome-extension://";
        static readonly Regex ExtIdPattern = new Regex("^[a-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

        // ------------------------------------------------------------------ lifetime

        static readonly object startGate = new object();
        static volatile BrowserBridge instance;

        /// <summary>The running bridge, or null until Start.</summary>
        public static BrowserBridge Instance { get { return instance; } }

        /// <summary>Listen on 127.0.0.1:port in the background. Idempotent. Never throws for a
        /// busy port: it logs once and keeps quietly retrying every few seconds.</summary>
        public static void Start(int port)
        {
            lock (startGate)
            {
                if (instance != null) return;
                var b = new BrowserBridge(port > 0 && port <= 65535 ? port : DefaultPort);
                b.Launch();
                instance = b;
            }
        }

        public static void Stop()
        {
            BrowserBridge b;
            lock (startGate) { b = instance; instance = null; }
            if (b != null) b.Shutdown();
        }

        readonly int port;
        readonly string pairingPath;
        readonly object gate = new object();          // queue, pending, polls, pairing, lastSeen
        readonly object tabsGate = new object();
        readonly object watchGate = new object();
        readonly List<Command> queue = new List<Command>();
        readonly Dictionary<string, Command> pending = new Dictionary<string, Command>();
        readonly Dictionary<string, DateTime> recentRejects = new Dictionary<string, DateTime>();
        readonly ManualResetEvent stopping = new ManualResetEvent(false);
        List<TabInfo> tabs = new List<TabInfo>();
        string pinnedId;
        long lastSeenTicks;                           // DateTime.UtcNow.Ticks of the last request from the paired extension
        int pollGeneration;
        int nextCommandId;
        int openConnections;
        bool wasConnected;
        volatile bool running;
        volatile string browserName;
        volatile string extensionVersion;
        TcpListener listener;
        Timer watchdog;

        BrowserBridge(int port)
        {
            this.port = port;
            pairingPath = Home.State("bridge.json");
            LoadPairing();
        }

        void Launch()
        {
            running = true;
            var t = new Thread(AcceptLoop);
            t.IsBackground = true;
            t.Name = "bestie-bridge";
            t.Start();
            watchdog = new Timer(Watch, null, 5000, 5000);
        }

        void Shutdown()
        {
            List<Command> orphans;
            lock (gate)
            {
                running = false;
                if (listener != null) { try { listener.Stop(); } catch { } }
                orphans = new List<Command>(pending.Values);
                pending.Clear();
                queue.Clear();
                Monitor.PulseAll(gate);
            }
            stopping.Set();
            if (watchdog != null) watchdog.Dispose();
            foreach (Command c in orphans) Finish(c, false, null, "the browser bridge stopped");
            DebugLog.Write("bridge: stopped");
        }

        // ------------------------------------------------------------------ public surface

        /// <summary>True if the extension talked to us within the last ~75 seconds.</summary>
        public bool Connected
        {
            get
            {
                if (!running) return false;
                long seen = Interlocked.Read(ref lastSeenTicks);
                return seen != 0 && DateTime.UtcNow.Ticks - seen < ConnectedWindowMs * TimeSpan.TicksPerMillisecond;
            }
        }

        /// <summary>What the extension said it runs in, e.g. "Chrome 140". Null before the first hello.</summary>
        public string BrowserName { get { return browserName; } }

        /// <summary>Copy of the latest tab list the extension pushed. Empty while not Connected
        /// (a snapshot from a browser that went away is worse than none).</summary>
        public List<TabInfo> GetTabs()
        {
            if (!Connected) return new List<TabInfo>();
            List<TabInfo> snap;
            lock (tabsGate) snap = tabs;
            var copy = new List<TabInfo>(snap.Count);
            foreach (TabInfo t in snap) copy.Add(Copy(t));
            return copy;
        }

        /// <summary>Active tab of the focused browser window; failing that the most recently used
        /// active tab; failing that the most recently used tab. Null if there are none.</summary>
        public TabInfo ActiveTab()
        {
            List<TabInfo> list = GetTabs();
            foreach (TabInfo t in list) if (t.Active && t.FocusedWindow) return t;
            TabInfo best = null;
            foreach (TabInfo t in list) if (t.Active && (best == null || t.LastAccessed > best.LastAccessed)) best = t;
            if (best != null) return best;
            foreach (TabInfo t in list) if (best == null || t.LastAccessed > best.LastAccessed) best = t;
            return best;
        }

        /// <summary>Raised on a background thread when the tab snapshot changes (including when the
        /// browser connects or goes quiet).</summary>
        public event Action TabsChanged;

        /// <summary>Queue a command for the extension and block until it answers or timeoutMs passes.
        /// Returns parsed JSON (Dictionary&lt;string,object&gt;, object[], string, number, bool or null).
        /// Throws BridgeException with a message fit to show the user. Call from a background thread.</summary>
        public object Execute(string type, Dictionary<string, object> args, int timeoutMs)
        {
            if (string.IsNullOrEmpty(type)) throw new BridgeException("i don't know what to ask the browser");
            if (!running) throw new BridgeException("the browser bridge isn't running");
            if (!Connected) throw new BridgeException("the browser isn't connected");
            if (timeoutMs <= 0) timeoutMs = 15000;

            var cmd = new Command();
            cmd.Id = "c" + Interlocked.Increment(ref nextCommandId).ToString(CultureInfo.InvariantCulture);
            cmd.Type = type;
            cmd.Args = args != null ? new Dictionary<string, object>(args) : new Dictionary<string, object>();
            try { Json.Write(cmd.Args); }
            catch (Exception ex) { throw new BridgeException("i couldn't pack that up for the browser (" + ex.Message + ")"); }
            lock (gate)
            {
                pending[cmd.Id] = cmd;
                queue.Add(cmd);
                Monitor.PulseAll(gate);
            }

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            lock (cmd)
            {
                while (!cmd.Finished)
                {
                    int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) break;
                    Monitor.Wait(cmd, left);
                }
            }
            lock (gate)
            {
                pending.Remove(cmd.Id);
                queue.Remove(cmd);
            }
            lock (cmd)
            {
                if (!cmd.Finished)
                {
                    cmd.Finished = true;   // a late answer is ignored
                    throw new BridgeException("the browser didn't answer");
                }
            }
            if (!cmd.Ok)
                throw new BridgeException(string.IsNullOrEmpty(cmd.Error) ? "the browser said no, but not why" : cmd.Error);
            return cmd.Result;
        }

        /// <summary>Forget which extension we trust. The next extension to call in gets pinned.</summary>
        public void ForgetPairing()
        {
            lock (gate)
            {
                pinnedId = null;
                SavePairing(null);
                Interlocked.Exchange(ref lastSeenTicks, 0);
            }
            browserName = null;
            extensionVersion = null;
            lock (tabsGate) tabs = new List<TabInfo>();
            DebugLog.Write("bridge: forgot the browser pairing");
            RaiseTabsChanged();
        }

        // ------------------------------------------------------------------ plumbing types

        sealed class Command
        {
            public string Id, Type, Error;
            public Dictionary<string, object> Args;
            public bool Sent, Finished, Ok;
            public object Result;
        }

        sealed class Request
        {
            public string Method, Path, Body;
            public Socket Socket;
            public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Header(string name) { string v; return Headers.TryGetValue(name, out v) ? v : null; }
        }

        sealed class Response
        {
            public int Status;
            public object Body;
            public List<Command> Delivering;    // commands riding on this response (requeued if the write fails)
            public readonly Dictionary<string, string> Headers = new Dictionary<string, string>();
            public Response(int status, object body) { Status = status; Body = body; }
        }

        // ------------------------------------------------------------------ server loop

        void AcceptLoop()
        {
            bool warned = false;
            while (running)
            {
                TcpListener l = new TcpListener(IPAddress.Loopback, port);
                try
                {
                    l.ExclusiveAddressUse = true;
                    l.Start(32);
                }
                catch (Exception ex)
                {
                    if (!warned) DebugLog.Write("bridge: can't listen on 127.0.0.1:" + port + " (" + ex.Message + "), will keep trying");
                    warned = true;
                    try { l.Stop(); } catch { }
                    stopping.WaitOne(5000);
                    continue;
                }
                lock (gate)
                {
                    if (!running) { l.Stop(); return; }
                    listener = l;
                }
                DebugLog.Write("bridge: listening on 127.0.0.1:" + port);
                try
                {
                    while (running)
                    {
                        TcpClient c = l.AcceptTcpClient();
                        if (Interlocked.Increment(ref openConnections) > MaxConnections)
                        {
                            Interlocked.Decrement(ref openConnections);
                            try { c.Close(); } catch { }
                            continue;
                        }
                        var t = new Thread(ServeClient);
                        t.IsBackground = true;
                        t.Start(c);
                    }
                }
                catch (Exception ex)
                {
                    if (running) { DebugLog.Write("bridge: accept failed: " + ex.Message); stopping.WaitOne(1000); }
                }
                finally { try { l.Stop(); } catch { } }
            }
        }

        void ServeClient(object state)
        {
            var client = (TcpClient)state;
            Response res = null;
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = IoTimeoutMs;
                client.SendTimeout = IoTimeoutMs;
                NetworkStream stream = client.GetStream();
                int failStatus;
                Request req = ReadRequest(stream, out failStatus);
                if (req == null)
                {
                    if (failStatus > 0)
                    {
                        Write(stream, new Response(failStatus, Err(failStatus == 413 ? "that's way too much for me" : "i couldn't make sense of that request")));
                        Drain(client, stream);
                    }
                    return;
                }
                req.Socket = client.Client;
                try { res = Handle(req); }
                catch (Exception ex)
                {
                    DebugLog.Write("bridge: handler blew up on " + req.Path + ": " + ex);
                    res = new Response(500, Err("i tripped over something"));
                }
                Write(stream, res);
            }
            catch (Exception ex)
            {
                if (res != null && res.Delivering != null && res.Delivering.Count > 0) Requeue(res.Delivering);
                if (!(ex is IOException) && !(ex is SocketException) && !(ex is ObjectDisposedException))
                    DebugLog.Write("bridge: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { client.Client.Shutdown(SocketShutdown.Both); } catch { }
                try { client.Close(); } catch { }
                Interlocked.Decrement(ref openConnections);
            }
        }

        Response Handle(Request req)
        {
            if (!HostOk(req.Header("Host")))
            {
                Reject("bad host", req.Header("Host") ?? "(none)");
                return new Response(403, Err("wrong door. i only answer to 127.0.0.1"));
            }
            string extId = ExtensionId(req.Header("Origin"));
            string path = req.Path.Length > 1 ? req.Path.TrimEnd('/') : req.Path;

            if (req.Method == "OPTIONS") return Preflight(req, extId);

            if (path == "/ping")
            {
                if (req.Method != "GET" && req.Method != "POST") return NotAllowed();
                var pong = new Response(200, Hi());
                if (extId != null && MayTalk(extId)) { AllowCors(pong, extId); Touch(extId); }
                return pong;
            }

            if (extId == null)
            {
                Reject("no extension origin", req.Header("Origin") ?? "(none)");
                return new Response(403, Err("i only talk to the bestie bridge extension"));
            }
            if (!Pair(extId))
            {
                Reject("not the paired extension", extId);
                var no = new Response(403, Err("i'm already paired with a different extension. ask bestie to forget its browser pairing"));
                AllowCors(no, extId);
                return no;
            }
            Touch(extId);

            Response res;
            if (path == "/poll")
            {
                res = req.Method == "GET" || req.Method == "POST" ? Poll(req, extId) : NotAllowed();
            }
            else if (path == "/hello" || path == "/tabs" || path == "/result")
            {
                Dictionary<string, object> body = req.Method == "POST" ? ParseBody(req.Body) : null;
                if (req.Method != "POST") res = NotAllowed();
                else if (body == null) res = new Response(400, Err("that wasn't json i could read"));
                else if (path == "/hello") res = Hello(body);
                else if (path == "/tabs") res = TabsIn(body);
                else res = ResultIn(body);
            }
            else res = new Response(404, Err("nothing lives here"));
            AllowCors(res, extId);
            return res;
        }

        Response Preflight(Request req, string extId)
        {
            if (extId == null || !MayTalk(extId))
            {
                Reject("preflight", req.Header("Origin") ?? "(none)");
                return new Response(403, Err("no thanks"));
            }
            var r = new Response(204, null);
            AllowCors(r, extId);
            r.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            r.Headers["Access-Control-Allow-Headers"] = "Content-Type";
            r.Headers["Access-Control-Max-Age"] = "600";
            if (req.Header("Access-Control-Request-Private-Network") != null)
                r.Headers["Access-Control-Allow-Private-Network"] = "true";
            return r;
        }

        Response Hello(Dictionary<string, object> body)
        {
            string name = Clip(Json.Str(body, "browser"), 60);
            string ver = Clip(Json.Str(body, "extensionVersion"), 30);
            if (name != browserName || ver != extensionVersion)
                DebugLog.Write("bridge: hello from " + (name ?? "some browser") + " (extension " + (ver ?? "?") + ")");
            browserName = name;
            extensionVersion = ver;
            return new Response(200, Hi());
        }

        Response TabsIn(Dictionary<string, object> body)
        {
            if (!body.ContainsKey("tabs")) return new Response(400, Err("where are the tabs?"));
            var list = new List<TabInfo>();
            foreach (object o in Json.Arr(body, "tabs"))
            {
                var d = o as Dictionary<string, object>;
                if (d == null || !d.ContainsKey("id")) continue;
                list.Add(ReadTab(d));
                if (list.Count >= MaxTabs) break;
            }
            lock (tabsGate) tabs = list;
            RaiseTabsChanged();
            return new Response(200, OkBody());
        }

        Response Poll(Request req, string extId)
        {
            var batch = new List<Command>();
            DateTime until = DateTime.UtcNow.AddMilliseconds(PollHoldMs);
            bool gone = false;
            lock (gate)
            {
                int mine = ++pollGeneration;
                Monitor.PulseAll(gate);   // an older poll (say, from a service worker that died) steps aside
                while (running && mine == pollGeneration)
                {
                    if (queue.Count > 0)
                    {
                        if (ClientGone(req.Socket)) { gone = true; break; }
                        batch.AddRange(queue);
                        queue.Clear();
                        foreach (Command c in batch) c.Sent = true;
                        break;
                    }
                    int left = (int)(until - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) break;
                    Monitor.Wait(gate, Math.Min(left, 1000));
                    if (ClientGone(req.Socket)) { gone = true; break; }
                }
            }
            if (!gone) Touch(extId);

            var list = new List<object>();
            foreach (Command c in batch)
            {
                var d = new Dictionary<string, object>();
                d["id"] = c.Id;
                d["type"] = c.Type;
                d["args"] = c.Args;
                list.Add(d);
            }
            var body = new Dictionary<string, object>();
            body["commands"] = list;
            var r = new Response(200, body);
            r.Delivering = batch;
            return r;
        }

        Response ResultIn(Dictionary<string, object> body)
        {
            string id = Json.Str(body, "id");
            if (string.IsNullOrEmpty(id)) return new Response(400, Err("an answer with no question?"));
            Command c;
            lock (gate)
            {
                if (pending.TryGetValue(id, out c)) pending.Remove(id);
            }
            if (c != null)
            {
                object result;
                body.TryGetValue("result", out result);
                Finish(c, Json.Bool(body, "ok", false), result, Clip(Json.Str(body, "error"), 1000));
            }
            return new Response(200, OkBody());
        }

        void Requeue(List<Command> cmds)
        {
            lock (gate)
            {
                var again = new List<Command>();
                foreach (Command c in cmds)
                    if (pending.ContainsKey(c.Id) && !queue.Contains(c)) { c.Sent = false; again.Add(c); }
                if (again.Count == 0) return;
                queue.InsertRange(0, again);
                Monitor.PulseAll(gate);
            }
        }

        static void Finish(Command c, bool ok, object result, string error)
        {
            lock (c)
            {
                if (c.Finished) return;
                c.Finished = true;
                c.Ok = ok;
                c.Result = result;
                c.Error = error;
                Monitor.PulseAll(c);
            }
        }

        void Watch(object state)
        {
            if (!Monitor.TryEnter(watchGate)) return;
            try
            {
                bool now = Connected;
                if (now == wasConnected) return;
                wasConnected = now;
                if (!now) DebugLog.Write("bridge: the browser went quiet");
                RaiseTabsChanged();
            }
            finally { Monitor.Exit(watchGate); }
        }

        void RaiseTabsChanged()
        {
            Action h = TabsChanged;
            if (h == null) return;
            try { h(); }
            catch (Exception ex) { DebugLog.Write("bridge: a TabsChanged handler threw: " + ex.Message); }
        }

        // ------------------------------------------------------------------ pairing & checks

        bool HostOk(string host)
        {
            if (host == null) return false;
            host = host.Trim().ToLowerInvariant();
            string p = ":" + port.ToString(CultureInfo.InvariantCulture);
            return host == "127.0.0.1" + p || host == "localhost" + p;
        }

        static string ExtensionId(string origin)
        {
            if (origin == null || !origin.StartsWith(ExtOrigin, StringComparison.OrdinalIgnoreCase)) return null;
            string id = origin.Substring(ExtOrigin.Length).TrimEnd('/').ToLowerInvariant();
            return ExtIdPattern.IsMatch(id) ? id : null;
        }

        bool MayTalk(string extId)
        {
            lock (gate) return pinnedId == null || pinnedId == extId;
        }

        /// <summary>Trust on first use: pin the first extension id we see, refuse the rest.</summary>
        bool Pair(string extId)
        {
            lock (gate)
            {
                if (pinnedId == null)
                {
                    pinnedId = extId;
                    SavePairing(extId);
                    DebugLog.Write("bridge: paired with browser extension " + extId);
                    return true;
                }
                return pinnedId == extId;
            }
        }

        void Touch(string extId)
        {
            lock (gate)
            {
                if (pinnedId != null && pinnedId == extId)
                    Interlocked.Exchange(ref lastSeenTicks, DateTime.UtcNow.Ticks);
            }
        }

        void LoadPairing()
        {
            try
            {
                if (!File.Exists(pairingPath)) return;
                string id = Json.Str(Json.Obj(File.ReadAllText(pairingPath, Encoding.UTF8)), "extensionId");
                if (id != null && ExtIdPattern.IsMatch(id)) pinnedId = id;
            }
            catch (Exception ex) { DebugLog.Write("bridge: couldn't read bridge.json: " + ex.Message); }
        }

        void SavePairing(string id)
        {
            try
            {
                var d = new Dictionary<string, object>();
                d["extensionId"] = id;
                d["pairedAt"] = id != null ? DateTime.Now.ToString("s", CultureInfo.InvariantCulture) : null;
                File.WriteAllText(pairingPath, Json.Write(d), new UTF8Encoding(false));
            }
            catch (Exception ex) { DebugLog.Write("bridge: couldn't save bridge.json: " + ex.Message); }
        }

        void Reject(string why, string what)
        {
            string key = why + "|" + what;
            lock (recentRejects)
            {
                DateTime last;
                if (recentRejects.TryGetValue(key, out last) && (DateTime.UtcNow - last).TotalSeconds < 60) return;
                if (recentRejects.Count > 200) recentRejects.Clear();
                recentRejects[key] = DateTime.UtcNow;
            }
            DebugLog.Write("bridge: turned away a request (" + why + ": " + Clip(what, 120) + ")");
        }

        static bool ClientGone(Socket s)
        {
            if (s == null) return false;
            try { return s.Poll(0, SelectMode.SelectRead) && s.Available == 0; }
            catch { return true; }
        }

        // ------------------------------------------------------------------ HTTP bits

        static Request ReadRequest(NetworkStream s, out int failStatus)
        {
            failStatus = 0;
            var buf = new byte[8192];
            var head = new MemoryStream();
            int end = -1;
            while (end < 0)
            {
                int n = s.Read(buf, 0, buf.Length);
                if (n <= 0) return null;   // they hung up
                head.Write(buf, 0, n);
                end = FindHeaderEnd(head.GetBuffer(), (int)head.Length);
                if (end < 0 && head.Length > MaxHeaderBytes) { failStatus = 431; return null; }
            }

            failStatus = 400;
            byte[] raw = head.GetBuffer();
            int rawLen = (int)head.Length;
            string[] lines = Encoding.UTF8.GetString(raw, 0, end).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length != 3 || first[1].Length == 0 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;

            var req = new Request();
            req.Method = first[0].ToUpperInvariant();
            int q = first[1].IndexOf('?');
            req.Path = q >= 0 ? first[1].Substring(0, q) : first[1];
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) return null;
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                if (req.Headers.ContainsKey(name))
                {
                    // two Hosts / Origins / lengths is someone being clever
                    if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Origin", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) return null;
                    req.Headers[name] = req.Headers[name] + ", " + value;
                }
                else req.Headers[name] = value;
            }

            if (req.Header("Transfer-Encoding") != null) { failStatus = 411; return null; }   // Content-Length only
            int len = 0;
            string cl = req.Header("Content-Length");
            if (cl != null && !int.TryParse(cl, NumberStyles.None, CultureInfo.InvariantCulture, out len)) return null;
            if (len > MaxBodyBytes) { failStatus = 413; return null; }

            var body = new byte[len];
            int have = Math.Min(rawLen - (end + 4), len);
            Buffer.BlockCopy(raw, end + 4, body, 0, have);
            while (have < len)
            {
                int n = s.Read(body, have, len - have);
                if (n <= 0) { failStatus = 0; return null; }
                have += n;
            }
            req.Body = Encoding.UTF8.GetString(body);
            failStatus = 0;
            return req;
        }

        static int FindHeaderEnd(byte[] b, int len)
        {
            for (int i = 0; i + 3 < len; i++)
                if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
            return -1;
        }

        static void Write(NetworkStream s, Response r)
        {
            byte[] body = r.Body == null ? new byte[0] : Encoding.UTF8.GetBytes(Json.Write(r.Body));
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(r.Status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Reason(r.Status)).Append("\r\n");
            if (body.Length > 0) sb.Append("Content-Type: application/json; charset=utf-8\r\n");
            sb.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            sb.Append("X-Content-Type-Options: nosniff\r\n");
            sb.Append("Connection: close\r\n");
            foreach (KeyValuePair<string, string> h in r.Headers) sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
            sb.Append("\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            s.Write(head, 0, head.Length);
            if (body.Length > 0) s.Write(body, 0, body.Length);
            s.Flush();
        }

        /// <summary>After refusing a request early, swallow what the client is still sending so it
        /// gets to read our answer instead of a connection reset.</summary>
        static void Drain(TcpClient client, NetworkStream s)
        {
            try
            {
                client.Client.Shutdown(SocketShutdown.Send);
                client.ReceiveTimeout = 2000;
                var buf = new byte[65536];
                long total = 0;
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    total += n;
                    if (total > 64L * 1024 * 1024) break;
                }
            }
            catch { }
        }

        static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 411: return "Length Required";
                case 413: return "Payload Too Large";
                case 431: return "Request Header Fields Too Large";
                case 500: return "Internal Server Error";
                default: return "Unknown";
            }
        }

        static Dictionary<string, object> ParseBody(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, object>();
            try { return Json.Obj(text); }
            catch { return null; }
        }

        static void AllowCors(Response r, string extId)
        {
            r.Headers["Access-Control-Allow-Origin"] = ExtOrigin + extId;
            r.Headers["Vary"] = "Origin";
        }

        static Response NotAllowed() { return new Response(405, Err("not like that")); }

        static Dictionary<string, object> OkBody()
        {
            var d = new Dictionary<string, object>();
            d["ok"] = true;
            return d;
        }

        static Dictionary<string, object> Hi()
        {
            Dictionary<string, object> d = OkBody();
            d["app"] = "bestie";
            return d;
        }

        static Dictionary<string, object> Err(string message)
        {
            var d = new Dictionary<string, object>();
            d["ok"] = false;
            d["error"] = message;
            return d;
        }

        static TabInfo ReadTab(Dictionary<string, object> d)
        {
            var t = new TabInfo();
            t.Id = ToInt(Json.Num(d, "id", -1));
            t.WindowId = ToInt(Json.Num(d, "windowId", -1));
            t.Index = ToInt(Json.Num(d, "index", 0));
            t.Title = Clip(Json.Str(d, "title"), 500) ?? "";
            t.Url = Clip(Json.Str(d, "url"), 4096) ?? "";
            t.Active = Json.Bool(d, "active", false);
            t.FocusedWindow = Json.Bool(d, "focusedWindow", false);
            t.Audible = Json.Bool(d, "audible", false);
            t.Pinned = Json.Bool(d, "pinned", false);
            t.LastAccessed = Json.Num(d, "lastAccessed", 0);
            string ai = Json.Str(d, "ai");
            t.Ai = string.IsNullOrEmpty(ai) ? null : Clip(ai.ToLowerInvariant(), 32);
            t.Busy = t.Ai != null && Json.Bool(d, "busy", false);
            t.Convo = t.Ai != null && Json.Bool(d, "convo", false);
            return t;
        }

        static TabInfo Copy(TabInfo s)
        {
            var t = new TabInfo();
            t.Id = s.Id; t.WindowId = s.WindowId; t.Index = s.Index;
            t.Title = s.Title; t.Url = s.Url;
            t.Active = s.Active; t.FocusedWindow = s.FocusedWindow;
            t.Audible = s.Audible; t.Pinned = s.Pinned;
            t.LastAccessed = s.LastAccessed;
            t.Ai = s.Ai; t.Busy = s.Busy; t.Convo = s.Convo;
            return t;
        }

        static int ToInt(double v)
        {
            if (double.IsNaN(v)) return -1;
            if (v >= int.MaxValue) return int.MaxValue;
            if (v <= int.MinValue) return int.MinValue;
            return (int)v;
        }

        static string Clip(string s, int max)
        {
            return s == null || s.Length <= max ? s : s.Substring(0, max);
        }
    }
}
