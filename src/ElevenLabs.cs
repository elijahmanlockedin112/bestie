// ElevenLabs.cs - Bestie's voice (and, optionally, its ears) through ElevenLabs: streaming text-to-speech as raw
// 24 kHz PCM (the same audio the OpenAI voice plays through), the account's voice list, and Scribe speech-to-text.
// Errors come back as OpenAIException so the voice pipeline handles both the same way. C# 5 only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace Bestie
{
    internal static class ElevenLabs
    {
        public const string DefaultModel = "eleven_v4_turbo";      // their real-time conversational model
        public const string FallbackModel = "eleven_flash_v2_5";   // fast and cheap; if the account can't use the first
        public const string SttModel = "scribe_v2";
        const string Api = "https://api.elevenlabs.io/v1/";
        static string learnedModel;   // set if the chosen model was refused and the fallback worked

        static ElevenLabs()
        {
            // .NET 4 starts out on old TLS, which elevenlabs refuses ("the underlying connection was closed")
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }

        static HttpWebRequest Make(string path, string method, string key, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(Api + path);
            req.Method = method;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.Headers["xi-api-key"] = key ?? "";
            req.UserAgent = "Bestie/1.0";
            return req;
        }

        /// <summary>Streams one piece of speech: raw 16-bit 24 kHz mono PCM. track gets the request so it can be aborted.</summary>
        public static HttpWebResponse Speak(string key, string voiceId, string model, string text, string previousText, int timeoutMs, Action<HttpWebRequest> track)
        {
            return Speak(key, voiceId, model, text, previousText, timeoutMs, track, "pcm_24000");
        }

        /// <summary>The same, in another output format (the phone app plays "mp3_44100_128").</summary>
        public static HttpWebResponse Speak(string key, string voiceId, string model, string text, string previousText, int timeoutMs, Action<HttpWebRequest> track, string format)
        {
            string m = learnedModel ?? (string.IsNullOrEmpty(model) ? DefaultModel : model);
            for (int attempt = 0; ; attempt++)
            {
                var body = new Dictionary<string, object>();
                body["text"] = text;
                body["model_id"] = m;
                if (!string.IsNullOrEmpty(previousText)) body["previous_text"] = previousText;   // keeps the tone flowing between sentences
                body["voice_settings"] = new Dictionary<string, object> { { "stability", 0.45 }, { "similarity_boost", 0.8 } };
                byte[] bytes = Encoding.UTF8.GetBytes(Json.Write(body));
                // the latency knob only exists on the older models (v4 refuses it outright)
                string tune = m.StartsWith("eleven_v4") || m.StartsWith("eleven_v3") ? "" : "&optimize_streaming_latency=3";
                var req = Make("text-to-speech/" + Uri.EscapeDataString(voiceId) + "/stream?output_format=" + Uri.EscapeDataString(format) + tune, "POST", key, timeoutMs);
                req.ContentType = "application/json";
                req.Accept = "audio/*";
                if (track != null) track(req);
                try
                {
                    req.ContentLength = bytes.Length;
                    using (Stream s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                    return (HttpWebResponse)req.GetResponse();
                }
                catch (WebException ex)
                {
                    OpenAIException e = Map(ex);
                    // the account can't use that model: try the always-available fast one, and remember
                    if (attempt == 0 && m != FallbackModel && (e.Status == 400 || e.Status == 403 || e.Status == 404 || e.Status == 422)
                        && (e.ApiMessage ?? "").ToLowerInvariant().Contains("model") && !(e.ApiMessage ?? "").Contains("optimize_streaming_latency"))
                    {
                        DebugLog.Write("elevenlabs: " + m + " refused (" + e.ApiMessage + "); using " + FallbackModel);
                        m = learnedModel = FallbackModel;
                        continue;
                    }
                    throw e;
                }
            }
        }

        /// <summary>The voices on their account: (id, "name (category)"), premade and their own.</summary>
        public static List<KeyValuePair<string, string>> Voices(string key)
        {
            var req = Make("voices", "GET", key, 15000);
            req.Accept = "application/json";
            Dictionary<string, object> d;
            try { using (var resp = (HttpWebResponse)req.GetResponse()) d = Json.Obj(Read(resp)); }
            catch (WebException ex) { throw Map(ex); }
            var list = new List<KeyValuePair<string, string>>();
            foreach (object o in Json.Arr(d, "voices"))
            {
                var v = o as Dictionary<string, object>;
                string id = Json.Str(v, "voice_id"), name = Json.Str(v, "name");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;
                string cat = Json.Str(v, "category");
                list.Add(new KeyValuePair<string, string>(id, name + (string.IsNullOrEmpty(cat) ? "" : " (" + cat + ")")));
            }
            return list;
        }

        static readonly object voiceGate = new object();
        static string defaultVoiceFor, defaultVoice;

        /// <summary>The voice to use: the chosen one, else the account's first voice (looked up once).</summary>
        public static string VoiceOrDefault(string key, string chosen)
        {
            if (!string.IsNullOrEmpty(chosen)) return chosen;
            lock (voiceGate)
            {
                if (defaultVoiceFor == key && defaultVoice != null) return defaultVoice;
                var all = Voices(key);
                if (all.Count == 0) throw new OpenAIException("your elevenlabs account has no voices to use.", "no voices");
                defaultVoiceFor = key;
                defaultVoice = (all.FirstOrDefault(v => v.Value.EndsWith("(premade)")).Key ?? all[0].Key);
                return defaultVoice;
            }
        }

        /// <summary>Speech to text with Scribe. wav = a whole recording.</summary>
        public static string Transcribe(string key, byte[] wav, int timeoutMs)
        {
            string boundary = "----bestie" + Guid.NewGuid().ToString("N");
            var ms = new MemoryStream();
            Action<string> w = s => { byte[] b = Encoding.UTF8.GetBytes(s); ms.Write(b, 0, b.Length); };
            w("--" + boundary + "\r\nContent-Disposition: form-data; name=\"model_id\"\r\n\r\n" + SttModel + "\r\n");
            w("--" + boundary + "\r\nContent-Disposition: form-data; name=\"tag_audio_events\"\r\n\r\nfalse\r\n");
            w("--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"speech.wav\"\r\nContent-Type: audio/wav\r\n\r\n");
            ms.Write(wav, 0, wav.Length);
            w("\r\n--" + boundary + "--\r\n");
            byte[] bytes = ms.ToArray();
            var req = Make("speech-to-text", "POST", key, timeoutMs);
            req.ContentType = "multipart/form-data; boundary=" + boundary;
            req.Accept = "application/json";
            try
            {
                req.ContentLength = bytes.Length;
                using (Stream s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    var d = Json.Obj(Read(resp));
                    return (Json.Str(d, "text") ?? "").Trim();
                }
            }
            catch (WebException ex) { throw Map(ex); }
        }

        static string Read(WebResponse resp)
        {
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return r.ReadToEnd();
        }

        static OpenAIException Map(WebException ex)
        {
            var resp = ex.Response as HttpWebResponse;
            if (ex.Status == WebExceptionStatus.RequestCanceled) return new OpenAIException("", "aborted") { Aborted = true };
            if (resp == null) return new OpenAIException("i can't reach elevenlabs right now.", ex.Message) { Network = true };
            int status = (int)resp.StatusCode;
            string text = "";
            try { text = Read(resp); } catch { }
            string msg = text;
            try
            {
                var d = Json.Obj(text);
                var detail = Json.Obj(d, "detail");
                msg = Json.Str(detail, "message") ?? Json.Str(d, "detail") ?? text;
            }
            catch { }
            string friendly = status == 401 ? "elevenlabs didn't accept the key. check it in settings."
                : status == 402 || status == 429 ? "elevenlabs says the account is out of characters (or busy)."
                : "elevenlabs had a problem (" + status + ").";
            return new OpenAIException(friendly, "elevenlabs " + status + ": " + OpenAIClient.Trunc(msg, 200)) { Status = status, ApiMessage = msg };
        }
    }
}
