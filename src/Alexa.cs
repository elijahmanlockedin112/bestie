// Alexa.cs - Bestie talks out loud on their Echo ("Party Time") through Voice Monkey (voicemonkey.io), the Alexa skill
// that lets an app make an Echo announce something. v3 API (Oct 2026, voicemonkey.io/docs/api/announcement):
//   POST api-v3.voicemonkey.io/announce  {token, device, speech}  -> {"success":true,"data":"OK"} or {"error":"CODE"}
//   GET  api-v3.voicemonkey.io/devices                           -> {"data":[{id, name, capability:"speakers"|"routines"}]}
// The device is a Voice Monkey "speaker" linked to the Echo; settings hold its name and this finds the id. C# 5 only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace Bestie
{
    internal static class Alexa
    {
        public const string DefaultDevice = "Party Time";
        const string Api = "https://api-v3.voicemonkey.io/";

        public const string SetupSteps =
            "1. in the alexa app on your phone: more → skills & games → search \"voice monkey\" → enable to use, and sign in (it makes a free voice monkey account).\n" +
            "2. on voicemonkey.io (same sign-in): devices → add a device → type speaker, name it Party Time, and pick the Party Time echo.\n" +
            "3. voicemonkey.io → api → copy your token.\n" +
            "4. paste it here and press test. party time should talk.";

        static readonly object gate = new object();
        static string cachedFor, cachedId;   // (token + device name) -> voice monkey device id

        static Alexa()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }

        /// <summary>Says speech on the device. Returns "" when it worked, else a friendly line about what went wrong. Any thread (blocks ~1-3 s).</summary>
        public static string Say(string token, string deviceName, string speech)
        {
            token = (token ?? "").Trim();
            if (token.Length == 0) return "there's no voice monkey token in my settings yet.";
            speech = (speech ?? "").Replace("“", "\"").Replace("”", "\"").Trim();
            if (speech.Length == 0) return "there was nothing to say.";
            if (speech.Length > 600) speech = speech.Substring(0, 597) + "...";
            string problem;
            string id = DeviceId(token, deviceName, out problem);
            if (id == null) return problem;
            var body = new Dictionary<string, object> { { "token", token }, { "device", id }, { "speech", speech } };
            int status; string error;
            Call("announce", "POST", Json.Write(body), null, out status, out error);
            if (status == 200 && error == null) return "";
            if (error == "DEVICE_NOT_FOUND") lock (gate) cachedFor = null;   // renamed or removed: look it up again next time
            return Friendly(status, error, deviceName);
        }

        /// <summary>The account's devices as (id, name, capability). problem is set (and the list empty) if it couldn't ask.</summary>
        public static List<string[]> Devices(string token, out string problem)
        {
            string code;
            return Devices(token, out problem, out code);
        }

        static List<string[]> Devices(string token, out string problem, out string code)
        {
            problem = null;
            var list = new List<string[]>();
            int status; string error;
            string text = Call("devices", "GET", null, token, out status, out error);
            if (status == 401 && error == "UNAUTHORIZED")   // in case the header isn't taken: the documented ?token= form
                text = Call("devices?token=" + Uri.EscapeDataString(token), "GET", null, null, out status, out error);
            code = error;
            if (status != 200 || error != null) { problem = Friendly(status, error, null); return list; }
            try
            {
                foreach (object o in Json.Arr(Json.Obj(text), "data"))
                {
                    var d = o as Dictionary<string, object>;
                    string id = Json.Str(d, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    list.Add(new[] { id, Json.Str(d, "name") ?? id, (Json.Str(d, "capability") ?? "").ToLowerInvariant() });
                }
            }
            catch (Exception ex) { problem = "voice monkey answered in a way i don't understand."; DebugLog.Write("alexa: devices parse: " + ex.Message); }
            return list;
        }

        static string Squash(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in (s ?? "").ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>"Party Time" -> the speaker device's id on their account (looked up once, then remembered).</summary>
        static string DeviceId(string token, string name, out string problem)
        {
            problem = null;
            name = string.IsNullOrWhiteSpace(name) ? DefaultDevice : name.Trim();
            string key = token + "\n" + name;
            lock (gate) if (cachedFor == key) return cachedId;
            string devProblem, code;
            List<string[]> all = Devices(token, out devProblem, out code);
            if (devProblem != null)
            {
                if (code == "UNAUTHORIZED" || code == "INVALID_TOKEN" || code == "THROTTLED" || code == "MONTHLY_QUOTA_EXCEEDED") { problem = devProblem; return null; }
                // couldn't list them: voice monkey ids are usually the name in kebab case ("party-time"), so try that
                DebugLog.Write("alexa: couldn't list devices (" + devProblem + "); guessing the id");
                return string.Join("-", name.ToLowerInvariant().Split(new[] { ' ', '_' }, StringSplitOptions.RemoveEmptyEntries));
            }
            string want = Squash(name);
            List<string[]> speakers = all.Where(d => d[2] == "speakers" || d[2] == "speaker").ToList();
            string[] hit = speakers.FirstOrDefault(d => Squash(d[1]) == want || Squash(d[0]) == want)
                ?? all.FirstOrDefault(d => Squash(d[1]) == want || Squash(d[0]) == want)
                ?? (speakers.Count == 1 ? speakers[0] : null);   // they named it something else, but there's only one
            if (hit == null)
            {
                problem = all.Count == 0
                    ? "your voice monkey account has no devices yet. on voicemonkey.io: devices → add a speaker device named " + name + " and link it to the echo."
                    : "voice monkey has no speaker called " + name + ". yours: " + string.Join(", ", all.Select(d => d[1] + (d[2].Length > 0 ? " (" + d[2] + ")" : "")).ToArray())
                      + ". put the right name in my settings, or add a speaker device for " + name + ".";
                return null;
            }
            if (hit[2] == "routines") DebugLog.Write("alexa: " + hit[1] + " is a routine trigger, not a speaker; trying it anyway");
            lock (gate) { cachedFor = key; cachedId = hit[0]; }
            return hit[0];
        }

        /// <summary>One request. bearer: sent as Authorization when set. error = the "error" code from the body (or a network note).</summary>
        static string Call(string path, string method, string json, string bearer, out int status, out string error)
        {
            status = 0; error = null;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(Api + path);
                req.Method = method;
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                req.UserAgent = "Bestie/1.0";
                req.Accept = "application/json";
                if (bearer != null) req.Headers["Authorization"] = "Bearer " + bearer;
                if (json != null)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    req.ContentType = "application/json";
                    req.ContentLength = bytes.Length;
                    using (Stream s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                }
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    status = (int)resp.StatusCode;
                    string text = Read(resp);
                    error = ErrorIn(text);
                    return text;
                }
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp == null) { error = "NETWORK"; DebugLog.Write("alexa: " + path.Split('?')[0] + ": " + ex.Message); return null; }
                status = (int)resp.StatusCode;
                string text = "";
                try { text = Read(resp); } catch { }
                resp.Close();
                error = ErrorIn(text) ?? ("HTTP_" + status);
                DebugLog.Write("alexa: " + path.Split('?')[0] + " -> " + status + " " + error);
                return text;
            }
            catch (Exception ex) { error = "NETWORK"; DebugLog.Write("alexa: " + ex.Message); return null; }
        }

        static string ErrorIn(string text)
        {
            try
            {
                var d = Json.Obj(text);
                if (d == null) return null;
                string e = Json.Str(d, "error");
                if (!string.IsNullOrEmpty(e)) return e;
                return d.ContainsKey("success") && !Json.Bool(d, "success", true) ? (Json.Str(d, "message") ?? "FAILED") : null;
            }
            catch { return null; }
        }

        static string Read(WebResponse resp)
        {
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return r.ReadToEnd();
        }

        static string Friendly(int status, string error, string device)
        {
            string d = string.IsNullOrWhiteSpace(device) ? DefaultDevice : device.Trim();
            switch (error ?? "")
            {
                case "NETWORK": return "i can't reach voice monkey right now (internet?).";
                case "UNAUTHORIZED":
                case "INVALID_TOKEN": return "voice monkey didn't accept the token. copy it again from voicemonkey.io → api. (accounts from before their v3 update need a fresh token there.)";
                case "MISSING_DEVICE":
                case "DEVICE_NOT_FOUND": return "voice monkey doesn't know a speaker called " + d + ". on voicemonkey.io: devices → add a speaker device for it.";
                case "THROTTLED": return "voice monkey is making me wait a bit (too many in a row). try again in a few minutes.";
                case "MONTHLY_QUOTA_EXCEEDED": return "this month's free voice monkey announcements are used up.";
                case "ALEXA_TRIGGER_FAILED": return "voice monkey tried, but alexa didn't respond. is the voice monkey skill still enabled in the alexa app?";
            }
            return "voice monkey had a problem (" + (status > 0 ? status.ToString() : "") + (error != null ? " " + error.ToLowerInvariant() : "") + ").";
        }
    }

    internal sealed partial class AssistantTools
    {
        void AddAlexaTools(List<object> t)
        {
            t.Add(Tool("alexa_say", "say something out loud right now on their alexa (the echo called party time, through voice monkey): when they ask you to "
                + "announce something on the alexa or party time, or tell someone in the house something. for later, use set_reminder with on_alexa.", R("text"),
                P("text", "string", "exactly what the alexa should say, short and natural")));
        }

        AssistantToolResult AlexaTool(string name, Dictionary<string, object> a, AssistantSettings s)
        {
            if (name != "alexa_say") return null;
            string text = (Json.Str(a, "text") ?? "").Trim();
            if (text.Length == 0) return new AssistantToolResult("say what?");
            if (s == null || s.AlexaToken.Length == 0)
                return new AssistantToolResult("the alexa isn't connected yet. in my settings (right-click me → settings…), the alexa row needs a voice monkey token; "
                    + "\"how do i set this up?\" next to it has the four steps. tell them that.");
            if (DryRun) { lock (DryRunLog) DryRunLog.Add("alexa: " + text); return new AssistantToolResult("said it on " + s.AlexaDevice + "."); }
            string problem = Alexa.Say(s.AlexaToken, s.AlexaDevice, text);
            if (problem.Length > 0) return new AssistantToolResult("it didn't go through: " + problem);
            return new AssistantToolResult("said it on " + s.AlexaDevice + ".", "alexa: " + text, null);
        }
    }
}
