// OpenAIClient.cs - tiny OpenAI-compatible HTTP client (Chat Completions + transcriptions). C# 5 only.
// Blocking calls; use from background threads. Errors come out as OpenAIException with a
// friendly, in-character message in .Friendly.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace Bestie
{
    public sealed class OpenAIException : Exception
    {
        public int Status;          // HTTP status, 0 if we never got one
        public string Code;         // error.code
        public string Param;        // error.param
        public string ApiMessage;   // error.message
        public bool Network;        // no HTTP response at all
        public bool Aborted;        // we cancelled it
        public string Friendly;     // show this to the user

        public OpenAIException(string friendly, string detail) : base(detail) { Friendly = friendly; }
    }

    public sealed class OpenAIClient
    {
        readonly string baseUrl;
        readonly string apiKey;
        readonly object gate = new object();
        HttpWebRequest current;
        volatile bool aborted;

        // "baseUrl|model" -> fixes we learned the hard way (e.g. "no_reasoning", "max_tokens").
        static readonly object quirkGate = new object();
        static readonly Dictionary<string, List<string>> quirks = new Dictionary<string, List<string>>();

        static OpenAIClient()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            try { ServicePointManager.Expect100Continue = false; } catch { }
        }

        public OpenAIClient(string baseUrl, string apiKey)
        {
            string b = string.IsNullOrEmpty(baseUrl) ? AssistantSettings.DefaultBaseUrl : baseUrl.Trim();
            this.baseUrl = b.TrimEnd('/');
            this.apiKey = apiKey ?? "";
        }

        public string BaseUrl { get { return baseUrl; } }

        /// <summary>Cancel whatever request is in flight (and any later ones on this client).</summary>
        public void Abort()
        {
            aborted = true;
            lock (gate) { if (current != null) { try { current.Abort(); } catch { } } }
        }

        // ------------------------------------------------------------------ chat

        /// <summary>POST /chat/completions. Adapts to common model quirks (unsupported
        /// reasoning_effort, max_tokens vs max_completion_tokens, no image input) by retrying.</summary>
        public Dictionary<string, object> ChatCompletion(Dictionary<string, object> body, int timeoutMs)
        {
            string model = Json.Str(body, "model") ?? "";
            string qkey = baseUrl + "|" + model;
            lock (quirkGate)
            {
                List<string> known;
                if (quirks.TryGetValue(qkey, out known)) foreach (string q in known) ApplyQuirk(q, body);
            }
            for (int attempt = 0; ; attempt++)
            {
                try { return PostJson("chat/completions", body, timeoutMs, model); }
                catch (OpenAIException ex)
                {
                    if (attempt >= 3 || ex.Status != 400) throw;
                    string fix = QuirkFor(ex, body);
                    if (fix == null) throw;
                    DebugLog.Write("assistant: adapting to '" + fix + "' for model " + model + " (" + Trunc(ex.ApiMessage, 160) + ")");
                    lock (quirkGate)
                    {
                        List<string> known;
                        if (!quirks.TryGetValue(qkey, out known)) { known = new List<string>(); quirks[qkey] = known; }
                        if (!known.Contains(fix)) known.Add(fix);
                    }
                    ApplyQuirk(fix, body);
                }
            }
        }

        static string QuirkFor(OpenAIException ex, Dictionary<string, object> body)
        {
            string m = ((ex.ApiMessage ?? "") + " " + (ex.Param ?? "") + " " + (ex.Code ?? "")).ToLowerInvariant();
            if (body.ContainsKey("reasoning_effort") && (m.Contains("reasoning_effort") || m.Contains("reasoning effort") || m.Contains("reasoning.effort")))
            {
                string cur = Json.Str(body, "reasoning_effort") ?? "";
                int sv = m.IndexOf("supported values");
                if (sv >= 0)
                {
                    string tail = m.Substring(sv);
                    foreach (string v in new[] { "none", "minimal", "low" })
                        if (v != cur && tail.Contains("'" + v + "'")) return "reasoning=" + v;
                }
                bool unsupported = m.Contains("unsupported parameter") || m.Contains("not supported with this model")
                    || m.Contains("unrecognized") || m.Contains("unknown parameter") || m.Contains("unknown_parameter")
                    || m.Contains("does not support") || m.Contains("unsupported_parameter");
                if (!unsupported && cur != "none" && (m.Contains("function") || m.Contains("tool"))) return "reasoning=none";
                return "no_reasoning";
            }
            if (body.ContainsKey("max_completion_tokens") && m.Contains("max_completion_tokens")) return "max_tokens";
            if (m.Contains("image") && HasImages(body)) return "no_images";
            return null;
        }

        static void ApplyQuirk(string q, Dictionary<string, object> body)
        {
            if (q == "no_reasoning") body.Remove("reasoning_effort");
            else if (q.StartsWith("reasoning="))
            {
                if (body.ContainsKey("reasoning_effort")) body["reasoning_effort"] = q.Substring(10);
            }
            else if (q == "max_tokens")
            {
                object v;
                if (body.TryGetValue("max_completion_tokens", out v)) { body.Remove("max_completion_tokens"); body["max_tokens"] = v; }
            }
            else if (q == "no_images") StripImages(body);
        }

        static IEnumerable<Dictionary<string, object>> Messages(Dictionary<string, object> body)
        {
            object v;
            if (!body.TryGetValue("messages", out v) || v == null) yield break;
            var list = v as IEnumerable;
            if (list == null) yield break;
            foreach (object o in list)
            {
                var d = o as Dictionary<string, object>;
                if (d != null) yield return d;
            }
        }

        static bool HasImages(Dictionary<string, object> body)
        {
            foreach (var msg in Messages(body))
            {
                var parts = msg.ContainsKey("content") ? msg["content"] as IEnumerable : null;
                if (parts == null || msg["content"] is string) continue;
                foreach (object p in parts)
                {
                    var pd = p as Dictionary<string, object>;
                    if (pd != null && Json.Str(pd, "type") == "image_url") return true;
                }
            }
            return false;
        }

        static void StripImages(Dictionary<string, object> body)
        {
            foreach (var msg in Messages(body))
            {
                object c;
                if (!msg.TryGetValue("content", out c) || c == null || c is string) continue;
                var parts = c as IEnumerable;
                if (parts == null) continue;
                var fresh = new List<object>();
                foreach (object p in parts)
                {
                    var pd = p as Dictionary<string, object>;
                    if (pd != null && Json.Str(pd, "type") == "image_url")
                    {
                        var t = new Dictionary<string, object>();
                        t["type"] = "text";
                        t["text"] = "[image left out: this model can't see pictures]";
                        fresh.Add(t);
                    }
                    else fresh.Add(p);
                }
                msg["content"] = fresh;
            }
        }

        // ------------------------------------------------------------------ transcription

        /// <summary>POST /audio/transcriptions (multipart). Returns the text ("" if none).</summary>
        public string Transcribe(byte[] audio, string fileName, string contentType, string model, int timeoutMs)
        {
            string boundary = "----bestie" + Guid.NewGuid().ToString("N");
            var ms = new MemoryStream();
            WriteField(ms, boundary, "model", model);
            WriteField(ms, boundary, "response_format", "json");
            WriteAscii(ms, "--" + boundary + "\r\n" +
                "Content-Disposition: form-data; name=\"file\"; filename=\"" + fileName + "\"\r\n" +
                "Content-Type: " + contentType + "\r\n\r\n");
            ms.Write(audio, 0, audio.Length);
            WriteAscii(ms, "\r\n--" + boundary + "--\r\n");

            HttpWebRequest req = Make("audio/transcriptions", "POST", timeoutMs);
            req.ContentType = "multipart/form-data; boundary=" + boundary;
            Dictionary<string, object> d = Send(req, ms.ToArray(), model);
            return (Json.Str(d, "text") ?? "").Trim();
        }

        static void WriteField(Stream s, string boundary, string name, string value)
        {
            WriteAscii(s, "--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + name + "\"\r\n\r\n");
            byte[] v = Encoding.UTF8.GetBytes(value ?? "");
            s.Write(v, 0, v.Length);
            WriteAscii(s, "\r\n");
        }

        static void WriteAscii(Stream s, string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            s.Write(b, 0, b.Length);
        }

        // ------------------------------------------------------------------ speech

        /// <summary>POST /audio/speech. Returns the open response so the audio can be played while it
        /// streams in (caller disposes it). Abort() stops it mid-stream.</summary>
        public HttpWebResponse Speech(string model, string voice, string input, string instructions, string format, int timeoutMs)
        {
            var body = new Dictionary<string, object>();
            body["model"] = model;
            body["voice"] = voice;
            body["input"] = input;
            body["response_format"] = format;
            if (!string.IsNullOrEmpty(instructions)) body["instructions"] = instructions;
            byte[] bytes = Encoding.UTF8.GetBytes(Json.Write(body));
            HttpWebRequest req = Make("audio/speech", "POST", timeoutMs);
            req.ContentType = "application/json";
            req.Accept = "*/*";
            lock (gate)
            {
                if (aborted) throw AbortedException();
                current = req;   // stays set while streaming so Abort() can cut it off
            }
            try
            {
                req.ContentLength = bytes.Length;
                using (Stream s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                return (HttpWebResponse)req.GetResponse();
            }
            catch (WebException ex) { throw Map(ex, model); }
            catch (IOException ex)
            {
                if (aborted) throw AbortedException();
                var e = new OpenAIException(NetworkDown, ex.Message);
                e.Network = true;
                throw e;
            }
        }

        public const string NetworkDown = "the big outside isn't answering. is the internet on?";

        // ------------------------------------------------------------------ plumbing

        public Dictionary<string, object> PostJson(string path, object body, int timeoutMs, string model)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(Json.Write(body));
            HttpWebRequest req = Make(path, "POST", timeoutMs);
            req.ContentType = "application/json";
            return Send(req, bytes, model);
        }

        public Dictionary<string, object> GetJson(string path, int timeoutMs, string model)
        {
            return Send(Make(path, "GET", timeoutMs), null, model);
        }

        HttpWebRequest Make(string path, string method, int timeoutMs)
        {
            if (aborted) throw AbortedException();
            Uri uri;
            if (!Uri.TryCreate(baseUrl + "/" + path.TrimStart('/'), UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                throw new OpenAIException("the api address in settings doesn't look right. it should be like https://api.openai.com/v1", "bad base url: " + baseUrl);
            var req = (HttpWebRequest)WebRequest.Create(uri);
            req.Method = method;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.Accept = "application/json";
            req.UserAgent = "Bestie/1.0";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (apiKey.Length > 0) req.Headers[HttpRequestHeader.Authorization] = "Bearer " + apiKey;
            return req;
        }

        Dictionary<string, object> Send(HttpWebRequest req, byte[] body, string model)
        {
            lock (gate)
            {
                if (aborted) throw AbortedException();
                current = req;
            }
            try
            {
                if (body != null)
                {
                    req.ContentLength = body.Length;
                    using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                }
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    string text = ReadAll(resp);
                    Dictionary<string, object> d = null;
                    try { d = Json.Obj(text); } catch { }
                    if (d == null)
                        throw new OpenAIException("the big outside said something i couldn't read.", "unparseable response: " + Trunc(text, 200));
                    return d;
                }
            }
            catch (WebException ex) { throw Map(ex, model); }
            catch (IOException ex)
            {
                if (aborted) throw AbortedException();
                var e = new OpenAIException(NetworkDown, ex.Message);
                e.Network = true;
                throw e;
            }
            finally
            {
                lock (gate) { if (current == req) current = null; }
            }
        }

        static string ReadAll(WebResponse resp)
        {
            using (Stream s = resp.GetResponseStream())
            using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
        }

        static OpenAIException AbortedException()
        {
            var e = new OpenAIException("ok, never mind.", "aborted");
            e.Aborted = true;
            return e;
        }

        OpenAIException Map(WebException ex, string model)
        {
            if (aborted || ex.Status == WebExceptionStatus.RequestCanceled) return AbortedException();
            var hr = ex.Response as HttpWebResponse;
            if (hr == null)
            {
                string friendly;
                if (ex.Status == WebExceptionStatus.Timeout)
                    friendly = "the big outside took too long to answer. try again?";
                else if (ex.Status == WebExceptionStatus.TrustFailure || ex.Status == WebExceptionStatus.SecureChannelFailure)
                    friendly = "i couldn't make a safe connection to the big outside. is the computer's clock right?";
                else
                    friendly = NetworkDown;
                var ne = new OpenAIException(friendly, ex.Status + ": " + ex.Message);
                ne.Network = true;
                return ne;
            }
            int status = (int)hr.StatusCode;
            string text = "";
            try { text = ReadAll(hr); } catch { }
            finally { try { hr.Close(); } catch { } }
            string msg = null, code = null, type = null, param = null;
            try
            {
                Dictionary<string, object> d = Json.Obj(text);
                Dictionary<string, object> err = Json.Obj(d, "error");
                if (err != null)
                {
                    msg = Json.Str(err, "message"); code = Json.Str(err, "code"); type = Json.Str(err, "type"); param = Json.Str(err, "param");
                }
                else if (d != null) msg = Json.Str(d, "message") ?? Json.Str(d, "error");
            }
            catch { }
            if (string.IsNullOrEmpty(msg)) msg = Trunc(text, 200);
            var e = new OpenAIException(FriendlyFor(status, code, type, msg, model), "HTTP " + status + " " + (code ?? type ?? "") + ": " + msg);
            e.Status = status; e.Code = code; e.Param = param; e.ApiMessage = msg;
            return e;
        }

        public static string FriendlyFor(int status, string code, string type, string msg, string model)
        {
            string m = ((msg ?? "") + " " + (code ?? "") + " " + (type ?? "")).ToLowerInvariant();
            string mdl = string.IsNullOrEmpty(model) ? "that model" : "\"" + model + "\"";
            if (status == 401)
                return "that key didn't work. maybe it got pasted wrong, or it was turned off. check it in settings.";
            if (status == 403)
            {
                if (m.Contains("country") || m.Contains("region") || m.Contains("territory"))
                    return "the big outside says it doesn't work from where you are.";
                return "the big outside won't let this key use " + mdl + ". the key's project might not have access to it.";
            }
            if (status == 404)
            {
                if (m.Contains("model") || code == "model_not_found")
                    return "the big outside doesn't know a model called " + mdl + ". pick another one in settings.";
                return "that address doesn't look like an openai-style api. check the api address in settings.";
            }
            if (status == 429)
            {
                if (code == "insufficient_quota" || m.Contains("quota") || m.Contains("billing") || m.Contains("credit"))
                    return "the key is out of credit. top it up at platform.openai.com and i'll get back to work.";
                return "the big outside says slow down. give it a few seconds.";
            }
            if (status == 413) return "that was too big to send. maybe a smaller screenshot?";
            if (status == 400)
            {
                if (code == "context_length_exceeded" || m.Contains("context length") || m.Contains("maximum context"))
                    return "that was too much to carry at once. try something shorter, or type /new to start fresh.";
                if (m.Contains("model") && (m.Contains("does not exist") || m.Contains("not found") || m.Contains("invalid model")))
                    return "the big outside doesn't know a model called " + mdl + ". pick another one in settings.";
                return "the big outside didn't like how i asked: " + Trunc(msg, 140);
            }
            if (status >= 500) return "the big outside is having a bad day. 2 out of 7. try again in a bit.";
            return "the big outside said no (" + status + "). " + Trunc(msg, 120);
        }

        public static string Trunc(string s, int max)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
