// PhoneLinks.cs - one of their apps on their phone, in one step: "put the maze solver on my phone" finds its port, starts it
// if it isn't running, shares it on their private tailnet with `tailscale serve` (https://<this computer>.ts.net:<port>,
// only their own tailscale devices can open it; never funnel), checks the link really loads, and sends it to the phone.
// Their other serve mounts are never touched. C# 5 only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Bestie
{
    internal sealed partial class AssistantTools
    {
        void AddPhoneLinkTools(List<object> t)
        {
            t.Add(Tool("phone_link", "put one of their apps or servers on their phone, instantly: 'make the maze solver work on my phone', 'open synaptic on my "
                + "phone', 'share port 5173 to my phone'. it starts the project if needed, shares it privately over their tailscale (only their own devices; never "
                + "public), checks the link loads, and sends it to their phone. also lists what's shared, or stops sharing one. use this, not claude or use_computer.",
                R(),
                P("name", "string", "the project, as they said it (empty if they gave a port)"),
                P("port", "number", "the local port, if they said one"),
                PE("action", "share (default), stop sharing, or list what's shared", new[] { "share", "stop", "list" })));
        }

        AssistantToolResult PhoneLinkTool(string name, Dictionary<string, object> a)
        {
            if (name != "phone_link") return null;
            return PhoneLink(Json.Str(a, "action") ?? "share", (Json.Str(a, "name") ?? "").Trim(), (int)Json.Num(a, "port", 0));
        }

        /// <summary>What tailscale serves now: (https port, path, target) for every mount.</summary>
        static List<string[]> ServeMounts(out string host)
        {
            host = null;
            var list = new List<string[]>();
            var j = Json.Obj(PhoneServer.RunTailscale("serve status --json", 15000) ?? "");
            foreach (var web in (Json.Obj(j, "Web") ?? new Dictionary<string, object>()))
            {
                int colon = web.Key.LastIndexOf(':');
                if (colon < 0) continue;
                host = web.Key.Substring(0, colon);
                foreach (var h in (Json.Obj(web.Value as Dictionary<string, object>, "Handlers") ?? new Dictionary<string, object>()))
                    list.Add(new[] { web.Key.Substring(colon + 1), h.Key, Json.Str(h.Value as Dictionary<string, object>, "Proxy") ?? "" });
            }
            return list;
        }

        static bool ProxiesTo(string target, int port)
        {
            target = (target ?? "").ToLowerInvariant().TrimEnd('/');
            return target == "http://127.0.0.1:" + port || target == "http://localhost:" + port || target == "http://[::1]:" + port;
        }

        AssistantToolResult PhoneLink(string action, string name, int port)
        {
            if (!File.Exists(@"C:\Program Files\Tailscale\tailscale.exe")) return new AssistantToolResult("tailscale isn't installed on this computer, so there's no private way to reach it from the phone.");
            string host;
            List<string[]> mounts;
            try { mounts = ServeMounts(out host); }
            catch (Exception ex) { return new AssistantToolResult("couldn't ask tailscale what it shares: " + ex.Message); }
            if (host == null) host = PhoneServer.TailnetHost();
            if (host == null) return new AssistantToolResult("tailscale isn't signed in on this computer. they need to open tailscale and sign in first.");

            if (action == "list")
            {
                if (mounts.Count == 0) return new AssistantToolResult("nothing is shared to their phone right now.");
                return new AssistantToolResult("shared privately on their tailnet: " + string.Join("; ", mounts.Select(m => "https://" + host + (m[0] == "443" ? "" : ":" + m[0]) + m[1]
                    + " → " + m[2].Replace("http://127.0.0.1:", "port ")).ToArray()) + ". (port 8445 is your own phone app.)");
            }

            string label = name.Length > 0 ? name : "port " + port;
            if (port <= 0 && name.Length > 0) port = FindPort(name, out label);
            if (port <= 0) return new AssistantToolResult(name.Length > 0 ? "i don't know which port " + name + " runs on. ask them, or start_project it first." : "which app (or port)?");
            if (port == PhoneServer.Port) return new AssistantToolResult("that's you: your phone app is already at https://" + host + ":" + PhoneServer.TailscalePort + "/.");
            string[] existing = mounts.FirstOrDefault(m => ProxiesTo(m[2], port));

            if (action == "stop")
            {
                if (existing == null) return new AssistantToolResult(label + " isn't shared to the phone.");
                if (existing[0] == "443" || existing[1] != "/") return new AssistantToolResult(label + " is shared at " + existing[1] + " on the main address, which they set up themselves; leaving it alone.");
                if (DryRun) return new AssistantToolResult("stopped sharing.");
                string outp = PhoneServer.RunTailscale("serve --https=" + existing[0] + " off", 20000) ?? "";
                DebugLog.Write("phone link: stopped sharing " + label + " (:" + existing[0] + "): " + OpenAIClient.Trunc(outp, 200));
                return new AssistantToolResult("stopped sharing " + label + " to the phone.", "stopped sharing " + label, null);
            }

            // it has to be running to be useful
            if (!PortOpen(port))
            {
                if (name.Length == 0) return new AssistantToolResult("nothing is running on port " + port + ". start it first.");
                if (!DryRun)
                {
                    StartProject(name);
                    var sw = Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < 60 && !PortOpen(port)) Thread.Sleep(1000);
                }
                if (!PortOpen(port)) return new AssistantToolResult(label + " isn't running and didn't come up on port " + port + " within a minute, so there's nothing to share yet.");
            }
            if (DryRun) { lock (DryRunLog) DryRunLog.Add("phone link " + label + " :" + port); return new AssistantToolResult("shared."); }

            string httpsPort;
            if (existing != null) httpsPort = existing[0];
            else
            {
                var taken = new HashSet<string>(mounts.Select(m => m[0]));
                int pick = port >= 1024 && port <= 65535 && port != PhoneServer.Port ? port : 8446;
                while (taken.Contains(pick.ToString()) || pick == PhoneServer.TailscalePort) pick = pick == 65535 ? 8446 : pick + 1;
                httpsPort = pick.ToString();
                // node can listen on ipv6 only; tailscale needs an address that answers
                string target = Answers("127.0.0.1", port) ? "http://127.0.0.1:" + port : "http://localhost:" + port;
                string outp = PhoneServer.RunTailscale("serve --bg --https=" + httpsPort + " " + target, 25000) ?? "";
                DebugLog.Write("phone link: shared " + label + " as :" + httpsPort + " -> " + target + ": " + OpenAIClient.Trunc(outp.Replace("\n", " "), 200));
                string ignored;
                if (!ServeMounts(out ignored).Any(m => m[0] == httpsPort && ProxiesTo(m[2], port)))
                    return new AssistantToolResult("tailscale wouldn't share it: " + OpenAIClient.Trunc(outp.Trim(), 200));
            }
            string url = "https://" + host + ":" + httpsPort + "/";

            // load it the way the phone will (through the tailnet), so a broken link never gets sent
            string problem = CheckLink(url);
            PhoneServer srv = PhoneServer.Current;
            if (problem == null && srv != null) srv.SendTextToPhone(url);
            Journal.Add("put " + label + " on your phone: " + url);
            if (problem != null)
                return new AssistantToolResult("shared " + label + " at " + url + " but loading it through tailscale " + problem + " tell them that plainly.", "shared " + label, null);
            return new AssistantToolResult(label + " is on their phone: " + url + " (private: only their tailscale devices can open it" + (existing != null ? "; it was already shared" : "")
                + "). checked: it loads. " + (srv != null ? "the link is in the phone app's inbox" + (srv.PushCount > 0 ? " and a notification is on its way" : "") + ". " : "")
                + "tell them it's ready and where to tap; don't read the whole url out loud.", "on your phone: " + label, null);
        }

        static bool Answers(string address, int port)
        {
            try
            {
                using (var c = new TcpClient(AddressFamily.InterNetwork))
                {
                    var ar = c.BeginConnect(address, port, null, null);
                    return ar.AsyncWaitHandle.WaitOne(400) && c.Connected;
                }
            }
            catch { return false; }
        }

        /// <summary>Null if the page loads through the tailnet; else what went wrong, as a phrase.</summary>
        static string CheckLink(string url)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Timeout = 15000; req.ReadWriteTimeout = 15000; req.Proxy = null;
                    req.UserAgent = "Mozilla/5.0 (iPhone; Bestie link check)";
                    using (var resp = (HttpWebResponse)req.GetResponse()) return null;
                }
                catch (WebException ex)
                {
                    var r = ex.Response as HttpWebResponse;
                    if (r != null)
                    {
                        int code = (int)r.StatusCode;
                        string body = "";
                        try { using (r) using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) body = sr.ReadToEnd(); } catch { }
                        if (body.Contains("Blocked request") || body.Contains("allowedHosts"))
                            return "got \"blocked request\": it's a vite dev server that only answers to localhost. the fix is server.allowedHosts: ['.ts.net'] in its vite config (delegate_to_claude can add it).";
                        if (code == 404) return null;   // it answered; the app just has no page at / (an api, say)
                        return "got an error " + code + " from the app.";
                    }
                    // a brand-new https port can take a few seconds to get its certificate
                    if (attempt < 2) { Thread.Sleep(3000); continue; }
                    return "timed out (" + ex.Message + ").";
                }
                catch (Exception ex) { return "failed (" + ex.Message + ")."; }
            }
        }
    }
}
